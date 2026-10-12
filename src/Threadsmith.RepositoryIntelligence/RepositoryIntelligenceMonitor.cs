namespace Threadsmith.RepositoryIntelligence;

using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Owns supervised repository bookkeeping independently of compiler or model execution.</summary>
public sealed class RepositoryIntelligenceMonitor : IRepositoryIntelligenceStatusProvider, IAsyncDisposable
{
    private readonly IOutputSanitizer _sanitizer;
    private readonly IManagedRepositoryMemoryStore _store;
    private readonly IRepositoryFileObserver _observer;
    private readonly IDomainEventStream _events;
    private readonly ILogger<RepositoryIntelligenceMonitor> _logger;
    private readonly Lock _sessionGate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _faults = new(StringComparer.Ordinal);
    private SessionId _sessionId;
    private RepositoryIntelligenceStatus _status = new() { RepositoryIdentity = string.Empty };
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private Func<string, Task>? _ensureSupport;
    private Task _ready = Task.CompletedTask;
    private CancellationTokenSource _failure = new();
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="RepositoryIntelligenceMonitor"/> class without starting observation.</summary>
    public RepositoryIntelligenceMonitor(
        IManagedRepositoryMemoryStore store,
        IRepositoryFileObserver observer,
        IDomainEventStream events,
        ILogger<RepositoryIntelligenceMonitor> logger,
        IOutputSanitizer sanitizer)
    {
        _sanitizer = sanitizer ?? throw new ArgumentNullException(nameof(sanitizer));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Retargets background diagnostics without interrupting repository observation.</summary>
    public void ActivateSession(SessionId sessionId)
    {
        lock (_sessionGate)
        {
            _sessionId = sessionId;
        }
    }

    /// <summary>Cancels active collection when observation can no longer track its evidence.</summary>
    public CancellationToken FailureToken => _failure.Token;

    /// <summary>Waits for startup only for feature-owned work, never for ordinary recall.</summary>
    public Task WaitUntilReadyAsync(CancellationToken cancellationToken = default)
    {
        return _ready.WaitAsync(cancellationToken);
    }

    /// <summary>Establishes observation before a collection source read, including excluded support directories.</summary>
    public async Task EnsureSupportAsync(string identity, string relativePath, CancellationToken cancellationToken = default)
    {
        if (Capture(identity).Fault is not null)
        {
            throw new InvalidOperationException("Source observation is faulted.");
        }

        var ensure = _ensureSupport ?? throw new InvalidOperationException("Source observation is not ready.");
        await ensure(relativePath).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public RepositoryIntelligenceStatus Capture(string repositoryIdentity)
    {
        var status = Volatile.Read(ref _status);
        return status.RepositoryIdentity == repositoryIdentity
            ? status : new RepositoryIntelligenceStatus { RepositoryIdentity = repositoryIdentity };
    }

    /// <summary>Releases observation before the repository-local persistence binding changes.</summary>
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

    /// <summary>Forgets stopped observation and transient faults after a committed repository reset.</summary>
    public async Task ResetAsync(string repositoryIdentity, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            await StopAsync();
            _faults.TryRemove(repositoryIdentity, out _);
            _failure.Dispose();
            _failure = new CancellationTokenSource();
            _ready = Task.CompletedTask;
            _ensureSupport = null;
            Volatile.Write(ref _status, new RepositoryIntelligenceStatus { RepositoryIdentity = repositoryIdentity });
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Stops the previous consumer before starting coverage for an explicitly bound repository.</summary>
    public async Task BindAsync(
        SessionId sessionId,
        string repositoryPath,
        RepositoryIntelligenceOptions options,
        CancellationToken cancellationToken = default)
    {
        await BindCoreAsync(sessionId, repositoryPath, options, false, false, cancellationToken);
    }

    /// <summary>Tracks collection reads even before initial onboarding, without enabling recall.</summary>
    public Task TrackCollectionAsync(SessionId sessionId, string repositoryPath, RepositoryIntelligenceOptions options, CancellationToken cancellationToken = default)
    {
        return BindCoreAsync(sessionId, repositoryPath, options, true, false, cancellationToken);
    }

    /// <summary>Explicit recovery establishes observation and accounts for the gap before clearing a fault.</summary>
    public Task RecoverAsync(SessionId sessionId, string repositoryPath, RepositoryIntelligenceOptions options, CancellationToken cancellationToken = default)
    {
        return BindCoreAsync(sessionId, repositoryPath, options, false, true, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            _disposed = true;
            await StopAsync();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task BindCoreAsync(
        SessionId sessionId, string repositoryPath, RepositoryIntelligenceOptions options, bool collecting, bool recovering, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var identity = RepositoryIdentity.Create(root);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ActivateSession(sessionId);
            var previous = Capture(identity);
            var state = await _store.GetIntelligenceStateAsync(identity, cancellationToken);
            if (!recovering && _worker is { IsCompleted: false } && previous.RepositoryIdentity == identity
                && previous.Fault is null && ((options.Enabled && state.Onboarded) || collecting))
            {
                if (collecting)
                {
                    await _ready.WaitAsync(cancellationToken);
                }

                if (_ready.IsCompletedSuccessfully)
                {
                    var current = await _store.GetIntelligenceStateAsync(identity, cancellationToken);
                    var observed = Capture(identity);
                    if (observed.Fault is not null || _worker.IsCompleted)
                    {
                        throw new InvalidOperationException(observed.Fault ?? "Source observation stopped.");
                    }

                    Interlocked.CompareExchange(ref _status, observed with { Onboarded = current.Onboarded, Enabled = options.Enabled }, observed);
                }

                return;
            }

            await StopAsync();
            state = await _store.GetIntelligenceStateAsync(identity, cancellationToken);
            var status = new RepositoryIntelligenceStatus
            {
                RepositoryIdentity = identity,
                Onboarded = state.Onboarded,
                Enabled = options.Enabled,
                Fault = _faults.GetValueOrDefault(identity) ?? state.Fault,
            };
            Volatile.Write(ref _status, status with { Enabled = false });
            if ((!collecting && !recovering && (!options.Enabled || !state.Onboarded)) || (status.Fault is not null && !recovering))
            {
                return;
            }

            _failure.Dispose();
            _failure = new CancellationTokenSource();
            _ready = ready.Task;
            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _worker = Task.Run(() => ObserveAsync(root, status, recovering, ready, token), CancellationToken.None);
        }
        finally
        {
            _lifecycle.Release();
        }

        if (collecting || recovering)
        {
            await ready.Task.WaitAsync(cancellationToken);
            if (Capture(identity).Fault is { } fault)
            {
                throw new InvalidOperationException(fault);
            }
        }
    }

    private SessionId CaptureSession()
    {
        lock (_sessionGate)
        {
            return _sessionId;
        }
    }

    private async Task StopAsync()
    {
        Volatile.Write(ref _status, Volatile.Read(ref _status) with { Enabled = false });
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

        Volatile.Write(ref _status, Volatile.Read(ref _status) with { Enabled = false });
    }

    private async Task ObserveAsync(
        string root, RepositoryIntelligenceStatus status, bool recovering, TaskCompletionSource? ready, CancellationToken cancellationToken)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var subscriptions = new Dictionary<string, IRepositoryDirectoryObservation>(comparer);
        var queue = Channel.CreateBounded<RepositoryFileChange>(new BoundedChannelOptions(1024)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var supportRequests = new System.Collections.Concurrent.ConcurrentQueue<(string Path, TaskCompletionSource Completion)>();
        var gap = 0;
        void Enqueue(RepositoryFileChange change)
        {
            if (!cancellationToken.IsCancellationRequested && !queue.Writer.TryWrite(change))
            {
                Interlocked.Exchange(ref gap, 1);
            }
        }

        void ObservationFailed()
        {
            Interlocked.Exchange(ref gap, 1);
            Enqueue(new RepositoryFileChange(root, SemanticFileChangeKind.Uncertain));
        }

        try
        {
            // Persist uncertainty before exposing eligibility; recall never waits for this worker.
            await _store.RecordIntelligenceChangesAsync(status.RepositoryIdentity, ["."], true, cancellationToken: cancellationToken);
            var snapshot = await _store.GetSnapshotAsync(status.RepositoryIdentity, [], cancellationToken);
            var supportDirectories = snapshot.Entries.Where(entry => entry.Ownership == RepositoryMemoryOwnership.Intelligence)
                .SelectMany(entry => entry.Supports)
                .Select(support => Path.GetDirectoryName(Path.GetFullPath(support.Path, root)) ?? root)
                .ToHashSet(comparer);
            Cover(root);
            await ReconcileGapAsync();
            if (recovering)
            {
                await _store.ClearIntelligenceFaultAsync(status.RepositoryIdentity, cancellationToken);
                _faults.TryRemove(status.RepositoryIdentity, out _);
                status = status with { Fault = null };
            }

            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _status, status);
            _ensureSupport = relativePath =>
            {
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                supportRequests.Enqueue((relativePath, completion));
                Enqueue(new RepositoryFileChange(root, SemanticFileChangeKind.Changed));
                return completion.Task;
            };
            ready?.TrySetResult();
            while (await queue.Reader.WaitToReadAsync(cancellationToken))
            {
                EnsureRequestedSupports();
                var paths = new HashSet<string>(StringComparer.Ordinal);
                var uncertain = Interlocked.Exchange(ref gap, 0) != 0;
                var restart = uncertain;
                var consumed = 0;
                while (consumed++ < 1024 && queue.Reader.TryRead(out var change))
                {
                    if (change.Path == root && change.Kind == SemanticFileChangeKind.Changed)
                    {
                        EnsureRequestedSupports();

                        continue;
                    }

                    uncertain |= change.Kind == SemanticFileChangeKind.Uncertain;
                    restart |= change.Kind == SemanticFileChangeKind.Uncertain && !change.ObservationRestarted;
                    AddPath(change.Path);
                    if (change.PreviousPath is { } previous)
                    {
                        AddPath(previous);
                        ReleaseSubtree(previous);
                    }

                    if (change.Kind == SemanticFileChangeKind.Deleted)
                    {
                        ReleaseSubtree(change.Path);
                    }

                    if (Directory.Exists(change.Path))
                    {
                        Cover(change.Path);
                    }
                }

                var overflow = Interlocked.Exchange(ref gap, 0) != 0;
                uncertain |= overflow;
                restart |= overflow;

                if (!Directory.Exists(root))
                {
                    throw new IOException("The observed repository directory is no longer available.");
                }

                if (uncertain)
                {
                    paths.Add(".");
                }

                await _store.RecordIntelligenceChangesAsync(status.RepositoryIdentity, paths.ToArray(), uncertain, cancellationToken: cancellationToken);
                if (uncertain)
                {
                    if (restart)
                    {
                        foreach (var subscription in subscriptions.Values)
                        {
                            subscription.Restart();
                        }
                    }

                    foreach (var subscription in subscriptions.Values)
                    {
                        subscription.Dispose();
                    }

                    subscriptions.Clear();
                    Cover(root);
                    await ReconcileGapAsync();
                }

                void AddPath(string path)
                {
                    var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (!RepositoryObservationPaths.IsHostStatePath(relative) && relative != "." && !relative.StartsWith("../", StringComparison.Ordinal)
                        && !Path.IsPathRooted(relative)
                        && (!relative.Split('/').Any(RepositoryObservationPaths.IsIgnoredDirectorySegment)
                            || supportDirectories.Contains(Path.GetDirectoryName(path) ?? root)
                            || supportDirectories.Any(directory => comparer.Equals(directory, path)
                                || directory.StartsWith(path + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))))
                    {
                        paths.Add(relative);
                    }
                }
            }

            void EnsureRequestedSupports()
            {
                while (supportRequests.TryDequeue(out var request))
                {
                    try
                    {
                        if (RepositoryObservationPaths.IsHostStatePath(request.Path))
                        {
                            throw new InvalidOperationException("Host state and Git metadata cannot be primary supports.");
                        }

                        var fullPath = Path.GetFullPath(request.Path, root);
                        var directory = Path.GetDirectoryName(fullPath) ?? root;
                        var relative = Path.GetRelativePath(root, fullPath);
                        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("Primary supports must belong to this repository.");
                        }

                        supportDirectories.Add(directory);
                        var uncovered = directory;
                        while (!comparer.Equals(uncovered, root) && Path.GetDirectoryName(uncovered) is { } parent && !subscriptions.ContainsKey(parent))
                        {
                            uncovered = parent;
                        }

                        Cover(uncovered);
                        if (!subscriptions.ContainsKey(directory))
                        {
                            throw new InvalidOperationException("Primary support directory cannot be observed.");
                        }

                        request.Completion.TrySetResult();
                    }
                    catch (Exception exception)
                    {
                        request.Completion.TrySetException(exception);
                        throw;
                    }
                }
            }

            void ReleaseSubtree(string path)
            {
                foreach (var directory in subscriptions.Keys.Where(directory => comparer.Equals(directory, path)
                    || directory.StartsWith(path + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ToArray())
                {
                    subscriptions.Remove(directory, out var subscription);
                    subscription?.Dispose();
                }
            }

            async Task ReconcileGapAsync()
            {
                var state = await _store.GetIntelligenceStateAsync(status.RepositoryIdentity, cancellationToken);
                var current = await _store.GetSnapshotAsync(status.RepositoryIdentity, [], cancellationToken);
                var observations = new List<RepositoryMemorySupport>();
                foreach (var support in current.Entries.SelectMany(entry => entry.Supports).DistinctBy(support => support.Path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fullPath = Path.GetFullPath(support.Path, root);
                    var parent = Path.GetDirectoryName(fullPath);
                    if (parent is not null && Directory.Exists(parent) && !subscriptions.ContainsKey(parent))
                    {
                        continue;
                    }

                    try
                    {
                        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
                        observations.Add(support with { ContentHash = hash });
                    }
                    catch (IOException exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                    {
                        observations.Add(support with { ContentHash = new string('0', 64) });
                    }
                }

                await _store.ReconcileIntelligenceObservationAsync(status.RepositoryIdentity, state.ChangeSequence, observations, cancellationToken);
            }

            void Cover(string directory)
            {
                var pending = new Stack<string>();
                pending.Push(directory);
                while (pending.TryPop(out var current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (subscriptions.ContainsKey(current))
                    {
                        continue;
                    }

                    try
                    {
                        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }

                        var relative = Path.GetRelativePath(root, current).Replace('\\', '/');
                        var required = supportDirectories.Any(path => comparer.Equals(path, current)
                            || path.StartsWith(current + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
                        if (RepositoryObservationPaths.IsHostStatePath(relative)
                            || (!required && relative.Split('/').Any(RepositoryObservationPaths.IsIgnoredDirectorySegment)))
                        {
                            continue;
                        }

                        subscriptions.Add(current, _observer.Subscribe(current, Enqueue, ObservationFailed));
                        foreach (var child in Directory.EnumerateDirectories(current))
                        {
                            pending.Push(child);
                        }
                    }
                    catch (IOException exception) when (exception is DirectoryNotFoundException or FileNotFoundException)
                    {
                        Enqueue(new RepositoryFileChange(current, SemanticFileChangeKind.Deleted));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ready?.TrySetCanceled(cancellationToken);

            // Expected lifetime cancellation is not an observation failure.
        }
        catch (Exception exception)
        {
            var cause = _sanitizer.Sanitize(exception.Message.Replace(root, ".", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)).ReplaceLineEndings(" ");
            if (cause.Length > 1024)
            {
                cause = cause[..1024] + "…";
            }

            var reason = $"Repository intelligence was disabled: {exception.GetType().Name}: {cause} Correct the cause and explicitly re-enable intelligence.";
            Volatile.Write(ref _status, status with { Fault = reason });
            _faults[status.RepositoryIdentity] = reason;
            await _failure.CancelAsync();
            ready?.TrySetResult();
            _logger.LogError(exception, "Repository intelligence source observation failed for {RepositoryIdentity}", status.RepositoryIdentity);
            try
            {
                await _store.RecordIntelligenceChangesAsync(status.RepositoryIdentity, [], true, reason, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception persistenceException) when (persistenceException is not OperationCanceledException)
            {
                _logger.LogError(persistenceException, "Could not persist the repository intelligence fault marker");
            }

            try
            {
                await _events.PublishAsync(new DiagnosticObserved(CaptureSession(), DateTimeOffset.UtcNow, "intelligence.monitor.failed", reason), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception publicationException) when (publicationException is not OperationCanceledException)
            {
                _logger.LogError(publicationException, "Could not publish the repository intelligence fault diagnostic");
            }
        }
        finally
        {
            _ensureSupport = null;
            while (supportRequests.TryDequeue(out var request))
            {
                request.Completion.TrySetException(new InvalidOperationException("Source observation stopped."));
            }

            queue.Writer.TryComplete();
            foreach (var subscription in subscriptions.Values)
            {
                subscription.Dispose();
            }
        }
    }
}
