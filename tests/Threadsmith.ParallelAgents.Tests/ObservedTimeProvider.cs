namespace Threadsmith.ParallelAgents.Tests;

using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

/// <summary>Exposes timer registration so tests advance time only after the deadline exists.</summary>
internal sealed class ObservedTimeProvider : FakeTimeProvider
{
    private readonly Channel<TimeSpan> _registered = Channel.CreateUnbounded<TimeSpan>();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        _registered.Writer.TryWrite(dueTime);
        return timer;
    }

    public async Task WaitForTimerAsync(TimeSpan dueTime, CancellationToken cancellationToken)
    {
        while (await _registered.Reader.ReadAsync(cancellationToken) != dueTime)
        {
        }
    }
}
