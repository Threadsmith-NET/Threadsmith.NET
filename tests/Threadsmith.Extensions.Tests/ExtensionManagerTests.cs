namespace Threadsmith.Extensions.Tests;

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
        var path = Path.Combine(Path.GetTempPath(), "threadsmith-m7-tests", Guid.NewGuid().ToString("N") + ".json");
        var config = ExtensionSelectionConfig.LoadOrDefault(path);
        Assert.Empty(config.AutoLoad);
        Assert.Equal(".threadsmith/extensions", config.DiscoveryDirectory);
    }

    /// <summary>A valid extension selection file supplies discovery and automatic-load settings.</summary>
    [Fact]
    public void ExtensionSelectionConfig_LoadOrDefault_parses_autoload_and_discovery_directory()
    {
        var path = Path.Combine(Path.GetTempPath(), "threadsmith-m7-tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"discoveryDirectory":".threadsmith/ext","autoLoad":["a","b"]}""");
        var config = ExtensionSelectionConfig.LoadOrDefault(path);
        Assert.Equal(".threadsmith/ext", config.DiscoveryDirectory);
        Assert.Equal(2, config.AutoLoad.Count);
        Assert.Equal("a", config.AutoLoad[0]);
    }

    private static string NewStagingRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-m7-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string ResolveSampleOutputDirectory()
    {
        var bin = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "..", ".."));
        var configuration = bin.Contains("Debug", StringComparison.Ordinal) ? "Debug" : "Release";
        return Path.Combine(repoRoot, "samples", "extensions", "MinimalToolExtension", "bin", configuration, "net10.0");
    }

    private static string ResolveFixtureOutputDirectory(string fixtureProjectDirName)
    {
        var bin = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(bin, "..", "..", "..", "..", ".."));
        var configuration = bin.Contains("Debug", StringComparison.Ordinal) ? "Debug" : "Release";
        return Path.Combine(repoRoot, "tests", "Threadsmith.Extensions.Tests", "Fixtures", fixtureProjectDirName, "bin", configuration, "net10.0");
    }
}
