namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Xunit;

/// <summary>Startup timings follow actual work without changing readiness.</summary>
public static class StartupProgressTests
{
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

    private sealed class StartupClock : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        internal void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
