namespace Threadsmith.Execution;

/// <summary>Coordinates cancellation callbacks with retirement of a run's token source.</summary>
internal sealed class RunCancellationLifetime : IDisposable
{
    private readonly Lock _cancellationGate = new();
    private int _activeCancellations;
    private bool _disposalRequested;
    private bool _terminalCommitted;

    /// <summary>Commits a terminal result only while cancellation has not been admitted.</summary>
    public bool TryCommitTerminal(Func<bool> commit)
    {
        lock (_cancellationGate)
        {
            if (_disposalRequested || _activeCancellations != 0 || Source.IsCancellationRequested || _terminalCommitted || !commit())
            {
                return false;
            }

            _terminalCommitted = true;
            return true;
        }
    }

    /// <summary>Initializes a new instance of the <see cref="RunCancellationLifetime"/> class.</summary>
    internal RunCancellationLifetime(CancellationTokenSource source)
    {
        Source = source;
    }

    /// <summary>The owned source, available to the run until its completion.</summary>
    internal CancellationTokenSource Source { get; }

    /// <summary>Leases the source through callback completion without holding a lock across callbacks.</summary>
    public async Task<bool> CancelAsync()
    {
        lock (_cancellationGate)
        {
            if (_disposalRequested || _terminalCommitted)
            {
                return false;
            }

            _activeCancellations++;
        }

        try
        {
            await Source.CancelAsync();
            return true;
        }
        finally
        {
            lock (_cancellationGate)
            {
                _activeCancellations--;
                if (_disposalRequested && _activeCancellations == 0)
                {
                    Source.Dispose();
                }
            }
        }
    }

    /// <summary>Retires the source immediately or lets the last cancellation lease dispose it.</summary>
    public void Dispose()
    {
        lock (_cancellationGate)
        {
            if (_disposalRequested)
            {
                return;
            }

            _disposalRequested = true;
            if (_activeCancellations == 0)
            {
                Source.Dispose();
            }
        }
    }
}
