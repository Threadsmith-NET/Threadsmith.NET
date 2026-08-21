namespace Threadsmith.App;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Hooks;
using Threadsmith.Mcp;
using Threadsmith.Persistence;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Validation;
using Threadsmith.Workspaces;

/// <summary>Composes session, mutation, repository, and validation command applications.</summary>
internal static class ApplicationComposition
{
    /// <summary>Creates the shared context assembler, session state, governed mutation path, and dispatcher.</summary>
    internal static async Task<ApplicationServices> CreateAsync(ApplicationCompositionInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        // Cohesive non-owning composition inputs grouped by responsibility. Each bundle references
        // already-initialized instances constructed by the owning composition phase; none are disposed here.
        var host = inputs.Host;
        var persistence = inputs.Persistence;
        var tools = inputs.Tools;
        var semantic = inputs.Semantic;
        var integration = inputs.Integration;

        // Resolve model hints through host-owned catalog policy; an offline session has no resolver.
        var modelHints = new InMemoryModelPreferenceSnapshotProvider();
        IModelResolver? modelResolver = integration.Models.Catalog.Profiles.Count > 0
            ? new ModelResolver(integration.Models.Catalog, modelHints)
            : null;
        var conversationPolicy = host.Configuration
            .GetSection("context:conversation")
            .Get<ConversationContextPolicy>() ?? new ConversationContextPolicy();
        var conversationRetriever = new ConversationMemoryRetriever(persistence.ConversationStore);
        var contextAssembler = new ContextAssembler(
            persistence.EvidenceStore,
            new TokenEstimator(),
            new ContextPolicy(),
            host.PromptAppendLoader,
            host.Sanitizer,
            host.Events,
            new ContextAssemblerOptions
            {
                PromptAppendFiles = host.Configuration
                    .GetSection("prompt append files")
                    .Get<string[]>() ?? [],
                Conversation = conversationPolicy,
            },
            modelResolver,
            persistence.ConversationStore,
            conversationRetriever,
            new RepositoryInstructionResolver(host.Sanitizer));

        // Session preferences and usage are shared by headless and interactive surfaces so both project
        // the same effective profile, reasoning level, and provider-neutral accounting.
        var preferences = integration.Models.SessionPreferences;
        var usage = new SessionUsageProjection();
        var compactionPolicy = host.Configuration
            .GetSection("context:conversation:compaction")
            .Get<ConversationCompactionPolicy>() ?? new ConversationCompactionPolicy();
        var conversationGovernor = new ConversationMemoryGovernor(
            persistence.ConversationStore,
            host.Sanitizer,
            compactionPolicy);
        var conversationCompactor = new ConversationCompactor(
            persistence.ConversationStore,
            new DeterministicConversationSummaryCandidateProvider(),
            new ConversationSummaryValidator(compactionPolicy, host.Sanitizer),
            compactionPolicy);
        var conversationContextApplication = new ConversationContextApplication(
            contextAssembler,
            conversationCompactor,
            persistence.EvidenceStore);
        TransactionalWorkspaceCoordinator? mutationCoordinator = null;
        var executionRouter = new ExecutionOrchestratorRouter();
        var approvalPolicy = new MutationApprovalPolicyService(
            host.Configuration,
            host.Paths.RepositoryConfiguration);
        var userPlanTrustPath = Path.Combine(
            Path.GetDirectoryName(host.Paths.UserConfiguration)
                ?? throw new InvalidOperationException("The user configuration path has no parent directory."),
            "plan-policy-trust.json");
        var planTrustGrantStore = new UserPlanTrustGrantStore(userPlanTrustPath);
        var planRepositoryPolicyStore = new RepositoryPlanApprovalPolicyStore();
        var planPolicyPersistence = new PlanApprovalPolicyPersistence(
            planRepositoryPolicyStore,
            planTrustGrantStore);
        var planApprovalPolicy = new PlanApprovalPolicyService(
            host.Configuration,
            PlanApprovalRepositoryBinding.CreateFromConfigurationPath(host.Paths.RepositoryConfiguration),
            planTrustGrantStore,
            planPolicyPersistence,
            host.Events);
        var planSanityChecker = new PlanSanityChecker();
        var validationStages = GetValidationStages(host.Configuration);
        var sessionApplication = new SessionApplication(
            host.Events,
            integration.Models.Provider,
            host.Budget,
            host.Sanitizer,
            host.LoggerFactory.CreateLogger<SessionApplication>(),
            tools.ToolPipeline,
            async (sessionId, cancellationToken) =>
            {
                var key = new ProjectionKey("session", sessionId.Value.ToString("D"));
                var state = await host.Projections.GetAsync<SessionProjection>(
                    key,
                    cancellationToken);
                return CreateToolInvocationContext(host, state);
            },
            contextAssembler,
            persistence.EvidenceStore,
            tools.ToolRegistry,
            integration.Models.PreferredProfileId,
            host.ExecutionLimits,
            preferences,
            usage,
            persistence.ConversationStore,
            conversationGovernor,
            conversationPolicy.Mode,
            conversationCompactor,
            executionRouter,
            async (sessionId, runId, task, plan, cancellationToken) =>
            {
                var key = new ProjectionKey("session", sessionId.Value.ToString("D"));
                var state = await host.Projections.GetAsync<SessionProjection>(
                    key,
                    cancellationToken);
                if (state?.WorkspaceId is not { } workspaceId || mutationCoordinator is null)
                {
                    return null;
                }

                var baseline = mutationCoordinator.GetWorkspace(workspaceId).Baseline;
                var projectInventory = semantic.SemanticEngines.GetProjects(workspaceId);
                var affectedProjects = AffectedProjectCalculator.Calculate(
                    baseline.RepositoryPath,
                    plan.Steps.SelectMany(step => step.GetAffectedPaths()).ToArray(),
                    projectInventory);
                return new ExecutionStartRequest
                {
                    SessionId = sessionId,
                    RunId = runId,
                    Baseline = baseline,
                    Task = task,
                    ApprovedPlan = plan,
                    ValidationRequest = new BuildValidationRequest
                    {
                        SessionId = sessionId,
                        RunId = runId,
                        Baseline = baseline,
                        Projects = affectedProjects.Projects,
                        Confidence = state.SemanticConfidence,
                        ProjectInventory = projectInventory,
                        Stages = validationStages,
                    },
                    CorrectionBudget = host.Configuration.GetValue("execution:correctionBudget", 3),
                };
            },
            tools.HookCoordinator,
            budgetFactory: static () => UnboundedBudget.Instance,
            userUrlIntake: async (sessionId, runId, messageId, rawMessage, cancellationToken) =>
            {
                if (!tools.ToolStateManager.IsEnabled("web_fetch")
                    || tools.ToolStateManager.RequiresCurrentMessageUrlConsent())
                {
                    return [];
                }

                var key = new ProjectionKey("session", sessionId.Value.ToString("D"));
                var state = await host.Projections.GetAsync<SessionProjection>(
                    key,
                    cancellationToken);
                var invocationContext = CreateToolInvocationContext(host, state);
                return tools.WebFetchAuthorization.IssueCurrentUserMessageUrls(
                    invocationContext.RepositoryPath,
                    sessionId,
                    runId,
                    messageId,
                    rawMessage,
                    invocationContext);
            },
            planSanityChecker: planSanityChecker,
            planApprovalPolicy: planApprovalPolicy,
            planSanityRequestFactory: async (sessionId, plan, cancellationToken) =>
            {
                var key = new ProjectionKey("session", sessionId.Value.ToString("D"));
                var state = await host.Projections.GetAsync<SessionProjection>(
                    key,
                    cancellationToken);
                if (state?.WorkspaceId is not { } workspaceId || mutationCoordinator is null)
                {
                    return null;
                }

                var baseline = TryGetWorkspaceBaseline(mutationCoordinator, workspaceId);
                if (baseline is null)
                {
                    return null;
                }

                var invocationContext = CreateToolInvocationContext(host, state);
                return CreatePlanSanityCheckRequest(plan, invocationContext, baseline);
            });

        // Mutation coordination is shared across repository lifecycle, proposal application, and dispatch.
        var repositoryBindings = new RepositoryScopedBindingCoordinator(
            host.Paths.RepositoryRoot,
            tools.ToolStateManager,
            integration.Models.ActiveModels,
            approvalPolicy,
            planApprovalPolicy,
            tools.RepositorySecretProvider,
            integration.McpManager);
        mutationCoordinator = new TransactionalWorkspaceCoordinator(
            host.Events,
            mutationApprovalPolicy: approvalPolicy,
            hooks: tools.HookCoordinator);
        IDomainEventSubscription? sessionCheckpointSubscription = null;
        try
        {
            var mutationProposals = new MutationProposalApplication(
                integration.Models.Provider,
                contextAssembler,
                mutationCoordinator,
                host.Budget,
                host.Sanitizer,
                host.Events,
                integration.Models.PreferredProfileId,
                host.ExecutionLimits,
                preferences,
                usage,
                budgetFactory: host.Budget.CreateScope,
                semanticMutations: semantic.SemanticMutations,
                preMutationAnalyzer: semantic.SemanticEngines);
            var repositoryLifecycle = new RepositoryLifecycle(
                host.Events,
                persistence.RepositoryFacts,
                new DotNetEnvironmentResolver(),
                mutationCoordinator,
                host.LoggerFactory.CreateLogger<RepositoryLifecycle>(),
                mutationApprovalPolicy: null,
                repositoryOpened: repositoryBindings.BindRepositoryAsync);

            // Validation reuses the tracked process manager and publishes normalized host-owned evidence.
            var buildExecutor = new BuildExecutor(
                host.Events,
                new DiagnosticNormalizer(),
                host.LoggerFactory.CreateLogger<BuildExecutor>());
            var testPipeline = new TestValidationPipeline(
                new TestDiscoverer(tools.ProcessManager),
                new TestRunner(tools.ProcessManager, host.Events),
                host.Events);
            var validationApplication = new ValidationApplication(
                new BaselineBuildCapture(buildExecutor),
                new ValidationPipeline(
                    buildExecutor,
                    new DiagnosticClassifier(),
                    new DiagnosticCorrelator(),
                    new AcceptanceGate(),
                    testPipeline,
                    host.Events,
                    semantic.SemanticEngines),
                tools.HookCoordinator);
            var executionOrchestrator = new ExecutionOrchestrator(
                mutationProposals,
                mutationCoordinator,
                validationApplication,
                validationApplication,
                mutationCoordinator,
                persistence.ExecutionCheckpoints,
                new ExecutionArtifactPublisher(persistence.ArtifactStore),
                host.Events,
                host.LoggerFactory.CreateLogger<ExecutionOrchestrator>());
            var agentScheduler = new AgentRunScheduler(new AgentSchedulerOptions
            {
                QueueCapacity = host.Configuration.GetValue("agents:queueCapacity", 32),
                MaximumActiveChildren = host.Configuration.GetValue("agents:maxActiveGlobal", 4),
                MaximumActiveChildrenPerParent = host.Configuration.GetValue("agents:maxActivePerParent", 3),
                MaximumActiveImplementers = host.Configuration.GetValue("agents:maxActiveImplementers", 2),
                ShutdownTimeout = TimeSpan.FromSeconds(
                    host.Configuration.GetValue("agents:shutdownTimeoutSeconds", 30)),
            });
            var delegationCoordinator = new DelegationCoordinator(
                agentScheduler,
                persistence.DelegationCheckpoints,
                host.Events);
            var delegatingExecutionOrchestrator = new ApprovedPlanDelegatingOrchestrator(
                executionOrchestrator,
                delegationCoordinator,
                new ApprovedPlanAssignmentRunner());
            executionRouter.Attach(delegatingExecutionOrchestrator);

            // Skill discovery remains metadata-only until an explicit verify or invoke boundary.
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var userSkillRoot = Path.Combine(userProfile, ".threadsmith", "skills");
            var skillSources = new List<SkillCatalogSource>
            {
                new(
                    SkillScope.Maintained,
                    Path.Combine(AppContext.BaseDirectory, "MaintainedSkills"),
                    "threadsmith-maintained",
                    IsMaintained: true),
                new(
                    SkillScope.User,
                    userSkillRoot,
                    "user:~/.threadsmith/skills"),
                new(
                    SkillScope.Machine,
                    Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Threadsmith",
                        "skills"),
                    "machine:Threadsmith/skills"),
            };
            if (host.Configuration.GetValue("skills:repositoryCatalogEnabled", true))
            {
                skillSources.Add(new SkillCatalogSource(
                    SkillScope.Repository,
                    Path.Combine(host.Paths.RepositoryRoot, ".threadsmith", "skills"),
                    "repository:.threadsmith/skills",
                    IsRepositoryControlled: true));
            }

            var organizationCatalog = host.TrustedConfiguration["skills:organizationCatalogPath"];
            if (!string.IsNullOrWhiteSpace(organizationCatalog))
            {
                skillSources.Add(new SkillCatalogSource(
                    SkillScope.Organization,
                    organizationCatalog,
                    "organization:trusted-configuration"));
            }

            var skillCatalog = new SkillCatalog(skillSources);
            await skillCatalog.RefreshAsync();
            var claudeSkillCatalog = new ClaudeSkillCompatibilityCatalog(
            [
                new ClaudeSkillRoot(
                    SkillScope.User,
                    Path.Combine(userProfile, ".claude", "skills"),
                    "user:~/.claude/skills"),
                new ClaudeSkillRoot(
                    SkillScope.Repository,
                    Path.Combine(host.Paths.RepositoryRoot, ".claude", "skills"),
                    "repository:.claude/skills",
                    IsRepositoryControlled: true),
            ]);
            await claudeSkillCatalog.RefreshAsync();
            var baseSkillPolicy = new SkillTrustPolicySnapshot
            {
                TrustedSignerPublicKeys = host.TrustedConfiguration
                    .GetSection("skills:trustedSigners")
                    .Get<Dictionary<string, string>>() ?? [],
                AllowlistedDigests = host.TrustedConfiguration
                    .GetSection("skills:allowlistedPackages")
                    .Get<string[]>()?.ToHashSet(StringComparer.Ordinal) ?? [],
                EnabledSelectors = host.TrustedConfiguration
                    .GetSection("skills:enabledSelectors")
                    .Get<string[]>()?.ToHashSet(StringComparer.Ordinal) ?? [],
                RevokedDigests = host.TrustedConfiguration
                    .GetSection("skills:revokedDigests")
                    .Get<string[]>()?.ToHashSet(StringComparer.Ordinal) ?? [],
                DeniedSkillIds = host.TrustedConfiguration
                    .GetSection("skills:deniedSkillIds")
                    .Get<string[]>()?.ToHashSet(StringComparer.Ordinal) ?? [],
                DeniedPublishers = host.TrustedConfiguration
                    .GetSection("skills:deniedPublishers")
                    .Get<string[]>()?.ToHashSet(StringComparer.Ordinal) ?? [],
                RevokedSigners = host.TrustedConfiguration
                    .GetSection("skills:revokedSigners")
                    .Get<string[]>()?.ToHashSet(StringComparer.Ordinal) ?? [],
            };
            var skillPolicy = new FileSkillTrustPolicyProvider(
                Path.Combine(userProfile, ".threadsmith", "skill-policy.json"),
                baseSkillPolicy);
            var nativeSkillVerifier = new SkillPackageVerifier(skillPolicy);
            var compatibleSkillCatalog = new CompatibleSkillCatalog(skillCatalog, claudeSkillCatalog);
            await compatibleSkillCatalog.RefreshAsync();
            repositoryBindings.AttachSkillCatalogs(
                skillCatalog,
                claudeSkillCatalog,
                compatibleSkillCatalog);
            var skillVerifier = new CompatibleSkillPackageVerifier(
                nativeSkillVerifier,
                compatibleSkillCatalog,
                skillPolicy);
            var skillCompatibility = new SkillCompatibilityEvaluator(
                tools.ToolRegistry,
                integration.Models.Catalog,
                "1.0.0");
            var skillWorkflow = new SkillWorkflowOrchestrator(
                compatibleSkillCatalog,
                skillVerifier,
                skillCompatibility,
                new CompatibleSkillContentLoader(
                    new SkillContentLoader(host.Sanitizer),
                    compatibleSkillCatalog,
                    host.Sanitizer),
                new BoundedJsonSchemaValidator(),
                new ModelSkillProcedureRunner(
                    integration.Models.Provider,
                    tools.ToolRegistry,
                    tools.ToolPipeline,
                    host.Sanitizer,
                    async (request, cancellationToken) =>
                    {
                        var key = new ProjectionKey("session", request.SessionId.Value.ToString("D"));
                        var state = await host.Projections.GetAsync<SessionProjection>(
                            key,
                            cancellationToken);
                        return CreateToolInvocationContext(host, state);
                    }),
                persistence.SkillStateStore,
                async (sessionId, cancellationToken) =>
                {
                    var key = new ProjectionKey("session", sessionId.Value.ToString("D"));
                    var state = await host.Projections.GetAsync<SessionProjection>(
                        key,
                        cancellationToken) ?? throw new InvalidOperationException(
                            "The current session state is unavailable for skill workflow revalidation.");

                    return new SkillInvocationHostContext
                    {
                        WorkspaceId = state.WorkspaceId,
                        Trust = state.RepositoryTrust ?? RepositoryTrustLevel.UntrustedInspection,
                        Phase = state.Phase,
                    };
                },
                host.Events);
            var skillApplication = new SkillApplication(
                compatibleSkillCatalog,
                skillVerifier,
                skillPolicy,
                skillCompatibility,
                skillWorkflow,
                persistence.SkillStateStore,
                new SkillPackageInstaller(
                    userSkillRoot,
                    Path.Combine(userProfile, ".threadsmith", "skill-quarantine")));
            var invokeSkillTool = new InvokeSkillTool(skillWorkflow);
            tools.ToolRegistry.RegisterOrReplace(
                invokeSkillTool,
                new ToolActivitySource(ToolActivitySourceKind.BuiltIn));
            var hookApplication = new HookManagementApplication(tools.HookCoordinator, persistence.HookStore, host.Events);
            var sessionLifecycle = new SessionLifecycleApplication(
                host.Paths.RepositoryRoot,
                persistence.SessionLifecycleStore,
                persistence.SessionRestorer,
                sessionApplication,
                host.Projections,
                host.Events,
                persistence.EvidenceStore,
                contextAssembler,
                usage,
                integration.Models.ActiveModels);
            repositoryBindings.AttachSessionLifecycle(sessionLifecycle);
            sessionCheckpointSubscription = host.Events.Subscribe(
                async (domainEvent, _) =>
                {
                    if (domainEvent is RunCompleted completed)
                    {
                        await sessionLifecycle.CheckpointCompletedTurnAsync(
                            completed.SessionId,
                            CancellationToken.None);
                    }
                });
            var handlers = new List<object>
            {
                sessionApplication,
                sessionLifecycle,
                hookApplication,
                planApprovalPolicy,
                executionOrchestrator,
                delegationCoordinator,
                skillApplication,
                conversationContextApplication,
                repositoryLifecycle,
                mutationProposals,
                semantic.SemanticMutations,
                mutationCoordinator,
                validationApplication,
                new CodexAuthenticationApplication(host.Paths),
                integration.McpManager,
            };
            if (integration.Models.ActiveModels is { } activeModels)
            {
                handlers.Add(new ActiveModelSelectionApplication(
                    activeModels,
                    contextAssembler,
                    host.Projections,
                    sessionLifecycle.CheckpointActiveSelectionAsync));
            }

            var dispatcher = new CommandDispatcher(
                handlers,
                CreateProductionMiddleware(host.LoggerFactory));
            return new ApplicationServices(
                dispatcher,
                mutationCoordinator,
                agentScheduler,
                skillWorkflow,
                tools.ToolRegistry,
                invokeSkillTool,
                approvalPolicy,
                planApprovalPolicy,
                preferences,
                usage,
                sessionLifecycle,
                sessionCheckpointSubscription,
                integration.Models.StartupProfile?.Id,
                claudeSkillCatalog,
                validationStages);
        }
        catch
        {
            if (sessionCheckpointSubscription is not null)
            {
                await sessionCheckpointSubscription.DisposeAsync();
            }

            await mutationCoordinator.DisposeAsync();
            throw;
        }
    }

    /// <summary>Builds the always-active production command middleware pipeline.</summary>
    /// <param name="loggerFactory">The shared logger factory.</param>
    /// <returns>Exactly one metadata-only telemetry middleware. Existing logger and activity filtering controls emission; repository configuration cannot alter activation or fields.</returns>
    internal static ICommandMiddleware[] CreateProductionMiddleware(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        return
        [
            new CommandTelemetryMiddleware(loggerFactory.CreateLogger<CommandTelemetryMiddleware>()),
        ];
    }

    /// <summary>Returns the active transactional baseline when one has been registered for mutation.</summary>
    internal static WorkspaceBaseline? TryGetWorkspaceBaseline(
        ITransactionalWorkspaceResolver? resolver,
        WorkspaceId? workspaceId)
    {
        if (resolver is null || workspaceId is null)
        {
            return null;
        }

        try
        {
            return resolver.GetWorkspace(workspaceId.Value).Baseline;
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Builds plan sanity input from the active workspace policy when a baseline is available.</summary>
    internal static PlanSanityCheckRequest CreatePlanSanityCheckRequest(
        ImplementationPlan plan,
        ToolInvocationContext invocationContext,
        WorkspaceBaseline? baseline)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(invocationContext);
        return new PlanSanityCheckRequest
        {
            Plan = plan,
            RepositoryRoot = baseline?.RepositoryPath ?? invocationContext.RepositoryPath,
            Baseline = baseline,
            TrustLevel = baseline?.TrustLevel ?? invocationContext.TrustLevel,
            ProhibitedPaths = baseline?.ProhibitedPaths ?? invocationContext.ProhibitedPaths,
        };
    }

    /// <summary>Resolves configured validation stages or the compiled default set.</summary>
    private static IReadOnlyList<MutationValidationStage> GetValidationStages(IConfiguration configuration)
    {
        var configured = configuration.GetSection("validation:stages").Get<string[]>() ?? [];
        if (configured.Length == 0)
        {
            return
            [
                MutationValidationStage.Semantic,
                MutationValidationStage.Compile,
                MutationValidationStage.Diagnostics,
                MutationValidationStage.Tests,
            ];
        }

        var stages = new List<MutationValidationStage>();
        foreach (var stage in configured)
        {
            if (string.Equals(stage, "semantic", StringComparison.OrdinalIgnoreCase))
            {
                stages.Add(MutationValidationStage.Semantic);
            }
            else if (string.Equals(stage, "compile", StringComparison.OrdinalIgnoreCase))
            {
                stages.Add(MutationValidationStage.Compile);
            }
            else if (string.Equals(stage, "diagnostics", StringComparison.OrdinalIgnoreCase))
            {
                stages.Add(MutationValidationStage.Diagnostics);
            }
            else if (string.Equals(stage, "tests", StringComparison.OrdinalIgnoreCase))
            {
                stages.Add(MutationValidationStage.Tests);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unsupported validation stage '{stage}'.");
            }
        }

        return stages
            .Distinct()
            .ToArray();
    }

    private static ToolInvocationContext CreateToolInvocationContext(
        HostCompositionInputs host,
        SessionProjection? state)
    {
        return new ToolInvocationContext
        {
            WorkspaceId = state?.WorkspaceId,
            RepositoryPath = state?.RepositoryPath ?? host.Paths.RepositoryRoot,
            TrustLevel = state?.RepositoryTrust ?? RepositoryTrustLevel.UntrustedInspection,
            ApprovedRoots = ["."],
            ProhibitedPaths = host.Configuration.GetSection("prohibitedPaths").Get<string[]>() ?? [],
            AllowedExecutables = HostFoundation.ResolveAllowedExecutables(host.Configuration),
            AllowedNetworkHosts = host.TrustedConfiguration
                .GetSection("tools:allowedNetworkHosts")
                .Get<string[]>() ?? [],
            AllowedToolIds = host.Configuration.GetSection("tools:allow").Get<string[]>() ?? [],
            DeniedToolIds = host.Configuration.GetSection("tools:deny").Get<string[]>() ?? [],
            RequireApprovalToolIds = host.Configuration
                .GetSection("tools:requireApproval")
                .Get<string[]>() ?? [],
            AllowedSecretReferences = host.TrustedConfiguration
                .GetSection("tools:allowedSecretReferences")
                .Get<string[]>() ?? [],
            RequestedBy = "model",
        };
    }
}

