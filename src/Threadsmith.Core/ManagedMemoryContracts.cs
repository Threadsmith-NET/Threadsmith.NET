namespace Threadsmith.Core;

using System.Text.Json.Serialization;

/// <summary>Effective repository memory bounds and retrieval policy, frozen for each operation or user turn.</summary>
public sealed record RepositoryMemoryOptions
{
    /// <summary>Calibrated default for the bundled local embedding model's semantic branch.</summary>
    public const double DefaultSemanticMinimum = 0.47;

    /// <summary>Maximum number of stored entries, shared by manual and model origins.</summary>
    public int MaxNumberOfRepoMemories { get; init; } = 20;

    /// <summary>Maximum number of relevant entries injected into automatic context.</summary>
    public int MaxRepoMemoriesInContext { get; init; } = 3;

    /// <summary>Committed standing-preference count at which callers may warn about accumulating durable guidance.</summary>
    public int StandingPreferenceWarningThreshold { get; init; } = 3;

    /// <summary>Strict minimum cosine similarity for semantic matches; lexical matches qualify independently.</summary>
    /// <remarks>Must be finite and between -1 and 1 inclusive. A value of 1 excludes the semantic branch.</remarks>
    public double SemanticMinimum { get; init; } = DefaultSemanticMinimum;

    /// <summary>Enables local cross-encoder reranking after ordinary hybrid candidate qualification.</summary>
    public bool RerankerEnabled { get; init; }

    /// <summary>Maximum qualified candidates sent to the local reranker.</summary>
    public int RerankerCandidateLimit { get; init; } = 8;

    /// <summary>Independent write-time collision checking; ranks potential overlaps for caller review.</summary>
    public bool ReconciliationEnabled { get; init; }

    /// <summary>Required reranking for enabled write-time reconciliation.</summary>
    public bool ReconciliationRerankerEnabled { get; init; } = true;

    /// <summary>Independent lexical policy for write-time reconciliation.</summary>
    public RepositoryMemoryLexicalOptions ReconciliationLexical { get; init; } = new();

    /// <summary>Semantic qualification for write-time candidate discovery.</summary>
    public double ReconciliationSemanticMinimum { get; init; } = DefaultSemanticMinimum;

    /// <summary>Maximum candidates compared during a write check.</summary>
    public int ReconciliationCandidateLimit { get; init; } = 20;

    /// <summary>Uses supplied concepts to discover additional reconciliation candidates.</summary>
    public bool ReconciliationConceptsEnabled { get; init; } = true;

    /// <summary>Additional concept-only comparison slots for reconciliation.</summary>
    public int ReconciliationConceptCandidateLimit { get; init; } = 4;

    /// <summary>Enables fuzzy concept resolution during reconciliation.</summary>
    public bool ReconciliationConceptFuzzyEnabled { get; init; }

    /// <summary>Maximum reconciliation concept edit cost when enabled.</summary>
    public int ReconciliationConceptFuzzyMaximumDistance { get; init; } = 1;

    /// <summary>Fuzzy lexical discovery policy for conversational recall.</summary>
    public RepositoryMemoryLexicalOptions Lexical { get; init; } = new();

    /// <summary>Enables concept discovery during ordinary continuations.</summary>
    public bool ConceptRecallEnabled { get; init; }

    /// <summary>Enables app-owned spellfix lookup for hints with no exact match.</summary>
    public bool ConceptFuzzyEnabled { get; init; }

    /// <summary>Maximum spellfix edit cost; mandatory when fuzzy lookup is enabled.</summary>
    public int ConceptFuzzyMaximumDistance { get; init; } = 1;

    /// <summary>Additional concept-only candidates reserved in the reranking window.</summary>
    public int ConceptCandidateLimit { get; init; } = 4;

    /// <summary>Maximum complete sanitized memory characters.</summary>
    public int MaximumTextCharacters { get; init; } = 2000;

    /// <summary>Maximum retrieval query characters.</summary>
    public int MaximumQueryCharacters { get; init; } = 8000;

    /// <summary>Maximum distinct lexical query terms.</summary>
    public int MaximumQueryTerms { get; init; } = 32;

    /// <summary>Maximum entries in each retrieval cache.</summary>
    public int MaximumCacheEntries { get; init; } = 64;

    /// <summary>Maximum retrieval diagnostics retained.</summary>
    public int MaximumDiagnostics { get; init; } = 64;

    /// <summary>Maximum serialized entry bytes in a memories tool list.</summary>
    public int MaximumListBytes { get; init; } = 48 * 1024;

    /// <summary>Context maximum constrained by the storage capacity.</summary>
    public int EffectiveContextMaximum => Math.Min(MaxNumberOfRepoMemories, MaxRepoMemoriesInContext);

    /// <summary>Rejects invalid configuration before it affects storage or retrieval.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ReconciliationCandidateLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ConceptCandidateLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ReconciliationConceptCandidateLimit);
        ArgumentNullException.ThrowIfNull(Lexical);
        Lexical.Validate();
        ArgumentNullException.ThrowIfNull(ReconciliationLexical);
        ReconciliationLexical.Validate();
        if (ReconciliationEnabled && !ReconciliationRerankerEnabled)
        {
            throw new ArgumentException("Enabled memory reconciliation requires Reconciliation:RerankerEnabled=true.");
        }

