namespace Threadsmith.DotNet;

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Coordinates generation-owned readiness, demand, and lower-priority compilation warming.</summary>
public sealed partial class SemanticEngine
{
    private readonly SemaphoreSlim _preparationPublication = new(1, 1);
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly CancellationTokenSource _engineLifetime = new();
    private readonly SemanticPreparationLimits _preparationLimits = new();
    private readonly ConcurrentQueue<string> _preparationDiagnostics = new();
    private readonly List<ProjectCompilationSample> _preparationSlowSamples = [];
    private CancellationTokenSource _preparationOwnerLifetime = new();
    private SemanticCompilationCoordinator? _preparation;
    private TaskCompletionSource _initialPublication = CompletedPublication();
    private SemanticLoadResult? _pendingInitialResult;
    private bool _preparationHasWorkspaceFailures;
    private IReadOnlyList<string> _expectedPreparationPaths = [];
    private long _solutionGeneration;
    private long _preparationOwnership;
    private long _pendingInitialOwnership;
    private int _disposed;
    private Task _warmObservation = Task.CompletedTask;
    private bool _allowPreparationEvents;
    private int _preparationFailures;
    private Func<SemanticLoadRequest, SemanticConfidenceLevel, CancellationToken, Task>? _confidencePublisher;

    /// <summary>Uses the existing refresh binding owner to fan confidence out to workspace aliases.</summary>
    internal void SetConfidencePublisher(Func<SemanticLoadRequest, SemanticConfidenceLevel, CancellationToken, Task> publisher)
    {
        lock (_gate)
        {
            _confidencePublisher = publisher;
        }
    }

    /// <summary>Initializes a new instance of the <see cref="SemanticEngine"/> class with immutable test/measurement bounds.</summary>
    internal SemanticEngine(
        IDomainEventStream events,
        ILogger<SemanticEngine> logger,
        SemanticPreparationLimits preparationLimits,
        TimeSpan? cancellationBackstop = null,
        SemanticResourceLimits? resourceLimits = null)
        : this(events, logger, cancellationBackstop, resourceLimits)
    {
        preparationLimits.Validate();
        _preparationLimits = preparationLimits;
    }

