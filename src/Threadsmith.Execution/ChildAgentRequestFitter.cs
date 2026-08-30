namespace Threadsmith.Execution;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>One fitted child request and the exact evidence identities rendered into it.</summary>
internal sealed record ChildAgentInitialRequest(
    List<ModelMessage> Messages,
    ModelWireEstimate WireEstimate,
    IReadOnlyList<EvidenceId> DeliveredEvidenceIds);

/// <summary>Validates the complete governed child request against the selected model.</summary>
internal static class ChildAgentRequestFitter
{
    /// <summary>Creates the complete request or fails when the selected model cannot carry it.</summary>
    public static ChildAgentInitialRequest Create(
        AgentContextSnapshot context,
        RepositoryInstructionBundle instructions,
        ModelWireToolEstimate toolEstimate,
        AgentModelSelection model,
        int desiredOutputTokens)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(desiredOutputTokens);
        var messages = ChildAgentPrompt.CreateMessages(context, instructions);
        var wireEstimate = ModelWireEstimator.Estimate(
            messages,
            toolEstimate,
            stablePrefixMessageCount: 0,
            outputReserveTokens: desiredOutputTokens);
        return wireEstimate.TotalCapacityTokens <= model.ContextWindowTokens
            ? new ChildAgentInitialRequest(
                messages,
                wireEstimate,
                context.Evidence.Select(item => item.EvidenceId).ToArray())
            : throw new InvalidOperationException(
                "The complete child context exceeds the selected model context window.");
    }
}
