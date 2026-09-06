namespace Threadsmith.Interaction.Coordination;

using System.Text;
using Threadsmith.Core;

/// <summary>Maintains a bounded, event-derived index of delegations observed by the interaction session.</summary>
internal sealed class DelegationActivityRegistry
{
    private const int MaximumDisplayedDelegations = 12;
    private const int MaximumTrackedDelegations = 64;
    private readonly Lock _gate = new();
    private readonly Dictionary<(SessionId SessionId, DelegationId DelegationId), DelegationState> _delegations = [];

    /// <summary>Observes one durable delegation event.</summary>
    /// <param name="domainEvent">Event to index.</param>
    internal void Observe(IDomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        lock (_gate)
        {
            switch (domainEvent)
            {
                case DelegationCheckpointWritten checkpoint:
                    ObserveCheckpoint(checkpoint);
                    break;
                case AgentRunLifecycleObserved lifecycle:
                    ObserveLifecycle(lifecycle);
                    break;
            }
        }
    }

    /// <summary>Formats the bounded active/recent delegation index for one session.</summary>
    /// <param name="sessionId">Session whose observed delegations are listed.</param>
    /// <returns>User-facing delegation index.</returns>
    internal string FormatSummary(SessionId sessionId)
    {
        DelegationSnapshot[] delegations;
        var total = 0;
        lock (_gate)
        {
            var matching = _delegations.Values
                .Where(item => item.SessionId == sessionId)
                .OrderByDescending(item => IsActive(item.Phase))
                .ThenByDescending(item => item.UpdatedAt)
                .ToArray();
            total = matching.Length;
            delegations = [.. matching
                .Take(MaximumDisplayedDelegations)
                .Select(CreateSnapshot)];
        }

        if (delegations.Length == 0)
        {
            return "No delegations have been observed for this session.\n"
                + "Usage: /agents <delegation-id> [cancel|cancel-child <assignment-id>]\n";
        }

        var output = new StringBuilder("Delegations observed this session:\n");
        foreach (var delegation in delegations)
        {
            output.Append("  ")
                .Append(delegation.DelegationId.Value.ToString("D"))
                .Append(' ')
                .Append(delegation.Phase?.ToString() ?? "Observed")
                .Append("; generation ")
                .Append(delegation.Generation)
                .Append("; next: ")
                .Append(string.IsNullOrWhiteSpace(delegation.NextAction) ? "unknown" : delegation.NextAction)
                .Append('\n');

            foreach (var child in delegation.Children)
            {
                output.Append("    ")
                    .Append(child.AssignmentId.Value.ToString("D"))
                    .Append(' ')
                    .Append(child.Role)
                    .Append(' ')
                    .Append(child.Status)
                    .Append('\n');
            }
        }

        if (total > delegations.Length)
        {
            output.Append("  ... ")
                .Append(total - delegations.Length)
                .Append(" older delegation(s) omitted\n");
        }

        output.Append("Usage: /agents <delegation-id> [cancel|cancel-child <assignment-id>]\n");
        return output.ToString();
    }

    /// <summary>Formats the stable identity announced at the accepted checkpoint.</summary>
    /// <param name="checkpoint">Accepted delegation checkpoint.</param>
    /// <returns>User-facing stable delegation identity.</returns>
    internal static string FormatAccepted(DelegationCheckpointWritten checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return $"Delegation started: {checkpoint.DelegationId.Value:D}\n"
            + $"  Inspect or cancel: /agents {checkpoint.DelegationId.Value:D}\n";
    }

    private static bool IsActive(DelegationCheckpointPhase? phase)
    {
        return phase is not DelegationCheckpointPhase.ResearchJoined
            and not DelegationCheckpointPhase.WorkersFrozen
            and not DelegationCheckpointPhase.ReviewsJoined
            and not DelegationCheckpointPhase.Completed
            and not DelegationCheckpointPhase.Failed
            and not DelegationCheckpointPhase.Cancelled;
    }

