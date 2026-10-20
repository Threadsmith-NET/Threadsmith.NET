namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Admission and write safety through real consumer boundaries.</summary>
public static class WeightedMemoryConsumerTests
{
    /// <summary>No score failure may authorize fallback recall or a new memory.</summary>
    [Theory]
    [InlineData("unavailable", RepositoryMemoryType.Situational)]
    [InlineData("truncated", RepositoryMemoryType.StandingPreference)]
    [InlineData("nan", RepositoryMemoryType.Situational)]
    public static async Task Failed_scoring_omits_recall_and_blocks_reconciliation(string failure, RepositoryMemoryType type)
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
        var scoring = new RepositoryMemoryScoringOptions { Mode = RepositoryMemoryScoringMode.Weighted, FinalThreshold = 0 };
        var request = new RepositoryMemorySearchRequest
        {
            RepositoryIdentity = MemoryTestData.Repository,
            Query = "alpha refined convention",
            IncludeStandingPreferences = true,
            Options = new() { RerankerEnabled = true, Scoring = scoring },
        };
        var result = await search.SearchAsync(request, ct);
        Assert.Empty(result.Selected);
        Assert.False(result.IsComplete);
        Assert.NotEqual(RepositoryMemorySearchBranchStatus.Completed, result.SearchDetails!.Scoring);
        Assert.All(result.SearchDetails.Candidates, row => Assert.Null(row.ScoreEvidence));
        var service = new RepositoryMemoryService(store, generator, new SecretOutputSanitizer(), search: search);
        var options = new RepositoryMemoryOptions { ReconciliationEnabled = true, ReconciliationRerankerEnabled = true, ReconciliationScoring = scoring };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(MemoryTestData.Operation("add", request.Query) with { Options = options }, ct));
        Assert.Equal(saved.Id, Assert.Single((await store.GetSnapshotAsync(MemoryTestData.Repository, [], ct)).Entries).Id);
        var duplicate = await service.ExecuteAsync(MemoryTestData.Operation("add", "alpha convention") with { Options = options, MemoryType = type }, ct);
        Assert.Equal("duplicate", duplicate.Outcome);
        Assert.Equal(saved.Id, duplicate.Id);
    }

    /// <summary>Budget admission cannot restore rejected entries; every admitted weighted item carries evidence.</summary>
    [Theory]
    [InlineData(RepositoryMemoryOwnership.Ordinary)]
    [InlineData(RepositoryMemoryOwnership.Intelligence)]
    public static async Task Context_preserves_threshold_and_standing_preferences(RepositoryMemoryOwnership ownership)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        using var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        var options = new RepositoryMemoryOptions
        {
            RerankerEnabled = true,
            MaximumDiagnostics = 1,
            RecallScoring = new() { Mode = RepositoryMemoryScoringMode.Weighted, FinalThreshold = .6 },
            Intelligence = new() { Enabled = true, Recall = new() { RerankerEnabled = true, MaximumDiagnostics = 1, Scoring = new() { Mode = RepositoryMemoryScoringMode.Weighted, FinalThreshold = .6 } } },
        };
        foreach (var text in (string[])["alpha relevant", "alpha irrelevant", "standing preference"])
        {
            var write = new RepositoryMemoryWrite
            {
                Text = text,
                Origin = RepositoryMemoryOrigin.Manual,
                Ownership = text == "standing preference" ? RepositoryMemoryOwnership.Ordinary : ownership,
                MemoryType = text == "standing preference" ? RepositoryMemoryType.StandingPreference : RepositoryMemoryType.Situational,
            };
            await store.AddAsync(MemoryTestData.Repository, write, generator.Model, await generator.GenerateAsync(text, ct), options, ct);
        }

        using var retriever = new HybridRepositoryMemoryRetriever(store, generator, new MemoryScoreEncoder { Score = (_, text) => new(text == "alpha relevant" ? 1 : 0, 12, false) { QueryTokenCount = 3, UsedQueryTokenCount = 3 } });
        await using var events = new Threadsmith.Execution.DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var assembler = new ContextAssembler(new EvidenceStore(events, sanitizer), new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, events, TestPromptLoader.Instance, repositoryMemoryRetriever: retriever);
        var request = new ContextAssemblyRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            Phase = RunPhase.EvidenceCollection,
            RepositoryPath = fixture.DirectoryPath,
            RepositoryIdentity = MemoryTestData.Repository,
            Task = new TaskSpecification("alpha", []),
            IntelligenceEnabled = true,
            RepositoryMemoryOptions = options,
        };
        var admitted = await assembler.AssembleAsync(request, ct);
        Assert.Equal(3, admitted.Inspection.RepositoryMemoryItems.Count(item => item.Included));
        Assert.All(admitted.Inspection.RepositoryMemoryItems.Where(item => item.Included && item.MemoryType == RepositoryMemoryType.Situational), item => Assert.True(item.ScoreEvidence!.Accepted));
        // Raise the cutoff above equal-CE+retrieval evidence. Configuration must invalidate cached selection.
        var stricter = options with
        {
            RecallScoring = options.RecallScoring with { FinalThreshold = .9 },
            Intelligence = options.Intelligence with { Recall = options.Intelligence.Recall with { Scoring = options.Intelligence.Recall.Scoring with { FinalThreshold = .9 } } },
        };
        var changed = await assembler.AssembleAsync(request with { RepositoryMemoryOptions = stricter }, ct);
        Assert.DoesNotContain(changed.Inspection.RepositoryMemoryItems, item => item.Included && item.Text == "alpha irrelevant");
        Assert.Contains(changed.Inspection.RepositoryMemoryItems, item => item.Included && item.Text == "standing preference");
        Assert.All(changed.Inspection.RepositoryMemoryItems.Where(item => item.Included && item.MemoryType == RepositoryMemoryType.Situational), item => Assert.True(item.ScoreEvidence!.FinalScore >= .9));
        var rejectionRequest = new RepositoryMemorySearchRequest
        {
            RepositoryIdentity = MemoryTestData.Repository,
            Ownership = ownership,
            Query = "unmatched",
            Options = new() { RerankerEnabled = true, Scoring = new() { Mode = RepositoryMemoryScoringMode.Weighted, FinalThreshold = 1 } },
        };
        var rejected = await retriever.SearchAsync(rejectionRequest, ct);
        Assert.Empty(rejected.Selected);
        Assert.Equal(3, rejected.SearchDetails!.RerankerQueryTokens);
        Assert.Equal(3, rejected.SearchDetails.RerankerUsedQueryTokens);
    }
}
