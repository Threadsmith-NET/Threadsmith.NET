namespace Threadsmith.Execution;

using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>One focused historical investigation, without model-selectable authority.</summary>
public sealed record RepositoryArcheologyInput(string Question);

/// <summary>Bounded projection of the existing child execution result.</summary>
public sealed record RepositoryArcheologyOutput(
    string Status, string ReasonCode, string Message, bool Complete,
    string Question, string? ChildRunId, string? TerminationReason,
    DelegateAgentsResult? Investigation)
{
    /// <summary>Host-observed references, separate from unverified child prose.</summary>
    public IReadOnlyList<RepositoryArcheologyEvidence> References { get; init; } = [];

    /// <summary>Evidence references omitted by the bounded projection.</summary>
    public int OmittedReferences { get; init; }

    /// <summary>Explicit scope and completion limits.</summary>
    public IReadOnlyList<string> Limitations { get; init; } = [];
}

/// <summary>Reference to an actual child tool observation.</summary>
public sealed record RepositoryArcheologyEvidence(string Id, string? Path, string? Revision, string Source, string? SourceIdentifier);

/// <summary>Hosts one read-only Explorer through the ordinary delegation scheduler and model loop.</summary>
public sealed class RepositoryArcheologyTool : Tool<RepositoryArcheologyInput, RepositoryArcheologyOutput>, IRepositoryFeatureTool
{
    private readonly IEvidenceStore _evidence;
    private readonly IRepositoryIntelligenceQuery _query;
    private readonly IRepositoryIntelligenceOperations _operations;
    private readonly DelegateAgentsPlanFactory _plans;
    private readonly ModelExplorerAssignmentRunnerFactory? _runners;
    private readonly IDelegationCoordinator _coordinator;
    private readonly IConversationToolSnapshotStore _snapshots;
    private readonly IToolRegistry _registry;
    private readonly RunSteeringCoordinator? _steering;
    private readonly DelegateAgentsOptions _options;
    private readonly AgentResourceBudget _budget;
    private readonly DelegateAgentsResultProjector _projector;
    private readonly IPromptLoader _prompts;
    private readonly ToolDefinition _definition;

