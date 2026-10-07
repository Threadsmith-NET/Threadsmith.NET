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
    private readonly IPromptLoader _prompts;
    private readonly IDomainEventStream _events;
    private readonly Lock _gate = new();
    private readonly ConcurrentQueue<string> _invalidations = new();
    private readonly ILogger<SemanticEngine> _logger;
    private readonly SemanticStartupProgress? _startupProgress;
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
        IPromptLoader prompts,
        TimeSpan? cancellationBackstop = null,
        SemanticResourceLimits? resourceLimits = null,
        SemanticStartupProgress? startupProgress = null)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(logger);
        if (cancellationBackstop <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cancellationBackstop));
        }

        _resourceLimits = resourceLimits ?? new SemanticResourceLimits();
        _resourceLimits.Validate();
        _prompts = prompts;
        _events = events;
        _logger = logger;
        _startupProgress = startupProgress;
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
        using var evaluationProgress = _startupProgress?.Begin(request.SessionId, SemanticStartupPhase.OpenWorkspace, cancellationToken);
        (MSBuildWorkspace Workspace, Solution Solution) load;
        MSBuildWorkspace? provisionalWorkspace = null;
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
                    provisionalWorkspace = workspace;
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
                },
                cancellationToken,
                abandoned => DisposeCompilerWorkspace(abandoned.Workspace),
                failedOperationCleanup: () =>
                {
                    if (provisionalWorkspace is { } failed)
                    {
                        DisposeCompilerWorkspace(failed);
                    }
                });
            evaluationProgress?.Complete();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            evaluationProgress?.Dispose();
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

        using var workspaceLease = new WorkspaceLease(load.Workspace, RetireCompilerWorkspace);
        var confinementStarted = Stopwatch.GetTimestamp();
        using var confinementProgress = _startupProgress?.Begin(request.SessionId, SemanticStartupPhase.ConfineInputs, cancellationToken);
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
        confinementProgress?.Complete();

        var compilationStarted = Stopwatch.GetTimestamp();
        using var compilationProgress = _startupProgress?.Begin(request.SessionId, SemanticStartupPhase.PrepareCompilation, cancellationToken);

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
                    ResetEditAnalysisInputs();
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
                    compilationProgress?.Complete();
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
            RetireCompilerWorkspace(previousState.Workspace);
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
        return await RunPreparedOperationAsync<IReadOnlyList<SymbolResult>>(
            preparedSolution,
            async operationToken =>
            {
                var symbols = new List<ISymbol>();
                foreach (var project in solution.Projects.Where(project => compiledProjects.Contains(project.Id)))
                {
                    var declarations = await SymbolFinder.FindDeclarationsAsync(
                        project,
                        query,
                        ignoreCase: true,
                        SymbolFilter.TypeAndMember,
                        operationToken);
                    symbols.AddRange(declarations);
                }

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
            },
            cancellationToken);
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
        return await RunPreparedOperationAsync<IReadOnlyList<ReferenceResult>>(
            preparedSolution,
            async operationToken =>
            {
                var symbol = await ResolveSymbolAsync(solution, symbolId, operationToken);
                var referencedSymbols = await SymbolFinder.FindReferencesAsync(symbol, solution, operationToken);
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
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImplementationResult>> FindImplementationsAsync(
        string symbolId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolId);
        var preparedSolution = await EnsurePreparedAsync(null, "implementations", requireSuccess: true, cancellationToken);
        (var solution, var _, var confidence, var _) = CaptureSemanticState(preparedSolution);
        return await RunPreparedOperationAsync<IReadOnlyList<ImplementationResult>>(
            preparedSolution,
            async operationToken =>
            {
                var symbol = await ResolveSymbolAsync(solution, symbolId, operationToken);
                var implementations = await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: operationToken);
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
            },
            cancellationToken);
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
                ResetEditAnalysisInputs();
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
            request = _lastRequest
                ?? throw new InvalidOperationException("No semantic solution has been loaded.");
        }

        var replacement = solution;
        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(document.Path);
            var documentIds = solution.GetDocumentIdsWithFilePath(fullPath);
            if (!IsPathWithinRoot(fullPath, request.RepositoryPath)
                || !IsCSharpSourcePath(fullPath)
                || documentIds.Length == 0)
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
            }
        }

        // Validate replacement inputs before obsoleting useful shared work.
        lock (_gate)
        {
            if (ownership != _preparationOwnership)
            {
                throw new InvalidOperationException("The semantic preparation owner changed during refresh validation.");
            }

            if (_editCandidate is { Promotable: true } candidate && ReferenceEquals(candidate.Base, solution)
                && candidate.Endpoints.Count == documents.Count
                && documents.All(document => candidate.Endpoints.TryGetValue(Path.GetFullPath(document.Path), out var identity)
                    && identity.Equals(document.ContentIdentity, StringComparison.OrdinalIgnoreCase)))
            {
                replacement = candidate.Solution;
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

        // Text replacement preserves evaluated project coverage. Roslyn invalidates dependent
        // compilations in the immutable solution and materializes them when a query or explicit
        // validation needs them; publishing source must not compile the downstream graph.
        var preparation = CreatePreparationCoordinator(replacement, compiledProjects);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                if (_editCandidate is { } candidate)
                {
                    if (ReferenceEquals(replacement, candidate.Solution))
                    {
                        candidate.Result = candidate.Result with { CommittedGeneration = _solutionGeneration, CandidateReused = true, Revision = candidate.Result.Revision + 1 };
                        Interlocked.Increment(ref _editCandidatePromotions);
                        SemanticLoadMetrics.EditCandidatePromotions.Add(1);
                        candidate.Promotion.TrySetResult();
                    }
                    else
                    {
                        candidate.Result = candidate.Result with { Obsolete = true, Pending = false, Revision = candidate.Result.Revision + 1 };
                        candidate.Cancel();
                    }
                }
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

        return await RunSnapshotOperationAsync<IReadOnlyList<SemanticDocumentRefresh>>(
            solution,
            async token =>
            {
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
                    token.ThrowIfCancellationRequested();
                    var text = await document.GetTextAsync(token);
                    var content = text.ToString();
                    var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
                    documents.Add(new SemanticDocumentRefresh(
                        Path.GetFullPath(document.FilePath ?? string.Empty),
                        content,
                        identity));
                }

                return documents;
            },
            cancellationToken);
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
        var refreshPaths = changedFiles.Concat(GetDiagnosticRefreshPaths(solution, compiledProjects, requestedPaths));
        return await RunPreparedOperationAsync<IReadOnlyList<Threadsmith.Core.Diagnostic>>(
            preparedSolution,
            async token =>
            {
                var diagnosticSolution = await RefreshChangedDocumentsAsync(solution, refreshPaths, repositoryPath, token);
                var diagnostics = new List<Threadsmith.Core.Diagnostic>();
                SemanticLocationContext? context = null;
                foreach (var project in diagnosticSolution.Projects)
                {
                    token.ThrowIfCancellationRequested();
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

                    var compilation = await project.GetCompilationAsync(token);
                    if (compilation is null)
                    {
                        continue;
                    }

                    context ??= CreateLocationContext(diagnosticSolution);
                    var targetFramework = project.FilePath is { } filePath
                        && context.TargetFrameworks.TryGetValue(filePath, out var framework)
                            ? framework
                            : string.Empty;
                    foreach (var diagnostic in compilation.GetDiagnostics(token))
                    {
                        var location = diagnostic.Location == Location.None
                            ? null
                            : CreateLocation(diagnosticSolution, diagnostic.Location, context);
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
            },
            cancellationToken);
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
            _editCandidate?.Cancel();
        }

        if (preparation is not null)
        {
            await preparation.DisposeAsync();
        }

#pragma warning disable VSTHRD003 // The engine owns its terminal warming observation.
        await _warmObservation;
#pragma warning restore VSTHRD003
        RetireCompilerWorkspace(workspace);
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
            return _disposed == 0
                && generation >= _solutionGeneration && generation <= _generation;
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
                _lastRequest.RepositoryPath,
                _generation);
        }
    }

    /// <summary>Rejects mutation results computed against superseded or disposed semantic inputs.</summary>
    internal void EnsureMutationSnapshotCurrent(SemanticMutationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsCurrentGeneration(snapshot.Generation))
        {
            throw new InvalidOperationException("Semantic inputs changed while the mutation was being computed. Read current semantic evidence before retrying.");
        }
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

    private static string ToRepositoryRelativePath(string repositoryPath, string fullPath)
    {
        var relative = Path.GetRelativePath(repositoryPath, fullPath);
        return relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
                ? fullPath
                : relative.Replace('\\', '/');
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

    private static async Task<ISymbol> ResolveSymbolAsync(
        Solution solution,
        string symbolId,
        CancellationToken cancellationToken)
    {
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                continue;
            }

            var symbol = DocumentationCommentId.GetFirstSymbolForDeclarationId(symbolId, compilation);
            if (symbol is not null)
            {
                return symbol;
            }
        }

        throw new KeyNotFoundException($"Semantic symbol '{symbolId}' is not loaded.");
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

    private static IEnumerable<string> GetDiagnosticRefreshPaths(
        Solution solution,
        IReadOnlySet<ProjectId> compiledProjects,
        IReadOnlySet<string> requestedProjectPaths)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(compiledProjects);
        ArgumentNullException.ThrowIfNull(requestedProjectPaths);
        return solution.Projects
            .Where(project => compiledProjects.Contains(project.Id) && (requestedProjectPaths.Count == 0
                || (project.FilePath is not null
                    && requestedProjectPaths.Contains(Path.GetFullPath(project.FilePath)))))
            .SelectMany(project => project.Documents)
            .Select(document => document.FilePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path ?? string.Empty));
    }

    private static async Task<Solution> RefreshChangedDocumentsAsync(
        Solution solution,
        IEnumerable<string> changedFiles,
        string repositoryPath,
        CancellationToken cancellationToken)
    {
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

        var documentsByPath = CreateDocumentsByPath(solution);

        var refreshed = solution;
        foreach (var changedPath in normalizedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            documentsByPath.TryGetValue(changedPath, out var documentIds);
            SourceText? sourceText = null;
            if (File.Exists(changedPath) && IsCSharpSourcePath(changedPath))
            {
                var existingDocument = documentIds is { Length: > 0 } ? solution.GetDocument(documentIds[0]) : null;
                var existingText = existingDocument is null ? null : await existingDocument.GetTextAsync(cancellationToken);
                sourceText = await SemanticDiagnosticTextReader.ReadAsync(changedPath, existingText, cancellationToken);
            }

            if (documentIds is not null)
            {
                if (sourceText is not null)
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

            if (sourceText is null)
            {
                continue;
            }

            foreach (var project in FindContainingProjects(refreshed, changedPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                refreshed = refreshed.AddDocument(
                    DocumentId.CreateNewId(project.Id),
                    Path.GetFileName(changedPath),
                    sourceText,
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
                ResetEditAnalysisInputs();
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
            RetireCompilerWorkspace(previousWorkspace);
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

    private sealed class WorkspaceLease : IDisposable
    {
        private readonly Action<Workspace> _retire;
        private MSBuildWorkspace? _workspace;

        public WorkspaceLease(MSBuildWorkspace workspace, Action<Workspace> retire)
        {
            _workspace = workspace;
            _retire = retire;
        }

        public void TransferOwnership()
        {
            _workspace = null;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _workspace, null) is { } workspace)
            {
                _retire(workspace);
            }
        }
    }

    private async Task<T> RunNonCooperativeAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken,
        Action<T>? abandonedResultCleanup = null,
        Solution? sourceSolution = null,
        CancellationToken queryDeadline = default,
        Action? failedOperationCleanup = null,
        CancellationToken? retainedCandidateLifetime = null,
        Action<Task<T>>? retainActualOperation = null)
    {
        using var hostWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, queryDeadline);
        var operationCancellation = new CompilerOperationCancellation(retainedCandidateLifetime ?? cancellationToken, queryDeadline, _logger);
        var operationToken = operationCancellation.Token;
        try
        {
            await AcquireCompilerResourcesAsync(sourceSolution, hostWait.Token);
        }
        catch
        {
            await operationCancellation.DisposeAsync();
            throw;
        }

        var task = Task.Run(
            async () =>
            {
                var succeeded = false;
                try
                {
                    var result = await operation(operationToken);
                    succeeded = true;
                    return result;
                }
                finally
                {
                    try
                    {
                        await operationCancellation.DisposeAsync();
                        if (!succeeded)
                        {
                            failedOperationCleanup?.Invoke();
                        }
                    }
                    finally
                    {
                        ReleaseCompilerResources(sourceSolution);
                    }
                }
            },
            CancellationToken.None);
        retainActualOperation?.Invoke(task);
        try
        {
            // The host deadline cannot share the callback queue exposed to compiler extensions.
            return await task.WaitAsync(hostWait.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || queryDeadline.IsCancellationRequested)
        {
            var backstop = retainedCandidateLifetime is { IsCancellationRequested: false }
                ? Task.CompletedTask : Task.Delay(_cancellationBackstop, CancellationToken.None);
            var completed = await Task.WhenAny(
                task,
                backstop);
            if (completed == task)
            {
                try
                {
                    var abandonedResult = await task;
                    if (queryDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        return abandonedResult;
                    }

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
                throw new SemanticOperationAbandonedException(task, operationToken);
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
