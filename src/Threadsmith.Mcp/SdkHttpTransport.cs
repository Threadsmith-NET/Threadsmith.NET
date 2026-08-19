namespace Threadsmith.Mcp;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Threadsmith.Tools;

/// <summary>SDK-backed MCP transport for SSE and streamable-HTTP endpoints.</summary>
internal sealed class SdkHttpTransport : IMcpTransport
{
    private readonly ISecretResolver _secretResolver;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly McpOAuthFlow? _oauthFlow;
    private readonly ILogger<McpCapabilityChangeSubscription> _capabilityLogger;
    private readonly ILogger<SdkHttpTransport> _logger;
    private McpCapabilityChangeSubscription? _capabilityChanges;
    private McpClient? _client;
    private HttpClientTransport? _transport;

    /// <summary>Initializes a new instance of the <see cref="SdkHttpTransport"/> class.</summary>
    internal SdkHttpTransport(
        ISecretResolver secretResolver,
        ILoggerFactory loggerFactory,
        McpOAuthFlow? oauthFlow = null,
        HttpClient? httpClient = null)
    {
        ArgumentNullException.ThrowIfNull(secretResolver);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _secretResolver = secretResolver;
        _oauthFlow = oauthFlow;
        _capabilityLogger = loggerFactory.CreateLogger<McpCapabilityChangeSubscription>();
        _logger = loggerFactory.CreateLogger<SdkHttpTransport>();
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            // Bounded pool lifetime refreshes DNS/endpoint changes while reusing connections;
            // matches the model-transport host default. See Plan 67 (AR-04).
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        });
        _ownsHttpClient = httpClient is null;
    }

    /// <summary>Initializes a new instance of the <see cref="SdkHttpTransport"/> class for legacy hosts and tests.</summary>
    internal SdkHttpTransport(
        ISecretStore secretStore,
        ILoggerFactory loggerFactory,
        McpOAuthFlow? oauthFlow = null,
        HttpClient? httpClient = null)
        : this(new LegacySecretStoreResolver(secretStore), loggerFactory, oauthFlow, httpClient)
    {
    }

    /// <inheritdoc />
    public int? ProcessId => null;

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
            throw new InvalidOperationException("The MCP HTTP transport is already connected.");
        }

        var headers = await ResolveHeadersAsync(profile, cancellationToken);
        var transportOptions = CreateOptions(profile, headers);
        if (profile.OAuth?.Enabled is true)
        {
            var oauthFlow = _oauthFlow
                ?? throw new InvalidOperationException("Interactive MCP OAuth is not configured for this host surface.");
            transportOptions.OAuth = await oauthFlow.CreateOptionsAsync(profile, cancellationToken);
        }

        var transport = new HttpClientTransport(
            transportOptions,
            _httpClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
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
                _transport = transport;
                _client = client;
                _capabilityChanges = capabilityChanges;
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
            await transport.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public void SetCapabilityChangeHandler(
        Func<IReadOnlyList<McpImportedCapability>, CancellationToken, Task>? handler)
    {
        var subscription = _capabilityChanges
            ?? throw new InvalidOperationException("The MCP HTTP transport is not connected.");
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
            ?? throw new InvalidOperationException("The MCP HTTP transport is not connected.");
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
            ?? throw new InvalidOperationException("The MCP HTTP transport is not connected.");
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
            ?? throw new InvalidOperationException("The MCP HTTP transport is not connected.");
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
            ?? throw new InvalidOperationException("The MCP HTTP transport is not connected.");
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
        var transport = Interlocked.Exchange(ref _transport, null);
        try
        {
            return await DisposeAllWithinDeadlineAsync(
                [capabilityChanges, client, transport],
                cancellationToken);
        }
        finally
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync(Timeout.InfiniteTimeSpan, CancellationToken.None);
    }

    /// <summary>Attempts every owned async cleanup even when the shared shutdown deadline expires.</summary>
    /// <param name="resources">Owned resources in dependency-safe disposal order.</param>
    /// <param name="cancellationToken">The shared remaining shutdown deadline.</param>
    /// <returns>Whether every cleanup completed successfully within the deadline.</returns>
    internal async Task<bool> DisposeAllWithinDeadlineAsync(
        IReadOnlyList<IAsyncDisposable?> resources,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var disposals = new List<Task>(resources.Count);
        foreach (var resource in resources)
        {
            if (resource is null)
            {
                continue;
            }

            try
            {
                disposals.Add(resource.DisposeAsync().AsTask());
            }
            catch (Exception exception)
            {
                disposals.Add(Task.FromException(exception));
            }
        }

        bool completedWithinDeadline = true;
        foreach (var disposal in disposals)
        {
            try
            {
                await disposal.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                completedWithinDeadline = false;
                ObserveBackgroundFailure(disposal);
            }
            catch (Exception exception)
            {
                completedWithinDeadline = false;
                _logger.LogWarning(
                    "MCP HTTP transport resource cleanup failed with {FailureType} during bounded shutdown.",
                    exception.GetType().Name);
            }
        }

        return completedWithinDeadline;
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

    /// <summary>Maps a host-owned connection profile to SDK HTTP transport options.</summary>
    internal static HttpClientTransportOptions CreateOptions(
        McpConnectionProfile profile,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new HttpClientTransportOptions
        {
            Endpoint = new Uri(profile.Command, UriKind.Absolute),
            TransportMode = profile.Transport switch
            {
                McpTransport.Sse => HttpTransportMode.Sse,
                McpTransport.Http => HttpTransportMode.StreamableHttp,
                _ => throw new ArgumentException(
                    $"MCP transport '{profile.Transport}' is not an HTTP transport.",
                    nameof(profile)),
            },
            Name = $"threadsmith-{profile.Id}",
            ConnectionTimeout = profile.StartupTimeout,
            AdditionalHeaders = (headers ?? profile.Headers).ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>Resolves explicitly scoped logical header secrets without retaining them in profile state.</summary>
    internal async Task<IReadOnlyDictionary<string, string>> ResolveHeadersAsync(
        McpConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Headers.Count > 64)
        {
            throw new InvalidOperationException("An MCP profile may define at most 64 HTTP headers.");
        }

        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var request = new HttpRequestMessage();
        foreach (var header in profile.Headers)
        {
            if (header.Key.Length is 0 or > 128
                || !request.Headers.TryAddWithoutValidation(header.Key, "validation"))
            {
                throw new InvalidOperationException($"MCP profile '{profile.Id}' contains an invalid HTTP header name.");
            }

            string value = header.Value;
            if (value.StartsWith("secrets:", StringComparison.OrdinalIgnoreCase))
            {
                if (!profile.SecretScope.Contains(value, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"MCP header secret reference '{value}' is outside profile '{profile.Id}' secretScope.");
                }

                var secretRequest = new SecretResolutionRequest
                {
                    Reference = SecretReference.Parse(value),
                    ComponentId = SecretResolutionRequest.CreateConfiguredComponentId("mcp:http", profile.Id),
                    Purpose = "authenticate an explicitly scoped MCP HTTP connection",
                    MinimumTrust = SecretProviderTrust.UserOwned,
                };
                var resolution = await _secretResolver.ResolveAsync(secretRequest, cancellationToken);
                value = resolution.RequireValue(secretRequest);
            }

            if (value.Length > 8192 || value.Any(char.IsControl))
            {
                throw new InvalidOperationException(
                    $"MCP profile '{profile.Id}' contains an invalid HTTP header value.");
            }

            resolved[header.Key] = value;
        }

        return resolved;
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
