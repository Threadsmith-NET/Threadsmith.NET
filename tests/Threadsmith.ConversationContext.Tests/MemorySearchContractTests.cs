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
}