/// <summary>Cohesive non-owning composition inputs grouped by subsystem responsibility, replacing the flat aggregate parameter bag.</summary>
/// <remarks>Passive views only: each bundle references already-initialized instances constructed by the owning composition phase and neither creates nor disposes them.</remarks>
internal sealed record ApplicationCompositionInputs
{
    /// <summary>Gets shared host state and runtime services.</summary>
    internal required HostCompositionInputs Host { get; init; }

    /// <summary>Gets durable persistence stores.</summary>
    internal required PersistenceCompositionInputs Persistence { get; init; }

    /// <summary>Gets the governed tool pipeline, policy, and process coordination.</summary>
    internal required ToolPolicyCompositionInputs Tools { get; init; }

    /// <summary>Gets repository semantic-engine and mutation services.</summary>
    internal required SemanticCompositionInputs Semantic { get; init; }

    /// <summary>Gets the single MCP lifecycle authority and composed model services.</summary>
    internal required IntegrationCompositionInputs Integration { get; init; }
}

/// <summary>Shared host state, runtime services, and loaded resources owned by the host foundation phase.</summary>
internal sealed record HostCompositionInputs
{
    /// <summary>Gets effective layered configuration.</summary>
    internal required IConfiguration Configuration { get; init; }

    /// <summary>Gets the machine/user/environment configuration used for credential-bearing policy.</summary>
    internal required IConfiguration TrustedConfiguration { get; init; }

