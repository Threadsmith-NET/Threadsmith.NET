namespace Threadsmith.App;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Extensions.Runtime;
using Threadsmith.Hooks;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Validation;
using Threadsmith.Workspaces;

/// <summary>Initializes and owns foundational event, persistence, semantic, process, and tool services.</summary>
internal sealed class HostFoundation : IAsyncDisposable
{
    private readonly IDomainEventSubscription _contextLifecycleSubscription;
    private readonly IDomainEventSubscription _hookSubscription;
    private readonly HookEventObserver _hookObserver;
    private readonly HttpClient _hookHttpClient;
    private readonly IDomainEventSubscription _persistenceSubscription;
    private readonly IDomainEventSubscription _projectionSubscription;
    private readonly IDomainEventSubscription _semanticSubscription;
    private readonly SemanticLifecycleObserver _semanticObserver;
    private readonly IDomainEventSubscription _telemetrySubscription;
    private readonly IDomainEventSubscription _webFetchLifecycleSubscription;

    /// <summary>Initializes a new instance of the <see cref="HostFoundation"/> class.</summary>
    private HostFoundation(
        DomainEventStream events,
        InMemoryProjectionStore projections,
        ExecutionLimits executionLimits,
        OperationalLimits operationalLimits,
        SecretOutputSanitizer sanitizer,
        SqliteRepositoryFactsStore repositoryFacts,
        SqliteConversationStore conversationStore,
        RepositoryBoundMemoryStore repositoryMemoryStore,
        SqliteSessionLifecycleStore sessionLifecycleStore,
        SessionRestorer sessionRestorer,
        ArtifactStore artifactStore,
        ExecutionCheckpointStore executionCheckpoints,
        DelegationCheckpointStore delegationCheckpoints,
        SqliteSkillStateStore skillStateStore,
        SqliteHookStore hookStore,
        HookCoordinator hookCoordinator,
        HttpClient hookHttpClient,
        EvidenceStore evidenceStore,
        IPromptLoader promptLoader,
        PromptAppendLoader promptAppendLoader,
        ExecutionBudget budget,
        ISecretResolver secretResolver,
        RepositorySecretProvider repositorySecretProvider,
        SemanticEngineRegistry semanticEngines,
        SemanticRefreshCoordinator semanticRefreshCoordinator,
        SemanticRefreshPublicationGateRouter semanticRefreshPublicationGate,
        SemanticMutationEngine semanticMutations,
        ProcessManager processManager,
        ToolStateManager toolStateManager,
        ToolRegistry toolRegistry,
        CodeExploreOutputOptions codeExploreOutputOptions,
        WebFetchAuthorizationAuthority webFetchAuthorization,
        DirectFetchApprovalPromptRouter directFetchApprovalPrompt,
        InvocationLeaseAuthority extensionLeaseAuthority,
        CapabilityRegistry extensionCapabilityRegistry,
        ToolInvocationPipeline toolPipeline,
        IDomainEventSubscription projectionSubscription,
        IDomainEventSubscription persistenceSubscription,
        IDomainEventSubscription telemetrySubscription,
        IDomainEventSubscription contextLifecycleSubscription,
        IDomainEventSubscription hookSubscription,
        IDomainEventSubscription webFetchLifecycleSubscription,
        HookEventObserver hookObserver,
        SemanticLifecycleObserver semanticObserver,
        IDomainEventSubscription semanticSubscription)
    {
        Events = events;
        Projections = projections;
        ExecutionLimits = executionLimits;
        OperationalLimits = operationalLimits;
        Sanitizer = sanitizer;
        RepositoryFacts = repositoryFacts;
        ConversationStore = conversationStore;
        RepositoryMemoryStore = repositoryMemoryStore;
        SessionLifecycleStore = sessionLifecycleStore;
        SessionRestorer = sessionRestorer;
        ArtifactStore = artifactStore;
        ExecutionCheckpoints = executionCheckpoints;
        DelegationCheckpoints = delegationCheckpoints;
        SkillStateStore = skillStateStore;
        HookStore = hookStore;
        HookCoordinator = hookCoordinator;
        _hookHttpClient = hookHttpClient;
        EvidenceStore = evidenceStore;
        PromptLoader = promptLoader;
        PromptAppendLoader = promptAppendLoader;
        Budget = budget;
        SecretResolver = secretResolver;
        RepositorySecretProvider = repositorySecretProvider;
        SemanticEngines = semanticEngines;
        SemanticRefreshCoordinator = semanticRefreshCoordinator;
        SemanticRefreshPublicationGate = semanticRefreshPublicationGate;
        SemanticMutations = semanticMutations;
        ProcessManager = processManager;
        ToolStateManager = toolStateManager;
        ToolRegistry = toolRegistry;
        CodeExploreOutputOptions = codeExploreOutputOptions;
        WebFetchAuthorization = webFetchAuthorization;
        DirectFetchApprovalPrompt = directFetchApprovalPrompt;
        ExtensionLeaseAuthority = extensionLeaseAuthority;
        ExtensionCapabilityRegistry = extensionCapabilityRegistry;
        ToolPipeline = toolPipeline;
        _projectionSubscription = projectionSubscription;
        _persistenceSubscription = persistenceSubscription;
        _telemetrySubscription = telemetrySubscription;
        _contextLifecycleSubscription = contextLifecycleSubscription;
        _hookSubscription = hookSubscription;
        _webFetchLifecycleSubscription = webFetchLifecycleSubscription;
        _hookObserver = hookObserver;
        _semanticObserver = semanticObserver;
        _semanticSubscription = semanticSubscription;
    }

