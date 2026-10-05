namespace Threadsmith.Core;

using System.Text.Json.Serialization;

/// <summary>Direct-query access to the same mechanics used by conversational memory recall.</summary>
public interface IRepositoryMemorySearch
{
    /// <summary>Searches a consistent repository snapshot without assembling model context.</summary>
    Task<RepositoryMemoryRetrievalResult> SearchAsync(RepositoryMemorySearchRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Host-owned direct search inputs, independent of conversational query construction.</summary>
public sealed record RepositoryMemorySearchRequest
{
    /// <summary>Canonical repository identity.</summary>
    public required string RepositoryIdentity { get; init; }

    /// <summary>Complete direct query.</summary>
    public required string Query { get; init; }

    /// <summary>Immutable search limits and qualification settings.</summary>
    public required RepositoryMemorySearchOptions Options { get; init; }

    /// <summary>Searches standing preferences as well as situational entries.</summary>
    public bool IncludeStandingPreferences { get; init; }

    /// <summary>Turn-scoped degraded cache identity for ordinary recall.</summary>
    public RunId? UserTurnId { get; init; }

    /// <summary>Indicates that the conversational caller bounded its query.</summary>
    public bool QueryBounded { get; init; }

    /// <summary>Previously generated complete query vector for the current embedding space.</summary>
    public TextEmbeddingResult? PreparedEmbedding { get; init; }

    /// <summary>Space of the prepared vector; prevents accidental cross-model reuse.</summary>
    public string? PreparedEmbeddingSpaceId { get; init; }

    /// <summary>Previously admitted revisions retained only while still current and eligible.</summary>
    public IReadOnlyList<RepositoryMemoryInclusion> RetainedMemories { get; init; } = [];

    /// <summary>Normalized active-turn discovery hints.</summary>
    public IReadOnlyList<string> Concepts { get; init; } = [];
}

/// <summary>Search qualification and selection bounds independent of context admission and storage capacity.</summary>
public sealed record RepositoryMemorySearchOptions
{
    /// <summary>Strict semantic discovery floor.</summary>
    public double SemanticMinimum { get; init; } = RepositoryMemoryOptions.DefaultSemanticMinimum;

    /// <summary>Maximum returned candidates; zero disables selection.</summary>
    public int MaximumResults { get; init; } = 3;

    /// <summary>Whether complete pairs are scored by the cross-encoder.</summary>
    public bool RerankerEnabled { get; init; }

    /// <summary>Maximum hybrid candidates scored.</summary>
    public int RerankerCandidateLimit { get; init; } = 8;

    /// <summary>Whether concepts can discover candidates.</summary>
    public bool ConceptRecallEnabled { get; init; }

    /// <summary>Reserved concept-only scoring window.</summary>
    public int ConceptCandidateLimit { get; init; } = 4;

    /// <summary>Whether unmatched concepts use the native resolver.</summary>
    public bool ConceptFuzzyEnabled { get; init; }

    /// <summary>Maximum native edit cost.</summary>
    public int ConceptFuzzyMaximumDistance { get; init; } = 1;

    /// <summary>Shared text matching and bounded fuzzy expansion policy.</summary>
    public RepositoryMemoryLexicalOptions Lexical { get; init; } = new();

    /// <summary>Maximum lexical query terms.</summary>
    public int MaximumQueryTerms { get; init; } = 32;

    /// <summary>Bound on each shared cache.</summary>
    public int MaximumCacheEntries { get; init; } = 64;

    /// <summary>Bound on textual diagnostics.</summary>
    public int MaximumDiagnostics { get; init; } = 64;

    /// <summary>Maps conversational configuration to independent search limits.</summary>
    public static RepositoryMemorySearchOptions ForRecall(RepositoryMemoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new()
        {
            Lexical = options.Lexical,
            SemanticMinimum = options.SemanticMinimum,
            MaximumResults = options.EffectiveContextMaximum,
            RerankerEnabled = options.RerankerEnabled,
            RerankerCandidateLimit = options.RerankerCandidateLimit,
            ConceptRecallEnabled = options.ConceptRecallEnabled,
            ConceptCandidateLimit = options.ConceptCandidateLimit,
            ConceptFuzzyEnabled = options.ConceptFuzzyEnabled,
            ConceptFuzzyMaximumDistance = options.ConceptFuzzyMaximumDistance,
            MaximumQueryTerms = options.MaximumQueryTerms,
            MaximumCacheEntries = options.MaximumCacheEntries,
            MaximumDiagnostics = options.MaximumDiagnostics,
        };
    }

    /// <summary>Maps independent collision configuration without inheriting conversational admission limits.</summary>
    public static RepositoryMemorySearchOptions ForReconciliation(RepositoryMemoryOptions options)
    {
        return ForRecall(options) with
        {
            SemanticMinimum = options.ReconciliationSemanticMinimum,
            MaximumResults = checked(options.ReconciliationCandidateLimit + (options.ReconciliationConceptsEnabled ? options.ReconciliationConceptCandidateLimit : 0)),
            RerankerEnabled = options.ReconciliationRerankerEnabled,
            Lexical = options.ReconciliationLexical,
            RerankerCandidateLimit = options.ReconciliationCandidateLimit,
            ConceptRecallEnabled = options.ReconciliationConceptsEnabled,
            ConceptCandidateLimit = options.ReconciliationConceptCandidateLimit,
            ConceptFuzzyEnabled = options.ReconciliationConceptFuzzyEnabled,
            ConceptFuzzyMaximumDistance = options.ReconciliationConceptFuzzyMaximumDistance,
        };
    }

    /// <summary>Validates search bounds without consulting context or storage options.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Lexical);
        Lexical.Validate();
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumResults);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RerankerCandidateLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ConceptCandidateLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumQueryTerms);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCacheEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDiagnostics);
        if (!double.IsFinite(SemanticMinimum) || SemanticMinimum is < -1 or > 1
            || (ConceptFuzzyEnabled && ConceptFuzzyMaximumDistance is not > 0))
        {
            throw new ArgumentException("Memory search requires valid semantic bounds and positive fuzzy bounds.");
        }
    }
}