    /// <summary>Gets normalized startup paths.</summary>
    internal required ConfigurationPaths Paths { get; init; }

    /// <summary>Gets the shared logger factory.</summary>
    internal required ILoggerFactory LoggerFactory { get; init; }

    /// <summary>Gets the shared domain event stream.</summary>
    internal required DomainEventStream Events { get; init; }

    /// <summary>Gets queryable in-memory domain projections.</summary>
    internal required InMemoryProjectionStore Projections { get; init; }

    /// <summary>Gets bounded model execution limits.</summary>
    internal required ExecutionLimits ExecutionLimits { get; init; }

    /// <summary>Gets the shared secret-output sanitizer.</summary>
    internal required SecretOutputSanitizer Sanitizer { get; init; }

    /// <summary>Gets the bounded untrusted prompt-append loader.</summary>
    internal required PromptAppendLoader PromptAppendLoader { get; init; }

    /// <summary>Gets the session execution budget.</summary>
    internal required ExecutionBudget Budget { get; init; }
}

/// <summary>Durable stores owned by the persistence initialization phase.</summary>
internal sealed record PersistenceCompositionInputs
{
    /// <summary>Gets durable conversation archive and governed memory storage.</summary>
    internal required SqliteConversationStore ConversationStore { get; init; }

    /// <summary>Gets repository-bound durable session metadata and clone storage.</summary>
    internal required SqliteSessionLifecycleStore SessionLifecycleStore { get; init; }

