namespace Threadsmith.Persistence;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>Onboarding checkpoints preserve preparation without exposing it to ordinary memory readers.</summary>
public sealed partial class SqliteManagedRepositoryMemoryStore
{
    /// <inheritdoc />
    public async Task<RepositoryIntelligenceCheckpoint?> ReadOnboardingCheckpointAsync(string repositoryIdentity, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryIdentity);
        await using var connection = new SqliteConnection(_connectionString);
        await OpenConnectionAsync(connection, cancellationToken);
        await using var command = CreateCommand(connection, null, "SELECT checkpoint_json FROM repository_intelligence_checkpoints WHERE repository_identity = $repo;", repositoryIdentity);
        if (await command.ExecuteScalarAsync(cancellationToken) is not string json)
        {
            return null;
        }

        var payload = JsonSerializer.Deserialize<CheckpointPayload>(json) ?? throw new InvalidDataException("Onboarding checkpoint is empty.");
        var checkpoint = payload.Checkpoint;
        if (checkpoint.Version is not (1 or 2) || checkpoint.RepositoryIdentity != repositoryIdentity)
        {
            throw new InvalidDataException("Onboarding checkpoint format or repository identity is incompatible.");
        }

        return checkpoint with
        {
            Entries = checkpoint.Entries.Select(entry => entry with
            {
                Embedding = payload.Embeddings.TryGetValue(entry.Id.Value.ToString("D"), out var embedding) ? embedding : ReadOnlyMemory<float>.Empty,
            }).ToArray(),
        };
    }

    /// <inheritdoc />
    public async Task SaveOnboardingCheckpointAsync(RepositoryIntelligenceCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (_collection is not null || checkpoint.Version is not (1 or 2) || checkpoint.NextHistoryPage < 0
            || (checkpoint.NextHistoryPage == 0 && !checkpoint.HistoryComplete)
            || (checkpoint.HistoryComplete && (checkpoint.Version < 2 || !checkpoint.CoverageReviewStarted))
            || checkpoint.Entries.Any(entry => entry.RepositoryIdentity != checkpoint.RepositoryIdentity || entry.Ownership != RepositoryMemoryOwnership.Intelligence))
        {
            throw new InvalidOperationException("Invalid onboarding checkpoint ownership or progress.");
        }

        var payload = new CheckpointPayload(checkpoint, checkpoint.Entries.ToDictionary(entry => entry.Id.Value.ToString("D"), entry => entry.Embedding.ToArray()));
        await using var connection = new SqliteConnection(_connectionString);
        await OpenConnectionAsync(connection, cancellationToken);
        const string save = """
            INSERT INTO repository_intelligence_checkpoints(repository_identity, checkpoint_json) VALUES($repo, $json)
            ON CONFLICT(repository_identity) DO UPDATE SET checkpoint_json = $json;
            """;
        await using var command = CreateCommand(connection, null, save, checkpoint.RepositoryIdentity);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(payload));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record CheckpointPayload(RepositoryIntelligenceCheckpoint Checkpoint, IReadOnlyDictionary<string, float[]> Embeddings);
}
