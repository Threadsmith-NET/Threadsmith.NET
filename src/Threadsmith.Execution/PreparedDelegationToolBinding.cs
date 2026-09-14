namespace Threadsmith.Execution;

using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>One host-prepared plan admitted through the ordinary delegation tool.</summary>
internal sealed class PreparedDelegationToolBinding(
    DelegationPlan plan,
    WorkspaceId? workspaceId,
    Func<ToolExecutionContext, IAgentAssignmentRunner> createRunner) : IToolHostBinding
{
    /// <inheritdoc />
    public string ToolId => DelegateAgentsContract.ToolId;

    /// <inheritdoc />
    public ToolInvocationId ToolInvocationId => plan.Provenance.ToolInvocationId
        ?? throw new InvalidOperationException("Prepared delegation requires a reserved tool invocation identity.");

    /// <summary>Public tool arguments corresponding exactly to the admitted plan.</summary>
    internal DelegateAgentsInput Input => new()
    {
        Agents = plan.Assignments.Select(assignment => new DelegateAgentRequest
        {
            Role = assignment.Role,
            Task = assignment.Objective,
            Context = assignment.InitialContext,
            ToolAccess = DelegateAgentToolAccess.ReadOnly,
        }).ToArray(),
    };

    /// <summary>Revalidates this host admission at the normal tool execution boundary.</summary>
    internal (DelegationPlan Plan, IAgentAssignmentRunner Runner) Resolve(
        DelegateAgentsInput input,
        ToolExecutionContext context)
    {
        if (!ReferenceEquals(context.HostBinding, this)
            || context.ToolInvocationId != ToolInvocationId
            || context.SessionId != plan.Provenance.SessionId || context.RunId != plan.Provenance.ParentRunId
            || context.Invocation.WorkspaceId != workspaceId
            || !Path.GetFullPath(context.Invocation.RepositoryPath).Equals(
                Path.GetFullPath(plan.Provenance.RepositoryIdentity),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !input.Agents.SequenceEqual(Input.Agents))
        {
            throw new UnauthorizedAccessException("Prepared delegation no longer matches its invocation, workspace, or assignments.");
        }

        DelegationPlanValidator.Validate(plan);
        return (plan, createRunner(context));
    }
}
