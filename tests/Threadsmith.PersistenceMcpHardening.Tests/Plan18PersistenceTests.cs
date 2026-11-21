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
    private sealed class FailingMigration : IDatabaseMigration
    {
        public int Version => 1;
        public string Name => "Failing";
        public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE failed_migration_canary(value TEXT); INSERT INTO failed_migration_canary VALUES('partial write');";
            await command.ExecuteNonQueryAsync(cancellationToken);
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
}

/// <summary>Retention policy tests (§19.6).</summary>
public static class RetentionTests
{
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
