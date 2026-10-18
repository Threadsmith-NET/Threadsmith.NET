namespace Threadsmith.Persistence;

using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>Metadata for one persisted artifact (strategy §19.3).</summary>
public sealed record ArtifactMetadata
{
    /// <summary>Content hash (SHA-256) used as the storage key.</summary>
    public required string ContentHash { get; init; }

    /// <summary>Artifact kind (e.g. <c>processOutput</c>, <c>diff</c>, <c>buildLog</c>).</summary>
    public required string Kind { get; init; }

    /// <summary>Byte length of the stored artifact.</summary>
    public required long Length { get; init; }

    /// <summary>Owning session, when applicable.</summary>
    public SessionId? SessionId { get; init; }

    /// <summary>Relative file path under the artifact directory.</summary>
    public required string RelativePath { get; init; }

    /// <summary>When the artifact was recorded.</summary>
    public required DateTimeOffset RecordedAt { get; init; }
}

/// <summary>Stores large artifacts on disk with metadata in SQLite (strategy §19.3).</summary>
/// <remarks>
/// Artifacts are content-addressed (SHA-256) so identical content is stored once. The database
/// stores metadata, hashes, and paths; the files live under a configured artifact directory. Text
/// content is sanitized via the host <c>SecretOutputSanitizer</c> before it is written so secrets
/// never reach disk artifacts (§19.6, §22.3).
/// </remarks>
public sealed partial class ArtifactStore : IArtifactStore
{
    /// <summary>Default store-wide image budget: 512 MiB.</summary>
    public const long DefaultMaximumRetainedImageBytes = 512L * 1024 * 1024;

    /// <summary>Ordered covering index shared by startup and migration.</summary>
    internal const string ImageQuotaIndexSql = "CREATE INDEX IF NOT EXISTS ix_artifacts_image_quota ON artifacts(kind, recorded_at, content_hash, length);";

    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly long _maximumRetainedImageBytes;
    private readonly string _connectionString;
    private readonly string _artifactDirectory;
    private readonly IOutputSanitizer _sanitizer;
    private readonly TimeProvider _timeProvider;
    private bool _initialized;

    /// <summary>Initializes a new instance of the <see cref="ArtifactStore"/> class.</summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="artifactDirectory">The directory holding artifact files.</param>
    /// <param name="sanitizer">The output sanitizer applied before persist.</param>
    /// <param name="timeProvider">The time provider.</param>
    /// <param name="maximumRetainedImageBytes">Positive store-wide budget for retained image bodies.</param>
    public ArtifactStore(
        string connectionString,
        string artifactDirectory,
        IOutputSanitizer sanitizer,
        TimeProvider? timeProvider = null,
        long maximumRetainedImageBytes = DefaultMaximumRetainedImageBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRetainedImageBytes);
        _maximumRetainedImageBytes = maximumRetainedImageBytes;
        _connectionString = connectionString;
        _artifactDirectory = artifactDirectory;
        _sanitizer = sanitizer;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ArtifactMetadata> StoreAsync(
        string content,
        string kind,
        SessionId? sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        cancellationToken.ThrowIfCancellationRequested();
        await InitializeAsync(cancellationToken);
        var sanitized = _sanitizer.Sanitize(content);
        var bytes = System.Text.Encoding.UTF8.GetBytes(sanitized);
        var hash = SHA256.HashData(bytes);
        var hashText = Convert.ToHexString(hash).ToLowerInvariant();
        var relativePath = Path.Combine(hashText[..2], hashText[2..4], hashText + ".bin");
        var absolutePath = Path.Combine(_artifactDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath) ?? throw new InvalidOperationException("Missing artifact directory."));
        if (!File.Exists(absolutePath))
        {
            await File.WriteAllBytesAsync(absolutePath, bytes, cancellationToken);
        }

