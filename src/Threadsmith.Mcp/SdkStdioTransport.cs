namespace Threadsmith.Mcp;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Threadsmith.Core;

/// <summary>SDK-backed MCP transport for a child process communicating over standard input and output.</summary>
internal sealed class SdkStdioTransport : IMcpTransport
{
    private readonly McpResourceLimits _limits;
    private readonly TimeProvider _timeProvider;
    private readonly McpTransportMapping _mapping;
    private readonly IOutputSanitizer _sanitizer;
    private readonly ILogger<SdkStdioTransport> _logger;
    private readonly ILogger<McpCapabilityChangeSubscription> _capabilityLogger;
    private McpCapabilityChangeSubscription? _capabilityChanges;
    private McpClient? _client;
    private Process? _process;
    private CancellationTokenSource? _stderrLifetime;
    private Task? _stderrPump;
    private long _shutdownTimeoutTicks = TimeSpan.FromSeconds(10).Ticks;

    /// <summary>Initializes a new instance of the <see cref="SdkStdioTransport"/> class.</summary>
    internal SdkStdioTransport(IOutputSanitizer sanitizer, ILoggerFactory loggerFactory, McpResourceLimits? limits = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _limits = limits ?? new();
        _limits.Validate();
        _mapping = new(_limits);
        _sanitizer = sanitizer;
        _logger = loggerFactory.CreateLogger<SdkStdioTransport>();
        _capabilityLogger = loggerFactory.CreateLogger<McpCapabilityChangeSubscription>();
    }

