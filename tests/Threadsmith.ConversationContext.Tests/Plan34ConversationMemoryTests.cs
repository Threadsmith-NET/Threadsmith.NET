namespace Threadsmith.ConversationContext.Tests;

using Microsoft.Data.Sqlite;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Xunit;

/// <summary>Plan 103 retrieval contracts replacing automatic conversation-fact promotion and overlap scoring.</summary>
public static class Plan34ConversationMemoryTests
{
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
        Assert.Empty(atBoundary.Selected);
        var explicitSearch = await retriever.SearchAsync(new RepositoryMemorySearchRequest
        {
            RepositoryIdentity = MemoryTestData.Repository,
            Query = "alpha",
            Options = new RepositoryMemorySearchOptions { SemanticMinimum = 1 },
        });
        var lexicalAtBoundary = Assert.Single(explicitSearch.Selected);
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

    /// <summary>Embedding-space changes rebuild complete vectors; failed rebuilds cannot enter automatic recall.</summary>
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

        Assert.Empty(result.Selected);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("rebuild failed", StringComparison.Ordinal));
        Assert.Equal("semantic scoring unavailable", Assert.Single(result.SearchDetails!.Candidates).Outcome);
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

    /// <summary>An unreadable memory snapshot fails clearly because standing preferences cannot be recovered.</summary>
    [Fact]
    public static async Task Store_failure_reports_unavailable_standing_preferences()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DROP TABLE managed_memory_repositories;";
        await command.ExecuteNonQueryAsync();
        using var retriever = CreateRetriever(fixture, new TestMemoryEmbeddingGenerator());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => retriever.RetrieveAsync(Query("query")));

        Assert.Contains("standing preferences cannot be assembled", failure.Message, StringComparison.Ordinal);
        Assert.IsType<SqliteException>(failure.InnerException);
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

        Assert.Empty(degraded.Selected);
        Assert.Empty(sameTurn.Selected);
        Assert.True(sameTurn.RankingCacheHit);
        Assert.NotNull(Assert.Single(recovered.Selected).SemanticRank);
        Assert.Equal(3, generator.Calls);
    }

    private static RepositoryMemoryRetrievalRequest Query(string text)
    {
        return new()
        {
            RepositoryIdentity = MemoryTestData.Repository,
            CurrentInstruction = text,
            Options = new RepositoryMemoryOptions { SemanticMinimum = 0.8 },
        };
    }

    private static HybridRepositoryMemoryRetriever CreateRetriever(ConversationFixture fixture, ITextEmbeddingGenerator generator)
    {
        return new(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator);
    }
}