    /// <summary>Gets tolerant session event/conversation restoration.</summary>
    internal required SessionRestorer SessionRestorer { get; init; }

    /// <summary>Gets sanitized content-addressed artifact storage.</summary>
    internal required ArtifactStore ArtifactStore { get; init; }

    /// <summary>Gets atomic approved-plan execution checkpoints.</summary>
    internal required ExecutionCheckpointStore ExecutionCheckpoints { get; init; }

    /// <summary>Gets atomic parallel-agent delegation checkpoints.</summary>
    internal required DelegationCheckpointStore DelegationCheckpoints { get; init; }

    /// <summary>Gets atomic governed-skill pins and workflow checkpoints.</summary>
    internal required SqliteSkillStateStore SkillStateStore { get; init; }

    /// <summary>Gets durable lifecycle-hook approvals and audit.</summary>
    internal required SqliteHookStore HookStore { get; init; }

    /// <summary>Gets governed evidence storage.</summary>
    internal required EvidenceStore EvidenceStore { get; init; }

    /// <summary>Gets persisted repository discovery facts.</summary>
    internal required SqliteRepositoryFactsStore RepositoryFacts { get; init; }
}

/// <summary>Tool pipeline, repository-scoped policy, process, fetch-authorization, secret, and hook coordination.</summary>
internal sealed record ToolPolicyCompositionInputs
{
    /// <summary>Gets the governed tool invocation pipeline.</summary>
    internal required ToolInvocationPipeline ToolPipeline { get; init; }

