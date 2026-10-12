namespace Threadsmith.Core;

/// <summary>Durable preparation at a completed onboarding history batch; never an eligible published memory collection.</summary>
public sealed record RepositoryIntelligenceCheckpoint
{
    /// <summary>Checkpoint format and history paging contract.</summary>
    public int Version { get; init; } = 2;

    /// <summary>Repository whose published intelligence must remain unchanged until completion.</summary>
    public required string RepositoryIdentity { get; init; }

    /// <summary>Immutable ancestry root used by every completed batch; null denotes an empty history scope.</summary>
    public required string? HistoryRevision { get; init; }

    /// <summary>Maximum newest commits selected before chronological traversal, or null for full ancestry.</summary>
    public int? HistoryDepth { get; init; }

    /// <summary>First history batch that has not completed investigation.</summary>
    public int NextHistoryPage { get; init; }

    /// <summary>History has finished; NextHistoryPage identifies the final page replayed only to validate the pinned scope.</summary>
    public bool HistoryComplete { get; init; }

    /// <summary>The mandatory final coverage review has begun.</summary>
    public bool CoverageReviewStarted { get; init; }

    /// <summary>Published intelligence revisions against which the prepared work was reconciled.</summary>
    public IReadOnlyList<RepositoryMemoryInclusion> BaselineIntelligence { get; init; } = [];

    /// <summary>Detached prepared entries, including their original evidence receipts.</summary>
    public IReadOnlyList<RepositoryMemoryEntry> Entries { get; init; } = [];

    /// <summary>Entries explicitly accepted for the replacement collection.</summary>
    public IReadOnlyList<RepositoryMemoryId> AcceptedIds { get; init; } = [];

    /// <summary>Repository-specific investigation state; absent in checkpoints created before checklist support.</summary>
    public IReadOnlyList<RepositoryIntelligenceCoverage> Coverage { get; init; } = [];

    /// <summary>Bounded model-authored working context, treated as evidence rather than authority when resumed.</summary>
    public string WorkingContext { get; init; } = string.Empty;

    /// <summary>Indicates that the saved working context exceeded its bounded checkpoint allowance.</summary>
    public bool WorkingContextTruncated { get; init; }
}
