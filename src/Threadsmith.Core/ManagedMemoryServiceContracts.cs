namespace Threadsmith.Core;

/// <summary>One explicit repository-memory operation with host-supplied authority and options.</summary>
public sealed record RepositoryMemoryOperationRequest
{
    /// <summary>Canonical repository identity supplied by the host.</summary>
    public required string RepositoryIdentity { get; init; }

    /// <summary>Closed operation name: add, update, remove, or list.</summary>
    public required string Action { get; init; }

    /// <summary>Target identity for update or remove.</summary>
    public RepositoryMemoryId? Id { get; init; }

    /// <summary>Complete text for add or update.</summary>
    public string? Text { get; init; }

    /// <summary>Host-owned source of the explicit operation.</summary>
    public RepositoryMemoryOrigin Origin { get; init; }

    /// <summary>Sensitivity propagated to conversational model selection.</summary>
    public ConversationSensitivity Sensitivity { get; init; } = ConversationSensitivity.Sensitive;

    /// <summary>Optional source session identity.</summary>
    public string? SourceSessionId { get; init; }

    /// <summary>Optional source run identity.</summary>
    public string? SourceRunId { get; init; }

    /// <summary>Optional source tool invocation identity.</summary>
    public string? SourceInvocationId { get; init; }

    /// <summary>One immutable effective configuration snapshot.</summary>
    public RepositoryMemoryOptions Options { get; init; } = new();
}

/// <summary>Explicit operation outcome, including detached entries for inspection.</summary>
public sealed record RepositoryMemoryOperationResult(
    string Outcome,
    RepositoryMemoryId? Id,
    RepositoryMemoryEntry? Entry,
    IReadOnlyList<RepositoryMemoryEntry> Entries,
    IReadOnlyList<RepositoryMemoryId> EvictedIds);

/// <summary>Shared authority boundary for manual and model-requested repository memories.</summary>
public interface IManagedRepositoryMemoryService
{
    /// <summary>Validates and executes one explicit operation; accepted writes embed the complete text.</summary>
    Task<RepositoryMemoryOperationResult> ExecuteAsync(
        RepositoryMemoryOperationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads current entries without generating embeddings or changing usage.</summary>
    Task<RepositoryMemoryReadSnapshot> GetSnapshotAsync(
        string repositoryIdentity,
        CancellationToken cancellationToken = default);

    /// <summary>Applies a lowered configured capacity at repository bind or configuration refresh.</summary>
    Task<IReadOnlyList<RepositoryMemoryId>> EnforceCapacityAsync(
        string repositoryIdentity,
        RepositoryMemoryOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Accounts only for content revisions submitted to the provider.</summary>
    Task RecordInclusionsAsync(
        string repositoryIdentity,
        RunId runId,
        IReadOnlyList<RepositoryMemoryInclusion> inclusions,
        CancellationToken cancellationToken = default);
}

/// <summary>Bounded retrieval query made exclusively from current instructions and active task intent.</summary>
public sealed record RepositoryMemoryRetrievalRequest
{
    /// <summary>Canonical repository identity supplied by the host.</summary>
    public required string RepositoryIdentity { get; init; }

    /// <summary>Top-level user turn; degraded results are retried on a later turn.</summary>
    public RunId? UserTurnId { get; init; }

    /// <summary>Current user instruction, including current steering when present.</summary>
    public required string CurrentInstruction { get; init; }

    /// <summary>Optional active task intent appended after the current instruction.</summary>
    public string? TaskIntent { get; init; }

    /// <summary>One immutable effective configuration snapshot.</summary>
    public RepositoryMemoryOptions Options { get; init; } = new();
}

/// <summary>Qualified memory candidate with inspectable branch contributions, separate from prompt text.</summary>
public sealed record RepositoryMemoryRetrievalCandidate(
    RepositoryMemoryEntry Entry,
    double Score,
    int? LexicalRank,
    int? SemanticRank,
    double? CosineSimilarity,
    double? CrossEncoderScore = null);

/// <summary>Detached ranking and truthful retrieval/rebuild diagnostics.</summary>
public sealed record RepositoryMemoryRetrievalResult(
    IReadOnlyList<RepositoryMemoryRetrievalCandidate> Selected,
    IReadOnlyList<string> Diagnostics,
    long? MemorySetRevision = null,
    bool QueryTruncated = false,
    bool QueryEmbeddingCacheHit = false,
    bool RankingCacheHit = false);

/// <summary>Combines snapshot-consistent SQLite lexical matches and compatible local semantic vectors.</summary>
public interface IHybridRepositoryMemoryRetriever
{
    /// <summary>Selects zero to the effective configured maximum, with qualification before rank fusion.</summary>
    Task<RepositoryMemoryRetrievalResult> RetrieveAsync(
        RepositoryMemoryRetrievalRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Receipt outcome recorded after a real provider submission, independent of preview selection.</summary>
public sealed record RepositoryMemoryDispatchInspection(
    IReadOnlyList<RepositoryMemoryInclusion> Inclusions,
    string Outcome,
    double ElapsedMilliseconds);
