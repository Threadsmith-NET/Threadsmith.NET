namespace Threadsmith.Execution;

using System.Runtime.ExceptionServices;
using System.Text;
using Threadsmith.Core;

/// <summary>Coalesces sanitized display text while retaining awaited, ordered event delivery.</summary>
internal sealed class ModelOutputCoalescer : IAsyncDisposable
{
    private readonly IDomainEventStream _events;
    private readonly SessionId _sessionId;
    private readonly int _maximumCharacters;
    private readonly TimeSpan _flushInterval;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime;
    private readonly PeriodicTimer _timer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _pending = new();
    private readonly Task _worker;
    private bool _published;
    private ExceptionDispatchInfo? _publicationFailure;

    /// <summary>Joins timed delivery and drains accepted text, including on cancellation or provider failure.</summary>
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        try
        {
            try
            {
#pragma warning disable VSTHRD003 // This scope owns and joins its sole timer worker.
                await _worker;
#pragma warning restore VSTHRD003
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }

            if (_publicationFailure is null)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5), _timeProvider);
                await FlushAsync(cleanup.Token);
            }
        }
        finally
        {
            _timer.Dispose();
            _lifetime.Dispose();
            _gate.Dispose();
        }
    }

    /// <summary>Initializes a new instance of the <see cref="ModelOutputCoalescer"/> class.</summary>
    internal ModelOutputCoalescer(
        IDomainEventStream events,
        SessionId sessionId,
        ExecutionLimits limits,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(timeProvider);
        limits.Validate();
        _events = events;
        _sessionId = sessionId;
        _maximumCharacters = limits.MaxModelOutputBatchCharacters;
        _flushInterval = TimeSpan.FromMilliseconds(limits.ModelOutputFlushIntervalMilliseconds);
        _timeProvider = timeProvider;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = new PeriodicTimer(Timeout.InfiniteTimeSpan, timeProvider);
        _worker = PublishOnTimerAsync();
    }

    /// <summary>Stops provider consumption if timed publication fails.</summary>
    internal CancellationToken Token => _lifetime.Token;

    /// <summary>Appends already-sanitized text without changing raw response accounting.</summary>
    internal async Task AppendAsync(string text, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _publicationFailure?.Throw();
            var offset = 0;
            while (offset < text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(_maximumCharacters - _pending.Length, text.Length - offset);

                // Do not split a surrogate pair already present in a sanitized provider fragment.
                if (count > 0 && offset + count < text.Length
                    && char.IsHighSurrogate(text[offset + count - 1]) && char.IsLowSurrogate(text[offset + count]))
                {
                    count--;
                }

                if (_pending.Length == 0)
                {
                    _timer.Period = _flushInterval;
                }

                _pending.Append(text, offset, count);
                offset += count;
                if (!_published || _pending.Length == _maximumCharacters || count == 0)
                {
                    await PublishPendingAsync();
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drains prior text before the caller publishes another kind of observation.</summary>
    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _publicationFailure?.Throw();
            await PublishPendingAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PublishOnTimerAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_lifetime.Token))
            {
                await FlushAsync(_lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch
        {
            await _lifetime.CancelAsync();
            throw;
        }
    }

    private async Task PublishPendingAsync()
    {
        if (_pending.Length == 0)
        {
            return;
        }

        var text = _pending.ToString();
        _pending.Clear();
        _timer.Period = Timeout.InfiniteTimeSpan;

        // Bound admission independently of run cancellation. Once committed, the event stream
        // owns bounded delivery and quarantines stalled subscribers before later events.
        using var admission = new CancellationTokenSource(TimeSpan.FromSeconds(5), _timeProvider);
        try
        {
            await _events.PublishCommittedBatchAsync(
                [new ModelOutputObserved(_sessionId, _timeProvider.GetUtcNow(), text)],
                static () => true,
                admission.Token);
            _published = true;
        }
        catch (Exception exception)
        {
            // Some subscribers may already have accepted the event. Retrying could duplicate text.
            _publicationFailure = ExceptionDispatchInfo.Capture(exception);
            if (exception is OperationCanceledException && admission.IsCancellationRequested)
            {
                var timeout = new TimeoutException("Model output admission exceeded its bounded wait interval.", exception);
                _publicationFailure = ExceptionDispatchInfo.Capture(timeout);
                throw timeout;
            }

            throw;
        }
    }
}
