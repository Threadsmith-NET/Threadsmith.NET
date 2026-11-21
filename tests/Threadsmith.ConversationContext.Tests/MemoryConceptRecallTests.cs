namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Xunit;

/// <summary>Behavioral coverage for MemoryConceptRecallTests.</summary>
public static class MemoryConceptRecallTests
{
    /// <summary>Verifies the observable memory contract through its owning boundary.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a b")]
    [InlineData("-prefix")]
    [InlineData("suffix-")]
    [InlineData("double--hyphen")]
    [InlineData("c#")]
    public static void Invalid_concepts_are_rejected(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => MemoryConcepts.Normalize([value]));
    }

    /// <summary>Verifies the observable memory contract through its owning boundary.</summary>
    [Fact]
    public static void Normalization_enforces_scalar_and_input_bounds()
    {
        Assert.Equal(["cancellation", "dotnet"], MemoryConcepts.Normalize(["ＤＯＴＮＥＴ", " Cancellation ", "cancellation"]));
        Assert.Single(MemoryConcepts.Normalize([string.Concat(Enumerable.Repeat("\U00020000", 48))]));
        Assert.Throws<ArgumentException>(() => MemoryConcepts.Normalize([new string('a', 49)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => MemoryConcepts.Normalize(Enumerable.Repeat("a", 9).ToArray()));
        new RepositoryMemoryOptions { ReconciliationEnabled = true }.Validate();
        new RepositoryMemoryOptions { ConceptRecallEnabled = true }.Validate();
        new RepositoryMemoryOptions { ConceptFuzzyEnabled = true }.Validate();
        Assert.Throws<ArgumentException>(() => new RepositoryMemoryOptions { ConceptFuzzyEnabled = true, ConceptFuzzyMaximumDistance = 0 }.Validate());
        Assert.Throws<ArgumentException>(() => new RepositoryMemoryOptions { ReconciliationEnabled = true, MaximumListBytes = 1024 }.Validate());
    }

    private static RepositoryMemorySearchOptions Options => new() { ConceptRecallEnabled = true, RerankerEnabled = true, SemanticMinimum = 1 };

    private sealed class ConceptReadStore : IManagedRepositoryMemoryStore, IRepositoryMemoryTermResolver
    {
        public ConceptReadStore(RepositoryMemoryReadSnapshot snapshot)
        {
            Snapshot = snapshot;
        }

        public RepositoryMemoryReadSnapshot Snapshot { get; set; }

        public int Resolutions { get; private set; }

        public Task<RepositoryMemoryReadSnapshot> GetSnapshotAsync(string repositoryIdentity, IReadOnlyList<string> lexicalTerms, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Snapshot);
        }

        public Task<MemoryTermResolution> ResolveTermsAsync(RepositoryMemoryVocabularySnapshot snapshot, IReadOnlyList<string> queries, int maximumDistance, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Resolutions++;
            return Task.FromResult(Resolutions == 1 ? new MemoryTermResolution([], "temporary native load failure")
                : new MemoryTermResolution(snapshot.Vocabulary.Contains("cancellation") ? [new("cancelation", "cancellation", 10)] : []));
        }

        public Task<RepositoryMemoryWriteResult> AddAsync(string repositoryIdentity, RepositoryMemoryWrite write, TextEmbeddingModelDescriptor model, TextEmbeddingResult embedding, RepositoryMemoryOptions options, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<RepositoryMemoryWriteResult> UpdateAsync(string repositoryIdentity, RepositoryMemoryId id, long expectedRevision, RepositoryMemoryWrite write, TextEmbeddingModelDescriptor? model, TextEmbeddingResult? embedding, RepositoryMemoryOptions options, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<RepositoryMemoryEntry?> RemoveAsync(string repositoryIdentity, RepositoryMemoryId id, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<RepositoryMemoryId>> EnforceCapacityAsync(string repositoryIdentity, RepositoryMemoryOptions options, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<bool> AttachEmbeddingAsync(string repositoryIdentity, RepositoryMemoryId id, long expectedRevision, string expectedContentHash, TextEmbeddingModelDescriptor model, TextEmbeddingResult embedding, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task RecordInclusionsAsync(string repositoryIdentity, RunId runId, IReadOnlyList<RepositoryMemoryInclusion> inclusions, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task PruneInclusionsAsync(string repositoryIdentity, RunId runId, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
