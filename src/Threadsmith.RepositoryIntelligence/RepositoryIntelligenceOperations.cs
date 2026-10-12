namespace Threadsmith.RepositoryIntelligence;

using System.Collections.Concurrent;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Collection policy hosted inside ordinary run execution and the shared memory writer.</summary>
public sealed class RepositoryIntelligenceOperations : IRepositoryIntelligenceOperations,
    ICommandHandler<ManageRepositoryIntelligenceCommand, RepositoryIntelligenceStatus>, IAsyncDisposable
{
    private readonly RepositoryIntelligenceAutomation _automation;
    private readonly IManagedRepositoryMemoryStore _store;
    private readonly IRepositoryMemoryOptionsProvider _options;
    private readonly RepositoryIntelligenceMonitor _monitor;
    private readonly IPromptLoader _prompts;
    private readonly IDomainEventStream _events;
    private readonly Func<string, bool, CancellationToken, Task> _setEnabled;
    private readonly Lock _cancellationGate = new();
    private readonly SemaphoreSlim _collectionGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _activeCancellation;
    private Binding? _binding;

    private sealed record Binding(SessionId SessionId, string RepositoryPath);

    /// <summary>Initializes a new instance of the <see cref="RepositoryIntelligenceOperations"/> class.</summary>
    public RepositoryIntelligenceOperations(
        IManagedRepositoryMemoryStore store,
        IRepositoryMemoryOptionsProvider options,
        RepositoryIntelligenceMonitor monitor,
        IPromptLoader prompts,
        IDomainEventStream events,
        Func<string, bool, CancellationToken, Task> setEnabled,
        RepositoryIntelligenceAutomation automation)
    {
        _automation = automation ?? throw new ArgumentNullException(nameof(automation));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _setEnabled = setEnabled ?? throw new ArgumentNullException(nameof(setEnabled));
    }

    /// <summary>Starts feature consumers after repository persistence and settings have rebound.</summary>
    public async Task BindAsync(SessionId sessionId, string repositoryPath, CancellationToken cancellationToken = default)
    {
        await _monitor.BindAsync(sessionId, repositoryPath, _options.Capture(RepositoryIdentity.Create(repositoryPath)).Intelligence, cancellationToken);
        await _automation.BindAsync(sessionId, repositoryPath, cancellationToken);
        Volatile.Write(ref _binding, new Binding(sessionId, repositoryPath));
    }

    /// <summary>Retargets repository-owned notifications at a successful same-repository session transition.</summary>
    public void ActivateSession(SessionId sessionId, string repositoryIdentity)
    {
        var binding = Volatile.Read(ref _binding);
        if (binding is null || RepositoryIdentity.Create(binding.RepositoryPath) != repositoryIdentity)
        {
            return;
        }

        _monitor.ActivateSession(sessionId);
        _automation.ActivateSession(sessionId);
        Volatile.Write(ref _binding, binding with { SessionId = sessionId });
    }

    /// <summary>Restores the last successful consumer binding after repository preparation failed.</summary>
    public Task RestoreBindingAsync(CancellationToken cancellationToken = default)
    {
        var binding = Volatile.Read(ref _binding);
        return binding is null ? Task.CompletedTask : BindAsync(binding.SessionId, binding.RepositoryPath, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IRepositoryIntelligenceRun> BeginAsync(
        SessionId sessionId, RunId runId, string repositoryPath, RepositoryIntelligenceOperation operation, CancellationToken cancellationToken = default)
    {
        var identity = RepositoryIdentity.Create(repositoryPath);
        var options = _options.Capture(identity);
        if (operation is not (RepositoryIntelligenceOperation.Onboard or RepositoryIntelligenceOperation.Refresh or RepositoryIntelligenceOperation.Archeology))
        {
            throw new ArgumentException("An explicit intelligence action is required.", nameof(operation));
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        if (!await _collectionGate.WaitAsync(0, cancellation.Token))
        {
            cancellation.Dispose();
            throw new InvalidOperationException("An intelligence operation is already active.");
        }

        try
        {
            lock (_cancellationGate)
            {
                _activeCancellation = cancellation;
            }

            var state = await _store.GetIntelligenceStateAsync(identity, cancellation.Token);
            if (operation != RepositoryIntelligenceOperation.Onboard && options.Intelligence.Enabled && state.Onboarded)
            {
                await _monitor.WaitUntilReadyAsync(cancellation.Token);
            }

            if (operation != RepositoryIntelligenceOperation.Onboard
                && (!options.Intelligence.Enabled || !state.Onboarded || !_monitor.Capture(identity).IsEffective))
            {
                throw new InvalidOperationException("Repository intelligence requires successful onboarding and healthy enabled monitoring.");
            }

            if (state.Fault is not null)
            {
                throw new InvalidOperationException("Correct the monitoring failure and explicitly re-enable intelligence before collection.");
            }

            var commitGeneration = _automation.CaptureGeneration(identity);
            RepositoryMemoryCollection? collection = null;
            if (operation != RepositoryIntelligenceOperation.Archeology)
            {
                await _monitor.TrackCollectionAsync(sessionId, repositoryPath, options.Intelligence, cancellation.Token);
                var snapshot = await _store.GetSnapshotAsync(identity, [], cancellation.Token);
                state = await _store.GetIntelligenceStateAsync(identity, cancellation.Token);
                collection = new RepositoryMemoryCollection(snapshot, operation == RepositoryIntelligenceOperation.Onboard, state.ChangeSequence);
            }

            var instructions = _prompts.Get(operation == RepositoryIntelligenceOperation.Archeology
                ? PromptFileNames.ContextRepositoryArcheology : PromptFileNames.ContextRepositoryIntelligenceCollection);
            instructions += "\n" + JsonSerializer.Serialize(new
            {
                Operation = operation.ToString(),
                PendingChangesCheckpoint = state.ChangeSequence,
                state.ObservationGap,
                options.Intelligence.MaximumStoredMemories,
            });
            return new IntelligenceRun(this, sessionId, repositoryPath, operation, collection, instructions, commitGeneration, cancellation);
        }
        catch
        {
            try
            {
                await BindAsync(sessionId, repositoryPath, _lifetime.Token);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // Shutdown owns the remaining consumer cleanup.
            }
            finally
            {
                lock (_cancellationGate)
                {
                    _activeCancellation = null;
                    cancellation.Dispose();
                }

                _collectionGate.Release();
            }

            throw;
        }
    }

    /// <inheritdoc />
    public async Task<RepositoryIntelligenceStatus> HandleAsync(ManageRepositoryIntelligenceCommand command, CancellationToken cancellationToken = default)
    {
        var identity = RepositoryIdentity.Create(command.RepositoryPath);
        if (command.Action == "status")
        {
            return _monitor.Capture(identity);
        }

        if (command.Action is not ("enable" or "disable"))
        {
            throw new ArgumentException("Use intelligence status, enable, or disable.", nameof(command));
        }

        await SuspendAsync(cancellationToken);
        await _collectionGate.WaitAsync(cancellationToken);
        try
        {
            await _setEnabled(command.RepositoryPath, command.Action == "enable", cancellationToken);
            var state = await _store.GetIntelligenceStateAsync(identity, cancellationToken);
            if (command.Action == "enable" && (state.Onboarded || state.Fault is not null || _monitor.Capture(identity).Fault is not null))
            {
                await _monitor.RecoverAsync(command.SessionId, command.RepositoryPath, _options.Capture(identity).Intelligence, cancellationToken);
            }
            else
            {
                await _monitor.BindAsync(command.SessionId, command.RepositoryPath, _options.Capture(identity).Intelligence, cancellationToken);
            }

            await _automation.BindAsync(command.SessionId, command.RepositoryPath, cancellationToken);
            return _monitor.Capture(identity);
        }
        finally
        {
            _collectionGate.Release();
        }
    }

    /// <summary>Cancels and joins feature-owned inference before repository rebinding or disable.</summary>
    public async Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        Task cancellation;
        lock (_cancellationGate)
        {
            cancellation = _activeCancellation?.CancelAsync() ?? Task.CompletedTask;
        }

        await cancellation;

        await _collectionGate.WaitAsync(cancellationToken);
        _collectionGate.Release();
        await _automation.SuspendAsync(cancellationToken);
        await _monitor.SuspendAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync();
        await _collectionGate.WaitAsync();
        _collectionGate.Release();
        await _automation.DisposeAsync();
        _lifetime.Dispose();
    }

    private sealed class IntelligenceRun : IRepositoryIntelligenceRun
    {
        private readonly RepositoryIntelligenceOperations _owner;
        private readonly SessionId _sessionId;
        private readonly string _repositoryPath;
        private readonly string _identity;
        private readonly CancellationTokenSource _cancellation;
        private readonly CancellationTokenRegistration _monitorFailure;
        private readonly long _commitGeneration;
        private readonly ConcurrentDictionary<string, RepositoryMemorySupport> _sources = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private bool _completed;
        private int _disposed;

        public IntelligenceRun(
            RepositoryIntelligenceOperations owner, SessionId sessionId, string repositoryPath, RepositoryIntelligenceOperation operation, RepositoryMemoryCollection? collection, string instructions, long commitGeneration, CancellationTokenSource cancellation)
        {
            _owner = owner;
            _sessionId = sessionId;
            _repositoryPath = repositoryPath;
            _identity = RepositoryIdentity.Create(repositoryPath);
            _commitGeneration = commitGeneration;
            _cancellation = cancellation;
            _monitorFailure = owner._monitor.FailureToken.Register(cancellation.Cancel);
            Operation = operation;
            Collection = collection;
            Instructions = instructions;
        }

        public CancellationToken CancellationToken => _cancellation.Token;

        public RepositoryIntelligenceOperation Operation { get; }

        public RepositoryMemoryCollection? Collection { get; }

        public string Instructions { get; }

        public async Task<long> CaptureSourceCheckpointAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            if (RepositoryObservationPaths.IsHostStatePath(relativePath))
            {
                return 0;
            }

            await _owner._monitor.EnsureSupportAsync(_identity, relativePath, cancellationToken);
            return await _owner._store.GetIntelligenceCheckpointAsync(_identity, cancellationToken);
        }

        public void ObserveSource(string relativePath, string contentHash, long checkpoint)
        {
            var path = relativePath.Replace('\\', '/');
            if (Path.IsPathRooted(path) || path.Split('/').Any(segment => segment is "" or "." or "..") || RepositoryObservationPaths.IsHostStatePath(path))
            {
                return;
            }

            _sources[path] = new RepositoryMemorySupport
            {
                Path = path,
                ContentHash = contentHash,
                ObservedAt = DateTimeOffset.UtcNow,
                ObservedChangeSequence = checkpoint,
            };
        }

        public IReadOnlyList<RepositoryMemorySupport> ResolveSupports(IReadOnlyList<string> paths)
        {
            ArgumentNullException.ThrowIfNull(paths);
            return paths.Distinct(_sources.Comparer).Select(path => _sources.TryGetValue(path, out var source)
                ? source : throw new InvalidOperationException($"Read primary support '{path}' through read_file during this collection before citing it.")).ToArray();
        }

        public Task<RepositoryIntelligenceChangePage> ReadChangesAsync(string? afterPath, CancellationToken cancellationToken = default)
        {
            var collection = Collection ?? throw new InvalidOperationException("Pending work belongs to a collection operation.");
            return _owner._store.ReadIntelligenceChangesAsync(
                _identity,
                collection.Checkpoint,
                afterPath,
                Math.Min(32 * 1024, _owner._options.Capture(_identity).MaximumListBytes),
                cancellationToken);
        }

        public void CoverChanges(IReadOnlyList<string> paths)
        {
            var collection = Collection ?? throw new InvalidOperationException("Pending work belongs to a collection operation.");
            collection.CoverChanges(paths);
        }

        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            using var completion = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken, cancellationToken);
            cancellationToken = completion.Token;
            cancellationToken.ThrowIfCancellationRequested();
            if (_completed || Collection is null)
            {
                return;
            }

            var options = _owner._options.Capture(_identity);
            if (_owner._monitor.Capture(_identity).Fault is not null)
            {
                throw new InvalidOperationException("Source observation failed during collection. The prior collection was preserved.");
            }

            var wasEnabled = options.Intelligence.Enabled;
            await _owner._setEnabled(_repositoryPath, true, cancellationToken);
            try
            {
                await _owner._store.CommitCollectionAsync(Collection, options, cancellationToken);
            }
            catch
            {
                await _owner._setEnabled(_repositoryPath, wasEnabled, CancellationToken.None);
                throw;
            }

            _completed = true;
            _owner._automation.Complete(_identity, _commitGeneration);
            await _owner._events.PublishAsync(
                new DiagnosticObserved(_sessionId, DateTimeOffset.UtcNow, "intelligence.collection.completed",                 $"Repository intelligence {Operation.ToString().ToLowerInvariant()} committed {Collection.Snapshot(forPublication: true).Count} assertions."),
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await _cancellation.CancelAsync();
                await _owner.BindAsync(_sessionId, _repositoryPath, _owner._lifetime.Token);
            }
            catch (OperationCanceledException) when (_owner._lifetime.IsCancellationRequested)
            {
                // Host shutdown has already revoked observation and collection eligibility.
            }
            finally
            {
                await _monitorFailure.DisposeAsync();
                lock (_owner._cancellationGate)
                {
                    _owner._activeCancellation = null;
                    _cancellation.Dispose();
                }

                _owner._collectionGate.Release();
            }
        }
    }
}
