namespace Threadsmith.Execution;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>Coordinates scripted sessions through application commands.</summary>
public sealed partial class SessionApplication :
    ICommandHandler<CreateSessionCommand, SessionId>,
    ICommandHandler<SubmitRequestCommand, RunId>,
    ICommandHandler<WaitForRunCommand, bool>,
    ICommandHandler<CancelRunCommand, bool>,
    ICommandHandler<RequestRunSteeringPauseCommand, RunSteeringPauseRequestResult>,
    ICommandHandler<WaitForRunSteeringPauseCommand, RunSteeringPauseWaitResult>,
    ICommandHandler<SubmitRunSteeringCommand, RunSteeringSubmissionResult>,
    ICommandHandler<SetConversationContextModeCommand, bool>,
    ICommandHandler<GetConversationStateCommand, ConversationStateSnapshot>,
    ISemanticRefreshPublicationGate
{
    private static readonly Meter _meter = new("Threadsmith.Execution");
    private static readonly Histogram<double> _semanticAdmissionWait = _meter.CreateHistogram<double>(
        "threadsmith.semantic.refresh.admission_wait.duration",
        "ms");

    private readonly Func<IBudget> _budgetFactory;
    private readonly IContextAssembler? _contextAssembler;
    private readonly IConversationStore? _conversationStore;
    private readonly IManagedRepositoryMemoryService? _repositoryMemories;
    private readonly IRepositoryMemoryOptionsProvider? _repositoryMemoryOptions;
    private readonly IConversationToolSnapshotStore? _conversationToolSnapshots;
    private readonly CorrectiveMessageFactory _correctiveMessages;
    private readonly IPromptLoader _prompts;
    private readonly ConversationContextMode _defaultConversationMode;
    private readonly ModelProfileId? _defaultModelProfileId;
    private readonly IEvidenceStore? _evidenceStore;
    private readonly IHookCoordinator? _hooks;
    private readonly ISemanticRefreshCoordinator? _semanticRefreshCoordinator;
    private readonly SourceEditApplication? _sourceEdits;
    private readonly IActiveTurnCompactor? _activeTurnCompactor;
    private readonly ActiveTurnCompactionPolicy _activeTurnCompactionPolicy;
    private readonly ActiveTurnCompactionCandidateProfile? _activeTurnCompactionProfile;
    private readonly IDomainEventStream _events;
    private readonly ILogger<SessionApplication> _logger;
    private readonly ExecutionLimits _limits;
    private readonly IModelProvider _model;

    private readonly ConcurrentDictionary<RunId, RunRegistration> _runs = new();
    private readonly ConcurrentDictionary<SemanticAdmissionKey, SemaphoreSlim> _semanticAdmissionGates = new();
    private readonly RunSteeringCoordinator _steering;
    private readonly IOutputSanitizer _sanitizer;
    private readonly ConcurrentDictionary<SessionId, byte> _sessions = new();
    private readonly Func<SessionId, CancellationToken, Task<ToolInvocationContext>>?
        _toolContextFactory;

    private readonly Func<SessionId, RunId, ConversationMessageId, string, CancellationToken, Task<IReadOnlyList<UserUrlReference>>>?
        _userUrlIntake;

    private readonly IToolInvocationPipeline? _toolPipeline;
    private readonly IToolRegistry? _toolRegistry;
    private readonly SessionModelPreferences? _sessionPreferences;
    private readonly SessionUsageProjection? _sessionUsage;
    private readonly Func<ModelProfileId, CancellationToken, Task<ActiveModelSelectionResult>>?
        _selectActiveModel;

    /// <summary>Gets whether any model or governed run is still active.</summary>
    public bool HasActiveWork => _runs.Values.Any(registration => !registration.Completion.Task.IsCompleted);

    /// <summary>Creates and registers one session using the ordinary durable event boundary.</summary>
    public Task<SessionId> CreateRegisteredSessionAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        return HandleAsync(new CreateSessionCommand(name), cancellationToken);
    }

    /// <summary>Registers a safely restored durable session for subsequent commands.</summary>
    public void RegisterRestoredSession(SessionId sessionId)
    {
        if (sessionId == default)
        {
            throw new ArgumentException("The session id cannot be default.", nameof(sessionId));
        }

        _sessions.TryAdd(sessionId, 0);
    }

    /// <inheritdoc />
    public async Task<TResult> PublishAsync<TResult>(
        SessionId sessionId,
        WorkspaceId workspaceId,
        Func<CancellationToken, Task<TResult>> publication,
        CancellationToken cancellationToken = default)
    {
        if (sessionId == default)
        {
            throw new ArgumentException("The session id cannot be default.", nameof(sessionId));
        }

        ArgumentNullException.ThrowIfNull(publication);
        var admissionGate = _semanticAdmissionGates.GetOrAdd(
            SemanticAdmissionKey.Create(sessionId, workspaceId),
            static _ => new SemaphoreSlim(1, 1));
        await admissionGate.WaitAsync(cancellationToken);
        try
        {
            // Compiler operations retain their source generation and actual resource lifetime.
            // Waiting for this run here would deadlock an edit that publishes its own next generation.
            return await publication(cancellationToken);
        }
        finally
        {
            admissionGate.Release();
        }
    }

    /// <summary>Initializes a new instance of the <see cref="SessionApplication"/> class.</summary>
    public SessionApplication(
        IDomainEventStream events,
        IModelProvider model,
        IBudget budget,
        IOutputSanitizer sanitizer,
        ILogger<SessionApplication> logger,
        IToolInvocationPipeline? toolPipeline = null,
        Func<SessionId, CancellationToken, Task<ToolInvocationContext>>?
            toolContextFactory = null,
        IContextAssembler? contextAssembler = null,
        IEvidenceStore? evidenceStore = null,
        IToolRegistry? toolRegistry = null,
        ModelProfileId? defaultModelProfileId = null,
        ExecutionLimits? limits = null,
        SessionModelPreferences? sessionPreferences = null,
        SessionUsageProjection? sessionUsage = null,
        IConversationStore? conversationStore = null,
        ConversationContextMode defaultConversationMode = ConversationContextMode.ConversationAware,
        IHookCoordinator? hooks = null,
        Func<IBudget>? budgetFactory = null,
        Func<SessionId, RunId, ConversationMessageId, string, CancellationToken, Task<IReadOnlyList<UserUrlReference>>>?
            userUrlIntake = null,
        IActiveTurnCompactor? activeTurnCompactor = null,
        ActiveTurnCompactionPolicy? activeTurnCompactionPolicy = null,
        ActiveTurnCompactionCandidateProfile? activeTurnCompactionProfile = null,
        Func<ModelProfileId, CancellationToken, Task<ActiveModelSelectionResult>>? selectActiveModel = null,
        IConversationToolSnapshotStore? conversationToolSnapshots = null,
        RunSteeringCoordinator? steering = null,
        CorrectiveMessageFactory? correctiveMessages = null,
        IPromptLoader? prompts = null,
        ISemanticRefreshCoordinator? semanticRefreshCoordinator = null,
        IManagedRepositoryMemoryService? repositoryMemories = null,
        IRepositoryMemoryOptionsProvider? repositoryMemoryOptions = null,
        SourceEditApplication? sourceEdits = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(correctiveMessages);
        ArgumentNullException.ThrowIfNull(prompts);
        if ((toolPipeline is null) != (toolContextFactory is null))
        {
            throw new ArgumentException(
                "The tool pipeline and invocation-context factory must be configured together.",
                nameof(toolPipeline));
        }

        _events = events;
        _model = model;
        _budgetFactory = budgetFactory ?? budget switch
        {
            ExecutionBudget executionBudget => executionBudget.CreateScope,
            _ => () => budget,
        };
        _sanitizer = sanitizer;
        _logger = logger;
        _toolPipeline = toolPipeline;
        _toolContextFactory = toolContextFactory;
        _contextAssembler = contextAssembler;
        _evidenceStore = evidenceStore;
        _sourceEdits = sourceEdits;
        _hooks = hooks;
        _activeTurnCompactor = activeTurnCompactor;
        _activeTurnCompactionPolicy = activeTurnCompactionPolicy ?? new ActiveTurnCompactionPolicy();
        _activeTurnCompactionPolicy.Validate();
        _activeTurnCompactionProfile = activeTurnCompactionProfile;
        _userUrlIntake = userUrlIntake;
        _toolRegistry = toolRegistry;
        _defaultModelProfileId = defaultModelProfileId;
        _limits = limits ?? ExecutionLimits.Default;
        _sessionPreferences = sessionPreferences;
        _sessionUsage = sessionUsage;
        _selectActiveModel = selectActiveModel;
        _conversationStore = conversationStore;
        _repositoryMemories = repositoryMemories;
        _repositoryMemoryOptions = repositoryMemoryOptions;
        _conversationToolSnapshots = conversationToolSnapshots;
        _steering = steering ?? new RunSteeringCoordinator(_limits);
        _correctiveMessages = correctiveMessages;
        _prompts = prompts;
        _semanticRefreshCoordinator = semanticRefreshCoordinator;
        if (!Enum.IsDefined(defaultConversationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultConversationMode));
        }

        _defaultConversationMode = defaultConversationMode;
    }

    /// <inheritdoc />
    public async Task<SessionId> HandleAsync(
        CreateSessionCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Name);
        var id = SessionId.New();
        if (!_sessions.TryAdd(id, 0))
        {
            throw new InvalidOperationException("The session identifier already exists.");
        }

        await _events.PublishAsync(
            new SessionCreated(id, DateTimeOffset.UtcNow, _sanitizer.Sanitize(command.Name)),
            cancellationToken);
        if (_conversationStore is not null)
        {
            await _conversationStore.SetModeAsync(id, _defaultConversationMode, cancellationToken);
        }

        return id;
    }

    /// <inheritdoc />
    public async Task<RunId> HandleAsync(
        SubmitRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.ContainsKey(command.SessionId))
        {
            throw new InvalidOperationException($"Session {command.SessionId.Value:D} does not exist.");
        }

        if (_semanticRefreshCoordinator is null)
        {
            return AdmitRun(command, default, cancellationToken);
        }

        var admissionStarted = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                var refreshResult = await _semanticRefreshCoordinator.EnsureCurrentAsync(
                    command.SessionId,
                    SemanticRefreshReason.UserAdmission,
                    cancellationToken);

                var expectedWorkspaceId = refreshResult.WorkspaceId;
                var admissionGate = _semanticAdmissionGates.GetOrAdd(
                    SemanticAdmissionKey.Create(command.SessionId, expectedWorkspaceId),
                    static _ => new SemaphoreSlim(1, 1));
                var runId = default(RunId);
                RunRegistration? registration = null;
                var admitted = false;
                await admissionGate.WaitAsync(cancellationToken);
                try
                {
                    admitted = _semanticRefreshCoordinator.TryAdmitCurrent(
                        command.SessionId,
                        expectedWorkspaceId,
                        () => TryRegisterRun(
                            command,
                            expectedWorkspaceId,
                            cancellationToken,
                            out runId,
                            out registration));
                }
                finally
                {
                    admissionGate.Release();
                }

                if (!admitted)
                {
                    continue;
                }

                if (registration is null)
                {
                    throw new InvalidOperationException(
                        "Semantic admission completed without registering a run.");
                }

                _ = ExecuteRunAsync(command, runId, registration);
                return runId;
            }
        }
        finally
        {
            _semanticAdmissionWait.Record(
                Stopwatch.GetElapsedTime(admissionStarted).TotalMilliseconds);
        }
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(
        WaitForRunCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!_runs.TryGetValue(command.RunId, out var registration))
        {
            throw new InvalidOperationException($"Run {command.RunId.Value:D} does not exist.");
        }

        try
        {
            return await registration.Completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            if (registration.Completion.Task.IsCompleted
                && _runs.TryRemove(new KeyValuePair<RunId, RunRegistration>(command.RunId, registration)))
            {
                registration.Cancellation.Dispose();
            }
        }
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(
        CancelRunCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_runs.TryGetValue(command.RunId, out var registration)
            || registration.SessionId != command.SessionId
            || registration.Completion.Task.IsCompleted)
        {
            return false;
        }

        await registration.Cancellation.CancelAsync();
        return true;
    }

    /// <inheritdoc />
    public async Task<RunSteeringPauseRequestResult> HandleAsync(
        RequestRunSteeringPauseCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _steering.RequestPause(command.SessionId, command.RunId);
        if (result.Status == RunSteeringPauseRequestStatus.Accepted)
        {
            await _events.PublishAsync(
                new RunSteeringPauseRequested(
                    command.SessionId,
                    DateTimeOffset.UtcNow,
                    command.RunId,
                    result.PauseId),
                cancellationToken);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<RunSteeringPauseWaitResult> HandleAsync(
        WaitForRunSteeringPauseCommand command,
        CancellationToken cancellationToken = default)
    {
        var result = await _steering.WaitForPauseAsync(
            command.SessionId,
            command.RunId,
            command.PauseId,
            cancellationToken);
        if (result.Status == RunSteeringPauseWaitStatus.Ready
            && _steering.TryMarkPausedPublished(command.SessionId, command.RunId, command.PauseId))
        {
            await _events.PublishAsync(
                new RunSteeringPaused(
                    command.SessionId,
                    DateTimeOffset.UtcNow,
                    command.RunId,
                    command.PauseId),
                cancellationToken);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<RunSteeringSubmissionResult> HandleAsync(
        SubmitRunSteeringCommand command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sanitized = string.IsNullOrWhiteSpace(command.Text)
            ? null
            : _sanitizer.Sanitize(command.Text);
        var result = _steering.Submit(
            command.SessionId,
            command.RunId,
            command.PauseId,
            sanitized);
        if (result.Status is RunSteeringSubmissionStatus.Accepted
            or RunSteeringSubmissionStatus.Dismissed)
        {
            await _events.PublishAsync(
                new RunSteeringSubmitted(
                    command.SessionId,
                    DateTimeOffset.UtcNow,
                    command.RunId,
                    command.PauseId,
                    result.Sequence,
                    result.Status == RunSteeringSubmissionStatus.Accepted),
                cancellationToken);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(
        SetConversationContextModeCommand command,
        CancellationToken cancellationToken = default)
    {
        if (_conversationStore is null || !_sessions.ContainsKey(command.SessionId))
        {
            return false;
        }

        await _conversationStore.SetModeAsync(command.SessionId, command.Mode, cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public Task<ConversationStateSnapshot> HandleAsync(
        GetConversationStateCommand command,
        CancellationToken cancellationToken = default)
    {
        return _conversationStore is null
            ? Task.FromResult(new ConversationStateSnapshot
            {
                SessionId = command.SessionId,
                Warnings = ["Conversation storage is not configured."],
            })
            : _conversationStore.GetSnapshotAsync(
                command.SessionId,
                command.IncludeBodies,
                command.HistoryWindow,
                cancellationToken);
    }

    /// <summary>Revokes admission for a newly created session whose repository binding failed.</summary>
    internal void UnregisterPreparedSession(SessionId sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
    }

    private CorrectiveMessageFactory RequireCorrectiveMessages()
    {
        return _correctiveMessages;
    }

    private IPromptLoader RequirePrompts()
    {
        return _prompts;
    }

    private RunId AdmitRun(
        SubmitRequestCommand command,
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        if (!TryRegisterRun(
            command,
            workspaceId,
            cancellationToken,
            out var runId,
            out var registration))
        {
            throw new InvalidOperationException("The run identifier already exists.");
        }

        _ = ExecuteRunAsync(command, runId, registration);
        return runId;
    }

    private bool TryRegisterRun(
        SubmitRequestCommand command,
        WorkspaceId workspaceId,
        CancellationToken cancellationToken,
        out RunId runId,
        [NotNullWhen(true)] out RunRegistration? registration)
    {
        runId = RunId.New();
        var criteria = command.AcceptanceCriteria?.Select(criterion =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(criterion.Description);
            return criterion with
            {
                Description = _sanitizer.Sanitize(criterion.Description),
            };
        }).ToArray() ?? [];
        var task = new TaskSpecification(
            _sanitizer.Sanitize(command.Request),
            criteria);
        registration = CreateRunRegistration(command.SessionId, runId, workspaceId, task, RunPhase.Intake, cancellationToken);
        if (!_runs.TryAdd(runId, registration))
        {
            registration.Cancellation.Dispose();
            registration = null;
            return false;
        }

        _steering.RegisterRun(command.SessionId, runId);
        return true;
    }

    private RunRegistration CreateRunRegistration(
        SessionId sessionId,
        RunId runId,
        WorkspaceId workspaceId,
        TaskSpecification task,
        RunPhase phase,
        CancellationToken cancellationToken)
    {
        var budget = _budgetFactory()
            ?? throw new InvalidOperationException("The execution budget factory returned no budget.");
        return new RunRegistration(
            sessionId,
            workspaceId,
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken),
            task,
            new RunStateMachine(sessionId, runId, _events, phase),
            budget);
    }

    private async Task ExecuteRunAsync(
        SubmitRequestCommand command,
        RunId runId,
        RunRegistration registration)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var userMessage = await ArchiveVisibleMessageAsync(
                command.SessionId,
                runId,
                ConversationRole.User,
                registration.Task.Intent,
                registration.Cancellation.Token);
            registration.CurrentMessageId = userMessage?.Id;
            registration.SourceMessage = userMessage;
            if (userMessage is not null && _userUrlIntake is not null)
            {
                var userUrlReferences = await _userUrlIntake(
                    command.SessionId,
                    runId,
                    userMessage.Id,
                    command.Request,
                    registration.Cancellation.Token);
                registration.CurrentTurnHostContext =
                [
                    .. userUrlReferences.Select(reference =>
                        _prompts.Render(
                            PromptFileNames.ContextCurrentTurnHostAuthorizedUserUrl,
                            new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["Ordinal"] = reference.Ordinal.ToString(
                                    System.Globalization.CultureInfo.CurrentCulture),
                                ["UserUrlId"] = reference.Id,
                            })),
                ];
            }

            if (_conversationStore is not null)
            {
                var conversationState = await _conversationStore.GetSnapshotAsync(
                    command.SessionId,
                    includeBodies: false,
                    cancellationToken: registration.Cancellation.Token);
                registration.ConversationMode = conversationState.Mode;
            }

            await _events.PublishAsync(
                new TaskIntentRecorded(
                    command.SessionId,
                    DateTimeOffset.UtcNow,
                    registration.Task.Intent),
                registration.Cancellation.Token);
            await _events.PublishAsync(
                new AcceptanceCriteriaRecorded(
                    command.SessionId,
                    DateTimeOffset.UtcNow,
                    registration.Task.AcceptanceCriteria),
                registration.Cancellation.Token);
            await registration.Machine.TransitionAsync(
                RunPhase.EvidenceCollection,
                "request accepted",
                registration.Cancellation.Token);

            await RunConversationAsync(
                runId,
                registration,
                registration.Machine.Phase,
                registration.Cancellation.Token);
            stopwatch.Stop();
            AccrueUnchargedWallClockOrThrow(registration, stopwatch.Elapsed);

            registration.Cancellation.Token.ThrowIfCancellationRequested();

            // Once the terminal state commits, publish its outcome and release waiters even
            // if cancellation arrives while subscribers are processing that final publication.
            var editSucceeded = await RecordDirectEditOutcomeAsync(command.SessionId, runId, ExecutionCheckpointPhase.Completed, registration.Cancellation.Token);
            await registration.Machine.TransitionAsync(
                editSucceeded ? RunPhase.Completion : RunPhase.Failed,
                "scripted activity completed",
                CancellationToken.None);
            await _events.PublishAsync(
                new RunCompleted(command.SessionId, DateTimeOffset.UtcNow, runId, editSucceeded),
                CancellationToken.None);
            registration.Completion.TrySetResult(editSucceeded);
        }
        catch (OperationCanceledException)
        {
            if (registration.Completion.Task.IsCompleted)
            {
                return;
            }

            await RecordDirectEditOutcomeAsync(command.SessionId, runId, ExecutionCheckpointPhase.Cancelled);
            await registration.Machine.TransitionAsync(
                RunPhase.Cancelled,
                "cancellation requested",
                CancellationToken.None);
            await _events.PublishAsync(
                new RunCompleted(command.SessionId, DateTimeOffset.UtcNow, runId, false),
                CancellationToken.None);
            registration.Completion.TrySetCanceled(registration.Cancellation.Token);
        }
        catch (Exception exception)
        {
            var sanitizedMessage = _sanitizer.Sanitize(exception.Message);
            _logger.LogError(
                "Run {RunId} failed for session {SessionId}: {Classification}: {Message}",
                runId.Value,
                command.SessionId.Value,
                ModelFailureClassifier.Classify(exception),
                sanitizedMessage);
            var classification = ModelFailureClassifier.Classify(exception);
            await RecordDirectEditOutcomeAsync(command.SessionId, runId, ExecutionCheckpointPhase.Failed);
            await registration.Machine.TransitionAsync(
                RunPhase.Failed,
                "run failed",
                CancellationToken.None);
            await _events.PublishAsync(
                new DiagnosticObserved(
                    command.SessionId,
                    DateTimeOffset.UtcNow,
                    classification.ToString(),
                    sanitizedMessage),
                CancellationToken.None);
            await _events.PublishAsync(
                new RunCompleted(command.SessionId, DateTimeOffset.UtcNow, runId, false),
                CancellationToken.None);
            registration.Completion.TrySetException(exception);
        }
        finally
        {
            _sourceEdits?.CompleteRun(runId);
            _steering.CompleteRun(command.SessionId, runId);
        }
    }

    private async Task<bool> RecordDirectEditOutcomeAsync(SessionId sessionId, RunId runId, ExecutionCheckpointPhase status, CancellationToken cancellationToken = default)
    {
        if (_sourceEdits is null)
        {
            return true;
        }

        try
        {
            var outcome = await _sourceEdits.RecordRunOutcomeAsync(sessionId, runId, status, cancellationToken);
            if (outcome is not null && _conversationStore is not null)
            {
                try
                {
                    var outcomeJson = JsonSerializer.Serialize(new
                    {
                        RunId = runId.Value,
                        Status = outcome.Status.ToString(),
                        ChangedFiles = outcome.ChangedFiles.Take(32),
                        OmittedFileCount = Math.Max(0, outcome.ChangedFiles.Count - 32),
                        ValidationStatus = outcome.Validation?.Gate.Status.ToString(),
                        ValidationReasons = outcome.Validation?.Gate.Reasons.Take(8),
                        outcome.FinalDiff,
                        ResidualRisks = outcome.ResidualRisks.Take(8),
                    });
                    var content = _prompts.Render(PromptFileNames.ContextExecutionOutcome, new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["OutcomeJson"] = JsonOutputSanitizer.Sanitize(outcomeJson, _sanitizer),
                    });
                    await ArchiveVisibleMessageAsync(sessionId, runId, ConversationRole.Assistant, content, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "The authoritative direct-edit outcome for run {RunId} could not be archived", runId.Value);
                }
            }

            return outcome is null || outcome.Status == ExecutionCheckpointPhase.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Durable per-edit receipts remain authoritative even when optional cumulative evidence fails.
            _logger.LogWarning(exception, "Cumulative direct-edit evidence is unavailable for run {RunId}", runId.Value);
            await _events.PublishAsync(new DiagnosticObserved(sessionId, DateTimeOffset.UtcNow, "SourceEditOutcomeUnavailable", "Cumulative edit evidence is unavailable; individual durable edit receipts remain authoritative."), CancellationToken.None);
            return false;
        }
    }

    private static void AccrueUnchargedWallClockOrThrow(
        RunRegistration registration,
        TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var chargedModelWallClock = registration.ModelRequestWallClockAccrued
            - registration.ModelRequestWallClockSettled;
        if (chargedModelWallClock < TimeSpan.Zero)
        {
            chargedModelWallClock = TimeSpan.Zero;
        }

        var unchargedWallClock = elapsed > chargedModelWallClock
            ? elapsed - chargedModelWallClock
            : TimeSpan.Zero;
        registration.ModelRequestWallClockSettled = registration.ModelRequestWallClockAccrued;
        var elapsedStatus = registration.Budget.Accrue(new BudgetDimensions(
            0,
            0,
            unchargedWallClock));
        if (elapsedStatus.IsExhausted)
        {
            throw new BudgetExceededException(elapsedStatus.Reason ?? "Execution budget exhausted.");
        }
    }

    private static string RenderLegacyContinuation(
        string modelInput,
        IReadOnlyList<ModelMessage> continuationMessages)
    {
        if (continuationMessages.Count == 0)
        {
            return modelInput;
        }

        return modelInput + "\n\n" + string.Join(
            "\n",
            continuationMessages.Select(message =>
                $"<continuation role=\"{message.Role}\" tool=\"{message.ToolName}\" call=\"{message.ToolCallId}\">"
                + $"{System.Security.SecurityElement.Escape(message.GetModelVisibleContent())}"
                + "</continuation>"));
    }

    private static bool BoundContinuationMessages(
        List<ModelMessage> continuationMessages,
        ContextAssemblyResult context,
        IReadOnlyList<ModelToolDefinition> tools,
        ModelRequestLayout layout,
        int? firstNeverDeliveredMessageIndex)
    {
        if (continuationMessages.Count == 0)
        {
            return false;
        }

        var tokenBudget = context.Inspection.TokenBudget;
        var reductionApplied = false;
        ModelWireEstimate Estimate() => ModelWireEstimator.Estimate(
            [.. context.Messages ?? [], .. continuationMessages],
            tools,
            ToolTransportMode.Native,
            layout.StablePrefixMessageCount,
            context.ModelResolution?.EffectiveRequestOutputTokenReserve ?? 0,
            context.ProviderInstructions);
        var estimate = Estimate();
        foreach (var index in continuationMessages
            .Select((message, index) => (message, index))
            .Where(item => item.message.Role == ModelMessageRole.Tool)
            .Where(item => firstNeverDeliveredMessageIndex is null
                || item.index < firstNeverDeliveredMessageIndex.Value)
            .OrderByDescending(item => item.message.GetModelVisibleContentLength())
            .Select(item => item.index))
        {
            if (estimate.WireInputTokens <= tokenBudget)
            {
                return reductionApplied;
            }

            var original = continuationMessages[index];
            var content = original.GetModelVisibleContent();
            var low = 0;
            var high = Math.Max(0, content.Length - 1);
            var smallest = CreateReducedToolResultMessage(original, content, 0);
            continuationMessages[index] = smallest;
            reductionApplied = true;
            estimate = Estimate();
            if (estimate.WireInputTokens > tokenBudget)
            {
                continue;
            }

            var best = smallest;
            while (low <= high)
            {
                var middle = low + ((high - low) / 2);
                var candidate = CreateReducedToolResultMessage(original, content, middle);
                continuationMessages[index] = candidate;
                estimate = Estimate();
                if (estimate.WireInputTokens <= tokenBudget)
                {
                    best = candidate;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            continuationMessages[index] = best;
            return true;
        }

        if (estimate.WireInputTokens > tokenBudget)
        {
            throw new BudgetExceededException(
                $"Tool continuation requires {estimate.WireInputTokens} input tokens but the selected model budget is {tokenBudget}.");
        }

        return reductionApplied;
    }

    private static ModelMessage CreateReducedToolResultMessage(
        ModelMessage original,
        string content,
        int previewCharacters)
    {
        var reduced = previewCharacters == 0
            ? "{\"isTruncated\":true}"
            : JsonSerializer.Serialize(new
            {
                isTruncated = true,
                preview = content[..previewCharacters],
            });
        return original with { Content = [CreateJsonContentPart(reduced)] };
    }

    private static ModelContentPart CreateJsonContentPart(string content, bool isModelVisible = true)
    {
        return new ModelContentPart
        {
            Kind = ModelContentPartKind.Json,
            Content = content,
            IsModelVisible = isModelVisible,
        };
    }

    private static ModelContentPart CreateTextContentPart(string content)
    {
        return new ModelContentPart
        {
            Kind = ModelContentPartKind.Text,
            Content = content,
        };
    }

    private static ModelMessage CreateToolCallMessage(
        string toolCallId,
        string toolName,
        string argumentsJson)
    {
        return new ModelMessage
        {
            Role = ModelMessageRole.Assistant,
            SectionId = "tool-call",
            ToolCallId = toolCallId,
            ToolName = toolName,
            Content = [CreateJsonContentPart(argumentsJson)],
        };
    }

    private static ModelMessage CreateToolResultMessage(
        string toolCallId,
        string toolName,
        string content,
        bool isJson,
        string? structuredContent,
        int? modelRound = null,
        bool? isError = null)
    {
        List<ModelContentPart> contentParts = [isJson ? CreateJsonContentPart(content) : CreateTextContentPart(content)];
        if (!isJson && !string.IsNullOrWhiteSpace(structuredContent))
        {
            contentParts.Add(CreateJsonContentPart(structuredContent, isModelVisible: false));
        }

        return new ModelMessage
        {
            Role = ModelMessageRole.Tool,
            SectionId = "tool-result",
            ToolCallId = toolCallId,
            ToolName = toolName,
            Content = contentParts,
            ModelRound = modelRound,
            IsError = isError,
        };
    }

    private static ReasoningLevel ResolveRequestReasoning(
        SessionModelPreferenceSnapshot? preference,
        ModelResolution? resolution)
    {
        var fallback = resolution?.SupportsReasoningOff == false
            ? resolution.DefaultReasoningLevel
            : (ReasoningLevel?)null;
        return preference?.ResolveFor(resolution?.ProfileId, fallback) ?? fallback ?? ReasoningLevel.None;
    }

    private async Task<ConversationMessage?> ArchiveVisibleMessageAsync(
        SessionId sessionId,
        RunId runId,
        ConversationRole role,
        string content,
        CancellationToken cancellationToken)
    {
        if (_conversationStore is null)
        {
            return null;
        }

        var sanitized = _sanitizer.Sanitize(content);
        var hash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(sanitized)));
        return await _conversationStore.ArchiveMessageAsync(
            new ConversationMessage
            {
                Id = ConversationMessageId.New(),
                SessionId = sessionId,
                RunId = runId,
                Sequence = 0,
                Role = role,
                Content = sanitized,
                ContentHash = hash,
                EstimatedTokens = Math.Max(1, (sanitized.Length + 3) / 4),

                // Repository-session conversation can retain source, paths, or derived findings
                // after secret redaction, so classify it before context assembly/model selection.
                Sensitivity = ConversationSensitivity.Sensitive,
                OccurredAt = DateTimeOffset.UtcNow,
            },
            cancellationToken);
    }

    private static void AddRetainedOutputCharacters(
        int additionalCharacters,
        int maximumCharacters,
        ref int retainedCharacters)
    {
        if (maximumCharacters > 0 && additionalCharacters > maximumCharacters - retainedCharacters)
        {
            throw new MalformedModelOutputException(
                "The model exceeded the host's maximum retained output size.");
        }

        retainedCharacters = checked(retainedCharacters + additionalCharacters);
    }

    private readonly record struct SemanticAdmissionKey(
        WorkspaceId WorkspaceId,
        SessionId SessionId)
    {
        public static SemanticAdmissionKey Create(SessionId sessionId, WorkspaceId workspaceId)
        {
            return workspaceId == default
                ? new SemanticAdmissionKey(default, sessionId)
                : new SemanticAdmissionKey(workspaceId, default);
        }
    }

    private sealed class RunRegistration
    {
        public RunRegistration(
            SessionId sessionId,
            WorkspaceId workspaceId,
            CancellationTokenSource cancellation,
            TaskSpecification task,
            RunStateMachine machine,
            IBudget budget)
        {
            SessionId = sessionId;
            WorkspaceId = workspaceId;
            Cancellation = cancellation;
            Task = task;
            Machine = machine;
            Budget = budget;
        }

        public IBudget Budget { get; }

        public CancellationTokenSource Cancellation { get; }

        public ConversationMessageId? CurrentMessageId { get; set; }

        public IReadOnlyList<string> CurrentTurnHostContext { get; set; } = [];

        public ConversationContextMode ConversationMode { get; set; } = ConversationContextMode.ConversationAware;

        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RunStateMachine Machine { get; }

        public TimeSpan ModelRequestWallClockAccrued { get; set; }

        public TimeSpan ModelRequestWallClockSettled { get; set; }

        public string? RepositoryIdentity { get; set; }

        public RepositoryMemoryOptions? MemoryOptions { get; set; }

        public string? MemoryCurrentInstruction { get; set; }

        public long? MemorySetRevision { get; set; }

        public SortedSet<string> MemoryConcepts { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<RepositoryMemoryInclusion> RetainedMemories { get; set; } = [];

        public string? MemoryRepositoryIdentity { get; set; }

        public int ConceptResolutionRetries { get; set; }

        public bool ConceptOverflowReported { get; set; }

        public bool MemoriesEnabled { get; set; }

        public SessionId SessionId { get; }

        public ConversationMessage? SourceMessage { get; set; }

        public TaskSpecification Task { get; set; }

        public WorkspaceId WorkspaceId { get; }
    }
}

/// <summary>Indicates that a run cannot continue within its configured budget.</summary>
public sealed class BudgetExceededException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="BudgetExceededException"/> class.</summary>
    public BudgetExceededException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="BudgetExceededException"/> class.</summary>
    public BudgetExceededException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="BudgetExceededException"/> class.</summary>
    public BudgetExceededException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
