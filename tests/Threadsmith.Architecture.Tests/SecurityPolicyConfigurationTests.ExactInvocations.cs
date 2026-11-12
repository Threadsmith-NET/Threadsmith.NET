namespace Threadsmith.Architecture.Tests;

using Microsoft.Extensions.Configuration;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    /// <summary>Verifies exact-call limits through the owning admission and configuration boundaries.</summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("true")]
    [InlineData("\"\"")]
    [InlineData("null")]
    [InlineData("2147483648")]
    public void ExactInvocationLimits_RejectInvalidValuesBeforeRegistration(string value)
    {
        File.WriteAllText(_paths.UserConfiguration, "{\"tools\":{\"runProcess\":{\"NumberOfExactToolInvocationsPerTurn\":" + value + "}}}");
        Assert.Throws<InvalidDataException>(Load);
        File.WriteAllText(_paths.UserConfiguration, "{\"tools\":{\"runtime\":{\"byTool\":[{\"toolId\":\"unloaded:tool\",\"NumberOfExactToolInvocationsPerTurn\":" + value + "}]}}}");
        Assert.Throws<InvalidDataException>(Load);
    }

    /// <summary>Verifies exact-call limits through the owning admission and configuration boundaries.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void ExactInvocationLimits_PerToolWinsAndMachineConstraintsRemain(int limit)
    {
        File.WriteAllText(_paths.UserConfiguration, "{\"tools\":{\"runProcess\":{\"NumberOfExactToolInvocationsPerTurn\":1},\"runtime\":{\"byTool\":[{\"toolId\":\"run_process\",\"NumberOfExactToolInvocationsPerTurn\":" + limit + "}]}}}");
        const string key = "tools:runtime:byTool:run_process:NumberOfExactToolInvocationsPerTurn";
        Assert.Equal(limit, Load().ResourceLimits[key]);
        File.WriteAllText(_paths.MachineConfiguration, """{"enforced":{"tools":{"runProcess":{"NumberOfExactToolInvocationsPerTurn":0}}}}""");
        Assert.Equal(0, Load().ResourceLimits[key]);
    }

    /// <summary>Verifies exact-call state and validation invariants.</summary>
    [Fact]
    public void ExactInvocationLimits_RepositoryCannotRaiseTrustedLimit()
    {
        File.WriteAllText(_paths.RepositoryConfiguration, """{"tools":{"runProcess":{"NumberOfExactToolInvocationsPerTurn":9},"runtime":{"byTool":[{"toolId":"run_process","NumberOfExactToolInvocationsPerTurn":9}]}}}""");
        Assert.Equal(3, Load().ResourceLimits["tools:runtime:byTool:run_process:NumberOfExactToolInvocationsPerTurn"]);
        Assert.True(ConfigurationPermissionCatalog.Properties.Single(property => property.Path.Equals("tools:runProcess:NumberOfExactToolInvocationsPerTurn", StringComparison.OrdinalIgnoreCase)).StartupBound);
    }

    /// <summary>Machine ceilings cannot create repeat authority or erase ordinary per-tool preferences during binding.</summary>
    [Theory]
    [InlineData(null, "list_files", 5, 0)]
    [InlineData(2, "other:tool", 5, 2)]
    [InlineData(2, "list_files", 5, 2)]
    [InlineData(2, "list_files", 1, 1)]
    [InlineData(2, "list_files", 0, 0)]
    public void ExactInvocationLimits_BindingKeepsPreferencesSeparateFromCeilings(int? preference, string enforcedTool, int ceiling, int expected)
    {
        if (preference is { } value)
        {
            File.WriteAllText(_paths.UserConfiguration, "{\"tools\":{\"runtime\":{\"byTool\":[{\"toolId\":\"list_files\",\"NumberOfExactToolInvocationsPerTurn\":" + value + "}]}}}");
        }

        File.WriteAllText(_paths.MachineConfiguration, "{\"enforced\":{\"tools\":{\"runtime\":{\"byTool\":[{\"toolId\":\"" + enforcedTool + "\",\"NumberOfExactToolInvocationsPerTurn\":" + ceiling + "}]}}}}");
        var policy = Load();
        var ordinary = SecurityPolicyConfigurationLoader.BuildTrusted(_paths, includeEnforced: false);
        using var ordinaryLifetime = ordinary as IDisposable;
        var runtime = ordinary.GetSection("tools:runtime").Get<ToolRuntimeOptions>(options => options.ErrorOnUnknownConfiguration = true);
        var registry = new ToolRegistry([new ListFilesTool(TestPromptLoader.Instance)], runtimeOptions: runtime);
        var registration = registry.GetRegistration("list_files");
        var tool = ToolRuntimePolicy.ApplyCeiling(registration.Tool, policy);
        Assert.Equal(expected, tool.Definition.NumberOfExactToolInvocationsPerTurn);
        Assert.Same(registration.Tool, registry.GetRegistration("list_files").Tool);
        var history = new ToolCallHistory();
        for (var index = 0; index < Math.Max(1, expected); index++)
        {
            Assert.True(history.TryAdd(tool.Definition, "{}"));
        }

        Assert.False(history.TryAdd(tool.Definition, "{}", out var rejection));
        Assert.NotNull(rejection);
        Assert.Equal(Math.Max(1, expected), rejection.InvocationCount);
    }
}
