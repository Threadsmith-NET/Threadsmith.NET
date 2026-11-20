namespace Threadsmith.DotNet;

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Globbing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <content>Project-scoped source membership refresh.</content>
public sealed partial class SemanticEngine
{
    private static readonly FrozenSet<string> _compilerItems = new[] { "Compile", "AdditionalFiles", "EditorConfigFiles", "GlobalAnalyzerConfigFiles" }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly char[] _dynamicPathCharacters = ['$', '@', '%'];

    // Only known stock-SDK declarations can prove that an unselected project is not an owner.
    // An unfamiliar property can redirect an implicit import, even with no local <Import>.
    private static readonly FrozenSet<string> _scopeProperties = FrozenSet.ToFrozenSet<string>(
        [
            "TargetFramework", "TargetFrameworks", "LangVersion", "Nullable", "ImplicitUsings", "UseSharedCompilation",
            "AssemblyName", "RootNamespace", "OutputType", "DefineConstants", "AllowUnsafeBlocks", "Optimize",
            "Version", "AssemblyVersion", "FileVersion", "InformationalVersion", "GenerateAssemblyInfo",
            "GenerateDocumentationFile", "NoWarn", "TreatWarningsAsErrors", "WarningsAsErrors", "WarningsNotAsErrors",
            "EnableDefaultItems", "EnableDefaultCompileItems", "DefaultItemExcludes", "DefaultItemExcludesInProjectFolder",
            "ManagePackageVersionsCentrally", "ProjectAssetsFile", "NuGetPackageRoot", "NuGetPackageFolders",
            "NuGetProjectStyle", "NuGetToolVersion", "RestoreSuccess",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] _buildEnvironmentOverrides =
    [
        "DirectoryBuildPropsPath", "DirectoryBuildTargetsPath", "DirectoryPackagesPropsPath",
        "MSBuildProjectExtensionsPath", "BaseIntermediateOutputPath", "UseArtifactsOutput", "ArtifactsPath",
        "MSBuildUserExtensionsPath", "AlternateCommonTargets",
        "UseWPF", "UseWindowsForms",
    ];

    private readonly ConditionalWeakTable<MSBuildWorkspace, ProjectScopeIndex> _projectInputScopes = new();

    /// <summary>Verifies new compile items against the loaded projects before incremental publication.</summary>
    internal async Task<SemanticLoadResult?> TryRefreshProjectsAsync(
        IReadOnlyList<SemanticDocumentRefresh> documents,
        CancellationToken cancellationToken)
    {
        MSBuildWorkspace workspace;
        Solution solution;
        SemanticLoadRequest request;
        SemanticRefreshInventory inventory;
        lock (_gate)
        {
            if (_workspace is null || _solution is null || _lastRequest is not { TrustLevel: >= RepositoryTrustLevel.TrustedBuild }
                || _projects.Count != _solution.ProjectIds.Count)
            {
                return null;
            }

            workspace = _workspace;
            solution = _solution;
            request = _lastRequest;
            inventory = _refreshInventory;
        }

        var comparer = StringComparerForCurrentPlatform();
        var added = documents.Where(document => !inventory.SourceDocuments.Contains(document.Path))
            .ToDictionary(document => document.Path, comparer);
        if (added.Count == 0 || added.Keys.Any(path => !IsSemanticRefreshInputPathAllowed(path, request)))
        {
            return null;
        }

        var replacement = await TryResolveProjectDocumentsAsync(workspace, solution, request, added, cancellationToken);
        if (replacement is null)
        {
            return null;
        }

        var addition = new ProjectSourceAddition(
            solution,
            replacement,
            inventory.SourceDocuments.Concat(added.Keys).ToFrozenSet(comparer));
        var textUpdates = documents.Where(document => inventory.SourceDocuments.Contains(document.Path)).ToArray();
        return await PublishDocumentsAsync(textUpdates, addition, cancellationToken);
    }

    // The CLR can resolve the catch filter's Microsoft.Build type before the method body runs.
    // Do not enter this boundary until a trusted workspace has registered the MSBuild locator.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<Solution?> TryResolveProjectDocumentsAsync(
        MSBuildWorkspace workspace,
        Solution solution,
        SemanticLoadRequest request,
        Dictionary<string, SemanticDocumentRefresh> added,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunNonCooperativeAsync(
                token => ResolveProjectDocumentsAsync(workspace, solution, request, added, token),
                sourceSolution: solution,
                cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidProjectFileException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or XmlException)
        {
            _logger.LogDebug(exception, "Project membership evaluation requires a full semantic refresh.");
            return null;
        }
    }

