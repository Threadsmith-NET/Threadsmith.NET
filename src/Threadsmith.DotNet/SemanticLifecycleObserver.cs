namespace Threadsmith.DotNet;

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Queues repository lifecycle changes for workspace-isolated semantic loading.</summary>
public sealed class SemanticLifecycleObserver : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ConcurrentDictionary<SessionId, ActiveLoad> _activeLoads = new();
    private readonly ConcurrentDictionary<SessionId, long> _bindingGenerations = new();
    private readonly IDomainEventStream _events;
    private readonly ILogger<SemanticLifecycleObserver> _logger;
    private readonly ISemanticLifecycleLoader _semanticLoader;
    private readonly SemanticRefreshCoordinator? _refreshCoordinator;
    private readonly ConcurrentDictionary<SessionId, RepositoryState> _repositories = new();
    private readonly Channel<SemanticLoadWorkItem> _requests = Channel.CreateUnbounded<SemanticLoadWorkItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly Task _worker;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="SemanticLifecycleObserver"/> class.</summary>
    public SemanticLifecycleObserver(
        SemanticEngineRegistry semanticEngines,
        IDomainEventStream events,
        ILogger<SemanticLifecycleObserver> logger)
        : this(
            new RegistrySemanticLifecycleLoader(semanticEngines),
            refreshCoordinator: null,
            events,
            logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SemanticLifecycleObserver"/> class.</summary>
    public SemanticLifecycleObserver(
        SemanticEngineRegistry semanticEngines,
        SemanticRefreshCoordinator? refreshCoordinator,
        IDomainEventStream events,
        ILogger<SemanticLifecycleObserver> logger)
        : this(
            new RegistrySemanticLifecycleLoader(semanticEngines),
            refreshCoordinator,
            events,
            logger)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SemanticLifecycleObserver"/> class with a deterministic loader.</summary>
    internal SemanticLifecycleObserver(
        ISemanticLifecycleLoader semanticLoader,
        SemanticRefreshCoordinator? refreshCoordinator,
        IDomainEventStream events,
        ILogger<SemanticLifecycleObserver> logger)
    {
        ArgumentNullException.ThrowIfNull(semanticLoader);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(logger);
        _semanticLoader = semanticLoader;
        _refreshCoordinator = refreshCoordinator;
        _events = events;
        _logger = logger;
        _worker = ProcessAsync();
    }

    /// <summary>Captures repository state and queues solution loads without publishing re-entrantly.</summary>
    public async Task ObserveAsync(
        IDomainEvent domainEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (domainEvent is RepositoryOpened repositoryOpened)
        {
            await CancelActiveLoadAsync(repositoryOpened.SessionId, cancellationToken);
            _bindingGenerations.AddOrUpdate(
                repositoryOpened.SessionId,
                addValue: 1,
                (_, current) => current + 1);
            if (_refreshCoordinator is not null)
            {
                await _refreshCoordinator.UnbindAsync(repositoryOpened.SessionId, cancellationToken);
            }

            _repositories[repositoryOpened.SessionId] = new RepositoryState(
                repositoryOpened.Path,
                repositoryOpened.WorkspaceId,
                repositoryOpened.TrustLevel,
                repositoryOpened.ProhibitedPaths ?? []);
            return;
        }

        if (domainEvent is SolutionLoaded solutionLoaded
            && _repositories.TryGetValue(solutionLoaded.SessionId, out var repository))
        {
            await CancelActiveLoadAsync(solutionLoaded.SessionId, cancellationToken);

            var request = new SemanticLoadRequest(
                solutionLoaded.SessionId,
                repository.WorkspaceId,
                repository.Path,
                solutionLoaded.Path,
                repository.TrustLevel,
                repository.ProhibitedPaths);
            await CancelConflictingWorkspaceLoadsAsync(request, cancellationToken);
            var bindingGeneration = _bindingGenerations.AddOrUpdate(
                solutionLoaded.SessionId,
                addValue: 1,
                (_, current) => current + 1);
            var bindingBegin = _refreshCoordinator is null
                ? new SemanticBindingBeginResult(0, ReusedWorkspaceBinding: false)
                : await _refreshCoordinator.BeginBindingForLifecycleAsync(request, cancellationToken);
            if (!_requests.Writer.TryWrite(new SemanticLoadWorkItem(
                request,
                bindingGeneration,
                bindingBegin.Generation,
                bindingBegin.ReusedWorkspaceBinding)))
            {
                throw new InvalidOperationException("The semantic lifecycle queue is closed.");
            }

            return;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _requests.Writer.TryComplete();
        await _lifetimeCancellation.CancelAsync();
        try
        {
#pragma warning disable VSTHRD003 // The worker is started by this observer's constructor.
            await _worker;
#pragma warning restore VSTHRD003
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }

        _lifetimeCancellation.Dispose();
    }

    private async Task ProcessAsync()
    {
        await foreach (var workItem in _requests.Reader.ReadAllAsync(_lifetimeCancellation.Token))
        {
            var request = workItem.Request;
            try
            {
                if (!IsCurrent(workItem))
                {
                    continue;
                }

                SemanticLoadResult result;
                if (workItem.ReusedWorkspaceBinding)
                {
                    var refresh = await (_refreshCoordinator
                        ?? throw new InvalidOperationException(
                            "A reused semantic binding requires a refresh coordinator."))
                        .EnsureCurrentAsync(
                            request.SessionId,
                            SemanticRefreshReason.UserAdmission,
                            _lifetimeCancellation.Token);
                    result = new SemanticLoadResult(
                        request.WorkspaceId,
                        refresh.Confidence,
                        [],
                        []);
                }
                else
                {
                    result = _refreshCoordinator is null
                        ? await LoadWithTrackedCancellationAsync(
                            request,
                            _lifetimeCancellation.Token,
                            () => IsCurrent(workItem))
                        : await _refreshCoordinator.PublishLifecycleBindingAsync(
                            request,
                            publicationToken => LoadWithTrackedCancellationAsync(
                                request,
                                publicationToken,
                                () => IsCurrent(workItem)),
                            _lifetimeCancellation.Token);
                }

                if (!IsCurrent(workItem))
                {
                    continue;
                }

                if (_refreshCoordinator is not null && !workItem.ReusedWorkspaceBinding)
                {
                    await _refreshCoordinator.CompleteBindingAsync(
                        request,
                        workItem.RefreshBindingGeneration,
                        _lifetimeCancellation.Token);
                }

                if (!IsCurrent(workItem))
                {
                    continue;
                }

                await _events.PublishAsync(
                    new SemanticConfidenceChanged(
                        request.SessionId,
                        DateTimeOffset.UtcNow,
                        result.Confidence.ToString()),
                    _lifetimeCancellation.Token);
                await _events.PublishAsync(
                    new SemanticLoadCompleted(
                        request.SessionId,
                        DateTimeOffset.UtcNow,
                        request.WorkspaceId,
                        result.Confidence.ToString()),
                    _lifetimeCancellation.Token);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // A newer lifecycle binding cancelled this obsolete load before publication.
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Semantic loading failed for solution {SolutionPath}",
                    request.SolutionPath);
                if (IsCurrent(workItem))
                {
                    if (_refreshCoordinator is not null)
                    {
                        await _refreshCoordinator.FailBindingAsync(
                            request.SessionId,
                            workItem.RefreshBindingGeneration,
                            "Semantic loading could not establish a current repository model.",
                            _lifetimeCancellation.Token);
                    }

                    await _events.PublishAsync(
                        new SemanticLoadCompleted(
                            request.SessionId,
                            DateTimeOffset.UtcNow,
                            request.WorkspaceId,
                            SemanticConfidenceLevel.None.ToString()),
                        _lifetimeCancellation.Token);
                }
            }
            finally
            {
                CompleteTrackedLoad(request.SessionId);
            }
        }
    }

    private async Task<SemanticLoadResult> LoadWithTrackedCancellationAsync(
        SemanticLoadRequest request,
        CancellationToken cancellationToken,
        Func<bool> isCurrent)
    {
        var activeLoad = new ActiveLoad(
            request,
            CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCancellation.Token,
                cancellationToken));
        if (!_activeLoads.TryAdd(request.SessionId, activeLoad))
        {
            activeLoad.Cancellation.Dispose();
            throw new InvalidOperationException(
                "A semantic lifecycle load is already active for this session.");
        }

        if (!isCurrent())
        {
            throw new OperationCanceledException(
                "The semantic lifecycle load was superseded before publication.",
                activeLoad.Cancellation.Token);
        }

        return await _semanticLoader.LoadForBindingAsync(request, activeLoad.Cancellation.Token);
    }

    private void CompleteTrackedLoad(SessionId sessionId)
    {
        if (_activeLoads.TryRemove(sessionId, out var activeLoad))
        {
            activeLoad.Completion.TrySetResult();
            activeLoad.Cancellation.Dispose();
        }
    }

    private async Task CancelActiveLoadAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        if (!_activeLoads.TryGetValue(sessionId, out var activeLoad))
        {
            return;
        }

        await activeLoad.Cancellation.CancelAsync();
