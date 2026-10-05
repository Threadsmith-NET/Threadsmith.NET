namespace Threadsmith.Core;

/// <summary>Lexical correction supplied by the persistence implementation, never semantic expansion.</summary>
public sealed record MemoryTermMatch(string Query, string Term, int Distance);

/// <summary>Bounded fuzzy matches and visible exact-only degradation.</summary>
public sealed record MemoryTermResolution(IReadOnlyList<MemoryTermMatch> Matches, string? DegradedReason = null, bool VocabularyCacheHit = false);

/// <summary>Detached authoritative vocabulary and the repository generation to which it belongs.</summary>
public sealed record RepositoryMemoryVocabularySnapshot(string RepositoryIdentity, long Revision, IReadOnlyList<string> Vocabulary)
{
    /// <summary>Separates bounded text and concept vocabularies in the same native owner.</summary>
    public MemoryVocabularyKind Kind { get; init; }
}

/// <summary>Native fuzzy vocabulary capability on the existing repository memory store.</summary>
public interface IRepositoryMemoryTermResolver
{
    /// <summary>Resolves unmatched terms against a detached repository-scoped vocabulary.</summary>
    Task<MemoryTermResolution> ResolveTermsAsync(
        RepositoryMemoryVocabularySnapshot snapshot,
        IReadOnlyList<string> queries,
        int maximumDistance,
        CancellationToken cancellationToken = default);
}

/// <summary>The two authoritative vocabularies used by shared memory search.</summary>
public enum MemoryVocabularyKind
{
    /// <summary>Explicit normalized memory concepts.</summary>
    Concepts,

    /// <summary>Terms from the memory text FTS index.</summary>
    Text,
}
