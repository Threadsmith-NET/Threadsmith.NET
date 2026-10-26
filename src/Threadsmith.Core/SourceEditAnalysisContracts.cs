namespace Threadsmith.Core;

/// <summary>One bounded compiler or analyzer finding with its known origin across successive edits.</summary>
public sealed record SourceEditDiagnostic(
    string Code,
    string Message,
    string? File,
    int? Line,
    string Project,
    string TargetFramework,
    string Origin)
{
    /// <summary>Effective diagnostic severity; older serialized findings remain errors.</summary>
    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Error;

    /// <summary>Evidence producer; older findings remain compiler evidence.</summary>
    public string Producer { get; init; } = "compiler";

    /// <summary>Assembly, version and implementation identity for analyzer evidence.</summary>
    public string? AnalyzerIdentity { get; init; }
}

/// <summary>Independent optional analyzer coverage; it never contributes to compiler totals or pending state.</summary>
public sealed record SourceEditAnalyzerAnalysis
{
    /// <summary>Disabled, pending, completed, partial, unavailable, skipped, cancelled, timed-out or failed.</summary>
    public string Status { get; init; } = "disabled";

    /// <summary>Owned changed document instances selected for scoped checks.</summary>
    public int DocumentsInScope { get; init; }

    /// <summary>Document instances for which the selected analyzer comparison completed.</summary>
    public int DocumentsAnalyzed { get; init; }

    /// <summary>Reportable, unsuppressed selected findings after shared exclusions.</summary>
    public int ReportableFindings { get; init; }

    /// <summary>Introduced findings, or null when matching complete comparison is unavailable.</summary>
    public int? NewFindings { get; init; }

    /// <summary>Resolved findings, or null when matching complete comparison is unavailable.</summary>
    public int? ResolvedFindings { get; init; }

    /// <summary>Whether shared diagnostic exclusions filtered output.</summary>
    public bool OutputFiltered { get; init; }

    /// <summary>Whether retention or projection omitted reportable analyzer findings.</summary>
    public bool Truncated { get; init; }

    /// <summary>Bounded scope, qualification, failure and cancellation limitations.</summary>
    public IReadOnlyList<string> Omissions { get; init; } = [];
}

/// <summary>Versioned advisory coverage, independent of write authorization and authoritative build/test results.</summary>
public sealed record SourceEditAnalysis
{
    /// <summary>Separate optional analyzer coverage with backward-compatible disabled defaults.</summary>
    public SourceEditAnalyzerAnalysis Analyzers { get; init; } = new();

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

    /// <summary>Reportable syntax errors, or null when syntax results are unavailable.</summary>
    public int? SyntaxErrors { get; init; }

    /// <summary>Whether every completed project has comparable, untruncated before/after evidence.</summary>
    public bool ErrorComparisonAvailable { get; init; }

    /// <summary>Whether exact committed inputs reused this candidate's analysis.</summary>
    public bool CandidateReused { get; init; }

    /// <summary>Current reportable errors within completed coverage; a bounded list does not imply a complete list.</summary>
    public int CurrentErrors { get; init; }

    /// <summary>Errors absent from the known preceding snapshot.</summary>
    public int NewErrors { get; init; }

    /// <summary>Previously observed errors absent from matching completed coverage.</summary>
    public int ResolvedErrors { get; init; }

    /// <summary>Bounded actionable findings distinguished by producer identity.</summary>
    public IReadOnlyList<SourceEditDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>Whether bounded retention or compact projection omits reportable findings.</summary>
    public bool DiagnosticsTruncated => FindingRetentionTruncated || FindingProjectionTruncated || Analyzers.Truncated
        || CurrentErrors + (CurrentWarnings ?? 0) + Analyzers.ReportableFindings > Diagnostics.Count;

    /// <summary>Selected warning total in completed coverage, or null when not measured.</summary>
    public int? CurrentWarnings { get; init; }

    /// <summary>Whether bounded internal retention omitted reportable findings.</summary>
    public bool FindingRetentionTruncated { get; init; }

    /// <summary>Whether compact output omitted retained findings, independent of unknown coverage totals.</summary>
    public bool FindingProjectionTruncated { get; init; }

    /// <summary>Whether exclusions removed diagnostics from this advisory evidence.</summary>
    public bool OutputFiltered { get; init; }

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
