namespace Threadsmith.Tools;

using Threadsmith.Core;

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
        bool toolsWithheld,
        IReadOnlyList<ToolRegistration>? callerRegistrations = null)
    {
        if (toolsWithheld || toolPipeline is null || toolRegistry is null)
        {
            return ConversationToolSnapshot.Empty;
        }

        ToolRegistration[] registrations =
        [
            .. (callerRegistrations ?? toolRegistry.GetRegistrations(sessionId, runId, invocationContext))
                .Where(registration => IsAdvertised(registration.Tool.Definition, invocationContext))
                .Where(registration => invocationContext is null
                    || registration.Implementation is not IRepositoryFeatureTool feature
                    || feature.CaptureAvailability(invocationContext.RepositoryPath).Ready),
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

        if (context.SecurityPolicy is { } captured)
        {
            context.PolicyBinding?.Validate(captured);
        }

        if (!RepositoryIntelligenceToolPolicy.IsAllowed(definition, context))
        {
            return false;
        }

        if (context.SecurityPolicy is { } policy
            && (policy.DeniedTools.Contains(definition.Id, StringComparer.OrdinalIgnoreCase)
                || (!policy.AllowedTools.IsDefault && !policy.AllowedTools.Contains(definition.Id, StringComparer.OrdinalIgnoreCase))))
        {
            return false;
        }

        var scratchpadEligible = context.Scratchpad.IsActive
            && definition.Id is "search" or "read_file" or "write_file"
            && definition.Source.Equals("Built-in", StringComparison.OrdinalIgnoreCase);
        if ((!scratchpadEligible && (context.TrustLevel < definition.RequiredTrust
                || (context.SecurityPolicy is { } trustPolicy && trustPolicy.MaximumTrust < definition.RequiredTrust)))
            || context.DenyAllTools
            || context.DeniedToolIds.Contains(definition.Id, StringComparer.OrdinalIgnoreCase)
            || (context.AllowedToolIds.Count > 0
                && !context.AllowedToolIds.Contains(definition.Id, StringComparer.OrdinalIgnoreCase)))
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
