namespace Threadsmith.Architecture.Tests;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.Workspaces;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    /// <summary>A managed project preference creates no migration or permission demand and does not widen unrelated tools.</summary>
    [Fact]
    public void Ownership_ManagedScratchpadDoesNotGrantUnrelatedPermissions()
    {
        File.WriteAllText(_paths.RepositoryConfiguration, """{"scratchpad":{"enabled":true}}""");
        Assert.Equal("True", ConfigurationBootstrap.Build([], _paths)["scratchpad:enabled"]);
        Assert.False(new RepositoryPermissionStore(_paths.UserConfiguration).Read(_paths.RepositoryRoot).Exists);
        File.WriteAllText(_paths.UserConfiguration, """{"tools":{"deny":["run_process"]}}""");
        Assert.False(Load().AllowsTool("run_process"));
        Assert.Equal(MutationApprovalPolicy.ReviewAll, Load().MutationDefault);
    }

    /// <summary>Fact refreshes cannot restore a user-revoked grant.</summary>
    [Fact]
    public async Task Ownership_DiscoveryRefreshCannotRestoreTrust()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new SqliteRepositoryFactsStore($"Data Source={Path.Combine(_root, "refresh.db")};Pooling=False", _paths.UserConfiguration);
        await store.InitializeAsync(ct);
        var oldTrust = new RepositoryTrustState(_paths.RepositoryRoot, RepositoryTrustLevel.TrustedMutation, DateTimeOffset.UtcNow);
        await store.SetTrustAsync(oldTrust, ct);
        var oldFacts = new RepositoryFacts(WorkspaceId.New(), _paths.RepositoryRoot, oldTrust, null, [], null);
        await store.UpsertAsync(oldFacts, ct);
        await store.SetTrustAsync(oldTrust with { Level = RepositoryTrustLevel.TrustedRead }, ct);
        var local = new RepositoryPermissionStore(_paths.UserConfiguration);
        var lowerTrustBytes = await File.ReadAllBytesAsync(local.GetPath(_paths.RepositoryRoot), ct);
        await store.UpsertAsync(oldFacts, ct);
        Assert.Equal(RepositoryTrustLevel.TrustedRead, (await store.GetTrustAsync(_paths.RepositoryRoot, ct))?.Level);
        Assert.Equal(lowerTrustBytes, await File.ReadAllBytesAsync(local.GetPath(_paths.RepositoryRoot), ct));
        File.Delete(local.GetPath(_paths.RepositoryRoot));
        await store.UpsertAsync(oldFacts, ct);
        Assert.Null(await store.GetTrustAsync(_paths.RepositoryRoot, ct));
    }

    /// <summary>Legacy SQLite trust cannot reject discovery facts or override current file permissions.</summary>
    [Theory]
    [InlineData(999, "2020-01-01T00:00:00Z", false)]
    [InlineData(999, "2020-01-01T00:00:00Z", true)]
    [InlineData(3, "invalid-timestamp", false)]
    [InlineData(3, "invalid-timestamp", true)]
    [InlineData(3, "2020-01-01T00:00:00Z", false)]
    [InlineData(3, "2020-01-01T00:00:00Z", true)]
    public async Task Ownership_DiscoveryIgnoresLegacySqliteTrust(int legacyLevel, string legacyTimestamp, bool hasPermission)
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = $"Data Source={Path.Combine(_root, "discovery.db")};Pooling=False";
        var store = new SqliteRepositoryFactsStore(connectionString, _paths.UserConfiguration);
        await store.InitializeAsync(ct);
        var staleTrust = new RepositoryTrustState(_paths.RepositoryRoot, RepositoryTrustLevel.TrustedMutation, DateTimeOffset.UnixEpoch);
        var environment = new MsBuildEnvironmentSnapshot("dotnet", "10.0.401", null, null, null, "Debug", "AnyCPU", RepositoryRestoreState.Succeeded, new Dictionary<string, string>());
        var expected = new RepositoryFacts(WorkspaceId.New(), _paths.RepositoryRoot, staleTrust, "project.sln", ["net10.0"], environment);
        await store.UpsertAsync(expected, ct);
        var currentTrust = staleTrust with { Level = RepositoryTrustLevel.TrustedRead, GrantedAt = DateTimeOffset.UnixEpoch.AddDays(1) };
        if (hasPermission)
        {
            await store.SetTrustAsync(currentTrust, ct);
        }

        await using (var db = new SqliteConnection(connectionString))
        {
            await db.OpenAsync(ct);
            await using var command = db.CreateCommand();
            command.CommandText = "UPDATE repository_facts SET trust_level = $level, trust_granted_at = $timestamp;";
            command.Parameters.AddWithValue("$level", legacyLevel);
            command.Parameters.AddWithValue("$timestamp", legacyTimestamp);
            await command.ExecuteNonQueryAsync(ct);
        }

        var actual = await store.GetAsync(_paths.RepositoryRoot, ct);

        Assert.NotNull(actual);
        Assert.Equal(expected.WorkspaceId, actual.WorkspaceId);
        Assert.Equal(expected.RepositoryPath, actual.RepositoryPath);
        Assert.Equal(expected.SolutionPath, actual.SolutionPath);
        Assert.Equal(expected.TargetFrameworks, actual.TargetFrameworks);
        Assert.Equivalent(environment, actual.Environment);
        Assert.Equal(hasPermission ? RepositoryTrustLevel.TrustedRead : RepositoryTrustLevel.UntrustedInspection, actual.Trust.Level);
        var permissions = new RepositoryPermissionStore(_paths.UserConfiguration);
        Assert.Equal(hasPermission, permissions.Read(_paths.RepositoryRoot).Exists);
        if (hasPermission)
        {
            Assert.Equal(currentTrust, actual.Trust);
        }

        // Refresh discovery without restoring the stale trust carried by the input DTO.
        await store.UpsertAsync(expected with { SolutionPath = null, Environment = null }, ct);
        var refreshed = await store.GetAsync(_paths.RepositoryRoot, ct);
        Assert.NotNull(refreshed);
        Assert.Null(refreshed.SolutionPath);
        Assert.Null(refreshed.Environment);
        Assert.Equal(actual.Trust.Level, refreshed.Trust.Level);

        if (hasPermission)
        {
            var permissionPath = permissions.GetPath(_paths.RepositoryRoot);
            var document = JsonNode.Parse(await File.ReadAllTextAsync(permissionPath, ct))!;
            document["settings"]!["repository"]!["trust"] = "invalid";
            await File.WriteAllTextAsync(permissionPath, document.ToJsonString(), ct);
            await Assert.ThrowsAsync<InvalidDataException>(() => store.GetAsync(_paths.RepositoryRoot, ct));
        }
    }

    /// <summary>Local empty arrays override defaults but not enforced denials.</summary>
    [Fact]
    public async Task Ownership_DefaultsAndEnforcementRemainDistinct()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(_paths.UserConfiguration, """{"tools":{"deny":["run_process"]}}""", ct);
        var store = new RepositoryPermissionStore(_paths.UserConfiguration);
        await store.SetAsync(_paths.RepositoryRoot, "tools:deny", new JsonArray(), ct);
        Assert.Empty(Load().DeniedTools);
        await File.WriteAllTextAsync(_paths.MachineConfiguration, """{"enforced":{"tools":{"deny":["run_process"]}}}""", ct);
        Assert.Contains("run_process", Load().DeniedTools);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SetAsync(_paths.RepositoryRoot, "tools:allow", null, ct));
        Assert.Empty(store.Read(_paths.RepositoryRoot).Settings["tools:deny"].EnumerateArray());
        await Assert.ThrowsAsync<ArgumentException>(() => store.SetAsync(_paths.RepositoryRoot, "enforced:tools:allow", new JsonArray(), ct));
    }

    /// <summary>Only the local permission source accepts remembered trust.</summary>
    [Fact]
    public void Ownership_UnsupportedTrustSourcesAreRejected()
    {
        File.WriteAllText(_paths.MachineConfiguration, """{"enforced":{"repository":{"trust":"FullyTrustedAutomation"}}}""");
        Assert.Throws<InvalidDataException>(Load);
        File.WriteAllText(_paths.MachineConfiguration, "{}");
        File.WriteAllText(_paths.UserConfiguration, """{"repository":{"trust":"FullyTrustedAutomation"}}""");
        Assert.Throws<InvalidDataException>(Load);
    }

    /// <summary>Shared metadata governs the settings guard and nested contracts.</summary>
    [Fact]
    public void Ownership_MetadataIsSharedAndTraversesOtherContracts()
    {
        Assert.Equal(ConfigurationPermissionCatalog.Paths, AuthoritySettingsChangeGuard.Keys);
        var properties = ConfigurationPermissionCatalog.Describe(typeof(OwnershipFixture));
        Assert.Contains(properties, item => item.Path == "nested:grants" && item.Shape == PermissionInputShape.List);
        Assert.False(ConfigurationPermissionCatalog.Allows("enforced:tools:allow", PermissionConfigurationSources.User));
        Assert.True(ConfigurationPermissionCatalog.Allows("enforced:tools:allow", PermissionConfigurationSources.Machine));
    }

    /// <summary>Alternative JSON spellings cannot bypass source classification.</summary>
    [Theory]
    [InlineData("{\"tools:allow\":[]}")]
    [InlineData("{\"tools:writeFile\":{\"allowedFolders\":[\"exports\"]}}")]
    [InlineData("{\"tools:runProcess\":{\"requireApproval\":false}}")]
    [InlineData("{\"mcp\":{\"profiles\":[{\"command\":\"a\",\"Command\":\"b\"}]}}")]
    public void Ownership_AliasesAreRejectedByReaderAndWriter(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        Assert.Throws<InvalidDataException>(() => ConfigurationDocument.Parse(bytes));
        Assert.Throws<InvalidDataException>(() => AuthoritySettingsChangeGuard.Validate("{}"u8.ToArray(), bytes, []));
    }

    /// <summary>The real loader distinguishes overridable defaults from enforced restrictions.</summary>
    [Fact]
    public async Task Ownership_LocalFileOverridesDefaultsButNotMachineEnforcement()
    {
        var ct = TestContext.Current.CancellationToken;
        const string user = """{"mutation":{"approvalPolicy":"reviewAll"}}""";
        await File.WriteAllTextAsync(_paths.UserConfiguration, user, ct);
        var store = new RepositoryPermissionStore(_paths.UserConfiguration);
        await store.SetAsync(_paths.RepositoryRoot, "mutation:approvalPolicy", JsonValue.Create("AlwaysTrustRepo"), ct);
        var policy = Load();
        Assert.Equal(MutationApprovalPolicy.AlwaysTrustRepo, policy.MutationDefault);
        Assert.False(policy.HasExplicitMutationCeiling);
        var binding = new SessionPolicyBinding();
        binding.Publish(policy);
        var facts = new SqliteRepositoryFactsStore($"Data Source={Path.Combine(_root, "facts.db")};Pooling=False", _paths.UserConfiguration);
        await facts.InitializeAsync(ct);
        var service = new MutationApprovalPolicyService(policyBinding: binding, facts: facts);
        await service.BindRepositoryAsync(_paths.RepositoryRoot, ct);
        Assert.Equal(MutationApprovalPolicy.AlwaysTrustRepo, service.CurrentPolicy);

        await File.WriteAllTextAsync(_paths.MachineConfiguration, """{"enforced":{"mutation":{"approvalPolicy":"reviewAll"}}}""", ct);
        binding.Publish(SecurityPolicyConfigurationLoader.Load(_paths, policy.BindingId, policy.Generation + 1));
        Assert.Equal(MutationApprovalPolicy.ReviewAll, service.CurrentPolicy);
        Assert.Equal(user, await File.ReadAllTextAsync(_paths.UserConfiguration, ct));
    }

    /// <summary>User settings are defaults without a separate ownership decision.</summary>
    [Fact]
    public async Task Ownership_UnversionedUserDefaultsCanBeOverridden()
    {
        var ct = TestContext.Current.CancellationToken;
        const string user = """{"mutation":{"approvalPolicy":"reviewAll"}}""";
        await File.WriteAllTextAsync(_paths.UserConfiguration, user, ct);
        var store = new RepositoryPermissionStore(_paths.UserConfiguration);
        await store.SetAsync(_paths.RepositoryRoot, "mutation:approvalPolicy", JsonValue.Create("AlwaysTrustRepo"), ct);
        Assert.False(Load().HasExplicitMutationCeiling);
        Assert.False(Load().HasExplicitMutationCeiling);
        Assert.Equal(user, await File.ReadAllTextAsync(_paths.UserConfiguration, ct));
    }

    /// <summary>Concurrent coordinated edits preserve unrelated values and repository identity.</summary>
    [Fact]
    public async Task Ownership_CommonWriterPreservesConcurrentUnrelatedChoicesAndIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new RepositoryPermissionStore(_paths.UserConfiguration);
        await Task.WhenAll(
            store.SetAsync(_paths.RepositoryRoot, "mutation:approvalPolicy", JsonValue.Create("ReviewRisky"), ct),
            store.SetAsync(_paths.RepositoryRoot, "tools:deny", new JsonArray("run_process"), ct));
        var saved = store.Read(_paths.RepositoryRoot);
        Assert.Equal("ReviewRisky", saved.Settings["mutation:approvalPolicy"].GetString());
        Assert.Equal("run_process", saved.Settings["tools:deny"][0].GetString());
        var other = Directory.CreateDirectory(Path.Combine(_root, "other", "repository")).FullName;
        Assert.NotEqual(store.GetPath(_paths.RepositoryRoot), store.GetPath(other));
        Assert.False(store.Read(other).Exists);
        if (RepositoryPathPolicy.GetPathComparer(_paths.RepositoryRoot).Equals("a", "A"))
        {
            Assert.Equal(store.GetPath(_paths.RepositoryRoot), store.GetPath(_paths.RepositoryRoot.ToUpperInvariant()));
        }

        var path = store.GetPath(_paths.RepositoryRoot);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(path, ct))!.AsObject();
        root["repositoryIdentity"] = other;
        await File.WriteAllTextAsync(path, root.ToJsonString(), ct);
        Assert.Throws<InvalidDataException>(() => store.Read(_paths.RepositoryRoot));
    }

    /// <summary>Old SQLite choices are not imported and reads never create permission files.</summary>
    [Fact]
    public async Task Ownership_OldSqliteGrantIsIgnoredWithoutCreatingPermissionFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var connectionString = $"Data Source={Path.Combine(_root, "legacy.db")};Pooling=False";
        var facts = new SqliteRepositoryFactsStore(connectionString, _paths.UserConfiguration);
        await facts.InitializeAsync(ct);
        await using (var db = new SqliteConnection(connectionString))
        {
            await db.OpenAsync(ct);
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE repository_mutation_consent(repository_key TEXT PRIMARY KEY, policy INTEGER, granted_at TEXT); INSERT INTO repository_mutation_consent VALUES($key, $policy, 'fixture');";
            command.Parameters.AddWithValue("$key", OperatingSystem.IsWindows() ? _paths.RepositoryRoot.ToUpperInvariant() : _paths.RepositoryRoot);
            command.Parameters.AddWithValue("$policy", (int)MutationApprovalPolicy.AlwaysTrustRepo);
            await command.ExecuteNonQueryAsync(ct);
        }

        Assert.Null(await facts.GetMutationPolicyAsync(_paths.RepositoryRoot, ct));
        var permissions = new RepositoryPermissionStore(_paths.UserConfiguration);
        Assert.False(permissions.Read(_paths.RepositoryRoot).Exists);
        Assert.Null(await facts.GetMutationPolicyAsync(_paths.RepositoryRoot, ct));
    }

    /// <summary>Unknown future files and containing-directory operations remain protected.</summary>
    [Fact]
    public void Ownership_EntirePermissionTreeIsProtectedIncludingFutureFiles()
    {
        var policy = Load();
        var directory = new RepositoryPermissionStore(_paths.UserConfiguration).DirectoryPath;
        foreach (var target in new[] { directory, Path.Combine(directory, "future", "unknown.json"), Path.GetDirectoryName(directory)! })
        {
            Assert.Throws<UnauthorizedAccessException>(() => AuthorityControlPathGuard.Validate(target, _paths.RepositoryRoot, policy.ControlPaths, policy.ControlDirectories));
        }
    }

    /// <summary>Project preferences still bind without admitting project permission values.</summary>
    [Fact]
    public void Ownership_ProjectPreferenceBindingDoesNotBindPermissionValues()
    {
        File.WriteAllText(_paths.RepositoryConfiguration, """{"solution":{"path":"project.sln"},"mutation":{"approvalPolicy":"alwaysTrustRepo"}}""");
        using var configuration = ConfigurationBootstrap.Build([], _paths) as IDisposable;
        var loaded = (IConfiguration)configuration!;
        Assert.Equal("project.sln", loaded["solution:path"]);
        Assert.Equal("reviewAll", loaded["mutation:approvalPolicy"]);
    }

    private sealed record OwnershipFixture
    {
        [JsonPropertyName("nested")]
        public OwnershipNested? Nested { get; init; }
    }

    private sealed record OwnershipNested
    {
        [JsonPropertyName("grants")]
        [ConfigurationPermission(Shape = PermissionInputShape.List)]
        public string[]? Grants { get; init; }
    }
}
