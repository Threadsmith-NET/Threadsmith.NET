namespace Threadsmith.Execution;

using Threadsmith.Core;

/// <summary>Runs one role through the shared child execution services.</summary>
public interface IAgentRoleRunner : IAgentAssignmentRunner
{
    /// <summary>Role implemented by this runner.</summary>
    AgentRole Role { get; }
}

/// <summary>Resolves role runners while retaining one atomic parent evidence join.</summary>
public sealed class AgentRoleRunnerRegistry : IAgentAssignmentRunner, IAgentOutcomeJoiner
{
    private readonly IAgentOutcomeJoiner _joiner;
    private readonly IReadOnlyDictionary<AgentRole, IAgentRoleRunner> _runners;

    /// <summary>Initializes a new instance of the <see cref="AgentRoleRunnerRegistry"/> class.</summary>
    public AgentRoleRunnerRegistry(IEnumerable<IAgentRoleRunner> runners, IAgentOutcomeJoiner joiner)
    {
        ArgumentNullException.ThrowIfNull(runners);
        ArgumentNullException.ThrowIfNull(joiner);
        _runners = runners.ToDictionary(runner => runner.Role);
        _joiner = joiner;
        if (Enum.GetValues<AgentRole>().Any(role => !_runners.ContainsKey(role)))
        {
            throw new ArgumentException("Every defined subagent role requires a runner.", nameof(runners));
        }
    }

    /// <inheritdoc />
    public Task<AgentRunOutcome> RunAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return _runners.TryGetValue(assignment.Role, out var runner)
            ? runner.RunAsync(plan, assignment, cancellationToken)
            : throw new InvalidDataException("The requested subagent role has no runner.");
    }

    /// <inheritdoc />
    public Task<bool> JoinAsync(
        DelegationPlan plan,
        IReadOnlyList<AgentRunOutcome> outcomes,
        Func<bool> tryCommit,
        CancellationToken cancellationToken = default)
    {
        return _joiner.JoinAsync(plan, outcomes, tryCommit, cancellationToken);
    }
}

/// <summary>Applies a fixed role before entering the common model and tool loop.</summary>
internal sealed class ModelAgentRoleRunner : IAgentRoleRunner
{
    private readonly IAgentAssignmentRunner _execution;

    /// <summary>Initializes a new instance of the <see cref="ModelAgentRoleRunner"/> class.</summary>
    public ModelAgentRoleRunner(AgentRole role, IAgentAssignmentRunner execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        if (!Enum.IsDefined(role))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }

        Role = role;
        _execution = execution;
    }

    /// <inheritdoc />
    public AgentRole Role { get; }

    /// <inheritdoc />
    public Task<AgentRunOutcome> RunAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        if (assignment.Role != Role)
        {
            throw new InvalidDataException("The assignment does not match the selected role runner.");
        }

        return _execution.RunAsync(plan, assignment, cancellationToken);
    }
}
