namespace Threadsmith.Tui;

using System.Text;
using PrettyPrompt.Consoles;

/// <summary>Hot-key signals admitted while an active run owns the conversation.</summary>
internal enum ActiveRunInputSignal
{
    /// <summary>The first Escape key armed the bounded cancellation chord.</summary>
    CancellationArmed,

    /// <summary>A second Escape key requested active-run cancellation.</summary>
    CancellationRequested,

    /// <summary>Enter requested one idempotent safe-boundary steering prompt.</summary>
    SteeringRequested,
}

/// <summary>One exclusive active-run terminal input lease.</summary>
internal interface IActiveRunInputSession : IAsyncDisposable
{
    /// <summary>Waits for the next admitted active-run hot key.</summary>
    Task<ActiveRunInputSignal> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Serializes active-run hot keys with PrettyPrompt while replaying all non-hot-key input to the composer.
/// </summary>
internal sealed class BufferedPromptConsole : IConsole
{
    private const int MaximumBufferedKeys = 100_000;
    private readonly Lock _gate = new();
    private readonly IConsole _inner;
    private readonly Queue<ConsoleKeyInfo> _replay = [];
    private ActiveRunInputSession? _activeLease;

    /// <summary>Initializes a new instance of the <see cref="BufferedPromptConsole"/> class.</summary>
    public BufferedPromptConsole(IConsole inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc />
    public int CursorTop => _inner.CursorTop;

    /// <inheritdoc />
    public int BufferWidth => _inner.BufferWidth;

    /// <inheritdoc />
    public int WindowHeight => _inner.WindowHeight;

    /// <inheritdoc />
    public int WindowTop => _inner.WindowTop;

    /// <inheritdoc />
    public bool KeyAvailable
    {
        get
        {
            lock (_gate)
            {
                return _replay.Count > 0 || _inner.KeyAvailable;
            }
        }
    }

    /// <inheritdoc />
    public bool IsErrorRedirected => _inner.IsErrorRedirected;

    /// <inheritdoc />
    public bool CaptureControlC
    {
        get => _inner.CaptureControlC;
        set => _inner.CaptureControlC = value;
    }

    /// <inheritdoc />
    public event ConsoleCancelEventHandler? CancelKeyPress
    {
        add => _inner.CancelKeyPress += value;
        remove => _inner.CancelKeyPress -= value;
    }

    /// <summary>Begins an exclusive active-run lease when PrettyPrompt is inactive.</summary>
    public IActiveRunInputSession? TryBeginActiveRunInput(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        lock (_gate)
        {
            if (_activeLease is not null)
            {
                return null;
            }

            var lease = new ActiveRunInputSession(this, timeProvider);
            _activeLease = lease;
            return lease;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        _inner.Clear();
    }

    /// <inheritdoc />
    public ConsoleKeyInfo ReadKey(bool intercept)
    {
        lock (_gate)
        {
            if (_activeLease is not null)
            {
                throw new InvalidOperationException(
                    "PrettyPrompt cannot read while the active-run input lease is held.");
            }

            return _replay.Count > 0 ? _replay.Dequeue() : _inner.ReadKey(intercept);
        }
    }

    /// <inheritdoc />
    public void InitVirtualTerminalProcessing()
    {
        _inner.InitVirtualTerminalProcessing();
    }

    /// <inheritdoc />
    public void SetNewlineAutoReturn(bool enabled)
    {
        _inner.SetNewlineAutoReturn(enabled);
    }

    /// <inheritdoc />
    public void SetModifyOtherKeys(bool enabled)
    {
        _inner.SetModifyOtherKeys(enabled);
    }

    /// <inheritdoc />
    public void Write(StringBuilder value, bool hideCursor)
    {
        _inner.Write(value, hideCursor);
    }

    /// <inheritdoc />
    public void WriteLine(StringBuilder value, bool hideCursor)
    {
        _inner.WriteLine(value, hideCursor);
    }

    /// <inheritdoc />
    public void WriteError(StringBuilder value, bool hideCursor)
    {
        _inner.WriteError(value, hideCursor);
    }

    /// <inheritdoc />
    public void WriteErrorLine(StringBuilder value, bool hideCursor)
    {
        _inner.WriteErrorLine(value, hideCursor);
    }

    /// <inheritdoc />
    public void Write(string? value)
    {
        _inner.Write(value);
    }

    /// <inheritdoc />
    public void WriteLine(string? value)
    {
        _inner.WriteLine(value);
    }

    /// <inheritdoc />
    public void WriteError(string? value)
    {
        _inner.WriteError(value);
    }

    /// <inheritdoc />
    public void WriteErrorLine(string? value)
    {
        _inner.WriteErrorLine(value);
    }

    /// <inheritdoc />
    public void Write(ReadOnlySpan<char> value)
    {
        _inner.Write(value);
    }

    /// <inheritdoc />
    public void WriteLine(ReadOnlySpan<char> value)
    {
        _inner.WriteLine(value);
    }

    /// <inheritdoc />
    public void WriteError(ReadOnlySpan<char> value)
    {
        _inner.WriteError(value);
    }

    /// <inheritdoc />
    public void WriteErrorLine(ReadOnlySpan<char> value)
    {
        _inner.WriteErrorLine(value);
    }

    /// <inheritdoc />
    public void ShowCursor()
    {
        _inner.ShowCursor();
    }

    /// <inheritdoc />
    public void HideCursor()
    {
        _inner.HideCursor();
    }

    private bool TryReadAvailableBatch(
        ActiveRunInputSession lease,
        out IReadOnlyList<ConsoleKeyInfo> keys)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_activeLease, lease)
                || _replay.Count >= MaximumBufferedKeys
                || !TryGetKeyAvailable())
            {
                keys = [];
                return false;
            }

