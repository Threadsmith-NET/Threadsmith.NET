namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Xunit;

/// <summary>Numerical and shared-store weighted admission contracts.</summary>
public static class WeightedMemoryScoringTests
{
    /// <summary>Checks complete-pool numerical evidence.</summary>
    [Fact]
    public static void Fusion_matches_hand_calculation_and_ownership_isolation()
    {
        var rows = new[] { Candidate(1, .8, -4, -3), Candidate(2, .4, -2, -1), Candidate(3, -.2, null, 1) };
        var options = new RepositoryMemoryScoringOptions { Mode = RepositoryMemoryScoringMode.Weighted, FinalThreshold = .5 };
        var other = Candidate(4, 1, -100, 100);
        other = other with { Entry = other.Entry with { Ownership = RepositoryMemoryOwnership.Intelligence } };
        var scored = HybridRepositoryMemoryRetriever.FuseWeightedScores([.. rows.Reverse(), other], options);
        var ordinary = scored.Where(row => row.Entry.Ownership == RepositoryMemoryOwnership.Ordinary).ToArray();
        Assert.Equal([.75, .5, .25], ordinary.Select(row => row.Score));
        Assert.Equal([true, true, false], ordinary.Select(row => row.ScoreEvidence!.Accepted));
        Assert.Equal([1d, .5, 0], ordinary.Select(row => row.ScoreEvidence!.Semantic));
        Assert.Equal([1d, .5, 0], ordinary.Select(row => row.ScoreEvidence!.Lexical));
        Assert.Equal([0d, .5, 1], ordinary.Select(row => row.ScoreEvidence!.CrossEncoder));
        Assert.Null(ordinary[2].ScoreEvidence!.RawLexical);
        Assert.Equal(-.2, ordinary[2].ScoreEvidence!.RawSemantic);
        Assert.Equal(HybridRepositoryMemoryRetriever.FuseWeightedScores(rows, options), ordinary);
    }

    /// <summary>Checks complete-pool numerical evidence.</summary>
    [Fact]
    public static void Empty_singleton_equal_zero_and_extreme_scores_have_defined_results()
    {
        var options = new RepositoryMemoryScoringOptions { Mode = RepositoryMemoryScoringMode.Weighted, FinalThreshold = .1 };
        Assert.Empty(HybridRepositoryMemoryRetriever.FuseWeightedScores([], options));
        var concept = Candidate(1, null, null, 7) with { ConceptMatches = ["hint"] };
        var singleton = Assert.Single(HybridRepositoryMemoryRetriever.FuseWeightedScores([concept], options));
        Assert.Equal(0, singleton.Score);
        Assert.False(singleton.ScoreEvidence!.Accepted);
        var tied = HybridRepositoryMemoryRetriever.FuseWeightedScores([Candidate(3, -.2, 1, 7), Candidate(2, 0, 0, 7), concept], options);
        Assert.All(tied, row => Assert.Equal(0, row.Score));
        Assert.Equal([1, 2, 3], tied.Select(row => int.Parse(row.Entry.Text)));
        var extremes = HybridRepositoryMemoryRetriever.FuseWeightedScores([Candidate(1, null, null, -double.MaxValue), Candidate(2, null, null, double.MaxValue)], options);
        Assert.Equal([.25, 0], extremes.Select(row => row.Score));
    }

    /// <summary>Checks validity and execution invariance.</summary>
    [Theory]
    [InlineData(double.NaN, -1, 1)]
    [InlineData(.5, double.NegativeInfinity, 1)]
    [InlineData(.5, -1, double.PositiveInfinity)]
    public static void Invalid_evidence_is_failure_not_zero(double semantic, double lexical, double ce)
    {
        Assert.Throws<InvalidOperationException>(() => HybridRepositoryMemoryRetriever.FuseWeightedScores(
            [Candidate(1, semantic, lexical, ce)], new() { Mode = RepositoryMemoryScoringMode.Weighted, FinalThreshold = 0 }));
    }

    /// <summary>Checks validity and execution invariance.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(17)]
    public static async Task Complete_pool_scores_do_not_depend_on_batch_boundaries_or_TopN(int count)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        using var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        for (var index = 0; index < count; index++)
        {
            var text = $"memory {index}";
            await store.AddAsync(MemoryTestData.Repository, new() { Text = text, Origin = RepositoryMemoryOrigin.Manual }, generator.Model, await generator.GenerateAsync(text, ct), new(), ct);
        }

        var request = new RepositoryMemorySearchRequest
        {
            RepositoryIdentity = MemoryTestData.Repository,
            Query = "memory",
            Options = new() { RerankerEnabled = true, RerankerCandidateLimit = 1, MaximumResults = 1, Scoring = new() { Mode = RepositoryMemoryScoringMode.Weighted, FinalThreshold = .2 } },
        };
        using var singles = new HybridRepositoryMemoryRetriever(store, generator, new BatchEncoder { BatchSize = 1 });
        using var batches = new HybridRepositoryMemoryRetriever(store, generator, new BatchEncoder { BatchSize = 8 });
        var first = await singles.SearchAsync(request, ct);
        var second = await batches.SearchAsync(request with { Options = request.Options with { MaximumResults = 20 } }, ct);
        Assert.Equal(count, first.SearchDetails!.ComparedCandidates);
        Assert.Equal(RepositoryMemorySearchBranchStatus.Completed, first.SearchDetails.Scoring);
        Assert.Equal(first.SearchDetails.Candidates.Select(row => (row.Id, row.ScoreEvidence)), second.SearchDetails!.Candidates.Select(row => (row.Id, row.ScoreEvidence)));
        Assert.Equal(second.Selected[0].Entry.Id, Assert.Single(first.Selected).Entry.Id);
        Assert.All(second.Selected, row => Assert.True(row.ScoreEvidence!.Accepted));
    }

    private static RepositoryMemoryRetrievalCandidate Candidate(int id, double? semantic, double? lexical, double ce)
    {
        var entry = new RepositoryMemoryEntry
        {
            Id = new(new Guid(id, 0, 0, new byte[8])),
            RepositoryIdentity = "fixture",
            Text = id.ToString(),
            ContentHash = "fixture",
            Origin = RepositoryMemoryOrigin.Manual,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
        return new(entry, 0, lexical is null ? null : 1, semantic is null ? null : 1, semantic) { LexicalScore = lexical, CrossEncoderScore = ce };
    }

    private sealed class BatchEncoder : ITextCrossEncoder
    {
        public int BatchSize { get; init; }

        public TextCrossEncoderModelDescriptor Model => new("weighted-fixture", 512, BatchSize);

        public Task<IReadOnlyList<TextCrossEncoderScore>> ScoreAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.InRange(documents.Count, 1, BatchSize);
            return Task.FromResult<IReadOnlyList<TextCrossEncoderScore>>(documents.Select(text => new TextCrossEncoderScore(int.Parse(text.Split(' ')[1]), 12, false)).ToArray());
        }
    }
}
