namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Xunit;

/// <summary>Eligibility and provenance contracts through the shared search path.</summary>
public static class MemoryIntentPolicyTests
{
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
