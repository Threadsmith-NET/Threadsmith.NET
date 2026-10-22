namespace Threadsmith.Execution;

/// <summary>Owns caller-linked cancellation with optional timeouts beyond the runtime timer interval.</summary>
internal sealed class AgentTimeoutScope : IAsyncDisposable
{
    private static readonly TimeSpan _maximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);
    private readonly CancellationTokenSource _timerStop = new();
    private readonly Task _timerTask;
    private readonly TimeProvider _timeProvider;
    private int _timedOut;

    /// <summary>Initializes a new instance of the <see cref="AgentTimeoutScope"/> class.</summary>
    public AgentTimeoutScope(TimeSpan timeout, CancellationToken cancellationToken)
        : this(timeout, TimeProvider.System, cancellationToken)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="AgentTimeoutScope"/> class using the scheduler clock.</summary>
    public AgentTimeoutScope(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        Source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timerTask = Task.CompletedTask;
        if (timeout > TimeSpan.Zero)
        {
            _timerTask = CancelAfterTimeoutAsync(timeout, _timerStop.Token);
        }
    }

    /// <summary>Gets the source registered with the scheduler for targeted cancellation.</summary>
    public CancellationTokenSource Source { get; }

    /// <summary>Gets cancellation shared by the caller and the optional timeout.</summary>
    public CancellationToken Token => Source.Token;

    /// <summary>Whether the deadline triggered cancellation before the caller or scheduler cancelled the child.</summary>
    public bool TimedOut => Volatile.Read(ref _timedOut) != 0;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _timerStop.CancelAsync().ConfigureAwait(false);
        try
        {
#pragma warning disable VSTHRD003 // This scope starts and owns the context-free timer task, and cancellation above terminates its delay.
            await _timerTask.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
        finally
        {
            _timerStop.Dispose();
            Source.Dispose();
        }
    }

    private async Task CancelAfterTimeoutAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var started = _timeProvider.GetTimestamp();
        try
        {
            while (_timeProvider.GetElapsedTime(started) < timeout)
            {
                var remaining = timeout - _timeProvider.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(remaining < _maximumTimerDelay ? remaining : _maximumTimerDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!Source.IsCancellationRequested)
            {
                Volatile.Write(ref _timedOut, 1);
                await Source.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
