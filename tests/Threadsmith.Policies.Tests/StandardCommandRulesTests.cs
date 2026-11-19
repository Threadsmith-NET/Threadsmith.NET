namespace Threadsmith.Policies.Tests;

using System.Collections.Immutable;
using Threadsmith.Policies.Commands;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Evaluation;
using Xunit;

public static class StandardCommandRulesTests
{
    [Theory]
    [InlineData("windows", "pwsh")]
    [InlineData("macos", "bash")]
    [InlineData("linux", "bash")]
    public static void ShippedDefaultsAllowGitSymbolicRefInspection(string platform, string shell)
    {
        // Arrange
        var snapshot = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, null, platform, shell);
        var facts = new CommandFacts(
            platform,
            shell,
            "git",
            Path.GetFullPath("git"),
            "external",
            ["symbolic-ref", "refs/remotes/origin/HEAD"],
            Path.GetFullPath("."));

        // Act
        var decision = TestPolicyEvaluation.Evaluate(snapshot, facts);

        // Assert
        Assert.True(decision.IsAllowed);
        Assert.Equal(PolicyOutcome.Allow, decision.Outcome);
        Assert.False(decision.RequiresApproval);
        Assert.Contains("git-standard", decision.RuleIds);
    }
}
