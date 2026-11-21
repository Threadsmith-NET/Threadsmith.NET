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
