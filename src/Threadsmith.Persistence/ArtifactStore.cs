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
/// stores metadata, hashes, and paths; the files live under a configured artifact directory. All
/// content is sanitized via the host <c>SecretOutputSanitizer</c> before it is written so secrets
/// never reach disk artifacts (§19.6, §22.3).
/// </remarks>
public sealed class ArtifactStore : IArtifactStore
{
    private readonly string _connectionString;
    private readonly string _artifactDirectory;
    private readonly IOutputSanitizer _sanitizer;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="ArtifactStore"/> class.</summary>
    /// <param name="connectionString">The SQLite connection string.</param>
    /// <param name="artifactDirectory">The directory holding artifact files.</param>
    /// <param name="sanitizer">The output sanitizer applied before persist.</param>
    /// <param name="timeProvider">The time provider.</param>
    public ArtifactStore(
        string connectionString,
        string artifactDirectory,
        IOutputSanitizer sanitizer,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactDirectory);
        ArgumentNullException.ThrowIfNull(sanitizer);
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
        var sanitized = _sanitizer.Sanitize(content);
        var bytes = System.Text.Encoding.UTF8.GetBytes(sanitized);
        var hash = SHA256.HashData(bytes);
        var hashText = Convert.ToHexString(hash).ToLowerInvariant();
        var relativePath = Path.Combine(hashText[..2], hashText[2..4], hashText + ".bin");
        var absolutePath = Path.Combine(_artifactDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        if (!File.Exists(absolutePath))
        {
            await File.WriteAllBytesAsync(absolutePath, bytes, cancellationToken);
        }

        DateTimeOffset recordedAt = _timeProvider.GetUtcNow();
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
        await using SqliteCommand command = connection.CreateCommand();
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
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await using SqliteCommand insert = connection.CreateCommand();
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
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT relative_path FROM artifacts WHERE content_hash = $hash;
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
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT relative_path FROM artifacts WHERE content_hash = $hash;";
        select.Parameters.AddWithValue("$hash", contentHash.ToLowerInvariant());
        var result = await select.ExecuteScalarAsync(cancellationToken);
        if (result is not string relativePath)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await using SqliteCommand delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM artifacts WHERE content_hash = $hash;";
        delete.Parameters.AddWithValue("$hash", contentHash.ToLowerInvariant());
        await delete.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        File.Delete(Path.Combine(_artifactDirectory, relativePath));
        return true;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArtifactMetadata>> ListAsync(
        SessionId? sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
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
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var hash = reader.GetString(0);
            var kind = reader.GetString(1);
            var length = reader.GetInt64(2);
            var sessionText = await reader.IsDBNullAsync(3) ? null : reader.GetString(3);
            var relativePath = reader.GetString(4);
            DateTimeOffset recordedAt = DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture);
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
        Directory.CreateDirectory(_artifactDirectory);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
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
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
