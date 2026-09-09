namespace Threadsmith.Execution;

using Threadsmith.Core;

/// <summary>Holds a validated candidate and its approved plan steps before staging.</summary>
internal sealed record PreparedMutationProposal(
    MutationSet MutationSet,
    IReadOnlyList<StepId> PlanStepIds);
