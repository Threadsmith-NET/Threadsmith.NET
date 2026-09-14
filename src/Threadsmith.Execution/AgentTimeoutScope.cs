namespace Threadsmith.Execution;

using System.Diagnostics;

/// <summary>Owns caller-linked cancellation with optional timeouts beyond the runtime timer interval.</summary>
internal sealed class AgentTimeoutScope : IAsyncDisposable
{
    private static readonly TimeSpan _maximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);
    private readonly CancellationTokenSource _timerStop = new();
    private readonly Task _timerTask;
    private int _timedOut;

    /// <summary>Initializes a new instance of the <see cref="AgentTimeoutScope"/> class.</summary>
    public AgentTimeoutScope(TimeSpan timeout, CancellationToken cancellationToken)
    {
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
        var started = Stopwatch.GetTimestamp();
        try
        {
            while (Stopwatch.GetElapsedTime(started) < timeout)
            {
                var remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                await Task.Delay(remaining < _maximumTimerDelay ? remaining : _maximumTimerDelay, cancellationToken).ConfigureAwait(false);
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
