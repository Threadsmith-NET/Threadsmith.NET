namespace Threadsmith.Execution;

using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Latest admitted request capacity and effective profile; independent of token accounting.</summary>
public sealed record AgentRequestStatus(ModelProfileId? ProfileId, ReasoningLevel Reasoning, long? ContextTokens, long? ContextLimit, long Timestamp);
