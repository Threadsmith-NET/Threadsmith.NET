namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Xunit;

/// <summary>Search limits and execution evidence are independent of the consumer's context policy.</summary>
public static class MemorySearchContractTests
{
    /// <summary>Result and comparison windows report separate omissions and share ranking cache evidence.</summary>
    [Fact]
    public static async Task Independent_search_limits_report_comparison_and_result_omissions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        for (var index = 0; index < 4; index++)
        {
            await service.ExecuteAsync(MemoryTestData.Operation("add", "cancellation convention " + index), ct);
        }

        using var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        using var search = new HybridRepositoryMemoryRetriever(store, generator, new MemoryScoreEncoder());
        var request = new RepositoryMemorySearchRequest
        {
            RepositoryIdentity = MemoryTestData.Repository,
            Query = "cancellation",
            Options = new RepositoryMemorySearchOptions { SemanticMinimum = 1, RerankerEnabled = true, RerankerCandidateLimit = 3, MaximumResults = 2 },
        };
        var result = await search.SearchAsync(request, ct);
        var details = Assert.IsType<RepositoryMemorySearchDetails>(result.SearchDetails);
        Assert.True(result.IsComplete);
        Assert.Equal(2, result.Selected.Count);
        Assert.Equal(RepositoryMemorySearchBranchStatus.Completed, details.Lexical);
        Assert.Equal(RepositoryMemorySearchBranchStatus.Completed, details.Semantic);
        Assert.Equal(RepositoryMemorySearchBranchStatus.Completed, details.Reranker);
        Assert.Equal(4, details.HybridCandidates);
        Assert.Equal(3, details.ComparedCandidates);
        Assert.Equal(1, details.CandidateWindowOmissions);
        Assert.Equal(1, details.ResultLimitOmissions);
        var cached = await search.SearchAsync(request, ct);
        Assert.True(cached.RankingCacheHit);
        Assert.Equal(details, cached.SearchDetails);

        var configuration = new RepositoryMemoryOptions
        {
            MaxNumberOfRepoMemories = 1,
            MaxRepoMemoriesInContext = 0,
            ReconciliationEnabled = true,

            ReconciliationSemanticMinimum = 1,
            ReconciliationCandidateLimit = 3,
        };
        Assert.Equal(0, RepositoryMemorySearchOptions.ForRecall(configuration).MaximumResults);
        var reconciled = await search.SearchAsync(request with { Options = RepositoryMemorySearchOptions.ForReconciliation(configuration) }, ct);
        Assert.Equal(3, reconciled.Selected.Count);
        Assert.Equal(1, reconciled.SearchDetails?.CandidateWindowOmissions);
        Assert.Equal(0, reconciled.SearchDetails?.ResultLimitOmissions);
    }

    /// <summary>Branch policy and complete-query provenance cannot reuse stale ranking evidence.</summary>
    [Fact]
    public static async Task Ranking_cache_separates_branch_policy_and_query_completeness()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "cancellation convention"), ct);
        using var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        using var search = new HybridRepositoryMemoryRetriever(store, generator, new MemoryScoreEncoder());
        var request = new RepositoryMemorySearchRequest
        {
            RepositoryIdentity = MemoryTestData.Repository,
            Query = "cancellation",
            Options = new RepositoryMemorySearchOptions { SemanticMinimum = 1, RerankerEnabled = true },
        };
        var initial = await search.SearchAsync(request, ct);
        var concepts = await search.SearchAsync(request with { Options = request.Options with { ConceptRecallEnabled = true, } }, ct);
        Assert.False(concepts.RankingCacheHit);
        Assert.Equal(RepositoryMemorySearchBranchStatus.Completed, concepts.SearchDetails?.Concepts);
        Assert.Equal(RepositoryMemorySearchBranchStatus.Disabled, initial.SearchDetails?.Concepts);
        var bounded = await search.SearchAsync(request with { QueryBounded = true }, ct);
        Assert.False(bounded.RankingCacheHit);
        Assert.False(bounded.IsComplete);
        Assert.Equal(RepositoryMemorySearchBranchStatus.Incomplete, bounded.SearchDetails?.Semantic);
        Assert.True((await search.SearchAsync(request, ct)).IsComplete);
        var selected = Assert.Single(initial.Selected);
        var retained = await search.SearchAsync(
            request with
            {
                Options = request.Options with { ConceptRecallEnabled = true, },
                RetainedMemories = [new(selected.Entry.Id, selected.Entry.Revision)],
            },
            ct);
        var retainedCandidate = Assert.Single(retained.Selected);
        Assert.Equal(selected.Entry.Id, retainedCandidate.Entry.Id);
        Assert.Equal(selected.Score, retainedCandidate.Score);
        Assert.Equal(selected.LexicalRank, retainedCandidate.LexicalRank);
        Assert.Equal(selected.SemanticRank, retainedCandidate.SemanticRank);
        Assert.Equal(selected.CrossEncoderScore, retainedCandidate.CrossEncoderScore);
        Assert.Equal(selected.ConceptMatches, retainedCandidate.ConceptMatches);
    }

    /// <summary>Unavailable scoring and incomplete pairs preserve ordinary recall without claiming a complete check.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task Scoring_outcomes_are_structured_and_degraded_results_do_not_become_complete(bool truncated)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "cancellation convention"), ct);
        using var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        var encoder = new MemoryScoreEncoder { Score = (_, _) => new(2, 257, true) };
        using var search = new HybridRepositoryMemoryRetriever(store, generator, truncated ? encoder : null);
        var result = await search.SearchAsync(
            new RepositoryMemorySearchRequest
            {
                RepositoryIdentity = MemoryTestData.Repository,
                Query = "cancellation",
                Options = new RepositoryMemorySearchOptions { SemanticMinimum = 1, RerankerEnabled = true },
            },
            ct);
        Assert.False(result.IsComplete);
        Assert.Single(result.Selected);
        var details = Assert.IsType<RepositoryMemorySearchDetails>(result.SearchDetails);
        Assert.Equal(truncated ? RepositoryMemorySearchBranchStatus.Incomplete : RepositoryMemorySearchBranchStatus.Unavailable, details.Reranker);
        Assert.Equal(truncated, details.PairTruncated);
    }
}
