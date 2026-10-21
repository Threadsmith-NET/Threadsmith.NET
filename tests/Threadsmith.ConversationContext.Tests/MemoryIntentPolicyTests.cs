namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Xunit;

/// <summary>Eligibility and provenance contracts through the shared search path.</summary>
public static class MemoryIntentPolicyTests
{
    /// <summary>Verifies independent memory routing and intent eligibility.</summary>
    [Theory]
    [InlineData(SituationalConceptFilterMode.Off, 3)]
    [InlineData(SituationalConceptFilterMode.Shadow, 3)]
    [InlineData(SituationalConceptFilterMode.Enforce, 2)]
    public static async Task Filter_precedes_comparison_slots_and_preserves_fallbacks(SituationalConceptFilterMode mode, int expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        var match = await service.ExecuteAsync(MemoryTestData.Operation("add", "compiler errors match") with { Concepts = ["compiler"] }, ct);
        var mismatch = await service.ExecuteAsync(MemoryTestData.Operation("add", "compiler errors distractor") with { Concepts = ["logging"] }, ct);
        var untagged = await service.ExecuteAsync(MemoryTestData.Operation("add", "compiler errors fallback"), ct);
        var encoder = new MemoryScoreEncoder();
        using var search = new HybridRepositoryMemoryRetriever(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator, encoder);
        var turn = RunId.New();
        var request = Request(turn) with { SituationalConceptFilterMode = mode };
        var result = await search.SearchAsync(request, ct);
        Assert.Equal(expected, result.Selected.Count);
        Assert.Equal(expected, encoder.Documents.Count);
        Assert.Contains(result.Selected, item => item.Entry.Id == match.Id);
        Assert.Contains(result.Selected, item => item.Entry.Id == untagged.Id);
        Assert.Equal(mode != SituationalConceptFilterMode.Enforce, result.Selected.Any(item => item.Entry.Id == mismatch.Id));
        if (mode != SituationalConceptFilterMode.Off)
        {
            Assert.Contains(result.SearchDetails!.Candidates, item => item.Id == mismatch.Id && item.ConceptPolicyDecision == "concept_nonmatch_policy");
            Assert.Contains(result.SearchDetails.Candidates, item => item.Id == untagged.Id && item.ConceptPolicyDecision == "untagged_fallback");
        }

        var wrongRun = await search.SearchAsync(request with { UserTurnId = RunId.New() }, ct);
        Assert.Equal(3, wrongRun.Selected.Count);
        var staleEpoch = await search.SearchAsync(request with { IntentEpoch = 2 }, ct);
        Assert.Equal(3, staleEpoch.Selected.Count);
        var rolledBack = await search.SearchAsync(request with { SituationalConceptFilterMode = SituationalConceptFilterMode.Off }, ct);
        Assert.Equal(3, rolledBack.Selected.Count);
        var retained = await search.SearchAsync(
            request with
        {
            RetainedMemories = [new(mismatch.Id!.Value, mismatch.Entry!.Revision)],
        },
            ct);
        Assert.Equal(expected, retained.Selected.Count);
    }