    /// <summary>Disposes event observers in reverse dependency order before closing the event stream.</summary>
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        RepositoryMemoryStore.Dispose();
        WebFetchAuthorization.RevokeAll();
        await _webFetchLifecycleSubscription.DisposeAsync();
        DirectFetchApprovalPrompt.Dispose();
        await _semanticSubscription.DisposeAsync();
        await _semanticObserver.DisposeAsync();
        await SemanticRefreshCoordinator.DisposeAsync();
        await _hookSubscription.DisposeAsync();
        await _hookObserver.DisposeAsync();
        await HookCoordinator.DisposeAsync();
        _hookHttpClient.Dispose();
        await SemanticEngines.DisposeAsync();
        await _contextLifecycleSubscription.DisposeAsync();
        await _telemetrySubscription.DisposeAsync();
        await _persistenceSubscription.DisposeAsync();
        await _projectionSubscription.DisposeAsync();
        await Events.DisposeAsync();
    }

    /// <summary>Gets the shared ordered domain event stream.</summary>
    internal DomainEventStream Events { get; }

    /// <summary>Gets in-memory read projections.</summary>
    internal InMemoryProjectionStore Projections { get; }

    /// <summary>Gets bounded execution-loop limits.</summary>
    internal ExecutionLimits ExecutionLimits { get; }

    /// <summary>Validated application resource policy for this host.</summary>
    internal OperationalLimits OperationalLimits { get; }

    /// <summary>Gets the shared secret-output sanitizer.</summary>
    internal SecretOutputSanitizer Sanitizer { get; }

    /// <summary>Gets durable machine-level repository discovery facts.</summary>
    internal SqliteRepositoryFactsStore RepositoryFacts { get; }

    /// <summary>Gets the durable sanitized conversation archive and governed memory store.</summary>
    internal SqliteConversationStore ConversationStore { get; }

    /// <summary>Gets durable local repository-scoped cross-session memory storage.</summary>
    internal RepositoryBoundMemoryStore RepositoryMemoryStore { get; }

    /// <summary>Gets repository-bound durable session metadata and clone storage.</summary>
    internal SqliteSessionLifecycleStore SessionLifecycleStore { get; }

    /// <summary>Gets tolerant composite event/conversation restoration.</summary>
    internal SessionRestorer SessionRestorer { get; }

    /// <summary>Gets sanitized content-addressed artifact storage.</summary>
    internal ArtifactStore ArtifactStore { get; }

    /// <summary>Gets atomic approved-plan execution checkpoints.</summary>
    internal ExecutionCheckpointStore ExecutionCheckpoints { get; }

    /// <summary>Gets atomic parallel-agent delegation checkpoints.</summary>
    internal DelegationCheckpointStore DelegationCheckpoints { get; }

    /// <summary>Gets atomic governed-skill pins and workflow checkpoints.</summary>
    internal SqliteSkillStateStore SkillStateStore { get; }

    /// <summary>Gets durable external repository-hook approvals and audit.</summary>
    internal SqliteHookStore HookStore { get; }

    /// <summary>Gets the host-owned lifecycle hook coordinator.</summary>
    internal HookCoordinator HookCoordinator { get; }

    /// <summary>Gets governed session evidence storage.</summary>
    internal EvidenceStore EvidenceStore { get; }

    /// <summary>Gets the immutable deployed prompt catalog.</summary>
    internal IPromptLoader PromptLoader { get; }

    /// <summary>Gets bounded prompt-append loading.</summary>
    internal PromptAppendLoader PromptAppendLoader { get; }

    /// <summary>Gets the shared execution budget.</summary>
    internal ExecutionBudget Budget { get; }

    /// <summary>Gets layered just-in-time secret resolution.</summary>
    internal ISecretResolver SecretResolver { get; }

    /// <summary>Gets the repository-rebindable lower-trust secret provider.</summary>
    internal RepositorySecretProvider RepositorySecretProvider { get; }

    /// <summary>Gets the repository semantic-engine registry.</summary>
    internal SemanticEngineRegistry SemanticEngines { get; }

    /// <summary>Gets the single workspace semantic-refresh authority.</summary>
    internal SemanticRefreshCoordinator SemanticRefreshCoordinator { get; }

    /// <summary>Gets the deferred application-owned semantic publication boundary.</summary>
    internal SemanticRefreshPublicationGateRouter SemanticRefreshPublicationGate { get; }

    /// <summary>Gets semantic mutation operations.</summary>
    internal SemanticMutationEngine SemanticMutations { get; }

    /// <summary>Gets tracked external process execution.</summary>
    internal ProcessManager ProcessManager { get; }

    /// <summary>Gets persisted repository-scoped tool availability.</summary>
    internal ToolStateManager ToolStateManager { get; }

    /// <summary>Gets the effective built-in and dynamic tool registry.</summary>
    internal ToolRegistry ToolRegistry { get; }

    /// <summary>Gets host-owned per-session code_explore output presentation state.</summary>
    internal CodeExploreOutputOptions CodeExploreOutputOptions { get; }

    /// <summary>Gets transient host-owned web-fetch activation and direct authorization.</summary>
    internal WebFetchAuthorizationAuthority WebFetchAuthorization { get; }

    /// <summary>Gets the serialized interactive/headless direct-fetch approval boundary.</summary>
    internal DirectFetchApprovalPromptRouter DirectFetchApprovalPrompt { get; }

    /// <summary>Gets extension invocation lease ownership.</summary>
    internal InvocationLeaseAuthority ExtensionLeaseAuthority { get; }

    /// <summary>Gets the extension capability registry connected to tools.</summary>
    internal CapabilityRegistry ExtensionCapabilityRegistry { get; }

    /// <summary>Gets the governed tool invocation pipeline.</summary>
    internal ToolInvocationPipeline ToolPipeline { get; }

    /// <summary>Binds and validates resource settings, rejecting misspelled or unsupported keys.</summary>
    internal static OperationalLimits LoadOperationalLimits(IConfiguration configuration, IConfiguration? trustedConfiguration = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var limits = configuration.GetSection("limits").Get<OperationalLimits>(options => options.ErrorOnUnknownConfiguration = true)
            ?? new OperationalLimits();
        limits.Validate();
        var trustedDiffLines = (trustedConfiguration ?? configuration).GetValue(
            "limits:workspace:maximumDiffLinesForLcs", new WorkspaceResourceLimits().MaximumDiffLinesForLcs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(trustedDiffLines);
        return limits with
        {
            Workspace = limits.Workspace with
            {
                MaximumDiffLinesForLcs = Math.Min(limits.Workspace.MaximumDiffLinesForLcs, trustedDiffLines),
            },
        };
    }

    /// <summary>Allows repository prompt bounds to narrow the trusted host ceilings.</summary>
    internal static PromptAppendLimits LoadPromptAppendLimits(IConfiguration configuration, IConfiguration trustedConfiguration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(trustedConfiguration);
        const string section = "context:promptAppends:limits";
        var trusted = trustedConfiguration.GetSection(section).Get<PromptAppendLimits>(options => options.ErrorOnUnknownConfiguration = true) ?? new();
        var effective = configuration.GetSection(section).Get<PromptAppendLimits>(options => options.ErrorOnUnknownConfiguration = true) ?? trusted;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(trusted.MaximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(trusted.MaximumTotalBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(trusted.MaximumFileBytes, trusted.MaximumTotalBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(effective.MaximumFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(effective.MaximumTotalBytes);
        var total = Math.Min(effective.MaximumTotalBytes, trusted.MaximumTotalBytes);
        return new(Math.Min(Math.Min(effective.MaximumFileBytes, trusted.MaximumFileBytes), total), total);
    }

    /// <summary>Initializes persistence before subscribers, then semantic and tool services in dependency order.</summary>
    internal static async Task<HostFoundation> CreateAsync(
        IConfiguration configuration,
        IConfiguration trustedConfiguration,
        ConfigurationPaths paths,
        ILoggerFactory loggerFactory,
        IPromptLoader promptLoader)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(trustedConfiguration);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(promptLoader);

        var operationalLimits = LoadOperationalLimits(configuration, trustedConfiguration);
        var maximumCorrectiveTurns = configuration.GetValue("execution:maxCorrectiveTurns", 3);
        var executionLimits = new ExecutionLimits
        {
            MaxModelRounds = configuration.GetValue(
                "execution:maxModelRounds",
                ExecutionLimits.DefaultMaxModelRounds),
            MaxPlanningToolRounds = configuration.GetValue(
                "execution:maxPlanningToolRounds",
                ExecutionLimits.DefaultMaxPlanningToolRounds),
            MaxCorrectiveTurns = maximumCorrectiveTurns,
            Plan = operationalLimits.Plan,
            MaxSourceFrontierEntries = configuration.GetValue("execution:maxSourceFrontierEntries", 256),
            MaxPlanSanityIssues = configuration.GetValue("execution:maxPlanSanityIssues", 32),
            MaxSteeringCharacters = configuration.GetValue("execution:maxSteeringCharacters", 100000),
            MaxAgentDisplayFragments = configuration.GetValue("execution:maxAgentDisplayFragments", 256),
            MaxAgentDisplayFragmentCharacters = configuration.GetValue("execution:maxAgentDisplayFragmentCharacters", 4096),
            MaxAgentDisplayLineCharacters = configuration.GetValue("execution:maxAgentDisplayLineCharacters", 16384),

            MaxRetainedToolCalls = configuration.GetValue("execution:maxRetainedToolCalls", 256),
            MaxStructuredOutputCharacters = configuration.GetValue(
                "execution:maxStructuredOutputCharacters",
                8 * 1024 * 1024),
            MaxToolResultPreviewCharacters = configuration.GetValue(
                "execution:toolResultPreviewCharacters",
                4096),
        };
        var toolLimits = CreateToolLimits(configuration);
        executionLimits.Validate();
        var codeExploreOutputOptions = CodeExploreOutputOptions.FromConfiguration(configuration);
        var codeExploreOptions = CodeExploreConfiguration.FromConfiguration(configuration);
        var events = new DomainEventStream(TimeSpan.FromMilliseconds(trustedConfiguration.GetValue("events:committedDeliveryTimeoutMilliseconds", 5000)));
        var projections = new InMemoryProjectionStore(executionLimits);
        var subscriberCapacity = configuration.GetValue("events:subscriberCapacity", 256);
        var projectionSubscription = events.Subscribe(
            (domainEvent, cancellationToken) => domainEvent is ModelReasoningObserved
                ? Task.CompletedTask
                : projections.ApplyAsync(domainEvent, cancellationToken),
            subscriberCapacity);
        IDomainEventSubscription? persistenceSubscription = null;
        IDomainEventSubscription? telemetrySubscription = null;
        IDomainEventSubscription? contextSubscription = null;
        IDomainEventSubscription? hookSubscription = null;
        IDomainEventSubscription? semanticSubscription = null;
        IDomainEventSubscription? webFetchLifecycleSubscription = null;
        DirectFetchApprovalPromptRouter? directFetchApprovalPrompt = null;
        ContextLifecycleObserver? contextLifecycle = null;
        SemanticEngineRegistry? semanticEngines = null;
        SemanticRefreshCoordinator? semanticRefreshCoordinator = null;
        SemanticRefreshPublicationGateRouter? semanticRefreshPublicationGate = null;
        SemanticLifecycleObserver? semanticObserver = null;

        try
        {
            // Migrations and redaction complete before event persistence begins, preventing partial startup state.
            var sanitizer = new SecretOutputSanitizer();
            var persistence = await InitializePersistenceAsync(
                configuration,
                paths,
                sanitizer,
                events,
                loggerFactory);
            var eventStore = persistence.EventStore;
            var factsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Threadsmith");
            Directory.CreateDirectory(factsDirectory);
            var repositoryFacts = new SqliteRepositoryFactsStore(
                $"Data Source={Path.Combine(factsDirectory, "repository-facts.db")}");
            await repositoryFacts.InitializeAsync();
            persistenceSubscription = events.Subscribe(
                (domainEvent, cancellationToken) => domainEvent is ModelReasoningObserved
                    ? Task.CompletedTask
                    : eventStore.AppendAsync(domainEvent, cancellationToken),
                subscriberCapacity);

            var telemetry = new DomainEventTelemetry(loggerFactory.CreateLogger<DomainEventTelemetry>());
            telemetrySubscription = events.Subscribe(
                (domainEvent, cancellationToken) => domainEvent is ModelReasoningObserved
                    ? Task.CompletedTask
                    : telemetry.ObserveAsync(domainEvent, cancellationToken),
                subscriberCapacity);
            var evidenceStore = new EvidenceStore(events, sanitizer);
            var promptAppendLoader = new PromptAppendLoader(sanitizer, LoadPromptAppendLimits(configuration, trustedConfiguration));
            contextLifecycle = new ContextLifecycleObserver(
                evidenceStore,
                promptAppendLoader);
            contextSubscription = events.Subscribe(
                (domainEvent, cancellationToken) => domainEvent is ModelReasoningObserved
                    ? Task.CompletedTask
                    : contextLifecycle.ObserveAsync(domainEvent, cancellationToken),
                subscriberCapacity);
            var budget = new ExecutionBudget(new BudgetDimensions(
                configuration.GetValue<long>("budget:tokens", 100000),
                configuration.GetValue<int>("budget:calls", 1000),
                TimeSpan.FromSeconds(configuration.GetValue("budget:wallClockSeconds", 3600)),
                configuration.GetValue<decimal>("budget:cost", 0)));
            var userSecretsPath = Path.Combine(
                Path.GetDirectoryName(paths.UserConfiguration)
                    ?? throw new InvalidOperationException("The user configuration path has no parent directory."),
                "secrets",
                "config.json");
            var secretLimits = trustedConfiguration.GetSection("secretResolution:limits").Get<SecretResourceLimits>(options => options.ErrorOnUnknownConfiguration = true) ?? new();
            secretLimits.Validate();
            var repositorySecretProvider = new RepositorySecretProvider(paths.RepositoryRoot, secretLimits);
            var secretResolver = new SecretResolver(
                [
                    new EnvironmentSecretProvider(secretLimits),
                    repositorySecretProvider,
                    new UserFileSecretProvider(userSecretsPath, secretLimits),
                ],
                secretLimits);
            semanticEngines = new SemanticEngineRegistry(events, loggerFactory, resourceLimits: operationalLimits.Semantic);
            semanticRefreshPublicationGate = new SemanticRefreshPublicationGateRouter();
            semanticRefreshCoordinator = new SemanticRefreshCoordinator(
                semanticEngines,
                events,
                loggerFactory.CreateLogger<SemanticRefreshCoordinator>(),
                semanticRefreshPublicationGate,
                resourceLimits: configuration.GetSection("semanticRefresh:limits").Get<SemanticRefreshResourceLimits>(options => options.ErrorOnUnknownConfiguration = true));
            var semanticMutations = new SemanticMutationEngine(
                semanticEngines,
                loggerFactory.CreateLogger<SemanticMutationEngine>());
            semanticObserver = new SemanticLifecycleObserver(
                semanticEngines,
                semanticRefreshCoordinator,
                events,
                loggerFactory.CreateLogger<SemanticLifecycleObserver>());
            semanticSubscription = events.Subscribe(semanticObserver.ObserveAsync, subscriberCapacity);
            var processManager = new ProcessManager(
                sanitizer,
                loggerFactory.CreateLogger<ProcessManager>(),
                limits: operationalLimits.Process);
            var hookHttpClient = new HttpClient(new SocketsHttpHandler
            {
                // Bounded pool lifetime refreshes DNS/endpoint changes while reusing connections;
                // matches the model-transport host default. See Plan 67 (AR-04).
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            });
            var hookHandlers = LoadHookHandlers(
                configuration,
                trustedConfiguration);
            var hookGrants = LoadHookGrants(trustedConfiguration);
            var hookPolicy = new HookPolicyEvaluator(persistence.HookStore, hookGrants);
            var hookCoordinator = new HookCoordinator(
                hookHandlers,
                [
                    new ExecutableHookAdapter(processManager, paths.RepositoryRoot),
                    new HttpHookAdapter(hookHttpClient, secretResolver),
                ],
                hookPolicy,
                persistence.HookStore,
                sanitizer,
                loggerFactory.CreateLogger<HookCoordinator>(),
                events,
                limits: trustedConfiguration.GetSection("hooks:limits").Get<HookResourceLimits>(options => options.ErrorOnUnknownConfiguration = true) ?? new());
            var hookObserver = new HookEventObserver(hookCoordinator);
            hookSubscription = events.Subscribe(
                (domainEvent, cancellationToken) => domainEvent is ModelReasoningObserved
                    ? Task.CompletedTask
                    : hookObserver.ObserveAsync(domainEvent, cancellationToken),
                subscriberCapacity);

            // Tool registration precedes extension capability composition so extensions share one governed catalog.
            var gitQueries = new GitQueryService(operationalLimits.Git);
            var dotNetInventory = new DotNetInventoryService(semanticEngines, gitQueries, operationalLimits.Semantic);
            var advancedSemanticQueries = new AdvancedSemanticQueryService(semanticEngines, promptLoader, codeExploreOptions, operationalLimits.Semantic);
            var advisorySources = LoadNuGetAdvisorySources(trustedConfiguration, operationalLimits.Validation);
            var nativeValidation = new NativeValidationToolService(
                processManager,
                secretResolver,
                advisorySources,
                operationalLimits.Validation);
            (var toolStateManager, var toolRegistry, var webFetchAuthorization, var approvalPrompt) = CreateTools(
                configuration,
                trustedConfiguration,
                paths,
                semanticEngines,
                processManager,
                gitQueries,
                dotNetInventory,
                nativeValidation,
                advancedSemanticQueries,
                advancedSemanticQueries,
                secretResolver,
                toolLimits,
                codeExploreOutputOptions,
                promptLoader,
                codeExploreOptions,
                persistence.ConversationStore,
                operationalLimits);
            directFetchApprovalPrompt = approvalPrompt;
            webFetchLifecycleSubscription = events.Subscribe(
                (domainEvent, _) =>
                {
                    if (domainEvent is RunCompleted completed)
                    {
                        webFetchAuthorization.RevokeRun(completed.SessionId, completed.RunId);
                    }

                    return Task.CompletedTask;
                },
                subscriberCapacity);
            var leaseAuthority = new InvocationLeaseAuthority(
                loggerFactory.CreateLogger<InvocationLeaseAuthority>());
            var capabilityRegistry = new CapabilityRegistry(
                leaseAuthority,
                loggerFactory.CreateLogger<CapabilityRegistry>(),
                toolRegistry);
            var parallelOptions = CreateToolParallelOptions(
                configuration,
                trustedConfiguration);
            var toolPipeline = new ToolInvocationPipeline(
                toolRegistry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                loggerFactory.CreateLogger<ToolInvocationPipeline>(),
                budget,
                hooks: hookCoordinator,
                parallelOptions: parallelOptions,
                presentationLimits: configuration.GetSection("tools:runtime:presentation").Get<ToolPresentationLimits>(options => options.ErrorOnUnknownConfiguration = true));
            return new HostFoundation(
                events,
                projections,
                executionLimits,
                operationalLimits,
                sanitizer,
                repositoryFacts,
                persistence.ConversationStore,
                persistence.RepositoryMemoryStore,
                persistence.SessionLifecycleStore,
                persistence.SessionRestorer,
                persistence.ArtifactStore,
                persistence.ExecutionCheckpoints,
                persistence.DelegationCheckpoints,
                persistence.SkillStateStore,
                persistence.HookStore,
                hookCoordinator,
                hookHttpClient,
                evidenceStore,
                promptLoader,
                promptAppendLoader,
                budget,
                secretResolver,
                repositorySecretProvider,
                semanticEngines,
                semanticRefreshCoordinator,
                semanticRefreshPublicationGate,
                semanticMutations,
                processManager,
                toolStateManager,
                toolRegistry,
                codeExploreOutputOptions,
                webFetchAuthorization,
                approvalPrompt,
                leaseAuthority,
                capabilityRegistry,
                toolPipeline,
                projectionSubscription,
                persistenceSubscription,
                telemetrySubscription,
                contextSubscription,
                hookSubscription,
                webFetchLifecycleSubscription,
                hookObserver,
                semanticObserver,
                semanticSubscription);
        }
        catch
        {
            // Reverse partial initialization so a failed startup does not retain subscriptions or workspace state.
            await DisposeIfPresentAsync(semanticSubscription);
            if (semanticObserver is not null)
            {
                await semanticObserver.DisposeAsync();
            }

            if (semanticRefreshCoordinator is not null)
            {
                await semanticRefreshCoordinator.DisposeAsync();
            }

            if (semanticEngines is not null)
            {
                await semanticEngines.DisposeAsync();
            }

            await DisposeIfPresentAsync(webFetchLifecycleSubscription);
            directFetchApprovalPrompt?.Dispose();
            await DisposeIfPresentAsync(hookSubscription);
            await DisposeIfPresentAsync(contextSubscription);
            await DisposeIfPresentAsync(telemetrySubscription);
            await DisposeIfPresentAsync(persistenceSubscription);
            await projectionSubscription.DisposeAsync();
            await events.DisposeAsync();
            throw;
        }
    }

    /// <summary>Creates effective tool concurrency settings without broadening the trusted host ceiling.</summary>
    /// <param name="configuration">Effective layered configuration whose repository values may narrow policy.</param>
    /// <param name="trustedConfiguration">Repository-excluding host configuration.</param>
    /// <returns>Bounded effective parallel tool options.</returns>
    internal static ToolParallelOptions CreateToolParallelOptions(
        IConfiguration configuration,
        IConfiguration trustedConfiguration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(trustedConfiguration);
        var hostEnabled = trustedConfiguration.GetValue("tools:parallel:enabled", true);
        var hostMaximum = trustedConfiguration.GetValue("tools:parallel:maximumConcurrency", 4);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(hostMaximum);
        var effectiveEnabled = configuration.GetValue("tools:parallel:enabled", hostEnabled);
        var effectiveMaximum = configuration.GetValue("tools:parallel:maximumConcurrency", hostMaximum);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(effectiveMaximum);
        return new ToolParallelOptions
        {
            Enabled = hostEnabled && effectiveEnabled,
            MaximumConcurrency = Math.Min(hostMaximum, effectiveMaximum),
            FailureMode = Enum.TryParse(
                configuration["tools:parallel:failureMode"],
                ignoreCase: true,
                out ToolBatchFailureMode failureMode)
                ? failureMode
                : ToolBatchFailureMode.CompleteStarted,
        };
    }

    /// <summary>Resolves the executable allowlist shared by advertisement and invocation policy.</summary>
    internal static string[] ResolveAllowedExecutables(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaultShellExecutable = OperatingSystem.IsWindows() ? "powershell" : "bash";
        const string sectionPath = "tools:allowedExecutables";
        if (configuration is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers.Reverse())
            {
                string[] keys = [.. provider.GetChildKeys([], sectionPath).Distinct(StringComparer.OrdinalIgnoreCase)];
                if (keys.Length == 0)
                {
                    continue;
                }

                return
                [
                    .. keys
                        .OrderBy(key => int.TryParse(key, out var index) ? index : int.MaxValue)
                        .Select(key => provider.TryGet($"{sectionPath}:{key}", out var value) ? value : null)
                        .Select(value => value?.Trim())
                        .OfType<string>()
                        .Where(value => value.Length > 0),
                ];
            }
        }

        return configuration.GetSection(sectionPath).Get<string[]>()
            ?? [defaultShellExecutable, "dotnet", "git"];
    }

    /// <summary>Creates bounded tool limits from the effective configuration.</summary>
    private static ToolLimits CreateToolLimits(IConfiguration configuration)
    {
        var readFileMaxLines = configuration.GetValue(
            "tools:readFile:maxLines",
            ToolLimits.DefaultReadFileLineLimit);
        var readFileDefaultLines = configuration.GetValue(
            "tools:readFile:defaultLines",
            Math.Min(ToolLimits.DefaultReadFileLineLimit, readFileMaxLines));
        var limits = new ToolLimits
        {
            ListFilesDefaultEntries = configuration.GetValue("tools:listFiles:defaultEntries", 200),
            ListFilesMaxEntries = configuration.GetValue("tools:listFiles:maxEntries", 2000),
            ReadFileMaximumBytes = configuration.GetValue("tools:readFile:maxBytes", 1024L * 1024L),
            ReadFileDefaultLines = readFileDefaultLines,
            ReadFileMaxLines = readFileMaxLines,
            ReadFileMaximumContentBytes = configuration.GetValue(
                "tools:readFile:maxContentBytes",
                ToolLimits.DefaultReadFileContentByteLimit),
            SearchMaximumQueryCharacters = configuration.GetValue("tools:search:maxQueryCharacters", 500),
            SearchMaximumBytes = configuration.GetValue("tools:search:maxBytes", 1024L * 1024L),
            SearchDefaultMatches = configuration.GetValue("tools:search:defaultMatches", 100),
            SearchMaxMatches = configuration.GetValue("tools:search:maxMatches", 500),
            FindSymbolMaxResults = configuration.GetValue("tools:findSymbol:maxResults", 1000),
            FindReferencesMaxResults = configuration.GetValue("tools:findReferences:maxResults", 1000),
            FindImplementationsMaxResults = configuration.GetValue("tools:findImplementations:maxResults", 1000),
            WriteFileMaximumContentBytes = configuration.GetValue("tools:writeFile:maxContentBytes", 1024 * 1024),
            WriteFileMaximumPathCharacters = configuration.GetValue("tools:writeFile:maxPathCharacters", 4096),
            SemanticMaximumModelResults = configuration.GetValue("tools:semantic:maxModelResults", 100),
            SearchRegexTimeoutMilliseconds = configuration.GetValue("tools:search:regexTimeoutMilliseconds", 250),
            SearchProcessTimeoutMilliseconds = configuration.GetValue("tools:search:processTimeoutMilliseconds", 25_000),
            RunProcessDefaultTimeoutSeconds = configuration.GetValue("tools:runProcess:defaultTimeoutSeconds", 30),
            RunProcessMaxTimeoutSeconds = configuration.GetValue("tools:runProcess:maxTimeoutSeconds", 60),
        };
        limits.Validate();
        return limits;
    }

    /// <summary>Initializes transactional schema, artifact storage, and startup redaction auditing.</summary>
    private static async Task<PersistenceServices> InitializePersistenceAsync(
        IConfiguration configuration,
        ConfigurationPaths paths,
        SecretOutputSanitizer sanitizer,
        IDomainEventStream events,
        ILoggerFactory loggerFactory)
    {
        var databasePath = Path.GetFullPath(
            configuration.GetValue<string>("persistence:path") ?? ".threadsmith/threadsmith.db",
            paths.RepositoryRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("The persistence path has no parent directory."));
        var eventStore = new SqliteEventStore($"Data Source={databasePath}");
        await eventStore.InitializeAsync();
        var memoryOptions = configuration.GetSection(RepositoryMemoryConfiguration.SectionName).Get<RepositoryMemoryOptions>() ?? new RepositoryMemoryOptions();
        memoryOptions.Validate();
        var migrations = new MigrationRunner($"Data Source={databasePath}", DefaultMigrations.ForRepositoryMemoryCapacity(memoryOptions.MaxNumberOfRepoMemories));
        await migrations.RunAsync();
        if (migrations.LastBackupPath is { } backupPath)
        {
            var migrationLogger = loggerFactory.CreateLogger<MigrationRunner>();
            if (migrationLogger.IsEnabled(LogLevel.Information))
            {
                migrationLogger.LogInformation(
                    "Repository memory migration completed. Verified pre-migration SQLite backup: {BackupPath}.", backupPath);
            }
        }

        var artifactDirectory = Path.GetFullPath(
            configuration.GetValue<string>("persistence:artifactDirectory") ?? ".threadsmith/artifacts",
            paths.RepositoryRoot);
        var artifactStore = new ArtifactStore(
            $"Data Source={databasePath}",
            artifactDirectory,
            sanitizer,
            TimeProvider.System);
        await artifactStore.InitializeAsync();
        await new RedactionAudit(
            eventStore,
            artifactStore,
            sanitizer,
            new RedactionAuditOptions
            {
                Enabled = configuration.GetValue("persistence:redactionAudit:enabled", true),
                RepairArtifacts = configuration.GetValue("persistence:redactionAudit:repairArtifacts", true),
            },
            loggerFactory.CreateLogger<RedactionAudit>()).RunAsync();
        var connectionString = $"Data Source={databasePath}";
        var skillStateStore = new SqliteSkillStateStore(connectionString);
        var conversationStore = new SqliteConversationStore(
            connectionString,
            artifactStore,
            sanitizer,
            events,
            configuration.GetValue("context:conversation:artifactThresholdCharacters", 16_384));
        var repositoryMemoryStore = new RepositoryBoundMemoryStore(connectionString, paths.RepositoryRoot, retentionOwner: eventStore);
        var sessionLifecycleStore = new SqliteSessionLifecycleStore(connectionString);
        var sessionRestorer = new SessionRestorer(
            eventStore,
            new DomainEventMigrationRegistry([], currentVersion: 1),
            loggerFactory.CreateLogger<SessionRestorer>(),
            conversationStore);
        var retentionOptions = new RetentionOptions
        {
            Enabled = configuration.GetValue("persistence:retention:enabled", true),
            SessionAge = TimeSpan.FromDays(configuration.GetValue("persistence:retention:sessionAgeDays", 30)),
            MetadataOnly = configuration.GetValue("persistence:retention:metadataOnly", false),
            RetainFullPrompts = configuration.GetValue("persistence:retention:retainFullPrompts", true),
            RetainFullModelOutput = configuration.GetValue("persistence:retention:retainFullModelOutput", true),
            RetainProcessLogs = configuration.GetValue("persistence:retention:retainProcessLogs", true),
            RetainSourceExcerpts = configuration.GetValue("persistence:retention:retainSourceExcerpts", true),
            RetainDiffs = configuration.GetValue("persistence:retention:retainDiffs", true),
            RetainTelemetry = configuration.GetValue("persistence:retention:retainTelemetry", true),
            RetainSessionSummaries = configuration.GetValue("persistence:retention:retainSessionSummaries", true),
            RetainConversationBodies = configuration.GetValue("persistence:retention:retainConversationBodies", false),
            ConversationMessageBodyAge = configuration.GetValue(
                "persistence:retention:conversationMessageBodyAge",
                TimeSpan.FromDays(30)),
        };
        await new RetentionService(
            eventStore,
            artifactStore,
            retentionOptions,
            loggerFactory.CreateLogger<RetentionService>(),
            TimeProvider.System,
            conversationStore,
            skillStateStore).RunAsync();
        return new PersistenceServices(
            eventStore,
            conversationStore,
            repositoryMemoryStore,
            sessionLifecycleStore,
            sessionRestorer,
            artifactStore,
            new ExecutionCheckpointStore(connectionString),
            new DelegationCheckpointStore(connectionString),
            skillStateStore,
            new SqliteHookStore(connectionString));
    }

    private static IReadOnlyList<HookHandlerDescriptor> LoadHookHandlers(
        IConfiguration configuration,
        IConfiguration trustedConfiguration)
    {
        var trusted = LoadHookHandlerSection(
            trustedConfiguration.GetSection("hooks:handlers"),
            HookHandlerScope.User);
        var repository = LoadHookHandlerSection(
            configuration.GetSection("hooks:repositoryHandlers"),
            HookHandlerScope.Repository);
        return HookDescriptorValidator.Normalize(trusted.Concat(repository), trustedConfiguration.GetSection("hooks:limits").Get<HookResourceLimits>(options => options.ErrorOnUnknownConfiguration = true) ?? new());
    }

    private static IEnumerable<HookHandlerDescriptor> LoadHookHandlerSection(
        IConfigurationSection section,
        HookHandlerScope scope)
    {
        foreach (var child in section.GetChildren())
        {
            var id = child["id"] ?? throw new InvalidOperationException("A hook handler id is required.");
            var version = child["version"] ?? throw new InvalidOperationException($"Hook handler '{id}' requires a version.");
            var target = child["target"] ?? throw new InvalidOperationException($"Hook handler '{id}' requires a target.");
            var adapter = Enum.Parse<HookAdapterKind>(child["type"] ?? string.Empty, ignoreCase: true);
            HookPoint[] points = [.. child.GetSection("hookPoints").GetChildren().Select(value =>
                Enum.Parse<HookPoint>(value.Value ?? string.Empty, ignoreCase: true))];
            var dataScope = child.GetSection("dataScope").GetChildren()
                .Select(value => Enum.Parse<HookDataScope>(value.Value ?? string.Empty, ignoreCase: true))
                .Aggregate(HookDataScope.Metadata, (current, value) => current | value);
            yield return new HookHandlerDescriptor
            {
                Identity = new HookHandlerIdentity(
                    new HookHandlerId(id),
                    version,
                    new HookConfigurationDigest(string.Empty)),
                Scope = scope,
                AdapterKind = adapter,
                Enabled = child.GetValue("enabled", false),
                HookPoints = points,
                Target = target,
                RequestedAuthority = Enum.Parse<HookAuthority>(child["authority"] ?? "Advisory", ignoreCase: true),
                RequestedFailureMode = Enum.Parse<HookFailureMode>(child["failureMode"] ?? "FailOpen", ignoreCase: true),
                Limits = new HookHandlerLimits
                {
                    Timeout = TimeSpan.FromSeconds(child.GetValue("timeoutSeconds", 10)),
                    MaximumInputBytes = child.GetValue("maximumInputBytes", 64 * 1024),
                    MaximumOutputBytes = child.GetValue("maximumOutputBytes", 64 * 1024),
                    MaximumConcurrency = child.GetValue("maximumConcurrency", 1),
                    MaximumRetries = child.GetValue("maximumRetries", 0),
                },
                RequestedDataScope = dataScope,
                SecretReferences = [.. child.GetSection("secretReferences").GetChildren()
                    .Select(value => value.Value ?? string.Empty)],
                Idempotent = child.GetValue("idempotent", false),
                Priority = child.GetValue("priority", 0),
            };
        }
    }

    private static IReadOnlyList<HookManagedPolicyGrant> LoadHookGrants(IConfiguration trustedConfiguration)
    {
        var grants = new List<HookManagedPolicyGrant>();
        foreach (var child in trustedConfiguration.GetSection("hooks:managedGrants").GetChildren())
        {
            var id = child["handlerId"] ?? throw new InvalidOperationException("A managed hook grant handlerId is required.");
            var version = child["version"] ?? throw new InvalidOperationException($"Managed hook grant '{id}' requires a version.");
            var digest = child["configurationDigest"] ?? throw new InvalidOperationException($"Managed hook grant '{id}' requires a configurationDigest.");
            grants.Add(new HookManagedPolicyGrant
            {
                HandlerIdentity = new HookHandlerIdentity(new HookHandlerId(id), version, new HookConfigurationDigest(digest)),
                HookPoints = [.. child.GetSection("hookPoints").GetChildren().Select(value =>
                    Enum.Parse<HookPoint>(value.Value ?? string.Empty, ignoreCase: true))],
                AllowedDenialCodes = [.. child.GetSection("allowedDenialCodes").GetChildren().Select(value => value.Value ?? string.Empty)],
                FailureMode = Enum.Parse<HookFailureMode>(child["failureMode"] ?? "FailOpen", ignoreCase: true),
                DataScope = child.GetSection("dataScope").GetChildren()
                    .Select(value => Enum.Parse<HookDataScope>(value.Value ?? string.Empty, ignoreCase: true))
                    .Aggregate(HookDataScope.Metadata, (current, value) => current | value),
                SecretReferences = [.. child.GetSection("secretReferences").GetChildren().Select(value => value.Value ?? string.Empty)],
                AuthoritySource = child["authoritySource"] ?? "managed-policy",
            });
        }

        return grants;
    }

    /// <summary>Loads bounded trusted HTTPS package advisory sources or fails startup closed.</summary>
    private static IReadOnlyList<NuGetAdvisorySourceOptions> LoadNuGetAdvisorySources(
        IConfiguration trustedConfiguration,
        ValidationResourceLimits limits)
    {
        IConfigurationSection[] configured = [.. trustedConfiguration
            .GetSection("nuget:advisorySources")
            .GetChildren()];
        if (configured.Length > limits.MaximumAdvisorySources)
        {
            throw new InvalidDataException($"At most {limits.MaximumAdvisorySources} trusted NuGet advisory sources may be configured.");
        }

        var sources = new List<NuGetAdvisorySourceOptions>(configured.Length);
        for (var index = 0; index < configured.Length; index++)
        {
            var item = configured[index];
            var name = item["name"] ?? $"source-{index + 1}";
            var value = item["uri"] ?? item.Value;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var source))
            {
                throw new InvalidDataException("NuGet advisory sources must be absolute HTTPS URIs.");
            }

            sources.Add(new NuGetAdvisorySourceOptions(
                name,
                source,
                item["username"],
                item["secretReference"]));
        }

        return sources;
    }

    /// <summary>Creates built-in tools, persisted availability, and the shared dynamic registry.</summary>
    private static (ToolStateManager StateManager, ToolRegistry Registry, WebFetchAuthorizationAuthority WebFetchAuthorization, DirectFetchApprovalPromptRouter ApprovalPrompt) CreateTools(
        IConfiguration configuration,
        IConfiguration trustedConfiguration,
        ConfigurationPaths paths,
        SemanticEngineRegistry semanticEngines,
        ProcessManager processManager,
        IGitQueryService gitQueries,
        IDotNetInventoryService dotNetInventory,
        INativeValidationToolService nativeValidation,
        IAdvancedSemanticQueryService advancedSemanticQueries,
        ICodeExploreService codeExplore,
        ISecretResolver secretResolver,
        ToolLimits limits,
        CodeExploreOutputOptions codeExploreOutputOptions,
        IPromptLoader promptLoader,
        CodeExploreOptions codeExploreOptions,
        IConversationStore conversationStore,
        OperationalLimits operationalLimits)
    {
        var workerExecutableName = OperatingSystem.IsWindows()
            ? "Threadsmith.Scripting.Worker.exe"
            : "Threadsmith.Scripting.Worker";
        var workerExecutablePath = Path.Combine(AppContext.BaseDirectory, workerExecutableName);
        var workerPath = File.Exists(workerExecutablePath)
            ? workerExecutablePath
            : Path.Combine(AppContext.BaseDirectory, "Threadsmith.Scripting.Worker.dll");
        var ripgrepExecutableName = OperatingSystem.IsWindows() ? "rg.exe" : "rg";
        var bundledRipgrepPath = Path.Combine(AppContext.BaseDirectory, "tools", ripgrepExecutableName);
        var ripgrepExecutable = File.Exists(bundledRipgrepPath)
            ? bundledRipgrepPath
            : ripgrepExecutableName;
        var scriptEngine = new CSharpScriptEngine(
            processManager,
            new ToolConfig(configuration),
            workerPath);
        var webSearchOptions = WebSearchOptions.FromConfiguration(trustedConfiguration);
        var webFetchOptions = new WebFetchOptionsState(configuration, trustedConfiguration);
        var webFetchAuthorization = new WebFetchAuthorizationAuthority(
            webFetchOptions,
            TimeProvider.System,
            new UserAllowedNetworkHostStore(paths.UserConfiguration, trustedConfiguration.GetValue("repository:configurationBytes", 1024 * 1024)));
        var directFetchApprovalPrompt = new DirectFetchApprovalPromptRouter();
        var webContentFetcher = new WebContentFetcher(
            new PublicHttpsWebContentTransport(),
            webFetchOptions,
            promptLoader);
        var webSearchClient = new BraveWebSearchClient(
            new HttpClient(new SocketsHttpHandler
            {
                // Bounded pool lifetime refreshes DNS/endpoint changes while reusing connections;
                // matches the model-transport host default. See Plan 67 (AR-04).
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip
                    | System.Net.DecompressionMethods.Deflate,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            }),
            secretResolver,
            webSearchOptions,
            promptLoader);
        var defaultShellExecutable = OperatingSystem.IsWindows() ? "powershell" : "bash";
        var writeFileConfiguration = new WriteFileConfiguration(configuration, trustedConfiguration, paths.RepositoryRoot);
        var allowedExecutables = ResolveAllowedExecutables(configuration);
        var requireRunProcessApproval = trustedConfiguration.GetValue(
            "tools:runProcess:requireApproval",
            true);
        var shellExecutable = configuration["tools:runProcess:shellExecutable"]
            ?? defaultShellExecutable;
        ITool[] tools =
        [
            new ListFilesTool(promptLoader, limits),
            new ReadFileTool(promptLoader, limits),
            new WriteFileTool(writeFileConfiguration, conversationStore, promptLoader, limits),
            new SearchTextTool(promptLoader, limits, processManager, ripgrepExecutable),
            new GitStatusTool(processManager, promptLoader),
            new GitDiffTool(gitQueries, promptLoader),
            new GitLogTool(gitQueries, promptLoader),
            new GitShowTool(gitQueries, promptLoader),
            new GitBlameTool(gitQueries, promptLoader),
            new GitBranchComparisonTool(gitQueries, promptLoader),
            new DotNetInventoryTool(dotNetInventory, promptLoader, operationalLimits.Semantic),
            new NuGetHealthTool(nativeValidation, promptLoader, operationalLimits.Validation),
            new DotNetBuildTool(nativeValidation, promptLoader, operationalLimits.Validation),
            new DotNetAnalyzerTool(nativeValidation, promptLoader, operationalLimits.Validation),
            new DotNetFormatCheckTool(nativeValidation, promptLoader, operationalLimits.Validation),
            new DiagnosticQueryTool(nativeValidation, promptLoader, operationalLimits.Validation),
            new TestDiscoveryTool(nativeValidation, promptLoader, operationalLimits.Validation),
            new TargetedTestTool(nativeValidation, promptLoader, operationalLimits.Validation),
            new CodeExploreOutputFormattingTool(
                new CodeExploreTool(codeExplore, promptLoader, processManager, codeExploreOptions),
                codeExploreOutputOptions,
                promptLoader,
                codeExploreOptions),
            new CallHierarchyTool(advancedSemanticQueries, promptLoader, operationalLimits.Semantic),
            new SymbolImpactTool(advancedSemanticQueries, promptLoader, operationalLimits.Semantic),
            new CSharpPatternSearchTool(advancedSemanticQueries, promptLoader, operationalLimits.Semantic),
            new GeneratedCodeTool(advancedSemanticQueries, promptLoader, operationalLimits.Semantic),
            new FindSymbolTool(semanticEngines, promptLoader, limits),
            new FindReferencesTool(semanticEngines, promptLoader, limits),
            new FindImplementationsTool(semanticEngines, promptLoader, limits),
            new RunProcessTool(
                processManager,
                promptLoader,
                limits,
                allowedExecutables,
                requireRunProcessApproval,
                shellExecutable),
            new DateTimeTool(promptLoader),
            new CSharpScriptTool(scriptEngine, promptLoader, configuration.GetValue("tools:config:csharp_script:max_code_characters", 65536)),
            new WebSearchTool(
                webSearchClient,
                webSearchOptions,
                new SecretOutputSanitizer(),
                promptLoader,
                webFetchAuthorization),
            new WebFetchTool(
                webContentFetcher,
                webFetchAuthorization,
                webFetchOptions.TrustedCeiling,
                promptLoader,
                directFetchApprovalPrompt),
        ];
        var mcpApprovalPath = Path.Combine(
            Path.GetDirectoryName(paths.UserConfiguration)
                ?? throw new InvalidOperationException("User configuration must have a parent directory."),
            "mcp-tool-approvals.json");
        var stateManager = new ToolStateManager(
            tools.Select(tool => tool.Definition),
            configuration,
            paths.RepositoryConfiguration,
            mcpApprovalPath: mcpApprovalPath,
            fetchAuthorization: webFetchAuthorization,
            writeFileConfiguration: writeFileConfiguration,
            policyStoreLimits: operationalLimits.PolicyStores);
        return (
            stateManager,
            new ToolRegistry(
                tools,
                stateManager,
                webFetchAuthorization,
                configuration.GetSection("tools:runtime").Get<ToolRuntimeOptions>(options => options.ErrorOnUnknownConfiguration = true)),
            webFetchAuthorization,
            directFetchApprovalPrompt);
    }

    /// <summary>Disposes an optional partially-created subscription during failed startup.</summary>
    private static async Task DisposeIfPresentAsync(IDomainEventSubscription? subscription)
    {
        if (subscription is not null)
        {
            await subscription.DisposeAsync();
        }
    }

    private sealed record PersistenceServices(
        SqliteEventStore EventStore,
        SqliteConversationStore ConversationStore,
        RepositoryBoundMemoryStore RepositoryMemoryStore,
        SqliteSessionLifecycleStore SessionLifecycleStore,
        SessionRestorer SessionRestorer,
        ArtifactStore ArtifactStore,
        ExecutionCheckpointStore ExecutionCheckpoints,
        DelegationCheckpointStore DelegationCheckpoints,
        SqliteSkillStateStore SkillStateStore,
        SqliteHookStore HookStore);
}
