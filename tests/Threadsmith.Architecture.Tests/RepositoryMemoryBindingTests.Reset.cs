namespace Threadsmith.Architecture.Tests;

using Microsoft.Data.Sqlite;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.RepositoryIntelligence;
using Xunit;

/// <summary>Confirmed reset owns durable deletion, cancellation, and transient intelligence cleanup.</summary>
public static partial class RepositoryMemoryBindingTests
{
    /// <summary>Deletion is atomic and affects only the selected repository's intelligence partition.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task IntelligenceReset_IsAtomicAndPreservesOrdinaryAndOtherRepositoryMemories(bool failDelete)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await using var intelligence = await IntelligenceFixture.CreateAsync(fixture, ct);
        var store = new SqliteManagedRepositoryMemoryStore(fixture.InitialConnection);
        var options = intelligence.Options.CaptureCurrent();
        var model = new TextEmbeddingModelDescriptor("fixture", 1, 256);
        var embedding = new TextEmbeddingResult(new float[] { 1 }, 5, false);
        var ordinary = await store.AddAsync(intelligence.Identity, new RepositoryMemoryWrite { Text = "Keep this preference", Origin = RepositoryMemoryOrigin.Manual, MemoryType = RepositoryMemoryType.StandingPreference, Concepts = ["preference"] }, model, embedding, options, ct);
        var supported = await store.AddAsync(
            intelligence.Identity,
            new RepositoryMemoryWrite
            {
                Text = "Supported intelligence", Origin = RepositoryMemoryOrigin.Model, Ownership = RepositoryMemoryOwnership.Intelligence, Concepts = ["architecture"],
                Supports = [new RepositoryMemorySupport { Path = "service.cs", ContentHash = new string('a', 64), ObservedAt = DateTimeOffset.UtcNow }],
            },
            model,
            embedding,
            options,
            ct);
        var otherIdentity = RepositoryIdentity.Create(fixture.TargetRoot);
        await store.AddAsync(otherIdentity, new RepositoryMemoryWrite { Text = "Other repository intelligence", Origin = RepositoryMemoryOrigin.Model, Ownership = RepositoryMemoryOwnership.Intelligence }, model, embedding, options, ct);
        await store.RecordInclusionsAsync(intelligence.Identity, RunId.New(), [new RepositoryMemoryInclusion(supported.Entry!.Id, supported.Entry.Revision), new RepositoryMemoryInclusion(ordinary.Entry!.Id, ordinary.Entry.Revision)], ct);
        await store.SaveOnboardingCheckpointAsync(new RepositoryIntelligenceCheckpoint { RepositoryIdentity = intelligence.Identity, HistoryRevision = new string('a', 40), NextHistoryPage = 1 }, ct);
        await store.SaveOnboardingCheckpointAsync(new RepositoryIntelligenceCheckpoint { RepositoryIdentity = otherIdentity, HistoryRevision = new string('b', 40), NextHistoryPage = 1 }, ct);
        await using var operations = new RepositoryIntelligenceOperations(store, intelligence.Options, intelligence.Monitor, TestPromptLoader.Instance, intelligence.Events, intelligence.Options.SetIntelligenceEnabledAsync, intelligence.Automation);
        var session = SessionId.New();
        await operations.BindAsync(session, fixture.InitialRoot, ct);
        await intelligence.Monitor.WaitUntilReadyAsync(ct);
        await intelligence.Commits.Queue.Writer.WriteAsync(new RepositoryCommitCreation("pending"), ct);
        await intelligence.Pending.Reader.ReadAsync(ct);
        await store.RecordIntelligenceChangesAsync(intelligence.Identity, ["new.cs"], true, "persisted fault", ct);
        var before = await store.GetSnapshotAsync(intelligence.Identity, [], ct);
        var otherBefore = await store.GetSnapshotAsync(otherIdentity, [], ct);
        await using var connection = new SqliteConnection(fixture.InitialConnection);
        await connection.OpenAsync(ct);
        await using var query = connection.CreateCommand();
        if (failDelete)
        {
            query.CommandText = "CREATE TRIGGER fail_reset BEFORE DELETE ON repository_intelligence_state BEGIN SELECT RAISE(ABORT, 'Injected reset failure'); END;";
            await query.ExecuteNonQueryAsync(ct);
        }

