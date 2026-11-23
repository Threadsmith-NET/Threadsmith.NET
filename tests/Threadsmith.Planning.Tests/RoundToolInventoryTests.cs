namespace Threadsmith.Planning.Tests;

using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Tools;
using Xunit;

/// <summary>Checks round-local inventory reuse against the existing canonical tool contract.</summary>
public static class RoundToolInventoryTests
{
    /// <summary>Changing visibility selects a stable view without altering canonical tool data.</summary>
    [Fact]
    public static void SelectPreservesCanonicalToolsAcrossVisibilityChanges()
    {
        ToolDefinition[] definitions = [Tool("z"), Tool(ActiveTurnEvidenceTool.ToolId), Tool("find_symbol")];
        var inventory = new RoundToolInventory(definitions, workspaceAvailable: true);
        var expected = ModelToolCanonicalizer.Canonicalize(definitions.Select(tool => new ModelToolDefinition
        {
            Name = tool.Id,
            Description = tool.Description,
            ArgumentsJsonSchema = tool.InputSchema.JsonSchema,
            PreferStrictArguments = tool.PreferStrictArguments,
        }));

        var visible = inventory.Select(true);
        var hidden = inventory.Select(false);
        Assert.Equal(expected.ToArray(), visible.ToArray());
        Assert.Equal(expected.Where(tool => tool.Name != ActiveTurnEvidenceTool.ToolId), hidden);
        Assert.Same(visible, inventory.Select(true));
        Assert.Same(hidden, inventory.Select(false));
    }

    /// <summary>Recovery respects workspace eligibility and retains fallback-model prose.</summary>
    [Fact]
    public static void ContextSchemasPreserveRecoveryEligibilityAndDescriptionResolver()
    {
        var description = "preferred";
        var recovery = Tool(ActiveTurnEvidenceTool.ToolId) with { RequiresWorkspace = true, DescriptionResolver = () => description };
        var inventory = new RoundToolInventory([recovery], workspaceAvailable: true);
        var schema = inventory.CreateContextSchema(inventory.RecoveryTool!);
        description = "fallback";

        Assert.Equal("preferred", schema.Description);
        Assert.Equal("fallback", schema.DescriptionResolver!());
        Assert.Equal(inventory.RecoveryTool!.ArgumentsJsonSchema, schema.JsonSchema);
        Assert.True(schema.PreferStrictArguments);
        var unavailable = new RoundToolInventory([recovery], workspaceAvailable: false);
        Assert.Null(unavailable.RecoveryTool);
        Assert.Empty(unavailable.Select(true));
    }

    private static ToolDefinition Tool(string id) => new()
    {
        Id = id,
        Version = "1",
        Description = id,
        InputSchema = new("input", 1, "{ \"type\": \"object\", \"properties\": {} }"),
        OutputSchema = new("output", 1, "{}"),
        PreferStrictArguments = true,
    };
}
