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
    ICommandHandler<ResumeRunCommand, ExecutionContinuation>,
    ICommandHandler<RequestRunSteeringPauseCommand, RunSteeringPauseRequestResult>,
    ICommandHandler<WaitForRunSteeringPauseCommand, RunSteeringPauseWaitResult>,
    ICommandHandler<SubmitRunSteeringCommand, RunSteeringSubmissionResult>,
    ICommandHandler<ApprovePlanCommand, bool>,
    ICommandHandler<RejectPlanCommand, bool>,
    ICommandHandler<RevisePlanCommand, bool>,
    ICommandHandler<SetConversationContextModeCommand, bool>,
    ICommandHandler<GetConversationStateCommand, ConversationStateSnapshot>,
    ISemanticRefreshPublicationGate
{
    private const string ProposePlanToolName = "propose_plan";
    private const string CompleteObjectiveToolName = "complete_objective";
    private readonly string _proposePlanArgumentsSchema;

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
    private readonly IExecutionOrchestrator? _executionOrchestrator;
    private readonly IHookCoordinator? _hooks;
    private readonly IPlanApprovalPolicy? _planApprovalPolicy;
    private readonly IPlanSanityChecker? _planSanityChecker;
    private readonly ISemanticRefreshCoordinator? _semanticRefreshCoordinator;
    private readonly Func<SessionId, RunId, TaskSpecification, ImplementationPlan, CancellationToken, Task<ExecutionStartRequest?>>?
        _executionRequestFactory;

    private readonly Func<SessionId, ImplementationPlan, CancellationToken, Task<PlanSanityCheckRequest?>>?
        _planSanityRequestFactory;

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
    public async Task<ExecutionContinuation> HandleAsync(
        ResumeRunCommand command,
        CancellationToken cancellationToken = default)
    {
        var orchestrator = _executionOrchestrator
            ?? throw new InvalidOperationException("The execution orchestrator is unavailable.");
        _ = await GetResumeRegistrationAsync(command, cancellationToken);

        var request = await orchestrator.GetResumeRequestAsync(
            command.SessionId,
            command.RunId,
            cancellationToken);
        if (_semanticRefreshCoordinator is null)
        {
            return await ResumeWithAdmissionAsync(
                command,
                request,
                orchestrator,
                cancellationToken);
        }

        while (true)
        {
            var refresh = await _semanticRefreshCoordinator.EnsureCurrentAsync(
                command.SessionId,
                SemanticRefreshReason.UserAdmission,
                cancellationToken);
            if (refresh.WorkspaceId != request.Baseline.WorkspaceId)
            {
                throw new InvalidOperationException(
                    "The resumed execution workspace no longer matches the session's semantic binding.");
            }

            var gate = _semanticAdmissionGates.GetOrAdd(
                SemanticAdmissionKey.Create(command.SessionId, refresh.WorkspaceId),
                static _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (!_semanticRefreshCoordinator.TryAdmitCurrent(
                    command.SessionId,
                    refresh.WorkspaceId,
                    static () => true))
                {
                    continue;
                }

                return await ResumeAndAttachAsync(
                    command,
                    request,
                    orchestrator,
                    cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }
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
            // These completion tasks are the intentional cross-command terminal signals owned by each run.
#pragma warning disable VSTHRD003
            var activeRuns = _runs.Values
                .Where(registration => workspaceId == default
                    ? registration.WorkspaceId == default && registration.SessionId == sessionId
                    : registration.WorkspaceId == workspaceId)
                .Where(registration => !registration.Completion.Task.IsCompleted)
                .Select(registration => registration.Completion.Task)
                .ToArray();
#pragma warning restore VSTHRD003
            foreach (var activeRun in activeRuns)
            {
                await WaitForTerminalStateAsync(activeRun, cancellationToken);
            }

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
        IExecutionOrchestrator? executionOrchestrator = null,
        Func<SessionId, RunId, TaskSpecification, ImplementationPlan, CancellationToken, Task<ExecutionStartRequest?>>?
            executionRequestFactory = null,
        IHookCoordinator? hooks = null,
        Func<IBudget>? budgetFactory = null,
        Func<SessionId, RunId, ConversationMessageId, string, CancellationToken, Task<IReadOnlyList<UserUrlReference>>>?
            userUrlIntake = null,
        IPlanSanityChecker? planSanityChecker = null,
        IPlanApprovalPolicy? planApprovalPolicy = null,
        Func<SessionId, ImplementationPlan, CancellationToken, Task<PlanSanityCheckRequest?>>?
            planSanityRequestFactory = null,
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
        IRepositoryMemoryOptionsProvider? repositoryMemoryOptions = null)
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

        if ((executionOrchestrator is null) != (executionRequestFactory is null))
        {
            throw new ArgumentException(
                "The execution orchestrator and start-request factory must be configured together.",
                nameof(executionOrchestrator));
        }

        _events = events;
        _model = model;
        _budgetFactory = budgetFactory ?? (() => budget);
        _sanitizer = sanitizer;
        _logger = logger;
        _toolPipeline = toolPipeline;
        _toolContextFactory = toolContextFactory;
        _contextAssembler = contextAssembler;
        _evidenceStore = evidenceStore;
        _executionOrchestrator = executionOrchestrator;
        _executionRequestFactory = executionRequestFactory;
        _hooks = hooks;
        _planSanityChecker = planSanityChecker;
        _planApprovalPolicy = planApprovalPolicy;
        _planSanityRequestFactory = planSanityRequestFactory;
        _activeTurnCompactor = activeTurnCompactor;
        _activeTurnCompactionPolicy = activeTurnCompactionPolicy ?? new ActiveTurnCompactionPolicy();
        _activeTurnCompactionPolicy.Validate();
        _activeTurnCompactionProfile = activeTurnCompactionProfile;
        _userUrlIntake = userUrlIntake;
        _toolRegistry = toolRegistry;
        _defaultModelProfileId = defaultModelProfileId;
        _limits = limits ?? ExecutionLimits.Default;
        _proposePlanArgumentsSchema = CreateProposePlanArgumentsSchema(_limits.Plan);
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
        if (registration.PendingApprovalId is { } approvalId)
        {
            await registration.Gate.WaitAsync(cancellationToken);
            try
            {
                if (!registration.Completion.Task.IsCompleted)
                {
                    await _events.PublishAsync(
                        new ApprovalDenied(
                            command.SessionId,
                            DateTimeOffset.UtcNow,
                            approvalId,
                            "Run cancelled while awaiting plan approval."),
                        cancellationToken);
                    registration.PendingApprovalId = null;
                    await registration.Machine.TransitionAsync(
                        RunPhase.Cancelled,
                        "cancellation requested",
                        cancellationToken);
                    await _events.PublishAsync(
                        new RunCompleted(
                            command.SessionId,
                            DateTimeOffset.UtcNow,
                            command.RunId,
                            false),
                        cancellationToken);
                    registration.Completion.TrySetCanceled(registration.Cancellation.Token);
                }
            }
            finally
            {
                registration.Gate.Release();
            }
        }

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
        ApprovePlanCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetPendingPlan(command.SessionId, command.RunId, out var registration))
        {
            return false;
        }

        await registration.Gate.WaitAsync(cancellationToken);
        try
        {
            if (registration.PendingApprovalId is not { } approvalId
                || registration.Completion.Task.IsCompleted)
            {
                return false;
            }

            var approvedPlan = registration.PendingPlan
                ?? throw new InvalidOperationException("The approved plan is no longer available.");
            await _events.PublishAsync(
                new ApprovalGranted(command.SessionId, DateTimeOffset.UtcNow, approvalId),
                cancellationToken);
            registration.PendingApprovalId = null;
            return await ContinueApprovedPlanAsync(
                command.RunId,
                registration,
                approvedPlan,
                approvalId,
                cancellationToken);
        }
        finally
        {
            registration.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(
        RejectPlanCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Reason);
        if (!TryGetPendingPlan(command.SessionId, command.RunId, out var registration))
        {
            return false;
        }

        await registration.Gate.WaitAsync(cancellationToken);
        try
        {
            if (registration.PendingApprovalId is not { } approvalId
                || registration.Completion.Task.IsCompleted)
            {
                return false;
            }

            await _events.PublishAsync(
                new ApprovalDenied(
                    command.SessionId,
                    DateTimeOffset.UtcNow,
                    approvalId,
                    _sanitizer.Sanitize(command.Reason)),
                cancellationToken);
            registration.PendingApprovalId = null;
            await registration.Machine.TransitionAsync(
                RunPhase.Cancelled,
                "plan rejected",
                cancellationToken);
            await _events.PublishAsync(
                new RunCompleted(command.SessionId, DateTimeOffset.UtcNow, command.RunId, false),
                cancellationToken);
            registration.Completion.TrySetResult(false);
            await registration.Cancellation.CancelAsync();
            return true;
        }
        finally
        {
            registration.Gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<bool> HandleAsync(
        RevisePlanCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.RevisionInstructions);
        if (_contextAssembler is null
            || !TryGetPendingPlan(command.SessionId, command.RunId, out var registration))
        {
            return false;
        }

        await registration.Gate.WaitAsync(cancellationToken);
        try
        {
            if (registration.PendingApprovalId is null
                || registration.Completion.Task.IsCompleted)
            {
                return false;
            }

            var instructions = _sanitizer.Sanitize(command.RevisionInstructions);
            await _events.PublishAsync(
                new PlanRevisionRequested(
                    command.SessionId,
                    DateTimeOffset.UtcNow,
                    command.RunId,
                    instructions),
                cancellationToken);
            registration.Task = registration.Task with
            {
                UserConstraints =
                [
                    .. registration.Task.UserConstraints ?? [],
                    $"Plan revision request: {instructions}",
                ],
            };
            registration.PendingApprovalId = null;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                var revisedPublication = await GeneratePlanAsync(
                    command.RunId,
                    registration,
                    RunPhase.AwaitingPlanApproval,
                    cancellationToken) ?? throw new MalformedModelOutputException(
                        "The revision response did not contain a structured plan.");
                stopwatch.Stop();
                AccrueUnchargedWallClockOrThrow(registration, stopwatch.Elapsed);
                await PublishPlanAsync(
                    command.RunId,
                    registration,
                    revisedPublication.Plan,
                    revisedPublication.Decision,
                    cancellationToken);
                return true;
            }
            catch (Exception exception)
            {
                var classification = ModelFailureClassifier.Classify(exception);
                await registration.Machine.TransitionAsync(
                    exception is OperationCanceledException
                        ? RunPhase.Cancelled
                        : RunPhase.Failed,
                    "plan revision failed",
                    CancellationToken.None);
                await _events.PublishAsync(
                    new DiagnosticObserved(
                        command.SessionId,
                        DateTimeOffset.UtcNow,
                        classification.ToString(),
                        _sanitizer.Sanitize(exception.Message)),
                    CancellationToken.None);
                await _events.PublishAsync(
                    new RunCompleted(
                        command.SessionId,
                        DateTimeOffset.UtcNow,
                        command.RunId,
                        false),
                    CancellationToken.None);
                if (exception is OperationCanceledException cancellation)
                {
                    registration.Completion.TrySetCanceled(cancellation.CancellationToken);
                }
                else
                {
                    registration.Completion.TrySetException(exception);
                }

                throw;
            }
        }
        finally
        {
            registration.Gate.Release();
        }
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
                cancellationToken);
    }

    /// <summary>Revokes admission for a newly created session whose repository binding failed.</summary>
    internal void UnregisterPreparedSession(SessionId sessionId) => _sessions.TryRemove(sessionId, out _);

    private static string CreateProposePlanArgumentsSchema(PlanResourceLimits limits) => $$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["summary", "steps", "risks", "outstandingQuestions"],
          "properties": {
            "summary": { "type": "string", "maxLength": {{limits.MaximumSummaryCharacters}} },
            "steps": {
              "type": "array",
              "minItems": 1,
              "maxItems": {{limits.MaximumSteps}},
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["title", "description", "fileIntents", "expectedOutcome", "validation"],
                "properties": {
                  "title": { "type": "string", "maxLength": {{limits.MaximumTitleCharacters}} },
                  "description": { "type": "string", "maxLength": {{limits.MaximumDescriptionCharacters}} },
                  "fileIntents": {
                    "type": "array",
                    "description": "Every step must declare at least one concrete file change. Put review-only or verification work in a relevant step's validation array.",
                    "minItems": 1,
                    "maxItems": {{limits.MaximumMetadataItems}},
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["kind", "path"],
                      "properties": {
                        "kind": { "type": "string", "enum": ["Modify", "Create", "Delete", "Move", "Rename"] },
                        "path": { "type": "string", "maxLength": {{limits.MaximumPathCharacters}} },
                        "destinationPath": {
                          "type": ["string", "null"],
                          "description": "Required for Move/Rename: the repository-relative destination path. Omit for Modify/Create/Delete; use null if the transport requires this property.",
                          "maxLength": {{limits.MaximumPathCharacters}}
                        }
                      }
                    }
                  },
                  "expectedOutcome": { "type": "string", "maxLength": {{limits.MaximumSummaryCharacters}} },
                  "validation": { "type": "array", "maxItems": {{limits.MaximumMetadataItems}}, "items": { "type": "string", "maxLength": {{limits.MaximumSummaryCharacters}} } }
                }
              }
            },
            "risks": { "type": "array", "maxItems": {{limits.MaximumMetadataItems}}, "items": { "type": "string", "maxLength": {{limits.MaximumSummaryCharacters}} } },
            "outstandingQuestions": { "type": "array", "maxItems": {{limits.MaximumMetadataItems}}, "items": { "type": "string", "maxLength": {{limits.MaximumSummaryCharacters}} } }
          }
        }
        """;

    private CorrectiveMessageFactory RequireCorrectiveMessages()
    {
        return _correctiveMessages;
    }

    private IPromptLoader RequirePrompts()
    {
        return _prompts;
    }

    private static async Task WaitForTerminalStateAsync(
        Task<bool> activeRun,
        CancellationToken cancellationToken)
    {
        // Run failure and cancellation are already projected by ExecuteRunAsync. Publication needs only
        // the terminal boundary, while cancellation of this waiter must remain observable to its caller.
        await ((Task)activeRun)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        cancellationToken.ThrowIfCancellationRequested();
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
        registration.SteeringRegistered = true;
        return true;
    }

    private async Task<ExecutionContinuation> ResumeWithAdmissionAsync(
        ResumeRunCommand command,
        ExecutionStartRequest request,
        IExecutionOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var gate = _semanticAdmissionGates.GetOrAdd(
            SemanticAdmissionKey.Create(command.SessionId, request.Baseline.WorkspaceId),
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ResumeAndAttachAsync(
                command,
                request,
                orchestrator,
                cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<RunRegistration?> GetResumeRegistrationAsync(
        ResumeRunCommand command,
        CancellationToken cancellationToken)
    {
        _runs.TryGetValue(command.RunId, out var registration);
        if (registration is not null && registration.SessionId != command.SessionId)
        {
            throw new UnauthorizedAccessException("The execution does not belong to the requesting session.");
        }

        if (registration?.Cancellation.IsCancellationRequested == true
            && registration.ExecutionObservation is { } observation)
        {
            await observation.WaitAsync(cancellationToken);
        }

        return registration;
    }

    private async Task<ExecutionContinuation> ResumeAndAttachAsync(
        ResumeRunCommand command,
        ExecutionStartRequest request,
        IExecutionOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var previous = await GetResumeRegistrationAsync(command, cancellationToken);

        var checkpoint = await orchestrator.ResumeAsync(
            command.SessionId,
            command.RunId,
            cancellationToken);
        if (previous is not null && !previous.Completion.Task.IsCompleted)
        {
            return checkpoint;
        }

        if (previous?.ExecutionObservation is { } finishedObservation)
        {
            await finishedObservation.WaitAsync(cancellationToken);
        }

        var registration = CreateRunRegistration(
            command.SessionId,
            command.RunId,
            request.Baseline.WorkspaceId,
            request.Task,
            RunPhase.ImplementationPreparing,
            cancellationToken);
        var usage = registration.Budget.Accrue(request.InitialBudgetUsage);
        if (usage.IsExhausted)
        {
            registration.Cancellation.Dispose();
            throw new BudgetExceededException(usage.Reason ?? "Execution budget exhausted.");
        }

        registration.ExecutionStarted = true;
        registration.IncrementalPlanExecution = request.AllowPlanContinuation;
        registration.LastPlanBoundaryOrdinal = checkpoint.PlanOrdinal - 1;
        registration.LastPublishedPlan = request.ApprovedPlan;
        if (_conversationStore is not null)
        {
            var snapshot = await _conversationStore.GetSnapshotAsync(
                command.SessionId,
                cancellationToken: cancellationToken);
            registration.SourceMessage = snapshot.Messages.FirstOrDefault(message =>
                message.RunId == command.RunId && message.Role == ConversationRole.User);
            registration.CurrentMessageId = registration.SourceMessage?.Id;
            registration.ConversationMode = snapshot.Mode;
        }

        previous?.Cancellation.Dispose();
        _runs[command.RunId] = registration;
        _steering.RegisterRun(command.SessionId, command.RunId);
        registration.SteeringRegistered = true;
        registration.ExecutionObservation = CompleteExecutionAsync(command.RunId, registration);
        return checkpoint;
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

            registration.BaseCurrentTurnHostContext = registration.CurrentTurnHostContext;
            SetIncrementalPlanningContext(registration, plansUsed: 0);

            if (_conversationStore is not null)
            {
                var conversationState = await _conversationStore.GetSnapshotAsync(
                    command.SessionId,
                    includeBodies: false,
                    registration.Cancellation.Token);
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

            var publication = await GeneratePlanAsync(
                runId,
                registration,
                registration.Machine.Phase,
                registration.Cancellation.Token);
            stopwatch.Stop();
            AccrueUnchargedWallClockOrThrow(registration, stopwatch.Elapsed);

            if (publication is not null)
            {
                if (registration.Machine.Phase == RunPhase.EvidenceCollection)
                {
                    await registration.Machine.TransitionAsync(
                        RunPhase.ChangePlanning,
                        "model proposed governed repository work",
                        registration.Cancellation.Token);
                }

                await PublishPlanAsync(
                    runId,
                    registration,
                    publication.Plan,
                    publication.Decision,
                    registration.Cancellation.Token);
                return;
            }

            registration.Cancellation.Token.ThrowIfCancellationRequested();

            // Once the terminal state commits, publish its outcome and release waiters even
            // if cancellation arrives while subscribers are processing that final publication.
            await registration.Machine.TransitionAsync(
                RunPhase.Completion,
                "scripted activity completed",
                CancellationToken.None);
            await _events.PublishAsync(
                new RunCompleted(command.SessionId, DateTimeOffset.UtcNow, runId, true),
                CancellationToken.None);
            registration.Completion.TrySetResult(true);
        }
        catch (OperationCanceledException)
        {
            if (registration.Completion.Task.IsCompleted)
            {
                return;
            }

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
            if (!registration.ExecutionStarted || registration.Completion.Task.IsCompleted)
            {
                _steering.CompleteRun(command.SessionId, runId);
                registration.SteeringRegistered = false;
            }
        }
    }

    private async Task CompleteExecutionAsync(
        RunId runId,
        RunRegistration registration)
    {
        using var observationCancellation = CancellationTokenSource.CreateLinkedTokenSource(registration.Cancellation.Token);
        try
        {
            var orchestrator = _executionOrchestrator
                ?? throw new InvalidOperationException("The execution orchestrator is unavailable.");
            if (registration.IncrementalPlanExecution)
            {
                await CompleteIncrementalExecutionAsync(runId, registration, orchestrator, observationCancellation.Token);
                return;
            }

            var outcome = await orchestrator.WaitForOutcomeAsync(
                runId,
                observationCancellation.Token);
            await FinalizeExecutionOutcomeAsync(runId, registration, outcome);
        }
        catch (OperationCanceledException)
        {
            if (registration.Completion.Task.IsCompleted)
            {
                return;
            }

            await registration.Machine.TransitionAsync(
                RunPhase.Cancelled,
                "execution cancelled",
                CancellationToken.None);
            await _events.PublishAsync(
                new RunCompleted(
                    registration.SessionId,
                    DateTimeOffset.UtcNow,
                    runId,
                    false),
                CancellationToken.None);
            registration.Completion.TrySetCanceled(registration.Cancellation.Token);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Execution completion observation failed for run {RunId}.",
                runId.Value);
            await registration.Machine.TransitionAsync(
                RunPhase.Failed,
                "execution completion observation failed",
                CancellationToken.None);
            await _events.PublishAsync(
                new RunCompleted(
                    registration.SessionId,
                    DateTimeOffset.UtcNow,
                    runId,
                    false),
                CancellationToken.None);
            registration.Completion.TrySetException(exception);
        }
        finally
        {
            _steering.CompleteRun(registration.SessionId, runId);
            registration.SteeringRegistered = false;
            await observationCancellation.CancelAsync();
        }
    }

    private async Task CompleteIncrementalExecutionAsync(
        RunId runId,
        RunRegistration registration,
        IExecutionOrchestrator orchestrator,
        CancellationToken cancellationToken)
    {
        var terminalTask = orchestrator.WaitForOutcomeAsync(
            runId,
            cancellationToken);
        while (!registration.Completion.Task.IsCompleted)
        {
            var boundaryTask = orchestrator.WaitForPlanCompletionAsync(
                runId,
                registration.LastPlanBoundaryOrdinal,
                cancellationToken);
            var completed = await Task.WhenAny(
                boundaryTask,
                terminalTask);

            if (completed == terminalTask)
            {
                await FinalizeExecutionOutcomeAsync(runId, registration, await terminalTask);
                return;
            }

            var boundary = await boundaryTask;
            registration.LastPlanBoundaryOrdinal = boundary.PlanOrdinal;
            registration.ReplanningPlan = boundary.PlanUnderRevision;
            await ArchiveExecutionOutcomeAsync(
                runId,
                registration,
                boundary.Progress,
                CancellationToken.None);
            registration.PendingPlan = null;
            var boundaryReason = boundary.PlanUnderRevision is null
                ? "validated plan tranche completed; remaining objective assessment started"
                : "implementation requested investigation and replacement of unfinished work";
            await registration.Machine.TransitionAsync(
                RunPhase.EvidenceCollection,
                boundaryReason,
                registration.Cancellation.Token);
            SetIncrementalPlanningContext(registration, boundary.PlanOrdinal, boundary.Progress);

            PlanPublication? publication;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                publication = await GeneratePlanAsync(
                    runId,
                    registration,
                    RunPhase.EvidenceCollection,
                    registration.Cancellation.Token);
            }
            finally
            {
                stopwatch.Stop();
            }

            AccrueUnchargedWallClockOrThrow(registration, stopwatch.Elapsed);
            if (publication is null)
            {
                if (!registration.ObjectiveCompletionRequested || registration.ReplanningPlan is not null)
                {
                    await registration.Machine.TransitionAsync(
                        RunPhase.Cancelled,
                        "objective assessment ended without confirmed completion; explicit resume is available",
                        CancellationToken.None);
                    await _events.PublishAsync(
                        new RunCompleted(registration.SessionId, DateTimeOffset.UtcNow, runId, false),
                        CancellationToken.None);
                    registration.Completion.TrySetResult(false);
                    return;
                }

                var outcome = await orchestrator.CompleteObjectiveAsync(
                    registration.SessionId,
                    runId,
                    registration.Cancellation.Token);
                await FinalizeExecutionOutcomeAsync(runId, registration, outcome);
                return;
            }

            await registration.Machine.TransitionAsync(
                RunPhase.ChangePlanning,
                "model proposed the next governed plan tranche",
                registration.Cancellation.Token);
            await PublishPlanAsync(
                runId,
                registration,
                publication.Plan,
                publication.Decision,
                registration.Cancellation.Token);
        }
    }

    private async Task FinalizeExecutionOutcomeAsync(
        RunId runId,
        RunRegistration registration,
        ExecutionOutcomeProjection outcome)
    {
        var succeeded = outcome.Status == ExecutionCheckpointPhase.Completed;

        // Complete the archived exchange before another request can observe this run as finished.
        await ArchiveExecutionOutcomeAsync(runId, registration, outcome, CancellationToken.None);
        await registration.Machine.TransitionAsync(
            succeeded ? RunPhase.Completion : RunPhase.Failed,
            "authoritative execution outcome recorded",
            CancellationToken.None);
        await _events.PublishAsync(
            new RunCompleted(
                registration.SessionId,
                DateTimeOffset.UtcNow,
                runId,
                succeeded),
            CancellationToken.None);
        registration.Completion.TrySetResult(succeeded);
    }

    private void SetIncrementalPlanningContext(
        RunRegistration registration,
        int plansUsed,
        ExecutionOutcomeProjection? progress = null)
    {
        if (!_limits.IncrementalPlanning.Enabled || _executionOrchestrator is null)
        {
            registration.CurrentTurnHostContext = registration.BaseCurrentTurnHostContext;
            return;
        }

        var options = _limits.IncrementalPlanning;
        registration.CurrentTurnHostContext =
        [
            .. registration.BaseCurrentTurnHostContext,
            .. progress is null ? Array.Empty<string>() : [CreateExecutionOutcomeContent(registration, progress)],
            .. registration.ReplanningPlan is null ? Array.Empty<string>() : [RequirePrompts().Get(PromptFileNames.ContextReplanning)],
            RequirePrompts().Render(
                PromptFileNames.ContextIncrementalPlanning,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["PlansUsed"] = plansUsed.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ["TargetSteps"] = options.TargetSteps.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    ["TargetFiles"] = options.TargetFiles.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                }),
        ];
    }

    private async Task ArchiveExecutionOutcomeAsync(
        RunId runId,
        RunRegistration registration,
        ExecutionOutcomeProjection outcome,
        CancellationToken cancellationToken)
    {
        if (_conversationStore is null)
        {
            return;
        }

        try
        {
            // Keep the authoritative result in ordinary history. Raw diffs and full diagnostics
            // remain in execution artifacts; transcript retention and context budgets still apply.
            await ArchiveVisibleMessageAsync(
                registration.SessionId,
                runId,
                ConversationRole.Assistant,
                CreateExecutionOutcomeContent(registration, outcome),
                cancellationToken);
        }
        catch (Exception exception)
        {
            // An archive failure cannot change the already-persisted mutation outcome.
            _logger.LogWarning(
                exception,
                "Execution outcome for run {RunId} could not be archived in conversation history; the authoritative execution result is unchanged.",
                runId.Value);
        }
    }

    private string CreateExecutionOutcomeContent(RunRegistration registration, ExecutionOutcomeProjection outcome)
    {
        var outcomeJson = JsonSerializer.Serialize(new
        {
            RunId = outcome.RunId.Value,
            Request = registration.Task.Intent,
            Status = outcome.Status.ToString(),
            CompletedStepIds = outcome.CompletedStepIds.Select(step => step.Value),
            UncompletedStepIds = outcome.UncompletedStepIds.Select(step => step.Value),
            outcome.ChangedFiles,
            LifecycleChanges = outcome.LifecycleChanges.Select(change => new
            {
                Type = change.Type.ToString(),
                change.SourcePath,
                change.DestinationPath,
                change.IsCaseOnlyMove,
            }),
            LifecycleReconciliations = outcome.LifecycleReconciliations.Select(item => new
            {
                State = item.State.ToString(),
                item.SourcePath,
                item.DestinationPath,
                item.Reason,
            }),
            outcome.BehaviorSummary,
            Validation = new
            {
                Status = outcome.Validation?.Gate.Status.ToString(),
                Reasons = outcome.Validation?.Gate.Reasons ?? [],
            },
            outcome.RollbackAvailable,
            outcome.FinalDiff,
            outcome.ResidualRisks,
            outcome.ReplanReason,
        });
        return RequirePrompts().Render(
            PromptFileNames.ContextExecutionOutcome,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["OutcomeJson"] = JsonOutputSanitizer.Sanitize(outcomeJson, _sanitizer),
            });
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

    private async Task<PlanSanityEvaluation> CheckPlanSanityAsync(
        RunRegistration registration,
        ImplementationPlan plan,
        CancellationToken cancellationToken)
    {
        if (_planSanityChecker is null || _planSanityRequestFactory is null)
        {
            return CreateUnavailableSanityEvaluation(plan);
        }

        var request = await _planSanityRequestFactory(
            registration.SessionId,
            plan,
            cancellationToken);
        if (request is null)
        {
            return CreateUnavailableSanityEvaluation(plan);
        }

        registration.PendingSanityTrust = request.TrustLevel;
        var result = await _planSanityChecker.CheckAsync(
            request with { Plan = plan },
            cancellationToken);
        return new PlanSanityEvaluation(result, CanAutoApprove: true);
    }

    private static PlanSanityEvaluation CreateUnavailableSanityEvaluation(ImplementationPlan plan)
    {
        return new PlanSanityEvaluation(
            new PlanSanityCheckResult
            {
                Risk = PlanRiskClassification.High,
                Issues =
                [
                    new PlanSanityIssue
                    {
                        Kind = PlanSanityIssueKind.EvidenceUnavailable,
                        IsRepairable = false,
                        IsBlocking = false,
                        Message = "Required host plan sanity evidence is unavailable.",
                    },
                ],
                NormalizedAffectedPaths = plan.Steps.SelectMany(step => step.GetAffectedPaths()).ToArray(),
                DeclaredAffectedPathCount = plan.Steps.Sum(step => step.GetAffectedPaths().Count),
            },
            CanAutoApprove: false);
    }

    private static PlanApprovalDecision RequireManualPlanReview(
        PlanSanityCheckResult sanity,
        PlanApprovalPolicy policy,
        string reason)
    {
        return new PlanApprovalDecision
        {
            Kind = PlanApprovalDecisionKind.RequiresReview,
            Policy = policy,
            Risk = sanity.Risk,
            Reason = reason,
        };
    }

    private static RepositoryTrustLevel ResolveTrust(RunRegistration registration)
    {
        return registration.PendingSanityTrust ?? RepositoryTrustLevel.UntrustedInspection;
    }

    private async Task PublishPlanAsync(
        RunId runId,
        RunRegistration registration,
        ImplementationPlan plan,
        PlanApprovalDecision decision,
        CancellationToken cancellationToken)
    {
        var approvalId = ApprovalId.New();
        if (_hooks is not null)
        {
            var hookDecision = await _hooks.InvokeAsync(
                HookPoint.PlanProposed,
                registration.SessionId,
                runId,
                registration.RepositoryIdentity,
                approvalId.Value,
                0,
                new Dictionary<string, string>
                {
                    ["revision"] = plan.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["stepCount"] = plan.Steps.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["summaryLength"] = plan.Summary.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                cancellationToken: cancellationToken);
            if (hookDecision.Decision == HookDecisionKind.Block)
            {
                throw new UnauthorizedAccessException("A trusted managed lifecycle policy blocked the proposed plan.");
            }
        }

        registration.PendingPlan = plan;
        registration.LastPublishedPlan = plan;
        var autoApproved = decision.Kind == PlanApprovalDecisionKind.AutoApproved;
        registration.PendingApprovalId = autoApproved ? null : approvalId;
        await _events.PublishAsync(
            new PlanProposed(
                registration.SessionId,
                DateTimeOffset.UtcNow,
                plan.Summary,
                runId,
                plan,
                approvalId,
                PlanReviewStatus.Pending)
            {
                SchemaVersion = 2,
            },
            cancellationToken);
        if (autoApproved)
        {
            await _events.PublishAsync(
                new PlanAutoApproved(
                    registration.SessionId,
                    DateTimeOffset.UtcNow,
                    runId,
                    approvalId,
                    decision.Policy,
                    decision.Risk,
                    plan.Revision,
                    _sanitizer.Sanitize(decision.Reason)),
                cancellationToken);
            await _events.PublishAsync(
                new ApprovalGranted(registration.SessionId, DateTimeOffset.UtcNow, approvalId),
                cancellationToken);
            if (registration.Machine.Phase == RunPhase.ChangePlanning)
            {
                await registration.Machine.TransitionAsync(
                    RunPhase.AwaitingPlanApproval,
                    "structured plan approved by policy",
                    cancellationToken);
            }

            _ = await ContinueApprovedPlanAsync(
                runId,
                registration,
                plan,
                approvalId,
                cancellationToken,
                rethrowStartupFailure: false);
            return;
        }

        await _events.PublishAsync(
            new ApprovalRequested(
                registration.SessionId,
                DateTimeOffset.UtcNow,
                approvalId,
                $"Approve plan revision {plan.Revision}: {plan.Summary} ({decision.Risk} risk; {decision.Policy})",
                ApprovalRequestKind.Plan)
            {
                SchemaVersion = 2,
            },
            cancellationToken);
        if (registration.Machine.Phase == RunPhase.ChangePlanning)
        {
            await registration.Machine.TransitionAsync(
                RunPhase.AwaitingPlanApproval,
                "structured plan proposed",
                cancellationToken);
        }
    }

    private async Task<bool> ContinueApprovedPlanAsync(
        RunId runId,
        RunRegistration registration,
        ImplementationPlan approvedPlan,
        ApprovalId approvalId,
        CancellationToken cancellationToken,
        bool rethrowStartupFailure = true)
    {
        if (_hooks is not null)
        {
            _ = await _hooks.InvokeAsync(
                HookPoint.PlanApproved,
                registration.SessionId,
                runId,
                registration.RepositoryIdentity,
                approvalId.Value,
                0,
                new Dictionary<string, string>
                {
                    ["revision"] = approvedPlan.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture),
                },
                cancellationToken: cancellationToken);
        }

        if (_executionOrchestrator is not null && _executionRequestFactory is not null)
        {
            try
            {
                var startRequest = await _executionRequestFactory(
                    registration.SessionId,
                    runId,
                    registration.Task,
                    approvedPlan,
                    cancellationToken) ?? throw new InvalidOperationException(
                        "The approved plan cannot execute without a trusted workspace and selected solution.");
                startRequest = startRequest with
                {
                    InitialBudgetUsage = registration.Budget
                        .Check(new BudgetDimensions(0, 0, TimeSpan.Zero))
                        .Used,
                };

                await registration.Machine.TransitionAsync(
                    RunPhase.ImplementationPreparing,
                    "approved plan entered governed execution",
                    cancellationToken);
                registration.IncrementalPlanExecution = startRequest.AllowPlanContinuation;
                if (!registration.SteeringRegistered)
                {
                    _steering.RegisterRun(registration.SessionId, runId);
                    registration.SteeringRegistered = true;
                }

                if (registration.ExecutionStarted)
                {
                    _ = await _executionOrchestrator.ContinueWithPlanAsync(
                        startRequest,
                        registration.Cancellation.Token);
                }
                else
                {
                    _ = await _executionOrchestrator.StartAsync(
                        startRequest,
                        registration.Cancellation.Token);
                    registration.ExecutionStarted = true;
                    registration.ExecutionObservation = CompleteExecutionAsync(runId, registration);
                }

                return true;
            }
            catch (Exception exception)
            {
                var classification = ModelFailureClassifier.Classify(exception);
                await registration.Machine.TransitionAsync(
                    exception is OperationCanceledException
                        ? RunPhase.Cancelled
                        : RunPhase.Failed,
                    "execution startup failed",
                    CancellationToken.None);
                await _events.PublishAsync(
                    new DiagnosticObserved(
                        registration.SessionId,
                        DateTimeOffset.UtcNow,
                        classification.ToString(),
                        _sanitizer.Sanitize(exception.Message)),
                    CancellationToken.None);
                await _events.PublishAsync(
                    new RunCompleted(
                        registration.SessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        false),
                    CancellationToken.None);
                if (exception is OperationCanceledException cancellation)
                {
                    registration.Completion.TrySetCanceled(cancellation.CancellationToken);
                }
                else
                {
                    registration.Completion.TrySetException(exception);
                }

                await registration.Cancellation.CancelAsync();
                if (registration.SteeringRegistered)
                {
                    _steering.CompleteRun(registration.SessionId, runId);
                    registration.SteeringRegistered = false;
                }

                if (rethrowStartupFailure)
                {
                    throw;
                }

                return false;
            }
        }

        await registration.Machine.TransitionAsync(
            RunPhase.Completion,
            "plan approved in compatibility planning mode",
            cancellationToken);
        await _events.PublishAsync(
            new RunCompleted(registration.SessionId, DateTimeOffset.UtcNow, runId, true),
            cancellationToken);
        registration.Completion.TrySetResult(true);
        return true;
    }

    private static void AddRetainedPlanOutputCharacters(
        ImplementationPlan plan,
        int maximumCharacters,
        ref int retainedCharacters)
    {
        const int planStructuralCharacters = 64;
        const int stepStructuralCharacters = 128;
        const int itemStructuralCharacters = 16;
        AddRetainedOutputCharacters(
            planStructuralCharacters + plan.Summary.Length,
            maximumCharacters,
            ref retainedCharacters);
        foreach (var risk in plan.Risks)
        {
            AddRetainedOutputCharacters(
                itemStructuralCharacters + risk.Length,
                maximumCharacters,
                ref retainedCharacters);
        }

        foreach (var question in plan.OutstandingQuestions)
        {
            AddRetainedOutputCharacters(
                itemStructuralCharacters + question.Length,
                maximumCharacters,
                ref retainedCharacters);
        }

        foreach (var step in plan.Steps)
        {
            AddRetainedOutputCharacters(
                stepStructuralCharacters
                    + step.Title.Length
                    + step.Description.Length
                    + step.ExpectedOutcome.Length,
                maximumCharacters,
                ref retainedCharacters);
            foreach (var path in step.GetAffectedPaths())
            {
                AddRetainedOutputCharacters(
                    itemStructuralCharacters + path.Length,
                    maximumCharacters,
                    ref retainedCharacters);
            }

            foreach (var validation in step.Validation)
            {
                AddRetainedOutputCharacters(
                    itemStructuralCharacters + validation.Length,
                    maximumCharacters,
                    ref retainedCharacters);
            }
        }
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

    private bool TryGetPendingPlan(
        SessionId sessionId,
        RunId runId,
        [NotNullWhen(true)] out RunRegistration? registration)
    {
        if (_runs.TryGetValue(runId, out var found)
            && found.SessionId == sessionId
            && found.PendingApprovalId is not null
            && !found.Completion.Task.IsCompleted)
        {
            registration = found;
            return true;
        }

        registration = null;
        return false;
    }

    private sealed record PlanPublication(
        ImplementationPlan Plan,
        PlanApprovalDecision Decision);

    private sealed record PlanSanityEvaluation(
        PlanSanityCheckResult Result,
        bool CanAutoApprove);

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

        public IReadOnlyList<string> BaseCurrentTurnHostContext { get; set; } = [];

        public ConversationContextMode ConversationMode { get; set; } = ConversationContextMode.ConversationAware;

        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public RunStateMachine Machine { get; }

        public TimeSpan ModelRequestWallClockAccrued { get; set; }

        public TimeSpan ModelRequestWallClockSettled { get; set; }

        public ApprovalId? PendingApprovalId { get; set; }

        public ImplementationPlan? PendingPlan { get; set; }

        public ImplementationPlan? LastPublishedPlan { get; set; }

        public ImplementationPlan? ReplanningPlan { get; set; }

        public int LastPlanBoundaryOrdinal { get; set; }

        public bool ExecutionStarted { get; set; }

        public bool IncrementalPlanExecution { get; set; }

        public Task? ExecutionObservation { get; set; }

        public bool ObjectiveCompletionRequested { get; set; }

        public bool SteeringRegistered { get; set; }

        public RepositoryTrustLevel? PendingSanityTrust { get; set; }

        public string? RepositoryIdentity { get; set; }

        public RepositoryMemoryOptions? MemoryOptions { get; set; }

        public string? MemoryCurrentInstruction { get; set; }

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
