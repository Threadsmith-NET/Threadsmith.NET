namespace Threadsmith.Persistence;

using System.Buffers.Binary;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>SQLite mechanics shared by transactional CRUD, lexical retrieval and forward migration.</summary>
public sealed partial class SqliteManagedRepositoryMemoryStore
{
    /// <summary>Reads detached rows in the caller transaction for CRUD, migration and retrieval.</summary>
    internal static async Task<List<RepositoryMemoryEntry>> ReadEntriesAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string repositoryIdentity, List<string> warnings, bool includesMemoryType = true, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        command.Parameters.AddWithValue("$repo", repositoryIdentity);
        command.CommandText = includesMemoryType
            ? """
                SELECT memory_id, text, content_hash, content_revision, origin, memory_type, sensitivity,
                    created_at, updated_at, inclusion_count, last_included_at,
                    source_session_id, source_run_id, source_invocation_id,
                    embedding, embedding_space_id, embedding_dimensions, embedding_content_hash, embedding_revision
                FROM managed_memories WHERE repository_identity = $repo ORDER BY created_at, memory_id;
                """
            : """
                SELECT memory_id, text, content_hash, content_revision, origin, sensitivity,
                    created_at, updated_at, inclusion_count, last_included_at,
                    source_session_id, source_run_id, source_invocation_id,
                    embedding, embedding_space_id, embedding_dimensions, embedding_content_hash, embedding_revision
                FROM managed_memories WHERE repository_identity = $repo ORDER BY created_at, memory_id;
                """;
        var entries = new List<RepositoryMemoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var idText = reader.GetString(0);
            var hash = reader.GetString(2);
            var revision = reader.GetInt64(3);
            if (!Guid.TryParse(idText, out var id) || revision <= 0)
            {
                AddWarning(warnings, "A malformed memory identity or content revision was omitted.");
                continue;
            }

            var typeOffset = includesMemoryType ? 1 : 0;
            var dimensions = await reader.IsDBNullAsync(15 + typeOffset, cancellationToken) ? 0 : reader.GetInt32(15 + typeOffset);
            var vectorHash = NullableString(reader, 16 + typeOffset);
            var vectorRevision = await reader.IsDBNullAsync(17 + typeOffset, cancellationToken) ? (long?)null : reader.GetInt64(17 + typeOffset);
            var space = NullableString(reader, 14 + typeOffset);
            var vector = await reader.IsDBNullAsync(13 + typeOffset, cancellationToken) ? [] : DecodeVector((byte[])reader[13 + typeOffset], dimensions);
            if (string.IsNullOrWhiteSpace(space) || vectorHash != hash || vectorRevision != revision
                || !IsValidVector(vector, dimensions))
            {
                vector = [];
                AddWarning(warnings, $"Memory {idText} has no compatible content-matched embedding; lexical retrieval remains available.");
            }

            var text = reader.GetString(1);
            if (ComputeHash(text) != hash)
            {
                vector = [];
                AddWarning(warnings, $"Memory {idText} has inconsistent content metadata; semantic retrieval is disabled.");
            }

            if (text.Length > MaximumTextCharacters)
            {
                AddWarning(warnings, $"Imported memory {idText} exceeds current write bounds; inspect and correct it manually.");
            }

