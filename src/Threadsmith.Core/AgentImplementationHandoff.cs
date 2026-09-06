namespace Threadsmith.Core;

/// <summary>A read-only implementation proposal; it is never proof of writes or validation execution.</summary>
public sealed record AgentImplementationHandoff
{
    /// <summary>Version of the proposal contract.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Assignment that prepared the proposal.</summary>
    public required AgentAssignmentId AssignmentId { get; init; }

    /// <summary>Child that inspected the cited source.</summary>
    public required RunId ChildRunId { get; init; }

    /// <summary>Attempt generation supplied by the host.</summary>
    public required int Generation { get; init; }

    /// <summary>Bounded synthesis of the proposed implementation.</summary>
    public required string Summary { get; init; }

    /// <summary>Files inspected as evidence for the proposal.</summary>
    public required IReadOnlyList<AgentInspectedFile> InspectedFiles { get; init; }

    /// <summary>Candidate changes requiring the parent's normal planning, approval, and mutation workflow.</summary>
    public required IReadOnlyList<AgentProposedFileChange> ProposedChanges { get; init; }

    /// <summary>Suggested validation, never a claim that tests or processes ran.</summary>
    public required IReadOnlyList<string> ValidationPlan { get; init; }

    /// <summary>Unresolved implementation risks.</summary>
    public required IReadOnlyList<string> Risks { get; init; }

    /// <summary>Suggested next actions for the parent.</summary>
    public required string Handoff { get; init; }
}

/// <summary>One inspected repository file with exact delivered-evidence citations.</summary>
public sealed record AgentInspectedFile
{
    /// <summary>Repository-relative inspected path.</summary>
    public required string RelativePath { get; init; }

    /// <summary>What the inspection established.</summary>
    public required string Summary { get; init; }

    /// <summary>Citations checked by the host before admission.</summary>
    public required IReadOnlyList<EvidenceId> EvidenceIds { get; init; }
}

/// <summary>A candidate file change expressed as intent only, with no executable mutation or write authority.</summary>
public sealed record AgentProposedFileChange
{
    /// <summary>Repository-relative proposed target, which may be a new file.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Inspected evidence supporting this proposed change.</summary>
    public required IReadOnlyList<EvidenceId> EvidenceIds { get; init; }

    /// <summary>Intended behavior to implement through the parent's approved workflow.</summary>
    public required string IntendedChange { get; init; }

    /// <summary>Suggested checks for this file; no execution is implied.</summary>
    public required IReadOnlyList<string> ValidationPlan { get; init; }

    /// <summary>Known risks for the proposed change.</summary>
    public required IReadOnlyList<string> Risks { get; init; }

    /// <summary>Follow-up guidance for the parent that would implement this proposal.</summary>
    public required string Handoff { get; init; }
}
