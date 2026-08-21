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
public sealed class SessionApplication :
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
    private readonly Func<SessionId, RunId, TaskSpecification, ImplementationPlan, CancellationToken, Task<ExecutionStartRequest?>>?
        _executionRequestFactory;

    private readonly Func<SessionId, ImplementationPlan, CancellationToken, Task<PlanSanityCheckRequest?>>?
        _planSanityRequestFactory;

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
            planSanityRequestFactory = null)
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
        var registration = new RunRegistration(command.SessionId, linkedSource, task, machine, runBudget);
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
            try
            {
                var revisedPlan = await GeneratePlanAsync(
                    command.RunId,
                    registration,
                    RunPhase.AwaitingPlanApproval,
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
        RunRegistration registration,
        IReadOnlyList<string>? decisions = null,
        IReadOnlyList<string>? unresolvedQuestions = null,
        IReadOnlyList<string>? completedWork = null,
        CancellationToken cancellationToken = default)
    {
        if (_conversationGovernor is null || registration.SourceMessage is not { } sourceMessage)
        {
            return;
        }

        var repositoryEvidence = _evidenceStore?.Snapshot(registration.SessionId) ?? [];
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

    private static void BoundContinuationMessages(
        List<ModelMessage> continuationMessages,
        ContextAssemblyResult context,
        IReadOnlyList<ModelToolDefinition> tools,
        ModelRequestLayout layout)
    {
        if (continuationMessages.Count == 0)
        {
            return;
        }

        var tokenBudget = context.Inspection.TokenBudget;
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
            .OrderByDescending(item => item.message.Content.Sum(part => part.Content.Length))
            .Select(item => item.index))
        {
            if (estimate.WireInputTokens <= tokenBudget)
            {
                return;
            }

            var original = continuationMessages[index];
            var content = string.Concat(original.Content.Select(part => part.Content));
            var low = 0;
            var high = Math.Max(0, content.Length - 1);
            var smallest = CreateReducedToolResultMessage(original, content, 0);
            continuationMessages[index] = smallest;
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
            return;
        }

        if (estimate.WireInputTokens > tokenBudget)
        {
            throw new BudgetExceededException(
                $"Tool continuation requires {estimate.WireInputTokens} input tokens but the selected model budget is {tokenBudget}.");
        }
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
            || !modelTools.Any(static definition => string.Equals(
                definition.Name,
                "find_symbol",
                StringComparison.OrdinalIgnoreCase))
            || !TryGetSearchQuery(tool.ArgumentsJson, out var query)
            || !LooksLikeCSharpSymbolOrFileQuery(query))
        {
            return false;
        }

        var boundedQuery = BoundSingleLine(query, 160);
        var suggestedQuery = BoundSingleLine(StripCSharpExtension(query), 160);
        content = "A semantic workspace is loaded and find_symbol is advertised. Do not use search first for C# type, class, symbol, or .cs filename lookup. "
            + $"Call find_symbol with query '{suggestedQuery}' before text search. The rejected search query was '{boundedQuery}'. "
            + "Use search only after semantic tools fail, report incomplete evidence, or no semantic tool applies.";
        return true;
    }

    private static bool IsSemanticInspectionTool(string toolName)
    {
        return string.Equals(toolName, "find_symbol", StringComparison.OrdinalIgnoreCase)
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

    private async Task<ImplementationPlan?> GeneratePlanAsync(
        RunId runId,
        RunRegistration registration,
        RunPhase phase,
        CancellationToken cancellationToken)
    {
        var maximumModelRounds = _limits.MaxModelRounds;
        var maximumPlanningToolRounds = Math.Clamp(
            _limits.MaxPlanningToolRounds,
            1,
            Math.Max(1, maximumModelRounds - 1));
        ToolInvocationContext? invocationContext = null;
        if (_toolContextFactory is not null)
        {
            invocationContext = await _toolContextFactory(registration.SessionId, cancellationToken);
            registration.RepositoryIdentity = invocationContext?.RepositoryPath;
        }

        var workspaceAvailable = invocationContext?.WorkspaceId is not null;
        var invokedToolKeys = new HashSet<string>(StringComparer.Ordinal);
        var continuationMessages = new List<ModelMessage>();
        ContextAssemblyResult? frozenContext = null;
        const int maximumRetainedToolCalls = 256;
        var maximumOutputCharacters = _limits.MaxStructuredOutputCharacters;
        var retainedOutputCharacters = 0;
        var retainedToolCalls = 0;
        var semanticToolAttempted = false;
        var planProposalRepairAttempts = 0;
        var maximumPlanProposalRepairAttempts = Math.Max(0, _limits.MaxPlanProposalRepairAttempts);
        for (var modelRound = 1; modelRound <= maximumModelRounds; modelRound++)
        {
            var planningToolsWithheld = phase == RunPhase.EvidenceCollection
                && modelRound > maximumPlanningToolRounds;
            ToolDefinition[] conversationDefinitions = !planningToolsWithheld
                && _toolPipeline is not null
                && _toolRegistry is not null
                ? [.. _toolRegistry.GetDefinitions(registration.SessionId, runId)
                    .Where(definition => definition.SideEffect == ToolSideEffect.ReadOnly
                        || definition.ConversationAvailable)
                    .Where(definition => IsAdvertisedToModel(definition, invocationContext))]
                : [];
            IEnumerable<ToolDefinition> availableDefinitions = conversationDefinitions;
            if (!workspaceAvailable)
            {
                availableDefinitions = conversationDefinitions
                    .Where(definition => !definition.RequiresWorkspace);
            }

            List<ModelToolDefinition> modelTools = [.. availableDefinitions.Select(definition => new ModelToolDefinition
            {
                Name = definition.Id,
                Description = definition.Description,
                ArgumentsJsonSchema = definition.InputSchema.JsonSchema,
            })];
            if (phase == RunPhase.EvidenceCollection)
            {
                modelTools.Add(new ModelToolDefinition
                {
                    Name = ProposePlanToolName,
                    Description = "Propose a governed implementation plan when the user requests repository changes. Calling this tool never mutates files.",
                    ArgumentsJsonSchema = ProposePlanArgumentsSchema,
                });
            }

            modelTools = [.. ModelToolCanonicalizer.Canonicalize(modelTools)];
            var modelPreference = _sessionPreferences?.Capture();
            var context = frozenContext;
            if (_contextAssembler is not null && context is null)
            {
                ContextToolSchema[] toolSchemas = [.. modelTools.Select(definition =>
                    new ContextToolSchema(
                        definition.Name,
                        definition.Description,
                        definition.ArgumentsJsonSchema))];
                context = await _contextAssembler.AssembleAsync(
                    new ContextAssemblyRequest
                    {
                        SessionId = registration.SessionId,
                        RunId = runId,
                        Phase = phase,
                        Task = registration.Task,
                        RepositoryPath = invocationContext?.RepositoryPath
                            ?? Directory.GetCurrentDirectory(),
                        WorkingScope = RepositoryWorkingScope.Resolve(
                            invocationContext?.RepositoryPath ?? Directory.GetCurrentDirectory(),
                            registration.PendingPlan?.Steps.SelectMany(step => step.GetAffectedPaths()),
                            Directory.GetCurrentDirectory()),
                        ProhibitedPaths = invocationContext?.ProhibitedPaths ?? [],
                        ToolSchemas = toolSchemas,
                        RequiredCapabilities = new ModelCapabilitySet
                        {
                            Streaming = true,
                            StructuredOutput = phase != RunPhase.EvidenceCollection,
                            ToolCalls = modelTools.Count > 0,
                        },
                        DefaultModelProfileId = modelPreference?.ProfileId
                            ?? _defaultModelProfileId,
                        PlanUnderRevision = phase == RunPhase.AwaitingPlanApproval
                            ? registration.PendingPlan
                            : null,
                        CurrentTurnHostContext = registration.CurrentTurnHostContext,
                        CurrentMessageId = registration.CurrentMessageId,
                        ConversationModeOverride = registration.ConversationMode,
                        ConversationModeSource = "session-state",
                    },
                    cancellationToken);
                frozenContext = context;
            }

            var textOutput = new StringBuilder(Math.Min(maximumOutputCharacters, 16 * 1024));
            ImplementationPlan? plan = null;
            var toolInvoked = false;
            var usageRequestId = new ModelRequestUsageId(
                runId,
                "conversation",
                modelRound - 1,
                Guid.NewGuid());
            ModelUsage? reportedUsage = null;
            var modelSucceeded = false;
            IReadOnlyList<ModelMessage> requestMessages =
            [
                .. context?.Messages ?? [],
                .. continuationMessages,
            ];
            var wireEstimate = context?.WireEstimate;
            if (context?.Layout is { } requestLayout)
            {
                BoundContinuationMessages(
                    continuationMessages,
                    context,
                    modelTools,
                    requestLayout);
                requestMessages =
                [
                    .. context.Messages ?? [],
                    .. continuationMessages,
                ];
                wireEstimate = ModelWireEstimator.Estimate(
                    requestMessages,
                    modelTools,
                    ToolTransportMode.Native,
                    requestLayout.StablePrefixMessageCount,
                    context.ModelResolution?.EffectiveRequestOutputTokenReserve ?? 0);
            }

            var modelRequest = new ModelStreamRequest
            {
                RunId = runId,
                Input = RenderLegacyContinuation(
                    context?.ModelInput ?? registration.Task.Intent,
                    continuationMessages),
                Seed = 42,
                ToolContinuationRound = modelRound - 1,
                WorkloadClass = context?.WorkloadClass ?? WorkloadClass.General,
                ContainsSensitiveData = context?.ModelConstraints.ContainsSensitiveData
                    ?? false,
                RequiredCapabilities = context?.RequiredCapabilities
                    ?? new ModelCapabilitySet
                    {
                        Streaming = true,
                        StructuredOutput = phase != RunPhase.EvidenceCollection,
                        ToolCalls = modelTools.Count > 0,
                    },
                SelectionConstraints = context?.ModelConstraints ?? new ModelSelectionConstraints(),
                ResolvedProfileId = context?.ModelResolution?.ProfileId,
                ReasoningLevel = ResolveRequestReasoning(
                    modelPreference,
                    context?.ModelResolution?.ProfileId),
                Tools = modelTools,
                Messages = requestMessages,
                Layout = context?.Layout,
                ToolTransportMode = ToolTransportMode.Native,
                WireEstimate = wireEstimate,
            };
            var modelOperationId = usageRequestId.InvocationId;
            if (_hooks is not null)
            {
                var hookDecision = await _hooks.InvokeAsync(
                    HookPoint.BeforeModelRequest,
                    registration.SessionId,
                    runId,
                    invocationContext?.RepositoryPath,
                    modelOperationId,
                    modelRound - 1,
                    new Dictionary<string, string>
                    {
                        ["workload"] = modelRequest.WorkloadClass.ToString(),
                        ["containsSensitiveData"] = modelRequest.ContainsSensitiveData.ToString(),
                        ["toolCount"] = modelRequest.Tools.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    },
                    cancellationToken: cancellationToken);
                if (hookDecision.Decision == HookDecisionKind.Block)
                {
                    throw new UnauthorizedAccessException("A trusted managed lifecycle policy blocked the model request.");
                }
            }

            try
            {
                var toolCallOrdinal = 0;
                var pendingToolCalls = new List<ToolBatchRequest>();
                await foreach (var chunk in _model.StreamAsync(modelRequest, cancellationToken))
                {
                    if (chunk.Reasoning is not null)
                    {
                        AddRetainedOutputCharacters(
                            chunk.Reasoning.Length,
                            maximumOutputCharacters,
                            ref retainedOutputCharacters);
                        await _events.PublishAsync(
                            new ModelReasoningObserved(
                                registration.SessionId,
                                DateTimeOffset.UtcNow,
                                _sanitizer.Sanitize(chunk.Reasoning)),
                            cancellationToken);
                    }

                    if (chunk.Text is not null)
                    {
                        AddRetainedOutputCharacters(
                            chunk.Text.Length,
                            maximumOutputCharacters,
                            ref retainedOutputCharacters);
                        textOutput.Append(chunk.Text);
                        await _events.PublishAsync(
                            new ModelOutputObserved(
                                registration.SessionId,
                                DateTimeOffset.UtcNow,
                                _sanitizer.Sanitize(chunk.Text)),
                            cancellationToken);
                    }

                    if (chunk.Output is PlanModelOutput planOutput)
                    {
                        ModelOutputValidator.Validate(planOutput);
                        AddRetainedPlanOutputCharacters(
                            planOutput.Plan,
                            maximumOutputCharacters,
                            ref retainedOutputCharacters);
                        plan = planOutput.Plan;
                    }

                    if (chunk.Output is ToolRequestModelOutput tool)
                    {
                        AddRetainedOutputCharacters(
                            tool.ToolName.Length + tool.ArgumentsJson.Length,
                            maximumOutputCharacters,
                            ref retainedOutputCharacters);
                        retainedToolCalls++;
                        if (retainedToolCalls > maximumRetainedToolCalls)
                        {
                            throw new MalformedModelOutputException(
                                "The model exceeded the host's maximum retained tool-call count.");
                        }

                        var suppressPipelineInvocation = false;
                        var isProposePlanTool = string.Equals(
                            tool.ToolName,
                            ProposePlanToolName,
                            StringComparison.OrdinalIgnoreCase);
                        if (isProposePlanTool)
                        {
                            if (phase != RunPhase.EvidenceCollection)
                            {
                                throw new MalformedModelOutputException(
                                    "The model requested propose_plan outside the initial conversational turn.");
                            }

                            try
                            {
                                ModelOutputValidator.Validate(tool);
                                plan = ModelOutputValidator.ParsePlan(tool.ArgumentsJson).Plan;
                            }
                            catch (MalformedModelOutputException)
                                when (modelRound < maximumModelRounds
                                    && planProposalRepairAttempts < maximumPlanProposalRepairAttempts)
                            {
                                planProposalRepairAttempts++;
                                toolCallOrdinal++;
                                var toolCallId = $"host-tool-{modelRound.ToString(System.Globalization.CultureInfo.InvariantCulture)}-"
                                    + toolCallOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                                continuationMessages.Add(CreateToolCallMessage(
                                    toolCallId,
                                    tool.ToolName,
                                    tool.ArgumentsJson));
                                const string repairContent =
                                    "The propose_plan arguments did not match the required plan schema. Do not return a text plan. "
                                    + "Call propose_plan again with strict JSON: {schemaVersion:1, plan:{schemaVersion:2, revision:int, summary:string, steps:[{stepId:{value:guid}, title:string, description:string, fileIntents:[{kind:string, path:string, destinationPath:string?}], expectedOutcome:string, validation:string[]}], risks:string[], outstandingQuestions:string[]}}. "
                                    + "Use kind Modify, Create, Delete, Move, or Rename; Move/Rename require destinationPath and other kinds must omit it.";
                                continuationMessages.Add(CreateToolResultMessage(
                                    toolCallId,
                                    tool.ToolName,
                                    repairContent));
                                toolInvoked = true;
                                suppressPipelineInvocation = true;
                            }
                        }
                        else
                        {
                            ModelOutputValidator.Validate(tool);
                            if (IsSemanticInspectionTool(tool.ToolName))
                            {
                                semanticToolAttempted = true;
                            }

                            // Tool activity is published by the pipeline via ToolInvocationStarted /
                            // ToolInvocationCompleted; no transcript answer text is emitted here.
                        }

                        if (!suppressPipelineInvocation
                            && plan is null
                            && _toolPipeline is not null
                            && invocationContext is not null)
                        {
                            toolCallOrdinal++;
                            var toolCallId = $"host-tool-{modelRound.ToString(System.Globalization.CultureInfo.InvariantCulture)}-"
                                + toolCallOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                            continuationMessages.Add(CreateToolCallMessage(
                                toolCallId,
                                tool.ToolName,
                                tool.ArgumentsJson));
                            if (planningToolsWithheld)
                            {
                                const string convergenceContent =
                                    "The host planning-exploration limit was reached, so this inspection tool was not invoked. "
                                    + "Use the gathered evidence and call propose_plan now, or answer directly if no repository change is needed.";
                                continuationMessages.Add(CreateToolResultMessage(
                                    toolCallId,
                                    tool.ToolName,
                                    convergenceContent));
                                toolInvoked = true;
                            }
                            else
                            {
                                var toolKey = $"{tool.ToolName}|{tool.ArgumentsJson}";
                                if (TryCreateSemanticFirstSearchCorrection(
                                    tool,
                                    workspaceAvailable,
                                    semanticToolAttempted,
                                    modelTools,
                                    out var semanticFirstContent))
                                {
                                    if (_evidenceStore is not null)
                                    {
                                        await _evidenceStore.AddAsync(
                                            new Evidence
                                            {
                                                EvidenceId = EvidenceId.New(),
                                                SessionId = registration.SessionId,
                                                RunId = runId,
                                                Kind = EvidenceKind.Failure,
                                                Content = semanticFirstContent,
                                                Provenance = new EvidenceProvenance
                                                {
                                                    Source = "tool:search:semantic-first",
                                                    SemanticConfidence = SemanticConfidenceLevel.FullSemantic,
                                                },
                                                CollectedAt = DateTimeOffset.UtcNow,
                                                Relevance = 1,
                                                EstimatedTokens = Math.Max(1, (semanticFirstContent.Length + 3) / 4),
                                                InvalidationKeys = ["repository", "semantic"],
                                            },
                                            cancellationToken);
                                    }

                                    continuationMessages.Add(CreateToolResultMessage(
                                        toolCallId,
                                        tool.ToolName,
                                        semanticFirstContent));
                                    toolInvoked = _contextAssembler is not null;
                                }
                                else if (invokedToolKeys.Contains(toolKey))
                                {
                                    if (_evidenceStore is not null)
                                    {
                                        var repeatContent =
                                            $"Tool '{tool.ToolName}' was already called with these arguments. "
                                            + "Do not repeat it; use the earlier result or answer the user directly.";
                                        await _evidenceStore.AddAsync(
                                            new Evidence
                                            {
                                                EvidenceId = EvidenceId.New(),
                                                SessionId = registration.SessionId,
                                                RunId = runId,
                                                Kind = EvidenceKind.Failure,
                                                Content = repeatContent,
                                                Provenance = new EvidenceProvenance
                                                {
                                                    Source = $"tool:{tool.ToolName}:duplicate",
                                                    SemanticConfidence = SemanticConfidenceLevel.None,
                                                },
                                                CollectedAt = DateTimeOffset.UtcNow,
                                                Relevance = 1,
                                                EstimatedTokens = Math.Max(1, (repeatContent.Length + 3) / 4),
                                                InvalidationKeys = ["repository"],
                                            },
                                            cancellationToken);
                                        continuationMessages.Add(CreateToolResultMessage(
                                            toolCallId,
                                            tool.ToolName,
                                            repeatContent));
                                        toolInvoked = _contextAssembler is not null;
                                    }
                                }
                                else
                                {
                                    invokedToolKeys.Add(toolKey);
                                    pendingToolCalls.Add(new ToolBatchRequest(
                                        toolCallOrdinal,
                                        toolCallId,
                                        new ToolInvocationRequest
                                        {
                                            SessionId = registration.SessionId,
                                            RunId = runId,
                                            Phase = phase,
                                            ToolId = tool.ToolName,
                                            ArgumentsJson = tool.ArgumentsJson,
                                            Context = invocationContext,
                                        }));
                                }
                            }
                        }
                    }

                    if (chunk.Usage is not null)
                    {
                        reportedUsage = chunk.Usage;
                        _sessionUsage?.Observe(
                            registration.SessionId,
                            usageRequestId,
                            chunk.Usage);
                        var usage = registration.Budget.Accrue(new BudgetDimensions(
                            chunk.Usage.InputTokens + chunk.Usage.OutputTokens,
                            1,
                            TimeSpan.Zero,
                            chunk.Usage.EstimatedCost));
                        if (usage.IsExhausted)
                        {
                            throw new BudgetExceededException(
                                usage.Reason ?? "Execution budget exhausted.");
                        }
                    }
                }

                if (pendingToolCalls.Count > 0 && _toolPipeline is not null)
                {
                    var batchResults = await _toolPipeline.InvokeBatchAsync(
                        pendingToolCalls,
                        cancellationToken);
                    foreach (var batchResult in batchResults.OrderBy(item => item.Ordinal))
                    {
                        var result = batchResult.Result;
                        var content = result.ResultJson ?? result.Error ?? "Tool completed.";
                        if (_evidenceStore is not null)
                        {
                            var source = result.Sources.FirstOrDefault();
                            await _evidenceStore.AddAsync(
                                new Evidence
                                {
                                    EvidenceId = EvidenceId.New(),
                                    SessionId = registration.SessionId,
                                    RunId = runId,
                                    Kind = result.Succeeded ? EvidenceKind.ToolResult : EvidenceKind.Failure,
                                    Content = content,
                                    Provenance = new EvidenceProvenance
                                    {
                                        SourcePath = source?.Identifier,
                                        ToolInvocationId = result.ToolInvocationId,
                                        SemanticConfidence = SemanticConfidenceLevel.None,
                                        Source = $"tool:{result.ToolId}",
                                    },
                                    CollectedAt = DateTimeOffset.UtcNow,
                                    Relevance = result.Succeeded ? 0.8 : 1,
                                    EstimatedTokens = Math.Max(1, (content.Length + 3) / 4),
                                    InvalidationKeys = result.ToolId is "find_symbol"
                                        or "find_references"
                                        or "find_implementations"
                                        ? ["repository", "semantic"]
                                        : ["repository"],
                                },
                                cancellationToken);
                        }

                        continuationMessages.Add(CreateToolResultMessage(
                            batchResult.CorrelationId,
                            result.ToolId,
                            content));
                    }

                    toolInvoked = _contextAssembler is not null;
                }

                modelSucceeded = true;
            }
            finally
            {
                if (_hooks is not null)
                {
                    _ = await _hooks.InvokeAsync(
                        HookPoint.AfterModelRequest,
                        registration.SessionId,
                        runId,
                        invocationContext?.RepositoryPath,
                        modelOperationId,
                        modelRound - 1,
                        new Dictionary<string, string>
                        {
                            ["succeeded"] = modelSucceeded.ToString(),
                            ["usageReported"] = (reportedUsage is not null).ToString(),
                        },
                        cancellationToken: CancellationToken.None);
                }

                if (reportedUsage is null)
                {
                    _sessionUsage?.ObserveMissing(registration.SessionId, usageRequestId);
                }
            }

            if (plan is null && context is not null && phase != RunPhase.EvidenceCollection)
            {
                var candidate = textOutput.ToString().Trim();
                if (candidate.StartsWith('{'))
                {
                    plan = ModelOutputValidator.ParsePlan(candidate).Plan;
                }
            }

            if (plan is not null)
            {
                plan = plan with
                {
                    Summary = _sanitizer.Sanitize(plan.Summary),
                    Steps = plan.Steps.Select(step => step with
                    {
                        Title = _sanitizer.Sanitize(step.Title),
                        Description = _sanitizer.Sanitize(step.Description),
                        FileIntents = step.FileIntents.Select(intent => intent with
                        {
                            Path = intent.Path,
                            DestinationPath = intent.DestinationPath,
                        }).ToArray(),
                        ExpectedOutcome = _sanitizer.Sanitize(step.ExpectedOutcome),
                        Validation = step.Validation
                            .Select(_sanitizer.Sanitize)
                            .ToArray(),
                    }).ToArray(),
                    Risks = plan.Risks.Select(_sanitizer.Sanitize).ToArray(),
                    OutstandingQuestions = plan.OutstandingQuestions
                        .Select(_sanitizer.Sanitize)
                        .ToArray(),
                };
                ModelOutputValidator.Validate(new PlanModelOutput(plan));

                if (registration.PendingPlan is { } previousPlan)
                {
                    plan = plan with { Revision = previousPlan.Revision + 1 };
                }

                return plan;
            }

            if (!toolInvoked)
            {
                var finalResponse = textOutput.ToString();
                if (!string.IsNullOrWhiteSpace(finalResponse))
                {
                    await ArchiveVisibleMessageAsync(
                        registration.SessionId,
                        runId,
                        ConversationRole.Assistant,
                        finalResponse,
                        cancellationToken);
                }

                return null;
            }

            if (modelRound == maximumModelRounds)
            {
                throw new InvalidOperationException(
                    $"The model exceeded the limit of {maximumModelRounds} tool continuation rounds.");
            }
        }

        throw new UnreachableException();
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
        var maximumRepairs = Math.Max(0, _limits.MaxPlanRevisionRepairAttempts);
        for (var attempt = 0; attempt <= maximumRepairs; attempt++)
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

            if (sanity.HasRepairableBlockingIssues && attempt < maximumRepairs)
            {
                var repair = _sanitizer.Sanitize(CreatePlanRepairInstructions(sanity));
                await _events.PublishAsync(
                    new PlanRevisionRequested(
                        registration.SessionId,
                        DateTimeOffset.UtcNow,
                        runId,
                        repair),
                    cancellationToken);
                registration.Task = registration.Task with
                {
                    UserConstraints =
                    [
                        .. registration.Task.UserConstraints ?? [],
                        $"Plan sanity repair request: {repair}",
                    ],
                };
                registration.PendingPlan = currentPlan;
                var repairStopwatch = Stopwatch.StartNew();
                var repairedPlan = await GeneratePlanAsync(
                    runId,
                    registration,
                    RunPhase.AwaitingPlanApproval,
                    cancellationToken);
                AccrueRepairWallClock(registration, repairStopwatch.Elapsed);
                currentPlan = repairedPlan ?? throw new MalformedModelOutputException(
                    "The plan sanity repair response did not contain a structured plan.");
                continue;
            }

            if (!sanity.Passed)
            {
                var reason = sanity.HasRepairableBlockingIssues
                    ? "The plan still has repairable sanity-check failures after the revision budget was exhausted."
                    : "The plan violates a non-repairable sanity-check guardrail.";
                throw new MalformedModelOutputException(reason);
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

    private static void AccrueRepairWallClock(RunRegistration registration, TimeSpan elapsed)
    {
        var status = registration.Budget.Accrue(new BudgetDimensions(0, 0, elapsed));
        if (status.IsExhausted)
        {
            throw new BudgetExceededException(
                status.Reason ?? "Execution wall-clock budget exhausted during plan repair.");
        }
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
            IBudget budget)
        {
            SessionId = sessionId;
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

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public RunStateMachine Machine { get; }

        public ApprovalId? PendingApprovalId { get; set; }

        public ImplementationPlan? PendingPlan { get; set; }

        public RepositoryTrustLevel? PendingSanityTrust { get; set; }

        public string? RepositoryIdentity { get; set; }

        public SessionId SessionId { get; }

        public ConversationMessage? SourceMessage { get; set; }

        public TaskSpecification Task { get; set; }
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
