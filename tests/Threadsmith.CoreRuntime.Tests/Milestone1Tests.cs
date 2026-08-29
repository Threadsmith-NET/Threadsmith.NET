namespace Threadsmith.CoreRuntime.Tests;

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PrettyPrompt;
using PrettyPrompt.Rendering;
using Threadsmith.Cli;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Hooks;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Tui;
using Xunit;

/// <summary>Verifies the complete Milestone 1 command, event, shell, and durability contracts.</summary>
public static class Milestone1Tests
{
    /// <summary>Gets every legal non-terminal transition path used by the transition matrix.</summary>
    public static TheoryData<RunPhase[]> LegalTransitionPaths => [.. GetLegalTransitionPaths()];

    /// <summary>Every declared legal transition path succeeds and emits one event per edge.</summary>
    [Theory]
    [MemberData(nameof(LegalTransitionPaths))]
    public static async Task StateMachine_LegalTransitionPaths_AreAccepted(RunPhase[] path)
    {
        await using var stream = new DomainEventStream();
        var events = new List<IDomainEvent>();
        await using var subscription = stream.Subscribe((domainEvent, _) =>
        {
            events.Add(domainEvent);
            return Task.CompletedTask;
        });
        var machine = new RunStateMachine(SessionId.New(), RunId.New(), stream);

        foreach (var phase in path)
        {
            await machine.TransitionAsync(phase, "test");
        }

        Assert.Equal(path[^1], machine.Phase);
        Assert.Equal(path.Length, events.Count(item => item is RunTransitioned));
    }

