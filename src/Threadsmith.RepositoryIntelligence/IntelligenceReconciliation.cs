namespace Threadsmith.RepositoryIntelligence;

/// <summary>Host-selected reconciliation authority; model suggestions do not select durable targets.</summary>
internal enum IntelligenceRetentionAction
{
    Consolidate,
    Revise,
    Merge,
    Supersede,
    Conflict,
    Reject,
    Defer,
}

/// <summary>Explicit deterministic outcome for every selected candidate.</summary>
internal enum IntelligenceReconciliationOutcome
{
    Added,
    Corroborating,
    Revised,
    Merged,
    Superseding,
    Conflicting,
    Rejected,
    Deferred,
}

/// <summary>Selection of one validated invocation-local candidate with explicit optimistic target references.</summary>
internal sealed record IntelligenceRetentionSelection(
    string CandidateKey,
    IntelligenceRetentionAction Action = IntelligenceRetentionAction.Consolidate,
    IntelligenceRevisionReference? Target = null,
    IntelligenceRevisionReference? MergeFrom = null,
    bool PartialSupersession = false,
    string? Reason = null,
    IReadOnlyList<IntelligenceRevisionReference>? ConfirmDistinctFrom = null);

/// <summary>Host-defined bounded publication unit; it cannot acquire evidence or inference.</summary>
internal sealed record IntelligenceReconciliationRequest(
    string UnitId,
    IntelligenceProvenance Provenance,
    IReadOnlyList<IntelligenceRetentionSelection> Selections);

/// <summary>Outcome provenance preserves proposed conflicts and source attribution without rewriting old citations.</summary>
internal sealed record IntelligenceCandidateOutcome(
    string CandidateKey,
    IntelligenceReconciliationOutcome Outcome,
    string Reason,
    IntelligenceRevisionReference? Item,
    IntelligenceRevisionReference? RelatedItem,
    IReadOnlyList<string> EvidenceIds,
    RepositoryInterpretationCandidate? Proposal = null,
    IReadOnlyList<string>? SupportingEvidenceIds = null);

/// <summary>Atomically retained idempotency receipt, not a copy of the host event stream.</summary>
internal sealed record IntelligenceReconciliationReceipt(
    string Id,
    string Fingerprint,
    IntelligenceProvenance Provenance,
    IReadOnlyList<IntelligenceCandidateOutcome> Outcomes,
    IReadOnlyList<string> Diagnostics);

/// <summary>Additional claim keys preserve stable identity across explicit substantive revisions.</summary>
internal sealed record IntelligenceClaimIdentity(string Key, Guid ItemId);