    private static ProjectScopeIndex CaptureProjectScopes(
        Solution solution, SemanticLoadRequest request, CancellationToken cancellationToken)
    {
        var comparer = StringComparerForCurrentPlatform();
        var inputs = new Dictionary<string, ScopeInput>(comparer);
        var scopes = new List<ProjectInputScope>();
        foreach (var path in solution.Projects.Select(project => project.FilePath).Distinct(comparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (path is null || !IsSemanticInputPathAllowed(path, request) || Path.GetDirectoryName(path) is not { } directory)
            {
                throw new InvalidOperationException("The project scope could not be confined to the repository.");
            }

            var globs = new List<IMSBuildGlob>();
            var importsVerified = TryCaptureScope(path, directory, request, inputs, globs, cancellationToken);
            scopes.Add(new(path, directory, importsVerified, [.. globs]));
        }

        return new([.. scopes], inputs.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.Identity, comparer));
    }

    private static bool TryCaptureScope(
        string projectPath,
        string projectDirectory,
        SemanticLoadRequest request,
        Dictionary<string, ScopeInput> inputs,
        List<IMSBuildGlob> globs,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(inputs.Comparer);
        pending.Push(projectPath);
        foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
        {
            for (var directory = projectDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
            {
                var candidate = Path.Combine(directory, name);
                if (CaptureScopeInput(candidate, request, inputs).Identity is not null)
                {
                    pending.Push(candidate);
                    break;
                }
            }
        }

        foreach (var suffix in new[] { ".nuget.g.props", ".nuget.g.targets" })
        {
            var candidate = Path.Combine(projectDirectory, "obj", Path.GetFileName(projectPath) + suffix);

            // Restore can materialize these files in an unwatched directory after the index is built.
            if (CaptureScopeInput(candidate, request, inputs).Identity is not null)
            {
                pending.Push(candidate);
            }
        }

        while (pending.TryPop(out var path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(path))
            {
                continue;
            }

            if (!IsPathWithinRoot(path, request.RepositoryPath))
            {
                return false;
            }

            var elements = CaptureScopeInput(path, request, inputs).Elements;
            if (elements.Length == 0)
            {
                return false;
            }

            // Only the stock SDK's implicit, project-directory source scope is assumed here.
            // Read all declared conditions (including TFM-specific ones) as possible inputs.
            var sdk = (string?)elements[0].Attribute("Sdk");
            if (inputs.Comparer.Equals(path, projectPath) ? sdk != "Microsoft.NET.Sdk" : sdk is not null)
            {
                return false;
            }

            var declaringDirectory = Path.GetDirectoryName(path) ?? projectDirectory;
            foreach (var element in elements)
            {
                var name = element.Name.LocalName;

                // A file-existence condition can change more than the membership of that file.
                if (name == "Sdk"
                    || (element.Parent?.Name.LocalName == "PropertyGroup" && !_scopeProperties.Contains(name))
                    || ((string?)element.Attribute("Condition") is { } condition && condition.Contains("Exists", StringComparison.OrdinalIgnoreCase))
                    || (name is "Output" or "ProjectReference"
                        && HasUnknownCompilerOutput((string?)element.Attribute("ItemName") ?? (string?)element.Attribute("OutputItemType")))
                    || (name == "OutputItemType" && HasUnknownCompilerOutput(element.Value)))
                {
                    return false;
                }

                if (name == "Import")
                {
                    var import = ExpandScopePath((string?)element.Attribute("Project") ?? string.Empty, projectDirectory, declaringDirectory);
                    if (element.Attribute("Sdk") is not null || string.IsNullOrWhiteSpace(import)
                        || import.IndexOfAny(_dynamicPathCharacters) >= 0 || import.Contains('*') || import.Contains('?') || import.Contains(';'))
                    {
                        return false;
                    }

                    pending.Push(Path.GetFullPath(import, declaringDirectory));
                }

                if (!_compilerItems.Contains(name) || (string?)element.Attribute("Include") is not { } include)
                {
                    continue;
                }

                var paths = ExpandScopePath(include, projectDirectory, declaringDirectory);
                if (paths.IndexOfAny(_dynamicPathCharacters) >= 0)
                {
                    return false;
                }

                // Item paths are relative to the main project, unlike import paths.
                // Excludes/conditions can only narrow these candidates and are verified on reload.
                foreach (var pattern in paths.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var glob = MSBuildGlob.Parse(projectDirectory, pattern);
                    if (!glob.IsLegal)
                    {
                        return false;
                    }

                    globs.Add(glob);
                }
            }
        }

        return true;
    }

    private static ScopeInput CaptureScopeInput(string path, SemanticLoadRequest request, Dictionary<string, ScopeInput> inputs)
    {
        if (!inputs.TryGetValue(path, out var input))
        {
            using var stream = OpenScopeInput(path, request);
            if (stream is null)
            {
                input = new(null, []);
            }
            else
            {
                // Derive the declaration and its identity from exactly the same bytes.
                using var content = new MemoryStream();
                stream.CopyTo(content);
                var identity = Convert.ToHexString(SHA256.HashData(content.GetBuffer().AsSpan(0, (int)content.Length)));
                content.Position = 0;
                using var reader = XmlReader.Create(content);
                input = new(identity, [.. XElement.Load(reader, LoadOptions.None).DescendantsAndSelf()]);
            }

            inputs.Add(path, input);
        }

        return input;
    }

    private static FileStream? OpenScopeInput(string path, SemanticLoadRequest request)
    {
        var insideRepository = IsPathWithinRoot(path, request.RepositoryPath);
        if (insideRepository && !IsSemanticInputPathAllowed(path, request))
        {
            throw new InvalidOperationException("A project scope input is prohibited by repository path policy.");
        }

        try
        {
            // An absent ancestor candidate matters even above the repository. Never read it if it appears.
            _ = File.GetAttributes(path);
        }
        catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        if (!insideRepository)
        {
            throw new InvalidOperationException("A project scope input is outside the repository.");
        }

        return File.OpenRead(path);
    }

    private static bool ScopeInputsMatch(ProjectScopeIndex index, SemanticLoadRequest request, CancellationToken cancellationToken)
    {
        var toolset = _registeredMsBuildInstance?.MSBuildPath;
        if (_buildEnvironmentOverrides.Any(name => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name)))
            || HasRedirectedToolsetPath("MSBuildExtensionsPath", toolset)
            || HasRedirectedToolsetPath("MSBuildSDKsPath", toolset is null ? null : Path.Combine(toolset, "Sdks"))
            || Environment.GetEnvironmentVariables().Keys.OfType<string>().Any(name => name.StartsWith("Custom", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        foreach (var (path, expected) in index.InputIdentities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = OpenScopeInput(path, request);
            var actual = stream is null ? null : Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                return false;
            }
        }

        foreach (var scope in index.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (HasUnindexedImplicitImports(scope, request))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasRedirectedToolsetPath(string name, string? expected)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return !string.IsNullOrEmpty(value)
            && (expected is null || !string.Equals(Path.TrimEndingDirectorySeparator(value), Path.TrimEndingDirectorySeparator(expected), PathComparison));
    }

    private static bool HasUnindexedImplicitImports(ProjectInputScope scope, SemanticLoadRequest request)
    {
        try
        {
            _ = File.GetAttributes(scope.ProjectPath + ".user");
            return true;
        }
        catch (FileNotFoundException)
        {
            // No per-project user import.
        }

        var extensionsDirectory = Path.Combine(scope.Directory, "obj");
        if (!IsSemanticInputPathAllowed(extensionsDirectory, request))
        {
            return true;
        }

        var projectName = Path.GetFileName(scope.ProjectPath);
        try
        {
            foreach (var path in Directory.EnumerateFiles(extensionsDirectory, projectName + ".*"))
            {
                var name = Path.GetFileName(path);
                var extension = Path.GetExtension(name);
                if ((extension.Equals(".props", StringComparison.OrdinalIgnoreCase) || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase))
                    && !name.Equals(projectName + ".nuget.g.props", PathComparison)
                    && !name.Equals(projectName + ".nuget.g.targets", PathComparison))
                {
                    return true;
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // The default project extensions directory has not been created.
        }

        return false;
    }

    private static string ExpandScopePath(string path, string projectDirectory, string declaringDirectory) => path
        .Replace("$(MSBuildProjectDirectory)", projectDirectory, StringComparison.OrdinalIgnoreCase)
        .Replace("$(MSBuildThisFileDirectory)", declaringDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        .Replace('\\', Path.DirectorySeparatorChar);

    private static bool HasUnknownCompilerOutput(string? name) => name is not null
        && (_compilerItems.Contains(name.Trim()) || name.IndexOfAny(_dynamicPathCharacters) >= 0);

    private async Task<Solution?> ResolveProjectDocumentsAsync(
        MSBuildWorkspace sourceWorkspace,
        Solution solution,
        SemanticLoadRequest request,
        Dictionary<string, SemanticDocumentRefresh> added,
        CancellationToken cancellationToken)
    {
        var comparer = StringComparerForCurrentPlatform();
        var scopeIndex = _projectInputScopes.GetValue(sourceWorkspace, _ => CaptureProjectScopes(solution, request, cancellationToken));
        if (!ScopeInputsMatch(scopeIndex, request, cancellationToken))
        {
            return null;
        }

        var paths = new HashSet<string>(comparer);
        foreach (var scope in scopeIndex.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (added.Keys.Any(path => IsPathWithinRoot(path, scope.Directory) || scope.Globs.Any(glob => glob.IsMatch(path))))
            {
                paths.Add(scope.ProjectPath);
            }
            else if (!scope.ImportsVerified)
            {
                // Full refresh already handles arbitrary import chains; don't guess here or
                // start a second design-time loader for every project with unverified imports.
                return null;
            }
        }

        var projectsByPath = solution.Projects.ToLookup(project => project.FilePath, comparer);
        var owners = paths.SelectMany(path => projectsByPath[path]).ToArray();
        if (owners.Length == 0 || owners.Length != paths.Count || owners.Any(project => project.Language != LanguageNames.CSharp))
        {
            return null;
        }

        var generatedInputs = await CaptureGeneratedInputsAsync(owners, request, cancellationToken);
        if (generatedInputs is null)
        {
            return null;
        }

        var properties = sourceWorkspace.Properties;
        if (!IsDirectProjectSelection(request.SolutionPath))
        {
            properties = properties.SetItem("SolutionDir", Path.GetDirectoryName(request.SolutionPath) + Path.DirectorySeparatorChar);
        }

        var evaluations = new ProjectInfo?[owners.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, owners.Length),
            new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = _resourceLimits.CompilationWorkers },
            async (index, token) => evaluations[index] = await LoadProjectConfigurationAsync(owners[index], projectsByPath, properties, token));
        if (!await GeneratedInputsMatchAsync(generatedInputs, request, cancellationToken)
            || !ScopeInputsMatch(scopeIndex, request, cancellationToken))
        {
            return null;
        }

        var replacement = solution;
        for (var index = 0; index < owners.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = owners[index];
            var info = evaluations[index];
            if (info is null || !HasSameProjectConfiguration(owner, info, request))
            {
                return null;
            }

            var priorDocuments = owner.Documents.Where(document => document.FilePath is not null)
                .ToLookup(document => document.FilePath, comparer);
            var order = ImmutableList.CreateBuilder<DocumentId>();
            var addedCount = 0;
            foreach (var document in info.Documents)
            {
                var path = document.FilePath;
                var existing = priorDocuments[path];
                if (existing.Any())
                {
                    if (existing.Count() != 1)
                    {
                        return null;
                    }

                    order.Add(existing.First().Id);
                    continue;
                }

                if (path is null || !IsSemanticInputPathAllowed(path, request))
                {
                    continue;
                }

                if (!added.TryGetValue(path, out var change))
                {
                    return null;
                }

                var id = DocumentId.CreateNewId(owner.Id);
                order.Add(id);
                addedCount++;
                replacement = replacement.AddDocument(document.WithId(id).WithTextLoader(
                    TextLoader.From(TextAndVersion.Create(SourceText.From(change.Text, System.Text.Encoding.UTF8), VersionStamp.Create(), path))));
            }

            if (order.Count != owner.DocumentIds.Count + addedCount || order.Distinct().Count() != order.Count)
            {
                return null;
            }

            if (addedCount > 0)
            {
                replacement = replacement.WithProjectDocumentsOrder(owner.Id, order.ToImmutable());
            }
        }

        return added.Keys.All(path => replacement.GetDocumentIdsWithFilePath(path).Length > 0) ? replacement : null;
    }

    private static async Task<List<(string Path, SourceText Text)>?> CaptureGeneratedInputsAsync(
        IEnumerable<Project> owners,
        SemanticLoadRequest request,
        CancellationToken cancellationToken)
    {
        // Snapshot the old Roslyn text before any design-time load can overwrite a file.
        // Existing ordinary source comes from the stable refresh batch, not from this check.
        var inputs = new List<(string Path, SourceText Text)>();
        foreach (var owner in owners)
        {
            var documents = owner.Documents.Cast<TextDocument>().Concat(owner.AdditionalDocuments).Concat(owner.AnalyzerConfigDocuments);
            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (document.FilePath is not { } path
                    || (!SemanticRefreshPathPolicy.IsIgnoredPath(request.RepositoryPath, path)
                        && !SemanticRefreshPathPolicy.IsIgnoredGeneratedDocument(request.RepositoryPath, path)
                        && !(document is Document && CodeExploreGeneratedSourceClassifier.IsGeneratedPath(path))))
                {
                    continue;
                }

                if (!IsSemanticInputPathAllowed(path, request))
                {
                    return null;
                }

                inputs.Add((path, await document.GetTextAsync(cancellationToken)));
            }
        }

        return inputs;
    }

    private static async Task<bool> GeneratedInputsMatchAsync(
        List<(string Path, SourceText Text)> inputs,
        SemanticLoadRequest request,
        CancellationToken cancellationToken)
    {
        var current = new Dictionary<string, SourceText>(StringComparerForCurrentPlatform());
        foreach (var (path, prior) in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!current.TryGetValue(path, out var text))
            {
                if (!IsSemanticInputPathAllowed(path, request))
                {
                    return false;
                }

                var loader = new FileTextLoader(path, prior.Encoding);
                var loaded = await loader.LoadTextAndVersionAsync(new LoadTextOptions(prior.ChecksumAlgorithm), cancellationToken);
                text = loaded.Text;
                current.Add(path, text);
            }

            if (!text.ContentEquals(prior))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<ProjectInfo?> LoadProjectConfigurationAsync(
        Project owner,
        ILookup<string?, Project> projectsByPath,
        ImmutableDictionary<string, string> properties,
        CancellationToken cancellationToken)
    {
        var workspace = MSBuildWorkspace.Create(properties, RoslynWorkspaceHost.Services);
        using var lease = new WorkspaceLease(workspace, DisposeCompilerWorkspace);
        var failed = 0;
        workspace.RegisterWorkspaceFailedHandler(args =>
        {
            if (args.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
            {
                Interlocked.Exchange(ref failed, 1);
            }
        });
        var loader = new MSBuildProjectLoader(workspace, properties) { LoadMetadataForReferencedProjects = true };
        var map = ProjectMap.Create();

        // Preserve Roslyn's choice among target frameworks for each existing reference without
        // walking the unrelated projects again for every owner. A changed reference falls back.
        var referencePaths = owner.ProjectReferences
            .Select(reference => owner.Solution.GetProject(reference.ProjectId)?.FilePath)
            .OfType<string>()
            .Distinct(StringComparerForCurrentPlatform());
        foreach (var project in referencePaths.SelectMany(path => projectsByPath[path]))
        {
            map.Add(project);
        }

        var infos = await loader.LoadProjectInfoAsync(owner.FilePath ?? throw new InvalidOperationException(), map, cancellationToken: cancellationToken);
        return infos.Length == 1 && Volatile.Read(ref failed) == 0 ? infos[0] : null;
    }

    private static bool HasSameProjectConfiguration(Microsoft.CodeAnalysis.Project project, ProjectInfo info, SemanticLoadRequest request)
    {
        var comparer = StringComparerForCurrentPlatform();
        var options = project.CompilationOptions;
        if (options is null || info.CompilationOptions is not { } evaluated
            || !evaluated.SpecificDiagnosticOptions.ToHashSet().SetEquals(options.SpecificDiagnosticOptions))
        {
            return false;
        }

        // These resolvers belong to the individual Roslyn workspace. Compare the evaluated
        // references and analyzer configuration separately before retaining the live services.
        var comparable = evaluated.WithSyntaxTreeOptionsProvider(options.SyntaxTreeOptionsProvider)
            .WithMetadataReferenceResolver(options.MetadataReferenceResolver)
            .WithSpecificDiagnosticOptions(options.SpecificDiagnosticOptions);
        return Equals(comparable, options)
            && comparer.Equals(info.OutputFilePath, project.OutputFilePath)
            && Equals(info.ParseOptions, project.ParseOptions)
            && string.Equals(info.AssemblyName, project.AssemblyName, StringComparison.Ordinal)
            && info.MetadataReferences.Select(reference => (reference.Display, reference.Properties)).SequenceEqual(
                project.MetadataReferences.Select(reference => (reference.Display, reference.Properties)))
            && info.AnalyzerReferences.Select(reference => reference.FullPath).SequenceEqual(
                project.AnalyzerReferences.Select(reference => reference.FullPath), comparer)
            && project.ProjectReferences.ToHashSet().SetEquals(info.ProjectReferences)
            && SameDocumentPaths(info.AdditionalDocuments, project.AdditionalDocuments)
            && SameDocumentPaths(info.AnalyzerConfigDocuments, project.AnalyzerConfigDocuments);

        bool SameDocumentPaths(IEnumerable<DocumentInfo> reloaded, IEnumerable<TextDocument> loaded)
        {
            var known = loaded.Select(document => document.FilePath).ToHashSet(comparer);
            return reloaded.All(document => known.Remove(document.FilePath) || IsRepositoryLocalSemanticInputRejected(document.FilePath, request))
                && known.Count == 0;
        }
    }

    private sealed record ScopeInput(string? Identity, XElement[] Elements);

    private sealed record ProjectScopeIndex(ProjectInputScope[] Projects, FrozenDictionary<string, string?> InputIdentities);

    private sealed record ProjectInputScope(string ProjectPath, string Directory, bool ImportsVerified, IMSBuildGlob[] Globs);

    private sealed record ProjectSourceAddition(Solution Base, Solution Replacement, IReadOnlySet<string> SourcePaths);
}