    /// <summary>Joins terminal warm coverage, used by deterministic verification and complete refresh.</summary>
    internal async Task WaitForWarmAsync(CancellationToken cancellationToken)
    {
        SemanticCompilationCoordinator? preparation;
        lock (_gate)
        {
            preparation = _preparation;
        }

        if (preparation is not null)
        {
            await preparation.Completion.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Captures and publishes current confidence in order with preparation updates.</summary>
    internal async Task PublishCurrentConfidenceAsync(
        Func<SemanticConfidenceLevel, CancellationToken, Task> publication,
        CancellationToken cancellationToken)
    {
        await _preparationPublication.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await publication(Confidence, cancellationToken);
        }
        finally
        {
            _preparationPublication.Release();
        }
    }

    /// <summary>Releases warming only after the initial lifecycle pair was delivered for this result.</summary>
    internal async Task CompleteInitialPublicationAsync(
        SemanticLoadResult result,
        bool succeeded,
        CancellationToken cancellationToken)
    {
        await _preparationPublication.WaitAsync(cancellationToken);
        try
        {
            SemanticCompilationCoordinator? preparation;
            lock (_gate)
            {
                if (!ReferenceEquals(result, _pendingInitialResult) || _pendingInitialOwnership != _preparationOwnership)
                {
                    return;
                }

                preparation = _preparation;
                _pendingInitialResult = null;
                if (!succeeded)
                {
                    preparation?.Abort();
                    _initialPublication.TrySetCanceled(CancellationToken.None);
                    return;
                }

                _initialPublication.TrySetResult();
                _allowPreparationEvents = true;
            }

            if (preparation is not null)
            {
                preparation.Warm(RankReadinessProjects(preparation.Solution, LoadedRequest.SolutionPath));
                _warmObservation = ObserveWarmAsync(preparation);
            }
        }
        finally
        {
            _preparationPublication.Release();
        }
    }

    /// <summary>Prepares exact ownership, or global coverage when no narrower scope is supplied.</summary>
    internal async Task<SemanticPreparationReceipt?> EnsurePreparedAsync(
        string? path,
        string reason,
        bool requireSuccess,
        CancellationToken cancellationToken)
    {
        Solution? solution;
        string? repository;
        lock (_gate)
        {
            solution = _solution;
            repository = _lastRequest?.RepositoryPath;
        }

        if (solution is null || repository is null)
        {
            return null;
        }

        var fullPath = path is null ? null : Path.GetFullPath(path, repository);
        var ids = solution.Projects.Where(project => fullPath is null
                || PathMatchesScope(project.FilePath, fullPath)
                || project.Documents.Any(document => PathMatchesScope(document.FilePath, fullPath)))
            .Select(project => project.Id).ToHashSet();
        if (ids.Count == 0 && reason == "generated-code")
        {
            // Virtual generated filenames have no ordinary document owner before generation.
            ids.UnionWith(solution.ProjectIds);
        }

        var graph = solution.GetProjectDependencyGraph();
        foreach (var id in ids.ToArray())
        {
            ids.UnionWith(graph.GetProjectsThatThisProjectTransitivelyDependsOn(id));
        }

        return await EnsureProjectsPreparedAsync(solution, ids, reason, requireSuccess, cancellationToken);
    }

    /// <summary>Stops detached workspace preparation without disposing its retained compiler snapshot.</summary>
    internal async Task RetirePreparationAsync(bool retainCurrentPreparation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        SemanticCompilationCoordinator? preparation;
        CancellationTokenSource retiredLifetime;
        lock (_gate)
        {
            retiredLifetime = _preparationOwnerLifetime;
            _preparationOwnerLifetime = new();
            _preparationOwnership++;
            _solutionGeneration = ++_generation;
            preparation = retainCurrentPreparation ? null : _preparation;
            if (!retainCurrentPreparation)
            {
                preparation?.Abort();
                _initialPublication.TrySetCanceled(CancellationToken.None);
                _pendingInitialResult = null;
            }
        }

        await retiredLifetime.CancelAsync();
        retiredLifetime.Dispose();
        if (preparation is not null)
        {
            await preparation.DisposeAsync();
        }
    }

    /// <summary>Admits an exact immutable-solution demand and rejects obsolete required coverage.</summary>
    internal async Task<SemanticPreparationReceipt> EnsureProjectsPreparedAsync(
        Solution solution,
        IReadOnlySet<ProjectId> ids,
        string reason,
        bool requireSuccess,
        CancellationToken cancellationToken)
    {
        SemanticCompilationCoordinator? preparation;
        Task initialPublication;
        long sourceGeneration;
        lock (_gate)
        {
            preparation = _preparation;
            initialPublication = _initialPublication.Task;
            sourceGeneration = _solutionGeneration;
            if (!ReferenceEquals(solution, _solution))
            {
                throw new InvalidOperationException("The semantic solution changed before preparation admission.");
            }
        }

        if (preparation is null)
        {
            lock (_gate)
            {
                if (requireSuccess && ids.Any(id => !_compiledProjects.Contains(id)))
                {
                    throw new InvalidOperationException("Required semantic project coverage is unavailable.");
                }
            }

            return new(solution, sourceGeneration);
        }

        var started = Stopwatch.GetTimestamp();

        // Waiter cancellation does not own the shared preparation lifetime.
#pragma warning disable VSTHRD003 // The engine owns the lifecycle publication signal.
        await initialPublication.WaitAsync(cancellationToken);
#pragma warning restore VSTHRD003
        var tasks = ids.Select(id => preparation.PrepareAsync(id, demand: true, cancellationToken)).ToArray();
        SemanticPreparationOutcome[] outcomes;
        try
        {
            outcomes = await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SemanticLoadMetrics.PreparationCancellations.Add(1, new KeyValuePair<string, object?>("owner", "waiter"));
            throw;
        }

        lock (_gate)
        {
            if (!ReferenceEquals(preparation, _preparation) || outcomes.Any(outcome => outcome.Obsolete))
            {
                throw new InvalidOperationException("The semantic preparation generation was superseded.");
            }
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Semantic demand {Reason}: {Projects} projects, {Failed} unavailable, {DurationMs} ms",
                reason,
                ids.Count,
                outcomes.Count(outcome => !outcome.Succeeded),
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        SemanticLoadMetrics.PreparationDuration.Record(
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            new KeyValuePair<string, object?>("phase", "demand"),
            new KeyValuePair<string, object?>("reason", reason));
        if (requireSuccess && outcomes.Any(outcome => !outcome.Succeeded))
        {
            throw new InvalidOperationException("Required semantic project coverage is unavailable; no complete result can be produced.");
        }

        return new(solution, sourceGeneration);
    }

    private static TaskCompletionSource CompletedPublication()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }

    private void CheckPreparedSolution(SemanticPreparationReceipt? preparedSolution)
    {
        if (preparedSolution is not null && (!ReferenceEquals(preparedSolution.Solution, _solution) || preparedSolution.Generation != _solutionGeneration))
        {
            throw new InvalidOperationException("The semantic solution changed after required preparation; retry the operation.");
        }
    }

    private async Task<T> RunPreparedOperationAsync<T>(SemanticPreparationReceipt? preparedSolution, Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        SemanticCompilationCoordinator? preparation;
        lock (_gate)
        {
            CheckPreparedSolution(preparedSolution);
            preparation = _preparation;
        }

        async Task<T> ExecuteAsync(CancellationToken token)
        {
            EnsurePreparedSolutionCurrent(preparedSolution);
            var value = await RunNonCooperativeAsync(operation, token);
            EnsurePreparedSolutionCurrent(preparedSolution);
            return value;
        }

        return preparation is null
            ? await ExecuteAsync(cancellationToken)
            : await preparation.RunAsync(ExecuteAsync, cancellationToken);
    }

    private void EnsurePreparedSolutionCurrent(SemanticPreparationReceipt? preparedSolution)
    {
        lock (_gate)
        {
            CheckPreparedSolution(preparedSolution);
        }
    }

    private static bool PathMatchesScope(string? path, string scope)
    {
        return path is not null && (Path.GetFullPath(path).Equals(scope, PathComparison)
            || Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(scope) + Path.DirectorySeparatorChar, PathComparison));
    }

