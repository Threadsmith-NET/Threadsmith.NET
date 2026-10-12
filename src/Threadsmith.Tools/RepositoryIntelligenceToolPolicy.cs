namespace Threadsmith.Tools;

using Threadsmith.Core;

/// <summary>Restricts intelligence work on the existing advertisement and invocation paths.</summary>
internal static class RepositoryIntelligenceToolPolicy
{
    /// <summary>Intersects host collection capability with ordinary tool permissions.</summary>
    public static bool IsAllowed(ToolDefinition definition, ToolInvocationContext context)
    {
        if (context.IntelligenceRun is null)
        {
            return true;
        }

        if (!definition.Source.Equals("Built-in", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (definition.Id == "memories")
        {
            return true;
        }

        return definition.SideEffect == ToolSideEffect.ReadOnly
            && definition.Category is ToolCategory.RepositoryInspection or ToolCategory.FileRead
                or ToolCategory.FileSearch or ToolCategory.SemanticSearch or ToolCategory.GitInspection;
    }
}
