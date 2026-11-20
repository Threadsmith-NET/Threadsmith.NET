namespace Threadsmith.DotNet;

using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.CodeAnalysis;
using Threadsmith.Core;
using BuildProject = Microsoft.Build.Evaluation.Project;
using Project = Microsoft.CodeAnalysis.Project;

/// <content>Discovers possible source owners before running design-time targets.</content>
public sealed partial class SemanticEngine
{
    private static readonly FrozenSet<string> _sourceInputItems = FrozenSet.ToFrozenSet<string>(
        ["Compile", "AdditionalFiles", "EditorConfigFiles", "GlobalAnalyzerConfigFiles", "CscCommandLineArgs"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] _designTimeOutputPaths =
    [
        "BaseIntermediateOutputPath", "IntermediateOutputPath", "MSBuildProjectExtensionsPath", "OutDir", "OutputPath",
        "TargetPath", "GeneratedAssemblyInfoFile", "TargetFrameworkMonikerAssemblyAttributesPath",
        "GeneratedGlobalUsingsFile", "GeneratedMSBuildEditorConfigFile", "CompilerGeneratedFilesOutputPath",
    ];

    private static ProjectSourceOwners? FindSourceRefreshOwners(
        ILookup<string?, Project> projectsByPath,
        ImmutableDictionary<string, string> workspaceProperties,
        SemanticLoadRequest request,
        Dictionary<string, SemanticDocumentRefresh> added,
        bool allowParallelLoads,
        CancellationToken cancellationToken)
    {
        if (projectsByPath.Count == 1)
        {
            var group = projectsByPath.Single();
            return group.Key is { } path && IsSemanticInputPathAllowed(path, request)
                && !group.Skip(1).Any() && group.First().Language == LanguageNames.CSharp
                    ? new([group.First()], CanLoadInParallel: false)
                    : null;
        }

        // OpenSolutionAsync supplies SolutionDir through the workspace's actual loader properties.
        // Use the same global properties as the .NET Roslyn build host for evaluation-only discovery.
        var globals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DesignTimeBuild"] = bool.TrueString,
            ["NonExistentFile"] = @"__NonExistentSubDir__\__NonExistentFile__",
            ["BuildProjectReferences"] = bool.FalseString,
            ["BuildingProject"] = bool.FalseString,
            ["ProvideCommandLineArgs"] = bool.TrueString,
            ["SkipCompilerExecution"] = bool.TrueString,
            ["ContinueOnError"] = "ErrorAndContinue",
            ["ShouldUnsetParentConfigurationAndPlatform"] = bool.FalseString,
        };
        foreach (var pair in workspaceProperties)
        {
            globals[pair.Key] = pair.Value;
        }

        // Share imported XML and filesystem lookups within this batch. A new collection/context
        // for the next batch rediscovers conditional imports and previously absent wildcard matches.
        using var collection = new ProjectCollection(globals);
        var context = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);
        var options = new ProjectOptions { ProjectCollection = collection, EvaluationContext = context };
        var opaqueTargets = new Dictionary<string, bool>(StringComparerForCurrentPlatform());
        var owners = new List<Project>();
        foreach (var group in projectsByPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Key is not { } path || !IsSemanticInputPathAllowed(path, request)
                || Path.GetDirectoryName(path) is not { } directory || group.Any(project => project.Language != LanguageNames.CSharp))
            {
                return null;
            }

            if (!added.Keys.Any(file => IsPathWithinRoot(file, directory))
                && !MayOwnAddedSource(path, group, options, opaqueTargets, request, added, cancellationToken))
            {
                continue;
            }

            // A multi-target owner needs the normal loader's complete set of inner builds.
            if (group.Skip(1).Any())
            {
                return null;
            }