            var availableCapacity = MaximumBufferedKeys - _replay.Count;
            var batch = new List<ConsoleKeyInfo>();
            do
            {
                batch.Add(_inner.ReadKey(intercept: true));
            }
            while (batch.Count < availableCapacity && TryGetKeyAvailable());
            keys = batch;
            return true;
        }
    }

    private bool TryGetKeyAvailable()
    {
        try
        {
            return _inner.KeyAvailable;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private void QueueForPrompt(ActiveRunInputSession lease, IReadOnlyList<ConsoleKeyInfo> keys)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_activeLease, lease))
            {
                return;
            }

            foreach (var key in keys)
            {
                if (_replay.Count >= MaximumBufferedKeys)
                {
                    break;
                }

                _replay.Enqueue(key);
            }
        }
    }

    private bool HasBufferedPromptInput(ActiveRunInputSession lease)
    {
        lock (_gate)
        {
            return ReferenceEquals(_activeLease, lease) && _replay.Count > 0;
        }
    }

    private void Release(ActiveRunInputSession lease)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeLease, lease))
            {
                _activeLease = null;
            }
        }
    }

    private sealed class ActiveRunInputSession : IActiveRunInputSession
    {
        private static readonly TimeSpan EscapeChordWindow = TimeSpan.FromMilliseconds(850);
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);
        private readonly CancellationTokenSource _disposed = new();
        private readonly BufferedPromptConsole _owner;
        private readonly TimeProvider _timeProvider;
        private DateTimeOffset? _escapeArmedAt;
        private TaskCompletionSource? _readCompletion;
        private int _readActive;

        public ActiveRunInputSession(BufferedPromptConsole owner, TimeProvider timeProvider)
        {
            _owner = owner;
            _timeProvider = timeProvider;
        }

        /// <inheritdoc />
        public async Task<ActiveRunInputSignal> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _readCompletion, completion);
            if (Interlocked.Exchange(ref _readActive, 1) != 0)
            {
                Interlocked.CompareExchange(ref _readCompletion, null, completion);
                throw new InvalidOperationException("Only one active-run input read may be pending.");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposed.Token);
            try
            {
                while (true)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (!_owner.TryReadAvailableBatch(this, out var keys))
                    {
                        await Task.Delay(PollInterval, _timeProvider, linked.Token);
                        continue;
                    }

                    if (TryClassify(keys, out var signal))
                    {
                        return signal;
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _readActive, 0);
                completion.TrySetResult();
            }
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            await _disposed.CancelAsync();
            if (Volatile.Read(ref _readCompletion) is { } completion)
            {
                await completion.Task;
            }

            _owner.Release(this);
            _disposed.Dispose();
        }

        private bool TryClassify(
            IReadOnlyList<ConsoleKeyInfo> keys,
            out ActiveRunInputSignal signal)
        {
            if (keys.Count >= 2 && keys.All(IsUnmodifiedEscape))
            {
                _escapeArmedAt = null;
                signal = ActiveRunInputSignal.CancellationRequested;
                return true;
            }

            if (keys.Count >= 1
                && keys.All(IsUnmodifiedEnter)
                && !_owner.HasBufferedPromptInput(this))
            {
                _escapeArmedAt = null;
                signal = ActiveRunInputSignal.SteeringRequested;
                return true;
            }

            if (keys.Count != 1)
            {
                _escapeArmedAt = null;
                _owner.QueueForPrompt(this, keys);
                signal = default;
                return false;
            }

            var key = keys[0];
            if (IsUnmodifiedEnter(key))
            {
                _escapeArmedAt = null;
                if (_owner.HasBufferedPromptInput(this))
                {
                    _owner.QueueForPrompt(this, keys);
                    signal = default;
                    return false;
                }

                signal = ActiveRunInputSignal.SteeringRequested;
                return true;
            }

            if (IsUnmodifiedEscape(key))
            {
                var now = _timeProvider.GetUtcNow();
                if (_escapeArmedAt is { } armedAt && now - armedAt <= EscapeChordWindow)
                {
                    _escapeArmedAt = null;
                    signal = ActiveRunInputSignal.CancellationRequested;
                    return true;
                }

                _escapeArmedAt = now;
                signal = ActiveRunInputSignal.CancellationArmed;
                return true;
            }

            _escapeArmedAt = null;
            _owner.QueueForPrompt(this, keys);
            signal = default;
            return false;
        }

        private static bool IsUnmodifiedEnter(ConsoleKeyInfo key)
        {
            return key.Key == ConsoleKey.Enter && key.Modifiers == 0;
        }

        private static bool IsUnmodifiedEscape(ConsoleKeyInfo key)
        {
            return key.Key == ConsoleKey.Escape && key.Modifiers == 0;
        }
    }
}
