namespace Threadsmith.DotNet;

using System.Diagnostics;
using Microsoft.CodeAnalysis;

/// <summary>Immutable engine-owned bounds; deliberately absent from repository configuration.</summary>
internal sealed record SemanticPreparationLimits
{
    /// <summary>Gets the maximum simultaneous preparation operations.</summary>
    public int Workers { get; init; } = 1;

    /// <summary>Gets the number of initial graph-ranked candidates admitted together.</summary>
    public int Frontier { get; init; } = 1;

    /// <summary>Rejects fan-out beyond the reviewed conservative range.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(Workers, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Workers, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(Frontier, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Frontier, Workers);
    }
}

/// <summary>A detached preparation outcome inside the compiler-aware subsystem.</summary>
internal sealed record SemanticPreparationOutcome(ProjectId ProjectId, bool Succeeded, bool Obsolete = false, string? Failure = null);

/// <summary>Fences both source replacement and explicit coverage invalidation after demand admission.</summary>
internal sealed record SemanticPreparationReceipt(Solution Solution, long Generation);

/// <summary>One bounded priority queue and one shared completion per project in an immutable solution.</summary>
internal sealed class SemanticCompilationCoordinator : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly Dictionary<ProjectId, Entry> _entries;
    private readonly Queue<ProjectId> _demand = new();
    private readonly Queue<ProjectId> _warm = new();
    private readonly Queue<CompilerOperation> _operations = new();
    private readonly Func<ProjectId, CancellationToken, Task<SemanticPreparationOutcome>> _prepare;
    private readonly Func<SemanticCompilationCoordinator, SemanticPreparationOutcome, CancellationToken, Task> _publish;
    private readonly Task[] _workers;
    private readonly TaskCompletionSource _disposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stopped;
    private int _running;
    private int _maximumRunning;
    private int _joins;
    private int _promotions;
    private int _disposed;
    private int _attempts;
    private int _discarded;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
#pragma warning disable VSTHRD003 // All disposal callers join the coordinator-owned disposal.
            await _disposal.Task;
#pragma warning restore VSTHRD003
            return;
        }

        Abort();
#pragma warning disable VSTHRD003 // Workers are generation-owned and compiler waits have the engine's bounded backstop.
        await Task.WhenAll(_workers);
#pragma warning restore VSTHRD003
        _lifetime.Dispose();
        _available.Dispose();
        _disposal.TrySetResult();
    }

    /// <summary>Initializes a new instance of the <see cref="SemanticCompilationCoordinator"/> class without queuing compiler work.</summary>
    internal SemanticCompilationCoordinator(
        Solution solution,
        SemanticPreparationLimits limits,
        IReadOnlySet<ProjectId> alreadyPrepared,
        Func<ProjectId, CancellationToken, Task<SemanticPreparationOutcome>> prepare,
        Func<SemanticCompilationCoordinator, SemanticPreparationOutcome, CancellationToken, Task> publish)
    {
        limits.Validate();
        Solution = solution;
        _prepare = prepare;
        _publish = publish;
        _entries = solution.ProjectIds.ToDictionary(id => id, id => new Entry(id, alreadyPrepared.Contains(id)));
        _workers = [.. Enumerable.Range(0, limits.Workers).Select(_ => Task.Run(WorkAsync))];
    }

    /// <summary>Gets the exact immutable solution prepared by this generation.</summary>
    internal Solution Solution { get; }

    /// <summary>Gets the beginning of this evaluated generation's preparation lifetime.</summary>
    internal long StartedAt { get; } = Stopwatch.GetTimestamp();

    /// <summary>Gets the all-project terminal signal, including never-admitted obsolete work.</summary>
#pragma warning disable VSTHRD003 // Project completion sources and workers belong to this coordinator.
    internal Task<SemanticPreparationOutcome[]> Completion => Task.WhenAll(_entries.Values.Select(entry => entry.Completion.Task));
