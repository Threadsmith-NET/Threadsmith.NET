namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Automatic recall admits only strict semantic matches through the shared search pipeline.</summary>
public static class MemorySemanticAdmissionTests
{
    /// <summary>Every reranker fallback receives only already admitted candidates.</summary>
    [Theory]
    [InlineData("disabled")]
    [InlineData("missing")]
    [InlineData("descriptor")]
    [InlineData("failure")]
    public static async Task Reranker_fallback_never_restores_rejected_candidates(string mode)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator
        {
            Generate = text => new TextEmbeddingResult(text == "alpha rejected" ? (float[])[-1, 0, 0] : (float[])[1, 0, 0], 3, false),
        };
        var service = MemoryTestData.CreateService(fixture, generator);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "alpha rejected"), ct);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "paraphrase"), ct);
        using var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        var encoder = new RecordingEncoder { Fail = mode == "failure", FailDescriptor = mode == "descriptor" };
        using var retriever = new HybridRepositoryMemoryRetriever(store, generator, mode == "missing" ? null : encoder);
        var request = Recall();
        var result = await retriever.RetrieveAsync(request with { Options = request.Options with { RerankerEnabled = mode != "disabled" } }, ct);

        Assert.Equal("paraphrase", Assert.Single(result.Selected).Entry.Text);
        Assert.DoesNotContain("alpha rejected", encoder.Documents);
        if (mode == "failure")
        {
            Assert.Equal("paraphrase", Assert.Single(encoder.Documents));
        }
    }

    /// <summary>Policy and threshold changes cannot borrow successful or turn-scoped degraded rankings.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task Policy_and_threshold_isolate_successful_and_degraded_ranking_caches(bool strictFirst, bool degraded)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator
        {
            Generate = text => new TextEmbeddingResult(text == "alpha rejected" ? (float[])[0, 1, 0] : (float[])[1, 0, 0], 3, false),
        };
        var service = MemoryTestData.CreateService(fixture, generator);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "alpha rejected"), ct);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "paraphrase"), ct);
        using var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        using var retriever = new HybridRepositoryMemoryRetriever(store, generator, new RecordingEncoder { Fail = degraded });
        var request = new RepositoryMemorySearchRequest
        {
            RepositoryIdentity = MemoryTestData.Repository,
            Query = "alpha",
            UserTurnId = RunId.New(),
            RequireSemanticMatch = strictFirst,
            Options = new RepositoryMemorySearchOptions { SemanticMinimum = 0, RerankerEnabled = true },
        };
        var first = await retriever.SearchAsync(request, ct);
        var other = await retriever.SearchAsync(request with { RequireSemanticMatch = !strictFirst }, ct);
        var cached = await retriever.SearchAsync(request, ct);
        var boundary = await retriever.SearchAsync(request with { RequireSemanticMatch = true, Options = request.Options with { SemanticMinimum = 1 } }, ct);

        Assert.Equal(strictFirst ? 1 : 2, first.Selected.Count);
        Assert.Equal(strictFirst ? 2 : 1, other.Selected.Count);
        Assert.False(other.RankingCacheHit);
        Assert.True(other.QueryEmbeddingCacheHit);
        Assert.True(cached.RankingCacheHit);
        Assert.Empty(boundary.Selected);
        Assert.False(boundary.RankingCacheHit);
        Assert.True(boundary.QueryEmbeddingCacheHit);
        Assert.Equal(3, generator.Calls);
    }

    /// <summary>Invalid or stale stored vectors cannot qualify when maintenance cannot repair them.</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("zero")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("dimensions")]
    [InlineData("space")]
    [InlineData("revision")]
    [InlineData("hash")]
    public static async Task Invalid_stored_embeddings_cannot_qualify_despite_lexical_and_concept_matches(string failure)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "alpha invalid") with { Concepts = ["routing"] }, ct);
        using var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        var snapshot = await store.GetSnapshotAsync(MemoryTestData.Repository, ["alpha"], ct);
        var entry = Assert.Single(snapshot.Entries);
        entry = failure switch
        {
            "space" => entry with { EmbeddingSpaceId = "other" },
            "revision" => entry with { EmbeddingRevision = entry.Revision + 1 },
            "hash" => entry with { EmbeddingContentHash = "stale" },
            _ => entry with { Embedding = InvalidVector(failure) },
        };
        generator.Generate = text => text == "alpha invalid" ? throw new InvalidOperationException("rebuild unavailable") : new TextEmbeddingResult((float[])[1, 0, 0], 3, false);
        var encoder = new RecordingEncoder();
        using var retriever = new HybridRepositoryMemoryRetriever(new SnapshotStore(snapshot with { Entries = [entry] }), generator, encoder);

        var result = await retriever.RetrieveAsync(Recall(), ct);

        Assert.Empty(result.Selected);
        Assert.Empty(encoder.Documents);
        Assert.Equal("semantic scoring unavailable", Assert.Single(result.SearchDetails!.Candidates).Outcome);
        Assert.Equal(0, result.SearchDetails.CandidateWindowOmissions);
        Assert.Equal(0, result.SearchDetails.ResultLimitOmissions);
    }

    /// <summary>Query-independent preferences and lexical reconciliation keep their existing contracts.</summary>
    [Fact]
    public static async Task Standing_preferences_and_reconciliation_keep_independent_behavior()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        using var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        var generator = new TestMemoryEmbeddingGenerator();
        using var retriever = new HybridRepositoryMemoryRetriever(store, generator, new RecordingEncoder());
        var service = new RepositoryMemoryService(store, generator, new SecretOutputSanitizer(), search: retriever);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "alpha beta situational"), ct);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "alpha beta preference") with { MemoryType = RepositoryMemoryType.StandingPreference }, ct);
        var request = Recall();
        var automatic = await retriever.RetrieveAsync(request with { Options = request.Options with { SemanticMinimum = 1 } }, ct);
        var proposal = MemoryTestData.Operation("add", "alpha beta proposal") with
        {
            Options = new RepositoryMemoryOptions { ReconciliationEnabled = true, ReconciliationRerankerEnabled = true, ReconciliationSemanticMinimum = 1 },
        };
        var reconciliation = await service.ExecuteAsync(proposal, ct);

        Assert.Empty(automatic.Selected);
        Assert.Single(automatic.StandingPreferences);
        Assert.Equal("reconciliationRequired", reconciliation.Outcome);
        Assert.Equal(2, reconciliation.Entries.Count);
        Assert.Equal(2, (await store.GetSnapshotAsync(MemoryTestData.Repository, [], ct)).Entries.Count);
    }

    private static RepositoryMemoryRetrievalRequest Recall() => new()
    {
        RepositoryIdentity = MemoryTestData.Repository,
        CurrentInstruction = "alpha",
        Concepts = ["routing"],
        Options = new RepositoryMemoryOptions
        {
            SemanticMinimum = 0,
            RerankerEnabled = true,
            ConceptRecallEnabled = true,
            Intelligence = new()
            {
                Enabled = true,
                Recall = new() { SemanticMinimum = 0, RerankerEnabled = true, ConceptRecallEnabled = true },
            },
        },
    };

    private static float[] InvalidVector(string failure) => failure switch
    {
        "empty" => [],
        "zero" => [0, 0, 0],
        "nan" => [float.NaN, 0, 0],
        "infinity" => [float.PositiveInfinity, 0, 0],
        "dimensions" => [1, 0],
        _ => throw new InvalidOperationException("embedding unavailable"),
    };

    private sealed class RecordingEncoder : ITextCrossEncoder
    {
        public bool Fail { get; init; }

        public bool FailDescriptor { get; init; }

        public List<string> Documents { get; } = [];

        public TextCrossEncoderModelDescriptor Model => FailDescriptor ? throw new InvalidOperationException("unavailable") : new("recording", 512, 64);

        public Task<IReadOnlyList<TextCrossEncoderScore>> ScoreAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Documents.AddRange(documents);
            return Fail ? throw new InvalidOperationException("unavailable") : Task.FromResult<IReadOnlyList<TextCrossEncoderScore>>([.. documents.Select(_ => new TextCrossEncoderScore(1, 3, false))]);
        }
    }

    private sealed class SnapshotStore : IManagedRepositoryMemoryStore
    {
        private readonly RepositoryMemoryReadSnapshot _snapshot;

        public SnapshotStore(RepositoryMemoryReadSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public Task<RepositoryMemoryReadSnapshot> GetSnapshotAsync(string repositoryIdentity, IReadOnlyList<string> lexicalTerms, CancellationToken cancellationToken = default) => Task.FromResult(_snapshot);

        public Task<RepositoryMemoryWriteResult> AddAsync(string repositoryIdentity, RepositoryMemoryWrite write, TextEmbeddingModelDescriptor model, TextEmbeddingResult embedding, RepositoryMemoryOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RepositoryMemoryWriteResult> UpdateAsync(string repositoryIdentity, RepositoryMemoryId id, long expectedRevision, RepositoryMemoryWrite write, TextEmbeddingModelDescriptor? model, TextEmbeddingResult? embedding, RepositoryMemoryOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<RepositoryMemoryEntry?> RemoveAsync(string repositoryIdentity, RepositoryMemoryId id, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<RepositoryMemoryId>> EnforceCapacityAsync(string repositoryIdentity, RepositoryMemoryOptions options, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> AttachEmbeddingAsync(string repositoryIdentity, RepositoryMemoryId id, long expectedRevision, string expectedContentHash, TextEmbeddingModelDescriptor model, TextEmbeddingResult embedding, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RecordInclusionsAsync(string repositoryIdentity, RunId runId, IReadOnlyList<RepositoryMemoryInclusion> inclusions, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task PruneInclusionsAsync(string repositoryIdentity, RunId runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
