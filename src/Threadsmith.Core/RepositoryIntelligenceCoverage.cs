namespace Threadsmith.Core;

/// <summary>One repository-specific investigation question, separate from published memories.</summary>
public sealed record RepositoryIntelligenceCoverage
{
    /// <summary>Stable model-chosen key used to replace or reopen this question.</summary>
    public required string Key { get; init; }

    /// <summary>Feature family or shared facility discovered in this repository.</summary>
    public required string Area { get; init; }

    /// <summary>Implementation question to investigate.</summary>
    public required string Question { get; init; }

    /// <summary>Uninvestigated, investigating, investigated, or blocked.</summary>
    public required string Status { get; init; }

    /// <summary>Evidence-based conclusion, including why no memory was warranted when applicable.</summary>
    public string Conclusion { get; init; } = string.Empty;

    /// <summary>Remaining work or concrete reason investigation is blocked.</summary>
    public string Gaps { get; init; } = string.Empty;

    /// <summary>Explicit scope or verification limits that do not represent unfinished implementation investigation.</summary>
    public string Limitations { get; init; } = string.Empty;

    /// <summary>Separately tracked investigation questions identified while answering this item.</summary>
    public IReadOnlyList<string> FollowUpKeys { get; init; } = [];

    /// <summary>Prepared assertions resulting from investigation; may later be reconciled or evicted.</summary>
    public IReadOnlyList<RepositoryMemoryId> MemoryIds { get; init; } = [];

    /// <summary>Host-resolved receipts for files actually delivered during collection.</summary>
    public IReadOnlyList<RepositoryMemorySupport> Supports { get; init; } = [];
}

/// <summary>Bounded checklist inspection with a stable lexical cursor.</summary>
public sealed record RepositoryIntelligenceCoveragePage(IReadOnlyList<RepositoryIntelligenceCoverage> Items, string? NextAfterKey);