    /// <summary>Gets the effective built-in and extension tool registry.</summary>
    internal required ToolRegistry ToolRegistry { get; init; }

    /// <summary>Gets mutable repository-scoped tool availability state.</summary>
    internal required ToolStateManager ToolStateManager { get; init; }

    /// <summary>Gets transient governed web-fetch authority for fresh message intake.</summary>
    internal required WebFetchAuthorizationAuthority WebFetchAuthorization { get; init; }

    /// <summary>Gets repository-bound lower-trust static-secret lookup.</summary>
    internal required RepositorySecretProvider RepositorySecretProvider { get; init; }

    /// <summary>Gets the tracked external process manager.</summary>
    internal required ProcessManager ProcessManager { get; init; }

    /// <summary>Gets the host-owned lifecycle-hook coordinator.</summary>
    internal required HookCoordinator HookCoordinator { get; init; }
}

/// <summary>Repository semantic-engine registry and semantic mutation operations.</summary>
internal sealed record SemanticCompositionInputs
{
    /// <summary>Gets the repository semantic-engine registry.</summary>
    internal required SemanticEngineRegistry SemanticEngines { get; init; }

    /// <summary>Gets semantic mutation operations backed by loaded workspaces.</summary>
    internal required SemanticMutationEngine SemanticMutations { get; init; }
}

