namespace Threadsmith.Tui.TuiKit;

using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Threadsmith.Interaction.Runs;

/// <summary>Owns bounded steering and cancellation input for one active model run.</summary>
internal sealed class ActiveInputLease : IActiveRunInputLease
{
    private readonly CancellationTokenSource _disposed = new();
    private readonly Lock _gate = new();
    private readonly TuiKitSurface _owner;
    private readonly TimeProvider _clock;
    private readonly Channel<ActiveRunInputSignal> _signals = Channel.CreateBounded<ActiveRunInputSignal>(3);
    private Task? _disposeTask;
    private TaskCompletionSource? _readCompletion;
    private long? _escape;
    private bool _disposeStarted;
    private bool _readActive;
    private bool _steered;
    private bool _cancelled;

    /// <summary>Initializes a new instance of the <see cref="ActiveInputLease"/> class.</summary>
    internal ActiveInputLease(TuiKitSurface owner, TimeProvider clock)
    {
        _owner = owner;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<ActiveRunInputSignal> ReadAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposeStarted, this);
            if (_readActive)
            {
                throw new InvalidOperationException("Only one active-run input read may be pending.");
            }

            _readActive = true;
            _readCompletion = completion;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        try
        {
            return await _signals.Reader.ReadAsync(linked.Token);
        }
        finally
        {
            lock (_gate)
            {
                _readActive = false;
                if (ReferenceEquals(_readCompletion, completion))
                {
                    _readCompletion = null;
                }
            }

            completion.TrySetResult();
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposeStarted = true;
            _disposeTask = DisposeCoreAsync(_readCompletion?.Task);
            return new ValueTask(_disposeTask);
        }
    }

    /// <summary>Requests one steering prompt per active-run lease.</summary>
    internal void Steer()
    {
        if (!_steered)
        {
            _steered = true;
            _signals.Writer.TryWrite(ActiveRunInputSignal.SteeringRequested);
        }
    }

    /// <summary>Arms cancellation and emits it after a second nearby Escape.</summary>
    internal void Escape()
    {
        if (_cancelled)
        {
            return;
        }

        var now = _clock.GetTimestamp();
        if (_escape is { } previous && _clock.GetElapsedTime(previous, now).TotalMilliseconds <= 850)
        {
            _cancelled = true;
            _escape = null;
            _signals.Writer.TryWrite(ActiveRunInputSignal.CancellationRequested);
        }
        else
        {
            _escape = now;
            _signals.Writer.TryWrite(ActiveRunInputSignal.CancellationArmed);
        }
    }

    [SuppressMessage("Usage", "VSTHRD003", Justification = "The lease joins its owned pending read before closing the signal channel.")]
    private async Task DisposeCoreAsync(Task? pendingRead)
    {
        await _disposed.CancelAsync();
        if (pendingRead is not null)
        {
            await pendingRead;
        }

        _owner.ReleaseActiveInput(this);
        _signals.Writer.TryComplete();
        _disposed.Dispose();
    }
}