/// <summary>Execution state of one search branch, independent of human-readable diagnostic wording.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RepositoryMemorySearchBranchStatus>))]
public enum RepositoryMemorySearchBranchStatus
{
    /// <summary>The caller disabled the branch.</summary>
    Disabled,

    /// <summary>The branch completed successfully.</summary>
    Completed,

    /// <summary>The branch had no work to perform.</summary>
    NotRequired,

    /// <summary>The dependency or lookup failed.</summary>
    Unavailable,

    /// <summary>Complete inputs or scores could not be established.</summary>
    Incomplete,
}

/// <summary>Structured search evidence; window omissions are separate from later output or context omissions.</summary>
public sealed record RepositoryMemorySearchDetails
{
    /// <summary>Lexical snapshot outcome.</summary>
    public RepositoryMemorySearchBranchStatus Lexical { get; init; }

    /// <summary>Bounded fuzzy text expansion outcome.</summary>
    public RepositoryMemorySearchBranchStatus FuzzyLexical { get; init; }

    /// <summary>Additional text terms searched after preserving exact matches.</summary>
    public IReadOnlyList<MemoryTermMatch> LexicalExpansions { get; init; } = [];

    /// <summary>Compatible complete-vector outcome.</summary>
    public RepositoryMemorySearchBranchStatus Semantic { get; init; }

    /// <summary>Complete-pair scoring outcome.</summary>
    public RepositoryMemorySearchBranchStatus Reranker { get; init; }

    /// <summary>Concept lookup outcome.</summary>
    public RepositoryMemorySearchBranchStatus Concepts { get; init; }

    /// <summary>Distinct candidates qualified by lexical or semantic discovery.</summary>
    public int HybridCandidates { get; init; }

    /// <summary>Distinct candidates qualified only by concepts.</summary>
    public int ConceptOnlyCandidates { get; init; }

    /// <summary>Candidates admitted to the complete-pair comparison window.</summary>
    public int ComparedCandidates { get; init; }

    /// <summary>Discovery candidates excluded by the comparison window.</summary>
    public int CandidateWindowOmissions { get; init; }

    /// <summary>Qualified candidates excluded by the independent result bound.</summary>
    public int ResultLimitOmissions { get; init; }

    /// <summary>At least one query-memory pair exceeded the complete-input limit.</summary>
    public bool PairTruncated { get; init; }
}

/// <summary>Bounded typo recovery shared by both memory search consumers.</summary>
public sealed record RepositoryMemoryLexicalOptions
{
    /// <summary>Enables expansion of terms absent from the indexed vocabulary.</summary>
    public bool FuzzyEnabled { get; init; }

    /// <summary>Required positive native edit cost when enabled.</summary>
    public int FuzzyMaximumDistance { get; init; } = 1;

    /// <summary>Maximum alternatives per original term; the native lookup supports one through three.</summary>
    public int MaximumExpansionsPerTerm { get; init; } = 3;

    /// <summary>Maximum additional terms across the complete query.</summary>
    public int MaximumExpansions { get; init; } = 16;

    /// <summary>Validates finite work bounds and enabled distance policy.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaximumExpansionsPerTerm, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumExpansionsPerTerm, 3);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumExpansions);
        if (FuzzyEnabled && FuzzyMaximumDistance is not > 0)
        {
            throw new ArgumentException("Fuzzy memory text lookup requires a positive maximum distance.");
        }
    }
}
