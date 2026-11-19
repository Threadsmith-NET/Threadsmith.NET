namespace Threadsmith.ParallelAgents.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

/// <summary>Checks the public role names and compatibility of existing delegation input.</summary>
public sealed class DelegateAgentRoleContractTests
{
    /// <summary>Older tool callers continue to create Explorer assignments.</summary>
    [Fact]
    public void Deserialize_MissingRole_DefaultsToExplorer()
    {
        var input = JsonSerializer.Deserialize<DelegateAgentsInput>(
            """{"agents":[{"task":"Inspect source.","context":"src/A.cs","toolAccess":"inherit"}]}""");

        Assert.NotNull(input);
        Assert.Equal(AgentRole.Explorer, Assert.Single(input.Agents).Role);
    }

    /// <summary>Numeric values, unknown roles, null, and alias spellings cannot become execution roles.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("\"SecurityReviewer\"")]
    [InlineData("\"admin\"")]
    [InlineData("\"1\"")]
    public void Deserialize_UnknownRole_RejectsBeforeExecution(string roleJson)
    {
        var input = "{\"agents\":[{\"role\":" + roleJson
            + ",\"task\":\"Inspect source.\",\"context\":\"src/A.cs\",\"toolAccess\":\"readOnly\"}]}";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DelegateAgentsInput>(input));
    }
}
