namespace Threadsmith.Execution;

using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Resolves the ordinary model-visible tool surface under current host policy.</summary>
internal static class ConversationToolAvailability
{
    /// <summary>Returns one atomic enabled conversational snapshot visible to a model request.</summary>
    public static ConversationToolSnapshot CreateSnapshot(
        IToolInvocationPipeline? toolPipeline,
        IToolRegistry? toolRegistry,
        SessionId sessionId,
        RunId runId,
        ToolInvocationContext? invocationContext,
        bool toolsWithheld)
    {
        if (toolsWithheld || toolPipeline is null || toolRegistry is null)
        {
            return ConversationToolSnapshot.Empty;
        }

        ToolRegistration[] registrations =
        [
            .. toolRegistry.GetRegistrations(sessionId, runId)
                .Where(registration => (registration.Tool.Definition.SideEffect == ToolSideEffect.ReadOnly
                    || registration.Tool.Definition.ConversationAvailable)
                    && IsAdvertised(registration.Tool.Definition, invocationContext)),
        ];
        return new ConversationToolSnapshot(
            registrations.Select(registration => registration.Tool.Definition).ToArray(),
            registrations);
    }

    /// <summary>Returns whether one enabled definition remains visible after current request policy.</summary>
    public static bool IsAdvertised(ToolDefinition definition, ToolInvocationContext? context)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (context is null)
        {
            return true;
        }

        if (context.TrustLevel < definition.RequiredTrust
            || context.DenyAllTools
            || context.DeniedToolIds.Contains(definition.Id, StringComparer.OrdinalIgnoreCase)
            || (context.AllowedToolIds.Count > 0
                && !context.AllowedToolIds.Contains(definition.Id, StringComparer.OrdinalIgnoreCase))
            || context.RequireApprovalToolIds.Contains(definition.Id, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}

/// <summary>Atomic model-visible tool definitions and their exact registration identities.</summary>
internal sealed record ConversationToolSnapshot(
    IReadOnlyList<ToolDefinition> Definitions,
    IReadOnlyList<ToolRegistration> Registrations)
{
    /// <summary>Empty withheld tool snapshot.</summary>
    public static ConversationToolSnapshot Empty { get; } = new(
        [],
        []);
}
