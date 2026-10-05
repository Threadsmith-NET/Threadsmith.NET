namespace Threadsmith.Persistence;

using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>Connection-local text and concept spellfix vocabularies; core CRUD never depends on loading an extension.</summary>
public sealed partial class SqliteManagedRepositoryMemoryStore : IRepositoryMemoryTermResolver, IDisposable
{
    private readonly SemaphoreSlim _conceptGate = new(1, 1);
    private readonly Dictionary<MemoryVocabularyKind, RepositoryMemoryVocabularySnapshot> _vocabularies = [];
    private SqliteConnection? _conceptConnection;
    private bool _conceptResolverDisposed;

    /// <inheritdoc />
    public async Task<MemoryTermResolution> ResolveTermsAsync(
        RepositoryMemoryVocabularySnapshot snapshot,
        IReadOnlyList<string> queries,
        int maximumDistance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshot.RepositoryIdentity);
        ArgumentNullException.ThrowIfNull(snapshot.Vocabulary);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDistance);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(_termResolver, this))
        {
            return await _termResolver.ResolveTermsAsync(snapshot, queries, maximumDistance, cancellationToken);
        }

        await _conceptGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_conceptResolverDisposed, this);
            if (!Enum.IsDefined(snapshot.Kind))
            {
                throw new ArgumentOutOfRangeException(nameof(snapshot));
            }

            if (_vocabularies.Values.Any(current => current.RepositoryIdentity != snapshot.RepositoryIdentity || current.Revision != snapshot.Revision))
            {
                DiscardConceptVocabulary();
            }

            var cacheHit = _vocabularies.TryGetValue(snapshot.Kind, out var current) && _conceptConnection is not null
                && current.Vocabulary.SequenceEqual(snapshot.Vocabulary, StringComparer.Ordinal);
            if (queries.Count == 0 || snapshot.Vocabulary.Count == 0)
            {
                if (!cacheHit)
                {
                    DiscardConceptVocabulary();
                }

                return new MemoryTermResolution([], VocabularyCacheHit: cacheHit);
            }

            var table = snapshot.Kind == MemoryVocabularyKind.Text ? "text_vocabulary" : "concept_vocabulary";
            if (_conceptConnection is null)
            {
                var suffix = OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
                var library = Path.Combine(AppContext.BaseDirectory, "native", "spellfix", "spellfix" + suffix);
                var expected = (await File.ReadAllTextAsync(library + ".sha256", cancellationToken)).Trim();
                await using var file = File.OpenRead(library);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken));
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return new MemoryTermResolution([], "Spellfix asset verification failed; using exact matches only.");
                }

                _conceptConnection = new SqliteConnection("Data Source=:memory:;Pooling=False");
                await _conceptConnection.OpenAsync(cancellationToken);
                _conceptConnection.LoadExtension(library, "sqlite3_spellfix_init");
                _conceptConnection.EnableExtensions(false);
            }

            if (!cacheHit)
            {
                await using var populate = _conceptConnection.CreateCommand();
                populate.CommandText = $"DROP TABLE IF EXISTS temp.{table}; CREATE VIRTUAL TABLE temp.{table} USING spellfix1;";
                await populate.ExecuteNonQueryAsync(cancellationToken);
                populate.CommandText = $"INSERT INTO {table}(word, rank) VALUES($word, 1);";
                var word = populate.Parameters.Add("$word", SqliteType.Text);
                foreach (var concept in snapshot.Vocabulary)
                {
                    word.Value = concept;
                    await populate.ExecuteNonQueryAsync(cancellationToken);
                }

                _vocabularies[snapshot.Kind] = snapshot with { Vocabulary = snapshot.Vocabulary.ToArray() };
            }

            var connection = _conceptConnection ?? throw new InvalidOperationException("The native vocabulary was not initialized.");
            await using var command = connection.CreateCommand();
            command.Parameters.Clear();
            command.CommandText = $"SELECT word, distance FROM {table} WHERE word MATCH $query AND top = 3 AND distance <= $distance ORDER BY distance, word;";
            var queryParameter = command.Parameters.Add("$query", SqliteType.Text);
            command.Parameters.AddWithValue("$distance", maximumDistance);
            var matches = new List<MemoryTermMatch>();
            foreach (var query in queries.Where(query => query.Length >= 5))
            {
                queryParameter.Value = query;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    matches.Add(new MemoryTermMatch(query, reader.GetString(0), reader.GetInt32(1)));
                }
            }

            return new MemoryTermResolution(matches, VocabularyCacheHit: cacheHit);
        }
        catch (OperationCanceledException)
        {
            DiscardConceptVocabulary();
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException or BadImageFormatException or DllNotFoundException)
        {
            DiscardConceptVocabulary();
            return new MemoryTermResolution([], $"Spellfix unavailable ({exception.GetType().Name}); using exact matches only.");
        }
        finally
        {
            _conceptGate.Release();
        }
    }

    /// <summary>Releases the bounded derived native vocabularies owned by this store.</summary>
    public void Dispose()
    {
        _conceptGate.Wait();
        try
        {
            _conceptResolverDisposed = true;
            DiscardConceptVocabulary();
        }
        finally
        {
            _conceptGate.Release();
        }
    }

    private void DiscardConceptVocabulary()
    {
        _vocabularies.Clear();
        _conceptConnection?.Dispose();
        _conceptConnection = null;
    }
}
