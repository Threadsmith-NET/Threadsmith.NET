namespace Threadsmith.Execution;

using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Holds a validated candidate and its approved plan steps before parent staging.</summary>
internal sealed record PreparedMutationProposal(
    MutationSet MutationSet,
    IReadOnlyList<StepId> PlanStepIds);

/// <summary>Owns the frozen model identity and request-local telemetry of one preparation child.</summary>
internal sealed class ApprovedImplementerPreparationContext
{
    private readonly AgentAssignment _assignment;
    private readonly AgentModelSelector _selector;

    private readonly HashSet<EvidenceId> _deliveredEvidenceIds = [];

    /// <summary>Initializes a new instance of the <see cref="ApprovedImplementerPreparationContext"/> class.</summary>
    public ApprovedImplementerPreparationContext(AgentAssignment assignment, AgentModelSelector selection)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(selection);
        _assignment = assignment;
        _selector = selection;
        Selection = selection.Select(assignment, requireToolCalls: true);
    }

    /// <summary>Gets the frozen child identity used for model requests.</summary>
    public RunId ChildRunId => _assignment.ChildRunId;

    /// <summary>Gets the compatible model route retained across request reassembly.</summary>
    public AgentModelSelection Selection { get; private set; }

    /// <summary>Gets or sets cumulative reported model token usage.</summary>
    public long ModelTokens { get; set; }

    /// <summary>Gets or sets the number of private proposal calls observed.</summary>
    public int ToolCalls { get; set; }

    /// <summary>Gets or sets the number of corrective turns consumed.</summary>
    public int Corrections { get; set; }

    /// <summary>Gets the host-known evidence identities delivered on attempted child model requests.</summary>
    public IReadOnlyList<EvidenceId> DeliveredEvidenceIds => _deliveredEvidenceIds.ToArray();

    /// <summary>Retains delivered evidence identity without manufacturing model citations.</summary>
    public void RecordDeliveredEvidence(IEnumerable<EvidenceId> evidenceIds)
    {
        _deliveredEvidenceIds.UnionWith(evidenceIds);
    }

    /// <summary>Validates the complete request and reports whether its model route remains unchanged.</summary>
    public bool ResolveRequest(ModelStreamRequest request)
    {
        var selected = _selector.SelectForRequest(
            _assignment with
            {
                Policy = _assignment.Policy with { ModelSelection = Selection.Provenance },
            },
            request,
            useProfileOutputReserve: true);
        var unchanged = selected.ProfileId == Selection.ProfileId
            && selected.ReasoningLevel == Selection.ReasoningLevel;
        Selection = selected;
        return unchanged;
    }

    /// <summary>Captures measured preparation usage for successful, failed, or cancelled child outcomes.</summary>
    public AgentResourceUsage CreateUsage(TimeSpan wallTime, int files = 0, int mutations = 0)
    {
        return new AgentResourceUsage
        {
            ModelTokens = ModelTokens,
            ToolCalls = ToolCalls,
            Corrections = Corrections,
            Files = files,
            EvidenceItems = _deliveredEvidenceIds.Count,
            Mutations = mutations,
            WallTime = wallTime,
        };
    }
}
