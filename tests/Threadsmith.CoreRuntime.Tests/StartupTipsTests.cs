namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Tui.TuiKit;
using Xunit;

/// <summary>Verifies tip timing and deck exhaustion.</summary>
public static class StartupTipsTests
{
    /// <summary>Each tip remains visible for five seconds and every cycle contains the complete deck.</summary>
    [Fact]
    public static void RotationHoldsEachTipForFiveSecondsAndExhaustsEveryCycle()
    {
        var clock = new TipClock();
        string[] tips = ["first", "second", "third", "fourth"];
        var rotation = new StartupTips(tips, clock);
        string? previous = null;

        for (var cycle = 0; cycle < 3; cycle++)
        {
            var seen = new HashSet<string>();
            for (var index = 0; index < tips.Length; index++)
            {
                var current = rotation.Current;
                Assert.NotNull(current);
                Assert.NotEqual(previous, current);
                Assert.True(seen.Add(current));
                clock.Advance(TimeSpan.FromMilliseconds(4999));
                Assert.Equal(current, rotation.Current);
                clock.Advance(TimeSpan.FromMilliseconds(1));
                previous = current;
            }

            Assert.Equal(tips.Order(), seen.Order());
        }
    }

    /// <summary>Rotation follows the file's count, including zero or one tip.</summary>
    [Fact]
    public static void EmptyAndSingleTipFilesNeedNoFixedCount()
    {
        var clock = new TipClock();
        Assert.Null(new StartupTips([], clock).Current);
        var rotation = new StartupTips(["only tip"], clock);
        Assert.Equal("only tip", rotation.Current);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal("only tip", rotation.Current);
    }

    private sealed class TipClock : TimeProvider
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
