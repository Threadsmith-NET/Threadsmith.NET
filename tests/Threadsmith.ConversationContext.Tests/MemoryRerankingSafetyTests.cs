namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Admission and write safety through real consumer boundaries.</summary>
public static class MemoryRerankingSafetyTests
{
    /// <summary>Reranker failure preserves qualified baseline recall but cannot authorize a new memory.</summary>
    [Theory]
    [InlineData("unavailable", RepositoryMemoryType.Situational)]
    [InlineData("truncated", RepositoryMemoryType.StandingPreference)]
    [InlineData("nan", RepositoryMemoryType.Situational)]
    public static async Task Failed_reranking_preserves_baseline_recall_and_blocks_reconciliation(string failure, RepositoryMemoryType type)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var baseline = MemoryTestData.CreateService(fixture, generator);
        var saved = await baseline.ExecuteAsync(MemoryTestData.Operation("add", "alpha convention") with { MemoryType = type }, ct);
        using var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        var encoder = new MemoryScoreEncoder
        {
            Score = (_, _) => failure == "unavailable" ? throw new InvalidOperationException("fixture failure")
                : new(failure == "nan" ? double.NaN : 1, 20, failure == "truncated"),
        };
        using var search = new HybridRepositoryMemoryRetriever(store, generator, encoder);
        var request = new RepositoryMemorySearchRequest
        {
            RepositoryIdentity = MemoryTestData.Repository,
            Query = "alpha refined convention",
            IncludeStandingPreferences = true,
            Options = new() { RerankerEnabled = true },
        };
        var result = await search.SearchAsync(request, ct);
        Assert.Equal(saved.Id, Assert.Single(result.Selected).Entry.Id);
        Assert.False(result.IsComplete);
        Assert.NotEqual(RepositoryMemorySearchBranchStatus.Completed, result.SearchDetails!.Reranker);
        Assert.All(result.Selected, row => Assert.Null(row.CrossEncoderScore));
        var service = new RepositoryMemoryService(store, generator, new SecretOutputSanitizer(), search: search);
        var options = new RepositoryMemoryOptions { ReconciliationEnabled = true, ReconciliationRerankerEnabled = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(MemoryTestData.Operation("add", request.Query) with { Options = options }, ct));
        Assert.Equal(saved.Id, Assert.Single((await store.GetSnapshotAsync(MemoryTestData.Repository, [], ct)).Entries).Id);
        var duplicate = await service.ExecuteAsync(MemoryTestData.Operation("add", "alpha convention") with { Options = options, MemoryType = type }, ct);
        Assert.Equal("duplicate", duplicate.Outcome);
        Assert.Equal(saved.Id, duplicate.Id);
    }
}
