namespace Threadsmith.Persistence;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>Scoped historical observations and indexed pages in the existing artifact store.</summary>
public sealed partial class ArtifactStore
{
    private const int EvidenceChunkCharacters = 4096;
    private const int MaximumEvidenceBytes = 1024 * 1024;
    private const int MaximumRunEvidenceBytes = 4 * MaximumEvidenceBytes;
    private const int MaximumSessionEvidenceBytes = 64 * MaximumEvidenceBytes;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReceiptEvidenceReference>> CaptureAsync(ConversationReceiptProvenance receipt, IReadOnlyList<ReceiptEvidenceCapture> captures, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(captures);
        if (receipt.Version != 1 || captures.Count > 8)
        {
            throw new ArgumentException("Unsupported receipt or too many evidence references.");
        }

        await InitializeAsync(cancellationToken);
        var references = new List<ReceiptEvidenceReference>();
        var runBytes = 0;
        foreach (var capture in captures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (capture.Content.Length > MaximumEvidenceBytes || capture.Label.Length > 128 || string.IsNullOrWhiteSpace(capture.Content))
            {
                continue;
            }

            var content = _sanitizer.Sanitize(capture.Content);
            var bytes = Encoding.UTF8.GetByteCount(content);
            if (bytes > MaximumEvidenceBytes || runBytes + bytes > MaximumRunEvidenceBytes)
            {
                continue;
            }

            var encoded = Encoding.UTF8.GetBytes(content);
            var hash = Convert.ToHexStringLower(SHA256.HashData(encoded));
            var reference = new ReceiptEvidenceReference(capture.EvidenceId, capture.InvocationId, capture.RepositoryIdentity, hash, bytes, content.Count(character => character == '\n') + 1, _sanitizer.Sanitize(capture.Label), capture.WasTruncated);
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = "SELECT descriptor, message_id, run_id FROM receipt_evidence WHERE session_id = $session AND evidence_id = $evidence;";
            existing.Parameters.AddWithValue("$session", receipt.OriginSessionId.Value.ToString("D"));
            existing.Parameters.AddWithValue("$evidence", capture.EvidenceId.Value.ToString("D"));
            await using (var reader = await existing.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    if (reader.GetString(0) == JsonSerializer.Serialize(reference)
                        && reader.GetString(1) == receipt.OriginMessageId.Value.ToString("D")
                        && reader.GetString(2) == receipt.OriginRunId.Value.ToString("D")
                        && !references.Contains(reference))
                    {
                        references.Add(reference);
                        runBytes += bytes;
                    }

                    continue;
                }
            }

            await using var quota = connection.CreateCommand();
            quota.Transaction = transaction;
            quota.CommandText = "SELECT COALESCE(SUM(length), 0) FROM receipt_evidence WHERE session_id = $session;";
            quota.Parameters.AddWithValue("$session", receipt.OriginSessionId.Value.ToString("D"));
            if (Convert.ToInt64(await quota.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) + bytes > MaximumSessionEvidenceBytes)
            {
                continue;
            }

            quota.CommandText = "SELECT COALESCE(SUM(length), 0) FROM receipt_evidence WHERE session_id = $session AND run_id = $run;";
            quota.Parameters.AddWithValue("$run", receipt.OriginRunId.Value.ToString("D"));
            if (Convert.ToInt64(await quota.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) + bytes > MaximumRunEvidenceBytes)
            {
                continue;
            }

            quota.CommandText = "SELECT COUNT(*) FROM receipt_evidence WHERE session_id = $session AND run_id = $run;";
            if (Convert.ToInt64(await quota.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) >= 8)
            {
                continue;
            }

            await StoreTextBytesAsync(encoded, "receipt-evidence", receipt.OriginSessionId, connection, transaction, cancellationToken);
            await IndexEvidenceAsync(connection, transaction, hash, content, cancellationToken);
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO receipt_evidence(session_id, evidence_id, message_id, run_id, content_hash, descriptor, length, recorded_at)
                VALUES($session, $evidence, $message, $run, $hash, $descriptor, $length, $time);
                """;
            insert.Parameters.AddWithValue("$session", receipt.OriginSessionId.Value.ToString("D"));
            insert.Parameters.AddWithValue("$evidence", reference.EvidenceId.Value.ToString("D"));
            insert.Parameters.AddWithValue("$message", receipt.OriginMessageId.Value.ToString("D"));
            insert.Parameters.AddWithValue("$run", receipt.OriginRunId.Value.ToString("D"));
            insert.Parameters.AddWithValue("$hash", reference.ContentHash);
            insert.Parameters.AddWithValue("$descriptor", JsonSerializer.Serialize(reference));
            insert.Parameters.AddWithValue("$length", bytes);
            insert.Parameters.AddWithValue("$time", _timeProvider.GetUtcNow().ToString("O"));
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            references.Add(reference);
            runBytes += bytes;
        }

        return references;
    }

    /// <inheritdoc />
    public async Task<TextEvidenceReadResult?> ReadReceiptEvidenceAsync(SessionId sessionId, ReceiptEvidenceGrant grant, int startLine, int? endLine, int startColumn, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        var receipt = grant.Receipt;
        var reference = grant.Reference;
        if (receipt.Version != 1 || receipt.OriginSessionId != sessionId || !receipt.Evidence.Contains(reference))
        {
            throw new UnauthorizedAccessException("Evidence is not owned by this receipt and session.");
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var lookup = connection.CreateCommand();
        lookup.CommandText = """
            SELECT e.descriptor, m.receipt_json FROM receipt_evidence e
            JOIN conversation_messages m ON m.message_id = e.message_id AND m.session_id = e.session_id
            WHERE e.session_id = $session AND e.evidence_id = $evidence AND e.message_id = $message AND e.run_id = $run
                AND (m.body IS NOT NULL OR m.artifact_id IS NOT NULL);
            """;
        lookup.Parameters.AddWithValue("$session", sessionId.Value.ToString("D"));
        lookup.Parameters.AddWithValue("$evidence", reference.EvidenceId.Value.ToString("D"));
        lookup.Parameters.AddWithValue("$message", receipt.OriginMessageId.Value.ToString("D"));
        lookup.Parameters.AddWithValue("$run", receipt.OriginRunId.Value.ToString("D"));
        await using (var reader = await lookup.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken) || await reader.IsDBNullAsync(1, cancellationToken))
            {
                return null;
            }

            try
            {
                var archived = JsonSerializer.Deserialize<ConversationReceiptProvenance>(reader.GetString(1));
                if (JsonSerializer.Deserialize<ReceiptEvidenceReference>(reader.GetString(0)) != reference
                    || archived is null || archived.Version != receipt.Version || archived.OriginSessionId != sessionId
                    || archived.OriginRunId != receipt.OriginRunId || archived.OriginMessageId != receipt.OriginMessageId
                    || !archived.Evidence.Contains(reference))
                {
                    return null;
                }
            }
            catch (JsonException)
            {
                return null;
            }
        }

        if (startLine < 1 || startLine > reference.TotalLines || startColumn < 1 || endLine < startLine)
        {
            throw new ArgumentException("Evidence lines and columns must be positive, ordered, and within the captured evidence.");
        }

        await using var chunks = connection.CreateCommand();
        chunks.CommandText = """
            SELECT ordinal, byte_offset, byte_length, start_line, start_column, checksum FROM artifact_text_chunks
            WHERE content_hash = $hash AND ordinal >= (
                SELECT ordinal FROM artifact_text_chunks WHERE content_hash = $hash
                    AND (start_line < $line OR (start_line = $line AND start_column <= $column))
                ORDER BY start_line DESC, start_column DESC LIMIT 1)
            ORDER BY ordinal LIMIT 5;
            """;
        chunks.Parameters.AddWithValue("$hash", reference.ContentHash);
        chunks.Parameters.AddWithValue("$line", startLine);
        chunks.Parameters.AddWithValue("$column", startColumn);
        var selected = new List<EvidenceChunk>();
        await using (var reader = await chunks.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                selected.Add(new EvidenceChunk(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetString(5)));
            }
        }

        if (selected.Count == 0 || reference.ContentHash.Length != 64 || !reference.ContentHash.All(Uri.IsHexDigit))
        {
            return null;
        }

        var path = Path.Combine(_artifactDirectory, reference.ContentHash[..2], reference.ContentHash[2..4], reference.ContentHash + ".bin");
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess);
            if (stream.Length != reference.ByteLength)
            {
                return null;
            }

            var text = new StringBuilder();
            var expectedOffset = selected[0].Offset;
            foreach (var chunk in selected)
            {
                if (chunk.Offset != expectedOffset || chunk.Length is < 1 or > EvidenceChunkCharacters * 4 || chunk.Offset < 0 || chunk.Offset + chunk.Length > stream.Length)
                {
                    return null;
                }

                var buffer = new byte[chunk.Length];
                stream.Position = chunk.Offset;
                await stream.ReadExactlyAsync(buffer, cancellationToken);
                if (ChunkChecksum(chunk, buffer) != chunk.Checksum)
                {
                    return null;
                }

                text.Append(Encoding.UTF8.GetString(buffer));
                expectedOffset += chunk.Length;
            }

            if (selected.Count < 5 && expectedOffset != reference.ByteLength)
            {
                return null;
            }

            var first = selected[0];
            var result = new CapturedTextDocument(text.ToString()).Read(startLine - first.Line + 1, endLine - first.Line + 1, startLine == first.Line ? startColumn - first.Column + 1 : startColumn);
            return result with
            {
                StartLine = startLine,
                StartColumn = startColumn,
                TotalLines = reference.TotalLines,
                NextLine = result.NextLine + first.Line - 1,
                NextColumn = result.NextLine == 1 ? result.NextColumn + first.Column - 1 : result.NextColumn,
            };
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<int> PruneReceiptEvidenceAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM receipt_evidence WHERE recorded_at < $cutoff;
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteForRedactionAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var revoke = connection.CreateCommand();
        revoke.Transaction = transaction;
        revoke.CommandText = "DELETE FROM receipt_evidence WHERE content_hash = $hash;";
        revoke.Parameters.AddWithValue("$hash", contentHash.ToLowerInvariant());
        await revoke.ExecuteNonQueryAsync(cancellationToken);
        var deleted = await DeleteUnreferencedAsync(connection, transaction, contentHash, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    private static async Task IndexEvidenceAsync(SqliteConnection connection, SqliteTransaction transaction, string hash, string content, CancellationToken cancellationToken)
    {
        var offset = 0;
        var line = 1;
        var column = 1;
        var ordinal = 0;
        for (var position = 0; position < content.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(EvidenceChunkCharacters, content.Length - position);
            if (position + count < content.Length && char.IsHighSurrogate(content[position + count - 1]))
            {
                count--;
            }

            var bytes = Encoding.UTF8.GetBytes(content.AsSpan(position, count).ToString());
            var chunk = new EvidenceChunk(ordinal, offset, bytes.Length, line, column, string.Empty);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO artifact_text_chunks(content_hash, ordinal, byte_offset, byte_length, start_line, start_column, checksum)
                VALUES($hash, $ordinal, $offset, $length, $line, $column, $checksum);
                """;
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$ordinal", ordinal++);
            command.Parameters.AddWithValue("$offset", offset);
            command.Parameters.AddWithValue("$length", bytes.Length);
            command.Parameters.AddWithValue("$line", line);
            command.Parameters.AddWithValue("$column", column);
            command.Parameters.AddWithValue("$checksum", ChunkChecksum(chunk, bytes));
            await command.ExecuteNonQueryAsync(cancellationToken);
            for (var index = position; index < position + count; index++)
            {
                if (content[index] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }

            offset += bytes.Length;
            position += count;
        }
    }

    private static string ChunkChecksum(EvidenceChunk chunk, byte[] bytes)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{chunk.Ordinal}:{chunk.Offset}:{chunk.Length}:{chunk.Line}:{chunk.Column}:")));
        hash.AppendData(bytes);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private sealed record EvidenceChunk(int Ordinal, int Offset, int Length, int Line, int Column, string Checksum);
}