        if (ReconciliationConceptFuzzyEnabled && ReconciliationConceptFuzzyMaximumDistance is not > 0)
        {
            throw new ArgumentException("Fuzzy reconciliation concepts require a positive maximum distance.");
        }

        if (ConceptFuzzyEnabled && ConceptFuzzyMaximumDistance is not > 0)
        {
            throw new ArgumentException("Fuzzy concepts require a positive maximum distance.");
        }

        if (!double.IsFinite(ReconciliationSemanticMinimum) || ReconciliationSemanticMinimum is < -1 or > 1)
        {
            throw new ArgumentException("Memory reconciliation requires finite semantic bounds between -1 and 1.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTextCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumQueryCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumQueryTerms);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCacheEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDiagnostics);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumListBytes);
        if (ReconciliationEnabled && MaximumListBytes < 24 * 1024)
        {
            throw new ArgumentException("Reconciliation requires at least 24 KiB for a usable collision response.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxNumberOfRepoMemories);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxRepoMemoriesInContext);
        ArgumentOutOfRangeException.ThrowIfNegative(StandingPreferenceWarningThreshold);
        if (!double.IsFinite(SemanticMinimum))
        {
            throw new ArgumentOutOfRangeException(nameof(SemanticMinimum), "The semantic minimum must be a finite cosine similarity between -1 and 1.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(SemanticMinimum, -1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SemanticMinimum, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(RerankerCandidateLimit, 1);
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

/// <summary>Controls whether a memory is retrieved from the current task or supplied as durable repository guidance.</summary>
public enum RepositoryMemoryType
{
    /// <summary>A task-specific memory eligible for ordinary hybrid retrieval.</summary>
    Situational = 0,

    /// <summary>A durable preference supplied separately from task-specific retrieval.</summary>
    StandingPreference = 1,
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

    /// <summary>Content classification without authority or ranking weight.</summary>
    public ManagedRepositoryMemoryKind Kind { get; init; }

    /// <summary>Normalized applicability hints.</summary>
    public IReadOnlyList<string> Concepts { get; init; } = [];

    /// <summary>Selection behavior for this memory.</summary>
    public RepositoryMemoryType MemoryType { get; init; } = RepositoryMemoryType.Situational;

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

    /// <summary>Content classification without authority or ranking weight.</summary>
    public ManagedRepositoryMemoryKind Kind { get; init; }

    /// <summary>Complete normalized concept replacement.</summary>
    public IReadOnlyList<string> Concepts { get; init; } = [];

    /// <summary>Repository revision checked by reconciliation, fenced before add/eviction.</summary>
    public long? ExpectedRepositoryRevision { get; init; }

    /// <summary>Selection behavior for the committed memory.</summary>
    public RepositoryMemoryType MemoryType { get; init; } = RepositoryMemoryType.Situational;

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
    IReadOnlyList<RepositoryMemoryId> EvictedIds)
{
    /// <summary>Standing-preference count in the same committed write transaction, when a mutation was committed.</summary>
    public int? StandingPreferenceCount { get; init; }
}

/// <summary>Qualified SQLite lexical candidate; lower BM25 values rank ahead of higher ones.</summary>
public sealed record RepositoryMemoryLexicalMatch(RepositoryMemoryId Id, double Bm25)
{
    /// <summary>Qualified only after bounded query expansion; exact matches rank first.</summary>
    public bool IsFuzzy { get; init; }
}

/// <summary>One consistent database snapshot for both retrieval branches and ranking-cache identity.</summary>
public sealed record RepositoryMemoryReadSnapshot(
    string RepositoryIdentity,
    long Revision,
    IReadOnlyList<RepositoryMemoryEntry> Entries,
    IReadOnlyList<RepositoryMemoryLexicalMatch> LexicalMatches,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Native text expansion outcome, separate from successful exact FTS lookup.</summary>
    public RepositoryMemorySearchBranchStatus FuzzyLexicalStatus { get; init; }

    /// <summary>Bounded additional text terms searched.</summary>
    public IReadOnlyList<MemoryTermMatch> LexicalExpansions { get; init; } = [];
}

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

    /// <summary>Reads exact and optionally expanded lexical candidates in the same database snapshot.</summary>
    async Task<RepositoryMemoryReadSnapshot> GetSnapshotAsync(
        string repositoryIdentity,
        IReadOnlyList<string> lexicalTerms,
        RepositoryMemoryLexicalOptions lexicalOptions,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await GetSnapshotAsync(repositoryIdentity, lexicalTerms, cancellationToken);
        return lexicalOptions.FuzzyEnabled ? snapshot with
        {
            FuzzyLexicalStatus = RepositoryMemorySearchBranchStatus.Unavailable,
            Warnings = [.. snapshot.Warnings, "Fuzzy memory text lookup is unavailable; using exact lexical matches only."],
        } : snapshot;
    }

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
        TextEmbeddingModelDescriptor? model,
        TextEmbeddingResult? embedding,
        RepositoryMemoryOptions options,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes current content, its FTS row and inclusion receipts atomically, returning the deleted entry when present.</summary>
    Task<RepositoryMemoryEntry?> RemoveAsync(string repositoryIdentity, RepositoryMemoryId id, CancellationToken cancellationToken = default);

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
