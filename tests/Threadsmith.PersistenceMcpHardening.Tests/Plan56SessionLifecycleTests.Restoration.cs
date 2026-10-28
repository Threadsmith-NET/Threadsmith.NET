namespace Threadsmith.PersistenceMcpHardening.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Xunit;

public static partial class Plan56SessionLifecycleTests
{
    /// <summary>Version-three receipts retain the operation and destination needed by restored history.</summary>
    [Fact]
    public static void VersionThreeMutationReceiptRetainsOperation()
    {
        var receipt = new MutationApplied(SessionId.New(), DateTimeOffset.UtcNow, MutationId.New(), MutationSetId.New(), "source.cs")
        {
            SchemaVersion = 3,
            Type = MutationType.MoveFile,
            DestinationRelativePath = "moved.cs",
        };
        var restored = Assert.IsType<MutationApplied>(DomainEventJson.Deserialize("mutationApplied", 3, DomainEventJson.Serialize(receipt)));
        Assert.Equal(receipt, restored);
    }

    /// <summary>The migration registry shares the event reader's allow-list for compatible per-event schemas.</summary>
    [Theory]
    [InlineData("repositoryOpened")]
    [InlineData("planProposed")]
    [InlineData("approvalRequested")]
    [InlineData("mutationSetProposed")]
    [InlineData("mutationApplied")]
    public static void KnownVersionTwoEventsAreCurrentWithoutMigration(string discriminator)
    {
        var registry = new DomainEventMigrationRegistry([], currentVersion: 1);
        Assert.True(DomainEventJson.SupportsSchemaVersion(discriminator, 2));
        Assert.Equal(LegacyState.Current, registry.Classify(discriminator, 2, "{}").State);
        Assert.Equal(LegacyState.Current, registry.Classify(discriminator, 1, "{}").State);
        Assert.Equal(discriminator == "mutationApplied" ? LegacyState.Current : LegacyState.Legacy, registry.Classify(discriminator, 3, "{}").State);
        Assert.Equal(LegacyState.Legacy, registry.Classify(discriminator, 4, "{}").State);
        Assert.Equal(LegacyState.Legacy, registry.Classify("sessionCreated", 2, "{}").State);
        Assert.Equal(LegacyState.Legacy, registry.Classify("unknownEvent", 2, "{}").State);
    }

    /// <summary>Real durable replay restores supported history, including schema-3 mutation receipts, and repairs an earlier read-only catalog mark.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public static async Task ResumeSupportedHistoryAcceptsNextRequest(bool previouslyMarkedLegacy, bool hasCurrentBinding = true)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await SessionLifecycleFixture.CreateAsync();
        var repository = Path.Combine(fixture.DirectoryPath, "repository");
        Directory.CreateDirectory(repository);
        var catalog = new SqliteSessionLifecycleStore(fixture.ConnectionString);
        var eventStore = new SqliteEventStore(fixture.ConnectionString);
        var sessionId = SessionId.New();
        var now = DateTimeOffset.UtcNow;
        var entry = CreateEntry(sessionId, RepositoryIdentity.Create(repository), now) with
        {
            State = previouslyMarkedLegacy ? SessionLifecycleState.Legacy : SessionLifecycleState.Idle,
            IsWritable = !previouslyMarkedLegacy,
        };
        await catalog.CreateAsync(entry, EmptyUsage(), token);
        var mutationId = MutationSetId.New();
        var approvalId = ApprovalId.New();
        var historicalWorkspace = WorkspaceId.New();
        DomainEvent[] history =
        [
            new SessionCreated(sessionId, now, "Restored conversation"),
            new RepositoryOpened(sessionId, now, repository, historicalWorkspace, RepositoryTrustLevel.TrustedMutation) { SchemaVersion = 2 },
            new SolutionLoaded(sessionId, now, Path.Combine(repository, "Old.sln"), historicalWorkspace, ["old-framework"]),
            new MutationSetProposed(sessionId, now, mutationId, new(mutationId, "historical diff", [], 1, 0), ApprovalId: approvalId) { SchemaVersion = 2 },
            new ApprovalRequested(sessionId, now, approvalId, "Historical review", ApprovalRequestKind.MutationSet) { SchemaVersion = 2 },
            new ApprovalDenied(sessionId, now, approvalId, "Cancelled"),
            new MutationApplied(sessionId, now, MutationId.New(), mutationId, "source.cs") { SchemaVersion = 3, Type = MutationType.MoveFile, DestinationRelativePath = "moved.cs" },
            new MutationSetRolledBack(sessionId, now, mutationId, []),
        ];
        foreach (var item in history)
        {
            await eventStore.AppendAsync(item, token);
        }

