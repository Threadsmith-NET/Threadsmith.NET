namespace Threadsmith.Execution;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>Admits only verified skill-bound review batches through the existing role scheduler and model loop.</summary>
public sealed class FocusedReviewExecutor : IFocusedReviewExecutor
{
    private readonly IPromptLoader _prompts;
    private readonly ModelExplorerAssignmentRunnerFactory _runners;
    private readonly DelegationCoordinator _coordinator;
    private readonly AgentModelSelector _models;
    private readonly SessionModelPreferences _preferences;
    private readonly DelegateAgentsOptions _options;
    private readonly IConversationToolSnapshotStore _snapshots;
    private readonly IToolRegistry _tools;
    private readonly IToolInvocationPipeline _pipeline;
    private readonly Func<SkillInvocationRequest, CancellationToken, Task<ToolInvocationContext>> _authority;

    /// <summary>Initializes a new instance of the <see cref="FocusedReviewExecutor"/> class.</summary>
    public FocusedReviewExecutor(
        ModelExplorerAssignmentRunnerFactory runners,
        DelegationCoordinator coordinator,
        AgentModelSelector models,
        SessionModelPreferences preferences,
        DelegateAgentsOptions options,
        IConversationToolSnapshotStore snapshots,
        IToolRegistry tools,
        IToolInvocationPipeline pipeline,
        Func<SkillInvocationRequest, CancellationToken, Task<ToolInvocationContext>> authority,
        IPromptLoader prompts)
    {
        _prompts = prompts ?? throw new ArgumentNullException(nameof(prompts));
        _runners = runners ?? throw new ArgumentNullException(nameof(runners));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    /// <inheritdoc />
    public async Task PreflightAsync(SkillInvocationRequest request, CancellationToken cancellationToken = default)
    {
        _ = await ResolveAuthorityAsync(request, null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DelegationPlan>> PrepareAsync(
        SkillInvocationPlan invocation,
        FocusedReviewTarget target,
        IReadOnlyList<IFocusedReviewCompletionPolicy> procedures,
        CancellationToken cancellationToken = default)
    {
        var authority = await ResolveAuthorityAsync(invocation.Request, target, cancellationToken);
        if (procedures.Count != 4 || procedures.Select(item => item.Binding.Role).Distinct().Count() != 4
            || invocation.EffectiveBudget.DelegatedChildren < 4 || invocation.EffectiveBudget.ParallelChildren < 1)
        {
            throw new InvalidOperationException("Focused review requires authority for four total reviewers and at least one active reviewer.");
        }

        var preference = _preferences.Capture();
        var accepted = DateTimeOffset.UtcNow;
        var assignments = procedures.Select(
            procedure =>
        {
            var binding = procedure.Binding;
            if (binding.InvocationId != invocation.Request.InvocationId || binding.SnapshotIdentity != target.Identity || binding.ContractVersion != 1)
            {
                throw new UnauthorizedAccessException("The review procedure does not belong to this verified invocation.");
            }

            var assignment = new AgentAssignment
            {
                AssignmentId = AgentAssignmentId.New(),
                ChildRunId = RunId.New(),
                Role = binding.Role,
                Mode = AgentRunMode.ReadOnlyReview,
                FocusedReview = binding,
                Objective = _prompts.Get(PromptFileNames.ContextFocusedReviewObjective),
                Tasks = [_prompts.Get(PromptFileNames.ContextFocusedReviewTask)],
                InitialContext = JsonSerializer.Serialize(
                    new
                    {
                        target.Mode,
                        target.Repository,
                        target.Branch,
                        target.Revision,
                        target.MergeBase,
                        target.BaseBranch,
                        target.ComparisonRevision,
                        ScopeKind = target.ComparisonRevision is not null || target.MergeBase is not null ? "changes" : "snapshot",
                        target.Identity,
                        target.Instructions,
                        requirements = target.Requirements is null ? null : new
                        {
                            target.Requirements.Source,
                            target.Requirements.Path,
                            target.Requirements.Digest,
                            CriterionCount = target.Requirements.Criteria.Count,
                        },
                    }),
                OutputSchema = "focused-review/1",
                StoppingCondition = _prompts.Get(PromptFileNames.ContextFocusedReviewStoppingCondition),
                Deadline = _options.EffectiveChildBudget.CreateDeadline(accepted),
                Scope = new AgentAssignmentScope { IsOwnershipProven = true },
                Policy = new AgentPolicySnapshot
                {
                    AllowedToolIds = [FocusedReviewReadTool.ToolId],
                    DeniedToolIds = [DelegateAgentsContract.ToolId, "invoke_skill"],
                    TrustCeiling = RepositoryTrustLevel.TrustedRead,
                    AllowNetwork = false,
                    AllowProcesses = false,
                    ProhibitedPaths = authority.ProhibitedPaths.ToArray(),
                    Sensitivity = invocation.Request.Sensitivity,
                    ResultLimits = _options.ResultLimits,
                    ModelProfileId = preference.ProfileId ?? default,
                    ReasoningLevel = preference.Reasoning.ToString(),
                    ModelSelectionRationale = "Use the existing trusted role and inherited model policy.",
                    ContextPolicyVersion = "focused-review-context/1",
                    ToolPolicyVersion = "focused-review-snapshot/1",
                },
                Budget = _options.EffectiveChildBudget,
            };
            return assignment with { Policy = _models.FreezePolicy(assignment) };
        }).ToArray();
        var limits = _options.CreateAssignmentLimits();
        var countLimit = limits.EffectiveLimit(limits.MaximumAssignments);
        var batchSize = Math.Min(invocation.EffectiveBudget.ParallelChildren, countLimit == 0 ? 4 : countLimit);
        return assignments.Chunk(batchSize).Select(
            batch => new DelegationPlan
            {
                DelegationId = DelegationId.New(),
                Assignments = batch,
                AcceptedAt = accepted,
                AssignmentLimits = limits,
                ParentBudget = AgentResourceBudget.Aggregate(batch.Select(item => item.Budget).ToArray()),
                Provenance = new DelegationProvenance
                {
                    ToolInvocationId = ToolInvocationId.New(),
                    ReviewInvocationId = invocation.Request.InvocationId,
                    SessionId = invocation.Request.SessionId,
                    ParentRunId = invocation.Request.RunId,
                    RepositoryIdentity = authority.RepositoryPath,
                    BaselineIdentity = target.Identity,
                    WorkspaceId = invocation.Request.WorkspaceId ?? WorkspaceId.New(),
                    Generation = procedures[0].Binding.Generation,
                },
            }).ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentRunOutcome>> ExecuteAsync(
        SkillInvocationPlan invocation,
        DelegationPlan plan,
        FocusedReviewTarget target,
        IReadOnlyList<IFocusedReviewCompletionPolicy> procedures,
        bool restoreOnly,
        CancellationToken cancellationToken = default)
    {
        var authority = await ResolveAuthorityAsync(invocation.Request, target, cancellationToken);
        if (plan.Provenance.ReviewInvocationId != invocation.Request.InvocationId || plan.Provenance.BaselineIdentity != target.Identity
            || plan.Assignments.Any(assignment => !procedures.Any(procedure => procedure.Binding == assignment.FocusedReview)))
        {
            throw new UnauthorizedAccessException("Stored review launch provenance no longer matches the verified invocation.");
        }

        if (restoreOnly)
        {
            var stored = await _coordinator.GetAsync(plan.DelegationId, cancellationToken);
            if (stored is not null && (stored.DelegationId != plan.DelegationId || stored.Provenance != plan.Provenance))
            {
                throw new UnauthorizedAccessException("Stored review checkpoint provenance does not match the accepted launch.");
            }

            return plan.Assignments.Select(
                assignment => stored?.ChildOutcomes.SingleOrDefault(
                outcome => outcome.AssignmentId == assignment.AssignmentId
                && outcome.ChildRunId == assignment.ChildRunId && outcome.Role == assignment.Role
                && stored.Assignments.Any(saved => saved.AssignmentId == assignment.AssignmentId
                    && saved.ChildRunId == assignment.ChildRunId && saved.Role == assignment.Role && saved.FocusedReview == assignment.FocusedReview)
                && outcome.Generation == plan.Provenance.Generation && outcome.FocusedReviewValidated && outcome.Status == AgentRunStatus.Completed)
                ?? new AgentRunOutcome
                {
                    AssignmentId = assignment.AssignmentId,
                    ChildRunId = assignment.ChildRunId,
                    Role = assignment.Role,
                    Generation = plan.Provenance.Generation,
                    Status = AgentRunStatus.Failed,
                    Usage = new AgentResourceUsage(),
                    Reason = "Interrupted review inference was not restarted; start a new review for this missing coverage.",
                }).ToArray();
        }

        var registration = _tools.GetRegistrations(invocation.Request.SessionId, invocation.Request.RunId)
            .SingleOrDefault(item => item.Tool.Definition.Id == DelegateAgentsContract.ToolId)
            ?? throw new UnauthorizedAccessException("Focused review requires the enabled delegate_agents tool.");
        var binding = new PreparedDelegationToolBinding(plan, authority.WorkspaceId, context =>
            _runners.CreateFocused(
                context with
                {
                    Invocation = context.Invocation with
                    {
                        AllowedToolIds = [FocusedReviewReadTool.ToolId],
                        AllowedNetworkHosts = [],
                        AllowedExecutables = [],
                    },
                },
                target,
                procedures));
        var result = await _pipeline.InvokeAsync(
            new ToolInvocationRequest
            {
                SessionId = invocation.Request.SessionId,
                RunId = invocation.Request.RunId,
                Phase = invocation.Request.Phase,
                ToolId = DelegateAgentsContract.ToolId,
                ArgumentsJson = JsonSerializer.Serialize(binding.Input),
                ExpectedRegistration = registration,
                HostBinding = binding,
                Context = authority with { RequestedBy = $"skill-host:{invocation.Request.InvocationId.Value:D}" },
            },
            cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Review delegation failed: {result.Error}");
        }

        var checkpoint = await _coordinator.GetAsync(plan.DelegationId, cancellationToken)
            ?? throw new InvalidDataException("Review delegation returned without a durable checkpoint.");
        return checkpoint.ChildOutcomes;
    }

    private async Task<ToolInvocationContext> ResolveAuthorityAsync(
        SkillInvocationRequest request,
        FocusedReviewTarget? target,
        CancellationToken cancellationToken)
    {
        var authority = await _authority(request, cancellationToken);
        if (authority.WorkspaceId != request.WorkspaceId || authority.TrustLevel < RepositoryTrustLevel.TrustedRead
            || (target?.InvokingRepository is not null && !Path.GetFullPath(authority.RepositoryPath).Equals(
                target.InvokingRepository,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
        {
            throw new UnauthorizedAccessException("Review workspace or trust authority changed.");
        }

        var registrations = request.ModelVisibleToolSnapshotId is { } snapshot
            ? _snapshots.Resolve(
                snapshot,
                request.SessionId,
                request.RunId)
            : ConversationToolAvailability.CreateSnapshot(_pipeline, _tools, request.SessionId, request.RunId, authority, false).Registrations;
        foreach (var toolId in new[] { "read_file", DelegateAgentsContract.ToolId })
        {
            if (!registrations.Any(registration => registration.Tool.Definition.Id == toolId
                && registration.Tool.Definition.SideEffect == ToolSideEffect.ReadOnly
                && registration.Tool.Definition.RequiredApproval == ApprovalLevel.None
                && ConversationToolAvailability.IsAdvertised(registration.Tool.Definition, authority)))
            {
                throw new UnauthorizedAccessException($"Focused review requires eligible {toolId} authority in the invoking request.");
            }
        }

        if (target is not null)
        {
            ReviewPathAccess.ValidateTarget(target, authority);
        }

        return authority;
    }
}
