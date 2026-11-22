namespace Threadsmith.Execution;

using Threadsmith.Context;
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>Canonical tool views for one availability snapshot and receiving-model prompt scope.</summary>
internal sealed class RoundToolInventory
{
    private readonly Dictionary<string, ToolDefinition> _definitions;
    private readonly IReadOnlyList<ModelToolDefinition> _all;
    private readonly IReadOnlyList<ModelToolDefinition> _withoutRecovery;

    /// <summary>Initializes a new instance of the <see cref="RoundToolInventory"/> class for the current prompt scope.</summary>
    internal RoundToolInventory(IReadOnlyList<ToolDefinition> definitions, bool workspaceAvailable)
    {
        _definitions = definitions.Where(tool => workspaceAvailable || !tool.RequiresWorkspace)
            .ToDictionary(tool => tool.Id, StringComparer.Ordinal);
        _all = ModelToolCanonicalizer.Canonicalize(_definitions.Values.Select(tool => new ModelToolDefinition
        {
            Name = tool.Id,
            Description = tool.Description,
            ArgumentsJsonSchema = tool.InputSchema.JsonSchema,
            PreferStrictArguments = tool.PreferStrictArguments,
        }));
        RecoveryTool = _all.FirstOrDefault(tool => tool.Name == ActiveTurnEvidenceTool.ToolId);
        _withoutRecovery = RecoveryTool is null ? _all : [.. _all.Where(tool => tool != RecoveryTool)];
    }

    /// <summary>Canonical recovery schema, if offered by the workspace-filtered snapshot.</summary>
    internal ModelToolDefinition? RecoveryTool { get; }

    /// <summary>Original definition supplying the recovery version for source projections.</summary>
    internal ToolDefinition? RecoveryDefinition => _definitions.GetValueOrDefault(ActiveTurnEvidenceTool.ToolId);

    /// <summary>Selects the existing ordered view for the current recovery decision.</summary>
    internal IReadOnlyList<ModelToolDefinition> Select(bool includeRecovery) => includeRecovery ? _all : _withoutRecovery;

    /// <summary>Projects a canonical schema while retaining its receiving-model description resolver.</summary>
    internal ContextToolSchema CreateContextSchema(ModelToolDefinition tool) =>
        new(tool.Name, tool.Description, tool.ArgumentsJsonSchema, tool.PreferStrictArguments)
        {
            DescriptionResolver = _definitions[tool.Name].DescriptionResolver,
        };
}
