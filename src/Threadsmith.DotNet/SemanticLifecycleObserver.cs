namespace Threadsmith.DotNet;

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Queues repository lifecycle changes for workspace-isolated semantic loading.</summary>
public sealed class SemanticLifecycleObserver : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly IDomainEventStream _events;
    private readonly ILogger<SemanticLifecycleObserver> _logger;
    private readonly ConcurrentDictionary<SessionId, RepositoryState> _repositories = new();
    private readonly Channel<SemanticLoadRequest> _requests = Channel.CreateUnbounded<SemanticLoadRequest>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly SemanticEngineRegistry _semanticEngines;
    private readonly Task _worker;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="SemanticLifecycleObserver"/> class.</summary>
    public SemanticLifecycleObserver(
        SemanticEngineRegistry semanticEngines,
        IDomainEventStream events,
        ILogger<SemanticLifecycleObserver> logger)
    {
        ArgumentNullException.ThrowIfNull(semanticEngines);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(logger);
        _semanticEngines = semanticEngines;
        _events = events;
        _logger = logger;
        _worker = ProcessAsync();
    }

    /// <summary>Captures repository state and queues solution loads without publishing re-entrantly.</summary>
    public Task ObserveAsync(
        IDomainEvent domainEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (domainEvent is RepositoryOpened repositoryOpened)
        {
            _repositories[repositoryOpened.SessionId] = new RepositoryState(
                repositoryOpened.Path,
                repositoryOpened.WorkspaceId,
                repositoryOpened.TrustLevel,
                repositoryOpened.ProhibitedPaths ?? []);
            return Task.CompletedTask;
        }

        if (domainEvent is SolutionLoaded solutionLoaded
            && _repositories.TryGetValue(solutionLoaded.SessionId, out var repository))
        {
            var request = new SemanticLoadRequest(
                solutionLoaded.SessionId,
                repository.WorkspaceId,
                repository.Path,
                solutionLoaded.Path,
                repository.TrustLevel,
                repository.ProhibitedPaths);
            if (!_requests.Writer.TryWrite(request))
            {
                throw new InvalidOperationException("The semantic lifecycle queue is closed.");
            }
        }

        return Task.CompletedTask;
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
        await foreach (var request in _requests.Reader.ReadAllAsync(_lifetimeCancellation.Token))
        {
            try
            {
                await _semanticEngines.LoadAsync(request, _lifetimeCancellation.Token);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Semantic loading failed for solution {SolutionPath}",
                    request.SolutionPath);
                await _events.PublishAsync(
                    new SemanticLoadCompleted(
                        request.SessionId,
                        DateTimeOffset.UtcNow,
                        request.WorkspaceId,
                        SemanticConfidenceLevel.None.ToString()),
                    _lifetimeCancellation.Token);
            }
        }
    }

    private sealed record RepositoryState(
        string Path,
        WorkspaceId WorkspaceId,
        RepositoryTrustLevel TrustLevel,
        IReadOnlyList<string> ProhibitedPaths);
}
