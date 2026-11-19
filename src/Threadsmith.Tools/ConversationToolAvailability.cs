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
        IReadOnlyList<ToolRegistration>? callerRegistrations = null,
        IPromptLoader? prompts = null)
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
            ProjectDefinitions(registrations, toolPipeline, invocationContext, prompts),
            registrations);
    }

    /// <summary>Projects descriptions without modifying the registrations used for dispatch.</summary>
    public static ToolDefinition[] ProjectDefinitions(
        IReadOnlyList<ToolRegistration> registrations,
        IToolInvocationPipeline? pipeline,
        ToolInvocationContext? context,
        IPromptLoader? prompts)
    {
        var definitions = registrations.Select(item => item.Tool.Definition).ToArray();
        if (pipeline is not ToolInvocationPipeline scriptPipeline || context is null || prompts is null
            || !registrations.Any(item => item.Implementation is CSharpScriptTool && IsAdvertised(item.Tool.Definition, context)))
        {
            return definitions;
        }

        for (var index = 0; index < registrations.Count; index++)
        {
            if (scriptPipeline.IsScriptCallable(registrations[index], context))
            {
                var definition = definitions[index];
                definitions[index] = definition with { DescriptionResolver = () => definition.Description + " " + prompts.Get(PromptFileNames.ToolScriptCallableMarker) };
            }
        }

        return definitions;
    }

    /// <summary>Returns whether one enabled definition remains visible after current request policy.</summary>
    public static bool IsAdvertised(ToolDefinition definition, ToolInvocationContext? context)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (string.Equals(definition.Id, "ask_user", StringComparison.OrdinalIgnoreCase) && (context?.IsChildAgent != false || context.UserQuestions?.IsAvailable != true))
        {
            return false;
        }

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

        if (context.SecurityPolicy is { } policy && !policy.AllowsTool(definition.Id))
        {
            return false;
        }

        var scratchpadEligible = context.Scratchpad.IsActive
            && definition.Id is "list_files" or "search" or "read_file" or "write_file"
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
