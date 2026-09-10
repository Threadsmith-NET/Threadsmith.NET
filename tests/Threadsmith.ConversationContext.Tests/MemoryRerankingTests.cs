namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Xunit;

/// <summary>Optional cross-encoder ranking, qualification, cache identity and fallback over the real memory store.</summary>
public static class MemoryRerankingTests
{
    /// <summary>Reranking sees qualified candidates before the final cap and never receives unrelated records.</summary>
    [Fact]
    public static async Task Enabled_reranking_recovers_a_candidate_beyond_the_final_hybrid_cap()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = await SeedAsync(fixture);
        var encoder = new TestCrossEncoder();
        using var retriever = CreateRetriever(fixture, generator, encoder);
        var baseline = await retriever.RetrieveAsync(Query(enabled: false));
        var target = baseline.Selected[^1].Entry;
        encoder.Score = (_, texts) => texts.Select(text => new TextCrossEncoderScore(text == target.Text ? 9 : -9, 12, false)).ToArray();
        Assert.Equal(0, encoder.Calls);

        var result = await retriever.RetrieveAsync(Query() with
        {
            TaskIntent = "active task intent",
            Options = new RepositoryMemoryOptions { RerankerEnabled = true, MaxRepoMemoriesInContext = 1 },
        });

        Assert.Equal(target.Id, Assert.Single(result.Selected).Entry.Id);
        Assert.Equal(9, result.Selected[0].CrossEncoderScore);
        Assert.Equal(3, encoder.Documents.Count);
        Assert.DoesNotContain("unrelated topic", encoder.Documents);
        Assert.Equal("current request\nactive task intent", encoder.Query);
        Assert.Equal(0, result.Selected[0].Entry.InclusionCount);
    }

    /// <summary>The candidate cap bounds inference input and equal logits preserve deterministic hybrid order.</summary>
    [Fact]
    public static async Task Candidate_limit_bounds_inference_before_stable_selection()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = await SeedAsync(fixture);
        var encoder = new TestCrossEncoder();
        using var retriever = CreateRetriever(fixture, generator, encoder);
        var baseline = await retriever.RetrieveAsync(Query(enabled: false));

        var result = await retriever.RetrieveAsync(Query() with
        {
            Options = new RepositoryMemoryOptions { RerankerEnabled = true, RerankerCandidateLimit = 2 },
        });

        Assert.Equal(2, encoder.Documents.Count);
        Assert.Equal(baseline.Selected.Take(2).Select(item => item.Entry.Id), result.Selected.Select(item => item.Entry.Id));
    }

    /// <summary>Raw negative logits remain eligible without a cutoff and a supplied cutoff is strict.</summary>
    [Fact]
    public static async Task Optional_raw_logit_cutoff_is_strict_and_can_select_zero_memories()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = await SeedAsync(fixture);
        var encoder = new TestCrossEncoder
        {
            Score = (_, texts) => [.. texts.Select((_, index) => new TextCrossEncoderScore(index - 1, 12, false))],
        };
        using var retriever = CreateRetriever(fixture, generator, encoder);
        var all = await retriever.RetrieveAsync(Query());
        var positive = await retriever.RetrieveAsync(Query() with
        {
            Options = new RepositoryMemoryOptions { RerankerEnabled = true, RerankerMinimumScore = 0 },
        });
        var none = await retriever.RetrieveAsync(Query() with
        {
            Options = new RepositoryMemoryOptions { RerankerEnabled = true, RerankerMinimumScore = 1 },
        });

        Assert.Equal(3, all.Selected.Count);
        Assert.Equal(1, Assert.Single(positive.Selected).CrossEncoderScore);
        Assert.Empty(none.Selected);
        Assert.True(none.QueryEmbeddingCacheHit);
        Assert.False(none.RankingCacheHit);
    }

    /// <summary>Enabled state, limits, model identity and content revisions never reuse stale rankings.</summary>
    [Fact]
    public static async Task Cache_reuses_success_and_invalidates_on_options_model_and_content_changes()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = await SeedAsync(fixture);
        var encoder = new TestCrossEncoder();
        using var retriever = CreateRetriever(fixture, generator, encoder);
        var disabled = await retriever.RetrieveAsync(Query(enabled: false));
        var enabled = await retriever.RetrieveAsync(Query());
        var cached = await retriever.RetrieveAsync(Query());
        Assert.False(enabled.RankingCacheHit);
        Assert.True(cached.RankingCacheHit);
        Assert.Equal(1, encoder.Calls);
        Assert.Equal(5, generator.Calls);

        var changedLimit = await retriever.RetrieveAsync(Query() with
        {
            Options = new RepositoryMemoryOptions { RerankerEnabled = true, RerankerCandidateLimit = 1 },
        });
        Assert.Single(changedLimit.Selected);
        Assert.Equal(2, encoder.Calls);
        encoder.Model = encoder.Model with { ModelId = "changed-model" };
        var changedModel = await retriever.RetrieveAsync(Query());
        Assert.False(changedModel.RankingCacheHit);
        Assert.True(changedModel.QueryEmbeddingCacheHit);
        Assert.Equal(3, encoder.Calls);

        var service = MemoryTestData.CreateService(fixture, generator);
        await service.ExecuteAsync(MemoryTestData.Operation("update", "updated note", disabled.Selected[0].Entry.Id));
        var changedContent = await retriever.RetrieveAsync(Query());
        Assert.False(changedContent.RankingCacheHit);
        Assert.Equal(4, encoder.Calls);
        var disabledAgain = await retriever.RetrieveAsync(Query(enabled: false));
        Assert.All(disabledAgain.Selected, candidate => Assert.Null(candidate.CrossEncoderScore));
        Assert.Equal(4, encoder.Calls);
    }

    /// <summary>Failures or partial/invalid scores preserve the full hybrid fallback, ignoring an unusable cutoff.</summary>
    [Theory]
    [InlineData("failure")]
    [InlineData("count")]
    [InlineData("nonfinite")]
    [InlineData("truncated")]
    [InlineData("overlength")]
    [InlineData("negativeTokens")]
    public static async Task Invalid_reranking_preserves_hybrid_results_with_a_diagnostic(string mode)
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = await SeedAsync(fixture);
        var encoder = new TestCrossEncoder
        {
            Score = (_, texts) => mode switch
            {
                "failure" => throw new TextCrossEncoderUnavailableException("missing assets"),
                "count" => [],
                "nonfinite" => [.. texts.Select(_ => new TextCrossEncoderScore(double.NaN, 12, false))],
                "truncated" => [.. texts.Select(_ => new TextCrossEncoderScore(100, 300, true))],
                "overlength" => [.. texts.Select(_ => new TextCrossEncoderScore(100, 300, false))],
                _ => [.. texts.Select(_ => new TextCrossEncoderScore(100, -1, false))],
            },
        };
        using var retriever = CreateRetriever(fixture, generator, encoder);
        var baseline = await retriever.RetrieveAsync(Query(enabled: false));

        var result = await retriever.RetrieveAsync(Query() with
        {
            Options = new RepositoryMemoryOptions { RerankerEnabled = true, RerankerCandidateLimit = 1, RerankerMinimumScore = 1_000 },
        });

        Assert.Equal(
            baseline.Selected.Select(candidate => (candidate.Entry.Id, candidate.Entry.Revision, candidate.Score, candidate.LexicalRank, candidate.SemanticRank, candidate.CosineSimilarity)),
            result.Selected.Select(candidate => (candidate.Entry.Id, candidate.Entry.Revision, candidate.Score, candidate.LexicalRank, candidate.SemanticRank, candidate.CosineSimilarity)));
        Assert.All(result.Selected, candidate => Assert.Null(candidate.CrossEncoderScore));
        Assert.Contains(result.Diagnostics, message => message.Contains("using qualified hybrid ranking", StringComparison.Ordinal));
    }

    /// <summary>Degraded rankings are cached only within the owning turn and retried later.</summary>
    [Fact]
    public static async Task Failed_reranking_retries_on_a_later_turn()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = await SeedAsync(fixture);
        var encoder = new TestCrossEncoder { Score = (_, _) => throw new TextCrossEncoderUnavailableException("offline") };
        using var retriever = CreateRetriever(fixture, generator, encoder);
        var query = Query() with { UserTurnId = RunId.New() };
        var failed = await retriever.RetrieveAsync(query);
        encoder.Score = (_, texts) => texts.Select(_ => new TextCrossEncoderScore(2, 12, false)).ToArray();
        var sameTurn = await retriever.RetrieveAsync(query);
        var laterTurn = await retriever.RetrieveAsync(query with { UserTurnId = RunId.New() });

        Assert.All(failed.Selected, candidate => Assert.Null(candidate.CrossEncoderScore));
        Assert.True(sameTurn.RankingCacheHit);
        Assert.False(laterTurn.RankingCacheHit);
        Assert.All(laterTurn.Selected, candidate => Assert.Equal(2, candidate.CrossEncoderScore));
        Assert.Equal(2, encoder.Calls);
    }

    /// <summary>Callers without a turn identity retry unavailable reranking instead of retaining a degraded cache forever.</summary>
    [Fact]
    public static async Task Missing_turn_identity_does_not_cache_degraded_results()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = await SeedAsync(fixture);
        var encoder = new TestCrossEncoder { Score = (_, _) => throw new TextCrossEncoderUnavailableException("offline") };
        using var retriever = CreateRetriever(fixture, generator, encoder);
        await retriever.RetrieveAsync(Query());
        encoder.Score = (_, texts) => texts.Select(_ => new TextCrossEncoderScore(2, 12, false)).ToArray();

        var recovered = await retriever.RetrieveAsync(Query());

        Assert.False(recovered.RankingCacheHit);
        Assert.All(recovered.Selected, candidate => Assert.Equal(2, candidate.CrossEncoderScore));
        Assert.Equal(2, encoder.Calls);
    }

    /// <summary>Descriptor failures preserve hybrid retrieval and a disabled reranker never reads the descriptor.</summary>
    [Fact]
    public static async Task Descriptor_failure_falls_back_and_disabled_skips_descriptor_access()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = await SeedAsync(fixture);
        var encoder = new UnavailableDescriptorCrossEncoder();
        using var retriever = CreateRetriever(fixture, generator, encoder);
        var disabled = await retriever.RetrieveAsync(Query(enabled: false));
        Assert.Equal(0, encoder.DescriptorReads);

        var result = await retriever.RetrieveAsync(Query());

        Assert.Equal(1, encoder.DescriptorReads);
        Assert.Equal(
            disabled.Selected.Select(candidate => (candidate.Entry.Id, candidate.Entry.Revision, candidate.Score, candidate.LexicalRank, candidate.SemanticRank, candidate.CosineSimilarity)),
            result.Selected.Select(candidate => (candidate.Entry.Id, candidate.Entry.Revision, candidate.Score, candidate.LexicalRank, candidate.SemanticRank, candidate.CosineSimilarity)));
        Assert.Contains(result.Diagnostics, message => message.Contains("descriptor is unavailable", StringComparison.Ordinal));
    }

    /// <summary>Cross-encoder cancellation propagates without poisoning the ranking cache.</summary>
    [Fact]
    public static async Task Cancellation_propagates_and_a_retry_can_rerank()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = await SeedAsync(fixture);
        var encoder = new TestCrossEncoder { Score = (_, _) => throw new OperationCanceledException() };
        using var retriever = CreateRetriever(fixture, generator, encoder);

        await Assert.ThrowsAsync<OperationCanceledException>(() => retriever.RetrieveAsync(Query()));
        encoder.Score = (_, texts) => texts.Select(_ => new TextCrossEncoderScore(2, 12, false)).ToArray();
        var result = await retriever.RetrieveAsync(Query());

        Assert.Equal(3, result.Selected.Count);
        Assert.False(result.RankingCacheHit);
        Assert.Equal(2, encoder.Calls);
    }

    /// <summary>Empty, unqualified and context-disabled requests avoid native cross-encoder work.</summary>
    [Fact]
    public static async Task No_candidates_or_context_avoids_cross_encoder_work()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var encoder = new TestCrossEncoder { Score = (_, _) => throw new InvalidOperationException("must not run") };
        using var retriever = CreateRetriever(fixture, generator, encoder);
        Assert.Empty((await retriever.RetrieveAsync(Query())).Selected);
        var service = MemoryTestData.CreateService(fixture, generator);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "stored note"));
        Assert.Empty((await retriever.RetrieveAsync(Query() with
        {
            Options = new RepositoryMemoryOptions { RerankerEnabled = true, SemanticMinimum = 1 },
        })).Selected);
        Assert.Empty((await retriever.RetrieveAsync(Query() with
        {
            Options = new RepositoryMemoryOptions { RerankerEnabled = true, MaxRepoMemoriesInContext = 0 },
        })).Selected);
        Assert.Equal(0, encoder.Calls);
    }

    /// <summary>Optional composition without a cross-encoder remains usable when reranking is enabled.</summary>
    [Fact]
    public static async Task Missing_provider_retains_hybrid_results()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = await SeedAsync(fixture);
        using var retriever = CreateRetriever(fixture, generator, null);

        var result = await retriever.RetrieveAsync(Query());

        Assert.Equal(3, result.Selected.Count);
        Assert.Contains(result.Diagnostics, message => message.Contains("no cross-encoder", StringComparison.Ordinal));
    }

    private static async Task<TestMemoryEmbeddingGenerator> SeedAsync(ConversationFixture fixture)
    {
        var generator = new TestMemoryEmbeddingGenerator
        {
            Generate = text => new TextEmbeddingResult(text == "unrelated topic" ? (float[])[0, 1, 0] : (float[])[1, 0, 0], 12, false),
        };
        var service = MemoryTestData.CreateService(fixture, generator);
        foreach (var text in (string[])["first note", "second note", "third note", "unrelated topic"])
        {
            await service.ExecuteAsync(MemoryTestData.Operation("add", text));
        }

        return generator;
    }

    private static RepositoryMemoryRetrievalRequest Query(bool enabled = true) => new()
    {
        RepositoryIdentity = MemoryTestData.Repository,
        CurrentInstruction = "current request",
        Options = new RepositoryMemoryOptions { RerankerEnabled = enabled },
    };

    private static HybridRepositoryMemoryRetriever CreateRetriever(
        ConversationFixture fixture, ITextEmbeddingGenerator generator, ITextCrossEncoder? encoder) =>
        new(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator, encoder);

    private sealed class UnavailableDescriptorCrossEncoder : ITextCrossEncoder
    {
        public int DescriptorReads { get; private set; }

        public TextCrossEncoderModelDescriptor Model
        {
            get
            {
                DescriptorReads++;
                throw new TextCrossEncoderUnavailableException("descriptor unavailable");
            }
        }

        public Task<IReadOnlyList<TextCrossEncoderScore>> ScoreAsync(
            string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Inference must not run without a descriptor.");
    }

    private sealed class TestCrossEncoder : ITextCrossEncoder
    {
        public TextCrossEncoderModelDescriptor Model { get; set; } = new("test-cross-encoder", 256, 64);

        public Func<string, IReadOnlyList<string>, IReadOnlyList<TextCrossEncoderScore>> Score { get; set; } =
            (_, texts) => texts.Select(_ => new TextCrossEncoderScore(2, 12, false)).ToArray();

        public int Calls { get; private set; }

        public string? Query { get; private set; }

        public IReadOnlyList<string> Documents { get; private set; } = [];

        public Task<IReadOnlyList<TextCrossEncoderScore>> ScoreAsync(
            string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Query = query;
            Documents = documents.ToArray();
            return Task.FromResult(Score(query, documents));
        }
    }
}
