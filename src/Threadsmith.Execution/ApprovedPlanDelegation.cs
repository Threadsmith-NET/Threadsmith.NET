namespace Threadsmith.Execution;

using System.Security.Cryptography;
using System.Text;
using Threadsmith.Core;

/// <summary>Runs host-created read-only plan-scope assignments before serial approved-plan mutation.</summary>
public sealed class ApprovedPlanDelegatingOrchestrator : IExecutionOrchestrator
{
    private readonly IDelegationCoordinator _delegations;
    private readonly IExecutionOrchestrator _inner;
    private readonly IAgentAssignmentRunner _runner;

    /// <summary>Initializes a new instance of the <see cref="ApprovedPlanDelegatingOrchestrator"/> class.</summary>
    public ApprovedPlanDelegatingOrchestrator(
        IExecutionOrchestrator inner,
        IDelegationCoordinator delegations,
        IAgentAssignmentRunner runner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(delegations);
        ArgumentNullException.ThrowIfNull(runner);
        _inner = inner;
        _delegations = delegations;
        _runner = runner;
    }

    /// <inheritdoc />
    public async Task<ExecutionContinuation> StartAsync(
        ExecutionStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = ApprovedPlanDelegationFactory.Create(request);
        if (plan is not null)
        {
            _ = await _delegations.StartAsync(plan, _runner, cancellationToken);
        }

        return await _inner.StartAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExecutionOutcomeProjection> ContinueAsync(
        ContinueExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        return _inner.ContinueAsync(request, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExecutionContinuation> ResumeAsync(
        SessionId sessionId,
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        return _inner.ResumeAsync(sessionId, runId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExecutionOutcomeProjection> WaitForOutcomeAsync(
        RunId runId,
        CancellationToken cancellationToken = default)
    {
        return _inner.WaitForOutcomeAsync(runId, cancellationToken);
    }
}

/// <summary>Executes the host-owned, transcript-free scope preflight for an approved plan step.</summary>
public sealed class ApprovedPlanAssignmentRunner : IAgentAssignmentRunner
{
    /// <inheritdoc />
    public Task<AgentRunOutcome> RunAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(assignment);
        cancellationToken.ThrowIfCancellationRequested();
        var frozen = plan.Assignments.SingleOrDefault(
            item => item.AssignmentId == assignment.AssignmentId);
        if (frozen is null || frozen.ChildRunId != assignment.ChildRunId)
        {
            throw new UnauthorizedAccessException("The assignment does not belong to this delegation.");
        }

        return Task.FromResult(new AgentRunOutcome
        {
            AssignmentId = frozen.AssignmentId,
            ChildRunId = frozen.ChildRunId,
            Role = frozen.Role,
            Generation = plan.Provenance.Generation,
            Status = AgentRunStatus.Completed,
            Usage = new AgentResourceUsage { Files = frozen.Scope.Files.Count },
            Reason = "host plan-scope preflight completed",
            Findings = new AgentFindingSet
            {
                AssignmentId = frozen.AssignmentId,
                ChildRunId = frozen.ChildRunId,
                Generation = plan.Provenance.Generation,
                CoverageNotes = ["Approved step paths were partitioned for bounded parallel preflight."],
            },
        });
    }
}

/// <summary>Creates bounded read-only assignments from disjoint approved-plan steps.</summary>
internal static class ApprovedPlanDelegationFactory
{
    private const int MaximumAssignments = 16;

    /// <summary>Creates a delegation when at least two step path scopes are disjoint.</summary>
    internal static DelegationPlan? Create(ExecutionStartRequest request)
    {
        ImplementationPlanStep[] steps =
        [
            .. request.ApprovedPlan.Steps
                .Where(step => step.GetAffectedPaths().Count > 0)
                .Take(MaximumAssignments),
        ];
        if (steps.Length < 2 || HasOverlap(steps))
        {
            return null;
        }

        AgentAssignment[] assignments = [.. steps.Select(CreateAssignment)];
        var childBudget = new AgentResourceBudget
        {
            ModelTokens = 0,
            ToolCalls = 0,
            EvidenceItems = 0,
            Files = assignments.Sum(item => item.Budget.Files),
            Bytes = 0,
            Mutations = 0,
            Processes = 0,
            Builds = 0,
            Tests = 0,
            Corrections = 0,
            WallTime = TimeSpan.FromTicks(assignments.Sum(item => item.Budget.WallTime.Ticks)),
        };
        return new DelegationPlan
        {
            DelegationId = DelegationId.New(),
            Provenance = new DelegationProvenance
            {
                SessionId = request.SessionId,
                ParentRunId = request.RunId,
                RepositoryIdentity = request.Baseline.RepositoryPath,
                BaselineIdentity = CreateBaselineIdentity(request.Baseline),
                WorkspaceId = request.Baseline.WorkspaceId,
                ApprovedPlanIdentity = request.RunId.Value.ToString("D"),
                ApprovedPlanRevision = request.ApprovedPlan.Revision,
            },
            Assignments = assignments,
            ParentBudget = childBudget,
            AcceptedAt = DateTimeOffset.UtcNow,
        };
    }

    private static AgentAssignment CreateAssignment(ImplementationPlanStep step)
    {
        return new AgentAssignment
        {
            AssignmentId = AgentAssignmentId.New(),
            ChildRunId = RunId.New(),
            Role = AgentRole.Explorer,
            Mode = AgentRunMode.ReadOnlyBaseline,
            Objective = $"Preflight approved plan step: {step.Title}",
            Tasks = [step.Description],
            OutputSchema = "agent-findings/1",
            StoppingCondition = "Stop after the frozen path scope has been checked.",
            Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
            Scope = new AgentAssignmentScope
            {
                Files = step.GetAffectedPaths(),
                IsOwnershipProven = true,
            },
            Policy = new AgentPolicySnapshot
            {
                ModelSelectionRationale = "Host-only approved-plan scope preflight.",
                ContextPolicyVersion = "agent-context/1",
                ToolPolicyVersion = "agent-tools/1",
            },
            Budget = new AgentResourceBudget
            {
                ModelTokens = 0,
                ToolCalls = 0,
                EvidenceItems = 0,
                Files = step.GetAffectedPaths().Count,
                Bytes = 0,
                Mutations = 0,
                Processes = 0,
                Builds = 0,
                Tests = 0,
                Corrections = 0,
                WallTime = TimeSpan.FromMinutes(1),
            },
        };
    }

    private static bool HasOverlap(IReadOnlyList<ImplementationPlanStep> steps)
    {
        return steps.SelectMany(step => step.GetAffectedPaths())
            .GroupBy(path => path.Replace('\\', '/').Trim('/'), StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1);
    }

    private static string CreateBaselineIdentity(WorkspaceBaseline baseline)
    {
        string content = string.Join('\n', baseline.Files.Select(file => $"{file.RelativePath}:{file.Sha256}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
    }
}
