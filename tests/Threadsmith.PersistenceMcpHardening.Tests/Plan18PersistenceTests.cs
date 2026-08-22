// Threadsmith.NET Milestone 8 — Plan 18: Persistence completion and session restoration tests.
//
// Covers: migration safety (§19.5), event/schema-version tolerance (gap #3), artifact storage (§19.3),
// retention (§19.6), session round-trip (scenarios B/H), and redaction-before-persist.
namespace Threadsmith.PersistenceMcpHardening.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Migration framework safety tests (§19.5).</summary>
public static class MigrationSafetyTests
{
    /// <summary>A failed migration rolls back while preserving previously persisted events.</summary>
    [Fact]
    public static async Task Migration_failure_rolls_back_and_leaves_prior_data_readable()
    {
        var fixture = await DatabaseFixture.CreateAsync();
        try
        {
            var store = new SqliteEventStore(fixture.ConnectionString);
            await store.InitializeAsync();

            var sessionId = SessionId.New();
            await store.AppendAsync(new SessionCreated(sessionId, DateTimeOffset.UtcNow, "pre-migration"));

            var migrations = new IDatabaseMigration[]
            {
                new InitialSchemaMigration(),
                new FailingMigration(),
            };
            var runner = new MigrationRunner(fixture.ConnectionString, migrations);
            await Assert.ThrowsAsync<NotImplementedException>(() => runner.RunAsync());

            var events = await store.ReadAsync(sessionId);
            Assert.Single(events);
            Assert.IsType<SessionCreated>(events[0]);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    /// <summary>Re-running completed migrations leaves the schema version unchanged.</summary>
    [Fact]
    public static async Task Migration_is_idempotent_when_re_run()
    {
        var fixture = await DatabaseFixture.CreateAsync();
        try
        {
            var runner = new MigrationRunner(fixture.ConnectionString, DefaultMigrations.All);
            var v1 = await runner.RunAsync();
            var v2 = await runner.RunAsync();
            Assert.Equal(v1, v2);
            Assert.Equal(9, v2);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private sealed class FailingMigration : IDatabaseMigration
    {
        public int Version => 1;
        public string Name => "Failing";
        public Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException("Simulated migration failure.");
        }
    }
}

/// <summary>Event/schema-version tolerance tests (gap #3).</summary>
public static class VersionToleranceTests
{
    /// <summary>Pre-mutation analysis events are registered for durable event serialization.</summary>
    [Fact]
    public static void PreMutationAnalysisCompleted_serializes_with_registered_discriminator()
    {
        var sessionId = SessionId.New();
        var domainEvent = new PreMutationAnalysisCompleted(
            sessionId,
            DateTimeOffset.UtcNow,
            RunId.New(),
            MutationSetId.New(),
            PreMutationGateDecision.PassedCheapGates,
            DiagnosticCount: 0,
            BlockingDiagnosticCount: 0,
            OmissionCount: 1,
            SemanticConfidenceLevel.PartialCompilation);

        var discriminator = DomainEventJson.GetDiscriminator(domainEvent);
        var payload = DomainEventJson.Serialize(domainEvent);
        var roundTripped = DomainEventJson.Deserialize(discriminator, schemaVersion: 1, payload);

        Assert.Equal("preMutationAnalysisCompleted", discriminator);
        var typed = Assert.IsType<PreMutationAnalysisCompleted>(roundTripped);
        Assert.Equal(domainEvent.MutationSetId, typed.MutationSetId);
        Assert.Equal(domainEvent.Decision, typed.Decision);
        Assert.Equal(domainEvent.OmissionCount, typed.OmissionCount);
    }

    /// <summary>Restoration migrates an older event schema and replays it successfully.</summary>
    [Fact]
    public static async Task Restore_migrates_older_event_schema_without_crashing()
    {
        var fixture = await DatabaseFixture.CreateAsync();
        try
        {
            var store = new SqliteEventStore(fixture.ConnectionString);
            await store.InitializeAsync();
            var sessionId = SessionId.New();
            // Persist a real event but record it at an older schema version to exercise the migrator.
            var legacyEvent = new SessionCreated(sessionId, DateTimeOffset.Parse("2026-01-01T00:00:00Z"), "legacy");
            var payload = DomainEventJson.Serialize(legacyEvent);
            await InsertRowAsync(fixture.ConnectionString, sessionId, "sessionCreated", 0, payload);

            var registry = new DomainEventMigrationRegistry(
                [new SessionCreatedMigrator()],
                currentVersion: 1);
            var restorer = new SessionRestorer(store, registry, NullLogger<SessionRestorer>.Instance);
            var projections = new InMemoryProjectionStore();

            var result = await restorer.RestoreAsync(sessionId, projections, CancellationToken.None);

            Assert.True(result.Succeeded);
            if (result.LegacyEvents != 0)
            {
                Assert.Fail($"Unexpected legacy events: {result.Warnings}");
            }

            Assert.Equal(1, result.MigratedEvents);
            Assert.False(result.IsLegacy);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    /// <summary>Restoration marks an unmigratable future event as legacy without failing.</summary>
    [Fact]
    public static async Task Restore_marks_unmigratable_event_legacy_without_crashing()
    {
        var fixture = await DatabaseFixture.CreateAsync();
        try
        {
            var store = new SqliteEventStore(fixture.ConnectionString);
            await store.InitializeAsync();
            var sessionId = SessionId.New();
            var futureEvent = new SessionCreated(sessionId, DateTimeOffset.UtcNow, "future");
            var payload = DomainEventJson.Serialize(futureEvent);
            await InsertRowAsync(fixture.ConnectionString, sessionId, "sessionCreated", 99, payload);

            var registry = new DomainEventMigrationRegistry(Array.Empty<IDomainEventMigrator>(), currentVersion: 1);
            var restorer = new SessionRestorer(store, registry, NullLogger<SessionRestorer>.Instance);
            var projections = new InMemoryProjectionStore();

            var result = await restorer.RestoreAsync(sessionId, projections, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(1, result.LegacyEvents);
            Assert.True(result.IsLegacy);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task InsertRowAsync(string cs, SessionId sessionId, string name, int version, string payload)
    {
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO domain_events(session_id, event_name, schema_version, payload)
            VALUES($session, $name, $version, $payload);
            """;
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$payload", payload);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>A sample migrator that lifts a v0 sessionCreated event to the v1 schema.</summary>
    private sealed class SessionCreatedMigrator : IDomainEventMigrator
    {
        public string Discriminator => "sessionCreated";
        public int FromVersion => 0;
        public int ToVersion => 1;
        public string Migrate(string oldJson)
        {
            return oldJson;
        }
    }
}

/// <summary>Artifact store tests (§19.3).</summary>
public static class ArtifactStoreTests
{
    /// <summary>Identical artifact content shares one content-addressed stored artifact.</summary>
    [Fact]
    public static async Task Artifact_is_content_addressed_and_deduplicated()
    {
        var fixture = await DatabaseFixture.CreateAsync();
        var dir = DirectoryFixture.Create();
        try
        {
            var store = new ArtifactStore(fixture.ConnectionString, dir.Path, new SecretOutputSanitizer());
            await store.InitializeAsync();
            var session = SessionId.New();

            var m1 = await store.StoreAsync("hello world", "processOutput", session, CancellationToken.None);
            var m2 = await store.StoreAsync("hello world", "processOutput", session, CancellationToken.None);

            Assert.Equal(m1.ContentHash, m2.ContentHash);
            var list = await store.ListAsync(session, CancellationToken.None);
            Assert.Single(list);

            var read = await store.ReadAsync(m1.ContentHash, CancellationToken.None);
            Assert.Equal("hello world", read);
        }
        finally
        {
            await fixture.DisposeAsync();
            dir.Dispose();
        }
    }

    /// <summary>Artifact content is redacted before it reaches persistent storage.</summary>
    [Fact]
    public static async Task Artifact_is_redacted_before_persist()
    {
        var fixture = await DatabaseFixture.CreateAsync();
        var dir = DirectoryFixture.Create();
        try
        {
            var store = new ArtifactStore(fixture.ConnectionString, dir.Path, new SecretOutputSanitizer());
            await store.InitializeAsync();
            var session = SessionId.New();

            var m = await store.StoreAsync(
                "api_key=sk-abcdefghijklmnopqrstuvwxyz1234567890 data",
                "processOutput",
                session,
                CancellationToken.None);

            var read = await store.ReadAsync(m.ContentHash, CancellationToken.None);
            Assert.NotNull(read);
            Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz1234567890", read);
            Assert.Contains("[REDACTED]", read);
        }
        finally
        {
            await fixture.DisposeAsync();
            dir.Dispose();
        }
    }
}

/// <summary>Retention policy tests (§19.6).</summary>
public static class RetentionTests
{
    /// <summary>General artifact cleanup preserves bodies still referenced by retained conversation rows.</summary>
    [Fact]
    public static async Task Retention_preserves_referenced_conversation_artifact()
    {
        var fixture = await DatabaseFixture.CreateAsync();
        var dir = DirectoryFixture.Create();
        try
        {
            await new MigrationRunner(fixture.ConnectionString, DefaultMigrations.All).RunAsync();
            var store = new SqliteEventStore(fixture.ConnectionString);
            await store.InitializeAsync();
            var oldClock = new FixedTimeProvider(DateTimeOffset.UtcNow.AddDays(-90));
            var artifactStore = new ArtifactStore(
                fixture.ConnectionString,
                dir.Path,
                new SecretOutputSanitizer(),
                oldClock);
            await artifactStore.InitializeAsync();
            var conversationStore = new SqliteConversationStore(
                fixture.ConnectionString,
                artifactStore,
                new SecretOutputSanitizer(),
                artifactThresholdCharacters: 8,
                timeProvider: oldClock);
            var sessionId = SessionId.New();
            string body = new('x', 128);
            var archived = await conversationStore.ArchiveMessageAsync(new ConversationMessage
            {
                Id = ConversationMessageId.New(),
                SessionId = sessionId,
                RunId = RunId.New(),
                Sequence = 0,
                Role = ConversationRole.User,
                Content = body,
                ContentHash = "pending",
                EstimatedTokens = 1,
                OccurredAt = oldClock.GetUtcNow(),
            });
            var retention = new RetentionService(
                store,
                artifactStore,
                new RetentionOptions
                {
                    Enabled = true,
                    SessionAge = TimeSpan.FromDays(30),
                    RetainConversationBodies = true,
                },
                NullLogger<RetentionService>.Instance,
                conversationStore: conversationStore);

            var outcome = await retention.RunAsync();

            Assert.Equal(0, outcome.RemovedArtifacts);
            Assert.Equal(body, await artifactStore.ReadAsync(archived.ContentHash, CancellationToken.None));
            var retained = Assert.Single(
                (await conversationStore.GetSnapshotAsync(sessionId)).Messages);
            Assert.Equal(body, retained.Content);
            Assert.Equal(archived.ArtifactId, retained.ArtifactId);
            Assert.DoesNotContain("MissingConversationArtifact", (await conversationStore.GetSnapshotAsync(sessionId)).Warnings);
        }
        finally
        {
            await fixture.DisposeAsync();
            dir.Dispose();
        }
    }

    /// <summary>Enabled retention removes both aged artifact metadata and its content file.</summary>
    [Fact]
    public static async Task Retention_deletes_aged_artifact_body_and_metadata()
    {
        var fixture = await DatabaseFixture.CreateAsync();
        var dir = DirectoryFixture.Create();
        try
        {
            var store = new SqliteEventStore(fixture.ConnectionString);
            await store.InitializeAsync();
            var oldClock = new FixedTimeProvider(DateTimeOffset.UtcNow.AddDays(-90));
            var artifactStore = new ArtifactStore(
                fixture.ConnectionString,
                dir.Path,
                new SecretOutputSanitizer(),
                oldClock);
            await artifactStore.InitializeAsync();
            var artifact = await artifactStore.StoreAsync(
                "old process output",
                "processOutput",
                sessionId: null,
                CancellationToken.None);
            var retention = new RetentionService(
                store,
                artifactStore,
                new RetentionOptions
                {
                    Enabled = true,
                    SessionAge = TimeSpan.FromDays(30),
                    RetainProcessLogs = false,
                },
                NullLogger<RetentionService>.Instance);

            var outcome = await retention.RunAsync();

            Assert.Equal(1, outcome.RemovedArtifacts);
            Assert.Null(await artifactStore.ReadAsync(artifact.ContentHash, CancellationToken.None));
            Assert.Empty(await artifactStore.ListAsync(sessionId: null, CancellationToken.None));
        }
        finally
        {
            await fixture.DisposeAsync();
            dir.Dispose();
        }
    }

    /// <summary>Enabled retention removes sessions older than the configured age.</summary>
    [Fact]
    public static async Task Retention_removes_aged_sessions()
    {
        var fixture = await DatabaseFixture.CreateAsync();
        var dir = DirectoryFixture.Create();
        try
        {
            var store = new SqliteEventStore(fixture.ConnectionString);
            await store.InitializeAsync();
            var oldSession = SessionId.New();
            await store.AppendAsync(new SessionCreated(oldSession, DateTimeOffset.UtcNow.AddDays(-90), "old"));

            var artifactStore = new ArtifactStore(fixture.ConnectionString, dir.Path, new SecretOutputSanitizer());
            await artifactStore.InitializeAsync();
            var retention = new RetentionService(
                store,
                artifactStore,
                new RetentionOptions { Enabled = true, SessionAge = TimeSpan.FromDays(30) },
                NullLogger<RetentionService>.Instance);

            var outcome = await retention.RunAsync();

            Assert.True(outcome.RemovedSessions >= 1);
            var remaining = await store.ListSessionsAsync();
            Assert.DoesNotContain(oldSession, remaining);
        }
        finally
        {
            await fixture.DisposeAsync();
            dir.Dispose();
        }
    }

    /// <summary>Disabled retention leaves aged sessions untouched.</summary>
    [Fact]
    public static async Task Retention_disabled_removes_nothing()
    {
        var fixture = await DatabaseFixture.CreateAsync();
        var dir = DirectoryFixture.Create();
        try
        {
            var store = new SqliteEventStore(fixture.ConnectionString);
            await store.InitializeAsync();
            var session = SessionId.New();
            await store.AppendAsync(new SessionCreated(session, DateTimeOffset.UtcNow.AddDays(-90), "kept"));

            var artifactStore = new ArtifactStore(fixture.ConnectionString, dir.Path, new SecretOutputSanitizer());
            await artifactStore.InitializeAsync();
            var retention = new RetentionService(
                store,
                artifactStore,
                new RetentionOptions { Enabled = false },
                NullLogger<RetentionService>.Instance);

            var outcome = await retention.RunAsync();
            Assert.Equal(0, outcome.RemovedSessions);
        }
        finally
        {
            await fixture.DisposeAsync();
            dir.Dispose();
        }
    }
}

/// <summary>Session round-trip restore tests (scenarios B/H).</summary>
public static class SessionRoundTripTests
{
    /// <summary>Persisted session events restore into equivalent host projections.</summary>
    [Fact]
    public static async Task Persisted_session_restores_into_matching_projections()
    {
        var fixture = await DatabaseFixture.CreateAsync();
        try
        {
            var store = new SqliteEventStore(fixture.ConnectionString);
            await store.InitializeAsync();
            var session = SessionId.New();
            await store.AppendAsync(new SessionCreated(session, DateTimeOffset.UtcNow, "roundtrip"));
            await store.AppendAsync(new TaskIntentRecorded(session, DateTimeOffset.UtcNow, "Do the thing"));

            var registry = new DomainEventMigrationRegistry(Array.Empty<IDomainEventMigrator>(), currentVersion: 1);
            var restorer = new SessionRestorer(store, registry, NullLogger<SessionRestorer>.Instance);
            var projections = new InMemoryProjectionStore();

            var result = await restorer.RestoreAsync(session, projections, CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(2, result.ReplayedEvents);
            Assert.False(result.IsLegacy);

            var key = new ProjectionKey("session", session.Value.ToString("D"));
            var restored = await projections.GetAsync<SessionProjection>(key, CancellationToken.None);
            Assert.NotNull(restored);
            Assert.Equal("roundtrip", restored.Name);
            Assert.Equal("Do the thing", restored.Intent);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}

/// <summary>Creates a temp SQLite database with connection pooling disabled for deterministic teardown.</summary>
internal sealed class DatabaseFixture : IAsyncDisposable
{
    private readonly string _path;

    private DatabaseFixture(string path)
    {
        _path = path;
        ConnectionString = $"Data Source={path};Pooling=False";
    }

    public string ConnectionString { get; }

    public static async Task<DatabaseFixture> CreateAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"m8-{Guid.NewGuid():N}.db");
        await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        await connection.OpenAsync();
        return new DatabaseFixture(path);
    }

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}

/// <summary>Creates a temp directory for artifact storage with deterministic teardown.</summary>
internal sealed class DirectoryFixture : IDisposable
{
    private DirectoryFixture(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static DirectoryFixture Create()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"m8-dir-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(path);
        return new DirectoryFixture(path);
    }

    public void Dispose()
    {
        if (System.IO.Directory.Exists(Path))
        {
            System.IO.Directory.Delete(Path, recursive: true);
        }
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
{
    public override DateTimeOffset GetUtcNow()
    {
        return value;
    }
}
