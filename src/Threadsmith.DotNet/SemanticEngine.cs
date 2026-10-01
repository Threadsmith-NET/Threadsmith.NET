namespace Threadsmith.DotNet;

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Provides confidence-aware Roslyn and MSBuild semantic discovery.</summary>
public sealed partial class SemanticEngine : ISemanticEngine
{
    private const int MaximumSlowProjectSamples = 3;
    private static readonly Lock _msBuildGate = new();
    private static readonly SemanticRefreshInventory EmptyRefreshInventory = new(
        FrozenSet<string>.Empty,
        FrozenSet<string>.Empty,
        FrozenSet<string>.Empty,
        FrozenSet<string>.Empty);

    private readonly TimeSpan _cancellationBackstop;
    private readonly SemanticResourceLimits _resourceLimits;
    private readonly IDomainEventStream _events;
    private readonly Lock _gate = new();
    private readonly ConcurrentQueue<string> _invalidations = new();
    private readonly ILogger<SemanticEngine> _logger;
    private static VisualStudioInstance? _registeredMsBuildInstance;
    private static int _semanticLoadSequence;
    private static int _versionFactsLogged;
    private HashSet<ProjectId> _compiledProjects = [];
    private SemanticConfidenceLevel _confidence;
    private SemanticLoadRequest? _lastRequest;
    private IReadOnlyList<SemanticProjectInfo> _projects = [];
    private long _generation;
    private Solution? _solution;
    private SemanticRefreshInventory _refreshInventory = EmptyRefreshInventory;
    private MSBuildWorkspace? _workspace;

    /// <summary>Initializes a new instance of the <see cref="SemanticEngine"/> class.</summary>
    public SemanticEngine(
        IDomainEventStream events,
        ILogger<SemanticEngine> logger,
        TimeSpan? cancellationBackstop = null,
        SemanticResourceLimits? resourceLimits = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(logger);
        if (cancellationBackstop <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cancellationBackstop));
        }

        _resourceLimits = resourceLimits ?? new SemanticResourceLimits();
        _resourceLimits.Validate();
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
    public Task<SemanticLoadResult> LoadAsync(
        SemanticLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        return LoadCoreAsync(
            request,
            publishLoadCompleted: true,
            publishConfidenceChanged: true,
            allowTextFallback: true,
            cancellationToken);
    }

    /// <summary>Loads semantic state with explicit initial-load lifecycle publication control.</summary>
    public async Task<SemanticLoadResult> LoadCoreAsync(
        SemanticLoadRequest request,
        bool publishLoadCompleted,
        bool publishConfidenceChanged,
        bool allowTextFallback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RepositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SolutionPath);
        var selectionExtension = Path.GetExtension(request.SolutionPath);
        var isDirectProject = selectionExtension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || selectionExtension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            || selectionExtension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase);
        var measurement = new SemanticLoadMeasurementState(
            isDirectProject ? "project" : "solution",
            Interlocked.Increment(ref _semanticLoadSequence) == 1 ? "cold" : "warm");
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var callerCancellation = cancellationToken;
        long ownership;
        CancellationToken ownershipToken;
        lock (_gate)
        {
            ownership = _preparationOwnership;
            ownershipToken = _preparationOwnerLifetime.Token;
        }

        using var loadLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _engineLifetime.Token, ownershipToken);
        cancellationToken = loadLifetime.Token;
        var transitionAcquired = false;
        try
        {
            await _transition.WaitAsync(cancellationToken);
            transitionAcquired = true;
            lock (_gate)
            {
                if (ownership != _preparationOwnership)
                {
                    throw new InvalidOperationException("The semantic preparation owner changed before load admission.");
                }
            }

            return await LoadCoreImplementationAsync(
                request,
                publishLoadCompleted,
                publishConfidenceChanged,
                allowTextFallback,
                measurement,
                ownership,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            measurement.Outcome = "cancelled";
            if (callerCancellation.IsCancellationRequested)
            {
                throw new OperationCanceledException(callerCancellation);
            }

            throw;
        }
        finally
        {
            RecordSemanticLoadMeasurement(measurement);
            if (transitionAcquired)
            {
                _transition.Release();
            }
        }
    }

