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
    /// <summary>Stable message and memory identifiers preserve value equality through JSON.</summary>
    [Fact]
    public static void Conversation_identifiers_round_trip()
    {
        var messageId = ConversationMessageId.New();
        var memoryId = ConversationMemoryId.New();

        var json = JsonSerializer.Serialize(new IdentifierPair(messageId, memoryId));
        var restored = JsonSerializer.Deserialize<IdentifierPair>(json);

        Assert.NotNull(restored);
        Assert.Equal(messageId, restored.MessageId);
        Assert.Equal(memoryId, restored.MemoryId);
    }

    /// <summary>Concurrent accepted messages receive one deterministic contiguous session order.</summary>
    [Fact]
    public static async Task Concurrent_capture_has_contiguous_unique_sequence()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var sessionId = SessionId.New();
        var runId = RunId.New();
        Task<ConversationMessage>[] captures =
        [
            .. Enumerable.Range(0, 20)
                .Select(index => fixture.Store.ArchiveMessageAsync(CreateMessage(
                    sessionId,
                    runId,
                    ConversationRole.User,
                    $"message-{index:D2}"))),
        ];

        var archived = await Task.WhenAll(captures);
        var snapshot = await fixture.Store.GetSnapshotAsync(sessionId);

        Assert.Equal(Enumerable.Range(1, 20).Select(value => (long)value), archived.Select(item => item.Sequence).Order());
        Assert.Equal(Enumerable.Range(1, 20).Select(value => (long)value), snapshot.Messages.Select(item => item.Sequence));
    }

    /// <summary>Sanitization precedes inline persistence and hidden/provider/tool data has no archive path.</summary>
    [Fact]
    public static async Task Archive_contains_only_sanitized_visible_message_content()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var sessionId = SessionId.New();
        await fixture.Store.ArchiveMessageAsync(CreateMessage(
            sessionId,
            RunId.New(),
            ConversationRole.Assistant,
            "visible token=top-secret\u0001"));

        var snapshot = await fixture.Store.GetSnapshotAsync(sessionId);

        var message = Assert.Single(snapshot.Messages);
        Assert.Equal("visible token=[REDACTED]", message.Content);
        Assert.DoesNotContain("top-secret", message.Content, StringComparison.Ordinal);
        Assert.DoesNotContain('\u0001', message.Content ?? string.Empty);
        Assert.DoesNotContain(nameof(ModelReasoningObserved), JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(ToolInvocationCompleted), JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
    }

    /// <summary>Large bodies use content-addressed artifacts and hash verification restores them.</summary>
    [Fact]
    public static async Task Large_message_uses_artifact_and_restores_verified_body()
    {
        await using var fixture = await ConversationFixture.CreateAsync(artifactThreshold: 32);
        var sessionId = SessionId.New();
        string body = new('x', 256);

        var archived = await fixture.Store.ArchiveMessageAsync(
            CreateMessage(sessionId, RunId.New(), ConversationRole.User, body));
        var restored = await fixture.Store.GetSnapshotAsync(sessionId);

        Assert.Null(archived.Content);
        Assert.NotNull(archived.ArtifactId);
        Assert.Equal(body, Assert.Single(restored.Messages).Content);
    }

    /// <summary>All seven categories and their provenance edges survive an atomic summary replacement.</summary>
    [Fact]
    public static async Task Structured_memory_and_provenance_round_trip()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var message = await fixture.Store.ArchiveMessageAsync(
            CreateMessage(sessionId, runId, ConversationRole.User, "retain this"));
        var now = DateTimeOffset.UtcNow;
        ConversationMemoryItem[] items =
        [
            .. Enum.GetValues<ConversationMemoryKind>()
                .Select(kind => new ConversationMemoryItem
                {
                    Id = ConversationMemoryId.New(),
                    SessionId = sessionId,
                    Kind = kind,
                    Content = $"memory-{kind}",
                    SourceMessageIds = [message.Id],
                    SourceRunIds = [runId],
                    SourceArtifactIds = ["artifact-reference"],
                    CreatedAt = now,
                    UpdatedAt = now,
                }),
        ];
        var snapshot = new ConversationSummarySnapshot
        {
            SessionId = sessionId,
            Version = 1,
            ThroughMessageSequence = message.Sequence,
            MemoryIdsByKind = items.ToDictionary(
                item => item.Kind,
                item => (IReadOnlyList<ConversationMemoryId>)[item.Id]),
            CreatedAt = now,
        };

        await fixture.Store.ReplaceSummaryAsync(sessionId, items, snapshot);
        var restored = await fixture.Store.GetSnapshotAsync(sessionId);

        Assert.Equal(Enum.GetValues<ConversationMemoryKind>().Length, restored.MemoryItems.Count);
        Assert.All(restored.MemoryItems, item =>
        {
            Assert.Equal(message.Id, Assert.Single(item.SourceMessageIds));
            Assert.Equal(runId, Assert.Single(item.SourceRunIds));
            Assert.Equal("artifact-reference", Assert.Single(item.SourceArtifactIds));
        });
        Assert.Equal(1, restored.Summary?.Version);
    }

    /// <summary>Mode changes survive restore without deleting archive or governed memory.</summary>
    [Fact]
    public static async Task Mode_change_preserves_underlying_archive()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var sessionId = SessionId.New();
        await fixture.Store.ArchiveMessageAsync(CreateMessage(
            sessionId,
            RunId.New(),
            ConversationRole.User,
            "preserved"));

        await fixture.Store.SetModeAsync(sessionId, ConversationContextMode.Stateless);
        var snapshot = await fixture.Store.GetSnapshotAsync(sessionId);

        Assert.Equal(ConversationContextMode.Stateless, snapshot.Mode);
        Assert.Single(snapshot.Messages);
    }

    /// <summary>Unknown future memory schema is skipped with a bounded warning rather than fabricated.</summary>
    [Fact]
    public static async Task Unknown_memory_schema_produces_warning_and_no_fabricated_item()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var sessionId = SessionId.New();
        await fixture.InsertFutureMemoryAsync(sessionId, schemaVersion: 99);

        var snapshot = await fixture.Store.GetSnapshotAsync(sessionId);

        Assert.Empty(snapshot.MemoryItems);
        Assert.Contains(snapshot.Warnings, warning => warning == "UnsupportedConversationMemorySchema:99");
    }

    /// <summary>Retention removes bodies but preserves ordered message provenance metadata.</summary>
    [Fact]
    public static async Task Retention_removes_body_without_orphaning_metadata()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var sessionId = SessionId.New();
        var archived = await fixture.Store.ArchiveMessageAsync(CreateMessage(
            sessionId,
            RunId.New(),
            ConversationRole.User,
            "old body") with
        { OccurredAt = DateTimeOffset.UtcNow.AddDays(-10) });

        var removed = await fixture.Store.RemoveMessageBodiesOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-1));
        var snapshot = await fixture.Store.GetSnapshotAsync(sessionId);

        Assert.Equal(1, removed);
        var retained = Assert.Single(snapshot.Messages);
        Assert.Equal(archived.Id, retained.Id);
        Assert.Equal(archived.ContentHash, retained.ContentHash);
        Assert.Null(retained.Content);
    }

    /// <summary>Retention detaches artifact-backed bodies so reads cannot rehydrate expired content.</summary>
    [Fact]
    public static async Task Retention_detaches_artifact_backed_body()
    {
        await using var fixture = await ConversationFixture.CreateAsync(artifactThreshold: 8);
        var sessionId = SessionId.New();
        var archived = await fixture.Store.ArchiveMessageAsync(CreateMessage(
            sessionId,
            RunId.New(),
            ConversationRole.User,
            new string('x', 128)) with
        { OccurredAt = DateTimeOffset.UtcNow.AddDays(-10) });

        var removed = await fixture.Store.RemoveMessageBodiesOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-1));
        var snapshot = await fixture.Store.GetSnapshotAsync(sessionId);

        Assert.NotNull(archived.ArtifactId);
        Assert.Equal(1, removed);
        var retained = Assert.Single(snapshot.Messages);
        Assert.Null(retained.Content);
        Assert.Null(retained.ArtifactId);
        Assert.Null(await fixture.Artifacts.ReadAsync(archived.ContentHash));
    }

    /// <summary>Retention preserves a deduplicated artifact while a retained message still references it.</summary>
    [Fact]
    public static async Task Retention_preserves_artifact_referenced_by_newer_message()
    {
        await using var fixture = await ConversationFixture.CreateAsync(artifactThreshold: 8);
        var sessionId = SessionId.New();
        string body = new('x', 128);
        var oldMessage = await fixture.Store.ArchiveMessageAsync(CreateMessage(
            sessionId,
            RunId.New(),
            ConversationRole.User,
            body) with
        { OccurredAt = DateTimeOffset.UtcNow.AddDays(-10) });
        var newMessage = await fixture.Store.ArchiveMessageAsync(CreateMessage(
            sessionId,
            RunId.New(),
            ConversationRole.Assistant,
            body));

        var removed = await fixture.Store.RemoveMessageBodiesOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-1));

        Assert.Equal(1, removed);
        Assert.Equal(oldMessage.ArtifactId, newMessage.ArtifactId);
        Assert.Equal(body, await fixture.Artifacts.ReadAsync(newMessage.ContentHash));
    }

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

    private sealed record IdentifierPair(
        ConversationMessageId MessageId,
        ConversationMemoryId MemoryId);
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
