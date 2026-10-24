namespace Threadsmith.Persistence;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;

/// <summary>SQLite discovery facts with user-owned file permissions and bounded legacy migration.</summary>
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
            CREATE TABLE IF NOT EXISTS repository_permission_migration (
                repository_key TEXT PRIMARY KEY,
                completed INTEGER NOT NULL,
                snapshot_json TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<RepositoryFacts?> GetAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var facts = await ReadLegacyFactsAsync(repositoryPath, cancellationToken);
        var trust = await GetTrustAsync(repositoryPath, cancellationToken);
        return facts is null ? null : facts with
        {
            Trust = trust ?? new RepositoryTrustState(repositoryPath, RepositoryTrustLevel.UntrustedInspection, DateTimeOffset.UtcNow),
        };
    }

    /// <inheritdoc />
    public async Task UpsertAsync(
        RepositoryFacts facts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(facts);
        await MigratePermissionsAsync(facts.RepositoryPath, cancellationToken);

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

    /// <inheritdoc />
    public async Task SetTrustAsync(RepositoryTrustState trust, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trust);
        if (!Enum.IsDefined(trust.Level))
        {
            throw new ArgumentException("Trust level is invalid.", nameof(trust));
        }

        await MigratePermissionsAsync(trust.RepositoryPath, cancellationToken);
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
    public async Task<MutationApprovalPolicy?> GetMutationPolicyAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        await MigratePermissionsAsync(repositoryPath, cancellationToken);
        var settings = _permissions.Read(repositoryPath).Settings;
        if (!settings.TryGetValue("mutation:approvalPolicy", out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || !Enum.TryParse<MutationApprovalPolicy>(value.GetString(), true, out var policy)
            || policy is not (MutationApprovalPolicy.ReviewAll or MutationApprovalPolicy.ReviewRisky or MutationApprovalPolicy.AlwaysTrustRepo))
        {
            throw new InvalidDataException("Stored repository mutation permission is invalid.");
        }

        return policy;
    }

    /// <inheritdoc />
    public async Task<RepositoryTrustState?> GetTrustAsync(string repositoryPath, CancellationToken cancellationToken = default)
    {
        await MigratePermissionsAsync(repositoryPath, cancellationToken);
        var settings = _permissions.Read(repositoryPath).Settings;
        if (!settings.TryGetValue("repository:trust", out var value))
        {
            return null;
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

        return new(repositoryPath, trust, grantedAt);
    }

    /// <inheritdoc />
    public async Task SetMutationPolicyAsync(string repositoryPath, MutationApprovalPolicy policy, CancellationToken cancellationToken = default)
    {
        if (policy is not (MutationApprovalPolicy.ReviewAll or MutationApprovalPolicy.ReviewRisky or MutationApprovalPolicy.AlwaysTrustRepo))
        {
            throw new ArgumentOutOfRangeException(nameof(policy));
        }

        await MigratePermissionsAsync(repositoryPath, cancellationToken);
        await _permissions.SetAsync(repositoryPath, "mutation:approvalPolicy", JsonValue.Create(policy.ToString()), cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetLegacyPermissionDecisionAsync(string repositoryPath, bool useDefaults, CancellationToken cancellationToken = default)
    {
        await MigratePermissionsAsync(repositoryPath, cancellationToken);
        await _permissions.SetLegacyDefaultsDecisionAsync(repositoryPath, useDefaults, cancellationToken);
    }

    private async Task<RepositoryFacts?> ReadLegacyFactsAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT workspace_id, trust_level, trust_granted_at, solution_path,
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
            RepositoryPermissionStore.GetRepositoryIdentity(reader.GetString(6)),
            RepositoryPermissionStore.GetRepositoryIdentity(repositoryPath),
            StringComparison.Ordinal))
        {
            throw new InvalidDataException("Stored repository facts have a different canonical identity; no permissions were imported.");
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

    private async Task MigratePermissionsAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        // Only the host-owned database is a migration source. Repository configuration is never imported.
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT completed, snapshot_json FROM repository_permission_migration WHERE repository_key = $key;";
        command.Parameters.AddWithValue("$key", GetRepositoryKey(repositoryPath));
        string? snapshot = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                if (reader.GetInt32(0) == 1)
                {
                    return;
                }

                snapshot = reader.GetString(1);
            }
        }

        if (snapshot is null)
        {
            var grants = new Dictionary<string, string>();
            var legacy = await ReadLegacyFactsAsync(repositoryPath, cancellationToken);
            if (legacy is not null)
            {
                grants["repository:trust"] = legacy.Trust.Level.ToString();
                grants["repository:trustGrantedAt"] = legacy.Trust.GrantedAt.ToString("O", CultureInfo.InvariantCulture);
            }

            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='repository_mutation_consent';";
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) > 0)
            {
                command.CommandText = "SELECT policy FROM repository_mutation_consent WHERE repository_key = $key;";
                var value = await command.ExecuteScalarAsync(cancellationToken);
                if (value is not null)
                {
                    if (OperatingSystem.IsWindows() && !RepositoryPathPolicy.GetPathComparer(repositoryPath).Equals("a", "A") && legacy is null)
                    {
                        throw new InvalidDataException("Legacy mutation consent lacks an unambiguous repository identity on this case-sensitive Windows directory; select the local permission explicitly.");
                    }

                    if (value is not long number || number is < 0 or > int.MaxValue
                        || (MutationApprovalPolicy)number is not (MutationApprovalPolicy.ReviewAll or MutationApprovalPolicy.ReviewRisky or MutationApprovalPolicy.AlwaysTrustRepo))
                    {
                        throw new InvalidDataException("Legacy host mutation consent is invalid; migration stopped.");
                    }

                    grants["mutation:approvalPolicy"] = ((MutationApprovalPolicy)number).ToString();
                }
            }

            snapshot = JsonSerializer.Serialize(new PermissionMigrationSnapshot(RepositoryPermissionStore.GetRepositoryIdentity(repositoryPath), grants));
            command.CommandText = "INSERT OR IGNORE INTO repository_permission_migration(repository_key, completed, snapshot_json) VALUES($key, 0, $snapshot);";
            command.Parameters.AddWithValue("$snapshot", snapshot);
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.CommandText = "SELECT snapshot_json FROM repository_permission_migration WHERE repository_key = $key;";
            snapshot = await command.ExecuteScalarAsync(cancellationToken) as string ?? throw new InvalidDataException("Permission migration snapshot is missing.");
        }

        if (snapshot.Length > 4096)
        {
            throw new InvalidDataException("Permission migration snapshot exceeds its bound.");
        }

        var pending = JsonSerializer.Deserialize<PermissionMigrationSnapshot>(snapshot)
            ?? throw new InvalidDataException("Permission migration snapshot is invalid.");
        if (!string.Equals(pending.RepositoryIdentity, RepositoryPermissionStore.GetRepositoryIdentity(repositoryPath), StringComparison.Ordinal)
            || pending.Grants is null)
        {
            throw new InvalidDataException("Pending permission migration belongs to a different repository identity; no grants were imported.");
        }

        var imported = pending.Grants;
        foreach (var (key, value) in imported)
        {
            var valid = key switch
            {
                "repository:trust" => Enum.TryParse<RepositoryTrustLevel>(value, out var trust) && Enum.IsDefined(trust),
                "repository:trustGrantedAt" => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
                "mutation:approvalPolicy" => Enum.TryParse<MutationApprovalPolicy>(value, out var policy)
                    && policy is MutationApprovalPolicy.ReviewAll or MutationApprovalPolicy.ReviewRisky or MutationApprovalPolicy.AlwaysTrustRepo,
                _ => false,
            };
            if (!valid)
            {
                throw new InvalidDataException("Permission migration snapshot contains an unsupported legacy grant.");
            }
        }

        if (imported.Count > 0)
        {
            await _permissions.ImportLegacyAsync(repositoryPath, imported.ToDictionary(pair => pair.Key, pair => (JsonNode?)JsonValue.Create(pair.Value)), cancellationToken);
        }

        // The durable marker prevents deleting a permissions file from resurrecting old grants.
        command.CommandText = "UPDATE repository_permission_migration SET completed = 1 WHERE repository_key = $key;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string GetRepositoryKey(string repositoryPath)
    {
        // Retain the existing SQLite key contract. This is not permission identity:
        // imported rows are independently checked against their stored repository path,
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

    private sealed record PermissionMigrationSnapshot(string RepositoryIdentity, Dictionary<string, string> Grants);
}
