namespace Threadsmith.Mcp;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Host-owned MCP adapter that manages connection profiles and imported tools (strategy §20, gap #6).</summary>
/// <remarks>
/// The adapter resolves the per-server secret scope (§21.3, gap #6), rejects path-qualified stdio
/// executables, drives the host-owned transport, and on
/// disconnect drains in-flight requests then forces termination after the profile's drain/kill
/// timeout so an unresponsive server cannot wedge a run (§5.8, gap #6).
/// </remarks>
public sealed class McpAdapter : IMcpAdapter
{
    private const int MaximumCapabilities = 256;
    private const int MaximumFailureCharacters = 1024;

    private readonly Func<McpConnectionProfile, IMcpTransport> _transportFactory;
    private readonly ISecretResolver _secretResolver;
    private readonly IOutputSanitizer _sanitizer;
    private readonly ILogger<McpAdapter> _logger;
    private readonly ToolRegistry? _toolRegistry;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, Connection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, McpImportedTool> _tools = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="McpAdapter"/> class.</summary>
    public McpAdapter(
        Func<McpConnectionProfile, IMcpTransport> transportFactory,
        ISecretResolver secretResolver,
        IOutputSanitizer sanitizer,
        ILogger<McpAdapter> logger,
        ToolRegistry? toolRegistry = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        ArgumentNullException.ThrowIfNull(secretResolver);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(logger);
        _transportFactory = transportFactory;
        _secretResolver = secretResolver;
        _sanitizer = sanitizer;
        _logger = logger;
        _toolRegistry = toolRegistry;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Initializes a new instance of the <see cref="McpAdapter"/> class for legacy hosts and tests.</summary>
    public McpAdapter(
        Func<McpConnectionProfile, IMcpTransport> transportFactory,
        ISecretStore secretStore,
        IOutputSanitizer sanitizer,
        ILogger<McpAdapter> logger,
        ToolRegistry? toolRegistry = null,
        TimeProvider? timeProvider = null)
        : this(
            transportFactory,
            new LegacySecretStoreResolver(secretStore),
            sanitizer,
            logger,
            toolRegistry,
            timeProvider)
    {
    }

    /// <inheritdoc />
    public async Task<McpConnectionResult> ConnectAsync(
        McpConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Id);
        if (_connections.ContainsKey(profile.Id))
        {
            var existing = _connections[profile.Id].Status;
            return new McpConnectionResult
            {
                ProfileId = profile.Id,
                Succeeded = existing.State == McpConnectionState.Connected,
                Capabilities = GetCapabilities(profile.Id),
                Tools = _tools.Values
                    .Where(tool => string.Equals(tool.Profile.Id, profile.Id, StringComparison.Ordinal))
                    .ToArray(),
                Status = existing,
            };
        }

        if (profile.Trust == McpTrustLevel.Untrusted)
        {
            throw new InvalidOperationException(
                $"MCP profile '{profile.Id}' is untrusted and may not be connected (§22.1).");
        }

        if (profile.Transport == McpTransport.Stdio && !IsBareExecutable(profile.Command))
        {
            throw new InvalidOperationException(
                $"MCP profile '{profile.Id}' command must be a bare executable name (§22.4).");
        }

        using var startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startupCancellation.CancelAfter(profile.StartupTimeout);
        IReadOnlyDictionary<string, string> environment;
        try
        {
            environment = await ResolveEnvironmentAsync(profile, startupCancellation.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateFailedConnectionResult(profile, exception);
        }
        catch (SecretResolutionException exception)
        {
            return CreateFailedConnectionResult(profile, exception);
        }

        var transport = _transportFactory(profile);
        IReadOnlyList<McpImportedCapability> capabilities;
        var startupStarted = _timeProvider.GetTimestamp();
        try
        {
            capabilities = await transport.StartAsync(profile, environment, startupCancellation.Token);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await SafeStopAsync(transport, profile);
            return CreateFailedConnectionResult(profile, exception);
        }

        if (capabilities.Count > MaximumCapabilities)
        {
            await SafeStopAsync(transport, profile);
            return CreateFailedConnectionResult(
                profile,
                new InvalidOperationException(
                    $"The MCP server advertises more than {MaximumCapabilities} total capabilities."));
        }

        var connection = new Connection(transport, profile, capabilities);
        var capabilityGeneration = connection.CapabilityGeneration;
        var importedTools = new List<McpImportedTool>();
        foreach (var capability in capabilities.Where(c => c.Kind == McpCapabilityKind.Tool))
        {
            if (!profile.AllowedCapabilities.Contains(McpCapabilityKind.Tool))
            {
                // Policy gating (gap #6): tools denied by profile policy are not imported.
                continue;
            }

            importedTools.Add(new McpImportedTool(
                transport,
                profile,
                capability,
                _sanitizer,
                _timeProvider,
                () => connection.AcquireInvocation(capabilityGeneration)));
        }

        try
        {
            _toolRegistry?.RegisterOrReplace(
                importedTools,
                new ToolActivitySource(
                    ToolActivitySourceKind.Mcp,
                    _sanitizer.Sanitize(profile.DisplayName)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await SafeStopAsync(transport, profile);
            return new McpConnectionResult
            {
                ProfileId = profile.Id,
                Succeeded = false,
                Capabilities = [],
                Tools = [],
                Status = new McpConnectionStatus
                {
                    ProfileId = profile.Id,
                    DisplayName = profile.DisplayName,
                    State = McpConnectionState.Failed,
                    Error = NormalizeFailureMessage(exception.Message),
                },
            };
        }

        foreach (var tool in importedTools)
        {
            _tools[tool.Definition.Id] = tool;
        }

        if (!_connections.TryAdd(profile.Id, connection))
        {
            foreach (var tool in importedTools)
            {
                _toolRegistry?.Remove(tool.Definition.Id, tool);
                _tools.TryRemove(tool.Definition.Id, out _);
            }

            await SafeStopAsync(transport, profile);
            return CreateFailedConnectionResult(
                profile,
                new InvalidOperationException("A concurrent MCP connection replaced this startup attempt."));
        }

        var counts = capabilities
            .GroupBy(c => c.Kind)
            .ToDictionary(g => g.Key, g => g.Count());
        var successStatus = new McpConnectionStatus
        {
            ProfileId = profile.Id,
            DisplayName = profile.DisplayName,
            State = McpConnectionState.Connected,
            InFlightRequests = 0,
            ImportedCount = counts,
            ProcessId = transport.ProcessId,
            ProcessPresent = transport.ProcessPresent,
            StartupDurationMilliseconds = ToElapsedMilliseconds(
                _timeProvider.GetElapsedTime(startupStarted)),
        };
        connection.Status = successStatus;
        transport.SetCapabilityChangeHandler(
            (snapshot, token) => RefreshCapabilitiesAsync(connection, snapshot, token));
        return new McpConnectionResult
        {
            ProfileId = profile.Id,
            Succeeded = true,
            Capabilities = capabilities,
            Tools = importedTools,
            Status = successStatus,
        };
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(string profileId, CancellationToken cancellationToken = default)
    {
        _ = await DisconnectWithOutcomeAsync(profileId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<McpConnectionState> DisconnectWithOutcomeAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (!_connections.TryRemove(profileId, out var connection))
        {
            return McpConnectionState.Disconnected;
        }

        connection.Status = connection.Status with { State = McpConnectionState.Draining };
        connection.BeginRetirement();
        connection.Transport.SetCapabilityChangeHandler(null);

        await connection.CapabilityGate.WaitAsync(CancellationToken.None);
        try
        {
            // Remove discovery entries before draining so no new calls can acquire the retiring generation.
            foreach (var tool in _tools
                .Where(kvp => string.Equals(kvp.Value.Profile.Id, profileId, StringComparison.Ordinal))
                .ToArray())
            {
                _toolRegistry?.Remove(tool.Key, tool.Value);
                _tools.TryRemove(tool.Key, out _);
            }
        }
        finally
        {
            connection.CapabilityGate.Release();
        }

        var drainStarted = _timeProvider.GetTimestamp();
        var drainWindow = TimeSpan.FromTicks(connection.Profile.DrainKillTimeout.Ticks / 2);
        bool requestsDrained;
        using (var drainCancellation = new CancellationTokenSource(drainWindow))
        {
            try
            {
                await connection.WaitForDrainAsync(drainCancellation.Token);
                requestsDrained = true;
            }
            catch (OperationCanceledException)
            {
                requestsDrained = false;
            }
        }

        var elapsedDrain = _timeProvider.GetElapsedTime(drainStarted);
        var remaining = connection.Profile.DrainKillTimeout - elapsedDrain;
        if (remaining <= TimeSpan.Zero)
        {
            remaining = TimeSpan.FromMilliseconds(1);
        }

        var transportDrained = await SafeStopAsync(connection.Transport, connection.Profile, remaining);
        var drained = requestsDrained && transportDrained;
        if (!drained)
        {
            _logger.LogWarning(
                "MCP server '{ProfileId}' did not drain within {Timeout}; process tree killed (gap #6).",
                profileId,
                connection.Profile.DrainKillTimeout);
            connection.Status = connection.Status with { State = McpConnectionState.Killed };
        }
        else
        {
            connection.Status = connection.Status with { State = McpConnectionState.Disconnected };
        }

        connection.CapabilityGate.Dispose();
        return connection.Status.State;
    }

    /// <inheritdoc />
    public IReadOnlyList<McpConnectionStatus> GetConnections()
    {
        return _connections.Values.Select(c => c.Status).ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<McpImportedCapability> GetCapabilities(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return _connections.TryGetValue(profileId, out var connection)
            ? connection.Capabilities
            : [];
    }

    /// <inheritdoc />
    public async Task<McpTransportContentResult> ReadResourceAsync(
        string profileId,
        string capabilityId,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken = default)
    {
        var connection = GetConnection(profileId);
        var capability = GetCapability(connection, capabilityId);
        if (capability.Kind is not (McpCapabilityKind.Resource or McpCapabilityKind.ResourceTemplate))
        {
            throw new InvalidOperationException("The selected MCP capability is not a resource.");
        }

        return await InvokeConnectionAsync(
            connection,
            token => connection.Transport.ReadResourceAsync(capability, arguments, token),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<McpTransportContentResult> GetPromptAsync(
        string profileId,
        string capabilityId,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken = default)
    {
        var connection = GetConnection(profileId);
        var capability = GetCapability(connection, capabilityId);
        if (capability.Kind != McpCapabilityKind.Prompt)
        {
            throw new InvalidOperationException("The selected MCP capability is not a prompt.");
        }

        return await InvokeConnectionAsync(
            connection,
            token => connection.Transport.GetPromptAsync(capability, arguments, token),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task PingAsync(string profileId, CancellationToken cancellationToken = default)
    {
        var connection = GetConnection(profileId);
        _ = await InvokeConnectionAsync(
            connection,
            async token =>
            {
                await connection.Transport.PingAsync(token);
                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public McpImportedTool? GetTool(string toolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);
        return _tools.TryGetValue(toolId, out var tool) ? tool : null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var profileId in _connections.Keys.ToArray())
        {
            await DisconnectAsync(profileId, CancellationToken.None);
        }
    }

    private async Task<bool> SafeStopAsync(
        IMcpTransport transport,
        McpConnectionProfile profile,
        TimeSpan? timeout = null)
    {
        // Enforce the remaining drain/kill timeout at the adapter level: the transport may hang, so
        // cancel the stop after the bound and treat the server as killed (gap #6, §5.8).
        var effectiveTimeout = timeout ?? profile.DrainKillTimeout;
        using var drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None);
        drainCancellation.CancelAfter(effectiveTimeout);
        try
        {
            return await transport.StopAsync(effectiveTimeout, drainCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                "MCP transport stop failed and will be treated as killed: {Message}",
                NormalizeFailureMessage(exception.Message));
            return false;
        }
    }

    private McpConnectionResult CreateFailedConnectionResult(
        McpConnectionProfile profile,
        Exception exception)
    {
        var status = new McpConnectionStatus
        {
            ProfileId = profile.Id,
            DisplayName = profile.DisplayName,
            State = McpConnectionState.Failed,
            Error = NormalizeFailureMessage(exception.Message),
        };
        return new McpConnectionResult
        {
            ProfileId = profile.Id,
            Succeeded = false,
            Capabilities = [],
            Tools = [],
            Status = status,
        };
    }

    private string NormalizeFailureMessage(string message)
    {
        return Bound(_sanitizer.Sanitize(message), MaximumFailureCharacters);
    }

    private async Task RefreshCapabilitiesAsync(
        Connection connection,
        IReadOnlyList<McpImportedCapability> capabilities,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (capabilities.Count > MaximumCapabilities)
        {
            throw new InvalidOperationException(
                $"The MCP server advertises more than {MaximumCapabilities} total capabilities.");
        }

        await connection.CapabilityGate.WaitAsync(cancellationToken);
        try
        {
            if (!_connections.TryGetValue(connection.Profile.Id, out var current)
                || !ReferenceEquals(current, connection))
            {
                return;
            }

            McpImportedTool[] existingTools =
            [
                .. _tools.Values.Where(tool => string.Equals(
                    tool.Profile.Id,
                    connection.Profile.Id,
                    StringComparison.Ordinal)),
            ];
            var replacementGeneration = connection.BeginCapabilityReplacement();
            var replacementCompleted = false;
            try
            {
                McpImportedTool[] replacementTools =
                [
                    .. capabilities
                        .Where(capability => capability.Kind == McpCapabilityKind.Tool)
                        .Select(capability => new McpImportedTool(
                            connection.Transport,
                            connection.Profile,
                            capability,
                            _sanitizer,
                            _timeProvider,
                            () => connection.AcquireInvocation(replacementGeneration))),
                ];
                _toolRegistry?.RegisterOrReplace(
                    replacementTools,
                    new ToolActivitySource(
                        ToolActivitySourceKind.Mcp,
                        _sanitizer.Sanitize(connection.Profile.DisplayName)),
                    existingTools);
                connection.CompleteCapabilityReplacement(replacementGeneration);
                replacementCompleted = true;

                var replacementIds = replacementTools
                .Select(tool => tool.Definition.Id)
                .ToHashSet(StringComparer.Ordinal);
                foreach (var existing in existingTools.Where(tool => !replacementIds.Contains(
                    tool.Definition.Id)))
                {
                    _toolRegistry?.Remove(existing.Definition.Id, existing);
                    _tools.TryRemove(existing.Definition.Id, out _);
                }

                foreach (var replacement in replacementTools)
                {
                    _tools[replacement.Definition.Id] = replacement;
                }

                connection.Capabilities = capabilities.ToArray();
                connection.Status = connection.Status with
                {
                    ImportedCount = capabilities
                        .GroupBy(capability => capability.Kind)
                        .ToDictionary(group => group.Key, group => group.Count()),
                };
                _logger.LogInformation(
                    "MCP profile '{ProfileId}' refreshed {CapabilityCount} capabilities after a bounded list-change notification.",
                    connection.Profile.Id,
                    capabilities.Count);
            }
            finally
            {
                if (!replacementCompleted)
                {
                    connection.CancelCapabilityReplacement();
                }
            }
        }
        finally
        {
            connection.CapabilityGate.Release();
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> ResolveEnvironmentAsync(
        McpConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string>(profile.Environment, StringComparer.Ordinal);
        foreach (var secretReference in profile.SecretScope)
        {
            if (!SecretReference.TryParse(secretReference, out var reference) || reference is null)
            {
                throw new InvalidOperationException(
                    "MCP secretScope contains a malformed logical secret reference.");
            }

            var request = new SecretResolutionRequest
            {
                Reference = reference,
                ComponentId = SecretResolutionRequest.CreateConfiguredComponentId("mcp:stdio", profile.Id),
                Purpose = "populate an explicitly scoped MCP process environment",
                MinimumTrust = SecretProviderTrust.UserOwned,
            };
            var resolution = await _secretResolver.ResolveAsync(request, cancellationToken);
            var value = resolution.RequireValue(request);

            // Inject only the secrets named in the profile scope (§21.3, gap #6). The key is the
            // environment variable name; secrets: references resolve to the configured value.
            var key = secretReference.StartsWith("secrets:", StringComparison.OrdinalIgnoreCase)
                ? secretReference["secrets:".Length..]
                : secretReference;
            environment[key] = value;
        }

        return environment;
    }

    private Connection GetConnection(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return _connections.TryGetValue(profileId, out var connection)
            ? connection
            : throw new InvalidOperationException($"MCP profile '{profileId}' is not connected.");
    }

    private static McpImportedCapability GetCapability(Connection connection, string capabilityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        return connection.Capabilities.FirstOrDefault(capability => string.Equals(
                capability.Id,
                capabilityId,
                StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"MCP capability '{capabilityId}' is not active.");
    }

    private static long? ToElapsedMilliseconds(TimeSpan elapsed)
    {
        return elapsed < TimeSpan.Zero ? null : elapsed.Ticks / TimeSpan.TicksPerMillisecond;
    }

    private static string Bound(string value, int maximumCharacters)
    {
        return value.Length <= maximumCharacters ? value : value[..maximumCharacters];
    }

    private static async Task<T> InvokeConnectionAsync<T>(
        Connection connection,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        using var invocation = connection.AcquireInvocation(connection.CapabilityGeneration);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(connection.Profile.RequestTimeout);
        return await operation(requestCancellation.Token);
    }

    private static bool IsBareExecutable(string command)
    {
        // The adapter validates that a stdio command is a bare executable basename resolved from the
        // host PATH (mirrors ProcessManager). Startup composition admits auto-connect profiles only
        // from trusted configuration; here the transport boundary also rejects path-qualified commands.
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        return !Path.IsPathFullyQualified(command)
            && !command.Contains('/')
            && !command.Contains('\\');
    }

    private sealed class Connection
    {
        private readonly Lock _invocationGate = new();
        private TaskCompletionSource _drained = CreateCompletedDrain();
        private int _inFlightRequests;
        private long _capabilityGeneration;
        private bool _replacingCapabilities;
        private bool _retiring;

        public Connection(
            IMcpTransport transport,
            McpConnectionProfile profile,
            IReadOnlyList<McpImportedCapability> capabilities)
        {
            Transport = transport;
            Profile = profile;
            Capabilities = capabilities.ToArray();
            Status = new McpConnectionStatus
            {
                ProfileId = profile.Id,
                DisplayName = profile.DisplayName,
                State = McpConnectionState.Connecting,
            };
        }

        public long CapabilityGeneration
        {
            get
            {
                lock (_invocationGate)
                {
                    return _capabilityGeneration;
                }
            }
        }

        public int CurrentInFlight
        {
            get
            {
                lock (_invocationGate)
                {
                    return _inFlightRequests;
                }
            }
        }

        public IMcpTransport Transport { get; }

        public McpConnectionProfile Profile { get; }

        public SemaphoreSlim CapabilityGate { get; } = new(1, 1);

        public IReadOnlyList<McpImportedCapability> Capabilities { get; set; }

        public McpConnectionStatus Status { get; set; }

        public IDisposable AcquireInvocation(long expectedCapabilityGeneration)
        {
            lock (_invocationGate)
            {
                if (_retiring)
                {
                    throw new InvalidOperationException(
                        $"MCP profile '{Profile.Id}' is retiring and cannot admit a new invocation.");
                }

                if (_replacingCapabilities || expectedCapabilityGeneration != _capabilityGeneration)
                {
                    throw new InvalidOperationException(
                        $"MCP capability generation for profile '{Profile.Id}' is no longer active.");
                }

                if (_inFlightRequests == 0)
                {
                    _drained = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                }

                _inFlightRequests++;
                RefreshInFlightStatus();
                return new InvocationLease(this);
            }
        }

        public long BeginCapabilityReplacement()
        {
            lock (_invocationGate)
            {
                if (_retiring)
                {
                    throw new InvalidOperationException(
                        $"MCP profile '{Profile.Id}' is retiring and cannot replace capabilities.");
                }

                _replacingCapabilities = true;
                return checked(_capabilityGeneration + 1);
            }
        }

        public void CompleteCapabilityReplacement(long replacementGeneration)
        {
            lock (_invocationGate)
            {
                _capabilityGeneration = replacementGeneration;
                _replacingCapabilities = false;
            }
        }

        public void CancelCapabilityReplacement()
        {
            lock (_invocationGate)
            {
                _replacingCapabilities = false;
            }
        }

        public void BeginRetirement()
        {
            lock (_invocationGate)
            {
                _retiring = true;
            }
        }

        public async Task WaitForDrainAsync(CancellationToken cancellationToken)
        {
            Task drain;
            lock (_invocationGate)
            {
                drain = _drained.Task;
            }

            await drain.WaitAsync(cancellationToken);
        }

        private static TaskCompletionSource CreateCompletedDrain()
        {
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            drained.SetResult();
            return drained;
        }

        private void ReleaseInvocation()
        {
            lock (_invocationGate)
            {
                if (_inFlightRequests <= 0)
                {
                    return;
                }

                _inFlightRequests--;
                RefreshInFlightStatus();
                if (_inFlightRequests == 0)
                {
                    _drained.TrySetResult();
                }
            }
        }

        private void RefreshInFlightStatus()
        {
            Status = Status with { InFlightRequests = _inFlightRequests };
        }

        private sealed class InvocationLease : IDisposable
        {
            private Connection? _owner;

            public InvocationLease(Connection owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                var ownerToRelease = Interlocked.Exchange(ref _owner, null);
                ownerToRelease?.ReleaseInvocation();
            }
        }
    }
}
