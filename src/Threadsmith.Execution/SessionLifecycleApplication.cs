namespace Threadsmith.Execution;

using System.Security.Cryptography;
using System.Text;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;

/// <summary>Serializes safe-boundary creation, restoration, and independent cloning of active sessions.</summary>
public sealed class SessionLifecycleApplication :
    ICommandHandler<CreateNewSessionCommand, SessionTransitionResult>,
    ICommandHandler<ListResumableSessionsCommand, IReadOnlyList<SessionCatalogEntry>>,
    ICommandHandler<ResumeSessionCommand, SessionTransitionResult>,
    ICommandHandler<CloneSessionCommand, SessionTransitionResult>,
    ICommandHandler<GetActiveSessionCommand, SessionCatalogEntry>
{
    private readonly ActiveModelSelectionService? _activeModels;
    private readonly IContextAssembler _contextAssembler;
    private readonly IEvidenceStore _evidenceStore;
    private readonly IDomainEventStream _events;
    private readonly ISessionLifecycleStore _lifecycleStore;
    private readonly InMemoryProjectionStore _projections;
    private readonly ISessionRestorer _restorer;
    private readonly SemaphoreSlim _transitionGate = new(1, 1);
    private readonly SessionApplication _sessions;
    private readonly SessionUsageProjection _usage;
    private readonly TimeProvider _timeProvider;
    private SessionCatalogEntry? _active;
    private string _repositoryDisplayName;
    private string _repositoryIdentity;

    /// <summary>Initializes a new instance of the <see cref="SessionLifecycleApplication"/> class.</summary>
    public SessionLifecycleApplication(
        string repositoryPath,
        ISessionLifecycleStore lifecycleStore,
        ISessionRestorer restorer,
        SessionApplication sessions,
        InMemoryProjectionStore projections,
        IDomainEventStream events,
        IEvidenceStore evidenceStore,
        IContextAssembler contextAssembler,
        SessionUsageProjection usage,
        ActiveModelSelectionService? activeModels = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(lifecycleStore);
        ArgumentNullException.ThrowIfNull(restorer);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(projections);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(evidenceStore);
        ArgumentNullException.ThrowIfNull(contextAssembler);
        ArgumentNullException.ThrowIfNull(usage);
        (_repositoryIdentity, _repositoryDisplayName) = CreateRepositoryBinding(repositoryPath);

        _lifecycleStore = lifecycleStore;
        _restorer = restorer;
        _sessions = sessions;
        _projections = projections;
        _events = events;
        _evidenceStore = evidenceStore;
        _contextAssembler = contextAssembler;
        _usage = usage;
        _activeModels = activeModels;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public Task<SessionTransitionResult> HandleAsync(
        CreateNewSessionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return TransitionAsync(SessionTransitionKind.New, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SessionCatalogEntry>> HandleAsync(
        ListResumableSessionsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.MaximumCount is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        return await _lifecycleStore.ListAsync(_repositoryIdentity, command.MaximumCount, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SessionTransitionResult> HandleAsync(
        ResumeSessionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return TransitionAsync(SessionTransitionKind.Resume, command.SessionId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SessionTransitionResult> HandleAsync(
        CloneSessionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return TransitionAsync(SessionTransitionKind.Clone, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SessionCatalogEntry> HandleAsync(
        GetActiveSessionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_active
            ?? throw new InvalidOperationException("No active session has been initialized."));
    }

    /// <summary>Persists the current session model snapshot after a successful model/reasoning change.</summary>
    public async Task CheckpointActiveSelectionAsync(CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            if (_active is null)
            {
                return;
            }

            if (!_active.IsWritable && _active.State == SessionLifecycleState.Unavailable)
            {
                _sessions.RegisterRestoredSession(_active.SessionId);
                _active = _active with { IsWritable = true };
            }

            _active = await CheckpointAsync(_active, SessionLifecycleState.Active, cancellationToken);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Rebinds lifecycle creation and selection to a newly opened repository.</summary>
    public async Task BindRepositoryAsync(
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        (var nextIdentity, var nextDisplayName) = CreateRepositoryBinding(repositoryPath);
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(nextIdentity, _repositoryIdentity, StringComparison.Ordinal))
            {
                return;
            }

            if (_sessions.HasActiveWork)
            {
                throw new InvalidOperationException(
                    "Repository rebinding requires a complete safe boundary. Cancel or finish active work first.");
            }

            var previousIdentity = _repositoryIdentity;
            var previousDisplayName = _repositoryDisplayName;
            var source = _active;
            try
            {
                if (source is not null)
                {
                    _active = source = await CheckpointAsync(
                        source,
                        SessionLifecycleState.Idle,
                        cancellationToken,
                        preserveModelSelection: true);
                }

                _repositoryIdentity = nextIdentity;
                _repositoryDisplayName = nextDisplayName;
                _ = await CreateNewAsync(source, cancellationToken);
            }
            catch
            {
                _repositoryIdentity = previousIdentity;
                _repositoryDisplayName = previousDisplayName;
                if (source is not null)
                {
                    _active = await CheckpointAsync(
                        source,
                        SessionLifecycleState.Active,
                        CancellationToken.None,
                        preserveModelSelection: true);
                }

                throw;
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    /// <summary>Persists current usage and selector metadata after a terminal turn boundary.</summary>
    public async Task CheckpointCompletedTurnAsync(
        SessionId sessionId,
        CancellationToken cancellationToken = default)
    {
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            if (_active is not { } active || active.SessionId != sessionId)
            {
                return;
            }

            _active = await CheckpointAsync(active, active.State, cancellationToken);
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task<SessionTransitionResult> TransitionAsync(
        SessionTransitionKind kind,
        SessionId? requestedSessionId,
        CancellationToken cancellationToken)
    {
        await _transitionGate.WaitAsync(cancellationToken);
        try
        {
            if (_sessions.HasActiveWork)
            {
                throw new InvalidOperationException(
                    "Session transition requires a complete safe boundary. Cancel or finish active work first.");
            }

            var source = _active;
            var sourceSelection = CaptureModelSelection();
            if (kind == SessionTransitionKind.Resume
                && source is { } current
                && current.SessionId == requestedSessionId)
            {
                return CreateResult(
                    kind,
                    current,
                    current.SessionId,
                    [],
                    _usage.GetDurableSnapshot(current.SessionId));
            }

            try
            {
                if (source is not null)
                {
                    _active = source = await CheckpointAsync(
                        source,
                        SessionLifecycleState.Idle,
                        cancellationToken);
                }

                return kind switch
                {
                    SessionTransitionKind.New => await CreateNewAsync(source, cancellationToken),
                    SessionTransitionKind.Resume => await ResumeAsync(
                        requestedSessionId ?? throw new ArgumentException("A session id is required."),
                        source,
                        cancellationToken),
                    SessionTransitionKind.Clone => await CloneAsync(
                        source ?? throw new InvalidOperationException("No active session is available to clone."),
                        cancellationToken),
                    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                };
            }
            catch
            {
                if (source is not null)
                {
                    if (sourceSelection is not null && _activeModels is not null)
                    {
                        _ = await _activeModels.RestoreSessionSelectionAsync(
                            sourceSelection,
                            CancellationToken.None);
                    }

                    _active = await CheckpointAsync(
                        source,
                        SessionLifecycleState.Active,
                        CancellationToken.None);
                }

                throw;
            }
        }
        finally
        {
            _transitionGate.Release();
        }
    }

    private async Task<SessionTransitionResult> CreateNewAsync(
        SessionCatalogEntry? source,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var sessionId = await _sessions.CreateRegisteredSessionAsync("Interactive", cancellationToken);
        var entry = new SessionCatalogEntry
        {
            SessionId = sessionId,
            RepositoryIdentity = _repositoryIdentity,
            RepositoryDisplayName = _repositoryDisplayName,
            CreatedAt = now,
            UpdatedAt = now,
            State = SessionLifecycleState.Active,
            ConversationMode = ConversationContextMode.ConversationAware,
            ModelSelection = CaptureModelSelection(),
        };
        entry = await _lifecycleStore.CreateAsync(
            entry,
            new SessionDurableUsage(0, 0, false, false, false),
            cancellationToken);
        await SeedRepositoryProjectionAsync(source?.SessionId, entry.SessionId, false, cancellationToken);
        Publish(entry);
        return CreateResult(
            SessionTransitionKind.New,
            entry,
            source?.SessionId,
            [],
            new SessionDurableUsage(0, 0, false, false, false));
    }

    private async Task<SessionTransitionResult> ResumeAsync(
        SessionId sessionId,
        SessionCatalogEntry? source,
        CancellationToken cancellationToken)
    {
        var entry = await _lifecycleStore.GetAsync(sessionId, cancellationToken)
            ?? throw new KeyNotFoundException($"Session {sessionId.Value:D} was not found.");
        if (!string.Equals(entry.RepositoryIdentity, _repositoryIdentity, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Session {sessionId.Value:D} belongs to repository '{entry.RepositoryDisplayName}'. Open that repository before resuming it.");
        }

        if (!entry.IsWritable && entry.State == SessionLifecycleState.Legacy)
        {
            throw new InvalidOperationException("The selected session is inspectable only and cannot accept new turns.");
        }

        var candidate = new InMemoryProjectionStore();
        var restoration = await _restorer.RestoreAsync(sessionId, candidate, cancellationToken);
        var key = new ProjectionKey("session", sessionId.Value.ToString("D"));
        var restoredProjection = await candidate.GetAsync<SessionProjection>(key, cancellationToken);
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(restoration.Warnings))
        {
            warnings.Add(restoration.Warnings);
        }

        var selectionRequired = false;
        if (entry.ModelSelection is { } selection && _activeModels is not null)
        {
            var modelWarning = await _activeModels.RestoreSessionSelectionAsync(selection, cancellationToken);
            if (modelWarning is not null)
            {
                warnings.Add(modelWarning);
                selectionRequired = modelWarning.Contains("Run /models", StringComparison.Ordinal);
            }
        }

        if (!selectionRequired && !restoration.IsLegacy)
        {
            _sessions.RegisterRestoredSession(sessionId);
        }

        var usage = await _lifecycleStore.GetUsageAsync(sessionId, cancellationToken);
        _usage.Restore(sessionId, usage);
        entry = entry with
        {
            State = restoration.IsLegacy
                ? SessionLifecycleState.Legacy
                : selectionRequired
                    ? SessionLifecycleState.Unavailable
                    : SessionLifecycleState.Active,
            IsWritable = !restoration.IsLegacy && !selectionRequired,
            UpdatedAt = _timeProvider.GetUtcNow(),
            ModelSelection = selectionRequired
                ? entry.ModelSelection
                : CaptureModelSelection() ?? entry.ModelSelection,
        };
        entry = await _lifecycleStore.CheckpointAsync(entry, usage, cancellationToken);
        if (restoredProjection is not null)
        {
            _projections.ReplaceSession(restoredProjection);
        }
        else
        {
            await SeedRepositoryProjectionAsync(source?.SessionId, sessionId, true, cancellationToken);
        }

        Publish(entry);
        return CreateResult(SessionTransitionKind.Resume, entry, source?.SessionId, warnings, usage);
    }

    private async Task<SessionTransitionResult> CloneAsync(
        SessionCatalogEntry source,
        CancellationToken cancellationToken)
    {
        var sourceUsage = _usage.GetDurableSnapshot(source.SessionId);
        var now = _timeProvider.GetUtcNow();
        var destinationId = SessionId.New();
        var destination = source with
        {
            SessionId = destinationId,
            CreatedAt = now,
            UpdatedAt = now,
            State = SessionLifecycleState.Active,
            CloneSourceSessionId = source.SessionId,
            Preview = null,
            MessageCount = 0,
            IsWritable = true,
            ModelSelection = CaptureModelSelection() ?? source.ModelSelection,
        };
        var cloneUsage = new SessionDurableUsage(
            0,
            0,
            sourceUsage.IsEstimate,
            sourceUsage.HasUnknownUsage,
            false,
            SaturatingAdd(sourceUsage.InheritedInputTokens, sourceUsage.InputTokens),
            SaturatingAdd(sourceUsage.InheritedOutputTokens, sourceUsage.OutputTokens));
        destination = await _lifecycleStore.CloneAsync(
            source.SessionId,
            destination,
            cloneUsage,
            cancellationToken);
        _evidenceStore.CopySession(source.SessionId, destinationId);
        _sessions.RegisterRestoredSession(destinationId);
        _usage.Restore(destinationId, cloneUsage);
        await SeedRepositoryProjectionAsync(source.SessionId, destinationId, true, cancellationToken);
        Publish(destination);
        return CreateResult(SessionTransitionKind.Clone, destination, source.SessionId, [], cloneUsage);
    }

    private async Task<SessionCatalogEntry> CheckpointAsync(
        SessionCatalogEntry entry,
        SessionLifecycleState state,
        CancellationToken cancellationToken,
        bool preserveModelSelection = false)
    {
        var checkpoint = entry with
        {
            State = state,
            UpdatedAt = _timeProvider.GetUtcNow(),
            ModelSelection = preserveModelSelection
                ? entry.ModelSelection
                : CaptureModelSelection() ?? entry.ModelSelection,
        };
        return await _lifecycleStore.CheckpointAsync(
            checkpoint,
            _usage.GetDurableSnapshot(entry.SessionId),
            cancellationToken);
    }

    private SessionModelSelectionRecord? CaptureModelSelection()
    {
        if (_activeModels is null)
        {
            return null;
        }

        var selection = _activeModels.Current;
        return new SessionModelSelectionRecord
        {
            ProviderId = selection.ProviderId,
            ProfileId = selection.Profile.Id,
            ReasoningLevel = selection.ReasoningLevel.ToString(),
            Generation = selection.Generation,
        };
    }

    private async Task SeedRepositoryProjectionAsync(
        SessionId? sourceSessionId,
        SessionId destinationSessionId,
        bool createBaseProjection,
        CancellationToken cancellationToken)
    {
        var source = sourceSessionId is { } sourceId
            ? _projections.GetSession(sourceId)
            : null;
        var now = _timeProvider.GetUtcNow();
        if (createBaseProjection)
        {
            await _events.PublishAsync(
                new SessionCreated(destinationSessionId, now, "Interactive"),
                cancellationToken);
        }

        if (source?.RepositoryPath is { } repositoryPath)
        {
            await _events.PublishAsync(
                new RepositoryOpened(
                    destinationSessionId,
                    now,
                    repositoryPath,
                    source.WorkspaceId ?? default,
                    source.RepositoryTrust ?? RepositoryTrustLevel.UntrustedInspection),
                cancellationToken);
        }

        if (source?.SolutionPath is { } solutionPath)
        {
            await _events.PublishAsync(
                new SolutionLoaded(
                    destinationSessionId,
                    now,
                    solutionPath,
                    source.WorkspaceId ?? default,
                    source.TargetFrameworks.ToArray()),
                cancellationToken);
        }

        if (source is not null && source.SemanticConfidence != SemanticConfidenceLevel.None)
        {
            await _events.PublishAsync(
                new SemanticConfidenceChanged(
                    destinationSessionId,
                    now,
                    source.SemanticConfidence.ToString()),
                cancellationToken);
        }

        if (source is { IsSemanticLoadComplete: true, WorkspaceId: { } workspaceId })
        {
            await _events.PublishAsync(
                new SemanticLoadCompleted(
                    destinationSessionId,
                    now,
                    workspaceId,
                    source.SemanticConfidence.ToString()),
                cancellationToken);
        }
    }

    private void Publish(SessionCatalogEntry entry)
    {
        _contextAssembler.InvalidateInspections();
        _projections.InvalidateContextInspections();
        _active = entry;
    }

    private static SessionTransitionResult CreateResult(
        SessionTransitionKind kind,
        SessionCatalogEntry entry,
        SessionId? sourceSessionId,
        IReadOnlyList<string> warnings,
        SessionDurableUsage usage)
    {
        return new SessionTransitionResult
        {
            Kind = kind,
            ActiveSession = entry,
            SourceSessionId = sourceSessionId,
            Warnings = warnings,
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            UsageIsEstimate = usage.IsEstimate,
            InheritedInputTokens = usage.InheritedInputTokens,
            InheritedOutputTokens = usage.InheritedOutputTokens,
        };
    }

    private static long SaturatingAdd(long left, long right)
    {
        return left > long.MaxValue - right ? long.MaxValue : left + right;
    }

    private static (string Identity, string DisplayName) CreateRepositoryBinding(string repositoryPath)
    {
        var canonicalRepository = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            OperatingSystem.IsWindows() ? canonicalRepository.ToUpperInvariant() : canonicalRepository)));
        var displayName = Path.GetFileName(canonicalRepository);
        return (identity, string.IsNullOrWhiteSpace(displayName) ? canonicalRepository : displayName);
    }
}
