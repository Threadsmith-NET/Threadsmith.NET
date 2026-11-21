namespace Threadsmith.ConversationContext.Tests;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Plan 33 conversation archive, provenance, persistence, and tolerance tests.</summary>
public static class Plan33ConversationArchiveTests
{
    private static ConversationMessage CreateMessage(
        SessionId sessionId,
        RunId runId,
        ConversationRole role,
        string content)
    {
        return new ConversationMessage
        {
            Id = ConversationMessageId.New(),
            SessionId = sessionId,
            RunId = runId,
            Sequence = 0,
            Role = role,
            Content = content,
            ContentHash = "pending",
            EstimatedTokens = 1,
            OccurredAt = DateTimeOffset.UtcNow,
        };
    }
}

internal sealed class ConversationFixture : IAsyncDisposable
{
    private readonly string _directory;

    private ConversationFixture(
        string directory,
        string connectionString,
        ArtifactStore artifacts,
        SqliteConversationStore store)
    {
        _directory = directory;
        ConnectionString = connectionString;
        Artifacts = artifacts;
        Store = store;
    }

    internal string ConnectionString { get; }

    internal string DirectoryPath => _directory;

    internal ArtifactStore Artifacts { get; }

    internal SqliteConversationStore Store { get; }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }

    internal static async Task<ConversationFixture> CreateAsync(int artifactThreshold = 16_384)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"threadsmith-m74-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var connectionString = $"Data Source={Path.Combine(directory, "state.db")};Pooling=False";
        await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync();
        var sanitizer = new SecretOutputSanitizer();
        var artifacts = new ArtifactStore(
            connectionString,
            Path.Combine(directory, "artifacts"),
            sanitizer);
        await artifacts.InitializeAsync();
        var store = new SqliteConversationStore(
            connectionString,
            artifacts,
            sanitizer,
            artifactThresholdCharacters: artifactThreshold);
        return new ConversationFixture(directory, connectionString, artifacts, store);
    }

    internal async Task<SqliteConversationStore> ReopenStoreAsync(int artifactThreshold = 16_384)
    {
        var sanitizer = new SecretOutputSanitizer();
        var artifacts = new ArtifactStore(
            ConnectionString,
            Path.Combine(_directory, "artifacts"),
            sanitizer);
        await artifacts.InitializeAsync();
        return new SqliteConversationStore(
            ConnectionString,
            artifacts,
            sanitizer,
            artifactThresholdCharacters: artifactThreshold);
    }

    internal async Task SeedHistoricalMemoryAsync(
        SessionId sessionId,
        IReadOnlyList<ConversationMemoryItem> items,
        ConversationSummarySnapshot summary)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        foreach (var item in items)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO conversation_memory(
                    memory_id, session_id, kind, content, repository_revision,
                    repository_dependent, supersedes_id, validity, created_at, updated_at, schema_version)
                VALUES($id, $session, $kind, $content, $revision, $dependent, NULL, $validity, $created, $updated, $schema);
                """;
            command.Parameters.AddWithValue("$id", item.Id.Value.ToString("D"));
            command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
            command.Parameters.AddWithValue("$kind", (int)item.Kind);
            command.Parameters.AddWithValue("$content", item.Content);
            command.Parameters.AddWithValue("$revision", (object?)item.RepositoryRevision ?? DBNull.Value);
            command.Parameters.AddWithValue("$dependent", item.RepositoryDependent ? 1 : 0);
            command.Parameters.AddWithValue("$validity", (int)item.Validity);
            command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updated", item.UpdatedAt.ToString("O"));
            command.Parameters.AddWithValue("$schema", item.SchemaVersion);
            await command.ExecuteNonQueryAsync();

            (string Kind, IEnumerable<string> Ids)[] sources =
            [
                ("message", item.SourceMessageIds.Select(id => id.Value.ToString("D"))),
                ("run", item.SourceRunIds.Select(id => id.Value.ToString("D"))),
                ("evidence", item.SourceEvidenceIds.Select(id => id.Value.ToString("D"))),
                ("artifact", item.SourceArtifactIds),
            ];
            foreach (var source in sources)
            {
                var ordinal = 0;
                foreach (var sourceId in source.Ids)
                {
                    await using var sourceCommand = connection.CreateCommand();
                    sourceCommand.Transaction = transaction;
                    sourceCommand.CommandText = """
                        INSERT INTO conversation_memory_sources(memory_id, source_kind, source_id, ordinal)
                        VALUES($memory, $kind, $source, $ordinal);
                        """;
                    sourceCommand.Parameters.AddWithValue("$memory", item.Id.Value.ToString("D"));
                    sourceCommand.Parameters.AddWithValue("$kind", source.Kind);
                    sourceCommand.Parameters.AddWithValue("$source", sourceId);
                    sourceCommand.Parameters.AddWithValue("$ordinal", ordinal++);
                    await sourceCommand.ExecuteNonQueryAsync();
                }
            }
        }

        await using var summaryCommand = connection.CreateCommand();
        summaryCommand.Transaction = transaction;
        summaryCommand.CommandText = """
            INSERT INTO conversation_summaries(
                session_id, version, through_message_sequence, repository_revision,
                memory_index_json, created_at, schema_version)
            VALUES($session, $version, $through, $revision, $index, $created, $schema);
            """;
        summaryCommand.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        summaryCommand.Parameters.AddWithValue("$version", summary.Version);
        summaryCommand.Parameters.AddWithValue("$through", summary.ThroughMessageSequence);
        summaryCommand.Parameters.AddWithValue("$revision", (object?)summary.RepositoryRevision ?? DBNull.Value);
        summaryCommand.Parameters.AddWithValue("$index", JsonSerializer.Serialize(summary.MemoryIdsByKind));
        summaryCommand.Parameters.AddWithValue("$created", summary.CreatedAt.ToString("O"));
        summaryCommand.Parameters.AddWithValue("$schema", summary.SchemaVersion);
        await summaryCommand.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    internal async Task InsertFutureMemoryAsync(SessionId sessionId, int schemaVersion)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversation_memory(
                memory_id, session_id, kind, content, repository_revision,
                repository_dependent, supersedes_id, validity, created_at, updated_at, schema_version)
            VALUES($id, $session, 0, 'future', NULL, 0, NULL, 0, $now, $now, $schema);
            """;
        command.Parameters.AddWithValue("$id", ConversationMemoryId.New().Value.ToString("D"));
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$schema", schemaVersion);
        await command.ExecuteNonQueryAsync();
    }
}
