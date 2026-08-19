namespace Threadsmith.Milestone8.Tests;

using Microsoft.Data.Sqlite;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Xunit;

/// <summary>Verifies M16's irreversible hidden-reasoning persistence boundary.</summary>
public static class Plan46ReasoningPrivacyTests
{
    /// <summary>Migration 7 removes historical reasoning payloads while preserving ordinary events.</summary>
    [Fact]
    public static async Task ReasoningPrivacyMigration_HistoricalRows_ArePurgedTransactionally()
    {
        var path = Path.Combine(Path.GetTempPath(), $"threadsmith-plan46-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString();
        try
        {
            var store = new SqliteEventStore(connectionString);
            await store.InitializeAsync();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using SqliteCommand seed = connection.CreateCommand();
            seed.CommandText = """
                INSERT INTO domain_events(session_id, event_name, schema_version, payload)
                VALUES($session, 'modelReasoningObserved', 1, $reasoning),
                      ($session, 'sessionCreated', 1, $ordinary);
                """;
            seed.Parameters.AddWithValue("$session", Guid.NewGuid().ToString("D"));
            seed.Parameters.AddWithValue("$reasoning", "{\"Text\":\"reasoning-canary\"}");
            seed.Parameters.AddWithValue("$ordinary", "{\"Name\":\"ordinary\"}");
            await seed.ExecuteNonQueryAsync();

            await new ReasoningPrivacyMigration().ApplyAsync(connection);

            await using SqliteCommand inspect = connection.CreateCommand();
            inspect.CommandText = "SELECT event_name, payload FROM domain_events ORDER BY sequence;";
            await using SqliteDataReader reader = await inspect.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("sessionCreated", reader.GetString(0));
            Assert.DoesNotContain("reasoning-canary", reader.GetString(1), StringComparison.Ordinal);
            Assert.False(await reader.ReadAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The event store rejects reasoning text even if an obsolete caller attempts an append.</summary>
    [Fact]
    public static async Task EventStore_ReasoningEvent_IsRejected()
    {
        var path = Path.Combine(Path.GetTempPath(), $"threadsmith-plan46-{Guid.NewGuid():N}.db");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString();
        try
        {
            var store = new SqliteEventStore(connectionString);
            await store.InitializeAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendAsync(
                new ModelReasoningObserved(SessionId.New(), DateTimeOffset.UtcNow, "reasoning-canary")));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
