namespace Threadsmith.ConversationContext.Tests;

using Microsoft.Data.Sqlite;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Xunit;

/// <summary>Plan 103 retrieval contracts replacing automatic conversation-fact promotion and overlap scoring.</summary>
public static class Plan34ConversationMemoryTests
{
    /// <summary>Both qualified branches fuse while unrelated memories never fill the requested maximum.</summary>
    [Fact]
    public static async Task Hybrid_preserves_lexical_and_semantic_only_positives()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = CreateGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        var both = await service.ExecuteAsync(MemoryTestData.Operation("add", "alpha beta shared"));
        var lexical = await service.ExecuteAsync(MemoryTestData.Operation("add", "alpha beta lexical"));
        var semantic = await service.ExecuteAsync(MemoryTestData.Operation("add", "paraphrase"));
        await service.ExecuteAsync(MemoryTestData.Operation("add", "unrelated"));
        using var retriever = CreateRetriever(fixture, generator);

        var result = await retriever.RetrieveAsync(Query("alpha beta"));

        Assert.Equal(3, result.Selected.Count);
        Assert.Equal(both.Id, result.Selected[0].Entry.Id);
        Assert.Contains(result.Selected, candidate => candidate.Entry.Id == lexical.Id && candidate.LexicalRank is not null && candidate.SemanticRank is null);
        Assert.Contains(result.Selected, candidate => candidate.Entry.Id == semantic.Id && candidate.LexicalRank is null && candidate.SemanticRank is not null);
        Assert.DoesNotContain(result.Selected, candidate => candidate.Entry.Text == "unrelated");
    }

    /// <summary>Lexical fallback applies exact minimum-term qualification, including Unicode and FTS punctuation.</summary>
    [Theory]
    [InlineData("alpha beta", 1)]
    [InlineData("alpha", 2)]
    [InlineData("\"alpha\" OR (beta*)", 1)]
    [InlineData("café dépôt", 1)]
    [InlineData("東京", 1)]
    [InlineData("the and with", 0)]
    [InlineData("*** : ()", 0)]
    [InlineData("", 0)]
    public static async Task Lexical_fallback_qualifies_safe_terms(string query, int expected)
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = CreateGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "alpha beta"));
        await service.ExecuteAsync(MemoryTestData.Operation("add", "alpha alone"));
        await service.ExecuteAsync(MemoryTestData.Operation("add", "cafe depot 東京"));
        generator.Generate = _ => throw new InvalidOperationException("offline");
        using var retriever = CreateRetriever(fixture, generator);

        var result = await retriever.RetrieveAsync(Query(query));

        Assert.Equal(expected, result.Selected.Count);
        Assert.All(result.Selected, candidate => Assert.Null(candidate.SemanticRank));
        if (query.Length > 0)
        {
            Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("lexical matches only", StringComparison.Ordinal));
        }
    }

    /// <summary>Unchanged rounds reuse query embeddings and rankings, while writes invalidate only ranking.</summary>
    [Fact]
    public static async Task Cache_reuses_query_but_mutations_refresh_rankings()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = CreateGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        var saved = await service.ExecuteAsync(MemoryTestData.Operation("add", "alpha beta shared"));
        using var retriever = CreateRetriever(fixture, generator);
        var first = await retriever.RetrieveAsync(Query("alpha beta"));
        var cached = await retriever.RetrieveAsync(Query("alpha beta"));
        var entry = Assert.Single(first.Selected).Entry;
        await service.RecordInclusionsAsync(MemoryTestData.Repository, RunId.New(), [new RepositoryMemoryInclusion(entry.Id, entry.Revision)]);
        var afterUsage = await retriever.RetrieveAsync(Query("alpha beta"));
        await service.ExecuteAsync(MemoryTestData.Operation("remove", id: saved.Id));
        var afterDelete = await retriever.RetrieveAsync(Query("alpha beta"));

        Assert.True(cached.QueryEmbeddingCacheHit);
        Assert.True(cached.RankingCacheHit);
        Assert.True(afterUsage.RankingCacheHit);
        Assert.False(afterDelete.RankingCacheHit);

        Assert.Empty(afterDelete.Selected);
        Assert.Equal(2, generator.Calls);
    }

    /// <summary>A request-local cutoff reranks an unchanged query without repeating inference.</summary>
    [Fact]
    public static async Task Semantic_minimum_reranks_cached_query_without_reembedding()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator
        {
            Generate = text => text == "semantic only" ? new TextEmbeddingResult((float[])[0.6f, 0.8f, 0], 3, false) : new TextEmbeddingResult((float[])[1, 0, 0], 3, false),
        };
        var service = MemoryTestData.CreateService(fixture, generator);
        var saved = await service.ExecuteAsync(MemoryTestData.Operation("add", "semantic only"));
        using var retriever = CreateRetriever(fixture, generator);

        var included = await retriever.RetrieveAsync(Query("query") with { Options = new RepositoryMemoryOptions { SemanticMinimum = 0.5 } });
        var excluded = await retriever.RetrieveAsync(Query("query") with { Options = new RepositoryMemoryOptions { SemanticMinimum = 0.7 } });

        Assert.Equal(saved.Id, Assert.Single(included.Selected).Entry.Id);
        Assert.False(included.RankingCacheHit);
        Assert.Empty(excluded.Selected);
        Assert.True(excluded.QueryEmbeddingCacheHit);
        Assert.False(excluded.RankingCacheHit);
        Assert.Equal(2, generator.Calls);
    }

    /// <summary>Semantic qualification is strict while lexical qualification remains eligible.</summary>
    [Fact]
    public static async Task Semantic_minimum_excludes_equal_similarity_without_suppressing_lexical_matches()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        var semantic = await service.ExecuteAsync(MemoryTestData.Operation("add", "semantic only"));
        var lexical = await service.ExecuteAsync(MemoryTestData.Operation("add", "alpha lexical"));
        using var retriever = CreateRetriever(fixture, generator);

        var belowBoundary = await retriever.RetrieveAsync(Query("alpha") with { Options = new RepositoryMemoryOptions { SemanticMinimum = 0.999 } });
        var atBoundary = await retriever.RetrieveAsync(Query("alpha") with { Options = new RepositoryMemoryOptions { SemanticMinimum = 1 } });

        Assert.Contains(belowBoundary.Selected, candidate => candidate.Entry.Id == semantic.Id && candidate.SemanticRank is not null);
        var lexicalAtBoundary = Assert.Single(atBoundary.Selected);
        Assert.Equal(lexical.Id, lexicalAtBoundary.Entry.Id);
        Assert.NotNull(lexicalAtBoundary.LexicalRank);
        Assert.Null(lexicalAtBoundary.SemanticRank);
    }

    /// <summary>Semantic cutoffs must remain finite cosine bounds.</summary>
    [Fact]
    public static void Semantic_minimum_rejects_nonfinite_and_out_of_range_values()
    {
        foreach (var value in (double[])[double.NaN, double.NegativeInfinity, double.PositiveInfinity, -1.0000001, 1.0000001])
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RepositoryMemoryOptions { SemanticMinimum = value }.Validate());
        }
    }

    /// <summary>Equal branch scores use stable IDs rather than usage, origin, or age.</summary>
    [Fact]
    public static async Task Stable_ties_ignore_usage_and_origin()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        var first = await service.ExecuteAsync(MemoryTestData.Operation("add", "first paraphrase"));
        var second = await service.ExecuteAsync(MemoryTestData.Operation("add", "second paraphrase") with { Origin = RepositoryMemoryOrigin.Model });
        using var retriever = CreateRetriever(fixture, generator);

        var result = await retriever.RetrieveAsync(Query("unmatched exact query"));

        Assert.Equal(new[] { first.Id, second.Id }.OrderBy(id => id?.Value), result.Selected.Select(candidate => (RepositoryMemoryId?)candidate.Entry.Id));
        Assert.All(result.Selected, candidate => Assert.Null(candidate.LexicalRank));
    }

    /// <summary>Query truncation is inspectable and accepted only for queries, never persisted content.</summary>
    [Fact]
    public static async Task Query_truncation_is_visible_and_current_instruction_has_priority()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "stored note"));
        string? observed = null;
        generator.Generate = text =>
        {
            observed = text;
            return new TextEmbeddingResult(new float[] { 1, 0, 0 }, 1_000, true);
        };
        using var retriever = CreateRetriever(fixture, generator);

        var result = await retriever.RetrieveAsync(Query("current steering") with { TaskIntent = new string('x', 10_000) });

        Assert.True(result.QueryTruncated);
        Assert.StartsWith("current steering\n", observed, StringComparison.Ordinal);
        Assert.Equal(8_000, observed?.Length);
    }

    /// <summary>Embedding-space changes rebuild complete vectors, while unsupported rebuilds remain lexical-only.</summary>
    [Fact]
    public static async Task Incompatible_space_rebuild_failure_never_mixes_vectors()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "alpha beta saved"));
        generator.Model = new TextEmbeddingModelDescriptor("new-space", 3, 256);
        generator.Generate = text => text == "alpha beta saved"
            ? throw new InvalidOperationException("cannot rebuild")
            : new TextEmbeddingResult(new float[] { 1, 0, 0 }, 3, false);
        using var retriever = CreateRetriever(fixture, generator);

        var result = await retriever.RetrieveAsync(Query("alpha beta"));

        Assert.Null(Assert.Single(result.Selected).SemanticRank);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("rebuild failed", StringComparison.Ordinal));
    }

    /// <summary>Cancellation during inference propagates and leaves no poisoned ranking cache.</summary>
    [Fact]
    public static async Task Cancellation_propagates_and_retry_can_complete()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "saved note"));
        generator.Generate = _ => throw new OperationCanceledException();
        using var retriever = CreateRetriever(fixture, generator);

        await Assert.ThrowsAsync<OperationCanceledException>(() => retriever.RetrieveAsync(Query("query")));
        generator.Generate = _ => new TextEmbeddingResult(new float[] { 1, 0, 0 }, 3, false);
        var retried = await retriever.RetrieveAsync(Query("query"));

        Assert.Single(retried.Selected);
        Assert.False(retried.RankingCacheHit);
    }

    /// <summary>A failed SQLite search omits memories without aborting the main conversation.</summary>
    [Fact]
    public static async Task Store_failure_omits_memory_with_diagnostic()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE managed_memory_repositories;";
        await command.ExecuteNonQueryAsync();
        using var retriever = CreateRetriever(fixture, new TestMemoryEmbeddingGenerator());

        var result = await retriever.RetrieveAsync(Query("query"));

        Assert.Empty(result.Selected);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("search failed", StringComparison.Ordinal));
    }

    /// <summary>Unavailable semantics are cached only for the same turn and recover on a later turn.</summary>
    [Fact]
    public static async Task Degraded_result_reuses_same_turn_and_recovers_next_turn()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "alpha beta saved"));
        generator.Generate = _ => throw new InvalidOperationException("offline");
        using var retriever = CreateRetriever(fixture, generator);
        var query = Query("alpha beta") with { UserTurnId = RunId.New() };
        var degraded = await retriever.RetrieveAsync(query);
        generator.Generate = _ => new TextEmbeddingResult(new float[] { 1, 0, 0 }, 3, false);

        var sameTurn = await retriever.RetrieveAsync(query);
        var recovered = await retriever.RetrieveAsync(query with { UserTurnId = RunId.New() });

        Assert.Null(Assert.Single(degraded.Selected).SemanticRank);
        Assert.True(sameTurn.RankingCacheHit);
        Assert.NotNull(Assert.Single(recovered.Selected).SemanticRank);
        Assert.Equal(3, generator.Calls);
    }

    /// <summary>An empty repository does not load or invoke local inference.</summary>
    [Fact]
    public static async Task Empty_repository_avoids_embedding_work()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator { Generate = _ => throw new InvalidOperationException("offline") };
        using var retriever = CreateRetriever(fixture, generator);

        var result = await retriever.RetrieveAsync(Query("current request"));

        Assert.Empty(result.Selected);
        Assert.Equal(0, generator.Calls);
    }

    private static RepositoryMemoryRetrievalRequest Query(string text) => new()
    {
        RepositoryIdentity = MemoryTestData.Repository,
        CurrentInstruction = text,
        Options = new RepositoryMemoryOptions { SemanticMinimum = 0.8 },
    };

    private static HybridRepositoryMemoryRetriever CreateRetriever(ConversationFixture fixture, ITextEmbeddingGenerator generator) =>
        new(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator);

    private static TestMemoryEmbeddingGenerator CreateGenerator() => new()
    {
        Generate = text => new TextEmbeddingResult(
            text is "paraphrase" or "alpha beta shared" or "alpha beta" ? (float[])[1, 0, 0] : (float[])[0, 1, 0],
            4,
            false),
    };
}
