namespace Threadsmith.Persistence;

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>SQLite-backed repository trust and discovery facts.</summary>
public sealed class SqliteRepositoryFactsStore : IRepositoryFactsStore
{
    private readonly string _connectionString;

    /// <summary>Initializes a new instance of the <see cref="SqliteRepositoryFactsStore"/> class.</summary>
    public SqliteRepositoryFactsStore(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS repository_facts (
                repository_key TEXT PRIMARY KEY,
                repository_path TEXT NOT NULL,
                workspace_id TEXT NOT NULL,
                trust_level INTEGER NOT NULL,
                trust_granted_at TEXT NOT NULL,
                solution_path TEXT NULL,
                target_frameworks_json TEXT NOT NULL,
                environment_json TEXT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RepositoryFacts?> GetAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT workspace_id, trust_level, trust_granted_at, solution_path,
                   target_frameworks_json, environment_json
            FROM repository_facts
            WHERE repository_key = $repository_key;
            """;
        command.Parameters.AddWithValue("$repository_key", GetRepositoryKey(repositoryPath));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var workspaceText = reader.GetString(0);
        if (!Guid.TryParse(workspaceText, out var workspaceValue))
        {
            throw new InvalidDataException("Stored repository workspace identity is invalid.");
        }

        var trustValue = reader.GetInt32(1);
        if (!Enum.IsDefined(typeof(RepositoryTrustLevel), trustValue))
        {
            throw new InvalidDataException("Stored repository trust level is invalid.");
        }

        var grantedText = reader.GetString(2);
        if (!DateTimeOffset.TryParse(
            grantedText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var grantedAt))
        {
            throw new InvalidDataException("Stored repository trust timestamp is invalid.");
        }

        var solutionPath = await reader.IsDBNullAsync(3, cancellationToken)
            ? null
            : reader.GetString(3);
        var frameworks = JsonSerializer.Deserialize<string[]>(reader.GetString(4))
            ?? throw new InvalidDataException("Stored target-framework facts are invalid.");
        MsBuildEnvironmentSnapshot? environment = null;
        if (!await reader.IsDBNullAsync(5, cancellationToken))
        {
            environment = JsonSerializer.Deserialize<MsBuildEnvironmentSnapshot>(reader.GetString(5))
                ?? throw new InvalidDataException("Stored MSBuild environment facts are invalid.");
        }

        var trust = new RepositoryTrustState(
            repositoryPath,
            (RepositoryTrustLevel)trustValue,
            grantedAt);
        return new RepositoryFacts(
            new WorkspaceId(workspaceValue),
            repositoryPath,
            trust,
            solutionPath,
            frameworks,
            environment);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(
        RepositoryFacts facts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(facts);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO repository_facts(
                repository_key, repository_path, workspace_id, trust_level, trust_granted_at,
                solution_path, target_frameworks_json, environment_json, updated_at)
            VALUES(
                $repository_key, $repository_path, $workspace_id, $trust_level, $trust_granted_at,
                $solution_path, $target_frameworks, $environment, $updated_at)
            ON CONFLICT(repository_key) DO UPDATE SET
                repository_path = excluded.repository_path,
                workspace_id = excluded.workspace_id,
                trust_level = excluded.trust_level,
                trust_granted_at = excluded.trust_granted_at,
                solution_path = excluded.solution_path,
                target_frameworks_json = excluded.target_frameworks_json,
                environment_json = excluded.environment_json,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$repository_key", GetRepositoryKey(facts.RepositoryPath));
        command.Parameters.AddWithValue("$repository_path", facts.RepositoryPath);
        command.Parameters.AddWithValue("$workspace_id", facts.WorkspaceId.Value.ToString("D"));
        command.Parameters.AddWithValue("$trust_level", (int)facts.Trust.Level);
        command.Parameters.AddWithValue(
            "$trust_granted_at",
            facts.Trust.GrantedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$solution_path",
            facts.SolutionPath is null ? DBNull.Value : facts.SolutionPath);
        command.Parameters.AddWithValue(
            "$target_frameworks",
            JsonSerializer.Serialize(facts.TargetFrameworks));
        command.Parameters.AddWithValue(
            "$environment",
            facts.Environment is null ? DBNull.Value : JsonSerializer.Serialize(facts.Environment));
        command.Parameters.AddWithValue(
            "$updated_at",
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string GetRepositoryKey(string repositoryPath)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        if (!OperatingSystem.IsWindows())
        {
            return normalizedPath;
        }

        if (normalizedPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = @"\\" + normalizedPath[8..];
        }
        else if (normalizedPath.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            normalizedPath = normalizedPath[4..];
        }

        return normalizedPath.ToUpperInvariant();
    }
}
