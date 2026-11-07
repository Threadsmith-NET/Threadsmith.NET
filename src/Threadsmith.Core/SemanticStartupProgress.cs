namespace Threadsmith.Core;

/// <summary>Bounded semantic startup operations, independent of a frontend or compiler implementation.</summary>
public enum SemanticStartupPhase
{
    /// <summary>Captures input identities before loading.</summary>
    CaptureSnapshots,

    /// <summary>Opens the evaluated project graph.</summary>
    OpenWorkspace,

    /// <summary>Applies repository input confinement.</summary>
    ConfineInputs,

    /// <summary>Prepares the initial usable compilation.</summary>
    PrepareCompilation,

    /// <summary>Installs file monitoring.</summary>
    StartMonitoring,

    /// <summary>Reads loaded document identities.</summary>
    ReadDocuments,

    /// <summary>Verifies loaded inputs against current files.</summary>
    ReconcileSnapshots,
}

/// <summary>A startup operation's observed outcome.</summary>
public enum SemanticStartupPhaseState
{
    /// <summary>The operation is still running.</summary>
    Running,

    /// <summary>The operation finished successfully.</summary>
    Completed,

    /// <summary>The operation failed.</summary>
    Failed,

    /// <summary>The operation was cancelled.</summary>
    Cancelled,

    /// <summary>The operation was not needed.</summary>
    Skipped,
}

/// <summary>An immutable, transient projection of one actual startup operation.</summary>
public sealed record SemanticStartupPhaseSnapshot(SemanticStartupPhase Phase, SemanticStartupPhaseState State, TimeSpan Elapsed)
{
    /// <summary>Gets the stable operation identity.</summary>
    public string Id { get; init; } = Phase.ToString();

    /// <summary>Gets the optional owning operation identity.</summary>
    public string? ParentId { get; init; }

    /// <summary>Gets the stable display position.</summary>
    public int Order { get; init; } = (int)Phase;

    /// <summary>Gets the bounded process-operation label, or null for semantic labels.</summary>
    public string? Label { get; init; }

    /// <summary>Gets a bounded, sanitized explanation supplied by the host.</summary>
    public string? Detail { get; init; }

    /// <summary>Gets the monotonic timestamp immediately before work began.</summary>
    public long Started { get; init; }
}

/// <summary>Records bounded startup timings only while a frontend observes that session.</summary>
/// <remarks>This contains no execution state and does not publish or persist domain events.</remarks>
public sealed class SemanticStartupProgress
{
    private readonly Lock _gate = new();
    private readonly Dictionary<SessionId, Observation> _observations = [];
    private readonly TimeProvider _timeProvider;
    private Action<SemanticStartupPhaseSnapshot>? _report;

    /// <summary>Gets the process-entry timestamp on the monotonic clock.</summary>
    public long ProcessStartedTimestamp { get; }

    /// <summary>Gets the first emitted startup-frame timestamp.</summary>
    public long? FirstFrameTimestamp { get; private set; }

    /// <summary>Gets the first interactive-readiness timestamp.</summary>
    public long? ReadyTimestamp { get; private set; }

    /// <summary>Records the emitted-frame boundary.</summary>
    public void MarkFirstFrame() => FirstFrameTimestamp ??= _timeProvider.GetTimestamp();

    /// <summary>Records readiness once and reports total startup duration.</summary>
    public void MarkReady()
    {
        if (ReadyTimestamp is not null)
        {
            return;
        }

        ReadyTimestamp = _timeProvider.GetTimestamp();
        _report?.Invoke(new(default, SemanticStartupPhaseState.Completed, _timeProvider.GetElapsedTime(ProcessStartedTimestamp, ReadyTimestamp.Value))
        {
            Id = "startup.total",
            Label = "Total startup",
            Started = ProcessStartedTimestamp,
        });
    }

    /// <summary>Attaches existing host logging and replays bounded early completions.</summary>
    public void AttachReporter(Action<SemanticStartupPhaseSnapshot> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        SemanticStartupPhaseSnapshot[] completed;
        lock (_gate)
        {
            _report = report;
            completed = [.. _observations.Values.SelectMany(observation => observation.Operations.Values)
                .Select(operation => operation.Snapshot).Where(snapshot => snapshot.State != SemanticStartupPhaseState.Running)];
        }

        foreach (var snapshot in completed)
        {
            report(snapshot);
        }
    }

    /// <summary>Observes process startup before any session or database exists.</summary>
    public Observation ObserveProcess() => Observe(default);