    /// <summary>Illegal skips and every transition out of a terminal phase are rejected observably.</summary>
    [Theory]
    [InlineData(RunPhase.Completion)]
    [InlineData(RunPhase.Failed)]
    [InlineData(RunPhase.Cancelled)]
    [InlineData(RunPhase.RolledBack)]
    public static async Task StateMachine_TerminalPhases_AreAbsorbing(RunPhase terminalPhase)
    {
        await using var stream = new DomainEventStream();
        var events = new List<IDomainEvent>();
        await using var subscription = stream.Subscribe((domainEvent, _) =>
        {
            events.Add(domainEvent);
            return Task.CompletedTask;
        });
        var machine = new RunStateMachine(SessionId.New(), RunId.New(), stream);
        if (terminalPhase is RunPhase.Completion or RunPhase.RolledBack)
        {
            await machine.TransitionAsync(RunPhase.EvidenceCollection, "test");
            if (terminalPhase == RunPhase.RolledBack)
            {
                await machine.TransitionAsync(RunPhase.ChangePlanning, "test");
                await machine.TransitionAsync(RunPhase.AwaitingPlanApproval, "test");
                await machine.TransitionAsync(RunPhase.MutationPreparation, "test");
                await machine.TransitionAsync(RunPhase.AwaitingMutationApproval, "test");
                await machine.TransitionAsync(RunPhase.Mutation, "test");
            }
        }

        await machine.TransitionAsync(terminalPhase, "terminal");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            machine.TransitionAsync(RunPhase.Failed, "illegal"));
        Assert.Equal(terminalPhase, machine.Phase);
        Assert.Contains(events, item => item is RunTransitionFailed);
    }

    /// <summary>Every possible phase pair agrees with the declared transition policy.</summary>
    [Fact]
    public static async Task StateMachine_AllPhasePairs_MatchTheTransitionMatrix()
    {
        var paths = new Dictionary<RunPhase, RunPhase[]>
        {
            [RunPhase.Intake] = [],
            [RunPhase.RepositoryDiscovery] = [RunPhase.RepositoryDiscovery],
            [RunPhase.EvidenceCollection] = [RunPhase.EvidenceCollection],
            [RunPhase.ChangePlanning] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning],
            [RunPhase.AwaitingPlanApproval] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval],
            [RunPhase.ImplementationPreparing] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.ImplementationPreparing],
            [RunPhase.ImplementationModelTurn] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.ImplementationPreparing, RunPhase.ImplementationModelTurn],
            [RunPhase.MutationProposed] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.ImplementationPreparing, RunPhase.ImplementationModelTurn, RunPhase.MutationProposed],
            [RunPhase.MutationStaged] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.ImplementationPreparing, RunPhase.ImplementationModelTurn, RunPhase.MutationProposed, RunPhase.MutationStaged],
            [RunPhase.BaselineValidation] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.ImplementationPreparing, RunPhase.ImplementationModelTurn, RunPhase.MutationProposed, RunPhase.MutationStaged, RunPhase.AwaitingMutationApproval, RunPhase.BaselineValidation],
            [RunPhase.MutationApplyPending] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.ImplementationPreparing, RunPhase.ImplementationModelTurn, RunPhase.MutationProposed, RunPhase.MutationStaged, RunPhase.AwaitingMutationApproval, RunPhase.BaselineValidation, RunPhase.MutationApplyPending],
            [RunPhase.CorrectionPending] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.ImplementationPreparing, RunPhase.ImplementationModelTurn, RunPhase.MutationProposed, RunPhase.MutationStaged, RunPhase.AwaitingMutationApproval, RunPhase.BaselineValidation, RunPhase.MutationApplyPending, RunPhase.Mutation, RunPhase.Compilation, RunPhase.CorrectionPending],
            [RunPhase.CorrectionModelTurn] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.ImplementationPreparing, RunPhase.ImplementationModelTurn, RunPhase.MutationProposed, RunPhase.MutationStaged, RunPhase.AwaitingMutationApproval, RunPhase.BaselineValidation, RunPhase.MutationApplyPending, RunPhase.Mutation, RunPhase.Compilation, RunPhase.CorrectionPending, RunPhase.CorrectionModelTurn],
            [RunPhase.CompletionPending] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.ImplementationPreparing, RunPhase.ImplementationModelTurn, RunPhase.MutationProposed, RunPhase.MutationStaged, RunPhase.AwaitingMutationApproval, RunPhase.BaselineValidation, RunPhase.MutationApplyPending, RunPhase.Mutation, RunPhase.Compilation, RunPhase.CorrectionPending, RunPhase.CompletionPending],
            [RunPhase.MutationPreparation] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation],
            [RunPhase.AwaitingMutationApproval] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation, RunPhase.AwaitingMutationApproval],
            [RunPhase.Mutation] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation, RunPhase.AwaitingMutationApproval, RunPhase.Mutation],
            [RunPhase.Compilation] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation, RunPhase.AwaitingMutationApproval, RunPhase.Mutation, RunPhase.Compilation],
            [RunPhase.Testing] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation, RunPhase.AwaitingMutationApproval, RunPhase.Mutation, RunPhase.Compilation, RunPhase.Testing],
            [RunPhase.Verification] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation, RunPhase.AwaitingMutationApproval, RunPhase.Mutation, RunPhase.Compilation, RunPhase.Testing, RunPhase.Verification],
            [RunPhase.AwaitingAcceptance] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation, RunPhase.AwaitingMutationApproval, RunPhase.Mutation, RunPhase.Compilation, RunPhase.Testing, RunPhase.Verification, RunPhase.AwaitingAcceptance],
            [RunPhase.Completion] = [RunPhase.EvidenceCollection, RunPhase.Completion],
            [RunPhase.Failed] = [RunPhase.Failed],
            [RunPhase.Cancelled] = [RunPhase.Cancelled],
            [RunPhase.RolledBack] = [RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation, RunPhase.AwaitingMutationApproval, RunPhase.Mutation, RunPhase.RolledBack],
        };
        var allowed = GetLegalTransitionPaths()
            .SelectMany(path => path.Select((destination, index) =>
                (index == 0 ? RunPhase.Intake : path[index - 1], destination)))
            .ToHashSet();
        var terminal = new HashSet<RunPhase>
        {
            RunPhase.Completion,
            RunPhase.Failed,
            RunPhase.Cancelled,
            RunPhase.RolledBack,
        };

        foreach (var source in Enum.GetValues<RunPhase>())
        {
            foreach (var destination in Enum.GetValues<RunPhase>())
            {
                await using var stream = new DomainEventStream();
                var observed = new List<IDomainEvent>();
                await using var subscription = stream.Subscribe((domainEvent, _) =>
                {
                    observed.Add(domainEvent);
                    return Task.CompletedTask;
                });
                var machine = new RunStateMachine(SessionId.New(), RunId.New(), stream);
                foreach (var phase in paths[source])
                {
                    await machine.TransitionAsync(phase, "arrange");
                }

                observed.Clear();
                var isLegal = !terminal.Contains(source)
                    && (destination is RunPhase.Failed or RunPhase.Cancelled
                        || allowed.Contains((source, destination)));
                if (isLegal)
                {
                    await machine.TransitionAsync(destination, "matrix");
                    Assert.Contains(observed, item => item is RunTransitioned);
                }
                else
                {
                    await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        machine.TransitionAsync(destination, "matrix"));
                    Assert.Contains(observed, item => item is RunTransitionFailed);
                }
            }
        }
    }

    /// <summary>Every catalog event survives interface-polymorphic and durable-registry round trips.</summary>
    [Fact]
    public static void DomainEvents_AllCatalogTypes_RoundTrip()
    {
        var expectedEvents = TestEvents();

        foreach (var expected in expectedEvents)
        {
            var polymorphicJson = JsonSerializer.Serialize<IDomainEvent>(expected);
            var polymorphic = JsonSerializer.Deserialize<IDomainEvent>(polymorphicJson);
            Assert.NotNull(polymorphic);
            Assert.Equal(expected.GetType(), polymorphic.GetType());
            Assert.Equal(1, polymorphic.SchemaVersion);

            var eventName = DomainEventJson.GetDiscriminator(expected);
            var durable = DomainEventJson.Deserialize(
                eventName,
                expected.SchemaVersion,
                DomainEventJson.Serialize(expected));
            Assert.Equal(expected.GetType(), durable.GetType());
        }

        var declaredEventTypes = typeof(DomainEvent).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(DomainEvent).IsAssignableFrom(type))
            .ToHashSet();
        Assert.True(declaredEventTypes.SetEquals(expectedEvents.Select(item => item.GetType())));
        Assert.True(declaredEventTypes.SetEquals(DomainEventJson.RegisteredTypes.Values));
        Assert.Equal(expectedEvents.Count, DomainEventJson.RegisteredTypes.Count);
        Assert.Throws<InvalidDataException>(() =>
            DomainEventJson.Deserialize("unknown", 1, "{}"));
        Assert.Throws<NotSupportedException>(() =>
            DomainEventJson.Deserialize("sessionCreated", 2, "{}"));
    }

    /// <summary>Typed approval metadata is durable while legacy events default to an unclassified safe value.</summary>
    [Fact]
    public static void ApprovalRequested_Kind_RoundTrips_AndLegacyDefaultsToUnspecified()
    {
        var expected = new ApprovalRequested(
            SessionId.New(),
            DateTimeOffset.UtcNow,
            ApprovalId.New(),
            "Presentation text",
            ApprovalRequestKind.Plan)
        {
            SchemaVersion = 2,
        };

        var roundTripped = Assert.IsType<ApprovalRequested>(DomainEventJson.Deserialize(
            "approvalRequested",
            expected.SchemaVersion,
            DomainEventJson.Serialize(expected)));
        Assert.Equal(ApprovalRequestKind.Plan, roundTripped.Kind);

        var legacy = expected with
        {
            Kind = ApprovalRequestKind.Unspecified,
            SchemaVersion = 1,
        };
        var legacyPayload = JsonNode.Parse(DomainEventJson.Serialize(legacy))?.AsObject()
            ?? throw new InvalidDataException("The approval event did not serialize as a JSON object.");
        Assert.True(legacyPayload.Remove(nameof(ApprovalRequested.Kind)));
        var restoredLegacy = Assert.IsType<ApprovalRequested>(DomainEventJson.Deserialize(
            "approvalRequested",
            legacy.SchemaVersion,
            legacyPayload.ToJsonString()));
        Assert.Equal(ApprovalRequestKind.Unspecified, restoredLegacy.Kind);
    }

    /// <summary>Every subscriber receives every event, and a slow subscriber backpressures publication.</summary>
    [Fact]
    public static async Task DomainEventStream_FansOut_WithBoundedBackpressure()
    {
        await using var stream = new DomainEventStream();
        var first = new ConcurrentBag<Guid>();
        var second = new ConcurrentBag<Guid>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var firstSubscription = stream.Subscribe(
            (domainEvent, _) =>
            {
                first.Add(domainEvent.SessionId.Value);
                return Task.CompletedTask;
            },
            2);
        await using var secondSubscription = stream.Subscribe(
            async (domainEvent, cancellationToken) =>
            {
                await release.Task.WaitAsync(cancellationToken);
                second.Add(domainEvent.SessionId.Value);
            },
            2);
        var sessionId = SessionId.New();

        var publish = stream.PublishAsync(
            new SessionCreated(sessionId, DateTimeOffset.UtcNow, "test"));
        await Task.Delay(20);
        Assert.False(publish.IsCompleted);
        release.TrySetResult();
        await publish;

        for (var index = 0; index < 2048; index++)
        {
            await stream.PublishAsync(
                new ModelOutputObserved(sessionId, DateTimeOffset.UtcNow, index.ToString()));
        }

        Assert.Equal(2049, first.Count);
        Assert.Equal(2049, second.Count);
    }

    /// <summary>Projection reads are detached and completed runs reach the completion phase.</summary>
    [Fact]
    public static async Task SessionApplication_ProjectsTerminalState_AndDetachedSnapshots()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession
        {
            Turns = [new ScriptedTurn { Text = "hello", Usage = new ModelUsage(2, 3) }],
        });
        var sessionId = await harness.Dispatcher.DispatchAsync(new CreateSessionCommand("test"));
        var runId = await harness.Dispatcher.DispatchAsync(
            new SubmitRequestCommand(sessionId, "request"));

        Assert.True(await harness.Dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
        var key = new ProjectionKey("session", sessionId.Value.ToString("D"));
        var first = await harness.Projections.GetAsync<SessionProjection>(key);
        var second = await harness.Projections.GetAsync<SessionProjection>(key);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(RunPhase.Completion, first.Phase);
        Assert.NotSame(first.Activity, second.Activity);
        Assert.Contains("hello", string.Concat(first.Activity));
        Assert.Equal(new SessionUsageSnapshot(2, 3, false), harness.Usage.GetSnapshot(sessionId));
    }

    /// <summary>A completed provider request without usage metadata remains explicitly unknown.</summary>
    [Fact]
    public static async Task SessionApplication_MissingProviderUsage_RecordsUnknownUsage()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession
        {
            Turns = [new ScriptedTurn { Text = "hello" }],
        });
        var sessionId = await harness.Dispatcher.DispatchAsync(new CreateSessionCommand("test"));
        var runId = await harness.Dispatcher.DispatchAsync(
            new SubmitRequestCommand(sessionId, "request"));

        Assert.True(await harness.Dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
        Assert.True(harness.Usage.GetSnapshot(sessionId).HasUnknownUsage);
    }

    /// <summary>Run cancellation flows through the command boundary and remains inspectable.</summary>
    [Fact]
    public static async Task SessionApplication_CancelCommand_ProducesCancelledProjection()
    {
        await using var harness = await SessionHarness.CreateAsync(
            new ScriptedSession { Turns = [new ScriptedTurn { Text = "one two three" }] },
            TimeSpan.FromSeconds(1));
        var sessionId = await harness.Dispatcher.DispatchAsync(new CreateSessionCommand("test"));
        var runId = await harness.Dispatcher.DispatchAsync(
            new SubmitRequestCommand(sessionId, "request"));

        Assert.True(await harness.Dispatcher.DispatchAsync(new CancelRunCommand(sessionId, runId)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
        var state = await harness.Projections.GetAsync<SessionProjection>(
            new ProjectionKey("session", sessionId.Value.ToString("D")));

        Assert.NotNull(state);
        Assert.Equal(RunPhase.Cancelled, state.Phase);
        Assert.Contains(harness.Events, item => item is RunCompleted { Succeeded: false });
    }

    /// <summary>Requests for unknown sessions are rejected instead of reporting phantom success.</summary>
    [Fact]
    public static async Task SessionApplication_UnknownSession_IsRejected()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Dispatcher.DispatchAsync(
                new SubmitRequestCommand(SessionId.New(), "request")));
    }

    /// <summary>Dispatcher middleware runs in registration order and receives cancellation.</summary>
    [Fact]
    public static async Task CommandDispatcher_MiddlewareOrdering_AndCancellation_ArePreserved()
    {
        var order = new List<string>();
        var dispatcher = new CommandDispatcher(
            [new EchoHandler(order)],
            [new RecordingMiddleware("one", order), new RecordingMiddleware("two", order)]);

        var result = await dispatcher.DispatchAsync(new EchoCommand("value"));

        Assert.Equal("value", result);
        Assert.Equal(["one:before", "two:before", "handler", "two:after", "one:after"], order);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.DispatchAsync(new EchoCommand("cancel"), cancellation.Token));
    }

    /// <summary>A pre-cancelled known command is observed by middleware while its handler remains uncalled.</summary>
    [Fact]
    public static async Task CommandDispatcher_PreCancelledKnownCommand_IsObservedByMiddlewareWithoutHandler()
    {
        var order = new List<string>();
        var dispatcher = new CommandDispatcher(
            [new EchoHandler(order)],
            [new EntryRecordingMiddleware("stage", order)]);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.DispatchAsync(new EchoCommand("cancel"), cancellation.Token));

        // The middleware envelope is entered, but the handler is never invoked because the
        // terminal delegate observes cancellation before delegating to the handler.
        Assert.Equal(["stage:before", "stage:cancelled"], order);
    }

    /// <summary>A pre-cancelled unknown command preserves cancellation precedence outside middleware.</summary>
    [Fact]
    public static async Task CommandDispatcher_PreCancelledUnknownCommand_CancelsBeforeMissingHandlerFailure()
    {
        var order = new List<string>();
        var dispatcher = new CommandDispatcher(
            [new EchoHandler(order)],
            [new EntryRecordingMiddleware("stage", order)]);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            dispatcher.DispatchAsync(new UnhandledCommand(), cancellation.Token));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            dispatcher.DispatchAsync(new UnhandledCommand(), CancellationToken.None));
        Assert.Empty(order);
    }

    /// <summary>Turn staging remains private, cancellation discards it, and invalidation applies at commit.</summary>
    [Fact]
    public static async Task Turn_CopyOnWrite_EnforcesCommitAndCancellationBoundaries()
    {
        var baseline = new BaselineSnapshot(
            3,
            new Dictionary<string, string> { ["a"] = "old", ["stale"] = "value" });
        await using (var turn = new Turn(baseline))
        {
            turn.Staging.Set("a", "new");
            turn.Invalidate("stale");
            Assert.Equal("old", baseline.Get("a"));
            Assert.Equal("value", baseline.Get("stale"));
            var committed = await turn.CommitAsync();
            Assert.Equal("new", committed.Get("a"));
            Assert.Null(committed.Get("stale"));
            Assert.Equal(4, committed.Revision);
        }

        await using var cancelled = new Turn(baseline);
        cancelled.Staging.Set("a", "discarded");
        await cancelled.CancelAsync();
        Assert.Equal("old", baseline.Get("a"));
        Assert.Throws<ObjectDisposedException>(() => cancelled.Staging.Set("a", "illegal"));
    }

    /// <summary>Budgets reject negative dimensions and report exhaustion after positive accrual.</summary>
    [Fact]
    public static void ExecutionBudget_ValidatesAndAccruesEveryDimension()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ExecutionBudget(new BudgetDimensions(-1, 1, TimeSpan.Zero)));
        var budget = new ExecutionBudget(new BudgetDimensions(10, 2, TimeSpan.FromSeconds(1), 1));

        var check = budget.Check(new BudgetDimensions(11, 0, TimeSpan.Zero));
        var first = budget.Accrue(new BudgetDimensions(10, 2, TimeSpan.FromSeconds(1), 1));
        var exhausted = budget.Accrue(new BudgetDimensions(1, 0, TimeSpan.Zero));

        Assert.True(check.IsExhausted);
        Assert.False(first.IsExhausted);
        Assert.True(exhausted.IsExhausted);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            budget.Accrue(new BudgetDimensions(-1, 0, TimeSpan.Zero)));
    }

    /// <summary>Fresh operation scopes retain configured limits without inheriting prior usage.</summary>
    [Fact]
    public static void ExecutionBudget_CreateScope_StartsUnused()
    {
        var prototype = new ExecutionBudget(new BudgetDimensions(10, 2, TimeSpan.FromSeconds(1), 1));
        var first = prototype.CreateScope();
        var second = prototype.CreateScope();

        Assert.False(first.Accrue(new BudgetDimensions(10, 2, TimeSpan.FromSeconds(1), 1)).IsExhausted);
        Assert.True(first.Accrue(new BudgetDimensions(1, 0, TimeSpan.Zero)).IsExhausted);
        Assert.False(second.Accrue(new BudgetDimensions(10, 2, TimeSpan.FromSeconds(1), 1)).IsExhausted);
        Assert.False(prototype.Check(new BudgetDimensions(10, 2, TimeSpan.FromSeconds(1), 1)).IsExhausted);
    }

    /// <summary>Unbounded conversational budgets validate usage without accumulating a quota.</summary>
    [Fact]
    public static void UnboundedBudget_RepeatedUsage_NeverExhausts()
    {
        var usage = new BudgetDimensions(100_000, 100, TimeSpan.FromHours(1), 100);

        Assert.False(UnboundedBudget.Instance.Accrue(usage).IsExhausted);
        Assert.False(UnboundedBudget.Instance.Accrue(usage).IsExhausted);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            UnboundedBudget.Instance.Check(new BudgetDimensions(-1, 0, TimeSpan.Zero)));
    }

    /// <summary>The fake uses the seed deterministically and validates structured tool arguments.</summary>
    [Fact]
    public static async Task FakeModel_SeededReplay_IsDeterministicAndStructured()
    {
        var script = new ScriptedSession
        {
            Turns =
            [
                new ScriptedTurn { Text = "alpha beta gamma delta epsilon zeta eta theta" },
                new ScriptedTurn { ToolName = "read_file", ArgumentsJson = "{\"path\":\"README.md\"}" },
            ],
        };

        var first = await CollectAsync(new FakeModelProvider(script), 42);
        var second = await CollectAsync(new FakeModelProvider(script), 42);
        var differentSeed = await CollectAsync(new FakeModelProvider(script), 7);

        Assert.Equal(first, second);
        Assert.NotEqual(first, differentSeed);
        var malformed = new FakeModelProvider(new ScriptedSession
        {
            Turns = [new ScriptedTurn { ToolName = "read_file", ArgumentsJson = "{" }],
        });
        var nonObject = new FakeModelProvider(new ScriptedSession
        {
            Turns = [new ScriptedTurn { ToolName = "read_file", ArgumentsJson = "[]" }],
        });
        await Assert.ThrowsAsync<MalformedModelOutputException>(() => CollectAsync(malformed, 42));
        await Assert.ThrowsAsync<MalformedModelOutputException>(() => CollectAsync(nonObject, 42));
    }

    /// <summary>Tool scripts pause and resume without replaying the same request forever.</summary>
    [Fact]
    public static async Task FakeModel_ToolContinuation_ResumesAtNextScriptSegment()
    {
        var provider = new FakeModelProvider(new ScriptedSession
        {
            Turns =
            [
                new ScriptedTurn { Text = "Inspecting the request." },
                new ScriptedTurn
                {
                    ToolName = "list_files",
                    ArgumentsJson = "{\"path\":\".\",\"maximumEntries\":20}",
                },
                new ScriptedTurn
                {
                    Text = "Scripted session complete.",
                    Usage = new ModelUsage(8, 6),
                },
            ],
        });
        var runId = RunId.New();

        var initial = await CollectChunksAsync(provider, new ModelStreamRequest
        {
            RunId = runId,
            Input = "this is a test",
            Seed = 42,
        });
        var continuation = await CollectChunksAsync(provider, new ModelStreamRequest
        {
            RunId = runId,
            Input = "tool evidence",
            Seed = 42,
            ToolContinuationRound = 1,
        });
        var independentRun = await CollectChunksAsync(provider, new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "another test",
            Seed = 42,
        });

        Assert.Contains(initial, chunk => chunk.Text?.Contains(
            "Inspecting",
            StringComparison.Ordinal) == true);
        Assert.Contains(initial, chunk => chunk.Output is ToolRequestModelOutput
        {
            ToolName: "list_files",
        });
        Assert.DoesNotContain(initial, chunk => chunk.Text?.Contains(
            "complete",
            StringComparison.Ordinal) == true);
        Assert.DoesNotContain(continuation, chunk => chunk.Output is ToolRequestModelOutput);
        Assert.Contains(continuation, chunk => chunk.Text?.Contains(
            "complete",
            StringComparison.Ordinal) == true);
        Assert.Contains(continuation, chunk => chunk.Usage == new ModelUsage(8, 6));
        Assert.Contains(independentRun, chunk => chunk.Output is ToolRequestModelOutput
        {
            ToolName: "list_files",
        });

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => CollectChunksAsync(
            provider,
            new ModelStreamRequest
            {
                RunId = RunId.New(),
                Input = "invalid continuation",
                ToolContinuationRound = -1,
            }));
    }

    /// <summary>Checked-in JSON fixtures cover success, missing usage, failures, and cancellation.</summary>
    [Fact]
    public static async Task FakeModel_CheckedInFixtures_ExerciseScriptContract()
    {
        var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "fixtures", "scripts");
        var success = FakeModelProvider.Load(
            await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, "success.json")));
        var missingUsage = FakeModelProvider.Load(
            await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, "missing-usage.json")));

        var successOutput = await CollectAsync(new FakeModelProvider(success), 42);
        var missingUsageOutput = await CollectAsync(new FakeModelProvider(missingUsage), 42);

        Assert.NotEmpty(successOutput);
        Assert.Equal("read_file", success.Turns[0].ToolName);
        Assert.Contains("\"InputTokens\":8", successOutput, StringComparison.Ordinal);
        Assert.Equal("usage intentionally omitted", missingUsage.Turns[0].Text);
        Assert.Contains("\"Usage\":null", missingUsageOutput, StringComparison.Ordinal);
        await Assert.ThrowsAsync<TransientModelException>(
            () => CollectFixtureAsync(fixtureDirectory, "transient-failure.json"));
        await Assert.ThrowsAsync<MalformedModelOutputException>(
            () => CollectFixtureAsync(fixtureDirectory, "malformed-output.json"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CollectFixtureAsync(fixtureDirectory, "cancellation.json"));
    }

    /// <summary>Every scripted failure maps to its expected retry classification.</summary>
    [Theory]
    [InlineData(ScriptFailureKind.TransientProvider, RetryClassification.TransientProvider)]
    [InlineData(ScriptFailureKind.MalformedOutput, RetryClassification.MalformedOutput)]
    [InlineData(ScriptFailureKind.Cancellation, RetryClassification.Permanent)]
    public static async Task FakeModel_Failures_AreClassified(
        ScriptFailureKind failure,
        RetryClassification expected)
    {
        var provider = new FakeModelProvider(new ScriptedSession
        {
            Turns = [new ScriptedTurn { Failure = failure }],
        });

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => CollectAsync(provider, 42));

        Assert.Equal(expected, ModelFailureClassifier.Classify(exception));
    }

    /// <summary>Scripted failures and cancellation become observable terminal session states.</summary>
    [Theory]
    [InlineData(ScriptFailureKind.TransientProvider, RunPhase.Failed)]
    [InlineData(ScriptFailureKind.MalformedOutput, RunPhase.Failed)]
    [InlineData(ScriptFailureKind.Cancellation, RunPhase.Cancelled)]
    public static async Task SessionApplication_ScriptedFailures_AreTerminal(
        ScriptFailureKind failure,
        RunPhase expectedPhase)
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession
        {
            Turns = [new ScriptedTurn { Failure = failure }],
        });
        var sessionId = await harness.Dispatcher.DispatchAsync(new CreateSessionCommand("test"));
        var runId = await harness.Dispatcher.DispatchAsync(
            new SubmitRequestCommand(sessionId, "request"));

        await Assert.ThrowsAnyAsync<Exception>(() =>
            harness.Dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
        var state = await harness.Projections.GetAsync<SessionProjection>(
            new ProjectionKey("session", sessionId.Value.ToString("D")));

        Assert.NotNull(state);
        Assert.Equal(expectedPhase, state.Phase);
        Assert.Contains(harness.Events, item => item is RunCompleted { Succeeded: false });
    }

    /// <summary>Missing usage degrades gracefully while reported usage is accrued.</summary>
    [Fact]
    public static async Task FakeModel_MissingUsage_DoesNotCrashBudgetLayer()
    {
        await using var harness = await SessionHarness.CreateAsync(
            new ScriptedSession { Turns = [new ScriptedTurn { Text = "no usage" }] },
            budget: new ExecutionBudget(new BudgetDimensions(0, 0, TimeSpan.FromMinutes(1))));
        var sessionId = await harness.Dispatcher.DispatchAsync(new CreateSessionCommand("test"));
        var runId = await harness.Dispatcher.DispatchAsync(
            new SubmitRequestCommand(sessionId, "request"));

        Assert.True(await harness.Dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
    }

    /// <summary>The TUI controller maps open, submit, wait, and cancel gestures to commands.</summary>
    [Fact]
    public static async Task TuiController_DrivesInteractiveCommandBoundary()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession
        {
            Turns = [new ScriptedTurn { Text = "interactive" }],
        });
        var controller = new TuiController(new TuiPresenter(harness.Dispatcher, harness.Projections));

        var sessionId = await controller.OpenAsync("TUI");
        var runId = await controller.SubmitAsync("request");
        Assert.Equal(runId, controller.ActiveRunId);
        Assert.True(await controller.WaitForActiveRunAsync());
        var snapshot = await controller.RenderAsync();

        Assert.Equal(sessionId, controller.SessionId);
        Assert.Null(controller.ActiveRunId);
        Assert.Contains("interactive", snapshot.Workspace);
        Assert.Equal(nameof(RunPhase.Completion), snapshot.Status);
    }

    /// <summary>The interactive shell queues plan review only from explicit approval requests.</summary>
    [Fact]
    public static void ConversationalShell_PlanReviewQueue_UsesApprovalRequestedOnly()
    {
        var sessionId = SessionId.New();
        var now = DateTimeOffset.UtcNow;

        Assert.True(InteractiveDecisionClassifier.IsPlanApprovalRequest(new ApprovalRequested(
            sessionId,
            now,
            ApprovalId.New(),
            "Display text deliberately does not identify a plan",
            ApprovalRequestKind.Plan)));
        Assert.False(InteractiveDecisionClassifier.IsPlanApprovalRequest(new ApprovalRequested(
            sessionId,
            now,
            ApprovalId.New(),
            "Approve plan revision 2: misleading mutation display text",
            ApprovalRequestKind.MutationSet)));
        Assert.False(InteractiveDecisionClassifier.IsPlanApprovalRequest(new ApprovalRequested(
            sessionId,
            now,
            ApprovalId.New(),
            "Approve plan revision 2: legacy event without typed metadata")));
    }

    /// <summary>The TUI controller routes cancellation through the application command boundary.</summary>
    [Fact]
    public static async Task TuiController_CancelActiveRun_ProducesCancelledState()
    {
        await using var harness = await SessionHarness.CreateAsync(
            new ScriptedSession
            {
                Turns = [new ScriptedTurn { Text = "cancel this interactive run" }],
            },
            TimeSpan.FromSeconds(1));
        var controller = new TuiController(new TuiPresenter(harness.Dispatcher, harness.Projections));

        await controller.OpenAsync("TUI");
        await controller.SubmitAsync("request");
        Assert.True(await controller.CancelActiveRunAsync());
        var snapshot = await controller.RenderAsync();

        Assert.Null(controller.ActiveRunId);
        Assert.Equal(nameof(RunPhase.Cancelled), snapshot.Status);
    }

    /// <summary>Discarding a staged mutation clears the correction guard before the next submission.</summary>
    [Fact]
    public static async Task TuiController_DiscardStagedMutation_AllowsNextSubmission()
    {
        var fixture = new PostApplyValidationFixture(throwOnResume: false);
        var controller = new TuiController(new TuiPresenter(fixture.Dispatcher, fixture.Projections));
        await controller.OpenAsync("TUI");
        await controller.SelectSolutionAsync(fixture.WorkspaceId, fixture.SolutionPath);
        fixture.Projections.Session = fixture.CreatePlanProjection();
        _ = await controller.SubmitAsync("request");
        var staged = await controller.ApproveActivePlanAndProposeMutationSetAsync();
        Assert.NotNull(staged);

        _ = await controller.RollbackMutationSetAsync(fixture.MutationSetId);
        Assert.True(await controller.CancelActiveRunAsync());
        var nextRun = await controller.SubmitAsync("next request");

        Assert.Equal(fixture.RunId, nextRun);
    }

    /// <summary>A review-ready mutation can be loaded without waiting for execution-checkpoint hydration.</summary>
    [Fact]
    public static async Task TuiController_LoadMutationReview_AllowsAutoApprovedPlanMutationReview()
    {
        var fixture = new PostApplyValidationFixture(throwOnResume: false);
        var controller = new TuiController(new TuiPresenter(fixture.Dispatcher, fixture.Projections));
        await controller.OpenAsync("TUI");
        await controller.SelectSolutionAsync(fixture.WorkspaceId, fixture.SolutionPath);
        var projection = fixture.CreatePlanProjection();
        var plan = projection.Plan
            ?? throw new InvalidOperationException("The test fixture did not create a plan projection.");
        fixture.Projections.Session = projection with
        {
            Plan = plan with { Status = PlanReviewStatus.Approved },
        };
        var runId = await controller.SubmitAsync("request");

        var staged = await controller.LoadMutationReviewAsync(fixture.MutationSetId);
        var result = await controller.CommitMutationSetAsync(
            fixture.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = fixture.ApprovalId,
            });

        Assert.Equal(fixture.RunId, runId);
        Assert.Equal(fixture.MutationSetId, staged.MutationSet.MutationSetId);
        Assert.Equal(fixture.MutationSetId, result.MutationSetId);
        Assert.Equal(fixture.RunId, controller.BackgroundValidationRunId);
    }

    /// <summary>Interrupted post-apply validation keeps the controller guard so new work cannot start.</summary>
    [Fact]
    public static async Task TuiController_PostApplyValidationFailure_RetainsRunGuard()
    {
        var fixture = new PostApplyValidationFixture(throwOnResume: true);
        var controller = new TuiController(new TuiPresenter(fixture.Dispatcher, fixture.Projections));
        await StageAndApplyMutationAsync(controller, fixture);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.ResumeAppliedMutationValidationAsync(fixture.RunId));

        Assert.Equal(fixture.RunId, controller.BackgroundValidationRunId);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.SubmitAsync("new request"));
        Assert.Contains("Post-apply validation is still running", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Interrupted post-apply validation can be retried through the retained run identity.</summary>
    [Fact]
    public static async Task TuiController_PostApplyValidationFailure_CanRetryRetainedRun()
    {
        var fixture = new PostApplyValidationFixture(throwOnResume: true);
        var controller = new TuiController(new TuiPresenter(fixture.Dispatcher, fixture.Projections));
        await StageAndApplyMutationAsync(controller, fixture);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.ResumeAppliedMutationValidationAsync(fixture.RunId));
        fixture.Dispatcher.ThrowOnResume = false;

        var continuation = await controller.ResumeAppliedMutationValidationAsync(
            fixture.RunId);

        Assert.Equal(ExecutionCheckpointPhase.Completed, continuation.Phase);
        Assert.Null(controller.BackgroundValidationRunId);
    }

    /// <summary>Terminal post-apply validation releases the controller guard.</summary>
    [Fact]
    public static async Task TuiController_PostApplyValidationCompleted_ReleasesRunGuard()
    {
        var fixture = new PostApplyValidationFixture(throwOnResume: false);
        var controller = new TuiController(new TuiPresenter(fixture.Dispatcher, fixture.Projections));
        await StageAndApplyMutationAsync(controller, fixture);

        var continuation = await controller.ResumeAppliedMutationValidationAsync(fixture.RunId);

        Assert.Equal(ExecutionCheckpointPhase.Completed, continuation.Phase);
        Assert.Null(controller.BackgroundValidationRunId);
    }

    /// <summary>Post-apply validation failure is not presented as successful completion.</summary>
    [Fact]
    public static void ConversationalShell_PostApplyValidationFailure_IsReportedSeparately()
    {
        (var failedMessage, var failedRole) = ConversationalShell.FormatPostApplyValidationResult(
            ExecutionCheckpointPhase.Failed,
            " (1.7s)");
        (var completedMessage, var completedRole) = ConversationalShell.FormatPostApplyValidationResult(
            ExecutionCheckpointPhase.Completed,
            " (1.7s)");

        Assert.Equal("Validation failed (1.7s); mutation was not accepted.\n", failedMessage);
        Assert.Equal(TuiTextRole.Error, failedRole);
        Assert.DoesNotContain("completed", failedMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Validation completed (1.7s).\n", completedMessage);
        Assert.Equal(TuiTextRole.Status, completedRole);
    }

    /// <summary>Post-apply validation start remains visible when no semantic-check activity will follow.</summary>
    [Fact]
    public static void ConversationalShell_PostApplyValidationStart_RendersWhenSemanticStageIsDisabled()
    {
        Assert.Null(ConversationalShell.FormatPostApplyValidationStartSegments(
        [
            MutationValidationStage.Semantic,
            MutationValidationStage.Compile,
        ]));

        var segments = ConversationalShell.FormatPostApplyValidationStartSegments(
        [
            MutationValidationStage.Compile,
            MutationValidationStage.Diagnostics,
            MutationValidationStage.Tests,
        ]) ?? throw new InvalidOperationException("Non-semantic validation should render start status.");
        var start = string.Concat(segments.Select(segment => segment.Text));

        Assert.Equal(
            " MUTATION: Validating applied mutation"
                + Environment.NewLine
                + " \u2514 Stages: compile, diagnostics, tests"
                + Environment.NewLine,
            start);
        Assert.Collection(
            segments,
            segment =>
            {
                Assert.Equal(" MUTATION: Validating applied mutation" + Environment.NewLine, segment.Text);
                Assert.Equal(TuiTextRole.Status, segment.Role);
            },
            segment =>
            {
                Assert.Equal(" \u2514 Stages: compile, diagnostics, tests" + Environment.NewLine, segment.Text);
                Assert.Equal(TuiTextRole.Muted, segment.Role);
            });
    }

    /// <summary>Post-apply correction review restores the owning active run for discard cancellation.</summary>
    [Fact]
    public static async Task TuiController_PostApplyCorrectionReview_RestoresActiveRunForDiscard()
    {
        var fixture = new PostApplyValidationFixture(throwOnResume: false)
        {
            ResumePhase = ExecutionCheckpointPhase.MutationApprovalPending,
        };
        var controller = new TuiController(new TuiPresenter(fixture.Dispatcher, fixture.Projections));
        await StageAndApplyMutationAsync(controller, fixture);

        var continuation = await controller.ResumeAppliedMutationValidationAsync(fixture.RunId);
        _ = await controller.RollbackMutationSetAsync(fixture.MutationSetId);

        Assert.Equal(ExecutionCheckpointPhase.MutationApprovalPending, continuation.Phase);
        Assert.Equal(fixture.RunId, controller.ActiveRunId);
        Assert.Null(controller.BackgroundValidationRunId);
        Assert.True(await controller.CancelActiveRunAsync());
    }

    /// <summary>Repository and semantic responses append through one boundary without clearing prior turns.</summary>
    [Fact]
    public static void ConversationTranscript_RepositoryResponses_PreserveExistingConversation()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var transcript = new ConversationTranscript(string.Empty);
        Assert.False(transcript.Apply(new TaskIntentRecorded(sessionId, occurredAt, "hello")));
        Assert.True(transcript.Apply(new ModelOutputObserved(sessionId, occurredAt, "existing response")));
        Assert.True(transcript.Apply(new RunCompleted(sessionId, occurredAt, RunId.New(), true)));
        var existingConversation = transcript.Text;

        Assert.True(transcript.Apply(new RepositoryOpened(
            sessionId,
            occurredAt,
            "C:\\repo",
            WorkspaceId.New(),
            RepositoryTrustLevel.TrustedRead)));
        Assert.True(transcript.Apply(new SolutionLoaded(
            sessionId,
            occurredAt,
            "C:\\repo\\src\\Repo.sln",
            WorkspaceId.New(),
            ["net10.0"])));
        Assert.True(transcript.Apply(new SemanticConfidenceChanged(
            sessionId,
            occurredAt,
            nameof(SemanticConfidenceLevel.TextOnly))));
        Assert.True(transcript.Apply(new SemanticLoadCompleted(
            sessionId,
            occurredAt,
            WorkspaceId.New(),
            nameof(SemanticConfidenceLevel.None))));

        Assert.StartsWith(existingConversation, transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Repository opened.", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Solution: C:\\repo\\src\\Repo.sln", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Semantic confidence: TextOnly", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Semantic confidence: Unavailable", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>A resolved fallback visibly replaces the misleading selected-model presentation.</summary>
    [Fact]
    public static void ConversationTranscript_ModelFallbackSelected_RendersActiveModelWarning()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var transcript = new ConversationTranscript(string.Empty);
        Assert.False(transcript.Apply(new TaskIntentRecorded(sessionId, occurredAt, "hello")));

        Assert.True(transcript.Apply(new ModelFallbackSelected(
            sessionId,
            occurredAt,
            RunId.New(),
            ModelProfileId.New(),
            ModelProfileId.New(),
            "fallback-provider",
            "Fallback Model",
            Persisted: true)));

        Assert.Contains(
            "Switched to fallback model 'Fallback Model' (fallback-provider); it is now the active model.",
            transcript.Text,
            StringComparison.Ordinal);
    }

    /// <summary>The inline shell starts without a full-screen driver and exits through its command loop.</summary>
    [Fact]
    public static async Task ConversationalShell_Quit_UsesNativeTerminalSurface()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("_____ _", surface.Output, StringComparison.Ordinal);
        Assert.NotEmpty(surface.Writes);
        Assert.EndsWith(
            "Forge better code, not slop.\n\n",
            surface.Writes[0].ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains("Current status", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Model: Test model", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Repository: Not open", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Trust: Not granted", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Session status: Composer-adjacent", surface.Output, StringComparison.Ordinal);
        Assert.Single(surface.SessionStatuses);
        Assert.Equal(["status", "read"], surface.Operations);
    }

    /// <summary>The inline shell routes lifecycle-hook management locally through /hooks.</summary>
    [Fact]
    public static async Task ConversationalShell_HooksCommand_ListsConfiguredHandlers()
    {
        var hookStore = new InMemoryHookStore();
        var descriptor = HookDescriptorValidator.Normalize([
            new HookHandlerDescriptor
            {
                Identity = new HookHandlerIdentity(
                    new HookHandlerId("shell-hook"),
                    "1.0.0",
                    new HookConfigurationDigest(string.Empty)),
                Scope = HookHandlerScope.Machine,
                AdapterKind = HookAdapterKind.Mcp,
                Enabled = true,
                HookPoints = [HookPoint.BeforeToolInvocation],
                Target = "profile/tool",
            },
        ])[0];
        await using var coordinator = new HookCoordinator(
            [descriptor],
            [],
            new HookPolicyEvaluator(hookStore, []),
            hookStore,
            new SecretOutputSanitizer(),
            NullLogger<HookCoordinator>.Instance);
        var hookApplication = new HookManagementApplication(coordinator, hookStore);
        await using var harness = await SessionHarness.CreateAsync(
            new ScriptedSession(),
            additionalHandlers: [hookApplication]);
        var surface = new FakeConsoleSurface(["/hooks list", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("[enabled] shell-hook", surface.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown command", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>The inline shell parses MCP capability-kind filters and dispatches them through Core contracts.</summary>
    [Fact]
    public static async Task ConversationalShell_McpCapabilities_DispatchesSharedManagerRequest()
    {
        var mcpHandler = new RecordingMcpManagementHandler();
        await using var harness = await SessionHarness.CreateAsync(
            new ScriptedSession(),
            additionalHandlers: [mcpHandler]);
        var surface = new FakeConsoleSurface(["/mcp capabilities fixture tools", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var request = Assert.Single(mcpHandler.Requests);
        Assert.Equal(McpManagementAction.ListCapabilities, request.Action);
        Assert.Equal("fixture", request.ProfileId);
        Assert.Equal(McpManagedCapabilityKind.Tool, request.CapabilityKind);
        Assert.Contains("0 active MCP capability", surface.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown command", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>The inline MCP parser preserves quoted resource and prompt argument values.</summary>
    [Fact]
    public static async Task ConversationalShell_McpPrompt_PreservesQuotedArgumentValue()
    {
        var mcpHandler = new RecordingMcpManagementHandler();
        await using var harness = await SessionHarness.CreateAsync(
            new ScriptedSession(),
            additionalHandlers: [mcpHandler]);
        var surface = new FakeConsoleSurface(
            ["/mcp prompt get fixture fixture:prompt:review name=\"review this file\"", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var request = Assert.Single(mcpHandler.Requests);
        Assert.Equal(McpManagementAction.GetPrompt, request.Action);
        Assert.Equal("review this file", request.Arguments["name"]);
        Assert.DoesNotContain("Unknown command", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>Disabling the status surface leaves the composer operational and reports the selected mode once.</summary>
    [Fact]
    public static async Task ConversationalShell_DisabledSessionStatus_DoesNotRenderRow()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            showSessionStatus: false);

        await shell.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(surface.SessionStatuses);
        Assert.Contains(
            "Session status: Disabled by tui:footer:enabled",
            surface.Output,
            StringComparison.Ordinal);
    }

    /// <summary>A surface that cannot safely display status suppresses it without affecting input.</summary>
    [Fact]
    public static async Task ConversationalShell_SuppressedSessionStatus_StillReadsComposer()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/quit"], suppressSessionStatus: true);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(surface.SessionStatuses);
        Assert.Contains("Current status", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>Expected Git availability failures render an unavailable branch without escaping status projection.</summary>
    /// <param name="failureKind">Failure emitted by the Git query boundary.</param>
    [Theory]
    [InlineData("unauthorized")]
    [InlineData("timeout")]
    [InlineData("removed")]
    public static async Task ConversationalShell_CurrentBranchUnavailable_ReturnsNull(string failureKind)
    {
        Exception failure = failureKind switch
        {
            "unauthorized" => new UnauthorizedAccessException("Nested worktree path."),
            "timeout" => new TimeoutException("Git timed out."),
            "removed" => new DirectoryNotFoundException("Repository was removed."),
            _ => throw new ArgumentOutOfRangeException(nameof(failureKind)),
        };

        var branch = await ConversationalShell.ResolveCurrentBranchAsync(
            new FailingGitQueryService(failure),
            "repository",
            repositoryIsOpen: true,
            TestContext.Current.CancellationToken);

        Assert.Null(branch);
    }

    /// <summary>Submitted prompts retain raw state while one semantic answer is emitted into terminal scrollback.</summary>
    [Fact]
    public static async Task ConversationalShell_Submission_RendersOneCopyableSemanticAnswer()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession
        {
            Turns = [new ScriptedTurn { Text = "amber reply", Usage = new ModelUsage(11, 4) }],
        });
        var surface = new FakeConsoleSurface(["hi", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            sessionUsage: harness.Usage);

        await shell.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain("You: hi", surface.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Threadsmith: amber reply", surface.Output, StringComparison.Ordinal);
        Assert.Contains("amber reply", surface.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(
            surface.Segments,
            segment => segment.Role == TuiTextRole.UserPrompt);
        var defaultText = string.Concat(
            surface.Segments
                .Where(segment => segment.Role == TuiTextRole.Default)
                .Select(segment => segment.Text));
        Assert.Contains("amber reply", defaultText, StringComparison.Ordinal);
        var markdown = Assert.Single(surface.OutputItems.OfType<TuiMarkdownOutput>());
        var paragraph = Assert.IsType<TuiMarkdownParagraph>(Assert.Single(markdown.Document.Blocks));
        Assert.Equal("amber reply", string.Concat(paragraph.Spans.Select(span => span.Text)));
        var thinkingEnded = surface.Lifecycle.ToList().IndexOf("activity-end:THINKING");
        var answerWritten = surface.Lifecycle.ToList().IndexOf("output:markdown");
        Assert.True(thinkingEnded >= 0 && thinkingEnded < answerWritten);
        Assert.Contains(harness.Events, item => item is RunCompleted { Succeeded: true });
        Assert.Contains(
            surface.SessionStatuses,
            status => status.Contains("tokens 15", StringComparison.Ordinal));
    }

    /// <summary>A large multiline paste reaches the command boundary as one unchanged submission.</summary>
    [Fact]
    public static async Task ConversationalShell_LargeMultilinePaste_IsNotReplayedPerCharacter()
    {
        var pastedText = " leading line\n" + new string('x', 100_000) + "\ntrailing line ";
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession
        {
            Turns = [new ScriptedTurn { Text = "received" }],
        });
        var surface = new FakeConsoleSurface([pastedText, "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var intent = Assert.Single(harness.Events.OfType<TaskIntentRecorded>());
        Assert.Equal(pastedText, intent.Intent);
        Assert.Contains("received", surface.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Threadsmith: received", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>An unknown slash command is rejected locally instead of reaching the model.</summary>
    [Fact]
    public static async Task ConversationalShell_UnknownCommand_IsNotSubmitted()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/destructive-mystery", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("Unknown command", surface.Output, StringComparison.Ordinal);
        Assert.Empty(harness.Events.OfType<TaskIntentRecorded>());
    }

    /// <summary>An overlong fetch authorization chain is reported locally without escaping the shell boundary.</summary>
    [Fact]
    public static async Task ConversationalShell_OverlongFetchAuthorizationChain_IsRejectedLocally()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface([]);
        var authority = new WebFetchAuthorizationAuthority(new WebFetchOptions { MaximumRedirects = 1 });
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            webFetchAuthorization: authority);

        await shell.HandleFetchAuthorizationCommandAsync(
            "/fetch-authorize https://one.example https://two.example https://three.example",
            Path.GetTempPath(),
            SessionId.New(),
            CancellationToken.None);

        Assert.Contains(
            "accepts at most 2 URLs under current repository limits",
            surface.Output,
            StringComparison.Ordinal);
        Assert.False(authority.GetStatus().Active);
    }

    /// <summary>The trust host command is discoverable and fails closed without an open repository.</summary>
    [Fact]
    public static async Task ConversationalShell_TrustWithoutRepository_IsRejectedLocally()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/help", "/trust build", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("/trust [inspect|read|build|mutation]", surface.Output, StringComparison.Ordinal);
        Assert.Contains("No repository is open", surface.Output, StringComparison.Ordinal);
        Assert.Empty(harness.Events.OfType<TaskIntentRecorded>());
    }

    /// <summary>The UI dispatcher processes a flood in bounded redraw batches without losing events.</summary>
    [Fact]
    public static async Task ConversationalShell_ReasoningNoArg_ShowsModelAndLevels()
    {
        var profileId = new ModelProfileId(Guid.NewGuid());
        var profile = CreateReasoningProfile(
            profileId,
            "Qwen3",
            [ReasoningLevel.None, ReasoningLevel.Low, ReasoningLevel.Medium, ReasoningLevel.High]);
        var catalog = new ConfiguredModelCatalog([profile], enforceHttps: false);
        var preferences = new SessionModelPreferences();
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/reasoning", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            catalog,
            profileId,
            preferences);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("Model: Qwen3", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Reasoning levels: none, low, medium, high", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Current: none", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>The command uses the effective profile identity even without a separate startup preference.</summary>
    [Fact]
    public static async Task ConversationalShell_ReasoningNoArg_UsesSharedEffectiveProfileIdentity()
    {
        var profileId = new ModelProfileId(Guid.NewGuid());
        var profile = CreateReasoningProfile(
            profileId,
            "Policy fallback",
            [ReasoningLevel.None, ReasoningLevel.Medium]);
        var catalog = new ConfiguredModelCatalog([profile], enforceHttps: false);
        var preferences = new SessionModelPreferences(profileId, ReasoningLevel.Medium);
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/reasoning", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            catalog,
            activeProfileId: null,
            sessionPreferences: preferences);

        await shell.RunAsync(modelStatus: "Policy fallback").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("Model: Policy fallback", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Current: medium", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>The command follows a workload-driven profile transition instead of the startup profile.</summary>
    [Fact]
    public static async Task ConversationalShell_ReasoningNoArg_FollowsRuntimeProfileTransition()
    {
        var startupId = new ModelProfileId(Guid.NewGuid());
        var runtimeId = new ModelProfileId(Guid.NewGuid());
        var startup = CreateReasoningProfile(startupId, "Startup", [ReasoningLevel.None, ReasoningLevel.High]);
        var runtime = CreateReasoningProfile(runtimeId, "Runtime", [ReasoningLevel.None, ReasoningLevel.Low]);
        var catalog = new ConfiguredModelCatalog([startup, runtime], enforceHttps: false);
        var preferences = new SessionModelPreferences(startupId, ReasoningLevel.High);
        Assert.Equal(ReasoningLevel.None, preferences.ResolveFor(runtimeId));
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/reasoning", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            catalog,
            startupId,
            preferences);

        await shell.RunAsync(modelStatus: "Startup").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("Model: Runtime", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Reasoning levels: none, low", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Current: none", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>The /reasoning command with a valid level sets the session preference.</summary>
    [Fact]
    public static async Task ConversationalShell_ReasoningWithLevel_SetsSessionPreference()
    {
        var profileId = new ModelProfileId(Guid.NewGuid());
        var profile = CreateReasoningProfile(
            profileId,
            "Qwen3",
            [ReasoningLevel.None, ReasoningLevel.Low, ReasoningLevel.Medium, ReasoningLevel.High]);
        var catalog = new ConfiguredModelCatalog([profile], enforceHttps: false);
        var preferences = new SessionModelPreferences();
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/reasoning medium", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            catalog,
            profileId,
            preferences);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("Reasoning set to medium for Qwen3.", surface.Output, StringComparison.Ordinal);
        Assert.Equal(ReasoningLevel.Medium, preferences.Reasoning);
        Assert.Equal(profileId, preferences.CurrentProfileId);
    }

    /// <summary>The /reasoning command rejects an unknown level string.</summary>
    [Fact]
    public static async Task ConversationalShell_ReasoningUnknownLevel_ShowsError()
    {
        var profileId = new ModelProfileId(Guid.NewGuid());
        var profile = CreateReasoningProfile(
            profileId,
            "Qwen3",
            [ReasoningLevel.None, ReasoningLevel.Medium]);
        var catalog = new ConfiguredModelCatalog([profile], enforceHttps: false);
        var preferences = new SessionModelPreferences();
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/reasoning bogus", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            catalog,
            profileId,
            preferences);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("Unknown reasoning level 'bogus'", surface.Output, StringComparison.Ordinal);
        Assert.Equal(ReasoningLevel.None, preferences.Reasoning);
    }

    /// <summary>The /reasoning command rejects a level not supported by the active model.</summary>
    [Fact]
    public static async Task ConversationalShell_ReasoningUnsupportedLevel_ShowsError()
    {
        var profileId = new ModelProfileId(Guid.NewGuid());
        var profile = CreateReasoningProfile(
            profileId,
            "Qwen3",
            [ReasoningLevel.None, ReasoningLevel.Medium]);
        var catalog = new ConfiguredModelCatalog([profile], enforceHttps: false);
        var preferences = new SessionModelPreferences(profileId, ReasoningLevel.Medium);
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/reasoning high", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            catalog,
            profileId,
            preferences);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("does not support reasoning level 'high'", surface.Output, StringComparison.Ordinal);
        Assert.Equal(ReasoningLevel.Medium, preferences.Reasoning);
    }

    /// <summary>The /reasoning command reports no model when no catalog is configured.</summary>
    [Fact]
    public static async Task ConversationalShell_ReasoningWithoutModel_ShowsNoModelConfigured()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/reasoning", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync(modelStatus: "Scripted demo (offline)").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("No model is configured.", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>The code_explore output commands route through host-owned per-session state.</summary>
    [Fact]
    public static async Task ConversationalShell_CodeExploreOutputCommands_UpdateHostSessionOptions()
    {
        var options = new CodeExploreOutputOptions();
        await using var harness = await SessionHarness.CreateAsync(
            new ScriptedSession(),
            additionalHandlers: [options]);
        var surface = new FakeConsoleSurface([
            "/code_explore_output markdown",
            "/code_explore_inspect on",
            "/quit",
        ]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            codeExploreOutputOptions: options);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(5));

        var sessionCreated = Assert.Single(harness.Events.OfType<SessionCreated>());
        var snapshot = options.GetSnapshot(sessionCreated.SessionId);
        Assert.Equal(CodeExploreOutputFormat.Markdown, snapshot.OutputFormat);
        Assert.True(snapshot.InspectCodeExploreOutput);
        Assert.Equal(CodeExploreOutputFormat.Markdown, options.GetOutputFormat(SessionId.New()));
        Assert.Contains(
            "code_explore output format is markdown for this session.",
            surface.Output,
            StringComparison.Ordinal);
        Assert.Contains(
            "code_explore output inspection is on for this session.",
            surface.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Unknown command", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>The /help text aligns descriptions and wraps only commands that exceed the command column.</summary>
    [Fact]
    public static async Task ConversationalShell_HelpAlignsCommandDescriptions()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/help", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(5));

        const string descriptionIndent = "                                          ";
        Assert.Contains(
            "/open [path]                              Open a repository and choose trust",
            surface.Output,
            StringComparison.Ordinal);
        Assert.Contains(
            "/reasoning [level]                        Set reasoning effort for the active model",
            surface.Output,
            StringComparison.Ordinal);
        Assert.Contains(
            "/code_explore_output {structured|markdown}"
                + Environment.NewLine
                + descriptionIndent
                + "Set code_explore output format for this session",
            surface.Output,
            StringComparison.Ordinal);
        Assert.Contains(
            "/code_explore_inspect {on|off}            Show future code_explore outputs in the tool block for this session",
            surface.Output,
            StringComparison.Ordinal);
        Assert.Contains(
            "/skills [list|inspect|verify|enable|disable|pin|use|status|cancel]"
                + Environment.NewLine
                + descriptionIndent
                + "Govern skills",
            surface.Output,
            StringComparison.Ordinal);
        Assert.Contains(
            "/plan-policy [name|current]               Select or report plan approval policy",
            surface.Output,
            StringComparison.Ordinal);
        Assert.Contains("Ctrl+T", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>The /plan-policy command uses the shared command boundary and reports the selected policy.</summary>
    [Fact]
    public static async Task ConversationalShell_PlanPolicyCommand_UsesCommandBoundary()
    {
        var planPolicy = new RecordingPlanApprovalPolicyHandler();
        await using var harness = await SessionHarness.CreateAsync(
            new ScriptedSession(),
            additionalHandlers: [planPolicy]);
        var surface = new FakeConsoleSurface(["/plan-policy ReviewRisky", "/plan-policy current", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            planApprovalPolicy: planPolicy);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(PlanApprovalPolicy.ReviewRisky, planPolicy.CurrentPolicy);
        Assert.Equal(["repository"], planPolicy.Scopes);
        Assert.Contains("Plan policy changed to ReviewRisky.", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Current plan policy: ReviewRisky.", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>Ctrl+T toggles thinking streaming only from an empty composer so typed input is preserved.</summary>
    [Fact]
    public static async Task PrettyPromptConsoleSurface_ControlT_MapsToThinkingToggleOnEmptyInput()
    {
        IPromptCallbacks callbacks = new PrettyPromptConsoleSurface.ThinkingPromptCallbacks();
        var key = new ConsoleKeyInfo(
            '\u0014',
            ConsoleKey.T,
            shift: false,
            alt: false,
            control: true);

        Assert.True(callbacks.TryGetKeyPressCallbacks(key, out var callback));
        Assert.NotNull(callback);
        var emptyResult = await callback(string.Empty, 0, CancellationToken.None);
        var typedResult = await callback("draft", 5, CancellationToken.None);

        Assert.NotNull(emptyResult);
        Assert.Null(typedResult);
    }

    /// <summary>Session usage replaces duplicate request observations and accumulates distinct rounds.</summary>
    [Fact]
    public static void SessionUsageProjection_RequestIdentity_DeduplicatesUsage()
    {
        var projection = new SessionUsageProjection();
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var firstRequest = new ModelRequestUsageId(runId, "conversation", 0, Guid.NewGuid());

        projection.Observe(sessionId, firstRequest, new ModelUsage(100, 20));
        projection.Observe(sessionId, firstRequest, new ModelUsage(110, 25, IsEstimate: true));
        projection.Observe(
            sessionId,
            new ModelRequestUsageId(runId, "conversation", 1, Guid.NewGuid()),
            new ModelUsage(50, 10));

        var snapshot = projection.GetSnapshot(sessionId);
        Assert.Equal(160, snapshot.InputTokens);
        Assert.Equal(35, snapshot.OutputTokens);
        Assert.Equal(195, snapshot.TotalTokens);
        Assert.True(snapshot.IsEstimate);
    }

    /// <summary>The status formatter omits low-priority segments and never exceeds the available width.</summary>
    [Fact]
    public static void TuiSessionStatusFormatter_NarrowWidth_PreservesPriorityWithoutWrapping()
    {
        var status = new TuiSessionStatus(
            "C:\\source\\repos\\a-very-long-working-folder",
            "a-very-long-repository-name",
            "Configured model with a long display name/model-id",
            ReasoningLevel.High,
            12_000,
            32_000,
            new SessionUsageSnapshot(8_000, 2_000, true));

        var rendered = TuiSessionStatusFormatter.Format(status, 80, " | ");

        Assert.NotEmpty(rendered);
        Assert.DoesNotContain('\n', rendered);
        Assert.True(UnicodeWidth.GetWidth(rendered.AsSpan()) <= 80);
        Assert.Contains("model ", rendered, StringComparison.Ordinal);
        Assert.Contains("(high)", rendered, StringComparison.Ordinal);
        Assert.Contains("ctx ~12k/32k 38%", rendered, StringComparison.Ordinal);
        Assert.Contains("tokens ~10k", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("folder ", rendered, StringComparison.Ordinal);
    }

    /// <summary>Session usage saturates rather than overflowing cumulative counters.</summary>
    [Fact]
    public static void SessionUsageProjection_LargeUsage_SaturatesTotals()
    {
        var projection = new SessionUsageProjection();
        var sessionId = SessionId.New();
        var runId = RunId.New();

        projection.Observe(
            sessionId,
            new ModelRequestUsageId(runId, "conversation", 0, Guid.NewGuid()),
            new ModelUsage(long.MaxValue, long.MaxValue));
        projection.Observe(
            sessionId,
            new ModelRequestUsageId(runId, "conversation", 1, Guid.NewGuid()),
            new ModelUsage(1, 1));

        var snapshot = projection.GetSnapshot(sessionId);
        Assert.Equal(long.MaxValue, snapshot.InputTokens);
        Assert.Equal(long.MaxValue, snapshot.OutputTokens);
        Assert.Equal(long.MaxValue, snapshot.TotalTokens);
    }

    /// <summary>The status factory selects the stricter model and governed context limit.</summary>
    [Fact]
    public static void TuiSessionStatusFactory_ConfiguredProfile_UsesEffectiveContextLimit()
    {
        var profile = CreateReasoningProfile(
            new ModelProfileId(Guid.NewGuid()),
            "Status model",
            [ReasoningLevel.None, ReasoningLevel.High]) with
        {
            ContextWindow = 16_000,
        };
        var inspection = new ContextInspectionProjection
        {
            RunId = RunId.New(),
            EstimatedTokens = 8_000,
            TokenBudget = 32_000,
        };

        var status = TuiSessionStatusFactory.Create(
            "C:\\source",
            "Threadsmith",
            "fallback",
            profile,
            ReasoningLevel.High,
            inspection,
            new SessionUsageSnapshot(1, 2, false));

        Assert.Equal("Status model", status.Model);
        Assert.Equal(8_000, status.ContextTokens);
        Assert.Equal(16_000, status.ContextLimit);
    }

    /// <summary>Unknown context remains explicit rather than presenting a fabricated percentage.</summary>
    [Fact]
    public static void TuiSessionStatusFormatter_UnknownContext_ShowsUnknownMarker()
    {
        var status = new TuiSessionStatus(
            "C:\\source",
            "Threadsmith",
            "model",
            ReasoningLevel.None,
            null,
            null,
            new SessionUsageSnapshot(0, 0, false));

        var rendered = TuiSessionStatusFormatter.Format(status, 80, " | ");

        Assert.Contains("ctx --", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain('%', rendered);
    }

    /// <summary>Responsive layouts remain single-line and bounded at the Plan-26 compatibility widths.</summary>
    /// <param name="width">Available terminal width.</param>
    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    [InlineData(200)]
    public static void TuiSessionStatusFormatter_CompatibilityWidths_NeverWrapOrOverflow(int width)
    {
        var status = new TuiSessionStatus(
            "C:\\work\\Threadsmith\\src",
            "Threadsmith",
            "Long configured model/model-id",
            ReasoningLevel.Medium,
            12_000,
            32_000,
            new SessionUsageSnapshot(8_000, 2_000, false));

        var rendered = TuiSessionStatusFormatter.Format(status, width, " | ");

        Assert.DoesNotContain('\n', rendered);
        Assert.True(string.IsNullOrEmpty(rendered) || UnicodeWidth.GetWidth(rendered.AsSpan()) == width);
        if (!string.IsNullOrEmpty(rendered))
        {
            Assert.Contains("ctx ", rendered, StringComparison.Ordinal);
            Assert.Contains("tokens ", rendered, StringComparison.Ordinal);
        }
    }

    /// <summary>Long paths use bounded end-biased abbreviation when the full layout fits.</summary>
    [Fact]
    public static void TuiSessionStatusFormatter_LongPath_AbbreviatesSafely()
    {
        var status = new TuiSessionStatus(
            "C:\\a-very-long-root-name\\a-very-long-parent-name\\Threadsmith\\src",
            "Threadsmith",
            "model",
            ReasoningLevel.None,
            1,
            100,
            new SessionUsageSnapshot(1, 1, false));

        var rendered = TuiSessionStatusFormatter.Format(status, 200, " | ");

        Assert.Contains("folder C:/…/Threadsmith/src", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("a-very-long-root-name", rendered, StringComparison.Ordinal);
    }

    /// <summary>The status formatter measures and truncates wide Unicode values in terminal cells.</summary>
    [Fact]
    public static void TuiSessionStatusFormatter_WideUnicode_DoesNotExceedTerminalWidth()
    {
        var status = new TuiSessionStatus(
            "C:\\source\\リポジトリ",
            "工具箱",
            "模型模型模型模型模型",
            ReasoningLevel.High,
            12_000,
            32_000,
            new SessionUsageSnapshot(8_000, 2_000, true));

        var rendered = TuiSessionStatusFormatter.Format(status, 60, "｜");

        Assert.NotEmpty(rendered);
        Assert.Equal(60, UnicodeWidth.GetWidth(rendered.AsSpan()));
        Assert.Contains("(high)", rendered, StringComparison.Ordinal);
        Assert.Contains('…', rendered);
    }

    /// <summary>The compiled system theme inherits both terminal colors for every semantic role.</summary>
    [Fact]
    public static void TuiTheme_System_InheritsTerminalColorsForEveryRole()
    {
        var resolver = new TuiThemeResolver(TuiTheme.System);

        foreach (var role in Enum.GetValues<TuiTextRole>())
        {
            var style = resolver.Resolve(role);
            Assert.Null(style.Foreground);
            Assert.Null(style.Background);
        }

        Assert.Equal(
            TuiTextDecoration.Invert,
            resolver.Resolve(TuiTextRole.SessionStatus).Decorations);
    }

    /// <summary>Partial role styles independently inherit unspecified values from the default role.</summary>
    [Fact]
    public static void TuiThemeResolver_PartialRole_InheritsMissingValuesFromDefault()
    {
        var theme = new TuiTheme(
            "test",
            [
                new(TuiTextRole.Default, new TuiTextStyle(
                    TuiColor.Parse("white"),
                    TuiColor.Parse("#102030"))),
                new(TuiTextRole.Error, new TuiTextStyle(TuiColor.Parse("red"))),
            ]);
        var resolver = new TuiThemeResolver(theme);

        var result = resolver.Resolve(TuiTextRole.Error);

        Assert.Equal("red", result.Foreground?.Value);
        Assert.Equal("#102030", result.Background?.Value);
    }

    /// <summary>An explicit empty decoration set overrides inherited decorations.</summary>
    [Fact]
    public static void TuiThemeResolver_ExplicitNoDecorations_OverridesDefault()
    {
        var theme = new TuiTheme(
            "decorations",
            [
                new(TuiTextRole.Default, new TuiTextStyle(Decorations: TuiTextDecoration.Bold)),
                new(TuiTextRole.Error, new TuiTextStyle(Decorations: TuiTextDecoration.None)),
            ]);
        var resolver = new TuiThemeResolver(theme);

        Assert.Equal(TuiTextDecoration.Bold, resolver.Resolve(TuiTextRole.Status).Decorations);
        Assert.Equal(TuiTextDecoration.None, resolver.Resolve(TuiTextRole.Error).Decorations);
    }

    /// <summary>NO_COLOR-style suppression removes presentation without changing semantic text ownership.</summary>
    [Fact]
    public static void TuiThemeResolver_Suppressed_RemovesColorsAndDecorations()
    {
        var theme = new TuiTheme(
            "test",
            [
                new(TuiTextRole.Default, new TuiTextStyle(
                    TuiColor.Parse("cyan"),
                    TuiColor.Parse("black"),
                    TuiTextDecoration.Bold | TuiTextDecoration.Underline)),
            ]);
        var resolver = new TuiThemeResolver(theme, suppressStyles: true);

        var result = resolver.Resolve(TuiTextRole.Hyperlink);

        Assert.Equal(new TuiTextStyle(), result);
        Assert.Equal(new TuiTextStyle(), resolver.Resolve(TuiTextRole.SessionStatus));
    }

    /// <summary>Untrusted colors, ids, duplicate roles, and unknown decoration bits fail locally.</summary>
    [Fact]
    public static void TuiTheme_InvalidPresentationData_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => TuiColor.Parse("\u001b[31m"));
        Assert.Throws<ArgumentException>(() => TuiColor.Parse("#12345G"));
        Assert.Throws<ArgumentException>(() => new TuiTheme("bad id", []));
        Assert.Throws<ArgumentException>(() => new TuiTheme(
            "duplicate",
            [
                new(TuiTextRole.Default, new TuiTextStyle()),
                new(TuiTextRole.Default, new TuiTextStyle()),
            ]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TuiTheme(
            "decoration",
            [new(TuiTextRole.Default, new TuiTextStyle(Decorations: (TuiTextDecoration)64))]));
    }

    /// <summary>Every semantic role supports independent foreground, background, and validated decorations.</summary>
    [Fact]
    public static void TuiTheme_AllRoles_ResolveIndependentStyleValues()
    {
        KeyValuePair<TuiTextRole, TuiTextStyle>[] styles =
        [
            .. Enum.GetValues<TuiTextRole>().Select(role => new KeyValuePair<TuiTextRole, TuiTextStyle>(
                role,
                new TuiTextStyle(
                    TuiColor.Parse("#102030"),
                    TuiColor.Parse("brightwhite"),
                    TuiTextDecoration.Bold | TuiTextDecoration.Underline))),
        ];
        var resolver = new TuiThemeResolver(new TuiTheme("complete", styles));

        Assert.All(Enum.GetValues<TuiTextRole>(), role =>
        {
            var resolved = resolver.Resolve(role);
            Assert.Equal("#102030", resolved.Foreground?.Value);
            Assert.Equal("brightwhite", resolved.Background?.Value);
            Assert.Equal(TuiTextDecoration.Bold | TuiTextDecoration.Underline, resolved.Decorations);
        });
    }

    /// <summary>Built-in themes are ordered, complete, and keep system terminal-neutral.</summary>
    [Fact]
    public static void BuiltInThemes_DefineRequiredCatalog()
    {
        var themes = BuiltInThemes.Create();

        Assert.Equal(["system", "forge-dark", "ocean", "high-contrast"], themes.Select(theme => theme.Theme.Id));
        Assert.All(Enum.GetValues<TuiTextRole>(), role =>
        {
            var style = new TuiThemeResolver(themes[0].Theme).Resolve(role);
            Assert.Null(style.Foreground);
            Assert.Null(style.Background);
        });
        foreach (var theme in themes.Skip(1))
        {
            var resolver = new TuiThemeResolver(theme.Theme);
            Assert.Null(resolver.Resolve(TuiTextRole.Default).Background);
            Assert.Null(resolver.Resolve(TuiTextRole.ComposerPrompt).Background);
            Assert.Null(resolver.Resolve(TuiTextRole.ThinkingIndicator).Background);
            Assert.NotNull(resolver.Resolve(TuiTextRole.ComposerPrompt).Foreground);
            Assert.NotNull(resolver.Resolve(TuiTextRole.ThinkingIndicator).Foreground);
            Assert.NotEqual(
                resolver.Resolve(TuiTextRole.Default).Foreground,
                resolver.Resolve(TuiTextRole.ComposerPrompt).Foreground);
            Assert.NotEqual(
                resolver.Resolve(TuiTextRole.Default).Foreground,
                resolver.Resolve(TuiTextRole.ThinkingIndicator).Foreground);
            Assert.NotNull(resolver.Resolve(TuiTextRole.SelectionHighlight).Background);
        }

        Assert.All(themes, theme => Assert.True(
            new TuiThemeResolver(theme.Theme)
                .Resolve(TuiTextRole.SessionStatus)
                .Decorations?.HasFlag(TuiTextDecoration.Invert)));
        Assert.True(new TuiThemeResolver(themes[3].Theme).Resolve(TuiTextRole.Error).Decorations?.HasFlag(TuiTextDecoration.Underline));
    }

    /// <summary>Configured themes append in order and replace matching built-ins as whole entries.</summary>
    [Fact]
    public static void TuiThemeConfigurationLoader_ConfiguredThemes_MergeDeterministically()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tui:defaultTheme"] = "PROJECT-BLUE",
            ["tui:themes:0:id"] = "forge-dark",
            ["tui:themes:0:name"] = "Replaced Forge",
            ["tui:themes:0:styles:Error:foreground"] = "magenta",
            ["tui:themes:1:id"] = "project-blue",
            ["tui:themes:1:name"] = "Project Blue",
            ["tui:themes:1:styles:Hyperlink:foreground"] = "#5FAFFF",
            ["tui:themes:1:styles:Hyperlink:underline"] = "true",
        }).Build();

        (var catalog, var defaultId) = TuiThemeConfigurationLoader.Load(configuration);
        var preferences = new SessionThemePreferences(catalog, defaultId);

        Assert.Equal(["system", "forge-dark", "ocean", "high-contrast", "project-blue"], catalog.Themes.Select(theme => theme.Theme.Id));
        Assert.Equal("Replaced Forge", catalog.Themes[1].Name);
        Assert.Equal("project-blue", preferences.ActiveTheme.Theme.Id);
        Assert.Single(catalog.Warnings);
    }

    /// <summary>Theme arrays from separate providers remain whole before merging by stable id.</summary>
    [Fact]
    public static void TuiThemeConfigurationLoader_LayeredThemes_DoNotMergeByArrayIndex()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["tui:themes:0:id"] = "user-theme",
                ["tui:themes:0:name"] = "User Theme",
                ["tui:themes:0:styles:Error:foreground"] = "magenta",
                ["tui:themes:0:styles:Error:bold"] = "true",
            })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["tui:themes:0:id"] = "repository-theme",
                ["tui:themes:0:name"] = "Repository Theme",
            })
            .Build();

        (var catalog, _) = TuiThemeConfigurationLoader.Load(configuration);
        var userTheme = Assert.Single(catalog.Themes, theme => theme.Theme.Id == "user-theme");
        var repositoryTheme = Assert.Single(catalog.Themes, theme => theme.Theme.Id == "repository-theme");

        Assert.Equal(TuiColor.Parse("magenta"), userTheme.Theme.Styles[TuiTextRole.Error].Foreground);
        Assert.False(repositoryTheme.Theme.Styles.ContainsKey(TuiTextRole.Error));
    }

    /// <summary>Invalid configured themes fall back to system and report a sanitized startup warning.</summary>
    [Fact]
    public static async Task TuiThemeConfigurationLoader_InvalidTheme_FallsBackAndWarns()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tui:defaultTheme"] = "unsafe",
            ["tui:themes:0:id"] = "unsafe",
            ["tui:themes:0:styles:Branding:foreground"] = "cyan",
        }).Build();

        (var catalog, var defaultId) = TuiThemeConfigurationLoader.Load(configuration);
        var preferences = new SessionThemePreferences(catalog, defaultId);
        Assert.Equal("system", preferences.ActiveTheme.Theme.Id);
        Assert.Equal(2, catalog.Warnings.Count);
        Assert.Contains(
            catalog.Warnings,
            warning => warning.Contains("Unknown semantic theme role 'Branding'.", StringComparison.Ordinal));
        Assert.Contains(
            catalog.Warnings,
            warning => warning.Contains("Unknown default theme 'unsafe'; using system.", StringComparison.Ordinal));

        IConfiguration controls = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tui:themes:0:id"] = "unsafe",
            ["tui:themes:0:name"] = "bad\u001bname",
        }).Build();
        (var controlCatalog, var controlDefaultId) = TuiThemeConfigurationLoader.Load(controls);
        Assert.Equal("system", controlDefaultId);
        Assert.DoesNotContain('\u001b', Assert.Single(controlCatalog.Warnings));

        IConfiguration invalidColor = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tui:themes:0:id"] = "invalid-color",
            ["tui:themes:0:styles:Brand:foreground"] = "chartreuse",
        }).Build();
        (var colorCatalog, var colorDefaultId) = TuiThemeConfigurationLoader.Load(invalidColor);
        Assert.Equal("system", colorDefaultId);
        Assert.Contains("Unsupported theme color", Assert.Single(colorCatalog.Warnings), StringComparison.Ordinal);

        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            themePreferences: preferences);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(
            "Warning: Configured theme 'unsafe' is invalid and was ignored. Unknown semantic theme role 'Branding'.",
            surface.Output,
            StringComparison.Ordinal);
        Assert.Contains(
            "Warning: Unknown default theme 'unsafe'; using system.",
            surface.Output,
            StringComparison.Ordinal);
    }

    /// <summary>One invalid configured theme is reported without suppressing valid configured siblings.</summary>
    [Fact]
    public static void TuiThemeConfigurationLoader_InvalidSibling_RetainsValidThemeAndWarns()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tui:defaultTheme"] = "valid-theme",
            ["tui:themes:0:id"] = "valid-theme",
            ["tui:themes:0:name"] = "Valid Theme",
            ["tui:themes:0:styles:Brand:foreground"] = "cyan",
            ["tui:themes:1:id"] = "/theme",
            ["tui:themes:1:name"] = "Invalid Theme",
        }).Build();

        (var catalog, var defaultId) = TuiThemeConfigurationLoader.Load(configuration);
        var preferences = new SessionThemePreferences(catalog, defaultId);

        Assert.Equal("valid-theme", preferences.ActiveTheme.Theme.Id);
        Assert.Contains(catalog.Themes, theme => theme.Theme.Id == "valid-theme");
        var warning = Assert.Single(catalog.Warnings);
        Assert.Contains("Configured theme '/theme' is invalid and was ignored.", warning, StringComparison.Ordinal);
        Assert.Contains("Theme ids may contain only", warning, StringComparison.Ordinal);
    }

    /// <summary>The theme command persists the user default, reports current state, and rejects unknown ids.</summary>
    [Fact]
    public static async Task ConversationalShell_ThemeCommands_PersistUserDefault()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Threadsmith", "theme-tests", Guid.NewGuid().ToString("N"));
        var configurationPath = Path.Combine(directory, "config.json");
        Directory.CreateDirectory(directory);
        const string initialConfiguration = """
            {
              // Preserve this user annotation.
              "unrelated": { "value": 42 },
              "tui": {
                "showOperationDurations": true, // Preserve this setting annotation.
              }
            }
            """;
        await File.WriteAllTextAsync(
            configurationPath,
            initialConfiguration,
            new UTF8Encoding(false));
        try
        {
            await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
            var catalog = new ConfiguredThemeCatalog(BuiltInThemes.Create());
            var preferences = new SessionThemePreferences(catalog, "system");
            var surface = new FakeConsoleSurface(
                ["/theme", "/theme current", "/theme missing", "/theme", "/quit"],
                [1]);
            var shell = new ConversationalShell(
                new TuiPresenter(harness.Dispatcher, harness.Projections),
                harness.EventStream,
                surface,
                themePreferences: preferences,
                themePreferenceStore: new UserConfigurationThemePreferenceStore(configurationPath));

            await shell.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(5));

            var persistedText = await File.ReadAllTextAsync(configurationPath);
            using var persisted = JsonDocument.Parse(persistedText, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            Assert.Equal("forge-dark", persisted.RootElement.GetProperty("tui").GetProperty("defaultTheme").GetString());
            Assert.Equal(42, persisted.RootElement.GetProperty("unrelated").GetProperty("value").GetInt32());
            Assert.Contains("// Preserve this user annotation.", persistedText, StringComparison.Ordinal);
            Assert.Contains("// Preserve this setting annotation.", persistedText, StringComparison.Ordinal);
            Assert.Contains("\"unrelated\": { \"value\": 42 }", persistedText, StringComparison.Ordinal);
            var restartedConfiguration = new ConfigurationBuilder()
                .AddJsonFile(configurationPath)
                .Build();
            (var restartedCatalog, var restartedDefault) =
                TuiThemeConfigurationLoader.Load(restartedConfiguration);
            var restartedPreferences = new SessionThemePreferences(restartedCatalog, restartedDefault);
            Assert.Equal("forge-dark", restartedPreferences.ActiveTheme.Theme.Id);
            Assert.Equal("forge-dark", surface.ActiveThemeId);
            Assert.Contains("saved as the default", surface.Output, StringComparison.Ordinal);
            Assert.Contains("Current theme: forge-dark", surface.Output, StringComparison.Ordinal);
            Assert.Contains("Unknown theme: missing", surface.Output, StringComparison.Ordinal);
            Assert.Contains("Theme unchanged.", surface.Output, StringComparison.Ordinal);
            Assert.Contains("system", surface.Output, StringComparison.Ordinal);
            Assert.Contains("[active]", surface.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Replacing an existing theme value preserves comments and unrelated source text byte-for-byte.</summary>
    [Fact]
    public static async Task ThemePreferenceStore_ExistingDefault_PreservesConfigurationSyntax()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Threadsmith", "theme-tests", Guid.NewGuid().ToString("N"));
        var configurationPath = Path.Combine(directory, "config.json");
        Directory.CreateDirectory(directory);
        const string initialConfiguration = """
            {
              /* Keep the selected-theme rationale. */
              "tui": {
                "defaultTheme": /* chosen by the user */ "system",
                "footer": { "enabled": false }
              },
              "other": [ 1, 2, 3 ] // Keep this annotation too.
            }
            """;
        await File.WriteAllTextAsync(configurationPath, initialConfiguration, new UTF8Encoding(false));
        try
        {
            var store = new UserConfigurationThemePreferenceStore(configurationPath);

            await store.SetDefaultThemeAsync("ocean");

            var persisted = await File.ReadAllTextAsync(configurationPath);
            var expected = initialConfiguration.Replace("\"system\"", "\"ocean\"", StringComparison.Ordinal);
            Assert.Equal(expected, persisted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Replacing an existing theme value preserves a UTF-8 BOM and source syntax.</summary>
    [Fact]
    public static async Task ThemePreferenceStore_ExistingDefault_WithUtf8Bom_PreservesConfigurationSyntax()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Threadsmith", "theme-tests", Guid.NewGuid().ToString("N"));
        var configurationPath = Path.Combine(directory, "config.json");
        Directory.CreateDirectory(directory);
        const string initialConfiguration = """
            {
              /* Keep the selected-theme rationale. */
              "tui": {
                "defaultTheme": /* chosen by the user */ "system",
                "footer": { "enabled": false }
              },
              "other": [ 1, 2, 3 ] // Keep this annotation too.
            }
            """;
        await File.WriteAllTextAsync(
            configurationPath,
            initialConfiguration,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            var store = new UserConfigurationThemePreferenceStore(configurationPath);

            await store.SetDefaultThemeAsync("ocean");

            var persistedBytes = await File.ReadAllBytesAsync(configurationPath);
            Assert.Equal(0xEF, persistedBytes[0]);
            Assert.Equal(0xBB, persistedBytes[1]);
            Assert.Equal(0xBF, persistedBytes[2]);
            var persisted = Encoding.UTF8.GetString(persistedBytes, 3, persistedBytes.Length - 3);
            var expected = initialConfiguration.Replace("\"system\"", "\"ocean\"", StringComparison.Ordinal);
            Assert.Equal(expected, persisted);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A failed user-default write leaves the active theme unchanged.</summary>
    [Fact]
    public static async Task ConversationalShell_ThemePersistenceFailure_LeavesSelectionUnchanged()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Threadsmith", "theme-tests", Guid.NewGuid().ToString("N"));
        var configurationPath = Path.Combine(directory, "config.json");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(configurationPath, "[]", new UTF8Encoding(false));
        try
        {
            await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
            var preferences = new SessionThemePreferences(new ConfiguredThemeCatalog(BuiltInThemes.Create()), "system");
            var surface = new FakeConsoleSurface(["/theme forge-dark", "/quit"]);
            var shell = new ConversationalShell(
                new TuiPresenter(harness.Dispatcher, harness.Projections),
                harness.EventStream,
                surface,
                themePreferences: preferences,
                themePreferenceStore: new UserConfigurationThemePreferenceStore(configurationPath));

            await shell.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("system", preferences.ActiveTheme.Theme.Id);
            Assert.Contains("Theme could not be saved; selection unchanged.", surface.Output, StringComparison.Ordinal);
            Assert.Equal("[]", await File.ReadAllTextAsync(configurationPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The tools command lists metadata, protects essential tools, and persists optional toggles.</summary>
    [Fact]
    public static async Task ConversationalShell_ToolsCommand_ManagesRepositoryAvailability()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var toolStates = new FakeToolStateManager();
        var surface = new FakeConsoleSurface(["/tools", "/quit"], [0, 1, 2]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            toolStateManager: toolStates);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["optional"], toolStates.DisabledIds);
        Assert.Contains("[enabled] Essential Read (essential)", surface.Output, StringComparison.Ordinal);
        Assert.Contains("[enabled] Optional Tool", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Essential Read is essential and cannot be disabled.", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Optional Tool disabled.", surface.Output, StringComparison.Ordinal);
        Assert.Contains("[disabled] Optional Tool", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>The policy command lists every policy, changes state, reports current state, and warns for persistent trust.</summary>
    [Fact]
    public static async Task ConversationalShell_PolicyCommand_ChangesAndReportsPolicy()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var policy = new FakeMutationApprovalPolicy();
        var surface = new FakeConsoleSurface(
            ["/policy", "/policy current", "/policy missing", "/quit"],
            [4]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            mutationApprovalPolicy: policy);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MutationApprovalPolicy.AlwaysTrustRepo, policy.CurrentPolicy);
        Assert.Equal([MutationApprovalPolicy.AlwaysTrustRepo], policy.SelectedPolicies);
        Assert.Contains("ReviewAll — review every staged diff", surface.Output, StringComparison.Ordinal);
        Assert.Contains("ReviewRisky — auto-apply ordinary edits", surface.Output, StringComparison.Ordinal);
        Assert.Contains("TrustPlan — auto-apply changes within the approved plan", surface.Output, StringComparison.Ordinal);
        Assert.Contains("TrustSession — auto-apply in-repository changes this session", surface.Output, StringComparison.Ordinal);
        Assert.Contains("AlwaysTrustRepo — persistently auto-apply", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Current mutation policy: AlwaysTrustRepo", surface.Output, StringComparison.Ordinal);
        Assert.Contains("persists across restarts", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Unknown mutation policy 'missing'", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>Mixed event output retains exact text while assigning tool, link, validation, and diff roles.</summary>
    [Fact]
    public static void TuiEventSegments_MixedOutput_PreservesPlainTextAndSemanticRoles()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var segments = new List<TuiTextSegment>();
        TuiEventSegments.Append(
            segments,
            new ToolInvocationCompleted(sessionId, occurredAt, ToolInvocationId.New(), true),
            " TOOLS: read - completed"
                + Environment.NewLine
                + "   \u2514 read_file path src/Program.cs"
                + Environment.NewLine);
        TuiEventSegments.Append(
            segments,
            new ToolInvocationCompleted(sessionId, occurredAt, ToolInvocationId.New(), false),
            " TOOLS: build - failed"
                + Environment.NewLine
                + "   \u2514 dotnet build failed"
                + Environment.NewLine);
        TuiEventSegments.Append(
            segments,
            new RepositoryOpened(sessionId, occurredAt, "C:\\source\\repo"),
            $"Threadsmith: Repository opened.{Environment.NewLine}Repository: C:\\source\\repo{Environment.NewLine}");
        TuiEventSegments.Append(
            segments,
            new MutationSetProposed(sessionId, occurredAt, MutationSetId.New()),
            $"@@ -1 +1 @@{Environment.NewLine}{Environment.NewLine}-old{Environment.NewLine}+new{Environment.NewLine}");
        TuiEventSegments.Append(
            segments,
            new TestRunCompleted(sessionId, occurredAt, 4, 0),
            string.Empty);

        Assert.Equal(
            " TOOLS: read - completed"
            + Environment.NewLine
            + "   \u2514 read_file path src/Program.cs"
            + Environment.NewLine
            + " TOOLS: build - failed"
            + Environment.NewLine
            + "   \u2514 dotnet build failed"
            + Environment.NewLine
            + $"Threadsmith: Repository opened.{Environment.NewLine}Repository: C:\\source\\repo{Environment.NewLine}"
            + $"@@ -1 +1 @@{Environment.NewLine}{Environment.NewLine}-old{Environment.NewLine}+new{Environment.NewLine}"
            + $"Tests: 4 passed, 0 failed, 0 skipped{Environment.NewLine}",
            string.Concat(segments.Select(segment => segment.Text)));
        Assert.Contains(segments, segment => segment.Role == TuiTextRole.ToolSuccess);
        Assert.Contains(segments, segment => segment.Role == TuiTextRole.ToolFailure);
        Assert.Contains(segments, segment => segment.Role == TuiTextRole.Hyperlink);
        Assert.Contains(segments, segment => segment.Role == TuiTextRole.DiffAdded);
        Assert.Contains(segments, segment => segment.Role == TuiTextRole.DiffRemoved);
        Assert.Contains(segments, segment => segment.Role == TuiTextRole.DiffContext);
        Assert.Contains(segments, segment => segment.Role == TuiTextRole.Muted && segment.Text.Contains("read_file", StringComparison.Ordinal));
        Assert.Contains(segments, segment => segment.Role == TuiTextRole.Success);
    }

    /// <summary>Leading presentation-boundary spacing does not demote the completed-tool header.</summary>
    [Fact]
    public static void TuiEventSegments_ToolCompletionAfterBoundary_KeepsHeaderOutcomeRole()
    {
        var segments = new List<TuiTextSegment>();
        TuiEventSegments.Append(
            segments,
            new ToolInvocationCompleted(SessionId.New(), DateTimeOffset.UtcNow, ToolInvocationId.New(), true),
            Environment.NewLine
                + " TOOLS: read - completed"
                + Environment.NewLine
                + "   \u2514 read_file path src/Program.cs"
                + Environment.NewLine);

        Assert.Equal(
            Environment.NewLine
            + " TOOLS: read - completed"
            + Environment.NewLine
            + "   \u2514 read_file path src/Program.cs"
            + Environment.NewLine,
            string.Concat(segments.Select(segment => segment.Text)));
        Assert.Contains(
            segments,
            segment => segment.Role == TuiTextRole.ToolSuccess
                && segment.Text.Contains(" TOOLS: read - completed", StringComparison.Ordinal));
        Assert.DoesNotContain(
            segments,
            segment => segment.Role == TuiTextRole.Muted
                && segment.Text.Contains(" TOOLS: read - completed", StringComparison.Ordinal));
        Assert.Contains(
            segments,
            segment => segment.Role == TuiTextRole.Muted
                && segment.Text.Contains("read_file path src/Program.cs", StringComparison.Ordinal));
    }

    /// <summary>Plan lifecycle blocks keep status headers and muted guided body/detail roles.</summary>
    [Fact]
    public static void TuiEventSegments_PlanLifecycleBlocks_UseHeaderAndMutedBodyRoles()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var segments = new List<TuiTextSegment>();
        var proposedText = " PLAN: revision 1"
            + Environment.NewLine
            + " \u2502 Update the formatter."
            + Environment.NewLine
            + " \u2502"
            + Environment.NewLine
            + " \u2502 Steps:"
            + Environment.NewLine
            + " \u2514 1. Add shared block - Tool, plan, and semantic text align."
            + Environment.NewLine;
        var approvedText = " PLAN: auto-approved"
            + Environment.NewLine
            + " \u2502 Revision: 1"
            + Environment.NewLine
            + " \u2514 Reason: Policy ReviewRisky approved the plan."
            + Environment.NewLine;

        TuiEventSegments.Append(
            segments,
            new PlanProposed(sessionId, occurredAt, "Update the formatter."),
            proposedText);
        TuiEventSegments.Append(
            segments,
            new PlanAutoApproved(
                sessionId,
                occurredAt,
                RunId.New(),
                ApprovalId.New(),
                PlanApprovalPolicy.ReviewRisky,
                PlanRiskClassification.Low,
                1,
                "Policy ReviewRisky approved the plan."),
            approvedText);

        Assert.Equal(proposedText + approvedText, string.Concat(segments.Select(segment => segment.Text)));
        Assert.Contains(
            segments,
            segment => segment.Role == TuiTextRole.Status
                && segment.Text.Contains(" PLAN: revision 1", StringComparison.Ordinal));
        Assert.Contains(
            segments,
            segment => segment.Role == TuiTextRole.Success
                && segment.Text.Contains(" PLAN: auto-approved", StringComparison.Ordinal));
        Assert.Contains(
            segments,
            segment => segment.Role == TuiTextRole.Muted
                && segment.Text.Contains(" \u2502 Steps:", StringComparison.Ordinal));
        Assert.Contains(
            segments,
            segment => segment.Role == TuiTextRole.Muted
                && segment.Text.Contains("Reason: Policy ReviewRisky", StringComparison.Ordinal));
    }

    /// <summary>Mutation lifecycle blocks keep success headers and muted guided body/detail roles.</summary>
    [Fact]
    public static void TuiEventSegments_MutationLifecycleBlock_UsesHeaderAndMutedDetailRoles()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var segments = new List<TuiTextSegment>();
        var text = " MUTATION: Applied under the active approval policy"
            + Environment.NewLine
            + " \u2502 Mutation applied: src/File.cs"
            + Environment.NewLine
            + " \u2514 The expression uses the approved value."
            + Environment.NewLine;

        TuiEventSegments.Append(
            segments,
            new MutationApplied(sessionId, occurredAt, MutationId.New()),
            text);

        Assert.Equal(text, string.Concat(segments.Select(segment => segment.Text)));
        Assert.Contains(
            segments,
            segment => segment.Role == TuiTextRole.Success
                && segment.Text.Contains(" MUTATION: Applied under the active approval policy", StringComparison.Ordinal));
        Assert.Contains(
            segments,
            segment => segment.Role == TuiTextRole.Muted
                && segment.Text.Contains("Mutation applied: src/File.cs", StringComparison.Ordinal));
        Assert.Contains(
            segments,
            segment => segment.Role == TuiTextRole.Muted
                && segment.Text.Contains("The expression uses the approved value.", StringComparison.Ordinal));
    }

    /// <summary>Mutation preparation lifecycle blocks keep status/warning headers and muted guided rows.</summary>
    [Fact]
    public static void TuiEventSegments_MutationPreparationBlocks_UseHeaderAndMutedDetailRoles()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var runId = RunId.New();
        var segments = new List<TuiTextSegment>();
        var startedText = " MUTATION: Preparing preview"
            + Environment.NewLine
            + " \u2514 Attempt: 1/2"
            + Environment.NewLine;
        var repairText = " MUTATION: Retrying proposal with correction evidence"
            + Environment.NewLine
            + " \u2502 Attempt: 2/2"
            + Environment.NewLine
            + " \u2514 Reason: ReplaceText expectedText was not found in 'src/File.cs'."
            + Environment.NewLine;

        TuiEventSegments.Append(
            segments,
            new MutationProposalStarted(sessionId, occurredAt, runId, AttemptNumber: 1, MaximumAttempts: 2),
            startedText);
        TuiEventSegments.Append(
            segments,
            new MutationProposalRepairAttempted(
                sessionId,
                occurredAt,
                runId,
                AttemptNumber: 2,
                MaximumAttempts: 2,
                "ReplaceText expectedText was not found in 'src/File.cs'."),
            repairText);

        Assert.Equal(startedText + repairText, string.Concat(segments.Select(segment => segment.Text)));
        Assert.Contains(
            segments,
            segment => segment.Role == TuiTextRole.Status
                && segment.Text.Contains(" MUTATION: Preparing preview", StringComparison.Ordinal));
        Assert.Contains(
            segments,
            segment => segment.Role == TuiTextRole.Warning
                && segment.Text.Contains(" MUTATION: Retrying proposal", StringComparison.Ordinal));
        Assert.Contains(
            segments,
            segment => segment.Role == TuiTextRole.Muted
                && segment.Text.Contains("Attempt: 2/2", StringComparison.Ordinal));
        Assert.Contains(
            segments,
            segment => segment.Role == TuiTextRole.Muted
                && segment.Text.Contains("Reason: ReplaceText expectedText", StringComparison.Ordinal));
    }

    /// <summary>Model-authored THINKING text remains ordinary visible answer content.</summary>
    [Fact]
    public static void TuiEventSegments_ModelAuthoredThinking_UsesDefaultRole()
    {
        var segments = new List<TuiTextSegment>();

        TuiEventSegments.Append(
            segments,
            new ModelOutputObserved(SessionId.New(), DateTimeOffset.UtcNow, "answer"),
            $"THINKING{Environment.NewLine}answer{Environment.NewLine}");

        Assert.Contains(segments, segment => segment.Role == TuiTextRole.Default && segment.Text.Contains("THINKING", StringComparison.Ordinal));
        Assert.DoesNotContain(segments, segment => segment.Role == TuiTextRole.ThinkingIndicator);
    }

    /// <summary>Structured compiler diagnostics use the semantic role matching their severity.</summary>
    [Theory]
    [InlineData(DiagnosticSeverity.Info, nameof(TuiTextRole.Status))]
    [InlineData(DiagnosticSeverity.Warning, nameof(TuiTextRole.Warning))]
    [InlineData(DiagnosticSeverity.Error, nameof(TuiTextRole.Error))]
    public static void TuiEventSegments_StructuredDiagnostic_UsesSeverityRole(
        DiagnosticSeverity severity,
        string expectedRoleName)
    {
        var diagnostic = new Diagnostic
        {
            Id = "diagnostic",
            Code = "TS1",
            Severity = severity,
            Project = "Threadsmith.Tests",
            TargetFramework = "net10.0",
            Message = "message",
            Confidence = SemanticConfidenceLevel.FullSemantic,
        };
        var segments = new List<TuiTextSegment>();

        TuiEventSegments.Append(
            segments,
            new DiagnosticObserved(SessionId.New(), DateTimeOffset.UtcNow, diagnostic.Code, diagnostic.Message, diagnostic),
            string.Empty);

        var segment = Assert.Single(segments);
        Assert.Equal(Enum.Parse<TuiTextRole>(expectedRoleName), segment.Role);
    }

    /// <summary>An incomplete structured test run is rendered as an error even when no failures were counted.</summary>
    [Fact]
    public static void TuiEventSegments_IncompleteTestRun_UsesErrorRole()
    {
        var validation = new TestValidationResult
        {
            Selection = new TestSelection(),
            Completed = false,
        };
        var segments = new List<TuiTextSegment>();

        TuiEventSegments.Append(
            segments,
            new TestRunCompleted(SessionId.New(), DateTimeOffset.UtcNow, 0, 0, StructuredResult: validation),
            string.Empty);

        var segment = Assert.Single(segments);
        Assert.Equal(TuiTextRole.Error, segment.Role);
    }

    /// <summary>Hyperlink metadata rejects control characters, relative targets, and unsupported schemes.</summary>
    [Fact]
    public static void TuiTextSegment_UnsafeHyperlinks_AreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new TuiTextSegment("relative", TuiTextRole.Hyperlink, new Uri("relative", UriKind.Relative)).Validate());
        Assert.Throws<ArgumentException>(() =>
            new TuiTextSegment("script", TuiTextRole.Hyperlink, new Uri("javascript:alert(1)")).Validate());
        Assert.Throws<ArgumentException>(() =>
            new TuiTextSegment("control", TuiTextRole.Hyperlink, new Uri("https://example.test/%1B")).Validate());
    }

    /// <summary>Redirected, NO_COLOR, and capability-limited terminals use the plain-text fallback.</summary>
    [Theory]
    [InlineData(true, null, null)]
    [InlineData(false, "1", null)]
    [InlineData(false, null, "dumb")]
    public static void TuiThemeResolver_LimitedTerminal_SuppressesStyles(
        bool redirected,
        string? noColor,
        string? terminal)
    {
        Assert.True(TuiThemeResolver.ShouldSuppressStyles(redirected, noColor, terminal));
        Assert.NotNull(TuiThemeResolver.GetSuppressionReason(redirected, noColor, terminal));
    }

    /// <summary>Unknown roles safely resolve through the terminal-native default.</summary>
    [Fact]
    public static void TuiThemeResolver_UnknownRole_FallsBackToDefault()
    {
        var theme = new TuiTheme(
            "fallback",
            [new(TuiTextRole.Default, new TuiTextStyle(TuiColor.Parse("cyan")))]);
        var resolver = new TuiThemeResolver(theme);

        var result = resolver.Resolve((TuiTextRole)int.MaxValue);

        Assert.Equal("cyan", result.Foreground?.Value);
        Assert.Null(result.Background);
    }

    /// <summary>Reasoning is hidden by default and streams only while the thinking toggle is enabled.</summary>
    [Fact]
    public static async Task ConversationalShell_ThinkingOn_StreamsFutureReasoningUntilOff()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession
        {
            Turns =
            [
                new ScriptedTurn
                {
                    Reasoning = "Consider the greeting.",
                    Text = "Hello!",
                },
            ],
        });
        var surface = new FakeConsoleSurface(["/thinking on", "hello", "/thinking off", "hello again", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(surface.Statuses, status => status.StartsWith("THINKING · ", StringComparison.Ordinal));
        Assert.Contains("Streaming thinking is on.", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Streaming thinking is off.", surface.Output, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(surface.Output, "Consider the greeting."));
        Assert.DoesNotContain("<thinking>", surface.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("</thinking>", surface.Output, StringComparison.Ordinal);
        Assert.Contains(
            surface.Segments,
            segment => segment.Role == TuiTextRole.Reasoning
                && string.Equals(segment.Text, "Consider the greeting.", StringComparison.Ordinal));
        Assert.DoesNotContain("THINKING collapsed.", surface.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("// reasoning:", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>Streaming reasoning takes permanent scrollback ownership from transient thinking activity.</summary>
    [Fact]
    public static async Task ConversationalShell_ThinkingStreaming_DoesNotRestartTransientActivity()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession
        {
            Turns =
            [
                new ScriptedTurn
                {
                    Reasoning = "Inspect the repository.",
                    Text = "Done.",
                },
            ],
        });
        var surface = new FakeConsoleSurface(["/thinking on", "hello", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(
            1,
            surface.Lifecycle.Count(entry => string.Equals(
                entry,
                "activity-start:THINKING",
                StringComparison.Ordinal)));
        var thinkingEnded = surface.Lifecycle.ToList().IndexOf("activity-end:THINKING");
        var reasoningWritten = surface.Lifecycle.ToList().IndexOf("output:segments");
        Assert.True(thinkingEnded >= 0 && thinkingEnded < reasoningWritten);
        var streamedReasoning = string.Concat(surface.Segments
            .Where(segment => segment.Role == TuiTextRole.Reasoning)
            .Select(segment => segment.Text));
        Assert.StartsWith(
            $"{Environment.NewLine}Inspect the repository.",
            streamedReasoning,
            StringComparison.Ordinal);
        Assert.False(streamedReasoning.StartsWith(
            Environment.NewLine + Environment.NewLine,
            StringComparison.Ordinal));
    }

    /// <summary><c>/thinking</c> without arguments toggles future reasoning streaming.</summary>
    [Fact]
    public static async Task ConversationalShell_ThinkingNoArgument_TogglesStreamingMode()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/thinking", "/thinking", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("Streaming thinking is on.", surface.Output, StringComparison.Ordinal);
        Assert.Contains("Streaming thinking is off.", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>Invalid <c>/thinking</c> arguments are rejected locally.</summary>
    [Fact]
    public static async Task ConversationalShell_ThinkingInvalidArgument_ShowsUsage()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/thinking maybe", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains("Usage: /thinking [on|off]", surface.Output, StringComparison.Ordinal);
        Assert.Empty(harness.Events.OfType<TaskIntentRecorded>());
    }

    /// <summary>Answer-only model turns show transient thinking status while the request is pending.</summary>
    [Fact]
    public static async Task ConversationalShell_AnswerOnlyTurn_ShowsThinkingStatus()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession
        {
            Turns =
            [
                new ScriptedTurn
                {
                    Text = "Hello!",
                },
            ],
        });
        var surface = new FakeConsoleSurface(["hello", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Contains(surface.Statuses, status => status.StartsWith("THINKING · ", StringComparison.Ordinal));
    }

    /// <summary>Overlapping semantic-check completion keeps the still-running semantic check live.</summary>
    [Fact]
    public static async Task ConversationalShell_OverlappingSemanticChecks_KeepsDisplayedRunningCheckActive()
    {
        var provider = new LeadingWhitespaceModelProvider();
        await using var harness = await SessionHarness.CreateAsync(
            new ScriptedSession(),
            modelProvider: provider);
        var surface = new FakeConsoleSurface(["hello", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);
        var shellTask = shell.RunAsync(modelStatus: "Test model");
        try
        {
            await Task.WhenAll(provider.WhitespaceEmitted, surface.StatusStarted)
                .WaitAsync(TimeSpan.FromSeconds(5));
            var sessionId = harness.Events.OfType<SessionCreated>().Single().SessionId;
            var firstRunId = RunId.New();
            var firstCheckId = SemanticCheckId.New();
            var secondRunId = RunId.New();
            var secondCheckId = SemanticCheckId.New();
            var occurredAt = DateTimeOffset.UtcNow;
            var firstStarted = new SemanticCheckStarted(
                sessionId,
                occurredAt,
                firstRunId,
                firstCheckId,
                SemanticCheckPhase.PreMutation,
                "first semantic check");
            var secondStarted = new SemanticCheckStarted(
                sessionId,
                occurredAt,
                secondRunId,
                secondCheckId,
                SemanticCheckPhase.PostMutation,
                "second semantic check");
            var firstCompleted = new SemanticCheckCompleted(
                sessionId,
                occurredAt,
                firstRunId,
                firstCheckId,
                SemanticCheckPhase.PreMutation,
                "first semantic check",
                SemanticCheckOutcome.Completed,
                ElapsedMilliseconds: 42);

            await harness.EventStream.PublishAsync(firstStarted);
            await harness.EventStream.PublishAsync(secondStarted);
            await harness.EventStream.PublishAsync(firstCompleted);

            var keptSecondActivity = await WaitForConditionAsync(() =>
                surface.Output.Contains(
                    "SEMANTIC CHECKS: first semantic check - completed",
                    StringComparison.Ordinal)
                && surface.ActiveStatuses.Any(status => status.StartsWith(
                    "SEMANTIC CHECKS: second semantic check",
                    StringComparison.Ordinal)));

            Assert.True(keptSecondActivity, surface.Output);
        }
        finally
        {
            provider.ReleaseAnswer();
        }

        await shellTask.WaitAsync(TimeSpan.FromSeconds(5));

        static async Task<bool> WaitForConditionAsync(Func<bool> condition)
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (condition())
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(20));
            }

            return condition();
        }
    }

    /// <summary>Leading whitespace-only model chunks leave thinking active until visible answer text arrives.</summary>
    [Fact]
    public static async Task ConversationalShell_LeadingWhitespace_KeepsThinkingActive()
    {
        var provider = new LeadingWhitespaceModelProvider();
        await using var harness = await SessionHarness.CreateAsync(
            new ScriptedSession(),
            modelProvider: provider);
        var surface = new FakeConsoleSurface(["hello", "/quit"]);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        var shellTask = shell.RunAsync(modelStatus: "Test model");
        try
        {
            await Task.WhenAll(provider.WhitespaceEmitted, surface.StatusStarted)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(surface.IsStatusActive);
        }
        finally
        {
            provider.ReleaseAnswer();
        }

        await shellTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("answer", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>Activity-display failures escape while a verbose active run fills the event dispatcher.</summary>
    [Fact]
    public static async Task ConversationalShell_ActivityDisplayFailure_DuringVerboseRun_IsPropagated()
    {
        var provider = new LeadingWhitespaceModelProvider(trailingChunkCount: 300);
        await using var harness = await SessionHarness.CreateAsync(
            new ScriptedSession(),
            modelProvider: provider);
        var expected = new InvalidOperationException("status rendering failed");
        var surface = new FakeConsoleSurface(["hello"], statusFailure: expected);
        var shell = new ConversationalShell(
            new TuiPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        var shellTask = shell.RunAsync(modelStatus: "Test model");
        await Task.WhenAll(provider.WhitespaceEmitted, surface.StatusStarted)
            .WaitAsync(TimeSpan.FromSeconds(5));
        provider.ReleaseAnswer();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => shellTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(expected, exception);
    }

    /// <summary>Reasoning is retained transiently without a completed transcript marker.</summary>
    [Fact]
    public static void ConversationTranscript_ReasoningThenAnswer_OmitsCompletedMarker()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var transcript = new ConversationTranscript(string.Empty);
        Assert.False(transcript.Apply(new TaskIntentRecorded(sessionId, occurredAt, "hello")));
        Assert.False(transcript.Apply(new ModelReasoningObserved(sessionId, occurredAt, "Let me think... ")));
        Assert.False(transcript.Apply(new ModelReasoningObserved(sessionId, occurredAt, "Now I know.")));
        Assert.True(transcript.Apply(new ModelOutputObserved(sessionId, occurredAt, "42")));
        Assert.True(transcript.Apply(new RunCompleted(sessionId, occurredAt, RunId.New(), true)));

        var newline = Environment.NewLine;
        Assert.Equal($"42{newline}{newline}", transcript.Text);
        Assert.Equal("Let me think... Now I know.", transcript.LatestReasoning);
    }

    /// <summary>A tool continuation retains latest reasoning without materializing THINKING in the transcript.</summary>
    [Fact]
    public static void ConversationTranscript_ReasoningAcrossToolRound_OmitsThinkingMarker()
    {
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var transcript = new ConversationTranscript(string.Empty);
        Assert.False(transcript.Apply(new TaskIntentRecorded(sessionId, occurredAt, "look up a symbol")));
        var toolInvocation = new ToolInvocationId(Guid.NewGuid());
        Assert.False(transcript.Apply(new ModelReasoningObserved(sessionId, occurredAt, "First I will find it. ")));
        Assert.False(transcript.Apply(new ToolInvocationStarted(sessionId, occurredAt, toolInvocation, "find_symbol", runId)));
        Assert.True(transcript.Apply(new ToolInvocationCompleted(sessionId, occurredAt, toolInvocation, Succeeded: true, ResultJson: "{}", IsTruncated: false)));
        Assert.False(transcript.Apply(new ModelReasoningObserved(sessionId, occurredAt, "Now I have the result. ")));
        Assert.True(transcript.Apply(new ModelOutputObserved(sessionId, occurredAt, "found it")));
        Assert.True(transcript.Apply(new RunCompleted(sessionId, occurredAt, runId, true)));

        var newline = Environment.NewLine;
        Assert.DoesNotContain("THINKING", transcript.Text, StringComparison.Ordinal);
        Assert.Contains(
            " TOOLS: find_symbol - completed"
                + newline
                + "   \u2514 no additional detail"
                + newline
                + "found it",
            transcript.Text,
            StringComparison.Ordinal);
        Assert.Equal("Now I have the result. ", transcript.LatestReasoning);
    }

    /// <summary>Answer-only runs omit redundant user echoes, assistant labels, and leading whitespace chunks.</summary>
    [Fact]
    public static void ConversationTranscript_AnswerOnly_RendersExactLayout()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var transcript = new ConversationTranscript(string.Empty);
        Assert.False(transcript.Apply(new TaskIntentRecorded(sessionId, occurredAt, "hello")));
        Assert.False(transcript.Apply(new ModelOutputObserved(sessionId, occurredAt, "\n\n")));
        Assert.True(transcript.Apply(new ModelOutputObserved(sessionId, occurredAt, "answer")));
        Assert.True(transcript.Apply(new RunCompleted(sessionId, occurredAt, RunId.New(), true)));

        var newline = Environment.NewLine;
        Assert.Equal($"answer{newline}{newline}", transcript.Text);
    }

    /// <summary>Reasoning-only runs complete without rendering an empty answer label.</summary>
    [Fact]
    public static void ConversationTranscript_ReasoningOnly_RendersExactLayoutWithoutAnswerLabel()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var transcript = new ConversationTranscript(string.Empty);
        Assert.False(transcript.Apply(new TaskIntentRecorded(sessionId, occurredAt, "hello")));
        Assert.False(transcript.Apply(new ModelReasoningObserved(sessionId, occurredAt, "thinking...")));
        Assert.True(transcript.Apply(new RunCompleted(sessionId, occurredAt, RunId.New(), true)));

        var newline = Environment.NewLine;
        Assert.Equal($"{newline}{newline}", transcript.Text);
        Assert.DoesNotContain("Threadsmith:", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Tool activity renders as a collapsed TOOLS marker, not as assistant answer text.</summary>
    [Fact]
    public static void ConversationTranscript_ToolActivity_RendersCollapsedMarkerNotAnswerText()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var runId = RunId.New();
        var invocationId = ToolInvocationId.New();
        var transcript = new ConversationTranscript(string.Empty);
        Assert.False(transcript.Apply(new TaskIntentRecorded(sessionId, occurredAt, "find IDomainEvent references")));
        Assert.False(transcript.Apply(new ToolInvocationStarted(
            sessionId,
            occurredAt,
            invocationId,
            "find_references",
            runId)));
        Assert.True(transcript.Apply(new ToolInvocationCompleted(
            sessionId,
            occurredAt,
            invocationId,
            Succeeded: true)));
        Assert.True(transcript.Apply(new ModelOutputObserved(sessionId, occurredAt, "Found 3 references.")));
        Assert.True(transcript.Apply(new RunCompleted(sessionId, occurredAt, runId, true)));

        var newline = Environment.NewLine;
        Assert.StartsWith(newline + " TOOLS: find_references - completed", transcript.Text, StringComparison.Ordinal);
        Assert.Contains($" TOOLS: find_references - completed{newline}   \u2514 no additional detail{newline}", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("Found 3 references.", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Threadsmith:", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("tool:find_references requested", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("sources:", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>code_explore tool output inspection is off by default.</summary>
    [Fact]
    public static void ConversationTranscript_CodeExploreInspection_DefaultOff_OmitsOutput()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var invocationId = ToolInvocationId.New();
        var transcript = new ConversationTranscript(string.Empty);

        Assert.False(transcript.Apply(new ToolInvocationStarted(
            sessionId,
            occurredAt,
            invocationId,
            "code_explore",
            RunId.New())));
        Assert.True(transcript.Apply(new ToolInvocationCompleted(
            sessionId,
            occurredAt,
            invocationId,
            Succeeded: true,
            ResultJson: "{\"structured\":true}",
            ModelResultContent: "# code_explore result\nvisible markdown")));

        Assert.Contains("TOOLS: code_explore - completed", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("# code_explore result", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("{\"structured\":true}", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>code_explore tool output inspection renders the final model-visible content safely when enabled.</summary>
    [Fact]
    public static void ConversationTranscript_CodeExploreInspection_On_RendersModelContentSafely()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var invocationId = ToolInvocationId.New();
        var transcript = new ConversationTranscript(
            string.Empty,
            inspectCodeExploreOutput: () => true);

        Assert.False(transcript.Apply(new ToolInvocationStarted(
            sessionId,
            occurredAt,
            invocationId,
            "code_explore",
            RunId.New())));
        Assert.True(transcript.Apply(new ToolInvocationCompleted(
            sessionId,
            occurredAt,
            invocationId,
            Succeeded: true,
            ResultJson: "{\"structured\":true}",
            ModelResultContent: "# code_explore result\n\u001b[31mred")));

        Assert.Contains("Output:", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("# code_explore result", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("\\u001B[31mred", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', transcript.Text);
        Assert.DoesNotContain("{\"structured\":true}", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>code_explore inspection normalizes CRLF before terminal-control escaping.</summary>
    [Fact]
    public static void ConversationTranscript_CodeExploreInspection_NormalizesCrlfModelContent()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var invocationId = ToolInvocationId.New();
        var transcript = new ConversationTranscript(
            string.Empty,
            inspectCodeExploreOutput: () => true);

        Assert.False(transcript.Apply(new ToolInvocationStarted(
            sessionId,
            occurredAt,
            invocationId,
            "code_explore",
            RunId.New())));
        Assert.True(transcript.Apply(new ToolInvocationCompleted(
            sessionId,
            occurredAt,
            invocationId,
            Succeeded: true,
            ResultJson: "{\"structured\":true}",
            ModelResultContent: "# code_explore result\r\ncancellationToken: cancellationToken")));

        Assert.Contains("# code_explore result", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("cancellationToken: cancellationToken", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u000D", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("{\"structured\":true}", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Truncated code_explore inspection does not reintroduce CR control escapes.</summary>
    [Fact]
    public static void ConversationTranscript_CodeExploreInspection_TruncatedOutputNormalizesMarkerNewline()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var invocationId = ToolInvocationId.New();
        var transcript = new ConversationTranscript(
            string.Empty,
            inspectCodeExploreOutput: () => true);

        Assert.False(transcript.Apply(new ToolInvocationStarted(
            sessionId,
            occurredAt,
            invocationId,
            "code_explore",
            RunId.New())));
        Assert.True(transcript.Apply(new ToolInvocationCompleted(
            sessionId,
            occurredAt,
            invocationId,
            Succeeded: true,
            ResultJson: "{\"structured\":true}",
            ModelResultContent: new string('x', 100_000) + "\r\ntrailing")));

        Assert.Contains("[code_explore inspection truncated by TUI display bound]", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u000D", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Structured code_explore inspection is pretty-printed inside the tool block.</summary>
    [Fact]
    public static void ConversationTranscript_CodeExploreInspection_StructuredModePrettyPrintsJson()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var invocationId = ToolInvocationId.New();
        var transcript = new ConversationTranscript(
            string.Empty,
            inspectCodeExploreOutput: () => true);

        Assert.False(transcript.Apply(new ToolInvocationStarted(
            sessionId,
            occurredAt,
            invocationId,
            "code_explore",
            RunId.New())));
        Assert.True(transcript.Apply(new ToolInvocationCompleted(
            sessionId,
            occurredAt,
            invocationId,
            Succeeded: true,
            ResultJson: "{\"structured\":true}")));

        Assert.Contains("Output:", transcript.Text, StringComparison.Ordinal);
        Assert.Contains($"\u2502 {{", transcript.Text, StringComparison.Ordinal);
        Assert.Contains("\"structured\": true", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Built-in search completion identifies its scope and bounded result count.</summary>
    [Theory]
    [InlineData("{\"Matches\":[]}", false, "0 matches")]
    [InlineData("{\"Matches\":[{}]}", true, "1 match, truncated")]
    public static void ConversationTranscript_SearchCompletion_RendersPathAndResultCount(
        string resultJson,
        bool isTruncated,
        string expectedSummary)
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var invocationId = ToolInvocationId.New();
        var source = new ToolActivitySource(ToolActivitySourceKind.BuiltIn);
        var transcript = new ConversationTranscript(string.Empty);
        Assert.False(transcript.Apply(new ToolInvocationStarted(
            sessionId,
            occurredAt,
            invocationId,
            "search",
            RunId.New(),
            Source: source,
            ActivityDetail: "resultScope in container/source/AI.Inference.Fusion")));
        Assert.True(transcript.Apply(new ToolInvocationCompleted(
            sessionId,
            occurredAt,
            invocationId,
            Succeeded: true,
            ResultJson: resultJson,
            IsTruncated: isTruncated,
            Source: source)));

        Assert.Contains(
            $"\u2514 resultScope in container/source/AI.Inference.Fusion · {expectedSummary}",
            transcript.Text,
            StringComparison.Ordinal);
    }

    /// <summary>Mutation proposal startup is visible before model-generated mutations arrive.</summary>
    [Fact]
    public static void ConversationTranscript_MutationProposalStarted_RendersPreparationStatus()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var runId = RunId.New();
        var transcript = new ConversationTranscript(string.Empty);
        Assert.False(transcript.Apply(new TaskIntentRecorded(sessionId, occurredAt, "change a file")));

        Assert.True(transcript.Apply(new MutationProposalStarted(
            sessionId,
            occurredAt,
            runId,
            AttemptNumber: 1,
            MaximumAttempts: 2)));

        Assert.StartsWith(
            Environment.NewLine
                + " MUTATION: Preparing preview"
                + Environment.NewLine
                + " \u2514 Attempt: 1/2"
                + Environment.NewLine,
            transcript.Text,
            StringComparison.Ordinal);
    }

    /// <summary>Mutation proposal repair attempts are visible before the retry completes or fails.</summary>
    [Fact]
    public static void ConversationTranscript_MutationProposalRepairAttempt_RendersRetryStatus()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var runId = RunId.New();
        var transcript = new ConversationTranscript(string.Empty);
        Assert.False(transcript.Apply(new TaskIntentRecorded(sessionId, occurredAt, "change a file")));

        Assert.True(transcript.Apply(new MutationProposalRepairAttempted(
            sessionId,
            occurredAt,
            runId,
            AttemptNumber: 2,
            MaximumAttempts: 2,
            "ReplaceText expectedText was not found in 'src/File.cs'.")));

        Assert.StartsWith(
            Environment.NewLine
                + " MUTATION: Retrying proposal with correction evidence"
                + Environment.NewLine
                + " \u2502 Attempt: 2/2"
                + Environment.NewLine
                + " \u2514 Reason: ReplaceText expectedText was not found in 'src/File.cs'."
                + Environment.NewLine,
            transcript.Text,
            StringComparison.Ordinal);
    }

    /// <summary>Generic model correction attempts are visible through a sanitized lifecycle block.</summary>
    [Fact]
    public static void ConversationTranscript_ModelCorrectionAttempt_RendersRetryStatus()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var runId = RunId.New();
        var transcript = new ConversationTranscript(string.Empty);
        Assert.False(transcript.Apply(new TaskIntentRecorded(sessionId, occurredAt, "change a file")));

        Assert.True(transcript.Apply(new ModelCorrectionAttempted(
            sessionId,
            occurredAt,
            runId,
            ModelCorrectionCategory.PostApplyValidation,
            AttemptNumber: 1,
            MaximumAttempts: 3,
            "Validation gate requires correction.")));

        Assert.StartsWith(
            Environment.NewLine
                + " CORRECTION: Retrying model request"
                + Environment.NewLine
                + " │ Attempt: 1/3"
                + Environment.NewLine
                + " ├ Category: PostApplyValidation"
                + Environment.NewLine
                + " └ Reason: Validation gate requires correction."
                + Environment.NewLine,
            transcript.Text,
            StringComparison.Ordinal);
    }

    /// <summary>Policy-auto-approved mutation previews do not advertise a manual review prompt.</summary>
    [Fact]
    public static void ConversationTranscript_PolicyAutoApprovedMutation_RendersAppliedLifecycleBlock()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var runId = RunId.New();
        var mutationSetId = MutationSetId.New();
        var mutationId = MutationId.New();
        const string relativePath = "src/File.cs";
        var plan = new ImplementationPlan
        {
            Revision = 1,
            Summary = "Change one file.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = StepId.New(),
                    Title = "Update file",
                    Description = "Replace the old expression.",
                    FileIntents =
                    [
                        new PlanFileIntent
                        {
                            Path = relativePath,
                            Kind = PlanFileChangeKind.Modify,
                        },
                    ],
                    ExpectedOutcome = "The expression uses the approved value.",
                },
            ],
        };
        var preview = new MutationPreview(
            mutationSetId,
            $"@@ -1 +1 @@{Environment.NewLine}-old{Environment.NewLine}+new{Environment.NewLine}",
            [],
            AddedLines: 1,
            RemovedLines: 1);
        var transcript = new ConversationTranscript(string.Empty);
        Assert.False(transcript.Apply(new TaskIntentRecorded(sessionId, occurredAt, "change a file")));
        Assert.True(transcript.Apply(new PlanProposed(sessionId, occurredAt, plan.Summary, runId, plan)));
        Assert.True(transcript.Apply(new MutationProposalStarted(
            sessionId,
            occurredAt,
            runId,
            AttemptNumber: 1,
            MaximumAttempts: 2)));

        Assert.True(transcript.Apply(new MutationSetProposed(
            sessionId,
            occurredAt,
            preview.MutationSetId,
            preview,
            MutationApprovalLevel.PolicyAutoApproved)));
        Assert.True(transcript.Apply(new MutationApplied(
            sessionId,
            occurredAt,
            mutationId,
            mutationSetId,
            relativePath)));

        Assert.Contains(Environment.NewLine + "Threadsmith: Mutation preview", transcript.Text, StringComparison.Ordinal);
        Assert.Contains(
            " MUTATION: Applied under the active approval policy"
                + Environment.NewLine
                + " \u2502 Mutation applied: src/File.cs"
                + Environment.NewLine
                + " \u2514 The expression uses the approved value."
                + Environment.NewLine,
            transcript.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Choose apply or discard at the mutation review prompt.", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Applied mutation detail is scoped to its mutation set and exact path.</summary>
    [Fact]
    public static void ConversationTranscript_AppliedMutation_UsesMutationSetScopedExactPlanDetail()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var firstRunId = RunId.New();
        var secondRunId = RunId.New();
        var firstMutationSetId = MutationSetId.New();
        var secondMutationSetId = MutationSetId.New();
        const string relativePath = "src/File.cs";
        var transcript = new ConversationTranscript(string.Empty);
        Assert.True(transcript.Apply(new PlanProposed(
            sessionId,
            occurredAt,
            "First plan.",
            firstRunId,
            CreateSingleFilePlan(relativePath, "The first expected outcome."))));
        Assert.True(transcript.Apply(new MutationProposalStarted(
            sessionId,
            occurredAt,
            firstRunId,
            AttemptNumber: 1,
            MaximumAttempts: 2)));
        Assert.False(transcript.Apply(new MutationSetProposed(
            sessionId,
            occurredAt,
            firstMutationSetId,
            Preview: null,
            MutationApprovalLevel.PolicyAutoApproved)));
        Assert.True(transcript.Apply(new PlanProposed(
            sessionId,
            occurredAt,
            "Second plan.",
            secondRunId,
            CreateSingleFilePlan(relativePath, "The second expected outcome."))));

        Assert.True(transcript.Apply(new MutationApplied(
            sessionId,
            occurredAt,
            MutationId.New(),
            firstMutationSetId,
            relativePath)));
        Assert.True(transcript.Apply(new MutationProposalStarted(
            sessionId,
            occurredAt,
            secondRunId,
            AttemptNumber: 1,
            MaximumAttempts: 2)));
        Assert.False(transcript.Apply(new MutationSetProposed(
            sessionId,
            occurredAt,
            secondMutationSetId,
            Preview: null,
            MutationApprovalLevel.PolicyAutoApproved)));
        Assert.True(transcript.Apply(new MutationApplied(
            sessionId,
            occurredAt,
            MutationId.New(),
            secondMutationSetId,
            "src/file.cs")));

        Assert.Contains("\u2514 The first expected outcome.", transcript.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\u2514 The second expected outcome.", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>Terminal run and mutation boundaries release plan-derived presentation correlation state.</summary>
    [Fact]
    public static void ConversationTranscript_TerminalBoundaries_ClearPresentationCorrelationState()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var runId = RunId.New();
        var mutationSetId = MutationSetId.New();
        const string relativePath = "src/File.cs";
        var plan = CreateSingleFilePlan(relativePath, "This stale detail must not render.") with
        {
            Risks = ["Requires review."],
        };
        var transcript = new ConversationTranscript(string.Empty);

        Assert.False(transcript.Apply(new PlanSanityCheckCompleted(
            sessionId,
            occurredAt,
            runId,
            Revision: 1,
            PlanRiskClassification.High,
            IssueCount: 1,
            BlockingIssueCount: 0,
            RepairableIssueCount: 1,
            AffectedFileCount: 1)));
        Assert.True(transcript.Apply(new PlanProposed(sessionId, occurredAt, plan.Summary, runId, plan)));
        Assert.True(transcript.Apply(new MutationProposalStarted(
            sessionId,
            occurredAt,
            runId,
            AttemptNumber: 1,
            MaximumAttempts: 2)));
        Assert.False(transcript.Apply(new MutationSetProposed(
            sessionId,
            occurredAt,
            mutationSetId,
            Preview: null,
            MutationApprovalLevel.PolicyAutoApproved)));
        Assert.False(transcript.Apply(new RunCompleted(sessionId, occurredAt, runId, Succeeded: true)));
        Assert.True(transcript.Apply(new PlanAutoApproved(
            sessionId,
            occurredAt,
            runId,
            ApprovalId.New(),
            PlanApprovalPolicy.AutoApproveAllValid,
            PlanRiskClassification.High,
            Revision: 1,
            "late approval projection")));
        Assert.True(transcript.Apply(new MutationApplied(
            sessionId,
            occurredAt,
            MutationId.New(),
            mutationSetId,
            relativePath)));

        Assert.DoesNotContain("Risk basis:", transcript.Text, StringComparison.Ordinal);
        Assert.Equal(
            1,
            transcript.Text.Split(
                "This stale detail must not render.",
                StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("Applied under the active approval policy", transcript.Text, StringComparison.Ordinal);

        var rolledBackRunId = RunId.New();
        var rolledBackMutationSetId = MutationSetId.New();
        Assert.True(transcript.Apply(new PlanProposed(
            sessionId,
            occurredAt,
            "Rollback plan.",
            rolledBackRunId,
            CreateSingleFilePlan(relativePath, "Rolled-back detail must not render."))));
        Assert.True(transcript.Apply(new MutationProposalStarted(
            sessionId,
            occurredAt,
            rolledBackRunId,
            AttemptNumber: 1,
            MaximumAttempts: 2)));
        Assert.False(transcript.Apply(new MutationSetProposed(
            sessionId,
            occurredAt,
            rolledBackMutationSetId,
            Preview: null,
            MutationApprovalLevel.PolicyAutoApproved)));
        Assert.True(transcript.Apply(new MutationSetRolledBack(
            sessionId,
            occurredAt,
            rolledBackMutationSetId,
            [relativePath])));
        Assert.True(transcript.Apply(new MutationApplied(
            sessionId,
            occurredAt,
            MutationId.New(),
            rolledBackMutationSetId,
            relativePath)));

        Assert.Equal(
            1,
            transcript.Text.Split(
                "Rolled-back detail must not render.",
                StringSplitOptions.None).Length - 1);
    }

    /// <summary>Manually reviewed mutation previews retain the review prompt guidance.</summary>
    [Fact]
    public static void ConversationTranscript_ManualMutationReview_IncludesManualReviewPrompt()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var preview = new MutationPreview(
            MutationSetId.New(),
            $"@@ -1 +1 @@{Environment.NewLine}-old{Environment.NewLine}+new{Environment.NewLine}",
            [],
            AddedLines: 1,
            RemovedLines: 1);
        var transcript = new ConversationTranscript(string.Empty);
        Assert.False(transcript.Apply(new TaskIntentRecorded(sessionId, occurredAt, "change a file")));

        Assert.True(transcript.Apply(new MutationSetProposed(
            sessionId,
            occurredAt,
            preview.MutationSetId,
            preview,
            MutationApprovalLevel.EntireSet)));

        Assert.Contains("Choose apply or discard at the mutation review prompt.", transcript.Text, StringComparison.Ordinal);
    }

    /// <summary>The UI dispatcher processes a flood in bounded redraw batches without losing events.</summary>
    [Fact]
    public static async Task UiEventDispatcher_Flooding_CoalescesWithoutLoss()
    {
        var dispatcher = new UiEventDispatcher(32);
        var rendered = 0;
        var drain = dispatcher.DrainAsync((batch, _) =>
        {
            rendered += batch.Count;
            return Task.CompletedTask;
        });
        var sessionId = SessionId.New();

        for (var index = 0; index < 5000; index++)
        {
            await dispatcher.QueueAsync(
                new ModelOutputObserved(sessionId, DateTimeOffset.UtcNow, "x"));
        }

        dispatcher.Complete();
        await drain;
        Assert.Equal(5000, rendered);
    }

    /// <summary>TUI and CLI drive the same handlers and render identical activity.</summary>
    [Fact]
    public static async Task ScriptedSession_TuiAndCli_HaveCommandParity()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession
        {
            Turns = [new ScriptedTurn { Text = "hello world" }],
        });
        var controller = new TuiController(new TuiPresenter(harness.Dispatcher, harness.Projections));
        await controller.OpenAsync("TUI");
        await controller.SubmitAsync("request");
        await controller.WaitForActiveRunAsync();
        var tui = await controller.RenderAsync();
        await using var writer = new StringWriter();

        var exitCode = await new HeadlessShell(
            harness.Dispatcher,
            harness.Projections,
            writer).RunAsync("CLI", "request");

        Assert.Equal(0, exitCode);
        Assert.Equal(tui.Workspace, writer.ToString());
    }

    /// <summary>Headless repository requests fail closed before submission when semantic tools cannot run.</summary>
    [Fact]
    public static async Task HeadlessShell_RepositoryRequestWithoutSemanticReadiness_DoesNotSubmitRequest()
    {
        var sessionId = SessionId.New();
        var workspaceId = WorkspaceId.New();
        var projections = new InMemoryProjectionStore();
        projections.ReplaceSession(new SessionProjection
        {
            Key = new ProjectionKey("session", sessionId.Value.ToString("D")),
            SessionId = sessionId,
            Name = "headless-semantic-timeout",
            Phase = RunPhase.RepositoryDiscovery,
            WorkspaceId = workspaceId,
            RepositoryPath = "C:\\repo",
            RepositoryTrust = RepositoryTrustLevel.TrustedBuild,
            SolutionPath = "C:\\repo\\Repo.sln",
            SemanticConfidence = SemanticConfidenceLevel.None,
            IsSemanticLoadComplete = true,
        });
        await using var writer = new StringWriter();
        var dispatcher = new SemanticUnavailableRepositoryDispatcher(sessionId, workspaceId);
        var shell = new HeadlessShell(dispatcher, projections, writer);

        var exitCode = await shell.RunRepositoryRequestAsync(
            "CLI",
            "C:\\repo",
            RepositoryTrustLevel.TrustedBuild,
            "Repo.sln",
            "inspect repository",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, exitCode);
        Assert.False(dispatcher.SubmitRequestObserved);
        Assert.Contains("Semantic tools require PartialCompilation", writer.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Headless direct-authorization exit status ignores failures from earlier runs.</summary>
    [Fact]
    public static async Task HeadlessShell_DirectAuthorizationExitCode_IsScopedToCurrentRun()
    {
        // Arrange
        var sessionId = SessionId.New();
        var previousRunId = RunId.New();
        var currentRunId = RunId.New();
        var projections = new InMemoryProjectionStore();
        projections.ReplaceSession(new SessionProjection
        {
            Key = new ProjectionKey("session", sessionId.Value.ToString("D")),
            SessionId = sessionId,
            Name = "headless-run-scope",
            Phase = RunPhase.Completion,
            ToolActivity =
            [
                new ToolActivityProjection(
                    ToolInvocationId.New(),
                    previousRunId,
                    "web_fetch",
                    "model",
                    IsCompleted: true,
                    Succeeded: false,
                    IsTruncated: false,
                    Error: "DirectAuthorizationRequired: approve this URL."),
                new ToolActivityProjection(
                    ToolInvocationId.New(),
                    currentRunId,
                    "read_file",
                    "model",
                    IsCompleted: true,
                    Succeeded: true,
                    IsTruncated: false,
                    Error: null),
            ],
        });
        await using var writer = new StringWriter();
        var shell = new HeadlessShell(
            new FixedRunDispatcher(sessionId, currentRunId),
            projections,
            writer);

        // Act
        var exitCode = await shell.RunAsync("CLI", "request");

        // Assert
        Assert.Equal(0, exitCode);
        var output = writer.ToString();
        Assert.DoesNotContain("approve this URL", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Tool web_fetch", output, StringComparison.Ordinal);
        Assert.Contains("Tool read_file: succeeded", output, StringComparison.Ordinal);
    }

    /// <summary>SQLite uses stable event names and restores the complete catalog in order.</summary>
    [Fact]
    public static async Task SqliteEventStore_CompleteCatalog_RoundTripsDurably()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"threadsmith-m1-{Guid.NewGuid():N}.db");
        try
        {
            var store = new SqliteEventStore($"Data Source={databasePath}");
            await store.InitializeAsync();
            IReadOnlyList<IDomainEvent> expected = TestEvents()
                .Where(domainEvent => domainEvent is not ModelReasoningObserved)
                .ToArray();
            foreach (var domainEvent in expected)
            {
                await store.AppendAsync(domainEvent);
            }

            var previousSchema = new SessionCreated(
                expected[0].SessionId,
                DateTimeOffset.UtcNow,
                "n-1")
            {
                SchemaVersion = 0,
            };
            await store.AppendAsync(previousSchema);

            var actual = await store.ReadAsync(expected[0].SessionId);

            Assert.Equal(
                expected.Select(item => item.GetType()).Append(typeof(SessionCreated)),
                actual.Select(item => item.GetType()));
            Assert.All(actual.Take(expected.Count), item => Assert.Equal(1, item.SchemaVersion));
            Assert.Equal(0, actual[^1].SchemaVersion);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    /// <summary>Authorization, JSON, environment, query, multiline, and control inputs are sanitized.</summary>
    [Theory]
    [InlineData("Authorization: Bearer abc123", "abc123")]
    [InlineData("Authorization=Basic Zm9vOmJhcg==", "Zm9vOmJhcg==")]
    [InlineData("{\"apiKey\":\"secret-json\"}", "secret-json")]
    [InlineData("{\"threadsmith_api_key\":\"secret-json\"}", "secret-json")]
    [InlineData("{\"github_token\":\"secret-json\"}", "secret-json")]
    [InlineData("THREAD_TOKEN=secret-env", "secret-env")]
    [InlineData("/?api_key=secret-query&x=1", "secret-query")]
    [InlineData("token: first\napi-key: second", "first")]
    [InlineData("password='secret phrase'", "secret phrase")]
    [InlineData("Server=db;User Id=me;Password=connection-secret;", "connection-secret")]
    [InlineData("https://user:url-secret@example.test/path", "url-secret")]
    [InlineData("credential sk-abcdefghijklmnopqrstuvwxyz", "sk-abcdefghijklmnopqrstuvwxyz")]
    public static void SecretOutputSanitizer_RedactsCompleteCredentials(
        string input,
        string secret)
    {
        var sanitizer = new SecretOutputSanitizer();

        var sanitized = sanitizer.Sanitize(input + "\u001b");

        Assert.DoesNotContain(secret, sanitized, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', sanitized);
    }

    /// <summary>Source-code identifier echoes remain visible while credential-shaped literals redact.</summary>
    [Fact]
    public static void SecretOutputSanitizer_PreservesSourceIdentifiersAndRedactsCredentialLiterals()
    {
        var sanitizer = new SecretOutputSanitizer();
        const string secret = "sk-abcdefghijklmnopqrstuvwxyz";
        var input = """
            public Task RunAsync(CancellationToken cancellationToken, string accessToken)
            {
                return UseAsync(accessToken: accessToken, cancellationToken: cancellationToken);
            }

            _ = UseAsync(accessToken: accessToken);
            _ = UseAsync(cancellation_token: cancellation_token);
            _ = UseAsync(accessToken: providedAccessToken);
            _ = new { password = "password" };

            var apiKey = "sk-abcdefghijklmnopqrstuvwxyz";
            """;

        var sanitized = sanitizer.Sanitize(input);

        Assert.Contains("CancellationToken cancellationToken", sanitized, StringComparison.Ordinal);
        Assert.Contains("accessToken: accessToken", sanitized, StringComparison.Ordinal);
        Assert.Contains("accessToken: accessToken)", sanitized, StringComparison.Ordinal);
        Assert.Contains("cancellationToken: cancellationToken", sanitized, StringComparison.Ordinal);
        Assert.Contains("cancellation_token: cancellation_token", sanitized, StringComparison.Ordinal);
        Assert.Contains("accessToken: providedAccessToken", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("\"password\"", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, sanitized, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", sanitized, StringComparison.Ordinal);
    }

    /// <summary>Credential-looking config and header echoes still redact outside source named arguments.</summary>
    [Fact]
    public static void SecretOutputSanitizer_RedactsNonSourceCredentialEchoes()
    {
        var sanitizer = new SecretOutputSanitizer();
        var input = """
            THREAD_TOKEN=token
            thread_token=secret
            github_token=secret
            threadsmith_api_key=secret
            {"threadsmith_api_key":"secret-json"}
            {"github_token":"secret-json"}
            token=token
            accessToken=accessToken
            Authorization: authorization
            """;

        var sanitized = sanitizer.Sanitize(input);

        Assert.DoesNotContain("THREAD_TOKEN=token", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("thread_token=secret", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("github_token=secret", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("threadsmith_api_key=secret", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("{\"threadsmith_api_key\":\"secret-json\"}", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("{\"github_token\":\"secret-json\"}", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("token=token", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("accessToken=accessToken", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization: authorization", sanitized, StringComparison.Ordinal);
        Assert.Equal(9, sanitized.Split("[REDACTED]", StringSplitOptions.None).Length - 1);
    }

    private static IEnumerable<RunPhase[]> GetLegalTransitionPaths()
    {
        yield return new[] { RunPhase.RepositoryDiscovery };
        yield return new[] { RunPhase.EvidenceCollection };
        yield return new[] { RunPhase.RepositoryDiscovery, RunPhase.EvidenceCollection };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.Completion };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.Completion };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.Completion };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.ImplementationPreparing, RunPhase.Completion };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.ImplementationPreparing, RunPhase.ImplementationModelTurn, RunPhase.MutationProposed, RunPhase.MutationStaged, RunPhase.AwaitingMutationApproval, RunPhase.BaselineValidation, RunPhase.MutationApplyPending, RunPhase.Mutation, RunPhase.Compilation, RunPhase.Testing, RunPhase.Verification, RunPhase.CompletionPending, RunPhase.Completion };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.ImplementationPreparing, RunPhase.ImplementationModelTurn, RunPhase.MutationProposed, RunPhase.MutationStaged, RunPhase.AwaitingMutationApproval, RunPhase.BaselineValidation, RunPhase.MutationApplyPending, RunPhase.Mutation, RunPhase.Compilation, RunPhase.CorrectionPending, RunPhase.CorrectionModelTurn, RunPhase.CompletionPending, RunPhase.Completion };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.ImplementationPreparing, RunPhase.ImplementationModelTurn, RunPhase.MutationProposed, RunPhase.MutationStaged, RunPhase.AwaitingMutationApproval, RunPhase.BaselineValidation, RunPhase.MutationApplyPending, RunPhase.Mutation, RunPhase.Compilation, RunPhase.CorrectionPending, RunPhase.CorrectionModelTurn, RunPhase.MutationProposed };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.ImplementationPreparing, RunPhase.ImplementationModelTurn, RunPhase.MutationProposed, RunPhase.MutationStaged, RunPhase.AwaitingMutationApproval, RunPhase.BaselineValidation, RunPhase.MutationApplyPending, RunPhase.Mutation, RunPhase.Compilation, RunPhase.Testing, RunPhase.CorrectionPending, RunPhase.CompletionPending, RunPhase.Completion };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.ImplementationPreparing, RunPhase.ImplementationModelTurn, RunPhase.MutationProposed, RunPhase.MutationStaged, RunPhase.AwaitingMutationApproval, RunPhase.BaselineValidation, RunPhase.MutationApplyPending, RunPhase.Mutation, RunPhase.Compilation, RunPhase.Testing, RunPhase.Verification, RunPhase.AwaitingAcceptance };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation, RunPhase.AwaitingMutationApproval };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation, RunPhase.AwaitingMutationApproval, RunPhase.Mutation };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation, RunPhase.AwaitingMutationApproval, RunPhase.Mutation, RunPhase.Compilation };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation, RunPhase.AwaitingMutationApproval, RunPhase.Mutation, RunPhase.RolledBack };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation, RunPhase.AwaitingMutationApproval, RunPhase.Mutation, RunPhase.Compilation, RunPhase.Testing };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation, RunPhase.AwaitingMutationApproval, RunPhase.Mutation, RunPhase.Compilation, RunPhase.RolledBack };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation, RunPhase.AwaitingMutationApproval, RunPhase.Mutation, RunPhase.Compilation, RunPhase.Testing, RunPhase.Verification };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation, RunPhase.AwaitingMutationApproval, RunPhase.Mutation, RunPhase.Compilation, RunPhase.Testing, RunPhase.RolledBack };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation, RunPhase.AwaitingMutationApproval, RunPhase.Mutation, RunPhase.Compilation, RunPhase.Testing, RunPhase.Verification, RunPhase.AwaitingAcceptance };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation, RunPhase.AwaitingMutationApproval, RunPhase.Mutation, RunPhase.Compilation, RunPhase.Testing, RunPhase.Verification, RunPhase.AwaitingAcceptance, RunPhase.Completion };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.ChangePlanning, RunPhase.AwaitingPlanApproval, RunPhase.MutationPreparation, RunPhase.AwaitingMutationApproval, RunPhase.Mutation, RunPhase.Compilation, RunPhase.Testing, RunPhase.Verification, RunPhase.AwaitingAcceptance, RunPhase.RolledBack };
        yield return new[] { RunPhase.Failed };
        yield return new[] { RunPhase.Cancelled };
    }

    private static async Task<string> CollectAsync(IModelProvider provider, int seed)
    {
        var chunks = await CollectChunksAsync(provider, new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "test",
            Seed = seed,
        });
        var builder = new StringBuilder();
        foreach (var chunk in chunks)
        {
            builder.Append(JsonSerializer.Serialize(chunk));
        }

        return builder.ToString();
    }

    private static int CountOccurrences(string text, string value)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var count = 0;
        for (var index = 0; index < text.Length;)
        {
            var match = text.IndexOf(value, index, StringComparison.Ordinal);
            if (match < 0)
            {
                break;
            }

            count++;
            index = match + value.Length;
        }

        return count;
    }

    private static ModelProfile CreateReasoningProfile(
        ModelProfileId id,
        string name,
        ReasoningLevel[] supportedLevels)
    {
        return new()
        {
            Id = id,
            Name = name,
            Provider = "openai-compatible",
            Endpoint = new Uri("https://models.example/v1/chat/completions"),
            ModelId = "reasoning-model",
            ContextWindow = 128000,
            MaximumOutputTokens = 4096,
            Capabilities = new ModelCapabilitySet { Streaming = true },
            Cost = new ModelCostMetadata(),
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            SupportedReasoningLevels = supportedLevels,
            RetryPolicy = new ModelRetryPolicy { MaxAttempts = 1, Delay = TimeSpan.Zero },
        };
    }

    private sealed class LeadingWhitespaceModelProvider : IModelProvider
    {
        private readonly int _trailingChunkCount;

        private readonly TaskCompletionSource _releaseAnswer = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _whitespaceEmitted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WhitespaceEmitted => _whitespaceEmitted.Task;

        public LeadingWhitespaceModelProvider(int trailingChunkCount = 0)
        {
            _trailingChunkCount = trailingChunkCount;
        }

        public void ReleaseAnswer()
        {
            _releaseAnswer.TrySetResult();
        }

        public IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            return Stream2Async();

            async IAsyncEnumerable<ModelChunk> Stream2Async()
            {
                yield return new ModelChunk { Text = "\n\n" };
                _whitespaceEmitted.TrySetResult();
                await _releaseAnswer.Task.WaitAsync(cancellationToken);
                yield return new ModelChunk { Text = "answer" };
                for (var index = 0; index < _trailingChunkCount; index++)
                {
                    yield return new ModelChunk { Text = index.ToString(CultureInfo.InvariantCulture) };
                }
            }
        }
    }

    private sealed class FailingGitQueryService : IGitQueryService
    {
        private readonly Exception _failure;

        public FailingGitQueryService(Exception failure)
        {
            _failure = failure;
        }

        /// <inheritdoc />
        public Task<string?> GetCurrentBranchAsync(
            string repositoryPath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<string?>(_failure);
        }

        /// <inheritdoc />
        public Task<string?> GetRevisionAsync(
            string repositoryPath,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public Task<GitDiffResult> DiffAsync(
            string repositoryPath,
            GitDiffRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public Task<GitLogResult> LogAsync(
            string repositoryPath,
            GitLogRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public Task<GitShowResult> ShowAsync(
            string repositoryPath,
            GitShowRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public Task<GitBlameResult> BlameAsync(
            string repositoryPath,
            GitBlameRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public Task<GitBranchComparisonResult> CompareBranchesAsync(
            string repositoryPath,
            GitBranchComparisonRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class FakeConsoleSurface : IConsoleSurface
    {
        private readonly Lock _gate = new();
        private readonly Queue<ConsoleInput> _inputs;
        private readonly StringBuilder _output = new();
        private readonly Queue<int> _selections = [];
        private readonly List<string> _statuses = [];
        private readonly List<string> _activeStatuses = [];
        private readonly List<TuiTextSegment> _segments = [];
        private readonly List<string> _lifecycle = [];
        private readonly List<string> _operations = [];
        private readonly List<TuiOutputItem> _outputItems = [];
        private readonly List<string> _sessionStatuses = [];
        private readonly List<string> _writes = [];
        private readonly int _statusWidth;
        private readonly Exception? _statusFailure;
        private readonly bool _suppressSessionStatus;
        private readonly TaskCompletionSource _statusStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private int _activeStatusCount;

        public FakeConsoleSurface(
            IEnumerable<string> inputs,
            IEnumerable<int>? selections = null,
            int statusWidth = 120,
            bool suppressSessionStatus = false,
            Exception? statusFailure = null)
        {
            _statusWidth = statusWidth;
            _suppressSessionStatus = suppressSessionStatus;
            _statusFailure = statusFailure;
            _inputs = new Queue<ConsoleInput>(inputs.Select(text => new ConsoleInput(
                true,
                text,
                CancellationToken.None)));
            if (selections is not null)
            {
                foreach (var selection in selections)
                {
                    _selections.Enqueue(selection);
                }
            }
        }

        public string? ActiveThemeId { get; private set; }

        public bool IsStatusActive
        {
            get
            {
                lock (_gate)
                {
                    return _activeStatusCount > 0;
                }
            }
        }

        public string Output
        {
            get
            {
                lock (_gate)
                {
                    return _output.ToString();
                }
            }
        }

        public IReadOnlyList<string> Lifecycle
        {
            get
            {
                lock (_gate)
                {
                    return _lifecycle.ToArray();
                }
            }
        }

        public IReadOnlyList<string> Operations
        {
            get
            {
                lock (_gate)
                {
                    return _operations.ToArray();
                }
            }
        }

        public IReadOnlyList<TuiOutputItem> OutputItems
        {
            get
            {
                lock (_gate)
                {
                    return _outputItems.ToArray();
                }
            }
        }

        public IReadOnlyList<string> SessionStatuses
        {
            get
            {
                lock (_gate)
                {
                    return _sessionStatuses.ToArray();
                }
            }
        }

        public IReadOnlyList<string> Statuses
        {
            get
            {
                lock (_gate)
                {
                    return _statuses.ToArray();
                }
            }
        }

        public IReadOnlyList<string> ActiveStatuses
        {
            get
            {
                lock (_gate)
                {
                    return _activeStatuses.ToArray();
                }
            }
        }

        public Task StatusStarted => _statusStarted.Task;

        public IReadOnlyList<string> Writes
        {
            get
            {
                lock (_gate)
                {
                    return _writes.ToArray();
                }
            }
        }

        public IReadOnlyList<TuiTextSegment> Segments
        {
            get
            {
                lock (_gate)
                {
                    return _segments.ToArray();
                }
            }
        }

        /// <inheritdoc />
        public Task SetPromptAsync(
            string prompt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task SetThemeAsync(ConfiguredTheme theme, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActiveThemeId = theme.Theme.Id;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task ShowSessionStatusAsync(
            TuiSessionStatus status,
            string separator,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(status);
            cancellationToken.ThrowIfCancellationRequested();
            if (_suppressSessionStatus)
            {
                return Task.CompletedTask;
            }

            var rendered = TuiSessionStatusFormatter.Format(status, _statusWidth, separator);
            if (string.IsNullOrEmpty(rendered))
            {
                return Task.CompletedTask;
            }

            lock (_gate)
            {
                _operations.Add("status");
                _sessionStatuses.Add(rendered);
                _writes.Add(rendered + Environment.NewLine);
                _output.AppendLine(rendered);
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<ConsoleInput> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _operations.Add("read");
                return Task.FromResult(_inputs.Count > 0
                    ? _inputs.Dequeue()
                    : new ConsoleInput(false, string.Empty, CancellationToken.None));
            }
        }

        /// <inheritdoc />
        public Task<int> SelectAsync(
            string title,
            IReadOnlyList<string> choices,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _output.AppendLine(title);
                for (var index = 0; index < choices.Count; index++)
                {
                    _output.AppendLine($"{index + 1}. {choices[index]}");
                }

                return Task.FromResult(_selections.Count > 0
                    ? _selections.Dequeue()
                    : choices.Count - 1);
            }
        }

        /// <inheritdoc />
        public async Task ShowStatusUntilAsync(
            string text,
            Task operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            ArgumentNullException.ThrowIfNull(operation);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _statuses.Add(text);
                _activeStatuses.Add(text);
                _lifecycle.Add("activity-start:" + text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
                _activeStatusCount++;
            }

            _statusStarted.TrySetResult();
            try
            {
                await operation.WaitAsync(cancellationToken);
                if (_statusFailure is not null)
                {
                    throw _statusFailure;
                }
            }
            finally
            {
                lock (_gate)
                {
                    _activeStatusCount--;
                    _activeStatuses.Remove(text);
                    _lifecycle.Add("activity-end:" + text.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
                }
            }
        }

        /// <inheritdoc />
        public async Task WriteOutputAsync(
            IReadOnlyList<TuiOutputItem> items,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _outputItems.AddRange(items);
                foreach (var item in items)
                {
                    _lifecycle.Add(item is TuiMarkdownOutput ? "output:markdown" : "output:segments");
                }
            }

            foreach (var item in items)
            {
                var segments =
                    PrettyPromptConsoleSurface.ProjectInteractiveOutputItem(item, _statusWidth);
                foreach (var segment in segments)
                {
                    await WriteAsync(segment.Text, segment.Role, CancellationToken.None);
                }
            }
        }

        /// <inheritdoc />
        public Task WriteAsync(
            string text,
            TuiTextRole role = TuiTextRole.Default,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _writes.Add(text);
                _segments.Add(new TuiTextSegment(text, role));
                _output.Append(text);
            }

            return Task.CompletedTask;
        }
    }

    private static async Task<IReadOnlyList<ModelChunk>> CollectChunksAsync(
        IModelProvider provider,
        ModelStreamRequest request)
    {
        var chunks = new List<ModelChunk>();
        await foreach (var chunk in provider.StreamAsync(request))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static async Task<string> CollectFixtureAsync(string fixtureDirectory, string fileName)
    {
        var script = FakeModelProvider.Load(
            await File.ReadAllTextAsync(Path.Combine(fixtureDirectory, fileName)));
        return await CollectAsync(new FakeModelProvider(script), 42);
    }

    private static IReadOnlyList<IDomainEvent> TestEvents()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        return
        [
            new SessionCreated(sessionId, occurredAt, "test"),
            new RepositoryOpened(sessionId, occurredAt, "repo"),
            new SolutionLoaded(sessionId, occurredAt, "solution"),
            new TaskIntentRecorded(sessionId, occurredAt, "intent"),
            new AcceptanceCriteriaRecorded(
                sessionId,
                occurredAt,
                [new AcceptanceCriterion("criterion")]),
            new EvidenceAdded(sessionId, occurredAt, EvidenceId.New(), "file"),
            new PlanProposed(sessionId, occurredAt, "plan"),
            new PlanSanityCheckCompleted(
                sessionId,
                occurredAt,
                RunId.New(),
                1,
                PlanRiskClassification.Low,
                IssueCount: 0,
                BlockingIssueCount: 0,
                RepairableIssueCount: 0,
                AffectedFileCount: 1),
            new PlanAutoApproved(
                sessionId,
                occurredAt,
                RunId.New(),
                ApprovalId.New(),
                PlanApprovalPolicy.ReviewRisky,
                PlanRiskClassification.Low,
                1,
                "auto"),
            new PlanApprovalPolicyChanged(
                sessionId,
                occurredAt,
                PlanApprovalPolicy.ReviewRisky,
                "session"),
            new PlanRevisionRequested(sessionId, occurredAt, RunId.New(), "revise"),
            new ContextAssembled(
                sessionId,
                occurredAt,
                new ContextInspectionProjection
                {
                    RunId = RunId.New(),
                    TokenBudget = 100,
                }),
            new ModelFallbackSelected(
                sessionId,
                occurredAt,
                RunId.New(),
                ModelProfileId.New(),
                ModelProfileId.New(),
                "provider",
                "model",
                Persisted: true),
            new ActiveTurnCompactionStarted(
                sessionId,
                occurredAt,
                RunId.New(),
                ModelProfileId.New(),
                BeforeInputTokens: 100,
                PressureTargetTokens: 75),
            new ActiveTurnCompactionCompleted(
                sessionId,
                occurredAt,
                RunId.New(),
                ModelProfileId.New(),
                ActiveTurnCompactionInspectionStatus.Completed,
                BeforeInputTokens: 100,
                AfterInputTokens: 50,
                DurationMilliseconds: 12),
            new ApprovalRequested(sessionId, occurredAt, ApprovalId.New(), "action"),
            new ApprovalGranted(sessionId, occurredAt, ApprovalId.New()),
            new ApprovalDenied(sessionId, occurredAt, ApprovalId.New(), "reason"),
            new ToolInvocationStarted(sessionId, occurredAt, ToolInvocationId.New(), "tool"),
            new ToolInvocationCompleted(sessionId, occurredAt, ToolInvocationId.New(), true),
            new SemanticCheckStarted(
                sessionId,
                occurredAt,
                RunId.New(),
                SemanticCheckId.New(),
                SemanticCheckPhase.PreMutation,
                "pre-mutation overlay syntax"),
            new SemanticCheckCompleted(
                sessionId,
                occurredAt,
                RunId.New(),
                SemanticCheckId.New(),
                SemanticCheckPhase.PreMutation,
                "pre-mutation overlay syntax",
                SemanticCheckOutcome.Completed,
                ElapsedMilliseconds: 12,
                Detail: "1 files, 0 diagnostics, 0 blocking"),
            new MutationProposalStarted(sessionId, occurredAt, RunId.New(), 1, 2),
            new MutationProposalRepairAttempted(sessionId, occurredAt, RunId.New(), 2, 2, "reason"),
            new ModelCorrectionAttempted(
                sessionId,
                occurredAt,
                RunId.New(),
                ModelCorrectionCategory.MutationProposal,
                1,
                3,
                "safe reason"),
            new PreMutationAnalysisCompleted(
                sessionId,
                occurredAt,
                RunId.New(),
                MutationSetId.New(),
                PreMutationGateDecision.PassedCheapGates,
                DiagnosticCount: 0,
                BlockingDiagnosticCount: 0,
                OmissionCount: 1,
                SemanticConfidenceLevel.PartialCompilation),
            new SemanticMutationWarningObserved(
                sessionId,
                occurredAt,
                RunId.New(),
                SemanticConfidenceLevel.PartialCompilation,
                "warning"),
            new MutationSetProposed(sessionId, occurredAt, MutationSetId.New()),
            new MutationApplied(sessionId, occurredAt, MutationId.New()),
            new MutationSetRolledBack(sessionId, occurredAt, MutationSetId.New(), ["src/Example.cs"]),
            new BuildStarted(sessionId, occurredAt, RunId.New()),
            new DiagnosticObserved(sessionId, occurredAt, "TS1", "message"),
            new TestRunCompleted(sessionId, occurredAt, 1, 0),
            new ExtensionDiscovered(sessionId, occurredAt, ExtensionId.New()),
            new ExtensionActivated(sessionId, occurredAt, ExtensionId.New()),
            new ExtensionLoadFailed(sessionId, occurredAt, ExtensionId.New(), "reason"),
            new ExtensionDraining(sessionId, occurredAt, ExtensionId.New()),
            new ExtensionUnloaded(sessionId, occurredAt, ExtensionId.New()),
            new ExtensionUnloadFailed(sessionId, occurredAt, ExtensionId.New(), "reason"),
            new SemanticConfidenceChanged(sessionId, occurredAt, "FullSemantic"),
            new SemanticLoadCompleted(sessionId, occurredAt, WorkspaceId.New(), "FullSemantic"),
            new RunTransitioned(sessionId, occurredAt, RunId.New(), RunPhase.Intake, RunPhase.EvidenceCollection),
            new RunTransitionFailed(sessionId, occurredAt, RunId.New(), RunPhase.Intake, RunPhase.Testing, "reason"),
            new ModelOutputObserved(sessionId, occurredAt, "text"),
            new ModelReasoningObserved(sessionId, occurredAt, "reasoning text"),
            new ConversationMessageArchived(
                sessionId,
                occurredAt,
                ConversationMessageId.New(),
                RunId.New(),
                1,
                ConversationRole.User,
                ConversationSensitivity.None,
                false),
            new ConversationModeChanged(
                sessionId,
                occurredAt,
                ConversationContextMode.ConversationAware),
            new ConversationMemoryPromoted(
                sessionId,
                occurredAt,
                ConversationMemoryId.New(),
                ConversationMemoryKind.Decision),
            new ConversationMemorySuperseded(
                sessionId,
                occurredAt,
                ConversationMemoryId.New(),
                ConversationMemoryId.New()),
            new ConversationMemoryInvalidated(
                sessionId,
                occurredAt,
                ConversationMemoryId.New(),
                "reason"),
            new RepositoryMemoryRemembered(
                sessionId,
                occurredAt,
                "repo:fixture",
                RepositoryMemoryId.New(),
                RepositoryMemoryKind.WorkflowFact,
                RepositoryMemoryAuthority.UserAuthored),
            new RepositoryMemorySuperseded(
                sessionId,
                occurredAt,
                "repo:fixture",
                RepositoryMemoryId.New(),
                RepositoryMemoryId.New()),
            new RepositoryMemoryValidityChanged(
                sessionId,
                occurredAt,
                "repo:fixture",
                RepositoryMemoryId.New(),
                RepositoryMemoryValidity.Forgotten,
                "reason"),
            new ConversationSummarySnapshotReplaced(sessionId, occurredAt, 1, 1, 1),
            new ExecutionCheckpointWritten(sessionId, occurredAt, RunId.New(), ExecutionCheckpointPhase.ImplementationPreparing, "continue"),
            new ExecutionSideEffectRecorded(sessionId, occurredAt, RunId.New(), Guid.NewGuid(), "mutation_commit", ExecutionOperationState.Pending),
            new ExecutionResumeRecorded(sessionId, occurredAt, RunId.New(), true, "accepted"),
            new ExecutionOutcomeRecorded(sessionId, occurredAt, RunId.New(), ExecutionCheckpointPhase.Completed),
            new DelegationCheckpointWritten(
                sessionId,
                occurredAt,
                DelegationId.New(),
                RunId.New(),
                DelegationCheckpointPhase.ResearchJoined,
                1,
                "synthesize findings"),
            new AgentRunLifecycleObserved(
                sessionId,
                occurredAt,
                DelegationId.New(),
                AgentAssignmentId.New(),
                RunId.New(),
                AgentRole.Explorer,
                AgentRunStatus.Completed,
                1,
                "completed"),
            new SkillCatalogRefreshed(sessionId, occurredAt, 1, 3),
            new SkillVerificationDecided(
                sessionId,
                occurredAt,
                new SkillId("analyzer-fix"),
                "1.0.0",
                new string('a', 64),
                SkillScope.Maintained,
                SkillVerificationState.Maintained,
                "verified"),
            new SkillWorkflowCheckpointWritten(
                sessionId,
                occurredAt,
                SkillInvocationId.New(),
                SkillWorkflowId.New(),
                new SkillId("analyzer-fix"),
                "1.0.0",
                new string('a', 64),
                SkillInvocationStatus.AwaitingHost,
                1,
                "resolve host action"),
            new SkillInvocationCompleted(
                sessionId,
                occurredAt,
                SkillInvocationId.New(),
                new SkillId("pr-review"),
                "1.0.0",
                new string('b', 64),
                SkillInvocationStatus.Completed,
                "completed"),
            new HookInvocationStartedEvent(
                sessionId,
                occurredAt,
                HookInvocationId.New(),
                HookPoint.BeforeToolInvocation,
                new HookHandlerId("policy-check"),
                Guid.NewGuid()),
            new HookInvocationCompletedEvent(
                sessionId,
                occurredAt,
                HookInvocationId.New(),
                HookPoint.BeforeToolInvocation,
                new HookHandlerId("policy-check"),
                Guid.NewGuid(),
                HookInvocationStatus.Acknowledged,
                HookDecisionKind.Continue,
                null),
            new HookRepositoryApprovalChanged(
                sessionId,
                occurredAt,
                "repository",
                new HookHandlerId("policy-check"),
                new HookConfigurationDigest(new string('c', 64)),
                true),
            new RunCompleted(sessionId, occurredAt, RunId.New(), true),
        ];
    }

    private sealed record EchoCommand(string Value) : ICommand<string>;

    private sealed record UnhandledCommand : ICommand<string>;

    private sealed class RecordingMcpManagementHandler
        : ICommandHandler<ExecuteMcpManagementCommand, McpManagementResult>
    {
        internal List<McpManagementRequest> Requests { get; } = [];

        public Task<McpManagementResult> HandleAsync(
            ExecuteMcpManagementCommand command,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(command.Request);
            return Task.FromResult(new McpManagementResult
            {
                Action = command.Request.Action,
                Succeeded = true,
                ExitCode = 0,
                FailureKind = McpManagementFailureKind.None,
                Message = "0 active MCP capability descriptor(s).",
            });
        }
    }

    private static ImplementationPlan CreateSingleFilePlan(string relativePath, string expectedOutcome)
    {
        return new ImplementationPlan
        {
            Revision = 1,
            Summary = "Change one file.",
            Steps =
            [
                new ImplementationPlanStep
                {
                    StepId = StepId.New(),
                    Title = "Update file",
                    Description = "Apply the requested edit.",
                    FileIntents =
                    [
                        new PlanFileIntent
                        {
                            Path = relativePath,
                            Kind = PlanFileChangeKind.Modify,
                        },
                    ],
                    ExpectedOutcome = expectedOutcome,
                },
            ],
        };
    }

    private static async Task StageAndApplyMutationAsync(
        TuiController controller,
        PostApplyValidationFixture fixture)
    {
        await controller.OpenAsync("TUI");
        await controller.SelectSolutionAsync(fixture.WorkspaceId, fixture.SolutionPath);
        fixture.Projections.Session = fixture.CreatePlanProjection();
        var runId = await controller.SubmitAsync("request");
        Assert.Equal(fixture.RunId, runId);
        var staged = await controller.ApproveActivePlanAndProposeMutationSetAsync();
        Assert.NotNull(staged);
        _ = await controller.CommitMutationSetAsync(
            fixture.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = fixture.ApprovalId,
            });
        Assert.Equal(fixture.RunId, controller.BackgroundValidationRunId);
    }

    private sealed class PostApplyValidationFixture
    {
        public PostApplyValidationFixture(bool throwOnResume)
        {
            SessionId = SessionId.New();
            RunId = RunId.New();
            WorkspaceId = WorkspaceId.New();
            MutationSetId = MutationSetId.New();
            ApprovalId = ApprovalId.New();
            RepositoryPath = Path.Combine(Path.GetTempPath(), $"threadsmith-tui-{Guid.NewGuid():N}");
            SolutionPath = Path.Combine(RepositoryPath, "App.csproj");
            ResumePhase = ExecutionCheckpointPhase.Completed;
            Projections = new FixedProjectionStore();
            Dispatcher = new PostApplyValidationDispatcher(this, throwOnResume);
        }

        public SessionId SessionId { get; }

        public RunId RunId { get; }

        public WorkspaceId WorkspaceId { get; }

        public MutationSetId MutationSetId { get; }

        public ApprovalId ApprovalId { get; }

        public ExecutionCheckpointPhase ResumePhase { get; set; }

        public string RepositoryPath { get; }

        public string SolutionPath { get; }

        public FixedProjectionStore Projections { get; }

        public PostApplyValidationDispatcher Dispatcher { get; }

        public SessionProjection CreatePlanProjection()
        {
            return new()
            {
                Key = new ProjectionKey("session", SessionId.Value.ToString("D")),
                SessionId = SessionId,
                Name = "TUI",
                Intent = "request",
                Plan = new PlanProjection(
                RunId,
                ApprovalId.New(),
                new ImplementationPlan
                {
                    Summary = "Change one file.",
                    Steps =
                    [
                        new ImplementationPlanStep
                        {
                            StepId = StepId.New(),
                            Title = "Edit file",
                            Description = "Edit one file.",
                            FileIntents =
                            [
                                new PlanFileIntent
                                {
                                    Kind = PlanFileChangeKind.Modify,
                                    Path = "src/Example.cs",
                                },
                            ],
                            ExpectedOutcome = "File changed.",
                        },
                    ],
                },
                PlanReviewStatus.Pending),
            };
        }

        public WorkspaceBaseline CreateBaseline()
        {
            return new(
            WorkspaceId,
            RepositoryPath,
            DateTimeOffset.UtcNow,
            [],
            SelectedSolutionPath: SolutionPath,
            TrustLevel: RepositoryTrustLevel.TrustedBuild);
        }

        public StagedMutationSet CreateStagedMutationSet()
        {
            var mutationId = MutationId.New();
            var mutationSet = new MutationSet
            {
                MutationSetId = MutationSetId,
                SessionId = SessionId,
                RunId = RunId,
                WorkspaceId = WorkspaceId,
                BaselineCapturedAt = DateTimeOffset.UtcNow,
                Mutations =
                [
                    new Mutation
                    {
                        MutationId = mutationId,
                        Type = MutationType.ReplaceText,
                        RelativePath = "src/Example.cs",
                        ExpectedText = "old",
                        ReplacementText = "new",
                    },
                ],
                Rationale = "Test mutation.",
            };
            var preview = new MutationPreview(
                MutationSetId,
                "diff",
                [new MutationDiff(mutationId, "src/Example.cs", "diff", true)],
                AddedLines: 1,
                RemovedLines: 1);
            return new StagedMutationSet(
                mutationSet,
                preview,
                new ConflictReport(MutationSetId, []),
                ApprovalId);
        }

        public ExecutionContinuation CreateContinuation(ExecutionCheckpointPhase phase)
        {
            return new()
            {
                SessionId = SessionId,
                RunId = RunId,
                WorkspaceId = WorkspaceId,
                PlanRevision = 1,
                PlanHash = "plan",
                Phase = phase,
                DiagnosticBaselineIdentity = "baseline",
                MutationBaselineIdentity = "mutation-baseline",
                MutationSetId = MutationSetId,
                NextAction = "continue",
                RecordedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    private sealed class FixedProjectionStore : IProjectionStore
    {
        public SessionProjection? Session { get; set; }

        public Task ApplyAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<TProjection?> GetAsync<TProjection>(
            ProjectionKey key,
            CancellationToken cancellationToken = default)
            where TProjection : class, IProjection
        {
            return Task.FromResult(Session as TProjection);
        }
    }

    private sealed class PostApplyValidationDispatcher : ICommandDispatcher
    {
        private readonly PostApplyValidationFixture _fixture;

        public PostApplyValidationDispatcher(
            PostApplyValidationFixture fixture,
            bool throwOnResume)
        {
            _fixture = fixture;
            ThrowOnResume = throwOnResume;
        }

        public bool ThrowOnResume { get; set; }

        public Task<TResponse> DispatchAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object response = command switch
            {
                CreateSessionCommand => _fixture.SessionId,
                SelectSolutionCommand => new SolutionSelectionResult(
                    _fixture.WorkspaceId,
                    _fixture.SolutionPath,
                    ["net10.0"]),
                RecordBaselineCommand => _fixture.CreateBaseline(),
                SubmitRequestCommand => _fixture.RunId,
                ApprovePlanCommand => true,
                GetExecutionMutationCommand => _fixture.CreateStagedMutationSet(),
                GetMutationReviewCommand => _fixture.CreateStagedMutationSet(),
                PrepareExecutionValidationCommand => _fixture.CreateContinuation(
                    ExecutionCheckpointPhase.MutationApprovalPending),
                ApplyExecutionMutationCommand => new ExecutionApplyResult
                {
                    SessionId = _fixture.SessionId,
                    RunId = _fixture.RunId,
                    MutationSetId = _fixture.MutationSetId,
                    ChangedFiles = ["src/Example.cs"],
                    Continuation = _fixture.CreateContinuation(ExecutionCheckpointPhase.MutationApplied),
                },
                RollbackMutationSetCommand => new MutationRollbackResult(
                    _fixture.MutationSetId,
                    [],
                    new ConflictReport(_fixture.MutationSetId, [])),
                CancelRunCommand => true,
                WaitForRunCommand => true,
                ResumeRunCommand when ThrowOnResume => throw new InvalidOperationException(
                    "Simulated post-apply validation failure."),
                ResumeRunCommand => _fixture.CreateContinuation(_fixture.ResumePhase),
                _ => throw new InvalidOperationException($"Unexpected command {command.GetType().Name}."),
            };
            return Task.FromResult((TResponse)response);
        }
    }

    private sealed class SemanticUnavailableRepositoryDispatcher : ICommandDispatcher
    {
        private readonly SessionId _sessionId;
        private readonly WorkspaceId _workspaceId;

        public SemanticUnavailableRepositoryDispatcher(SessionId sessionId, WorkspaceId workspaceId)
        {
            _sessionId = sessionId;
            _workspaceId = workspaceId;
        }

        public bool SubmitRequestObserved { get; private set; }

        public Task<TResponse> DispatchAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = command switch
            {
                CreateSessionCommand => _sessionId,
                OpenRepositoryCommand open => new RepositoryOpenResult(
                    _workspaceId,
                    open.RepositoryPath,
                    new RepositoryTrustState(open.RepositoryPath, open.RequestedTrust, DateTimeOffset.UtcNow),
                    new RepositoryConfigurationSnapshot(null, ["."], []),
                    new MsBuildEnvironmentSnapshot(
                        "dotnet",
                        "10.0.100",
                        null,
                        null,
                        null,
                        "Debug",
                        "AnyCPU",
                        RepositoryRestoreState.NotAttempted,
                        new Dictionary<string, string>()),
                    ["Repo.sln"]),
                SelectSolutionCommand select => new SolutionSelectionResult(
                    select.WorkspaceId,
                    Path.Combine("C:\\repo", select.SolutionPath),
                    ["net10.0"]),
                RecordBaselineCommand baseline => new WorkspaceBaseline(
                    baseline.WorkspaceId,
                    "C:\\repo",
                    DateTimeOffset.UtcNow,
                    []),
                SubmitRequestCommand => ObserveUnexpectedSubmitRequest(),
                _ => throw new InvalidOperationException($"Unexpected command {command.GetType().Name}."),
            };
            return Task.FromResult((TResponse)response);
        }

        private object ObserveUnexpectedSubmitRequest()
        {
            SubmitRequestObserved = true;
            return RunId.New();
        }
    }

    private sealed class FixedRunDispatcher : ICommandDispatcher
    {
        private readonly RunId _runId;
        private readonly SessionId _sessionId;

        public FixedRunDispatcher(SessionId sessionId, RunId runId)
        {
            _sessionId = sessionId;
            _runId = runId;
        }

        public Task<TResponse> DispatchAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            object response = command switch
            {
                CreateSessionCommand => _sessionId,
                SubmitRequestCommand => _runId,
                WaitForRunCommand => true,
                _ => throw new InvalidOperationException($"Unexpected command {command.GetType().Name}."),
            };
            return Task.FromResult((TResponse)response);
        }
    }

    private sealed class RecordingPlanApprovalPolicyHandler
        : IPlanApprovalPolicy,
            ICommandHandler<GetPlanApprovalPolicyCommand, PlanApprovalPolicy>,
            ICommandHandler<SetPlanApprovalPolicyCommand, PlanApprovalPolicy>
    {
        public PlanApprovalPolicy CurrentPolicy { get; private set; } = PlanApprovalPolicy.ReviewAll;

        public List<string> Scopes { get; } = [];

        public Task BindRepositoryAsync(
            string repositoryRoot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public PlanApprovalDecision Decide(
            PlanSanityCheckResult result,
            RepositoryTrustLevel trustLevel)
        {
            ArgumentNullException.ThrowIfNull(result);
            return new PlanApprovalDecision
            {
                Kind = PlanApprovalDecisionKind.RequiresReview,
                Policy = CurrentPolicy,
                Risk = result.Risk,
                Reason = "test",
            };
        }

        public Task SetPolicyAsync(
            PlanApprovalPolicy policy,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentPolicy = policy;
            return Task.CompletedTask;
        }

        public Task<PlanApprovalPolicy> HandleAsync(
            GetPlanApprovalPolicyCommand command,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CurrentPolicy);
        }

        public Task<PlanApprovalPolicy> HandleAsync(
            SetPlanApprovalPolicyCommand command,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            cancellationToken.ThrowIfCancellationRequested();
            CurrentPolicy = command.Policy;
            Scopes.Add(command.Scope);
            return Task.FromResult(CurrentPolicy);
        }
    }

    private sealed class EchoHandler : ICommandHandler<EchoCommand, string>
    {
        private readonly List<string> _order;

        public EchoHandler(List<string> order)
        {
            _order = order;
        }

        public Task<string> HandleAsync(
            EchoCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _order.Add("handler");
            return Task.FromResult(command.Value);
        }
    }

    private sealed class RecordingMiddleware : ICommandMiddleware
    {
        private readonly string _name;
        private readonly List<string> _order;

        public RecordingMiddleware(string name, List<string> order)
        {
            _name = name;
            _order = order;
        }

        public async Task<TResponse> InvokeAsync<TResponse>(
            ICommand<TResponse> command,
            Func<CancellationToken, Task<TResponse>> next,
            CancellationToken cancellationToken = default)
        {
            _order.Add($"{_name}:before");
            var response = await next(cancellationToken);
            _order.Add($"{_name}:after");
            return response;
        }
    }

    private sealed class EntryRecordingMiddleware : ICommandMiddleware
    {
        private readonly string _name;
        private readonly List<string> _order;

        public EntryRecordingMiddleware(string name, List<string> order)
        {
            _name = name;
            _order = order;
        }

        public async Task<TResponse> InvokeAsync<TResponse>(
            ICommand<TResponse> command,
            Func<CancellationToken, Task<TResponse>> next,
            CancellationToken cancellationToken = default)
        {
            _order.Add($"{_name}:before");
            try
            {
                return await next(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _order.Add($"{_name}:cancelled");
                throw;
            }
        }
    }

    private sealed class FakeToolStateManager : IToolStateManager
    {
        private bool _optionalEnabled = true;

        public List<string> DisabledIds { get; } = [];

        public Task DisableAsync(string toolId, CancellationToken cancellationToken = default)
        {
            DisabledIds.Add(toolId);
            _optionalEnabled = false;
            return Task.CompletedTask;
        }

        public Task EnableAsync(string toolId, CancellationToken cancellationToken = default)
        {
            _optionalEnabled = true;
            return Task.CompletedTask;
        }

        public Task EnableAsync(
            string toolId,
            string expectedVersion,
            CancellationToken cancellationToken = default)
        {
            return EnableAsync(toolId, cancellationToken);
        }

        public Task GrantConsentAndEnableAsync(
            string toolId,
            bool retrievalDisclosureAcknowledged = false,
            bool currentMessageUrlDisclosureAcknowledged = false,
            CancellationToken cancellationToken = default)
        {
            _optionalEnabled = true;
            return Task.CompletedTask;
        }

        public bool RequiresCurrentMessageUrlConsent()
        {
            return false;
        }

        public IReadOnlyList<ToolStateEntry> GetAllStates()
        {
            return
            [
                new ToolStateEntry
                {
                    Id = "essential",
                    DisplayName = "Essential Read",
                    Category = ToolCategory.FileRead,
                    Source = "Built-in",
                    Enabled = true,
                    Essential = true,
                    ConsentRequired = false,
                },
                new ToolStateEntry
                {
                    Id = "optional",
                    DisplayName = "Optional Tool",
                    Category = ToolCategory.FileSearch,
                    Source = "Example Extension",
                    Enabled = _optionalEnabled,
                    Essential = false,
                    ConsentRequired = false,
                },
            ];
        }

        public bool IsEnabled(string toolId)
        {
            return !string.Equals(toolId, "optional", StringComparison.Ordinal) || _optionalEnabled;
        }

        public void Register(ToolDefinition definition)
        {
        }

        public void Unregister(string toolId)
        {
        }
    }

    private sealed class FakeMutationApprovalPolicy : IMutationApprovalPolicy
    {
        public MutationApprovalPolicy CurrentPolicy { get; private set; } = MutationApprovalPolicy.ReviewAll;

        public int LargeDiffThreshold => 500;

        public List<MutationApprovalPolicy> SelectedPolicies { get; } = [];

        public Task BindRepositoryAsync(
            string repositoryRoot,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public bool RequiresApproval(MutationRiskAssessment risk, bool isWithinPlan)
        {
            return true;
        }

        public Task SetPolicyAsync(
            MutationApprovalPolicy policy,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentPolicy = policy;
            SelectedPolicies.Add(policy);
            return Task.CompletedTask;
        }

        public void Validate(MutationSet mutations, string repositoryRoot)
        {
        }
    }

    private sealed class SessionHarness : IAsyncDisposable
    {
        private readonly IDomainEventSubscription _captureSubscription;
        private readonly IDomainEventSubscription _projectionSubscription;
        private readonly DomainEventStream _stream;

        private SessionHarness(
            DomainEventStream stream,
            IDomainEventSubscription projectionSubscription,
            IDomainEventSubscription captureSubscription,
            InMemoryProjectionStore projections,
            ICommandDispatcher dispatcher,
            ConcurrentBag<IDomainEvent> events,
            SessionUsageProjection usage)
        {
            _stream = stream;
            _projectionSubscription = projectionSubscription;
            _captureSubscription = captureSubscription;
            Projections = projections;
            Dispatcher = dispatcher;
            Events = events;
            Usage = usage;
        }

        public ICommandDispatcher Dispatcher { get; }

        public ConcurrentBag<IDomainEvent> Events { get; }

        public IDomainEventStream EventStream => _stream;

        public InMemoryProjectionStore Projections { get; }

        public SessionUsageProjection Usage { get; }

        public static Task<SessionHarness> CreateAsync(
            ScriptedSession script,
            TimeSpan? delay = null,
            IBudget? budget = null,
            IEnumerable<object>? additionalHandlers = null,
            IModelProvider? modelProvider = null)
        {
            var stream = new DomainEventStream();
            var projections = new InMemoryProjectionStore();
            var events = new ConcurrentBag<IDomainEvent>();
            var projectionSubscription = stream.Subscribe(projections.ApplyAsync);
            var captureSubscription = stream.Subscribe((domainEvent, _) =>
            {
                events.Add(domainEvent);
                return Task.CompletedTask;
            });
            var executionBudget = budget ?? new ExecutionBudget(
                new BudgetDimensions(100000, 1000, TimeSpan.FromHours(1)));
            var usage = new SessionUsageProjection();
            var application = new SessionApplication(
                stream,
                modelProvider ?? new FakeModelProvider(script, delay),
                executionBudget,
                new SecretOutputSanitizer(),
                NullLogger<SessionApplication>.Instance,
                sessionUsage: usage);
            var handlers = new List<object> { application };
            if (additionalHandlers is not null)
            {
                handlers.AddRange(additionalHandlers);
            }

            ICommandDispatcher dispatcher = new CommandDispatcher(handlers);
            return Task.FromResult(new SessionHarness(
                stream,
                projectionSubscription,
                captureSubscription,
                projections,
                dispatcher,
                events,
                usage));
        }

        public async ValueTask DisposeAsync()
        {
            await _captureSubscription.DisposeAsync();
            await _projectionSubscription.DisposeAsync();
            await _stream.DisposeAsync();
        }
    }
}
