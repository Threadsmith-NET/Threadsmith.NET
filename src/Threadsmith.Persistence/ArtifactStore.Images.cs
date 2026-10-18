namespace Threadsmith.Persistence;

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>Bounded image storage and session-scoped reference ownership.</summary>
public sealed partial class ArtifactStore
{
    private readonly SemaphoreSlim _imageGate = new(1, 1);
    private readonly ConcurrentDictionary<SessionId, int> _imagePins = new();

    /// <inheritdoc />
    public async Task<IDisposable?> PinSessionImagesAsync(SessionId sessionId, CancellationToken cancellationToken = default)
    {
        await _imageGate.WaitAsync(cancellationToken);
        try
        {
            _imagePins.AddOrUpdate(sessionId, 1, static (_, count) => count + 1);
            return new ImageSessionLease(_imagePins, sessionId);
        }
        finally
        {
            _imageGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<int> PruneImageReferencesAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await _imageGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM image_artifact_refs AS r
                WHERE COALESCE(json_extract(r.descriptor, '$.CreatedAt'), '0001') < $cutoff
                """;
            if (await HasConversationImagesAsync(connection, cancellationToken))
            {
                command.CommandText += """
                     AND NOT EXISTS (SELECT 1 FROM conversation_messages m, json_each(m.images_json) i
                         WHERE m.session_id = r.session_id AND json_extract(i.value, '$.Id') = r.id
                           AND (m.body IS NOT NULL OR m.artifact_id IS NOT NULL))
                    """;
            }

            command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
            var index = 0;
            foreach (var pin in _imagePins.Where(pair => pair.Value > 0))
            {
                var parameter = "$pin" + index++;
                command.CommandText += " AND r.session_id <> " + parameter;
                command.Parameters.AddWithValue(parameter, pin.Key.Value.ToString("D"));
            }

            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _imageGate.Release();
        }
    }

    private sealed class ImageSessionLease : IDisposable
    {
        private readonly ConcurrentDictionary<SessionId, int> _pins;
        private readonly SessionId _sessionId;
        private int _disposed;

        public ImageSessionLease(ConcurrentDictionary<SessionId, int> pins, SessionId sessionId)
        {
            _pins = pins;
            _sessionId = sessionId;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _pins.AddOrUpdate(_sessionId, 0, static (_, count) => Math.Max(0, count - 1));
                _pins.TryRemove(new KeyValuePair<SessionId, int>(_sessionId, 0));
            }
        }
    }

    /// <inheritdoc />
    public async Task<ImageArtifact> StoreImageAsync(Stream content, SessionId sessionId, string source, string? responseId = null, long maximumBytes = ImageArtifactLimits.MaximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        maximumBytes = Math.Min(maximumBytes, ImageArtifactLimits.MaximumBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (source.Length > 2048 || responseId?.Length > 256)
        {
            throw new ArgumentException("Image provenance exceeds its metadata limit.");
        }

        await _imageGate.WaitAsync(cancellationToken);
        try
        {
            var (buffer, length) = await ReadImageBufferAsync(content, (int)maximumBytes, cancellationToken);
            var bytes = buffer.AsMemory(0, length);
            var (mediaType, width, height) = RasterImageValidator.Validate(buffer, length, cancellationToken);
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
            var artifact = new ImageArtifact
            {
                Id = Guid.NewGuid().ToString("N"),
                ContentHash = hash,
                MediaType = mediaType,
                ByteLength = bytes.Length,
                Width = width,
                Height = height,
                Source = _sanitizer.Sanitize(source),
                ResponseId = responseId,
                CreatedAt = _timeProvider.GetUtcNow(),
            };
            await InitializeAsync(cancellationToken);
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var quota = connection.CreateCommand();
            quota.Transaction = transaction;
            quota.CommandText = "SELECT COALESCE(SUM(length), 0) FROM artifacts WHERE kind = 'image' AND content_hash <> $hash;";
            quota.Parameters.AddWithValue("$hash", hash);
            var retained = Convert.ToInt64(await quota.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            string? afterRecordedAt = null;
            var afterHash = string.Empty;
            while (retained > _maximumRetainedImageBytes - bytes.Length)
            {
                // Recover unowned bodies left by retention, older releases or interrupted cleanup.
                // The transaction prevents another session from acquiring a reference mid-deletion.
                await using var candidate = connection.CreateCommand();
                candidate.Transaction = transaction;
                candidate.CommandText = """
                    SELECT content_hash, length, recorded_at FROM artifacts a
                    WHERE kind = 'image' AND content_hash <> $hash
                      AND NOT EXISTS (SELECT 1 FROM image_artifact_refs r WHERE r.content_hash = a.content_hash)
                    """;
                if (afterRecordedAt is not null)
                {
                    candidate.CommandText += " AND (recorded_at, content_hash) > ($afterAt, $afterHash)";
                    candidate.Parameters.AddWithValue("$afterAt", afterRecordedAt);
                    candidate.Parameters.AddWithValue("$afterHash", afterHash);
                }

                candidate.CommandText += " ORDER BY recorded_at, content_hash LIMIT 128;";
                candidate.Parameters.AddWithValue("$hash", hash);
                var candidates = new List<(string Hash, long Length, string RecordedAt)>(128);
                await using (var reader = await candidate.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        candidates.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
                    }
                }

                if (candidates.Count == 0)
                {
                    // Commit reclaimed bodies even if owned images still fill the budget.
                    await transaction.CommitAsync(cancellationToken);
                    throw new InvalidOperationException("Image artifact storage quota reached. Remove unneeded attachments or increase persistence:maximumRetainedImageBytes before retrying.");
                }

                foreach (var unused in candidates)
                {
                    await DeleteUnreferencedAsync(connection, transaction, unused.Hash, cancellationToken);
                    retained -= unused.Length;
                    afterRecordedAt = unused.RecordedAt;
                    afterHash = unused.Hash;
                    if (retained <= _maximumRetainedImageBytes - bytes.Length)
                    {
                        break;
                    }
                }
            }

            var relative = Path.Combine(hash[..2], hash[2..4], hash + ".bin");
            var destination = Path.Combine(_artifactDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? throw new InvalidOperationException("Missing artifact directory."));
            var newBody = !File.Exists(destination);
            var committed = false;
            try
            {
                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    if (!await HasValidImageBodyAsync(destination, bytes.Length, hash, cancellationToken))
                    {
                        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        File.Move(temporary, destination, overwrite: true);
                    }
                }
                finally
                {
                    File.Delete(temporary);
                }

                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT OR IGNORE INTO artifacts(content_hash, kind, length, session_id, relative_path, recorded_at)
                    VALUES($hash, 'image', $length, NULL, $path, $at);
                    INSERT INTO image_artifact_refs(id, session_id, content_hash, descriptor)
                    VALUES($id, $session, $hash, $descriptor);
                    """;
                insert.Parameters.AddWithValue("$hash", hash);
                insert.Parameters.AddWithValue("$length", bytes.Length);
                insert.Parameters.AddWithValue("$path", relative);
                insert.Parameters.AddWithValue("$at", _timeProvider.GetUtcNow().ToString("O"));
                insert.Parameters.AddWithValue("$id", artifact.Id);
                insert.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
                insert.Parameters.AddWithValue("$descriptor", JsonSerializer.Serialize(artifact));
                await insert.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                committed = true;
                return artifact;
            }
            finally
            {
                if (!committed && newBody)
                {
                    File.Delete(destination);
                }
            }
        }
        finally
        {
            _imageGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ImageArtifact> GetImageAsync(string artifactId, SessionId sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT descriptor FROM image_artifact_refs WHERE id = $id AND session_id = $session;";
        command.Parameters.AddWithValue("$id", artifactId);
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        var json = await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new UnauthorizedAccessException("Image reference is unavailable in this session. Read or paste the image again.");
        return JsonSerializer.Deserialize<ImageArtifact>(json) ?? throw new InvalidDataException("Invalid image metadata.");
    }

    /// <inheritdoc />
    public async Task<ReadOnlyMemory<byte>> ReadImageAsync(ImageArtifact artifact, SessionId sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var admitted = await GetImageAsync(artifact.Id, sessionId, cancellationToken);
        if (admitted != artifact)
        {
            throw new InvalidDataException("Image reference metadata differs from its admitted descriptor.");
        }

        var hash = admitted.ContentHash;
        var path = Path.Combine(_artifactDirectory, hash[..2], hash[2..4], hash + ".bin");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (stream.Length != admitted.ByteLength || stream.Length > ImageArtifactLimits.MaximumBytes)
        {
            throw new InvalidDataException("Image artifact length changed. Read or paste the image again.");
        }

        var bytes = new byte[admitted.ByteLength];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != hash)
        {
            throw new InvalidDataException("Image artifact checksum mismatch. Read or paste the image again.");
        }

        return bytes;
    }

