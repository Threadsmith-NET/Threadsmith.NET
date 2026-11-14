namespace Threadsmith.Tools;

using System.Threading.Channels;

/// <summary>Startup preferences for fresh, single-use scripting workers.</summary>
public sealed record ScriptWorkerOptions
{
    /// <summary>Gets the number of unused workers to prepare; zero disables pooling.</summary>
    public int NumberOfWarmWorkersInPool { get; init; } = 1;

    /// <summary>Gets the worker creation and acquisition timeout in milliseconds.</summary>
    public int WorkerCreationTimeOut { get; init; } = 3000;

    /// <summary>Rejects invalid startup bounds.</summary>
    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(NumberOfWarmWorkersInPool);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(WorkerCreationTimeOut);
    }
}

/// <summary>Owns background creation and idle lifetime, never recycling a used worker.</summary>
internal sealed class ScriptWorkerPool<T> : IAsyncDisposable
    where T : class, IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Channel<Creation> _ready = Channel.CreateUnbounded<Creation>();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _creatorsStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<CancellationToken, Task<T>> _create;
    private readonly ScriptWorkerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly List<Exception> _shutdownErrors = [];
    private int _slots;
    private int _creators;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="ScriptWorkerPool{T}"/> class.</summary>
    internal ScriptWorkerPool(ScriptWorkerOptions options, Func<CancellationToken, Task<T>> create, TimeProvider timeProvider)
    {
        options.Validate();
        _options = options;
        _create = create;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_creators == 0)
            {
                _creatorsStopped.TrySetResult();
            }
        }

        await _shutdown.CancelAsync();
        await _creatorsStopped.Task.WaitAsync(CancellationToken.None);
        while (_ready.Reader.TryRead(out var creation))
        {
            if (creation.Worker is { } worker)
            {
                try
                {
                    await worker.DisposeAsync();
                }
                catch (Exception exception)
                {
                    _shutdownErrors.Add(exception);
                }
            }
        }

        _shutdown.Dispose();
        if (_shutdownErrors.Count > 0)
        {
            throw new AggregateException("Scripting workers could not be disposed.", _shutdownErrors);
        }
    }

    /// <summary>Schedules the initial unused workers without running the factory inline.</summary>
    internal void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Fill();
        }
    }

    /// <summary>Waits for one unused worker and schedules replacements before returning it.</summary>
    internal async Task<T> RentAsync(CancellationToken cancellationToken)
    {
        Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(_options.WorkerCreationTimeOut), _timeProvider);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token, _shutdown.Token);
        Creation creation;
        try
        {
            creation = await _ready.Reader.ReadAsync(waiting.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            throw new TimeoutException($"No fresh scripting worker became available within {_options.WorkerCreationTimeOut} ms.");
        }

        lock (_gate)
        {
            _slots--;
            if (!_disposed)
            {
                // Queue every replacement on the thread pool BEFORE handing a worker to script execution.
                Fill();
            }
        }

        if (creation.Error is TimeoutException timeout)
        {
            throw new TimeoutException(timeout.Message, timeout);
        }

        if (creation.Error is { } error)
        {
            throw new InvalidOperationException("The C# scripting worker could not be prepared.", error);
        }

        var worker = creation.Worker ?? throw new InvalidOperationException("Worker creation produced no worker.");
        if (_disposed)
        {
            await worker.DisposeAsync();
            throw new ObjectDisposedException(nameof(ScriptWorkerPool<>));
        }

        return worker;
    }

    private void Fill()
    {
        while (_slots < _options.NumberOfWarmWorkersInPool)
        {
            _slots++;
            _creators++;

            // Do not execute process launch, compiler startup, or any synchronous factory prefix on the caller.
            _ = Task.Run(ProduceAsync, CancellationToken.None);
        }
    }

    private async Task ProduceAsync()
    {
        T? worker = null;
        try
        {
            Creation creation;
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(_options.WorkerCreationTimeOut), _timeProvider);
                using var creating = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, deadline.Token);
                worker = await _create(creating.Token);
                creation = new Creation(worker, null);
            }
            catch (OperationCanceledException exception) when (!_shutdown.IsCancellationRequested)
            {
                creation = new Creation(null, new TimeoutException($"Scripting worker creation exceeded {_options.WorkerCreationTimeOut} ms.", exception));
            }
            catch (Exception exception)
            {
                creation = new Creation(null, exception);
            }

            lock (_gate)
            {
                if (!_disposed)
                {
                    _ready.Writer.TryWrite(creation);
                    worker = null; // Ownership transfers to the queue.
                }
            }

            if (worker is not null)
            {
                await worker.DisposeAsync();
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _shutdownErrors.Add(exception);
            }
        }
        finally
        {
            lock (_gate)
            {
                _creators--;
                if (_disposed && _creators == 0)
                {
                    _creatorsStopped.TrySetResult();
                }
            }
        }
    }

    private sealed record Creation(T? Worker, Exception? Error);
}