#pragma warning disable VSTHRD003 // This is the tracked completion signal for the observer-owned load.
        await activeLoad.Completion.Task.WaitAsync(cancellationToken);
#pragma warning restore VSTHRD003
    }

    private async Task CancelConflictingWorkspaceLoadsAsync(
        SemanticLoadRequest request,
        CancellationToken cancellationToken)
    {
        var activeLoads = _activeLoads
            .Where(item => item.Key != request.SessionId
                && item.Value.Request.WorkspaceId == request.WorkspaceId
                && !IsEquivalentSelection(item.Value.Request, request))
            .Select(item => item.Value)
            .ToArray();
        foreach (var activeLoad in activeLoads)
        {
            await activeLoad.Cancellation.CancelAsync();
        }

        foreach (var activeLoad in activeLoads)
        {
#pragma warning disable VSTHRD003 // These are observer-owned active-load completion signals.
            await activeLoad.Completion.Task.WaitAsync(cancellationToken);
#pragma warning restore VSTHRD003
        }
    }

    private static bool IsEquivalentSelection(
        SemanticLoadRequest current,
        SemanticLoadRequest replacement)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return Path.GetFullPath(current.RepositoryPath)
                .Equals(Path.GetFullPath(replacement.RepositoryPath), comparison)
            && Path.GetFullPath(current.SolutionPath)
                .Equals(Path.GetFullPath(replacement.SolutionPath), comparison)
            && current.TrustLevel == replacement.TrustLevel
            && (current.ProhibitedPaths ?? []).SequenceEqual(
                replacement.ProhibitedPaths ?? [],
                StringComparer.OrdinalIgnoreCase);
    }

    private bool IsCurrent(SemanticLoadWorkItem workItem)
    {
        return _bindingGenerations.TryGetValue(workItem.Request.SessionId, out var generation)
            && generation == workItem.BindingGeneration;
    }

    private sealed record SemanticLoadWorkItem(
        SemanticLoadRequest Request,
        long BindingGeneration,
        long RefreshBindingGeneration,
        bool ReusedWorkspaceBinding);

    private sealed record ActiveLoad(
        SemanticLoadRequest Request,
        CancellationTokenSource Cancellation)
    {
        public TaskCompletionSource Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record RepositoryState(
        string Path,
        WorkspaceId WorkspaceId,
        RepositoryTrustLevel TrustLevel,
        IReadOnlyList<string> ProhibitedPaths);
}

/// <summary>Loads a lifecycle-selected semantic candidate without publishing terminal events.</summary>
internal interface ISemanticLifecycleLoader
{
    /// <summary>Loads the selected candidate under the observer-owned cancellation fence.</summary>
    Task<SemanticLoadResult> LoadForBindingAsync(
        SemanticLoadRequest request,
        CancellationToken cancellationToken);
}

/// <summary>Adapts the engine registry to lifecycle candidate loading.</summary>
internal sealed class RegistrySemanticLifecycleLoader : ISemanticLifecycleLoader
{
    private readonly SemanticEngineRegistry _semanticEngines;

    /// <summary>Initializes a new instance of the <see cref="RegistrySemanticLifecycleLoader"/> class.</summary>
    public RegistrySemanticLifecycleLoader(SemanticEngineRegistry semanticEngines)
    {
        ArgumentNullException.ThrowIfNull(semanticEngines);
        _semanticEngines = semanticEngines;
    }

    /// <inheritdoc />
    public Task<SemanticLoadResult> LoadForBindingAsync(
        SemanticLoadRequest request,
        CancellationToken cancellationToken)
    {
        return _semanticEngines.LoadForBindingAsync(request, cancellationToken);
    }
}
