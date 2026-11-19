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

    /// <summary>Both ingestion and read APIs detach nested inspection collections.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task InspectionCollectionsAreOwnedAtEveryStoreBoundary(bool replace, bool synchronous)
    {
        // Arrange: mutable input graphs exercise both ingress and read APIs.
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryProjectionStore();
        var sessionId = SessionId.New();
        var key = new ProjectionKey("session", sessionId.Value.ToString("D"));
        var input = TestContextInspection.Create();
        var expectedSupport = input.RepositoryMemoryItems[0].Supports[0];
        var expectedMessage = input.ConversationItems[0].SourceMessageIds[0];
        await store.ApplyAsync(new SessionCreated(sessionId, DateTimeOffset.UtcNow, "inspection"), cancellationToken);
        if (replace)
        {
            store.ReplaceSession(new SessionProjection
            {
                Key = key,
                SessionId = sessionId,
                Name = "replaced",
                ContextInspection = input,
            });
        }
        else
        {
            await store.ApplyAsync(new ContextAssembled(sessionId, DateTimeOffset.UtcNow, input), cancellationToken);
        }

        // Act: change caller-owned input after ingestion.
        Assert.IsType<IList<RepositoryMemorySupport>>(input.RepositoryMemoryItems[0].Supports, exactMatch: false)[0] =
            expectedSupport with { Path = "changed-input" };
        Assert.IsType<IList<ConversationMessageId>>(input.ConversationItems[0].SourceMessageIds, exactMatch: false)[0] =
            ConversationMessageId.New();
        var first = synchronous ? store.GetSession(sessionId) : await store.GetAsync<SessionProjection>(key, cancellationToken);
        var inspection = Assert.IsType<ContextInspectionProjection>(first?.ContextInspection);

        // Assert: input mutations and mutations of either read API cannot affect later results.
        Assert.Equal(expectedSupport, inspection.RepositoryMemoryItems[0].Supports[0]);
        Assert.Equal(expectedMessage, inspection.ConversationItems[0].SourceMessageIds[0]);
        Assert.IsType<IList<RepositoryMemorySupport>>(inspection.RepositoryMemoryItems[0].Supports, exactMatch: false)[0] =
            expectedSupport with { Path = "changed-output" };
        Assert.IsType<IList<ConversationMessageId>>(inspection.ConversationItems[0].SourceMessageIds, exactMatch: false)[0] =
            ConversationMessageId.New();
        var next = await store.GetAsync<SessionProjection>(key, cancellationToken);
        var nextSynchronous = store.GetSession(sessionId);
        Assert.Equal(expectedSupport, next!.ContextInspection!.RepositoryMemoryItems[0].Supports[0]);
        Assert.Equal(expectedSupport, nextSynchronous!.ContextInspection!.RepositoryMemoryItems[0].Supports[0]);
        Assert.Equal(expectedMessage, next.ContextInspection.ConversationItems[0].SourceMessageIds[0]);
        Assert.Equal(expectedMessage, nextSynchronous.ContextInspection.ConversationItems[0].SourceMessageIds[0]);
    }

    /// <summary>Status reads track the existing event-owned session state without retaining mutable data.</summary>
    [Theory]
    [InlineData(MutationApprovalLevel.EntireSet)]
    [InlineData(MutationApprovalLevel.PolicyAutoApproved)]
    public static async Task MutationStatusTracksExistingSessionEvents(MutationApprovalLevel requiredApproval)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = new InMemoryProjectionStore();
        var sessionId = SessionId.New();
        var key = new ProjectionKey("session", sessionId.Value.ToString("D"));
        var now = DateTimeOffset.UtcNow;
        Assert.Null(await store.GetAsync<MutationStatusProjection>(key, cancellationToken));
        await store.ApplyAsync(new SessionCreated(sessionId, now, "status"), cancellationToken);
        Assert.Null(await store.GetAsync<MutationStatusProjection>(key, cancellationToken));
        var mutationSetId = MutationSetId.New();
        var preview = new MutationPreview(mutationSetId, "diff", [], 1, 1);

        // Act / Assert: reads derive from the same session state, not a second status store.
        await store.ApplyAsync(new MutationSetProposed(sessionId, now, mutationSetId, preview, requiredApproval), cancellationToken);
        var pending = await store.GetAsync<MutationStatusProjection>(key, cancellationToken);
        Assert.NotNull(pending);
        Assert.Equal(new MutationStatusProjection(key, mutationSetId, requiredApproval, false, false), pending);
        await store.ApplyAsync(new MutationApplied(sessionId, now, MutationId.New(), mutationSetId), cancellationToken);
        Assert.Equal(pending with { IsApplied = true }, await store.GetAsync<MutationStatusProjection>(key, cancellationToken));
        await store.ApplyAsync(new MutationSetRolledBack(sessionId, now, mutationSetId, []), cancellationToken);
        Assert.Equal(pending with { IsApplied = true, IsRolledBack = true }, await store.GetAsync<MutationStatusProjection>(key, cancellationToken));
        Assert.False(pending.IsApplied);
        Assert.False(pending.IsRolledBack);

        var replacementId = MutationSetId.New();
        await store.ApplyAsync(new MutationSetProposed(sessionId, now, replacementId, preview with { MutationSetId = replacementId }), cancellationToken);
        Assert.Equal(
            new MutationStatusProjection(key, replacementId, MutationApprovalLevel.EntireSet, false, false),
            await store.GetAsync<MutationStatusProjection>(key, cancellationToken));
    }

    /// <summary>Status reads honor lifecycle replacement and caller cancellation.</summary>
    [Fact]
    public static async Task MutationStatusHonorsSessionReplacementAndCancellation()
    {
        // Arrange
        var store = new InMemoryProjectionStore();
        var sessionId = SessionId.New();
        var key = new ProjectionKey("session", sessionId.Value.ToString("D"));
        var mutationSetId = MutationSetId.New();
        var session = new SessionProjection
        {
            Key = key,
            SessionId = sessionId,
            Name = "restored",
            Mutation = new MutationProjection(
                mutationSetId,
                ApprovalId.New(),
                new MutationPreview(mutationSetId, "diff", [], 1, 1),
                MutationApprovalLevel.PreviewOnly,
                WorkspaceIsolationMode.TrackedInPlace,
                false,
                false,
                false),
        };

        // Act / Assert
        store.ReplaceSession(session);
        Assert.Equal(
            new MutationStatusProjection(key, mutationSetId, MutationApprovalLevel.PreviewOnly, false, false),
            await store.GetAsync<MutationStatusProjection>(key, TestContext.Current.CancellationToken));
        store.ReplaceSession(session with { Mutation = null });
        Assert.Null(await store.GetAsync<MutationStatusProjection>(key, TestContext.Current.CancellationToken));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetAsync<MutationStatusProjection>(key, cancellation.Token));
    }
}
