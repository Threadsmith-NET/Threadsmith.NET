namespace Threadsmith.App;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Extensions.Runtime;
using Threadsmith.Mcp;
using Threadsmith.Telemetry;
using Threadsmith.Tools;

/// <summary>Composes optional extension and MCP integrations around host-owned boundaries.</summary>
internal static class IntegrationComposition
{
    /// <summary>Determines whether ordinary startup should compose optional extensions.</summary>
    internal static bool ShouldComposeExtensionHost(CommandLineOptions commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        return commandLine.McpAction is null;
    }

    /// <summary>Creates the extension host and auto-loads only repository-selected extensions for interactive use.</summary>
    internal static async Task<ExtensionHost> CreateExtensionHostAsync(
        string repositoryRoot,
        bool useInteractiveTerminal,
        DomainEventStream events,
        CapabilityRegistry capabilityRegistry,
        InvocationLeaseAuthority leaseAuthority,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken = default)
    {
        var extensionHost = new ExtensionHost(
            events,
            loggerFactory.CreateLogger<ExtensionHost>(),
            capabilityRegistry: capabilityRegistry,
            leaseAuthority: leaseAuthority,
            extensionLoggerFactory: loggerFactory);
        var selectionPath = Path.Combine(repositoryRoot, ".threadsmith", "extensions.json");
        var selection = ExtensionSelectionConfig.LoadOrDefault(selectionPath);
        extensionHost.SetDiscoveryDirectory(
            Path.GetFullPath(selection.DiscoveryDirectory, repositoryRoot));
        if (!useInteractiveTerminal || selection.AutoLoad.Count == 0)
        {
            return extensionHost;
        }

        // Extension failures are isolated so one optional integration cannot prevent the shell from starting.
        var startupLogger = loggerFactory.CreateLogger("Threadsmith.Startup.Extensions");
        await extensionHost.DiscoverAsync(cancellationToken);
        foreach (var extensionId in selection.AutoLoad)
        {
            try
            {
                await ((IExtensionManager)extensionHost).LoadAsync(
                    extensionId,
                    SessionId.New(),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The host also publishes ExtensionLoadFailed, while this log gives immediate startup context.
                startupLogger.LogError(
                    exception,
                    "Extension '{ExtensionId}' failed to load: {Message}",
                    extensionId,
                    exception.Message);
                await Console.Out.WriteLineAsync(
                    $"Extension '{extensionId}' failed to load: {exception.Message}");
            }
        }

        return extensionHost;
    }

    /// <summary>Creates the single MCP lifecycle manager shared by startup and both shell surfaces.</summary>
    internal static async Task<IMcpManager> CreateMcpManagerAsync(
        IConfiguration trustedConfiguration,
        bool useInteractiveTerminal,
        ISecretResolver secretResolver,
        SecretOutputSanitizer sanitizer,
        ToolRegistry toolRegistry,
        IToolStateManager toolStateManager,
        IToolInvocationPipeline toolPipeline,
        string repositoryRoot,
        ILoggerFactory loggerFactory,
        IHookCoordinator? hookCoordinator = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trustedConfiguration);
        ArgumentNullException.ThrowIfNull(secretResolver);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(toolRegistry);
        ArgumentNullException.ThrowIfNull(toolStateManager);
        ArgumentNullException.ThrowIfNull(toolPipeline);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        IBrowserLauncher browserLauncher = useInteractiveTerminal
            ? new SystemBrowserLauncher()
            : new ConsoleBrowserLauncher(Console.Error);
        IOAuthCallbackListener callbackListener = useInteractiveTerminal
            ? new LoopbackOAuthCallbackListener()
            : new ConsoleOAuthCallbackListener(Console.In, Console.Error);
        var lifecycleSecretStore = new SecretResolverStoreAdapter(
            secretResolver,
            "mcp:oauth-token-cache",
            SecretProviderTrust.UserOwned);
        var tokenStore = new McpOAuthSecretStore(
            lifecycleSecretStore,
            logger: loggerFactory.CreateLogger<McpOAuthSecretStore>());
        var oauthFlow = new McpOAuthFlow(
            browserLauncher,
            callbackListener,
            tokenStore,
            secretResolver,
            loggerFactory.CreateLogger<McpOAuthFlow>());
        var adapter = new McpAdapter(
            profile => profile.Transport switch
            {
                McpTransport.Stdio => new SdkStdioTransport(sanitizer, loggerFactory),
                McpTransport.Sse or McpTransport.Http => new SdkHttpTransport(secretResolver, loggerFactory, oauthFlow),
                _ => throw new PlatformNotSupportedException(
                    $"MCP transport '{profile.Transport}' is not supported."),
            },
            secretResolver,
            sanitizer,
            loggerFactory.CreateLogger<McpAdapter>(),
            toolRegistry);
        var identityManager = new McpIdentityManager(tokenStore, secretResolver);
        var profiles = McpProfileConfigurationLoader.Load(trustedConfiguration);
        Func<McpConnectionResult, CancellationToken, Task>? connectedCallback = null;
        if (hookCoordinator is not null)
        {
            connectedCallback = (result, token) => InvokeMcpConnectedHookAsync(
                hookCoordinator,
                result,
                token);
        }

        var manager = new McpManager(
            profiles,
            adapter,
            toolStateManager,
            identityManager,
            sanitizer,
            loggerFactory.CreateLogger<McpManager>(),
            connectedCallback: connectedCallback,
            explicitReadAuthorizer: async (profile, capability, token) =>
            {
                var policyTool = new McpExplicitReadPolicyTool(profile, capability);
                var decision = await toolPipeline.InvokeAsync(
                    new ToolInvocationRequest
                    {
                        ExpectedRegistration = new ToolRegistration(
                            policyTool,
                            new ToolActivitySource(
                                ToolActivitySourceKind.Mcp,
                                profile.Id)),
                        SessionId = SessionId.New(),
                        RunId = RunId.New(),
                        Phase = RunPhase.Intake,
                        ToolId = capability.Id,
                        ArgumentsJson = "{}",
                        Context = new ToolInvocationContext
                        {
                            RepositoryPath = repositoryRoot,
                            TrustLevel = RepositoryTrustLevel.TrustedRead,
                            ApprovedRoots = ["."],
                            AllowedNetworkHosts = trustedConfiguration
                                .GetSection("tools:allowedNetworkHosts").Get<string[]>() ?? [],
                            AllowedToolIds = trustedConfiguration.GetSection("tools:allow").Get<string[]>() ?? [],
                            DeniedToolIds = trustedConfiguration.GetSection("tools:deny").Get<string[]>() ?? [],
                            RequireApprovalToolIds = trustedConfiguration
                                .GetSection("tools:requireApproval").Get<string[]>() ?? [],
                            AllowedSecretReferences = trustedConfiguration
                                .GetSection("tools:allowedSecretReferences").Get<string[]>() ?? [],
                            RequestedBy = "host:mcp-explicit-read",
                        },
                    },
                    token);
                return decision.Succeeded;
            });
        try
        {
            await manager.AutoConnectAsync(cancellationToken);
            return manager;
        }
        catch
        {
            await manager.DisposeAsync();
            throw;
        }
    }

