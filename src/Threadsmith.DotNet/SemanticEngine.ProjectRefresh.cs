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

        if (await TryResolveProjectDocumentsAsync(workspace, solution, request, added, lifetime.Token) is not { } resolved)
        {
            return null;
        }

        var changedOptions = resolved.Solution.GetChanges(solution).GetProjectChanges()
            .Where(change => !Equals(change.OldProject.ParseOptions, change.NewProject.ParseOptions)
                || !Equals(change.OldProject.CompilationOptions, change.NewProject.CompilationOptions))
            .Select(change => change.ProjectId).ToImmutableArray();
        var addition = new ProjectSourceAddition(
            ownership,
            solution,
            resolved.Solution,
            resolved.Inputs,
            inventory.SourceDocuments.Concat(added.Keys).ToFrozenSet(comparer),
            changedOptions);
        var textUpdates = documents.Where(document => inventory.SourceDocuments.Contains(document.Path)).ToArray();
        return await PublishDocumentsAsync(textUpdates, addition, lifetime.Token);
    }

    // The CLR can resolve the catch filter's Microsoft.Build type before the method body runs.
    // Do not enter this boundary until a trusted workspace has registered the MSBuild locator.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task<ResolvedProjectDocuments?> TryResolveProjectDocumentsAsync(
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

                // The resolved-import baseline verifies external declarations without reopening them.
                var importPath = Path.GetFullPath(import, declaringDirectory);
                if (IsPathWithinRoot(importPath, request.RepositoryPath))
                {
                    pending.Push(importPath);
                }
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

    private static string? ReadRestoreIdentity(string path, SemanticLoadRequest request, CancellationToken cancellationToken)
    {
        using var stream = OpenScopeInput(path, request);
        if (stream is null)
        {
            return null;
        }

        // Assets are not XML inventory. Hash a fixed-length read in small chunks so
        // a large lock file, or one growing concurrently, does not require a large buffer.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var remaining = stream.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (remaining != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(remaining, buffer.Length);
                var chunk = buffer.AsSpan(0, count);
                stream.ReadExactly(chunk);
                hash.AppendData(chunk);
                remaining -= count;
            }

            if (stream.ReadByte() != -1)
            {
                throw new IOException("The NuGet assets file grew during the read.");
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private bool ScopeInputsMatch(
        FrozenDictionary<string, string?> inputs,
        SemanticLoadRequest request,
        CancellationToken cancellationToken,
        Dictionary<string, string?>? identities = null,
        bool restoreAssets = false)
    {
        foreach (var (path, expected) in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (identities?.TryGetValue(path, out var actual) != true)
            {
                actual = restoreAssets ? ReadRestoreIdentity(path, request, cancellationToken) : ReadScopeInput(path, request, captureDeclaration: false).Identity;
                identities?.Add(path, actual);
            }

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

    private async Task<ResolvedProjectDocuments?> ResolveProjectDocumentsAsync(
        MSBuildWorkspace sourceWorkspace,
        Solution solution,
        SemanticLoadRequest request,
        Dictionary<string, SemanticDocumentRefresh> added,
        CancellationToken cancellationToken)
    {
        var comparer = StringComparerForCurrentPlatform();
        var projectsByPath = solution.Projects.ToLookup(project => project.FilePath, comparer);
        var properties = sourceWorkspace.Properties;
        if (!_resolvedImportBaselines.TryGetValue(sourceWorkspace, out var importBaseline)
            || projectsByPath.Any(group => group.Key is null || !importBaseline.Projects.ContainsKey(group.Key)))
        {
            return RequireGraphLoad("Resolved project imports were not verified for the published graph");
        }

        // A changed operand explained by an added source still selects its known owner(s).
        // Other changes may come from an external build in an unwatched directory.
        var existsBeforeLoad = CaptureExistsStates(
            importBaseline.ExistsByProject.Values.SelectMany(project => project.Keys).Distinct(comparer), request, cancellationToken);
        foreach (var (path, operands) in importBaseline.ExistsByProject)
        {
            if (operands.Any(pair => existsBeforeLoad[pair.Key] != pair.Value && !TouchesAddition(pair.Key, added.Keys)))
            {
                return RequireGraphLoad($"Literal MSBuild Exists state changed outside the source batch for '{path}'");
            }
        }

        var untouched = new HashSet<string>(comparer);
        var identities = new Dictionary<string, string?>(comparer);
        if (!RestoreInputsMatch(importBaseline, identities, request, cancellationToken))
        {
            return RequireGraphLoad("NuGet restore inputs changed");
        }

        foreach (var (path, conventional) in importBaseline.Conventional)
        {
            if (!ConventionalInputsMatch(path, conventional, identities, request, cancellationToken))
            {
                return RequireGraphLoad($"Conventional project inputs changed for '{path}'");
            }

            if (Path.GetDirectoryName(path) is { } directory && !TouchesAddition(directory, added.Keys))
            {
                untouched.Add(path);
            }
        }

        var selection = FindSourceRefreshOwners(
            projectsByPath, properties, importBaseline, request, added, untouched, _resourceLimits.CompilationWorkers > 1, _logger, cancellationToken);
        if (selection is null || selection.FallbackReason is not null)
        {
            return RequireGraphLoad(selection?.FallbackReason ?? "Project ownership or target framework coverage could not be verified");
        }

        var owners = selection.Projects;
        var checks = new List<ProjectRefreshCheck>();
        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (project.FilePath is not { } projectPath || Path.GetDirectoryName(projectPath) is not { } directory)
            {
                return null;
            }

            var inputs = importBaseline.Conventional.TryGetValue(projectPath, out var conventional)
                ? conventional.Local
                : _projectInputScopes.GetValue(project.Id, _ => CaptureProjectInputs(projectPath, directory, request, cancellationToken));
            if (conventional is null && !LocalInputsMatch(inputs, projectPath, directory))
            {
                return RequireGraphLoad($"Project scope inputs changed for '{projectPath}'");
            }

            // The watcher excludes these inputs. An independent build can change them even
            // if none of the selected design-time builds can write to their directories.
            var generated = await CaptureGeneratedInputsAsync(project, request, cancellationToken);
            if (generated is null)
            {
                return RequireGraphLoad($"Generated inputs could not be verified for '{projectPath}'");
            }

            checks.Add(new(projectPath, directory, inputs, generated));
        }

        var replacement = solution;
        var matched = new HashSet<string>(comparer);
        ProjectInfo?[]? ownerConfigurations = null;
        if (selection.CanLoadInParallel)
        {
            // Collect by index so completion order cannot affect validation or publication.
            var configurations = new ProjectInfo?[owners.Count];
            await Parallel.ForEachAsync(
                Enumerable.Range(0, owners.Count),
                new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = _resourceLimits.CompilationWorkers },
                async (index, token) => configurations[index] = await LoadProjectConfigurationAsync(owners[index], projectsByPath, properties, token));
            ownerConfigurations = configurations;
        }

        for (var index = 0; index < owners.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = owners[index];
            var info = ownerConfigurations is null
                ? await LoadProjectConfigurationAsync(project, projectsByPath, properties, cancellationToken)
                : ownerConfigurations[index];
            if (info is null || ApplyProjectCompilerOptions(replacement, project, info, request) is not { } configured)
            {
                return RequireGraphLoad($"Project configuration could not be reconciled for '{project.FilePath}'");
            }

            var updated = ApplyProjectMembership(configured, project, info, request, added, matched, cancellationToken);
            if (updated is null)
            {
                return RequireGraphLoad($"Project source membership could not be reconciled for '{project.FilePath}'");
            }

            replacement = updated;
        }

        identities.Clear();
        if (!RestoreInputsMatch(importBaseline, identities, request, cancellationToken))
        {
            return RequireGraphLoad("NuGet restore inputs changed while loading scoped projects");
        }

        foreach (var (path, conventional) in importBaseline.Conventional)
        {
            if (!ConventionalInputsMatch(path, conventional, identities, request, cancellationToken))
            {
                return RequireGraphLoad($"Conventional project inputs changed while loading '{path}'");
            }
        }

        foreach (var check in checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await GeneratedInputsMatchAsync(check.Generated, request, cancellationToken)
                || (!importBaseline.Conventional.ContainsKey(check.ProjectPath) && !LocalInputsMatch(check.Inputs, check.ProjectPath, check.Directory)))
            {
                return RequireGraphLoad($"Local or generated inputs changed while loading '{check.ProjectPath}'");
            }
        }

        var evaluatedPaths = importBaseline.Projects.Keys.Where(path => !untouched.Contains(path));
        if (TryCaptureResolvedImports(evaluatedPaths, properties, request, cancellationToken: cancellationToken) is not { } finalImports
            || !importBaseline.CoversImports(finalImports))
        {
            return RequireGraphLoad("Resolved project imports changed while loading scoped projects");
        }

        if (!importBaseline.CoversRestoreInputs(finalImports))
        {
            return RequireGraphLoad("Effective NuGet restore inputs changed while loading scoped projects");
        }

        if (finalImports.ExistsByProject.Values.SelectMany(project => project)
            .Any(pair => !existsBeforeLoad.TryGetValue(pair.Key, out var expected) || pair.Value != expected))
        {
            return RequireGraphLoad("Literal MSBuild Exists state changed while loading scoped projects");
        }

        var updatedBaseline = importBaseline with
        {
            ExistsByProject = importBaseline.ExistsByProject.Select(pair => KeyValuePair.Create(pair.Key, finalImports.ExistsByProject.GetValueOrDefault(pair.Key, pair.Value)))
                .ToFrozenDictionary(comparer),
        };
        return matched.Count == added.Count
            ? new ResolvedProjectDocuments(replacement, updatedBaseline)
            : RequireGraphLoad("Not all added sources matched loaded projects");

        ResolvedProjectDocuments? RequireGraphLoad(string reason)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Project-scoped refresh requires a graph load: {ProjectRefreshFallbackReason}", reason);
            }

            return null;
        }

        bool LocalInputsMatch(FrozenDictionary<string, string?> inputs, string projectPath, string directory) =>
            ScopeInputsMatch(inputs, request, cancellationToken, identities) && !HasUnindexedLocalImports(projectPath, directory, request);
    }

    private static Solution? ApplyProjectMembership(
        Solution replacement,
        Project owner,
        ProjectInfo info,
        SemanticLoadRequest request,
        Dictionary<string, SemanticDocumentRefresh> added,
        HashSet<string> matched,
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

            if (!added.TryGetValue(path, out var change) || !newInProject.Add(path))
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

        return order.SequenceEqual(owner.DocumentIds)
            ? replacement
            : replacement.WithProjectDocumentsOrder(owner.Id, order.ToImmutable());
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

        var ownerPath = owner.FilePath ?? throw new InvalidOperationException();
        var progress = new ProjectLoadProgressCollector(_logger);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Opening semantic scoped project {ScopedProjectLoadPath}", ownerPath);
        }

        var infos = await loader.LoadProjectInfoAsync(ownerPath, map, progress, cancellationToken: cancellationToken);
        return infos.Length == 1 && Volatile.Read(ref failed) == 0 ? infos[0] : null;
    }

    private static Solution? ApplyProjectCompilerOptions(
        Solution solution, Project project, ProjectInfo info, SemanticLoadRequest request)
    {
        var comparer = StringComparerForCurrentPlatform();
        if (project.CompilationOptions is not { } options || info.CompilationOptions is not { } evaluated
            || info.ParseOptions is not { } parseOptions
            || !comparer.Equals(info.OutputFilePath, project.OutputFilePath)
            || !string.Equals(info.AssemblyName, project.AssemblyName, StringComparison.Ordinal)
            || !info.MetadataReferences.Select(reference => (reference.Display, reference.Properties)).SequenceEqual(
                project.MetadataReferences.Select(reference => (reference.Display, reference.Properties)))
            || !info.AnalyzerReferences.Select(reference => reference.FullPath).SequenceEqual(
                project.AnalyzerReferences.Select(reference => reference.FullPath), comparer)
            || !project.ProjectReferences.ToHashSet().SetEquals(info.ProjectReferences)
            || !SameDocuments(info.AdditionalDocuments, project.AdditionalDocuments)
            || !SameDocuments(info.AnalyzerConfigDocuments, project.AnalyzerConfigDocuments))
        {
            return null;
        }

        // Retain live workspace services after verifying the references and analyzer inputs above.
        var compilationOptions = evaluated.WithSyntaxTreeOptionsProvider(options.SyntaxTreeOptionsProvider)
            .WithMetadataReferenceResolver(options.MetadataReferenceResolver);
        if (compilationOptions.SpecificDiagnosticOptions.ToHashSet().SetEquals(options.SpecificDiagnosticOptions))
        {
            compilationOptions = compilationOptions.WithSpecificDiagnosticOptions(options.SpecificDiagnosticOptions);
        }

        if (!Equals(compilationOptions, options))
        {
            solution = solution.WithProjectCompilationOptions(project.Id, compilationOptions);
        }

        return Equals(parseOptions, project.ParseOptions)
            ? solution
            : solution.WithProjectParseOptions(project.Id, parseOptions);

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
        string ProjectPath,
        string Directory,
        FrozenDictionary<string, string?> Inputs,
        List<(string Path, SourceText Text)> Generated);

    private sealed record ResolvedProjectDocuments(Solution Solution, ResolvedImportSnapshot Inputs);

    private sealed record ProjectSourceAddition(
        long Ownership,
        Solution Base,
        Solution Replacement,
        ResolvedImportSnapshot Inputs,
        IReadOnlySet<string> SourcePaths,
        ImmutableArray<ProjectId> ChangedOptions);

    private sealed record ScopedProjectRefresh(long Generation, ImmutableArray<ProjectId> ChangedOptions);
}
