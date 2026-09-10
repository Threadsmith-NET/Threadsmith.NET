namespace Threadsmith.Persistence;

using System.Globalization;
using Microsoft.Data.Sqlite;

/// <summary>One ordered, idempotent schema migration (strategy §19.5).</summary>
/// <remarks>
/// A migration failure must not destroy prior session data. Ordinary migrations run in their own
/// transaction; the destructive managed-memory migration and all later pending migrations share
/// one transaction. A failure rolls back and the prior schema version remains readable.
/// Migrations must be idempotent so re-running against an already-migrated database is a no-op.
/// </remarks>
public interface IDatabaseMigration
{
    /// <summary>Gets the schema version this migration produces.</summary>
    int Version { get; }

    /// <summary>Gets the human-readable migration name.</summary>
    string Name { get; }

    /// <summary>Applies the migration within the supplied connection's transaction.</summary>
    /// <param name="connection">An open connection with an active transaction.</param>
    /// <param name="cancellationToken">A token that cancels the migration.</param>
    /// <returns>A task that completes when the migration is applied.</returns>
    Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default);
}

/// <summary>Ordered, transactional schema migration runner (strategy §19.5).</summary>
/// <remarks>
/// Records the current schema version in a <c>schema_version</c> table. A migration that fails rolls
/// back its transaction and leaves the prior version intact; the runner throws and the database
/// remains readable at the last successful version.
/// </remarks>
public sealed class MigrationRunner
{
    private readonly string _connectionString;
    private readonly IReadOnlyList<IDatabaseMigration> _migrations;

    /// <summary>Initializes a new instance of the <see cref="MigrationRunner"/> class.</summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="migrations">The ordered migrations to apply. Version 0 (initial schema) is included.</param>
    public MigrationRunner(string connectionString, IEnumerable<IDatabaseMigration> migrations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(migrations);
        _connectionString = connectionString;
        _migrations = migrations.OrderBy(m => m.Version).ToArray();
        for (var i = 0; i < _migrations.Count; i++)
        {
            if (_migrations[i].Version != i)
            {
                throw new ArgumentException(
                    $"Migrations must be contiguous starting at version 0; expected version {i} but found {_migrations[i].Version} ({_migrations[i].Name}).",
                    nameof(migrations));
            }
        }
    }

    /// <summary>Gets the registered migrations in order.</summary>
    public IReadOnlyList<IDatabaseMigration> Migrations => _migrations;

    /// <summary>Verified SQLite-consistent backup created before the managed-memory migration, when file-backed.</summary>
    public string? LastBackupPath { get; private set; }

    /// <summary>Applies every pending migration transactionally and returns the resulting schema version.</summary>
    /// <param name="cancellationToken">A token that cancels the run.</param>
    /// <returns>The final schema version.</returns>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaVersionTableAsync(connection, cancellationToken);
        var current = await ReadCurrentVersionAsync(connection, cancellationToken);
        var originalVersion = current;
        var pending = _migrations.Where(migration => migration.Version > current).ToArray();
        for (var index = 0; index < pending.Length; index++)
        {
            var migration = pending[index];
            cancellationToken.ThrowIfCancellationRequested();
            if (migration.Version == 10 && originalVersion > 0)
            {
                LastBackupPath = await CreateVerifiedMemoryBackupAsync(connection, cancellationToken);
            }

            IReadOnlyList<IDatabaseMigration> batch = migration.Version == 10 ? pending[index..] : [migration];
            current = await ApplyBatchAsync(connection, batch, cancellationToken);
            if (migration.Version == 10)
            {
                break;
            }
        }

        return current;
    }

    /// <summary>Reads the current schema version without applying migrations.</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The current schema version, or 0 when uninitialized.</returns>
    public async Task<int> ReadCurrentVersionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaVersionTableAsync(connection, cancellationToken);
        return await ReadCurrentVersionAsync(connection, cancellationToken);
    }

    private async Task<int> ApplyBatchAsync(
        SqliteConnection connection,
        IReadOnlyList<IDatabaseMigration> migrations,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var migration in migrations)
            {
                await migration.ApplyAsync(connection, cancellationToken);
                if (migration.Version == 10 && LastBackupPath is { } backupPath)
                {
                    await using var backupRecord = connection.CreateCommand();
                    backupRecord.Transaction = transaction;
                    backupRecord.CommandText = "UPDATE managed_memory_migration SET backup_path = $path WHERE version = 10;";
                    backupRecord.Parameters.AddWithValue("$path", backupPath);
                    await backupRecord.ExecuteNonQueryAsync(cancellationToken);
                }

                await WriteVersionAsync(connection, transaction, migration.Version, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return migrations[^1].Version;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Roll back: the prior version remains readable (§19.5). Re-throw so the caller
            // knows the migration failed; the database is still openable at the prior version.
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<string?> CreateVerifiedMemoryBackupAsync(
        SqliteConnection source,
        CancellationToken cancellationToken)
    {
        var sourcePath = source.DataSource;
        if (string.IsNullOrWhiteSpace(sourcePath) || sourcePath == ":memory:")
        {
            return null;
        }

        // SQLite's backup API includes committed WAL contents and never overwrites the source.
        // A unique sibling preserves earlier backups when a migration is retried after failure.
        var backupPath = Path.GetFullPath(sourcePath) + ".pre-managed-memory-v10."
            + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)
            + "." + Guid.NewGuid().ToString("N") + ".backup";
        var options = new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false };
        await using var backup = new SqliteConnection(options.ToString());
        await backup.OpenAsync(cancellationToken);
        await Task.Run(() => source.BackupDatabase(backup), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await using var verify = backup.CreateCommand();
        verify.CommandText = "PRAGMA quick_check;";
        if (!string.Equals(await verify.ExecuteScalarAsync(cancellationToken) as string, "ok", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Managed-memory migration requires a verified SQLite-consistent backup.");
        }

        return backupPath;
    }

    private static async Task EnsureSchemaVersionTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_version (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> ReadCurrentVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(version) FROM schema_version;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    private static async Task WriteVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int version,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO schema_version(version, applied_at)
            VALUES($version, $appliedAt);
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
