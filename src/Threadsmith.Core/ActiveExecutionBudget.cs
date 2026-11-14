namespace Threadsmith.Core;

/// <summary>Bounds automated execution without charging time spent waiting for a person.</summary>
public sealed class ActiveExecutionBudget : IDisposable
{
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _cancellation;
    private readonly UserInteractionScope _interaction;
    private TimeSpan _remaining;
    private long _started;
    private bool _paused;

    /// <summary>Initializes a new instance of the <see cref="ActiveExecutionBudget"/> class.</summary>
    public ActiveExecutionBudget(TimeSpan allowance, CancellationToken cancellationToken, TimeProvider? timeProvider = null)
    {
        _clock = timeProvider ?? TimeProvider.System;
        _remaining = allowance;
        _started = _clock.GetTimestamp();
        _deadline = new CancellationTokenSource(allowance, _clock);
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _deadline.Token);
        _interaction = new UserInteractionScope(Pause, ResumeAsync);
    }

    /// <summary>Cancellation from the caller or exhausted active allowance.</summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>Remaining automated allowance, without resetting the original budget.</summary>
    public TimeSpan Remaining => _paused || _remaining == Timeout.InfiniteTimeSpan
        ? _remaining
        : TimeSpan.FromTicks(Math.Max(0, (_remaining - _clock.GetElapsedTime(_started)).Ticks));

    /// <inheritdoc />
    public void Dispose()
    {
        _interaction.Dispose();
        _cancellation.Dispose();
        _deadline.Dispose();
    }

    private void Pause()
    {
        _deadline.CancelAfter(Timeout.InfiniteTimeSpan);
        _remaining = Remaining;
        _paused = true;
    }

    private Task ResumeAsync(CancellationToken cancellationToken)
    {
        _started = _clock.GetTimestamp();
        _paused = false;
        _deadline.CancelAfter(_remaining);
        return Task.CompletedTask;
    }
}