        var model = new GatedModelProvider();
        model.Release();
        var restorer = new SessionRestorer(eventStore, new DomainEventMigrationRegistry([], currentVersion: 1), NullLogger<SessionRestorer>.Instance, fixture.Conversations);
        await using var harness = LifecycleHarness.Create(repository, catalog, fixture.Conversations, modelProvider: model, restorer: restorer);
        var startup = await harness.Lifecycle.HandleAsync(new CreateNewSessionCommand(), token);
        var currentWorkspace = WorkspaceId.New();
        if (hasCurrentBinding)
        {
            await harness.Events.PublishAsync(new RepositoryOpened(startup.ActiveSession.SessionId, now, repository, currentWorkspace, RepositoryTrustLevel.TrustedRead), token);
        }

        var observed = new List<IDomainEvent>();
        await using var subscription = harness.Events.Subscribe((item, _) =>
        {
            observed.Add(item);
            return Task.CompletedTask;
        });

        var resumed = await harness.Lifecycle.HandleAsync(new ResumeSessionCommand(sessionId), token);
        Assert.True(resumed.ActiveSession.IsWritable);
        Assert.Equal(SessionLifecycleState.Active, resumed.ActiveSession.State);
        Assert.Empty(resumed.Warnings);
        var projection = harness.Projections.GetSession(sessionId);
        Assert.NotNull(projection);
        if (hasCurrentBinding)
        {
            Assert.Equal(currentWorkspace, projection.WorkspaceId);
            Assert.Equal(RepositoryTrustLevel.TrustedRead, projection.RepositoryTrust);
            Assert.Contains(observed, item => item is RepositoryOpened opened && opened.SessionId == sessionId && opened.WorkspaceId == currentWorkspace);
        }
        else
        {
            Assert.Null(projection.WorkspaceId);
            Assert.Null(projection.RepositoryTrust);
        }

        Assert.Null(projection.SolutionPath);
        Assert.Empty(projection.TargetFrameworks);
        Assert.True((await catalog.GetAsync(sessionId, token))?.IsWritable);
        Assert.Equal(history.Length, (await eventStore.ReadRowsAsync(sessionId, token)).Count);
        var run = await harness.Sessions.HandleAsync(new SubmitRequestCommand(sessionId, "Finish the requested issue."), token);
        Assert.True(await harness.Sessions.HandleAsync(new WaitForRunCommand(run), token));
        Assert.True(model.Entered.IsCompletedSuccessfully);
        Assert.DoesNotContain(observed, item => item is ApprovalRequested or MutationApplied or ToolInvocationStarted);
    }

    /// <summary>Unsupported future history remains inspectable and rejects writes with its actual availability.</summary>
    [Fact]
    public static async Task ResumeFutureHistoryReportsReadOnlyRatherThanMissingSession()
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await SessionLifecycleFixture.CreateAsync();
        var repository = Path.Combine(fixture.DirectoryPath, "repository");
        Directory.CreateDirectory(repository);
        var catalog = new SqliteSessionLifecycleStore(fixture.ConnectionString);
        var eventStore = new SqliteEventStore(fixture.ConnectionString);
        var sessionId = SessionId.New();
        var now = DateTimeOffset.UtcNow;
        await catalog.CreateAsync(CreateEntry(sessionId, RepositoryIdentity.Create(repository), now), EmptyUsage(), token);
        await eventStore.AppendAsync(new SessionCreated(sessionId, now, "Future history"), token);
        await eventStore.AppendAsync(new PlanProposed(sessionId, now, "Unsupported future schema") { SchemaVersion = 99 }, token);
        var restorer = new SessionRestorer(eventStore, new DomainEventMigrationRegistry([], currentVersion: 1), NullLogger<SessionRestorer>.Instance);
        await using var harness = LifecycleHarness.Create(repository, catalog, fixture.Conversations, restorer: restorer);
        _ = await harness.Lifecycle.HandleAsync(new CreateNewSessionCommand(), token);

        var resumed = await harness.Lifecycle.HandleAsync(new ResumeSessionCommand(sessionId), token);
        Assert.False(resumed.ActiveSession.IsWritable);
        Assert.Equal(SessionLifecycleState.Legacy, resumed.ActiveSession.State);
        Assert.NotEmpty(resumed.Warnings);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Sessions.HandleAsync(new SubmitRequestCommand(sessionId, "Must not execute."), token));
        Assert.Contains("read-only", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("does not exist", error.Message, StringComparison.Ordinal);
        Assert.False(await harness.Sessions.HandleAsync(new SetConversationContextModeCommand(sessionId, ConversationContextMode.Stateless), token));
        var repeated = await harness.Lifecycle.HandleAsync(new ResumeSessionCommand(sessionId), token);
        Assert.False(repeated.ActiveSession.IsWritable);
        Assert.NotEmpty(repeated.Warnings);
        var projection = harness.Projections.GetSession(sessionId);
        Assert.NotNull(projection);
        Assert.Null(projection.WorkspaceId);
        Assert.Null(projection.RepositoryTrust);
    }
}
