namespace Threadsmith.Architecture.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Xunit;

/// <summary>Checks the production loader against built or explicitly published application assets.</summary>
public static class RepositoryToolPromptTests
{
    /// <summary>Both repository descriptions must belong to the complete deployable catalog.</summary>
    [Fact]
    public static async Task Repository_tool_prompts_load_from_deployed_catalog()
    {
        var directory = Environment.GetEnvironmentVariable("THREADSMITH_PROMPT_PUBLISH_DIR") ?? AppContext.BaseDirectory;
        var loader = await DeployedPromptLoader.LoadAsync(directory, TestContext.Current.CancellationToken);
        Assert.Equal(TestPromptLoader.Instance.Get(PromptFileNames.ToolRepositoryIntelligenceDescription), loader.Get(PromptFileNames.ToolRepositoryIntelligenceDescription));
        Assert.Equal(TestPromptLoader.Instance.Get(PromptFileNames.ToolRepositoryArcheologyDescription), loader.Get(PromptFileNames.ToolRepositoryArcheologyDescription));
    }
}
