namespace Threadsmith.Models;

using Threadsmith.Core;

/// <summary>Latest admitted request capacity and effective profile; independent of token accounting.</summary>
public sealed record AgentRequestStatus(ModelProfileId? ProfileId, ReasoningLevel Reasoning, long? ContextTokens, long? ContextLimit, long Timestamp)
{
    /// <summary>Size-only final request detail, owned by this same status observation.</summary>
    public ContextUsageSnapshot? ContextUsage { get; init; }
}
