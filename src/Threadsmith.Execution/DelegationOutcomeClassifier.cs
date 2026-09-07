namespace Threadsmith.Execution;

using Threadsmith.Core;

/// <summary>Classifies terminal child outcomes consistently at checkpoint and projection boundaries.</summary>
internal static class DelegationOutcomeClassifier
{
    /// <summary>Returns a failed projection when a completed child has neither a usable response nor a compatible legacy result.</summary>
    public static AgentRunOutcome Normalize(DelegationPlan plan, AgentRunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Status is AgentRunStatus.Failed or AgentRunStatus.Cancelled or AgentRunStatus.Discarded)
        {
            return outcome.Response is null ? outcome : outcome with { Response = null };
        }

        return outcome.Status == AgentRunStatus.Completed && !HasUsableResult(plan, outcome)
            ? outcome with
            {
                Status = AgentRunStatus.Failed,
                Reason = "Child completed without a usable response or compatible legacy result.",
                Response = null,
            }
            : outcome;
    }

    /// <summary>Recognizes an ordinary completed read-only response without requiring role-specific structured payloads.</summary>
    public static bool HasNaturalResponse(AgentAssignment assignment, AgentRunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Status == AgentRunStatus.Completed
            && outcome.AssignmentId == assignment.AssignmentId
            && outcome.ChildRunId == assignment.ChildRunId
            && outcome.Role == assignment.Role
            && Enum.IsDefined(assignment.Role)
            && assignment.Mode is AgentRunMode.ReadOnlyBaseline or AgentRunMode.ReadOnlyReview
            && string.Equals(assignment.OutputSchema, AgentAssignment.ResponseSchema, StringComparison.Ordinal)
            && outcome.Response is not null
            && outcome.ChangeSet is null;
    }

    /// <summary>Determines whether an ordinary response or legacy terminal payload is usable for its frozen assignment.</summary>
    public static bool HasUsableResult(DelegationPlan plan, AgentRunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(outcome);
        var assignment = plan.Assignments.SingleOrDefault(item => item.AssignmentId == outcome.AssignmentId);
        if (outcome.Status != AgentRunStatus.Completed
            || outcome.Generation != plan.Provenance.Generation
            || assignment is null
            || outcome.ChildRunId != assignment.ChildRunId
            || outcome.Role != assignment.Role)
        {
            return false;
        }

        if (assignment.OutputSchema == AgentAssignment.ResponseSchema
            && assignment.Mode is AgentRunMode.ReadOnlyBaseline or AgentRunMode.ReadOnlyReview)
        {
            return HasNaturalResponse(assignment, outcome);
        }

        return outcome.Role switch
        {
            AgentRole.Explorer => outcome.Findings is not null
                && (outcome.Findings.Findings.Count > 0
                    || (plan.Provenance.ApprovedPlanIdentity is not null
                        && plan.Provenance.ApprovedPlanRevision is > 0)),
            AgentRole.Implementer => assignment.Mode == AgentRunMode.ReadOnlyBaseline
                ? outcome.Implementation is not null && outcome.Findings is not null
                : outcome.ChangeSet is { IsComplete: true },
            AgentRole.SecurityReviewer or AgentRole.TestReviewer
                or AgentRole.PerformanceReviewer or AgentRole.ArchitectureReviewer => outcome.Review is not null,
            _ => false,
        };
    }

    /// <summary>Classifies a joined model-facing delegation from normalized outcomes.</summary>
    public static DelegateAgentsStatus ResolveStatus(
        DelegationPlan plan,
        IReadOnlyList<AgentRunOutcome> outcomes,
        DelegationCheckpointPhase phase)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(outcomes);
        if (phase == DelegationCheckpointPhase.Cancelled)
        {
            return DelegateAgentsStatus.Cancelled;
        }

        if (phase == DelegationCheckpointPhase.Failed)
        {
            return DelegateAgentsStatus.Failed;
        }

        var usable = outcomes.Count(outcome => HasUsableResult(plan, outcome));
        if (usable == outcomes.Count && outcomes.Count > 0)
        {
            return DelegateAgentsStatus.Completed;
        }

        if (usable > 0)
        {
            return DelegateAgentsStatus.Partial;
        }

        return outcomes.Count > 0
            && outcomes.All(outcome => outcome.Status == AgentRunStatus.Cancelled)
            ? DelegateAgentsStatus.Cancelled
            : DelegateAgentsStatus.Failed;
    }
}
