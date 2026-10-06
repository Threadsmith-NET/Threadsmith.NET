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
}

/// <summary>An immutable, transient projection of one actual startup operation.</summary>
public sealed record SemanticStartupPhaseSnapshot(SemanticStartupPhase Phase, SemanticStartupPhaseState State, TimeSpan Elapsed);

/// <summary>Records bounded startup timings only while a frontend observes that session.</summary>
/// <remarks>This contains no execution state and does not publish or persist domain events.</remarks>
public sealed class SemanticStartupProgress
{
    private readonly Lock _gate = new();
    private readonly Dictionary<SessionId, Observation> _observations = [];
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="SemanticStartupProgress"/> class.</summary>
    public SemanticStartupProgress(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
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

            var operation = new Operation(this, phase, cancellationToken, _timeProvider.GetTimestamp());
            observation.Operations[phase] = operation;
            return operation;
        }
    }

    /// <summary>Owns a session's transient observation and its bounded phase snapshots.</summary>
    public sealed class Observation : IDisposable
    {
        private readonly SemanticStartupProgress _owner;
        private readonly SessionId _sessionId;

        /// <summary>Gets current monotonic durations; completed durations remain frozen.</summary>
        public IReadOnlyList<SemanticStartupPhaseSnapshot> Snapshot
        {
            get
            {
                lock (_owner._gate)
                {
                    return _owner._observations.TryGetValue(_sessionId, out var current)
                        && ReferenceEquals(current, this)
                        ? Operations.OrderBy(pair => pair.Key).Select(pair => pair.Value.Snapshot).ToArray()
                        : [];
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
            }
        }

        /// <summary>Initializes a new instance of the <see cref="Observation"/> class.</summary>
        internal Observation(SemanticStartupProgress owner, SessionId sessionId)
        {
            _owner = owner;
            _sessionId = sessionId;
        }

        /// <summary>Gets the bounded operation slots, accessed under the owner's lock.</summary>
        internal Dictionary<SemanticStartupPhase, Operation> Operations { get; } = [];
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
        internal Operation(SemanticStartupProgress owner, SemanticStartupPhase phase, CancellationToken cancellationToken, long started)
        {
            _owner = owner;
            _phase = phase;
            _cancellationToken = cancellationToken;
            _started = started;
        }

        /// <summary>Gets a phase snapshot under the owner's lock.</summary>
        internal SemanticStartupPhaseSnapshot Snapshot => new(_phase, _state, _elapsed ?? _owner._timeProvider.GetElapsedTime(_started));

        private void Finish(SemanticStartupPhaseState state)
        {
            lock (_owner._gate)
            {
                if (_elapsed is null)
                {
                    _elapsed = _owner._timeProvider.GetElapsedTime(_started);
                    _state = state;
                }
            }
        }
    }
}
