namespace Threadsmith.ExecutionOrchestration.Tests;

using Microsoft.Data.Sqlite;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Xunit;

/// <summary>Verifies historical checkpoint compatibility and current durable storage.</summary>
public sealed partial class ExecutionCheckpointStoreTests
{
    /// <summary>Verifies the current ordered migrations retain the Plan-37 execution checkpoint table.</summary>
    [Fact]
    public async Task CurrentMigrations_RetainExecutionRunsTable()
    {
        // Arrange
        var databasePath = Path.Combine(Path.GetTempPath(), $"threadsmith-m11-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        try
        {
            var runner = new MigrationRunner(connectionString, DefaultMigrations.All);

            // Act
            var version = await runner.RunAsync();

            // Assert
            Assert.Equal(13, version);
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='execution_runs';";
            var count = (long)(await command.ExecuteScalarAsync() ?? 0L);
            Assert.Equal(1, count);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    /// <summary>Terminal outcomes remain readable through the existing persistence owner.</summary>
    [Fact]
    public async Task ExecutionCheckpointStore_OutcomeRemainsReadable()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"threadsmith-execution-store-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var sessionId = SessionId.New();
        var runId = RunId.New();
        try
        {
            await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync();
            var store = new ExecutionCheckpointStore(connectionString);
            var outcome = new ExecutionOutcomeProjection
            {
                Key = new ProjectionKey("execution-outcome", runId.Value.ToString("D")),
                SessionId = sessionId,
                RunId = runId,
                Status = ExecutionCheckpointPhase.Completed,
                ApprovalProvenance = "test",
            };
            await store.SaveOutcomeAsync(outcome);

            var reopened = new ExecutionCheckpointStore(connectionString);
            var restored = await reopened.GetOutcomeAsync(runId);
            Assert.NotNull(restored);
            Assert.Equal(outcome.SchemaVersion, restored.SchemaVersion);
            Assert.Equal(outcome.SessionId, restored.SessionId);
            Assert.Equal(outcome.RunId, restored.RunId);
            Assert.Equal(outcome.Status, restored.Status);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    /// <summary>Orphaned checkpoints fence only their recorded repository before and after catalog migration.</summary>
    [Theory]
    [InlineData("{\"SchemaVersion\":2,\"Operation\":{\"State\":\"Pending\"}}", true)]
    [InlineData("{\"SchemaVersion\":2,\"Operation\":{\"State\":\"RecoveryRequired\"}}", true)]
    [InlineData("{\"SchemaVersion\":2,\"Phase\":\"MutationApplyPending\"}", true)]
    [InlineData("{\"SchemaVersion\":99}", true)]
    [InlineData("invalid checkpoint", true)]
    [InlineData("{\"SchemaVersion\":2,\"Operation\":{\"State\":\"Completed\"}}", false)]
    [InlineData("{\"SchemaVersion\":2,\"Operation\":{\"State\":\"RolledBack\"}}", false)]
    public async Task LegacyEffects_AreScopedToRecordedRepository(string checkpointJson, bool unresolved)
    {
        // Arrange: a shared database contains an orphaned historical run.
        var databasePath = Path.Combine(Path.GetTempPath(), $"threadsmith-legacy-scope-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var sessionId = SessionId.New();
        var repositoryA = Path.Combine(Path.GetTempPath(), "threadsmith-repository-a");
        var repositoryB = Path.Combine(Path.GetTempPath(), "threadsmith-repository-b");
        var cancellationToken = TestContext.Current.CancellationToken;
        try
        {
            var events = new SqliteEventStore(connectionString);
            await events.InitializeAsync(cancellationToken);
            await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync(cancellationToken);
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO execution_runs(run_id, session_id, schema_version, checkpoint_json, updated_at)
                VALUES($run, $session, 2, $checkpoint, $updated);
                """;
            insert.Parameters.AddWithValue("$run", RunId.New().Value.ToString("D"));
            insert.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
            insert.Parameters.AddWithValue("$checkpoint", checkpointJson);
            insert.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
            var store = new ExecutionCheckpointStore(connectionString);

            // Act/assert: absent attribution never becomes a global write lock.
            Assert.False(await store.HasUnresolvedLegacyEffectsAsync(RepositoryIdentity.Create(repositoryA), cancellationToken));
            Assert.False(await store.HasUnresolvedLegacyEffectsAsync(RepositoryIdentity.Create(repositoryB), cancellationToken));

            // An orphan created after migration still fences its recorded owner, without rerunning migration.
            await events.AppendAsync(new RepositoryOpened(sessionId, DateTimeOffset.UtcNow, repositoryA), cancellationToken);
            Assert.Equal(unresolved, await store.HasUnresolvedLegacyEffectsAsync(RepositoryIdentity.Create(repositoryA), cancellationToken));
            Assert.False(await store.HasUnresolvedLegacyEffectsAsync(RepositoryIdentity.Create(repositoryB), cancellationToken));

            // The existing historical-session migration restores catalog attribution.
            await new SessionLifecycleSchemaMigration().ApplyAsync(connection, cancellationToken);
            Assert.Equal(unresolved, await store.HasUnresolvedLegacyEffectsAsync(RepositoryIdentity.Create(repositoryA), cancellationToken));
            Assert.False(await store.HasUnresolvedLegacyEffectsAsync(RepositoryIdentity.Create(repositoryB), cancellationToken));

            // Once catalogued, the repository identity is authoritative over later stray events.
            await events.AppendAsync(new RepositoryOpened(sessionId, DateTimeOffset.UtcNow, repositoryB), cancellationToken);
            Assert.Equal(unresolved, await store.HasUnresolvedLegacyEffectsAsync(RepositoryIdentity.Create(repositoryA), cancellationToken));
            Assert.False(await store.HasUnresolvedLegacyEffectsAsync(RepositoryIdentity.Create(repositoryB), cancellationToken));
            await using var retained = connection.CreateCommand();
            retained.CommandText = "SELECT checkpoint_json FROM execution_runs;";
            Assert.Equal(checkpointJson, await retained.ExecuteScalarAsync(cancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }
}
