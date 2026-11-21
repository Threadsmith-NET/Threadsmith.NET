namespace Threadsmith.DotNet;

using System.Collections.Frozen;
using System.Collections.Immutable;
using Microsoft.Build.Construction;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
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
        ResolvedImportSnapshot baseline,
        SemanticLoadRequest request,
        Dictionary<string, SemanticDocumentRefresh> added,
        HashSet<string> untouched,
        bool allowParallelLoads,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var evaluation = new SourceRefreshEvaluation(workspaceProperties, request, baseline.MaximumXmlBytes);
        var singleProject = projectsByPath.Count == 1;
        var declarationImpacts = new Dictionary<(string Path, string Directory), SourceAdditionImpact>();
        var owners = new List<Project>();
        var membershipOwners = 0;
        foreach (var group in projectsByPath)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (group.Key is not { } path || !IsSemanticInputPathAllowed(path, request)
                || Path.GetDirectoryName(path) is not { } directory || group.Any(project => project.Language != LanguageNames.CSharp))
            {
                return null;
            }

            if (untouched.Contains(path))
            {
                continue;
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Evaluating project ownership for {ScopedProjectEvaluationPath}", path);
            }

            var evaluated = evaluation.OpenProject(path);
            if (!baseline.MatchesProject(path, evaluation.ReadImports(evaluated, cancellationToken)))
            {
                return new([], CanLoadInParallel: false, $"Resolved project imports changed or were not verified for '{path}'");
            }

            if (singleProject)
            {
                return group.Skip(1).Any() ? null : new([group.First()], CanLoadInParallel: false);
            }

            var (impact, detail) = InspectProjectImpact(directory, group, evaluated, evaluation, baseline, declarationImpacts, request, added, cancellationToken);
            if (impact == SourceAdditionImpact.Unknown)
            {
                return new([], CanLoadInParallel: false, $"Source addition dependencies could not be determined for '{path}': {detail}");
            }

            if (added.Keys.Any(file => IsPathWithinRoot(file, directory)))
            {
                impact = SourceAdditionImpact.Membership;
            }

            if (impact == SourceAdditionImpact.None)
            {
                continue;
            }

            // An affected multi-target project needs the normal loader's complete set of inner builds.
            if (group.Skip(1).Any())
            {
                return new([], CanLoadInParallel: false, $"Source addition affects multiple target frameworks in '{path}'");
            }

            owners.Add(group.First());
            if (impact == SourceAdditionImpact.Membership && ++membershipOwners > 1 && membershipOwners > projectsByPath.Count / 2)
            {
                return new([], CanLoadInParallel: false, "Source membership may change in a majority of the loaded projects");
            }
        }

        if (owners.Count == 0)
        {
            return null;
        }

        var outputDirectories = new List<string>(owners.Count);
        foreach (var owner in owners)
        {
            if (owner.FilePath is not { } path || Path.GetDirectoryName(path) is not { } directory
                || !HasConfinedDesignTimeOutputs(evaluation.GetLoadedProject(path), directory, request))
            {
                return new([], CanLoadInParallel: false, "Scoped design-time outputs could not be confined to the selected project directories");
            }

            outputDirectories.Add(directory);
        }

        return new(owners, allowParallelLoads && owners.Count > 1 && CanLoadOwnersInParallel(owners, outputDirectories, evaluation));
    }

    private static bool CanLoadOwnersInParallel(
        List<Project> owners,
        List<string> directories,
        SourceRefreshEvaluation evaluation)
    {
        // Separate workspaces isolate Roslyn state, not files written by design-time targets.
        var selected = new List<string>();
        for (var index = 0; index < owners.Count; index++)
        {
            var directory = directories[index];
            var owner = owners[index];
            if (owner.ProjectReferences.Any() || owner.FilePath is not { } path
                || TouchesAddition(directory, selected) || selected.Any(other => TouchesAddition(other, [directory])))
            {
                return false;
            }

            var evaluated = evaluation.GetLoadedProject(path);
            if (evaluated.GetItems("ProjectReference").Count != 0 || evaluated.GetPropertyValue("TargetFrameworks").Length != 0)
            {
                return false;
            }

            selected.Add(directory);
        }

        return true;
    }

    private static bool HasConfinedDesignTimeOutputs(BuildProject evaluated, string directory, SemanticLoadRequest request)
    {
        var toolsPath = evaluated.GetPropertyValue("MSBuildToolsPath");
        if (evaluated.Imports.Select(import => import.ImportedProject).Prepend(evaluated.Xml)
            .Any(declaration => (declaration.Targets.Count != 0 || declaration.UsingTasks.Count != 0)
                && !IsStockBuildDeclaration(declaration, toolsPath)))
        {
            return false;
        }

        foreach (var property in _designTimeOutputPaths)
        {
            var output = evaluated.GetPropertyValue(property);
            if (output.Length == 0)
            {
                continue;
            }

            var fullPath = Path.GetFullPath(output.Replace('\\', Path.DirectorySeparatorChar), directory);
            if (!IsPathWithinRoot(fullPath, directory) || !IsSemanticInputPathAllowed(fullPath, request))
            {
                return false;
            }
        }

        return true;
    }

    private static (SourceAdditionImpact Impact, string? Detail) InspectProjectImpact(
        string projectDirectory,
        IEnumerable<Project> instances,
        BuildProject evaluated,
        SourceRefreshEvaluation evaluation,
        ResolvedImportSnapshot baseline,
        Dictionary<(string Path, string Directory), SourceAdditionImpact> declarationImpacts,
        SemanticLoadRequest request,
        Dictionary<string, SemanticDocumentRefresh> added,
        CancellationToken cancellationToken)
    {
        const string unmatchedFrameworks = "The loaded target framework instances could not be matched";
        var targets = GetInnerTargetFrameworks(evaluated);
        if (targets.Length == 0)
        {
            return instances.Skip(1).Any()
                ? (SourceAdditionImpact.Unknown, unmatchedFrameworks)
                : InspectInputs(instances.First());
        }

        if (targets.Length != instances.Count())
        {
            return (SourceAdditionImpact.Unknown, unmatchedFrameworks);
        }

        foreach (var framework in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            evaluation.SelectFramework(evaluated, framework);
            if (!baseline.MatchesProject(evaluated.FullPath, evaluation.ReadImports(evaluated, cancellationToken)))
            {
                return (SourceAdditionImpact.Unknown, "Resolved project imports changed or were not verified for the target framework");
            }

            var output = evaluated.GetPropertyValue("TargetPath");
            var matching = instances.Where(instance => string.Equals(instance.OutputFilePath, output, PathComparison)).ToArray();
            if (matching.Length != 1)
            {
                return (SourceAdditionImpact.Unknown, unmatchedFrameworks);
            }

            var impact = InspectInputs(matching[0]);
            if (impact.Impact != SourceAdditionImpact.None)
            {
                return impact;
            }
        }

        return (SourceAdditionImpact.None, null);

        (SourceAdditionImpact Impact, string? Detail) InspectInputs(Project instance)
        {
            var impact = SourceAdditionImpact.None;
            var toolsPath = evaluated.GetPropertyValue("MSBuildToolsPath");
            foreach (var declaration in evaluated.Imports.Select(import => import.ImportedProject).Prepend(evaluated.Xml))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsStockBuildDeclaration(declaration, toolsPath))
                {
                    continue;
                }

                var key = (declaration.FullPath, projectDirectory);
                if (!declarationImpacts.TryGetValue(key, out var dependency))
                {
                    var dependencies = evaluation.GetSourceDependencies(declaration, projectDirectory);
                    dependency = dependencies.Opaque
                        ? SourceAdditionImpact.Unknown
                        : dependencies.ExistsPaths.Any(candidate => TouchesAddition(candidate, added.Keys))
                            ? SourceAdditionImpact.Configuration
                            : SourceAdditionImpact.None;
                    declarationImpacts.Add(key, dependency);
                }

                if (dependency == SourceAdditionImpact.Unknown)
                {
                    return (dependency, $"Opaque build declaration '{declaration.FullPath}'");
                }

                if (dependency == SourceAdditionImpact.Configuration)
                {
                    impact = dependency;
                }
            }

            if (evaluated.GetItems("ProjectReference").Any(reference => _sourceInputItems.Contains(reference.GetMetadataValue("OutputItemType"))))
            {
                return (SourceAdditionImpact.Unknown, "A project reference can produce compiler inputs");
            }

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
                        if (itemType == "Compile")
                        {
                            return (SourceAdditionImpact.Membership, null);
                        }

                        impact = SourceAdditionImpact.Configuration;
                    }

                    if (itemType == "Compile" && IsOrdinarySource(file))
                    {
                        sources.Add(file);
                    }
                }
            }

            // An import can also acquire or drop a previously published source at the same time.
            return sources.SetEquals(instance.Documents.Select(document => document.FilePath).OfType<string>().Where(IsOrdinarySource))
                ? (impact, null)
                : (SourceAdditionImpact.Membership, null);
        }

        // Compare names only. No file is opened or admitted by this comparison. A filtered
        // repository source missing from the live project will conservatively select its owner.
        bool IsOrdinarySource(string file) => IsPathWithinRoot(file, request.RepositoryPath)
            && !SemanticRefreshPathPolicy.IsIgnoredPath(request.RepositoryPath, file)
            && !SemanticRefreshPathPolicy.IsIgnoredGeneratedDocument(request.RepositoryPath, file)
            && !CodeExploreGeneratedSourceClassifier.IsGeneratedPath(file);
    }

    private static SourceDependencies ReadSourceDependencies(ProjectRootElement project, string projectDirectory)
    {
        // Evaluation cannot tell what files custom design-time targets or tasks read.
        var opaque = project.Targets.Count != 0 || project.UsingTasks.Count != 0;
        var declaration = System.Xml.Linq.XElement.Parse(project.OuterElement);
        var declaringDirectory = Path.GetDirectoryName(project.FullPath) ?? projectDirectory;
        var paths = ImmutableArray.CreateBuilder<string>();
        foreach (var element in declaration.DescendantsAndSelf())
        {
            // Property functions and item transforms can turn filesystem state into settings.
            if ((!element.HasElements && HasOpaqueExpression(element.Value))
                || element.Attributes().Any(attribute => HasOpaqueExpression(attribute.Value)))
            {
                opaque = true;
            }

            for (var remaining = ((string?)element.Attribute("Condition")).AsSpan(); !remaining.IsEmpty;)
            {
                var exists = remaining.IndexOf("Exists", StringComparison.OrdinalIgnoreCase);
                if (exists < 0)
                {
                    break;
                }

                remaining = remaining[(exists + "Exists".Length)..].TrimStart();
                if (remaining.IsEmpty || remaining[0] != '(')
                {
                    opaque = true;
                    break;
                }

                remaining = remaining[1..].TrimStart();
                if (remaining.IsEmpty || remaining[0] is not ('\'' or '"'))
                {
                    opaque = true;
                    break;
                }

                var end = remaining[1..].IndexOf(remaining[0]);
                if (end < 0)
                {
                    opaque = true;
                    break;
                }

                var path = ExpandScopePath(remaining.Slice(1, end).ToString(), projectDirectory, declaringDirectory);
                remaining = remaining[(end + 2)..].TrimStart();
                if (remaining.IsEmpty || remaining[0] != ')' || string.IsNullOrWhiteSpace(path)
                    || path.AsSpan().ContainsAny(_unresolvedImportCharacters) || path.Contains('(') || path.Contains(')'))
                {
                    opaque = true;
                    break;
                }

                remaining = remaining[1..];

                // Import conditions can use the declaring file's directory rather than the project's.
                paths.Add(Path.GetFullPath(path, projectDirectory));
                if (!declaringDirectory.Equals(projectDirectory, PathComparison))
                {
                    paths.Add(Path.GetFullPath(path, declaringDirectory));
                }
            }
        }

        return new(paths.ToImmutable(), opaque);

        static bool HasOpaqueExpression(string expression)
        {
            if (expression.Contains("@(", StringComparison.Ordinal))
            {
                return true;
            }

            for (var remaining = expression.AsSpan(); !remaining.IsEmpty;)
            {
                var start = remaining.IndexOf("$(", StringComparison.Ordinal);
                if (start < 0)
                {
                    break;
                }

                remaining = remaining[(start + 2)..];
                var end = remaining.IndexOf(')');
                if (end < 0 || remaining[..end].IndexOfAny('[', '(') >= 0)
                {
                    return true;
                }

                remaining = remaining[(end + 1)..];
            }

            return false;
        }
    }

    private static bool TouchesAddition(string candidate, IEnumerable<string> addedPaths)
    {
        // Select conservatively: a case-insensitive volume can also be mounted on Unix.
        var prefix = Path.EndsInDirectorySeparator(candidate) ? candidate : candidate + Path.DirectorySeparatorChar;
        return addedPaths.Any(path => string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsStockBuildDeclaration(ProjectRootElement project, string toolsPath)
    {
        if (toolsPath.Length == 0)
        {
            return false;
        }

        if (IsPathWithinRoot(project.FullPath, toolsPath))
        {
            // Keep arbitrary user/machine hooks opaque, except for known files shipped by the SDK.
            var commonImportAfter = Path.Combine(toolsPath, "Current", "Microsoft.Common.targets", "ImportAfter");
            var crossTargetingImportAfter = Path.Combine(toolsPath, "Current", "Microsoft.Common.CrossTargeting.targets", "ImportAfter");
            var relativeParts = Path.GetRelativePath(toolsPath, project.FullPath).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return (string.Equals(Path.GetDirectoryName(project.FullPath), commonImportAfter, PathComparison)
                    && Path.GetFileName(project.FullPath) is "Microsoft.NET.Build.Extensions.targets" or "Microsoft.TestPlatform.ImportAfter.targets")
                || string.Equals(project.FullPath, Path.Combine(crossTargetingImportAfter, "Microsoft.TestPlatform.CrossTargeting.targets"), PathComparison)
                || !relativeParts.Any(part => part.Equals("ImportBefore", StringComparison.OrdinalIgnoreCase) || part.Equals("ImportAfter", StringComparison.OrdinalIgnoreCase));
        }

        // The workload resolver can import installed SDK pack declarations even for portable projects.
        var sdkDirectory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(toolsPath));
        if (!string.Equals(Path.GetFileName(sdkDirectory), "sdk", PathComparison)
            || Path.GetDirectoryName(sdkDirectory) is not { } dotnetDirectory)
        {
            return false;
        }

        return IsPathWithinRoot(project.FullPath, Path.Combine(dotnetDirectory, "packs"))
            || IsPathWithinRoot(project.FullPath, Path.Combine(dotnetDirectory, "sdk-manifests"));
    }

    private enum SourceAdditionImpact
    {
        None,
        Configuration,
        Membership,
        Unknown,
    }

    private sealed record SourceDependencies(ImmutableArray<string> ExistsPaths, bool Opaque);

    private sealed record ProjectSourceOwners(IReadOnlyList<Project> Projects, bool CanLoadInParallel, string? FallbackReason = null);
}