    /// <inheritdoc />
    public async Task GrantImageAsync(string artifactId, SessionId sourceSessionId, SessionId destinationSessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        // Keep source authorization and the new reference atomic with release/deletion.
        await using var source = connection.CreateCommand();
        source.Transaction = transaction;
        source.CommandText = "SELECT EXISTS(SELECT 1 FROM image_artifact_refs WHERE id = $id AND session_id = $session);";
        source.Parameters.AddWithValue("$id", artifactId);
        source.Parameters.AddWithValue("$session", sourceSessionId.Value.ToString("D"));
        if (Convert.ToInt64(await source.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            throw new UnauthorizedAccessException("Image reference is unavailable in this session. Read or paste the image again.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO image_artifact_refs(id, session_id, content_hash, descriptor)
            SELECT id, $destination, content_hash, descriptor FROM image_artifact_refs WHERE id = $id AND session_id = $source;
            """;
        command.Parameters.AddWithValue("$id", artifactId);
        command.Parameters.AddWithValue("$destination", destinationSessionId.Value.ToString("D"));
        command.Parameters.AddWithValue("$source", sourceSessionId.Value.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReleaseImageAsync(string artifactId, SessionId sessionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = "SELECT content_hash FROM image_artifact_refs WHERE id = $id AND session_id = $session;";
        lookup.Parameters.AddWithValue("$id", artifactId);
        lookup.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        var hash = await lookup.ExecuteScalarAsync(cancellationToken) as string;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM image_artifact_refs WHERE id = $id AND session_id = $session
            """;
        if (await HasConversationImagesAsync(connection, cancellationToken, transaction))
        {
            command.CommandText += """
                 AND NOT EXISTS (SELECT 1 FROM conversation_messages m, json_each(m.images_json) i
                     WHERE m.session_id = $session AND json_extract(i.value, '$.Id') = $id
                         AND (m.body IS NOT NULL OR m.artifact_id IS NOT NULL))
                """;
        }

        command.Parameters.AddWithValue("$id", artifactId);
        command.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (hash is not null)
        {
            // Commit ownership release before deleting bytes so cancellation cannot restore a reference to a deleted body.
            // Ordinary deletion rechecks references in its own transaction; a concurrent new owner remains protected.
            await DeleteAsync(hash, cancellationToken);
        }
    }

    private static async Task<(byte[] Buffer, int Length)> ReadImageBufferAsync(Stream content, int maximumBytes, CancellationToken cancellationToken)
    {
        var capacity = content.CanSeek
            ? (int)Math.Clamp(content.Length - content.Position + 1, 1L, maximumBytes + 1L)
            : Math.Min(8192, maximumBytes + 1);
        var buffer = new byte[capacity];
        var length = 0;
        while (true)
        {
            var read = await content.ReadAsync(buffer.AsMemory(length), cancellationToken);
            if (read == 0)
            {
                return (buffer, length);
            }

            length += read;
            if (length > maximumBytes)
            {
                throw new InvalidDataException($"Image exceeds the {maximumBytes}-byte encoded input limit.");
            }

            if (length == buffer.Length)
            {
                Array.Resize(ref buffer, Math.Min(maximumBytes + 1, checked(buffer.Length * 2)));
            }
        }
    }

    private static async Task<bool> HasConversationImagesAsync(SqliteConnection connection, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM pragma_table_info('conversation_messages') WHERE name = 'images_json');";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<bool> HasValidImageBodyAsync(string path, int length, string hash, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        return stream.Length == length && Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken)) == hash;
    }
}