/// <summary>The single MCP lifecycle authority and already-composed model selection and provider services.</summary>
internal sealed record IntegrationCompositionInputs
{
    /// <summary>Gets the single MCP lifecycle authority composed before the dispatcher.</summary>
    internal required IMcpManager McpManager { get; init; }

    /// <summary>Gets composed model selection and provider services.</summary>
    internal required ModelServices Models { get; init; }
}

/// <summary>Atomically rebinds repository-scoped services and restores the prior repository after any failure.</summary>
internal sealed class RepositoryScopedBindingCoordinator
{
    private readonly ActiveModelSelectionService? _activeModels;
    private readonly MutationApprovalPolicyService _approvalPolicy;
    private readonly PlanApprovalPolicyService _planApprovalPolicy;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly RepositorySecretProvider _repositorySecretProvider;
    private readonly IMcpManager _mcpManager;
    private readonly ToolStateManager _toolState;
    private ClaudeSkillCompatibilityCatalog? _claudeSkills;
    private CompatibleSkillCatalog? _compatibleSkills;
    private string _currentRepositoryRoot;
    private SkillCatalog? _nativeSkills;
    private SessionLifecycleApplication? _sessionLifecycle;

    /// <summary>Initializes a new instance of the <see cref="RepositoryScopedBindingCoordinator"/> class.</summary>
    internal RepositoryScopedBindingCoordinator(
        string initialRepositoryRoot,
        ToolStateManager toolState,
        ActiveModelSelectionService? activeModels,
        MutationApprovalPolicyService approvalPolicy,
        PlanApprovalPolicyService planApprovalPolicy,
        RepositorySecretProvider repositorySecretProvider,
        IMcpManager mcpManager)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initialRepositoryRoot);
        ArgumentNullException.ThrowIfNull(toolState);
        ArgumentNullException.ThrowIfNull(approvalPolicy);
        ArgumentNullException.ThrowIfNull(planApprovalPolicy);
        ArgumentNullException.ThrowIfNull(repositorySecretProvider);
        ArgumentNullException.ThrowIfNull(mcpManager);
        _currentRepositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(initialRepositoryRoot));
        _toolState = toolState;
        _activeModels = activeModels;
        _approvalPolicy = approvalPolicy;
        _planApprovalPolicy = planApprovalPolicy;
        _repositorySecretProvider = repositorySecretProvider;
        _mcpManager = mcpManager;
    }

    /// <summary>Attaches the catalogs constructed later in the composition sequence.</summary>
    internal void AttachSkillCatalogs(
        SkillCatalog nativeSkills,
        ClaudeSkillCompatibilityCatalog claudeSkills,
        CompatibleSkillCatalog compatibleSkills)
    {
        ArgumentNullException.ThrowIfNull(nativeSkills);
        ArgumentNullException.ThrowIfNull(claudeSkills);
        ArgumentNullException.ThrowIfNull(compatibleSkills);
        _nativeSkills = nativeSkills;
        _claudeSkills = claudeSkills;
        _compatibleSkills = compatibleSkills;
    }

    /// <summary>Attaches the lifecycle authority constructed later in the composition sequence.</summary>
    internal void AttachSessionLifecycle(SessionLifecycleApplication sessionLifecycle)
    {
        ArgumentNullException.ThrowIfNull(sessionLifecycle);
        if (_sessionLifecycle is not null)
        {
            throw new InvalidOperationException("The session lifecycle authority is already attached.");
        }

        _sessionLifecycle = sessionLifecycle;
    }

    /// <summary>Rebinds every repository-scoped service as one recoverable repository-open boundary.</summary>
    internal async Task BindRepositoryAsync(
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var nextRepositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var previousRepositoryRoot = _currentRepositoryRoot;
            try
            {
                if (_activeModels is not null)
                {
                    await _activeModels.BindRepositoryAsync(nextRepositoryRoot, cancellationToken);
                }

                await _toolState.BindRepositoryAsync(nextRepositoryRoot, cancellationToken);
                await _approvalPolicy.BindRepositoryAsync(nextRepositoryRoot, cancellationToken);
                await _planApprovalPolicy.BindRepositoryAsync(nextRepositoryRoot, cancellationToken);
                _repositorySecretProvider.BindRepository(nextRepositoryRoot);
                await _mcpManager.RebindRepositoryAsync(cancellationToken);
                if (_nativeSkills is not null)
                {
                    await _nativeSkills.BindRepositoryAsync(nextRepositoryRoot, cancellationToken);
                }

                if (_claudeSkills is not null)
                {
                    await _claudeSkills.BindRepositoryAsync(nextRepositoryRoot, cancellationToken);
                }

                if (_compatibleSkills is not null)
                {
                    _ = await _compatibleSkills.RefreshAsync(cancellationToken);
                }

                if (_sessionLifecycle is not null)
                {
                    await _sessionLifecycle.BindRepositoryAsync(nextRepositoryRoot, cancellationToken);
                }

                _currentRepositoryRoot = nextRepositoryRoot;
            }
            catch (Exception exception)
            {
                var rollbackFailure = await RollBackAsync(previousRepositoryRoot);
                if (rollbackFailure is not null)
                {
                    throw new AggregateException(
                        "Repository-scoped services could not be restored after repository open failed.",
                        exception,
                        rollbackFailure);
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Exception?> RollBackAsync(string repositoryRoot)
    {
        var failures = new List<Exception>();
        async Task RestoreAsync(Func<Task> restoreAsync)
        {
            try
            {
                await restoreAsync();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        if (_nativeSkills is not null)
        {
            await RestoreAsync(() => _nativeSkills.BindRepositoryAsync(repositoryRoot));
        }

        if (_claudeSkills is not null)
        {
            await RestoreAsync(() => _claudeSkills.BindRepositoryAsync(repositoryRoot));
        }

        if (_compatibleSkills is not null)
        {
            await RestoreAsync(async () => _ = await _compatibleSkills.RefreshAsync());
        }

        await RestoreAsync(() => _approvalPolicy.BindRepositoryAsync(repositoryRoot));
        await RestoreAsync(() => _planApprovalPolicy.BindRepositoryAsync(repositoryRoot));
        await RestoreAsync(() => _toolState.BindRepositoryAsync(repositoryRoot));
        _repositorySecretProvider.BindRepository(repositoryRoot);
        await RestoreAsync(() => _mcpManager.RebindRepositoryAsync());
        if (_activeModels is not null)
        {
            await RestoreAsync(() => _activeModels.BindRepositoryAsync(repositoryRoot));
        }

        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(failures),
        };
    }
}

/// <summary>Owns composed command applications and their shared transactional coordinator.</summary>
internal sealed class ApplicationServices : IAsyncDisposable
{
    private readonly AgentRunScheduler _agentScheduler;
    private readonly InvokeSkillTool _invokeSkillTool;
    private readonly TransactionalWorkspaceCoordinator _mutationCoordinator;
    private readonly SkillWorkflowOrchestrator _skillWorkflow;
    private readonly ToolRegistry _toolRegistry;
    private readonly IDomainEventSubscription _sessionCheckpointSubscription;

    /// <summary>Initializes a new instance of the <see cref="ApplicationServices"/> class.</summary>
    internal ApplicationServices(
        CommandDispatcher dispatcher,
        TransactionalWorkspaceCoordinator mutationCoordinator,
        AgentRunScheduler agentScheduler,
        SkillWorkflowOrchestrator skillWorkflow,
        ToolRegistry toolRegistry,
        InvokeSkillTool invokeSkillTool,
        MutationApprovalPolicyService mutationApprovalPolicy,
        PlanApprovalPolicyService planApprovalPolicy,
        SessionModelPreferences sessionModelPreferences,
        SessionUsageProjection sessionUsage,
        SessionLifecycleApplication sessionLifecycle,
        IDomainEventSubscription sessionCheckpointSubscription,
        ModelProfileId? effectiveStartupProfileId,
        IClaudeSkillCompatibilityCatalog claudeSkillCatalog,
        IReadOnlyList<MutationValidationStage> validationStages)
    {
        ArgumentNullException.ThrowIfNull(claudeSkillCatalog);
        ArgumentNullException.ThrowIfNull(sessionCheckpointSubscription);
        ArgumentNullException.ThrowIfNull(validationStages);
        Dispatcher = dispatcher;
        _mutationCoordinator = mutationCoordinator;
        _agentScheduler = agentScheduler;
        _skillWorkflow = skillWorkflow;
        _toolRegistry = toolRegistry;
        _invokeSkillTool = invokeSkillTool;
        MutationApprovalPolicy = mutationApprovalPolicy;
        PlanApprovalPolicy = planApprovalPolicy;
        SessionModelPreferences = sessionModelPreferences;
        SessionUsage = sessionUsage;
        SessionLifecycle = sessionLifecycle;
        _sessionCheckpointSubscription = sessionCheckpointSubscription;
        EffectiveStartupProfileId = effectiveStartupProfileId;
        ClaudeSkillCatalog = claudeSkillCatalog;
        ValidationStages = validationStages;
    }

    /// <summary>Gets the dispatcher exposed to terminal command surfaces.</summary>
    internal CommandDispatcher Dispatcher { get; }

    /// <summary>Gets the effective mutation approval policy service.</summary>
    internal MutationApprovalPolicyService MutationApprovalPolicy { get; }

    /// <summary>Gets the effective plan approval policy service.</summary>
    internal PlanApprovalPolicyService PlanApprovalPolicy { get; }

    /// <summary>Gets mutable session model preferences shared with the terminal.</summary>
    internal SessionModelPreferences SessionModelPreferences { get; }

    /// <summary>Gets provider-neutral cumulative usage shared with the terminal.</summary>
    internal SessionUsageProjection SessionUsage { get; }

    /// <summary>Gets the serialized host-owned active-session authority.</summary>
    internal SessionLifecycleApplication SessionLifecycle { get; }

    /// <summary>Gets the selected startup profile identifier, when configured.</summary>
    internal ModelProfileId? EffectiveStartupProfileId { get; }

    /// <summary>Gets metadata-only Claude-style compatibility discovery.</summary>
    internal IClaudeSkillCompatibilityCatalog ClaudeSkillCatalog { get; }

    /// <summary>Gets the host-owned resolved post-apply validation stages.</summary>
    internal IReadOnlyList<MutationValidationStage> ValidationStages { get; }

    /// <summary>Releases transactional staging resources after all command surfaces stop.</summary>
    public async ValueTask DisposeAsync()
    {
        await _sessionCheckpointSubscription.DisposeAsync();
        _toolRegistry.Remove(_invokeSkillTool.Definition.Id, _invokeSkillTool);
        await _skillWorkflow.DisposeAsync();
        await _agentScheduler.DisposeAsync();
        await _mutationCoordinator.DisposeAsync();
    }
}
