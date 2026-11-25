namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
    private const string Goal = "Run ParserTests.Parse";
    private const string Promise = "I will run the test next.";
    private const string Receipt = "ParserTests passed";
    private static readonly bool[] _twoAssessmentActivityPairs = [true, false, true, false];

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
            hooks: hook,
            correctiveMessages: new CorrectiveMessageFactory(prompts),
            prompts: prompts,
            goalAssessmentProvider: held,
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
                Assert.Single(observed.OfType<ModelOutputObserved>(), item => item.Text.Contains("previous continuation check is still closing", StringComparison.Ordinal));
            }
            else
            {
                await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, "Write replacement text"), cancellationToken);
                Assert.True(await dispatcher.DispatchAsync(new CancelRunCommand(session, run), cancellationToken));
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => done.WaitAsync(cancellationToken));
                hook?.Release.TrySetResult();
                held.Release.TrySetResult();
                await app.StopRunsAsync(cancellationToken);
                Assert.Equal("Write replacement text", (await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), cancellationToken)).DisplayText);

                var replacement = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, "Write replacement text"), cancellationToken);
                Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(replacement), cancellationToken));
            }
        }
        finally
        {
            hook?.Release.TrySetResult();
            held.Release.TrySetResult();
            await app.StopRunsAsync(cancellationToken);
        }

        Assert.Equal(dismissWhileDraining ? initialGoal : null, (await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), cancellationToken)).DisplayText);
        Assert.Equal(dismissWhileDraining || holdHook ? [true, false] : _twoAssessmentActivityPairs, observed.OfType<GoalAssessmentActivity>().Select(item => item.Started));
        if (holdHook)
        {
            Assert.False(held.Entered.Task.IsCompleted);
        }
    }

    /// <summary>Stop wins during assessment settlement, but not after the terminal goal state commits.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task StopAndTerminalGoalCommitRespectAdmissionOrder(bool commitFirst)
    {
        const string goal = "Produce a summary of the parser";
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var hook = commitFirst ? null : new HeldAssessmentHook { Point = HookPoint.AfterModelRequest };
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new ConcurrentQueue<string>();
        var terminalEvents = new ConcurrentQueue<RunCompleted>();
        await using var subscription = events.Subscribe(async (item, _) =>
        {
            if (item is ModelOutputObserved observed)
            {
                output.Enqueue(observed.Text);
            }

            if (item is RunCompleted terminal)
            {
                terminalEvents.Enqueue(terminal);
                if (commitFirst)
                {
                    completed.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                }
            }
        });
        var prompts = TestPromptLoader.Instance;
        var app = new SessionApplication(
            events,
            new AnswerModel(goal, "The parser recognizes expressions."),
            new ExecutionBudget(new BudgetDimensions(200000, 20, TimeSpan.MaxValue)),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            hooks: hook,
            correctiveMessages: new CorrectiveMessageFactory(prompts),
            prompts: prompts,
            outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("terminal ordering"), cancellationToken);
        await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, goal), cancellationToken);
        var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, goal), cancellationToken);
        var done = dispatcher.DispatchAsync(new WaitForRunCommand(run), cancellationToken);
        try
        {
            var barrier = hook?.Entered.Task ?? completed.Task;
            Assert.Same(barrier, await Task.WhenAny(done, barrier).WaitAsync(cancellationToken));
            Assert.Equal(commitFirst ? null : goal, (await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), cancellationToken)).DisplayText);
            Assert.Equal(!commitFirst, await dispatcher.DispatchAsync(new CancelRunCommand(session, run), cancellationToken));
            if (commitFirst)
            {
                Assert.False(done.IsCompleted);
            }
            else
            {
                Assert.True(hook?.ModelSucceeded);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => done.WaitAsync(cancellationToken));
            }
        }
        finally
        {
            hook?.Release.TrySetResult();
            release.TrySetResult();
            await app.StopRunsAsync(cancellationToken);
        }

        if (commitFirst)
        {
            Assert.True(await done.WaitAsync(cancellationToken));
            Assert.Single(output, text => text.Contains("The parser recognizes expressions.", StringComparison.Ordinal));
        }

        Assert.Equal(commitFirst, Assert.Single(terminalEvents, item => item.RunId == run).Succeeded);
        Assert.Equal(commitFirst ? null : goal, (await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), cancellationToken)).DisplayText);
    }

    /// <summary>Retiring a goal lets the foreground exit an obsolete provider or settlement before the old operation drains.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task RetiredGoalReleasesForeground(bool replaceDuringFailedHook)
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var held = new HeldAssessor();
        var hook = replaceDuringFailedHook ? new HeldAssessmentHook { Point = HookPoint.AfterModelRequest } : null;
        IModelProvider status = replaceDuringFailedHook
            ? new AnswerModel(Goal, Promise) { AssessmentFailure = new IOException("Old provider failed.") }
            : held;
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
            new WorkModel { FinishAfterFirstRound = true },
            new ExecutionBudget(new BudgetDimensions(200000, 20, TimeSpan.MaxValue)),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            hooks: hook,
            correctiveMessages: new CorrectiveMessageFactory(prompts),
            prompts: prompts,
            goalAssessmentProvider: status,
            outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("retired status"), token);
        await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, Goal), token);
        var first = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, Goal), token);
        var firstDone = dispatcher.DispatchAsync(new WaitForRunCommand(first), token);
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(firstDone, entered.Task).WaitAsync(token));
            if (replaceDuringFailedHook)
            {
                await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, "Replacement goal"), token);
            }
            else
            {
                var second = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, Goal), token);
                Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(second), token));
            }

            Assert.True(await firstDone.WaitAsync(token));
            Assert.Null((await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), token)).DisplayText);
            Assert.DoesNotContain(observed.OfType<DiagnosticObserved>(), item => item.Code == "GoalAssessmentFailed");
            Assert.Equal(replaceDuringFailedHook ? 1 : 2, observed.OfType<RunCompleted>().Count());
        }
        finally
        {
            hook?.Release.TrySetResult();
            held.Release.TrySetResult();
            await app.StopRunsAsync(token);
        }
    }

    /// <summary>A failed provider attempt must settle its after-model hook before publishing run completion.</summary>
    [Fact]
    public static async Task FailedAssessmentSettlesBeforeRunCompletion()
    {
        const string goal = "Write one sentence";
        var cancellationToken = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var order = new ConcurrentQueue<string>();
        var hook = new HeldAssessmentHook { Point = HookPoint.AfterModelRequest, OnEntered = () => order.Enqueue("hook completed") };
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is RunCompleted)
            {
                order.Enqueue("run completed");
            }

            return Task.CompletedTask;
        });
        var prompts = TestPromptLoader.Instance;
        var assessor = new AnswerModel(goal, "One sentence.") { AssessmentFailure = new IOException("Assessment transport failed.") };
        var app = new SessionApplication(
            events,
            new WorkModel(),
            new ExecutionBudget(new BudgetDimensions(200000, 20, TimeSpan.MaxValue)),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            hooks: hook,
            correctiveMessages: new CorrectiveMessageFactory(prompts),
            prompts: prompts,
            goalAssessmentProvider: assessor,
            outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("failed assessment"), cancellationToken);
        await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, goal), cancellationToken);

        var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, goal), cancellationToken);
        var done = dispatcher.DispatchAsync(new WaitForRunCommand(run), cancellationToken);
        try
        {
            Assert.Same(hook.Entered.Task, await Task.WhenAny(done, hook.Entered.Task).WaitAsync(cancellationToken));
            Assert.False(done.IsCompleted);
            Assert.False(hook.ModelSucceeded);
            hook.Release.TrySetResult();
            Assert.Same(assessor.AssessmentFailure, await Assert.ThrowsAsync<IOException>(() => done.WaitAsync(cancellationToken)));
        }
        finally
        {
            hook.Release.TrySetResult();
            await app.StopRunsAsync(cancellationToken);
        }

        Assert.Equal(["hook completed", "run completed"], order);
        Assert.Equal(1, assessor.Dispatches);
        Assert.Equal(goal, (await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), cancellationToken)).DisplayText);
    }

    /// <summary>The receipt counts all check attempts and retains the most consequential evidence up to its limit.</summary>
    [Fact]
    public static async Task NativeCheckReceiptKeepsFailuresWhenFull()
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
        var (retained, total) = checks.Snapshot(baseline);
        Assert.Equal(9, total);
        Assert.Equal(8, retained.Length);
        Assert.All(retained, check =>
        {
            Assert.False(check.Succeeded);
            Assert.Equal(workspace.Value.ToString(), check.Workspace);
            Assert.Equal(baseline.CapturedAt, check.Baseline);
            Assert.Equal(baseline.GitRevision, check.Revision);
        });
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
            hooks: hook,
            correctiveMessages: new CorrectiveMessageFactory(prompts),
            prompts: prompts,
            goalAssessmentProvider: assessor,
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

    /// <summary>The active composer consumes goal controls locally and never submits them as work.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Write replacement text")]
    public static async Task ActiveComposerHandlesGoalChangesWithoutRestartingWork(string? replacement)
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var held = new HeldAssessor();
        var prompts = TestPromptLoader.Instance;
        var model = new WorkModel { FinishAfterFirstRound = true };
        var app = new SessionApplication(events, model, new ExecutionBudget(new BudgetDimensions(200000, 20, TimeSpan.MaxValue)), new SecretOutputSanitizer(), NullLogger<SessionApplication>.Instance, correctiveMessages: new CorrectiveMessageFactory(prompts), prompts: prompts, goalAssessmentProvider: held, outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var projections = new InMemoryProjectionStore();
        await using var subscription = events.Subscribe(projections.ApplyAsync);
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var observer = events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            return Task.CompletedTask;
        });
        var surface = new GoalSurface(held.Entered.Task) { SteeringReplacement = replacement };
        try
        {
            await new InteractionCoordinator(new InteractionPresenter(dispatcher, projections), events, surface, timeProvider: clock).RunAsync(cancellationToken: token);
            var run = Assert.Single(observed.OfType<RunCompleted>());
            Assert.True(run.Succeeded);
            Assert.Equal(replacement is null ? 3 : 1, surface.SteeringReads);
            Assert.False(Assert.Single(observed.OfType<RunSteeringSubmitted>()).HasText);
            var requests = model.Requests.ToArray();
            Assert.Equal(replacement is null ? 1 : 2, requests.Length);
            Assert.Contains(requests[0].Messages, message => message.SectionId == "session-goal" && message.GetModelVisibleContent().Contains(Goal, StringComparison.Ordinal));
            Assert.DoesNotContain(requests[0].Messages, message => message.SectionId != "session-goal" && message.GetModelVisibleContent().Contains(Goal, StringComparison.Ordinal));
            if (replacement is not null)
            {
                var updated = requests[1];
                Assert.Contains(updated.Messages, message => message.SectionId == "session-goal" && message.GetModelVisibleContent().Contains(replacement, StringComparison.Ordinal));
                Assert.DoesNotContain(Goal, updated.Input, StringComparison.Ordinal);
                Assert.DoesNotContain(updated.Messages, message => message.GetModelVisibleContent().Contains(Goal, StringComparison.Ordinal));
            }

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

    /// <summary>A late end leaves the replacement indicator visible until its own assessment ends.</summary>
    [Fact]
    public static async Task CoordinatorEndsOnlyTheMatchingAssessmentAttempt()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var held = new HeldAssessor();
        var prompts = TestPromptLoader.Instance;
        var app = new SessionApplication(events, new WorkModel(), UnboundedBudget.Instance, new SecretOutputSanitizer(), NullLogger<SessionApplication>.Instance, correctiveMessages: new CorrectiveMessageFactory(prompts), prompts: prompts, goalAssessmentProvider: held, outputClock: clock);
        var projections = new InMemoryProjectionStore();
        await using var project = events.Subscribe(projections.ApplyAsync);
        var first = new TaskCompletionSource<GoalAssessmentActivity>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var observe = events.Subscribe((item, _) =>
        {
            if (item is GoalAssessmentActivity { Started: true } start)
            {
                first.TrySetResult(start);
            }

            return Task.CompletedTask;
        });
        var presented = System.Threading.Channels.Channel.CreateUnbounded<(Guid Attempt, Task Operation)>();
        var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
        var surface = new GoalSurface(retainActivity: true)
        {
            ActivityPresented = (attempt, operation) => presented.Writer.TryWrite((attempt, operation)),
            ActivityRemoved = attempt => removed.Writer.TryWrite(attempt),
            SteeringMarkerPresented = () => marker.TrySetResult(),
        };
        var coordinator = new InteractionCoordinator(new InteractionPresenter(new CommandDispatcher([app]), projections), events, surface, timeProvider: clock);
        var running = coordinator.RunAsync(cancellationToken: lifetime.Token);
        try
        {
            var original = await first.Task.WaitAsync(ct);
            var a = await presented.Reader.ReadAsync(ct);
            Assert.Equal(original.AttemptId, a.Attempt);
            var next = original with { AttemptId = Guid.NewGuid() };
            await events.PublishAsync(next, ct);
            var b = await presented.Reader.ReadAsync(ct);
            Assert.Equal(next.AttemptId, b.Attempt);
            Assert.Equal(a.Attempt, await removed.Reader.ReadAsync(ct));

            await events.PublishAsync(original with { Started = false }, ct);
            await events.PublishAsync(new RunSteeringSubmitted(next.SessionId, clock.GetUtcNow(), next.RunId, SteeringPauseId.New(), null, false), ct);
            await marker.Task.WaitAsync(ct);
            Assert.False(b.Operation.IsCompleted);

            await events.PublishAsync(next with { Started = false }, ct);
            Assert.Equal(b.Attempt, await removed.Reader.ReadAsync(ct));
            held.Release.TrySetResult();
            await running.WaitAsync(ct);
        }
        finally
        {
            held.Release.TrySetResult();
            await lifetime.CancelAsync();
            await app.StopRunsAsync(ct);
        }
    }

    /// <summary>Detached requests hold token and cost capacity until provider settlement.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task DetachedAssessmentReservesTokenAndCostHeadroom(bool costLimit)
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var held = new HeldAssessor { LateUsage = new ModelUsage(10, 5, 15) };
        var model = new WorkModel { FinishAfterFirstRound = true, FixedAdmissionEstimate = true };
        var limit = new BudgetDimensions(costLimit ? 0 : 170, 20, TimeSpan.Zero, costLimit ? 170 : 0);
        var actual = new ExecutionBudget(limit);
        var prompts = TestPromptLoader.Instance;
        var app = new SessionApplication(events, model, actual, new SecretOutputSanitizer(), NullLogger<SessionApplication>.Instance, correctiveMessages: new CorrectiveMessageFactory(prompts), prompts: prompts, budgetFactory: () => actual, goalAssessmentProvider: held, outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("reserved exposure"), ct);
        await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, Goal), ct);
        var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, Goal), ct);
        var done = dispatcher.DispatchAsync(new WaitForRunCommand(run), ct);
        try
        {
            Assert.Same(held.Entered.Task, await Task.WhenAny(done, held.Entered.Task).WaitAsync(ct));
            var pause = await dispatcher.DispatchAsync(new RequestRunSteeringPauseCommand(session, run), ct);
            Assert.Equal(RunSteeringPauseRequestStatus.Accepted, pause.Status);
            Assert.Equal(RunSteeringPauseWaitStatus.Ready, (await dispatcher.DispatchAsync(new WaitForRunSteeringPauseCommand(session, run, pause.PauseId), ct)).Status);
            await dispatcher.DispatchAsync(new SubmitRunSteeringCommand(session, run, pause.PauseId, "Check the parser output."), ct);
            await Assert.ThrowsAsync<BudgetExceededException>(() => done.WaitAsync(ct));
            Assert.Equal(1, model.Calls);
        }
        finally
        {
            held.Release.TrySetResult();
            await app.StopRunsAsync(ct);
        }

        var used = actual.Check(new BudgetDimensions(0, 0, TimeSpan.Zero)).Used;
        Assert.Equal(2, used.Calls);
        Assert.Equal(15, used.Tokens);
        Assert.Equal(15m, used.Cost);
    }

    /// <summary>A tight budget allows a status check only when its small output cap is enforceable.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task TightBudgetAdmitsOnlyAnEnforceableSmallClassifier(bool enforceLimit)
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var status = new AnswerModel(Goal, Promise) { Status = "STOP" };
        var prompts = TestPromptLoader.Instance;
        var app = new SessionApplication(
            events,
            new WorkModel { FixedAdmissionEstimate = true },
            new ExecutionBudget(new BudgetDimensions(500, 4, TimeSpan.Zero)),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            activeTurnCompactionProfile: new ActiveTurnCompactionCandidateProfile
            {
                ProfileId = ModelProfileId.New(),
                ContextWindowTokens = 8192,
                OutputReserveTokens = 4096,
                MaximumOutputTokens = 4096,
                EnforcesRequestOutputTokenLimit = enforceLimit,
                ReasoningLevel = ReasoningLevel.High,
                SupportsReasoningOff = true,
                SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
                Cost = new ModelCostMetadata(),
            },
            correctiveMessages: new CorrectiveMessageFactory(prompts),
            prompts: prompts,
            goalAssessmentProvider: status,
            outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("tight classifier budget"), ct);
        await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, Goal), ct);
        var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, Goal), ct);
        if (enforceLimit)
        {
            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(run), ct));
            Assert.Equal(1, status.StatusChecks);
        }
        else
        {
            await Assert.ThrowsAsync<BudgetExceededException>(() => dispatcher.DispatchAsync(new WaitForRunCommand(run), ct));
            Assert.Equal(0, status.StatusChecks);
        }
    }

    /// <summary>Only an explicit report of unfinished work triggers another work round.</summary>
    [Theory]
    [InlineData("I finished addressing all 14 findings.", "STOP", 1, false)]
    [InlineData("All requested work is complete.", null, 1, false)]
    [InlineData("I still have two findings to fix.", "CONTINUE", 2, false)]
    [InlineData("Requested work remains.", null, 2, false)]
    [InlineData("Requested work remains.", null, 1, true)]
    public static async Task ContinuesOnlyForWorkerReportedUnfinishedWork(string answer, string? status, int expectedRounds, bool limitReached)
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var model = new AnswerModel(Goal, answer) { Status = status };
        var prompts = TestPromptLoader.Instance;
        var app = new SessionApplication(
            events,
            model,
            new ExecutionBudget(new BudgetDimensions(200000, 20, TimeSpan.MaxValue)),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            limits: new ExecutionLimits { MaxGoalContinuations = limitReached ? 0 : 1 },
            correctiveMessages: new CorrectiveMessageFactory(prompts),
            prompts: prompts,
            outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("worker status"), token);
        await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, Goal), token);
        var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, Goal), token);

        Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(run), token));
        Assert.Equal(expectedRounds, model.WorkRounds);
        Assert.Equal(status is null ? 0 : 1, model.StatusChecks);
        Assert.Equal(limitReached ? Goal : null, (await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), token)).DisplayText);
    }

    /// <summary>An exact requested answer does not count as a separate work status.</summary>
    [Fact]
    public static async Task RequestedLiteralContinuationSentenceEndsGoalAsync()
    {
        const string literal = "Requested work remains.";
        const string goal = $"Reply with exactly this sentence: {literal}";
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var model = new AnswerModel(goal, literal);
        var prompts = TestPromptLoader.Instance;
        var app = new SessionApplication(events, model, UnboundedBudget.Instance, new SecretOutputSanitizer(), NullLogger<SessionApplication>.Instance, correctiveMessages: new CorrectiveMessageFactory(prompts), prompts: prompts, outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("literal status"), ct);
        await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, goal), ct);

        var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, "continue"), ct);

        Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(run), ct));
        Assert.Equal(1, model.WorkRounds);
        Assert.Null((await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), ct)).DisplayText);
    }

    /// <summary>An incomplete stream cannot request an automatic continuation.</summary>
    [Fact]
    public static async Task OutputLimitPausesGoalEvenWithUnfinishedText()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var model = new AnswerModel(Goal, "Requested work remains.") { Finish = ModelFinishReason.Length, Status = null };
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
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("cutoff"), token);
        await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, Goal), token);
        var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, Goal), token);
        await dispatcher.DispatchAsync(new WaitForRunCommand(run), token);
        Assert.Equal(1, model.WorkRounds);
        Assert.Equal(0, model.StatusChecks);
        Assert.Equal(Goal, (await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), token)).DisplayText);
    }

    /// <summary>Invalid status text still incurs final provider usage and cannot start more work.</summary>
    [Fact]
    public static async Task InvalidStatusAccountsForLateUsage()
    {
        var token = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var model = new AnswerModel(Goal, "Here is my answer.") { Status = new string('X', 65), LateUsage = new ModelUsage(18000, 3000) };
        var prompts = TestPromptLoader.Instance;
        var app = new SessionApplication(
            events,
            model,
            new ExecutionBudget(new BudgetDimensions(20000, 20, TimeSpan.MaxValue)),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            correctiveMessages: new CorrectiveMessageFactory(prompts),
            prompts: prompts,
            outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("late status usage"), token);
        await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, Goal), token);
        var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, Goal), token);

        await Assert.ThrowsAsync<BudgetExceededException>(() => dispatcher.DispatchAsync(new WaitForRunCommand(run), token));
        Assert.Equal(1, model.WorkRounds);
        Assert.Equal(1, model.StatusChecks);
        Assert.Equal(Goal, (await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), token)).DisplayText);
    }

    /// <summary>Escaped closing text is admitted against the receiving provider's prepared request.</summary>
    [Theory]
    [InlineData(640, false, true)]
    [InlineData(128, false, false)]
    [InlineData(640, true, false)]
    public static async Task LongReplyFitsStatusProfileOrFinishesWithoutDispatch(int window, bool unbrokenSentence, bool fits)
    {
        const int reserve = 64;
        const string closing = "I still need to run the final check.";
        var answer = unbrokenSentence
            ? new string('x', 10000) + " would be an unwise place to say " + closing
            : string.Concat(Enumerable.Repeat("Observed \"漢字\" and a backslash \\.\n", 1500)) + closing;
        var work = new AnswerModel(Goal, answer);
        var status = new SmallWindowStatusModel();
        var clock = new FakeTimeProvider();
        var token = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream(timeProvider: clock);
        var prompts = TestPromptLoader.Instance;
        var app = new SessionApplication(
            events,
            work,
            new ExecutionBudget(new BudgetDimensions(200000, 20, TimeSpan.MaxValue)),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            activeTurnCompactionProfile: new ActiveTurnCompactionCandidateProfile
            {
                ProfileId = ModelProfileId.New(),
                ContextWindowTokens = window,
                OutputReserveTokens = reserve,
                ReasoningLevel = ReasoningLevel.None,
                SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
                Cost = new ModelCostMetadata(),
            },
            correctiveMessages: new CorrectiveMessageFactory(prompts),
            prompts: prompts,
            goalAssessmentProvider: status,
            outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("bounded status"), token);
        await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, Goal), token);
        var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, Goal), token);

        Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(run), token));
        Assert.Null((await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), token)).DisplayText);
        Assert.Equal(fits ? 2 : 1, work.WorkRounds);
        if (fits)
        {
            var dispatched = Assert.Single(status.Requests);
            Assert.InRange(dispatched.WireEstimate!.TotalCapacityTokens, reserve + 1, window);
            Assert.Equal(reserve, dispatched.WireEstimate.OutputReserveTokens);
            using var payload = JsonDocument.Parse(dispatched.Input);
            Assert.True(payload.RootElement.GetProperty("replyPrefixOmitted").GetBoolean());
            var suffix = payload.RootElement.GetProperty("reply").GetString();
            Assert.EndsWith(closing, suffix, StringComparison.Ordinal);
            Assert.EndsWith(suffix, answer, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(status.Requests);
        }
    }

    private sealed class SmallWindowStatusModel : IModelProvider, IModelRequestPreparationResolver
    {
        internal List<ModelStreamRequest> Requests { get; } = [];

        public ModelStreamRequest Prepare(ModelStreamRequest request)
        {
            using var input = JsonDocument.Parse(request.Input);
            var excerpt = input.RootElement.GetProperty("reply").GetString();
            Assert.InRange(excerpt!.Length, 1, 4 * (request.AdmissionContextWindowTokens!.Value - request.MaximumOutputTokens!.Value));
            var wire = JsonSerializer.SerializeToUtf8Bytes(request.Messages.Select(m => m.GetModelVisibleContent()));
            return request with { WireEstimate = request.WireEstimate! with { WireInputTokens = ((wire.Length + 3) / 4) + 64 } };
        }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            Requests.Add(request);
            yield return new ModelChunk { Text = "CONTINUE", FinishReason = ModelFinishReason.Stop };
        }
    }

    private sealed class AnswerModel(string goal, string answer) : IModelProvider
    {
        internal string? Status { get; init; } = "STOP";
        internal ModelFinishReason Finish { get; init; } = ModelFinishReason.Stop;
        internal ModelUsage? LateUsage { get; init; }
        internal IOException? AssessmentFailure { get; init; }
        internal int WorkRounds { get; private set; }
        internal int StatusChecks { get; private set; }
        internal int Dispatches => WorkRounds + StatusChecks;

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (request.Messages.Any(message => message.SectionId == "goal-assessment"))
            {
                StatusChecks++;
                if (AssessmentFailure is { } failure)
                {
                    throw failure;
                }

                Assert.Empty(request.Tools);
                using var packet = JsonDocument.Parse(request.Input);
                var field = Assert.Single(packet.RootElement.EnumerateObject());
                Assert.Equal("reply", field.Name);
                Assert.Equal(answer, field.Value.GetString());
                Assert.NotNull(Status);
                yield return new ModelChunk { Text = Status, FinishReason = ModelFinishReason.Stop };
                if (LateUsage is { } usage)
                {
                    yield return new ModelChunk { Usage = usage };
                }

                yield break;
            }

            WorkRounds++;
            Assert.Contains(request.Messages, message => message.SectionId == "session-goal" && message.GetModelVisibleContent().Contains(goal, StringComparison.Ordinal));
            if (WorkRounds == 2)
            {
                Assert.Contains(request.Messages, message => message.Role == ModelMessageRole.HostContext && message.GetModelVisibleContent().Contains("\"continuing\":true", StringComparison.Ordinal));
            }

            Assert.InRange(WorkRounds, 1, 2);
            yield return new ModelChunk { Text = WorkRounds == 1 ? answer : "All requested work is complete.", FinishReason = Finish };
        }
    }

    private sealed class GoalSurface(Task? assessmentStarted = null, bool resume = false, string goal = Goal, bool retainActivity = false) : IInteractionSurface
    {
        private readonly Queue<string> _inputs = new(resume ? [$"/goal set \"{goal}\"", "continue", "/quit"] : [$"/goal set \"{goal}\"", "/quit"]);

        private bool _pauseIssued;

        public InteractionSurfaceCapabilities Capabilities { get; } = new(SupportsActiveRunInput: assessmentStarted is not null, SupportsRetainedActivity: retainActivity);

        internal Action<Guid, Task>? ActivityPresented { get; init; }

        internal Action<Guid>? ActivityRemoved { get; init; }

        internal Action? SteeringMarkerPresented { get; init; }

        internal int SteeringReads { get; private set; }

        internal string? SteeringReplacement { get; init; }

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
                1 when SteeringReplacement is { } replacement => $"/goal set \"{replacement}\"",
                _ when SteeringReplacement is not null => throw new InvalidOperationException("The replacement must resume without another input."),
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
                    var content = string.Concat(text.Segments.Select(segment => segment.Text));
                    Answers.Enqueue(content);
                    if (content.StartsWith("Steering dismissed;", StringComparison.Ordinal))
                    {
                        SteeringMarkerPresented?.Invoke();
                    }

                    if (text.Notification is { } notification)
                    {
                        Notifications.Enqueue(notification);
                    }
                }
            }

            return Task.CompletedTask;
        }

        public Task PresentSessionStatusAsync(SessionStatusSnapshot status, CancellationToken cancellationToken = default) => Task.CompletedTask;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003", Justification = "The coordinator owns the activity lifetime observed by this test surface.")]
        public async Task PresentActivityUntilAsync(InteractionActivity activity, Task operation, CancellationToken cancellationToken = default)
        {
            if (activity.GoalAssessmentId is not { } attempt)
            {
                await operation.WaitAsync(cancellationToken);
                return;
            }

            ActivityPresented?.Invoke(attempt, operation);
            await operation.WaitAsync(cancellationToken);
            ActivityRemoved?.Invoke(attempt);
        }
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

    private sealed class HeldAssessmentHook : IHookCoordinator
    {
        public IReadOnlyList<HookHandlerDescriptor> Handlers => [];

        internal Action? OnEntered { get; init; }

        internal HookPoint Point { get; init; } = HookPoint.BeforeModelRequest;

        internal bool ModelSucceeded { get; private set; }

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HookHandlerDescriptor? GetHandler(HookHandlerId handlerId) => null;

        public bool SetEnabled(HookHandlerId handlerId, bool enabled) => false;

        public async Task<HookBoundaryDecision> InvokeAsync(HookPoint point, SessionId sessionId, RunId? runId, string? repositoryIdentity, Guid operationId, int generation, IReadOnlyDictionary<string, string>? payload = null, IReadOnlyList<ExecutionArtifactReference>? artifacts = null, IReadOnlyList<string>? callChain = null, RepositoryStorageBinding? storageBinding = null, CancellationToken cancellationToken = default)
        {
            if (point == Point && payload?.GetValueOrDefault("stage") == "goal-assessment" && !Entered.Task.IsCompleted)
            {
                ModelSucceeded = bool.TryParse(payload.GetValueOrDefault("succeeded"), out var succeeded) && succeeded;
                Entered.SetResult();
                await Release.Task.WaitAsync(TestContext.Current.CancellationToken);
                OnEntered?.Invoke();
            }

            return new(HookDecisionKind.Continue, [], []);
        }

        public Task<HookBoundaryDecision> InvokeHandlerAsync(HookHandlerId handlerId, HookPoint point, SessionId sessionId, RunId? runId, string? repositoryIdentity, Guid operationId, int generation, IReadOnlyDictionary<string, string>? payload = null, IReadOnlyList<ExecutionArtifactReference>? artifacts = null, IReadOnlyList<string>? callChain = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class HeldAssessor : IModelProvider, IModelRequestPreparationResolver
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Left { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ModelUsage? LateUsage { get; init; }

        public ModelStreamRequest Prepare(ModelStreamRequest request) => LateUsage is null ? request : request with
        {
            WireEstimate = new ModelWireEstimate { WireInputTokens = 20, OutputReserveTokens = request.MaximumOutputTokens.GetValueOrDefault() },
        };

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var packet = JsonDocument.Parse(request.Input);
            if (packet.RootElement.GetProperty("reply").GetString() == "Replacement completed.")
            {
                yield return new ModelChunk { Text = "STOP", FinishReason = ModelFinishReason.Stop };
                yield break;
            }

            Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(TestContext.Current.CancellationToken);
                if (LateUsage is { } usage)
                {
                    yield return new ModelChunk { Usage = usage };
                    yield return new ModelChunk { Usage = usage };
                }

                yield return new ModelChunk { Text = "STOP", FinishReason = ModelFinishReason.Stop };
            }
            finally
            {
                Left.TrySetResult();
            }
        }
    }

    private sealed class WorkModel : IModelProvider, IModelRequestPreparationResolver
    {
        private int _calls;

        internal bool FinishAfterFirstRound { get; init; }

        internal bool FixedAdmissionEstimate { get; init; }

        internal int Calls => Volatile.Read(ref _calls);

        internal ConcurrentQueue<ModelStreamRequest> Requests { get; } = new();

        public ModelStreamRequest Prepare(ModelStreamRequest request) => FixedAdmissionEstimate ? request with
        {
            WireEstimate = new ModelWireEstimate { WireInputTokens = 20, OutputReserveTokens = 96 },
            MaximumOutputTokens = 96,
            AdmissionCost = new ModelCostMetadata { InputPerMillionTokens = 1_000_000, OutputPerMillionTokens = 1_000_000 },
        } : request;

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            Requests.Enqueue(request);
            var call = Interlocked.Increment(ref _calls);
            if (FinishAfterFirstRound && call > 1)
            {
                yield return new ModelChunk { Text = "All requested work is complete.", FinishReason = ModelFinishReason.Stop };
                yield break;
            }

            var replacement = request.Messages.Any(message => message.SectionId == "session-goal" && message.GetModelVisibleContent().Contains("Write replacement text", StringComparison.Ordinal));
            yield return new ModelChunk { Text = replacement ? "Replacement completed." : Promise, FinishReason = ModelFinishReason.Stop };
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

        internal string? TestName { get; init; }

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
            if (TestName is { } name)
            {
                result = result with { Test = result.Test with { FullyQualifiedName = name } };
            }

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
