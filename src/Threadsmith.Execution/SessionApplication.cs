namespace Threadsmith.Execution;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
    ICommandHandler<ApprovePlanCommand, bool>,
    ICommandHandler<RejectPlanCommand, bool>,
    ICommandHandler<RevisePlanCommand, bool>,
    ICommandHandler<SetConversationContextModeCommand, bool>,
    ICommandHandler<GetConversationStateCommand, ConversationStateSnapshot>
{
    private const string ProposePlanToolName = "propose_plan";
    private const string ProposePlanArgumentsSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["schemaVersion", "plan"],
          "properties": {
            "schemaVersion": { "type": "integer", "const": 1 },
            "plan": {
              "type": "object",
              "additionalProperties": false,
              "required": ["schemaVersion", "revision", "summary", "steps", "risks", "outstandingQuestions"],
              "properties": {
                "schemaVersion": { "type": "integer", "const": 2 },
                "revision": { "type": "integer", "minimum": 1 },
                "summary": { "type": "string" },
                "steps": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "additionalProperties": false,
                    "required": ["stepId", "title", "description", "fileIntents", "expectedOutcome", "validation"],
                    "properties": {
                      "stepId": {
                        "type": "object",
                        "additionalProperties": false,
                        "required": ["value"],
                        "properties": { "value": { "type": "string", "format": "uuid" } }
                      },
                      "title": { "type": "string" },
                      "description": { "type": "string" },
                      "fileIntents": {
                        "type": "array",
                        "items": {
                          "type": "object",
                          "additionalProperties": false,
                          "required": ["kind", "path"],
                          "properties": {
                            "kind": { "type": "string", "enum": ["Modify", "Create", "Delete", "Move", "Rename"] },
                            "path": { "type": "string" },
                            "destinationPath": { "type": "string" }
                          }
                        }
                      },
                      "expectedOutcome": { "type": "string" },
                      "validation": { "type": "array", "items": { "type": "string" } }
                    }
                  }
                },
                "risks": { "type": "array", "items": { "type": "string" } },
                "outstandingQuestions": { "type": "array", "items": { "type": "string" } }
              }
            }
          }
        }
        """;

    private readonly Func<IBudget> _budgetFactory;
    private readonly IContextAssembler? _contextAssembler;
    private readonly IConversationCompactor? _conversationCompactor;
    private readonly IConversationMemoryGovernor? _conversationGovernor;
    private readonly IConversationStore? _conversationStore;
    private readonly ConversationContextMode _defaultConversationMode;
    private readonly ModelProfileId? _defaultModelProfileId;
    private readonly IEvidenceStore? _evidenceStore;
    private readonly IExecutionOrchestrator? _executionOrchestrator;
    private readonly IHookCoordinator? _hooks;
    private readonly IPlanApprovalPolicy? _planApprovalPolicy;
    private readonly IPlanSanityChecker? _planSanityChecker;
    private readonly IRepositoryMemoryGovernor? _repositoryMemoryGovernor;
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
        IConversationMemoryGovernor? conversationGovernor = null,
        ConversationContextMode defaultConversationMode = ConversationContextMode.ConversationAware,
        IConversationCompactor? conversationCompactor = null,
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
        IRepositoryMemoryGovernor? repositoryMemoryGovernor = null,
        IActiveTurnCompactor? activeTurnCompactor = null,
        ActiveTurnCompactionPolicy? activeTurnCompactionPolicy = null,
        ActiveTurnCompactionCandidateProfile? activeTurnCompactionProfile = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(budget);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(logger);
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
        _repositoryMemoryGovernor = repositoryMemoryGovernor;
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
        _conversationStore = conversationStore;
        _conversationGovernor = conversationGovernor;
        _conversationCompactor = conversationCompactor;
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
    public Task<RunId> HandleAsync(
        SubmitRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_sessions.ContainsKey(command.SessionId))
        {
            throw new InvalidOperationException($"Session {command.SessionId.Value:D} does not exist.");
        }

        var runId = RunId.New();
        var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
        var machine = new RunStateMachine(command.SessionId, runId, _events);
        var runBudget = _budgetFactory()
            ?? throw new InvalidOperationException("The execution budget factory returned no budget.");
        var registration = new RunRegistration(
            command.SessionId,
            linkedSource,
            task,
            machine,
            runBudget,
            _limits.MaxCorrectiveTurns);
        if (!_runs.TryAdd(runId, registration))
        {
            linkedSource.Dispose();
            throw new InvalidOperationException("The run identifier already exists.");
        }

        _ = ExecuteRunAsync(command, runId, registration);
        return Task.FromResult(runId);
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
                && _runs.TryRemove(command.RunId, out var completed))
            {
                completed.Cancellation.Dispose();
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
            await CompactAtTurnBoundaryAsync(command.SessionId, cancellationToken);
            registration.Completion.TrySetResult(false);
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
            registration.ResetCorrectiveTurns(_limits.MaxCorrectiveTurns);
            try
            {
                var revisedPlan = await GeneratePlanAsync(
                    command.RunId,
                    registration,
                    RunPhase.AwaitingPlanApproval,
                    initialCorrection: null,
                    cancellationToken) ?? throw new MalformedModelOutputException(
                        "The revision response did not contain a structured plan.");
                await PrepareAndPublishPlanAsync(
                    command.RunId,
                    registration,
                    revisedPlan,
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
                        $"Host-authorized current-user URL candidate #{reference.Ordinal}: use web_fetch userUrlId '{reference.Id}'."),
                ];
            }

            if (userMessage is not null && _conversationGovernor is not null)
            {
                await _conversationGovernor.PromoteAsync(
                    new ConversationPromotionRequest
                    {
                        SessionId = command.SessionId,
                        SourceMessage = userMessage,
                        UserRequirements =
                        [
                            registration.Task.Intent,
                            .. registration.Task.AcceptanceCriteria.Select(item => item.Description),
                        ],
                        Constraints = registration.Task.UserConstraints ?? [],
                    },
                    registration.Cancellation.Token);
            }

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

            var plan = await GeneratePlanAsync(
                runId,
                registration,
                registration.Machine.Phase,
                initialCorrection: null,
                registration.Cancellation.Token);

            var elapsed = registration.Budget.Accrue(
                new BudgetDimensions(0, 0, stopwatch.Elapsed));
            if (elapsed.IsExhausted)
            {
                throw new BudgetExceededException(elapsed.Reason ?? "Execution budget exhausted.");
            }

            if (plan is not null)
            {
                if (registration.Machine.Phase == RunPhase.EvidenceCollection)
                {
                    await registration.Machine.TransitionAsync(
                        RunPhase.ChangePlanning,
                        "model proposed governed repository work",
                        registration.Cancellation.Token);
                }

                await PrepareAndPublishPlanAsync(
                    runId,
                    registration,
                    plan,
                    registration.Cancellation.Token);
                return;
            }

            await registration.Machine.TransitionAsync(
                RunPhase.Completion,
                "scripted activity completed",
                registration.Cancellation.Token);
            await PromoteHostObservedMemoryAsync(
                runId,
                registration,
                completedWork: [$"Completed request: {registration.Task.Intent}"],
                cancellationToken: registration.Cancellation.Token);
            await _events.PublishAsync(
                new RunCompleted(command.SessionId, DateTimeOffset.UtcNow, runId, true),
                registration.Cancellation.Token);
            await CompactAtTurnBoundaryAsync(
                command.SessionId,
                registration.Cancellation.Token);
            registration.Completion.TrySetResult(true);
        }
        catch (OperationCanceledException)
        {
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
    }

    private async Task CompleteExecutionAsync(
        RunId runId,
        RunRegistration registration)
    {
        try
        {
            var orchestrator = _executionOrchestrator
                ?? throw new InvalidOperationException("The execution orchestrator is unavailable.");
            var outcome = await orchestrator.WaitForOutcomeAsync(
                runId,
                registration.Cancellation.Token);
            var succeeded = outcome.Status == ExecutionCheckpointPhase.Completed;
            await registration.Machine.TransitionAsync(
                succeeded ? RunPhase.Completion : RunPhase.Failed,
                "authoritative execution outcome recorded",
                CancellationToken.None);
            if (succeeded)
            {
                await PromoteHostObservedMemoryAsync(
                    runId,
                    registration,
                    completedWork: [$"Completed approved execution: {registration.Task.Intent}"],
                    cancellationToken: CancellationToken.None);
            }

            await _events.PublishAsync(
                new RunCompleted(
                    registration.SessionId,
                    DateTimeOffset.UtcNow,
                    runId,
                    succeeded),
                CancellationToken.None);
            await CompactAtTurnBoundaryAsync(registration.SessionId, CancellationToken.None);
            registration.Completion.TrySetResult(succeeded);
        }
        catch (OperationCanceledException)
        {
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
            registration.Completion.TrySetException(exception);
        }
    }

    private async Task CompactAtTurnBoundaryAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        if (_conversationCompactor is null || _evidenceStore is null)
        {
            return;
        }

        try
        {
            var result = await _conversationCompactor.CompactAtTurnBoundaryAsync(
                sessionId,
                _evidenceStore.Snapshot(sessionId),
                force: false,
                cancellationToken);
            if (result.Outcome is ConversationCompactionOutcomeKind.MalformedOutput
                or ConversationCompactionOutcomeKind.UnsupportedProvenance
                or ConversationCompactionOutcomeKind.ProviderFailure
                or ConversationCompactionOutcomeKind.PersistenceFailure)
            {
                _logger.LogWarning(
                    "Conversation compaction ended with {Outcome} for session {SessionId}; prior memory remains active.",
                    result.Outcome,
                    sessionId.Value);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Conversation compaction failed for session {SessionId}; ordinary conversation continues with prior memory.",
                sessionId.Value);
        }
    }

    private async Task PromoteHostObservedMemoryAsync(
        RunId runId,
        RunRegistration registration,
        IReadOnlyList<string>? decisions = null,
        IReadOnlyList<string>? unresolvedQuestions = null,
        IReadOnlyList<string>? completedWork = null,
        CancellationToken cancellationToken = default)
    {
        var repositoryEvidence = _evidenceStore?.Snapshot(registration.SessionId) ?? [];
        if (_conversationGovernor is not null && registration.SourceMessage is { } sourceMessage)
        {
            await _conversationGovernor.PromoteAsync(
                new ConversationPromotionRequest
                {
                    SessionId = registration.SessionId,
                    SourceMessage = sourceMessage,
                    Decisions = decisions ?? [],
                    UnresolvedQuestions = unresolvedQuestions ?? [],
                    CompletedWork = completedWork ?? [],
                    RepositoryEvidence = repositoryEvidence,
                },
                cancellationToken);
        }

        try
        {
            await PromoteHostObservedRepositoryMemoryAsync(
                runId,
                registration,
                decisions ?? [],
                unresolvedQuestions ?? [],
                completedWork ?? [],
                cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Repository-memory promotion failed for run {RunId}; the authoritative run outcome is unchanged.",
                runId.Value);
        }
    }

    private async Task PromoteHostObservedRepositoryMemoryAsync(
        RunId runId,
        RunRegistration registration,
        IReadOnlyList<string> decisions,
        IReadOnlyList<string> unresolvedQuestions,
        IReadOnlyList<string> completedWork,
        CancellationToken cancellationToken)
    {
        if (_repositoryMemoryGovernor is null
            || string.IsNullOrWhiteSpace(registration.RepositoryIdentity))
        {
            return;
        }

        var repositoryIdentity = RepositoryIdentity.Create(registration.RepositoryIdentity);
        foreach (var (kind, content) in CreateRepositoryMemoryCandidates(
            decisions,
            unresolvedQuestions,
            completedWork))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var result = await _repositoryMemoryGovernor.PromoteHostObservedAsync(
                new HostObservedRepositoryMemoryPromotion(
                    registration.SessionId,
                    runId,
                    repositoryIdentity,
                    kind,
                    content),
                cancellationToken);
            foreach (var change in result.StateUpdates.Where(change => change.PreviousValidity != change.Validity))
            {
                await _events.PublishAsync(
                    new RepositoryMemoryValidityChanged(
                        registration.SessionId,
                        DateTimeOffset.UtcNow,
                        repositoryIdentity,
                        change.MemoryId,
                        change.Validity,
                        change.Reason),
                    cancellationToken);
            }

            if (!result.WasInserted)
            {
                continue;
            }

            await _events.PublishAsync(
                new RepositoryMemoryRemembered(
                    registration.SessionId,
                    DateTimeOffset.UtcNow,
                    repositoryIdentity,
                    result.Item.Id,
                    result.Item.Kind,
                    result.Item.Authority),
                cancellationToken);
        }
    }

    private static IReadOnlyList<(RepositoryMemoryKind Kind, string Content)> CreateRepositoryMemoryCandidates(
        IReadOnlyList<string> decisions,
        IReadOnlyList<string> unresolvedQuestions,
        IReadOnlyList<string> completedWork)
    {
        return
        [
            .. decisions.Select(content => (RepositoryMemoryKind.ArchitectureDecision, content)),
            .. unresolvedQuestions.Select(content => (RepositoryMemoryKind.UnresolvedQuestion, content)),
            .. completedWork.Select(content => (RepositoryMemoryKind.WorkflowFact, content)),
        ];
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
                + $"{System.Security.SecurityElement.Escape(string.Concat(message.Content.Select(part => part.Content)))}"
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
            context.ModelResolution?.EffectiveRequestOutputTokenReserve ?? 0);
        var estimate = Estimate();
        foreach (var index in continuationMessages
            .Select((message, index) => (message, index))
            .Where(item => item.message.Role == ModelMessageRole.Tool)
            .Where(item => firstNeverDeliveredMessageIndex is null
                || item.index < firstNeverDeliveredMessageIndex.Value)
            .OrderByDescending(item => item.message.Content.Sum(part => part.Content.Length))
            .Select(item => item.index))
        {
            if (estimate.WireInputTokens <= tokenBudget)
            {
                return reductionApplied;
            }

            var original = continuationMessages[index];
            var content = string.Concat(original.Content.Select(part => part.Content));
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

    private static ModelContentPart CreateJsonContentPart(string content)
    {
        return new ModelContentPart
        {
            Kind = ModelContentPartKind.Json,
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
        string content)
    {
        return new ModelMessage
        {
            Role = ModelMessageRole.Tool,
            SectionId = "tool-result",
            ToolCallId = toolCallId,
            ToolName = toolName,
            Content = [CreateJsonContentPart(content)],
        };
    }

    private static bool TryCreateSemanticFirstSearchCorrection(
        ToolRequestModelOutput tool,
        bool workspaceAvailable,
        bool semanticToolAttempted,
        IReadOnlyList<ModelToolDefinition> modelTools,
        [NotNullWhen(true)] out string? content)
    {
        content = null;
        if (!workspaceAvailable
            || semanticToolAttempted
            || !string.Equals(tool.ToolName, "search", StringComparison.OrdinalIgnoreCase)
            || !TryGetSearchQuery(tool.ArgumentsJson, out var query)
            || !LooksLikeCSharpSymbolOrFileQuery(query))
        {
            return false;
        }

        var hasCodeExplore = modelTools.Any(static definition => string.Equals(
            definition.Name,
            "code_explore",
            StringComparison.OrdinalIgnoreCase));
        var hasFindSymbol = modelTools.Any(static definition => string.Equals(
            definition.Name,
            "find_symbol",
            StringComparison.OrdinalIgnoreCase));
        if (!hasCodeExplore && !hasFindSymbol)
        {
            return false;
        }

        var boundedQuery = BoundSingleLine(query, 160);
        var isFileQuery = boundedQuery.Contains(".cs", StringComparison.OrdinalIgnoreCase);
        var isExactPathQuery = isFileQuery && LooksLikeExactCSharpPathQuery(boundedQuery);
        var isExactSymbolQuery = !isFileQuery && LooksLikeExactCSharpSymbolQuery(boundedQuery);
        if (!isExactPathQuery && !isExactSymbolQuery && !hasFindSymbol)
        {
            return false;
        }

        var suggestedQuery = isExactPathQuery || isExactSymbolQuery
            ? boundedQuery
            : isFileQuery
                ? BoundSingleLine(GetCSharpFileSymbolQuery(query), 160)
                : BoundSingleLine(GetDiscoverableSymbolQuery(query), 160);
        var suggestedTool = hasCodeExplore && (isExactPathQuery || isExactSymbolQuery)
            ? "code_explore"
            : "find_symbol";
        var suggestedCall = suggestedTool == "code_explore" && isExactPathQuery
            ? $"Call code_explore with query '{suggestedQuery}' and a pathAnchors entry for that repository-relative .cs path before text search."
            : suggestedTool == "code_explore"
                ? $"Call code_explore with query '{suggestedQuery}' as an exact symbol anchor before text search."
                : $"Call find_symbol with query '{suggestedQuery}' before text search.";
        content = $"A semantic workspace is loaded and {suggestedTool} is advertised. Do not use search first for C# type, class, symbol, or .cs filename lookup. "
            + $"{suggestedCall} The rejected search query was '{boundedQuery}'. "
            + "Use search only after semantic tools fail, report incomplete evidence, or no semantic tool applies.";
        return true;
    }

    private static bool IsSemanticInspectionTool(string toolName)
    {
        return string.Equals(toolName, "code_explore", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "find_symbol", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "find_references", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "find_implementations", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "call_hierarchy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "symbol_impact", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "csharp_pattern_search", StringComparison.OrdinalIgnoreCase)
            || string.Equals(toolName, "generated_code_query", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetSearchQuery(string argumentsJson, [NotNullWhen(true)] out string? query)
    {
        query = null;
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("query", out var queryElement)
                || queryElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            query = queryElement.GetString();
            return !string.IsNullOrWhiteSpace(query);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool LooksLikeCSharpSymbolOrFileQuery(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Contains(".cs", StringComparison.OrdinalIgnoreCase)
            || ContainsDeclarationKeyword(trimmed))
        {
            return true;
        }

        foreach (var token in ExtractIdentifierTokens(trimmed))
        {
            if (token.Length >= 3
                && token.Any(char.IsUpper)
                && token.Any(char.IsLower))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsDeclarationKeyword(string query)
    {
        return query.Contains("class", StringComparison.OrdinalIgnoreCase)
            || query.Contains("interface", StringComparison.OrdinalIgnoreCase)
            || query.Contains("record", StringComparison.OrdinalIgnoreCase)
            || query.Contains("struct", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeExactCSharpPathQuery(string query)
    {
        var trimmed = query.Trim();
        return trimmed.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            && (trimmed.Contains('/', StringComparison.Ordinal)
                || trimmed.Contains('\\', StringComparison.Ordinal)
                || trimmed.StartsWith(".", StringComparison.Ordinal));
    }

    private static bool LooksLikeExactCSharpSymbolQuery(string query)
    {
        var trimmed = query.Trim();
        var parameterStart = trimmed.IndexOf('(');
        var name = parameterStart < 0 ? trimmed : trimmed[..parameterStart];
        return !string.IsNullOrWhiteSpace(name)
            && !name.Any(char.IsWhiteSpace)
            && LooksLikeQualifiedIdentifier(name)
            && (parameterStart < 0 || trimmed.EndsWith(")", StringComparison.Ordinal));
    }

    private static bool LooksLikeQualifiedIdentifier(string value)
    {
        var parts = value.Split('.');
        return parts.Length > 0 && parts.All(IsIdentifierLike);
    }

    private static bool IsIdentifierLike(string value)
    {
        if (value.Length == 0 || (!char.IsLetter(value[0]) && value[0] != '_'))
        {
            return false;
        }

        return value.Skip(1).All(character => char.IsLetterOrDigit(character) || character == '_');
    }

    private static string GetCSharpFileSymbolQuery(string query)
    {
        var trimmed = query.Trim();
        var separator = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        var fileName = separator < 0 ? trimmed : trimmed[(separator + 1)..];
        return StripCSharpExtension(fileName);
    }

    private static string GetDiscoverableSymbolQuery(string query)
    {
        var token = ExtractIdentifierTokens(query)
            .LastOrDefault(token => token.Any(char.IsUpper) && token.Any(char.IsLower));
        return token ?? StripCSharpExtension(query);
    }

    private static IEnumerable<string> ExtractIdentifierTokens(string query)
    {
        var builder = new StringBuilder(query.Length);
        foreach (var character in query)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                builder.Append(character);
                continue;
            }

            if (builder.Length > 0)
            {
                yield return builder.ToString();
                builder.Clear();
            }
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static string StripCSharpExtension(string query)
    {
        var trimmed = query.Trim();
        return trimmed.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^3]
            : trimmed;
    }

    private static string BoundSingleLine(string value, int maximumCharacters)
    {
        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters];
    }

    /// <summary>Determines whether a conversational tool may be advertised under effective policy.
    /// Explicit denial and non-empty tool allowlists remain authoritative. Read-only capabilities that
    /// require approval are withheld because the ordinary invocation pipeline cannot prompt interactively.
    /// </summary>
    private static bool IsAdvertisedToModel(ToolDefinition definition, ToolInvocationContext? context)
    {
        if (context is null)
        {
            return true;
        }

        if (context.TrustLevel < definition.RequiredTrust)
        {
            return false;
        }

        if (context.DeniedToolIds.Count > 0 && context.DeniedToolIds.Contains(definition.Id))
        {
            return false;
        }

        if (context.DenyAllTools
            || (context.AllowedToolIds.Count > 0 && !context.AllowedToolIds.Contains(definition.Id)))
        {
            return false;
        }

        if (context.RequireApprovalToolIds.Count > 0
            && context.RequireApprovalToolIds.Contains(definition.Id))
        {
            return false;
        }

        return true;
    }

    private static ReasoningLevel ResolveRequestReasoning(
        SessionModelPreferenceSnapshot? preference,
        ModelProfileId? resolvedProfileId)
    {
        return preference?.ResolveFor(resolvedProfileId) ?? ReasoningLevel.None;
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

    private async Task PrepareAndPublishPlanAsync(
        RunId runId,
        RunRegistration registration,
        ImplementationPlan plan,
        CancellationToken cancellationToken)
    {
        var publication = await RunPlanSanityAndPolicyAsync(
            runId,
            registration,
            plan,
            cancellationToken);
        await PublishPlanAsync(
            runId,
            registration,
            publication.Plan,
            publication.Decision,
            cancellationToken);
    }

    private async Task<PlanPublication> RunPlanSanityAndPolicyAsync(
        RunId runId,
        RunRegistration registration,
        ImplementationPlan initialPlan,
        CancellationToken cancellationToken)
    {
        var currentPlan = initialPlan;
        while (true)
        {
            var evaluation = await CheckPlanSanityAsync(
                registration,
                currentPlan,
                cancellationToken);
            var sanity = evaluation.Result;
            await _events.PublishAsync(
                new PlanSanityCheckCompleted(
                    registration.SessionId,
                    DateTimeOffset.UtcNow,
                    runId,
                    currentPlan.Revision,
                    sanity.Risk,
                    sanity.Issues.Count,
                    sanity.Issues.Count(issue => issue.IsBlocking),
                    sanity.Issues.Count(issue => issue.IsBlocking && issue.IsRepairable),
                    sanity.NormalizedAffectedPaths.Count),
                cancellationToken);

            if (sanity.HasNonRepairableBlockingIssues)
            {
                throw new MalformedModelOutputException(
                    "The plan violates a non-repairable sanity-check guardrail.");
            }

            if (sanity.HasRepairableBlockingIssues)
            {
                if (!registration.CorrectiveTurns.TryBeginAttempt(out var attemptNumber))
                {
                    throw new MalformedModelOutputException(
                        "The plan still has repairable sanity-check failures after the corrective-turn budget was exhausted.");
                }

                var repair = _sanitizer.Sanitize(CreatePlanRepairInstructions(sanity));
                await _events.PublishAsync(
                    new PlanRevisionRequested(
                        registration.SessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        repair),
                    cancellationToken);
                registration.PendingPlan = currentPlan;
                var repairedPlan = await GeneratePlanAsync(
                    runId,
                    registration,
                    RunPhase.AwaitingPlanApproval,
                    CorrectiveMessageFactory.CreateHostValidationMessage(
                        "plan-sanity",
                        repair,
                        attemptNumber,
                        registration.CorrectiveTurns.MaximumTurns,
                        "Emit one corrected propose_plan request."),
                    cancellationToken);
                currentPlan = repairedPlan ?? throw new MalformedModelOutputException(
                    "The plan sanity repair response did not contain a structured plan.");
                continue;
            }

            var decision = evaluation.CanAutoApprove
                ? _planApprovalPolicy?.Decide(sanity, ResolveTrust(registration))
                    ?? RequireManualPlanReview(
                        sanity,
                        PlanApprovalPolicy.ReviewAll,
                        "Plan approval policy is not configured; manual review is required.")
                : RequireManualPlanReview(
                    sanity,
                    _planApprovalPolicy?.CurrentPolicy ?? PlanApprovalPolicy.ReviewAll,
                    "Required plan sanity evidence is unavailable; policy auto-approval is forbidden.");
            if (decision.Kind == PlanApprovalDecisionKind.Blocked)
            {
                throw new UnauthorizedAccessException(decision.Reason);
            }

            return new PlanPublication(currentPlan, decision);
        }

        throw new UnreachableException();
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

    private static string CreatePlanRepairInstructions(PlanSanityCheckResult sanity)
    {
        string[] issueSummaries =
        [
            .. sanity.Issues
                .Where(issue => issue.IsBlocking && issue.IsRepairable)
                .Take(6)
                .Select(issue => issue.RelativePath is null
                    ? $"{issue.Kind}: {issue.Message}"
                    : $"{issue.Kind} ({issue.RelativePath}): {issue.Message}"),
        ];
        return "Revise the structured plan before approval. Use exact repository-relative fileIntents and fix: "
            + string.Join("; ", issueSummaries);
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

                await registration.Machine.TransitionAsync(
                    RunPhase.ImplementationPreparing,
                    "approved plan entered governed execution",
                    cancellationToken);
                _ = await _executionOrchestrator.StartAsync(
                    startRequest,
                    registration.Cancellation.Token);
                _ = CompleteExecutionAsync(runId, registration);
                await PromoteHostObservedMemoryAsync(
                    runId,
                    registration,
                    decisions: [$"Approved implementation plan: {approvedPlan.Summary}"],
                    unresolvedQuestions: approvedPlan.OutstandingQuestions,
                    cancellationToken: cancellationToken);
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
        await PromoteHostObservedMemoryAsync(
            runId,
            registration,
            decisions: [$"Approved implementation plan: {approvedPlan.Summary}"],
            unresolvedQuestions: approvedPlan.OutstandingQuestions,
            completedWork: [$"Completed implementation-plan approval: {approvedPlan.Summary}"],
            cancellationToken);
        await _events.PublishAsync(
            new RunCompleted(registration.SessionId, DateTimeOffset.UtcNow, runId, true),
            cancellationToken);
        await CompactAtTurnBoundaryAsync(registration.SessionId, cancellationToken);
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
        if (additionalCharacters > maximumCharacters - retainedCharacters)
        {
            throw new MalformedModelOutputException(
                "The model exceeded the host's maximum retained output size.");
        }

        retainedCharacters += additionalCharacters;
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

    private sealed class RunRegistration
    {
        public RunRegistration(
            SessionId sessionId,
            CancellationTokenSource cancellation,
            TaskSpecification task,
            RunStateMachine machine,
            IBudget budget,
            int maximumCorrectiveTurns)
        {
            SessionId = sessionId;
            Cancellation = cancellation;
            Task = task;
            Machine = machine;
            Budget = budget;
            CorrectiveTurns = new CorrectiveTurnState(Math.Max(0, maximumCorrectiveTurns));
        }

        public IBudget Budget { get; }

        public CancellationTokenSource Cancellation { get; }

        public ConversationMessageId? CurrentMessageId { get; set; }

        public IReadOnlyList<string> CurrentTurnHostContext { get; set; } = [];

        public ConversationContextMode ConversationMode { get; set; } = ConversationContextMode.ConversationAware;

        public CorrectiveTurnState CorrectiveTurns { get; private set; }

        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public RunStateMachine Machine { get; }

        public ApprovalId? PendingApprovalId { get; set; }

        public ImplementationPlan? PendingPlan { get; set; }

        public RepositoryTrustLevel? PendingSanityTrust { get; set; }

        public string? RepositoryIdentity { get; set; }

        public SessionId SessionId { get; }

        public ConversationMessage? SourceMessage { get; set; }

        public TaskSpecification Task { get; set; }

        public void ResetCorrectiveTurns(int maximumCorrectiveTurns)
        {
            CorrectiveTurns = new CorrectiveTurnState(Math.Max(0, maximumCorrectiveTurns));
        }
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
