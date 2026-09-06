namespace Threadsmith.Execution;

using Threadsmith.Core;

/// <summary>Classifies terminal child outcomes consistently at checkpoint and projection boundaries.</summary>
internal static class DelegationOutcomeClassifier
{
    /// <summary>Returns a failed projection when a completed child has no role-valid payload.</summary>
    public static AgentRunOutcome Normalize(DelegationPlan plan, AgentRunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Status == AgentRunStatus.Completed && !HasUsableResult(plan, outcome)
            ? outcome with
            {
                Status = AgentRunStatus.Failed,
                Reason = "Child completed without usable structured output.",
            }
            : outcome;
    }

    /// <summary>Determines whether a terminal payload is usable for its frozen role.</summary>
    public static bool HasUsableResult(DelegationPlan plan, AgentRunOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Status != AgentRunStatus.Completed)
        {
            return false;
        }

        return outcome.Role switch
        {
            AgentRole.Explorer => outcome.Findings is not null
                && (outcome.Findings.Findings.Count > 0
                    || (plan.Provenance.ApprovedPlanIdentity is not null
                        && plan.Provenance.ApprovedPlanRevision is > 0)),
            AgentRole.Implementer => outcome.ChangeSet is { IsComplete: true },
            _ => outcome.Review is not null,
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
