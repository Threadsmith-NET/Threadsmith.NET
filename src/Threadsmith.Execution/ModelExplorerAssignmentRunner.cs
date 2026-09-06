namespace Threadsmith.Execution;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>Creates one parent-request-fenced Explorer runner for a delegation tool invocation.</summary>
public interface IExplorerAssignmentRunnerFactory
{
    /// <summary>Creates a runner with the parent's exact visible registration identities.</summary>
    IAgentAssignmentRunner Create(ToolExecutionContext parentContext);
}

/// <summary>Composes model-backed Explorer runners from shared host authorities.</summary>
public sealed class ModelExplorerAssignmentRunnerFactory : IExplorerAssignmentRunnerFactory
{
    private readonly AgentFindingAdmission _admission;
    private readonly AgentContextAssembler _contexts;
    private readonly IEvidenceStore _evidence;
    private readonly IChildAgentInstructionProvider _instructions;
    private readonly IModelProvider _models;
    private readonly DelegateAgentsOptions _options;
    private readonly IPromptLoader _prompts;
    private readonly IOutputSanitizer _sanitizer;
    private readonly AgentModelSelector _selection;
    private readonly SessionUsageProjection? _sessionUsage;
    private readonly IConversationToolSnapshotStore _snapshots;
    private readonly RunSteeringCoordinator? _steering;
    private readonly IToolInvocationPipeline _tools;

    /// <summary>Initializes a new instance of the <see cref="ModelExplorerAssignmentRunnerFactory"/> class.</summary>
    public ModelExplorerAssignmentRunnerFactory(
        AgentContextAssembler contexts,
        AgentFindingAdmission admission,
        AgentModelSelector selection,
        IModelProvider models,
        IToolInvocationPipeline tools,
        IEvidenceStore evidence,
        IChildAgentInstructionProvider instructions,
        IConversationToolSnapshotStore snapshots,
        IOutputSanitizer sanitizer,
        DelegateAgentsOptions options,
        IPromptLoader prompts,
        SessionUsageProjection? sessionUsage = null,
        RunSteeringCoordinator? steering = null)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(prompts);
        _contexts = contexts;
        _admission = admission;
        _selection = selection;
        _models = models;
        _tools = tools;
        _evidence = evidence;
        _instructions = instructions;
        _snapshots = snapshots;
        _sanitizer = sanitizer;
        _options = options;
        _prompts = prompts;
        _sessionUsage = sessionUsage;
        _steering = steering;
    }

    /// <inheritdoc />
    public IAgentAssignmentRunner Create(ToolExecutionContext parentContext)
    {
        ArgumentNullException.ThrowIfNull(parentContext);
        var snapshotId = parentContext.Invocation.ModelVisibleToolSnapshotId
            ?? throw new InvalidOperationException(
                "Agent delegation requires the exact parent model-visible tool snapshot.");
        var registrations = _snapshots.Resolve(
            snapshotId,
            parentContext.SessionId,
            parentContext.RunId);
        return new ModelExplorerAssignmentRunner(
            _contexts,
            _admission,
            _selection,
            _models,
            _tools,
            _evidence,
            _instructions,
            _sanitizer,
            _options,
            parentContext,
            registrations,
            _prompts,
            _sessionUsage,
            _steering);
    }
}

/// <summary>Runs one transcript-free Explorer through a bounded model/tool/evidence loop.</summary>
public sealed class ModelExplorerAssignmentRunner : IAgentAssignmentRunner, IAgentOutcomeJoiner
{
    private readonly AgentFindingAdmission _admission;
    private readonly AgentContextAssembler _contexts;
    private readonly IChildAgentInstructionProvider _instructions;
    private readonly ChildAgentModelLoop _loop;
    private readonly ToolExecutionContext _parentContext;
    private readonly AgentModelSelector _selection;
    private readonly RunSteeringCoordinator? _steering;