    private static ProjectId[] RankReadinessProjects(Solution solution, string selection)
    {
        var graph = solution.GetProjectDependencyGraph();
        var order = solution.ProjectIds.Select((id, index) => (id, index)).ToDictionary(item => item.id, item => item.index);
        return [.. solution.Projects
            .OrderByDescending(project => Path.GetFullPath(project.FilePath ?? selection).Equals(selection, PathComparison))
            .ThenByDescending(project => graph.GetProjectsThatTransitivelyDependOnThisProject(project.Id).Count())
            .ThenByDescending(project => graph.GetProjectsThatDirectlyDependOnThisProject(project.Id).Count)
            .ThenBy(project => order[project.Id])
            .ThenBy(project => project.FilePath, StringComparerForCurrentPlatform())
            .Select(project => project.Id)];
    }

    private SemanticCompilationCoordinator CreatePreparationCoordinator(Solution solution, IReadOnlySet<ProjectId> prepared)
    {
        // Each analyzer reference/language validation is shared across this generation's projects.
        var analyzerValidations = new ConcurrentDictionary<(object Reference, string Language), TaskCompletionSource<bool>>();
        return new SemanticCompilationCoordinator(
            solution,
            _preparationLimits,
            prepared,
            async (id, token) =>
            {
                var started = Stopwatch.GetTimestamp();
                var project = solution.GetProject(id)
                    ?? throw new InvalidOperationException("The preparation project is no longer available.");
                try
                {
                    var analyzerFailed = false;
                    foreach (var reference in project.AnalyzerReferences)
                    {
                        var ownedValidation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var validation = analyzerValidations.GetOrAdd((reference, project.Language), ownedValidation);
                        if (ReferenceEquals(validation, ownedValidation))
                        {
                            try
                            {
                                var valid = await RunNonCooperativeAsync(
                                    operationToken => Task.FromResult(ValidateAnalyzerReferences(
                                        project.WithAnalyzerReferences([reference]),
                                        [],
                                        operationToken).Count == 0),
                                    token);
                                ownedValidation.TrySetResult(valid);
                            }
                            catch (OperationCanceledException)
                            {
                                ownedValidation.TrySetCanceled(token);
                                throw;
                            }
                            catch
                            {
                                ownedValidation.TrySetResult(false);
                                throw;
                            }
                        }

#pragma warning disable VSTHRD003 // One engine-generation validation task is shared by all projects using this reference.
                        analyzerFailed |= !await validation.Task.WaitAsync(token);
#pragma warning restore VSTHRD003
                    }

                    var compilation = await RunNonCooperativeAsync<Compilation?>(project.GetCompilationAsync, token);
                    return new SemanticPreparationOutcome(
                        id,
                        compilation is not null && !analyzerFailed,
                        Failure: analyzerFailed ? "Analyzer or source generator unavailable." : compilation is null ? "Compilation unavailable." : null);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return new SemanticPreparationOutcome(id, false, Failure: exception.GetType().Name);
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_solution, solution))
                        {
                            AddSlowProjectSample(_preparationSlowSamples, SanitizeProjectName(project.Name), Stopwatch.GetElapsedTime(started));
                        }
                    }
                }
            },
            PublishPreparationAsync);
    }

    private async Task PublishPreparationAsync(
        SemanticCompilationCoordinator preparation,
        SemanticPreparationOutcome outcome,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await _preparationPublication.WaitAsync(cancellationToken);
            Task? barrier;
            lock (_gate)
            {
                barrier = ReferenceEquals(_preparation, preparation) && _compiledProjects.Count > 0
                    && !_initialPublication.Task.IsCompleted ? _initialPublication.Task : null;
            }

            if (barrier is null)
            {
                break;
            }

            _preparationPublication.Release();
#pragma warning disable VSTHRD003 // Later frontier results wait for this engine's initial publication.
            await barrier.WaitAsync(cancellationToken);
#pragma warning restore VSTHRD003
        }

        try
        {
            SemanticConfidenceLevel previous;
            SemanticConfidenceLevel current;
            SemanticLoadRequest request;
            bool publish;
            lock (_gate)
            {
                if (!ReferenceEquals(_preparation, preparation) || !ReferenceEquals(_solution, preparation.Solution)
                    || _disposed != 0 || outcome.Obsolete)
                {
                    return;
                }

                var project = preparation.Solution.GetProject(outcome.ProjectId);
                if (project is null || _compiledProjects.Contains(project.Id))
                {
                    return;
                }

                previous = _confidence;
                if (outcome.Succeeded)
                {
                    _compiledProjects = [.. _compiledProjects, project.Id];
                }
                else
                {
                    _preparationFailures++;
                    if (_preparationDiagnostics.Count < 32)
                    {
                        _preparationDiagnostics.Enqueue($"{SanitizeProjectName(project.Name)}: {outcome.Failure ?? "Preparation unavailable."}");
                    }
                }

                _projects = _projects.Select(info => info.Name == project.Name && info.FilePath == project.FilePath
                    ? info with { Confidence = outcome.Succeeded ? SemanticConfidenceLevel.FullSemantic : SemanticConfidenceLevel.ProjectGraphOnly }
                    : info).ToArray();
                var compiledPaths = _compiledProjects.Select(id => preparation.Solution.GetProject(id)?.FilePath)
                    .Where(path => path is not null).ToHashSet(StringComparerForCurrentPlatform());
                _confidence = _compiledProjects.Count > 0
                    ? _compiledProjects.Count == preparation.Solution.ProjectIds.Count
                        && _expectedPreparationPaths.All(compiledPaths.Contains) && !_preparationHasWorkspaceFailures
                        ? SemanticConfidenceLevel.FullSemantic : SemanticConfidenceLevel.PartialCompilation
                    : SemanticConfidenceLevel.ProjectGraphOnly;
                current = _confidence;
                if (outcome.Succeeded)
                {
                    _generation++;
                }

                request = _lastRequest ?? throw new InvalidOperationException("Preparation has no workspace request.");
                publish = _allowPreparationEvents && previous != current;
            }

            if (publish)
            {
                try
                {
                    if (_confidencePublisher is { } publisher)
                    {
                        await publisher(request, current, cancellationToken);
                    }
                    else
                    {
                        await _events.PublishAsync(new SemanticConfidenceChanged(request.SessionId, DateTimeOffset.UtcNow, current.ToString()), cancellationToken);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A delivery failure cannot turn proven compiler coverage into a compilation failure.
                    preparation.Abort(outcome);
                    _logger.LogWarning("Semantic warm confidence publication failed: {ErrorType}", exception.GetType().Name);
                    await _events.PublishAsync(
                        new DiagnosticObserved(
                            request.SessionId,
                            DateTimeOffset.UtcNow,
                            "TSSEMWARM",
                            "Semantic background confidence publication failed. Run /semantic_refresh to recover."),
                        _engineLifetime.Token);
                }
            }
        }
        finally
        {
            _preparationPublication.Release();
        }
    }

    private async Task ObserveWarmAsync(SemanticCompilationCoordinator preparation)
    {
#pragma warning disable VSTHRD003 // This is the engine-owned coordinator's terminal completion signal.
        var outcomes = await preparation.Completion;
#pragma warning restore VSTHRD003
        var statistics = preparation.Statistics;
        var obsolete = outcomes.Count(outcome => outcome.Obsolete);
        if (obsolete > 0)
        {
            SemanticLoadMetrics.PreparationCancellations.Add(1, new KeyValuePair<string, object?>("owner", _engineLifetime.IsCancellationRequested ? "application" : "generation"));
        }

        SemanticLoadMetrics.PreparationDuration.Record(
            Stopwatch.GetElapsedTime(preparation.StartedAt).TotalMilliseconds,
            new KeyValuePair<string, object?>("phase", "warm"));
        foreach (var (kind, count) in new[]
        {
            ("succeeded", outcomes.Count(outcome => outcome.Succeeded)), ("failed", outcomes.Count(outcome => !outcome.Succeeded && !outcome.Obsolete)),
            ("obsolete", obsolete), ("joins", statistics.Joins), ("promotions", statistics.Promotions), ("attempts", statistics.Attempts),
            ("maximum_running", statistics.MaximumRunning), ("discarded", statistics.Discarded), ("demand_queue", statistics.Demand), ("warm_queue", statistics.Warm),
        })
        {
            SemanticLoadMetrics.PreparationCount.Record(count, new KeyValuePair<string, object?>("kind", kind));
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Semantic warm completed: {DurationMs} ms, {Succeeded} succeeded, {Failed} failed, worker limit {Workers}, maximum concurrency {MaximumRunning}, {Joins} joins, {Promotions} demand promotions",
                Stopwatch.GetElapsedTime(preparation.StartedAt).TotalMilliseconds,
                outcomes.Count(outcome => outcome.Succeeded),
                outcomes.Count(outcome => !outcome.Succeeded),
                _preparationLimits.Workers,
                statistics.MaximumRunning,
                statistics.Joins,
                statistics.Promotions);
        }
    }

    private ReplacementState CaptureReplacementState()
    {
        lock (_gate)
        {
            return new(
                _solution,
                _workspace,
                _compiledProjects,
                _projects,
                _confidence,
                _lastRequest,
                _preparation,
                _expectedPreparationPaths,
                _preparationHasWorkspaceFailures,
                _allowPreparationEvents);
        }
    }

    private void RestoreReplacementState(ReplacementState previous, SemanticCompilationCoordinator failed, long ownership)
    {
        SemanticCompilationCoordinator? recovered;
        lock (_gate)
        {
            if (!ReferenceEquals(failed, _preparation)
                && (ownership != _preparationOwnership || !ReferenceEquals(previous.Preparation, _preparation)))
            {
                return;
            }

            recovered = ownership == _preparationOwnership && previous.Solution is not null && previous.Published
                ? CreatePreparationCoordinator(previous.Solution, previous.Compiled) : null;
            _solution = previous.Solution;
            _workspace = previous.Workspace;
            _compiledProjects = previous.Compiled;
            _projects = previous.Projects;
            _confidence = previous.Confidence;
            _lastRequest = previous.Request;
            _preparation = recovered;
            _initialPublication.TrySetCanceled();
            _initialPublication = previous.Published ? CompletedPublication() : new(TaskCreationOptions.RunContinuationsAsynchronously);
            _allowPreparationEvents = previous.Published;
            _expectedPreparationPaths = previous.ExpectedPaths;
            _preparationHasWorkspaceFailures = previous.WorkspaceFailures;
            _generation++;
            _solutionGeneration = _generation;
        }

        if (recovered is not null && previous.Request is not null)
        {
            recovered.Warm(RankReadinessProjects(recovered.Solution, previous.Request.SolutionPath));
            _warmObservation = ObserveWarmAsync(recovered);
        }
    }

    private sealed record ReplacementState(Solution? Solution, Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace? Workspace,
        HashSet<ProjectId> Compiled, IReadOnlyList<SemanticProjectInfo> Projects, SemanticConfidenceLevel Confidence, SemanticLoadRequest? Request, SemanticCompilationCoordinator? Preparation,
        IReadOnlyList<string> ExpectedPaths, bool WorkspaceFailures, bool Published);
}
