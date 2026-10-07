namespace Threadsmith.RepositoryIntelligence.Tests;

using System.Reflection;
using System.Xml.Linq;
using Xunit;

/// <summary>Verifies the dormant assembly has only its approved dependency and no public feature API.</summary>
public static class RepositoryIntelligenceBoundaryTests
{
    private static readonly string RepositoryRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    /// <summary>The feature project is compiled into the product solution with only a Core edge.</summary>
    [Fact]
    public static void SolutionAndProjectContainTheApprovedBoundary()
    {
        var solution = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "Threadsmith.sln"));
        var projectPath = Path.Combine(
            RepositoryRoot,
            "src",
            "Threadsmith.RepositoryIntelligence",
            "Threadsmith.RepositoryIntelligence.csproj");
        var project = XDocument.Load(projectPath);
        var references = project.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => Path.GetFileNameWithoutExtension(element.Attribute("Include")?.Value))
            .OfType<string>()
            .ToArray();

        Assert.Contains("Threadsmith.RepositoryIntelligence\\Threadsmith.RepositoryIntelligence.csproj", solution);
        Assert.Equal(["Threadsmith.Core"], references);
        Assert.DoesNotContain(project.Descendants(), element => element.Name.LocalName == "PackageReference");
    }

    /// <summary>Loading the dormant assembly exports no feature operations or foreign SDK types.</summary>
    [Fact]
    public static void AssemblyExportsNoFeatureApi()
    {
        var assembly = Assembly.Load("Threadsmith.RepositoryIntelligence");

        Assert.Empty(assembly.ExportedTypes);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), name =>
            name.Name?.StartsWith("Threadsmith.", StringComparison.Ordinal) == true
            && name.Name != "Threadsmith.Core");
    }

    /// <summary>Shared compiler and package policy applies without project-local exemptions.</summary>
    [Fact]
    public static void ProjectInheritsSharedBuildAndPackagePolicy()
    {
        var props = XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Build.props"));
        var packages = XDocument.Load(Path.Combine(RepositoryRoot, "Directory.Packages.props"));
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "Threadsmith.RepositoryIntelligence",
            "Threadsmith.RepositoryIntelligence.csproj"));

        Assert.Equal("net10.0", props.Descendants("TargetFramework").First().Value);
        Assert.Equal("latest", props.Descendants("LangVersion").First().Value);
        Assert.Equal("enable", props.Descendants("Nullable").First().Value);
        Assert.Equal("true", packages.Descendants("ManagePackageVersionsCentrally").First().Value);
        Assert.DoesNotContain(project.Descendants(), element => element.Name.LocalName is
            "LangVersion" or "Nullable" or "NoWarn" or "TreatWarningsAsErrors");
        Assert.DoesNotContain(project.Descendants(), element => element.Name.LocalName == "PackageReference"
            && element.Attribute("Version") is not null);
    }
}