    /// <inheritdoc />
    public int? ProcessId
    {
        get
        {
            try
            {
                return Volatile.Read(ref _process) is { HasExited: false } process ? process.Id : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    /// <inheritdoc />
    public bool ProcessPresent => ProcessId is not null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<McpImportedCapability>> StartAsync(
        McpConnectionProfile profile,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(environment);
        using var startupDeadline = new CancellationTokenSource(profile.StartupTimeout, _timeProvider);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, startupDeadline.Token);
        cancellationToken = startup.Token;
        if (_client is not null)
        {
            throw new InvalidOperationException("The MCP stdio transport is already connected.");
        }

        var transportOptions = CreateOptions(profile, environment);
        var process = StartProcess(transportOptions);
        var stderrLifetime = new CancellationTokenSource();
        var stderrPump = PumpStandardErrorAsync(process, profile.Id, stderrLifetime.Token);
        var transport = new StreamClientTransport(
            process.StandardInput.BaseStream,
            new McpBoundedLineReadStream(process.StandardOutput.BaseStream, _limits.MaximumLineBytes),
            NullLoggerFactory.Instance);
        var clientOptions = McpProtocolCompatibility.CreateClientOptions(Timeout.InfiniteTimeSpan);
        McpClient? client = null;

        try
        {
            client = await McpClient.CreateAsync(
                transport,
                clientOptions,
                NullLoggerFactory.Instance,
                cancellationToken);
            var capabilityChanges = new McpCapabilityChangeSubscription(
                client,
                profile,
                _capabilityLogger,
                token => DiscoverCapabilitiesAsync(client, profile, token));
            var capabilities = await DiscoverCapabilitiesAsync(
                client,
                profile,
                cancellationToken);
            _client = client;
            _capabilityChanges = capabilityChanges;
            _process = process;
            _stderrLifetime = stderrLifetime;
            _stderrPump = stderrPump;
            Interlocked.Exchange(ref _shutdownTimeoutTicks, profile.DrainKillTimeout.Ticks);
            return capabilities;
        }
        catch
        {
            var cleanup = CleanupProcessAsync(
                client,
                process,
                stderrLifetime,
                stderrPump,
                profile.DrainKillTimeout);
            try
            {
                await cleanup.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ObserveBackgroundFailure(cleanup);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    "MCP stdio startup cleanup failed with {FailureType}.",
                    exception.GetType().Name);
            }

            throw;
        }
    }

    /// <inheritdoc />
    public void SetCapabilityChangeHandler(
        Func<IReadOnlyList<McpImportedCapability>, CancellationToken, Task>? handler)
    {
        var subscription = _capabilityChanges
            ?? throw new InvalidOperationException("The MCP stdio transport is not connected.");
        subscription.SetHandler(handler);
    }

    /// <inheritdoc />
    public async Task<McpTransportInvocation> InvokeAsync(
        string capabilityId,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(argumentsJson);
        var client = _client
            ?? throw new InvalidOperationException("The MCP stdio transport is not connected.");
        var arguments = McpTransportMapping.DeserializeArguments(argumentsJson);
        var result = await client.CallToolAsync(
            capabilityId,
            arguments,
            cancellationToken: cancellationToken);
        return _mapping.MapInvocation(result);
    }

    /// <inheritdoc />
    public async Task<McpTransportContentResult> ReadResourceAsync(
        McpImportedCapability capability,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(arguments);
        var client = _client
            ?? throw new InvalidOperationException("The MCP stdio transport is not connected.");
        var result = capability.Kind switch
        {
            McpCapabilityKind.Resource when arguments.Count == 0
                => await client.ReadResourceAsync(
                    capability.ResourceIdentity
                        ?? throw new InvalidOperationException("The MCP resource has no URI."),
                    cancellationToken: cancellationToken),
            McpCapabilityKind.ResourceTemplate
                => await client.ReadResourceAsync(
                    capability.ResourceIdentity
                        ?? throw new InvalidOperationException("The MCP resource template has no URI template."),
                    _mapping.MapArguments(arguments),
                    cancellationToken: cancellationToken),
            _ => throw new InvalidOperationException("The selected MCP capability is not a readable resource."),
        };
        return _mapping.MapResourceContent(result);
    }

    /// <inheritdoc />
    public async Task<McpTransportContentResult> GetPromptAsync(
        McpImportedCapability capability,
        IReadOnlyDictionary<string, string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(arguments);
        if (capability.Kind != McpCapabilityKind.Prompt)
        {
            throw new InvalidOperationException("The selected MCP capability is not a prompt.");
        }

        var client = _client
            ?? throw new InvalidOperationException("The MCP stdio transport is not connected.");
        var result = await client.GetPromptAsync(
            capability.ServerName,
            _mapping.MapArguments(arguments),
            cancellationToken: cancellationToken);
        return _mapping.MapPromptContent(result);
    }

    /// <inheritdoc />
    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        var client = _client
            ?? throw new InvalidOperationException("The MCP stdio transport is not connected.");
        _ = await client.PingAsync(cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> StopAsync(
        TimeSpan drainKillTimeout,
        CancellationToken cancellationToken = default)
    {
        var capabilityChanges = Interlocked.Exchange(
            ref _capabilityChanges,
            null);
        var client = Interlocked.Exchange(ref _client, null);
        var process = Interlocked.Exchange(ref _process, null);
        var stderrLifetime = Interlocked.Exchange(ref _stderrLifetime, null);
        var stderrPump = Interlocked.Exchange(ref _stderrPump, null);
        if (client is null && process is null)
        {
            return true;
        }

        var shutdown = _timeProvider.GetTimestamp();
        var completedWithinDeadline = true;
        if (capabilityChanges is not null)
        {
            var capabilityDisposal = capabilityChanges.DisposeAsync().AsTask();
            try
            {
                await capabilityDisposal.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completedWithinDeadline = false;
                ObserveBackgroundFailure(capabilityDisposal);
            }
            catch (Exception exception)
            {
                completedWithinDeadline = false;
                _logger.LogWarning(
                    "MCP capability-list subscription cleanup failed with {FailureType} during shutdown.",
                    exception.GetType().Name);
            }
        }

        if (client is not null)
        {
            var clientDisposal = client.DisposeAsync().AsTask();
            try
            {
                await clientDisposal.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completedWithinDeadline = false;
                ObserveBackgroundFailure(clientDisposal);
            }
        }

        if (process is not null)
        {
            process.StandardInput.Close();
            var remaining = drainKillTimeout == Timeout.InfiniteTimeSpan
                ? Timeout.InfiniteTimeSpan
                : drainKillTimeout - _timeProvider.GetElapsedTime(shutdown);
            completedWithinDeadline &= await StopProcessAsync(process, remaining, cancellationToken);
        }

        if (stderrLifetime is not null)
        {
            await stderrLifetime.CancelAsync();
        }

        if (stderrPump is not null)
        {
            try
            {
                await stderrPump.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                completedWithinDeadline = false;
                ObserveBackgroundFailure(stderrPump);
            }
        }

        stderrLifetime?.Dispose();
        process?.Dispose();
        return completedWithinDeadline;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        var shutdownTimeout = TimeSpan.FromTicks(Interlocked.Read(ref _shutdownTimeoutTicks));
        using var cancellation = new CancellationTokenSource(shutdownTimeout, _timeProvider);
        await StopAsync(shutdownTimeout, cancellation.Token);
    }

    /// <summary>Discovers only server-supported and profile-allowed bounded capabilities.</summary>
    internal async Task<IReadOnlyList<McpImportedCapability>> DiscoverCapabilitiesAsync(
        McpClient client,
        McpConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        using var discovery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        McpCapabilityKind[] families =
        [
            McpCapabilityKind.Tool,
            McpCapabilityKind.Resource,
            McpCapabilityKind.ResourceTemplate,
            McpCapabilityKind.Prompt,
        ];
        var tasks = families
            .Where(kind => profile.AllowedCapabilities.Contains(kind)
                && kind switch
                {
                    McpCapabilityKind.Tool => client.ServerCapabilities.Tools is not null,
                    McpCapabilityKind.Resource or McpCapabilityKind.ResourceTemplate
                        => client.ServerCapabilities.Resources is not null,
                    McpCapabilityKind.Prompt => client.ServerCapabilities.Prompts is not null,
                    _ => false,
                })
            .Select(DiscoverFamilyAsync)
            .ToArray();
        var results = await Task.WhenAll(tasks);
        cancellationToken.ThrowIfCancellationRequested();
        var capabilities = results.SelectMany(result => result).ToArray();
        if (capabilities.Select(capability => capability.Id).Distinct(StringComparer.Ordinal).Count()
            != capabilities.Length)
        {
            throw new InvalidOperationException("The MCP server advertises duplicate normalized capability identities.");
        }

        return capabilities;

        async Task<IReadOnlyList<McpImportedCapability>> DiscoverFamilyAsync(McpCapabilityKind kind)
        {
            try
            {
                return kind switch
                {
                    McpCapabilityKind.Tool => _mapping.MapTools(
                        profile, await client.ListToolsAsync(cancellationToken: discovery.Token)),
                    McpCapabilityKind.Resource => _mapping.MapResources(
                        profile, await client.ListResourcesAsync(cancellationToken: discovery.Token)),
                    McpCapabilityKind.ResourceTemplate => _mapping.MapResourceTemplates(
                        profile, await client.ListResourceTemplatesAsync(cancellationToken: discovery.Token)),
                    McpCapabilityKind.Prompt => _mapping.MapPrompts(
                        profile, await client.ListPromptsAsync(cancellationToken: discovery.Token)),
                    _ => throw new InvalidOperationException("Unsupported MCP capability family."),
                };
            }
            catch
            {
                await discovery.CancelAsync();
                throw;
            }
        }
    }

    /// <summary>Maps a host-owned connection profile to SDK stdio transport options.</summary>
    internal static StdioClientTransportOptions CreateOptions(
        McpConnectionProfile profile,
        IReadOnlyDictionary<string, string> environment)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(environment);
        var scopedEnvironment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        foreach (var pair in environment)
        {
            scopedEnvironment[pair.Key] = pair.Value;
        }

        return new StdioClientTransportOptions
        {
            Command = profile.Command,
            Arguments = [.. profile.Arguments],
            Name = $"threadsmith-{profile.Id}",
            WorkingDirectory = profile.WorkingDirectory,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = scopedEnvironment,
            ShutdownTimeout = profile.DrainKillTimeout,
        };
    }

    private static Process StartProcess(StdioClientTransportOptions options)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.Command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = options.WorkingDirectory ?? string.Empty,
        };
        foreach (var argument in options.Arguments ?? [])
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment.Clear();
        foreach (var pair in options.EnvironmentVariables
            ?? new Dictionary<string, string?>(StringComparer.Ordinal))
        {
            if (pair.Value is not null)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The MCP stdio server process did not start.");
            }

            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private async Task PumpStandardErrorAsync(
        Process process,
        string profileId,
        CancellationToken cancellationToken)
    {
        var maximumRetainedLineCharacters = _limits.MaximumStandardErrorLineCharacters;
        using var reader = process.StandardError;
        var buffer = new char[2048];
        var line = new System.Text.StringBuilder(Math.Min(maximumRetainedLineCharacters, buffer.Length));
        var discardRemainder = false;
        try
        {
            while (true)
            {
                var read = await reader.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    if (line.Length > 0)
                    {
                        LogStandardError(profileId, line.ToString(), discardRemainder);
                    }

                    return;
                }

                foreach (var character in buffer.AsSpan(0, read))
                {
                    if (character == '\n')
                    {
                        LogStandardError(profileId, line.ToString().TrimEnd('\r'), discardRemainder);
                        line.Clear();
                        discardRemainder = false;
                    }
                    else if (line.Length < maximumRetainedLineCharacters)
                    {
                        line.Append(character);
                    }
                    else
                    {
                        discardRemainder = true;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException) when (process.HasExited)
        {
        }
    }

    private void LogStandardError(string profileId, string line, bool truncated)
    {
        if (line.Length == 0 && !truncated)
        {
            return;
        }

        _logger.LogWarning(
            "MCP server '{ProfileId}' stderr: {ServerError}",
            profileId,
            _sanitizer.Sanitize(line) + (truncated ? " [truncated]" : string.Empty));
    }

    private async Task CleanupProcessAsync(
        McpClient? client,
        Process process,
        CancellationTokenSource stderrLifetime,
        Task stderrPump,
        TimeSpan timeout)
    {
        using var cleanupCancellation = new CancellationTokenSource(timeout, _timeProvider);

        if (client is not null)
        {
            var disposal = client.DisposeAsync().AsTask();
            try
            {
                await disposal.WaitAsync(cleanupCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                ObserveBackgroundFailure(disposal);
            }
        }

        process.StandardInput.Close();
        _ = await StopProcessAsync(process, TimeSpan.Zero, CancellationToken.None);
        await stderrLifetime.CancelAsync();
        try
        {
            await stderrPump.WaitAsync(cleanupCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            ObserveBackgroundFailure(stderrPump);
        }
        finally
        {
            stderrLifetime.Dispose();
            process.Dispose();
        }
    }

    private async Task<bool> StopProcessAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            if (process.HasExited)
            {
                return true;
            }

            if (timeout != Timeout.InfiniteTimeSpan && timeout <= TimeSpan.Zero)
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            using var deadline = new CancellationTokenSource(timeout, _timeProvider);
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);

            try
            {
                await process.WaitForExitAsync(waitCancellation.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                return false;
            }
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static void ObserveBackgroundFailure(Task task)
    {
        _ = task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
