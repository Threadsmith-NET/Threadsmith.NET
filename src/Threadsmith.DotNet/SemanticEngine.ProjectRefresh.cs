namespace Threadsmith.DotNet;

using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Build.Exceptions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <content>Project-scoped source membership refresh.</content>
public sealed partial class SemanticEngine
{
    private static readonly SearchValues<char> _unresolvedImportCharacters = SearchValues.Create("$@%*?;");

    private readonly ConditionalWeakTable<ProjectId, FrozenDictionary<string, string?>> _projectInputScopes = [];

    /// <summary>Refreshes the projects affected by new compile items in the selected workspace.</summary>
    internal async Task<SemanticLoadResult?> TryRefreshProjectsAsync(
        IReadOnlyList<SemanticDocumentRefresh> documents,
        CancellationToken cancellationToken)
    {
        MSBuildWorkspace workspace;
        Solution solution;
        SemanticLoadRequest request;
        SemanticRefreshInventory inventory;
        long ownership;
        CancellationToken ownerToken;
        lock (_gate)
        {
            if (_workspace is null || _solution is null || _lastRequest is not { TrustLevel: >= RepositoryTrustLevel.TrustedBuild }
                || _projects.Count != _solution.ProjectIds.Count)
            {
                return null;
            }

            ownership = _preparationOwnership;
            ownerToken = _preparationOwnerLifetime.Token;
            workspace = _workspace;
            solution = _solution;
            request = _lastRequest;
            inventory = _refreshInventory;
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _engineLifetime.Token, ownerToken);
        var comparer = StringComparerForCurrentPlatform();
        var added = documents.Where(document => !inventory.SourceDocuments.Contains(document.Path))
            .ToDictionary(document => document.Path, comparer);
        if (added.Count == 0 || added.Keys.Any(path => !IsSemanticRefreshInputPathAllowed(path, request)))
        {
            return null;
        }

        var replacement = await TryResolveProjectDocumentsAsync(workspace, solution, request, added, lifetime.Token);
        if (replacement is null)
        {
            return null;
        }

        var addition = new ProjectSourceAddition(
            ownership,
            solution,
            replacement,
            inventory.SourceDocuments.Concat(added.Keys).ToFrozenSet(comparer));
        var textUpdates = documents.Where(document => inventory.SourceDocuments.Contains(document.Path)).ToArray();
        return await PublishDocumentsAsync(textUpdates, addition, lifetime.Token);
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

    private FrozenDictionary<string, string?> CaptureProjectInputs(
        string projectPath, string projectDirectory, SemanticLoadRequest request, CancellationToken cancellationToken)
    {
        // This checks known local declarations, not the SDK's full import chain.
        var comparer = StringComparerForCurrentPlatform();
        var inputs = new Dictionary<string, ScopeInput>(comparer);
        var pending = new Stack<string>();
        var visited = new HashSet<string>(comparer);
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

            if (CaptureScopeInput(path, request, inputs).Declaration is not { } declaration)
            {
                continue;
            }

            var declaringDirectory = Path.GetDirectoryName(path) ?? projectDirectory;
            foreach (var element in declaration.Descendants().Where(element => element.Name.LocalName == "Import"))
            {
                var import = ExpandScopePath((string?)element.Attribute("Project") ?? string.Empty, projectDirectory, declaringDirectory);
                if (element.Attribute("Sdk") is not null || string.IsNullOrWhiteSpace(import)
                    || import.AsSpan().ContainsAny(_unresolvedImportCharacters))
                {
                    // MSBuild resolves this import during evaluation. Never use this local
                    // inventory to exclude another project.
                    continue;
                }

                pending.Push(Path.GetFullPath(import, declaringDirectory));
            }
        }

        return inputs.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.Identity, comparer);
    }

    private ScopeInput CaptureScopeInput(string path, SemanticLoadRequest request, Dictionary<string, ScopeInput> inputs)
    {
        if (!inputs.TryGetValue(path, out var input))
        {
            input = ReadScopeInput(path, request, captureDeclaration: true);
            inputs.Add(path, input);
        }

        return input;
    }

    private ScopeInput ReadScopeInput(string path, SemanticLoadRequest request, bool captureDeclaration)
    {
        using var stream = OpenScopeInput(path, request);
        if (stream is null)
        {
            return default;
        }

        var length = stream.Length;
        if (length > _resourceLimits.MaximumInventoryXmlBytes || length > Array.MaxLength)
        {
            throw new InvalidOperationException("A project scope input exceeds the XML inventory limit.");
        }

        var count = (int)length;
        var buffer = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            var bytes = buffer.AsSpan(0, count);
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                throw new IOException("A project scope input grew during the read.");
            }

            // Derive the declaration and its identity from the same bounded read.
            var identity = Convert.ToHexString(SHA256.HashData(bytes));
            if (!captureDeclaration)
            {
                return new(identity, null);
            }

            using var content = new MemoryStream(buffer, 0, count, writable: false);
            using var reader = XmlReader.Create(content);
            return new(identity, XElement.Load(reader, LoadOptions.None));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
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

    private bool ScopeInputsMatch(
        FrozenDictionary<string, string?> inputs, SemanticLoadRequest request, CancellationToken cancellationToken)
    {
        foreach (var (path, expected) in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actual = ReadScopeInput(path, request, captureDeclaration: false).Identity;
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasUnindexedLocalImports(string projectPath, string projectDirectory, SemanticLoadRequest request)
    {
        try
        {
            _ = File.GetAttributes(projectPath + ".user");
            return true;
        }
        catch (FileNotFoundException)
        {
            // No per-project user import.
        }

        var extensionsDirectory = Path.Combine(projectDirectory, "obj");
        if (!IsSemanticInputPathAllowed(extensionsDirectory, request))
        {
            return true;
        }

        var projectName = Path.GetFileName(projectPath);
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

    private async Task<Solution?> ResolveProjectDocumentsAsync(
        MSBuildWorkspace sourceWorkspace,
        Solution solution,
        SemanticLoadRequest request,
        Dictionary<string, SemanticDocumentRefresh> added,
        CancellationToken cancellationToken)
    {
        var comparer = StringComparerForCurrentPlatform();
        var projectsByPath = solution.Projects.ToLookup(project => project.FilePath, comparer);
        var properties = sourceWorkspace.Properties;
        var selection = FindSourceRefreshOwners(
            projectsByPath, properties, request, added, _resourceLimits.CompilationWorkers > 1, cancellationToken);
        if (selection is null)
        {
            return null;
        }

        var owners = selection.Projects;

        // An unchanged Compile set does not prove unchanged compiler settings. For example,
        // another project's constants or references may depend on the existence of an added file.
        var ownerIds = owners.Select(project => project.Id).ToHashSet();
        var excluded = solution.Projects.Where(project => !ownerIds.Contains(project.Id));
        var checks = new List<ProjectRefreshCheck>();
        foreach (var project in excluded.Concat(owners))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (project.FilePath is not { } projectPath || Path.GetDirectoryName(projectPath) is not { } directory)
            {
                return null;
            }

            var inputs = _projectInputScopes.GetValue(project.Id, _ => CaptureProjectInputs(projectPath, directory, request, cancellationToken));
            if (!LocalInputsMatch(inputs, projectPath, directory))
            {
                return null;
            }

            // Snapshot all existing generated text before any design-time load can replace it.
            var generated = await CaptureGeneratedInputsAsync(project, request, cancellationToken);
            if (generated is null)
            {
                return null;
            }

            checks.Add(new(project, projectPath, directory, inputs, generated));
        }

        var replacement = solution;
        var matched = new HashSet<string>(comparer);
        var firstOwner = checks.Count - owners.Count;
        ProjectInfo?[]? ownerConfigurations = null;
        for (var index = 0; index < checks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index == firstOwner && selection.CanLoadInParallel)
            {
                // Check non-owners first so a mismatch falls back before loading any owners.
                // Collect by index so completion order cannot affect validation or publication.
                var configurations = new ProjectInfo?[owners.Count];
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, owners.Count),
                    new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = _resourceLimits.CompilationWorkers },
                    async (ownerIndex, token) => configurations[ownerIndex] = await LoadProjectConfigurationAsync(owners[ownerIndex], projectsByPath, properties, token));
                ownerConfigurations = configurations;
            }

            var project = checks[index].Project;
            var info = ownerConfigurations is null
                ? await LoadProjectConfigurationAsync(project, projectsByPath, properties, cancellationToken)
                : ownerConfigurations[index - firstOwner];
            if (info is null || !HasSameProjectConfiguration(project, info, request))
            {
                return null;
            }

            var updated = ApplyProjectMembership(replacement, project, info, request, added, matched, index >= firstOwner, cancellationToken);
            if (updated is null)
            {
                return null;
            }

            replacement = updated;
        }

        foreach (var check in checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await GeneratedInputsMatchAsync(check.Generated, request, cancellationToken)
                || !LocalInputsMatch(check.Inputs, check.ProjectPath, check.Directory))
            {
                return null;
            }
        }

        return matched.Count == added.Count ? replacement : null;

        bool LocalInputsMatch(FrozenDictionary<string, string?> inputs, string projectPath, string directory) =>
            ScopeInputsMatch(inputs, request, cancellationToken) && !HasUnindexedLocalImports(projectPath, directory, request);
    }

    private static Solution? ApplyProjectMembership(
        Solution replacement,
        Project owner,
        ProjectInfo info,
        SemanticLoadRequest request,
        Dictionary<string, SemanticDocumentRefresh> added,
        HashSet<string> matched,
        bool allowAdditions,
        CancellationToken cancellationToken)
    {
        var priorDocuments = owner.Documents.Where(document => document.FilePath is not null)
            .ToLookup(document => document.FilePath, StringComparerForCurrentPlatform());
        var order = ImmutableList.CreateBuilder<DocumentId>();
        var newInProject = new HashSet<string>(StringComparerForCurrentPlatform());
        foreach (var document in info.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = document.FilePath;
            var existing = priorDocuments[path];
            if (existing.Any())
            {
                var retained = existing.First();
                if (existing.Skip(1).Any() || !HasSameDocumentMetadata(document, retained))
                {
                    return null;
                }

                order.Add(retained.Id);
                continue;
            }

            if (path is null || !IsSemanticInputPathAllowed(path, request))
            {
                continue;
            }

            if (!allowAdditions || !added.TryGetValue(path, out var change) || !newInProject.Add(path))
            {
                return null;
            }

            matched.Add(path);
            var id = DocumentId.CreateNewId(owner.Id);
            order.Add(id);
            replacement = replacement.AddDocument(document.WithId(id).WithTextLoader(
                TextLoader.From(TextAndVersion.Create(SourceText.From(change.Text, System.Text.Encoding.UTF8), VersionStamp.Create(), path))));
        }

        if (order.Count != owner.DocumentIds.Count + newInProject.Count || order.Distinct().Count() != order.Count)
        {
            return null;
        }

        if (!allowAdditions)
        {
            return order.SequenceEqual(owner.DocumentIds) ? replacement : null;
        }

        return replacement.WithProjectDocumentsOrder(owner.Id, order.ToImmutable());
    }

    private static async Task<List<(string Path, SourceText Text)>?> CaptureGeneratedInputsAsync(
        Project owner,
        SemanticLoadRequest request,
        CancellationToken cancellationToken)
    {
        // Snapshot the old Roslyn text before any design-time load can overwrite a file.
        // Existing ordinary source comes from the stable refresh batch, not from this check.
        var inputs = new List<(string Path, SourceText Text)>();
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

        // Reuse the live IDs for referenced projects, including their target-framework variants.
        var map = ProjectMap.Create();
        var referencePaths = owner.ProjectReferences.Select(reference => owner.Solution.GetProject(reference.ProjectId)?.FilePath)
            .OfType<string>().Distinct(StringComparerForCurrentPlatform());
        foreach (var referenced in referencePaths.SelectMany(path => projectsByPath[path]))
        {
            map.Add(referenced);
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
            && SameDocuments(info.AdditionalDocuments, project.AdditionalDocuments)
            && SameDocuments(info.AnalyzerConfigDocuments, project.AnalyzerConfigDocuments);

        bool SameDocuments(IEnumerable<DocumentInfo> reloaded, IEnumerable<TextDocument> loaded)
        {
            var known = loaded.ToLookup(document => document.FilePath, comparer);
            var remaining = known.Select(group => group.Key).ToHashSet(comparer);
            foreach (var document in reloaded)
            {
                if (!remaining.Remove(document.FilePath))
                {
                    if (IsRepositoryLocalSemanticInputRejected(document.FilePath, request))
                    {
                        continue;
                    }

                    return false;
                }

                var matches = known[document.FilePath];
                if (matches.Skip(1).Any() || !HasSameDocumentMetadata(document, matches.First()))
                {
                    return false;
                }
            }

            return remaining.Count == 0;
        }
    }

    private static bool HasSameDocumentMetadata(DocumentInfo evaluated, TextDocument retained)
    {
        return string.Equals(evaluated.Name, retained.Name, StringComparison.Ordinal)
            && evaluated.Folders.SequenceEqual(retained.Folders, StringComparer.Ordinal)
            && (retained is not Document source || evaluated.SourceCodeKind == source.SourceCodeKind);
    }

    private readonly record struct ScopeInput(string? Identity, XElement? Declaration);

    private sealed record ProjectRefreshCheck(
        Project Project,
        string ProjectPath,
        string Directory,
        FrozenDictionary<string, string?> Inputs,
        List<(string Path, SourceText Text)> Generated);

    private sealed record ProjectSourceAddition(long Ownership, Solution Base, Solution Replacement, IReadOnlySet<string> SourcePaths);
}
