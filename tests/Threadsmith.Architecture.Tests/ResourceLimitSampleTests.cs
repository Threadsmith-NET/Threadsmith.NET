namespace Threadsmith.Architecture.Tests;

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Xunit;

/// <summary>Ensures the published defaults bind without silently ignored or misstated fields.</summary>
public sealed class ResourceLimitSampleTests
{
    /// <summary>Every documented typed section accepts its sample and retains exact compiled defaults.</summary>
    [Theory]
    [InlineData("limits", "Threadsmith.Core.OperationalLimits", "Threadsmith.Core")]
    [InlineData("tui:limits", "Threadsmith.Interaction.Contracts.TuiResourceLimits", "Threadsmith.Interaction")]
    [InlineData("mcp:limits", "Threadsmith.Mcp.McpResourceLimits", "Threadsmith.Mcp")]
    [InlineData("hooks:limits", "Threadsmith.Hooks.HookResourceLimits", "Threadsmith.Hooks")]
    [InlineData("secretResolution:limits", "Threadsmith.Tools.SecretResourceLimits", "Threadsmith.Tools")]
    [InlineData("tools:runtime", "Threadsmith.Tools.ToolRuntimeOptions", "Threadsmith.Tools")]
    [InlineData("model:catalogLimits", "Threadsmith.Models.ModelProviderCatalogLimits", "Threadsmith.Models")]
    [InlineData("skills:catalogLimits", "Threadsmith.Skills.SkillCatalogOptions", "Threadsmith.Skills")]
    [InlineData("skills:schemaLimits", "Threadsmith.Skills.SkillSchemaOptions", "Threadsmith.Skills")]
    [InlineData("skills:claudeLimits", "Threadsmith.Skills.ClaudeSkillCompatibilityOptions", "Threadsmith.Skills")]
    [InlineData("skills:installerLimits", "Threadsmith.Skills.SkillInstallerOptions", "Threadsmith.Skills")]
    [InlineData("skills:runtimeLimits", "Threadsmith.Skills.SkillRuntimeLimits", "Threadsmith.Skills")]
    [InlineData("context:instructions:limits", "Threadsmith.Context.RepositoryInstructionLimits", "Threadsmith.Context")]
    [InlineData("context:promptAppends:limits", "Threadsmith.Context.PromptAppendLimits", "Threadsmith.Context")]
    [InlineData("context:deployedPrompts:limits", "Threadsmith.Context.DeployedPromptLoadLimits", "Threadsmith.Context")]
    [InlineData("semanticRefresh:limits", "Threadsmith.DotNet.SemanticRefreshResourceLimits", "Threadsmith.DotNet")]
    [InlineData("tools:config:memories", "Threadsmith.Core.RepositoryMemoryOptions", "Threadsmith.Core")]
    public void SampleMatchesTypedDefaults(string section, string typeName, string assemblyName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".threadsmith", "resource-limits.example")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(directory.FullName, ".threadsmith", "resource-limits.example"))
            .Build();
        var type = Assembly.Load(assemblyName).GetType(typeName, throwOnError: true)!;
        var configured = configuration.GetSection(section).Get(type, options => options.ErrorOnUnknownConfiguration = true);
        Assert.NotNull(configured);
        var constructor = Assert.Single(type.GetConstructors(), candidate => candidate.GetParameters().All(parameter => parameter.HasDefaultValue));
        var defaults = constructor.Invoke([.. constructor.GetParameters().Select(parameter => parameter.DefaultValue)]);
        foreach (var property in type.GetProperties().Where(property => property.PropertyType.IsPrimitive))
        {
            Assert.Equal(property.GetValue(defaults), property.GetValue(configured));
        }

        type.GetMethod("Validate", Type.EmptyTypes)?.Invoke(configured, null);
    }
}
