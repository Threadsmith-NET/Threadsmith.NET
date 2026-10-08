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

    /// <summary>Model-authored phrases share the same concept identity as canonical tokens.</summary>
    [Theory]
    [InlineData(" runtime filters ")]
    [InlineData("Runtime   Filters")]
    [InlineData("\tRuntime\t\r\nFilters\n")]
    [InlineData("\u3000ＲＵＮＴＩＭＥ\u00a0ＦＩＬＴＥＲＳ\u3000")]
    public static void Whitespace_concepts_normalize_and_deduplicate(string value)
    {
        Assert.Equal(["runtime-filters"], MemoryConcepts.Normalize([value, "runtime-filters"]));
    }

    /// <summary>Verifies the observable memory contract through its owning boundary.</summary>
    [Theory]
    [InlineData(true, 4, true)]
    [InlineData(true, 0, true)]
    [InlineData(true, -100, true)]
    [InlineData(false, 4, false)]
    public static async Task Concept_window_is_reserved_and_admission_requires_complete_reranking(bool enabled, double score, bool expected)
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "lexical first"));
        await service.ExecuteAsync(MemoryTestData.Operation("add", "lexical second"));
        var concept = await service.ExecuteAsync(MemoryTestData.Operation("add", "iterator policy") with { Concepts = ["cancellation"] });
        var encoder = new MemoryScoreEncoder { Score = (_, text) => new(text == "iterator policy" ? score : 10, 12, false) };
        using var retriever = new HybridRepositoryMemoryRetriever(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator, encoder);

        var result = await retriever.SearchAsync(new RepositoryMemorySearchRequest
        {
            RepositoryIdentity = MemoryTestData.Repository,
            Query = "lexical",
            Concepts = ["cancellation"],
            Options = Options with { RerankerEnabled = enabled, RerankerCandidateLimit = 1, ConceptCandidateLimit = 1, MaximumResults = 2, },
        });

        Assert.Equal(expected, result.Selected.Any(candidate => candidate.Entry.Id == concept.Id));
        if (enabled)
        {
            Assert.Equal(2, encoder.Documents.Count);
            Assert.Contains("iterator policy", encoder.Documents);
        }
        else
        {
            Assert.Empty(encoder.Documents);
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("Concept-only candidates deferred", StringComparison.Ordinal));
        }
    }

    /// <summary>Verifies the observable memory contract through its owning boundary.</summary>
    [Fact]
    public static async Task Retention_is_bounded_and_removed_or_changed_revisions_are_revalidated()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        var first = await service.ExecuteAsync(MemoryTestData.Operation("add", "first fact") with { Concepts = ["cancellation"] });
        var second = await service.ExecuteAsync(MemoryTestData.Operation("add", "second fact") with { Concepts = ["logging"] });
        using var retriever = new HybridRepositoryMemoryRetriever(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator, new MemoryScoreEncoder());
        var request = new RepositoryMemorySearchRequest { RepositoryIdentity = MemoryTestData.Repository, Query = "unrelated", Options = Options, Concepts = ["cancellation"] };
        var initial = await retriever.SearchAsync(request);
        Assert.Equal(first.Id, Assert.Single(initial.Selected).Entry.Id);
        var retained = new[] { new RepositoryMemoryInclusion(first.Id!.Value, first.Entry!.Revision) };
        var later = await retriever.SearchAsync(request with { Concepts = ["logging"], RetainedMemories = retained, Options = Options with { MaximumResults = 1 } });
        Assert.Equal(first.Id, Assert.Single(later.Selected).Entry.Id);
        Assert.Equal(1, later.SearchDetails?.ResultLimitOmissions);
        await service.ExecuteAsync(MemoryTestData.Operation("update", "changed first fact", first.Id));
        var changed = await retriever.SearchAsync(request with { Concepts = ["logging"], RetainedMemories = retained });
        Assert.Equal(second.Id, Assert.Single(changed.Selected).Entry.Id);
        await service.ExecuteAsync(MemoryTestData.Operation("remove", id: second.Id));
        var removed = await retriever.SearchAsync(request with { Concepts = ["logging"], RetainedMemories = [new(second.Id!.Value, second.Entry!.Revision)] });
        Assert.Empty(removed.Selected);
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

    /// <summary>Verifies the observable memory contract through its owning boundary.</summary>
    [Theory]
    [InlineData(RepositoryMemoryType.Situational)]
    [InlineData(RepositoryMemoryType.StandingPreference)]
    public static async Task Both_types_enter_context_and_disappear_after_removal(RepositoryMemoryType type)
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        var saved = await service.ExecuteAsync(MemoryTestData.Operation("add", "cancellation policy") with { MemoryType = type });
        using var retriever = new HybridRepositoryMemoryRetriever(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator, new MemoryScoreEncoder());
        await using var events = new Threadsmith.Execution.DomainEventStream();
        var sanitizer = new Threadsmith.Telemetry.SecretOutputSanitizer();
        var assembler = new ContextAssembler(new EvidenceStore(events, sanitizer), new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, events, TestPromptLoader.Instance, repositoryMemoryRetriever: retriever);
        var request = new ContextAssemblyRequest { SessionId = SessionId.New(), RunId = RunId.New(), Phase = RunPhase.EvidenceCollection, RepositoryPath = fixture.DirectoryPath, RepositoryIdentity = MemoryTestData.Repository, Task = new TaskSpecification("cancellation policy", []) };
        var included = await assembler.AssembleAsync(request);
        Assert.Equal(saved.Id, Assert.Single(included.RepositoryMemoryInclusions!).Id);
        Assert.NotNull(included.Inspection.RepositoryMemorySearch);
        await service.ExecuteAsync(MemoryTestData.Operation("remove", id: saved.Id));
        Assert.Empty((await assembler.AssembleAsync(request)).RepositoryMemoryInclusions!);
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
