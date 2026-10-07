namespace Threadsmith.Core;

/// <summary>One bounded compiler finding with its known origin across successive edits.</summary>
public sealed record SourceEditDiagnostic(
    string Code,
    string Message,
    string? File,
    int? Line,
    string Project,
    string TargetFramework,
    string Origin);

/// <summary>Versioned advisory coverage, independent of write authorization and authoritative build/test results.</summary>
public sealed record SourceEditAnalysis
{
    /// <summary>Exact edit whose bytes were analyzed.</summary>
    public required Guid EffectId { get; init; }

    /// <summary>Source generation observed for the candidate.</summary>
    public long SourceGeneration { get; init; }

    /// <summary>Generation that verified and published the candidate, or null before promotion.</summary>
    public long? CommittedGeneration { get; init; }

    /// <summary>Monotonic result revision for deduplicated delivery.</summary>
    public long Revision { get; init; }

    /// <summary>Whether broader compiler work is still pending.</summary>
    public bool Pending { get; init; }

    /// <summary>Whether all claimed compiler inputs still match the current semantic snapshot.</summary>
    public bool Obsolete { get; init; }

    /// <summary>Completed owning/dependent project instances, including distinct target frameworks.</summary>
    public int ProjectsAnalyzed { get; init; }

    /// <summary>Project instances in the conservative affected scope.</summary>
    public int ProjectsInScope { get; init; }

    /// <summary>Document instances checked for syntax, or null when that phase did not complete.</summary>
    public int? SyntaxDocumentsAnalyzed { get; init; }

    /// <summary>Retained syntax errors, or null when syntax results are unavailable.</summary>
    public int? SyntaxErrors { get; init; }

    /// <summary>Whether every completed project has comparable, untruncated before/after evidence.</summary>
    public bool ErrorComparisonAvailable { get; init; }

    /// <summary>Whether exact committed inputs reused this candidate's analysis.</summary>
    public bool CandidateReused { get; init; }

    /// <summary>Current errors within completed coverage; a bounded list does not imply a complete list.</summary>
    public int CurrentErrors { get; init; }

    /// <summary>Errors absent from the known preceding snapshot.</summary>
    public int NewErrors { get; init; }

    /// <summary>Previously observed errors absent from matching completed coverage.</summary>
    public int ResolvedErrors { get; init; }

    /// <summary>Bounded actionable compiler findings.</summary>
    public IReadOnlyList<SourceEditDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Whether the compact finding list omits errors counted in completed coverage.</summary>
    public bool DiagnosticsTruncated => CurrentErrors > Diagnostics.Count;

    /// <summary>Explicit limits, omitted checks and unavailable coverage.</summary>
    public IReadOnlyList<string> Omissions { get; init; } = [];
}

/// <summary>Existing semantic owner supplies advisory analysis of exact authorized writer snapshots.</summary>
public interface ISourceEditAnalyzer
{
    /// <summary>Returns whether any endpoint affects known compiler inputs.</summary>
    bool HasSemanticInputs(WorkspaceId workspaceId, string repositoryPath, MutationEffectSnapshot snapshot);

    /// <summary>Admits one candidate and waits only the supplied immediate allowance; returns null for unrelated inputs.</summary>
    Task<SourceEditAnalysis?> AnalyzeCandidateAsync(
        ApplySourceEditCommand command,
        string repositoryPath,
        MutationEffectSnapshot snapshot,
        TimeSpan immediateAllowance,
        CancellationToken cancellationToken = default);

    /// <summary>Gets the latest bounded result only for this edit and owner.</summary>
    SourceEditAnalysis? GetLatestAnalysis(SessionId sessionId, RunId runId, WorkspaceId workspaceId, Guid effectId);

    /// <summary>Records the authoritative applied outcome; candidate results alone never prove a write.</summary>
    void ConfirmApplied(WorkspaceId workspaceId, Guid effectId);

    /// <summary>Discards a denied, conflicted or superseded candidate without changing source state.</summary>
    void DiscardCandidate(WorkspaceId workspaceId, Guid effectId);
}
