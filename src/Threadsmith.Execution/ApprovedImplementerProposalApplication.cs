namespace Threadsmith.Execution;

using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Prepares approved-plan mutations in a child and stages only successfully joined proposals for separate approval.</summary>
public sealed class ApprovedImplementerProposalApplication :
    ICommandHandler<ProposeMutationSetCommand, StagedMutationSet>
{
    private readonly IDelegationCoordinator _delegations;
    private readonly DelegateAgentsOptions _options;
    private readonly MutationProposalApplication _proposals;
    private readonly AgentModelSelector _selection;
    private readonly SessionModelPreferences? _sessionPreferences;
    private readonly ITransactionalWorkspaceResolver _workspaces;

    /// <summary>Initializes a new instance of the <see cref="ApprovedImplementerProposalApplication"/> class.</summary>
    public ApprovedImplementerProposalApplication(
        MutationProposalApplication proposals,
        IDelegationCoordinator delegations,
        AgentModelSelector selection,
        ITransactionalWorkspaceResolver workspaces,
        DelegateAgentsOptions? options = null,
        SessionModelPreferences? sessionPreferences = null)
    {
        ArgumentNullException.ThrowIfNull(proposals);
        ArgumentNullException.ThrowIfNull(delegations);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(workspaces);
        _proposals = proposals;
        _delegations = delegations;
        _selection = selection;
        _sessionPreferences = sessionPreferences;
        _workspaces = workspaces;
        _options = options ?? new DelegateAgentsOptions();
        _options.Validate();
    }

    /// <inheritdoc />
    public async Task<StagedMutationSet> HandleAsync(
        ProposeMutationSetCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        if (command.Phase is not RunPhase.ImplementationModelTurn and not RunPhase.CorrectionModelTurn)
        {
            return await _proposals.HandleAsync(command, cancellationToken);
        }

        var baseline = _workspaces.GetWorkspace(command.WorkspaceId).Baseline;
        var plan = CreatePlan(command, baseline);
        var assignment = plan.Assignments[0];
        assignment = assignment with
        {
            Policy = _selection.FreezePolicy(assignment, requireToolCalls: true),
        };
        plan = plan with { Assignments = [assignment] };
        var runner = new ApprovedImplementerAssignmentRunner(_proposals, command, plan, _selection, _options);
        var joined = await _delegations.StartAsync(plan, runner, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = runner.GetJoinedProposal(joined);
        var current = _workspaces.GetWorkspace(command.WorkspaceId).Baseline;
        if (!string.Equals(WorkspaceBaselineIdentity.Create(current), plan.Provenance.BaselineIdentity, StringComparison.Ordinal)
            || !string.Equals(current.RepositoryPath, baseline.RepositoryPath, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The approved Implementer proposal belongs to a stale workspace baseline.");
        }

        return await _proposals.StagePreparedAsync(prepared, cancellationToken);
    }

    private DelegationPlan CreatePlan(ProposeMutationSetCommand command, WorkspaceBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(command.ApprovedPlan);
        var acceptedAt = DateTimeOffset.UtcNow;
        var preference = _sessionPreferences?.Capture();
        var assignment = new AgentAssignment
        {
            AssignmentId = AgentAssignmentId.New(),
            ChildRunId = RunId.New(),
            Role = AgentRole.Implementer,
            Mode = AgentRunMode.ReadOnlyBaseline,
            Objective = "Prepare the approved implementation proposal.",
            Tasks = command.ApprovedPlan.Steps.Select(step => step.Description).ToArray(),
            OutputSchema = "approved-implementer-preparation/1",
            StoppingCondition = "Stop after producing one validated candidate mutation set.",
            Deadline = _options.EffectiveChildBudget.CreateDeadline(acceptedAt),
            Scope = new AgentAssignmentScope
            {
                Files = command.ApprovedPlan.Steps.SelectMany(step => step.GetAffectedPaths())
                    .Distinct(StringComparer.Ordinal).ToArray(),
                IsOwnershipProven = true,
            },
            Policy = new AgentPolicySnapshot
            {
                ProhibitedPaths = [.. baseline.ProhibitedPaths ?? []],
                ResultLimits = _options.ResultLimits,
                ModelProfileId = preference?.ProfileId ?? default,
                ReasoningLevel = preference?.Reasoning.ToString() ?? ReasoningLevel.None.ToString(),
                ModelSelectionRationale = "Resolve the configured Implementer route for approved-plan preparation.",
                ContextPolicyVersion = "approved-implementer-context/1",
                ToolPolicyVersion = "approved-implementer-proposal-only/1",
            },
            Budget = _options.EffectiveChildBudget,
            PlanStepIds = command.ApprovedPlan.Steps.Select(step => step.StepId).ToArray(),
            FailurePolicy = AgentFailurePolicy.FailDelegation,
        };
        return new DelegationPlan
        {
            DelegationId = DelegationId.New(),
            Provenance = new DelegationProvenance
            {
                SessionId = command.SessionId,
                ParentRunId = command.RunId,
                WorkspaceId = command.WorkspaceId,
                RepositoryIdentity = baseline.RepositoryPath,
                BaselineIdentity = WorkspaceBaselineIdentity.Create(baseline),
                ApprovedPlanIdentity = command.RunId.Value.ToString("D"),
                ApprovedPlanRevision = command.ApprovedPlan.Revision,
            },
            Assignments = [assignment],
            AssignmentLimits = _options.CreateAssignmentLimits(),
            ParentBudget = _options.EffectiveChildBudget,
            ImplementationAuthorized = true,
            AcceptedAt = acceptedAt,
        };
    }
}
