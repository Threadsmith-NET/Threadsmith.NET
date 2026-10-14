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
    public Task<IRepositoryIntelligenceRun> BeginAsync(
        SessionId sessionId, RunId runId, string repositoryPath, RepositoryIntelligenceOperation operation, CancellationToken cancellationToken = default)
    {
        return BeginAsync(sessionId, runId, repositoryPath, operation, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IRepositoryIntelligenceRun> BeginAsync(
        SessionId sessionId, RunId runId, string repositoryPath, RepositoryIntelligenceOperation operation, int? historyDepth, CancellationToken cancellationToken = default)
    {
        if (historyDepth is { } depth)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
            if (operation != RepositoryIntelligenceOperation.Onboard)
            {
                throw new ArgumentException("History depth applies only to onboarding.", nameof(historyDepth));
            }
        }

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
            RepositoryIntelligenceCheckpoint? resume = null;
            if (operation != RepositoryIntelligenceOperation.Archeology)
            {
                await _monitor.TrackCollectionAsync(sessionId, repositoryPath, options.Intelligence, cancellation.Token);
                var snapshot = await _store.GetSnapshotAsync(identity, [], cancellation.Token);
                state = await _store.GetIntelligenceStateAsync(identity, cancellation.Token);
                collection = new RepositoryMemoryCollection(snapshot, operation == RepositoryIntelligenceOperation.Onboard, state.ChangeSequence);
                if (operation == RepositoryIntelligenceOperation.Onboard)
                {
                    resume = await _store.ReadOnboardingCheckpointAsync(identity, cancellation.Token);
                    if (resume is not null && (resume.HistoryDepth != historyDepth
                        || resume.Entries.Count > options.Intelligence.MaximumStoredMemories
                        || !resume.BaselineIntelligence.ToHashSet().SetEquals(snapshot.Entries
                            .Where(entry => entry.Ownership == RepositoryMemoryOwnership.Intelligence)
                            .Select(entry => new RepositoryMemoryInclusion(entry.Id, entry.Revision)))))
                    {
                        resume = null;
                        await _events.PublishAsync(new DiagnosticObserved(sessionId, DateTimeOffset.UtcNow, "intelligence.onboarding.restarted", "Saved onboarding scope or published intelligence changed; starting a fresh collection."), cancellation.Token);
                    }
                }
            }

            var instructions = _prompts.Get(operation == RepositoryIntelligenceOperation.Archeology
                ? PromptFileNames.ContextRepositoryArcheology : PromptFileNames.ContextRepositoryIntelligenceCollection);
            instructions += "\n" + JsonSerializer.Serialize(new
            {
                Operation = operation.ToString(),
                PendingChangesCheckpoint = state.ChangeSequence,
                state.ObservationGap,
                options.Intelligence.MaximumStoredMemories,
                HistoryDepth = historyDepth,
            });
            return new IntelligenceRun(this, sessionId, repositoryPath, operation, collection, instructions, commitGeneration, cancellation, historyDepth, resume);
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

        if (command.Action == "reset")
        {
            if (!command.ResetConfirmed)
            {
                throw new InvalidOperationException("Repository intelligence reset requires explicit user confirmation.");
            }

            return await ResetAsync(command, identity, cancellationToken);
        }

        if (command.Action is not ("enable" or "disable"))
        {
            throw new ArgumentException("Use intelligence status, enable, disable, or reset.", nameof(command));
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
        await CancelActiveCollectionAsync();
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

    private Task CancelActiveCollectionAsync()
    {
        lock (_cancellationGate)
        {
            return _activeCancellation?.CancelAsync() ?? Task.CompletedTask;
        }
    }

    private async Task<RepositoryIntelligenceStatus> ResetAsync(
        ManageRepositoryIntelligenceCommand command, string identity, CancellationToken cancellationToken)
    {
        await CancelActiveCollectionAsync();
        await _collectionGate.WaitAsync(cancellationToken);
        try
        {
            var wasEnabled = _options.Capture(identity).Intelligence.Enabled;
            var configurationChanged = false;
            try
            {
                await _automation.SuspendAsync(cancellationToken);
                await _monitor.SuspendAsync(cancellationToken);
                await _setEnabled(command.RepositoryPath, false, cancellationToken);
                configurationChanged = true;
                await _store.ResetIntelligenceAsync(identity, cancellationToken);
            }
            catch (Exception resetFailure)
            {
                var failures = new List<Exception> { resetFailure };
                if (configurationChanged)
                {
                    try
                    {
                        await _setEnabled(command.RepositoryPath, wasEnabled, CancellationToken.None);
                    }
                    catch (Exception restoreFailure)
                    {
                        failures.Add(restoreFailure);
                    }
                }

                try
                {
                    await BindAsync(command.SessionId, command.RepositoryPath, CancellationToken.None);
                }
                catch (Exception bindFailure)
                {
                    failures.Add(bindFailure);
                }

                if (failures.Count > 1)
                {
                    throw new AggregateException("Repository intelligence reset failed and recovery was incomplete.", failures);
                }

                throw;
            }

            // Persistence committed; finish revoking transient state even if the caller cancels now.
            await _automation.ResetAsync(identity, CancellationToken.None);
            await _monitor.ResetAsync(identity, CancellationToken.None);
            await _events.PublishAsync(
                new DiagnosticObserved(command.SessionId, DateTimeOffset.UtcNow, "intelligence.reset.completed", "Repository intelligence reset. Onboarding is required before it can be used again."),
                CancellationToken.None);
            return _monitor.Capture(identity);
        }
        finally
        {
            _collectionGate.Release();
        }
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
        private RepositoryIntelligenceCheckpoint? _resume;
        private IReadOnlyList<RepositoryMemorySupport> _verifiedResumeSupports = [];
        private bool _resumed;
        private string? _historyRevision;
        private int _lastHistoryPage;
        private bool _historyAccepted;
        private string? _historyNotice;
        private bool _completed;
        private int _disposed;

        public IntelligenceRun(
            RepositoryIntelligenceOperations owner, SessionId sessionId, string repositoryPath, RepositoryIntelligenceOperation operation, RepositoryMemoryCollection? collection, string instructions, long commitGeneration, CancellationTokenSource cancellation, int? historyDepth, RepositoryIntelligenceCheckpoint? resume)
        {
            _owner = owner;
            _sessionId = sessionId;
            _repositoryPath = repositoryPath;
            _identity = RepositoryIdentity.Create(repositoryPath);
            _commitGeneration = commitGeneration;
            _cancellation = cancellation;
            CancellationToken = cancellation.Token;
            _monitorFailure = owner._monitor.FailureToken.Register(cancellation.Cancel);
            Operation = operation;
            Collection = collection;
            Instructions = instructions;
            HistoryDepth = historyDepth;
            _resume = resume;
            PendingHistoryPage = operation == RepositoryIntelligenceOperation.Onboard ? resume?.NextHistoryPage ?? 0 : null;
        }

        public CancellationToken CancellationToken { get; }

        public RepositoryIntelligenceOperation Operation { get; }

        public RepositoryMemoryCollection? Collection { get; }

        public string Instructions { get; private set; }

        public bool IsCoverageReviewActive { get; private set; }

        public int? HistoryDepth { get; }

        public string? ResumeHistoryRevision => _resume?.HistoryRevision;

        public int? PendingHistoryPage { get; private set; }

        public string AcceptHistoryPage(int page, GitLogResult result, string modelContent)
        {
            if (PendingHistoryPage != page || !result.IsCollectionPage || result.IsTruncated || result.HistoryDepth != HistoryDepth
                || (result.NextCollectionPage is { } next && next != result.CollectionPage + 1))
            {
                throw new InvalidOperationException("Incomplete or out-of-order onboarding history cannot be published.");
            }

            if (_resume is { } resume)
            {
                if (result.HistoryRevision == resume.HistoryRevision && result.CollectionPage == page)
                {
                    var collection = Collection ?? throw new InvalidOperationException("No collection owns the checkpoint.");
                    if (resume.HistoryComplete && result.NextCollectionPage is not null)
                    {
                        throw new InvalidOperationException("Saved final coverage does not match the history boundary.");
                    }

                    collection.Restore(resume, _verifiedResumeSupports);
                    _resumed = true;
                    IsCoverageReviewActive = resume.CoverageReviewStarted;
                    foreach (var support in collection.Snapshot().SelectMany(entry => entry.Supports))
                    {
                        _sources[support.Path] = support;
                    }

                    Instructions += "\n" + JsonSerializer.Serialize(new
                    {
                        ResumedOnboarding = true,
                        CompletedHistoryPages = page,
                        PreparedAssertions = collection.Snapshot(forPublication: true).Count,
                        resume.WorkingContext,
                        resume.WorkingContextTruncated,
                        resume.HistoryComplete,
                        resume.CoverageReviewStarted,
                    });
                    _historyNotice = $"Resuming repository onboarding after {page} completed history batches.";
                }
                else if (result.CollectionPage == 0)
                {
                    _historyNotice = "Repository HEAD changed since the saved onboarding checkpoint; starting a fresh collection.";
                }
                else
                {
                    throw new InvalidOperationException("Saved onboarding progress does not match delivered history.");
                }

                _resume = null;
            }
            else if (result.CollectionPage != page || (_historyRevision is not null && _historyRevision != result.HistoryRevision))
            {
                throw new InvalidOperationException("Onboarding history changed during collection.");
            }

            _historyRevision = result.HistoryRevision;
            _lastHistoryPage = result.CollectionPage;
            _historyAccepted = true;

            var instructions = _owner._prompts.Render(
                PromptFileNames.ContextRepositoryIntelligenceHistory,
                new Dictionary<string, string>(StringComparer.Ordinal) { ["HistoryJson"] = modelContent });
            PendingHistoryPage = result.NextCollectionPage;
            return instructions;
        }

        public async Task<string> AcceptHistoryPageAsync(int page, GitLogResult result, string modelContent, CancellationToken cancellationToken = default)
        {
            if (_resume is { } resume && result.HistoryRevision == resume.HistoryRevision && result.CollectionPage == page)
            {
                await _owner._events.PublishAsync(new DiagnosticObserved(_sessionId, DateTimeOffset.UtcNow, "intelligence.onboarding.progress", "Checking saved onboarding evidence for changes."), cancellationToken);
                _verifiedResumeSupports = await _owner._monitor.VerifySupportsAsync(
                    _repositoryPath, resume.Entries.SelectMany(entry => entry.Supports).Concat(resume.Coverage.SelectMany(item => item.Supports)).ToArray(), cancellationToken);
            }

            var instructions = AcceptHistoryPage(page, result, modelContent);
            if (_historyNotice is { } notice)
            {
                _historyNotice = null;
                await _owner._events.PublishAsync(new DiagnosticObserved(_sessionId, DateTimeOffset.UtcNow, "intelligence.onboarding.progress", notice), cancellationToken);
            }

            return instructions;
        }

        public bool CanCompleteWithoutModel => _resumed && PendingHistoryPage is null && IsCoverageReviewActive
            && Collection is { HasOpenCoverage: false } collection
            && collection.Snapshot(forPublication: true).All(entry => entry.Supports.Count > 0 && entry.Supports.All(support => !support.IsUnknown && !support.IsDirty));

        public async Task SaveCheckpointAsync(string workingContext, CancellationToken cancellationToken = default)
        {
            if (Operation != RepositoryIntelligenceOperation.Onboard || Collection is not { } collection
                || (PendingHistoryPage is not > 0 && !IsCoverageReviewActive) || !_historyAccepted)
            {
                return;
            }

            var length = Math.Min(32 * 1024, workingContext.Length);
            if (length < workingContext.Length && length > 0 && char.IsHighSurrogate(workingContext[length - 1]))
            {
                length--;
            }

            await collection.Operations.WaitAsync(cancellationToken);
            try
            {
                await _owner._store.SaveOnboardingCheckpointAsync(
                    new RepositoryIntelligenceCheckpoint
                    {
                        RepositoryIdentity = _identity,
                        HistoryRevision = _historyRevision,
                        HistoryDepth = HistoryDepth,
                        NextHistoryPage = PendingHistoryPage ?? _lastHistoryPage,
                        HistoryComplete = PendingHistoryPage is null,
                        CoverageReviewStarted = IsCoverageReviewActive,
                        BaselineIntelligence = collection.Baseline.Entries.Where(entry => entry.Ownership == RepositoryMemoryOwnership.Intelligence)
                            .Select(entry => new RepositoryMemoryInclusion(entry.Id, entry.Revision)).ToArray(),
                        Entries = collection.Snapshot(),
                        AcceptedIds = collection.AcceptedIds,
                        Coverage = collection.Coverage,
                        InitialSurvey = collection.InitialSurvey,
                        PendingReconciliations = collection.PendingReconciliations,
                        WorkingContext = workingContext[..length],
                        WorkingContextTruncated = length < workingContext.Length,
                    },
                    cancellationToken);
            }
            finally
            {
                collection.Operations.Release();
            }

            if (PendingHistoryPage is not null)
            {
                await _owner._events.PublishAsync(new DiagnosticObserved(_sessionId, DateTimeOffset.UtcNow, "intelligence.onboarding.checkpoint", $"Saved onboarding progress through {PendingHistoryPage} history batches."), cancellationToken);
            }
        }

        public async Task<string?> ContinueCoverageAsync(string workingContext, CancellationToken cancellationToken = default)
        {
            if (Operation != RepositoryIntelligenceOperation.Onboard || Collection is not { } collection)
            {
                return null;
            }

            if (collection.InitialSurvey.Count == 0)
            {
                return _owner._prompts.Render(
                    PromptFileNames.ContextRepositoryIntelligenceCoverage,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["CoverageJson"] = JsonSerializer.Serialize(new { Phase = "initialSurveyRequired", SurveyInitialized = false }) });
            }

            if (PendingHistoryPage is not null)
            {
                return null;
            }

            var firstReview = !IsCoverageReviewActive;
            IsCoverageReviewActive = true;
            await SaveCheckpointAsync(workingContext, cancellationToken);
            if (!firstReview && !collection.HasOpenCoverage)
            {
                return null;
            }

            var coverage = collection.Coverage;
            var acceptedIds = collection.Snapshot(forPublication: true).Select(entry => entry.Id).ToHashSet();
            var summary = JsonSerializer.Serialize(new
            {
                FirstReview = firstReview,
                SurveyInitialized = true,
                InitialAreas = collection.InitialSurvey.Select(item => new { item.Key, item.Area, item.Question }),
                PendingReconciliations = collection.PendingReconciliations.Select(item => new { item.Key }),
                InvalidMemoryLinks = coverage.Where(item => item.MemoryIds.Any(id => !acceptedIds.Contains(id))).Select(item => item.Key),
                TotalItems = coverage.Count,
                OpenItems = coverage.Count(item => item.Priority == "required" && item.Status is "uninvestigated" or "investigating"),
                OptionalItems = coverage.Count(item => item.Priority == "optional"),
                BlockedItems = coverage.Count(item => item.Status == "blocked"),
            });
            await _owner._events.PublishAsync(new DiagnosticObserved(_sessionId, DateTimeOffset.UtcNow, "intelligence.onboarding.coverage", "History processing is complete. Continuing the final investigation coverage pass; preparation is checkpointed."), cancellationToken);
            return _owner._prompts.Render(PromptFileNames.ContextRepositoryIntelligenceCoverage, new Dictionary<string, string>(StringComparer.Ordinal) { ["CoverageJson"] = summary });
        }

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
                ? source : throw new InvalidOperationException($"Read primary support '{path}' through read_file or a complete git_show file during this collection before citing it.")).ToArray();
        }

        public void ObserveHistoricalSource(string relativePath, string contentHash, string revision)
        {
            ObserveSource(relativePath, contentHash, 0);
            var path = relativePath.Replace('\\', '/');
            if (_sources.TryGetValue(path, out var source))
            {
                _sources[path] = source with { Revision = revision, IsUnknown = true };
            }
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

            if (PendingHistoryPage is not null)
            {
                throw new InvalidOperationException("Onboarding history is incomplete. The prior collection was preserved.");
            }

            if (Operation == RepositoryIntelligenceOperation.Onboard && (!IsCoverageReviewActive || Collection.HasOpenCoverage))
            {
                throw new InvalidOperationException("Onboarding investigation coverage is incomplete. The prior collection was preserved.");
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
            foreach (var item in Collection.Coverage.Where(item => item.Status == "blocked"))
            {
                await _owner._events.PublishAsync(new DiagnosticObserved(_sessionId, DateTimeOffset.UtcNow, "intelligence.onboarding.blocked", $"Investigation blocked: {item.Area}: {item.Gaps}"), CancellationToken.None);
            }

            foreach (var item in Collection.Coverage.Where(item => item.Priority == "optional" && item.Status is "uninvestigated" or "investigating"))
            {
                await _owner._events.PublishAsync(new DiagnosticObserved(_sessionId, DateTimeOffset.UtcNow, "intelligence.onboarding.optional", $"Optional investigation left open: {item.Area}: {item.Question} Scope: {item.Limitations}"), CancellationToken.None);
            }

            _owner._automation.Complete(_identity, _commitGeneration);
            await _owner._events.PublishAsync(
                new DiagnosticObserved(_sessionId, DateTimeOffset.UtcNow, "intelligence.collection.completed", $"Repository intelligence {Operation.ToString().ToLowerInvariant()} committed {Collection.Snapshot(forPublication: true).Count} assertions."),
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
