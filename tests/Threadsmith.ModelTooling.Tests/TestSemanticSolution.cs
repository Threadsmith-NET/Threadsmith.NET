namespace Threadsmith.ModelTooling.Tests;

using System.Xml.Linq;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Xunit;

internal sealed class TestSemanticSolution : IDisposable
{
    private static readonly string[] _requiredFiles =
    [
        "SmallDotNetSolution.sln",
        "Directory.Build.props",
        "global.json",
        Path.Combine("App", "App.csproj"),
        Path.Combine("App", "Program.cs"),
        Path.Combine("Contracts", "Contracts.csproj"),
        Path.Combine("Contracts", "GeneratedMarker.g.cs"),
        Path.Combine("Contracts", "ModernSyntax.cs"),
        Path.Combine("Contracts", "Services.cs"),
        Path.Combine("Shared", "Linked.cs"),
    ];

    public void Dispose()
    {
        // Root is the exact owned child validated during construction. Engines declared
        // after this owner are disposed first, before their MSBuild outputs are removed.
        Directory.Delete(Root, recursive: true);
    }

    internal TestSemanticSolution()
    {
        Root = CopyToTemporaryRoot("threadsmith-semantic-owned");
    }

    internal string Root { get; }

    internal static string SourceRoot => Path.Combine(
        AppContext.BaseDirectory,
        "fixtures",
        "semantic",
        "SmallDotNetSolution");

    internal static SemanticLoadRequest CreateLoadRequest(string root)
    {
        return new SemanticLoadRequest(
            SessionId.New(),
            WorkspaceId.New(),
            root,
            Path.Combine(root, "SmallDotNetSolution.sln"),
            RepositoryTrustLevel.TrustedBuild);
    }

    internal static string CopyToTemporaryRoot(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        if (!string.Equals(prefix, Path.GetFileName(prefix), StringComparison.Ordinal)
            || Path.IsPathRooted(prefix))
        {
            throw new ArgumentException("The fixture prefix must be one path segment.", nameof(prefix));
        }

        var fixtureId = Guid.NewGuid();
        var temporaryParent = Path.GetFullPath(Path.GetTempPath());
        var expectedName = $"{prefix}-{fixtureId:N}";
        var destinationRoot = Path.GetFullPath(Path.Combine(temporaryParent, expectedName));
        var destinationParent = Path.GetDirectoryName(destinationRoot);
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(destinationParent, Path.TrimEndingDirectorySeparator(temporaryParent), pathComparison)
            || !string.Equals(Path.GetFileName(destinationRoot), expectedName, pathComparison))
        {
            throw new InvalidOperationException("The fixture root is not the exact owned temporary child.");
        }

        Directory.CreateDirectory(destinationRoot);
        try
        {
            foreach (var relativePath in _requiredFiles)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                var destinationPath = Path.Combine(destinationRoot, relativePath);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(destinationPath)
                        ?? throw new InvalidOperationException("Fixture destination has no parent."));
                File.Copy(Path.Combine(SourceRoot, relativePath), destinationPath);
            }

            // Roslyn retains loaded analyzer assemblies for the process lifetime. Share the
            // immutable deployed DLL, while each owner has independent project/obj state.
            var analyzerPath = Path.Combine(SourceRoot, "Analyzers", "Threadsmith.SemanticFixtures.Roslyn59.dll");
            if (!File.Exists(analyzerPath))
            {
                throw new FileNotFoundException("The deployed fixture analyzer is missing.", analyzerPath);
            }

            var projectPath = Path.Combine(destinationRoot, "Contracts", "Contracts.csproj");
            var project = XDocument.Load(projectPath);
            project.Descendants("Analyzer").Single().SetAttributeValue("Include", analyzerPath);
            project.Save(projectPath);
            return destinationRoot;
        }
        catch (Exception primaryFailure)
        {
            try
            {
                Directory.Delete(destinationRoot, recursive: true);
            }
            catch (Exception cleanupFailure) when (cleanupFailure is IOException or UnauthorizedAccessException)
            {
                primaryFailure.Data["FixtureCleanupFailure"] = cleanupFailure;
            }

            throw;
        }
    }
}
