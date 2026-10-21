namespace Threadsmith.Persistence;

using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>Expands indexed text terms while preserving exact qualification and snapshot consistency.</summary>
public sealed partial class SqliteManagedRepositoryMemoryStore
{
    private async Task<RepositoryMemoryReadSnapshot> ExpandLexicalSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RepositoryMemoryReadSnapshot snapshot,
        IReadOnlyList<string> terms,
        RepositoryMemoryLexicalOptions options,
        RepositoryMemoryOwnership? ownership,
        CancellationToken cancellationToken)
    {
        if (terms.Count == 0 || snapshot.Entries.Count == 0)
        {
            return snapshot with { FuzzyLexicalStatus = RepositoryMemorySearchBranchStatus.NotRequired };
        }

        try
        {
            await using var vocabularyCommand = connection.CreateCommand();
            vocabularyCommand.Transaction = transaction;
            vocabularyCommand.CommandText = "CREATE VIRTUAL TABLE IF NOT EXISTS temp.memory_text_terms USING fts5vocab(main, managed_memories_fts, instance);";
            await vocabularyCommand.ExecuteNonQueryAsync(cancellationToken);
            vocabularyCommand.CommandText = """
                SELECT DISTINCT v.term FROM temp.memory_text_terms v
                JOIN managed_memories m ON m.rowid = v.doc
                WHERE m.repository_identity = $repo AND ($ownership IS NULL OR m.ownership = $ownership) ORDER BY v.term;
                """;
            vocabularyCommand.Parameters.AddWithValue("$repo", snapshot.RepositoryIdentity);
            vocabularyCommand.Parameters.AddWithValue("$ownership", ownership is { } owner ? (int)owner : DBNull.Value);
            var vocabulary = new List<string>();
            await using (var reader = await vocabularyCommand.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    vocabulary.Add(reader.GetString(0));
                }
            }

            var exactTerms = vocabulary.ToHashSet(StringComparer.Ordinal);
            var pending = terms.Where(term => term.Length >= 5
                    && !RepositoryMemoryLexicalTerms.IsCodeIdentifier(term) && !exactTerms.Contains(term))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (pending.Length == 0)
            {
                return snapshot with { FuzzyLexicalStatus = RepositoryMemorySearchBranchStatus.NotRequired };
            }

            var resolution = await _termResolver.ResolveTermsAsync(
                new RepositoryMemoryVocabularySnapshot(snapshot.RepositoryIdentity, snapshot.Revision, vocabulary) { Kind = MemoryVocabularyKind.Text, Ownership = ownership },
                pending,
                options.FuzzyMaximumDistance,
                cancellationToken);
            if (resolution.DegradedReason is { } reason)
            {
                return snapshot with
                {
                    FuzzyLexicalStatus = RepositoryMemorySearchBranchStatus.Unavailable,
                    Warnings = [.. snapshot.Warnings, reason],
                };
            }

            var expansions = pending.SelectMany(term => resolution.Matches.Where(match => match.Query == term && exactTerms.Contains(match.Term))
                .DistinctBy(match => match.Term).OrderBy(match => match.Distance).ThenBy(match => match.Term, StringComparer.Ordinal)
                .Take(options.MaximumExpansionsPerTerm)).Take(options.MaximumExpansions).ToArray();
            var matches = expansions.Length == 0 ? [] : await ReadLexicalMatchesAsync(
                connection, transaction, snapshot.RepositoryIdentity, terms, expansions, cancellationToken);
            var exactIds = snapshot.LexicalMatches.Select(match => match.Id).ToHashSet();
            return snapshot with
            {
                LexicalMatches = expansions.Length == 0 ? snapshot.LexicalMatches
                    : [.. snapshot.LexicalMatches, .. matches.Where(match => !exactIds.Contains(match.Id)).Select(match => match with { IsFuzzy = true })],
                FuzzyLexicalStatus = RepositoryMemorySearchBranchStatus.Completed,
                LexicalExpansions = expansions,
            };
        }
        catch (SqliteException exception)
        {
            return snapshot with
            {
                FuzzyLexicalStatus = RepositoryMemorySearchBranchStatus.Unavailable,
                Warnings = [.. snapshot.Warnings, $"Fuzzy memory text lookup failed ({exception.SqliteErrorCode}); using exact lexical matches only."],
            };
        }
    }
}
