namespace Threadsmith.ModelTooling.Tests;

/// <summary>Completes one-shot retry delays by advancing the fake clock at registration.</summary>
internal sealed class AdvancingTimeProvider : FakeTimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
        {
            Advance(dueTime);
        }

        return timer;
    }
}
