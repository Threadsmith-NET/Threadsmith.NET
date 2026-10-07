namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Tui.TuiKit;
using TUIKit;
using Xunit;

/// <summary>Verifies decorative startup timing independently of real operation completion.</summary>
public static class StartupModalTimingTests
{
    /// <summary>The random interval completes within its requested range and stays frozen during longer loading.</summary>
    [Fact]
    public static void SplinesCompleteBetweenHalfAndOneSecondWithoutInheritingLoadingTime()
    {
        var clock = new StartupClock();
        var modal = new StartupModal("Logo", "Loading solution", [], () => { }, _ => CellStyle.Default, timeProvider: clock);

        clock.Advance(TimeSpan.FromMilliseconds(499));
        Assert.False(modal.SplinesCompleted);
        Assert.Equal(TimeSpan.FromMilliseconds(499), modal.SplinesElapsed);
        clock.Advance(TimeSpan.FromMilliseconds(501));
        Assert.True(modal.SplinesCompleted);
        var duration = modal.SplinesElapsed;
        Assert.InRange(duration, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(1000));
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(duration, modal.SplinesElapsed);
        Assert.Equal(TimeSpan.FromSeconds(21), modal.Elapsed);
    }

    /// <summary>Real loading may finish before the decorative delay, which remains independent.</summary>
    [Fact]
    public static void EarlyLoadingCompletionDoesNotWaitForSplines()
    {
        var clock = new StartupClock();
        var modal = new StartupModal("Logo", "Loading solution", [], () => { }, _ => CellStyle.Default, timeProvider: clock);
        clock.Advance(TimeSpan.FromMilliseconds(50));

        modal.Complete();

        Assert.Equal(TimeSpan.FromMilliseconds(50), modal.Elapsed);
        Assert.False(modal.SplinesCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(modal.SplinesCompleted);
        Assert.Equal(TimeSpan.FromMilliseconds(50), modal.Elapsed);
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
