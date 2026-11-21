namespace Threadsmith.PersistenceMcpHardening.Tests;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Real SQLite contracts for explicit memories, snapshots, revisions, eviction and migration.</summary>
public sealed class SqliteManagedRepositoryMemoryStoreTests
{
    private static readonly TextEmbeddingModelDescriptor Model = new("test-space", 3, 256);
    private static readonly TextEmbeddingResult Embedding = new(new float[] { 1, 0, 0 }, 5, false);

    /// <summary>Warning thresholds are nonnegative configuration, independently of storage capacity.</summary>
    [Fact]
    public void Standing_preference_warning_threshold_rejects_negative_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RepositoryMemoryOptions
        {
            StandingPreferenceWarningThreshold = -1,
        }.Validate());
    }

    private static RepositoryMemoryWrite Write(string text, RepositoryMemoryType memoryType = RepositoryMemoryType.Situational)
    {
        return new()
        {
            Text = text,
            Origin = RepositoryMemoryOrigin.Manual,
            MemoryType = memoryType,
        };
    }

    private static async Task PrepareVersionNineWithOverCapacityManualEntriesAsync(MemoryDatabase fixture)
    {
        await new MigrationRunner(fixture.ConnectionString, DefaultMigrations.All.Take(10)).RunAsync();
        for (var index = 0; index < 22; index++)
        {
            await InsertLegacyAsync(
                fixture,
                RepositoryMemoryId.New(),
                RepositoryMemoryAuthority.UserAuthored,
                RepositoryMemoryValidity.Active,
                userCommand: true,
                "Manual memory " + index);
        }
    }

    private static async Task InsertVersionTenManagedEntriesAsync(string connectionString, string repositoryIdentity, int count)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        for (var index = 0; index < count; index++)
        {
            var text = "Version ten memory " + index;
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO managed_memories(memory_id, repository_identity, text, content_hash, content_revision,
                    origin, sensitivity, created_at, updated_at, embedding_dimensions, inclusion_count)
                VALUES($id, $repository, $text, $hash, 1, 'manual', 0, $now, $now, 0, 0);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$repository", repositoryIdentity);
            command.Parameters.AddWithValue("$text", text);
            command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant());
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task InsertLegacyAsync(
        MemoryDatabase fixture,
        RepositoryMemoryId id,
        RepositoryMemoryAuthority authority,
        RepositoryMemoryValidity validity,
        bool userCommand,
        string text)
    {
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO repository_memory(memory_id, repository_identity, kind, authority, validity, sensitivity,
                content, content_hash, created_at, updated_at, schema_version)
            VALUES($id, 'repo', 0, $authority, $validity, 0, $text, '', $now, $now, 1);
            INSERT INTO repository_memory_sources(memory_id, source_kind, source_id, ordinal)
            VALUES($id, $source, 'explicit-fixture', 0);
            """;
        command.Parameters.AddWithValue("$id", id.Value.ToString());
        command.Parameters.AddWithValue("$authority", (int)authority);
        command.Parameters.AddWithValue("$validity", (int)validity);
        command.Parameters.AddWithValue("$source", userCommand ? (int)RepositoryMemorySourceKind.UserCommand : (int)RepositoryMemorySourceKind.Run);
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$now", fixture.Clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private sealed class MemoryClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
        {
            return _now;
        }

        internal void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
        }
    }

    private sealed class FailingMemoryTypeMigration : IDatabaseMigration
    {
        public int Version => 11;

        public string Name => "Injected failing memory type migration";

        public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT missing_memory_type_column FROM managed_memories;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private sealed class CancellingMemoryTypeMigration : IDatabaseMigration
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingMemoryTypeMigration(CancellationTokenSource cancellation)
        {
            ArgumentNullException.ThrowIfNull(cancellation);
            _cancellation = cancellation;
        }

        public int Version => 11;

        public string Name => "Injected cancelled memory type migration";

        public async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(connection);
            await _cancellation.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private sealed class MemoryDatabase : IAsyncDisposable
    {
        private readonly string _directory;

        private MemoryDatabase(string directory, string relativeDatabasePath)
        {
            _directory = directory;
            var databasePath = Path.Combine(directory, relativeDatabasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? directory);
            ConnectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
            Store = new SqliteManagedRepositoryMemoryStore(ConnectionString, Clock);
        }

        internal string DirectoryPath => _directory;

        internal string ConnectionString { get; }

        internal MemoryClock Clock { get; } = new();

        internal SqliteManagedRepositoryMemoryStore Store { get; }

        public ValueTask DisposeAsync()
        {
            Store.Dispose();
            SqliteConnection.ClearAllPools();
            var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            var actualParent = Directory.GetParent(_directory)?.FullName;
            if (!string.Equals(actualParent, expectedParent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(_directory).StartsWith("threadsmith-plan103-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing cleanup outside the exact owned fixture directory.");
            }

            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }

        internal static async Task<MemoryDatabase> CreateAsync(bool migrate = true, string relativeDatabasePath = "memory.db")
        {
            var directory = Path.Combine(Path.GetTempPath(), "threadsmith-plan103-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var fixture = new MemoryDatabase(directory, relativeDatabasePath);
            await new SqliteEventStore(fixture.ConnectionString).InitializeAsync();
            if (migrate)
            {
                await new MigrationRunner(fixture.ConnectionString, DefaultMigrations.All).RunAsync();
            }

            return fixture;
        }

        internal async Task ExecuteAsync(string sql)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        internal async Task<int> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
    }
}
