namespace Threadsmith.RepositoryIntelligence;

using Threadsmith.Core;

/// <summary>Explicit deterministic capture limits; no setting is enabled by this request.</summary>
internal sealed record RepositoryProfileSelection
{
    /// <summary>Local ref resolved once before committed reads.</summary>
    public string Revision { get; init; } = "HEAD";

    /// <summary>Literal repository-relative scopes; empty selects the root.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>Maximum discovered paths in the first scoped inventory page.</summary>
    public int MaximumPaths { get; init; } = 200;

    /// <summary>Maximum paths examined while selecting metadata before ordinary inventory.</summary>
    public int MaximumScannedPaths { get; init; } = 10000;

    /// <summary>Maximum metadata files inspected.</summary>
    public int MaximumFiles { get; init; } = 32;

    /// <summary>Maximum committed metadata bytes admitted before batch reads.</summary>
    public int MaximumBytes { get; init; } = 65536;

    /// <summary>Overall collection deadline in seconds.</summary>
    public int MaximumSeconds { get; init; } = 30;

    /// <summary>Requests separately labeled mutable observations, bounded to three files.</summary>
    public bool IncludeOverlay { get; init; }
}

/// <summary>One static metadata observation; values are declarations, never evaluated build results.</summary>
internal sealed record RepositoryStructuralFact(string Path, string SourceIdentity, string Kind, string Name, string? Value);

/// <summary>One mutable source observation, never attributed to a commit.</summary>
internal sealed record RepositoryOverlayObservation(string Path, string? Digest, string State, string? Change = null, string? PreviousPath = null);

/// <summary>Already validated and charged source content retained only for the live operation.</summary>
internal sealed record RepositoryCapturedSource(string Path, string? Revision, string SourceIdentity, string Digest, string Text);

/// <summary>Invocation-only structural facts and explicit coverage limitations.</summary>
internal sealed record RepositoryStructuralProfile(
    GitSnapshotMetadata Snapshot,
    GitSnapshotMetadata? AfterCollection,
    bool PendingChanges,
    RepositoryProfileSelection Selection,
    IReadOnlyList<GitTreeFile> DiscoveredFiles,
    IReadOnlyList<RepositoryStructuralFact> Facts,
    IReadOnlyList<RepositoryOverlayObservation> Overlay,
    int InspectedFiles,
    int AdmittedFiles,
    long AdmittedBytes,
    IReadOnlyList<RepositoryProfileOmission> Omissions)
{
    /// <summary>Governed nested reads already consumed by this capture.</summary>
    public int ReadCalls { get; init; }

    /// <summary>Charged discovery bytes: Git acquisition receipts or reserved mutable-listing allowances.</summary>
    public long AcquiredMetadataBytes { get; init; }

    /// <summary>Live capture handoff; ordinary profile projections do not retain source bodies.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<RepositoryCapturedSource> CapturedSources { get; init; } = [];
}

/// <summary>Status-compatible output with an optional explicit structural capture.</summary>
internal sealed record RepositoryIntelligenceOutput(
    RepositoryIntelligenceControlSnapshot Controls,
    bool AnalysisAvailable,
    string Reason,
    RepositoryStructuralProfile? Profile = null);

/// <summary>Machine-readable coverage state owned by the collector, not inferred by a model.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<RepositoryProfileOmissionReason>))]
internal enum RepositoryProfileOmissionReason
{
    StaticDeclarationsOnly,
    ImmutableSemanticsUnavailable,
    HistoryNotAnalyzed,
    SnapshotUnavailableOrShallow,
    InventoryPageOrPolicyLimit,
    MetadataFileTypeOrByteLimit,
    ContentUnavailableOrSanitized,
    OverlayEnumerationIncomplete,
    NonAtomicOverlayThreeFileLimit,
    NoCommittedInventory,
    DeadlineReached,
    FactLimit,
    DeclarationLimit,
    InvalidOrUnsafeXml,
    OutputByteLimit,
    MetadataAcquisitionLimit,
}

/// <summary>Coverage reason and optional affected repository-relative path.</summary>
internal sealed record RepositoryProfileOmission(RepositoryProfileOmissionReason Reason, string? Path = null);
