namespace Threadsmith.DotNet;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Immutable host-owned resource bounds for semantic refresh coordination.</summary>
internal sealed record SemanticRefreshResourceLimits
{
    /// <summary>Initializes a new instance of the <see cref="SemanticRefreshResourceLimits"/> class.</summary>
    public SemanticRefreshResourceLimits(
        int maximumAuthoritativeInputPaths = 4096,
        int maximumGraphScanDepth = 64,
        int maximumGraphScanEntries = 20000,
        int maximumPendingPaths = 1024,
        int maximumRecentHostEchoIdentities = 1024,
        int maximumSafeReasonLength = 256,
        int maximumWatcherDirectories = 512,
        long maximumAuthoritativeSnapshotBytes = 64 * 1024 * 1024,
        long maximumStableReadBytes = 4 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAuthoritativeInputPaths);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumGraphScanDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumGraphScanEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPendingPaths);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRecentHostEchoIdentities);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSafeReasonLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumWatcherDirectories);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumAuthoritativeSnapshotBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumStableReadBytes);
        if (maximumAuthoritativeSnapshotBytes < maximumStableReadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAuthoritativeSnapshotBytes));
        }

        MaximumAuthoritativeInputPaths = maximumAuthoritativeInputPaths;
        MaximumGraphScanDepth = maximumGraphScanDepth;
        MaximumGraphScanEntries = maximumGraphScanEntries;
        MaximumPendingPaths = maximumPendingPaths;
        MaximumRecentHostEchoIdentities = maximumRecentHostEchoIdentities;
        MaximumSafeReasonLength = maximumSafeReasonLength;
        MaximumWatcherDirectories = maximumWatcherDirectories;
        MaximumAuthoritativeSnapshotBytes = maximumAuthoritativeSnapshotBytes;
        MaximumStableReadBytes = maximumStableReadBytes;
    }

    /// <summary>Gets the maximum authoritative input path count.</summary>
    public int MaximumAuthoritativeInputPaths { get; }

    /// <summary>Gets the aggregate authoritative snapshot byte bound.</summary>
    public long MaximumAuthoritativeSnapshotBytes { get; }

    /// <summary>Gets the maximum repository graph scan depth.</summary>
    public int MaximumGraphScanDepth { get; }

    /// <summary>Gets the maximum repository graph scan entry count.</summary>
    public int MaximumGraphScanEntries { get; }

    /// <summary>Gets the maximum pending path count.</summary>
    public int MaximumPendingPaths { get; }

    /// <summary>Gets the maximum retained host echo identity count.</summary>
    public int MaximumRecentHostEchoIdentities { get; }

    /// <summary>Gets the maximum safe failure-reason character count.</summary>
    public int MaximumSafeReasonLength { get; }

    /// <summary>Gets the per-file stable-read byte bound.</summary>
    public long MaximumStableReadBytes { get; }

    /// <summary>Gets the maximum watched directory count.</summary>
    public int MaximumWatcherDirectories { get; }

    /// <summary>Gets the production semantic refresh resource limits.</summary>
    public static SemanticRefreshResourceLimits Production { get; } = new();
}

