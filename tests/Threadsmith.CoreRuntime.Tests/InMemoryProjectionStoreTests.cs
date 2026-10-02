namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

/// <summary>Verifies streaming activity allocation and ownership at projection boundaries.</summary>
public static class InMemoryProjectionStoreTests
{
    /// <summary>Both read APIs detach activity from subsequent writes and caller mutations.</summary>
    [Fact]
    public static async Task ActivitySnapshotsRemainDetachedAcrossAppendsAndTurnReset()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryProjectionStore();
        var sessionId = SessionId.New();
        var key = new ProjectionKey("session", sessionId.Value.ToString("D"));
        var now = DateTimeOffset.UtcNow;
        await store.ApplyAsync(new SessionCreated(sessionId, now, "streaming"), cancellationToken);
        await store.ApplyAsync(new ModelOutputObserved(sessionId, now, "first"), cancellationToken);
        var first = await store.GetAsync<SessionProjection>(key, cancellationToken);
        var synchronous = store.GetSession(sessionId);
        Assert.NotNull(first);
        Assert.NotNull(synchronous);

        await store.ApplyAsync(new ModelOutputObserved(sessionId, now, "second"), cancellationToken);
        await store.ApplyAsync(
            new SemanticMutationWarningObserved(sessionId, now, RunId.New(), SemanticConfidenceLevel.TextOnly, "warning"),
            cancellationToken);
        var appended = await store.GetAsync<SessionProjection>(key, cancellationToken);
        Assert.NotNull(appended);
        Assert.Equal(["first", "second", "Semantic rename warning (TextOnly): warning"], appended.Activity);
        Assert.Equal(["first"], first.Activity);
        Assert.Equal(["first"], synchronous.Activity);

        Assert.IsType<string[]>(first.Activity)[0] = "changed by caller";
        Assert.IsType<string[]>(synchronous.Activity)[0] = "also changed by caller";
        Assert.Equal("first", store.GetSession(sessionId)!.Activity[0]);

        await store.ApplyAsync(new TaskIntentRecorded(sessionId, now, "next turn"), cancellationToken);
        Assert.Empty(store.GetSession(sessionId)!.Activity);
        await store.ApplyAsync(new ModelOutputObserved(sessionId, now, "new turn"), cancellationToken);
        Assert.Equal(["new turn"], store.GetSession(sessionId)!.Activity);
        Assert.Equal(["first", "second", "Semantic rename warning (TextOnly): warning"], appended.Activity);
    }

    /// <summary>Restored activity is copied on input and remains appendable without changing prior snapshots.</summary>
    [Fact]
    public static async Task ReplacedActivityIsOwnedByStoreAndCanContinueStreaming()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryProjectionStore();
        var sessionId = SessionId.New();
        var now = DateTimeOffset.UtcNow;
        List<string> activity = ["restored"];
        var replacement = new SessionProjection
        {
            Key = new ProjectionKey("session", sessionId.Value.ToString("D")),
            SessionId = sessionId,
            Name = "restored session",
            Activity = activity,
        };
        store.ReplaceSession(replacement);
        var snapshot = store.GetSession(sessionId);
        Assert.NotNull(snapshot);
        activity[0] = "changed by caller";

        await store.ApplyAsync(new ModelOutputObserved(sessionId, now, "continued"), cancellationToken);

        Assert.Equal(["restored", "continued"], store.GetSession(sessionId)!.Activity);
        Assert.Equal(["restored"], snapshot.Activity);
        Assert.Single(activity);
    }

    /// <summary>Long streams avoid quadratic activity allocations, including for events with no projection changes.</summary>
    [Fact]
    public static async Task StreamingActivityDoesNotAllocateCopiesOfAccumulatedReferences()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryProjectionStore();
        var sessionId = SessionId.New();
        var now = DateTimeOffset.UtcNow;
        var output = new ModelOutputObserved(sessionId, now, "chunk");
        var ignored = new ModelReasoningObserved(sessionId, now, "reasoning");
        await store.ApplyAsync(new SessionCreated(sessionId, now, "streaming"), cancellationToken);
        await store.ApplyAsync(output, cancellationToken);
        await store.ApplyAsync(ignored, cancellationToken);

        // ApplyAsync completes synchronously; measure allocation, not runner-dependent elapsed time.
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 4096; index++)
        {
            await store.ApplyAsync(output, cancellationToken);
            await store.ApplyAsync(ignored, cancellationToken);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        TestContext.Current.TestOutputHelper?.WriteLine($"4,096 output/ignored event pairs: {allocated:N0} bytes.");
        Assert.True(allocated < 8_000_000, $"Accumulated activity copying allocated {allocated:N0} bytes.");
        Assert.Equal(4097, store.GetSession(sessionId)!.Activity.Count);
    }
}
