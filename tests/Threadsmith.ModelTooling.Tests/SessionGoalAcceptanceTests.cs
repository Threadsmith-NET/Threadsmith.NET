namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Runs;
using Threadsmith.Interaction.Sessions;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises active goals through normal session submissions.</summary>
public static class SessionGoalAcceptanceTests
{
    private const string Goal = "Run the focused parser test and show that it passes";
    private const string Promise = "I will run the test next.";
    private const string Receipt = "ParserTests passed";

    /// <summary>Only confirmed tool output can satisfy this fixture's goal.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public static async Task PromiseContinuesWithoutUserInputUntilToolReceipt(bool toolTimesOut, bool stopAfterReceipt, bool prohibitOnResume)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var connectionString = $"Data Source=goal-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var store = new SqliteEventStore(connectionString);
        await store.InitializeAsync(cancellationToken);
        await using var persistence = events.Subscribe(store.AppendAsync);
        await using var telemetry = events.Subscribe(new DomainEventTelemetry(NullLogger<DomainEventTelemetry>.Instance).ObserveAsync);
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            return Task.CompletedTask;
        });
        var sanitizer = new SecretOutputSanitizer();
        var prompts = TestPromptLoader.Instance;
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = new ModelProfile
        {
            Id = new ModelProfileId(Guid.NewGuid()), Name = "work", Provider = "fixture", ModelId = "work",
            Endpoint = new Uri("https://example.test/"), ContextWindow = 200000, MaximumOutputTokens = 4096,
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true, StructuredOutput = true },
        };
        var assessment = new ActiveTurnCompactionCandidateProfile
        {
            ProfileId = new ModelProfileId(Guid.NewGuid()), ContextWindowTokens = 64000, OutputReserveTokens = 2048,
            ReasoningLevel = ReasoningLevel.None, SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed, Cost = new(),
        };
        var receiptReady = stopAfterReceipt ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
        var work = new WorkModel(receiptReady);
        var resolver = new ResumeResolver(profile, () => prohibitOnResume && receiptReady?.Task.IsCompleted == true);
        var assembler = new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, events, prompts, modelResolver: resolver);
        var budget = new ExecutionBudget(new BudgetDimensions(200000, 20, TimeSpan.MaxValue));
        var registry = new ToolRegistry([new ParserTestTool { TimesOut = toolTimesOut }]);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, budget, timeProvider: clock);
        var application = new SessionApplication(
            events,
            work,
            budget,
            sanitizer,
            NullLogger<SessionApplication>.Instance,
            pipeline,
            (_, _) => Task.FromResult(new ToolInvocationContext { RepositoryPath = Path.GetTempPath(), TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model" }),
            assembler,
            evidence,
            registry,
            activeTurnCompactionProfile: assessment,
            correctiveMessages: new CorrectiveMessageFactory(prompts),
            prompts: prompts,
            goalAssessmentProvider: new AssessmentModel { Profile = assessment.ProfileId },
            outputClock: clock);
        var dispatcher = new CommandDispatcher([application]);
        var projections = new InMemoryProjectionStore();
        await using var projection = events.Subscribe(projections.ApplyAsync);
        var coordinator = new InteractionCoordinator(new InteractionPresenter(dispatcher, projections), events, new GoalSurface(receiptReady?.Task, resume: stopAfterReceipt), timeProvider: clock);
        await coordinator.RunAsync(cancellationToken: cancellationToken);
        var runs = observed.OfType<RunCompleted>().ToArray();
        Assert.Equal(stopAfterReceipt ? 2 : 1, runs.Length);
        var session = runs[0].SessionId;
        Assert.All(runs, item => Assert.Equal(session, item.SessionId));
        if (prohibitOnResume)
        {
            Assert.True(resolver.DeniedSensitive);
            Assert.False(runs[1].Succeeded);
            Assert.DoesNotContain(runs[1].RunId, work.Dispatches);
            Assert.Equal(Goal, (await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), cancellationToken)).DisplayText);
            return;
        }

        if (toolTimesOut)
        {
            Assert.False(Assert.Single(observed.OfType<ToolInvocationCompleted>()).Succeeded);
            Assert.Equal([true, false], observed.OfType<GoalAssessmentActivity>().Select(item => item.Started));
            Assert.Equal(Goal, (await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), cancellationToken)).DisplayText);
            return;
        }

        Assert.Null((await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), cancellationToken)).DisplayText);
        var persisted = await store.ReadAsync(session, cancellationToken);
        Assert.Equal([true, false, true, false], persisted.OfType<GoalAssessmentActivity>().Select(item => item.Started));
        Assert.Equal([runs[0].RunId, runs[0].RunId, runs[^1].RunId, runs[^1].RunId], persisted.OfType<GoalAssessmentActivity>().Select(item => item.RunId));
        var completedTools = persisted.OfType<ToolInvocationCompleted>().ToArray();
        Assert.Equal(stopAfterReceipt ? 2 : 1, completedTools.Length);
        Assert.All(completedTools, item => Assert.True(item.Succeeded));
        Assert.Equal(runs[0].RunId, completedTools[0].RunId);
        Assert.Equal(runs[^1].RunId, completedTools[^1].RunId);
        var output = observed.OfType<ModelOutputObserved>().Select(item => item.Text.Trim()).ToArray();
        Assert.Single(output, text => text == "Continuing to work toward the goal.");
        Assert.Single(output, text => text.StartsWith("Met: " + Receipt, StringComparison.Ordinal));
        Assert.Equal([true, false, true, false], observed.OfType<GoalAssessmentActivity>().Select(item => item.Started));
    }

    /// <summary>Pause and stop do not wait for the old provider or clear the replacement goal.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public static async Task AcceptedPausePreemptsBlockedAssessment(bool dismissWhileDraining, bool holdHook)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var held = new HeldAssessor();
        var hook = holdHook ? new HeldAssessmentHook() : null;
        var entered = hook?.Entered ?? held.Entered;
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            return Task.CompletedTask;
        });
        var prompts = TestPromptLoader.Instance;
        var app = new SessionApplication(
            events,
            new WorkModel(),
            new ExecutionBudget(new BudgetDimensions(200000, 20, TimeSpan.MaxValue)),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            correctiveMessages: new CorrectiveMessageFactory(prompts),
            prompts: prompts,
            goalAssessmentProvider: held,
            hooks: hook,
            outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("interrupt"), cancellationToken);
        const string initialGoal = "Write one sentence";
        await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, initialGoal), cancellationToken);
        var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, initialGoal), cancellationToken);
        var done = dispatcher.DispatchAsync(new WaitForRunCommand(run), cancellationToken);
        try
        {
            if (await Task.WhenAny(done, entered.Task).WaitAsync(cancellationToken) == done)
            {
                await done;
                Assert.Fail("Work ended before assessment.");
            }

            var pause = await dispatcher.DispatchAsync(new RequestRunSteeringPauseCommand(session, run), cancellationToken);
            Assert.Equal(RunSteeringPauseRequestStatus.Accepted, pause.Status);
            var ready = await dispatcher.DispatchAsync(new WaitForRunSteeringPauseCommand(session, run, pause.PauseId), cancellationToken);
            Assert.Equal(RunSteeringPauseWaitStatus.Ready, ready.Status);
            Assert.Equal(initialGoal, (await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), cancellationToken)).DisplayText);
            if (dismissWhileDraining)
            {
                await dispatcher.DispatchAsync(new SubmitRunSteeringCommand(session, run, pause.PauseId, null), cancellationToken);
                Assert.True(await done.WaitAsync(cancellationToken));
                Assert.Single(observed.OfType<ModelOutputObserved>(), item => item.Text.Contains("previous assessment is still closing", StringComparison.Ordinal));
            }
            else
            {
                await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, "Write replacement text"), cancellationToken);
                Assert.True(await dispatcher.DispatchAsync(new CancelRunCommand(session, run), cancellationToken));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => done.WaitAsync(cancellationToken));
                var replacement = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, "Write replacement text"), cancellationToken);
                Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(replacement), cancellationToken));
            }
        }
        finally
        {
            hook?.Release.TrySetResult();
            held.Release.TrySetResult();
            if (held.Entered.Task.IsCompleted)
            {
                await held.Left.Task.WaitAsync(cancellationToken);
            }
        }

        Assert.Equal(dismissWhileDraining ? initialGoal : null, (await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), cancellationToken)).DisplayText);
        Assert.Equal(dismissWhileDraining || holdHook ? [true, false] : new[] { true, false, true, false }, observed.OfType<GoalAssessmentActivity>().Select(item => item.Started));
        if (holdHook)
        {
            Assert.False(held.Entered.Task.IsCompleted);
        }

        Assert.DoesNotContain(observed.OfType<ModelOutputObserved>(), item => item.Text.Contains("stale verdict", StringComparison.Ordinal));
    }

    /// <summary>A confirmed denial either establishes a blocker or allows work through a permitted alternative.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task PolicyDenialReachesAssessment(bool terminal)
    {
        const string goal = "Write a summary then run the tests";
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var sanitizer = new SecretOutputSanitizer();
        var prompts = TestPromptLoader.Instance;
        var evidence = new EvidenceStore(events, sanitizer);
        var assembler = new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, events, prompts);
        var registry = new ToolRegistry([new ParserTestTool("test_run_targeted"), new ParserTestTool()]);
        var policy = new BlockTestPolicy();
        var pipeline = new ToolInvocationPipeline(registry, policy, new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, timeProvider: clock);
        var model = new PolicyDenialModel(terminal);
        var app = new SessionApplication(
            events,
            model,
            new ExecutionBudget(new BudgetDimensions(200000, 20, TimeSpan.MaxValue)),
            sanitizer,
            NullLogger<SessionApplication>.Instance,
            pipeline,
            (_, _) => Task.FromResult(new ToolInvocationContext { RepositoryPath = Path.GetTempPath(), TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model" }),
            assembler,
            evidence,
            registry,
            correctiveMessages: new CorrectiveMessageFactory(prompts),
            prompts: prompts,
            outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("policy"), cancellationToken);
        await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, goal), cancellationToken);

        var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, goal), cancellationToken);
        Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(run), cancellationToken));
        Assert.Equal(1, policy.Denials);
        Assert.Equal(terminal ? 2 : 4, model.WorkCalls);
        Assert.Equal(terminal ? 2 : 3, model.Assessments);
        Assert.Null((await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), cancellationToken)).DisplayText);
    }

    /// <summary>A passing native check keeps its own identity when all report slots hold failures.</summary>
    [Fact]
    public static async Task NativeCheckIdentitySurvivesFullFailureReport()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var workspace = WorkspaceId.New();
        var repository = Path.GetTempPath();
        var baseline = new WorkspaceBaseline(workspace, repository, clock.GetUtcNow(), [], "revision");
        var checks = new ConversationReceiptChecks();
        var context = new ToolInvocationContext
        {
            RepositoryPath = repository,
            TrustLevel = RepositoryTrustLevel.TrustedRead,
            RequestedBy = "model",
            WorkspaceId = workspace,
            ReceiptChecks = checks,
            ReceiptBaseline = () => baseline,
        };
        var session = SessionId.New();
        var run = RunId.New();
        for (var index = 0; index < 8; index++)
        {
            var executionContext = new ToolExecutionContext(new ToolInvocationId(Guid.NewGuid()), session, run, context);
            await ConversationReceiptChecks.ObserveAsync(
                executionContext,
                "parser_test",
                "Parser.csproj",
                "ParserTests.Parse",
                _ => Task.FromResult(ParserTestTool.TestResult() with { Outcome = TestOutcome.Failed, Passed = 0, Failed = 1 }),
                token);
        }

        var registry = new ToolRegistry([new ParserTestTool { ObserveNativeCheck = true }]);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: clock);
        var invocation = await pipeline.InvokeAsync(
            new ToolInvocationRequest { SessionId = session, RunId = run, ToolId = "parser_test", ArgumentsJson = "{}", Context = context },
            token);

        Assert.True(invocation.Succeeded);
        Assert.Equal(8, checks.Snapshot(baseline).Checks.Length);
        Assert.All(checks.Snapshot(baseline).Checks, check => Assert.False(check.Succeeded));
        var proof = Assert.IsType<ConversationCheckObservation>(invocation.CheckObservation);
        Assert.True(proof.Succeeded);
        Assert.Equal(invocation.ToolInvocationId.Value.ToString(), proof.Invocation);
        Assert.Equal(workspace.Value.ToString(), proof.Workspace);
        Assert.Equal(baseline.CapturedAt, proof.Baseline);
        Assert.Equal(baseline.GitRevision, proof.Revision);
    }

    /// <summary>The wall-clock admission boundary settles hook time before a provider request.</summary>
    [Fact]
    public static async Task AssessmentHookCanExhaustWallClockBudget()
    {
        const string goal = "Write one sentence";
        var token = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var budget = new HookWallClockBudget();
        var hook = new HeldAssessmentHook { OnEntered = () => budget.HookCompleted = true };
        hook.Release.SetResult();
        var prompts = TestPromptLoader.Instance;
        var assessor = new AnswerModel(goal, "One sentence.");
        var app = new SessionApplication(
            events,
            new WorkModel(),
            budget,
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            correctiveMessages: new CorrectiveMessageFactory(prompts),
            prompts: prompts,
            goalAssessmentProvider: assessor,
            hooks: hook,
            outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("budget"), token);
        await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, goal), token);

        var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, goal), token);
        var error = await Assert.ThrowsAsync<BudgetExceededException>(() => dispatcher.DispatchAsync(new WaitForRunCommand(run), token));
        Assert.Contains("Wall-clock", error.Message, StringComparison.Ordinal);
        Assert.True(budget.HookCompleted);
        Assert.Equal(1, budget.Calls);
        Assert.Equal(0, assessor.Dispatches);
        Assert.Equal(goal, (await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), token)).DisplayText);
    }

    /// <summary>Only a supported terminal response clears the goal and emits its summary.</summary>
    [Theory]
    [InlineData("PartiallyMet", 1)]
    [InlineData("CannotMeet", 1)]
    [InlineData("Met", 999)]
    [InlineData("Met", 1)]
    [InlineData("PartiallyMet", 0)]
    public static async Task TerminalVerdictRequiresSuppliedEvidence(string verdict, int reference)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var output = new ConcurrentQueue<string>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is ModelOutputObserved observed)
            {
                output.Enqueue(observed.Text);
            }

            return Task.CompletedTask;
        });
        var model = new BlockedWorkModel { Verdict = verdict, Reference = reference };
        var prompts = TestPromptLoader.Instance;
        var application = new SessionApplication(events, model, new ExecutionBudget(new BudgetDimensions(200000, 20, TimeSpan.MaxValue)), new SecretOutputSanitizer(), NullLogger<SessionApplication>.Instance, correctiveMessages: new CorrectiveMessageFactory(prompts), prompts: prompts, outputClock: clock);
        var dispatcher = new CommandDispatcher([application]);
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("blocked"), cancellationToken);
        const string goal = Goal + " and explain the available runner";
        await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, goal), cancellationToken);
        var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, goal), cancellationToken);
        var done = dispatcher.DispatchAsync(new WaitForRunCommand(run), cancellationToken);
        if (reference == 0)
        {
            Assert.True(await done.WaitAsync(cancellationToken));
            Assert.Null((await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), cancellationToken)).DisplayText);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => done.WaitAsync(cancellationToken));
            Assert.Equal(goal, (await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), cancellationToken)).DisplayText);
            Assert.DoesNotContain(output, text => text.Contains("Met:", StringComparison.Ordinal));
        }

        Assert.Equal(reference == 0 ? 2 : 6, model.Calls);
    }

    /// <summary>The active composer consumes goal controls locally and never submits them as work.</summary>
    [Fact]
    public static async Task ActiveComposerShowsAndClearsGoalWithoutRestartingWork()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var held = new HeldAssessor();
        var prompts = TestPromptLoader.Instance;
        var app = new SessionApplication(events, new WorkModel(), new ExecutionBudget(new BudgetDimensions(200000, 20, TimeSpan.MaxValue)), new SecretOutputSanitizer(), NullLogger<SessionApplication>.Instance, correctiveMessages: new CorrectiveMessageFactory(prompts), prompts: prompts, goalAssessmentProvider: held, outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var projections = new InMemoryProjectionStore();
        await using var subscription = events.Subscribe(projections.ApplyAsync);
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var observer = events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            return Task.CompletedTask;
        });
        var surface = new GoalSurface(held.Entered.Task);
        try
        {
            await new InteractionCoordinator(new InteractionPresenter(dispatcher, projections), events, surface, timeProvider: clock).RunAsync(cancellationToken: token);
            var run = Assert.Single(observed.OfType<RunCompleted>());
            Assert.Equal(3, surface.SteeringReads);
            Assert.False(Assert.Single(observed.OfType<RunSteeringSubmitted>()).HasText);
            Assert.Equal([true, false], observed.OfType<GoalAssessmentActivity>().Select(item => item.Started));
            Assert.Null((await dispatcher.DispatchAsync(new SessionGoalCommand(run.SessionId, SessionGoalAction.Show), token)).DisplayText);
        }
        finally
        {
            held.Release.TrySetResult();
            if (held.Entered.Task.IsCompleted)
            {
                await held.Left.Task.WaitAsync(token);
            }
        }
    }

    /// <summary>The working answer reaches the presentation surface before the assessment result.</summary>
    [Theory]
    [InlineData("Display 10 + 10", 0)]
    [InlineData("Write a long answer", 2049)]
    public static async Task CompletedAnswerPrecedesVerdictOnSurface(string goal, int length)
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var answer = length == 0 ? "20" : new string('x', length);
        var connectionString = $"Data Source=answer-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(token);
        await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync(token);
        var sanitizer = new SecretOutputSanitizer();
        var archive = new SqliteConversationStore(connectionString, new ArtifactStore(connectionString, Path.GetTempPath(), sanitizer), sanitizer, timeProvider: clock);
        var app = new SessionApplication(
            events,
            new AnswerModel(goal, answer),
            new ExecutionBudget(new BudgetDimensions(200000, 20, TimeSpan.MaxValue)),
            sanitizer,
            NullLogger<SessionApplication>.Instance,
            conversationStore: archive,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var projections = new InMemoryProjectionStore();
        await using var subscription = events.Subscribe(projections.ApplyAsync);
        var finished = new TaskCompletionSource<RunCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var observer = events.Subscribe((item, _) =>
        {
            if (item is RunCompleted completion)
            {
                finished.TrySetResult(completion);
            }

            return Task.CompletedTask;
        });
        var surface = new GoalSurface(goal: goal);
        await new InteractionCoordinator(new InteractionPresenter(dispatcher, projections), events, surface, timeProvider: clock).RunAsync(cancellationToken: token);
        var run = await finished.Task.WaitAsync(token);
        Assert.True(run.Succeeded);
        var transcript = string.Concat(surface.Answers);
        var first = transcript.IndexOf(answer, StringComparison.Ordinal);
        Assert.InRange(first, 0, transcript.IndexOf("Met: Delivered.", StringComparison.Ordinal) - 1);
        Assert.Equal(first, transcript.LastIndexOf(answer, StringComparison.Ordinal));
        Assert.Null((await dispatcher.DispatchAsync(new SessionGoalCommand(run.SessionId, SessionGoalAction.Show), token)).DisplayText);
    }

    /// <summary>A written assessment of a review file can satisfy the session goal.</summary>
    [Fact]
    public static async Task WrittenAssessmentClearsGoal()
    {
        const string goal = @"Assess defects in C:\source\Sample Project\.inbox\review.md - some may already be addressed.";
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var model = new AnswerModel(goal, "Two defects are addressed. The startup delay remains open.");
        var prompts = TestPromptLoader.Instance;
        var app = new SessionApplication(
            events,
            model,
            new ExecutionBudget(new BudgetDimensions(200000, 20, TimeSpan.MaxValue)),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            correctiveMessages: new CorrectiveMessageFactory(prompts),
            prompts: prompts,
            outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("assessment"), cancellationToken);
        await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, goal), cancellationToken);

        var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, goal), cancellationToken);

        Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(run), cancellationToken));
        Assert.Equal(2, model.Dispatches);
        Assert.Null((await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), cancellationToken)).DisplayText);
    }

    /// <summary>A rejected assessment explains the failed turn and how to resume in the output.</summary>
    [Fact]
    public static async Task RejectedAssessmentExplainsWhyGoalRemainsActive()
    {
        const string goal = Goal + " and explain the available runner";
        const string reason = "The cited evidence does not confirm the requirement “" + Goal + "”.";
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var model = new BlockedWorkModel { Verdict = "Met", Reference = 1 };
        var prompts = TestPromptLoader.Instance;
        var app = new SessionApplication(
            events,
            model,
            new ExecutionBudget(new BudgetDimensions(200000, 20, TimeSpan.MaxValue)),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            correctiveMessages: new CorrectiveMessageFactory(prompts),
            prompts: prompts,
            outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var projections = new InMemoryProjectionStore();
        await using var subscription = events.Subscribe(projections.ApplyAsync);
        var finished = new TaskCompletionSource<RunCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var observer = events.Subscribe((item, _) =>
        {
            if (item is RunCompleted completion)
            {
                finished.TrySetResult(completion);
            }

            return Task.CompletedTask;
        });
        var surface = new GoalSurface(goal: goal);

        await new InteractionCoordinator(new InteractionPresenter(dispatcher, projections), events, surface, timeProvider: clock)
            .RunAsync(cancellationToken: cancellationToken);

        var run = await finished.Task.WaitAsync(cancellationToken);
        Assert.False(run.Succeeded);
        var notice = Assert.Single(surface.Answers, text => text.Contains("Goal assessment failed.", StringComparison.Ordinal));
        Assert.Contains(reason, notice, StringComparison.Ordinal);
        Assert.Contains("The goal remains active. Submit continue to resume.", notice, StringComparison.Ordinal);
        Assert.Contains(surface.Answers, text => text.Contains("Retrying the goal assessment. " + reason, StringComparison.Ordinal));
        Assert.Equal(new PresentationNotification("Goal assessment failed. See MAIN.", PresentationTextRole.Error), Assert.Single(surface.Notifications));
        Assert.Equal(goal, (await dispatcher.DispatchAsync(new SessionGoalCommand(run.SessionId, SessionGoalAction.Show), cancellationToken)).DisplayText);
        Assert.Equal(6, model.Calls);
    }

    /// <summary>Continues through a rejected verdict and honors default and configured continuation limits.</summary>
    [Theory]
    [InlineData(true, 0, 3, null)]
    [InlineData(false, 10, 22, null)]
    [InlineData(false, 11, 22, null)]
    [InlineData(false, 11, 6, 2)]
    [InlineData(false, 11, 2, 0)]
    public static async Task GoalWorksUntilCompletionOrContinuationLimit(bool omitScopeInitially, int continuations, int expectedRequests, int? configuredLimit)
    {
        const string request = "Assess the defects in the review.";
        const string goal = request + " Ignore feedback on missing tests or manual verification for now.";
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var output = new ConcurrentQueue<string>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is ModelOutputObserved observed)
            {
                output.Enqueue(observed.Text);
            }

            return Task.CompletedTask;
        });
        var model = new AnswerModel(goal, "The remaining defect is in workspace startup.", omitScopeInitially ? request : null, continuations);
        var prompts = TestPromptLoader.Instance;
        var app = new SessionApplication(
            events,
            model,
            new ExecutionBudget(new BudgetDimensions(200000, 100, TimeSpan.MaxValue)),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            limits: configuredLimit is { } value ? new ExecutionLimits { MaxGoalContinuations = value } : null,
            correctiveMessages: new CorrectiveMessageFactory(prompts),
            prompts: prompts,
            outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("assessment"), cancellationToken);
        await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, goal), cancellationToken);

        var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, goal), cancellationToken);

        Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(run), cancellationToken));
        Assert.Equal(expectedRequests, model.Dispatches);
        var limit = configuredLimit ?? 10;
        var paused = continuations > limit;
        Assert.Equal(paused ? goal : null, (await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), cancellationToken)).DisplayText);
        if (paused)
        {
            Assert.Single(output, text => text.Contains($"the limit of {limit} automatic continuations was reached", StringComparison.Ordinal));
        }
    }

    private sealed class AnswerModel(string goal, string answer, string? firstAssessmentClause = null, int continuations = 0) : IModelProvider
    {
        private int _assessments;

        internal int Dispatches { get; private set; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            Dispatches++;
            if (request.Messages.All(message => message.SectionId != "goal-assessment"))
            {
                if (_assessments > 0 && _assessments <= continuations)
                {
                    var goalContext = request.Messages.Last(message => message.SectionId == "session-goal");
                    Assert.Contains("Complete the defect assessment.", goalContext.GetModelVisibleContent(), StringComparison.Ordinal);
                }

                yield return new ModelChunk { Text = answer, FinishReason = ModelFinishReason.Stop };
                yield break;
            }

            _assessments++;
            Assert.Empty(request.Tools);
            using var input = JsonDocument.Parse(request.Input);
            var delivered = input.RootElement.GetProperty("evidence").EnumerateArray().Last(item => item.GetProperty("Kind").GetString() == "answer");
            Assert.Equal(answer, delivered.GetProperty("Text").GetString());
            Assert.False(delivered.GetProperty("Omitted").GetBoolean());
            if (firstAssessmentClause is not null && _assessments == 2)
            {
                Assert.Equal("The assessment omitted a goal requirement.", input.RootElement.GetProperty("correction").GetString());
            }

            if (_assessments <= continuations)
            {
                var continuation = _assessments == 1
                    ? """{"version":2,"verdict":"Continue","summary":"More analysis is needed.","reason":"The review is incomplete.","nextStep":"Complete the defect assessment.","evidence":[1],"requirements":[{"clause":"Assess the defects in the review.","kind":"action","scope":"","tool":"","evidence":[]}]}"""
                    : """{"version":2,"verdict":"Continue","nextStep":"Complete the defect assessment."}""";
                yield return new ModelChunk { Text = continuation, FinishReason = ModelFinishReason.Stop };
                yield break;
            }

            var evidenceId = delivered.GetProperty("Id").GetInt32();
            var clause = _assessments == 1 ? firstAssessmentClause ?? goal : goal;
            var text = JsonSerializer.Serialize(new
            {
                version = 2,
                verdict = "Met",
                reason = "The answer was displayed.",
                summary = "Delivered.",
                nextStep = string.Empty,
                evidence = new[] { evidenceId },
                requirements = new[] { new { clause, kind = "text", scope = string.Empty, tool = string.Empty, evidence = new[] { evidenceId } } },
            });
            yield return new ModelChunk { Text = text, FinishReason = ModelFinishReason.Stop };
        }
    }

    private sealed class GoalSurface(Task? assessmentStarted = null, bool resume = false, string goal = Goal) : IInteractionSurface
    {
        private readonly Queue<string> _inputs = new(resume ? [$"/goal set \"{goal}\"", "continue", "/quit"] : [$"/goal set \"{goal}\"", "/quit"]);

        private bool _pauseIssued;

        public InteractionSurfaceCapabilities Capabilities { get; } = new(SupportsActiveRunInput: assessmentStarted is not null);

        internal int SteeringReads { get; private set; }

        public IActiveRunInputLease? BeginActiveRunInput(TimeProvider timeProvider)
        {
            if (_pauseIssued || assessmentStarted is null)
            {
                return null;
            }

            _pauseIssued = true;
            return new SteeringLease(assessmentStarted, resume ? ActiveRunInputSignal.CancellationRequested : ActiveRunInputSignal.SteeringRequested);
        }

        public Task<InteractionInput> ReadComposerAsync(ComposerRequest request, CancellationToken cancellationToken = default)
        {
            var text = request.Purpose != ComposerPurpose.Steering ? _inputs.Dequeue() : ++SteeringReads switch
            {
                1 => "/goal",
                2 => "/goal cancel",
                3 => null,
                _ => throw new InvalidOperationException("Unexpected fourth steering read."),
            };
            return Task.FromResult(new InteractionInput(text is not null, text ?? string.Empty, cancellationToken));
        }

        public Task<InteractionSelectionResult> SelectAsync(InteractionSelectionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        internal ConcurrentQueue<string> Answers { get; } = new();

        internal ConcurrentQueue<PresentationNotification> Notifications { get; } = new();

        public Task PresentAsync(PresentationBatch batch, CancellationToken cancellationToken = default)
        {
            foreach (var item in batch.Items)
            {
                if (item is PresentationMarkdownItem markdown)
                {
                    Answers.Enqueue(markdown.RawSource);
                }
                else if (item is PresentationSourceItem source)
                {
                    Answers.Enqueue(source.RawSource);
                }
                else if (item is PresentationTextItem text)
                {
                    Answers.Enqueue(string.Concat(text.Segments.Select(segment => segment.Text)));
                    if (text.Notification is { } notification)
                    {
                        Notifications.Enqueue(notification);
                    }
                }
            }

            return Task.CompletedTask;
        }

        public Task PresentSessionStatusAsync(SessionStatusSnapshot status, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PresentActivityUntilAsync(InteractionActivity activity, Task operation, CancellationToken cancellationToken = default) => operation.WaitAsync(cancellationToken);
    }

    private sealed class SteeringLease(Task assessmentStarted, ActiveRunInputSignal signal) : IActiveRunInputLease
    {
        private readonly CancellationTokenSource _disposed = new();
        private readonly TaskCompletionSource _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _requested;

        public async Task<ActiveRunInputSignal> ReadAsync(CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_disposed.Token, cancellationToken);
            var wait = _requested ? _pending.Task : assessmentStarted;
            _requested = true;
            await wait.WaitAsync(linked.Token);
            return signal;
        }

        public async ValueTask DisposeAsync()
        {
            await _disposed.CancelAsync();
            _disposed.Dispose();
        }
    }

    private sealed class BlockedWorkModel : IModelProvider
    {
        internal required string Verdict { get; init; }

        internal required int Reference { get; init; }

        internal int Calls { get; private set; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            Assert.InRange(++Calls, 1, 6);
            var assessment = request.Messages.Any(message => message.SectionId == "goal-assessment");
            var text = assessment
                ? JsonSerializer.Serialize(new { version = 2, verdict = Verdict, summary = "Work reviewed.", reason = "No permitted test runner.", evidence = new[] { Reference }, nextStep = string.Empty, requirements = new[] { new { clause = Goal, kind = Verdict == "Met" ? "text" : "action", tool = Verdict == "Met" ? string.Empty : "parser_test", scope = string.Empty, evidence = Verdict == "Met" ? new[] { Reference } : [] }, new { clause = "explain the available runner", kind = "text", tool = string.Empty, scope = string.Empty, evidence = Verdict == "PartiallyMet" ? new[] { 1 } : [] } } })
                : "No permitted test runner.";
            yield return new ModelChunk { Text = text, FinishReason = ModelFinishReason.Stop };
        }
    }

    private sealed class HeldAssessmentHook : IHookCoordinator
    {
        public IReadOnlyList<HookHandlerDescriptor> Handlers => [];

        internal Action? OnEntered { get; init; }

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HookHandlerDescriptor? GetHandler(HookHandlerId handlerId) => null;

        public bool SetEnabled(HookHandlerId handlerId, bool enabled) => false;

        public async Task<HookBoundaryDecision> InvokeAsync(HookPoint point, SessionId sessionId, RunId? runId, string? repositoryIdentity, Guid operationId, int generation, IReadOnlyDictionary<string, string>? payload = null, IReadOnlyList<ExecutionArtifactReference>? artifacts = null, IReadOnlyList<string>? callChain = null, RepositoryStorageBinding? storageBinding = null, CancellationToken cancellationToken = default)
        {
            if (point == HookPoint.BeforeModelRequest && payload?.GetValueOrDefault("stage") == "goal-assessment" && Entered.TrySetResult())
            {
                await Release.Task.WaitAsync(TestContext.Current.CancellationToken);
                OnEntered?.Invoke();
            }

            return new(HookDecisionKind.Continue, [], []);
        }

        public Task<HookBoundaryDecision> InvokeHandlerAsync(HookHandlerId handlerId, HookPoint point, SessionId sessionId, RunId? runId, string? repositoryIdentity, Guid operationId, int generation, IReadOnlyDictionary<string, string>? payload = null, IReadOnlyList<ExecutionArtifactReference>? artifacts = null, IReadOnlyList<string>? callChain = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class HeldAssessor : IModelProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Left { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var packet = JsonDocument.Parse(request.Input);
            if (packet.RootElement.GetProperty("goal").GetString() == "Write replacement text")
            {
                yield return new ModelChunk { Text = """{"version":2,"verdict":"Met","summary":"Replacement completed","reason":"delivered","evidence":[1],"nextStep":"","requirements":[{"clause":"Write replacement text","kind":"text","tool":"","scope":"","evidence":[1]}]}""", FinishReason = ModelFinishReason.Stop };
                yield break;
            }

            Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(TestContext.Current.CancellationToken);
                yield return new ModelChunk { Text = """{"version":2,"verdict":"Met","summary":"stale verdict","reason":"delivered","evidence":[1],"nextStep":"","requirements":[{"clause":"Write one sentence","kind":"text","tool":"","scope":"","evidence":[1]}]}""", FinishReason = ModelFinishReason.Stop };
            }
            finally
            {
                Left.TrySetResult();
            }
        }
    }

    private sealed class ResumeResolver(ModelProfile profile, Func<bool> prohibit) : IModelResolver
    {
        private readonly ModelResolver _allowed = new(new ConfiguredModelCatalog([profile]), new InMemoryModelPreferenceSnapshotProvider());
        private readonly ModelResolver _prohibited = new(new ConfiguredModelCatalog([profile with { SensitiveDataPolicy = ModelSensitiveDataPolicy.Prohibited }]), new InMemoryModelPreferenceSnapshotProvider());

        public int MaximumInputTokenBudget => _allowed.MaximumInputTokenBudget;

        internal bool DeniedSensitive { get; private set; }

        public ModelResolution Resolve(WorkloadClass workloadClass, ModelCapabilitySet requiredCapabilities, ModelSelectionConstraints constraints, ModelProfileId? defaultModelProfileId = null)
        {
            var useProhibited = prohibit();
            DeniedSensitive |= useProhibited && constraints.ContainsSensitiveData;
            return (useProhibited ? _prohibited : _allowed).Resolve(workloadClass, requiredCapabilities, constraints, defaultModelProfileId);
        }
    }

    private sealed class WorkModel(TaskCompletionSource? receiptReady = null) : IModelProvider
    {
        private int _goalBytesBeforeTool;

        internal ConcurrentQueue<RunId> Dispatches { get; } = new();

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            Dispatches.Enqueue(request.RunId);
            var goalBytes = request.Messages.Where(message => message.SectionId == "session-goal")
                .Sum(message => System.Text.Encoding.UTF8.GetByteCount(message.GetModelVisibleContent()));
            if (request.Messages.Any(message => message.Role == ModelMessageRole.Tool && message.ToolName == "parser_test"))
            {
                if (request.TransientState?.HasResponses == true)
                {
                    request.TransientState.ValidateHistory(request);
                    Assert.True(_goalBytesBeforeTool > 0);
                    Assert.Equal(_goalBytesBeforeTool, goalBytes);
                }

                if (receiptReady?.TrySetResult() == true)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }

                yield return new ModelChunk { Text = "See the test result.", FinishReason = ModelFinishReason.Stop };
            }
            else if (receiptReady?.Task.IsCompleted == true)
            {
                var goal = Assert.Single(request.Messages, item => item.SectionId == "session-goal");
                Assert.True(request.ContainsSensitiveData);
                Assert.True(request.SelectionConstraints.ContainsSensitiveData);
                Assert.Equal(ModelMessageRole.User, goal.Role);
                Assert.Contains("parser_test", goal.GetModelVisibleContent(), StringComparison.Ordinal);
                yield return new ModelChunk { Output = new ToolRequestModelOutput("parser_test", "{}"), FinishReason = ModelFinishReason.ToolCalls };
            }
            else if (request.Messages.Any(message => message.Role == ModelMessageRole.Assistant && message.GetModelVisibleContent().Contains(Promise, StringComparison.Ordinal)))
            {
                _goalBytesBeforeTool = goalBytes;
                var binding = new ModelReplayBinding
                {
                    ProviderId = "fixture", ModelId = "work", ProfileId = request.ResolvedProfileId!.Value,
                    RunId = request.RunId, ModelRound = request.ToolContinuationRound, HistoryRewriteGeneration = request.HistoryRewriteGeneration,
                    CredentialGeneration = "fixture", ToolInventoryDigest = "tools", InstructionDigest = "instructions", NormalizedRoundDigest = "response",
                };
                yield return new ModelChunk { ResponseEnvelope = new ModelResponseReplayEnvelope(binding, [1], ["native-call"], 1) };
                yield return new ModelChunk { Output = new ToolRequestModelOutput("parser_test", "{}"), FinishReason = ModelFinishReason.ToolCalls };
            }
            else
            {
                yield return new ModelChunk { Text = Promise, FinishReason = ModelFinishReason.Stop };
            }
        }
    }

    private sealed class AssessmentModel : IModelProvider
    {
        internal required ModelProfileId Profile { get; init; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            Assert.Equal(Profile, request.ResolvedProfileId);
            Assert.Empty(request.Tools);
            using var input = JsonDocument.Parse(request.Input);
            Assert.Equal(Goal, input.RootElement.GetProperty("goal").GetString());
            var entries = input.RootElement.GetProperty("evidence").EnumerateArray().ToArray();
            var promise = Assert.Single(entries, entry => entry.GetProperty("Text").GetString() == Promise);
            var results = entries.Where(entry => entry.GetProperty("Kind").GetString()!.StartsWith("tool:", StringComparison.Ordinal)).ToArray();
            var met = results.Length != 0;
            var support = met ? results[^1] : promise;
            if (met)
            {
                Assert.Contains(Receipt, support.GetProperty("Text").GetString(), StringComparison.Ordinal);
            }

            var text = JsonSerializer.Serialize(new { version = 2, requirements = new[] { new { clause = Goal, kind = "action", tool = "parser_test", scope = string.Empty, evidence = met ? new[] { support.GetProperty("Id").GetInt32() } : [] } }, verdict = met ? "Met" : "Continue", summary = met ? Receipt : "Only a promise", reason = met ? "Confirmed by tool." : "No receipt.", evidence = new[] { support.GetProperty("Id").GetInt32() }, nextStep = met ? string.Empty : "Run the parser test." });
            yield return new ModelChunk { Text = text, FinishReason = ModelFinishReason.Stop };
        }
    }

    private sealed class BlockTestPolicy : IPolicyEngine
    {
        internal int Denials { get; private set; }

        public ToolPolicyDecision Evaluate(ITool tool, object input, ToolInvocationContext context)
        {
            if (tool.Definition.Id == "parser_test")
            {
                return new(true, ApprovalLevel.None, "The alternative test runner is permitted.");
            }

            Assert.Equal("test_run_targeted", tool.Definition.Id);
            Denials++;
            return new(false, ApprovalLevel.None, "Repository policy disables tests.");
        }
    }

    private sealed class PolicyDenialModel(bool terminal) : IModelProvider
    {
        internal int WorkCalls { get; private set; }

        internal int Assessments { get; private set; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (request.Messages.All(message => message.SectionId != "goal-assessment"))
            {
                yield return ++WorkCalls switch
                {
                    1 => new ModelChunk { Text = "A completed summary.", FinishReason = ModelFinishReason.Stop },
                    2 => new ModelChunk { Output = new ToolRequestModelOutput("test_run_targeted", "{}"), FinishReason = ModelFinishReason.ToolCalls },
                    3 => new ModelChunk { Output = new ToolRequestModelOutput("parser_test", "{}"), FinishReason = ModelFinishReason.ToolCalls },
                    4 => new ModelChunk { Text = "The permitted runner passed.", FinishReason = ModelFinishReason.Stop },
                    _ => throw new InvalidOperationException("Unexpected goal continuation."),
                };
                yield break;
            }

            Assessments++;
            Assert.Empty(request.Tools);
            using var packet = JsonDocument.Parse(request.Input);
            var evidence = packet.RootElement.GetProperty("evidence").EnumerateArray().ToArray();
            var answer = Assert.Single(evidence, entry => entry.GetProperty("Text").GetString() == "A completed summary.").GetProperty("Id").GetInt32();
            var blocker = evidence.SingleOrDefault(entry => entry.GetProperty("Kind").GetString() == "tool:test_run_targeted:PolicyDenied");
            var blocked = Assessments >= 2;
            Assert.Equal(blocked, blocker.ValueKind == JsonValueKind.Object);
            var partial = blocked && terminal;
            var met = Assessments == 3;
            var passed = evidence.SingleOrDefault(entry => entry.GetProperty("Kind").GetString()?.StartsWith("tool:parser_test:", StringComparison.Ordinal) == true);
            Assert.Equal(met, passed.ValueKind == JsonValueKind.Object);
            int[] support = met ? [passed.GetProperty("Id").GetInt32()] : [];
            int[] citedEvidence = met ? [answer, .. support] : blocked ? [answer, blocker.GetProperty("Id").GetInt32()] : [answer];
            var response = JsonSerializer.Serialize(new
            {
                version = 2,
                verdict = met ? "Met" : partial ? "PartiallyMet" : "Continue",
                summary = "Summary delivered.",
                reason = met ? "The alternative test runner passed." : blocked ? "Repository policy disables the first runner." : "Tests still needed.",
                evidence = citedEvidence,
                nextStep = met || partial ? string.Empty : blocked ? "Use the permitted test runner." : "Run the tests.",
                requirements = new[]
                {
                    new { clause = "Write a summary", kind = "text", tool = string.Empty, scope = string.Empty, evidence = new[] { answer } },
                    new { clause = "run the tests", kind = "action", tool = met ? "parser_test" : "test_run_targeted", scope = string.Empty, evidence = support },
                },
            });
            yield return new ModelChunk { Text = response, FinishReason = ModelFinishReason.Stop };
        }
    }

    private sealed class HookWallClockBudget : IBudget
    {
        private readonly ExecutionBudget _budget = new(new BudgetDimensions(200000, 20, TimeSpan.MaxValue));

        internal bool HookCompleted { get; set; }

        internal int Calls => _budget.Check(new BudgetDimensions(0, 0, TimeSpan.Zero)).Used.Calls;

        public BudgetStatus Check(BudgetDimensions delta) => _budget.Check(delta);

        public BudgetStatus Accrue(BudgetDimensions delta)
        {
            var status = _budget.Accrue(delta);
            // Deterministically model exhaustion on settlement rather than depending on runner speed.
            return HookCompleted && delta is { Calls: 0, Tokens: 0 }
                ? new BudgetStatus(true, status.Used, "Wall-clock budget exhausted.") { ExhaustedDimensions = BudgetExhaustionDimension.WallClock }
                : status;
        }
    }

    private sealed class ParserTestTool : Tool<DateTimeInput, TargetedTestResult>
    {
        internal ParserTestTool(string toolId = "parser_test")
        {
            Definition = new DateTimeTool(TestPromptLoader.Instance).Definition with { Id = toolId, DisplayName = "Parser test" };
        }

        public override ToolDefinition Definition { get; }

        internal bool TimesOut { get; init; }

        internal bool ObserveNativeCheck { get; init; }

        public override async Task<ToolExecution<TargetedTestResult>> ExecuteAsync(DateTimeInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TimesOut)
            {
                throw new TimeoutException("The runner did not confirm the result.");
            }

            var result = ObserveNativeCheck
                ? await ConversationReceiptChecks.ObserveAsync(context, Definition.Id, "Parser.csproj", "ParserTests.Parse", _ => Task.FromResult(TestResult()), cancellationToken)
                : TestResult();
            return new ToolExecution<TargetedTestResult>(result, [], false);
        }

        internal static TargetedTestResult TestResult()
        {
            var test = new DiscoveredTest(new("parser"), "ParserTests.Parse", "Parser.csproj", null, "ParserTests", "Parse", new Dictionary<string, string>());
            return new TargetedTestResult(test, "ParserTests.Parse", ValidationAuthority.Exploratory, TestOutcome.Passed, 1, 0, 0, Receipt, TimeSpan.Zero, false, false, []);
        }

        protected override void ValidateInput(DateTimeInput input)
        {
        }
    }
}
