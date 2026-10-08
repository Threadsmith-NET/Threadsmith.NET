namespace Threadsmith.Architecture.Tests;

using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.App;
using Threadsmith.Cli;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Models;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises the normal App composition and repository lifecycle with the optional assembly installed.</summary>
public static partial class AppBootstrapTests
{
    /// <summary>Repository open, restart restoration, tool inventory, and shutdown leave the feature unresolved.</summary>
    [Fact]
    public static async Task RepositoryIntelligenceRemainsDormantAcrossOpenRestoreAndShutdownAsync()
    {
        using var temporary = new TemporaryDirectory("repository-intelligence-dormant");
        var paths = CreatePaths(temporary.Root);
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(temporary.GetPath("README.md"), "fixture repository", cancellationToken);

        var sessionId = await WithComposedHostAsync(
            paths,
            async (foundation, applications, token) =>
        {
            Assert.False(IsFeatureCreated(applications));
            var definitions = foundation.ToolRegistry.AllDefinitions;
            Assert.Contains(definitions, definition => definition.Id == "read_file");
            Assert.Contains(definitions, definition => definition.Id == "code_explore");
            Assert.Contains(definitions, definition => definition.Id == "memories");
            Assert.Contains(definitions, definition => definition.Id == "repository_intelligence");

            var shell = new HeadlessShell(
                applications.Dispatcher,
                foundation.Projections,
                TextWriter.Null,
                foundation.WebFetchAuthorization,
                paths.RepositoryRoot);
            var created = await shell.CreateNewSessionAsync(token);
            var opened = await applications.Dispatcher.DispatchAsync(
                new OpenRepositoryCommand(
                    created.ActiveSession.SessionId,
                    paths.RepositoryRoot,
                    RepositoryTrustLevel.UntrustedInspection),
                token);

            Assert.Equal(paths.RepositoryRoot, opened.RepositoryPath);
            Assert.False(IsFeatureCreated(applications));
            return created.ActiveSession.SessionId;
        },
            cancellationToken);

        await WithComposedHostAsync(
            paths,
            async (foundation, applications, token) =>
        {
            var shell = new HeadlessShell(
                applications.Dispatcher,
                foundation.Projections,
                TextWriter.Null,
                foundation.WebFetchAuthorization,
                paths.RepositoryRoot);
            var restored = await shell.ResumeSessionAsync(sessionId, token);

            Assert.Equal(sessionId, restored.ActiveSession.SessionId);
            Assert.False(IsFeatureCreated(applications));
            Assert.Contains(
                foundation.ToolRegistry.AllDefinitions,
                definition => definition.Id == "repository_intelligence");
            return true;
        },
            cancellationToken);

        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(temporary.Root, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path).Contains("repository-intelligence", StringComparison.OrdinalIgnoreCase));
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Repository-owned configuration and existing content cannot grant intelligence authority.</summary>
    [Fact]
    public static async Task RepositoryConfigurationCannotActivateIntelligenceAsync()
    {
        using var temporary = new TemporaryDirectory("repository-intelligence-trust");
        var paths = CreatePaths(temporary.Root);
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(paths.RepositoryConfigurationDirectory);
        await File.WriteAllTextAsync(
            paths.RepositoryConfiguration,
            """{"repositoryIntelligence":{"persistence":true,"archeology":true,"recall":true,"maintenance":true}}""",
            cancellationToken);
        await File.WriteAllTextAsync(temporary.GetPath("README.md"), "Existing repository content", cancellationToken);
        var identity = RepositoryIdentity.Create(paths.RepositoryRoot);

        await WithComposedHostAsync(
            paths,
            async (foundation, applications, token) =>
            {
                var sessionId = await CreateOpenedIntelligenceSessionAsync(
                    foundation, applications, paths, token);
                var initial = await applications.Dispatcher.DispatchAsync(
                    new GetRepositoryIntelligenceControlsCommand(identity, sessionId),
                    token);
                Assert.False(initial.Persistence);
                Assert.False(initial.Archeology);
                Assert.False(initial.Recall);
                Assert.False(initial.Maintenance);

                var changed = await applications.Dispatcher.DispatchAsync(
                    new SetRepositoryIntelligenceControlCommand(identity, RepositoryIntelligenceControl.Recall, true),
                    token);
                Assert.False(changed.Persistence);
                Assert.False(changed.Archeology);
                Assert.True(changed.Recall);
                Assert.False(changed.Maintenance);
                return true;
            },
            cancellationToken);

        await WithComposedHostAsync(
            paths,
            async (foundation, applications, token) =>
            {
                var sessionId = await CreateOpenedIntelligenceSessionAsync(
                    foundation, applications, paths, token);
                var restored = await applications.Dispatcher.DispatchAsync(
                    new GetRepositoryIntelligenceControlsCommand(identity, sessionId),
                    token);
                Assert.False(restored.Persistence);
                Assert.True(restored.Recall);
                return true;
            },
            cancellationToken);

        Assert.Contains("\"persistence\":true", await File.ReadAllTextAsync(paths.RepositoryConfiguration, cancellationToken));
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Toggling intelligence authority does not delete user memory or launch analysis.</summary>
    [Fact]
    public static async Task IntelligenceDisablePreservesCuratedMemoryAsync()
    {
        using var temporary = new TemporaryDirectory("repository-intelligence-memory");
        var paths = CreatePaths(temporary.Root);
        var identity = RepositoryIdentity.Create(paths.RepositoryRoot);
        var cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(temporary.GetPath("README.md"), "fixture repository", cancellationToken);

        await WithComposedHostAsync(
            paths,
            async (foundation, applications, token) =>
            {
                var shell = new HeadlessShell(
                    applications.Dispatcher,
                    foundation.Projections,
                    TextWriter.Null,
                    foundation.WebFetchAuthorization,
                    paths.RepositoryRoot);
                var created = await shell.CreateNewSessionAsync(token);
                var sessionId = created.ActiveSession.SessionId;
                await applications.Dispatcher.DispatchAsync(
                    new OpenRepositoryCommand(sessionId, paths.RepositoryRoot, RepositoryTrustLevel.UntrustedInspection),
                    token);
                var remembered = await applications.Dispatcher.DispatchAsync(
                    new RememberRepositoryMemoryCommand(sessionId, identity, "Keep this curated note."),
                    token);
                Assert.NotNull(remembered.Id);

                await applications.Dispatcher.DispatchAsync(
                    new SetRepositoryIntelligenceControlCommand(identity, RepositoryIntelligenceControl.Persistence, true),
                    token);
                await applications.Dispatcher.DispatchAsync(
                    new SetRepositoryIntelligenceControlCommand(identity, RepositoryIntelligenceControl.Persistence, false),
                    token);
                var disabled = await applications.Dispatcher.DispatchAsync(
                    new GetRepositoryIntelligenceControlsCommand(identity, sessionId),
                    token);
                Assert.False(disabled.Persistence);
                var whileDisabled = await applications.Dispatcher.DispatchAsync(
                    new ListRepositoryMemoryCommand(sessionId, identity),
                    token);
                Assert.Contains(whileDisabled.Entries, entry => entry.Id == remembered.Id);

                await applications.Dispatcher.DispatchAsync(
                    new SetRepositoryIntelligenceControlCommand(identity, RepositoryIntelligenceControl.Persistence, true),
                    token);
                var afterReenable = await applications.Dispatcher.DispatchAsync(
                    new ListRepositoryMemoryCommand(sessionId, identity),
                    token);
                Assert.Contains(afterReenable.Entries, entry => entry.Id == remembered.Id);
                var preview = await applications.Dispatcher.DispatchAsync(
                    new PreviewRepositoryIntelligenceOperationCommand(
                        new RepositoryIntelligenceOperationSelection(identity, "src", false, "caller-value", 10, 2, 1),
                        OneOffInvestigation: false),
                    token);
                Assert.False(preview.Available);
                return true;
            },
            cancellationToken);

        SqliteConnection.ClearAllPools();
    }

    /// <summary>Manual, model-shaped, and internal status requests share tool receipts and control results.</summary>
    [Fact]
    public static async Task RepositoryIntelligenceStatus_EntryPointsShareGovernedReceiptsAsync()
    {
        using var temporary = new TemporaryDirectory("repository-intelligence-status");
        var paths = CreatePaths(temporary.Root);
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(temporary.GetPath("README.md"), "fixture repository", token);
        var identity = RepositoryIdentity.Create(paths.RepositoryRoot);

        await WithComposedHostAsync(
            paths,
            async (foundation, applications, cancellationToken) =>
        {
            var sessionId = await CreateOpenedIntelligenceSessionAsync(
                foundation, applications, paths, cancellationToken);
            var observed = new List<IDomainEvent>();
            await using var subscription = foundation.Events.Subscribe((domainEvent, _) =>
            {
                observed.Add(domainEvent);
                return Task.CompletedTask;
            });

            var presenter = new InteractionPresenter(applications.Dispatcher, foundation.Projections);
            var interactive = await presenter.GetRepositoryIntelligenceStatusAsync(
                sessionId, identity, cancellationToken);
            using var output = new StringWriter();
            var headless = new HeadlessShell(
                applications.Dispatcher,
                foundation.Projections,
                output,
                foundation.WebFetchAuthorization,
                paths.RepositoryRoot);
            var exitCode = await headless.WriteRepositoryIntelligenceStatusAsync(
                paths.RepositoryRoot,
                RepositoryTrustLevel.UntrustedInspection,
                cancellationToken);
            var manual = JsonSerializer.Deserialize<RepositoryIntelligenceStatusReceipt>(output.ToString());
            Assert.NotNull(manual);
            Assert.Equal(0, exitCode);
            var model = new FakeModelProvider(new ScriptedSession
            {
                Turns =
                [
                    new ScriptedTurn
                    {
                        ToolName = "repository_intelligence",
                        ArgumentsJson = "{}",
                    },
                ],
            });
            var modelApplication = new SessionApplication(
                foundation.Events,
                model,
                foundation.Budget,
                foundation.Sanitizer,
                NullLogger<SessionApplication>.Instance,
                foundation.ToolPipeline,
                (_, _) => Task.FromResult(CreateIntelligenceToolRequest(sessionId, paths.RepositoryRoot).Context),
                toolRegistry: foundation.ToolRegistry,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var modelSessionId = await modelApplication.HandleAsync(
                new CreateSessionCommand("Model intelligence status"), cancellationToken);
            var modelRunId = await modelApplication.HandleAsync(
                new SubmitRequestCommand(modelSessionId, "Report intelligence status"), cancellationToken);
            Assert.True(await modelApplication.HandleAsync(
                new WaitForRunCommand(modelRunId), cancellationToken));
            var internalControls = await applications.Dispatcher.DispatchAsync(
                new GetRepositoryIntelligenceControlsCommand(identity, sessionId), cancellationToken);

            Assert.True(manual.Succeeded);
            Assert.Equal(interactive.Status, manual.Status);
            var modelStart = Assert.Single(
                observed.OfType<ToolInvocationStarted>(),
                item => item.ToolName == "repository_intelligence" && item.RequestedBy == "model");
            var modelCompletion = Assert.Single(
                observed.OfType<ToolInvocationCompleted>(),
                item => item.ToolInvocationId == modelStart.ToolInvocationId);
            Assert.True(modelCompletion.Succeeded, modelCompletion.Error);
            var modelStatus = JsonSerializer.Deserialize<RepositoryIntelligenceStatus>(modelCompletion.ResultJson!);
            Assert.NotNull(modelStatus);
            Assert.Equal(manual.Status, modelStatus);
            Assert.Equal(manual.Status!.Controls, internalControls);
            Assert.False(manual.Status.AnalysisAvailable);
            var started = observed.OfType<ToolInvocationStarted>()
                .Where(item => item.ToolName == "repository_intelligence").ToArray();
            var completed = observed.OfType<ToolInvocationCompleted>()
                .Where(item => started.Any(start => start.ToolInvocationId == item.ToolInvocationId)).ToArray();
            Assert.Equal(4, started.Length);
            Assert.Equal(4, completed.Length);
            Assert.All(completed, item => Assert.True(item.Succeeded));
            Assert.Contains(started, item => item.ToolInvocationId == manual.InvocationId && item.RequestedBy == "user");
            Assert.Contains(started, item => item.ToolInvocationId == interactive.InvocationId && item.RequestedBy == "user");
            Assert.Contains(started, item => item.ToolInvocationId == modelStart.ToolInvocationId && item.RequestedBy == "model");
            return true;
            },
            token);
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Policy and child-run denials complete through the pipeline without resolving feature storage.</summary>
    [Fact]
    public static async Task RepositoryIntelligenceStatus_DeniedAndDelegatedCallsDoNoFeatureWorkAsync()
    {
        using var temporary = new TemporaryDirectory("repository-intelligence-denial");
        var paths = CreatePaths(temporary.Root);
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(temporary.GetPath("README.md"), "fixture repository", token);

        await WithComposedHostAsync(
            paths,
            async (foundation, applications, cancellationToken) =>
        {
            var sessionId = await CreateOpenedIntelligenceSessionAsync(
                foundation, applications, paths, cancellationToken);
            Assert.False(IsFeatureCreated(applications));
            var observed = new List<IDomainEvent>();
            await using var subscription = foundation.Events.Subscribe((domainEvent, _) =>
            {
                observed.Add(domainEvent);
                return Task.CompletedTask;
            });
            var denied = await foundation.ToolPipeline.InvokeAsync(
                CreateIntelligenceToolRequest(sessionId, paths.RepositoryRoot) with
                {
                    Context = CreateIntelligenceToolRequest(sessionId, paths.RepositoryRoot).Context with
                    {
                        DeniedToolIds = ["repository_intelligence"],
                    },
                },
                cancellationToken);
            var delegated = await foundation.ToolPipeline.InvokeAsync(
                CreateIntelligenceToolRequest(sessionId, paths.RepositoryRoot) with
                {
                    Context = CreateDelegatedIntelligenceContext(sessionId, paths.RepositoryRoot),
                },
                cancellationToken);

            Assert.Equal(ToolErrorClassification.PolicyDenied, denied.ErrorClassification);
            Assert.Equal(ToolErrorClassification.PolicyDenied, delegated.ErrorClassification);
            Assert.False(IsFeatureCreated(applications));
            Assert.Equal(2, observed.OfType<ToolInvocationStarted>()
                .Count(item => item.ToolName == "repository_intelligence"));
            Assert.Equal(2, observed.OfType<ToolInvocationCompleted>()
                .Count(item => item.ToolInvocationId == denied.ToolInvocationId
                    || item.ToolInvocationId == delegated.ToolInvocationId));
            return true;
            },
            token);
        SqliteConnection.ClearAllPools();
    }

    private static ToolInvocationRequest CreateIntelligenceToolRequest(SessionId sessionId, string repositoryPath)
    {
        return new ToolInvocationRequest
        {
            SessionId = sessionId,
            RunId = RunId.New(),
            ToolId = "repository_intelligence",
            ArgumentsJson = "{}",
            Context = new ToolInvocationContext
            {
                RepositoryPath = repositoryPath,
                TrustLevel = RepositoryTrustLevel.UntrustedInspection,
                RequestedBy = "model",
            },
        };
    }

    private static ToolInvocationContext CreateDelegatedIntelligenceContext(SessionId sessionId, string repositoryPath)
    {
        var assignment = new AgentAssignment
        {
            AssignmentId = AgentAssignmentId.New(),
            ChildRunId = RunId.New(),
            Mode = AgentRunMode.ReadOnlyBaseline,
            Objective = "Inspect repository status",
            OutputSchema = AgentAssignment.ResponseSchema,
            StoppingCondition = "Return status",
            Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
            Scope = new AgentAssignmentScope { Files = ["README.md"], IsOwnershipProven = true },
            Policy = new AgentPolicySnapshot
            {
                AllowedToolIds = ["repository_intelligence"],
                ModelSelectionRationale = "test",
                ContextPolicyVersion = "test/1",
                ToolPolicyVersion = "test/1",
            },
            Budget = new AgentResourceBudget { ToolCalls = 1 },
        };
        var plan = new DelegationPlan
        {
            DelegationId = DelegationId.New(),
            Provenance = new DelegationProvenance
            {
                SessionId = sessionId,
                ParentRunId = RunId.New(),
                RepositoryIdentity = RepositoryIdentity.Create(repositoryPath),
                BaselineIdentity = "test-baseline",
                WorkspaceId = WorkspaceId.New(),
            },
            Assignments = [assignment],
            ParentBudget = assignment.Budget,
            AcceptedAt = DateTimeOffset.UtcNow,
        };
        return AgentToolPolicy.Scope(
            CreateIntelligenceToolRequest(sessionId, repositoryPath).Context,
            plan,
            assignment,
            repositoryPath) with { RequestedBy = "model" };
    }

    private static async Task<SessionId> CreateOpenedIntelligenceSessionAsync(
        HostFoundation foundation,
        ApplicationServices applications,
        ConfigurationPaths paths,
        CancellationToken cancellationToken)
    {
        var shell = new HeadlessShell(
            applications.Dispatcher,
            foundation.Projections,
            TextWriter.Null,
            foundation.WebFetchAuthorization,
            paths.RepositoryRoot);
        var created = await shell.CreateNewSessionAsync(cancellationToken);
        var sessionId = created.ActiveSession.SessionId;
        await applications.Dispatcher.DispatchAsync(
            new OpenRepositoryCommand(
                sessionId,
                paths.RepositoryRoot,
                RepositoryTrustLevel.UntrustedInspection),
            cancellationToken);
        return sessionId;
    }

    private static bool IsFeatureCreated(ApplicationServices applications)
    {
        var property = typeof(ApplicationServices).GetProperty(
            "RepositoryIntelligence",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(property);
        var lazy = property.GetValue(applications);
        Assert.NotNull(lazy);
        var created = lazy.GetType().GetProperty(nameof(Lazy<>.IsValueCreated));
        Assert.NotNull(created);
        return Assert.IsType<bool>(created.GetValue(lazy));
    }

    private static async Task<TResult> WithComposedHostAsync<TResult>(
        ConfigurationPaths paths,
        Func<HostFoundation, ApplicationServices, CancellationToken, Task<TResult>> action,
        CancellationToken cancellationToken)
    {
        var configuration = ConfigurationBootstrap.Build([], paths);
        var trusted = ConfigurationBootstrap.BuildTrusted(paths);
        using var loggerFactory = LoggerFactory.Create(_ => { });
        await using var foundation = await HostFoundation.CreateAsync(
            configuration, trusted, paths, loggerFactory, TestPromptLoader.Instance);
        using var models = await ModelComposition.CreateAsync(
            configuration, paths, foundation.SecretResolver, loggerFactory, trustedConfiguration: trusted);
        await using var mcp = await IntegrationComposition.CreateMcpManagerAsync(
            trusted,
            useInteractiveTerminal: false,
            foundation.SecretResolver,
            foundation.Sanitizer,
            foundation.ToolRegistry,
            foundation.ToolStateManager,
            foundation.ToolPipeline,
            paths.RepositoryRoot,
            loggerFactory,
            foundation.PromptLoader,
            foundation.HookCoordinator,
            cancellationToken);
        await using var applications = await ApplicationComposition.CreateAsync(
            new ApplicationCompositionInputs
            {
                Host = new HostCompositionInputs
                {
                    Configuration = configuration,
                    TrustedConfiguration = trusted,
                    Paths = paths,
                    LoggerFactory = loggerFactory,
                    Events = foundation.Events,
                    Projections = foundation.Projections,
                    ExecutionLimits = foundation.ExecutionLimits,
                    OperationalLimits = foundation.OperationalLimits,
                    Sanitizer = foundation.Sanitizer,
                    PromptAppendLoader = foundation.PromptAppendLoader,
                    PromptLoader = foundation.PromptLoader,
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
                    CodeExploreOutputOptions = foundation.CodeExploreOutputOptions,
                    WebFetchAuthorization = foundation.WebFetchAuthorization,
                    RepositorySecretProvider = foundation.RepositorySecretProvider,
                    ProcessManager = foundation.ProcessManager,
                    HookCoordinator = foundation.HookCoordinator,
                },
                Semantic = new SemanticCompositionInputs
                {
                    SemanticEngines = foundation.SemanticEngines,
                    SemanticMutations = foundation.SemanticMutations,
                    SemanticRefreshCoordinator = foundation.SemanticRefreshCoordinator,
                    SemanticRefreshPublicationGate = foundation.SemanticRefreshPublicationGate,
                },
                Integration = new IntegrationCompositionInputs
                {
                    McpManager = mcp,
                    Models = models,
                },
            });

        return await action(foundation, applications, cancellationToken);
    }
}
