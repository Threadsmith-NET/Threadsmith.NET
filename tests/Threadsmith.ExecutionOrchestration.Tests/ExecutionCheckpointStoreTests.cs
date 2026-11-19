namespace Threadsmith.ExecutionOrchestration.Tests;

using Microsoft.Data.Sqlite;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Xunit;

/// <summary>Checks repository-scoped recovery state from historical checkpoints.</summary>
public static class ExecutionCheckpointStoreTests
{
    /// <summary>Unknown and pending historical effects fence their owner until resolved.</summary>
    [Theory]
    [InlineData("unreadable", true)]
    [InlineData("""{"SchemaVersion":2,"Operation":{"State":"Pending"}}""", true)]
    [InlineData("""{"SchemaVersion":2,"Operation":{"State":"Completed"}}""", false)]
    public static async Task LegacyCheckpointStatesFenceOnlyTheirRepositoryAcrossMigration(string checkpoint, bool fenced)
    {
        var token = TestContext.Current.CancellationToken;
        var connectionString = $"Data Source=legacy-fence-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(token);
        var events = new SqliteEventStore(connectionString);
        await events.InitializeAsync(token);
        await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync(token);
        var sessionId = SessionId.New();
        var owner = Path.Combine(Path.GetTempPath(), "fence-owner");
        var other = Path.Combine(Path.GetTempPath(), "fence-other");
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO execution_runs(run_id, session_id, schema_version, checkpoint_json, updated_at)
            VALUES($run, $session, 2, $checkpoint, $updated);
            """;
        insert.Parameters.AddWithValue("$run", RunId.New().Value.ToString("D"));
        insert.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        insert.Parameters.AddWithValue("$checkpoint", checkpoint);
        insert.Parameters.AddWithValue("$updated", DateTimeOffset.UnixEpoch.ToString("O"));
        await insert.ExecuteNonQueryAsync(token);
        await events.AppendAsync(new RepositoryOpened(sessionId, DateTimeOffset.UnixEpoch, owner), token);
        var store = new ExecutionCheckpointStore(connectionString);

        Assert.Equal(fenced, await store.HasUnresolvedLegacyEffectsAsync(RepositoryIdentity.Create(owner), token));
        Assert.False(await store.HasUnresolvedLegacyEffectsAsync(RepositoryIdentity.Create(other), token));

        await new SessionLifecycleSchemaMigration().ApplyAsync(connection, token);
        await events.AppendAsync(new RepositoryOpened(sessionId, DateTimeOffset.UnixEpoch.AddSeconds(1), other), token);

        Assert.Equal(fenced, await store.HasUnresolvedLegacyEffectsAsync(RepositoryIdentity.Create(owner), token));
        Assert.False(await store.HasUnresolvedLegacyEffectsAsync(RepositoryIdentity.Create(other), token));
    }
}