    /// <summary>Creates the MCP adapter for legacy composition tests using the former store boundary.</summary>
    internal static Task<IMcpAdapter> CreateMcpAdapterAsync(
        IConfiguration trustedConfiguration,
        bool useInteractiveTerminal,
        ConfigurationSecretStore secretStore,
        SecretOutputSanitizer sanitizer,
        ToolRegistry toolRegistry,
        ILoggerFactory loggerFactory,
        IHookCoordinator? hookCoordinator = null,
        CancellationToken cancellationToken = default)
    {
        return CreateMcpAdapterAsync(
                trustedConfiguration,
                useInteractiveTerminal,
                new LegacySecretStoreResolver(secretStore),
                sanitizer,
                toolRegistry,
                loggerFactory,
                hookCoordinator,
                cancellationToken);
    }

    /// <summary>Creates the host-owned MCP adapter and auto-connects configured profiles that request it.</summary>
    internal static async Task<IMcpAdapter> CreateMcpAdapterAsync(
        IConfiguration trustedConfiguration,
        bool useInteractiveTerminal,
        ISecretResolver secretResolver,
        SecretOutputSanitizer sanitizer,
        ToolRegistry toolRegistry,
        ILoggerFactory loggerFactory,
        IHookCoordinator? hookCoordinator = null,
        CancellationToken cancellationToken = default)
    {
        IBrowserLauncher browserLauncher = useInteractiveTerminal
            ? new SystemBrowserLauncher()
            : new ConsoleBrowserLauncher(Console.Error);
        IOAuthCallbackListener callbackListener = useInteractiveTerminal
            ? new LoopbackOAuthCallbackListener()
            : new ConsoleOAuthCallbackListener(Console.In, Console.Error);
        var lifecycleSecretStore = new SecretResolverStoreAdapter(
            secretResolver,
            "mcp:oauth-token-cache",
            SecretProviderTrust.UserOwned);
        var oauthFlow = new McpOAuthFlow(
            browserLauncher,
            callbackListener,
            new McpOAuthSecretStore(
                lifecycleSecretStore,
                logger: loggerFactory.CreateLogger<McpOAuthSecretStore>()),
            secretResolver,
            loggerFactory.CreateLogger<McpOAuthFlow>());
        var adapter = new McpAdapter(
            profile => profile.Transport switch
            {
                McpTransport.Stdio => new SdkStdioTransport(sanitizer, loggerFactory),
                McpTransport.Sse or McpTransport.Http => new SdkHttpTransport(secretResolver, loggerFactory, oauthFlow),
                _ => throw new PlatformNotSupportedException(
                    $"MCP transport '{profile.Transport}' is not supported."),
            },
            secretResolver,
            sanitizer,
            loggerFactory.CreateLogger<McpAdapter>(),
            toolRegistry);
        var profiles = McpProfileConfigurationLoader.Load(trustedConfiguration);
        var startupLogger = loggerFactory.CreateLogger("Threadsmith.Startup.Mcp");
        if (profiles.Count > 0)
        {
            startupLogger.LogInformation("Loaded {Count} MCP connection profile(s).", profiles.Count);
        }

        try
        {
            foreach (var profile in profiles.Where(profile => profile.AutoConnect))
            {
                try
                {
                    var result = await adapter.ConnectAsync(profile, cancellationToken);
                    if (!result.Succeeded)
                    {
                        startupLogger.LogWarning(
                            "MCP profile '{ProfileId}' failed to auto-connect: {Error}",
                            profile.Id,
                            result.Status.Error);
                    }
                    else if (hookCoordinator is not null)
                    {
                        _ = await hookCoordinator.InvokeAsync(
                            HookPoint.McpConnected,
                            SessionId.New(),
                            null,
                            null,
                            Guid.NewGuid(),
                            0,
                            new Dictionary<string, string>
                            {
                                ["profileId"] = result.ProfileId,
                                ["capabilityCount"] = result.Capabilities.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                ["toolCount"] = result.Tools.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            },
                            cancellationToken: cancellationToken);
                    }
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    startupLogger.LogWarning(
                        "MCP profile '{ProfileId}' could not auto-connect: {Error}",
                        profile.Id,
                        sanitizer.Sanitize(exception.Message));
                }
            }

            return adapter;
        }
        catch
        {
            await adapter.DisposeAsync();
            throw;
        }
    }

    private static async Task InvokeMcpConnectedHookAsync(
        IHookCoordinator hookCoordinator,
        McpConnectionResult result,
        CancellationToken cancellationToken)
    {
        _ = await hookCoordinator.InvokeAsync(
            HookPoint.McpConnected,
            SessionId.New(),
            null,
            null,
            Guid.NewGuid(),
            0,
            new Dictionary<string, string>
            {
                ["profileId"] = result.ProfileId,
                ["capabilityCount"] = result.Capabilities.Count.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                ["toolCount"] = result.Tools.Count.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            },
            cancellationToken: cancellationToken);
    }
}