        var recordedAt = _timeProvider.GetUtcNow();
        var metadata = new ArtifactMetadata
        {
            ContentHash = hashText,
            Kind = kind,
            Length = bytes.Length,
            SessionId = sessionId,
            RelativePath = relativePath,
            RecordedAt = recordedAt,
        };
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT OR IGNORE INTO artifacts(content_hash, kind, length, session_id, relative_path, recorded_at)
            VALUES($hash, $kind, $length, $session, $path, $recordedAt);
            """;
        insert.Parameters.AddWithValue("$hash", hashText);
        insert.Parameters.AddWithValue("$kind", kind);
        insert.Parameters.AddWithValue("$length", bytes.Length);
        insert.Parameters.AddWithValue("$session", (object?)sessionId?.Value.ToString("D") ?? DBNull.Value);
        insert.Parameters.AddWithValue("$path", relativePath);
        insert.Parameters.AddWithValue("$recordedAt", recordedAt.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return metadata;
    }

    /// <inheritdoc />
    public async Task<string?> ReadAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT relative_path FROM artifacts WHERE content_hash = $hash AND kind <> 'image';
            """;
        command.Parameters.AddWithValue("$hash", contentHash.ToLowerInvariant());
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not string relativePath)
        {
            return null;
        }

        var absolutePath = Path.Combine(_artifactDirectory, relativePath);
        return File.Exists(absolutePath)
            ? await File.ReadAllTextAsync(absolutePath, cancellationToken)
            : null;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        string contentHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var deleted = await DeleteUnreferencedAsync(connection, transaction, contentHash, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtifactMetadata>> ListAsync(
        SessionId? sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        if (sessionId is null)
        {
            command.CommandText = """
                SELECT content_hash, kind, length, session_id, relative_path, recorded_at
                FROM artifacts ORDER BY recorded_at;
                """;
        }
        else
        {
            command.CommandText = """
                SELECT content_hash, kind, length, session_id, relative_path, recorded_at
                FROM artifacts WHERE session_id = $session ORDER BY recorded_at;
                """;
            command.Parameters.AddWithValue("$session", sessionId.Value.Value.ToString("D"));
        }

        var results = new List<ArtifactMetadata>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var hash = reader.GetString(0);
            var kind = reader.GetString(1);
            var length = reader.GetInt64(2);
            var sessionText = await reader.IsDBNullAsync(3, cancellationToken) ? null : reader.GetString(3);
            var relativePath = reader.GetString(4);
            var recordedAt = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture);
            results.Add(new ArtifactMetadata
            {
                ContentHash = hash,
                Kind = kind,
                Length = length,
                SessionId = sessionText is null ? null : new SessionId(Guid.Parse(sessionText)),
                RelativePath = relativePath,
                RecordedAt = recordedAt,
            });
        }

        return results;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _initialized))
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_artifactDirectory);
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS artifacts (
                    content_hash TEXT PRIMARY KEY,
                    kind TEXT NOT NULL,
                    length INTEGER NOT NULL,
                    session_id TEXT,
                    relative_path TEXT NOT NULL,
                    recorded_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_artifacts_session
                    ON artifacts(session_id);
                CREATE INDEX IF NOT EXISTS ix_artifacts_kind
                    ON artifacts(kind);
                """ + ImageArtifactSchemaMigration.ReferenceSchemaSql + ImageQuotaIndexSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
            Volatile.Write(ref _initialized, true);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task<bool> DeleteUnreferencedAsync(
        SqliteConnection connection, SqliteTransaction transaction, string contentHash, CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT relative_path FROM artifacts WHERE content_hash = $hash AND NOT EXISTS (SELECT 1 FROM image_artifact_refs WHERE content_hash = $hash);";
        select.Parameters.AddWithValue("$hash", contentHash.ToLowerInvariant());
        var result = await select.ExecuteScalarAsync(cancellationToken);
        if (result is not string relativePath)
        {
            return false;
        }

        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM artifacts WHERE content_hash = $hash;";
        delete.Parameters.AddWithValue("$hash", contentHash.ToLowerInvariant());
        await delete.ExecuteNonQueryAsync(cancellationToken);
        try
        {
            File.Delete(Path.Combine(_artifactDirectory, relativePath));
        }
        catch (DirectoryNotFoundException)
        {
            // A missing shard (or storage root) means the body is already absent,
            // just as a missing file does. Complete removal of its stale metadata.
        }

        // Keep metadata retryable if deleting the body fails. If commit fails after
        // deletion, the next attempt can safely finish removing the metadata.
        return true;
    }
}