            var memoryType = includesMemoryType ? (RepositoryMemoryType)reader.GetInt32(5) : RepositoryMemoryType.Situational;
            var sensitivity = (ConversationSensitivity)reader.GetInt32(5 + typeOffset);
            entries.Add(new RepositoryMemoryEntry
            {
                Id = new RepositoryMemoryId(id),
                RepositoryIdentity = repositoryIdentity,
                Text = text,
                ContentHash = hash,
                Revision = revision,
                Origin = reader.GetString(4) == "manual" ? RepositoryMemoryOrigin.Manual : RepositoryMemoryOrigin.Model,
                MemoryType = Enum.IsDefined(memoryType) ? memoryType : RepositoryMemoryType.Situational,
                Sensitivity = Enum.IsDefined(sensitivity) ? sensitivity : ConversationSensitivity.Sensitive,
                CreatedAt = ParseTimestamp(reader.GetString(6 + typeOffset)),
                UpdatedAt = ParseTimestamp(reader.GetString(7 + typeOffset)),
                InclusionCount = Math.Max(0, reader.GetInt64(8 + typeOffset)),
                LastIncludedAt = await reader.IsDBNullAsync(9 + typeOffset, cancellationToken) ? null : ParseTimestamp(reader.GetString(9 + typeOffset)),
                SourceSessionId = NullableString(reader, 10 + typeOffset),
                SourceRunId = NullableString(reader, 11 + typeOffset),
                SourceInvocationId = NullableString(reader, 12 + typeOffset),
                Embedding = vector,
                EmbeddingSpaceId = space,
                EmbeddingDimensions = dimensions,
                EmbeddingContentHash = vectorHash,
                EmbeddingRevision = vectorRevision,
            });
        }

        return entries;
    }

    /// <summary>Applies the same deterministic retention policy to additions, imports and capacity reductions.</summary>
    internal static async Task<IReadOnlyList<RepositoryMemoryId>> EvictAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string repositoryIdentity, List<RepositoryMemoryEntry> entries, int maximum, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var evicted = new List<RepositoryMemoryId>();
        while (entries.Count > maximum)
        {
            var unprotected = entries.Where(entry => now - entry.UpdatedAt >= TimeSpan.FromDays(7)).ToArray();
            var victim = unprotected.Length == 0
                ? entries.OrderBy(entry => entry.UpdatedAt).ThenBy(entry => entry.CreatedAt).ThenBy(entry => entry.Id.Value).First()
                : unprotected.OrderBy(entry => RetentionScore(entry, now))
                    .ThenBy(entry => entry.LastIncludedAt ?? entry.UpdatedAt)
                    .ThenBy(entry => entry.CreatedAt).ThenBy(entry => entry.Id.Value).First();
            await DeleteEntryAsync(connection, transaction, repositoryIdentity, victim.Id, cancellationToken);
            entries.Remove(victim);
            evicted.Add(victim.Id);
        }

        return evicted;
    }

    /// <summary>Inserts content and its complete vector within a caller-owned transaction.</summary>
    internal static async Task InsertEntryAsync(
        SqliteConnection connection, SqliteTransaction? transaction, RepositoryMemoryEntry entry, bool includesMemoryType = true, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        command.Parameters.AddWithValue("$repo", entry.RepositoryIdentity);
        command.CommandText = includesMemoryType
            ? """
                INSERT INTO managed_memories(memory_id, repository_identity, text, content_hash, content_revision,
                    origin, memory_type, sensitivity, created_at, updated_at, source_session_id, source_run_id, source_invocation_id,
                    inclusion_count, last_included_at, embedding, embedding_space_id, embedding_dimensions,
                    embedding_content_hash, embedding_revision)
                VALUES($id, $repo, $text, $hash, $revision, $origin, $memoryType, $sensitivity, $created, $updated,
                    $session, $run, $invocation, 0, NULL, $embedding, $space, $dimensions, $vectorHash, $vectorRevision);
                """
            : """
                INSERT INTO managed_memories(memory_id, repository_identity, text, content_hash, content_revision,
                    origin, sensitivity, created_at, updated_at, source_session_id, source_run_id, source_invocation_id,
                    inclusion_count, last_included_at, embedding, embedding_space_id, embedding_dimensions,
                    embedding_content_hash, embedding_revision)
                VALUES($id, $repo, $text, $hash, $revision, $origin, $sensitivity, $created, $updated,
                    $session, $run, $invocation, 0, NULL, $embedding, $space, $dimensions, $vectorHash, $vectorRevision);
                """;
        AddEntryParameters(command, entry);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Invalidates repository ranking caches after content or vector changes.</summary>
    internal static async Task AdvanceRevisionAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string repositoryIdentity, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        command.Parameters.AddWithValue("$repo", repositoryIdentity);
        command.CommandText = """
            INSERT INTO managed_memory_repositories(repository_identity, revision) VALUES($repo, 1)
            ON CONFLICT(repository_identity) DO UPDATE SET revision = revision + 1;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, string repositoryIdentity)
    {
        var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        command.CommandText = sql;
        command.Parameters.AddWithValue("$repo", repositoryIdentity);
        return command;
    }

    private static async Task<IReadOnlyList<RepositoryMemoryLexicalMatch>> ReadLexicalMatchesAsync(
        SqliteConnection connection, SqliteTransaction transaction, string repositoryIdentity, IReadOnlyList<string> lexicalTerms, CancellationToken cancellationToken)
    {
        var terms = lexicalTerms.Where(term => !string.IsNullOrWhiteSpace(term) && term.Length <= 128
                && !term.Any(char.IsControl))
            .Select(term => term.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Take(32).ToArray();
        if (terms.Length == 0)
        {
            return [];
        }

        var termMatches = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var term in terms)
        {
            await using var termCommand = connection.CreateCommand();
            termCommand.Transaction = transaction;
            termCommand.Parameters.AddWithValue("$repo", repositoryIdentity);
            termCommand.CommandText = """
                SELECT m.memory_id FROM managed_memories_fts
                JOIN managed_memories m ON m.rowid = managed_memories_fts.rowid
                WHERE managed_memories_fts MATCH $query AND m.repository_identity = $repo AND m.memory_type = $situational;
                """;
            termCommand.Parameters.AddWithValue("$query", QuoteTerm(term));
            termCommand.Parameters.AddWithValue("$situational", (int)RepositoryMemoryType.Situational);
            await using var reader = await termCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0);
                termMatches[id] = termMatches.GetValueOrDefault(id) + 1;
            }
        }

        await using var command = connection.CreateCommand();

        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        command.Parameters.AddWithValue("$repo", repositoryIdentity);

        command.CommandText = """
            SELECT m.memory_id, bm25(managed_memories_fts) FROM managed_memories_fts
            JOIN managed_memories m ON m.rowid = managed_memories_fts.rowid
            WHERE managed_memories_fts MATCH $query AND m.repository_identity = $repo AND m.memory_type = $situational
            ORDER BY bm25(managed_memories_fts), m.memory_id;
            """;
        command.Parameters.AddWithValue("$query", string.Join(" OR ", terms.Select(QuoteTerm)));
        command.Parameters.AddWithValue("$situational", (int)RepositoryMemoryType.Situational);
        var matches = new List<RepositoryMemoryLexicalMatch>();
        await using var result = await command.ExecuteReaderAsync(cancellationToken);
        while (await result.ReadAsync(cancellationToken))
        {
            var id = result.GetString(0);
            if (termMatches.GetValueOrDefault(id) >= Math.Min(2, terms.Length) && Guid.TryParse(id, out var guid))
            {
                matches.Add(new RepositoryMemoryLexicalMatch(new RepositoryMemoryId(guid), result.GetDouble(1)));
            }
        }

        return matches;
    }

    private static void AddEntryParameters(SqliteCommand command, RepositoryMemoryEntry entry)
    {
        command.Parameters.AddWithValue("$id", entry.Id.Value.ToString());
        command.Parameters.AddWithValue("$text", entry.Text);
        command.Parameters.AddWithValue("$hash", entry.ContentHash);
        command.Parameters.AddWithValue("$revision", entry.Revision);
        command.Parameters.AddWithValue("$origin", entry.Origin == RepositoryMemoryOrigin.Manual ? "manual" : "model");
        command.Parameters.AddWithValue("$memoryType", (int)entry.MemoryType);
        command.Parameters.AddWithValue("$sensitivity", (int)entry.Sensitivity);
        command.Parameters.AddWithValue("$created", entry.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", entry.UpdatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$session", (object?)entry.SourceSessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$run", (object?)entry.SourceRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$invocation", (object?)entry.SourceInvocationId ?? DBNull.Value);
        command.Parameters.AddWithValue("$embedding", entry.Embedding.IsEmpty ? DBNull.Value : EncodeVector(entry.Embedding.Span));
        command.Parameters.AddWithValue("$space", (object?)entry.EmbeddingSpaceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$dimensions", entry.EmbeddingDimensions);
        command.Parameters.AddWithValue("$vectorHash", (object?)entry.EmbeddingContentHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$vectorRevision", (object?)entry.EmbeddingRevision ?? DBNull.Value);
    }

    private static async Task<bool> DeleteEntryAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string repositoryIdentity, RepositoryMemoryId id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        command.Parameters.AddWithValue("$repo", repositoryIdentity);
        command.CommandText = """
            DELETE FROM managed_memory_inclusions WHERE repository_identity = $repo AND memory_id = $id;
            DELETE FROM managed_memories WHERE repository_identity = $repo AND memory_id = $id;
            SELECT changes();
            """;
        command.Parameters.AddWithValue("$id", id.Value.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0;
    }

    private static double RetentionScore(RepositoryMemoryEntry entry, DateTimeOffset now)
    {
        var age = Math.Max(0, (now - (entry.LastIncludedAt ?? entry.UpdatedAt)).TotalDays);
        return (1 + Math.Log2(1.0 + entry.InclusionCount)) * Math.Pow(2, -age / 30);
    }

    private static string QuoteTerm(string term) => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset ParseTimestamp(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).ToUniversalTime();

    private static byte[] EncodeVector(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[checked(vector.Length * sizeof(float))];
        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    private static float[] DecodeVector(byte[] bytes, int dimensions)
    {
        if (dimensions <= 0 || dimensions > 65_536 || bytes.Length != dimensions * sizeof(float))
        {
            return [];
        }

        var vector = new float[dimensions];
        for (var i = 0; i < dimensions; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * sizeof(float)));
        }

        return vector;
    }

    private static bool IsValidVector(ReadOnlySpan<float> vector, int dimensions)
    {
        if (dimensions <= 0 || vector.Length != dimensions)
        {
            return false;
        }

        var squaredLength = 0.0;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value))
            {
                return false;
            }

            squaredLength += (double)value * value;
        }

        return Math.Abs(squaredLength - 1) <= 0.002;
    }

    private static void AddWarning(List<string> warnings, string warning)
    {
        if (warnings.Count < 32)
        {
            warnings.Add(warning);
        }
    }
}
