namespace Threadsmith.Execution;

using Threadsmith.Core;

/// <summary>Explicit validated execution state machine.</summary>
public sealed class RunStateMachine : IStateMachine
{
    private static readonly IReadOnlySet<RunPhase> _terminalPhases = new HashSet<RunPhase>
    {
        RunPhase.Completion,
        RunPhase.Failed,
        RunPhase.Cancelled,
        RunPhase.RolledBack,
    };

    private static readonly IReadOnlyDictionary<RunPhase, IReadOnlySet<RunPhase>> _allowed =
        new Dictionary<RunPhase, IReadOnlySet<RunPhase>>
        {
            [RunPhase.Intake] = Set(RunPhase.RepositoryDiscovery, RunPhase.EvidenceCollection),
            [RunPhase.RepositoryDiscovery] = Set(RunPhase.EvidenceCollection),
            [RunPhase.EvidenceCollection] = Set(RunPhase.Completion),
        };

    private readonly IDomainEventStream _events;
    private readonly RunId _runId;
    private readonly SessionId _sessionId;

    /// <summary>Initializes a new instance of the <see cref="RunStateMachine"/> class.</summary>
    public RunStateMachine(
        SessionId sessionId,
        RunId runId,
        IDomainEventStream events,
        RunPhase initialPhase = RunPhase.Intake)
    {
        ArgumentNullException.ThrowIfNull(events);
        _sessionId = sessionId;
        _runId = runId;
        _events = events;
        Phase = initialPhase;
    }

    /// <inheritdoc />
    public RunPhase Phase { get; private set; } = RunPhase.Intake;

    /// <inheritdoc />
    public async Task TransitionAsync(
        RunPhase destination,
        string trigger,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);
        cancellationToken.ThrowIfCancellationRequested();
        var source = Phase;
        var generalTerminal = destination is RunPhase.Failed or RunPhase.Cancelled;
        var sourceIsTerminal = _terminalPhases.Contains(source);
        var specificallyAllowed = _allowed.TryGetValue(source, out var destinations)
            && destinations.Contains(destination);
        if (sourceIsTerminal || (!specificallyAllowed && !generalTerminal))
        {
            var reason = $"Transition {source} -> {destination} is not legal.";
            await _events.PublishAsync(
                new RunTransitionFailed(
                    _sessionId,
                    DateTimeOffset.UtcNow,
                    _runId,
                    source,
                    destination,
                    reason),
                cancellationToken);
            throw new InvalidOperationException(reason);
        }

        Phase = destination;
        await _events.PublishAsync(
            new RunTransitioned(
                _sessionId,
                DateTimeOffset.UtcNow,
                _runId,
                source,
                destination),
            cancellationToken);
    }

    private static IReadOnlySet<RunPhase> Set(params RunPhase[] phases)
    {
        return phases.ToHashSet();
    }
}
