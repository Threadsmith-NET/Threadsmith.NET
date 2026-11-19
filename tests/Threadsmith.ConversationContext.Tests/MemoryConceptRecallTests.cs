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

    /// <summary>Verifies the observable memory contract through its owning boundary.</summary>
    [Fact]
    public static async Task Fuzzy_failure_retries_without_poisoning_cache_and_revision_changes_refresh_vocabulary()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        var saved = await service.ExecuteAsync(MemoryTestData.Operation("add", "iterator policy") with { Concepts = ["cancellation"] });
        var store = new ConceptReadStore(await service.GetSnapshotAsync(MemoryTestData.Repository));
        using var retriever = new HybridRepositoryMemoryRetriever(store, generator, new MemoryScoreEncoder());
        var request = new RepositoryMemorySearchRequest
        {
            RepositoryIdentity = MemoryTestData.Repository,
            Query = "unrelated",
            UserTurnId = RunId.New(),
            Concepts = ["cancelation"],
            Options = Options with { ConceptFuzzyEnabled = true, ConceptFuzzyMaximumDistance = 20 },
        };
        var failed = await retriever.SearchAsync(request);
        Assert.Empty(failed.Selected);
        Assert.True(failed.ConceptResolutionPending);
        var recovered = await retriever.SearchAsync(request);
        Assert.Equal(saved.Id, Assert.Single(recovered.Selected).Entry.Id);
        Assert.False(recovered.ConceptResolutionPending);
        Assert.True((await retriever.SearchAsync(request)).RankingCacheHit);
        Assert.Equal(2, store.Resolutions);
        store.Snapshot = store.Snapshot with { Revision = store.Snapshot.Revision + 1, Entries = [saved.Entry! with { Concepts = [] }] };
        Assert.Empty((await retriever.SearchAsync(request)).Selected);
        Assert.Equal(3, store.Resolutions);
    }

    /// <summary>Intelligence fallback remains visible when ordinary recall succeeds.</summary>
    [Fact]
    public static async Task Inspection_preserves_intelligence_failure_separately_from_ordinary_success()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        using var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        await store.AddAsync(
            MemoryTestData.Repository,
            new RepositoryMemoryWrite { Text = "cancellation policy", Origin = RepositoryMemoryOrigin.Manual, Ownership = RepositoryMemoryOwnership.Intelligence },
            generator.Model,
            await generator.GenerateAsync("cancellation policy", ct),
            new RepositoryMemoryOptions(),
            ct);
        using var retriever = new HybridRepositoryMemoryRetriever(store, generator);
        await using var events = new Threadsmith.Execution.DomainEventStream();
        var sanitizer = new Threadsmith.Telemetry.SecretOutputSanitizer();
        var assembler = new ContextAssembler(new EvidenceStore(events, sanitizer), new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, events, TestPromptLoader.Instance, repositoryMemoryRetriever: retriever);
        var result = await assembler.AssembleAsync(
            new ContextAssemblyRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                Phase = RunPhase.EvidenceCollection,
                RepositoryPath = fixture.DirectoryPath,
                RepositoryIdentity = MemoryTestData.Repository,
                Task = new TaskSpecification("cancellation policy", []),
                IntelligenceEnabled = true,
                RepositoryMemoryOptions = new() { Intelligence = new() { Enabled = true } },
            },
            ct);
        Assert.Equal(RepositoryMemorySearchBranchStatus.Disabled, result.Inspection.RepositoryMemorySearch?.Reranker);
        Assert.Equal(RepositoryMemorySearchBranchStatus.Unavailable, result.Inspection.RepositoryIntelligenceSearch?.Reranker);
        Assert.Equal("selected", Assert.Single(result.Inspection.RepositoryIntelligenceSearch!.Candidates).Outcome);
        Assert.Single(result.RepositoryMemoryInclusions!);
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