    /// <summary>Initializes a new instance of the <see cref="RepositoryArcheologyTool"/> class.</summary>
    public RepositoryArcheologyTool(
        IEvidenceStore evidence,
        IRepositoryIntelligenceQuery query,
        IRepositoryIntelligenceOperations operations,
        DelegateAgentsPlanFactory plans,
        ModelExplorerAssignmentRunnerFactory? runners,
        IDelegationCoordinator coordinator,
        IConversationToolSnapshotStore snapshots,
        IToolRegistry registry,
        DelegateAgentsOptions options,
        IPromptLoader prompts,
        RunSteeringCoordinator? steering = null)
    {
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _runners = runners;
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _steering = steering;
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(prompts);
        _prompts = prompts;
        var inherited = options.EffectiveChildBudget;
        var budget = inherited.EnforceLimits ? inherited : new AgentResourceBudget();
        _budget = budget with
        {
            EnforceLimits = true,
            ModelTurns = budget.ModelTurns > 0 ? Math.Min(8, budget.ModelTurns) : 8,
            ModelTokens = Math.Min(16000, budget.ModelTokens),
            ToolCalls = Math.Min(24, budget.ToolCalls),
            WallTime = inherited.WallTime > TimeSpan.Zero && inherited.WallTime < TimeSpan.FromSeconds(120)
                ? inherited.WallTime : TimeSpan.FromSeconds(120),
            Mutations = 0, Processes = 0, Builds = 0, Tests = 0,
        };
        _options = options with
        {
            EnforceOperationalLimits = true,
            Compaction = options.Compaction with { Enabled = false },
            MaximumChildOutputCharacters = Bound(options.MaximumChildOutputCharacters, 128 * 1024),
            MaximumToolNameCharacters = Bound(options.MaximumToolNameCharacters, 256),
            MaximumToolArgumentBytes = Bound(options.MaximumToolArgumentBytes, 32768),
            MaximumToolArgumentsAggregateBytes = Bound(options.MaximumToolArgumentsAggregateBytes, 98304),
            MaximumToolRequestsPerRound = Bound(options.MaximumToolRequestsPerRound, 24),
            MaximumStructuredResultBytes = Bound(options.MaximumStructuredResultBytes, 32 * 1024),
            MaximumOutputBytes = 64 * 1024,
            MaximumSummaryCharacters = Bound(options.MaximumSummaryCharacters, 2048),
            MaximumProjectedDetailCharacters = Bound(options.MaximumProjectedDetailCharacters, 2048),
            MaximumProjectedOmissionCharacters = Bound(options.MaximumProjectedOmissionCharacters, 512),
            ResultLimits = new AgentResultLimits(),
        };
        _projector = new DelegateAgentsResultProjector(_options, prompts);
        var definition = ToolDefinitionFactory.Create<RepositoryArcheologyInput, RepositoryArcheologyOutput>(
            "repository_archeology",
            () => prompts.Get(PromptFileNames.ToolRepositoryArcheologyDescription),
            ToolCategory.RepositoryInspection,
            RepositoryTrustLevel.TrustedRead,
            ApprovalLevel.None,
            ToolSideEffect.ReadOnly,
            Timeout.InfiniteTimeSpan,
            64 * 1024);
        _definition = definition with
        {
            DisplayName = "Repository archeology",
            SubagentAvailable = false,
            RequiresWorkspace = true,
            AdmissionTimeout = _budget.WallTime,
            AllowDuplicateInvocations = true,
            InputSchema = definition.InputSchema with
            {
                JsonSchema = """{"type":"object","properties":{"question":{"type":"string","minLength":1,"maxLength":2048}},"required":["question"],"additionalProperties":false}""",
            },
            Scheduling = new ToolSchedulingDescriptor
            {
                ConcurrencyMode = ToolConcurrencyMode.ExclusiveSession,
                ClaimResolverId = "delegate-agents-session-v1",
                MaximumSourceConcurrency = int.MaxValue,
            },
        };
    }

    /// <inheritdoc />
    public override ToolDefinition Definition => _definition;

    /// <inheritdoc />
    public RepositoryIntelligenceAvailability CaptureAvailability(string repositoryPath)
    {
        var feature = _query.Capture(RepositoryIdentity.Create(repositoryPath));
        return !feature.Ready ? feature
            : _operations.IsBusy ? new(false, "operation_busy", "Another repository intelligence operation is active.")
            : _runners is null ? new(false, "unavailable", "No Explorer model is available.")
            : new(true, "ready", "A bounded read-only historical investigation is available.");
    }

    /// <inheritdoc />
    public override async Task<ToolExecution<RepositoryArcheologyOutput>> ExecuteAsync(
        RepositoryArcheologyInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
    {
        ValidateInput(input);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Invocation.IsChildAgent || context.Invocation.IntelligenceRun is not null)
        {
            return Reject(input, "policy_denied", "Nested repository investigations are not permitted.");
        }

        var availability = CaptureAvailability(context.Invocation.RepositoryPath);
        if (!availability.Ready || _runners is null)
        {
            return Reject(input, availability.ReasonCode, availability.Message);
        }

        var expiresAt = context.Deadline ?? context.TimeProvider.GetUtcNow() + _budget.WallTime;
        var remaining = expiresAt - context.TimeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return Reject(input, "deadline_exceeded", "The investigation deadline expired while waiting.");
        }

