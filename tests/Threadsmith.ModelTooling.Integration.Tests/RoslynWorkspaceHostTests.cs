namespace Threadsmith.ModelTooling.Integration.Tests;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Text;
using Threadsmith.DotNet;
using Xunit;

/// <summary>Verifies the pinned Roslyn storage composition and workspace isolation.</summary>
public static class RoslynWorkspaceHostTests
{
    /// <summary>The production host excludes the pinned optional SQLite export on every platform.</summary>
    [Fact]
    public static void ProductionStorageSelectionKeepsNonpersistentHostFreeOfSqlite()
    {
        // Resolve the registered service contract from the pinned factory rather than asking MEF for a concrete implementation type.
        var factory = typeof(Workspace).Assembly.GetType("Microsoft.CodeAnalysis.SQLite.v2.SQLitePersistentStorageService+ServiceFactory", throwOnError: true)!;
        var export = Assert.Single(factory.GetCustomAttributesData(), attribute => attribute.AttributeType == typeof(ExportWorkspaceServiceFactoryAttribute));
        var contract = Assert.IsAssignableFrom<Type>(export.ConstructorArguments[0].Value);
        var getService = typeof(HostWorkspaceServices).GetMethod(nameof(HostWorkspaceServices.GetService))!.MakeGenericMethod(contract);

        using var productionWorkspace = new AdhocWorkspace(RoslynWorkspaceHost.Services);
        Assert.Null(getService.Invoke(productionWorkspace.Services, null));
    }

    /// <summary>Concurrent workspaces retain source and metadata lookup without sharing mutable storage.</summary>
    [Fact]
    public static async Task NonPersistentHost_ConcurrentWorkspaces_QuerySourceAndMetadataIndependently()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
        {
            using var workspace = new AdhocWorkspace(RoslynWorkspaceHost.Services);
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