    /// <summary>Times an outer presentation operation without creating another observation store.</summary>
    public Operation Measure(string id, string label, CancellationToken cancellationToken)
    {
        return new Operation(this, default, _timeProvider.GetTimestamp(), cancellationToken) { Id = Bound(id), Label = Bound(label) };
    }

    /// <summary>Starts a bounded process operation in its declared display position.</summary>
    public Operation? Begin(string id, string label, int order = 0, string? parentId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            if (!_observations.TryGetValue(default, out var observation))
            {
                return null;
            }

            if (observation.Operations.Count >= 256 && !observation.Operations.ContainsKey(id))
            {
                var oldest = observation.Operations
                    .Where(pair => pair.Value.Snapshot.State != SemanticStartupPhaseState.Running)
                    .OrderBy(pair => pair.Value.Snapshot.Order)
                    .FirstOrDefault();
                if (oldest.Key is null)
                {
                    return null;
                }

                observation.Operations.Remove(oldest.Key);
            }

            var operation = new Operation(this, default, _timeProvider.GetTimestamp(), cancellationToken)
            {
                Id = Bound(id),
                Label = Bound(label),
                ParentId = parentId is null ? null : Bound(parentId),
                Order = order == 0 ? observation.Operations.GetValueOrDefault(id)?.Snapshot.Order ?? observation.NextOrder++ : order,
            };
            observation.Operations[id] = operation;
            observation.Invalidate();
            return operation;
        }
    }

    /// <summary>Runs and observes one actual asynchronous startup boundary.</summary>
    public async Task<T> RunAsync<T>(string id, string label, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        using var operation = Begin(id, label, cancellationToken: cancellationToken);
        var result = await work(cancellationToken);
        operation?.Complete();
        return result;
    }

    /// <summary>Runs and observes one actual asynchronous startup boundary.</summary>
    public async Task RunAsync(string id, string label, Func<CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        using var operation = Begin(id, label, cancellationToken: cancellationToken);
        await work(cancellationToken);
        operation?.Complete();
    }

    /// <summary>Runs and observes a synchronous startup boundary.</summary>
    public T Run<T>(string id, string label, Func<T> work, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = Begin(id, label, cancellationToken: cancellationToken);
        var result = work();
        operation?.Complete();
        return result;
    }

    /// <summary>Initializes a new instance of the <see cref="SemanticStartupProgress"/> class.</summary>
    public SemanticStartupProgress(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        ProcessStartedTimestamp = _timeProvider.GetTimestamp();
    }

    /// <summary>Observes one startup until the returned lease is disposed.</summary>
    public Observation Observe(SessionId sessionId)
    {
        lock (_gate)
        {
            var observation = new Observation(this, sessionId);
            _observations[sessionId] = observation;
            return observation;
        }
    }

    /// <summary>Starts an actual operation if this session has an active startup observer.</summary>
    public Operation? Begin(SessionId sessionId, SemanticStartupPhase phase, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_observations.TryGetValue(sessionId, out var observation))
            {
                return null;
            }

            var operation = new Operation(this, phase, _timeProvider.GetTimestamp(), cancellationToken);
            observation.Operations[phase.ToString()] = operation;
            observation.Invalidate();
            return operation;
        }
    }

    private static string Bound(string value) => new([.. value.Take(240).Select(character => char.IsControl(character) ? ' ' : character)]);

    /// <summary>Owns a session's transient observation and its bounded phase snapshots.</summary>
    public sealed class Observation : IDisposable
    {
        private readonly SemanticStartupProgress _owner;
        private readonly SessionId _sessionId;
        private Operation[]? _ordered;

        /// <summary>Gets current monotonic durations; completed durations remain frozen.</summary>
        public IReadOnlyList<SemanticStartupPhaseSnapshot> Snapshot
        {
            get
            {
                lock (_owner._gate)
                {
                    if (!_owner._observations.TryGetValue(_sessionId, out var current) || !ReferenceEquals(current, this))
                    {
                        return [];
                    }

                    if (_ordered is null)
                    {
                        field = null;
                        _ordered = [.. Operations.Values.OrderBy(operation => operation.Snapshot.Order)];
                    }

                    if (field is not null)
                    {
                        return field;
                    }

                    var snapshots = new SemanticStartupPhaseSnapshot[_ordered.Length];
                    var running = false;
                    for (var index = 0; index < _ordered.Length; index++)
                    {
                        var snapshot = _ordered[index].Snapshot;
                        snapshots[index] = snapshot;
                        running |= snapshot.State == SemanticStartupPhaseState.Running;
                    }

                    if (!running)
                    {
                        field = snapshots;
                    }

                    return snapshots;
                }
            }
        }

        /// <summary>Stops collecting startup data without affecting semantic work.</summary>
        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_owner._observations.TryGetValue(_sessionId, out var current) && ReferenceEquals(current, this))
                {
                    _owner._observations.Remove(_sessionId);
                }

                Operations.Clear();
                Invalidate();
            }
        }

        /// <summary>Initializes a new instance of the <see cref="Observation"/> class.</summary>
        internal Observation(SemanticStartupProgress owner, SessionId sessionId)
        {
            _owner = owner;
            _sessionId = sessionId;
        }

        /// <summary>Gets the bounded operation slots, accessed under the owner's lock.</summary>
        internal Dictionary<string, Operation> Operations { get; } = [];

        /// <summary>Gets or sets the monotonic insertion order even when completed history is evicted.</summary>
        internal int NextOrder { get; set; }

        /// <summary>Invalidates the retained projection when operation slots change under the owner's lock.</summary>
        internal void Invalidate()
        {
            _ordered = null;
        }
    }

    /// <summary>Times one operation; only an explicit successful completion receives a success outcome.</summary>
    public sealed class Operation : IDisposable
    {
        private readonly SemanticStartupProgress _owner;
        private readonly SemanticStartupPhase _phase;
        private readonly CancellationToken _cancellationToken;
        private readonly long _started;
        private SemanticStartupPhaseState _state;
        private TimeSpan? _elapsed;
        private string? _detail;

        /// <summary>Gets the stable operation identity.</summary>
        internal string? Id { get; init; }

        /// <summary>Gets the parent identity.</summary>
        internal string? ParentId { get; init; }

        /// <summary>Gets the display label.</summary>
        internal string? Label { get; init; }

        /// <summary>Gets the display order.</summary>
        internal int? Order { get; init; }

        /// <summary>Records a recoverable failure with a host-sanitized fallback explanation.</summary>
        public void Fail(string detail)
        {
            lock (_owner._gate)
            {
                _detail = Bound(detail);
                Finish(SemanticStartupPhaseState.Failed);
            }
        }

        /// <summary>Records work that was not required without starting its timer.</summary>
        public void Skip()
        {
            lock (_owner._gate)
            {
                Finish(SemanticStartupPhaseState.Skipped);
            }
        }

        /// <summary>Records the actual successful boundary and freezes its timer.</summary>
        public void Complete()
        {
            Finish(SemanticStartupPhaseState.Completed);
        }

        /// <summary>Records failure or cancellation if the operation did not complete.</summary>
        public void Dispose()
        {
            Finish(_cancellationToken.IsCancellationRequested ? SemanticStartupPhaseState.Cancelled : SemanticStartupPhaseState.Failed);
        }

        /// <summary>Initializes a new instance of the <see cref="Operation"/> class.</summary>
        internal Operation(SemanticStartupProgress owner, SemanticStartupPhase phase, long started, CancellationToken cancellationToken)
        {
            _owner = owner;
            _phase = phase;
            _cancellationToken = cancellationToken;
            _started = started;
        }

        /// <summary>Gets a phase snapshot under the owner's lock.</summary>
        public SemanticStartupPhaseSnapshot Snapshot
        {
            get
            {
                lock (_owner._gate)
                {
                    if (field is not null)
                    {
                        return field;
                    }

                    var snapshot = new SemanticStartupPhaseSnapshot(_phase, _state, _elapsed ?? _owner._timeProvider.GetElapsedTime(_started))
                    {
                        Id = Id ?? _phase.ToString(),
                        ParentId = ParentId,
                        Label = Label,
                        Order = Order ?? (int)_phase,
                        Started = _started,
                        Detail = _detail,
                    };
                    if (_elapsed is not null)
                    {
                        field = snapshot;
                    }

                    return snapshot;
                }
            }
        }

        private void Finish(SemanticStartupPhaseState state)
        {
            SemanticStartupPhaseSnapshot? completed = null;
            lock (_owner._gate)
            {
                if (_elapsed is null)
                {
                    _elapsed = state == SemanticStartupPhaseState.Skipped ? TimeSpan.Zero : _owner._timeProvider.GetElapsedTime(_started);
                    _state = state;
                    completed = Snapshot;
                }
            }

            if (completed is not null)
            {
                _owner._report?.Invoke(completed);
            }
        }
    }
}