        var reset = new ManageRepositoryIntelligenceCommand(session, fixture.InitialRoot, "reset") { ResetConfirmed = true };
        if (failDelete)
        {
            await Assert.ThrowsAsync<SqliteException>(() => operations.HandleAsync(reset, ct));
            var afterFailure = await store.GetSnapshotAsync(intelligence.Identity, [], ct);
            Assert.Equal(before.Entries.Select(item => (item.Id, item.Text)), afterFailure.Entries.Select(item => (item.Id, item.Text)));
            Assert.True((await store.GetIntelligenceStateAsync(intelligence.Identity, ct)).Onboarded);
            Assert.True(intelligence.Options.CaptureCurrent().Intelligence.Enabled);
            Assert.NotNull(await store.ReadOnboardingCheckpointAsync(intelligence.Identity, ct));
            return;
        }

        var result = await operations.HandleAsync(reset, ct);
        Assert.Null(await store.ReadOnboardingCheckpointAsync(intelligence.Identity, ct));
        Assert.NotNull(await store.ReadOnboardingCheckpointAsync(otherIdentity, ct));
        Assert.False(result.Onboarded);
        Assert.False(result.IsEffective);
        Assert.Null(result.Fault);
        Assert.False(intelligence.Options.CaptureCurrent().Intelligence.Enabled);
        Assert.Equal(0, intelligence.Automation.CaptureGeneration(intelligence.Identity));
        var after = await store.GetSnapshotAsync(intelligence.Identity, ["Supported"], ct);
        Assert.Equal(ordinary.Entry.Id, Assert.Single(after.Entries).Id);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(before.Entries.Single(item => item.Id == ordinary.Entry.Id)), System.Text.Json.JsonSerializer.Serialize(after.Entries[0]));
        Assert.True(after.Revision > before.Revision);
        Assert.Empty(after.LexicalMatches);
        Assert.Equal(otherBefore.Entries.Select(item => item.Id), (await store.GetSnapshotAsync(otherIdentity, [], ct)).Entries.Select(item => item.Id));
        query.CommandText = """
            SELECT
              (SELECT count(*) FROM managed_memory_supports WHERE repository_identity = $repo)
            + (SELECT count(*) FROM managed_memory_concepts WHERE repository_identity = $repo AND concept = 'architecture')
            + (SELECT count(*) FROM managed_memory_inclusions WHERE repository_identity = $repo AND memory_id = $id)
            + (SELECT count(*) FROM repository_intelligence_changes WHERE repository_identity = $repo)
            + (SELECT count(*) FROM repository_intelligence_state WHERE repository_identity = $repo);
            """;
        query.Parameters.AddWithValue("$repo", intelligence.Identity);
        query.Parameters.AddWithValue("$id", supported.Entry.Id.Value.ToString("D"));
        Assert.Equal(0L, await query.ExecuteScalarAsync(ct));
        Assert.False((await new SqliteManagedRepositoryMemoryStore(fixture.InitialConnection).GetIntelligenceStateAsync(intelligence.Identity, ct)).Onboarded);
        Assert.False((await operations.HandleAsync(new ManageRepositoryIntelligenceCommand(session, fixture.InitialRoot, "enable"), ct)).IsEffective);
        await Assert.ThrowsAsync<InvalidOperationException>(() => operations.BeginAsync(session, RunId.New(), fixture.InitialRoot, RepositoryIntelligenceOperation.Refresh, ct));
        await using var onboard = await operations.BeginAsync(session, RunId.New(), fixture.InitialRoot, RepositoryIntelligenceOperation.Onboard, ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => onboard.CompleteAsync(ct));
        await onboard.AcceptHistoryPageAsync(0, new GitLogResult([], false) { IsCollectionPage = true }, "{}", ct);
        Assert.NotNull(await onboard.ContinueCoverageAsync("Final pass", ct));
        onboard.Collection!.UpdateCoverage(new RepositoryIntelligenceCoverage
        {
            Key = "fixture", Area = "Fixture", Question = "What guidance exists?", Status = "blocked", Gaps = "Fixture provides no source to inspect.",
        });
        await onboard.CompleteAsync(ct);
        Assert.True((await store.GetIntelligenceStateAsync(intelligence.Identity, ct)).Onboarded);
    }

    /// <summary>A failed disable write restores consumers without repeating the failed configuration write.</summary>
    [Fact]
    public static async Task IntelligenceReset_ConfigFailureRestoresObservation()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await using var intelligence = await IntelligenceFixture.CreateAsync(fixture, ct);
        var writes = 0;
        await using var operations = new RepositoryIntelligenceOperations(
            intelligence.Store,
            intelligence.Options,
            intelligence.Monitor,
            TestPromptLoader.Instance,
            intelligence.Events,
            (_, _, _) =>
            {
                writes++;
                throw new IOException("Configuration is read-only.");
            },
            intelligence.Automation);
        var session = SessionId.New();
        await operations.BindAsync(session, fixture.InitialRoot, ct);
        await intelligence.Monitor.WaitUntilReadyAsync(ct);
        await Assert.ThrowsAsync<IOException>(() => operations.HandleAsync(new ManageRepositoryIntelligenceCommand(session, fixture.InitialRoot, "reset") { ResetConfirmed = true }, ct));
        await intelligence.Monitor.WaitUntilReadyAsync(ct);
        Assert.Equal(1, writes);
        Assert.True(intelligence.Monitor.Capture(intelligence.Identity).IsEffective);
        Assert.Single((await intelligence.Store.GetSnapshotAsync(intelligence.Identity, [], ct)).Entries);
        await intelligence.Commits.Queue.Writer.WriteAsync(new RepositoryCommitCreation("after-failed-reset"), ct);
        Assert.Equal(session, (await intelligence.Pending.Reader.ReadAsync(ct)).SessionId);
        Assert.True(await intelligence.Automation.HandleAsync(new TakeRepositoryIntelligenceRefreshCommand(session, fixture.InitialRoot), ct));
    }

    /// <summary>Unconfirmed commands cannot alter state; confirmed reset joins an interrupted collection.</summary>
    [Fact]
    public static async Task IntelligenceReset_RequiresConfirmationAndCancelsActiveCollection()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await using var intelligence = await IntelligenceFixture.CreateAsync(fixture, ct);
        await using var operations = intelligence.CreateOperations();
        var session = SessionId.New();
        await operations.BindAsync(session, fixture.InitialRoot, ct);
        await intelligence.Monitor.WaitUntilReadyAsync(ct);
        var command = new ManageRepositoryIntelligenceCommand(session, fixture.InitialRoot, "reset");
        await Assert.ThrowsAsync<InvalidOperationException>(() => operations.HandleAsync(command, ct));
        Assert.True(intelligence.Monitor.Capture(intelligence.Identity).IsEffective);
        Assert.Single((await intelligence.Store.GetSnapshotAsync(intelligence.Identity, [], ct)).Entries);
        var run = await operations.BeginAsync(session, RunId.New(), fixture.InitialRoot, RepositoryIntelligenceOperation.Onboard, ct);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = run.CancellationToken.Register(() => cancelled.TrySetResult());
        var reset = operations.HandleAsync(command with { ResetConfirmed = true }, ct);
        await cancelled.Task.WaitAsync(ct);
        Assert.False(reset.IsCompleted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.CompleteAsync(ct));
        await run.DisposeAsync();
        Assert.False((await reset).Onboarded);
        Assert.Empty((await intelligence.Store.GetSnapshotAsync(intelligence.Identity, [], ct)).Entries);
    }
}
