namespace Threadsmith.Milestone7.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Extensions.Runtime;
using Xunit;

/// <summary>
/// Plan-16 task 10 (Extension Manager surface): the host-owned <see cref="IExtensionManager"/> contract
/// exposes discovery, load-by-id, unload-by-id, and summaries as host-owned DTOs so the interactive
/// shell can drive the navigable /extensions list without referencing the extension runtime (§8.1).
/// Also covers the repo-level <see cref="ExtensionSelectionConfig"/> (strategy §21; repo-level only).
/// </summary>
public sealed class ExtensionManagerTests
{
    /// <summary>Discovery reports both active and available extension generations.</summary>
    [Fact]
    public async Task DiscoverAsync_lists_loaded_and_unloaded_extensions()
    {
        var host = new ExtensionHost(new RecordingEventStream(), NullLogger<ExtensionHost>.Instance);
        host.SetDiscoveryDirectory(ResolveSampleOutputDirectory());
        // Load the sample so it appears as Active alongside any discovered-but-unloaded entries.
        var generation = await host.LoadAsync(new ExtensionLoadRequest
        {
            ExtensionDirectory = ResolveSampleOutputDirectory(),
            EffectiveTrust = RepositoryTrustLevel.TrustedRead,
            SessionId = SessionId.New(),
            ShadowStagingRoot = NewStagingRoot(),
        });

        var summaries = await host.DiscoverAsync();

        var active = summaries.FirstOrDefault(s => s.IsLoaded);
        Assert.NotNull(active);
        Assert.Equal("Active", active.State);
        Assert.Equal(1, active.ToolCount);
        Assert.Equal(1, active.ModelPreferenceContributorCount);
        Assert.Equal(generation.GenerationId, active.GenerationId);
    }

    /// <summary>Loading a discovered extension by id activates and summarizes it.</summary>
    [Fact]
    public async Task LoadAsync_by_id_loads_a_discovered_extension()
    {
        var host = new ExtensionHost(new RecordingEventStream(), NullLogger<ExtensionHost>.Instance);
        host.SetDiscoveryDirectory(ResolveSampleOutputDirectory());
        await host.DiscoverAsync();

        var loaded = await ((IExtensionManager)host).LoadAsync(
            "threadsmith.sample.minimal-tool",
            SessionId.New());

        Assert.NotNull(loaded);
        Assert.True(loaded.IsLoaded);
        Assert.Equal("Active", loaded.State);
        Assert.Equal(1, loaded.ToolCount);
    }

    /// <summary>Unloading by extension id releases the active generation.</summary>
    [Fact]
    public async Task UnloadAsync_by_id_unloads_the_active_generation()
    {
        // Use the CleanUnloadExtension fixture (never invoked by other tests) so its ALC is not
        // JIT-rooted and unload verification succeeds.
        var host = new ExtensionHost(new RecordingEventStream(), NullLogger<ExtensionHost>.Instance);
        host.SetDiscoveryDirectory(ResolveFixtureOutputDirectory("CleanUnloadExtension"));
        await ((IExtensionManager)host).LoadAsync("threadsmith.tests.clean-unload", SessionId.New());

        bool unloaded = await ((IExtensionManager)host).UnloadAsync(
            "threadsmith.tests.clean-unload",
            SessionId.New());

        Assert.True(unloaded);
        var summary = ((IExtensionManager)host).Summaries
            .FirstOrDefault(s => s.ExtensionId == "threadsmith.tests.clean-unload");
        Assert.True(summary is null || !summary.IsLoaded);
    }

    /// <summary>Loading an unknown extension id returns no summary.</summary>
    [Fact]
    public async Task LoadAsync_by_id_returns_null_for_unknown_extension()
    {
        var host = new ExtensionHost(new RecordingEventStream(), NullLogger<ExtensionHost>.Instance);
        host.SetDiscoveryDirectory(ResolveSampleOutputDirectory());
        var loaded = await ((IExtensionManager)host).LoadAsync(
            "com.example.does-not-exist",
            SessionId.New());
        Assert.Null(loaded);
    }

    /// <summary>Extension selection defaults to no automatic loads and the repository discovery directory.</summary>
    [Fact]
    public void ExtensionSelectionConfig_defaults_to_no_autoload_and_default_discovery_directory()
    {
        var config = new ExtensionSelectionConfig();
        Assert.Empty(config.AutoLoad);
        Assert.Equal(".threadsmith/extensions", config.DiscoveryDirectory);
    }

    /// <summary>A missing extension selection file produces the compiled defaults.</summary>
    [Fact]
    public void ExtensionSelectionConfig_LoadOrDefault_returns_defaults_for_missing_file()
    {
        string path = Path.Combine(Path.GetTempPath(), "threadsmith-m7-tests", Guid.NewGuid().ToString("N") + ".json");
        var config = ExtensionSelectionConfig.LoadOrDefault(path);
        Assert.Empty(config.AutoLoad);
        Assert.Equal(".threadsmith/extensions", config.DiscoveryDirectory);
    }

    /// <summary>A valid extension selection file supplies discovery and automatic-load settings.</summary>
    [Fact]
    public void ExtensionSelectionConfig_LoadOrDefault_parses_autoload_and_discovery_directory()
    {
        string path = Path.Combine(Path.GetTempPath(), "threadsmith-m7-tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"discoveryDirectory":".threadsmith/ext","autoLoad":["a","b"]}""");
        var config = ExtensionSelectionConfig.LoadOrDefault(path);
        Assert.Equal(".threadsmith/ext", config.DiscoveryDirectory);
        Assert.Equal(2, config.AutoLoad.Count);
        Assert.Equal("a", config.AutoLoad[0]);
    }

    /// <summary>Malformed extension selection JSON safely falls back to compiled defaults.</summary>
    [Fact]
    public void ExtensionSelectionConfig_LoadOrDefault_falls_back_on_malformed_json()
    {
        string path = Path.Combine(Path.GetTempPath(), "threadsmith-m7-tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{not valid json");
        var config = ExtensionSelectionConfig.LoadOrDefault(path);
        Assert.Empty(config.AutoLoad);
    }

    private static string NewStagingRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "threadsmith-m7-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ResolveSampleOutputDirectory()
    {
        string bin = AppContext.BaseDirectory;
        string repoRoot = Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "..", ".."));
        string configuration = bin.Contains("Debug", StringComparison.Ordinal) ? "Debug" : "Release";
        return Path.Combine(repoRoot, "samples", "extensions", "MinimalToolExtension", "bin", configuration, "net10.0");
    }

    private static string ResolveFixtureOutputDirectory(string fixtureProjectDirName)
    {
        string bin = AppContext.BaseDirectory;
        string repoRoot = Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "..", ".."));
        string configuration = bin.Contains("Debug", StringComparison.Ordinal) ? "Debug" : "Release";
        return Path.Combine(repoRoot, "tests", "Threadsmith.Milestone7.Tests", "Fixtures", fixtureProjectDirName, "bin", configuration, "net10.0");
    }
}