/// <summary>Owns workspace-scoped monitoring and single-flight semantic refresh convergence.</summary>
public sealed class SemanticRefreshCoordinator :
    ISemanticRefreshCoordinator,
    ISemanticHostMutationAttribution,
    ICommandHandler<EnsureSemanticCurrentCommand, SemanticRefreshResult>,
    ICommandHandler<ForceSemanticRefreshCommand, SemanticRefreshResult>
{
    private static readonly TimeSpan _defaultMaximumBurstWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan _defaultRecentHostEchoLifetime = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _defaultSettleInterval = TimeSpan.FromMilliseconds(350);
    private readonly Dictionary<SessionId, WorkspaceBinding> _bindings = [];
    private readonly Dictionary<WorkspaceId, WorkspaceBinding> _workspaceBindings = [];
    private readonly ISemanticRefreshBackend _backend;
    private readonly IDomainEventStream _events;
    private readonly ISemanticFileSnapshotReader? _fileSnapshotReader;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ILogger<SemanticRefreshCoordinator> _logger;
    private readonly TimeSpan _maximumBurstWindow;
    private readonly ISemanticPathSafetyValidator? _pathSafetyValidator;
    private readonly ISemanticRefreshPublicationGate? _publicationGate;
    private readonly TimeSpan _recentHostEchoLifetime;
    private readonly SemanticRefreshResourceLimits _resourceLimits;
    private readonly TimeSpan _settleInterval;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, FileSystemWatcher> _watcherFactory;
    private readonly int _watcherScanEntryLimit;
    private readonly bool _watchFileSystem;
    private long _nextBindingGeneration;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="SemanticRefreshCoordinator"/> class.</summary>
    public SemanticRefreshCoordinator(
        SemanticEngineRegistry semanticEngines,
        IDomainEventStream events,
        ILogger<SemanticRefreshCoordinator> logger,
        ISemanticRefreshPublicationGate? publicationGate = null,
        TimeProvider? timeProvider = null,
        TimeSpan? settleInterval = null,
        TimeSpan? maximumBurstWindow = null)
        : this(
            new RegistrySemanticRefreshBackend(semanticEngines),
            events,
            logger,
            publicationGate,
            timeProvider,
            settleInterval,
            maximumBurstWindow,
            watchFileSystem: true,
            fileSnapshotReader: null,
            pathSafetyValidator: null)
    {
    }

    /// <inheritdoc />
    public bool IsCurrent(SessionId sessionId)
    {
        var binding = GetBinding(sessionId, required: false);
        if (binding is null)
        {
            return true;
        }

        lock (binding.Gate)
        {
            return !binding.IsObsolete
                && !binding.IsLoading
                && binding.BindingFailure is null
                && !binding.HasWork;
        }
    }

    /// <inheritdoc />
    public bool TryGetWorkspaceId(SessionId sessionId, out WorkspaceId workspaceId)
    {
        lock (_gate)
        {
            if (_bindings.TryGetValue(sessionId, out var binding))
            {
                workspaceId = binding.Request.WorkspaceId;
                return true;
            }
        }

        workspaceId = default;
        return false;
    }

    /// <inheritdoc />
    public bool TryAdmitCurrent(
        SessionId sessionId,
        WorkspaceId expectedWorkspaceId,
        Func<bool> admit)
    {
        ArgumentNullException.ThrowIfNull(admit);
        lock (_gate)
        {
            if (!_bindings.TryGetValue(sessionId, out var binding))
            {
                return expectedWorkspaceId == default && admit();
            }

            if (binding.Request.WorkspaceId != expectedWorkspaceId)
            {
                return false;
            }

            lock (binding.Gate)
            {
                return !binding.IsObsolete
                    && !binding.IsLoading
                    && binding.BindingFailure is null
                    && !binding.HasWork
                    && admit();
            }
        }
    }

    /// <summary>Initializes a new instance of the <see cref="SemanticRefreshCoordinator"/> class with a testable backend.</summary>
    internal SemanticRefreshCoordinator(
        ISemanticRefreshBackend backend,
        IDomainEventStream events,
        ILogger<SemanticRefreshCoordinator> logger,
        ISemanticRefreshPublicationGate? publicationGate = null,
        TimeProvider? timeProvider = null,
        TimeSpan? settleInterval = null,
        TimeSpan? maximumBurstWindow = null,
        bool watchFileSystem = false,
        ISemanticFileSnapshotReader? fileSnapshotReader = null,
        ISemanticPathSafetyValidator? pathSafetyValidator = null,
        TimeSpan? recentHostEchoLifetime = null,
        Func<string, FileSystemWatcher>? watcherFactory = null,
        int? watcherScanEntryLimit = null,
        SemanticRefreshResourceLimits? resourceLimits = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(logger);
        if (settleInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(settleInterval));
        }

        if (maximumBurstWindow <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBurstWindow));
        }

        if (recentHostEchoLifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(recentHostEchoLifetime));
        }

        if (watcherScanEntryLimit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(watcherScanEntryLimit));
        }

        _backend = backend;
        _events = events;
        _fileSnapshotReader = fileSnapshotReader;
        _logger = logger;
        _publicationGate = publicationGate;
        _recentHostEchoLifetime = recentHostEchoLifetime ?? _defaultRecentHostEchoLifetime;
        _resourceLimits = resourceLimits ?? SemanticRefreshResourceLimits.Production;
        _pathSafetyValidator = pathSafetyValidator;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _settleInterval = settleInterval ?? _defaultSettleInterval;
        _maximumBurstWindow = maximumBurstWindow ?? _defaultMaximumBurstWindow;
        _watcherFactory = watcherFactory ?? (static path => new FileSystemWatcher(path));
        _watcherScanEntryLimit = watcherScanEntryLimit ?? _resourceLimits.MaximumGraphScanEntries;
        if (_maximumBurstWindow < _settleInterval)
        {
            throw new ArgumentException(
                "The maximum burst window cannot be shorter than the settle interval.",
                nameof(maximumBurstWindow));
        }

        _watchFileSystem = watchFileSystem;
    }

    /// <summary>Installs a pending lifecycle binding before its initial semantic load begins.</summary>
    public async Task<long> BeginBindingAsync(
        SemanticLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await BeginBindingCoreAsync(request, cancellationToken);
        return result.Generation;
    }

    /// <inheritdoc />
    public async Task BindAsync(
        SemanticLoadRequest request,
        CancellationToken cancellationToken = default)
    {
        await BindCoreAsync(request, expectedGeneration: null, cancellationToken);
    }

    /// <summary>Completes only the exact pending lifecycle binding that began the load.</summary>
    public Task CompleteBindingAsync(
        SemanticLoadRequest request,
        long expectedGeneration,
        CancellationToken cancellationToken)
    {
        return BindCoreAsync(request, expectedGeneration, cancellationToken);
    }

    /// <summary>Binds directly or completes an exact pending lifecycle generation.</summary>
    public async Task BindCoreAsync(
        SemanticLoadRequest request,
        long? expectedGeneration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var repositoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.RepositoryPath));
        if (!Directory.Exists(repositoryPath))
        {
            throw new InvalidOperationException("The semantic repository binding no longer exists.");
        }

        var normalizedRequest = request with
        {
            RepositoryPath = repositoryPath,
            SolutionPath = Path.GetFullPath(request.SolutionPath),
        };
        WorkspaceBinding binding;
        var reusedPendingBinding = false;
        var bindingsToDispose = new HashSet<WorkspaceBinding>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_bindings.TryGetValue(request.SessionId, out var pending)
                && pending.IsLoading
                && (!expectedGeneration.HasValue || pending.Generation == expectedGeneration.Value)
                && pending.Request.WorkspaceId == request.WorkspaceId
                && PathComparer.Equals(pending.Request.SolutionPath, normalizedRequest.SolutionPath))
            {
                binding = pending;
                reusedPendingBinding = true;
            }
            else
            {
                if (expectedGeneration.HasValue)
                {
                    throw new OperationCanceledException(
                        "The pending semantic binding was superseded before it completed.");
                }

                binding = new WorkspaceBinding(
                    normalizedRequest,
                    Interlocked.Increment(ref _nextBindingGeneration),
                    _watchFileSystem,
                    _watcherFactory,
                    _watcherScanEntryLimit,
                    _resourceLimits.MaximumWatcherDirectories);
                if (_workspaceBindings.Remove(request.WorkspaceId, out var replacedWorkspace))
                {
                    MarkBindingObsolete(replacedWorkspace);
                    foreach (var alias in replacedWorkspace.SessionIds)
                    {
                        _bindings[alias] = binding;
                        binding.SessionIds.Add(alias);
                    }

                    replacedWorkspace.SessionIds.Clear();
                    bindingsToDispose.Add(replacedWorkspace);
                }

                if (_bindings.TryGetValue(request.SessionId, out var replacedSession)
                    && !ReferenceEquals(replacedSession, binding))
                {
                    DetachSessionBindingLocked(request.SessionId, bindingsToDispose);
                }

                _bindings[request.SessionId] = binding;
                binding.SessionIds.Add(request.SessionId);
                _workspaceBindings[request.WorkspaceId] = binding;
            }
        }

        await DisposeBindingsAsync(bindingsToDispose);

        try
        {
            binding.StartWatching(
                change => QueueChange(binding, change),
                () => QueueRecovery(binding, restartWatcher: true));
            var loadedDocuments = await _backend.GetLoadedDocumentsAsync(
                request.WorkspaceId,
                cancellationToken);
            lock (binding.Gate)
            {
                foreach (var document in loadedDocuments)
                {
                    binding.AppliedIdentities[document.Path] = document.ContentIdentity;
                }
            }

            // Add exact non-recursive roots for loaded documents that intentionally live beneath
            // a normally ignored directory without interrupting the already-active monitor.
            binding.StartWatching(
                change => QueueChange(binding, change),
                () => QueueRecovery(binding, restartWatcher: true));
            await SeedAuthoritativeInputIdentitiesAsync(binding, cancellationToken);
            await ReconcileBoundDocumentsAsync(binding, loadedDocuments, cancellationToken);
            lock (binding.Gate)
            {
                ThrowIfObsolete(binding);
                binding.IsLoading = false;
                binding.BindingFailure = null;
                binding.InitialLoadCompletion.TrySetResult();
                if (binding.HasWork)
                {
                    _ = EnsureWorkerLocked(binding);
                }
            }
        }
        catch (Exception exception)
        {
            binding.StopWatching();
            if (reusedPendingBinding || exception is InvalidDataException)
            {
                lock (binding.Gate)
                {
                    binding.BindingFailure = exception;
                    if (reusedPendingBinding)
                    {
                        binding.InitialLoadCompletion.TrySetException(
                            exception is InvalidDataException
                                ? exception
                                : new InvalidOperationException(
                                    "Semantic loading completed, but repository monitoring could not start."));
                    }
                }
            }
            else
            {
                lock (_gate)
                {
                    foreach (var alias in binding.SessionIds.ToArray())
                    {
                        if (_bindings.TryGetValue(alias, out var current)
                            && ReferenceEquals(current, binding))
                        {
                            _bindings.Remove(alias);
                        }
                    }

                    binding.SessionIds.Clear();
                    if (_workspaceBindings.TryGetValue(request.WorkspaceId, out var currentWorkspace)
                        && ReferenceEquals(currentWorkspace, binding))
                    {
                        _workspaceBindings.Remove(request.WorkspaceId);
                    }
                }

                await DisposeBindingAsync(binding, CancellationToken.None);
            }

            throw;
        }
    }

    /// <summary>Fails a pending lifecycle binding so request admission remains closed.</summary>
    public Task FailBindingAsync(
        SessionId sessionId,
        string safeReason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeReason);
        cancellationToken.ThrowIfCancellationRequested();
        var binding = GetBinding(sessionId, required: false);
        if (binding is not null)
        {
            lock (binding.Gate)
            {
                if (binding.IsLoading)
                {
                    binding.BindingFailure = new InvalidOperationException(safeReason);
                    binding.InitialLoadCompletion.TrySetException(binding.BindingFailure);
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>Fails only the exact pending lifecycle binding that began the load.</summary>
    public Task FailBindingAsync(
        SessionId sessionId,
        long expectedGeneration,
        string safeReason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeReason);
        cancellationToken.ThrowIfCancellationRequested();
        var binding = GetBinding(sessionId, required: false);
        if (binding is not null)
        {
            lock (binding.Gate)
            {
                if (binding.IsLoading && binding.Generation == expectedGeneration)
                {
                    binding.BindingFailure = new InvalidOperationException(safeReason);
                    binding.InitialLoadCompletion.TrySetException(binding.BindingFailure);
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task UnbindAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var bindingsToDispose = new HashSet<WorkspaceBinding>();
        lock (_gate)
        {
            DetachSessionBindingLocked(sessionId, bindingsToDispose);
        }

        foreach (var binding in bindingsToDispose)
        {
            await DisposeBindingAsync(binding, cancellationToken);
        }
    }

    /// <inheritdoc />
    public ValueTask ObserveChangeAsync(
        SemanticFileChange change,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentException.ThrowIfNullOrWhiteSpace(change.Path);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var binding = GetBinding(change.SessionId, required: false);
        if (binding is null)
        {
            return ValueTask.CompletedTask;
        }

        QueueChange(binding, change);
        if (change.Kind == SemanticFileChangeKind.Renamed
            && !string.IsNullOrWhiteSpace(change.PreviousPath))
        {
            QueueChange(binding, change with
            {
                Path = change.PreviousPath,
                Kind = SemanticFileChangeKind.Deleted,
                PreviousPath = null,
            });
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<SemanticRefreshResult> EnsureCurrentAsync(
        SessionId sessionId,
        SemanticRefreshReason reason,
        CancellationToken cancellationToken = default)
    {
        var binding = GetBinding(sessionId, required: false);
        if (binding is null)
        {
            return CreateCleanResult(default, reason, SemanticConfidenceLevel.None, 0, 0);
        }

        var waitedForRefresh = false;
        while (true)
        {
            Task? initialLoad = null;
            Task? worker = null;
            lock (binding.Gate)
            {
                ThrowIfObsolete(binding);
                if (binding.BindingFailure is not null)
                {
                    throw new InvalidOperationException(
                        "The semantic repository binding could not establish current state.",
                        binding.BindingFailure);
                }

                if (binding.IsLoading)
                {
                    initialLoad = binding.InitialLoadCompletion.Task;
                }
                else if (!binding.HasWork)
                {
                    if (waitedForRefresh && binding.LastResult is not null)
                    {
                        return binding.LastResult;
                    }

                    return CreateCleanResult(
                        binding.Request.WorkspaceId,
                        reason,
                        _backend.GetConfidence(binding.Request.WorkspaceId),
                        binding.DirtyVersion,
                        binding.AppliedVersion);
                }
                else
                {
                    if (binding.Worker is not null)
                    {
                        SemanticRefreshMetrics.JoinedWaiters.Add(1);
                    }

                    worker = EnsureWorkerLocked(binding);
                }
            }

            var pending = initialLoad ?? worker
                ?? throw new InvalidOperationException("Semantic refresh admission state is invalid.");
            await pending.WaitAsync(cancellationToken);
            waitedForRefresh |= worker is not null;
        }
    }

    /// <inheritdoc />
    public async Task<SemanticRefreshResult> ForceRefreshAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        var binding = GetBinding(sessionId, required: true)
            ?? throw new InvalidOperationException("No repository solution is bound for semantic refresh.");
        SemanticRefreshMetrics.Forced.Add(1);
        while (true)
        {
            Task? initialLoad;
            lock (binding.Gate)
            {
                ThrowIfObsolete(binding);
                if (binding.BindingFailure is not null)
                {
                    throw new InvalidOperationException(
                        "The semantic repository binding could not establish current state.",
                        binding.BindingFailure);
                }

                initialLoad = binding.IsLoading ? binding.InitialLoadCompletion.Task : null;
            }

            if (initialLoad is null)
            {
                break;
            }

            await initialLoad.WaitAsync(cancellationToken);
        }

        long requestedForce;
        lock (binding.Gate)
        {
            ThrowIfObsolete(binding);
            if (binding.ForceRequestedVersion == binding.ForceAppliedVersion)
            {
                binding.ForceRequestedVersion++;
            }

            requestedForce = binding.ForceRequestedVersion;
            if (!binding.IsLoading)
            {
                _ = EnsureWorkerLocked(binding);
            }
        }

        while (true)
        {
            Task worker;
            lock (binding.Gate)
            {
                ThrowIfObsolete(binding);
                if (binding.ForceAppliedVersion >= requestedForce)
                {
                    return binding.LastResult
                        ?? throw new InvalidOperationException(
                            "The forced semantic refresh completed without a result.");
                }

                worker = EnsureWorkerLocked(binding);
            }

            await worker.WaitAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public Task<SemanticRefreshResult> HandleAsync(
        EnsureSemanticCurrentCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return EnsureCurrentAsync(command.SessionId, command.Reason, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SemanticRefreshResult> HandleAsync(
        ForceSemanticRefreshCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ForceRefreshAsync(command.SessionId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SemanticHostMutationRegistration?> RegisterExpectedWritesAsync(
        SessionId sessionId,
        WorkspaceId workspaceId,
        MutationSetId mutationSetId,
        IReadOnlyList<SemanticHostWriteExpectation> writes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writes);
        cancellationToken.ThrowIfCancellationRequested();
        var binding = GetBinding(sessionId, required: false);
        if (binding is null || binding.Request.WorkspaceId != workspaceId)
        {
            return Task.FromResult<SemanticHostMutationRegistration?>(null);
        }

        var prepared = new List<(string Path, HashSet<string> Identities, bool ExistedBefore)>();
        foreach (var write in writes)
        {
            var fullPath = NormalizePath(binding, write.RelativePath);
            if (fullPath is null)
            {
                continue;
            }

            var identities = new HashSet<string>(StringComparer.Ordinal)
            {
                write.ContentIdentity,
            };
            if (write.AllowMissingTransition)
            {
                identities.Add("missing");
            }

            if (!string.IsNullOrWhiteSpace(write.CompensationContentIdentity))
            {
                identities.Add(write.CompensationContentIdentity);
            }

            prepared.Add((fullPath, identities, write.ExistedBefore));
        }

        lock (binding.Gate)
        {
            ThrowIfObsolete(binding);
            var pathShapes = prepared.ToDictionary(
                item => item.Path,
                item => item.ExistedBefore,
                PathComparer);
            if (!binding.ActiveHostMutations.TryAdd(mutationSetId, pathShapes))
            {
                throw new InvalidOperationException(
                    "The semantic host-write registration is already active.");
            }

            foreach (var write in prepared)
            {
                binding.HostIdentities[write.Path] = write.Identities;
                binding.HostMismatchPaths.Remove(write.Path);
            }
        }

        SemanticHostMutationRegistration registration = new(
            sessionId,
            workspaceId,
            mutationSetId,
            binding.Generation);
        return Task.FromResult<SemanticHostMutationRegistration?>(registration);
    }

    /// <inheritdoc />
    public async Task CompleteExpectedWritesAsync(
        SemanticHostMutationRegistration registration,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);
        cancellationToken.ThrowIfCancellationRequested();
        var binding = GetBinding(registration.SessionId, required: false);
        if (binding is not null
            && (binding.Generation != registration.BindingGeneration
                || binding.Request.WorkspaceId != registration.WorkspaceId))
        {
            return;
        }

        if (binding is not null)
        {
            var paths = relativePaths
                .Select(path => NormalizePath(binding, path))
                .Where(path => path is not null)
                .Select(path => path ?? string.Empty)
                .ToArray();
            IReadOnlyDictionary<string, bool>? pathShapes;
            lock (binding.Gate)
            {
                if (binding.IsObsolete
                    || !binding.ActiveHostMutations.Remove(
                        registration.MutationSetId,
                        out pathShapes))
                {
                    return;
                }
            }

            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = await ReadStableFileAsync(binding, path, cancellationToken);
                if (!content.IsStable)
                {
                    QueueRecovery(binding);
                    continue;
                }

                SemanticRefreshReason source;
                lock (binding.Gate)
                {
                    var matches = binding.HostIdentities.TryGetValue(path, out var identities)
                        && identities.Contains(content.Identity);
                    source = matches
                        ? SemanticRefreshReason.HostMutation
                        : SemanticRefreshReason.ExternalChange;
                    if (!matches)
                    {
                        binding.HostMismatchPaths.Add(path);
                    }
                }

                var existedBefore = pathShapes?.TryGetValue(path, out var existed) == true && existed;
                var kind = content.Exists
                    ? existedBefore
                        ? SemanticFileChangeKind.Changed
                        : SemanticFileChangeKind.Created
                    : SemanticFileChangeKind.Deleted;
                QueueChange(
                    binding,
                    new SemanticFileChange(
                        registration.SessionId,
                        path,
                        kind,
                        source));
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetimeCancellation.CancelAsync();
        WorkspaceBinding[] bindings;
        lock (_gate)
        {
            bindings = [.. _workspaceBindings.Values];
            foreach (var binding in bindings)
            {
                MarkBindingObsolete(binding);
                binding.SessionIds.Clear();
            }

            _bindings.Clear();
            _workspaceBindings.Clear();
        }

        foreach (var binding in bindings)
        {
            await DisposeBindingAsync(binding, CancellationToken.None);
        }

        _lifetimeCancellation.Dispose();
    }

    /// <summary>Begins lifecycle binding and reports whether the workspace already has one authority.</summary>
    internal Task<SemanticBindingBeginResult> BeginBindingForLifecycleAsync(
        SemanticLoadRequest request,
        CancellationToken cancellationToken)
    {
        return BeginBindingCoreAsync(request, cancellationToken);
    }

    /// <summary>Runs a lifecycle-selected replacement load at the workspace publication boundary.</summary>
    internal Task<SemanticLoadResult> PublishLifecycleBindingAsync(
        SemanticLoadRequest request,
        Func<CancellationToken, Task<SemanticLoadResult>> publication,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(publication);
        return _publicationGate is null
            ? publication(cancellationToken)
            : _publicationGate.PublishAsync(
                request.SessionId,
                request.WorkspaceId,
                publication,
                cancellationToken);
    }

    /// <summary>Queues a deterministic watcher recovery signal for focused verification.</summary>
    internal void ObserveFileSystemWatcherError(SessionId sessionId)
    {
        var binding = GetBinding(sessionId, required: false);
        if (binding is not null)
        {
            QueueRecovery(binding, restartWatcher: true);
        }
    }

    /// <summary>Gets the active host-write registration count for focused verification.</summary>
    internal int GetActiveHostMutationCount(SessionId sessionId)
    {
        var binding = GetBinding(sessionId, required: false);
        if (binding is null)
        {
            return 0;
        }

        lock (binding.Gate)
        {
            return binding.ActiveHostMutations.Count;
        }
    }

    private async Task<SemanticBindingBeginResult> BeginBindingCoreAsync(
        SemanticLoadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var repositoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.RepositoryPath));
        if (!Directory.Exists(repositoryPath))
        {
            throw new InvalidOperationException("The semantic repository binding no longer exists.");
        }

        var normalizedRequest = request with
        {
            RepositoryPath = repositoryPath,
            SolutionPath = Path.GetFullPath(request.SolutionPath),
        };
        WorkspaceBinding binding;
        var reusedWorkspaceBinding = false;
        var bindingsToDispose = new HashSet<WorkspaceBinding>();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_workspaceBindings.TryGetValue(request.WorkspaceId, out var existing)
                && AreEquivalentBindings(existing.Request, normalizedRequest)
                && (!_bindings.TryGetValue(request.SessionId, out var sessionBinding)
                    || !ReferenceEquals(sessionBinding, existing)))
            {
                binding = existing;
                reusedWorkspaceBinding = true;
                DetachSessionBindingLocked(request.SessionId, bindingsToDispose);
                _bindings[request.SessionId] = binding;
                binding.SessionIds.Add(request.SessionId);
            }
            else
            {
                binding = new WorkspaceBinding(
                    normalizedRequest,
                    Interlocked.Increment(ref _nextBindingGeneration),
                    _watchFileSystem,
                    _watcherFactory,
                    _watcherScanEntryLimit,
                    _resourceLimits.MaximumWatcherDirectories)
                {
                    IsLoading = true,
                };
                if (_workspaceBindings.Remove(request.WorkspaceId, out var replacedWorkspace))
                {
                    MarkBindingObsolete(replacedWorkspace);
                    foreach (var alias in replacedWorkspace.SessionIds)
                    {
                        _bindings[alias] = binding;
                        binding.SessionIds.Add(alias);
                    }

                    replacedWorkspace.SessionIds.Clear();
                    bindingsToDispose.Add(replacedWorkspace);
                }

                if (_bindings.TryGetValue(request.SessionId, out var replacedSession)
                    && !ReferenceEquals(replacedSession, binding))
                {
                    DetachSessionBindingLocked(request.SessionId, bindingsToDispose);
                }

                _bindings[request.SessionId] = binding;
                binding.SessionIds.Add(request.SessionId);
                _workspaceBindings[request.WorkspaceId] = binding;
            }
        }

        if (!reusedWorkspaceBinding)
        {
            try
            {
                // Initial load reconciliation compares this snapshot with the loaded workspace. Starting
                // native monitoring first would expose its bounded buffer to design-time build output.
                var initialSnapshot = await CaptureAuthoritativeInputSnapshotAsync(
                    binding,
                    _backend.GetRefreshInventory(binding.Request.WorkspaceId),
                    additionalPaths: [],
                    binaryAdditionalPaths: [],
                    includePotentialBinaryInputs: true,
                    cancellationToken);
                EnsureCompleteAuthoritativeSnapshot(
                    initialSnapshot,
                    "Initial semantic inputs could not be captured within safe resource bounds.");
                lock (binding.Gate)
                {
                    ThrowIfObsolete(binding);
                    binding.InitialInputSnapshot = initialSnapshot;
                }
            }
            catch (Exception exception)
            {
                var stopWatching = false;
                lock (binding.Gate)
                {
                    if (!binding.IsObsolete)
                    {
                        stopWatching = true;
                        binding.InitialLoadCompletion.TrySetException(
                            new InvalidOperationException(
                                "Semantic repository monitoring could not start.",
                                exception));
                    }
                }

                if (stopWatching)
                {
                    binding.StopWatching();
                }

                await DisposeBindingsAsync(bindingsToDispose);
                throw;
            }
        }

        await DisposeBindingsAsync(bindingsToDispose);
        return new SemanticBindingBeginResult(binding.Generation, reusedWorkspaceBinding);
    }

    private static bool AreEquivalentBindings(
        SemanticLoadRequest current,
        SemanticLoadRequest replacement)
    {
        return PathComparer.Equals(current.RepositoryPath, replacement.RepositoryPath)
            && PathComparer.Equals(current.SolutionPath, replacement.SolutionPath)
            && current.TrustLevel == replacement.TrustLevel
            && (current.ProhibitedPaths ?? []).SequenceEqual(
                replacement.ProhibitedPaths ?? [],
                StringComparer.OrdinalIgnoreCase);
    }

    private void DetachSessionBindingLocked(
        SessionId sessionId,
        HashSet<WorkspaceBinding> bindingsToDispose)
    {
        if (!_bindings.Remove(sessionId, out var previous))
        {
            return;
        }

        previous.SessionIds.Remove(sessionId);
        if (previous.SessionIds.Count == 0)
        {
            if (_workspaceBindings.TryGetValue(previous.Request.WorkspaceId, out var current)
                && ReferenceEquals(current, previous))
            {
                _workspaceBindings.Remove(previous.Request.WorkspaceId);
            }

            MarkBindingObsolete(previous);
            bindingsToDispose.Add(previous);
        }
    }

    private async Task DisposeBindingsAsync(IEnumerable<WorkspaceBinding> bindings)
    {
        foreach (var binding in bindings)
        {
            await DisposeBindingAsync(binding, CancellationToken.None);
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static void ThrowIfObsolete(WorkspaceBinding binding)
    {
        if (binding.IsObsolete)
        {
            throw new InvalidOperationException("The semantic repository binding is no longer active.");
        }
    }

    private static SemanticRefreshResult CreateCleanResult(
        WorkspaceId workspaceId,
        SemanticRefreshReason reason,
        SemanticConfidenceLevel confidence,
        long dirtyVersion,
        long appliedVersion)
    {
        return new SemanticRefreshResult(
            default,
            workspaceId,
            reason,
            SemanticRefreshMode.Incremental,
            0,
            dirtyVersion,
            appliedVersion,
            confidence,
            TimeSpan.Zero,
            WasRefreshed: false);
    }

    private WorkspaceBinding? GetBinding(SessionId sessionId, bool required)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_gate)
        {
            if (_bindings.TryGetValue(sessionId, out var binding))
            {
                return binding;
            }
        }

        if (required)
        {
            throw new InvalidOperationException("No repository solution is bound for semantic refresh.");
        }

        return null;
    }

    private SessionId[] GetBindingSessionIds(WorkspaceBinding binding)
    {
        lock (_gate)
        {
            return [.. binding.SessionIds];
        }
    }

    private static void MarkBindingObsolete(WorkspaceBinding binding)
    {
        lock (binding.Gate)
        {
            if (binding.IsObsolete)
            {
                return;
            }

            binding.IsObsolete = true;
            if (binding.IsLoading)
            {
                binding.InitialLoadCompletion.TrySetException(
                    new InvalidOperationException(
                        "The semantic repository binding is no longer active."));
            }
        }
    }

    private void QueueChange(WorkspaceBinding binding, SemanticFileChange change)
    {
        var fullPath = NormalizePath(binding, change.Path);
        if (fullPath is null)
        {
            return;
        }

        if (change.Kind == SemanticFileChangeKind.Changed && Directory.Exists(fullPath))
        {
            return;
        }

        if (!IsPotentiallyRelevant(binding, fullPath, change.Kind))
        {
            return;
        }

        lock (binding.Gate)
        {
            if (binding.IsObsolete)
            {
                return;
            }

            binding.DirtyVersion++;
            if (change.Kind is SemanticFileChangeKind.Created
                or SemanticFileChangeKind.Deleted
                or SemanticFileChangeKind.Renamed
                or SemanticFileChangeKind.Uncertain)
            {
                binding.WatcherRestartRequired = true;
            }

            var observedAt = _timeProvider.GetUtcNow();
            binding.BurstStartedAt ??= observedAt;
            binding.LastObservedAt = observedAt;
            if (binding.PendingChanges.Count >= _resourceLimits.MaximumPendingPaths
                && !binding.PendingChanges.ContainsKey(fullPath))
            {
                binding.PendingChanges.Clear();
                binding.RecoveryRequired = true;
            }
            else
            {
                binding.PendingChanges[fullPath] = MergeChange(
                    binding.PendingChanges.GetValueOrDefault(fullPath),
                    change with { Path = fullPath });
            }

            if (!binding.IsLoading)
            {
                _ = EnsureWorkerLocked(binding);
            }
        }
    }

    private void QueueRecovery(WorkspaceBinding binding, bool restartWatcher = false)
    {
        SemanticRefreshMetrics.RecoveryRequested.Add(1);
        lock (binding.Gate)
        {
            if (binding.IsObsolete)
            {
                return;
            }

            binding.DirtyVersion++;
            binding.RecoveryRequired = true;
            binding.WatcherRestartRequired |= restartWatcher;
            var observedAt = _timeProvider.GetUtcNow();
            binding.BurstStartedAt ??= observedAt;
            binding.LastObservedAt = observedAt;
            if (!binding.IsLoading)
            {
                _ = EnsureWorkerLocked(binding);
            }
        }
    }

    private static SemanticFileChange MergeChange(
        SemanticFileChange? current,
        SemanticFileChange replacement)
    {
        if (current is null)
        {
            return replacement;
        }

        var source = current.Source == SemanticRefreshReason.ExternalChange
            || replacement.Source == SemanticRefreshReason.ExternalChange
            ? SemanticRefreshReason.ExternalChange
            : replacement.Source;
        var kind = current.Kind == SemanticFileChangeKind.Uncertain
            || replacement.Kind == SemanticFileChangeKind.Uncertain
            ? SemanticFileChangeKind.Uncertain
            : replacement.Kind;
        return replacement with { Kind = kind, Source = source };
    }

    private bool IsPotentiallyRelevant(
        WorkspaceBinding binding,
        string path,
        SemanticFileChangeKind kind)
    {
        if (kind == SemanticFileChangeKind.Uncertain)
        {
            return true;
        }

        lock (binding.Gate)
        {
            if (binding.IsLoading)
            {
                return !IsIgnoredPath(binding.Request.RepositoryPath, path);
            }
        }

        var inventory = _backend.GetRefreshInventory(binding.Request.WorkspaceId);
        if (inventory.SourceDocuments.Contains(path)
            || inventory.AdditionalDocuments.Contains(path)
            || inventory.AnalyzerConfigDocuments.Contains(path))
        {
            return true;
        }

        if (IsIgnoredPath(binding.Request.RepositoryPath, path))
        {
            return false;
        }

        if (inventory.FullReloadInputs.Contains(path)
            || IsGraphControlPath(path)
            || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(path))
        {
            return true;
        }

        var isLifecycleChange = kind is SemanticFileChangeKind.Created
            or SemanticFileChangeKind.Deleted
            or SemanticFileChangeKind.Renamed;
        return isLifecycleChange;
    }

    private Task EnsureWorkerLocked(WorkspaceBinding binding)
    {
        binding.Worker ??= Task.Run(
            () => ProcessBindingAsync(binding),
            CancellationToken.None);
        return binding.Worker;
    }

    private async Task ProcessBindingAsync(WorkspaceBinding binding)
    {
        var lastFailedTarget = 0L;
        var sequence = new RefreshSequence();
        try
        {
            while (true)
            {
                var delay = GetSettleDelay(binding);
                if (delay is null)
                {
                    if (!await TryPublishConvergedCompletionAsync(
                        binding,
                        sequence,
                        binding.LifetimeCancellation.Token))
                    {
                        continue;
                    }

                    lock (binding.Gate)
                    {
                        if (!binding.HasWork)
                        {
                            return;
                        }
                    }

                    sequence = new RefreshSequence();
                    continue;
                }

                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay.Value, _timeProvider, binding.LifetimeCancellation.Token);
                    continue;
                }

                RefreshBatch batch;
                lock (binding.Gate)
                {
                    ThrowIfObsolete(binding);
                    batch = binding.CaptureBatch();
                }

                try
                {
                    await RefreshBatchAsync(
                        binding,
                        batch,
                        sequence,
                        binding.LifetimeCancellation.Token);
                }
                catch (OperationCanceledException) when (binding.LifetimeCancellation.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    lastFailedTarget = batch.TargetDirtyVersion;
                    RestoreFailedBatch(binding, batch);
                    throw new InvalidOperationException(
                        "Semantic refresh could not establish current state.",
                        exception);
                }
            }
        }
        finally
        {
            lock (binding.Gate)
            {
                binding.Worker = null;
                if (!binding.IsObsolete
                    && binding.HasWork
                    && binding.DirtyVersion > lastFailedTarget)
                {
                    _ = EnsureWorkerLocked(binding);
                }
            }
        }
    }

    private TimeSpan? GetSettleDelay(WorkspaceBinding binding)
    {
        lock (binding.Gate)
        {
            if (binding.IsObsolete || !binding.HasWork)
            {
                return null;
            }

            if (binding.HostMutationInProgress)
            {
                return _settleInterval;
            }

            if (binding.ForceRequestedVersion > binding.ForceAppliedVersion)
            {
                return TimeSpan.Zero;
            }

            var now = _timeProvider.GetUtcNow();
            var settleDeadline = binding.LastObservedAt + _settleInterval;
            var burstDeadline = (binding.BurstStartedAt ?? now) + _maximumBurstWindow;
            var deadline = settleDeadline <= burstDeadline ? settleDeadline : burstDeadline;
            return deadline <= now ? TimeSpan.Zero : deadline - now;
        }
    }

    private async Task EnsureRefreshStartedAsync(
        WorkspaceBinding binding,
        RefreshSequence sequence,
        CancellationToken cancellationToken)
    {
        if (sequence.HasStarted)
        {
            return;
        }

        sequence.MarkStarted(_timeProvider.GetTimestamp());
        foreach (var sessionId in GetBindingSessionIds(binding))
        {
            await PublishSafelyAsync(
                new SemanticRefreshStarted(
                    sessionId,
                    _timeProvider.GetUtcNow(),
                    sequence.RefreshId,
                    binding.Request.WorkspaceId,
                    sequence.Reason,
                    sequence.Mode,
                    sequence.ChangedPaths.Count,
                    sequence.DirtyVersion),
                cancellationToken);
        }
    }

    private async Task<bool> TryPublishConvergedCompletionAsync(
        WorkspaceBinding binding,
        RefreshSequence sequence,
        CancellationToken cancellationToken)
    {
        if (!sequence.HasStarted)
        {
            return true;
        }

        SemanticRefreshResult result;
        lock (binding.Gate)
        {
            ThrowIfObsolete(binding);
            if (binding.HasWork)
            {
                return false;
            }

            var duration = _timeProvider.GetElapsedTime(sequence.StartedTimestamp);
            result = new SemanticRefreshResult(
                sequence.RefreshId,
                binding.Request.WorkspaceId,
                sequence.Reason,
                sequence.Mode,
                sequence.ChangedPaths.Count,
                binding.DirtyVersion,
                binding.AppliedVersion,
                sequence.Confidence,
                duration,
                WasRefreshed: true);
            binding.LastResult = result;
        }

        foreach (var sessionId in GetBindingSessionIds(binding))
        {
            await PublishSafelyAsync(
                new SemanticRefreshCompleted(
                    sessionId,
                    _timeProvider.GetUtcNow(),
                    result.RefreshId,
                    result.WorkspaceId,
                    result.Reason,
                    result.Mode,
                    result.ChangedFileCount,
                    result.DirtyVersion,
                    result.AppliedVersion,
                    result.Confidence,
                    ToElapsedMilliseconds(result.Duration)),
                cancellationToken);
        }

        return true;
    }

    private async Task RefreshBatchAsync(
        WorkspaceBinding binding,
        RefreshBatch batch,
        RefreshSequence sequence,
        CancellationToken cancellationToken)
    {
        var prepared = await PrepareBatchAsync(binding, batch, cancellationToken);
        if (prepared.HasUnstableSnapshot)
        {
            var unstableReason = batch.RecoveryRequired
                ? SemanticRefreshReason.Recovery
                : prepared.Changes.Any(change => change.Source == SemanticRefreshReason.ExternalChange)
                    ? SemanticRefreshReason.ExternalChange
                    : SemanticRefreshReason.HostMutation;
            sequence.RecordAttempt(
                unstableReason,
                SemanticRefreshMode.Full,
                prepared.Changes,
                batch.TargetDirtyVersion);
            await EnsureRefreshStartedAsync(binding, sequence, cancellationToken);
            foreach (var sessionId in GetBindingSessionIds(binding))
            {
                await PublishSafelyAsync(
                    new SemanticRefreshFailed(
                        sessionId,
                        _timeProvider.GetUtcNow(),
                        sequence.RefreshId,
                        binding.Request.WorkspaceId,
                        sequence.Reason,
                        sequence.Mode,
                        sequence.ChangedPaths.Count,
                        sequence.DirtyVersion,
                        binding.AppliedVersion,
                        SemanticRefreshFailureKind.UnstableSnapshot,
                        "Repository files did not remain stable long enough to refresh.",
                        ElapsedMilliseconds: 0),
                    CancellationToken.None);
            }

            RecordCycle(SemanticRefreshMode.Full, "unstable", TimeSpan.Zero);
            throw new InvalidOperationException(
                "Semantic refresh preparation observed an unstable filesystem snapshot.");
        }

        if (!prepared.ForceFull && prepared.Changes.Count == 0)
        {
            lock (binding.Gate)
            {
                ThrowIfObsolete(binding);
                binding.AppliedVersion = Math.Max(binding.AppliedVersion, batch.TargetDirtyVersion);
                var cleanReason = batch.RecoveryRequired
                    ? SemanticRefreshReason.Recovery
                    : batch.Changes.Any(change => change.Source == SemanticRefreshReason.ExternalChange)
                        ? SemanticRefreshReason.ExternalChange
                        : SemanticRefreshReason.HostMutation;
                if (!sequence.HasStarted)
                {
                    binding.LastResult = CreateCleanResult(
                        binding.Request.WorkspaceId,
                        cleanReason,
                        _backend.GetConfidence(binding.Request.WorkspaceId),
                        binding.DirtyVersion,
                        binding.AppliedVersion);
                }
            }

            sequence.RecordNoOp(batch);

            RecordCycle(SemanticRefreshMode.Incremental, "noop", TimeSpan.Zero);
            return;
        }

        var mode = prepared.ForceFull || prepared.Changes.Any(change => change.RequiresFullRefresh)
            ? SemanticRefreshMode.Full
            : SemanticRefreshMode.Incremental;
        var manualForce = batch.TargetForceVersion > binding.ForceAppliedVersion;
        var reason = batch.RecoveryRequired
            ? SemanticRefreshReason.Recovery
            : manualForce
                ? SemanticRefreshReason.Manual
                : prepared.Changes.Any(change => change.Source == SemanticRefreshReason.ExternalChange)
                    ? SemanticRefreshReason.ExternalChange
                    : SemanticRefreshReason.HostMutation;
        sequence.RecordAttempt(reason, mode, prepared.Changes, batch.TargetDirtyVersion);
        await EnsureRefreshStartedAsync(binding, sequence, cancellationToken);
        var startedAt = _timeProvider.GetTimestamp();

        try
        {
            if (batch.RestartWatcher)
            {
                binding.RestartWatching(
                    change => QueueChange(binding, change),
                    () => QueueRecovery(binding, restartWatcher: true));
            }

            var preRefreshInputs = mode == SemanticRefreshMode.Full
                ? await CaptureAuthoritativeInputSnapshotAsync(
                    binding,
                    _backend.GetRefreshInventory(binding.Request.WorkspaceId),
                    additionalPaths: [],
                    binaryAdditionalPaths: [],
                    includePotentialBinaryInputs: false,
                    cancellationToken)
                : null;
            if (preRefreshInputs is not null
                && (!preRefreshInputs.IsComplete
                    || preRefreshInputs.Contents.Values.Any(content => !content.IsStable)))
            {
                throw new InvalidDataException(
                    "Semantic refresh inputs could not be captured within safe resource bounds.");
            }

            async Task<SemanticLoadResult> PublishAsync(CancellationToken publicationToken)
            {
                if (mode == SemanticRefreshMode.Full)
                {
                    return await _backend.RefreshFullAsync(
                        binding.Request.WorkspaceId,
                        publicationToken);
                }

                var documents = prepared.Changes.Select(change =>
                {
                    var text = change.Content.Text
                        ?? throw new InvalidOperationException(
                            "Incremental semantic refresh requires current document text.");
                    return new SemanticDocumentRefresh(
                        change.Path,
                        text,
                        change.Content.Identity);
                }).ToArray();
                return await _backend.RefreshIncrementalAsync(
                    binding.Request.WorkspaceId,
                    documents,
                    publicationToken);
            }

            var loadResult = _publicationGate is null
                ? await PublishAsync(cancellationToken)
                : await _publicationGate.PublishAsync(
                    binding.Request.SessionId,
                    binding.Request.WorkspaceId,
                    PublishAsync,
                    cancellationToken);
            IReadOnlyList<SemanticDocumentRefresh> refreshedDocuments = [];
            SemanticRefreshInventory? refreshedInventory = null;
            IReadOnlyDictionary<string, StableFileContent> confirmedCurrentInputs =
                new Dictionary<string, StableFileContent>(PathComparer);
            IReadOnlyDictionary<string, StableFileContent> confirmedPreparedInputs =
                new Dictionary<string, StableFileContent>(PathComparer);
            if (mode == SemanticRefreshMode.Full)
            {
                refreshedDocuments = await _backend.GetLoadedDocumentsAsync(
                    binding.Request.WorkspaceId,
                    cancellationToken);
                refreshedInventory = _backend.GetRefreshInventory(binding.Request.WorkspaceId);
                var capturedInputs = preRefreshInputs
                    ?? throw new InvalidOperationException(
                        "A full refresh started without an input snapshot.");
                confirmedCurrentInputs = await ReconcileFullRefreshSnapshotAsync(
                    binding,
                    refreshedDocuments,
                    refreshedInventory,
                    capturedInputs,
                    cancellationToken);
                confirmedPreparedInputs = await ConfirmPreparedInputIdentitiesAsync(
                    binding,
                    prepared.Changes,
                    refreshedDocuments,
                    refreshedInventory,
                    confirmedCurrentInputs,
                    cancellationToken);
            }

            var duration = _timeProvider.GetElapsedTime(startedAt);
            lock (binding.Gate)
            {
                ThrowIfObsolete(binding);
                if (mode == SemanticRefreshMode.Full)
                {
                    _ = refreshedInventory
                        ?? throw new InvalidOperationException(
                            "A full refresh completed without a refreshed inventory.");
                    ReplaceAppliedIdentitiesAfterFullRefresh(
                        binding,
                        refreshedDocuments,
                        confirmedCurrentInputs,
                        confirmedPreparedInputs);
                }
                else
                {
                    foreach (var change in prepared.Changes.Where(change => change.Content.IsStable))
                    {
                        binding.AppliedIdentities[change.Path] = change.Content.Identity;
                    }
                }

                RememberAppliedHostEchoIdentities(binding, prepared.Changes);
                foreach (var change in prepared.Changes)
                {
                    binding.HostIdentities.Remove(change.Path);
                    binding.HostMismatchPaths.Remove(change.Path);
                }

                binding.AppliedVersion = Math.Max(binding.AppliedVersion, batch.TargetDirtyVersion);
                if (prepared.ForceFull)
                {
                    binding.ForceAppliedVersion = Math.Max(
                        binding.ForceAppliedVersion,
                        batch.TargetForceVersion);
                }
            }

            if (mode == SemanticRefreshMode.Full)
            {
                try
                {
                    binding.RestartWatching(
                        change => QueueChange(binding, change),
                        () => QueueRecovery(binding, restartWatcher: true));
                }
                catch
                {
                    QueueRecovery(binding, restartWatcher: true);
                    throw;
                }
            }

            sequence.RecordSuccess(loadResult.Confidence);
            RecordCycle(mode, "success", duration);
            lock (binding.Gate)
            {
                if (binding.DirtyVersion > batch.TargetDirtyVersion)
                {
                    SemanticRefreshMetrics.DirtyFollowUps.Add(1);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var duration = _timeProvider.GetElapsedTime(startedAt);
            var failureKind = binding.IsObsolete
                ? SemanticRefreshFailureKind.BindingObsolete
                : exception is InvalidDataException
                    ? SemanticRefreshFailureKind.UnstableSnapshot
                    : SemanticRefreshFailureKind.Infrastructure;
            var safeReason = failureKind switch
            {
                SemanticRefreshFailureKind.BindingObsolete =>
                    "The repository binding changed before semantic refresh completed.",
                SemanticRefreshFailureKind.UnstableSnapshot =>
                    "Repository files did not remain stable long enough to refresh.",
                _ => "Semantic refresh could not establish current state.",
            };
            foreach (var sessionId in GetBindingSessionIds(binding))
            {
                await PublishSafelyAsync(
                    new SemanticRefreshFailed(
                        sessionId,
                        _timeProvider.GetUtcNow(),
                        sequence.RefreshId,
                        binding.Request.WorkspaceId,
                        sequence.Reason,
                        sequence.Mode,
                        sequence.ChangedPaths.Count,
                        sequence.DirtyVersion,
                        binding.AppliedVersion,
                        failureKind,
                        safeReason[..Math.Min(safeReason.Length, _resourceLimits.MaximumSafeReasonLength)],
                        ToElapsedMilliseconds(duration)),
                    CancellationToken.None);
            }

            _logger.LogWarning(
                exception,
                "Semantic refresh failed for workspace {WorkspaceId}",
                binding.Request.WorkspaceId.Value);
            RecordCycle(mode, "failure", duration);
            throw;
        }
    }

    private async Task<PreparedBatch> PrepareBatchAsync(
        WorkspaceBinding binding,
        RefreshBatch batch,
        CancellationToken cancellationToken)
    {
        var inventory = _backend.GetRefreshInventory(binding.Request.WorkspaceId);
        var prepared = new List<PreparedChange>();
        var hasUnstableSnapshot = false;
        foreach (var change in batch.Changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var classification = Classify(
                change,
                inventory,
                binding.Request.RepositoryPath);
            if (classification == SemanticChangeClassification.Irrelevant)
            {
                continue;
            }

            var readAsBinary = inventory.FullReloadInputs.Contains(change.Path);
            var content = await ReadStableFileAsync(
                binding,
                change.Path,
                cancellationToken,
                readAsBinary);
            if (!content.IsStable)
            {
                hasUnstableSnapshot = true;
                prepared.Add(new PreparedChange(
                    change.Path,
                    change.Source,
                    RequiresFullRefresh: true,
                    content));
                continue;
            }

            lock (binding.Gate)
            {
                if (binding.AppliedIdentities.TryGetValue(change.Path, out var appliedIdentity)
                    && string.Equals(appliedIdentity, content.Identity, StringComparison.Ordinal))
                {
                    binding.HostIdentities.Remove(change.Path);
                    binding.HostMismatchPaths.Remove(change.Path);
                    continue;
                }

                if (TryMatchRecentHostEchoIdentity(
                    binding,
                    change.Path,
                    content.Identity,
                    _timeProvider.GetUtcNow()))
                {
                    binding.AppliedIdentities[change.Path] = content.Identity;
                    binding.HostIdentities.Remove(change.Path);
                    binding.HostMismatchPaths.Remove(change.Path);
                    continue;
                }

                var source = !binding.HostMismatchPaths.Contains(change.Path)
                    && binding.HostIdentities.TryGetValue(change.Path, out var hostIdentities)
                    && hostIdentities.Contains(content.Identity)
                    ? SemanticRefreshReason.HostMutation
                    : change.Source;
                var requiresFullRefresh = classification == SemanticChangeClassification.Full
                    || content.Text is null;
                prepared.Add(new PreparedChange(
                    change.Path,
                    source,
                    requiresFullRefresh,
                    content));
            }
        }

        return new PreparedBatch(
            prepared,
            batch.TargetForceVersion > binding.ForceAppliedVersion || batch.RecoveryRequired,
            hasUnstableSnapshot);
    }

    private void RememberAppliedHostEchoIdentities(
        WorkspaceBinding binding,
        IReadOnlyList<PreparedChange> preparedChanges)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var expired in binding.RecentHostEchoIdentities
            .Where(item => item.Value.ExpiresAt <= now)
            .Select(item => item.Key)
            .ToArray())
        {
            binding.RecentHostEchoIdentities.Remove(expired);
        }

        foreach (var change in preparedChanges.Where(
            change => change.Content.IsStable
                && change.Source == SemanticRefreshReason.HostMutation))
        {
            if (!binding.AppliedIdentities.TryGetValue(change.Path, out var appliedIdentity)
                || !string.Equals(
                    appliedIdentity,
                    change.Content.Identity,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (!binding.RecentHostEchoIdentities.ContainsKey(change.Path)
                && binding.RecentHostEchoIdentities.Count >= _resourceLimits.MaximumRecentHostEchoIdentities)
            {
                var oldest = binding.RecentHostEchoIdentities.MinBy(
                    item => item.Value.ExpiresAt);
                binding.RecentHostEchoIdentities.Remove(oldest.Key);
            }

            binding.RecentHostEchoIdentities[change.Path] = new RecentHostEchoIdentity(
                change.Content.Identity,
                now + _recentHostEchoLifetime);
        }
    }

    private static bool TryMatchRecentHostEchoIdentity(
        WorkspaceBinding binding,
        string path,
        string identity,
        DateTimeOffset now)
    {
        if (!binding.RecentHostEchoIdentities.TryGetValue(path, out var recent))
        {
            return false;
        }

        if (recent.ExpiresAt <= now
            || !string.Equals(recent.Identity, identity, StringComparison.Ordinal))
        {
            binding.RecentHostEchoIdentities.Remove(path);
            return false;
        }

        return true;
    }

    private static void ReplaceAppliedIdentitiesAfterFullRefresh(
        WorkspaceBinding binding,
        IReadOnlyList<SemanticDocumentRefresh> refreshedDocuments,
        IReadOnlyDictionary<string, StableFileContent> confirmedCurrentInputs,
        IReadOnlyDictionary<string, StableFileContent> confirmedPreparedInputs)
    {
        binding.AppliedIdentities.Clear();

        foreach (var document in refreshedDocuments)
        {
            binding.AppliedIdentities[document.Path] = document.ContentIdentity;
        }

        foreach (var input in confirmedCurrentInputs)
        {
            binding.AppliedIdentities[input.Key] = input.Value.Identity;
        }

        foreach (var input in confirmedPreparedInputs)
        {
            binding.AppliedIdentities[input.Key] = input.Value.Identity;
        }
    }

    private async Task<IReadOnlyDictionary<string, StableFileContent>> ConfirmPreparedInputIdentitiesAsync(
        WorkspaceBinding binding,
        IReadOnlyList<PreparedChange> preparedChanges,
        IReadOnlyList<SemanticDocumentRefresh> refreshedDocuments,
        SemanticRefreshInventory refreshedInventory,
        IReadOnlyDictionary<string, StableFileContent> confirmedCurrentInputs,
        CancellationToken cancellationToken)
    {
        var loadedDocumentPaths = refreshedDocuments
            .Select(document => document.Path)
            .ToHashSet(PathComparer);
        var confirmed = new Dictionary<string, StableFileContent>(PathComparer);
        foreach (var change in preparedChanges.Where(change => change.Content.IsStable))
        {
            if (loadedDocumentPaths.Contains(change.Path)
                || confirmedCurrentInputs.ContainsKey(change.Path))
            {
                continue;
            }

            var readAsBinary = refreshedInventory.FullReloadInputs.Contains(change.Path);
            var current = await ReadStableFileAsync(
                binding,
                change.Path,
                cancellationToken,
                readAsBinary);
            if (!current.IsStable || (current.Exists && current.Text is null && !readAsBinary))
            {
                throw new InvalidDataException(
                    "A prepared semantic input could not be verified after full refresh.");
            }

            if (string.Equals(current.Identity, change.Content.Identity, StringComparison.Ordinal))
            {
                confirmed[change.Path] = current;
                continue;
            }

            var kind = current.Exists
                ? change.Content.Exists
                    ? SemanticFileChangeKind.Changed
                    : SemanticFileChangeKind.Created
                : SemanticFileChangeKind.Deleted;
            QueueChange(
                binding,
                new SemanticFileChange(
                    binding.Request.SessionId,
                    change.Path,
                    kind));
        }

        return confirmed;
    }

    private async Task<IReadOnlyDictionary<string, StableFileContent>> ReconcileFullRefreshSnapshotAsync(
        WorkspaceBinding binding,
        IReadOnlyList<SemanticDocumentRefresh> refreshedDocuments,
        SemanticRefreshInventory refreshedInventory,
        AuthoritativeInputSnapshot preRefreshInputs,
        CancellationToken cancellationToken)
    {
        await ReconcileBoundDocumentsAsync(binding, refreshedDocuments, cancellationToken);
        var postRefreshInputs = await CaptureAuthoritativeInputSnapshotAsync(
            binding,
            refreshedInventory,
            preRefreshInputs.Contents.Keys,
            preRefreshInputs.BinaryPaths,
            includePotentialBinaryInputs: false,
            cancellationToken);
        return ReconcileAuthoritativeInputSnapshots(
            binding,
            preRefreshInputs,
            postRefreshInputs);
    }

    private IReadOnlyDictionary<string, StableFileContent> ReconcileAuthoritativeInputSnapshots(
        WorkspaceBinding binding,
        AuthoritativeInputSnapshot beforeSnapshot,
        AuthoritativeInputSnapshot currentSnapshot)
    {
        EnsureCompleteAuthoritativeSnapshot(
            beforeSnapshot,
            "Semantic refresh inputs changed outside safe snapshot constraints.");
        EnsureCompleteAuthoritativeSnapshot(
            currentSnapshot,
            "Semantic refresh inputs changed outside safe snapshot constraints.");

        var confirmed = new Dictionary<string, StableFileContent>(PathComparer);
        var paths = new HashSet<string>(beforeSnapshot.Contents.Keys, PathComparer);
        paths.UnionWith(currentSnapshot.Contents.Keys);
        foreach (var path in paths)
        {
            var before = beforeSnapshot.Contents.GetValueOrDefault(path)
                ?? new StableFileContent(false, true, null, "missing");
            var current = currentSnapshot.Contents.GetValueOrDefault(path)
                ?? new StableFileContent(false, true, null, "missing");
            if (!before.IsStable || !current.IsStable)
            {
                throw new InvalidDataException(
                    "Semantic refresh input stability could not be established.");
            }

            if (string.Equals(current.Identity, before.Identity, StringComparison.Ordinal))
            {
                if (currentSnapshot.CurrentPaths.Contains(path))
                {
                    confirmed[path] = current;
                }

                continue;
            }

            var changeKind = current.Exists
                ? before.Exists
                    ? SemanticFileChangeKind.Changed
                    : SemanticFileChangeKind.Created
                : SemanticFileChangeKind.Deleted;
            QueueChange(
                binding,
                new SemanticFileChange(
                    binding.Request.SessionId,
                    path,
                    changeKind));
        }

        return confirmed;
    }

    private async Task SeedAuthoritativeInputIdentitiesAsync(
        WorkspaceBinding binding,
        CancellationToken cancellationToken)
    {
        AuthoritativeInputSnapshot? initialSnapshot;
        lock (binding.Gate)
        {
            initialSnapshot = binding.InitialInputSnapshot;
        }

        var currentSnapshot = await CaptureAuthoritativeInputSnapshotAsync(
            binding,
            _backend.GetRefreshInventory(binding.Request.WorkspaceId),
            initialSnapshot is null ? [] : initialSnapshot.Contents.Keys,
            initialSnapshot is null ? [] : initialSnapshot.BinaryPaths,
            includePotentialBinaryInputs: false,
            cancellationToken);
        EnsureCompleteAuthoritativeSnapshot(
            currentSnapshot,
            "Semantic binding inputs could not be captured within safe resource bounds.");
        if (initialSnapshot is null)
        {
            return;
        }

        var confirmed = ReconcileAuthoritativeInputSnapshots(
            binding,
            initialSnapshot,
            currentSnapshot);
        lock (binding.Gate)
        {
            ThrowIfObsolete(binding);
            binding.InitialInputSnapshot = null;
            foreach (var input in confirmed)
            {
                binding.AppliedIdentities[input.Key] = input.Value.Identity;
            }
        }
    }

    private async Task<AuthoritativeInputSnapshot> CaptureAuthoritativeInputSnapshotAsync(
        WorkspaceBinding binding,
        SemanticRefreshInventory inventory,
        IEnumerable<string> additionalPaths,
        IEnumerable<string> binaryAdditionalPaths,
        bool includePotentialBinaryInputs,
        CancellationToken cancellationToken)
    {
        var paths = new HashSet<string>(PathComparer);
        var currentPaths = new HashSet<string>(PathComparer);
        var binaryPaths = new HashSet<string>(PathComparer);
        var isComplete = AddCurrentFullReloadInputs(
            binding,
            inventory.FullReloadInputs,
            paths,
            currentPaths,
            binaryPaths);
        isComplete &= AddCurrentGraphControlInputs(
            binding,
            paths,
            currentPaths,
            binaryPaths,
            includePotentialBinaryInputs);

        foreach (var path in additionalPaths)
        {
            var normalized = NormalizePath(binding, path);
            if (normalized is null)
            {
                isComplete = false;
                continue;
            }

            if (!paths.Contains(normalized) && paths.Count >= _resourceLimits.MaximumAuthoritativeInputPaths)
            {
                isComplete = false;
                break;
            }

            paths.Add(normalized);
        }

        foreach (var path in binaryAdditionalPaths)
        {
            var normalized = NormalizePath(binding, path);
            if (normalized is null)
            {
                isComplete = false;
                continue;
            }

            binaryPaths.Add(normalized);
        }

        var contents = new Dictionary<string, StableFileContent>(PathComparer);
        var snapshotBytes = 0L;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pathLength = TryGetSafeFileLength(binding, path);
            if (!pathLength.HasValue
                || pathLength.Value > _resourceLimits.MaximumAuthoritativeSnapshotBytes - snapshotBytes)
            {
                isComplete = false;
                break;
            }

            snapshotBytes += pathLength.Value;
            var content = await ReadStableFileAsync(
                binding,
                path,
                cancellationToken,
                readAsBinary: binaryPaths.Contains(path));
            contents[path] = content with { Text = null };
        }

        return new AuthoritativeInputSnapshot(
            contents,
            currentPaths,
            binaryPaths,
            isComplete);
    }

    private long? TryGetSafeFileLength(WorkspaceBinding binding, string path)
    {
        if (!IsSafeForRead(binding, path))
        {
            return null;
        }

        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private bool AddCurrentFullReloadInputs(
        WorkspaceBinding binding,
        IEnumerable<string> inputs,
        HashSet<string> paths,
        HashSet<string> currentPaths,
        HashSet<string> binaryPaths)
    {
        var isComplete = true;
        foreach (var input in inputs)
        {
            var normalized = NormalizePath(binding, input);
            if (normalized is null)
            {
                isComplete = false;
                continue;
            }

            if (IsIgnoredPath(binding.Request.RepositoryPath, normalized))
            {
                continue;
            }

            paths.Add(normalized);
            currentPaths.Add(normalized);
            binaryPaths.Add(normalized);
            if (paths.Count > _resourceLimits.MaximumAuthoritativeInputPaths)
            {
                return false;
            }
        }

        return isComplete;
    }

    private bool AddCurrentGraphControlInputs(
        WorkspaceBinding binding,
        HashSet<string> paths,
        HashSet<string> currentPaths,
        HashSet<string> binaryPaths,
        bool includePotentialBinaryInputs)
    {
        var isComplete = true;
        var directories = new Stack<(string Path, int Depth)>();
        directories.Push((binding.Request.RepositoryPath, 0));
        var scannedEntries = 0;
        while (directories.Count > 0)
        {
            var (directory, depth) = directories.Pop();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(
                    directory,
                    "*",
                    SearchOption.TopDirectoryOnly))
                {
                    scannedEntries++;
                    if (scannedEntries > _resourceLimits.MaximumGraphScanEntries)
                    {
                        return false;
                    }

                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        if (IsIgnoredPath(binding.Request.RepositoryPath, entry)
                            || NormalizePath(binding, entry) is null)
                        {
                            continue;
                        }

                        if (depth >= _resourceLimits.MaximumGraphScanDepth)
                        {
                            isComplete = false;
                            continue;
                        }

                        directories.Push((entry, depth + 1));
                        continue;
                    }

                    if (IsIgnoredPath(binding.Request.RepositoryPath, entry))
                    {
                        continue;
                    }

                    var isGraphControl = IsGraphControlPath(entry);
                    var isPotentialBinaryInput = includePotentialBinaryInputs
                        && IsPotentialBinaryInputPath(entry);
                    if (!isGraphControl && !isPotentialBinaryInput)
                    {
                        continue;
                    }

                    var normalized = NormalizePath(binding, entry);
                    if (normalized is null)
                    {
                        continue;
                    }

                    if (!paths.Contains(normalized)
                        && paths.Count >= _resourceLimits.MaximumAuthoritativeInputPaths)
                    {
                        return false;
                    }

                    paths.Add(normalized);
                    if (isGraphControl)
                    {
                        currentPaths.Add(normalized);
                    }

                    if (isPotentialBinaryInput)
                    {
                        binaryPaths.Add(normalized);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                isComplete = false;
            }
        }

        var solutionPath = NormalizePath(binding, binding.Request.SolutionPath);
        if (solutionPath is null)
        {
            return false;
        }

        if (!paths.Contains(solutionPath) && paths.Count >= _resourceLimits.MaximumAuthoritativeInputPaths)
        {
            return false;
        }

        paths.Add(solutionPath);
        currentPaths.Add(solutionPath);
        return isComplete;
    }

    private static void EnsureCompleteAuthoritativeSnapshot(
        AuthoritativeInputSnapshot snapshot,
        string message)
    {
        if (!snapshot.IsComplete || snapshot.Contents.Values.Any(content => !content.IsStable))
        {
            throw new InvalidDataException(message);
        }
    }

    private static bool IsPotentialBinaryInputPath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".netmodule", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".winmd", StringComparison.OrdinalIgnoreCase);
    }

    private static SemanticChangeClassification Classify(
        SemanticFileChange change,
        SemanticRefreshInventory inventory,
        string repositoryPath)
    {
        if (change.Kind == SemanticFileChangeKind.Uncertain)
        {
            return SemanticChangeClassification.Full;
        }

        var path = change.Path;
        var isSourceDocument = inventory.SourceDocuments.Contains(path);
        var isAdditionalDocument = inventory.AdditionalDocuments.Contains(path);
        var isAnalyzerConfigDocument = inventory.AnalyzerConfigDocuments.Contains(path);
        var isFullReloadInput = inventory.FullReloadInputs.Contains(path);
        var isKnownTextDocument = isSourceDocument
            || isAdditionalDocument
            || isAnalyzerConfigDocument;
        if (!isKnownTextDocument && IsIgnoredPath(repositoryPath, path))
        {
            return SemanticChangeClassification.Irrelevant;
        }

        if (IsGraphControlPath(path))
        {
            return SemanticChangeClassification.Full;
        }

        if (isAdditionalDocument || isAnalyzerConfigDocument || isFullReloadInput)
        {
            return SemanticChangeClassification.Full;
        }

        if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return isSourceDocument
                && File.Exists(path)
                ? SemanticChangeClassification.Incremental
                : SemanticChangeClassification.Full;
        }

        if (change.Kind is SemanticFileChangeKind.Created
            or SemanticFileChangeKind.Deleted
            or SemanticFileChangeKind.Renamed
            || Directory.Exists(path))
        {
            return SemanticChangeClassification.Full;
        }

        return isSourceDocument
            ? SemanticChangeClassification.Full
            : SemanticChangeClassification.Irrelevant;
    }

    private static bool IsGraphControlPath(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ruleset", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".globalconfig", StringComparison.OrdinalIgnoreCase)
            || name.Equals("global.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Directory.Packages.props", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Directory.Build.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIgnoredPath(string repositoryPath, string path)
    {
        string normalized;
        try
        {
            normalized = Path.GetRelativePath(repositoryPath, path).Replace('\\', '/');
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException)
        {
            return true;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(IsIgnoredDirectorySegment))
        {
            return true;
        }

        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        return name.StartsWith("~", StringComparison.Ordinal)
            || name.EndsWith("~", StringComparison.Ordinal)
            || extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".swp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".swo", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsIgnoredDirectorySegment(string segment)
    {
        return segment.Equals(".codegraph", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".idea", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".inbox", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".threadsmith", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".vs", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".vscode", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("artifacts", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("TestResults", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlySet<string> CaptureRelevantWatcherEntries(
        SemanticLoadRequest request,
        IReadOnlyList<string> roots,
        int scanEntryLimit)
    {
        var entriesByPath = new HashSet<string>(PathComparer);
        var inspectedEntryCount = 0;
        foreach (var root in roots)
        {
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(root);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                continue;
            }

            try
            {
                foreach (var entry in entries)
                {
                    if (++inspectedEntryCount > scanEntryLimit)
                    {
                        throw new InvalidDataException(
                            "Semantic repository monitoring exceeds the safe entry bound.");
                    }

                    var normalized = NormalizePath(request, entry);
                    if (normalized is null || IsIgnoredPath(request.RepositoryPath, normalized))
                    {
                        continue;
                    }

                    entriesByPath.Add(normalized);
                }
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                continue;
            }
        }

        return entriesByPath;
    }

    private static string? NormalizePath(WorkspaceBinding binding, string path)
    {
        return NormalizePath(binding.Request, path);
    }

    private static string? NormalizePath(SemanticLoadRequest request, string path)
    {
        try
        {
            var fullPath = Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(
                    path.Replace('/', Path.DirectorySeparatorChar),
                    request.RepositoryPath);
            var relativePath = Path.GetRelativePath(request.RepositoryPath, fullPath);
            if (relativePath.Equals("..", StringComparison.Ordinal)
                || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || Path.IsPathRooted(relativePath)
                || RepositoryPathPolicy.IsProhibited(
                    relativePath.Replace('\\', '/'),
                    request.ProhibitedPaths ?? []))
            {
                return null;
            }

            return SemanticPathSafety.HasReparseComponent(request.RepositoryPath, fullPath)
                ? null
                : fullPath;
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task<StableFileContent> ReadStableFileAsync(
        WorkspaceBinding binding,
        string path,
        CancellationToken cancellationToken,
        bool readAsBinary = false)
    {
        if (!IsSafeForRead(binding, path))
        {
            return CreateUnsafeSnapshot(path);
        }

        if (_fileSnapshotReader is not null)
        {
            var snapshot = await _fileSnapshotReader.ReadAsync(
                path,
                readAsBinary,
                cancellationToken);
            if (!IsSafeForRead(binding, path))
            {
                return CreateUnsafeSnapshot(path);
            }

            return new StableFileContent(
                snapshot.Exists,
                snapshot.IsStable,
                snapshot.Text,
                snapshot.Identity);
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsSafeForRead(binding, path))
            {
                return CreateUnsafeSnapshot(path);
            }

            if (!File.Exists(path))
            {
                return new StableFileContent(false, true, null, "missing");
            }

            var before = new FileInfo(path);
            if (before.Length > _resourceLimits.MaximumStableReadBytes)
            {
                if (!IsSafeForRead(binding, path))
                {
                    return CreateUnsafeSnapshot(path);
                }

                return new StableFileContent(
                    true,
                    true,
                    null,
                    $"oversize:{before.Length}:{before.LastWriteTimeUtc.Ticks}");
            }

            string? text = null;
            byte[]? bytes = null;
            try
            {
                if (!IsSafeForRead(binding, path))
                {
                    return CreateUnsafeSnapshot(path);
                }

                if (readAsBinary)
                {
                    bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                }
                else
                {
                    text = await File.ReadAllTextAsync(path, cancellationToken);
                }
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new StableFileContent(File.Exists(path), false, null, "unreadable");
            }

            var after = new FileInfo(path);
            if (!IsSafeForRead(binding, path))
            {
                return CreateUnsafeSnapshot(path);
            }

            if (after.Exists
                && before.Length == after.Length
                && before.LastWriteTimeUtc == after.LastWriteTimeUtc)
            {
                var identityBytes = bytes ?? Encoding.UTF8.GetBytes(text ?? string.Empty);
                var identity = Convert.ToHexString(SHA256.HashData(identityBytes));
                return new StableFileContent(true, true, text, identity);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), _timeProvider, cancellationToken);
        }

        return new StableFileContent(File.Exists(path), false, null, "unstable");
    }

    private static StableFileContent CreateUnsafeSnapshot(string path)
    {
        return new StableFileContent(File.Exists(path), false, null, "unsafe-path-transition");
    }

    private bool IsSafeForRead(WorkspaceBinding binding, string path)
    {
        if (_pathSafetyValidator is not null)
        {
            return _pathSafetyValidator.IsSafe(binding.Request.RepositoryPath, path);
        }

        var normalized = NormalizePath(binding, path);
        return normalized is not null && PathComparer.Equals(normalized, path);
    }

    private async Task ReconcileBoundDocumentsAsync(
        WorkspaceBinding binding,
        IReadOnlyList<SemanticDocumentRefresh> loadedDocuments,
        CancellationToken cancellationToken)
    {
        foreach (var document in loadedDocuments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await ReadStableFileAsync(binding, document.Path, cancellationToken);
            if (!current.IsStable)
            {
                throw new InvalidDataException(
                    "A loaded semantic text document could not be verified as stable.");
            }

            if (current.Exists && current.Text is null)
            {
                throw new InvalidDataException(
                    "A loaded semantic text document exceeded the bounded verification limit.");
            }

            if (!string.Equals(
                current.Identity,
                document.ContentIdentity,
                StringComparison.Ordinal))
            {
                var changeKind = current.Exists
                    ? SemanticFileChangeKind.Changed
                    : SemanticFileChangeKind.Deleted;
                QueueChange(
                    binding,
                    new SemanticFileChange(
                        binding.Request.SessionId,
                        document.Path,
                        changeKind));
            }
        }
    }

    private void RestoreFailedBatch(WorkspaceBinding binding, RefreshBatch batch)
    {
        lock (binding.Gate)
        {
            if (binding.IsObsolete)
            {
                return;
            }

            foreach (var change in batch.Changes)
            {
                binding.PendingChanges[change.Path] = MergeChange(
                    binding.PendingChanges.GetValueOrDefault(change.Path),
                    change);
            }

            binding.RecoveryRequired |= batch.RecoveryRequired;
            binding.WatcherRestartRequired |= batch.RestartWatcher;
            binding.BurstStartedAt ??= _timeProvider.GetUtcNow();
            binding.LastObservedAt = _timeProvider.GetUtcNow();
        }
    }

    private async Task PublishSafelyAsync(
        IDomainEvent domainEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await _events.PublishAsync(domainEvent, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Semantic refresh lifecycle publication failed for {EventType}",
                domainEvent.GetType().Name);
        }
    }

    private static long ToElapsedMilliseconds(TimeSpan elapsed)
    {
        return Math.Max(0, (long)elapsed.TotalMilliseconds);
    }

    private static void RecordCycle(
        SemanticRefreshMode mode,
        string outcome,
        TimeSpan duration)
    {
        var modeTag = new KeyValuePair<string, object?>("mode", mode.ToString());
        var outcomeTag = new KeyValuePair<string, object?>("outcome", outcome);
        SemanticRefreshMetrics.Cycles.Add(1, modeTag, outcomeTag);
        SemanticRefreshMetrics.Duration.Record(duration.TotalMilliseconds, modeTag, outcomeTag);
    }

    private async Task DisposeBindingAsync(
        WorkspaceBinding binding,
        CancellationToken cancellationToken)
    {
        Task? worker;
        lock (binding.Gate)
        {
            if (binding.DisposalStarted)
            {
                return;
            }

            binding.DisposalStarted = true;
            binding.IsObsolete = true;
            if (binding.HasWork || binding.Worker is not null)
            {
                SemanticRefreshMetrics.ObsoleteDiscarded.Add(1);
            }

            if (binding.IsLoading)
            {
                binding.InitialLoadCompletion.TrySetException(
                    new InvalidOperationException(
                        "The semantic repository binding is no longer active."));
            }

            worker = binding.Worker;
        }

        binding.StopWatching();
        await binding.LifetimeCancellation.CancelAsync();
        if (worker is not null)
        {
            try
            {
                await worker.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (binding.LifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogDebug(
                    exception,
                    "Observed a failed semantic refresh while disposing workspace {WorkspaceId}",
                    binding.Request.WorkspaceId.Value);
            }
        }

        binding.LifetimeCancellation.Dispose();
    }

    private enum SemanticChangeClassification
    {
        Irrelevant,
        Incremental,
        Full,
    }

    private sealed class WorkspaceBinding
    {
        private readonly Lock _watcherGate = new();
        private readonly Func<string, FileSystemWatcher> _watcherFactory;
        private readonly int _maximumWatcherDirectories;
        private readonly int _watcherScanEntryLimit;
        private readonly bool _watchFileSystem;
        private readonly List<FileSystemWatcher> _watchers = [];

        public WorkspaceBinding(
            SemanticLoadRequest request,
            long generation,
            bool watchFileSystem,
            Func<string, FileSystemWatcher> watcherFactory,
            int watcherScanEntryLimit,
            int maximumWatcherDirectories)
        {
            Request = request;
            Generation = generation;
            _watchFileSystem = watchFileSystem;
            _watcherFactory = watcherFactory;
            _watcherScanEntryLimit = watcherScanEntryLimit;
            _maximumWatcherDirectories = maximumWatcherDirectories;
            AppliedIdentities = new Dictionary<string, string>(PathComparer);
            HostIdentities = new Dictionary<string, HashSet<string>>(PathComparer);
            HostMismatchPaths = new HashSet<string>(PathComparer);
            PendingChanges = new Dictionary<string, SemanticFileChange>(PathComparer);
            RecentHostEchoIdentities = new Dictionary<string, RecentHostEchoIdentity>(PathComparer);
            SessionIds = [];
        }

        public Dictionary<string, string> AppliedIdentities { get; }

        public Dictionary<MutationSetId, IReadOnlyDictionary<string, bool>> ActiveHostMutations { get; } = [];

        public long AppliedVersion { get; set; }

        public Exception? BindingFailure { get; set; }

        public DateTimeOffset? BurstStartedAt { get; set; }

        public long DirtyVersion { get; set; }

        public bool DisposalStarted { get; set; }

        public long ForceAppliedVersion { get; set; }

        public long ForceRequestedVersion { get; set; }

        public long Generation { get; }

        public Lock Gate { get; } = new();

        public bool HasWork => DirtyVersion > AppliedVersion
            || ForceRequestedVersion > ForceAppliedVersion;

        public Dictionary<string, HashSet<string>> HostIdentities { get; }

        public HashSet<string> HostMismatchPaths { get; }

        public bool HostMutationInProgress => ActiveHostMutations.Count > 0;

        public AuthoritativeInputSnapshot? InitialInputSnapshot { get; set; }

        public TaskCompletionSource InitialLoadCompletion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsLoading { get; set; }

        public bool IsObsolete { get; set; }

        public DateTimeOffset LastObservedAt { get; set; }

        public SemanticRefreshResult? LastResult { get; set; }

        public CancellationTokenSource LifetimeCancellation { get; } = new();

        public Dictionary<string, SemanticFileChange> PendingChanges { get; }

        public bool RecoveryRequired { get; set; }

        public Dictionary<string, RecentHostEchoIdentity> RecentHostEchoIdentities { get; }

        public SemanticLoadRequest Request { get; }

        public HashSet<SessionId> SessionIds { get; }

        public Task? Worker { get; set; }

        public bool WatcherRestartRequired { get; set; }

        public RefreshBatch CaptureBatch()
        {
            var batch = new RefreshBatch(
                PendingChanges.Values.ToArray(),
                DirtyVersion,
                ForceRequestedVersion,
                RecoveryRequired,
                WatcherRestartRequired);
            PendingChanges.Clear();
            RecoveryRequired = false;
            WatcherRestartRequired = false;
            BurstStartedAt = null;
            return batch;
        }

        public void StartWatching(
            Action<SemanticFileChange> changed,
            Action failed)
        {
            if (!_watchFileSystem)
            {
                return;
            }

            lock (_watcherGate)
            {
                try
                {
                    var roots = GetWatcherRoots();
                    var before = CaptureWatcherTopology(roots);
                    var existingPaths = new HashSet<string>(
                        _watchers.Select(watcher => watcher.Path),
                        PathComparer);
                    foreach (var path in roots)
                    {
                        if (!existingPaths.Add(path))
                        {
                            continue;
                        }

                        var watcher = CreateWatcher(
                            path,
                            changed,
                            failed);
                        _watchers.Add(watcher);
                        watcher.EnableRaisingEvents = true;
                    }

                    QueueTopologyChangeIfNeeded(
                        before,
                        CaptureWatcherTopology(GetWatcherRoots()),
                        changed);
                }
                catch
                {
                    DisposeWatchers();
                    throw;
                }
            }
        }

        public void RestartWatching(
            Action<SemanticFileChange> changed,
            Action failed)
        {
            if (!_watchFileSystem)
            {
                return;
            }

            lock (_watcherGate)
            {
                var replacements = new List<FileSystemWatcher>();
                try
                {
                    var roots = GetWatcherRoots();
                    var before = CaptureWatcherTopology(roots);
                    foreach (var path in roots)
                    {
                        var replacement = CreateWatcher(path, changed, failed);
                        replacements.Add(replacement);
                        replacement.EnableRaisingEvents = true;
                    }

                    var replaced = _watchers.ToArray();
                    _watchers.Clear();
                    _watchers.AddRange(replacements);
                    replacements.Clear();
                    foreach (var watcher in replaced)
                    {
                        watcher.Dispose();
                    }

                    QueueTopologyChangeIfNeeded(
                        before,
                        CaptureWatcherTopology(GetWatcherRoots()),
                        changed);
                }
                catch
                {
                    foreach (var watcher in replacements)
                    {
                        watcher.Dispose();
                    }

                    throw;
                }
            }
        }

        public void StopWatching()
        {
            lock (_watcherGate)
            {
                DisposeWatchers();
            }
        }

        private FileSystemWatcher CreateWatcher(
            string path,
            Action<SemanticFileChange> changed,
            Action failed)
        {
            var watcher = _watcherFactory(path);
            watcher.IncludeSubdirectories = false;
            watcher.InternalBufferSize = 4 * 1024;
            watcher.NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size;
            watcher.Changed += (_, args) => changed(CreateChange(args, SemanticFileChangeKind.Changed));
            watcher.Created += (_, args) => changed(CreateChange(args, SemanticFileChangeKind.Created));
            watcher.Deleted += (_, args) => changed(CreateChange(args, SemanticFileChangeKind.Deleted));
            watcher.Renamed += (_, args) => changed(new SemanticFileChange(
                Request.SessionId,
                args.FullPath,
                SemanticFileChangeKind.Renamed,
                SemanticRefreshReason.ExternalChange,
                args.OldFullPath));
            watcher.Renamed += (_, args) => changed(new SemanticFileChange(
                Request.SessionId,
                args.OldFullPath,
                SemanticFileChangeKind.Deleted,
                SemanticRefreshReason.ExternalChange));
            watcher.Error += (_, _) => failed();
            return watcher;
        }

        private void DisposeWatchers()
        {
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        private IReadOnlyList<string> GetWatcherRoots()
        {
            var roots = new List<string>();
            var scheduled = new HashSet<string>(PathComparer);
            var pending = new Stack<string>();
            var inspectedDirectoryCount = 0;
            scheduled.Add(Request.RepositoryPath);
            pending.Push(Request.RepositoryPath);
            while (pending.TryPop(out var current))
            {
                roots.Add(current);
                try
                {
                    foreach (var directory in Directory.EnumerateDirectories(current))
                    {
                        if (++inspectedDirectoryCount > _watcherScanEntryLimit)
                        {
                            throw new InvalidDataException(
                                "Semantic repository monitoring exceeds the safe entry bound.");
                        }

                        var relative = Path.GetRelativePath(Request.RepositoryPath, directory)
                            .Replace('\\', '/');
                        if (IsIgnoredDirectorySegment(Path.GetFileName(directory))
                            || RepositoryPathPolicy.IsProhibited(
                                relative,
                                Request.ProhibitedPaths ?? [])
                            || IsReparsePoint(directory))
                        {
                            continue;
                        }

                        if (scheduled.Contains(directory))
                        {
                            continue;
                        }

                        if (scheduled.Count >= _maximumWatcherDirectories)
                        {
                            throw new InvalidDataException(
                                "Semantic repository monitoring exceeds the safe directory bound.");
                        }

                        scheduled.Add(directory);
                        pending.Push(directory);
                    }
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException)
                {
                }
            }

            foreach (var path in AppliedIdentities.Keys)
            {
                var directory = Path.GetDirectoryName(path);
                if (directory is null
                    || scheduled.Contains(directory)
                    || !IsSafeExplicitWatcherDirectory(directory))
                {
                    continue;
                }

                if (scheduled.Count >= _maximumWatcherDirectories)
                {
                    throw new InvalidDataException(
                        "Semantic repository monitoring exceeds the safe directory bound.");
                }

                scheduled.Add(directory);
                roots.Add(directory);
            }

            return roots;
        }

        private IReadOnlySet<string> CaptureWatcherTopology(IReadOnlyList<string> roots)
        {
            return CaptureRelevantWatcherEntries(Request, roots, _watcherScanEntryLimit);
        }

        private void QueueTopologyChangeIfNeeded(
            IReadOnlySet<string> before,
            IReadOnlySet<string> after,
            Action<SemanticFileChange> changed)
        {
            if (before.SetEquals(after))
            {
                return;
            }

            changed(new SemanticFileChange(
                Request.SessionId,
                Request.RepositoryPath,
                SemanticFileChangeKind.Uncertain,
                SemanticRefreshReason.ExternalChange));
        }

        private bool IsSafeExplicitWatcherDirectory(string directory)
        {
            string relative;
            try
            {
                relative = Path.GetRelativePath(Request.RepositoryPath, directory)
                    .Replace('\\', '/');
            }
            catch (Exception exception) when (exception is ArgumentException
                or IOException
                or NotSupportedException)
            {
                return false;
            }

            return !relative.Equals("..", StringComparison.Ordinal)
                && !relative.StartsWith("../", StringComparison.Ordinal)
                && !Path.IsPathRooted(relative)
                && !RepositoryPathPolicy.IsProhibited(
                    relative,
                    Request.ProhibitedPaths ?? [])
                && Directory.Exists(directory)
                && !IsReparsePoint(directory);
        }

        private static bool IsReparsePoint(string path)
        {
            try
            {
                return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException)
            {
                return true;
            }
        }

        private SemanticFileChange CreateChange(
            FileSystemEventArgs args,
            SemanticFileChangeKind kind)
        {
            return new SemanticFileChange(
                Request.SessionId,
                args.FullPath,
                kind,
                SemanticRefreshReason.ExternalChange);
        }
    }

    private sealed record RefreshBatch(
        IReadOnlyList<SemanticFileChange> Changes,
        long TargetDirtyVersion,
        long TargetForceVersion,
        bool RecoveryRequired,
        bool RestartWatcher);

    private sealed record PreparedBatch(
        IReadOnlyList<PreparedChange> Changes,
        bool ForceFull,
        bool HasUnstableSnapshot);

    private sealed record PreparedChange(
        string Path,
        SemanticRefreshReason Source,
        bool RequiresFullRefresh,
        StableFileContent Content);

    private sealed record StableFileContent(
        bool Exists,
        bool IsStable,
        string? Text,
        string Identity);

    private sealed record RecentHostEchoIdentity(
        string Identity,
        DateTimeOffset ExpiresAt);

    private sealed record AuthoritativeInputSnapshot(
        IReadOnlyDictionary<string, StableFileContent> Contents,
        IReadOnlySet<string> CurrentPaths,
        IReadOnlySet<string> BinaryPaths,
        bool IsComplete);

    private sealed class RefreshSequence
    {
        private bool _hasReason;

        public HashSet<string> ChangedPaths { get; } = new(PathComparer);

        public SemanticConfidenceLevel Confidence { get; private set; }

        public long DirtyVersion { get; private set; }

        public bool HasStarted { get; private set; }

        public SemanticRefreshMode Mode { get; private set; } = SemanticRefreshMode.Incremental;

        public SemanticRefreshReason Reason { get; private set; } = SemanticRefreshReason.HostMutation;

        public SemanticRefreshId RefreshId { get; } = SemanticRefreshId.New();

        public long StartedTimestamp { get; private set; }

        public void MarkStarted(long timestamp)
        {
            HasStarted = true;
            StartedTimestamp = timestamp;
        }

        public void RecordAttempt(
            SemanticRefreshReason reason,
            SemanticRefreshMode mode,
            IReadOnlyList<PreparedChange> changes,
            long dirtyVersion)
        {
            RecordReason(reason);
            if (mode == SemanticRefreshMode.Full)
            {
                Mode = SemanticRefreshMode.Full;
            }

            foreach (var change in changes)
            {
                ChangedPaths.Add(change.Path);
            }

            DirtyVersion = Math.Max(DirtyVersion, dirtyVersion);
        }

        public void RecordNoOp(RefreshBatch batch)
        {
            foreach (var change in batch.Changes)
            {
                ChangedPaths.Add(change.Path);
            }

            DirtyVersion = Math.Max(DirtyVersion, batch.TargetDirtyVersion);
        }

        public void RecordSuccess(SemanticConfidenceLevel confidence)
        {
            Confidence = confidence;
        }

        private void RecordReason(SemanticRefreshReason reason)
        {
            if (!_hasReason || GetReasonPriority(reason) > GetReasonPriority(Reason))
            {
                Reason = reason;
                _hasReason = true;
            }
        }

        private static int GetReasonPriority(SemanticRefreshReason reason)
        {
            return reason switch
            {
                SemanticRefreshReason.Recovery => 4,
                SemanticRefreshReason.Manual => 3,
                SemanticRefreshReason.ExternalChange => 2,
                SemanticRefreshReason.HostMutation => 1,
                _ => 0,
            };
        }
    }
}

/// <summary>Exact loaded Roslyn document membership used for conservative refresh classification.</summary>
public sealed record SemanticRefreshInventory(
    IReadOnlySet<string> SourceDocuments,
    IReadOnlySet<string> AdditionalDocuments,
    IReadOnlySet<string> AnalyzerConfigDocuments,
    IReadOnlySet<string> FullReloadInputs);

/// <summary>Describes whether lifecycle binding installed or reused a workspace authority.</summary>
internal sealed record SemanticBindingBeginResult(
    long Generation,
    bool ReusedWorkspaceBinding);

/// <summary>Test seam for semantic refresh execution without filesystem timing.</summary>
internal interface ISemanticRefreshBackend
{
    /// <summary>Gets current workspace confidence.</summary>
    SemanticConfidenceLevel GetConfidence(WorkspaceId workspaceId);

    /// <summary>Gets exact loaded semantic document membership by Roslyn document kind.</summary>
    SemanticRefreshInventory GetRefreshInventory(WorkspaceId workspaceId);

    /// <summary>Gets current loaded document content identities.</summary>
    Task<IReadOnlyList<SemanticDocumentRefresh>> GetLoadedDocumentsAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken);

    /// <summary>Publishes a prepared incremental replacement.</summary>
    Task<SemanticLoadResult> RefreshIncrementalAsync(
        WorkspaceId workspaceId,
        IReadOnlyList<SemanticDocumentRefresh> documents,
        CancellationToken cancellationToken);

    /// <summary>Performs an authoritative full reload.</summary>
    Task<SemanticLoadResult> RefreshFullAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken);
}

/// <summary>Deterministic file snapshot seam for semantic refresh tests.</summary>
internal interface ISemanticFileSnapshotReader
{
    /// <summary>Reads one path as a stable semantic content identity.</summary>
    Task<SemanticFileSnapshot> ReadAsync(
        string path,
        bool readAsBinary,
        CancellationToken cancellationToken);
}

/// <summary>Deterministic seam for path identity and reparse-transition validation.</summary>
internal interface ISemanticPathSafetyValidator
{
    /// <summary>Returns whether the exact path identity remains confined and free of reparses.</summary>
    bool IsSafe(string repositoryPath, string path);
}

/// <summary>One file observation used during refresh preparation.</summary>
internal sealed record SemanticFileSnapshot(
    bool Exists,
    bool IsStable,
    string? Text,
    string Identity);

/// <summary>Adapts the semantic engine registry to refresh execution.</summary>
internal sealed class RegistrySemanticRefreshBackend : ISemanticRefreshBackend
{
    private readonly SemanticEngineRegistry _semanticEngines;

    /// <summary>Initializes a new instance of the <see cref="RegistrySemanticRefreshBackend"/> class.</summary>
    public RegistrySemanticRefreshBackend(SemanticEngineRegistry semanticEngines)
    {
        ArgumentNullException.ThrowIfNull(semanticEngines);
        _semanticEngines = semanticEngines;
    }

    /// <inheritdoc />
    public SemanticConfidenceLevel GetConfidence(WorkspaceId workspaceId)
    {
        return _semanticEngines.GetConfidence(workspaceId);
    }

    /// <inheritdoc />
    public SemanticRefreshInventory GetRefreshInventory(WorkspaceId workspaceId)
    {
        return _semanticEngines.GetEngine(workspaceId).GetRefreshInventory();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SemanticDocumentRefresh>> GetLoadedDocumentsAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        return _semanticEngines.GetEngine(workspaceId)
            .GetLoadedDocumentsAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<SemanticLoadResult> RefreshIncrementalAsync(
        WorkspaceId workspaceId,
        IReadOnlyList<SemanticDocumentRefresh> documents,
        CancellationToken cancellationToken)
    {
        return _semanticEngines.GetEngine(workspaceId)
            .RefreshDocumentsAsync(documents, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SemanticLoadResult> RefreshFullAsync(
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        return _semanticEngines.GetEngine(workspaceId).RefreshFullAsync(cancellationToken);
    }
}
