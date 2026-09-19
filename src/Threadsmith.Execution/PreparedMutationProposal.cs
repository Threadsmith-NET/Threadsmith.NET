namespace Threadsmith.Execution;

using Threadsmith.Core;

/// <summary>Holds a validated candidate and its approved plan steps before staging.</summary>
internal sealed record PreparedMutationProposal(
    MutationSet? MutationSet,
    IReadOnlyList<StepId> PlanStepIds,
    string Rationale,
    bool? StepComplete,
    BudgetDimensions BudgetUsed)
{
    /// <summary>Whether the model supplied a supported no-change completion candidate.</summary>
    public bool IsCompletionOnly => MutationSet is null && StepComplete == true;
}
