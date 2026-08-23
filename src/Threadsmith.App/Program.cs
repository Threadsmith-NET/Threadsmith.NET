namespace Threadsmith.App;

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;
using Threadsmith.Extensions.Runtime;
using Threadsmith.Hooks;
using Threadsmith.Mcp;
using Threadsmith.Models.OpenAiCodex;
using Threadsmith.Tools;

/// <summary>Process entry point and composition root for Threadsmith.NET.</summary>
public static class Program
{
    /// <summary>Runs the TUI or the headless command surface.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>Process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args);
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Threadsmith was canceled.");
            return 130;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync(FormatFatalError(exception));
            return 1;
        }
    }

    /// <summary>Runs the composed application after the process-level failure boundary.</summary>
    internal static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // Parse host-owned switches before configuration so invalid command lines fail without side effects.
        var parsedCommandLine = CommandLineParser.Parse(args);
        if (parsedCommandLine.Error is { } commandLineError)
        {
            await Console.Error.WriteLineAsync(commandLineError);
            return 2;
        }

        var commandLine = parsedCommandLine.Options
            ?? throw new InvalidOperationException("Successful command-line parsing did not produce options.");
        if (commandLine.ShowVersion)
        {
            var informationalVersion = typeof(Program).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
                ?? typeof(Program).Assembly.GetName().Version?.ToString()
                ?? "unknown";
            await Console.Out.WriteLineAsync(informationalVersion.Split('+', 2)[0]);
            return 0;
        }

        if (commandLine.ShowHelp)
        {
            await Console.Out.WriteLineAsync(
                "Threadsmith.NET\nUsage: threadsmith [--tui] [--repository PATH] [--solution PATH] [--trust LEVEL] [--raw-model-log PATH] [REQUEST]\n"
                + "       threadsmith --mcp ACTION [PROFILE] [CAPABILITY] [key=value ...] [--confirm] [--revoke-current] [--allow-local-cleanup]\n"
                + "       threadsmith [--tui] --codex-login | --codex-status | --codex-logout\n"
                + "       threadsmith --version");
            return 0;
        }

        var codexAuthenticationAction = commandLine.CodexAuthenticationAction;
        if (codexAuthenticationAction is null
            && commandLine.RequestArguments.Count is 2 or 3
            && string.Equals(commandLine.RequestArguments[0], "/auth", StringComparison.OrdinalIgnoreCase)
            && string.Equals(commandLine.RequestArguments[1], "openai-codex", StringComparison.OrdinalIgnoreCase))
        {
            codexAuthenticationAction = commandLine.RequestArguments.Count == 3
                ? commandLine.RequestArguments[2].ToLowerInvariant()
                : "login";
        }

        if (codexAuthenticationAction is not null)
        {
            return await RunCodexAuthenticationAsync(
                codexAuthenticationAction,
                commandLine.UseInteractiveTerminal,
                CancellationToken.None);
        }

        // Resolve every configuration path once so all later phases share one normalized repository identity.
        var paths = ConfigurationBootstrap.ResolvePaths(commandLine.RequestedRepository);
        ScaffoldUserConfigurationIfMissing(paths.UserConfiguration);

        // Build the effective configuration only after bounding untrusted repository-owned files.
        IConfigurationRoot configuration;
        IConfigurationRoot trustedConfiguration;
        try
        {
            configuration = ConfigurationBootstrap.Build(args, paths);
            trustedConfiguration = ConfigurationBootstrap.BuildTrusted(paths);
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        {
            await Console.Error.WriteLineAsync(ConfigurationBootstrap.FormatLoadError(exception));
            return 2;
        }

        // Use concise phase-local aliases while retaining normalized values from the immutable startup records.
        var repositoryRoot = paths.RepositoryRoot;
        var useInteractiveTerminal = commandLine.UseInteractiveTerminal
            && commandLine.McpAction is null;

        using var loggerFactory = LoggerFactory.Create(builder => builder.AddDebug());

        // Initialize durable state and shared host services before composing applications that consume them.
        await using var foundation = await HostFoundation.CreateAsync(
            configuration,
            trustedConfiguration,
            paths,
            loggerFactory);

        // Compose model transport, catalogs, migration, selection, and offline fallback as one owned phase.
        ModelServices composedModels;
        try
        {
            composedModels = await ModelComposition.CreateAsync(
                configuration,
                paths,
                foundation.SecretResolver,
                loggerFactory,
                commandLine.RawModelLogPath,
                trustedConfiguration);
        }
        catch (InvalidOperationException exception) when (!string.IsNullOrWhiteSpace(commandLine.RawModelLogPath))
        {
            await Console.Error.WriteLineAsync(exception.Message);
            return 2;
        }

        using var models = composedModels;
        using var processCancellation = new CancellationTokenSource();

        // Capability-backed hook transports must exist before optional integrations publish
        // their connected lifecycle boundaries.
        foundation.HookCoordinator.RegisterAdapter(new Threadsmith.Hooks.McpHookAdapter(
            (descriptor, envelope, cancellationToken) => InvokeCapabilityHookAsync(
                foundation.ToolPipeline,
                descriptor,
                envelope,
                cancellationToken)));
        foundation.HookCoordinator.RegisterAdapter(new ExtensionHookAdapter(
            (descriptor, envelope, cancellationToken) => InvokeCapabilityHookAsync(
                foundation.ToolPipeline,
                descriptor,
                envelope,
                cancellationToken)));

        // Compose MCP before the dispatcher so startup, commands, headless use, and shutdown share one authority.
        await using var mcp = await IntegrationComposition.CreateMcpManagerAsync(
            trustedConfiguration,
            useInteractiveTerminal,
            foundation.SecretResolver,
            foundation.Sanitizer,
            foundation.ToolRegistry,
            foundation.ToolStateManager,
            foundation.ToolPipeline,
            paths.RepositoryRoot,
            loggerFactory,
            foundation.HookCoordinator,
            processCancellation.Token);

        // Compose all command applications around one shared context, policy, and mutation coordinator.
        await using var applications = await ApplicationComposition.CreateAsync(
            new ApplicationCompositionInputs
            {
                Host = new HostCompositionInputs
                {
                    Configuration = configuration,
                    TrustedConfiguration = trustedConfiguration,
                    Paths = paths,
                    LoggerFactory = loggerFactory,
                    Events = foundation.Events,
                    Projections = foundation.Projections,
                    ExecutionLimits = foundation.ExecutionLimits,
                    Sanitizer = foundation.Sanitizer,
                    PromptAppendLoader = foundation.PromptAppendLoader,
                    Budget = foundation.Budget,
                },
                Persistence = new PersistenceCompositionInputs
                {
                    ConversationStore = foundation.ConversationStore,
                    RepositoryMemoryStore = foundation.RepositoryMemoryStore,
                    SessionLifecycleStore = foundation.SessionLifecycleStore,
                    SessionRestorer = foundation.SessionRestorer,
                    ArtifactStore = foundation.ArtifactStore,
                    ExecutionCheckpoints = foundation.ExecutionCheckpoints,
                    DelegationCheckpoints = foundation.DelegationCheckpoints,
                    SkillStateStore = foundation.SkillStateStore,
                    HookStore = foundation.HookStore,
                    EvidenceStore = foundation.EvidenceStore,
                    RepositoryFacts = foundation.RepositoryFacts,
                },
                Tools = new ToolPolicyCompositionInputs
                {
                    ToolPipeline = foundation.ToolPipeline,
                    ToolRegistry = foundation.ToolRegistry,
                    ToolStateManager = foundation.ToolStateManager,
                    WebFetchAuthorization = foundation.WebFetchAuthorization,
                    RepositorySecretProvider = foundation.RepositorySecretProvider,
                    ProcessManager = foundation.ProcessManager,
                    HookCoordinator = foundation.HookCoordinator,
                },
                Semantic = new SemanticCompositionInputs
                {
                    SemanticEngines = foundation.SemanticEngines,
                    SemanticMutations = foundation.SemanticMutations,
                },
                Integration = new IntegrationCompositionInputs
                {
                    McpManager = mcp,
                    Models = models,
                },
            });
        var dispatcher = applications.Dispatcher;

        // MCP management is machine-oriented and must reach its single JSON projection before optional
        // extension startup can write diagnostics. Ordinary terminal modes retain extension composition.
        ExtensionHost? extensionHost = null;
        if (IntegrationComposition.ShouldComposeExtensionHost(commandLine))
        {
            extensionHost = await IntegrationComposition.CreateExtensionHostAsync(
                repositoryRoot,
                useInteractiveTerminal,
                foundation.Events,
                foundation.ExtensionCapabilityRegistry,
                foundation.ExtensionLeaseAuthority,
                loggerFactory,
                processCancellation.Token);
        }

        // Run the chosen terminal mode only after its required host services are ready.
        return await ShellRunner.RunAsync(
            new ShellRunContext
            {
                CommandLine = commandLine,
                Paths = paths,
                Configuration = configuration,
                Dispatcher = dispatcher,
                Projections = foundation.Projections,
                Events = foundation.Events,
                Models = models,
                Applications = applications,
                ExtensionHost = extensionHost,
                ToolStateManager = foundation.ToolStateManager,
                WebFetchAuthorization = foundation.WebFetchAuthorization,
                DirectFetchApprovalPrompt = foundation.DirectFetchApprovalPrompt,
            },
            processCancellation);
    }

    /// <summary>Formats a bounded single-line fatal error without exposing a stack trace.</summary>
    internal static string FormatFatalError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = string.Join(
            ' ',
            exception.Message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (message.Length == 0)
        {
            message = "An unexpected error occurred.";
        }
        else if (message.Length > 512)
        {
            message = message[..512] + "…";
        }

        return $"Threadsmith could not start or continue: {message}";
    }

    /// <summary>Runs the standalone host-owned Codex authentication surface.</summary>
    /// <param name="action">Authentication action.</param>
    /// <param name="useInteractiveTerminal">Whether browser-loopback login may be used.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Process exit code.</returns>
    internal static async Task<int> RunCodexAuthenticationAsync(
        string action,
        bool useInteractiveTerminal,
        CancellationToken cancellationToken)
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var oauth = new OpenAiCodexOAuthManager(httpClient);
        var cache = new OpenAiCodexCatalogCache();
        switch (action)
        {
            case "status":
                var status = await oauth.GetStatusAsync(cancellationToken);
                await Console.Out.WriteLineAsync(status.IsAuthenticated
                    ? $"OpenAI Codex authenticated; token expires {status.ExpiresAt:O}."
                    : "OpenAI Codex is not authenticated.");
                return status.IsAuthenticated ? 0 : 1;
            case "logout":
                await oauth.LogoutAsync();
                await cache.ClearAsync();
                await Console.Out.WriteLineAsync("OpenAI Codex authentication removed from Threadsmith.");
                return 0;
            case "login":
                if (useInteractiveTerminal)
                {
                    var listener = new LoopbackOAuthCallbackListener();
                    var listenerReservation = listener.ReserveRedirectUri(1455);
                    var redirectUri = new Uri($"http://localhost:{listenerReservation.Port}/auth/callback");
                    var challenge = OpenAiCodexOAuthManager.CreateBrowserChallenge(redirectUri);
                    await new SystemBrowserLauncher().LaunchAsync(challenge.AuthorizationUri, cancellationToken);
                    var callback = await listener.WaitForCallbackAsync(listenerReservation, cancellationToken);
                    await oauth.CompleteBrowserAsync(challenge, callback, cancellationToken);
                }
                else
                {
                    var challenge = await oauth.StartDeviceAsync(cancellationToken);
                    await Console.Out.WriteLineAsync($"Open {challenge.VerificationUri} and enter code {challenge.UserCode}.");
                    await oauth.CompleteDeviceAsync(challenge, TimeSpan.FromMinutes(10), cancellationToken);
                }

                var accessToken = await oauth.GetAccessTokenAsync(cancellationToken)
                    ?? throw new InvalidOperationException("Codex authentication completed without an access token.");
                var catalog = await new OpenAiCodexCatalogClient(httpClient)
                    .DiscoverAsync(accessToken, cancellationToken: cancellationToken);
                await cache.SaveAsync(catalog, cancellationToken);
                await Console.Out.WriteLineAsync($"OpenAI Codex authenticated; discovered {catalog.Models.Count} models.");
                return 0;
            default:
                await Console.Error.WriteLineAsync(
                    "Unknown Codex authentication action; use login, status, or logout.");
                return 2;
        }
    }

    /// <summary>
    /// Preserves the existing testable entry point while delegating first-launch configuration work to
    /// the dedicated configuration bootstrap phase.
    /// </summary>
    /// <param name="userConfigPath">The absolute user configuration file path.</param>
    internal static void ScaffoldUserConfigurationIfMissing(string userConfigPath)
    {
        ConfigurationBootstrap.ScaffoldUserConfigurationIfMissing(userConfigPath);
    }

    private static async Task<HookHandlerResult> InvokeCapabilityHookAsync(
        IToolInvocationPipeline toolPipeline,
        HookHandlerDescriptor descriptor,
        HookInvocationEnvelope envelope,
        CancellationToken cancellationToken)
    {
        (var toolId, var registration) = ResolveCapabilityHookRegistration(
            toolPipeline,
            descriptor);
        var result = await toolPipeline.InvokeAsync(
            new ToolInvocationRequest
            {
                ExpectedRegistration = registration,
                SessionId = envelope.SessionId,
                RunId = envelope.RunId ?? default,
                Phase = RunPhase.Intake,
                ToolId = toolId,
                ArgumentsJson = JsonSerializer.Serialize(envelope),
                Context = new ToolInvocationContext
                {
                    RepositoryPath = envelope.RepositoryIdentity ?? Directory.GetCurrentDirectory(),
                    TrustLevel = RepositoryTrustLevel.UntrustedInspection,
                    AllowedToolIds = [toolId],
                    RequestedBy = $"hook:{descriptor.Identity.Id.Value}",
                },
            },
            cancellationToken);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.ResultJson))
        {
            return new HookFailureResult(
                result.ErrorClassification.ToString(),
                result.Error ?? "The capability hook failed.");
        }

        return JsonSerializer.Deserialize<HookHandlerResult>(result.ResultJson)
            ?? new HookFailureResult("malformed-capability-result", "The capability hook returned malformed output.");
    }

    private static (string ToolId, ITool Registration) ResolveCapabilityHookRegistration(
        IToolInvocationPipeline toolPipeline,
        HookHandlerDescriptor descriptor)
    {
        if (toolPipeline is not ToolInvocationPipeline concretePipeline)
        {
            throw new InvalidOperationException("Capability hooks require the host-owned tool pipeline.");
        }

        var target = descriptor.Target.Split("::", StringSplitOptions.None);
        var toolId = descriptor.AdapterKind switch
        {
            HookAdapterKind.Mcp when target.Length == 4 => target[3],
            HookAdapterKind.Extension when target.Length == 2 => target[1],
            _ => throw new InvalidOperationException(
                "MCP hook targets must be 'profile::server::schema-digest::tool' and extension hook targets must be 'generation::tool'."),
        };
        var registration = concretePipeline.Registry.Get(toolId);
        var identityMatches = descriptor.AdapterKind switch
        {
            HookAdapterKind.Mcp when registration is McpImportedTool mcp =>
                string.Equals(mcp.Profile.Id, target[0], StringComparison.Ordinal)
                && string.Equals(mcp.Capability.ServerName, target[1], StringComparison.Ordinal)
                && string.Equals(
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mcp.Capability.InputSchemaJson ?? "{}")))
                        .ToLowerInvariant(),
                    target[2],
                    StringComparison.Ordinal),
            HookAdapterKind.Extension when registration is CapabilityProxy extension =>
                Guid.TryParse(target[0], out var generationId)
                && extension.GenerationId == new ExtensionGenerationId(generationId),
            _ => false,
        };
        return identityMatches
            ? (toolId, registration)
            : throw new InvalidOperationException("The connected capability does not match the hook's approved immutable identity.");
    }
}