    private static DelegationSnapshot CreateSnapshot(DelegationState state)
    {
        var children = state.ChildOrder
            .Select(assignmentId => state.Children[assignmentId])
            .Select(child => new ChildSnapshot(child.AssignmentId, child.Role, child.Status))
            .ToArray();
        return new DelegationSnapshot(
            state.DelegationId,
            state.Phase,
            state.Generation,
            state.NextAction,
            children);
    }

    private DelegationState GetOrCreate(SessionId sessionId, DelegationId delegationId, DateTimeOffset occurredAt)
    {
        var key = (sessionId, delegationId);
        if (_delegations.TryGetValue(key, out var state))
        {
            return state;
        }

        state = new DelegationState(sessionId, delegationId, occurredAt);
        _delegations.Add(key, state);
        TrimToBound();
        return state;
    }

    private void ObserveCheckpoint(DelegationCheckpointWritten checkpoint)
    {
        var state = GetOrCreate(checkpoint.SessionId, checkpoint.DelegationId, checkpoint.OccurredAt);
        if (checkpoint.Revision <= state.CheckpointRevision)
        {
            return;
        }

        state.CheckpointRevision = checkpoint.Revision;
        state.Phase = checkpoint.Phase;
        state.Generation = checkpoint.Generation;
        state.NextAction = checkpoint.NextAction;
        state.UpdatedAt = checkpoint.OccurredAt;
    }

    private void ObserveLifecycle(AgentRunLifecycleObserved lifecycle)
    {
        var state = GetOrCreate(lifecycle.SessionId, lifecycle.DelegationId, lifecycle.OccurredAt);
        if (state.Children.TryGetValue(lifecycle.AssignmentId, out var existing)
            && lifecycle.Revision <= existing.Revision)
        {
            return;
        }

        if (existing is null)
        {
            state.ChildOrder.Add(lifecycle.AssignmentId);
        }

        state.Children[lifecycle.AssignmentId] = new ChildState(
            lifecycle.AssignmentId,
            lifecycle.Role,
            lifecycle.Status,
            lifecycle.Revision);
        state.Generation = Math.Max(state.Generation, lifecycle.Generation);
        state.UpdatedAt = lifecycle.OccurredAt;
    }

    private void TrimToBound()
    {
        while (_delegations.Count > MaximumTrackedDelegations)
        {
            var oldest = _delegations.Values
                .Where(item => !IsActive(item.Phase))
                .MinBy(item => item.UpdatedAt)
                ?? _delegations.Values.MinBy(item => item.UpdatedAt);
            if (oldest is null)
            {
                return;
            }

            _delegations.Remove((oldest.SessionId, oldest.DelegationId));
        }
    }

    private sealed class DelegationState(SessionId sessionId, DelegationId delegationId, DateTimeOffset occurredAt)
    {
        internal SessionId SessionId { get; } = sessionId;

        internal DelegationId DelegationId { get; } = delegationId;

        internal DelegationCheckpointPhase? Phase { get; set; }

        internal int Generation { get; set; }

        internal string NextAction { get; set; } = string.Empty;

        internal DateTimeOffset UpdatedAt { get; set; } = occurredAt;

        internal long CheckpointRevision { get; set; }

        internal List<AgentAssignmentId> ChildOrder { get; } = [];

        internal Dictionary<AgentAssignmentId, ChildState> Children { get; } = [];
    }

    private sealed record ChildState(
        AgentAssignmentId AssignmentId,
        AgentRole Role,
        AgentRunStatus Status,
        long Revision);

    private sealed record DelegationSnapshot(
        DelegationId DelegationId,
        DelegationCheckpointPhase? Phase,
        int Generation,
        string NextAction,
        IReadOnlyList<ChildSnapshot> Children);

    private sealed record ChildSnapshot(
        AgentAssignmentId AssignmentId,
        AgentRole Role,
        AgentRunStatus Status);
}
