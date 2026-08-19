namespace Threadsmith.Mcp;

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Threadsmith.Core;

/// <summary>SDK-backed MCP transport for a child process communicating over standard input and output.</summary>
internal sealed class SdkStdioTransport : IMcpTransport
{
    private readonly IOutputSanitizer _sanitizer;
    private readonly ILogger<SdkStdioTransport> _logger;
    private readonly ILogger<McpCapabilityChangeSubscription> _capabilityLogger;
    private McpCapabilityChangeSubscription? _capabilityChanges;
    private McpClient? _client;
    private StdioClientTransportOptions? _transportOptions;

    /// <summary>Initializes a new instance of the <see cref="SdkStdioTransport"/> class.</summary>
    internal SdkStdioTransport(IOutputSanitizer sanitizer, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _sanitizer = sanitizer;
        _logger = loggerFactory.CreateLogger<SdkStdioTransport>();
        _capabilityLogger = loggerFactory.CreateLogger<McpCapabilityChangeSubscription>();
    }

    /// <inheritdoc />
    public int? ProcessId => null;

    /// <inheritdoc />
    public bool ProcessPresent => Volatile.Read(ref _client) is not null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<McpImportedCapability>> StartAsync(
        McpConnectionProfile profile,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(environment);
        if (_client is not null)
        {
            throw new InvalidOperationException("The MCP stdio transport is already connected.");
        }

        var transportOptions = CreateOptions(profile, environment);
        var transport = new StdioClientTransport(transportOptions, NullLoggerFactory.Instance);
        var clientOptions = new McpClientOptions
        {
            InitializationTimeout = profile.StartupTimeout,
        };

        try
        {
            var client = await McpClient.CreateAsync(
                transport,
                clientOptions,
                NullLoggerFactory.Instance,
                cancellationToken);
            try
            {
                var capabilities = await DiscoverCapabilitiesAsync(
                    client,
                    profile,
                    cancellationToken);
                var capabilityChanges = new McpCapabilityChangeSubscription(
                    client,
                    profile,
                    _capabilityLogger);
                _client = client;
                _capabilityChanges = capabilityChanges;
                _transportOptions = transportOptions;
                return capabilities;
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }
        }
        catch
        {
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
        return McpTransportMapping.MapInvocation(result);
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
                    McpTransportMapping.MapArguments(arguments),
                    cancellationToken: cancellationToken),
            _ => throw new InvalidOperationException("The selected MCP capability is not a readable resource."),
        };
        return McpTransportMapping.MapResourceContent(result);
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
            McpTransportMapping.MapArguments(arguments),
            cancellationToken: cancellationToken);
        return McpTransportMapping.MapPromptContent(result);
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
        var transportOptions = Interlocked.Exchange(
            ref _transportOptions,
            null);
        if (client is null)
        {
            return true;
        }

        var shutdown = Stopwatch.StartNew();
        bool completedWithinDeadline = true;
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

        if (transportOptions is not null && drainKillTimeout != Timeout.InfiniteTimeSpan)
        {
            var remaining = drainKillTimeout - shutdown.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                remaining = TimeSpan.FromMilliseconds(2);
            }

            // The SDK can spend one timeout waiting for exit and another terminating the process
            // tree. Giving each half of the remaining host deadline prevents the original profile
            // timeout from surviving into this shortened stop attempt.
            transportOptions.ShutdownTimeout = TimeSpan.FromTicks(Math.Max(
                TimeSpan.FromMilliseconds(1).Ticks,
                remaining.Ticks / 2));
        }

        var clientDisposal = client.DisposeAsync().AsTask();
        try
        {
            await clientDisposal.WaitAsync(cancellationToken);
            return completedWithinDeadline;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveBackgroundFailure(clientDisposal);
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(Timeout.InfiniteTimeSpan, CancellationToken.None);
    }

    /// <summary>Discovers only server-supported and profile-allowed bounded capabilities.</summary>
    internal static async Task<IReadOnlyList<McpImportedCapability>> DiscoverCapabilitiesAsync(
        McpClient client,
        McpConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        var capabilities = new List<McpImportedCapability>();
        if (profile.AllowedCapabilities.Contains(McpCapabilityKind.Tool)
            && client.ServerCapabilities.Tools is not null)
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            capabilities.AddRange(McpTransportMapping.MapTools(profile, tools));
        }

        if (client.ServerCapabilities.Resources is not null)
        {
            if (profile.AllowedCapabilities.Contains(McpCapabilityKind.Resource))
            {
                var resources = await client.ListResourcesAsync(cancellationToken: cancellationToken);
                capabilities.AddRange(McpTransportMapping.MapResources(profile, resources));
            }

            if (profile.AllowedCapabilities.Contains(McpCapabilityKind.ResourceTemplate))
            {
                var templates = await client.ListResourceTemplatesAsync(
                    cancellationToken: cancellationToken);
                capabilities.AddRange(McpTransportMapping.MapResourceTemplates(profile, templates));
            }
        }

        if (profile.AllowedCapabilities.Contains(McpCapabilityKind.Prompt)
            && client.ServerCapabilities.Prompts is not null)
        {
            var prompts = await client.ListPromptsAsync(cancellationToken: cancellationToken);
            capabilities.AddRange(McpTransportMapping.MapPrompts(profile, prompts));
        }

        if (capabilities.Select(capability => capability.Id).Distinct(StringComparer.Ordinal).Count()
            != capabilities.Count)
        {
            throw new InvalidOperationException("The MCP server advertises duplicate normalized capability identities.");
        }

        return capabilities;
    }

    /// <summary>Maps a host-owned connection profile to SDK stdio transport options.</summary>
    internal StdioClientTransportOptions CreateOptions(
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
            StandardErrorLines = line => _logger.LogWarning(
                "MCP server '{ProfileId}' stderr: {ServerError}",
                profile.Id,
                _sanitizer.Sanitize(line)),
        };
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