    /// <summary>Verifies independent memory routing and intent eligibility.</summary>
    [Theory]
    [InlineData("missing")]
    [InlineData("epoch")]
    [InlineData("repository")]
    [InlineData("run")]
    [InlineData("mixed")]
    [InlineData("overflow")]
    public static async Task Unusable_provenance_preserves_baseline(string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "compiler errors distractor") with { Concepts = ["logging"] }, ct);
        using var search = new HybridRepositoryMemoryRetriever(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator);
        var request = Request(RunId.New()) with { Options = new() { SemanticMinimum = 1 } };
        var snapshot = request.IntentConcepts!;
        request = request with
        {
            IntentConcepts = reason switch
            {
                "missing" => null,
                "epoch" => snapshot with { Epoch = 9 },
                "repository" => snapshot with { RepositoryIdentity = "elsewhere" },
                "run" => snapshot with { RunId = RunId.New() },
                "overflow" => snapshot with { IsComplete = false },
                _ => snapshot with { Hints = [.. snapshot.Hints, new(2, ToolInvocationId.New(), false, ["logging"])] },
            },
        };
        var result = await search.SearchAsync(request, ct);
        Assert.Single(result.Selected);
        Assert.Equal("no_current_concepts", Assert.Single(result.SearchDetails!.Candidates).ConceptPolicyDecision);
    }

    /// <summary>Verifies independent memory routing and intent eligibility.</summary>
    [Fact]
    public static async Task Matching_tags_cannot_discover_candidates_or_bypass_incomplete_comparisons()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "unrelated content") with { Concepts = ["compiler"] }, ct);
        var encoder = new MemoryScoreEncoder { Score = (_, _) => new(4, 12, true) };
        using var search = new HybridRepositoryMemoryRetriever(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator, encoder);
        var request = Request(RunId.New());
        Assert.Empty((await search.SearchAsync(request, ct)).Selected);
        Assert.Empty((await search.SearchAsync(
            request with
        {
            Concepts = ["compiler"], Options = request.Options with { ConceptRecallEnabled = true },
        },
            ct)).Selected);
    }

    /// <summary>Verifies independent memory routing and intent eligibility.</summary>
    [Fact]
    public static void Runtime_epochs_discard_late_batches_and_keep_bounded_detached_evidence()
    {
        var state = new MemoryIntentState();
        var turn = RunId.New();
        var epoch = state.Epoch;
        Assert.True(state.Observe(epoch, 1, ToolInvocationId.New(), false, [" Compiler "]));
        var first = state.Capture(MemoryTestData.Repository, turn);
        Assert.Equal(["compiler"], first.Concepts);
        Assert.False(Assert.Single(first.Hints).Succeeded);
        state.Advance();
        Assert.False(state.Observe(epoch, 2, ToolInvocationId.New(), true, ["old"]));
        Assert.Empty(state.Capture(MemoryTestData.Repository, turn).Concepts);
        Assert.Single(first.Concepts);
        for (var index = 0; index < 100; index++)
        {
            state.Observe(state.Epoch, index, ToolInvocationId.New(), true, [$"concept{index}"]);
        }

        var bounded = state.Capture(MemoryTestData.Repository, turn);
        Assert.Equal(64, bounded.Concepts.Count);
        Assert.Equal(64, bounded.Hints.Count);
        Assert.True(bounded.MixedOrigins);
        Assert.False(bounded.IsComplete);
        Assert.Empty(new MemoryIntentState().Capture(MemoryTestData.Repository, RunId.New()).Concepts);
    }

    /// <summary>Explicit search cannot repair stale vectors or mutate repository revisions.</summary>
    [Fact]
    public static async Task Read_only_search_preserves_stale_embeddings()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        await store.AddAsync(
            MemoryTestData.Repository,
            new RepositoryMemoryWrite { Text = "compiler errors history", Origin = RepositoryMemoryOrigin.Model, Ownership = RepositoryMemoryOwnership.Intelligence },
            new TextEmbeddingModelDescriptor("old-space", 1, 256),
            new TextEmbeddingResult(new float[] { 1 }, 5, false),
            new RepositoryMemoryOptions(),
            ct);
        var before = await store.GetSnapshotAsync(MemoryTestData.Repository, [], ct);
        var generator = new TestMemoryEmbeddingGenerator();
        using var search = new HybridRepositoryMemoryRetriever(store, generator);
        var result = await search.SearchAsync(
            new RepositoryMemorySearchRequest
            {
                RepositoryIdentity = MemoryTestData.Repository, Ownership = RepositoryMemoryOwnership.Intelligence,
                Query = "compiler errors", AllowEmbeddingRebuild = false, Options = new(),
            },
            ct);
        var after = await store.GetSnapshotAsync(MemoryTestData.Repository, [], ct);
        Assert.Equal(before.Revision, after.Revision);
        Assert.Equal(before.Entries[0].EmbeddingSpaceId, after.Entries[0].EmbeddingSpaceId);
        Assert.Equal(before.Entries[0].Embedding, after.Entries[0].Embedding);
        Assert.Equal(1, generator.Calls);
        Assert.False(result.IsComplete);
    }

    /// <summary>Repeated observations retain evidence without repeating inference.</summary>
    [Fact]
    public static async Task Repeated_hints_preserve_provenance_and_reuse_ranking_until_eligibility_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "compiler errors guidance") with { Concepts = ["compiler"] }, ct);
        var encoder = new MemoryScoreEncoder();
        using var search = new HybridRepositoryMemoryRetriever(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator, encoder);
        var state = new MemoryIntentState();
        var run = RunId.New();
        Assert.True(state.Observe(state.Epoch, 1, ToolInvocationId.New(), true, ["compiler"]));
        var first = state.Capture(MemoryTestData.Repository, run);
        var request = Request(run) with { IntentEpoch = state.Epoch, IntentConcepts = first };
        await search.SearchAsync(request, ct);
        var scored = encoder.Documents.Count;
        Assert.False(state.Observe(state.Epoch, 2, ToolInvocationId.New(), false, ["compiler"]));
        var repeated = state.Capture(MemoryTestData.Repository, run);
        Assert.Equal(2, repeated.Hints.Count);
        Assert.Single(first.Hints);
        Assert.Equal(first.CacheKey, repeated.CacheKey);
        Assert.True((await search.SearchAsync(request with { IntentConcepts = repeated }, ct)).RankingCacheHit);
        Assert.Equal(scored, encoder.Documents.Count);
        Assert.True(state.Observe(state.Epoch, 3, ToolInvocationId.New(), true, ["logging"]));
        var mixed = state.Capture(MemoryTestData.Repository, run);
        Assert.True(mixed.MixedOrigins);
        Assert.False((await search.SearchAsync(request with { IntentConcepts = mixed }, ct)).RankingCacheHit);
        Assert.False(state.Observe(state.Epoch, 4, ToolInvocationId.New(), true, ["database"]));
        var cached = await search.SearchAsync(request with { IntentConcepts = state.Capture(MemoryTestData.Repository, run) }, ct);
        Assert.True(cached.RankingCacheHit);
        Assert.Equal(3, cached.SearchDetails!.IntentConceptCount);
    }

    /// <summary>Derived snapshot data remains detached and follows record updates.</summary>
    [Fact]
    public static void Snapshot_detaches_inputs_and_recomputes_derived_data_when_hints_are_replaced()
    {
        string[] concepts = ["compiler"];
        var snapshot = new MemoryIntentSnapshot("repo", RunId.New(), 1, [new(1, ToolInvocationId.New(), true, concepts)]);
        concepts[0] = "mutated";
        Assert.Equal(["compiler"], snapshot.Concepts);
        Assert.Same(snapshot.Concepts, snapshot.Concepts);
        var changed = snapshot with { Hints = [.. snapshot.Hints, new(2, ToolInvocationId.New(), true, ["logging"])] };
        Assert.True(changed.MixedOrigins);
        Assert.Equal(["compiler", "logging"], changed.Concepts);
        Assert.False(snapshot.MixedOrigins);
        Assert.NotEqual(snapshot.CacheKey, changed.CacheKey);
    }

    private static RepositoryMemorySearchRequest Request(RunId turn)
    {
        return new()
        {
            RepositoryIdentity = MemoryTestData.Repository,
            Ownership = RepositoryMemoryOwnership.Ordinary,
            Query = "compiler errors",
            UserTurnId = turn,
            IntentEpoch = 1,
            IntentConcepts = new(MemoryTestData.Repository, turn, 1, [new(1, ToolInvocationId.New(), false, ["compiler"])]),
            SituationalConceptFilterMode = SituationalConceptFilterMode.Enforce,
            Options = new() { SemanticMinimum = 1, RerankerEnabled = true, MaximumResults = 3 },
        };
    }
}
