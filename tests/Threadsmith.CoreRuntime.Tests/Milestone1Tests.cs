namespace Threadsmith.CoreRuntime.Tests;

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Cli;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Hooks;
using Threadsmith.Interaction.Agents;
using Threadsmith.Interaction.Commands;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Markdown;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Sessions;
using Threadsmith.Interaction.Themes;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Tui.TuiKit;
using Xunit;

/// <summary>Verifies the complete Milestone 1 command, event, shell, and durability contracts.</summary>
[Collection("TUIKit terminal")]
public static partial class Milestone1Tests
{
    /// <summary>Tips print every deployed line locally without a model run or archived conversation.</summary>
    [Fact]
    public static async Task InteractionCoordinator_TipsStayOutOfModelContext()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/tips", "/quit"]);
        var coordinator = CreateCoordinator(
            new InteractionPresenter(harness.Dispatcher, harness.Projections), harness.EventStream, surface);

        await coordinator.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        var tips = await File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory, "tips.txt"), TestContext.Current.CancellationToken);
        foreach (var tip in tips.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            Assert.Contains($"- {tip}\n", surface.Output, StringComparison.Ordinal);
        }

        Assert.Empty(harness.Events.OfType<TaskIntentRecorded>());
        Assert.Empty(harness.Events.OfType<ConversationMessageArchived>());
    }

    /// <summary>Gets every legal non-terminal transition path used by the transition matrix.</summary>
    public static TheoryData<RunPhase[]> LegalTransitionPaths => [.. GetLegalTransitionPaths()];

    /// <summary>A cancelled semantic selection maps to the coordinator's negative sentinel.</summary>
    [Fact]
    public static async Task InteractionSessionSurface_CancelledSelection_ReturnsNegativeSentinel()
    {
        var surface = new RecordingInteractionSurface(
            [],
            new InteractionSelectionResult(null, IsCancelled: true));

        var selected = await new InteractionSessionSurface(surface).SelectAsync(
            "Choose",
            ["First"],
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, selected);
    }

    /// <summary>The shared URL approval prompt exposes all durations and maps cancellation to denial.</summary>
    [Theory]
    [InlineData("0", DirectFetchApprovalOutcome.Denied)]
    [InlineData("1", DirectFetchApprovalOutcome.Approved)]
    [InlineData("2", DirectFetchApprovalOutcome.ApprovedForSession)]
    [InlineData("3", DirectFetchApprovalOutcome.ApprovedForUser)]
    [InlineData(null, DirectFetchApprovalOutcome.Denied)]
    public static async Task DirectFetchApprovalInteraction_ChoicesMapToDurationAsync(string? selectedId, DirectFetchApprovalOutcome expected)
    {
        // Arrange
        var surface = new RecordingInteractionSurface([], new InteractionSelectionResult(selectedId, IsCancelled: selectedId is null));
        var request = new DirectFetchApprovalRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            ToolInvocationId = ToolInvocationId.New(),
            Origin = "https://example.com",
            Path = "/[REDACTED]",
            UrlDigest = new string('a', 64),
            QueryPresent = true,
        };

        // Act
        var outcome = await DirectFetchApprovalInteraction.RequestAsync(new InteractionSessionSurface(surface), request);

        // Assert
        Assert.Equal(expected, outcome);
        var selection = Assert.Single(surface.SelectionRequests);
        Assert.Equal(["Deny", "Approve one attempt", "Approve for this session", "Add to user allowed list"], selection.Options.Select(option => option.Label));
        Assert.Contains("exact hostname", selection.Title, StringComparison.Ordinal);
        Assert.Contains("future sessions", selection.Title, StringComparison.Ordinal);
    }

    /// <summary>The public command list cannot be cast back to and mutate its backing array.</summary>
    [Fact]
    public static void InteractiveCommandCatalog_All_DoesNotExposeMutableBackingArray()
    {
        var commands = InteractiveCommandCatalog.All;

        Assert.IsNotType<InteractiveCommandDescriptor[]>(commands);
        var collection = Assert.IsAssignableFrom<IList<InteractiveCommandDescriptor>>(commands);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection[0] = commands[^1]);
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
            Assert.Equivalent(expected, polymorphic, strict: true);
            Assert.Equal(polymorphicJson, JsonSerializer.Serialize<IDomainEvent>(polymorphic));
            Assert.Equal(1, polymorphic.SchemaVersion);

            var eventName = DomainEventJson.GetDiscriminator(expected);
            var durable = DomainEventJson.Deserialize(
                eventName,
                expected.SchemaVersion,
                DomainEventJson.Serialize(expected));
            Assert.Equal(expected.GetType(), durable.GetType());
            Assert.Equivalent(expected, durable, strict: true);
            Assert.Equal(DomainEventJson.Serialize(expected), DomainEventJson.Serialize(durable));
        }

        var declaredEventTypes = typeof(DomainEvent).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(DomainEvent).IsAssignableFrom(type))
            .ToHashSet();
        Assert.Equal(declaredEventTypes.OrderBy(type => type.FullName), expectedEvents.Select(item => item.GetType()).OrderBy(type => type.FullName));
        Assert.Equal(declaredEventTypes.OrderBy(type => type.FullName), DomainEventJson.RegisteredTypes.Values.OrderBy(type => type.FullName));
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
        var sessionId = SessionId.New();
        IDomainEvent[] expected =
        [
            new SessionCreated(sessionId, DateTimeOffset.UtcNow, "test"),
            new ModelOutputObserved(sessionId, DateTimeOffset.UtcNow, "first"),
            new ModelOutputObserved(sessionId, DateTimeOffset.UtcNow, "second"),
        ];
        var first = new List<IDomainEvent>();
        var second = new List<IDomainEvent>();
        var firstReceivedAll = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var firstSubscription = stream.Subscribe(
            (domainEvent, _) =>
            {
                first.Add(domainEvent);
                if (first.Count == expected.Length)
                {
                    firstReceivedAll.TrySetResult();
                }

                return Task.CompletedTask;
            },
            2);
        await using var secondSubscription = stream.Subscribe(
            async (domainEvent, cancellationToken) =>
            {
                if (second.Count == 0)
                {
                    secondEntered.TrySetResult();
                    await releaseSecond.Task.WaitAsync(cancellationToken);
                }

                second.Add(domainEvent);
            },
            2);

        var firstPublish = stream.PublishAsync(expected[0]);
        try
        {
            await secondEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var secondPublish = stream.PublishAsync(expected[1]);
            var thirdPublish = stream.PublishAsync(expected[2]);
            await firstReceivedAll.Task.WaitAsync(TestContext.Current.CancellationToken);

            Assert.False(firstPublish.IsCompleted);
            Assert.False(secondPublish.IsCompleted);
            Assert.False(thirdPublish.IsCompleted);
            Assert.Equal(expected, first);

            releaseSecond.TrySetResult();
            await Task.WhenAll(firstPublish, secondPublish, thirdPublish)
                .WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            releaseSecond.TrySetResult();
        }

        Assert.Equal(expected, second);
    }

    /// <summary>Cancellation cannot interrupt publication after the terminal state has committed.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task SessionApplication_CancellationDuringCompletion_FinishesWaiter(bool duringTransition)
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession
        {
            Turns = [new ScriptedTurn { Text = "hello" }],
        });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = harness.EventStream.Subscribe(async (item, token) =>
        {
            if (duringTransition
                ? item is RunTransitioned { Destination: RunPhase.Completion }
                : item is RunCompleted { Succeeded: true })
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        });
        var sessionId = await harness.Dispatcher.DispatchAsync(new CreateSessionCommand("test"));
        var runId = await harness.Dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "request"));
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(await harness.Dispatcher.DispatchAsync(new CancelRunCommand(sessionId, runId)));
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.True(await harness.Dispatcher.DispatchAsync(new WaitForRunCommand(runId))
            .WaitAsync(TestContext.Current.CancellationToken));
        Assert.Single(harness.Events.OfType<RunCompleted>());
        Assert.DoesNotContain(harness.Events, item => item is RunTransitionFailed);
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

    /// <summary>Shutdown joins cancellation even when a frontend waiter retires the same registration.</summary>
    [Fact]
    public static async Task SessionApplication_StopRuns_JoinsConcurrentWaiter()
    {
        var model = new GatedAnswerModelProvider();
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession(), modelProvider: model);
        var token = TestContext.Current.CancellationToken;
        var session = await harness.Dispatcher.DispatchAsync(new CreateSessionCommand("shutdown"), token);
        var run = await harness.Dispatcher.DispatchAsync(new SubmitRequestCommand(session, "request"), token);
        await model.InitialContentEmitted.WaitAsync(token);
        var waiter = harness.Dispatcher.DispatchAsync(new WaitForRunCommand(run), token);
        await harness.Application.StopRunsAsync(token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(token));
        Assert.False(harness.Application.HasActiveWork);
        await harness.Application.StopRunsAsync(token);
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

    /// <summary>Budget arithmetic saturates and identifies every exhausted dimension at numeric limits.</summary>
    [Fact]
    public static void ExecutionBudget_Overflow_IsSaturatedAndDimensioned()
    {
        var maximum = new BudgetDimensions(
            long.MaxValue,
            int.MaxValue,
            TimeSpan.MaxValue,
            decimal.MaxValue);
        var budget = new ExecutionBudget(maximum);

        Assert.False(budget.Accrue(maximum).IsExhausted);
        var status = budget.Check(new BudgetDimensions(1, 1, TimeSpan.FromTicks(1), 1));

        Assert.True(status.IsExhausted);
        Assert.Equal(
            BudgetExhaustionDimension.Tokens
                | BudgetExhaustionDimension.Calls
                | BudgetExhaustionDimension.WallClock
                | BudgetExhaustionDimension.Cost,
            status.ExhaustedDimensions);
        Assert.Equal(maximum, status.Used);
    }

    /// <summary>Prepared-request admission includes output, calls, known cost, and supplied duration.</summary>
    [Fact]
    public static void ModelRequestAdmissionEstimator_UsesConservativeKnownDimensions()
    {
        var request = new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "ignored because a prepared estimate is present",
            WireEstimate = new ModelWireEstimate
            {
                WireInputTokens = 100,
                OutputReserveTokens = 10,
            },
            MaximumOutputTokens = 10,
            AdmissionOutputTokenCeiling = 50,
            AdmissionCost = new ModelCostMetadata
            {
                InputPerMillionTokens = 1,
                OutputPerMillionTokens = 2,
            },
            AdmissionWallClock = TimeSpan.FromSeconds(3),
        };

        var estimate = ModelRequestAdmissionEstimator.Estimate(request);
        var dimensions = ModelRequestAdmissionEstimator.ToBudgetDimensions(estimate);

        Assert.Equal(100, estimate.InputTokens);
        Assert.Equal(50, estimate.OutputTokens);
        Assert.Equal(1, estimate.Calls);
        Assert.Equal(0.0002m, estimate.Cost);
        Assert.Equal(new BudgetDimensions(150, 1, TimeSpan.FromSeconds(3), 0.0002m), dimensions);
    }

    /// <summary>An unenforced provider maximum is capped by output that can physically fit beside prepared input.</summary>
    [Fact]
    public static void ModelRequestAdmissionEstimator_CapsUnenforcedOutputAtRemainingContext()
    {
        var estimate = ModelRequestAdmissionEstimator.Estimate(new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "prepared",
            WireEstimate = new ModelWireEstimate
            {
                WireInputTokens = 400,
                OutputReserveTokens = 50,
            },
            MaximumOutputTokens = 50,
            AdmissionOutputTokenCeiling = 1_000,
            AdmissionContextWindowTokens = 500,
        });

        Assert.Equal(400, estimate.InputTokens);
        Assert.Equal(100, estimate.OutputTokens);
        Assert.Equal(500, ModelRequestAdmissionEstimator.ToBudgetDimensions(estimate).Tokens);
    }

    /// <summary>Combining unknown pricing retains every known cost and duration as a lower bound.</summary>
    [Fact]
    public static void ModelRequestAdmissionEstimator_MixedUnknownsPreserveKnownLowerBounds()
    {
        var known = new ModelRequestAdmissionEstimate(
            100,
            50,
            1,
            Cost: 2.5m,
            WallClock: TimeSpan.FromSeconds(3),
            CostIsComplete: true,
            WallClockIsComplete: true);
        var unknown = new ModelRequestAdmissionEstimate(200, 25, 1);

        var combined = ModelRequestAdmissionEstimator.Combine(known, unknown);
        var dimensions = ModelRequestAdmissionEstimator.ToBudgetDimensions(combined);

        Assert.Equal(2.5m, dimensions.Cost);
        Assert.Equal(TimeSpan.FromSeconds(3), dimensions.WallClock);
        Assert.False(combined.CostIsComplete);
        Assert.False(combined.WallClockIsComplete);
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
        var usage = new BudgetDimensions(100_000, 100, TimeSpan.MaxValue, 100);

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
        var budget = new ExecutionBudget(new BudgetDimensions(10_000, 1, TimeSpan.MaxValue));
        await using var harness = await SessionHarness.CreateAsync(
            new ScriptedSession { Turns = [new ScriptedTurn { Text = "no usage" }] },
            budget: budget);
        var sessionId = await harness.Dispatcher.DispatchAsync(new CreateSessionCommand("test"));
        var runId = await harness.Dispatcher.DispatchAsync(
            new SubmitRequestCommand(sessionId, "request"));

        Assert.True(await harness.Dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
        var charged = budget.Check(new BudgetDimensions(0, 0, TimeSpan.Zero)).Used;
        Assert.Equal(1, charged.Calls);
        Assert.Equal(0, charged.Tokens);
    }

    /// <summary>The default concrete execution budget creates an independent scope for each concurrent run.</summary>
    [Fact]
    public static async Task SessionApplication_DefaultExecutionBudgetScopesConcurrentRuns()
    {
        await using var events = new DomainEventStream();
        var template = new ExecutionBudget(
            new BudgetDimensions(10_000, 1, TimeSpan.MaxValue));
        var application = new SessionApplication(
            events,
            new FakeModelProvider(new ScriptedSession
            {
                Turns =
                [
                    new ScriptedTurn { Text = "first" },
                    new ScriptedTurn { Text = "second" },
                ],
            }),
            template,
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var dispatcher = new CommandDispatcher([application]);
        var firstSession = await dispatcher.DispatchAsync(new CreateSessionCommand("first"));
        var secondSession = await dispatcher.DispatchAsync(new CreateSessionCommand("second"));

        var firstRun = await dispatcher.DispatchAsync(
            new SubmitRequestCommand(firstSession, "first request"));
        var secondRun = await dispatcher.DispatchAsync(
            new SubmitRequestCommand(secondSession, "second request"));
        var completions = await Task.WhenAll(
            dispatcher.DispatchAsync(new WaitForRunCommand(firstRun)),
            dispatcher.DispatchAsync(new WaitForRunCommand(secondRun)));

        Assert.All(completions, Assert.True);
        Assert.Equal(
            0,
            template.Check(new BudgetDimensions(0, 0, TimeSpan.Zero)).Used.Calls);
    }

    /// <summary>The TUI controller maps open, submit, wait, and cancel gestures to commands.</summary>
    [Fact]
    public static async Task InteractionController_DrivesInteractiveCommandBoundary()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession
        {
            Turns = [new ScriptedTurn { Text = "interactive" }],
        });
        var controller = new InteractionController(new InteractionPresenter(harness.Dispatcher, harness.Projections));

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

    /// <summary>The TUI controller routes cancellation through the application command boundary.</summary>
    [Fact]
    public static async Task InteractionController_CancelActiveRun_ProducesCancelledState()
    {
        await using var harness = await SessionHarness.CreateAsync(
            new ScriptedSession
            {
                Turns = [new ScriptedTurn { Text = "cancel this interactive run" }],
            },
            TimeSpan.FromSeconds(1));
        var controller = new InteractionController(new InteractionPresenter(harness.Dispatcher, harness.Projections));

        await controller.OpenAsync("TUI");
        await controller.SubmitAsync("request");
        Assert.True(await controller.CancelActiveRunAsync());
        var snapshot = await controller.RenderAsync();

        Assert.Null(controller.ActiveRunId);
        Assert.Equal(nameof(RunPhase.Cancelled), snapshot.Status);
    }

    /// <summary>Cancellation reconciles the UI marker when the engine has already reaped the run.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task InteractionController_CancelAlreadyTerminalRun_AllowsNextSubmission(bool cancel)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await SessionHarness.CreateAsync(
            new ScriptedSession { Turns = [new ScriptedTurn { Text = "response" }] },
            modelProvider: cancel ? new GatedAnswerModelProvider() : null);
        var controller = new InteractionController(new InteractionPresenter(harness.Dispatcher, harness.Projections));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await controller.OpenAsync("TUI", ct);
        var runId = await controller.SubmitAsync("request", operation.Token);

        // Reproduce the engine reaching its terminal boundary before the UI cancellation handler.
        if (cancel)
        {
            await operation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => harness.Dispatcher.DispatchAsync(new WaitForRunCommand(runId), ct));
        }
        else
        {
            Assert.True(await harness.Dispatcher.DispatchAsync(new WaitForRunCommand(runId), ct));
        }

        Assert.False(await controller.CancelActiveRunAsync(ct));
        Assert.Null(controller.ActiveRunId);
        Assert.Equal(runId, controller.LatestRunId);
        var nextRunId = await controller.SubmitAsync("next request", ct);
        Assert.NotEqual(runId, nextRunId);
        await controller.CancelActiveRunAsync(ct);
    }

    /// <summary>The help command opens the retained column dialog without writing help into MAIN or invoking a model.</summary>
    [Fact]
    public static async Task InteractionCoordinator_TuiKitHelpUsesModalWithoutTranscriptOutput()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        using var backend = new TUIKit.Terminal.HeadlessBackend(120, 35);
        await using var terminal = new Threadsmith.Tui.TuiKit.TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        var surface = new TuiKitCommandSurface(terminal, backend, ["/help\r", "\u001b", "/quit\r"]);
        var coordinator = new InteractionCoordinator(
            new InteractionPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await terminal.RunAsync(token => coordinator.RunAsync(cancellationToken: token), timeout.Token);

        Assert.Equal(["/help", "/quit"], surface.Submitted);
        Assert.Equal(1, surface.HelpOpened);
        var output = string.Concat(surface.Batches.SelectMany(batch => batch.Items).OfType<PresentationTextItem>().SelectMany(item => item.Segments).Select(segment => segment.Text));
        Assert.DoesNotContain("Manage MCP profiles and capabilities", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Submit any other text to Threadsmith.", output, StringComparison.Ordinal);
        Assert.Empty(harness.Events.OfType<TaskIntentRecorded>());
    }

    /// <summary>Completed names enter shared routing only as submitted input; unknown commands never create model work.</summary>
    [Theory]
    [InlineData("/the\t current\r")]
    [InlineData("\u001b[13~theme\r current\r")]
    public static async Task InteractionCoordinator_TuiKitCompletionRetainsSharedAuthority(string completionKeys)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        using var backend = new TUIKit.Terminal.HeadlessBackend(80, 24);
        await using var terminal = new Threadsmith.Tui.TuiKit.TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        var surface = new TuiKitCommandSurface(terminal, backend, [completionKeys, "/destructive-mystery\r", "/qui\t\r"]);
        var coordinator = new InteractionCoordinator(
            new InteractionPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            frontendCommands: surface);

        await terminal.RunAsync(token => coordinator.RunAsync(cancellationToken: token), timeout.Token);

        Assert.Equal(["/theme current", "/destructive-mystery", "/quit"], surface.Submitted);
        var invocation = Assert.Single(surface.Invocations);
        Assert.Equal("/theme", invocation.Descriptor.Name);
        Assert.Equal("current", invocation.Argument);
        Assert.Contains(
            surface.Batches.SelectMany(batch => batch.Items).OfType<PresentationTextItem>().SelectMany(item => item.Segments),
            segment => segment.Text.Contains("Unknown command", StringComparison.Ordinal));
        Assert.Empty(harness.Events.OfType<TaskIntentRecorded>());
        Assert.True(backend.IsStopped);
    }

    /// <summary>Presentation-only yields cannot cancel or choose a plan or mutation review action.</summary>
    /// <param name="prompt">Review prompt that must be presented again after background output.</param>
    /// <param name="response">User response returned after the presentation-only yield.</param>
    [Theory]
    [InlineData("Plan review: 1 approve, 2 reject, 3 revise, 4 cancel run\n", "1")]
    [InlineData("Mutation review: 1 apply approved set, 2 discard\n", "2")]
    public static async Task InteractionCoordinator_SecondaryReviewYield_RetriesWithoutChoosing(
        string prompt,
        string response)
    {
        var surface = new FakeConsoleSurface(
            [response],
            initialInput: new InteractionInput(
                false,
                string.Empty,
                CancellationToken.None,
                InteractionInputKind.IdleOutputYield));
        var input = await InteractionInputReader.ReadSecondaryAsync(
            new InteractionSessionSurface(surface),
            prompt,
            PresentationTextRole.Status,
            ComposerPurpose.Secondary,
            TestContext.Current.CancellationToken);

        Assert.True(input.IsSubmitted);
        Assert.Equal(response, input.Text);
        Assert.Equal(2, surface.Writes.Count(write => string.Equals(write, prompt, StringComparison.Ordinal)));
        Assert.Equal(["read", "read"], surface.Operations);
        Assert.Equal(2, surface.ComposerRequests.Count);
        Assert.All(surface.ComposerRequests, request => Assert.Equal(ComposerPurpose.Secondary, request.Purpose));
    }

    /// <summary>One retained hook list applies multiple changes through the existing hook manager.</summary>
    [Fact]
    public static async Task HookCheckboxesApplyMultipleChangesAndRetainState()
    {
        var hookStore = new InMemoryHookStore();
        var descriptors = HookDescriptorValidator.Normalize([.. new[] { "first", "second" }.Select(id => new HookHandlerDescriptor
        {
            Identity = new HookHandlerIdentity(new HookHandlerId(id), "1", new HookConfigurationDigest(string.Empty)),
            Scope = HookHandlerScope.Machine, AdapterKind = HookAdapterKind.Mcp, Enabled = false,
            HookPoints = [HookPoint.BeforeToolInvocation], Target = "profile/tool",
        })]);
        await using var hooks = new HookCoordinator(descriptors, [], new HookPolicyEvaluator(hookStore, []), hookStore, new SecretOutputSanitizer(), NullLogger<HookCoordinator>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession(), additionalHandlers: [new HookManagementApplication(hooks, hookStore)]);
        var surface = new ToggleInteractionSurface(["/hooks", "/hooks list", "/quit"]);
        surface.Toggle = async (request, change, token) =>
        {
            Assert.Equal(2, request.Options.Count);
            if (surface.ToggleCount == 1)
            {
                Assert.All(request.Options, option => Assert.False(option.Enabled));
                Assert.True((await change("first", true, token)).Enabled);
                Assert.True((await change("second", true, token)).Enabled);
                Assert.False((await change("first", false, token)).Enabled);
            }
            else
            {
                Assert.False(request.Options.Single(option => option.Id == "first").Enabled);
                Assert.True(request.Options.Single(option => option.Id == "second").Enabled);
            }
        };
        var coordinator = new InteractionCoordinator(new InteractionPresenter(harness.Dispatcher, harness.Projections), harness.EventStream, surface);
        await coordinator.RunAsync().WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, surface.ToggleCount);
        Assert.Empty(surface.SelectionRequests);
    }

    /// <summary>MCP connection checkboxes reconcile failed connects and allow further changes in the same modal.</summary>
    [Fact]
    public static async Task McpCheckboxesConnectDisconnectAndReportFailures()
    {
        var manager = new ToggleMcpHandler();
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession(), additionalHandlers: [manager]);
        var surface = new ToggleInteractionSurface(["/mcp", "/mcp list", "/quit"]);
        surface.Toggle = async (request, change, token) =>
        {
            Assert.Contains("connected", request.Title, StringComparison.Ordinal);
            if (surface.ToggleCount == 1)
            {
                Assert.True((await change("first", true, token)).Enabled);
                var failed = await change("second", true, token);
                Assert.False(failed.Enabled);
                Assert.Equal("Authentication required", failed.Reason);
                Assert.False((await change("first", false, token)).Enabled);
                Assert.True((await change("first", true, token)).Enabled);
            }
            else
            {
                Assert.True(request.Options.Single(option => option.Id == "first").Enabled);
                Assert.False(request.Options.Single(option => option.Id == "second").Enabled);
            }
        };
        var coordinator = new InteractionCoordinator(new InteractionPresenter(harness.Dispatcher, harness.Projections), harness.EventStream, surface);
        await coordinator.RunAsync().WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, surface.ToggleCount);
        Assert.Equal([McpManagementAction.Connect, McpManagementAction.Connect, McpManagementAction.Disconnect, McpManagementAction.Connect], manager.Changes);
        Assert.Empty(surface.SelectionRequests);
    }

    /// <summary>Only OAuth profiles offer authentication, with current metadata after success or cancellation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task McpModalAuthenticationUsesExistingAuthority(bool cancel)
    {
        var manager = new ToggleMcpHandler();
        manager.EnableOAuth();
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession(), additionalHandlers: [manager]);
        var surface = new ToggleInteractionSurface(["/mcp", "/quit"])
        {
            Actions = async (request, action, token) =>
        {
            var oauth = request.Options.Single(option => option.Id == "first");
            Assert.Equal("authenticate", Assert.Single(oauth.Actions).Id);
            Assert.Contains("sign-in required", oauth.Label, StringComparison.Ordinal);
            Assert.Empty(request.Options.Single(option => option.Id == "second").Actions);
            var unsupported = await action("second", "authenticate", token);
            Assert.False(unsupported.Enabled);
            Assert.Empty(manager.Changes);
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (cancel)
            {
                manager.OnAuthenticate = operation.Cancel;
            }

            var result = await action("first", "authenticate", operation.Token);
            Assert.Equal(!cancel, result.Enabled);
            Assert.Contains(cancel ? "sign-in required" : "authenticated", result.UpdatedOption!.Label, StringComparison.Ordinal);
            if (cancel)
            {
                Assert.Equal("Authentication cancelled.", result.Reason);
                manager.OnAuthenticate = null;
                var retried = await action("first", "authenticate", token);
                Assert.True(retried.Enabled);
            }
        },
        };
        var coordinator = new InteractionCoordinator(new InteractionPresenter(harness.Dispatcher, harness.Projections), harness.EventStream, surface);
        await coordinator.RunAsync().WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(cancel ? 2 : 1, manager.Changes.Count);
        Assert.All(manager.Changes, action => Assert.Equal(McpManagementAction.Authenticate, action));
    }

    /// <summary>Rejected extension discovery reports the restart instruction and leaves the command loop usable.</summary>
    [Fact]
    public static async Task ExtensionDiscoveryRejectionKeepsInteractiveSessionUsable()
    {
        const string rejection = "ExtensionRepositoryStale: restart Threadsmith in the active repository before discovering or loading extensions.";
        var manager = new ToggleExtensionManager
        {
            DiscoveryFailure = new UnauthorizedAccessException(rejection),
        };
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/extensions", "/help", "/quit"]);
        var coordinator = new InteractionCoordinator(
            new InteractionPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            extensionManager: manager);

        await coordinator.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains(rejection, surface.Output, StringComparison.Ordinal);
        Assert.Contains("/open [path]", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>Extension checkboxes allow repeated load/unload changes and preserve actual state after blocked unload.</summary>
    [Fact]
    public static async Task ExtensionCheckboxesReconcileBlockedUnload()
    {
        var manager = new ToggleExtensionManager();
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new ToggleInteractionSurface(["/extensions", "/extensions", "/quit"]);
        surface.Toggle = async (request, change, token) =>
        {
            if (surface.ToggleCount == 1)
            {
                Assert.True((await change("first", true, token)).Enabled);
                Assert.True((await change("second", true, token)).Enabled);
                var blocked = await change("second", false, token);
                Assert.True(blocked.Enabled);
                Assert.Contains("UnloadBlocked", blocked.Reason, StringComparison.Ordinal);
                Assert.False((await change("first", false, token)).Enabled);
                Assert.True((await change("first", true, token)).Enabled);
                Assert.False((await change("first", false, token)).Enabled);
            }
            else
            {
                Assert.False(request.Options.Single(option => option.Id == "first").Enabled);
                Assert.True(request.Options.Single(option => option.Label == "second (1)").Enabled);
            }
        };
        var coordinator = new InteractionCoordinator(new InteractionPresenter(harness.Dispatcher, harness.Projections), harness.EventStream, surface, extensionManager: manager);
        await coordinator.RunAsync().WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, surface.ToggleCount);
        Assert.Empty(surface.SelectionRequests);
    }

    /// <summary>Known extension failures stay visible in the notice and details, while retries preserve actual state.</summary>
    [Fact]
    public static async Task ExtensionCheckboxesShowKnownFailureAndRecover()
    {
        const string reason = "Setting 'uri' is required; configure it before enabling the extension.";
        var manager = new ToggleExtensionManager { LoadFailure = new ExtensionOperationException(reason) };
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new ToggleInteractionSurface(["/extensions", "/quit"])
        {
            Toggle = async (_, change, token) =>
            {
                var failed = await change("first", true, token);
                Assert.False(failed.Enabled);
                Assert.Contains(reason, failed.Reason, StringComparison.Ordinal);
                Assert.Contains(reason, failed.UpdatedOption?.Reason, StringComparison.Ordinal);

                manager.LoadFailure = new InvalidOperationException("private-detail");
                var unknown = await change("first", true, token);
                Assert.False(unknown.Enabled);
                Assert.Equal("The extension change failed; showing current state.", unknown.Reason);
                Assert.DoesNotContain("private-detail", unknown.UpdatedOption?.Reason ?? string.Empty, StringComparison.Ordinal);

                manager.LoadFailure = null;
                var retry = await change("first", true, token);
                Assert.True(retry.Enabled);
                Assert.Null(retry.Reason);
                Assert.DoesNotContain(reason, retry.UpdatedOption?.Reason ?? string.Empty, StringComparison.Ordinal);
            },
        };
        var coordinator = new InteractionCoordinator(
            new InteractionPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            extensionManager: manager);

        await coordinator.RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(1, surface.ToggleCount);
    }

    /// <summary>A failed model turn renders its engine diagnostic once before accepting the next prompt.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task InteractionCoordinator_ProviderFailure_RendersOncePerTurn(bool retainsActivity)
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession
        {
            Turns = [new ScriptedTurn { Failure = ScriptFailureKind.TransientProvider }],
        });
        var surface = new FakeConsoleSurface(["first", "second", "/quit"], retainsActivity: retainsActivity);
        var shell = CreateCoordinator(
            new InteractionPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync().WaitAsync(TestContext.Current.CancellationToken);

        const string message = "Scripted transient provider failure.";
        Assert.Equal(2, surface.Output.Split(message, StringSplitOptions.None).Length - 1);
        Assert.Equal(2, surface.Output.Split("[TransientProvider]", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, harness.Events.OfType<RunCompleted>().Count(item => !item.Succeeded));
        Assert.Equal(2, harness.Events.OfType<DiagnosticObserved>().Count(item => item.Message == message));
        Assert.Empty(surface.ActiveStatuses);
    }

    /// <summary>A failed waiter without terminal events remains visible and does not block the next prompt.</summary>
    [Fact]
    public static async Task InteractionCoordinator_UnreportedWaitFailure_RemainsVisible()
    {
        var sessionId = SessionId.New();
        var projections = new InMemoryProjectionStore();
        projections.ReplaceSession(new SessionProjection
        {
            Key = new ProjectionKey("session", sessionId.Value.ToString("D")),
            SessionId = sessionId,
            Name = "unreported failure",
            Phase = RunPhase.Intake,
        });
        const string message = "Unreported waiter failure.";
        var surface = new FakeConsoleSurface(["request", "/quit"]);
        var shell = CreateCoordinator(
            new InteractionPresenter(new FixedRunDispatcher(sessionId, RunId.New(), message), projections),
            new DomainEventStream(),
            surface);

        await shell.RunAsync().WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, surface.Output.Split(message, StringSplitOptions.None).Length - 1);
    }

    /// <summary>A large multiline paste reaches the command boundary as one unchanged submission.</summary>
    [Fact]
    public static async Task InteractionCoordinator_LargeMultilinePaste_IsNotReplayedPerCharacter()
    {
        var pastedText = " leading line\n" + new string('x', 100_000) + "\ntrailing line ";
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession
        {
            Turns = [new ScriptedTurn { Text = "received" }],
        });
        var surface = new FakeConsoleSurface([pastedText, "/quit"]);
        var shell = CreateCoordinator(
            new InteractionPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync().WaitAsync(TestContext.Current.CancellationToken);

        var intent = Assert.Single(harness.Events.OfType<TaskIntentRecorded>());
        Assert.Equal(pastedText, intent.Intent);
        Assert.Contains("received", surface.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Threadsmith: received", surface.Output, StringComparison.Ordinal);
    }

    /// <summary>An overlong fetch authorization chain is reported locally without escaping the shell boundary.</summary>
    [Fact]
    public static async Task InteractionCoordinator_OverlongFetchAuthorizationChain_IsRejectedLocally()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface([]);
        var authority = new WebFetchAuthorizationAuthority(new WebFetchOptions { MaximumRedirects = 1 });
        var shell = CreateCoordinator(
            new InteractionPresenter(harness.Dispatcher, harness.Projections),
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
    public static async Task InteractionCoordinator_TrustWithoutRepository_IsRejectedLocally()
    {
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession());
        var surface = new FakeConsoleSurface(["/help", "/trust build", "/quit"]);
        var shell = CreateCoordinator(
            new InteractionPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync().WaitAsync(TestContext.Current.CancellationToken);

        Assert.Contains("/trust [inspect|read|build|mutation|automation]", surface.Output, StringComparison.Ordinal);
        Assert.Contains("No repository is open", surface.Output, StringComparison.Ordinal);
        Assert.Empty(harness.Events.OfType<TaskIntentRecorded>());
    }

    /// <summary>The /reasoning command rejects an unknown level string.</summary>
    [Fact]
    public static async Task InteractionCoordinator_ReasoningUnknownLevel_ShowsError()
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
        var shell = CreateCoordinator(
            new InteractionPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            catalog,
            profileId,
            preferences);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TestContext.Current.CancellationToken);

        Assert.Contains("does not support reasoning level 'bogus'", surface.Output, StringComparison.Ordinal);
        Assert.Equal(ReasoningLevel.None, preferences.Reasoning);
    }

    /// <summary>The /reasoning command rejects a level not supported by the active model.</summary>
    [Fact]
    public static async Task InteractionCoordinator_ReasoningUnsupportedLevel_ShowsError()
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
        var shell = CreateCoordinator(
            new InteractionPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface,
            catalog,
            profileId,
            preferences);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TestContext.Current.CancellationToken);

        Assert.Contains("does not support reasoning level 'high'", surface.Output, StringComparison.Ordinal);
        Assert.Equal(ReasoningLevel.Medium, preferences.Reasoning);
    }

    /// <summary>Partial role styles independently inherit unspecified values from the default role.</summary>
    [Fact]
    public static void TuiThemeResolver_PartialRole_InheritsMissingValuesFromDefault()
    {
        var theme = new TuiTheme(
            "test",
            [
                new(PresentationTextRole.Default, new TuiTextStyle(
                    TuiColor.Parse("white"),
                    TuiColor.Parse("#102030"),
                    TuiTextDecoration.Italic)),
                new(PresentationTextRole.Error, new TuiTextStyle(TuiColor.Parse("red"))),
                new(PresentationTextRole.MarkdownStrong, new TuiTextStyle(TuiColor.Parse("cyan"))),
            ]);
        var resolver = new TuiThemeResolver(theme);

        var result = resolver.Resolve(PresentationTextRole.Error);

        Assert.Equal("red", result.Foreground?.Value);
        Assert.Equal("#102030", result.Background?.Value);
        Assert.Equal(TuiTextDecoration.Italic, result.Decorations);
        var heading = resolver.Resolve(PresentationTextRole.MarkdownHeading);
        Assert.Equal("white", heading.Foreground?.Value);
        Assert.Equal("#102030", heading.Background?.Value);
        Assert.Equal(TuiTextDecoration.Italic, heading.Decorations);
        var strong = resolver.Resolve(PresentationTextRole.MarkdownStrong);
        Assert.Equal("cyan", strong.Foreground?.Value);
        Assert.Equal("#102030", strong.Background?.Value);
        Assert.Equal(TuiTextDecoration.Italic, strong.Decorations);
    }

    /// <summary>An explicit empty decoration set overrides inherited decorations.</summary>
    [Fact]
    public static void TuiThemeResolver_ExplicitNoDecorations_OverridesDefault()
    {
        var theme = new TuiTheme(
            "decorations",
            [
                new(PresentationTextRole.Default, new TuiTextStyle(Decorations: TuiTextDecoration.Bold)),
                new(PresentationTextRole.Error, new TuiTextStyle(Decorations: TuiTextDecoration.None)),
            ]);
        var resolver = new TuiThemeResolver(theme);

        Assert.Equal(TuiTextDecoration.Bold, resolver.Resolve(PresentationTextRole.Status).Decorations);
        Assert.Equal(TuiTextDecoration.None, resolver.Resolve(PresentationTextRole.Error).Decorations);
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
                new(PresentationTextRole.Default, new TuiTextStyle()),
                new(PresentationTextRole.Default, new TuiTextStyle()),
            ]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TuiTheme(
            "decoration",
            [new(PresentationTextRole.Default, new TuiTextStyle(Decorations: (TuiTextDecoration)64))]));
    }

    /// <summary>Every semantic role supports independent foreground, background, and validated decorations.</summary>
    [Fact]
    public static void TuiTheme_AllRoles_ResolveIndependentStyleValues()
    {
        KeyValuePair<PresentationTextRole, TuiTextStyle>[] styles =
        [
            .. Enum.GetValues<PresentationTextRole>().Select(role => new KeyValuePair<PresentationTextRole, TuiTextStyle>(
                role,
                new TuiTextStyle(
                    TuiColor.Parse("#102030"),
                    TuiColor.Parse("brightwhite"),
                    TuiTextDecoration.Bold | TuiTextDecoration.Underline))),
        ];
        var resolver = new TuiThemeResolver(new TuiTheme("complete", styles));

        Assert.All(Enum.GetValues<PresentationTextRole>(), role =>
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

        Assert.Equal(["system", "forge-dark", "ocean", "high-contrast", "MarkdownFriendlyDark"], themes.Select(theme => theme.Theme.Id));
        Assert.All(Enum.GetValues<PresentationTextRole>(), role =>
        {
            var style = new TuiThemeResolver(themes[0].Theme).Resolve(role);
            Assert.Null(style.Foreground);
            Assert.Null(style.Background);
        });
        foreach (var theme in themes.Where(theme => theme.Theme.Id is "forge-dark" or "ocean" or "high-contrast"))
        {
            var resolver = new TuiThemeResolver(theme.Theme);
            Assert.Null(resolver.Resolve(PresentationTextRole.Default).Background);
            Assert.Null(resolver.Resolve(PresentationTextRole.ComposerPrompt).Background);
            Assert.Null(resolver.Resolve(PresentationTextRole.ThinkingIndicator).Background);
            Assert.NotNull(resolver.Resolve(PresentationTextRole.ComposerPrompt).Foreground);
            Assert.NotNull(resolver.Resolve(PresentationTextRole.ThinkingIndicator).Foreground);
            Assert.NotEqual(
                resolver.Resolve(PresentationTextRole.Default).Foreground,
                resolver.Resolve(PresentationTextRole.ComposerPrompt).Foreground);
            Assert.NotEqual(
                resolver.Resolve(PresentationTextRole.Default).Foreground,
                resolver.Resolve(PresentationTextRole.ThinkingIndicator).Foreground);
            Assert.NotNull(resolver.Resolve(PresentationTextRole.SelectionHighlight).Background);
        }

        Assert.All(themes, theme => Assert.All(Enum.GetValues<PresentationTextRole>(), role => Assert.False(
            new TuiThemeResolver(theme.Theme)
                .Resolve(role)
                .Decorations.GetValueOrDefault().HasFlag(TuiTextDecoration.Invert))));
        Assert.True(new TuiThemeResolver(themes[3].Theme).Resolve(PresentationTextRole.Error).Decorations?.HasFlag(TuiTextDecoration.Underline));
        var defaultTheme = Assert.Single(themes, theme => theme.Theme.Id == BuiltInThemes.DefaultThemeId);
        var defaultResolver = new TuiThemeResolver(defaultTheme.Theme);
        Assert.Equal("#82D1B9", defaultResolver.Resolve(PresentationTextRole.Default).Foreground?.Value);
        Assert.Equal("#242121", defaultResolver.Resolve(PresentationTextRole.Default).Background?.Value);
        Assert.Equal("#000000", defaultResolver.Resolve(PresentationTextRole.OutputStreamPaneRole).Background?.Value);
        Assert.Equal(TuiTextDecoration.Dim | TuiTextDecoration.Italic, defaultResolver.Resolve(PresentationTextRole.MarkdownQuote).Decorations);

        (var defaultCatalog, var defaultId) = TuiThemeConfigurationLoader.Load(configuration: null);
        var defaultPreferences = new SessionThemePreferences(defaultCatalog, defaultId);
        Assert.Equal(BuiltInThemes.DefaultThemeId, defaultPreferences.ActiveTheme.Theme.Id);
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
            ["tui:themes:1:styles:SessionStatus:background"] = "black",
            ["tui:themes:1:styles:TitleBarRole:invert"] = "true",
        }).Build();

        (var catalog, var defaultId) = TuiThemeConfigurationLoader.Load(configuration);
        var preferences = new SessionThemePreferences(catalog, defaultId);

        Assert.Equal(["system", "forge-dark", "ocean", "high-contrast", "MarkdownFriendlyDark", "project-blue"], catalog.Themes.Select(theme => theme.Theme.Id));
        Assert.Equal("Replaced Forge", catalog.Themes[1].Name);
        Assert.Equal("project-blue", preferences.ActiveTheme.Theme.Id);
        Assert.Single(catalog.Warnings);
        var resolver = new TuiThemeResolver(preferences.ActiveTheme.Theme);
        Assert.Equal(TuiColor.Parse("black"), resolver.Resolve(PresentationTextRole.SessionStatus).Background);
        Assert.Equal(TuiTextDecoration.None, resolver.Resolve(PresentationTextRole.SessionStatus).Decorations);
        Assert.Equal(TuiTextDecoration.None, resolver.Resolve(PresentationTextRole.AgentSelectedTabRole).Decorations);
        Assert.Equal(TuiTextDecoration.Invert, resolver.Resolve(PresentationTextRole.TitleBarRole).Decorations);
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
    public static async Task InteractionCoordinator_ThemePersistenceFailure_LeavesSelectionUnchanged()
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
            var shell = CreateCoordinator(
                new InteractionPresenter(harness.Dispatcher, harness.Projections),
                harness.EventStream,
                surface,
                themePreferences: preferences,
                themePreferenceStore: new UserConfigurationThemePreferenceStore(configurationPath));

            await shell.RunAsync(modelStatus: "Test model").WaitAsync(TestContext.Current.CancellationToken);

            Assert.Equal("system", preferences.ActiveTheme.Theme.Id);
            Assert.Contains("Theme could not be saved; selection unchanged.", surface.Output, StringComparison.Ordinal);
            Assert.Equal("[]", await File.ReadAllTextAsync(configurationPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Mixed event output retains exact text while assigning tool, link, validation, and diff roles.</summary>
    [Fact]
    public static void InteractionEventSegments_MixedOutput_PreservesPlainTextAndSemanticRoles()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var segments = new List<PresentationTextSegment>();
        InteractionEventSegments.Append(
            segments,
            new ToolInvocationCompleted(sessionId, occurredAt, ToolInvocationId.New(), true),
            " TOOLS: read - completed"
                + Environment.NewLine
                + "   \u2514 read_file path src/Program.cs"
                + Environment.NewLine);
        InteractionEventSegments.Append(
            segments,
            new ToolInvocationCompleted(sessionId, occurredAt, ToolInvocationId.New(), false),
            " TOOLS: build - failed"
                + Environment.NewLine
                + "   \u2514 dotnet build failed"
                + Environment.NewLine);
        InteractionEventSegments.Append(
            segments,
            new RepositoryOpened(sessionId, occurredAt, "C:\\source\\repo"),
            $"Threadsmith: Repository opened.{Environment.NewLine}Repository: C:\\source\\repo{Environment.NewLine}");
        InteractionEventSegments.Append(
            segments,
            new MutationSetProposed(sessionId, occurredAt, MutationSetId.New()),
            $"@@ -1 +1 @@{Environment.NewLine}{Environment.NewLine}-old{Environment.NewLine}+new{Environment.NewLine}");
        InteractionEventSegments.Append(
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
        Assert.Contains(segments, segment => segment.Role == PresentationTextRole.ToolSuccess);
        Assert.Contains(segments, segment => segment.Role == PresentationTextRole.ToolFailure);
        Assert.Contains(segments, segment => segment.Role == PresentationTextRole.Hyperlink);
        Assert.Contains(segments, segment => segment.Role == PresentationTextRole.DiffAdded);
        Assert.Contains(segments, segment => segment.Role == PresentationTextRole.DiffRemoved);
        Assert.Contains(segments, segment => segment.Role == PresentationTextRole.DiffContext);
        Assert.Contains(segments, segment => segment.Role == PresentationTextRole.Muted && segment.Text.Contains("read_file", StringComparison.Ordinal));
        Assert.Contains(segments, segment => segment.Role == PresentationTextRole.Success);
    }

    /// <summary>Leading presentation-boundary spacing does not demote the completed-tool header.</summary>
    [Fact]
    public static void InteractionEventSegments_ToolCompletionAfterBoundary_KeepsHeaderOutcomeRole()
    {
        var segments = new List<PresentationTextSegment>();
        InteractionEventSegments.Append(
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
            segment => segment.Role == PresentationTextRole.ToolSuccess
                && segment.Text.Contains(" TOOLS: read - completed", StringComparison.Ordinal));
        Assert.DoesNotContain(
            segments,
            segment => segment.Role == PresentationTextRole.Muted
                && segment.Text.Contains(" TOOLS: read - completed", StringComparison.Ordinal));
        Assert.Contains(
            segments,
            segment => segment.Role == PresentationTextRole.Muted
                && segment.Text.Contains("read_file path src/Program.cs", StringComparison.Ordinal));
    }

    /// <summary>Plan lifecycle blocks keep status headers and muted guided body/detail roles.</summary>
    [Fact]
    public static void InteractionEventSegments_PlanLifecycleBlocks_UseHeaderAndMutedBodyRoles()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var segments = new List<PresentationTextSegment>();
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

        InteractionEventSegments.Append(
            segments,
            new PlanProposed(sessionId, occurredAt, "Update the formatter."),
            proposedText);
        InteractionEventSegments.Append(
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
            segment => segment.Role == PresentationTextRole.Status
                && segment.Text.Contains(" PLAN: revision 1", StringComparison.Ordinal));
        Assert.Contains(
            segments,
            segment => segment.Role == PresentationTextRole.Success
                && segment.Text.Contains(" PLAN: auto-approved", StringComparison.Ordinal));
        Assert.Contains(
            segments,
            segment => segment.Role == PresentationTextRole.Muted
                && segment.Text.Contains(" \u2502 Steps:", StringComparison.Ordinal));
        Assert.Contains(
            segments,
            segment => segment.Role == PresentationTextRole.Muted
                && segment.Text.Contains("Reason: Policy ReviewRisky", StringComparison.Ordinal));
    }

    /// <summary>Mutation lifecycle blocks keep success headers and muted guided body/detail roles.</summary>
    [Fact]
    public static void InteractionEventSegments_MutationLifecycleBlock_UsesHeaderAndMutedDetailRoles()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var segments = new List<PresentationTextSegment>();
        var text = " MUTATION: Applied under the active approval policy"
            + Environment.NewLine
            + " \u2502 Mutation applied: src/File.cs"
            + Environment.NewLine
            + " \u2514 The expression uses the approved value."
            + Environment.NewLine;

        InteractionEventSegments.Append(
            segments,
            new MutationApplied(sessionId, occurredAt, MutationId.New()),
            text);

        Assert.Equal(text, string.Concat(segments.Select(segment => segment.Text)));
        Assert.Contains(
            segments,
            segment => segment.Role == PresentationTextRole.Success
                && segment.Text.Contains(" MUTATION: Applied under the active approval policy", StringComparison.Ordinal));
        Assert.Contains(
            segments,
            segment => segment.Role == PresentationTextRole.Muted
                && segment.Text.Contains("Mutation applied: src/File.cs", StringComparison.Ordinal));
        Assert.Contains(
            segments,
            segment => segment.Role == PresentationTextRole.Muted
                && segment.Text.Contains("The expression uses the approved value.", StringComparison.Ordinal));
    }

    /// <summary>Mutation preparation lifecycle blocks keep status/warning headers and muted guided rows.</summary>
    [Fact]
    public static void InteractionEventSegments_MutationPreparationBlocks_UseHeaderAndMutedDetailRoles()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var runId = RunId.New();
        var segments = new List<PresentationTextSegment>();
        var startedText = " MUTATION: Generating edits"
            + Environment.NewLine
            + " \u2514 Attempt: 1/2"
            + Environment.NewLine;
        var repairText = " MUTATION: Retrying proposal with correction evidence"
            + Environment.NewLine
            + " \u2502 Attempt: 2/2"
            + Environment.NewLine
            + " \u2514 Reason: ReplaceText expectedText was not found in 'src/File.cs'."
            + Environment.NewLine;

        InteractionEventSegments.Append(
            segments,
            new MutationProposalStarted(sessionId, occurredAt, runId, AttemptNumber: 1, MaximumAttempts: 2),
            startedText);
        InteractionEventSegments.Append(
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
            segment => segment.Role == PresentationTextRole.Status
                && segment.Text.Contains(" MUTATION: Generating edits", StringComparison.Ordinal));
        Assert.Contains(
            segments,
            segment => segment.Role == PresentationTextRole.Warning
                && segment.Text.Contains(" MUTATION: Retrying proposal", StringComparison.Ordinal));
        Assert.Contains(
            segments,
            segment => segment.Role == PresentationTextRole.Muted
                && segment.Text.Contains("Attempt: 2/2", StringComparison.Ordinal));
        Assert.Contains(
            segments,
            segment => segment.Role == PresentationTextRole.Muted
                && segment.Text.Contains("Reason: ReplaceText expectedText", StringComparison.Ordinal));
    }

    /// <summary>Model-authored THINKING text remains ordinary visible answer content.</summary>
    [Fact]
    public static void InteractionEventSegments_ModelAuthoredThinking_UsesDefaultRole()
    {
        var segments = new List<PresentationTextSegment>();

        InteractionEventSegments.Append(
            segments,
            new ModelOutputObserved(SessionId.New(), DateTimeOffset.UtcNow, "answer"),
            $"THINKING{Environment.NewLine}answer{Environment.NewLine}");

        Assert.Contains(segments, segment => segment.Role == PresentationTextRole.Default && segment.Text.Contains("THINKING", StringComparison.Ordinal));
        Assert.DoesNotContain(segments, segment => segment.Role == PresentationTextRole.ThinkingIndicator);
    }

    /// <summary>An incomplete structured test run is rendered as an error even when no failures were counted.</summary>
    [Fact]
    public static void InteractionEventSegments_IncompleteTestRun_UsesErrorRole()
    {
        var validation = new TestValidationResult
        {
            Selection = new TestSelection(),
            Completed = false,
        };
        var segments = new List<PresentationTextSegment>();

        InteractionEventSegments.Append(
            segments,
            new TestRunCompleted(SessionId.New(), DateTimeOffset.UtcNow, 0, 0, StructuredResult: validation),
            string.Empty);

        var segment = Assert.Single(segments);
        Assert.Equal(PresentationTextRole.Error, segment.Role);
    }

    /// <summary>Hyperlink metadata rejects control characters, relative targets, and unsupported schemes.</summary>
    [Fact]
    public static void PresentationTextSegment_UnsafeHyperlinks_AreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            new PresentationTextSegment("relative", PresentationTextRole.Hyperlink, new Uri("relative", UriKind.Relative)).Validate());
        Assert.Throws<ArgumentException>(() =>
            new PresentationTextSegment("script", PresentationTextRole.Hyperlink, new Uri("javascript:alert(1)")).Validate());
        Assert.Throws<ArgumentException>(() =>
            new PresentationTextSegment("control", PresentationTextRole.Hyperlink, new Uri("https://example.test/%1B")).Validate());
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
            [new(PresentationTextRole.Default, new TuiTextStyle(TuiColor.Parse("cyan")))]);
        var resolver = new TuiThemeResolver(theme);

        var result = resolver.Resolve((PresentationTextRole)int.MaxValue);

        Assert.Equal("cyan", result.Foreground?.Value);
        Assert.Null(result.Background);
    }

    /// <summary>Streaming reasoning takes permanent scrollback ownership from transient thinking activity.</summary>
    [Fact]
    public static async Task InteractionCoordinator_ThinkingStreaming_DoesNotRestartTransientActivity()
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
        var shell = CreateCoordinator(
            new InteractionPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        await shell.RunAsync(modelStatus: "Test model").WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            1,
            surface.Lifecycle.Count(entry => string.Equals(
                entry,
                "activity-start:THINKING",
                StringComparison.Ordinal)));
        var thinkingEnded = surface.Lifecycle.ToList().IndexOf("activity-end:THINKING");
        var reasoningWritten = surface.Lifecycle.ToList().IndexOf("output:reasoning");
        Assert.True(
            thinkingEnded >= 0 && thinkingEnded < reasoningWritten,
            string.Join(" | ", surface.Lifecycle));
        var streamedReasoning = string.Concat(surface.Segments
            .Where(segment => segment.Role == PresentationTextRole.Reasoning)
            .Select(segment => segment.Text));
        Assert.StartsWith(
            $"{Environment.NewLine}Inspect the repository.",
            streamedReasoning,
            StringComparison.Ordinal);
        Assert.False(streamedReasoning.StartsWith(
            Environment.NewLine + Environment.NewLine,
            StringComparison.Ordinal));
    }

    /// <summary>Overlapping MCP calls remain independent when they finish in either order and with different outcomes.</summary>
    [Theory]
    [InlineData(false, OperationActivityOutcome.Completed)]
    [InlineData(true, OperationActivityOutcome.Failed)]
    [InlineData(true, OperationActivityOutcome.Cancelled)]
    [InlineData(false, OperationActivityOutcome.TimedOut)]
    public static async Task InteractionCoordinator_ConcurrentMcpCallsKeepTheirOwnActivityAndOutcome(bool reverse, OperationActivityOutcome outcome)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var provider = new GatedAnswerModelProvider();
        await using var harness = await SessionHarness.CreateAsync(new ScriptedSession(), modelProvider: provider);
        var surface = new ConcurrentToolConsoleSurface();
        var shell = CreateCoordinator(new InteractionPresenter(harness.Dispatcher, harness.Projections), harness.EventStream, surface);
        var shellTask = shell.RunAsync(modelStatus: "Test model", cancellationToken: timeout.Token);
        try
        {
            await Task.WhenAll(provider.InitialContentEmitted, surface.StatusStarted).WaitAsync(timeout.Token);
            var session = harness.Events.OfType<SessionCreated>().Single().SessionId;
            var first = new ToolInvocationStarted(session, DateTimeOffset.UtcNow, ToolInvocationId.New(), "first", Source: new ToolActivitySource(ToolActivitySourceKind.Mcp, "Server"));
            var second = first with { ToolInvocationId = ToolInvocationId.New(), ToolName = "second" };
            await harness.EventStream.PublishAsync(first, timeout.Token);
            var firstActivity = Assert.Single(await surface.ToolUpdates.Reader.ReadAsync(timeout.Token));
            await harness.EventStream.PublishAsync(second, timeout.Token);
            var both = await surface.ToolUpdates.Reader.ReadAsync(timeout.Token);
            Assert.Equal(2, both.Count);
            Assert.Same(firstActivity, both[0]);
            var finishing = reverse ? second : first;
            var remaining = reverse ? first : second;
            await harness.EventStream.PublishAsync(
                new ToolInvocationCompleted(session, DateTimeOffset.UtcNow, finishing.ToolInvocationId, outcome == OperationActivityOutcome.Completed, ElapsedMilliseconds: 5200, Outcome: outcome),
                timeout.Token);
            var survivor = Assert.Single(await surface.ToolUpdates.Reader.ReadAsync(timeout.Token));
            Assert.Same(both[reverse ? 0 : 1], survivor);
            Assert.Contains(remaining.ToolName, survivor.Label, StringComparison.Ordinal);
            await harness.EventStream.PublishAsync(new ToolInvocationCompleted(session, DateTimeOffset.UtcNow, remaining.ToolInvocationId, true, ElapsedMilliseconds: 6100), timeout.Token);
            Assert.Empty(await surface.ToolUpdates.Reader.ReadAsync(timeout.Token));
            await surface.WaitUntilAsync(() => surface.Output.Contains($"MCP: Server/{remaining.ToolName} - completed", StringComparison.Ordinal), timeout.Token);

            var expected = outcome switch
            {
                OperationActivityOutcome.Completed => "completed",
                OperationActivityOutcome.Failed => "failed",
                OperationActivityOutcome.Cancelled => "cancelled",
                _ => "timed out",
            };
            Assert.Contains($"MCP: Server/{finishing.ToolName} - {expected}", surface.Output, StringComparison.Ordinal);
            Assert.Equal(1, CountOccurrences(surface.Output, $"MCP: Server/{finishing.ToolName} -"));
            Assert.Equal(1, CountOccurrences(surface.Output, $"MCP: Server/{remaining.ToolName} -"));
        }
        finally
        {
            provider.ReleaseAnswer();
            await shellTask.WaitAsync(timeout.Token);
        }
    }

    /// <summary>Activity-display failures escape while a verbose active run fills the event dispatcher.</summary>
    [Fact]
    public static async Task InteractionCoordinator_ActivityDisplayFailure_DuringVerboseRun_IsPropagated()
    {
        var provider = new GatedAnswerModelProvider(trailingChunkCount: 1024);
        await using var harness = await SessionHarness.CreateAsync(
            new ScriptedSession(),
            modelProvider: provider);
        var expected = new InvalidOperationException("status rendering failed");
        var surface = new FakeConsoleSurface(["hello"], statusFailure: expected);
        var shell = CreateCoordinator(
            new InteractionPresenter(harness.Dispatcher, harness.Projections),
            harness.EventStream,
            surface);

        var shellTask = shell.RunAsync(modelStatus: "Test model");
        await Task.WhenAll(provider.InitialContentEmitted, surface.StatusStarted)
            .WaitAsync(TestContext.Current.CancellationToken);
        provider.ReleaseAnswer();

        var exception = await Record.ExceptionAsync(
            () => shellTask.WaitAsync(TestContext.Current.CancellationToken));
        var eventCounts = harness.Events.GroupBy(item => item.GetType().Name)
            .OrderBy(group => group.Key)
            .Select(group => $"{group.Key}={group.Count()}");
        Assert.True(
            exception is InvalidOperationException,
            $"Expected the rendering failure, received {exception}. "
                + $"Activity lifecycle: {string.Join(" | ", surface.Lifecycle)}. "
                + $"Observed events: {string.Join(", ", eventCounts)}.");
        Assert.Same(expected, exception);
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

    /// <summary>The UI dispatcher processes a flood in bounded redraw batches without losing events.</summary>
    [Fact]
    public static async Task InteractionEventDispatcher_Flooding_CoalescesWithoutLoss()
    {
        var dispatcher = new InteractionEventDispatcher(2);
        var sessionId = SessionId.New();
        IDomainEvent[] expected =
        [
            new ModelOutputObserved(sessionId, DateTimeOffset.UtcNow, "first"),
            new ModelOutputObserved(sessionId, DateTimeOffset.UtcNow, "second"),
            new ModelOutputObserved(sessionId, DateTimeOffset.UtcNow, "third"),
        ];
        var batches = new List<IReadOnlyList<IDomainEvent>>();

        await dispatcher.QueueAsync(expected[0]);
        await dispatcher.QueueAsync(expected[1]);
        var thirdQueue = dispatcher.QueueAsync(expected[2]);
        Assert.False(thirdQueue.IsCompleted);

        var drain = dispatcher.DrainAsync((batch, _) =>
        {
            batches.Add(batch);
            return Task.CompletedTask;
        });
        await thirdQueue.WaitAsync(TestContext.Current.CancellationToken);

        dispatcher.Complete();
        await drain.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(batches[0].Count >= 2);
        Assert.Equal(expected, batches.SelectMany(batch => batch));
    }

    /// <summary>Both frontend entry points produce the same successful run and scripted model output.</summary>
    [Fact]
    public static async Task ScriptedRequestProducesTheSameResultsInInteractiveAndHeadlessSessions()
    {
        var token = TestContext.Current.CancellationToken;
        const string answer = "fixture output";
        const string request = "fixture request";
        var harness = await SessionHarness.CreateAsync(new ScriptedSession
        {
            Turns = [new ScriptedTurn { Text = answer }],
        });
        await using (harness)
        {
            var controller = new InteractionController(new InteractionPresenter(harness.Dispatcher, harness.Projections));
            await controller.OpenAsync("interactive", token);
            await controller.SubmitAsync(request, cancellationToken: token);
            Assert.True(await controller.WaitForActiveRunAsync(token));
            await using var writer = new StringWriter();
            var shell = new HeadlessShell(harness.Dispatcher, harness.Projections, writer);
            Assert.Equal(0, await shell.RunAsync("headless", request, token));
        }

        var sessions = harness.Events.OfType<SessionCreated>().ToDictionary(item => item.Name);
        Assert.Equal(2, sessions.Count);
        var outputs = new List<string>();
        foreach (var name in new[] { "interactive", "headless" })
        {
            var session = sessions[name].SessionId;
            Assert.True(Assert.Single(harness.Events.OfType<RunCompleted>(), item => item.SessionId == session).Succeeded);
            var output = Assert.Single(harness.Events.OfType<ModelOutputObserved>(), item => item.SessionId == session);
            outputs.Add(output.Text);
        }

        Assert.Equal(answer, outputs[0].TrimEnd());
        Assert.Equal(outputs[0], outputs[1]);
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
        Assert.Contains("Semantic loading completed below the required confidence", writer.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("timed out", writer.ToString(), StringComparison.Ordinal);
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
    [InlineData("token:\n\nmultiline-secret", "multiline-secret")]
    [InlineData("{\"token\":\n\"multiline-json-secret\"}", "multiline-json-secret")]
    [InlineData("token = >credential", ">credential")]
    [InlineData("token = \"=>credential\"", "=>credential")]
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
        const string input = """
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
        const string input = """
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

    /// <summary>C# lambda arrows are syntax rather than credential assignment separators.</summary>
    [Theory]
    [InlineData("token =>\n {\n     return Fetch(token);\n }")]
    [InlineData("token =>\r\n {\r\n     return Fetch(token);\r\n }")]
    [InlineData("Use(token=>Fetch(token));")]
    [InlineData("access_token => Fetch(access_token)")]
    [InlineData("cancellation_token => Fetch(cancellation_token)")]
    [InlineData("password => Fetch(password)")]
    [InlineData("pwd=>Fetch(pwd)")]
    public static void SecretOutputSanitizer_PreservesLambdaArrows(string input)
    {
        var sanitizer = new SecretOutputSanitizer();

        Assert.Equal(input, sanitizer.Sanitize(input));
    }

    /// <summary>C# fence headers after prose credential names survive without a source-argument fast-path trigger.</summary>
    [Theory]
    [InlineData("csharp", "\n")]
    [InlineData("cs", "\n")]
    [InlineData("csharp", "\r\n")]
    [InlineData("cs", "\r\n")]
    public static void SecretOutputSanitizer_PreservesCSharpFenceHeaders(string language, string newline)
    {
        var sanitizer = new SecretOutputSanitizer();
        var input = $"caller-provided token:{newline}{newline}```{language}{newline}fetch(cancellationToken){newline}```";

        Assert.Equal(input, sanitizer.Sanitize(input));
    }

    /// <summary>Only standalone unquoted C# fence headers following a prose colon qualify as syntax.</summary>
    [Theory]
    [InlineData("token: ```csharp", "```csharp")]
    [InlineData("token=\n```csharp\n", "```csharp")]
    [InlineData("\"token\":\n```csharp\n", "```csharp")]
    [InlineData("token:\n\"```csharp\"\n", "```csharp")]
    [InlineData("token:\n```csharp-extra\n", "```csharp-extra")]
    [InlineData("token:\n```csharp,suffix\n", "```csharp")]
    [InlineData("token:\n```csharp suffix\n", "```csharp")]
    [InlineData("token:\n```python\n", "```python")]
    public static void SecretOutputSanitizer_RedactsFenceLikeCredentialValues(string input, string secret)
    {
        var sanitizer = new SecretOutputSanitizer();

        var sanitized = sanitizer.Sanitize(input);

        Assert.DoesNotContain(secret, sanitized, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", sanitized, StringComparison.Ordinal);
    }

    /// <summary>Fence detection does not enable identifier exemptions in unrelated credential matches.</summary>
    [Fact]
    public static void SecretOutputSanitizer_FenceHeaderDoesNotExemptCredentialIdentifiers()
    {
        var sanitizer = new SecretOutputSanitizer();
        const string input = "provided token:\n\n```cs\nfetch(cancellationToken)\n```\nHeader, token: sensitive trailing";
        var expected = input.Replace("sensitive", "[REDACTED]", StringComparison.Ordinal);

        Assert.Equal(expected, sanitizer.Sanitize(input));
    }

    /// <summary>Preserving source syntax never exempts neighboring text or fenced code from credential redaction.</summary>
    [Theory]
    [InlineData("csharp")]
    [InlineData("cs")]
    public static void SecretOutputSanitizer_RedactsCredentialsAlongsidePreservedSyntax(string language)
    {
        var sanitizer = new SecretOutputSanitizer();
        var input = $$"""
            Authorization: Bearer header-secret
            THREAD_TOKEN=environment-secret
            caller-provided token:

            ```{{language}}
            token =>
            {
                var apiKey = "source-secret";
                Use(token: "argument-secret");
                var value = "sk-abcdefghijklmnopqrstuvwxyz";
                return Fetch(token);
            }
            ```
            {"token":"json-secret"}
            token:
                multiline-secret
            """;
        var expected = input
            .Replace("Bearer header-secret", "[REDACTED]", StringComparison.Ordinal)
            .Replace("environment-secret", "[REDACTED]", StringComparison.Ordinal)
            .Replace("\"source-secret\"", "[REDACTED]", StringComparison.Ordinal)
            .Replace("\"argument-secret\"", "[REDACTED]", StringComparison.Ordinal)
            .Replace("sk-abcdefghijklmnopqrstuvwxyz", "[REDACTED]", StringComparison.Ordinal)
            .Replace("\"json-secret\"", "[REDACTED]", StringComparison.Ordinal)
            .Replace("multiline-secret", "[REDACTED]", StringComparison.Ordinal);

        Assert.Equal(expected, sanitizer.Sanitize(input));
    }

    private static IEnumerable<RunPhase[]> GetLegalTransitionPaths()
    {
        yield return new[] { RunPhase.RepositoryDiscovery };
        yield return new[] { RunPhase.EvidenceCollection };
        yield return new[] { RunPhase.RepositoryDiscovery, RunPhase.EvidenceCollection };
        yield return new[] { RunPhase.EvidenceCollection, RunPhase.Completion };
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

    private sealed class GatedAnswerModelProvider : IModelProvider
    {
        private readonly int _trailingChunkCount;
        private readonly bool _leadingWhitespace;

        private readonly TaskCompletionSource _releaseAnswer = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _initialContentEmitted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

#pragma warning disable VSTHRD003 // Fixture-owned initial-content signal.
        public Task InitialContentEmitted => _initialContentEmitted.Task;
#pragma warning restore VSTHRD003

        public GatedAnswerModelProvider(int trailingChunkCount = 0, bool leadingWhitespace = true)
        {
            _trailingChunkCount = trailingChunkCount;
            _leadingWhitespace = leadingWhitespace;
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
                if (_leadingWhitespace)
                {
                    yield return new ModelChunk { Text = "\n\n" };
                }

                _initialContentEmitted.TrySetResult();
                await _releaseAnswer.Task.WaitAsync(cancellationToken);
                // Flush a complete paragraph before the verbose tail so rendering fails
                // while the producer still has work, rather than after Markdown buffering.
                yield return new ModelChunk { Text = _trailingChunkCount > 0 ? "answer\n\n" : "answer" };
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

    private class ConcurrentToolConsoleSurface : FakeConsoleSurface, IInteractionToolActivitySurface
    {
        public ConcurrentToolConsoleSurface()
            : base(["hello", "/quit"], retainsActivity: true)
        {
        }

        internal System.Threading.Channels.Channel<IReadOnlyList<InteractionActivity>> ToolUpdates { get; } = System.Threading.Channels.Channel.CreateUnbounded<IReadOnlyList<InteractionActivity>>();

        public Task PresentToolActivitiesAsync(IReadOnlyList<InteractionActivity> activities, CancellationToken cancellationToken = default)
        {
            return ToolUpdates.Writer.WriteAsync(activities, cancellationToken).AsTask();
        }
    }

    private static InteractionCoordinator CreateCoordinator(
        InteractionPresenter presenter,
        IDomainEventStream events,
        FakeConsoleSurface surface,
        ConfiguredModelCatalog? modelCatalog = null,
        ModelProfileId? activeProfileId = null,
        SessionModelPreferences? sessionPreferences = null,
        IExtensionManager? extensionManager = null,
        SessionThemePreferences? themePreferences = null,
        SessionUsageProjection? sessionUsage = null,
        bool showSessionStatus = true,
        IToolStateManager? toolStateManager = null,
        IMutationApprovalPolicy? mutationApprovalPolicy = null,
        bool activeModelSelectionAvailable = false,
        IClaudeSkillCompatibilityCatalog? claudeSkills = null,
        bool sessionLifecycleAvailable = false,
        TuiDisplayOptions? displayOptions = null,
        TimeProvider? timeProvider = null,
        IGitQueryService? gitQueries = null,
        WebFetchAuthorizationAuthority? webFetchAuthorization = null,
        DirectFetchApprovalPromptRouter? directFetchApprovalPrompt = null,
        IThemePreferenceStore? themePreferenceStore = null,
        CodeExploreOutputOptions? codeExploreOutputOptions = null)
    {
        var themes = themePreferences ?? new SessionThemePreferences(
            new ConfiguredThemeCatalog(BuiltInThemes.Create()),
            BuiltInThemes.DefaultThemeId);
        return new InteractionCoordinator(
            presenter,
            events,
            surface,
            modelCatalog,
            activeProfileId,
            sessionPreferences,
            extensionManager,
            sessionUsage,
            showSessionStatus,
            toolStateManager,
            mutationApprovalPolicy,
            activeModelSelectionAvailable,
            claudeSkills,
            sessionLifecycleAvailable,
            displayOptions?.ToInteractionOptions(),
            displayWarnings: themes.Catalog.Warnings.Concat(displayOptions?.Diagnostics ?? []).ToArray(),
            timeProvider: timeProvider,
            gitQueries: gitQueries,
            webFetchAuthorization: webFetchAuthorization,
            directFetchApprovalPrompt: directFetchApprovalPrompt,
            frontendCommands: new ThemeCommandContribution(themes, surface.SetThemeAsync, themePreferenceStore),
            codeExploreOutputOptions: codeExploreOutputOptions);
    }

    private class FakeConsoleSurface : IInteractionSurface
    {
        private readonly Lock _gate = new();

        private readonly Queue<InteractionInput> _inputs;

        private readonly StringBuilder _output = new();

        private readonly Queue<int> _selections = [];

        private readonly List<string> _statuses = [];

        private readonly List<string> _activeStatuses = [];

        private readonly List<PresentationTextSegment> _segments = [];

        private readonly List<string> _lifecycle = [];

        private readonly List<string> _operations = [];

        private readonly List<PresentationItem> _outputItems = [];

        private readonly List<string> _sessionStatuses = [];

        private readonly List<ComposerRequest> _composerRequests = [];

        private readonly List<string> _writes = [];

        private readonly int _statusWidth;

        private readonly Exception? _statusFailure;

        private readonly bool _suppressSessionStatus;

        private readonly TaskCompletionSource _statusStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _reasoningPresented = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _activeStatusCount;

        public FakeConsoleSurface(
            IEnumerable<string> inputs,
            IEnumerable<int>? selections = null,
            int statusWidth = 120,
            bool suppressSessionStatus = false,
            Exception? statusFailure = null,
            InteractionInput? initialInput = null,
            bool retainsActivity = false)
        {
            Capabilities = new InteractionSurfaceCapabilities(SupportsRetainedActivity: retainsActivity);
            _statusWidth = statusWidth;
            _suppressSessionStatus = suppressSessionStatus;
            _statusFailure = statusFailure;
            _inputs = new Queue<InteractionInput>();
            if (initialInput is not null)
            {
                _inputs.Enqueue(initialInput);
            }

            foreach (var input in inputs.Select(text => new InteractionInput(
                true,
                text,
                CancellationToken.None)))
            {
                _inputs.Enqueue(input);
            }

            if (selections is not null)
            {
                foreach (var selection in selections)
                {
                    _selections.Enqueue(selection);
                }
            }
        }

        public async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task changed;
                lock (_gate)
                {
                    if (condition())
                    {
                        return;
                    }

                    changed = _changed.Task;
                }

                await changed.WaitAsync(cancellationToken);
            }
        }

        public InteractionSurfaceCapabilities Capabilities { get; }

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

        public IReadOnlyList<PresentationItem> OutputItems
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

        public IReadOnlyList<ComposerRequest> ComposerRequests
        {
            get
            {
                lock (_gate)
                {
                    return _composerRequests.ToArray();
                }
            }
        }

#pragma warning disable VSTHRD003 // Fixture-owned presentation phase signals.
        public Task StatusStarted => _statusStarted.Task;

        public Task ReasoningPresented => _reasoningPresented.Task;
#pragma warning restore VSTHRD003

        public async Task<InteractionSelectionResult> SelectAsync(
            InteractionSelectionRequest request,
            CancellationToken cancellationToken = default)
        {
            var selected = await SelectAsync(
                request.Title,
                request.Options.Select(option => option.Label).ToArray(),
                cancellationToken);
            return selected < 0 || selected >= request.Options.Count
                ? new InteractionSelectionResult(null, IsCancelled: true)
                : new InteractionSelectionResult(request.Options[selected].Id);
        }

        public Task PresentAsync(PresentationBatch batch, CancellationToken cancellationToken = default)
        {
            return WriteOutputAsync(batch.Items, cancellationToken);
        }

        public Task PresentSessionStatusAsync(SessionStatusSnapshot status, CancellationToken cancellationToken = default)
        {
            return ShowSessionStatusAsync(status, " | ", cancellationToken);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003", Justification = "The coordinator owns the operation represented by this test surface.")]
        public Task PresentActivityUntilAsync(InteractionActivity activity, Task operation, CancellationToken cancellationToken = default)
        {
            return ShowStatusUntilAsync(activity.Format(), operation, cancellationToken);
        }

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

        public IReadOnlyList<PresentationTextSegment> Segments
        {
            get
            {
                lock (_gate)
                {
                    return _segments.ToArray();
                }
            }
        }

        public Task SetThemeAsync(ConfiguredTheme theme, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActiveThemeId = theme.Theme.Id;
            return Task.CompletedTask;
        }

        public Task ShowSessionStatusAsync(
            SessionStatusSnapshot status,
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
                NotifyChanged();
            }

            return Task.CompletedTask;
        }

        public async Task<InteractionInput> ReadComposerAsync(
            ComposerRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _composerRequests.Add(request);
            }

            return await ReadAsync(cancellationToken);
        }

        public Task<InteractionInput> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _operations.Add("read");
                return Task.FromResult(_inputs.Count > 0
                    ? _inputs.Dequeue()
                    : new InteractionInput(false, string.Empty, CancellationToken.None));
            }
        }

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
                NotifyChanged();
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
                    NotifyChanged();
                }
            }
        }

        public async Task WriteOutputAsync(
            IReadOnlyList<PresentationItem> items,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _outputItems.AddRange(items);
                foreach (var item in items)
                {
                    _lifecycle.Add(item switch
                    {
                        PresentationMarkdownItem => "output:markdown",
                        PresentationTextItem textItem when textItem.Segments.Any(
                            segment => segment.Role == PresentationTextRole.Reasoning) => "output:reasoning",
                        _ => "output:segments",
                    });
                }
            }

            foreach (var item in items)
            {
                var segments = item switch
                {
                    PresentationTextItem textItem => textItem.Segments,
                    PresentationSourceItem sourceItem => PrefixAnswer(
                        sourceItem.StartsAnswerBlock,
                        [new PresentationTextSegment(sourceItem.SafeSource, PresentationTextRole.Default)]),
                    PresentationMarkdownItem markdownItem => PrefixAnswer(
                        markdownItem.StartsAnswerBlock,
                        TuiMarkdownLayout.Format(markdownItem.Document, _statusWidth)),
                    _ => throw new InvalidOperationException($"Unsupported test output: {item.GetType().Name}"),
                };
                foreach (var segment in segments)
                {
                    await WriteAsync(segment.Text, segment.Role, CancellationToken.None);
                }
            }

            if (items.OfType<PresentationTextItem>().Any(item => item.Segments.Any(segment => segment.Role == PresentationTextRole.Reasoning)))
            {
                _reasoningPresented.TrySetResult();
            }
        }

        public Task WriteAsync(
            string text,
            PresentationTextRole role = PresentationTextRole.Default,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                _writes.Add(text);
                _segments.Add(new PresentationTextSegment(text, role));
                _output.Append(text);
                NotifyChanged();
            }

            return Task.CompletedTask;
        }

        private void NotifyChanged()
        {
            var changed = _changed;
            _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            changed.TrySetResult();
        }

        private static IReadOnlyList<PresentationTextSegment> PrefixAnswer(
            bool startsAnswerBlock,
            IReadOnlyList<PresentationTextSegment> segments)
        {
            return startsAnswerBlock
                        ? [new PresentationTextSegment("\n", PresentationTextRole.Default), .. segments]
                        : segments;
        }
    }

    private sealed class TuiKitCommandSurface : IInteractionSurface, IFrontendCommandContribution, IStartupProgressSurface, IInteractionHelpSurface
    {
        private readonly Threadsmith.Tui.TuiKit.TuiKitSurface _surface;
        private readonly TUIKit.Terminal.HeadlessBackend _backend;
        private readonly Queue<string> _keys;

        internal TuiKitCommandSurface(Threadsmith.Tui.TuiKit.TuiKitSurface surface, TUIKit.Terminal.HeadlessBackend backend, IEnumerable<string> keys)
        {
            _surface = surface;
            _backend = backend;
            _keys = new Queue<string>(keys);
        }

        public InteractionSurfaceCapabilities Capabilities => _surface.Capabilities;

        internal List<string> Submitted { get; } = [];

        internal List<InteractiveCommandInvocation> Invocations { get; } = [];

        internal List<PresentationBatch> Batches { get; } = [];

        internal int HelpOpened { get; private set; }

        public async Task ShowCommandHelpAsync(IReadOnlyList<InteractiveCommandDescriptor> commands, CancellationToken cancellationToken = default)
        {
            var help = _surface.ShowCommandHelpAsync(commands, cancellationToken);
            await _surface.PresentAsync(new PresentationBatch([]), cancellationToken);
            HelpOpened++;
            _backend.FeedInput(_keys.Dequeue());
            await help;
        }

        public async Task<InteractionInput> ReadComposerAsync(ComposerRequest request, CancellationToken cancellationToken = default)
        {
            var read = _surface.ReadComposerAsync(request, cancellationToken);
            await _surface.PresentAsync(new PresentationBatch([]), cancellationToken);
            _backend.FeedInput(_keys.Dequeue());
            var result = await read;
            Submitted.Add(result.Text);
            return result;
        }

        public Task<InteractionSelectionResult> SelectAsync(InteractionSelectionRequest request, CancellationToken cancellationToken = default)
        {
            return _surface.SelectAsync(request, cancellationToken);
        }

        public Task PresentAsync(PresentationBatch batch, CancellationToken cancellationToken = default)
        {
            Batches.Add(batch);
            return _surface.PresentAsync(batch, cancellationToken);
        }

        public Task PresentSessionStatusAsync(SessionStatusSnapshot status, CancellationToken cancellationToken = default)
        {
            return _surface.PresentSessionStatusAsync(status, cancellationToken);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003", Justification = "Forwards the coordinator-owned operation to the actual surface without a synchronization context.")]
        public Task PresentActivityUntilAsync(InteractionActivity activity, Task operation, CancellationToken cancellationToken = default)
        {
            return _surface.PresentActivityUntilAsync(activity, operation, cancellationToken);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003", Justification = "Forwards the coordinator-owned startup operation to the actual surface.")]
        public Task ShowStartupAsync(string logo, string label, Task operation, CancellationToken cancellationToken = default)
        {
            return _surface.ShowStartupAsync(logo, label, operation, cancellationToken);
        }

        public Task ShowStartupAsync(string logo, string label, Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
        {
            return _surface.ShowStartupAsync(logo, label, operation, cancellationToken);
        }

        public Task SetStartupProgressAsync(Func<IReadOnlyList<SemanticStartupPhaseSnapshot>>? snapshots, CancellationToken cancellationToken = default)
        {
            return _surface.SetStartupProgressAsync(snapshots, cancellationToken);
        }

        public Task SetStartupDetailsAsync(IReadOnlyList<string> details, CancellationToken cancellationToken = default)
        {
            return _surface.SetStartupDetailsAsync(details, cancellationToken);
        }

        public Task<FrontendCommandOutcome> HandleAsync(InteractiveCommandInvocation invocation, IInteractionSurface surface, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations.Add(invocation);
            return Task.FromResult(FrontendCommandOutcome.Handled);
        }
    }

    private class RecordingInteractionSurface : IInteractionSurface
    {
        private readonly Queue<InteractionInput> _inputs;
        private readonly InteractionSelectionResult? _selectionResult;

        internal RecordingInteractionSurface(
            IEnumerable<string> inputs,
            InteractionSelectionResult? selectionResult = null)
        {
            _inputs = new Queue<InteractionInput>(inputs.Select(text => new InteractionInput(
                true,
                text,
                CancellationToken.None)));
            _selectionResult = selectionResult;
        }

        public InteractionSurfaceCapabilities Capabilities { get; } = new();

        internal IReadOnlyList<ImageArtifact> Images { get; init; } = [];

        internal ConcurrentQueue<PresentationBatch> Batches { get; } = new();

        internal List<ComposerRequest> ComposerRequests { get; } = [];

        internal List<InteractionSelectionRequest> SelectionRequests { get; } = [];

        internal List<SessionStatusSnapshot> SessionStatuses { get; } = [];

        public Task<InteractionInput> ReadComposerAsync(
            ComposerRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ComposerRequests.Add(request);
            return Task.FromResult(_inputs.Dequeue() with { Images = Images });
        }

        public Task<InteractionSelectionResult> SelectAsync(
            InteractionSelectionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SelectionRequests.Add(request);
            return Task.FromResult(_selectionResult
                ?? throw new InvalidOperationException("The scripted interaction does not request a selection."));
        }

        public Task PresentAsync(
            PresentationBatch batch,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Batches.Enqueue(batch);
            return Task.CompletedTask;
        }

        public Task PresentSessionStatusAsync(
            SessionStatusSnapshot status,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SessionStatuses.Add(status);
            return Task.CompletedTask;
        }

        public Task PresentActivityUntilAsync(
            InteractionActivity activity,
            Task operation,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The scripted interaction does not present activity.");
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

    private static IReadOnlyList<IDomainEvent> TestEvents()
    {
        var sessionId = SessionId.New();
        var occurredAt = DateTimeOffset.UtcNow;
        var question = new UserQuestionRequest(Guid.NewGuid(), sessionId, RunId.New(), ToolInvocationId.New(), "question-call", "repository", 0, [new("q", "Which output?", [])], occurredAt);
        return
        [
            new SessionCreated(sessionId, occurredAt, "test"),
            new UserQuestionStateChanged(sessionId, occurredAt, question, new(UserQuestionStatus.Pending, [])),
            new UserQuestionReady(sessionId, occurredAt, question.RunId, question.RequestId),
            new MemoriesIncluded(
                sessionId,
                occurredAt,
                RunId.New(),
                [
                    new RepositoryMemoryContextItemProjection
                    {
                        Id = RepositoryMemoryId.New(),
                        Text = "Architectural assertion",
                        Ownership = RepositoryMemoryOwnership.Intelligence,
                        Included = true,
                        Rationale = "Selected for this request",
                        EstimatedTokens = 8,
                        Supports = [new RepositoryMemorySupport { Path = "src/Service.cs", ContentHash = "hash", ObservedAt = occurredAt, IsDirty = true }],
                    },
                ]),
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
            new SemanticRefreshStarted(
                sessionId,
                occurredAt,
                SemanticRefreshId.New(),
                WorkspaceId.New(),
                SemanticRefreshReason.ExternalChange,
                SemanticRefreshMode.Incremental,
                ChangedFileCount: 1,
                DirtyVersion: 2),
            new SemanticRefreshModeChanged(
                sessionId,
                occurredAt,
                SemanticRefreshId.New(),
                WorkspaceId.New(),
                SemanticRefreshReason.ExternalChange,
                SemanticRefreshMode.Full,
                ChangedFileCount: 1),
            new SemanticRefreshCompleted(
                sessionId,
                occurredAt,
                SemanticRefreshId.New(),
                WorkspaceId.New(),
                SemanticRefreshReason.Manual,
                SemanticRefreshMode.Full,
                ChangedFileCount: 1,
                DirtyVersion: 2,
                AppliedVersion: 2,
                SemanticConfidenceLevel.FullSemantic,
                ElapsedMilliseconds: 12),
            new SemanticRefreshFailed(
                sessionId,
                occurredAt,
                SemanticRefreshId.New(),
                WorkspaceId.New(),
                SemanticRefreshReason.Recovery,
                SemanticRefreshMode.Full,
                ChangedFileCount: 1,
                DirtyVersion: 3,
                AppliedVersion: 2,
                SemanticRefreshFailureKind.Infrastructure,
                "safe reason",
                ElapsedMilliseconds: 12),
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
            new AgentRunInstructionsObserved(
                sessionId,
                occurredAt,
                DelegationId.New(),
                AgentAssignmentId.New(),
                RunId.New(),
                1,
                AgentInstructionSource.PreparedRequest,
                [new("Assignment", "Task\nContext 界")]),
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
            new SkillInvocationProgressObserved(sessionId, occurredAt, SkillInvocationId.New(), 1, "Preparing source snapshots"),
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
            new RunSteeringPauseRequested(
                sessionId,
                occurredAt,
                RunId.New(),
                SteeringPauseId.New()),
            new RunSteeringPaused(
                sessionId,
                occurredAt,
                RunId.New(),
                SteeringPauseId.New()),
            new RunSteeringSubmitted(
                sessionId,
                occurredAt,
                RunId.New(),
                SteeringPauseId.New(),
                Sequence: 1,
                HasText: true),
            new RunCompleted(sessionId, occurredAt, RunId.New(), true),
        ];
    }

    private sealed record EchoCommand(string Value) : ICommand<string>;

    private sealed record UnhandledCommand : ICommand<string>;

    private sealed class ToggleInteractionSurface : RecordingInteractionSurface, IInteractionActionToggleSurface
    {
        internal ToggleInteractionSurface(IEnumerable<string> inputs)
            : base(inputs)
        {
        }

        internal Func<InteractionToggleRequest, Func<string, bool, CancellationToken, Task<InteractionToggleResult>>, CancellationToken, Task>? Toggle { get; set; }

        internal int ToggleCount { get; private set; }

        internal Func<InteractionToggleRequest, Func<string, bool, CancellationToken, Task<InteractionToggleResult>>, Func<string, string, CancellationToken, Task<InteractionToggleResult>>, CancellationToken, Task>? Manage { get; set; }

        internal Func<InteractionToggleRequest, Func<string, string, CancellationToken, Task<InteractionToggleResult>>, CancellationToken, Task>? Actions { get; set; }

        public Task SelectActionTogglesAsync(
            InteractionToggleRequest request,
            Func<string, bool, CancellationToken, Task<InteractionToggleResult>> change,
            Func<string, string, CancellationToken, Task<InteractionToggleResult>> action,
            CancellationToken cancellationToken = default)
        {
            return Manage is { } manage ? manage(request, change, action, cancellationToken)
                : Actions is { } actions ? actions(request, action, cancellationToken) : SelectTogglesAsync(request, change, cancellationToken);
        }

        public Task SelectTogglesAsync(InteractionToggleRequest request, Func<string, bool, CancellationToken, Task<InteractionToggleResult>> change, CancellationToken cancellationToken = default)
        {
            ToggleCount++;
            return (Toggle ?? throw new InvalidOperationException("No toggle script"))(request, change, cancellationToken);
        }
    }

    private sealed class ToggleMcpHandler : ICommandHandler<ExecuteMcpManagementCommand, McpManagementResult>
    {
        private readonly Dictionary<string, McpProfileSummary> _profiles = new[] { "first", "second" }.ToDictionary(id => id, id => new McpProfileSummary
        {
            ProfileId = id,
            DisplayName = id,
            ConfigurationSource = "User",
            Transport = "http",
            Trust = "TrustedRead",
            EndpointIdentity = "https://example.com",
            State = "Disconnected",
            Eligible = true,
        });

        internal List<McpManagementAction> Changes { get; } = [];

        internal Action? OnAuthenticate { get; set; }

        public Task<McpManagementResult> HandleAsync(ExecuteMcpManagementCommand command, CancellationToken cancellationToken = default)
        {
            var request = command.Request;
            if (request.Action == McpManagementAction.List)
            {
                return Task.FromResult(new McpManagementResult { Succeeded = true, Message = "Profiles", Profiles = [.. _profiles.Values] });
            }

            var id = request.ProfileId!;
            if (request.Action == McpManagementAction.Authenticate)
            {
                Changes.Add(request.Action);
                OnAuthenticate?.Invoke();
                if (cancellationToken.IsCancellationRequested)
                {
                    return Task.FromCanceled<McpManagementResult>(cancellationToken);
                }

                _profiles[id] = _profiles[id] with { State = "Connected", AuthenticationState = McpAuthenticationState.Authenticated };
            }

            var failed = false;
            if (request.Action is McpManagementAction.Connect or McpManagementAction.Disconnect)
            {
                Changes.Add(request.Action);
                failed = id == "second";
                if (!failed)
                {
                    _profiles[id] = _profiles[id] with { State = request.Action == McpManagementAction.Connect ? "Connected" : "Disconnected" };
                }
            }

            return Task.FromResult(new McpManagementResult
            {
                Succeeded = !failed,
                Message = failed ? "Authentication required" : "Updated",
                Profile = new McpProfileDetail { Summary = _profiles[id] },
            });
        }

        internal void EnableOAuth()
        {
            _profiles["first"] = _profiles["first"] with { AuthenticationState = McpAuthenticationState.SignedOut };
        }
    }

    private sealed class ToggleExtensionManager : IExtensionManager
    {
        private readonly Dictionary<string, ExtensionSummary> _extensions = new[] { "first", "second" }.ToDictionary(id => id, id => new ExtensionSummary
        {
            ExtensionId = id,
            Name = id,
            Version = "1",
            Directory = id,
            State = "Discovered",
        });

        public IReadOnlyList<ExtensionSummary> Summaries => [.. _extensions.Values];

        public Exception? DiscoveryFailure { get; init; }

        public Exception? LoadFailure { get; set; }

        public Task<IReadOnlyList<ExtensionSummary>> DiscoverAsync(CancellationToken cancellationToken = default)
        {
            if (DiscoveryFailure is { } failure)
            {
                return Task.FromException<IReadOnlyList<ExtensionSummary>>(failure);
            }

            return Task.FromResult(Summaries);
        }

        public Task<ExtensionSummary?> LoadAsync(string extensionId, SessionId sessionId, CancellationToken cancellationToken = default)
        {
            if (LoadFailure is { } failure)
            {
                return Task.FromException<ExtensionSummary?>(failure);
            }

            _extensions[extensionId] = _extensions[extensionId] with { IsLoaded = true, State = "Active", GenerationId = ExtensionGenerationId.New() };
            return Task.FromResult<ExtensionSummary?>(_extensions[extensionId]);
        }

        public Task<bool> UnloadAsync(ExtensionGenerationId generationId, SessionId sessionId, CancellationToken cancellationToken = default)
        {
            return UnloadAsync(_extensions.Values.First(extension => extension.GenerationId == generationId).ExtensionId, sessionId, cancellationToken);
        }

        public Task<bool> UnloadAsync(string extensionId, SessionId sessionId, CancellationToken cancellationToken = default)
        {
            var blocked = extensionId == "second";
            _extensions[extensionId] = _extensions[extensionId] with { IsLoaded = blocked, State = blocked ? "UnloadBlocked" : "Unloaded", GenerationId = blocked ? _extensions[extensionId].GenerationId : null };
            return Task.FromResult(!blocked);
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
                TakeRepositoryIntelligenceRefreshCommand => (object)false,
                SubmitRequestCommand => ObserveUnexpectedSubmitRequest(),
                WaitForRunCommand => (object)true,
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
        private readonly string? _waitFailure;

        public FixedRunDispatcher(SessionId sessionId, RunId runId, string? waitFailure = null)
        {
            _sessionId = sessionId;
            _runId = runId;
            _waitFailure = waitFailure;
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
                WaitForRunCommand when _waitFailure is not null => throw new InvalidOperationException(_waitFailure),
                WaitForRunCommand => true,
                _ => throw new InvalidOperationException($"Unexpected command {command.GetType().Name}."),
            };
            return Task.FromResult((TResponse)response);
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
            SessionUsageProjection usage,
            SessionApplication application)
        {
            _stream = stream;
            _projectionSubscription = projectionSubscription;
            _captureSubscription = captureSubscription;
            Projections = projections;
            Dispatcher = dispatcher;
            Events = events;
            Usage = usage;
            Application = application;
        }

        public ICommandDispatcher Dispatcher { get; }

        public ConcurrentBag<IDomainEvent> Events { get; }

        public IDomainEventStream EventStream => _stream;

        public InMemoryProjectionStore Projections { get; }

        public SessionUsageProjection Usage { get; }

        public SessionApplication Application { get; }

        public static Task<SessionHarness> CreateAsync(
            ScriptedSession script,
            TimeSpan? delay = null,
            IBudget? budget = null,
            IEnumerable<object>? additionalHandlers = null,
            IModelProvider? modelProvider = null,
            Func<IDomainEventStream, IProjectionStore, IEnumerable<object>>? additionalHandlerFactory = null)
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
                new BudgetDimensions(100000, 1000, TimeSpan.MaxValue));
            var usage = new SessionUsageProjection();
            var application = new SessionApplication(
                stream,
                modelProvider ?? new FakeModelProvider(script, delay),
                executionBudget,
                new SecretOutputSanitizer(),
                NullLogger<SessionApplication>.Instance,
                sessionUsage: usage,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance,
                budgetFactory: budget is null ? null : () => executionBudget);
            var handlers = new List<object> { application };
            if (additionalHandlers is not null)
            {
                handlers.AddRange(additionalHandlers);
            }

            if (additionalHandlerFactory is not null)
            {
                handlers.AddRange(additionalHandlerFactory(stream, projections));
            }

            ICommandDispatcher dispatcher = new CommandDispatcher(handlers);
            return Task.FromResult(new SessionHarness(
                stream,
                projectionSubscription,
                captureSubscription,
                projections,
                dispatcher,
                events,
                usage,
                application));
        }

        public async ValueTask DisposeAsync()
        {
            await _captureSubscription.DisposeAsync();
            await _projectionSubscription.DisposeAsync();
            await _stream.DisposeAsync();
        }
    }
}