#pragma warning disable SA1202 // The private implementation keeps one measurement owner around the public load operation.
    private async Task<SemanticLoadResult> LoadCoreImplementationAsync(
        SemanticLoadRequest request,
        bool publishLoadCompleted,
        bool publishConfidenceChanged,
        bool allowTextFallback,
        SemanticLoadMeasurementState measurement,
        long ownership,
        CancellationToken cancellationToken)
    {
        var repositoryPath = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(request.RepositoryPath));
        var solutionPath = Path.GetFullPath(request.SolutionPath);
        if (!IsPathWithinRoot(solutionPath, repositoryPath)
            || !File.Exists(solutionPath))
        {
            throw new InvalidOperationException("The semantic solution must exist under the repository root.");
        }

        var isDirectProject = measurement.Mode == "project";
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
        measurement.ExpectedProjects = metadata.Length;
        var normalizedRequest = request with
        {
            RepositoryPath = repositoryPath,
            SolutionPath = solutionPath,
        };
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
                normalizedRequest,
                ownership,
                publishLoadCompleted,
                publishConfidenceChanged,
                cancellationToken);
            measurement.Outcome = "completed";
            measurement.LoadedProjects = textOnly.Length;
            measurement.FailedProjects = textOnly.Length;
            measurement.Confidence = textConfidence;
            return new SemanticLoadResult(
                request.WorkspaceId,
                Confidence,
                textOnly,
                ["MSBuild evaluation requires TrustedBuild; text-only project metadata was loaded."]);
        }

        var diagnostics = new ConcurrentQueue<string>();
        var evaluationStarted = Stopwatch.GetTimestamp();
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
                            _registeredMsBuildInstance = MSBuildLocator.RegisterDefaults();
                        }

                        LogSemanticToolchainVersionFacts();
                    }

                    var workspace = MSBuildWorkspace.Create(RoslynWorkspaceHost.Services);
                    workspace.LoadMetadataForReferencedProjects = true;
                    workspace.RegisterWorkspaceFailedHandler(eventArgs =>
                    {
                        diagnostics.Enqueue(
                            $"{eventArgs.Diagnostic.Kind}: {eventArgs.Diagnostic.Message}");
                        if (eventArgs.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                        {
                            measurement.IncrementWorkspaceFailure();
                        }
                    });
                    try
                    {
                        Solution solution;
                        if (isDirectProject)
                        {
                            var project = await workspace.OpenProjectAsync(
                                solutionPath,
                                measurement.Progress,
                                operationToken);
                            solution = project.Solution;
                        }
                        else
                        {
                            solution = await workspace.OpenSolutionAsync(
                                solutionPath,
                                measurement.Progress,
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
                cancellationToken,
                static abandoned => abandoned.Workspace.Dispose());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            measurement.EnsureWorkspaceFailure();
            _logger.LogWarning(
                exception,
                "Semantic MSBuild loading failed for solution {SolutionPath}; using text metadata",
                solutionPath);
            if (!allowTextFallback)
            {
                throw new InvalidOperationException(
                    "Semantic MSBuild refresh failed.",
                    exception);
            }

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
                normalizedRequest,
                ownership,
                publishLoadCompleted,
                publishConfidenceChanged,
                cancellationToken);
            measurement.LoadedProjects = degradedProjects.Length;
            measurement.FailedProjects = degradedProjects.Length;
            measurement.Confidence = degradedConfidence;
            return new SemanticLoadResult(
                request.WorkspaceId,
                degradedConfidence,
                degradedProjects,
                diagnostics.ToArray());
        }
        finally
        {
            measurement.EvaluationDuration = Stopwatch.GetElapsedTime(evaluationStarted);
        }

        using var workspaceLease = new WorkspaceLease(load.Workspace);
        var confinementStarted = Stopwatch.GetTimestamp();
        var confinedSolution = load.Solution;
        foreach (var project in confinedSolution.Projects.ToArray())
        {
            if (project.FilePath is null || !IsPathWithinRoot(project.FilePath, repositoryPath))
            {
                diagnostics.Enqueue(
                    $"Project '{project.Name}' was excluded because it is outside the repository root.");
                measurement.IncrementWorkspaceFailure();
                measurement.ExcludedProjects++;
                confinedSolution = confinedSolution.RemoveProject(project.Id);
                continue;
            }

            foreach (var document in project.Documents
                .Where(document => !IsSemanticInputPathAllowed(document.FilePath, normalizedRequest))
                .ToArray())
            {
                diagnostics.Enqueue(
                    $"Document '{document.Name}' was excluded by repository path policy.");
                measurement.IncrementWorkspaceFailure();
                confinedSolution = confinedSolution.RemoveDocument(document.Id);
            }

            foreach (var document in project.AdditionalDocuments
                .Where(document => IsRepositoryLocalSemanticInputRejected(
                    document.FilePath,
                    normalizedRequest))
                .ToArray())
            {
                diagnostics.Enqueue(
                    $"Additional document '{document.Name}' was excluded by repository path policy.");
                measurement.IncrementWorkspaceFailure();
                confinedSolution = confinedSolution.RemoveAdditionalDocument(document.Id);
            }

            foreach (var document in project.AnalyzerConfigDocuments
                .Where(document => IsRepositoryLocalSemanticInputRejected(
                    document.FilePath,
                    normalizedRequest))
                .ToArray())
            {
                diagnostics.Enqueue(
                    $"Analyzer config '{document.Name}' was excluded by repository path policy.");
                measurement.IncrementWorkspaceFailure();
                confinedSolution = confinedSolution.RemoveAnalyzerConfigDocument(document.Id);
            }
        }

        load = (load.Workspace, confinedSolution);
        measurement.ConfinementDuration = Stopwatch.GetElapsedTime(confinementStarted);

        var compilationStarted = Stopwatch.GetTimestamp();

        // Refresh admission needs a current usable generation, just like startup. Remaining
        // projects use the same bounded demand preparation and background warming path.
        var loadedProjects = load.Solution.Projects.Select(project =>
        {
            var info = metadata.FirstOrDefault(item => string.Equals(item.FilePath, project.FilePath, PathComparison));
            return info is null
                ? new SemanticProjectInfo(
                    project.Name,
                    project.FilePath ?? string.Empty,
                    [],
                    SemanticConfidenceLevel.ProjectGraphOnly,
                    project.ProjectReferences.Select(reference => load.Solution.GetProject(reference.ProjectId)?.Name ?? string.Empty).ToArray(),
                    [])
                : info with { Name = project.Name, Confidence = SemanticConfidenceLevel.ProjectGraphOnly };
        }).ToList();
        var loadedPaths = loadedProjects.Select(project => project.FilePath).ToHashSet(StringComparerForCurrentPlatform());
        loadedProjects.AddRange(metadata.Where(info => !loadedPaths.Contains(info.FilePath)));
        var refreshInventory = CreateRefreshInventory(load.Solution, normalizedRequest, cancellationToken);
        SemanticCompilationCoordinator? previousPreparation;
        lock (_gate)
        {
            if (ownership != _preparationOwnership)
            {
                throw new InvalidOperationException("The semantic preparation owner changed during evaluation.");
            }

            previousPreparation = _preparation;
            previousPreparation?.Abort();
        }

        var preparation = CreatePreparationCoordinator(load.Solution, new HashSet<ProjectId>());
        var previousState = CaptureReplacementState();
        try
        {
            if (previousPreparation is not null)
            {
                await previousPreparation.DisposeAsync();
            }

            previousState = CaptureReplacementState();
            await _preparationPublication.WaitAsync(cancellationToken);
            try
            {
                lock (_gate)
                {
                    if (ownership != _preparationOwnership)
                    {
                        throw new InvalidOperationException("The semantic preparation owner changed before load publication.");
                    }

                    _workspace = load.Workspace;
                    _solution = load.Solution;
                    _refreshInventory = refreshInventory;
                    _compiledProjects = [];
                    _projects = loadedProjects;
                    _confidence = SemanticConfidenceLevel.ProjectGraphOnly;
                    _lastRequest = normalizedRequest;
                    _preparation = preparation;
                    _preparationDiagnostics.Clear();
                    _preparationFailures = 0;
                    _preparationSlowSamples.Clear();
                    _preparationHasWorkspaceFailures = measurement.WorkspaceFailureCount > 0;
                    _expectedPreparationPaths = metadata.Select(info => info.FilePath).ToArray();
                    _initialPublication = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _allowPreparationEvents = false;
                    _pendingInitialResult = null;
                    _generation++;
                    _solutionGeneration = _generation;
                }
            }
            finally
            {
                _preparationPublication.Release();
            }

            var ranked = RankReadinessProjects(load.Solution, solutionPath);
            var pending = new List<Task<SemanticPreparationOutcome>>();
            var next = 0;
            while (next < ranked.Length || pending.Count > 0)
            {
                while (pending.Count < _preparationLimits.Frontier && next < ranked.Length)
                {
                    pending.Add(preparation.PrepareAsync(ranked[next++], demand: true, cancellationToken));
                }

                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);
                if ((await completed).Succeeded)
                {
                    break;
                }
            }

            lock (_gate)
            {
                if (ownership != _preparationOwnership)
                {
                    throw new InvalidOperationException("The semantic preparation owner changed during loading.");
                }
            }
        }
        catch
        {
            await preparation.DisposeAsync();
            RestoreReplacementState(previousState, preparation, ownership);
            throw;
        }

        workspaceLease.TransferOwnership();
        if (!ReferenceEquals(previousState.Workspace, load.Workspace))
        {
            previousState.Workspace?.Dispose();
        }

        var aggregate = Confidence;
        var initialFailures = _preparationFailures;
        var result = new SemanticLoadResult(
            request.WorkspaceId,
            aggregate,
            Projects,
            diagnostics.Concat(_preparationDiagnostics).ToArray());
        lock (_gate)
        {
            if (ownership != _preparationOwnership)
            {
                throw new InvalidOperationException("The semantic preparation owner changed before initial publication.");
            }

            _pendingInitialResult = result;
            _pendingInitialOwnership = ownership;
        }

        try
        {
            if (publishConfidenceChanged && previousState.Confidence != aggregate)
            {
                await _events.PublishAsync(new SemanticConfidenceChanged(request.SessionId, DateTimeOffset.UtcNow, aggregate.ToString()), cancellationToken);
            }

            if (publishLoadCompleted)
            {
                await _events.PublishAsync(new SemanticLoadCompleted(request.SessionId, DateTimeOffset.UtcNow, request.WorkspaceId, aggregate.ToString()), cancellationToken);
                await CompleteInitialPublicationAsync(result, succeeded: true, cancellationToken);
            }
            else if (publishConfidenceChanged)
            {
                // A full refresh owns its confidence publication rather than an initial-load
                // lifecycle pair. Release preparation after that publication succeeds.
                await CompleteInitialPublicationAsync(result, succeeded: true, cancellationToken);
            }
        }
        catch
        {
            await CompleteInitialPublicationAsync(result, succeeded: false, CancellationToken.None);
            throw;
        }

        measurement.CompilationDuration = Stopwatch.GetElapsedTime(compilationStarted);
        measurement.Outcome = "completed";
        measurement.LoadedProjects = loadedProjects.Count;
        measurement.CompiledProjects = result.Projects.Count(info => info.Confidence == SemanticConfidenceLevel.FullSemantic);
        measurement.FailedProjects = initialFailures;
        lock (_gate)
        {
            measurement.SlowProjectSamples.AddRange(_preparationSlowSamples);
        }

        measurement.Confidence = aggregate;
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SymbolResult>> FindSymbolsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var preparedSolution = await EnsurePreparedAsync(null, "symbols", requireSuccess: true, cancellationToken);
        (var solution, var compiledProjects, var confidence, var _) = CaptureSemanticState(preparedSolution);
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

        EnsurePreparedSolutionCurrent(preparedSolution);
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
                && inspectedEntries < _resourceLimits.MaximumFallbackEntries
                && inspectedFiles < _resourceLimits.MaximumFallbackFiles
                && fallback.Count < _resourceLimits.MaximumFallbackMatches)
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
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        _logger.LogDebug(
                            "Skipping inaccessible semantic fallback directory {Directory}: {ErrorType}",
                            directory,
                            exception.GetType().Name);
                    }

                    continue;
                }

                foreach (var entry in entries)
                {
                    if (++inspectedEntries > _resourceLimits.MaximumFallbackEntries)
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
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug(
                                "Skipping inaccessible semantic fallback entry {Path}: {ErrorType}",
                                entry,
                                exception.GetType().Name);
                        }

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
                        || ++inspectedFiles > _resourceLimits.MaximumFallbackFiles
                        || new FileInfo(entry).Length > _resourceLimits.MaximumFallbackFileBytes)
                    {
                        continue;
                    }

                    using var reader = new StreamReader(entry);
                    var lineNumber = 0;
                    while (fallback.Count < _resourceLimits.MaximumFallbackMatches
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

        var preparedSolution = await EnsurePreparedAsync(null, "references", requireSuccess: true, cancellationToken);
        (var solution, var _, var currentConfidence, var _) = CaptureSemanticState(preparedSolution);
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

        EnsurePreparedSolutionCurrent(preparedSolution);
        return results;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImplementationResult>> FindImplementationsAsync(
        string symbolId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        var preparedSolution = await EnsurePreparedAsync(null, "implementations", requireSuccess: true, cancellationToken);
        (var solution, var _, var confidence, var _) = CaptureSemanticState(preparedSolution);
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

        EnsurePreparedSolutionCurrent(preparedSolution);
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
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _engineLifetime.Token);
        await _transition.WaitAsync(lifetime.Token);
        try
        {
            return await ApplyInvalidationsCoreAsync(lifetime.Token);
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task<SemanticConfidenceLevel> ApplyInvalidationsCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_invalidations.IsEmpty)
        {
            return Confidence;
        }

        SemanticLoadRequest? request;
        SemanticConfidenceLevel previous;
        SemanticCompilationCoordinator? preparation;

        // Cancellation before publication ownership must retain both queued invalidations and preparation.
        await _preparationPublication.WaitAsync(cancellationToken);
        try
        {
            var changed = false;
            while (_invalidations.TryDequeue(out _))
            {
                changed = true;
            }

            if (!changed)
            {
                return Confidence;
            }

            lock (_gate)
            {
                preparation = _preparation;
                preparation?.Abort();
                _preparation = null;
                request = _lastRequest;
                previous = _confidence;
                _confidence = SemanticConfidenceLevel.ProjectGraphOnly;
                _compiledProjects = [];
                _generation++;
                _solutionGeneration = _generation;
            }
        }
        finally
        {
            _preparationPublication.Release();
        }

        if (preparation is not null)
        {
            await preparation.DisposeAsync();
        }

        if (request is not null && previous != SemanticConfidenceLevel.ProjectGraphOnly)
        {
            await _events.PublishAsync(
                new SemanticConfidenceChanged(
                    request.SessionId,
                    DateTimeOffset.UtcNow,
                    nameof(SemanticConfidenceLevel.ProjectGraphOnly)),
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

    /// <summary>Reloads the last semantic selection without publishing an initial-load completion.</summary>
    public Task<SemanticLoadResult> RefreshFullAsync(CancellationToken cancellationToken = default)
    {
        SemanticLoadRequest request;
        lock (_gate)
        {
            request = _lastRequest
                ?? throw new InvalidOperationException("No semantic solution has been loaded.");
        }

        return LoadCoreAsync(
            request,
            publishLoadCompleted: false,
            publishConfidenceChanged: true,
            allowTextFallback: false,
            cancellationToken);
    }

    /// <summary>Atomically publishes replacement text for proven existing loaded documents.</summary>
    public async Task<SemanticLoadResult> RefreshDocumentsAsync(
        IReadOnlyList<SemanticDocumentRefresh> documents,
        CancellationToken cancellationToken = default)
    {
        long ownership;
        CancellationToken ownershipToken;
        lock (_gate)
        {
            ownership = _preparationOwnership;
            ownershipToken = _preparationOwnerLifetime.Token;
        }

        using var refreshLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _engineLifetime.Token, ownershipToken);
        await _transition.WaitAsync(refreshLifetime.Token);
        try
        {
            lock (_gate)
            {
                if (ownership != _preparationOwnership)
                {
                    throw new InvalidOperationException("The semantic preparation owner changed before refresh admission.");
                }
            }

            return await RefreshDocumentsCoreAsync(documents, ownership, refreshLifetime.Token);
        }
        finally
        {
            _transition.Release();
        }
    }

    private async Task<SemanticLoadResult> RefreshDocumentsCoreAsync(
        IReadOnlyList<SemanticDocumentRefresh> documents,
        long ownership,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        cancellationToken.ThrowIfCancellationRequested();
        SemanticCompilationCoordinator? previousPreparation;
        lock (_gate)
        {
            previousPreparation = _preparation;
        }

        Solution solution;
        HashSet<ProjectId> compiledProjects;
        IReadOnlyList<SemanticProjectInfo> projects;
        SemanticLoadRequest request;
        SemanticConfidenceLevel confidence;
        long generation;
        lock (_gate)
        {
            solution = _solution
                ?? throw new InvalidOperationException("No compiler-aware semantic solution has been loaded.");
            compiledProjects = [.. _compiledProjects];
            projects = _projects.ToArray();
            request = _lastRequest
                ?? throw new InvalidOperationException("No semantic solution has been loaded.");
            confidence = _confidence;
            generation = _generation;
        }

        var documentsByPath = CreateDocumentsByPath(solution);
        var replacement = solution;
        var affectedProjects = new HashSet<ProjectId>();
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(document.Path);
            if (!IsPathWithinRoot(fullPath, request.RepositoryPath)
                || !IsCSharpSourcePath(fullPath)
                || !documentsByPath.TryGetValue(fullPath, out var documentIds))
            {
                throw new InvalidOperationException(
                    "Incremental semantic refresh requires an existing loaded C# document.");
            }

            foreach (var documentId in documentIds)
            {
                replacement = replacement.WithDocumentText(
                    documentId,
                    SourceText.From(document.Text, Encoding.UTF8),
                    PreservationMode.PreserveIdentity);
                affectedProjects.Add(documentId.ProjectId);
            }
        }

        // Validate replacement inputs before obsoleting useful shared work.
        lock (_gate)
        {
            if (ownership != _preparationOwnership)
            {
                throw new InvalidOperationException("The semantic preparation owner changed during refresh validation.");
            }

            previousPreparation?.Abort();
        }

        if (previousPreparation is not null)
        {
            await previousPreparation.DisposeAsync();
        }

        lock (_gate)
        {
            if (ownership != _preparationOwnership || !ReferenceEquals(_solution, solution))
            {
                throw new InvalidOperationException("The semantic solution changed during refresh validation.");
            }

            compiledProjects = [.. _compiledProjects];
            projects = _projects.ToArray();
            confidence = _confidence;
            generation = _generation;
        }

        var graph = replacement.GetProjectDependencyGraph();
        foreach (var id in affectedProjects.ToArray())
        {
            affectedProjects.UnionWith(graph.GetProjectsThatTransitivelyDependOnThisProject(id));
        }

        var required = affectedProjects.Where(compiledProjects.Contains).ToHashSet();
        var retained = compiledProjects.Except(affectedProjects).ToHashSet();
        var preparation = CreatePreparationCoordinator(replacement, retained);
        try
        {
            foreach (var id in required)
            {
                var outcome = await preparation.PrepareAsync(id, demand: true, cancellationToken);
                if (!outcome.Succeeded || outcome.Obsolete)
                {
                    throw new InvalidOperationException("Incremental semantic refresh could not prepare required affected coverage.");
                }
            }

            lock (_gate)
            {
                if (ownership != _preparationOwnership || _generation != generation || !ReferenceEquals(_solution, solution))
                {
                    throw new InvalidOperationException("The semantic workspace changed while refresh was being prepared.");
                }

                // Incremental replacement retains the lifecycle owner's outstanding publication barrier.
                // Only document text changes, so immutable refresh membership remains valid.
                _solution = replacement;
                _preparation = preparation;
                _generation++;
                _solutionGeneration = _generation;
            }
        }
        catch
        {
            await preparation.DisposeAsync();
            SemanticCompilationCoordinator? recovered;
            lock (_gate)
            {
                recovered = ownership == _preparationOwnership ? CreatePreparationCoordinator(solution, compiledProjects) : null;
                if (recovered is not null)
                {
                    _preparation = recovered;
                    if (_allowPreparationEvents)
                    {
                        recovered.Warm(RankReadinessProjects(solution, request.SolutionPath));
                        _warmObservation = ObserveWarmAsync(recovered);
                    }
                }
            }

            throw;
        }

        lock (_gate)
        {
            if (_allowPreparationEvents)
            {
                preparation.Warm(RankReadinessProjects(replacement, request.SolutionPath));
                _warmObservation = ObserveWarmAsync(preparation);
            }
        }

        return new SemanticLoadResult(request.WorkspaceId, confidence, projects, []);
    }

    /// <summary>Gets normalized paths for the currently loaded source documents.</summary>
    public IReadOnlySet<string> GetLoadedDocumentPaths()
    {
        lock (_gate)
        {
            if (_solution is null || _lastRequest is null)
            {
                return new HashSet<string>(StringComparerForCurrentPlatform());
            }

            return GetTextDocumentPaths(
                _solution.Projects.SelectMany(project => project.Documents),
                _lastRequest);
        }
    }

    /// <summary>Gets the exact Roslyn document inventory used for refresh classification.</summary>
    public SemanticRefreshInventory GetRefreshInventory()
    {
        lock (_gate)
        {
            return _refreshInventory;
        }
    }

    /// <summary>Captures current loaded source text identities for refresh no-op suppression.</summary>
    public async Task<IReadOnlyList<SemanticDocumentRefresh>> GetLoadedDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        Solution? solution;
        SemanticLoadRequest? request;
        lock (_gate)
        {
            solution = _solution;
            request = _lastRequest;
        }

        if (solution is null || request is null)
        {
            return [];
        }

        var documents = new List<SemanticDocumentRefresh>();
        foreach (var document in solution.Projects
            .SelectMany(project => project.Documents
                .Where(document => IsSemanticRefreshInputPathAllowed(document.FilePath, request))
                .Cast<TextDocument>()
                .Concat(project.AdditionalDocuments
                    .Where(document => IsSemanticRefreshInputPathAllowed(document.FilePath, request)))
                .Concat(project.AnalyzerConfigDocuments
                    .Where(document => IsSemanticRefreshInputPathAllowed(document.FilePath, request))))
            .GroupBy(
                document => Path.GetFullPath(document.FilePath ?? string.Empty),
                StringComparerForCurrentPlatform())
            .Select(group => group.First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = await document.GetTextAsync(cancellationToken);
            var content = text.ToString();
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            documents.Add(new SemanticDocumentRefresh(
                Path.GetFullPath(document.FilePath ?? string.Empty),
                content,
                identity));
        }

        return documents;
    }

    /// <summary>Gets fast compiler diagnostics from the loaded Roslyn solution.</summary>
    public async Task<IReadOnlyList<Threadsmith.Core.Diagnostic>> GetDiagnosticsAsync(
        IReadOnlyList<string> projectPaths,
        IReadOnlyList<string> changedFiles,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectPaths);
        ArgumentNullException.ThrowIfNull(changedFiles);
        SemanticPreparationReceipt? preparedSolution = null;
        if (projectPaths.Count == 0)
        {
            preparedSolution = await EnsurePreparedAsync(null, "diagnostics", requireSuccess: true, cancellationToken);
        }
        else
        {
            foreach (var projectPath in projectPaths)
            {
                var current = await EnsurePreparedAsync(projectPath, "diagnostics", requireSuccess: true, cancellationToken);
                if (preparedSolution is not null && preparedSolution != current)
                {
                    throw new InvalidOperationException("The semantic solution changed during diagnostic preparation.");
                }

                preparedSolution = current;
            }
        }

        (var solution, var compiledProjects, var confidence, var repositoryPath) =
            CaptureSemanticState(preparedSolution);
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

            var compilation = await RunPreparedOperationAsync<Compilation?>(
                preparedSolution,
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

        EnsurePreparedSolutionCurrent(preparedSolution);
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
        SemanticPreparationReceipt? preparationReceipt = null;
        if (solution is not null && confidence >= SemanticConfidenceLevel.PartialCompilation)
        {
            var owners = solution.Projects.Where(project => project.Documents.Any(document => document.FilePath is not null
                && sourceByFullPath.ContainsKey(Path.GetFullPath(document.FilePath)))).Select(project => project.Id).ToHashSet();
            owners.UnionWith(sourceByFullPath.Keys.SelectMany(path => FindContainingProjects(solution, path).Select(project => project.Id)));
            var dependencyGraph = solution.GetProjectDependencyGraph();
            foreach (var id in owners.ToArray())
            {
                owners.UnionWith(dependencyGraph.GetProjectsThatThisProjectTransitivelyDependsOn(id));
            }

            preparationReceipt = await EnsureProjectsPreparedAsync(solution, owners, "pre-mutation", requireSuccess: true, cancellationToken);
            (solution, compiledProjects, confidence, loadedRepository) = TryCaptureSemanticState(preparationReceipt);
        }

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
                    var compilation = await RunPreparedOperationAsync<Compilation?>(
                        preparationReceipt,
                        project.GetCompilationAsync,
                        cancellationToken);
                    if (compilation is null)
                    {
                        omissions.Add(ModelVisibleStructuredFact.Exact(
                            $"Compilation diagnostics were unavailable for project '{project.Name}'."));
                        continue;
                    }

                    var targetFramework = project.FilePath is null || !File.Exists(project.FilePath)
                        ? string.Empty
                        : ReadProjectInfo(project.FilePath).TargetFrameworks.FirstOrDefault() ?? string.Empty;
                    var baselineDiagnostics = await GetBaselineCompilationDiagnosticFingerprintsAsync(
                        solution,
                        preparationReceipt,
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
                omissions.Add(ModelVisibleStructuredFact.Exact(
                    "Compilation diagnostics were skipped because syntax diagnostics blocked compilation."));
            }
            else
            {
                omissions.Add(ModelVisibleStructuredFact.Exact(
                    $"Semantic and compilation pre-mutation checks require PartialCompilation confidence; current confidence is {confidence}."));
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
        EnsurePreparedSolutionCurrent(preparationReceipt);
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
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _engineLifetime.CancelAsync();
        await _transition.WaitAsync();
        SemanticCompilationCoordinator? preparation;
        MSBuildWorkspace? workspace;
        lock (_gate)
        {
            preparation = _preparation;
            _preparation = null;
            _initialPublication.TrySetCanceled();
            workspace = _workspace;
            _workspace = null;
            _solution = null;
            _refreshInventory = EmptyRefreshInventory;
            _compiledProjects = [];
        }

        if (preparation is not null)
        {
            await preparation.DisposeAsync();
        }

#pragma warning disable VSTHRD003 // The engine owns its terminal warming observation.
        await _warmObservation;
#pragma warning restore VSTHRD003
        workspace?.Dispose();
        _preparationOwnerLifetime.Dispose();
        _transition.Release();
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
                _lastRequest?.SolutionPath,
                _generation,
                _solutionGeneration);
        }
    }

    /// <summary>Captures immutable Roslyn references for one fenced advanced semantic query.</summary>
    internal AdvancedSemanticSnapshot CaptureAdvancedSnapshot(SemanticPreparationReceipt? preparedSolution = null)
    {
        lock (_gate)
        {
            CheckPreparedSolution(preparedSolution);
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
                _lastRequest.SolutionPath,
                _generation);
        }
    }

    /// <summary>Returns whether a captured advanced-query generation is still current.</summary>
    internal bool IsCurrentGeneration(long generation)
    {
        lock (_gate)
        {
            // Coverage promotion does not change immutable source identity. Source replacements reset the floor.
            return generation >= _solutionGeneration && generation <= _generation;
        }
    }

    /// <summary>Captures immutable Roslyn references for one serialized semantic mutation turn.</summary>
    internal SemanticMutationSnapshot CaptureMutationSnapshot(SemanticPreparationReceipt? preparedSolution = null)
    {
        lock (_gate)
        {
            CheckPreparedSolution(preparedSolution);
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

    private static IReadOnlySet<string> GetTextDocumentPaths(
        IEnumerable<TextDocument> documents,
        SemanticLoadRequest request)
    {
        return documents
            .Select(document => document.FilePath)
            .Where(path => IsSemanticInputPathAllowed(path, request))
            .Select(path => Path.GetFullPath(path ?? string.Empty))
            .ToHashSet(StringComparerForCurrentPlatform());
    }

    private static SemanticRefreshInventory CreateRefreshInventory(
        Solution? solution,
        SemanticLoadRequest request,
        CancellationToken cancellationToken)
    {
        if (solution is null)
        {
            return EmptyRefreshInventory;
        }

        // Membership belongs to this solution publication. Callbacks only look up paths;
        // filesystem safety is revalidated at the boundary that actually reads an input.
        return new SemanticRefreshInventory(
            GetRefreshDocumentPaths(solution.Projects.SelectMany(project => project.Documents), request, cancellationToken),
            GetRefreshDocumentPaths(solution.Projects.SelectMany(project => project.AdditionalDocuments), request, cancellationToken),
            GetRefreshDocumentPaths(solution.Projects.SelectMany(project => project.AnalyzerConfigDocuments), request, cancellationToken),
            GetReferencePaths(solution, request, cancellationToken));
    }

    private static IReadOnlySet<string> GetRefreshDocumentPaths(
        IEnumerable<TextDocument> documents,
        SemanticLoadRequest request,
        CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(StringComparerForCurrentPlatform());
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSemanticRefreshInputPathAllowed(document.FilePath, request))
            {
                paths.Add(Path.GetFullPath(document.FilePath ?? string.Empty));
            }
        }

        return paths.ToFrozenSet(StringComparerForCurrentPlatform());
    }

    private static IReadOnlySet<string> GetReferencePaths(
        Solution solution,
        SemanticLoadRequest request,
        CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(StringComparerForCurrentPlatform());
        foreach (var path in solution.Projects
            .SelectMany(project => project.AnalyzerReferences
                .Select(reference => reference.FullPath)
                .Concat(project.MetadataReferences
                    .OfType<PortableExecutableReference>()
                    .Select(reference => reference.FilePath))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSemanticInputPathAllowed(path, request))
            {
                paths.Add(Path.GetFullPath(path ?? string.Empty));
            }
        }

        return paths.ToFrozenSet(StringComparerForCurrentPlatform());
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
        TryCaptureSemanticState(SemanticPreparationReceipt? preparedSolution = null)
    {
        lock (_gate)
        {
            CheckPreparedSolution(preparedSolution);
            return _solution is null || _lastRequest is null
                ? (null, [], _confidence, string.Empty)
                : (_solution, [.. _compiledProjects], _confidence, _lastRequest.RepositoryPath);
        }
    }

    private async Task<Dictionary<string, int>> GetBaselineCompilationDiagnosticFingerprintsAsync(
        Solution solution,
        SemanticPreparationReceipt? preparationReceipt,
        ProjectId projectId,
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var baselineProject = solution.GetProject(projectId);
        if (baselineProject is null)
        {
            return [];
        }

        var baselineCompilation = await RunPreparedOperationAsync<Compilation?>(
            preparationReceipt,
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

    private static bool IsRepositoryLocalSemanticInputRejected(
        string? path,
        SemanticLoadRequest request)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return IsPathWithinRoot(fullPath, request.RepositoryPath)
                && !IsSemanticInputPathAllowed(fullPath, request);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return true;
        }
    }

    private static bool IsSemanticInputPathAllowed(
        string? path,
        SemanticLoadRequest request)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }

        if (!IsPathWithinRoot(fullPath, request.RepositoryPath))
        {
            return false;
        }

        if (SemanticPathSafety.HasReparseComponent(request.RepositoryPath, fullPath))
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(request.RepositoryPath, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        return !RepositoryPathPolicy.IsProhibited(
            relativePath,
            request.ProhibitedPaths ?? []);
    }

    private static bool IsSemanticRefreshInputPathAllowed(
        string? path,
        SemanticLoadRequest request)
    {
        if (!IsSemanticInputPathAllowed(path, request))
        {
            return false;
        }

        return !SemanticRefreshPathPolicy.IsIgnoredGeneratedDocument(
            request.RepositoryPath,
            Path.GetFullPath(path ?? string.Empty));
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
        CaptureSemanticState(SemanticPreparationReceipt? preparedSolution = null)
    {
        lock (_gate)
        {
            CheckPreparedSolution(preparedSolution);
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

    private static async Task<Solution> RefreshChangedDocumentsAsync(
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
                        var document = refreshed.GetDocument(documentId);
                        if (document is not null
                            && (await document.GetTextAsync(cancellationToken)).ContentEquals(sourceText))
                        {
                            continue;
                        }

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

        // Diagnostic overlays do not publish source state. The refresh coordinator owns replacement generations.
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
        SemanticLoadRequest request,
        long ownership,
        bool publishLoadCompleted,
        bool publishConfidenceChanged,
        CancellationToken cancellationToken,
        Action? stateCommitted = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var refreshInventory = CreateRefreshInventory(solution, request, cancellationToken);
        MSBuildWorkspace? previousWorkspace;
        SemanticCompilationCoordinator? previousPreparation;
        SemanticConfidenceLevel previousConfidence;
        await _preparationPublication.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (ownership != _preparationOwnership)
                {
                    throw new InvalidOperationException("The semantic preparation owner changed before metadata publication.");
                }

                previousWorkspace = _workspace;
                previousPreparation = _preparation;
                previousPreparation?.Abort();
                _preparation = null;
                _pendingInitialResult = null;
                _initialPublication.TrySetCanceled();
                previousConfidence = _confidence;
                _workspace = workspace;
                _solution = solution;
                _refreshInventory = refreshInventory;
                _compiledProjects = compiledProjects;
                _projects = projects;
                _confidence = confidence;
                _lastRequest = request;
                _generation++;
                _solutionGeneration = _generation;
                stateCommitted?.Invoke();
            }
        }
        finally
        {
            _preparationPublication.Release();
        }

        if (previousPreparation is not null)
        {
            await previousPreparation.DisposeAsync();
        }

        if (!ReferenceEquals(previousWorkspace, workspace))
        {
            previousWorkspace?.Dispose();
        }

        if (publishConfidenceChanged && previousConfidence != confidence)
        {
            await _events.PublishAsync(
                new SemanticConfidenceChanged(
                    request.SessionId,
                    DateTimeOffset.UtcNow,
                    confidence.ToString()),
                cancellationToken);
        }

        if (publishLoadCompleted)
        {
            await _events.PublishAsync(
                new SemanticLoadCompleted(
                    request.SessionId,
                    DateTimeOffset.UtcNow,
                    request.WorkspaceId,
                    confidence.ToString()),
                cancellationToken);
        }
    }

#pragma warning restore SA1202
    private static void AddSlowProjectSample(
        List<ProjectCompilationSample> samples,
        string projectName,
        TimeSpan duration)
    {
        samples.Add(new ProjectCompilationSample(projectName, duration));
        samples.Sort(static (left, right) => right.Duration.CompareTo(left.Duration));
        if (samples.Count > MaximumSlowProjectSamples)
        {
            samples.RemoveAt(samples.Count - 1);
        }
    }

    private static string GetInformationalVersion(Assembly assembly)
    {
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unknown";
    }

    private static string SanitizeProjectName(string projectName)
    {
        const int maximumLength = 64;
        var length = Math.Min(projectName.Length, maximumLength);
        var sanitized = new StringBuilder(length);
        for (var index = 0; index < length; index++)
        {
            var character = projectName[index];
            sanitized.Append(char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                ? character
                : '_');
        }

        return sanitized.ToString();
    }

    private static IReadOnlyList<string> ValidateAnalyzerReferences(
        Project project,
        HashSet<object> validatedReferences,
        CancellationToken cancellationToken)
    {
        var failedReferences = new List<string>(MaximumSlowProjectSamples);
        foreach (var reference in project.AnalyzerReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!validatedReferences.Add(reference.Id))
            {
                continue;
            }

            var analyzerLoadFailed = false;
            EventHandler<AnalyzerLoadFailureEventArgs>? loadFailureHandler = null;
            if (reference is AnalyzerFileReference fileReference)
            {
                loadFailureHandler = (_, _) => analyzerLoadFailed = true;
                fileReference.AnalyzerLoadFailed += loadFailureHandler;
            }

            try
            {
                var analyzers = reference.GetAnalyzersForAllLanguages();
                var generators = reference.GetGeneratorsForAllLanguages();
                if (analyzerLoadFailed)
                {
                    AddFailedAnalyzerReference(failedReferences, reference.Display);
                }
                else if (analyzers.IsEmpty && generators.IsEmpty)
                {
                    if (string.IsNullOrWhiteSpace(reference.FullPath)
                        || !File.Exists(reference.FullPath))
                    {
                        AddFailedAnalyzerReference(failedReferences, reference.Display);
                        continue;
                    }

                    _ = AssemblyName.GetAssemblyName(reference.FullPath);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                AddFailedAnalyzerReference(failedReferences, reference.Display);
            }
            finally
            {
                if (reference is AnalyzerFileReference fileReferenceForCleanup
                    && loadFailureHandler is not null)
                {
                    fileReferenceForCleanup.AnalyzerLoadFailed -= loadFailureHandler;
                }
            }
        }

        return failedReferences;
    }

    private static void AddFailedAnalyzerReference(List<string> failedReferences, string? display)
    {
        if (failedReferences.Count >= MaximumSlowProjectSamples)
        {
            return;
        }

        failedReferences.Add(SanitizeProjectName(Path.GetFileNameWithoutExtension(display ?? "unknown")));
    }

    private void LogSemanticToolchainVersionFacts()
    {
        if (Interlocked.Exchange(ref _versionFactsLogged, 1) != 0)
        {
            return;
        }

        var msBuildAssembly = Assembly.Load(new AssemblyName("Microsoft.Build"));
        _logger.LogDebug(
            "Semantic toolchain selected: SDK/MSBuild instance {InstanceVersion}; MSBuild {MSBuildVersion}; discovery {DiscoveryType}; Roslyn compiler {CompilerVersion}; Roslyn workspaces {WorkspacesVersion}",
            _registeredMsBuildInstance?.Version.ToString() ?? "externally-registered",
            GetInformationalVersion(msBuildAssembly),
            _registeredMsBuildInstance?.DiscoveryType.ToString() ?? "external",
            GetInformationalVersion(typeof(Compilation).Assembly),
            GetInformationalVersion(typeof(MSBuildWorkspace).Assembly));
    }

    private void RecordSemanticLoadMeasurement(SemanticLoadMeasurementState measurement)
    {
        var totalDuration = Stopwatch.GetElapsedTime(measurement.TotalStarted);
        var workingSetBytes = Environment.WorkingSet;
        SemanticLoadMetrics.Record(
            measurement.Mode,
            measurement.Temperature,
            measurement.Outcome,
            measurement.Confidence,
            totalDuration,
            measurement.EvaluationDuration,
            measurement.ConfinementDuration,
            measurement.CompilationDuration,
            measurement.ExpectedProjects,
            measurement.LoadedProjects,
            measurement.ExcludedProjects,
            measurement.FailedProjects,
            measurement.CompiledProjects,
            measurement.WorkspaceFailureCount,
            workingSetBytes);
        _logger.LogInformation(
            "Semantic load {Outcome}: mode {Mode}; temperature {Temperature}; total {TotalMs} ms; evaluation {EvaluationMs} ms; progress {ProgressOperations}; confinement {ConfinementMs} ms; compilation {CompilationMs} ms; projects expected {ExpectedProjects}, loaded {LoadedProjects}, excluded {ExcludedProjects}, failed {FailedProjects}, compiled {CompiledProjects}; workspace failures {WorkspaceFailures}; confidence {Confidence}; working set {WorkingSetBytes} bytes",
            measurement.Outcome,
            measurement.Mode,
            measurement.Temperature,
            totalDuration.TotalMilliseconds,
            measurement.EvaluationDuration.TotalMilliseconds,
            measurement.Progress.Summary,
            measurement.ConfinementDuration.TotalMilliseconds,
            measurement.CompilationDuration.TotalMilliseconds,
            measurement.ExpectedProjects,
            measurement.LoadedProjects,
            measurement.ExcludedProjects,
            measurement.FailedProjects,
            measurement.CompiledProjects,
            measurement.WorkspaceFailureCount,
            measurement.Confidence,
            workingSetBytes);
        if (_logger.IsEnabled(LogLevel.Debug) && measurement.SlowProjectSamples.Count > 0)
        {
            var summary = string.Join(
                ", ",
                measurement.SlowProjectSamples.Select(sample =>
                    $"{sample.ProjectName}={sample.Duration.TotalMilliseconds:F3}ms"));
            _logger.LogDebug("Slowest semantic project compilations: {SlowProjects}", summary);
        }
    }

    private sealed class SemanticLoadMeasurementState(string mode, string temperature)
    {
        private int _workspaceFailureCount;

        public int CompiledProjects { get; set; }

        public TimeSpan CompilationDuration { get; set; }

        public SemanticConfidenceLevel Confidence { get; set; }

        public TimeSpan ConfinementDuration { get; set; }

        public int ExcludedProjects { get; set; }

        public int ExpectedProjects { get; set; }

        public int FailedProjects { get; set; }

        public int LoadedProjects { get; set; }

        public string Mode { get; } = mode;

        public string Outcome { get; set; } = "failed";

        public ProjectLoadProgressCollector Progress { get; } = new();

        public List<ProjectCompilationSample> SlowProjectSamples { get; } =
            new(MaximumSlowProjectSamples);

        public string Temperature { get; } = temperature;

        public long TotalStarted { get; } = Stopwatch.GetTimestamp();

        public TimeSpan EvaluationDuration { get; set; }

        public int WorkspaceFailureCount => Volatile.Read(ref _workspaceFailureCount);

        public void EnsureWorkspaceFailure()
        {
            _ = Interlocked.CompareExchange(ref _workspaceFailureCount, 1, 0);
        }

        public void IncrementWorkspaceFailure()
        {
            _ = Interlocked.Increment(ref _workspaceFailureCount);
        }
    }

    private sealed class ProjectLoadProgressCollector : IProgress<ProjectLoadProgress>
    {
        private readonly ConcurrentDictionary<ProjectLoadOperation, int> _operations = new();

        public string Summary => string.Join(
            ",",
            _operations
                .OrderBy(static pair => pair.Key)
                .Select(static pair => $"{pair.Key}={pair.Value}"));

        public void Report(ProjectLoadProgress value)
        {
            _operations.AddOrUpdate(value.Operation, 1, static (_, count) => count + 1);
        }
    }

    private sealed record ProjectCompilationSample(string ProjectName, TimeSpan Duration);

    private sealed class WorkspaceLease(MSBuildWorkspace workspace) : IDisposable
    {
        private MSBuildWorkspace? _workspace = workspace;

        public void TransferOwnership()
        {
            _workspace = null;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _workspace, null)?.Dispose();
        }
    }

    private async Task<T> RunNonCooperativeAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        Action<T>? abandonedResultCleanup = null)
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
                    var abandonedResult = await task;
                    abandonedResultCleanup?.Invoke(abandonedResult);
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
                    completedTask =>
                    {
                        if (completedTask.Status == TaskStatus.RanToCompletion)
                        {
                            try
                            {
                                abandonedResultCleanup?.Invoke(completedTask.Result);
                            }
                            catch (Exception exception)
                            {
                                _logger.LogWarning(
                                    exception,
                                    "Abandoned semantic operation cleanup failed after cancellation");
                            }
                        }
                        else if (completedTask.IsFaulted)
                        {
                            _logger.LogWarning(
                                completedTask.Exception,
                                "Abandoned semantic operation faulted after cancellation");
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            throw;
        }
    }

    private sealed record SemanticLocationContext(
        IReadOnlyDictionary<string, int> DocumentCounts,
        IReadOnlyDictionary<string, string> TargetFrameworks);
}

/// <summary>Prepared current text for one existing loaded semantic document.</summary>
public sealed record SemanticDocumentRefresh(string Path, string Text, string ContentIdentity);
