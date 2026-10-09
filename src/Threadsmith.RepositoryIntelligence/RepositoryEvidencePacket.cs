namespace Threadsmith.RepositoryIntelligence;

using Threadsmith.Core;

/// <summary>Explicit collection authority; snapshot expansion cannot turn on history.</summary>
internal enum RepositoryEvidenceMode
{
    CurrentSnapshot,
    History,
}

/// <summary>Bounded question and discovery filters for one live operation.</summary>
internal sealed record RepositoryEvidenceSelection
{
    /// <summary>Question to be interpreted, never executable instructions.</summary>
    public required string Question { get; init; }

    /// <summary>Literal paths within the captured profile scope.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>Shared memory/tool vocabulary hints.</summary>
    public IReadOnlyList<string> Concepts { get; init; } = [];

    /// <summary>Requested symbol anchors; unsupported immutable resolution is reported.</summary>
    public IReadOnlyList<string> Symbols { get; init; } = [];

    /// <summary>History must be explicitly requested.</summary>
    public RepositoryEvidenceMode Mode { get; init; }

    /// <summary>Optional exclusive immutable history lower bound.</summary>
    public string? ExcludeCommit { get; init; }

    /// <summary>Cumulative content acquisitions, including the prerequisite profile.</summary>
    public required int MaximumFiles { get; init; }

    /// <summary>Cumulative nominated commit frontier, independent of repository history size.</summary>
    public required int MaximumCommits { get; init; }

    /// <summary>Cumulative source content bytes, including the prerequisite profile.</summary>
    public required int MaximumInputBytes { get; init; }

    /// <summary>Serialized bytes per packet, including escaped text and provenance.</summary>
    public required int MaximumPacketBytes { get; init; }

    /// <summary>Cumulative serialized packet bytes for initial collection and expansion.</summary>
    public required int MaximumOutputBytes { get; init; }
}

/// <summary>Canonical source identity is independent of the selected excerpt.</summary>
internal sealed record RepositoryEvidenceSource(
    string Id,
    string Kind,
    string RepositoryIdentity,
    string CheckoutIdentity,
    string? Revision,
    string Path,
    string SourceIdentity,
    string? PreviousRevision = null,
    string? PreviousPath = null);

/// <summary>Sanitized source evidence; tests are source assertions, never execution results.</summary>
internal sealed record RepositoryEvidenceExcerpt(
    string Id,
    RepositoryEvidenceSource Source,
    int? StartLine,
    int? EndLine,
    string State,
    string? Text);

/// <summary>Deterministic nomination, with no inferred intent or causal claim.</summary>
internal sealed record RepositoryEpisodeCandidate(
    string Id,
    string Scope,
    IReadOnlyList<string> Commits,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> Signals);

/// <summary>Machine-readable discovery limits; semantic coverage is always unassessed in T05.</summary>
internal sealed record RepositoryEvidenceOmission(string Reason, string? Locator = null, int Count = 1);

/// <summary>Cumulative work consumed by this live operation.</summary>
internal sealed record RepositoryEvidenceConsumption(int Files, int Commits, long InputBytes, long OutputBytes, int ReadCalls);

/// <summary>One bounded inference input, not a canonical intelligence record.</summary>
internal sealed record RepositoryEvidencePacket(
    RepositoryEvidenceSelection Selection,
    GitSnapshotMetadata Target,
    bool PendingChanges,
    string HistoryCoverage,
    IReadOnlyList<RepositoryEvidenceExcerpt> Evidence,
    IReadOnlyList<RepositoryEpisodeCandidate> Episodes,
    IReadOnlyList<RepositoryEvidenceOmission> Omissions,
    RepositoryEvidenceConsumption Consumption,
    string? Continuation);
