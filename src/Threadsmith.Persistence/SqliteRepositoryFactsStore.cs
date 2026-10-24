namespace Threadsmith.Persistence;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>SQLite discovery facts with user-owned file permissions.</summary>
public sealed class SqliteRepositoryFactsStore : IRepositoryFactsStore
{
    private readonly string _connectionString;
    private readonly RepositoryPermissionStore _permissions;

    /// <summary>Initializes a new instance of the <see cref="SqliteRepositoryFactsStore"/> class.</summary>
    public SqliteRepositoryFactsStore(string connectionString, string? userConfigurationPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
        var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        _permissions = new RepositoryPermissionStore(userConfigurationPath
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dataSource)) ?? throw new ArgumentException("A file-backed facts store is required.", nameof(connectionString)), "config.json"));
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
        var trust = await GetTrustAsync(repositoryPath, cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT workspace_id, solution_path,
                   target_frameworks_json, environment_json, repository_path
            FROM repository_facts
            WHERE repository_key = $repository_key;
            """;
        command.Parameters.AddWithValue("$repository_key", GetRepositoryKey(repositoryPath));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        if (!string.Equals(
            RepositoryPermissionStore.GetRepositoryIdentity(reader.GetString(4)),
            RepositoryPermissionStore.GetRepositoryIdentity(repositoryPath),
            StringComparison.Ordinal))
        {
            throw new InvalidDataException("Stored repository facts have a different canonical identity.");
        }

        var workspaceText = reader.GetString(0);
        if (!Guid.TryParse(workspaceText, out var workspaceValue))
        {
            throw new InvalidDataException("Stored repository workspace identity is invalid.");
        }

        var solutionPath = await reader.IsDBNullAsync(1, cancellationToken)
            ? null
            : reader.GetString(1);
        var frameworks = JsonSerializer.Deserialize<string[]>(reader.GetString(2))
            ?? throw new InvalidDataException("Stored target-framework facts are invalid.");
        MsBuildEnvironmentSnapshot? environment = null;
        if (!await reader.IsDBNullAsync(3, cancellationToken))
        {
            environment = JsonSerializer.Deserialize<MsBuildEnvironmentSnapshot>(reader.GetString(3))
                ?? throw new InvalidDataException("Stored MSBuild environment facts are invalid.");
        }

        return new RepositoryFacts(
            new WorkspaceId(workspaceValue),
            repositoryPath,
            trust ?? new RepositoryTrustState(repositoryPath, RepositoryTrustLevel.UntrustedInspection, DateTimeOffset.UtcNow),
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
                solution_path = excluded.solution_path,
                target_frameworks_json = excluded.target_frameworks_json,
                environment_json = excluded.environment_json,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$repository_key", GetRepositoryKey(facts.RepositoryPath));
        command.Parameters.AddWithValue("$repository_path", facts.RepositoryPath);
        command.Parameters.AddWithValue("$workspace_id", facts.WorkspaceId.Value.ToString("D"));

        // Legacy NOT NULL columns are retained for schema compatibility, never as a trust store.
        command.Parameters.AddWithValue("$trust_level", (int)RepositoryTrustLevel.UntrustedInspection);
        command.Parameters.AddWithValue(
            "$trust_granted_at",
            DateTimeOffset.MinValue.ToString("O", CultureInfo.InvariantCulture));
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

    /// <inheritdoc />
    public async Task SetTrustAsync(RepositoryTrustState trust, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trust);
        if (!Enum.IsDefined(trust.Level))
        {
            throw new ArgumentException("Trust level is invalid.", nameof(trust));
        }

        await _permissions.SetManyAsync(
            trust.RepositoryPath,
            new Dictionary<string, JsonNode?>
            {
                ["repository:trust"] = JsonValue.Create(trust.Level.ToString()),
                ["repository:trustGrantedAt"] = JsonValue.Create(trust.GrantedAt.ToString("O", CultureInfo.InvariantCulture)),
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<MutationApprovalPolicy?> GetMutationPolicyAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = _permissions.Read(repositoryPath).Settings;
        if (!settings.TryGetValue("mutation:approvalPolicy", out var value))
        {
            return Task.FromResult<MutationApprovalPolicy?>(null);
        }

        if (value.ValueKind != JsonValueKind.String || !Enum.TryParse<MutationApprovalPolicy>(value.GetString(), true, out var policy)
            || policy is not (MutationApprovalPolicy.ReviewAll or MutationApprovalPolicy.ReviewRisky or MutationApprovalPolicy.AlwaysTrustRepo))
        {
            throw new InvalidDataException("Stored repository mutation permission is invalid.");
        }

        return Task.FromResult<MutationApprovalPolicy?>(policy);
    }

    /// <inheritdoc />
    public Task<RepositoryTrustState?> GetTrustAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settings = _permissions.Read(repositoryPath).Settings;
        if (!settings.TryGetValue("repository:trust", out var value))
        {
            return Task.FromResult<RepositoryTrustState?>(null);
        }

        if (value.ValueKind != JsonValueKind.String || !Enum.TryParse<RepositoryTrustLevel>(value.GetString(), true, out var trust) || !Enum.IsDefined(trust))
        {
            throw new InvalidDataException("Stored repository trust permission is invalid.");
        }

        var grantedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(_permissions.GetPath(repositoryPath)));
        if (settings.TryGetValue("repository:trustGrantedAt", out var timestamp)
            && (timestamp.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out grantedAt)))
        {
            throw new InvalidDataException("Stored repository trust timestamp is invalid.");
        }

        return Task.FromResult<RepositoryTrustState?>(new(repositoryPath, trust, grantedAt));
    }

    /// <inheritdoc />
    public async Task SetMutationPolicyAsync(string repositoryPath, MutationApprovalPolicy policy, CancellationToken cancellationToken = default)
    {
        if (policy is not (MutationApprovalPolicy.ReviewAll or MutationApprovalPolicy.ReviewRisky or MutationApprovalPolicy.AlwaysTrustRepo))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        await _permissions.SetAsync(repositoryPath, "mutation:approvalPolicy", JsonValue.Create(policy.ToString()), cancellationToken);
    }

    private static string GetRepositoryKey(string repositoryPath)
    {
        // Retain the existing SQLite key contract. This is not permission identity:
        // fact rows are independently checked against their stored repository path,
        // and active permission files use filesystem-aware canonical identities.
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + path[8..];
        }
        else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            path = path[4..];
        }

        return path.ToUpperInvariant();
    }
}
