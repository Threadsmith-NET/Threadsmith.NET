namespace Threadsmith.Architecture.Tests;

using System.Text.Json;
using System.Xml.Linq;
using Xunit;

/// <summary>Guards the atomic Roslyn, SDK, and SDK-owned MSBuild toolchain contract.</summary>
public static class SemanticToolchainArchitectureTests
{
    private static string RepositoryRoot => Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>The repository SDK and all direct Roslyn packages move as one exact family.</summary>
    [Fact]
    public static void RepositoryPinsOneRoslyn59AndSdk104xxToolchain()
    {
        using var globalJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot, "global.json")));
        var sdk = globalJson.RootElement.GetProperty("sdk");
        Assert.Equal("10.0.401", sdk.GetProperty("version").GetString());
        Assert.Equal("latestPatch", sdk.GetProperty("rollForward").GetString());

        var packages = XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Packages.props"))
            .Descendants("PackageVersion")
            .Where(element => element.Attribute("Include")?.Value.StartsWith(
                "Microsoft.CodeAnalysis.",
                StringComparison.Ordinal) == true)
            .ToDictionary(
                element => element.Attribute("Include")!.Value,
                element => element.Attribute("Version")?.Value,
                StringComparer.Ordinal);
        var expected = new[]
        {
            "Microsoft.CodeAnalysis.CSharp",
            "Microsoft.CodeAnalysis.CSharp.Scripting",
            "Microsoft.CodeAnalysis.CSharp.Workspaces",
            "Microsoft.CodeAnalysis.Workspaces.MSBuild",
        };
        Assert.Equal(expected.Order(StringComparer.Ordinal), packages.Keys.Order(StringComparer.Ordinal));
        Assert.All(packages.Values, version => Assert.Equal("5.9.0", version));
    }

    /// <summary>Only the locator may supply MSBuild behavior; framework references stay compile-only.</summary>
    [Fact]
    public static void MsBuildPackageReferencesPreserveSdkRuntimeOwnership()
    {
        var projectPaths = new[] { "src", "tests", "spikes", "samples" }
            .Select(root => Path.Combine(RepositoryRoot, root))
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(
                root,
                "*.csproj",
                SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        var violations = new List<string>();
        foreach (var projectPath in projectPaths)
        {
            foreach (var reference in XDocument.Load(projectPath).Descendants("PackageReference"))
            {
                var id = reference.Attribute("Include")?.Value;
                if (id is null || !id.StartsWith("Microsoft.Build", StringComparison.Ordinal))
                {
                    continue;
                }

                if (id == "Microsoft.Build.Locator")
                {
                    continue;
                }

                if (id != "Microsoft.Build.Framework"
                    || reference.Attribute("ExcludeAssets")?.Value != "runtime"
                    || reference.Attribute("PrivateAssets")?.Value != "all")
                {
                    violations.Add($"{Path.GetRelativePath(RepositoryRoot, projectPath)}: {id}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "MSBuild runtime ownership violations: " + string.Join(", ", violations));
    }
}
