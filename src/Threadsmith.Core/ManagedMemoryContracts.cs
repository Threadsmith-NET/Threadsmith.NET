namespace Threadsmith.Core;

using System.Text.Json.Serialization;

/// <summary>Effective repository memory bounds, frozen for each operation or user turn.</summary>
public sealed record RepositoryMemoryOptions
{
    /// <summary>Maximum number of stored entries, shared by manual and model origins.</summary>
    public int MaxNumberOfRepoMemories { get; init; } = 20;

    /// <summary>Maximum number of relevant entries injected into automatic context.</summary>
    public int MaxRepoMemoriesInContext { get; init; } = 3;

    /// <summary>Context maximum constrained by the storage capacity.</summary>
    public int EffectiveContextMaximum => Math.Min(MaxNumberOfRepoMemories, MaxRepoMemoriesInContext);

    /// <summary>Rejects invalid configuration before it affects storage or retrieval.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxNumberOfRepoMemories);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRepoMemoriesInContext);
    }
}

/// <summary>Explicit actor that requested stored memory content.</summary>
public enum RepositoryMemoryOrigin
{
    /// <summary>The admitted model-callable memories tool.</summary>
    Model,

    /// <summary>An explicit user memory command.</summary>
    Manual,
}

/// <summary>Detached repository memory content, usage and compatible embedding metadata.</summary>
public sealed record RepositoryMemoryEntry
{
    /// <summary>Stable memory identity.</summary>
    public required RepositoryMemoryId Id { get; init; }

    /// <summary>Host-supplied canonical repository identity.</summary>
    public required string RepositoryIdentity { get; init; }

    /// <summary>Complete sanitized memory text.</summary>
    public required string Text { get; init; }

    /// <summary>SHA-256 of normalized text, independent of bookkeeping.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Monotonically increasing meaningful content revision.</summary>
    public long Revision { get; init; } = 1;

    /// <summary>Explicit origin of the most recent meaningful content write.</summary>
    public required RepositoryMemoryOrigin Origin { get; init; }

    /// <summary>Provider-routing sensitivity of the complete text.</summary>
    public ConversationSensitivity Sensitivity { get; init; } = ConversationSensitivity.Sensitive;

    /// <summary>Original creation time.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Last meaningful content change, including initial creation.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Distinct submitted user turns containing this content revision.</summary>
    public long InclusionCount { get; init; }

    /// <summary>Most recent submitted inclusion of this content revision.</summary>
    public DateTimeOffset? LastIncludedAt { get; init; }

    /// <summary>Optional source session identifier supplied by the host.</summary>
    public string? SourceSessionId { get; init; }

    /// <summary>Optional source run identifier supplied by the host.</summary>
    public string? SourceRunId { get; init; }

    /// <summary>Optional source invocation identifier supplied by the host.</summary>
    public string? SourceInvocationId { get; init; }

    /// <summary>Detached normalized vector; empty if absent or invalid.</summary>
    [JsonIgnore]
    public ReadOnlyMemory<float> Embedding { get; init; }

    /// <summary>Embedding identity, including preprocessing and model parameters.</summary>
    public string? EmbeddingSpaceId { get; init; }

    /// <summary>Stored vector dimensionality.</summary>
    public int EmbeddingDimensions { get; init; }

    /// <summary>Hash of the complete content represented by the vector.</summary>
    public string? EmbeddingContentHash { get; init; }

    /// <summary>Revision represented by the vector.</summary>
    public long? EmbeddingRevision { get; init; }
}

/// <summary>Sanitized explicit write prepared before embedding generation and database locking.</summary>
public sealed record RepositoryMemoryWrite
{
    /// <summary>Complete sanitized text, with normalized newlines and outer whitespace trimmed.</summary>
    public required string Text { get; init; }

    /// <summary>Host-authorized source of the write.</summary>
    public required RepositoryMemoryOrigin Origin { get; init; }

    /// <summary>Sensitivity after sanitization.</summary>
    public ConversationSensitivity Sensitivity { get; init; } = ConversationSensitivity.Sensitive;

    /// <summary>Optional host-supplied session provenance.</summary>
    public string? SourceSessionId { get; init; }

    /// <summary>Optional host-supplied run provenance.</summary>
    public string? SourceRunId { get; init; }

    /// <summary>Optional host-supplied tool invocation provenance.</summary>
    public string? SourceInvocationId { get; init; }
}

