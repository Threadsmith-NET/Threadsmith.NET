namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>Retains source admission only while automated execution is active.</summary>
internal sealed class ToolExecutionAdmission : IDisposable
{
    private readonly ToolSourceConcurrencyLimiter _limiter;
    private readonly ToolConcurrencyScope _source;
    private readonly int _maximumConcurrency;
    private readonly Action _validate;
    private readonly CancellationToken _executionCancellation;
    private readonly UserInteractionScope _interaction;
    private IDisposable? _lease;

    /// <summary>Initializes a new instance of the <see cref="ToolExecutionAdmission"/> class.</summary>
    public ToolExecutionAdmission(ToolSourceConcurrencyLimiter limiter, ToolConcurrencyScope source, int maximumConcurrency, Action validate, CancellationToken executionCancellation)
    {
        _limiter = limiter;
        _source = source;
        _maximumConcurrency = maximumConcurrency;
        _validate = validate;
        _executionCancellation = executionCancellation;
        _interaction = new UserInteractionScope(Release, ResumeAsync);
    }

    /// <summary>Acquires through the ordinary source concurrency limiter.</summary>
    public async Task AcquireAsync(CancellationToken cancellationToken)
    {
        _lease = await _limiter.AcquireAsync(_source, _maximumConcurrency, cancellationToken);
    }

    /// <summary>Bounds even synchronous external dispatch and retains source admission until the work finishes.</summary>
    public async Task<ToolExecutionEnvelope> AwaitExternalWriteAsync(
        Func<Action, CancellationToken, Task<ToolExecutionEnvelope>> invoke,
        ToolSuspensionCompletion? completion,
        CancellationToken cancellationToken)
    {
        const int pending = 0;
        const int dispatched = 1;
        const int closed = 2;
        var dispatchState = pending;
        var operation = Task.Run(DispatchAsync, cancellationToken);
        try
        {
            return await operation.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var retainedLease = _lease;
            _lease = null;
            _ = operation.ContinueWith(
                static (completed, state) =>
                {
                    _ = completed.Exception;
                    (state as IDisposable)?.Dispose();
                },
                retainedLease,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
        finally
        {
            var started = Interlocked.Exchange(ref dispatchState, closed) == dispatched;
            completion?.ExternalWriteDispatched = started;
        }

        Task<ToolExecutionEnvelope> DispatchAsync()
        {
            _validate();
            cancellationToken.ThrowIfCancellationRequested();
            return invoke(MarkDispatched, cancellationToken);
        }

        void MarkDispatched()
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The terminal path closes this gate before it reports that the operation did not start.
            if (Interlocked.CompareExchange(ref dispatchState, dispatched, pending) != pending)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _interaction.Dispose();
        Release();
    }

    private void Release()
    {
        _lease?.Dispose();
        _lease = null;
    }

    private async Task ResumeAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || _executionCancellation.IsCancellationRequested)
        {
            return;
        }

        using var resumeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _executionCancellation);
        _validate();
        await AcquireAsync(resumeCancellation.Token);
        _validate();
    }
}