            owners.Add(group.First());
            if (owners.Count > 1 && owners.Count > projectsByPath.Count / 2)
            {
                return null;
            }
        }

        if (owners.Count == 0)
        {
            return null;
        }

        var canLoadInParallel = allowParallelLoads && owners.Count > 1
            && CanLoadOwnersInParallel(owners, collection, options, request, cancellationToken);
        return new(owners, canLoadInParallel);
    }

    private static bool CanLoadOwnersInParallel(
        List<Project> owners,
        ProjectCollection collection,
        ProjectOptions options,
        SemanticLoadRequest request,
        CancellationToken cancellationToken)
    {
        // Separate workspaces isolate Roslyn state, not files written by design-time targets.
        // Only overlap standalone SDK builds whose known outputs stay in disjoint project trees.
        var directories = new List<string>();
        foreach (var owner in owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (owner.ProjectReferences.Any() || owner.FilePath is not { } path
                || Path.GetDirectoryName(path) is not { } directory
                || directories.Any(other => ContainsDirectory(other, directory) || ContainsDirectory(directory, other)))
            {
                return false;
            }

            var evaluated = collection.GetLoadedProjects(path).FirstOrDefault() ?? BuildProject.FromFile(path, options);
            var toolsPath = evaluated.GetPropertyValue("MSBuildToolsPath");
            if (evaluated.GetItems("ProjectReference").Count != 0
                || evaluated.GetPropertyValue("TargetFrameworks").Length != 0
                || evaluated.Imports.Select(import => import.ImportedProject).Prepend(evaluated.Xml)
                    .Any(declaration => (declaration.Targets.Count != 0 || declaration.UsingTasks.Count != 0)
                        && !IsStockBuildDeclaration(declaration, toolsPath)))
            {
                return false;
            }

            foreach (var property in _designTimeOutputPaths)
            {
                var output = evaluated.GetPropertyValue(property);
                if (output.Length != 0)
                {
                    var fullPath = Path.GetFullPath(output.Replace('\\', Path.DirectorySeparatorChar), directory);
                    if (!IsPathWithinRoot(fullPath, directory) || !IsSemanticInputPathAllowed(fullPath, request))
                    {
                        return false;
                    }
                }
            }

            directories.Add(directory);
        }

        return true;

        // Treat case-only differences as possible aliases even on Unix volumes.
        static bool ContainsDirectory(string root, string path) => path.Equals(root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MayOwnAddedSource(
        string projectPath,
        IEnumerable<Project> instances,
        ProjectOptions options,
        Dictionary<string, bool> opaqueTargets,
        SemanticLoadRequest request,
        Dictionary<string, SemanticDocumentRefresh> added,
        CancellationToken cancellationToken)
    {
        var evaluated = BuildProject.FromFile(projectPath, options);
        var frameworks = evaluated.GetPropertyValue("TargetFrameworks");
        if (evaluated.GetPropertyValue("TargetFramework").Length != 0 || frameworks.Length == 0)
        {
            return instances.Skip(1).Any() || InspectInputs(instances.First());
        }

        var targets = frameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (targets.Length != instances.Count())
        {
            return true;
        }

        foreach (var framework in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            evaluated.SetGlobalProperty("TargetFramework", framework);
            evaluated.ReevaluateIfNecessary(options.EvaluationContext);
            var output = evaluated.GetPropertyValue("TargetPath");
            var matching = instances.Where(instance => string.Equals(instance.OutputFilePath, output, PathComparison)).ToArray();
            if (matching.Length != 1 || InspectInputs(matching[0]))
            {
                return true;
            }
        }

        return false;

        bool InspectInputs(Project instance)
        {
            var comparer = StringComparerForCurrentPlatform();
            var sources = new HashSet<string>(comparer);
            foreach (var itemType in _sourceInputItems)
            {
                foreach (var item in evaluated.GetItems(itemType))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var file = item.GetMetadataValue("FullPath");
                    if (added.ContainsKey(file))
                    {
                        return true;
                    }

                    if (itemType == "Compile" && IsOrdinarySource(file))
                    {
                        sources.Add(file);
                    }
                }
            }

            // An import can also acquire or drop a previously published source at the same time.
            if (!sources.SetEquals(instance.Documents.Select(document => document.FilePath).OfType<string>().Where(IsOrdinarySource))
                || evaluated.GetItems("ProjectReference").Any(reference => _sourceInputItems.Contains(reference.GetMetadataValue("OutputItemType"))))
            {
                return true;
            }

            // Evaluation covers declarative items. Custom targets can create compiler inputs at
            // execution time, so the design-time loader must decide membership for those projects.
            foreach (var declaration in evaluated.Imports.Select(import => import.ImportedProject).Prepend(evaluated.Xml))
            {
                if (!opaqueTargets.TryGetValue(declaration.FullPath, out var opaque))
                {
                    opaque = HasCustomCompilerTargets(declaration, evaluated.GetPropertyValue("MSBuildToolsPath"));
                    opaqueTargets.Add(declaration.FullPath, opaque);
                }

                if (opaque)
                {
                    return true;
                }
            }

            return false;
        }

        // Compare names only. No file is opened or admitted by this comparison. A filtered
        // repository source missing from the live project will conservatively select its owner.
        bool IsOrdinarySource(string file) => IsPathWithinRoot(file, request.RepositoryPath)
            && !SemanticRefreshPathPolicy.IsIgnoredPath(request.RepositoryPath, file)
            && !SemanticRefreshPathPolicy.IsIgnoredGeneratedDocument(request.RepositoryPath, file)
            && !CodeExploreGeneratedSourceClassifier.IsGeneratedPath(file);
    }

    private static bool HasCustomCompilerTargets(ProjectRootElement project, string toolsPath)
    {
        if (IsStockBuildDeclaration(project, toolsPath))
        {
            return false;
        }

        return project.Targets.SelectMany(target => target.AllChildren).Any(element => element switch
        {
            ProjectItemElement item => _sourceInputItems.Contains(item.ItemType),
            ProjectOutputElement output => _sourceInputItems.Contains(output.ItemType),
            _ => false,
        });
    }

    private static bool IsStockBuildDeclaration(ProjectRootElement project, string toolsPath)
    {
        // User/machine extension directories under the toolset can contain arbitrary targets.
        return toolsPath.Length != 0 && IsPathWithinRoot(project.FullPath, toolsPath)
            && !project.FullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(part => part.Equals("ImportBefore", StringComparison.OrdinalIgnoreCase) || part.Equals("ImportAfter", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record ProjectSourceOwners(IReadOnlyList<Project> Projects, bool CanLoadInParallel);
}
