namespace Threadsmith.RepositoryIntelligence;

using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Coalesces validated commit notifications for normal frontend run admission.</summary>
public sealed class RepositoryIntelligenceAutomation : ICommandHandler<TakeRepositoryIntelligenceRefreshCommand, bool>, IAsyncDisposable
{
    private readonly IRepositoryCommitObserver _commits;
    private readonly RepositoryIntelligenceMonitor _monitor;
    private readonly IRepositoryMemoryOptionsProvider _options;
    private readonly IDomainEventStream _events;
    private readonly ILogger<RepositoryIntelligenceAutomation> _logger;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Lock _gate = new();
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenOrder = new();
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private string? _identity;
    private SessionId _sessionId;
    private long _generation;
    private long _attempted;
    private long _completed;

    /// <summary>Initializes a new instance of the <see cref="RepositoryIntelligenceAutomation"/> class.</summary>
    public RepositoryIntelligenceAutomation(
        IRepositoryCommitObserver commits, RepositoryIntelligenceMonitor monitor, IRepositoryMemoryOptionsProvider options, IDomainEventStream events, ILogger<RepositoryIntelligenceAutomation> logger)
    {
        _commits = commits;
        _monitor = monitor;
        _options = options;
        _events = events;
        _logger = logger;
    }

    /// <summary>Captures the generation that a successful collection may acknowledge.</summary>
    public long CaptureGeneration(string identity)
    {
        lock (_gate)
        {
            return _identity == identity ? _generation : 0;
        }
    }

    /// <summary>Retains commit observation and pending generations while changing the active session owner.</summary>
    public void ActivateSession(SessionId sessionId)
    {
        lock (_gate)
        {
            _sessionId = sessionId;
        }
    }

    /// <summary>Acknowledges only commits observed before collection began.</summary>
    public void Complete(string identity, long generation)
    {
        lock (_gate)
        {
            if (_identity == identity)
            {
                _completed = Math.Max(_completed, generation);
            }
        }
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(TakeRepositoryIntelligenceRefreshCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = RepositoryIdentity.Create(command.RepositoryPath);
        lock (_gate)
        {
            if (_identity != identity || _sessionId != command.SessionId || _generation <= Math.Max(_attempted, _completed)
                || !_monitor.Capture(identity).IsEffective || !_options.Capture(identity).Intelligence.RefreshOnCommit)
            {
                return Task.FromResult(false);
            }

            _attempted = _generation;
            return Task.FromResult(true);
        }
    }

    /// <summary>Rebinds commit observation without historical replay or automatic onboarding.</summary>
    public async Task BindAsync(SessionId sessionId, string repositoryPath, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            var identity = RepositoryIdentity.Create(repositoryPath);
            var options = _options.Capture(identity).Intelligence;
            ActivateSession(sessionId);
            if (_identity == identity && _worker is { IsCompleted: false } && options.Enabled && options.RefreshOnCommit)
            {
                return;
            }

            await StopAsync();
            lock (_gate)
            {
                if (_identity != identity)
                {
                    _identity = identity;
                    _generation = 0;
                    _attempted = 0;
                    _completed = 0;
                    _seen.Clear();
                    _seenOrder.Clear();
                }
            }

            if (!options.Enabled || !options.RefreshOnCommit || !_monitor.Capture(identity).Onboarded)
            {
                return;
            }

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _worker = Task.Run(() => ObserveAsync(repositoryPath, identity, token), CancellationToken.None);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Joins observation before repository rebinding, disable, or shutdown.</summary>
    public async Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopAsync();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await SuspendAsync();
    }

    private async Task StopAsync()
    {
        if (_cancellation is not null)
        {
            await _cancellation.CancelAsync();
            if (_worker is not null)
            {
                await _worker;
            }

            _cancellation.Dispose();
            _cancellation = null;
            _worker = null;
        }
    }

    private SessionId CaptureSession()
    {
        lock (_gate)
        {
            return _sessionId;
        }
    }

    private async Task ObserveAsync(string repositoryPath, string identity, CancellationToken cancellationToken)
    {
        try
        {
            await _monitor.WaitUntilReadyAsync(cancellationToken);
            if (!_monitor.Capture(identity).IsEffective)
            {
                return;
            }

            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _monitor.FailureToken);
            await _events.PublishAsync(new DiagnosticObserved(CaptureSession(), DateTimeOffset.UtcNow, "intelligence.commit.observing", "Commit refresh observes validated standard reflog creation records. Disabled reflogs or custom client messages may be missed; use /intelligence refresh when needed. Repository changes from offline periods remain pending for explicit refresh."), lifetime.Token);
            await foreach (var commit in _commits.ObserveAsync(repositoryPath, lifetime.Token))
            {
                lock (_gate)
                {
                    if (!_seen.Add(commit.CommitId))
                    {
                        continue;
                    }

                    _seenOrder.Enqueue(commit.CommitId);
                    if (_seenOrder.Count > 256)
                    {
                        _seen.Remove(_seenOrder.Dequeue());
                    }

                    _generation++;
                }

                await _events.PublishAsync(new DiagnosticObserved(CaptureSession(), DateTimeOffset.UtcNow, "intelligence.refresh.pending", "A new commit queued repository intelligence refresh. It will run when the current request and composer are idle."), lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _monitor.FailureToken.IsCancellationRequested)
        {
            // Feature disable, source-monitor failure, or repository shutdown revokes automatic work.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Commit observation stopped for {RepositoryIdentity}", identity);
            await _events.PublishAsync(new DiagnosticObserved(CaptureSession(), DateTimeOffset.UtcNow, "intelligence.commit.failed", "Automatic intelligence refresh stopped because commit observation failed. Manual refresh remains available; disable and re-enable after correcting the cause."), CancellationToken.None);
        }
    }
}
