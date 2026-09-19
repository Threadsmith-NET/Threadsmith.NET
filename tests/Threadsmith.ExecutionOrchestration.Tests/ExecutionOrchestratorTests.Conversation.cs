namespace Threadsmith.ExecutionOrchestration.Tests;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class ExecutionOrchestratorTests
{
    /// <summary>Verifies an approved successful execution is retained as an assistant receipt for the next request.</summary>
    [Fact]
    public async Task CompletedExecution_ArchivesOutcomeAndIncludesItInTheNextUndoContext()
    {
        // Arrange
        await using var scenario = await ConversationScenario.CreateAsync(
            new ExecutionOutcomeTemplate(
                ExecutionCheckpointPhase.Completed,
                ["src/ShellRunner.cs"],
                ["Added the _test field."],
                RollbackAvailable: true));

        // Act
        await scenario.CompletePlannedExecutionAsync();
        var archived = await scenario.GetReopenedSnapshotAsync();
        var undoRun = await scenario.Dispatcher.DispatchAsync(
            new SubmitRequestCommand(scenario.SessionId, "undo that change"));
        Assert.True(await scenario.Dispatcher.DispatchAsync(new WaitForRunCommand(undoRun)));

        // Assert
        Assert.Collection(
            archived.Messages,
            message =>
            {
                Assert.Equal(ConversationRole.User, message.Role);
                Assert.Contains("Add a private const string field", message.Content, StringComparison.Ordinal);
            },
            message =>
            {
                Assert.Equal(ConversationRole.Assistant, message.Role);
                Assert.Contains("Completed", message.Content, StringComparison.Ordinal);
                Assert.Contains("src/ShellRunner.cs", message.Content, StringComparison.Ordinal);
                Assert.Contains("Added the _test field.", message.Content, StringComparison.Ordinal);
                Assert.Contains("rollback", message.Content, StringComparison.OrdinalIgnoreCase);
            });
        var undoRequest = scenario.Model.SecondRequest
            ?? throw new InvalidOperationException("The undo request did not reach the model.");
        Assert.Contains(
            undoRequest.Messages,
            message => message.SectionId == "recent-user"
                && message.GetModelVisibleContent().Contains("Add a private const string field", StringComparison.Ordinal));
        Assert.Contains(
            undoRequest.Messages,
            message => message.SectionId == "recent-assistant"
                && message.GetModelVisibleContent().Contains("src/ShellRunner.cs", StringComparison.Ordinal));
    }

    /// <summary>Verifies failed executions retain their authoritative applied state without claiming success.</summary>
    [Fact]
    public async Task FailedExecution_ArchivesFailureOutcomeWithoutACompletionClaim()
    {
        // Arrange
        await using var scenario = await ConversationScenario.CreateAsync(
            new ExecutionOutcomeTemplate(
                ExecutionCheckpointPhase.Failed,
                ["src/ShellRunner.cs"],
                ["The field was staged before validation failed."],
                RollbackAvailable: true));

        // Act
        var completed = await scenario.CompletePlannedExecutionAsync();
        var archived = await scenario.Store.GetSnapshotAsync(scenario.SessionId);

        // Assert
        Assert.False(completed);
        var receipt = Assert.Single(archived.Messages, message => message.Role == ConversationRole.Assistant);
        Assert.Contains("Failed", receipt.Content, StringComparison.Ordinal);
        Assert.Contains("src/ShellRunner.cs", receipt.Content, StringComparison.Ordinal);
        Assert.Contains("rollback", receipt.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("execution completed", receipt.Content, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies an archive outage cannot change the authoritative execution completion result.</summary>
    [Fact]
    public async Task ExecutionOutcomeArchiveFailure_PreservesAuthoritativeCompletion()
    {
        // Arrange
        await using var scenario = await ConversationScenario.CreateAsync(
            new ExecutionOutcomeTemplate(
                ExecutionCheckpointPhase.Completed,
                ["src/ShellRunner.cs"],
                ["Added the _test field."],
                RollbackAvailable: true),
            failAssistantArchive: true);

        // Act
        var completed = await scenario.CompletePlannedExecutionAsync();
        var archived = await scenario.Store.GetSnapshotAsync(scenario.SessionId);

        // Assert
        Assert.True(completed);
        Assert.Single(archived.Messages, message => message.Role == ConversationRole.User);
        Assert.DoesNotContain(archived.Messages, message => message.Role == ConversationRole.Assistant);
    }

    /// <summary>Verifies the real staged-mutation orchestration path reaches the same conversation receipt boundary.</summary>
    [Fact]
    public async Task RealExecutionOrchestrator_ArchivesCompletedMutationOutcome()
    {
        // Arrange
        var fixture = CreateFixture();
        await using var scenario = await ConversationScenario.CreateAsync(fixture);

        // Act
        var runId = await scenario.SubmitAndApprovePlanAsync();
        var outcome = await fixture.Orchestrator.ContinueAsync(new ContinueExecutionRequest
        {
            SessionId = scenario.SessionId,
            RunId = runId,
            Approval = new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = fixture.Staged.ApprovalId,
            },
            ApprovalProvenance = "test user",
        });
        Assert.True(await scenario.Dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
        var archived = await scenario.Store.GetSnapshotAsync(scenario.SessionId);

        // Assert
        Assert.Equal(ExecutionCheckpointPhase.Completed, outcome.Status);
        var receipt = Assert.Single(archived.Messages, message => message.Role == ConversationRole.Assistant);
        Assert.Contains("Completed", receipt.Content, StringComparison.Ordinal);
        Assert.Contains("src/Example.cs", receipt.Content, StringComparison.Ordinal);
    }

    /// <summary>Verifies an ordinary direct response remains one archived assistant message.</summary>
    [Fact]
    public async Task OrdinaryResponse_ArchivesOneAssistantMessage()
    {
        // Arrange
        await using var scenario = await ConversationScenario.CreateAsync(outcome: null);

        // Act
        var runId = await scenario.Dispatcher.DispatchAsync(
            new SubmitRequestCommand(scenario.SessionId, "say hello"));
        Assert.True(await scenario.Dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
        var archived = await scenario.Store.GetSnapshotAsync(scenario.SessionId);

        // Assert
        Assert.Collection(
            archived.Messages,
            message => Assert.Equal(ConversationRole.User, message.Role),
            message =>
            {
                Assert.Equal(ConversationRole.Assistant, message.Role);
                Assert.Equal("Hello from the ordinary conversation.", message.Content);
            });
    }

    /// <summary>Current-run receipts must reach the model even when ordinary history is disabled.</summary>
    [Theory]
    [InlineData(ConversationContextMode.ConversationAware)]
    [InlineData(ConversationContextMode.GovernedMemoryOnly)]
    [InlineData(ConversationContextMode.Stateless)]
    public async Task IncrementalPlanBoundary_ProvidesReceipt_AndRequiresExplicitCompletion(ConversationContextMode mode)
    {
        await using var scenario = await ConversationScenario.CreateIncrementalAsync(maximumPlans: 1);
        await scenario.Store.SetModeAsync(scenario.SessionId, mode);

        var runId = await scenario.SubmitAndApprovePlanAsync();
        Assert.True(await scenario.Dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
        var archived = await scenario.Store.GetSnapshotAsync(scenario.SessionId);

        var continuationRequest = scenario.Model.SecondRequest
            ?? throw new InvalidOperationException("The objective continuation did not reach the model.");
        Assert.Contains(
            continuationRequest.Messages,
            message => message.GetModelVisibleContent().Contains(
                "ShellRunner declares the requested field.",
                StringComparison.Ordinal));
        Assert.Contains(continuationRequest.Tools, tool => tool.Name == "complete_objective");
        Assert.DoesNotContain(continuationRequest.Tools, tool => tool.Name == "propose_plan");
        Assert.Contains(
            archived.Messages,
            message => message.Role == ConversationRole.Assistant
                && message.Content == "Hello from the ordinary conversation.");
        Assert.Contains(
            archived.Messages,
            message => message.Role == ConversationRole.Assistant
                && message.Content?.Contains("PlanContinuationPending", StringComparison.Ordinal) == true);
        Assert.Contains(
            archived.Messages,
            message => message.Role == ConversationRole.Assistant
                && message.Content?.Contains("Completed", StringComparison.Ordinal) == true);
    }

    /// <summary>Neither exhaustion nor a clarification question is objective completion.</summary>
    [Theory]
    [InlineData(1, "More work remains, but the plan cap was reached.")]
    [InlineData(4, "Which implementation should I use for the remaining work?")]
    public async Task IncrementalPlanBoundary_OrdinaryTextDoesNotClaimSuccess(int maximumPlans, string response)
    {
        await using var scenario = await ConversationScenario.CreateIncrementalAsync(maximumPlans: maximumPlans);
        scenario.Model.ConfirmObjective = false;
        scenario.Model.ResponseText = response;
        var runId = await scenario.SubmitAndApprovePlanAsync();

        Assert.False(await scenario.Dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
        var snapshot = await scenario.Store.GetSnapshotAsync(scenario.SessionId);
        Assert.DoesNotContain(snapshot.Messages, message => message.Content?.Contains("\"Status\":\"Completed\"", StringComparison.Ordinal) == true);
        Assert.Contains(snapshot.Messages, message => message.Content == response);
        Assert.Equal(maximumPlans > 1, scenario.Model.SecondRequest!.Tools.Any(tool => tool.Name == "propose_plan"));
    }

    /// <summary>Fresh session/engine instances and subsequent same-process resumes reattach one observer.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeCommand_ReattachesPlanningAcrossProcessAndPauseBoundaries(bool atPlanBoundary)
    {
        var fixture = CreateFixture();
        var request = fixture.StartRequest with { AllowPlanContinuation = true };
        await fixture.Orchestrator.StartAsync(request);
        if (atPlanBoundary)
        {
            await fixture.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.Staged));
        }

        var restored = fixture with { Orchestrator = RecreateOrchestrator(fixture) };
        await using var scenario = await ConversationScenario.CreateRestoredAsync(restored);
        scenario.Model.ConfirmObjective = false;
        await scenario.Dispatcher.DispatchAsync(new ResumeRunCommand(request.SessionId, request.RunId));
        if (!atPlanBoundary)
        {
            await restored.Orchestrator.ContinueAsync(CreateContinuation(fixture, fixture.Staged));
        }

        Assert.False(await scenario.Dispatcher.DispatchAsync(new WaitForRunCommand(request.RunId)));
        Assert.Null(await fixture.Checkpoints.GetOutcomeAsync(request.RunId));
        scenario.Model.ConfirmObjective = true;
        await scenario.Dispatcher.DispatchAsync(new ResumeRunCommand(request.SessionId, request.RunId));

        Assert.True(await scenario.Dispatcher.DispatchAsync(new WaitForRunCommand(request.RunId)));
        Assert.Single(fixture.ProposalHandler.Commands);
        Assert.Single(fixture.CommitHandler.Commands);
        Assert.Contains(scenario.Model.LastRequest!.Messages, message =>
            message.GetModelVisibleContent().Contains("src/Example.cs", StringComparison.Ordinal));
        Assert.Equal(ExecutionCheckpointPhase.Completed, (await fixture.Checkpoints.GetOutcomeAsync(request.RunId))!.Status);
    }

    /// <summary>Verifies the conversation entry point can approve and execute a second plan on the same run.</summary>
    [Fact]
    public async Task IncrementalPlanBoundary_ProposesAndExecutesSecondPlan_OnSameRun()
    {
        await using var scenario = await ConversationScenario.CreateIncrementalAsync(planProposalCount: 2);
        var secondPlan = new TaskCompletionSource<PlanProposed>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondApproval = new TaskCompletionSource<RunTransitioned>(TaskCreationOptions.RunContinuationsAsynchronously);
        var approvalTransitions = 0;
        await using var subscription = scenario.Events.Subscribe((domainEvent, _) =>
        {
            if (domainEvent is PlanProposed { Plan.Revision: 2 } proposed)
            {
                secondPlan.TrySetResult(proposed);
            }

            if (domainEvent is RunTransitioned { Destination: RunPhase.AwaitingPlanApproval } transition
                && Interlocked.Increment(ref approvalTransitions) == 2)
            {
                secondApproval.TrySetResult(transition);
            }

            return Task.CompletedTask;
        });

        var runId = await scenario.SubmitAndApprovePlanAsync();
        var proposed = await secondPlan.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var awaiting = await secondApproval.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(runId, proposed.RunId);
        Assert.Equal(runId, awaiting.RunId);
        Assert.True(await scenario.Dispatcher.DispatchAsync(new ApprovePlanCommand(scenario.SessionId, runId)));
        Assert.True(await scenario.Dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

        var archived = await scenario.Store.GetSnapshotAsync(scenario.SessionId);
        Assert.Equal(
            2,
            archived.Messages.Count(message => message.Role == ConversationRole.Assistant
                && message.Content?.Contains("PlanContinuationPending", StringComparison.Ordinal) == true));
        var finalRequest = scenario.Model.ThirdRequest
            ?? throw new InvalidOperationException("The final objective assessment did not reach the model.");
        Assert.Contains(
            finalRequest.Messages,
            message => message.GetModelVisibleContent().Contains("2 tranche(s) complete", StringComparison.Ordinal));
        var undoRun = await scenario.Dispatcher.DispatchAsync(new SubmitRequestCommand(scenario.SessionId, "undo that change"));
        Assert.True(await scenario.Dispatcher.DispatchAsync(new WaitForRunCommand(undoRun)));
        Assert.Contains(scenario.Model.LastRequest!.Messages, message => message.SectionId == "recent-assistant"
            && message.GetModelVisibleContent().Contains("\"Status\":\"Completed\"", StringComparison.Ordinal));
    }

    private sealed record ExecutionOutcomeTemplate(
        ExecutionCheckpointPhase Status,
        IReadOnlyList<string> ChangedFiles,
        IReadOnlyList<string> BehaviorSummary,
        bool RollbackAvailable);

    private sealed class ConversationScenario : IAsyncDisposable
    {
        private readonly string _directory;

        private ConversationScenario(
            string directory,
            DomainEventStream events,
            SqliteConversationStore store,
            CommandDispatcher dispatcher,
            SessionId sessionId,
            ConversationModel model)
        {
            _directory = directory;
            Events = events;
            Store = store;
            Dispatcher = dispatcher;
            SessionId = sessionId;
            Model = model;
        }

        public CommandDispatcher Dispatcher { get; }

        public DomainEventStream Events { get; }

        public ConversationModel Model { get; }

        public SessionId SessionId { get; }

        public SqliteConversationStore Store { get; }

        public static async Task<ConversationScenario> CreateAsync(
            ExecutionOutcomeTemplate? outcome,
            bool failAssistantArchive = false)
        {
            var events = new DomainEventStream();
            return await CreateCoreAsync(
                events,
                CreatePlan(),
                outcome is null ? null : new OutcomeOrchestrator(outcome),
                outcome is null ? null : CreateStartRequest,
                outcome is not null,
                failAssistantArchive);
        }

        public static Task<ConversationScenario> CreateAsync(ExecutionFixture fixture)
        {
            ArgumentNullException.ThrowIfNull(fixture);
            return CreateCoreAsync(
                fixture.Events,
                fixture.StartRequest.ApprovedPlan,
                fixture.Orchestrator,
                CreateStartRequestFactory(fixture),
                proposePlan: true,
                failAssistantArchive: false);
        }

        public static Task<ConversationScenario> CreateRestoredAsync(ExecutionFixture fixture)
        {
            return CreateCoreAsync(
                fixture.Events,
                fixture.StartRequest.ApprovedPlan,
                fixture.Orchestrator,
                CreateStartRequestFactory(fixture),
                proposePlan: false,
                failAssistantArchive: false,
                restoredSession: fixture.StartRequest.SessionId);
        }

        public static Task<ConversationScenario> CreateIncrementalAsync(int planProposalCount = 1, int maximumPlans = 12)
        {
            var events = new DomainEventStream();
            return CreateCoreAsync(
                events,
                CreatePlan(),
                new IncrementalOutcomeOrchestrator(),
                async (sessionId, runId, task, plan, cancellationToken) =>
                {
                    var request = await CreateStartRequest(
                        sessionId,
                        runId,
                        task,
                        plan,
                        cancellationToken) ?? throw new InvalidOperationException("The test request was not created.");
                    return request with { AllowPlanContinuation = true };
                },
                proposePlan: true,
                failAssistantArchive: false,
                planProposalCount,
                new ExecutionLimits { IncrementalPlanning = new IncrementalPlanningOptions { MaximumPlansPerObjective = maximumPlans } });
        }

        public async Task<bool> CompletePlannedExecutionAsync()
        {
            var runId = await SubmitAndApprovePlanAsync();
            return await Dispatcher.DispatchAsync(new WaitForRunCommand(runId));
        }

        public async Task<RunId> SubmitAndApprovePlanAsync()
        {
            var planProposed = new TaskCompletionSource<PlanProposed>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var awaitingPlanApproval = new TaskCompletionSource<RunTransitioned>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using var subscription = Events.Subscribe((domainEvent, _) =>
            {
                if (domainEvent is PlanProposed plan)
                {
                    planProposed.TrySetResult(plan);
                }

                if (domainEvent is RunTransitioned transition
                    && transition.Destination == RunPhase.AwaitingPlanApproval)
                {
                    awaitingPlanApproval.TrySetResult(transition);
                }

                return Task.CompletedTask;
            });
            var runId = await Dispatcher.DispatchAsync(new SubmitRequestCommand(
                SessionId,
                "Add a private const string field to the ShellRunner class named _test with the value tested!."));
            var proposed = await planProposed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(runId, proposed.RunId);
            var transitioned = await awaitingPlanApproval.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(runId, transitioned.RunId);
            Assert.True(await Dispatcher.DispatchAsync(new ApprovePlanCommand(SessionId, runId)));
            return runId;
        }

        public async ValueTask DisposeAsync()
        {
            await Events.DisposeAsync();
            DeleteOwnedDirectory(_directory);
        }

        public async Task<ConversationStateSnapshot> GetReopenedSnapshotAsync()
        {
            var sanitizer = new SecretOutputSanitizer();
            var artifacts = new ArtifactStore(
                $"Data Source={Path.Combine(_directory, "state.db")};Pooling=False",
                Path.Combine(_directory, "artifacts"),
                sanitizer);
            await artifacts.InitializeAsync();
            var reopened = new SqliteConversationStore(
                $"Data Source={Path.Combine(_directory, "state.db")};Pooling=False",
                artifacts,
                sanitizer);
            return await reopened.GetSnapshotAsync(SessionId);
        }

        private static Task<ExecutionStartRequest?> CreateStartRequest(
            SessionId sessionId,
            RunId runId,
            TaskSpecification task,
            ImplementationPlan plan,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var baseline = new WorkspaceBaseline(
                WorkspaceId.New(),
                Path.GetTempPath(),
                DateTimeOffset.UtcNow,
                [],
                ApprovedRoots: ["src"],
                TrustLevel: RepositoryTrustLevel.TrustedMutation);
            return Task.FromResult<ExecutionStartRequest?>(new ExecutionStartRequest
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
                    Confidence = SemanticConfidenceLevel.FullSemantic,
                    Stages = [MutationValidationStage.Semantic],
                },
            });
        }

        private static Func<SessionId, RunId, TaskSpecification, ImplementationPlan, CancellationToken, Task<ExecutionStartRequest?>>
            CreateStartRequestFactory(ExecutionFixture fixture)
        {
            return (sessionId, runId, task, plan, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var validation = fixture.StartRequest.ValidationRequest with
                {
                    SessionId = sessionId,
                    RunId = runId,
                    Baseline = fixture.StartRequest.Baseline,
                };
                return Task.FromResult<ExecutionStartRequest?>(fixture.StartRequest with
                {
                    SessionId = sessionId,
                    RunId = runId,
                    Task = task,
                    ApprovedPlan = plan,
                    ValidationRequest = validation,
                });
            };
        }

        private static ImplementationPlan CreatePlan() => new()
        {
            Summary = "Add the requested private test field.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = StepId.New(),
                    Title = "Add the test field",
                    Description = "Add the requested private const field to ShellRunner.",
                    FileIntents = [new PlanFileIntent { Kind = PlanFileChangeKind.Modify, Path = "src/ShellRunner.cs" }],
                    ExpectedOutcome = "ShellRunner declares the requested field.",
                    Validation = ["Build the affected project."],
                },
            ],
        };

        private static async Task<ConversationScenario> CreateCoreAsync(
            DomainEventStream events,
            ImplementationPlan plan,
            IExecutionOrchestrator? orchestrator,
            Func<SessionId, RunId, TaskSpecification, ImplementationPlan, CancellationToken, Task<ExecutionStartRequest?>>?
                executionRequestFactory,
            bool proposePlan,
            bool failAssistantArchive,
            int? planProposalCount = null,
            ExecutionLimits? limits = null,
            SessionId? restoredSession = null)
        {
            var directory = Path.Combine(Path.GetTempPath(), $"threadsmith-conversation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var connectionString = $"Data Source={Path.Combine(directory, "state.db")};Pooling=False";
            try
            {
                await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync();
                var sanitizer = new SecretOutputSanitizer();
                var artifacts = new ArtifactStore(connectionString, Path.Combine(directory, "artifacts"), sanitizer);
                await artifacts.InitializeAsync();
                var store = new SqliteConversationStore(connectionString, artifacts, sanitizer);
                IConversationStore conversationStore = failAssistantArchive
                    ? new FailingAssistantConversationStore(store)
                    : store;
                var evidence = new EvidenceStore(events, sanitizer);
                var assembler = new ContextAssembler(
                    evidence,
                    new TokenEstimator(),
                    new ContextPolicy(),
                    new PromptAppendLoader(sanitizer),
                    sanitizer,
                    events,
                    TestPromptLoader.Instance,
                    conversationStore: conversationStore);
                var model = new ConversationModel(plan, planProposalCount ?? (proposePlan ? 1 : 0));
                var registry = new ToolRegistry([]);
                var pipeline = new ToolInvocationPipeline(
                    registry,
                    new DefaultPolicyEngine(),
                    new DenyApprovalPolicy(),
                    events,
                    sanitizer,
                    NullLogger<ToolInvocationPipeline>.Instance);
                var application = new SessionApplication(
                    events,
                    model,
                    new ExecutionBudget(new BudgetDimensions(100_000, 100, TimeSpan.FromMinutes(1))),
                    sanitizer,
                    NullLogger<SessionApplication>.Instance,
                    pipeline,
                    (_, cancellationToken) => CreateToolInvocationContextAsync(directory, cancellationToken),
                    contextAssembler: assembler,
                    evidenceStore: evidence,
                    limits: limits,
                    conversationStore: conversationStore,
                    executionOrchestrator: orchestrator,
                    executionRequestFactory: executionRequestFactory,
                    correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                    prompts: TestPromptLoader.Instance);
                var dispatcher = new CommandDispatcher([application]);
                var sessionId = restoredSession ?? await dispatcher.DispatchAsync(new CreateSessionCommand("conversation outcome"));
                if (restoredSession is not null)
                {
                    application.RegisterRestoredSession(sessionId);
                }

                return new ConversationScenario(directory, events, store, dispatcher, sessionId, model);
            }
            catch
            {
                DeleteOwnedDirectory(directory);
                throw;
            }
        }

        private static Task<ToolInvocationContext> CreateToolInvocationContextAsync(
            string directory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ToolInvocationContext
            {
                RepositoryPath = directory,
                TrustLevel = RepositoryTrustLevel.TrustedMutation,
                RequestedBy = "conversation-outcome-test",
            });
        }

        private static void DeleteOwnedDirectory(string directory)
        {
            var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var resolvedDirectory = Path.GetFullPath(directory);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var expectedParent = Path.GetDirectoryName(resolvedDirectory);
            if (!string.Equals(expectedParent, temporaryRoot, comparison)
                || !Path.GetFileName(resolvedDirectory).StartsWith("threadsmith-conversation-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The test cleanup directory is outside the owned temporary root.");
            }

            Directory.Delete(resolvedDirectory, recursive: true);
        }
    }

    private sealed class FailingAssistantConversationStore : IConversationStore
    {
        private readonly IConversationStore _inner;

        public FailingAssistantConversationStore(IConversationStore inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public Task<ConversationMessage> ArchiveMessageAsync(
            ConversationMessage message,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(message);
            if (message.Role == ConversationRole.Assistant)
            {
                throw new InvalidOperationException("The test conversation archive rejected the assistant receipt.");
            }

            return _inner.ArchiveMessageAsync(message, cancellationToken);
        }

        public Task SetModeAsync(
            SessionId sessionId,
            ConversationContextMode mode,
            CancellationToken cancellationToken = default)
        {
            return _inner.SetModeAsync(sessionId, mode, cancellationToken);
        }

        public Task<ConversationStateSnapshot> GetSnapshotAsync(
            SessionId sessionId,
            bool includeBodies = true,
            CancellationToken cancellationToken = default)
        {
            return _inner.GetSnapshotAsync(sessionId, includeBodies, cancellationToken);
        }

        public Task<int> RemoveMessageBodiesOlderThanAsync(
            DateTimeOffset cutoff,
            CancellationToken cancellationToken = default)
        {
            return _inner.RemoveMessageBodiesOlderThanAsync(cutoff, cancellationToken);
        }
    }

    private sealed class ConversationModel : IModelProvider
    {
        private readonly ImplementationPlan _plan;
        private readonly int _planProposalCount;
        private int _requests;

        public ConversationModel(ImplementationPlan plan, int planProposalCount)
        {
            _plan = plan;
            _planProposalCount = planProposalCount;
        }

        public ModelStreamRequest? SecondRequest { get; private set; }

        public ModelStreamRequest? ThirdRequest { get; private set; }

        public ModelStreamRequest? LastRequest { get; private set; }

        public bool ConfirmObjective { get; set; } = true;

        public string ResponseText { get; set; } = "Hello from the ordinary conversation.";

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests++;
            LastRequest = request;
            await Task.Yield();
            if (_requests == 2)
            {
                SecondRequest = request;
            }
            else if (_requests == 3)
            {
                ThirdRequest = request;
            }

            if (_requests <= _planProposalCount
                && request.Tools.Any(tool => string.Equals(tool.Name, "propose_plan", StringComparison.Ordinal)))
            {
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput("propose_plan", SerializePlanProposal(_plan)),
                    FinishReason = ModelFinishReason.ToolCalls,
                };
                yield break;
            }

            SecondRequest ??= request;
            yield return new ModelChunk
            {
                Text = ResponseText,
                Output = ConfirmObjective && request.Tools.Any(tool => tool.Name == "complete_objective")
                    ? new ToolRequestModelOutput("complete_objective", "{}")
                    : null,
                FinishReason = ModelFinishReason.Stop,
            };
        }
    }

    private sealed class OutcomeOrchestrator : IExecutionOrchestrator
    {
        private readonly ExecutionOutcomeTemplate _template;
        private ExecutionStartRequest? _request;

        public OutcomeOrchestrator(ExecutionOutcomeTemplate template)
        {
            _template = template;
        }

        public Task<ExecutionOutcomeProjection> ContinueAsync(
            ContinueExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExecutionContinuation> ResumeAsync(
            SessionId sessionId,
            RunId runId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExecutionContinuation> StartAsync(
            ExecutionStartRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _request = request;
            return Task.FromResult(new ExecutionContinuation
            {
                SessionId = request.SessionId,
                RunId = request.RunId,
                WorkspaceId = request.Baseline.WorkspaceId,
                PlanRevision = request.ApprovedPlan.Revision,
                PlanHash = "conversation-outcome-test",
                Phase = ExecutionCheckpointPhase.MutationApprovalPending,
                DiagnosticBaselineIdentity = "diagnostic",
                MutationBaselineIdentity = "mutation",
                NextAction = "complete test execution",
                RecordedAt = DateTimeOffset.UtcNow,
            });
        }

        public Task<ExecutionOutcomeProjection> WaitForOutcomeAsync(
            RunId runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = _request ?? throw new InvalidOperationException("Execution did not start.");
            return Task.FromResult(new ExecutionOutcomeProjection
            {
                Key = new ProjectionKey("execution-outcome", runId.Value.ToString("D")),
                SessionId = request.SessionId,
                RunId = runId,
                Status = _template.Status,
                ChangedFiles = _template.ChangedFiles,
                BehaviorSummary = _template.BehaviorSummary,
                ApprovalProvenance = "test",
                RollbackAvailable = _template.RollbackAvailable,
            });
        }
    }

    private sealed class IncrementalOutcomeOrchestrator : IExecutionOrchestrator
    {
        private readonly ConcurrentDictionary<int, TaskCompletionSource<ExecutionPlanBoundary>> _boundaries = new();
        private readonly TaskCompletionSource<ExecutionOutcomeProjection> _terminal = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private ExecutionStartRequest? _request;
        private int _planOrdinal;

        public Task<ExecutionOutcomeProjection> ContinueAsync(
            ContinueExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExecutionContinuation> ContinueWithPlanAsync(
            ExecutionStartRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _request = request;
            var ordinal = Interlocked.Increment(ref _planOrdinal);
            CompleteBoundary(ordinal, request);
            return Task.FromResult(CreateContinuation(request, ordinal));
        }

        public Task<ExecutionOutcomeProjection> CompleteObjectiveAsync(
            SessionId sessionId,
            RunId runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = _request ?? throw new InvalidOperationException("Execution did not start.");
            var outcome = CreateProgress(request, ExecutionCheckpointPhase.Completed);
            _terminal.TrySetResult(outcome);
            return Task.FromResult(outcome);
        }

        public Task<ExecutionContinuation> ResumeAsync(
            SessionId sessionId,
            RunId runId,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ExecutionContinuation> StartAsync(
            ExecutionStartRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _request = request;
            _planOrdinal = 1;
            CompleteBoundary(_planOrdinal, request);
            return Task.FromResult(CreateContinuation(request, _planOrdinal));
        }

        public Task<ExecutionPlanBoundary> WaitForPlanCompletionAsync(
            RunId runId,
            int afterPlanOrdinal,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetOrdinal = checked(afterPlanOrdinal + 1);
            var boundary = _boundaries.GetOrAdd(
                targetOrdinal,
                static _ => new TaskCompletionSource<ExecutionPlanBoundary>(
                    TaskCreationOptions.RunContinuationsAsynchronously));
            return boundary.Task.WaitAsync(cancellationToken);
        }

        public async Task<ExecutionOutcomeProjection> WaitForOutcomeAsync(
            RunId runId,
            CancellationToken cancellationToken = default)
        {
            return await _terminal.Task.WaitAsync(cancellationToken);
        }

        private static ExecutionOutcomeProjection CreateProgress(
            ExecutionStartRequest request,
            ExecutionCheckpointPhase status)
        {
            return new ExecutionOutcomeProjection
            {
                Key = new ProjectionKey("execution-outcome", request.RunId.Value.ToString("D")),
                SessionId = request.SessionId,
                RunId = request.RunId,
                Status = status,
                CompletedStepIds = request.ApprovedPlan.Steps.Select(step => step.StepId).ToArray(),
                ChangedFiles = ["src/ShellRunner.cs"],
                BehaviorSummary = request.ApprovedPlan.Steps.Select(step => step.ExpectedOutcome).ToArray(),
                ApprovalProvenance = "test",
                RollbackAvailable = true,
            };
        }

        private static ExecutionContinuation CreateContinuation(ExecutionStartRequest request, int planOrdinal)
        {
            return new ExecutionContinuation
            {
                SessionId = request.SessionId,
                RunId = request.RunId,
                WorkspaceId = request.Baseline.WorkspaceId,
                PlanRevision = request.ApprovedPlan.Revision,
                PlanOrdinal = planOrdinal,
                PlanHash = "incremental-conversation-test",
                Phase = ExecutionCheckpointPhase.MutationApprovalPending,
                DiagnosticBaselineIdentity = "diagnostic",
                MutationBaselineIdentity = "mutation",
                NextAction = "complete test plan",
                RecordedAt = DateTimeOffset.UtcNow,
            };
        }

        private void CompleteBoundary(int planOrdinal, ExecutionStartRequest request)
        {
            _boundaries.GetOrAdd(
                planOrdinal,
                static _ => new TaskCompletionSource<ExecutionPlanBoundary>(
                    TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult(new ExecutionPlanBoundary
                {
                    PlanOrdinal = planOrdinal,
                    Progress = CreateProgress(request, ExecutionCheckpointPhase.PlanContinuationPending),
                });
        }
    }
}
