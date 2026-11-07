namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Xunit;

/// <summary>Startup timings follow actual work without changing readiness.</summary>
public static class StartupProgressTests
{
    /// <summary>An observation at its active-operation ceiling remains bounded without throwing.</summary>
    [Fact]
    public static void ActiveOperationCeilingDoesNotEvictRunningWork()
    {
        var progress = new SemanticStartupProgress();
        using var observation = progress.ObserveProcess();
        for (var index = 0; index < 256; index++)
        {
            Assert.NotNull(progress.Begin($"active.{index}", "Active operation", cancellationToken: TestContext.Current.CancellationToken));
        }

        Assert.Null(progress.Begin("overflow", "Overflow", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(256, observation.Snapshot.Count);
        Assert.All(observation.Snapshot, row => Assert.Equal(SemanticStartupPhaseState.Running, row.State));
    }

    /// <summary>Bounded history admits later work without evicting active operations.</summary>
    [Fact]
    public static void CompletedHistoryYieldsToLaterOperations()
    {
        var progress = new SemanticStartupProgress();
        using var observation = progress.ObserveProcess();
        using var active = progress.Begin("active", "Long operation", cancellationToken: TestContext.Current.CancellationToken);
        for (var index = 0; index < 300; index++)
        {
            using var operation = progress.Begin($"history.{index}", "Completed operation", cancellationToken: TestContext.Current.CancellationToken);
            operation?.Complete();
        }

        using var last = progress.Begin("last", "Later operation", cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(last);
        Assert.Equal(256, observation.Snapshot.Count);
        Assert.Equal("active", observation.Snapshot[0].Id);
        Assert.Equal("last", observation.Snapshot[^1].Id);
        Assert.DoesNotContain(observation.Snapshot, row => row.Id == "history.0");
        Assert.Equal(observation.Snapshot.Count, observation.Snapshot.Select(row => row.Order).Distinct().Count());
    }

    /// <summary>Process records share semantic timing and state rules.</summary>
    [Fact]
    public static void ProcessOperationsKeepStableOrderAndFreezeEveryOutcome()
    {
        var clock = new StartupClock();
        var progress = new SemanticStartupProgress(clock);
        using var observation = progress.ObserveProcess();
        using var first = progress.Begin("first", "First\u001b operation", 1, cancellationToken: TestContext.Current.CancellationToken);
        using var second = progress.Begin("second", "Second operation", 2, "first", TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(3));
        second!.Complete();
        clock.Advance(TimeSpan.FromSeconds(2));
        first!.Fail("Unavailable; using fallback.");
        using var skipped = progress.Begin("skipped", "Not required", 3, cancellationToken: TestContext.Current.CancellationToken);
        skipped!.Skip();
        clock.Advance(TimeSpan.FromSeconds(9));

        Assert.Equal(["first", "second", "skipped"], observation.Snapshot.Select(row => row.Id));
        Assert.Equal("First  operation", observation.Snapshot[0].Label);
        Assert.Equal("first", observation.Snapshot[1].ParentId);
        Assert.Equal(TimeSpan.FromSeconds(5), observation.Snapshot[0].Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(3), observation.Snapshot[1].Elapsed);
        Assert.Equal(TimeSpan.Zero, observation.Snapshot[2].Elapsed);
        Assert.Equal(SemanticStartupPhaseState.Failed, observation.Snapshot[0].State);
        Assert.Equal(SemanticStartupPhaseState.Skipped, observation.Snapshot[2].State);
        Assert.Equal("Unavailable; using fallback.", observation.Snapshot[0].Detail);
    }

    /// <summary>Completed timings remain fixed and old observers cannot remove their replacements.</summary>
    [Fact]
    public static void TimingsFreezeAtActualCompletionAndDoNotCrossSessionsOrObservationLifetimes()
    {
        var clock = new StartupClock();
        var progress = new SemanticStartupProgress(clock);
        var session = SessionId.New();
        Assert.Null(progress.Begin(session, SemanticStartupPhase.OpenWorkspace, TestContext.Current.CancellationToken));
        using var observation = progress.Observe(session);
        using var operation = progress.Begin(session, SemanticStartupPhase.OpenWorkspace, TestContext.Current.CancellationToken);
        Assert.NotNull(operation);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(observation.Snapshot).Elapsed);
        operation.Complete();
        clock.Advance(TimeSpan.FromSeconds(5));
        var completed = Assert.Single(observation.Snapshot);
        Assert.Equal(SemanticStartupPhaseState.Completed, completed.State);
        Assert.Equal(TimeSpan.FromSeconds(2), completed.Elapsed);
        using var other = progress.Observe(SessionId.New());
        Assert.Empty(other.Snapshot);
        using var replacement = progress.Observe(session);
        observation.Dispose();
        using var next = progress.Begin(session, SemanticStartupPhase.ReconcileSnapshots, TestContext.Current.CancellationToken);
        Assert.NotNull(next);
        Assert.Empty(observation.Snapshot);
        Assert.Equal(SemanticStartupPhase.ReconcileSnapshots, Assert.Single(replacement.Snapshot).Phase);
    }

    /// <summary>Disposal without successful completion preserves failure or cancellation.</summary>
    [Theory]
    [InlineData(false, SemanticStartupPhaseState.Failed)]
    [InlineData(true, SemanticStartupPhaseState.Cancelled)]
    public static void UnsuccessfulOperationsNeverReportSuccess(bool cancel, SemanticStartupPhaseState expected)
    {
        var progress = new SemanticStartupProgress();
        var session = SessionId.New();
        using var observation = progress.Observe(session);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var operation = progress.Begin(session, SemanticStartupPhase.ReconcileSnapshots, cancellation.Token);
        Assert.NotNull(operation);
        if (cancel)
        {
            cancellation.Cancel();
        }

        operation.Dispose();
        Assert.Equal(expected, Assert.Single(observation.Snapshot).State);
    }

    /// <summary>Completed rows are reused while live rows and replaced slots retain snapshot semantics.</summary>
    [Fact]
    public static void CachedSnapshotsPreserveLiveTimersAndInvalidateOnReplacement()
    {
        var clock = new StartupClock();
        var progress = new SemanticStartupProgress(clock);
        using var observation = progress.ObserveProcess();
        using var first = progress.Begin("first", "First", cancellationToken: TestContext.Current.CancellationToken);
        using var second = progress.Begin("second", "Second", cancellationToken: TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(1));
        first!.Complete();
        var before = observation.Snapshot;
        clock.Advance(TimeSpan.FromSeconds(2));
        var after = observation.Snapshot;
        Assert.Same(before[0], after[0]);
        Assert.Equal(TimeSpan.FromSeconds(1), before[1].Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(3), after[1].Elapsed);
        second!.Complete();
        var completed = observation.Snapshot;
        Assert.Same(completed, observation.Snapshot);
        Assert.Same(first.Snapshot, completed[0]);

        using var replacement = progress.Begin("first", "Replacement", cancellationToken: TestContext.Current.CancellationToken);
        var replaced = observation.Snapshot;
        Assert.Equal("Replacement", replaced[0].Label);
        Assert.Equal(SemanticStartupPhaseState.Running, replaced[0].State);
        Assert.Equal("First", completed[0].Label);
        replacement!.Complete();
        Assert.Equal(SemanticStartupPhaseState.Completed, observation.Snapshot[0].State);
        observation.Dispose();
        Assert.Empty(observation.Snapshot);
    }

    /// <summary>Completed history no longer creates a new record per row on every refresh.</summary>
    [Fact]
    public static void LiveSnapshotAllocationsAreLimitedToTheReferenceArrayAndActiveRow()
    {
        var progress = new SemanticStartupProgress();
        using var observation = progress.ObserveProcess();
        for (var index = 0; index < 255; index++)
        {
            using var operation = progress.Begin($"history.{index}", "History", cancellationToken: TestContext.Current.CancellationToken);
            operation!.Complete();
        }

        using var active = progress.Begin("active", "Active", cancellationToken: TestContext.Current.CancellationToken);
        _ = observation.Snapshot;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 100; index++)
        {
            _ = observation.Snapshot;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestContext.Current.TestOutputHelper!.WriteLine($"256-row live snapshots: {allocated / 100} bytes/refresh");
        Assert.InRange(allocated, 0, 100 * 4096);
    }

    private sealed class StartupClock : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            return _timestamp;
        }

        internal void Advance(TimeSpan duration)
        {
            _timestamp += duration.Ticks;
        }
    }
}
