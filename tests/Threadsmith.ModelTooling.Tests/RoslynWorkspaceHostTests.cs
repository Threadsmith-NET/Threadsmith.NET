namespace Threadsmith.ModelTooling.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Text;
using Threadsmith.DotNet;
using Xunit;

/// <summary>Verifies the pinned Roslyn storage composition and workspace isolation.</summary>
public static class RoslynWorkspaceHostTests
{
    /// <summary>The nonpersistent host selects Roslyn's fallback on every operating system.</summary>
    [Fact]
    public static void NonPersistentHost_OmitsSqliteService()
    {
        using var workspace = new AdhocWorkspace(RoslynWorkspaceHost.WithoutPersistentStorage);
        // Check the actual workspace service, not just the composition filter. Roslyn's
        // storage service is internal, so reflection is confined to this upgrade contract test.
        var storageType = typeof(Workspace).Assembly.GetType(
            "Microsoft.CodeAnalysis.SQLite.v2.SQLitePersistentStorageService", throwOnError: true)!;
        var getService = typeof(HostWorkspaceServices).GetMethod(nameof(HostWorkspaceServices.GetService))!
            .MakeGenericMethod(storageType);
        Assert.Null(getService.Invoke(workspace.Services, null));
        Assert.Same(
            OperatingSystem.IsWindows() ? Microsoft.CodeAnalysis.Host.Mef.MefHostServices.DefaultHost : RoslynWorkspaceHost.WithoutPersistentStorage,
            RoslynWorkspaceHost.Services);
    }

    /// <summary>Concurrent workspaces retain source and metadata lookup without sharing mutable storage.</summary>
    [Fact]
    public static async Task NonPersistentHost_ConcurrentWorkspaces_QuerySourceAndMetadataIndependently()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            using var workspace = new AdhocWorkspace(RoslynWorkspaceHost.WithoutPersistentStorage);
            workspace.AddSolution(SolutionInfo.Create(
                SolutionId.CreateNewId(),
                VersionStamp.Create(),
                filePath: Path.Combine(Path.GetTempPath(), $"threadsmith-storage-{Guid.NewGuid():N}.sln")));
            var projectId = ProjectId.CreateNewId();
            var solution = workspace.CurrentSolution
                .AddProject(projectId, $"Project{index}", $"Assembly{index}", LanguageNames.CSharp)
                .AddMetadataReference(projectId, MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                .AddDocument(DocumentId.CreateNewId(projectId), "Marker.cs", SourceText.From($"public class Marker{index} {{ }}"));
            Assert.True(workspace.TryApplyChanges(solution));
            var project = workspace.CurrentSolution.GetProject(projectId)!;
            for (var iteration = 0; iteration < 3; iteration++)
            {
                var source = await SymbolFinder.FindDeclarationsAsync(project, $"Marker{index}", false, SymbolFilter.Type, cancellationToken);
                Assert.Single(source);
                var metadata = await SymbolFinder.FindDeclarationsAsync(project, "String", false, SymbolFilter.Type, cancellationToken);
                Assert.Contains(metadata, symbol => symbol.ToDisplayString() == "string");
                var other = await SymbolFinder.FindDeclarationsAsync(project, $"Marker{(index + 1) % 8}", false, SymbolFilter.Type, cancellationToken);
                Assert.Empty(other);
            }
        }));
    }
}