        using var timer = new CancellationTokenSource(remaining, context.TimeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        var request = new DelegateAgentsInput
        {
            Agents =
            [
                new DelegateAgentRequest
                {
                    Task = input.Question, Context = input.Question, ToolAccess = DelegateAgentToolAccess.ReadOnly,
                },
            ],
        };
        var plan = _plans.Create(request, context);
        var assignment = plan.Assignments.Single();
        IRepositoryIntelligenceRun scope;
        try
        {
            scope = await _operations.BeginAsync(context.SessionId, assignment.ChildRunId, context.Invocation.RepositoryPath, RepositoryIntelligenceOperation.Archeology, deadline.Token);
        }
        catch (InvalidOperationException exception)
        {
            var current = CaptureAvailability(context.Invocation.RepositoryPath);
            return Reject(input, current.Ready ? "operation_busy" : current.ReasonCode, exception.Message);
        }

        await using (scope)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, scope.CancellationToken);
            var childParent = context with { Invocation = context.Invocation with { IntelligenceRun = scope } };
            var snapshotId = context.Invocation.ModelVisibleToolSnapshotId
                ?? throw new InvalidOperationException("Archeology requires the initiating model request snapshot.");
            var parentRegistrations = _snapshots.Resolve(snapshotId, context.SessionId, context.RunId);
            var history = _registry.GetRegistrations(context.SessionId, assignment.ChildRunId, childParent.Invocation)
                .Where(registration => registration.Implementation is GitLogTool or GitShowTool or GitDiffTool);
            var registrations = parentRegistrations.Concat(history).DistinctBy(registration => registration.Tool.Definition.Id)
                .Where(registration => registration.Tool.Definition.SubagentAvailable
                    && registration.Tool.Definition.SideEffect == ToolSideEffect.ReadOnly
                    && registration.Tool.Definition.RequiredApproval == ApprovalLevel.None
                    && ConversationToolAvailability.IsAdvertised(registration.Tool.Definition, childParent.Invocation))
                .ToArray();
            assignment = assignment with
            {
                Tasks = [scope.Instructions],
                Budget = _budget,
                Deadline = expiresAt,
                Policy = assignment.Policy with
                {
                    AllowedToolIds = registrations.Select(registration => registration.Tool.Definition.Id).ToArray(),
                    AllowedNetworkToolIds = [], AllowNetwork = false, AllowProcesses = false,
                    IncludeParentEvidence = false, ResultLimits = _options.ResultLimits,
                },
            };
            plan = plan with { Assignments = [assignment], ParentBudget = assignment.Budget };
            var runner = _runners.CreateArcheology(childParent, registrations, _options);
            _steering?.RegisterDelegation(context.SessionId, context.RunId, plan.DelegationId, [assignment.ChildRunId]);
            try
            {
                var checkpoint = await _coordinator.StartAsync(plan, runner, linked.Token);
                var projection = _projector.Project(plan, checkpoint);
                var outcome = checkpoint.ChildOutcomes.SingleOrDefault();
                var references = (outcome?.DeliveredEvidenceIds ?? []).Take(24)
                    .Select(id => _evidence.Find(context.SessionId, id))
                    .Where(item => item is not null && item.RunId == assignment.ChildRunId && item.Kind == EvidenceKind.ToolResult)
                    .OfType<Evidence>()
                    .Select(item => new RepositoryArcheologyEvidence(
                        item.EvidenceId.Value.ToString("D"),
                        BoundedText.Truncate(item.FileDependencies.FirstOrDefault()?.Path ?? string.Empty, 1024, out _),
                        BoundedText.Truncate(item.Provenance.RepositoryRevision ?? string.Empty, 256, out _),
                        BoundedText.Truncate(item.Provenance.Source, 256, out _),
                        BoundedText.Truncate(item.Provenance.SourcePath ?? string.Empty, 1024, out _)))
                    .ToArray();
                while (references.Length > 0 && System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(references).Length > 8 * 1024)
                {
                    references = references[..^1];
                }

                var status = projection.Result.Status.ToString().ToLowerInvariant();
                var reason = deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested ? "deadline_exceeded" : status;
                if ((status == "failed" || reason == "deadline_exceeded") && references.Length > 0 && !cancellationToken.IsCancellationRequested)
                {
                    status = "partial";
                }

                var output = new RepositoryArcheologyOutput(
                    status,
                    reason,
                    "Historical investigation finished.",
                    projection.Result.Status == DelegateAgentsStatus.Completed,
                    input.Question,
                    assignment.ChildRunId.Value.ToString("D"),
                    outcome?.Reason,
                    projection.Result)
                {
                    References = references,
                    OmittedReferences = Math.Max(0, (outcome?.DeliveredEvidenceIds.Count ?? 0) - references.Length),
                    Limitations = status == "completed" ? ["Child conclusions are advisory; verify current source."]
                        : ["The investigation did not finish. Missing findings do not establish that no relevant history exists."],
                };
                var maximumBytes = Math.Min(Definition.MaximumOutputBytes, context.MaximumOutputBytes ?? Definition.MaximumOutputBytes);
                var bounded = BoundOutput(output, plan, checkpoint, maximumBytes);
                return new(bounded, [new ToolProvenanceSource("delegation", plan.DelegationId.Value.ToString("D"))], projection.IsTruncated || bounded != output);
            }
            finally
            {
                // StartAsync joins scheduler work before returning, including cancellation and failure.
                _steering?.CompleteDelegation(context.SessionId, context.RunId, plan.DelegationId);
            }
        }
    }

    /// <inheritdoc />
    protected override void ValidateInput(RepositoryArcheologyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Question) || input.Question.Length > 2048)
        {
            throw new ToolArgumentValidationException("question requires 1–2048 characters and must not be blank.");
        }
    }

    private static int Bound(int configured, int ceiling) => configured > 0 ? Math.Min(configured, ceiling) : ceiling;

    private RepositoryArcheologyOutput BoundOutput(RepositoryArcheologyOutput output, DelegationPlan plan, DelegationCheckpoint checkpoint, int maximumBytes)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(output).Length <= maximumBytes)
        {
            return output;
        }

        // Drop the echoed input before findings. Measure the complete JSON envelope, including
        // escaping, references and truncation metadata, rather than only the child projection.
        output = output with
        {
            Status = output.Status == "completed" ? "partial" : output.Status,
            ReasonCode = output.Status == "completed" ? "output_truncated" : output.ReasonCode,
            Complete = false,
            Question = string.Empty,
            Limitations = [.. output.Limitations, "Result content was omitted to fit the configured output limit."],
        };
        if (JsonSerializer.SerializeToUtf8Bytes(output).Length <= maximumBytes)
        {
            return output;
        }

        while (true)
        {
            var envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(output with { Investigation = null }).Length;

            // The serializer replaces the four-byte null literal with the projected object.
            var investigationBytes = maximumBytes - envelopeBytes + 4;
            if (investigationBytes > 0)
            {
                // Reserve half for structured metadata; a UTF-16 code unit can serialize as
                // six bytes (\uXXXX). The projector still measures every complete candidate.
                var summaryCharacters = Math.Min(_options.MaximumSummaryCharacters, Math.Max(1, investigationBytes / 12));
                var projector = new DelegateAgentsResultProjector(
                    _options with { MaximumSummaryCharacters = summaryCharacters },
                    new DelegateAgentsProjectionLimits(Math.Min(_options.MaximumStructuredResultBytes, investigationBytes)),
                    _prompts);

                // Ordinary delegation preserves free-form responses verbatim. Narrow only this
                // presentation copy; the durable checkpoint retains the complete child response.
                var presentation = checkpoint with
                {
                    ChildOutcomes = checkpoint.ChildOutcomes.Select(outcome => outcome with
                    {
                        Response = outcome.Response is { } response ? BoundedText.Truncate(response, summaryCharacters, out _) : null,
                    }).ToArray(),
                };
                var candidate = output with { Investigation = projector.Project(plan, presentation).Result };
                if (JsonSerializer.SerializeToUtf8Bytes(candidate).Length <= maximumBytes)
                {
                    return candidate;
                }
            }

            if (output.References.Count == 0)
            {
                // The shared projector has a minimum status envelope. Very small configured
                // limits may only admit the outer status and child identity.
                output = output with { Investigation = null, TerminationReason = null, Message = string.Empty, Limitations = ["Output truncated."] };
                return output;
            }

            output = output with
            {
                References = output.References.Take(output.References.Count - 1).ToArray(),
                OmittedReferences = output.OmittedReferences + 1,
            };
        }
    }

    private static ToolExecution<RepositoryArcheologyOutput> Reject(RepositoryArcheologyInput input, string reason, string message)
    {
        return new(new("rejected", reason, message, false, input.Question, null, null, null), [], false);
    }
}
