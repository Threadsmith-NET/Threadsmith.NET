namespace Threadsmith.DotNet;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Provides confidence-aware Roslyn and MSBuild semantic discovery.</summary>
public sealed class SemanticEngine : ISemanticEngine
{
    private const int _maximumFallbackFiles = 10_000;
    private const int _maximumFallbackMatches = 500;
    private const int _maximumFallbackEntries = 50_000;
    private const long _maximumFallbackFileBytes = 1024 * 1024;
    private static readonly Lock _msBuildGate = new();
    private readonly TimeSpan _cancellationBackstop;
    private readonly IDomainEventStream _events;
    private readonly Lock _gate = new();
    private readonly ConcurrentQueue<string> _invalidations = new();
    private readonly ILogger<SemanticEngine> _logger;
    private HashSet<ProjectId> _compiledProjects = [];
    private SemanticConfidenceLevel _confidence;
    private SemanticLoadRequest? _lastRequest;
    private IReadOnlyList<SemanticProjectInfo> _projects = [];
    private long _generation;
    private Solution? _solution;
    private MSBuildWorkspace? _workspace;

    /// <summary>Initializes a new instance of the <see cref="SemanticEngine"/> class.</summary>
    public SemanticEngine(
        IDomainEventStream events,
        ILogger<SemanticEngine> logger,
        TimeSpan? cancellationBackstop = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(logger);
        if (cancellationBackstop <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cancellationBackstop));
        }

        _events = events;
        _logger = logger;
        _cancellationBackstop = cancellationBackstop ?? TimeSpan.FromSeconds(2);
    }

    /// <inheritdoc />
    public SemanticConfidenceLevel Confidence
    {
        get
        {
            lock (_gate)
            {
                return _confidence;
            }
        }
    }

    /// <summary>Gets the current host-owned semantic project inventory.</summary>
    public IReadOnlyList<SemanticProjectInfo> Projects
    {
        get
        {
            lock (_gate)
            {
                return _projects.ToArray();
            }
        }
    }

    /// <summary>Gets the authoritative request that produced the loaded workspace.</summary>
    internal SemanticLoadRequest LoadedRequest
    {
        get
        {
            lock (_gate)
            {
                return _lastRequest
                    ?? throw new InvalidOperationException("No semantic solution has been loaded.");
            }
        }
    }

    /// <inheritdoc />
    public async Task<SemanticLoadResult> LoadAsync(
        SemanticLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SolutionPath);
        var repositoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(request.RepositoryPath));
        var solutionPath = Path.GetFullPath(request.SolutionPath);
        if (!IsPathWithinRoot(solutionPath, repositoryPath)
            || !File.Exists(solutionPath))
        {
            throw new InvalidOperationException("The semantic solution must exist under the repository root.");
        }

        var selectionExtension = Path.GetExtension(solutionPath);
        var isDirectProject = selectionExtension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || selectionExtension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            || selectionExtension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase);
        var projectPaths = new List<string>();
        if (isDirectProject)
        {
            projectPaths.Add(solutionPath);
        }
        else
        {
            foreach (var line in await File.ReadAllLinesAsync(solutionPath, cancellationToken))
            {
                var relativePath = line.Split('"')
                    .FirstOrDefault(part => part.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                        || part.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
                        || part.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase));
                if (relativePath is null)
                {
                    continue;
                }

                var projectPath = Path.GetFullPath(
                    relativePath.Replace('\\', Path.DirectorySeparatorChar),
                    Path.GetDirectoryName(solutionPath) ?? repositoryPath);
                if (IsPathWithinRoot(projectPath, repositoryPath))
                {
                    projectPaths.Add(projectPath);
                }
            }
        }

        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        SemanticProjectInfo[] metadata = [.. projectPaths
            .Distinct(pathComparer)
            .Select(path => File.Exists(path)
                ? ReadProjectInfo(path)
                : new SemanticProjectInfo(
                    Path.GetFileNameWithoutExtension(path),
                    path,
                    [],
                    SemanticConfidenceLevel.ProjectGraphOnly,
                    [],
                    []))];
        var normalizedRequest = request with
        {
            RepositoryPath = repositoryPath,
            SolutionPath = solutionPath,
        };
        lock (_gate)
        {
            _lastRequest = normalizedRequest;
        }

        if (request.TrustLevel < RepositoryTrustLevel.TrustedBuild)
        {
            SemanticProjectInfo[] textOnly = [.. metadata.Select(project => project with { Confidence = SemanticConfidenceLevel.TextOnly })];
            var textConfidence = textOnly.Length == 0
                ? SemanticConfidenceLevel.None
                : SemanticConfidenceLevel.TextOnly;
            await ReplaceStateAsync(
                solution: null,
                workspace: null,
                compiledProjects: [],
                projects: textOnly,
                confidence: textConfidence,
                request.SessionId,
                request.WorkspaceId,
                cancellationToken);
            return new SemanticLoadResult(
                request.WorkspaceId,
                Confidence,
                textOnly,
                ["MSBuild evaluation requires TrustedBuild; text-only project metadata was loaded."]);
        }

        var diagnostics = new ConcurrentQueue<string>();
        var workspaceFailureCount = 0;
        (MSBuildWorkspace Workspace, Solution Solution) load;
        try
        {
            load = await RunNonCooperativeAsync(
                async operationToken =>
                {
                    lock (_msBuildGate)
                    {
                        if (!MSBuildLocator.IsRegistered)
                        {
                            MSBuildLocator.RegisterDefaults();
                        }
                    }

                    var workspace = MSBuildWorkspace.Create();
                    workspace.LoadMetadataForReferencedProjects = true;
                    workspace.RegisterWorkspaceFailedHandler(eventArgs =>
                    {
                        diagnostics.Enqueue(
                            $"{eventArgs.Diagnostic.Kind}: {eventArgs.Diagnostic.Message}");
                        if (eventArgs.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                        {
                            Interlocked.Increment(ref workspaceFailureCount);
                        }
                    });
                    try
                    {
                        Solution solution;
                        if (isDirectProject)
                        {
                            var project = await workspace.OpenProjectAsync(
                                solutionPath,
                                progress: null,
                                operationToken);
                            solution = project.Solution;
                        }
                        else
                        {
                            solution = await workspace.OpenSolutionAsync(
                                solutionPath,
                                progress: null,
                                operationToken);
                        }

                        return (Workspace: workspace, Solution: solution);
                    }
                    catch
                    {
                        workspace.Dispose();
                        throw;
                    }
                },
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(
                exception,
                "Semantic MSBuild loading failed for solution {SolutionPath}; using text metadata",
                solutionPath);
            diagnostics.Enqueue($"MSBuild load failed: {exception.Message}");
            SemanticProjectInfo[] degradedProjects = [.. metadata.Select(project => project with { Confidence = SemanticConfidenceLevel.TextOnly })];
            var degradedConfidence = degradedProjects.Length == 0
                ? SemanticConfidenceLevel.None
                : SemanticConfidenceLevel.TextOnly;
            await ReplaceStateAsync(
                solution: null,
                workspace: null,
                compiledProjects: [],
                projects: degradedProjects,
                confidence: degradedConfidence,
                request.SessionId,
                request.WorkspaceId,
                cancellationToken);
            return new SemanticLoadResult(
                request.WorkspaceId,
                degradedConfidence,
                degradedProjects,
                diagnostics.ToArray());
        }

        var confinedSolution = load.Solution;
        foreach (var project in confinedSolution.Projects.ToArray())
        {
            if (project.FilePath is null || !IsPathWithinRoot(project.FilePath, repositoryPath))
            {
                diagnostics.Enqueue(
                    $"Project '{project.Name}' was excluded because it is outside the repository root.");
                Interlocked.Increment(ref workspaceFailureCount);
                confinedSolution = confinedSolution.RemoveProject(project.Id);
                continue;
            }

            foreach (var document in project.Documents
                .Where(document => document.FilePath is null
                    || !IsPathWithinRoot(document.FilePath, repositoryPath))
                .ToArray())
            {
                diagnostics.Enqueue(
                    $"Document '{document.Name}' was excluded because it is outside the repository root.");
                Interlocked.Increment(ref workspaceFailureCount);
                confinedSolution = confinedSolution.RemoveDocument(document.Id);
            }
        }

        load = (load.Workspace, confinedSolution);

        var compiledProjects = new HashSet<ProjectId>();
        var loadedProjects = new List<SemanticProjectInfo>();
        foreach (var project in load.Solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Compilation? compilation = null;
            try
            {
                compilation = await RunNonCooperativeAsync<Compilation?>(
                    project.GetCompilationAsync,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception,
                    "Compilation creation failed for project {ProjectName}",
                    project.Name);
                diagnostics.Enqueue($"{project.Name}: {exception.Message}");
            }

            var projectConfidence = compilation is null
                ? SemanticConfidenceLevel.ProjectGraphOnly
                : SemanticConfidenceLevel.FullSemantic;
            if (compilation is not null)
            {
                compiledProjects.Add(project.Id);
            }

            var filePath = project.FilePath ?? string.Empty;
            var projectMetadata = metadata.FirstOrDefault(item => string.Equals(
                item.FilePath,
                filePath,
                PathComparison));
            loadedProjects.Add(projectMetadata is null
                ? new SemanticProjectInfo(
                    project.Name,
                    filePath,
                    [],
                    projectConfidence,
                    project.ProjectReferences
                        .Select(reference => load.Solution.GetProject(reference.ProjectId)?.Name)
                        .Where(name => name is not null)
                        .Select(name => name ?? string.Empty)
                        .ToArray(),
                    [])
                : projectMetadata with
                {
                    Name = project.Name,
                    Confidence = projectConfidence,
                });
        }

        var loadedProjectPaths = loadedProjects
            .Select(project => project.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(pathComparer);
        loadedProjects.AddRange(metadata
            .Where(project => !loadedProjectPaths.Contains(project.FilePath))
            .Select(project => project with
            {
                Confidence = SemanticConfidenceLevel.ProjectGraphOnly,
            }));

        var compiledProjectPaths = compiledProjects
            .Select(projectId => load.Solution.GetProject(projectId)?.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path ?? string.Empty)
            .ToHashSet(pathComparer);
        var everyExpectedProjectCompiled = metadata.Length > 0
            && metadata.All(project => compiledProjectPaths.Contains(project.FilePath));
        var everyLoadedProjectCompiled = loadedProjects.Count > 0
            && loadedProjects.All(project => project.Confidence == SemanticConfidenceLevel.FullSemantic);
        var aggregate = loadedProjects.Count == 0
            ? metadata.Length == 0
                ? SemanticConfidenceLevel.None
                : SemanticConfidenceLevel.ProjectGraphOnly
            : everyExpectedProjectCompiled
                && everyLoadedProjectCompiled
                && Volatile.Read(ref workspaceFailureCount) == 0
                ? SemanticConfidenceLevel.FullSemantic
                : compiledProjects.Count > 0
                    ? SemanticConfidenceLevel.PartialCompilation
                    : SemanticConfidenceLevel.ProjectGraphOnly;
        await ReplaceStateAsync(
            load.Solution,
            load.Workspace,
            compiledProjects,
            loadedProjects,
            aggregate,
            request.SessionId,
            request.WorkspaceId,
            cancellationToken);
        return new SemanticLoadResult(
            request.WorkspaceId,
            aggregate,
            loadedProjects,
            diagnostics.ToArray());
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SymbolResult>> FindSymbolsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        (var solution, var compiledProjects, var confidence, var _) = CaptureSemanticState();
        var symbols = await RunNonCooperativeAsync(
            async operationToken =>
            {
                var found = new List<ISymbol>();
                foreach (var project in solution.Projects.Where(project => compiledProjects.Contains(project.Id)))
                {
                    var declarations = await SymbolFinder.FindDeclarationsAsync(
                        project,
                        query,
                        ignoreCase: true,
                        SymbolFilter.TypeAndMember,
                        operationToken);
                    found.AddRange(declarations);
                }

                return found;
            },
            cancellationToken);
        var results = new List<SymbolResult>();
        var locationContext = CreateLocationContext(solution);
        foreach (var symbol in symbols.Distinct(SymbolEqualityComparer.Default))
        {
            var identity = CreateIdentity(symbol);
            foreach (var location in symbol.Locations.Where(location => location.IsInSource))
            {
                var source = CreateLocation(solution, location, locationContext);
                if (source is not null)
                {
                    results.Add(new SymbolResult(identity, source, confidence));
                }
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReferenceResult>> FindReferencesAsync(
        string symbolId,
        bool allowTextFallback = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        SemanticLoadRequest? request;
        SemanticConfidenceLevel confidence;
        lock (_gate)
        {
            request = _lastRequest;
            confidence = _confidence;
        }

        if (confidence < SemanticConfidenceLevel.PartialCompilation)
        {
            if (!allowTextFallback || request is null)
            {
                throw new InvalidOperationException(
                    $"FindReferences requires PartialCompilation; current confidence is {confidence}. "
                    + "Restore the repository with the selected SDK or opt into text fallback.");
            }

            var simpleName = symbolId[(symbolId.LastIndexOf('.') + 1)..]
                .Split('(', ':')[0];
            var fallback = new List<ReferenceResult>();
            var pending = new Stack<string>();
            pending.Push(request.RepositoryPath);
            var inspectedEntries = 0;
            var inspectedFiles = 0;
            var prohibitedPaths = request.ProhibitedPaths ?? [];
            while (pending.Count > 0
                && inspectedEntries < _maximumFallbackEntries
                && inspectedFiles < _maximumFallbackFiles
                && fallback.Count < _maximumFallbackMatches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                string[] entries;
                try
                {
                    entries = Directory.GetFileSystemEntries(directory);
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or System.Security.SecurityException)
                {
                    _logger.LogDebug(
                        "Skipping inaccessible semantic fallback directory {Directory}: {ErrorType}",
                        directory,
                        exception.GetType().Name);
                    continue;
                }

                foreach (var entry in entries)
                {
                    if (++inspectedEntries > _maximumFallbackEntries)
                    {
                        break;
                    }

                    FileAttributes attributes;
                    try
                    {
                        attributes = File.GetAttributes(entry);
                    }
                    catch (Exception exception) when (exception is IOException
                        or UnauthorizedAccessException
                        or System.Security.SecurityException)
                    {
                        _logger.LogDebug(
                            "Skipping inaccessible semantic fallback entry {Path}: {ErrorType}",
                            entry,
                            exception.GetType().Name);
                        continue;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    var relative = Path.GetRelativePath(request.RepositoryPath, entry).Replace('\\', '/');
                    if (RepositoryPathPolicy.IsProhibited(relative, prohibitedPaths))
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        var name = Path.GetFileName(entry);
                        if (name is not ".git" and not "bin" and not "obj")
                        {
                            pending.Push(entry);
                        }

                        continue;
                    }

                    if (!entry.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || ++inspectedFiles > _maximumFallbackFiles
                        || new FileInfo(entry).Length > _maximumFallbackFileBytes)
                    {
                        continue;
                    }

                    using var reader = new StreamReader(entry);
                    var lineNumber = 0;
                    while (fallback.Count < _maximumFallbackMatches
                        && await reader.ReadLineAsync(cancellationToken) is { } line)
                    {
                        lineNumber++;
                        var column = line.IndexOf(simpleName, StringComparison.Ordinal);
                        if (column >= 0)
                        {
                            fallback.Add(new ReferenceResult(
                                new SemanticSymbolIdentity(symbolId, simpleName, "TextMatch"),
                                new SemanticSourceLocation(
                                    string.Empty,
                                    string.Empty,
                                    entry,
                                    new SourceRange(
                                        lineNumber,
                                        column + 1,
                                        lineNumber,
                                        column + simpleName.Length + 1),
                                    IsGeneratedPath(entry),
                                    IsLinked: false),
                                SemanticConfidenceLevel.TextOnly));
                        }
                    }
                }
            }

            return fallback;
        }

        (var solution, var _, var currentConfidence, var _) = CaptureSemanticState();
        var symbol = await ResolveSymbolAsync(solution, symbolId, cancellationToken);
        var referencedSymbols = await RunNonCooperativeAsync(
            token => SymbolFinder.FindReferencesAsync(symbol, solution, token),
            cancellationToken);
        var results = new List<ReferenceResult>();
        var identity = CreateIdentity(symbol);
        var locationContext = CreateLocationContext(solution);
        foreach (var reference in referencedSymbols.SelectMany(item => item.Locations))
        {
            var source = CreateLocation(solution, reference.Location, locationContext);
            if (source is not null)
            {
                results.Add(new ReferenceResult(identity, source, currentConfidence));
            }
        }

        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImplementationResult>> FindImplementationsAsync(
        string symbolId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        (var solution, var _, var confidence, var _) = CaptureSemanticState();
        var symbol = await ResolveSymbolAsync(solution, symbolId, cancellationToken);
        var implementations = await RunNonCooperativeAsync(
            token => SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: token),
            cancellationToken);
        var results = new List<ImplementationResult>();
        var locationContext = CreateLocationContext(solution);
        foreach (var implementation in implementations)
        {
            var identity = CreateIdentity(implementation);
            foreach (var location in implementation.Locations.Where(location => location.IsInSource))
            {
                var source = CreateLocation(solution, location, locationContext);
                if (source is not null)
                {
                    results.Add(new ImplementationResult(identity, source, confidence));
                }
            }
        }

        return results;
    }

    /// <inheritdoc />
    public void QueueInvalidation(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _invalidations.Enqueue(path);
    }

    /// <inheritdoc />
    public async Task<SemanticConfidenceLevel> ApplyInvalidationsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var changed = new List<string>();
        while (_invalidations.TryDequeue(out var path))
        {
            changed.Add(path);
        }

        if (changed.Count == 0)
        {
            return Confidence;
        }

        SemanticLoadRequest? request;
        SemanticConfidenceLevel previous;
        lock (_gate)
        {
            request = _lastRequest;
            previous = _confidence;
            _confidence = SemanticConfidenceLevel.ProjectGraphOnly;
            _compiledProjects = [];
            _generation++;
        }

        if (request is not null && previous != SemanticConfidenceLevel.ProjectGraphOnly)
        {
            await _events.PublishAsync(
                new SemanticConfidenceChanged(
                    request.SessionId,
                    DateTimeOffset.UtcNow,
                    SemanticConfidenceLevel.ProjectGraphOnly.ToString()),
                cancellationToken);
        }

        return Confidence;
    }

    /// <inheritdoc />
    public Task<SemanticLoadResult> PromoteAsync(CancellationToken cancellationToken = default)
    {
        SemanticLoadRequest request;
        lock (_gate)
        {
            request = _lastRequest
                ?? throw new InvalidOperationException("No semantic solution has been loaded.");
        }

        return LoadAsync(request, cancellationToken);
    }

    /// <summary>Gets fast compiler diagnostics from the loaded Roslyn solution.</summary>
    public async Task<IReadOnlyList<Threadsmith.Core.Diagnostic>> GetDiagnosticsAsync(
        IReadOnlyList<string> projectPaths,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectPaths);
        ArgumentNullException.ThrowIfNull(changedFiles);
        (var solution, var compiledProjects, var confidence, var repositoryPath) =
            CaptureSemanticState();
        var pathComparer = StringComparerForCurrentPlatform();
        var requestedPaths = new HashSet<string>(
            projectPaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath),
            pathComparer);
        string[] refreshPaths =
        [
            .. changedFiles.Concat(GetDiagnosticRefreshPaths(solution, compiledProjects, requestedPaths))
                .Distinct(pathComparer),
        ];
        solution = await RefreshChangedDocumentsAsync(solution, refreshPaths, repositoryPath, cancellationToken);
        var diagnostics = new List<Threadsmith.Core.Diagnostic>();
        foreach (var project in solution.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!compiledProjects.Contains(project.Id))
            {
                continue;
            }

            var projectPath = project.FilePath is null ? null : Path.GetFullPath(project.FilePath);
            if (requestedPaths.Count > 0
                && (projectPath is null || !requestedPaths.Contains(projectPath)))
            {
                continue;
            }

            var compilation = await RunNonCooperativeAsync<Compilation?>(
                project.GetCompilationAsync,
                cancellationToken);
            if (compilation is null)
            {
                continue;
            }

            var targetFramework = projectPath is null || !File.Exists(projectPath)
                ? string.Empty
                : ReadProjectInfo(projectPath).TargetFrameworks.FirstOrDefault() ?? string.Empty;
            var context = CreateLocationContext(solution);
            foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken))
            {
                var location = diagnostic.Location == Location.None
                    ? null
                    : CreateLocation(solution, diagnostic.Location, context);
                var relativeFile = location?.FilePath is null
                    ? null
                    : Path.GetRelativePath(repositoryPath, location.FilePath).Replace('\\', '/');
                var range = location?.Range;
                var message = diagnostic.GetMessage();
                diagnostics.Add(new Threadsmith.Core.Diagnostic
                {
                    Id = string.Join(
                        ':',
                        diagnostic.Id,
                        project.Name,
                        relativeFile ?? string.Empty,
                        range?.StartLine.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                        range?.StartColumn.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                        message),
                    Code = diagnostic.Id,
                    Severity = diagnostic.Severity switch
                    {
                        Microsoft.CodeAnalysis.DiagnosticSeverity.Error => Threadsmith.Core.DiagnosticSeverity.Error,
                        Microsoft.CodeAnalysis.DiagnosticSeverity.Warning => Threadsmith.Core.DiagnosticSeverity.Warning,
                        _ => Threadsmith.Core.DiagnosticSeverity.Info,
                    },
                    Project = project.Name,
                    TargetFramework = targetFramework,
                    File = relativeFile,
                    Range = range,
                    Message = message,
                    Confidence = confidence,
                });
            }
        }

        return diagnostics;
    }

    /// <summary>Runs read-only Roslyn diagnostics over proposed in-memory C# mutation content.</summary>
    public async Task<PreMutationAnalysisResult> AnalyzePreMutationAsync(
        PreMutationAnalysisRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Baseline);
        ArgumentNullException.ThrowIfNull(request.MutationSet);
        var repositoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Baseline.RepositoryPath));
        PreMutationOverlayFile[] sourceFiles = [.. request.OverlayFiles
            .Where(file => IsCSharpSourcePath(file.RelativePath))];
        if (sourceFiles.Length == 0)
        {
            return new PreMutationAnalysisResult
            {
                Decision = PreMutationGateDecision.PassedCheapGates,
                Omissions = ["No changed C# source files required pre-mutation Roslyn analysis."],
                Confidence = _confidence,
                Score = new MutationCandidateScore
                {
                    SyntaxClean = true,
                    SemanticClean = true,
                    AnalyzerClean = true,
                },
            };
        }

        var diagnostics = new List<PreMutationDiagnostic>();
        var omissions = new List<string>
        {
            "Pre-approval analyzer execution is limited to host-owned allowlisted or isolated analyzers; ordinary repository analyzer/source-generator assemblies were not loaded.",
        };
        (var solution, var compiledProjects, var confidence, var loadedRepository) =
            TryCaptureSemanticState();
        var semanticRepository = string.IsNullOrWhiteSpace(loadedRepository)
            ? repositoryPath
            : loadedRepository;
        var sourceByFullPath = CreateOverlayMap(
            sourceFiles,
            repositoryPath);
        var documentsByPath = solution is null
            ? new Dictionary<string, DocumentId[]>(StringComparerForCurrentPlatform())
            : CreateDocumentsByPath(solution);
        var parseOptionsByPath = solution is null
            ? new Dictionary<string, CSharpParseOptions?>(StringComparerForCurrentPlatform())
            : CreateParseOptionsByPath(solution);

        var syntaxCheckId = SemanticCheckId.New();
        var syntaxStarted = Stopwatch.GetTimestamp();
        await PublishSemanticCheckStartedAsync(
            request,
            syntaxCheckId,
            "pre-mutation overlay syntax");
        try
        {
            foreach ((var fullPath, var overlay) in sourceByFullPath)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (overlay.Text is null)
                {
                    continue;
                }

                var parseOptions = GetParseOptions(fullPath, parseOptionsByPath);
                var tree = CSharpSyntaxTree.ParseText(
                    SourceText.From(overlay.Text, Encoding.UTF8),
                    parseOptions,
                    fullPath,
                    cancellationToken);
                foreach (var diagnostic in tree.GetDiagnostics(cancellationToken))
                {
                    diagnostics.Add(CreatePreMutationDiagnostic(
                        PreMutationDiagnosticSource.Syntax,
                        diagnostic,
                        repositoryPath,
                        overlay,
                        projectName: null,
                        targetFramework: null,
                        tree,
                        overlay.Text));
                }
            }
        }
        catch (OperationCanceledException)
        {
            await PublishSemanticCheckCompletedAsync(
                request,
                syntaxCheckId,
                "pre-mutation overlay syntax",
                SemanticCheckOutcome.Cancelled,
                syntaxStarted,
                "cancelled before syntax diagnostics completed");
            throw;
        }
        catch
        {
            await PublishSemanticCheckCompletedAsync(
                request,
                syntaxCheckId,
                "pre-mutation overlay syntax",
                SemanticCheckOutcome.Degraded,
                syntaxStarted,
                "syntax diagnostics failed before completion");
            throw;
        }

        var syntaxDiagnostics = diagnostics.Count(diagnostic => diagnostic.Source == PreMutationDiagnosticSource.Syntax);
        var syntaxBlocking = diagnostics.Count(diagnostic => diagnostic.Source == PreMutationDiagnosticSource.Syntax
            && diagnostic.Severity == Threadsmith.Core.DiagnosticSeverity.Error);
        await PublishSemanticCheckCompletedAsync(
            request,
            syntaxCheckId,
            "pre-mutation overlay syntax",
            syntaxBlocking > 0 ? SemanticCheckOutcome.Failed : SemanticCheckOutcome.Completed,
            syntaxStarted,
            FormatPreMutationCheckDetail(sourceFiles.Length, syntaxDiagnostics, syntaxBlocking, omissionCount: 0));

        var syntaxBlocks = syntaxBlocking > 0;
        var compilationCheckId = SemanticCheckId.New();
        var compilationStarted = Stopwatch.GetTimestamp();
        await PublishSemanticCheckStartedAsync(
            request,
            compilationCheckId,
            "pre-mutation compilation");
        var omissionCountBeforeCompilation = omissions.Count;
        var diagnosticCountBeforeCompilation = diagnostics.Count;
        try
        {
            if (!syntaxBlocks && solution is not null && confidence >= SemanticConfidenceLevel.PartialCompilation)
            {
                var overlaySolution = ApplyOverlayToSolution(
                    solution,
                    sourceByFullPath,
                    documentsByPath,
                    semanticRepository);
                var context = CreateLocationContext(overlaySolution);
                var affectedProjects = new HashSet<ProjectId>();
                foreach (var fullPath in sourceByFullPath.Keys)
                {
                    if (documentsByPath.TryGetValue(fullPath, out var documentIds))
                    {
                        foreach (var documentId in documentIds)
                        {
                            affectedProjects.Add(documentId.ProjectId);
                        }

                        continue;
                    }

                    foreach (var project in FindContainingProjects(overlaySolution, fullPath))
                    {
                        affectedProjects.Add(project.Id);
                    }
                }

                foreach (var project in overlaySolution.Projects
                    .Where(project => affectedProjects.Contains(project.Id) && compiledProjects.Contains(project.Id))
                    .OrderBy(project => project.Name, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var compilation = await RunNonCooperativeAsync<Compilation?>(
                        project.GetCompilationAsync,
                        cancellationToken);
                    if (compilation is null)
                    {
                        omissions.Add($"Compilation diagnostics were unavailable for project '{project.Name}'.");
                        continue;
                    }

                    var targetFramework = project.FilePath is null || !File.Exists(project.FilePath)
                        ? string.Empty
                        : ReadProjectInfo(project.FilePath).TargetFrameworks.FirstOrDefault() ?? string.Empty;
                    var baselineDiagnostics = await GetBaselineCompilationDiagnosticFingerprintsAsync(
                        solution,
                        project.Id,
                        repositoryPath,
                        cancellationToken);
                    foreach (var diagnostic in compilation.GetDiagnostics(cancellationToken)
                        .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error))
                    {
                        var diagnosticFingerprint = CreateCompilationDiagnosticFingerprint(
                            overlaySolution,
                            diagnostic,
                            repositoryPath);
                        if (baselineDiagnostics.TryGetValue(diagnosticFingerprint, out var baselineCount)
                            && baselineCount > 0)
                        {
                            baselineDiagnostics[diagnosticFingerprint] = baselineCount - 1;
                            continue;
                        }

                        var location = diagnostic.Location == Location.None
                            ? null
                            : CreateLocation(overlaySolution, diagnostic.Location, context);
                        if (location?.FilePath is not { } filePath)
                        {
                            continue;
                        }

                        var diagnosticFullPath = Path.GetFullPath(filePath);
                        var overlay = sourceByFullPath.TryGetValue(diagnosticFullPath, out var changedOverlay)
                            ? changedOverlay
                            : new PreMutationOverlayFile
                            {
                                RelativePath = ToRepositoryRelativePath(repositoryPath, diagnosticFullPath),
                            };
                        diagnostics.Add(CreatePreMutationDiagnostic(
                            PreMutationDiagnosticSource.Compilation,
                            diagnostic,
                            repositoryPath,
                            overlay,
                            project.Name,
                            targetFramework,
                            diagnostic.Location.SourceTree,
                            overlay.Text));
                    }
                }
            }
            else if (syntaxBlocks)
            {
                omissions.Add("Compilation diagnostics were skipped because syntax diagnostics blocked compilation.");
            }
            else
            {
                omissions.Add(
                    $"Semantic and compilation pre-mutation checks require PartialCompilation confidence; current confidence is {confidence}.");
            }
        }
        catch (OperationCanceledException)
        {
            await PublishSemanticCheckCompletedAsync(
                request,
                compilationCheckId,
                "pre-mutation compilation",
                SemanticCheckOutcome.Cancelled,
                compilationStarted,
                "cancelled before compilation diagnostics completed");
            throw;
        }
        catch
        {
            await PublishSemanticCheckCompletedAsync(
                request,
                compilationCheckId,
                "pre-mutation compilation",
                SemanticCheckOutcome.Degraded,
                compilationStarted,
                "compilation diagnostics failed before completion");
            throw;
        }

        var compilationDiagnostics = diagnostics.Count - diagnosticCountBeforeCompilation;
        var compilationBlocking = diagnostics
            .Skip(diagnosticCountBeforeCompilation)
            .Count(diagnostic => diagnostic.Severity == Threadsmith.Core.DiagnosticSeverity.Error);
        var compilationOmissions = omissions.Count - omissionCountBeforeCompilation;
        var compilationOutcome = syntaxBlocks
            ? SemanticCheckOutcome.Skipped
            : compilationBlocking > 0
                ? SemanticCheckOutcome.Failed
                : compilationOmissions > 0
                    ? SemanticCheckOutcome.Degraded
                    : SemanticCheckOutcome.Completed;
        await PublishSemanticCheckCompletedAsync(
            request,
            compilationCheckId,
            "pre-mutation compilation",
            compilationOutcome,
            compilationStarted,
            FormatPreMutationCheckDetail(sourceFiles.Length, compilationDiagnostics, compilationBlocking, compilationOmissions));

        PreMutationDiagnostic[] distinctDiagnostics = [.. diagnostics
            .DistinctBy(CreatePreMutationFingerprint, StringComparer.Ordinal)
            .OrderBy(diagnostic => diagnostic.File, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Range?.StartLine ?? 0)
            .ThenBy(diagnostic => diagnostic.Range?.StartColumn ?? 0)
            .ThenBy(diagnostic => diagnostic.Code, StringComparer.Ordinal)];
        var blockingCount = distinctDiagnostics.Count(diagnostic => diagnostic.Severity == Threadsmith.Core.DiagnosticSeverity.Error);
        return new PreMutationAnalysisResult
        {
            Decision = blockingCount > 0
                ? PreMutationGateDecision.RepairableDiagnostics
                : omissions.Count > 0
                    ? PreMutationGateDecision.DegradedProceedWithWarning
                    : PreMutationGateDecision.PassedCheapGates,
            Diagnostics = distinctDiagnostics,
            Omissions = omissions.Distinct(StringComparer.Ordinal).ToArray(),
            Confidence = confidence,
            Score = new MutationCandidateScore
            {
                SyntaxClean = !distinctDiagnostics.Any(diagnostic => diagnostic.Source == PreMutationDiagnosticSource.Syntax
                    && diagnostic.Severity == Threadsmith.Core.DiagnosticSeverity.Error),
                SemanticClean = !distinctDiagnostics.Any(diagnostic => diagnostic.Source is PreMutationDiagnosticSource.Semantic or PreMutationDiagnosticSource.Compilation
                    && diagnostic.Severity == Threadsmith.Core.DiagnosticSeverity.Error),
                AnalyzerClean = !distinctDiagnostics.Any(diagnostic => diagnostic.Source == PreMutationDiagnosticSource.Analyzer
                    && diagnostic.Severity == Threadsmith.Core.DiagnosticSeverity.Error),
                BlockingDiagnosticCount = blockingCount,
            },
        };
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        MSBuildWorkspace? workspace;
        lock (_gate)
        {
            workspace = _workspace;
            _workspace = null;
            _solution = null;
            _compiledProjects = [];
        }

        workspace?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Captures semantic readiness before deciding whether code_explore can return source.</summary>
    internal CodeExploreReadinessSnapshot CaptureCodeExploreReadinessSnapshot()
    {
        lock (_gate)
        {
            return new CodeExploreReadinessSnapshot(
                _solution,
                _compiledProjects.ToHashSet(),
                _confidence,
                _lastRequest?.RepositoryPath,
                _generation);
        }
    }

    /// <summary>Captures immutable Roslyn references for one fenced advanced semantic query.</summary>
    internal AdvancedSemanticSnapshot CaptureAdvancedSnapshot()
    {
        lock (_gate)
        {
            if (_confidence < SemanticConfidenceLevel.PartialCompilation
                || _solution is null
                || _lastRequest is null)
            {
                throw new InvalidOperationException(
                    $"Advanced semantic queries require PartialCompilation; current confidence is {_confidence}.");
            }

            return new AdvancedSemanticSnapshot(
                _solution,
                _compiledProjects.ToHashSet(),
                _confidence,
                _lastRequest.RepositoryPath,
                _generation);
        }
    }

    /// <summary>Returns whether a captured advanced-query generation is still current.</summary>
    internal bool IsCurrentGeneration(long generation)
    {
        lock (_gate)
        {
            return generation == _generation;
        }
    }

    /// <summary>Captures immutable Roslyn references for one serialized semantic mutation turn.</summary>
    internal SemanticMutationSnapshot CaptureMutationSnapshot()
    {
        lock (_gate)
        {
            if (_confidence < SemanticConfidenceLevel.PartialCompilation
                || _solution is null
                || _lastRequest is null)
            {
                throw new InvalidOperationException(
                    $"Semantic mutations require PartialCompilation; current confidence is {_confidence}. "
                    + "Restore the repository with the selected SDK or propose an explicitly approved text patch.");
            }

            return new SemanticMutationSnapshot(
                _solution,
                _compiledProjects.ToHashSet(),
                _confidence,
                _lastRequest.RepositoryPath);
        }
    }

    private async Task PublishSemanticCheckStartedAsync(
        PreMutationAnalysisRequest request,
        SemanticCheckId checkId,
        string checkName)
    {
        await _events.PublishAsync(
            new SemanticCheckStarted(
                request.SessionId,
                DateTimeOffset.UtcNow,
                request.RunId,
                checkId,
                SemanticCheckPhase.PreMutation,
                checkName),
            CancellationToken.None);
    }

    private async Task PublishSemanticCheckCompletedAsync(
        PreMutationAnalysisRequest request,
        SemanticCheckId checkId,
        string checkName,
        SemanticCheckOutcome outcome,
        long started,
        string detail)
    {
        await _events.PublishAsync(
            new SemanticCheckCompleted(
                request.SessionId,
                DateTimeOffset.UtcNow,
                request.RunId,
                checkId,
                SemanticCheckPhase.PreMutation,
                checkName,
                outcome,
                ToElapsedMilliseconds(Stopwatch.GetElapsedTime(started)),
                detail),
            CancellationToken.None);
    }

    private static string FormatPreMutationCheckDetail(
        int fileCount,
        int diagnosticCount,
        int blockingDiagnosticCount,
        int omissionCount)
    {
        return $"{fileCount} files, {diagnosticCount} diagnostics, {blockingDiagnosticCount} blocking, {omissionCount} omissions";
    }

    private static long? ToElapsedMilliseconds(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero || elapsed.TotalMilliseconds > long.MaxValue)
        {
            return null;
        }

        return (long)elapsed.TotalMilliseconds;
    }

    private (Solution? Solution, HashSet<ProjectId> CompiledProjects, SemanticConfidenceLevel Confidence, string RepositoryPath)
        TryCaptureSemanticState()
    {
        lock (_gate)
        {
            return _solution is null || _lastRequest is null
                ? (null, [], _confidence, string.Empty)
                : (_solution, [.. _compiledProjects], _confidence, _lastRequest.RepositoryPath);
        }
    }

    private async Task<Dictionary<string, int>> GetBaselineCompilationDiagnosticFingerprintsAsync(
        Solution solution,
        ProjectId projectId,
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var baselineProject = solution.GetProject(projectId);
        if (baselineProject is null)
        {
            return [];
        }

        var baselineCompilation = await RunNonCooperativeAsync<Compilation?>(
            baselineProject.GetCompilationAsync,
            cancellationToken);
        if (baselineCompilation is null)
        {
            return [];
        }

        return baselineCompilation.GetDiagnostics(cancellationToken)
            .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .Select(diagnostic => CreateCompilationDiagnosticFingerprint(
                solution,
                diagnostic,
                repositoryPath))
            .GroupBy(fingerprint => fingerprint, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Count(),
                StringComparer.Ordinal);
    }

    private static string CreateCompilationDiagnosticFingerprint(
        Solution solution,
        Microsoft.CodeAnalysis.Diagnostic diagnostic,
        string repositoryPath)
    {
        var file = string.Empty;
        if (diagnostic.Location != Location.None)
        {
            var location = CreateLocation(
                solution,
                diagnostic.Location,
                CreateLocationContext(solution));
            if (location?.FilePath is not null)
            {
                file = ToRepositoryRelativePath(repositoryPath, Path.GetFullPath(location.FilePath));
            }
        }

        return string.Join(
            '|',
            diagnostic.Id,
            file,
            diagnostic.GetMessage());
    }

    private static Dictionary<string, PreMutationOverlayFile> CreateOverlayMap(
        IReadOnlyList<PreMutationOverlayFile> files,
        string repositoryPath)
    {
        var sourceByFullPath = new Dictionary<string, PreMutationOverlayFile>(StringComparerForCurrentPlatform());
        foreach (var file in files)
        {
            var fullPath = Path.GetFullPath(
                file.RelativePath.Replace('/', Path.DirectorySeparatorChar),
                repositoryPath);
            if (!IsPathWithinRoot(fullPath, repositoryPath))
            {
                continue;
            }

            sourceByFullPath[fullPath] = file;
        }

        return sourceByFullPath;
    }

    private static Dictionary<string, DocumentId[]> CreateDocumentsByPath(Solution solution)
    {
        var comparer = StringComparerForCurrentPlatform();
        return solution.Projects
            .SelectMany(project => project.Documents)
            .Where(document => !string.IsNullOrWhiteSpace(document.FilePath))
            .GroupBy(document => Path.GetFullPath(document.FilePath ?? string.Empty), comparer)
            .ToDictionary(
                group => group.Key,
                group => group.Select(document => document.Id).ToArray(),
                comparer);
    }

    private static Dictionary<string, CSharpParseOptions?> CreateParseOptionsByPath(Solution solution)
    {
        var comparer = StringComparerForCurrentPlatform();
        return solution.Projects
            .SelectMany(project => project.Documents.Select(document => new
            {
                document.FilePath,
                ParseOptions = project.ParseOptions as CSharpParseOptions,
            }))
            .Where(item => !string.IsNullOrWhiteSpace(item.FilePath))
            .GroupBy(item => Path.GetFullPath(item.FilePath ?? string.Empty), comparer)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.ParseOptions).FirstOrDefault(),
                comparer);
    }

    private static CSharpParseOptions GetParseOptions(
        string fullPath,
        IReadOnlyDictionary<string, CSharpParseOptions?> parseOptionsByPath)
    {
        return parseOptionsByPath.TryGetValue(fullPath, out var options) && options is not null
            ? options
            : CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);
    }

    private static Solution ApplyOverlayToSolution(
        Solution solution,
        IReadOnlyDictionary<string, PreMutationOverlayFile> sourceByFullPath,
        IReadOnlyDictionary<string, DocumentId[]> documentsByPath,
        string repositoryPath)
    {
        var overlaySolution = solution;
        foreach ((var fullPath, var overlay) in sourceByFullPath)
        {
            if (documentsByPath.TryGetValue(fullPath, out var documentIds))
            {
                foreach (var documentId in documentIds)
                {
                    overlaySolution = overlay.Text is null
                        ? overlaySolution.RemoveDocument(documentId)
                        : overlaySolution.WithDocumentText(
                            documentId,
                            SourceText.From(overlay.Text, Encoding.UTF8),
                            PreservationMode.PreserveIdentity);
                }

                continue;
            }

            if (overlay.Text is null)
            {
                continue;
            }

            foreach (var project in FindContainingProjects(overlaySolution, fullPath))
            {
                overlaySolution = overlaySolution.AddDocument(
                    DocumentId.CreateNewId(project.Id),
                    Path.GetFileName(fullPath),
                    SourceText.From(overlay.Text, Encoding.UTF8),
                    GetDocumentFolders(project.FilePath, fullPath),
                    fullPath);
            }
        }

        return overlaySolution;
    }

    private static string ToRepositoryRelativePath(string repositoryPath, string fullPath)
    {
        var relative = Path.GetRelativePath(repositoryPath, fullPath);
        return relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
                ? fullPath
                : relative.Replace('\\', '/');
    }

    private static PreMutationDiagnostic CreatePreMutationDiagnostic(
        PreMutationDiagnosticSource source,
        Microsoft.CodeAnalysis.Diagnostic diagnostic,
        string repositoryPath,
        PreMutationOverlayFile overlay,
        string? projectName,
        string? targetFramework,
        SyntaxTree? syntaxTree,
        string? text)
    {
        var lineSpan = diagnostic.Location == Location.None
            ? default
            : diagnostic.Location.GetLineSpan();
        var range = diagnostic.Location == Location.None
            ? null
            : new SourceRange(
                lineSpan.StartLinePosition.Line + 1,
                lineSpan.StartLinePosition.Character + 1,
                lineSpan.EndLinePosition.Line + 1,
                lineSpan.EndLinePosition.Character + 1);
        string? file = null;
        if (diagnostic.Location != Location.None)
        {
            var path = string.IsNullOrWhiteSpace(lineSpan.Path)
                ? overlay.RelativePath
                : lineSpan.Path;
            file = ToRepositoryRelativePath(repositoryPath, Path.GetFullPath(path));
        }

        return new PreMutationDiagnostic
        {
            Source = source,
            Code = diagnostic.Id,
            Severity = diagnostic.Severity switch
            {
                Microsoft.CodeAnalysis.DiagnosticSeverity.Error => Threadsmith.Core.DiagnosticSeverity.Error,
                Microsoft.CodeAnalysis.DiagnosticSeverity.Warning => Threadsmith.Core.DiagnosticSeverity.Warning,
                _ => Threadsmith.Core.DiagnosticSeverity.Info,
            },
            File = file ?? overlay.RelativePath,
            Range = range,
            Message = diagnostic.GetMessage(),
            Project = projectName,
            TargetFramework = targetFramework,
            RelatedMutationId = overlay.RelatedMutationId,
            ChangedHunk = GetLineExcerpt(text, range?.StartLine),
            ContainingSymbol = GetContainingSyntax(syntaxTree, diagnostic.Location),
        };
    }

    private static string CreatePreMutationFingerprint(PreMutationDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return string.Join(
            '|',
            diagnostic.Source,
            diagnostic.Code,
            diagnostic.File,
            diagnostic.Range?.StartLine,
            diagnostic.Range?.StartColumn,
            diagnostic.Message);
    }

    private static string? GetLineExcerpt(string? text, int? oneBasedLine)
    {
        if (string.IsNullOrEmpty(text) || oneBasedLine is null || oneBasedLine <= 0)
        {
            return null;
        }

        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var index = oneBasedLine.Value - 1;
        return index >= 0 && index < lines.Length
            ? lines[index].Trim()
            : null;
    }

    private static string? GetContainingSyntax(SyntaxTree? syntaxTree, Location location)
    {
        if (syntaxTree is null || location == Location.None || !location.IsInSource)
        {
            return null;
        }

        var root = syntaxTree.GetRoot();
        var node = root.FindNode(location.SourceSpan, getInnermostNodeForTie: true);
        var containing = node.AncestorsAndSelf()
            .FirstOrDefault(candidate => candidate is Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.NamespaceDeclarationSyntax
                or Microsoft.CodeAnalysis.CSharp.Syntax.FileScopedNamespaceDeclarationSyntax);
        return containing is null
            ? node.Kind().ToString()
            : containing.Kind().ToString();
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static StringComparer StringComparerForCurrentPlatform()
    {
        return OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
    }

    private static bool IsPathWithinRoot(string path, string root)
    {
        return path.Equals(root, PathComparison)
                || path.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    private static SemanticProjectInfo ReadProjectInfo(string projectPath)
    {
        var document = XDocument.Load(projectPath, LoadOptions.None);
        string[] frameworks = [.. document.Descendants()
            .Where(element => element.Name.LocalName is "TargetFramework" or "TargetFrameworks")
            .SelectMany(element => element.Value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        string[] projectReferences = [.. document.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value ?? string.Empty)];
        string[] packageReferences = [.. document.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value ?? string.Empty)];
        return new SemanticProjectInfo(
            Path.GetFileNameWithoutExtension(projectPath),
            projectPath,
            frameworks,
            SemanticConfidenceLevel.TextOnly,
            projectReferences,
            packageReferences);
    }

    private static SemanticSymbolIdentity CreateIdentity(ISymbol symbol)
    {
        var id = symbol.GetDocumentationCommentId()
            ?? $"{symbol.Kind}:{symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}";
        return new SemanticSymbolIdentity(
            id,
            symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat),
            symbol.Kind.ToString());
    }

    private static SemanticLocationContext CreateLocationContext(Solution solution)
    {
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var documentCounts = solution.Projects
            .SelectMany(project => project.Documents)
            .Where(document => !string.IsNullOrWhiteSpace(document.FilePath))
            .GroupBy(document => document.FilePath ?? string.Empty, pathComparer)
            .ToDictionary(group => group.Key, group => group.Count(), pathComparer);
        var targetFrameworks = solution.Projects
            .Where(project => project.FilePath is not null && File.Exists(project.FilePath))
            .GroupBy(project => project.FilePath ?? string.Empty, pathComparer)
            .ToDictionary(
                group => group.Key,
                group => ReadProjectInfo(group.Key)
                    .TargetFrameworks.FirstOrDefault() ?? string.Empty,
                pathComparer);
        return new SemanticLocationContext(documentCounts, targetFrameworks);
    }

    private static SemanticSourceLocation? CreateLocation(
        Solution solution,
        Location location,
        SemanticLocationContext context)
    {
        if (!location.IsInSource || location.SourceTree is null)
        {
            return null;
        }

        var document = solution.GetDocument(location.SourceTree);
        if (document is null)
        {
            return null;
        }

        var lineSpan = location.GetLineSpan();
        var filePath = document.FilePath ?? lineSpan.Path;
        var targetFramework = document.Project.FilePath is { } projectPath
            && context.TargetFrameworks.TryGetValue(projectPath, out var framework)
                ? framework
                : string.Empty;
        var linked = context.DocumentCounts.TryGetValue(filePath, out var count) && count > 1;
        return new SemanticSourceLocation(
            document.Project.Name,
            targetFramework,
            filePath,
            new SourceRange(
                lineSpan.StartLinePosition.Line + 1,
                lineSpan.StartLinePosition.Character + 1,
                lineSpan.EndLinePosition.Line + 1,
                lineSpan.EndLinePosition.Character + 1),
            IsGeneratedPath(filePath),
            linked);
    }

    private static bool IsGeneratedPath(string path)
    {
        return path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)
                || path.Contains(
                    $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ISymbol> ResolveSymbolAsync(
        Solution solution,
        string symbolId,
        CancellationToken cancellationToken)
    {
        return await RunNonCooperativeAsync(
            async operationToken =>
            {
                foreach (var project in solution.Projects)
                {
                    var compilation = await project.GetCompilationAsync(operationToken);
                    if (compilation is null)
                    {
                        continue;
                    }

                    var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(
                        symbolId,
                        compilation);
                    if (symbol is not null)
                    {
                        return symbol;
                    }
                }

                throw new KeyNotFoundException($"Semantic symbol '{symbolId}' is not loaded.");
            },
            cancellationToken);
    }

    private (Solution Solution, HashSet<ProjectId> CompiledProjects, SemanticConfidenceLevel Confidence, string RepositoryPath)
        CaptureSemanticState()
    {
        lock (_gate)
        {
            if (_confidence < SemanticConfidenceLevel.PartialCompilation || _solution is null || _lastRequest is null)
            {
                throw new InvalidOperationException(
                    $"Semantic search requires PartialCompilation; current confidence is {_confidence}.");
            }

            return (_solution, [.. _compiledProjects], _confidence, _lastRequest.RepositoryPath);
        }
    }

    private static IReadOnlyList<string> GetDiagnosticRefreshPaths(
        Solution solution,
        IReadOnlySet<ProjectId> compiledProjects,
        IReadOnlySet<string> requestedProjectPaths)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(compiledProjects);
        ArgumentNullException.ThrowIfNull(requestedProjectPaths);
        return [.. solution.Projects
            .Where(project => compiledProjects.Contains(project.Id))
            .Where(project => requestedProjectPaths.Count == 0
                || (project.FilePath is not null
                    && requestedProjectPaths.Contains(Path.GetFullPath(project.FilePath))))
            .SelectMany(project => project.Documents)
            .Select(document => document.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path ?? string.Empty))];
    }

    private async Task<Solution> RefreshChangedDocumentsAsync(
        Solution solution,
        IReadOnlyList<string> changedFiles,
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        if (changedFiles.Count == 0)
        {
            return solution;
        }

        var comparer = StringComparerForCurrentPlatform();
        string[] normalizedPaths = [.. changedFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar), repositoryPath))
            .Where(path => IsPathWithinRoot(path, repositoryPath))
            .Distinct(comparer)];
        if (normalizedPaths.Length == 0)
        {
            return solution;
        }

        var sourceByPath = new Dictionary<string, SourceText>(comparer);
        foreach (var changedPath in normalizedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(changedPath) || !IsCSharpSourcePath(changedPath))
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(changedPath, cancellationToken);
            sourceByPath[changedPath] = SourceText.From(content, Encoding.UTF8);
        }

        var documentsByPath = solution.Projects
            .SelectMany(project => project.Documents)
            .Where(document => !string.IsNullOrWhiteSpace(document.FilePath))
            .GroupBy(document => Path.GetFullPath(document.FilePath ?? string.Empty), comparer)
            .ToDictionary(
                group => group.Key,
                group => group.Select(document => document.Id).ToArray(),
                comparer);

        var refreshed = solution;
        foreach (var changedPath in normalizedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (documentsByPath.TryGetValue(changedPath, out var documentIds))
            {
                if (sourceByPath.TryGetValue(changedPath, out var sourceText))
                {
                    foreach (var documentId in documentIds)
                    {
                        refreshed = refreshed.WithDocumentText(
                            documentId,
                            sourceText,
                            PreservationMode.PreserveIdentity);
                    }
                }
                else
                {
                    foreach (var documentId in documentIds)
                    {
                        refreshed = refreshed.RemoveDocument(documentId);
                    }
                }

                continue;
            }

            if (!sourceByPath.TryGetValue(changedPath, out var newSourceText))
            {
                continue;
            }

            foreach (var project in FindContainingProjects(refreshed, changedPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                refreshed = refreshed.AddDocument(
                    DocumentId.CreateNewId(project.Id),
                    Path.GetFileName(changedPath),
                    newSourceText,
                    GetDocumentFolders(project.FilePath, changedPath),
                    changedPath);
            }
        }

        if (!ReferenceEquals(refreshed, solution))
        {
            lock (_gate)
            {
                if (ReferenceEquals(_solution, solution))
                {
                    _solution = refreshed;
                }
            }
        }

        return refreshed;
    }

    private static bool IsCSharpSourcePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<Project> FindContainingProjects(Solution solution, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return [.. solution.Projects.Where(project =>
        {
            if (project.FilePath is null)
            {
                return false;
            }

            var projectDirectory = Path.GetDirectoryName(project.FilePath);
            return !string.IsNullOrWhiteSpace(projectDirectory)
                && IsPathWithinRoot(sourcePath, Path.GetFullPath(projectDirectory));
        })];
    }

    private static IReadOnlyList<string> GetDocumentFolders(string? projectPath, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return [];
        }

        var projectDirectory = Path.GetDirectoryName(projectPath);
        var sourceDirectory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(projectDirectory)
            || string.IsNullOrWhiteSpace(sourceDirectory))
        {
            return [];
        }

        var relativeDirectory = Path.GetRelativePath(projectDirectory, sourceDirectory);
        if (relativeDirectory == "."
            || relativeDirectory.StartsWith("..", StringComparison.Ordinal))
        {
            return [];
        }

        return [.. relativeDirectory.Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries)];
    }

    private async Task ReplaceStateAsync(
        Solution? solution,
        MSBuildWorkspace? workspace,
        HashSet<ProjectId> compiledProjects,
        IReadOnlyList<SemanticProjectInfo> projects,
        SemanticConfidenceLevel confidence,
        SessionId sessionId,
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        MSBuildWorkspace? previousWorkspace;
        SemanticConfidenceLevel previousConfidence;
        lock (_gate)
        {
            previousWorkspace = _workspace;
            previousConfidence = _confidence;
            _workspace = workspace;
            _solution = solution;
            _compiledProjects = compiledProjects;
            _projects = projects;
            _confidence = confidence;
            _generation++;
        }

        if (!ReferenceEquals(previousWorkspace, workspace))
        {
            previousWorkspace?.Dispose();
        }

        if (previousConfidence != confidence)
        {
            await _events.PublishAsync(
                new SemanticConfidenceChanged(
                    sessionId,
                    DateTimeOffset.UtcNow,
                    confidence.ToString()),
                cancellationToken);
        }

        await _events.PublishAsync(
            new SemanticLoadCompleted(
                sessionId,
                DateTimeOffset.UtcNow,
                workspaceId,
                confidence.ToString()),
            cancellationToken);
    }

    private async Task<T> RunNonCooperativeAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var task = Task.Run(() => operation(operationCancellation.Token), CancellationToken.None);
        try
        {
            return await task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await operationCancellation.CancelAsync();
            var completed = await Task.WhenAny(
                task,
                Task.Delay(_cancellationBackstop, CancellationToken.None));
            if (completed == task)
            {
                try
                {
                    _ = await task;
                }
                catch (OperationCanceledException)
                {
                    // Cancellation is the expected bounded-wait outcome.
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Abandoned semantic operation faulted during backstop wait");
                }
            }
            else
            {
                _ = task.ContinueWith(
                    faulted => _logger.LogWarning(
                        faulted.Exception,
                        "Abandoned semantic operation faulted after cancellation"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }

            throw;
        }
    }

    private sealed record SemanticLocationContext(
        IReadOnlyDictionary<string, int> DocumentCounts,
        IReadOnlyDictionary<string, string> TargetFrameworks);
}