    /// <summary>Initializes a new instance of the <see cref="ModelExplorerAssignmentRunner"/> class.</summary>
    public ModelExplorerAssignmentRunner(
        AgentContextAssembler contexts,
        AgentFindingAdmission admission,
        AgentModelSelector selection,
        IModelProvider models,
        IToolInvocationPipeline tools,
        IEvidenceStore evidence,
        IChildAgentInstructionProvider instructions,
        IOutputSanitizer sanitizer,
        DelegateAgentsOptions options,
        ToolExecutionContext parentContext,
        IReadOnlyList<ToolRegistration> registrations,
        IPromptLoader prompts,
        SessionUsageProjection? sessionUsage = null,
        RunSteeringCoordinator? steering = null)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(parentContext);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(prompts);
        _contexts = contexts;
        _admission = admission;
        _selection = selection;
        _instructions = instructions;
        _parentContext = parentContext;
        _steering = steering;
        _loop = new ChildAgentModelLoop(
            models,
            tools,
            evidence,
            sanitizer,
            options,
            registrations,
            prompts,
            sessionUsage,
            steering);
    }

    /// <inheritdoc />
    public async Task<AgentRunOutcome> RunAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        CancellationToken cancellationToken = default)
    {
        var frozen = plan.Assignments.SingleOrDefault(
            candidate => candidate.AssignmentId == assignment.AssignmentId);
        if (frozen is null
            || frozen.ChildRunId != assignment.ChildRunId
            || frozen.Role != AgentRole.Explorer
            || frozen.Mode != AgentRunMode.ReadOnlyBaseline)
        {
            throw new UnauthorizedAccessException("The Explorer assignment is not owned by this delegation.");
        }

        try
        {
            var model = _selection.Select(frozen);
            var effective = frozen with
            {
                Policy = frozen.Policy with
                {
                    ModelProfileId = model.ProfileId,
                    ReasoningLevel = model.ReasoningLevel.ToString(),
                    ModelSelectionRationale = string.Join("; ", model.Rationale),
                },
            };
            var context = _contexts.Assemble(plan, frozen);
            var childToolContext = AgentToolPolicy.Scope(
                _parentContext.Invocation,
                plan,
                frozen,
                plan.Provenance.RepositoryIdentity) with
            {
                ModelContextWindowTokens = model.ContextWindowTokens,
                ModelRequestOutputReserveTokens = model.OutputReserveTokens,
                ModelEffectiveInputBudgetTokens = model.ContextWindowTokens
                    - model.OutputReserveTokens,
                VisibleSourceFrontier = null,
            };
            var instructions = await _instructions.GetAsync(
                plan,
                effective,
                _parentContext.Invocation,
                cancellationToken);
            var result = await _loop.RunAsync(
                plan,
                effective,
                context,
                instructions,
                childToolContext,
                model,
                cancellationToken);
            return new AgentRunOutcome
            {
                AssignmentId = frozen.AssignmentId,
                ChildRunId = frozen.ChildRunId,
                Role = frozen.Role,
                Generation = plan.Provenance.Generation,
                Status = AgentRunStatus.Completed,
                Usage = result.Usage,
                Reason = "structured Explorer findings collected",
                ModelProfileId = model.ProfileId,
                Findings = result.Findings,
                DeliveredEvidenceIds = result.DeliveredEvidenceIds,
            };
        }
        finally
        {
            _steering?.CompleteChild(
                plan.Provenance.SessionId,
                plan.Provenance.ParentRunId,
                frozen.ChildRunId);
        }
    }

    /// <inheritdoc />
    public async Task<bool> JoinAsync(
        DelegationPlan plan,
        IReadOnlyList<AgentRunOutcome> outcomes,
        Func<bool> tryCommit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(tryCommit);
        var requests = new List<AgentFindingAdmissionRequest>();
        foreach (var outcome in outcomes)
        {
            if (outcome.Status != AgentRunStatus.Completed
                || outcome.Findings is not { Findings.Count: > 0 } findings)
            {
                continue;
            }

            var assignment = plan.Assignments.Single(candidate =>
                candidate.AssignmentId == outcome.AssignmentId
                && candidate.ChildRunId == outcome.ChildRunId);
            requests.Add(new AgentFindingAdmissionRequest(
                assignment,
                findings,
                outcome.ModelProfileId,
                outcome.DeliveredEvidenceIds.ToHashSet()));
        }

        if (requests.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return tryCommit();
        }

        return await _admission.TryAdmitAsync(
            plan,
            requests,
            tryCommit,
            cancellationToken);
    }
}