#pragma warning restore VSTHRD003

    /// <summary>Gets bounded diagnostic counts.</summary>
    internal (int MaximumRunning, int Joins, int Promotions, int Attempts, int Discarded, int Demand, int Warm) Statistics
    {
        get
        {
            lock (_gate)
            {
                return (_maximumRunning, _joins, _promotions, _attempts, _discarded,
                    _entries.Values.Count(entry => entry.State == PreparationState.Demand) + _operations.Count,
                    _entries.Values.Count(entry => entry.State == PreparationState.Warm));
            }
        }
    }

    /// <summary>Admits transient compiler work through the same bounded foreground queue.</summary>
    internal Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        return RunQueryAsync(operation, default, cancellationToken);
    }

    /// <summary>Bounds queue waiting by the query deadline while preserving partial results from started queries.</summary>
    internal async Task<T> RunQueryAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken queryDeadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        queryDeadline.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var admissionState = 0;
        using var deadlineRegistration = queryDeadline.Register(() =>
        {
            if (Interlocked.CompareExchange(ref admissionState, -1, 0) == 0)
            {
                completion.TrySetCanceled(queryDeadline);
            }
        });
        lock (_gate)
        {
            if (_stopped != 0)
            {
                throw new InvalidOperationException("The semantic compilation generation was superseded.");
            }

            if (_operations.Count >= 32)
            {
                throw new InvalidOperationException("The semantic foreground compilation queue is full.");
            }

            _operations.Enqueue(new(
                async token =>
                {
                    if (Interlocked.CompareExchange(ref admissionState, 1, 0) != 0)
                    {
                        return;
                    }

                    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
                    try
                    {
                        lifetime.Token.ThrowIfCancellationRequested();
                        var value = await operation(lifetime.Token);
                        lifetime.Token.ThrowIfCancellationRequested();
                        completion.TrySetResult(value);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && token.IsCancellationRequested)
                    {
                        completion.TrySetException(new InvalidOperationException("The semantic compilation generation was superseded."));
                    }
                    catch (OperationCanceledException)
                    {
                        completion.TrySetCanceled(lifetime.Token);
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                },
                token =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        completion.TrySetCanceled(cancellationToken);
                    }
                    else
                    {
                        completion.TrySetException(new InvalidOperationException("The semantic compilation generation was superseded."));
                    }
                }));
            _available.Release();
            foreach (var running in _entries.Values.Where(entry => entry.State == PreparationState.Running && !entry.Demanded))
            {
                if (running.Interruption is { IsCancellationRequested: false } interruption)
                {
                    running.InterruptionCompletion = interruption.CancelAsync();
                    _ = running.InterruptionCompletion.ContinueWith(
                        completed => _ = completed.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
        }

#pragma warning disable VSTHRD003 // The bounded worker owns this operation's completion.
        return await completion.Task.WaitAsync(cancellationToken);
#pragma warning restore VSTHRD003
    }

    /// <summary>Joins or promotes a project; a caller's cancellation only cancels its wait.</summary>
    internal Task<SemanticPreparationOutcome> PrepareAsync(ProjectId id, bool demand, CancellationToken cancellationToken)
    {
        Task<SemanticPreparationOutcome> completion;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var entry))
            {
                throw new ArgumentException("The preparation project is not in this solution.", nameof(id));
            }

            completion = entry.Completion.Task;
            entry.Demanded |= demand;
            if (entry.State == PreparationState.Pending && _stopped == 0)
            {
                entry.State = demand ? PreparationState.Demand : PreparationState.Warm;
                (demand ? _demand : _warm).Enqueue(id);
                _available.Release();
            }
            else if (demand && entry.State == PreparationState.Warm && _stopped == 0)
            {
                entry.State = PreparationState.Demand;
                _demand.Enqueue(id);
                Interlocked.Increment(ref _promotions);
                _available.Release();
            }
            else
            {
                Interlocked.Increment(ref _joins);
            }
        }

#pragma warning disable VSTHRD003 // This coordinator owns the shared project completion task.
        return completion.WaitAsync(cancellationToken);
