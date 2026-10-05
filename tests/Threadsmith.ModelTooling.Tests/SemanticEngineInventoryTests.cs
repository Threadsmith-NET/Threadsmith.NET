namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Frozen;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Xunit;

/// <summary>Checks that refresh membership follows solution publication without callback-time reconstruction.</summary>
public static class SemanticEngineInventoryTests
{
    /// <summary>Text refresh retains membership, full reload replaces it, and metadata-only load clears it.</summary>
    [Fact]
    public static async Task InventoryIsImmutableAndTracksPublishedSolutionMembership()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), "threadsmith-inventory", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var projectPath = Path.Combine(root, "App.csproj");
            var sourcePath = Path.Combine(root, "Source.cs");
            var ignoredDirectory = Directory.CreateDirectory(Path.Combine(root, "obj")).FullName;
            var explicitSourcePath = Path.Combine(ignoredDirectory, "UserSource.cs");
            var additionalPath = Path.Combine(ignoredDirectory, "loaded.json");
            var configPath = Path.Combine(ignoredDirectory, "custom.editorconfig");
            var referencePath = Path.Combine(root, "Library.dll");
            File.Copy(typeof(SessionId).Assembly.Location, referencePath);
            await File.WriteAllTextAsync(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <Compile Include="obj/UserSource.cs" />
                    <AdditionalFiles Include="obj/loaded.json" />
                    <EditorConfigFiles Include="obj/custom.editorconfig" />
                    <Reference Include="Threadsmith.Core"><HintPath>Library.dll</HintPath></Reference>
                  </ItemGroup>
                </Project>
                """,
                cancellationToken);
            await File.WriteAllTextAsync(sourcePath, "public class Source { }", cancellationToken);
            await File.WriteAllTextAsync(explicitSourcePath, "public class UserSource { }", cancellationToken);
            await File.WriteAllTextAsync(additionalPath, "{}", cancellationToken);
            await File.WriteAllTextAsync(configPath, "root = true", cancellationToken);
            await using var events = new DomainEventStream();
            await using var engine = new SemanticEngine(events, NullLogger<SemanticEngine>.Instance, TestPromptLoader.Instance);
            var empty = engine.GetRefreshInventory();
            var request = new SemanticLoadRequest(
                SessionId.New(), WorkspaceId.New(), root, projectPath, RepositoryTrustLevel.TrustedBuild);
            await engine.LoadAsync(request, cancellationToken);
            var inventory = engine.GetRefreshInventory();

            Assert.NotSame(empty, inventory);
            Assert.Contains(sourcePath, inventory.SourceDocuments);
            Assert.Contains(explicitSourcePath, inventory.SourceDocuments);
            Assert.Contains(additionalPath, inventory.AdditionalDocuments);
            Assert.Contains(configPath, inventory.AnalyzerConfigDocuments);
            Assert.Contains(referencePath, inventory.FullReloadInputs);
            Assert.IsType<FrozenSet<string>>(inventory.SourceDocuments, exactMatch: false);
            Assert.IsType<FrozenSet<string>>(inventory.AdditionalDocuments, exactMatch: false);
            Assert.IsType<FrozenSet<string>>(inventory.AnalyzerConfigDocuments, exactMatch: false);
            Assert.IsType<FrozenSet<string>>(inventory.FullReloadInputs, exactMatch: false);
            for (var index = 0; index < 256; index++)
            {
                Assert.Same(inventory, engine.GetRefreshInventory());
            }

            await engine.RefreshDocumentsAsync(
                [new SemanticDocumentRefresh(sourcePath, "public class Updated { }", "updated")], cancellationToken);
            Assert.Same(inventory, engine.GetRefreshInventory());

            var addedPath = Path.Combine(root, "Added.cs");
            await File.WriteAllTextAsync(addedPath, "public class Added { }", cancellationToken);
            await engine.RefreshFullAsync(cancellationToken);
            var reloaded = engine.GetRefreshInventory();
            Assert.NotSame(inventory, reloaded);
            Assert.Contains(addedPath, reloaded.SourceDocuments);
            Assert.DoesNotContain(addedPath, inventory.SourceDocuments);
            Assert.Same(reloaded, engine.GetRefreshInventory());

            await engine.LoadAsync(request with { TrustLevel = RepositoryTrustLevel.UntrustedInspection }, cancellationToken);
            Assert.Same(empty, engine.GetRefreshInventory());
            await engine.DisposeAsync();
            Assert.Same(empty, engine.GetRefreshInventory());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
