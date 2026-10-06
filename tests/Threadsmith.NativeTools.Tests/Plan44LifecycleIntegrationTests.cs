namespace Threadsmith.NativeTools.Tests;

using Threadsmith.Core;
using Threadsmith.Validation;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Verifies Plan-44 lifecycle endpoints remain integrated with validation and isolated workers.</summary>
public sealed class Plan44LifecycleIntegrationTests
{
    /// <summary>Move source and destination paths select direct owners and transitive dependent tests.</summary>
    [Fact]
    public void LifecycleEndpoints_SelectAffectedProjectsAndDependents()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-plan44-impact-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "src", "Library", "Library.csproj");
        var app = Path.Combine(root, "src", "App", "App.csproj");
        var tests = Path.Combine(root, "tests", "App.Tests", "App.Tests.csproj");
        SemanticProjectInfo[] projects =
        [
            new("Library", library, ["net10.0"], SemanticConfidenceLevel.FullSemantic, [], []),
            new("App", app, ["net10.0"], SemanticConfidenceLevel.FullSemantic, [library], []),
            new("App.Tests", tests, ["net10.0"], SemanticConfidenceLevel.FullSemantic, [app], []),
        ];

        var affected = AffectedProjectCalculator.Calculate(
            root,
            ["src/Library/Before.cs", "src/App/After.cs"],
            projects);

        Assert.Contains(affected.Projects, project => project.Name == "Library" && project.IsDirectlyChanged);
        Assert.Contains(affected.Projects, project => project.Name == "App" && project.IsDirectlyChanged);
        Assert.Contains(affected.Projects, project => project.Name == "App.Tests" && !project.IsDirectlyChanged);
        Assert.Empty(affected.UnmappedFiles);
    }
}