#pragma warning restore VSTHRD003
    }

    /// <summary>Queues remaining projects once in stable graph order after initial publication.</summary>
    internal void Warm(IEnumerable<ProjectId> order)
    {
        foreach (var id in order)
        {
            _ = PrepareAsync(id, demand: false, CancellationToken.None);
        }
    }

    /// <summary>Fences all results immediately and wakes workers without polling.</summary>
    internal void Abort(SemanticPreparationOutcome? published = null)
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return;
            }

            if (published is not null && _entries.TryGetValue(published.ProjectId, out var completed))
            {
                completed.Completion.TrySetResult(published);
            }

            foreach (var entry in _entries.Values)
            {
                entry.Completion.TrySetResult(new(entry.Id, false, Obsolete: true));
            }

            while (_operations.TryDequeue(out var operation))
            {
                operation.Cancel(new CancellationToken(canceled: true));
            }
        }

        _lifetime.Cancel();
    }

    private async Task WorkAsync()
    {
        try
        {
            while (true)
            {
                await _available.WaitAsync(_lifetime.Token);
                Entry? entry;
                CompilerOperation? operation;
                lock (_gate)
                {
                    _operations.TryDequeue(out operation);
                    entry = operation is null ? Take(_demand, PreparationState.Demand) ?? Take(_warm, PreparationState.Warm) : null;
                    if (entry is not null)
                    {
                        entry.State = PreparationState.Running;
                        entry.Interruption = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                        _attempts++;
                    }

                    if (entry is not null || operation is not null)
                    {
                        _running++;
                        _maximumRunning = Math.Max(_maximumRunning, _running);
                    }
                }

                if (operation is not null)
                {
                    try
                    {
                        await operation.Execute(_lifetime.Token);
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            _running--;
                        }
                    }

                    continue;
                }

                if (entry is null)
                {
                    continue;
                }

                Task? deferredCompletion = null;
                var retry = false;
                var interruption = entry.Interruption ?? throw new InvalidOperationException("Running preparation has no lifetime.");
                try
                {
                    var outcome = await _prepare(entry.Id, interruption.Token);
                    if (_lifetime.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref _discarded);
                        SemanticLoadMetrics.PreparationDiscarded.Add(1);
                    }

                    _lifetime.Token.ThrowIfCancellationRequested();
                    await _publish(this, outcome, _lifetime.Token);
                    entry.Completion.TrySetResult(outcome);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    entry.Completion.TrySetResult(new(entry.Id, false, Obsolete: true));
                }
                catch (OperationCanceledException exception) when (interruption.IsCancellationRequested)
                {
                    retry = true;
                    deferredCompletion = (exception as SemanticOperationAbandonedException)?.Completion;
                }
                catch (Exception exception)
                {
                    // Publication/operation failure is terminal; no automatic retry in this generation.
                    entry.Completion.TrySetResult(new(entry.Id, false, Failure: exception.GetType().Name));
                }
                finally
                {
                    Task? callbacks;
                    lock (_gate)
                    {
                        callbacks = entry.InterruptionCompletion;
                        entry.InterruptionCompletion = null;
                        entry.Interruption = null;
                        entry.State = retry ? PreparationState.Deferred : PreparationState.Terminal;
                        _running--;
                    }

                    if (callbacks is null || callbacks.IsCompleted)
                    {
                        interruption.Dispose();
                    }
                    else
                    {
                        _ = callbacks.ContinueWith(
                            _ => interruption.Dispose(),
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                        deferredCompletion = deferredCompletion is null ? callbacks : Task.WhenAll(deferredCompletion, callbacks);
                    }
                }

                if (retry)
                {
                    if (deferredCompletion is null)
                    {
                        RequeueInterrupted(entry);
                    }
                    else
                    {
                        _ = deferredCompletion.ContinueWith(
                            _ => RequeueInterrupted(entry),
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void RequeueInterrupted(Entry entry)
    {
        lock (_gate)
        {
            if (_stopped != 0 || entry.State != PreparationState.Deferred)
            {
                return;
            }

            entry.State = entry.Demanded ? PreparationState.Demand : PreparationState.Warm;
            (entry.Demanded ? _demand : _warm).Enqueue(entry.Id);
            _available.Release();
        }
    }

    private Entry? Take(Queue<ProjectId> queue, PreparationState state)
    {
        while (queue.TryDequeue(out var id))
        {
            var entry = _entries[id];
            if (entry.State == state)
            {
                return entry;
            }
        }

        return null;
    }

    private enum PreparationState
    {
        Pending,
        Warm,
        Demand,
        Running,
        Deferred,
        Terminal,
    }

    private sealed record CompilerOperation(Func<CancellationToken, Task> Execute, Action<CancellationToken> Cancel);

    private sealed class Entry
    {
        internal Entry(ProjectId id, bool prepared)
        {
            Id = id;
            if (prepared)
            {
                State = PreparationState.Terminal;
                Completion.SetResult(new(id, true));
            }
        }

        internal ProjectId Id { get; }

        internal PreparationState State { get; set; }

        internal bool Demanded { get; set; }

        internal CancellationTokenSource? Interruption { get; set; }

        internal Task? InterruptionCompletion { get; set; }

        internal TaskCompletionSource<SemanticPreparationOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
