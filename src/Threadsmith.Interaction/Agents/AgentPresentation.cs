namespace Threadsmith.Interaction.Agents;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Models;

/// <summary>Exact accepted child identity. A null target on an output batch means MAIN.</summary>
public sealed record AgentPresentationTarget(SessionId SessionId, DelegationId DelegationId, AgentAssignmentId AssignmentId, RunId RunId, int Generation);

/// <summary>Immutable child membership and effective request status.</summary>
public sealed record AgentPresentationSnapshot(
    AgentPresentationTarget Target,
    string Name,
    AgentRole Role,
    AgentRunStatus State,
    long Revision,
    string? Model = null,
    ReasoningLevel Reasoning = default,
    long? ContextTokens = null,
    long? ContextLimit = null,
    SessionUsageSnapshot? Usage = null)
{
    /// <summary>Gets the provider display name for this child's effective request.</summary>
    public string? ProviderName { get; init; }

    /// <summary>Gets current child-local tool activity.</summary>
    public string? Activity { get; init; }

    /// <summary>Gets the independently timed live tool blocks for this child.</summary>
    public IReadOnlyList<InteractionActivity> ToolActivities { get; init; } = [];

    /// <summary>Gets the human-readable tab label.</summary>
    public string Label => Name + " · " + RoleLabel(Role);

    /// <summary>Gets whether this assignment has ended, including discarded attempts.</summary>
    public bool IsTerminal => State is AgentRunStatus.Completed or AgentRunStatus.Failed or AgentRunStatus.Cancelled or AgentRunStatus.Discarded;

    /// <summary>Formats each public role for display.</summary>
    public static string RoleLabel(AgentRole role) => role switch
    {
        AgentRole.SecurityReviewer => "Security Reviewer",
        AgentRole.TestReviewer => "Test Reviewer",
        AgentRole.PerformanceReviewer => "Performance Reviewer",
        AgentRole.ArchitectureReviewer => "Architecture Reviewer",
        _ => role.ToString(),
    };
}

/// <summary>Optional retained-agent capability; other frontends retain their ordinary presentation.</summary>
public interface IAgentWorkspaceSurface
{
    /// <summary>Replaces session ownership and clears retired-session child views.</summary>
    Task AttachAgentSessionAsync(SessionId sessionId, CancellationToken cancellationToken = default);

    /// <summary>Updates or retires an accepted child. Output never creates membership.</summary>
    Task PresentAgentAsync(AgentPresentationSnapshot snapshot, CancellationToken cancellationToken = default);
}