/// <summary>Outcome of an atomic content write, including non-mutating duplicate or conflict results.</summary>
public enum RepositoryMemoryWriteStatus
{
    /// <summary>A new entry was committed.</summary>
    Added,

    /// <summary>Existing content was meaningfully corrected.</summary>
    Updated,

    /// <summary>Identical content was already stored under the same identity.</summary>
    Unchanged,

    /// <summary>A different entry already has the complete normalized text.</summary>
    Duplicate,

    /// <summary>The update target no longer exists.</summary>
    NotFound,

    /// <summary>The target changed while its replacement embedding was computed.</summary>
    Conflict,
}

/// <summary>Atomic write outcome and IDs removed by bounded capacity enforcement.</summary>
public sealed record RepositoryMemoryWriteResult(
    RepositoryMemoryWriteStatus Status,
    RepositoryMemoryEntry? Entry,
    IReadOnlyList<RepositoryMemoryId> EvictedIds);

/// <summary>Qualified SQLite lexical candidate; lower BM25 values rank ahead of higher ones.</summary>
public sealed record RepositoryMemoryLexicalMatch(RepositoryMemoryId Id, double Bm25);

/// <summary>One consistent database snapshot for both retrieval branches and ranking-cache identity.</summary>
public sealed record RepositoryMemoryReadSnapshot(
    string RepositoryIdentity,
    long Revision,
    IReadOnlyList<RepositoryMemoryEntry> Entries,
    IReadOnlyList<RepositoryMemoryLexicalMatch> LexicalMatches,
    IReadOnlyList<string> Warnings);

/// <summary>Final-dispatch receipt fenced by the exact content revision transmitted.</summary>
public sealed record RepositoryMemoryInclusion(RepositoryMemoryId Id, long Revision);

/// <summary>Transactional repository-local persistence independent of inference and provider types.</summary>
public interface IManagedRepositoryMemoryStore
{
    /// <summary>Reads entries and qualified, safely quoted lexical matches in one database snapshot.</summary>
    Task<RepositoryMemoryReadSnapshot> GetSnapshotAsync(
        string repositoryIdentity,
        IReadOnlyList<string> lexicalTerms,
        CancellationToken cancellationToken = default);

    /// <summary>Commits a pre-embedded entry and any deterministic eviction in one short transaction.</summary>
    Task<RepositoryMemoryWriteResult> AddAsync(
        string repositoryIdentity,
        RepositoryMemoryWrite write,
        TextEmbeddingModelDescriptor model,
        TextEmbeddingResult embedding,
        RepositoryMemoryOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Commits a correction only while its originally observed content revision is current.</summary>
    Task<RepositoryMemoryWriteResult> UpdateAsync(
        string repositoryIdentity,
        RepositoryMemoryId id,
        long expectedRevision,
        RepositoryMemoryWrite write,
        TextEmbeddingModelDescriptor model,
        TextEmbeddingResult embedding,
        RepositoryMemoryOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes current content, its FTS row and inclusion receipts atomically.</summary>
    Task<bool> RemoveAsync(string repositoryIdentity, RepositoryMemoryId id, CancellationToken cancellationToken = default);

    /// <summary>Enforces a reduced capacity at repository bind without performing inference.</summary>
    Task<IReadOnlyList<RepositoryMemoryId>> EnforceCapacityAsync(
        string repositoryIdentity,
        RepositoryMemoryOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Attaches rebuilt vectors only to unchanged complete content; never evicts.</summary>
    Task<bool> AttachEmbeddingAsync(
        string repositoryIdentity,
        RepositoryMemoryId id,
        long expectedRevision,
        string expectedContentHash,
        TextEmbeddingModelDescriptor model,
        TextEmbeddingResult embedding,
        CancellationToken cancellationToken = default);

    /// <summary>Accounts once per submitted run and content revision without changing ranking revision or FTS.</summary>
    Task RecordInclusionsAsync(
        string repositoryIdentity,
        RunId runId,
        IReadOnlyList<RepositoryMemoryInclusion> inclusions,
        CancellationToken cancellationToken = default);

    /// <summary>Removes idempotency receipts when their owning run is expired by retention.</summary>
    Task PruneInclusionsAsync(string repositoryIdentity, RunId runId, CancellationToken cancellationToken = default);
}
