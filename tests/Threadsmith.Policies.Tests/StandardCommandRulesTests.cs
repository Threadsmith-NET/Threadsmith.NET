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
        var decision = PolicyEvaluator.Evaluate(snapshot, facts);

        // Assert
        Assert.True(decision.IsAllowed);
        Assert.Equal(PolicyOutcome.Allow, decision.Outcome);
        Assert.False(decision.RequiresApproval);
        Assert.Contains("git-standard", decision.RuleIds);
    }

    [Theory]
    [InlineData("clone")]
    [InlineData("fetch")]
    [InlineData("pull")]
    [InlineData("push")]
    [InlineData("ls-remote")]
    [InlineData("submodule")]
    [InlineData("remote")]
    public static void RemoteTransportsDenyExplicitExecutionButAllowHttpsAndSsh(string command)
    {
        var snapshot = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, null, "linux", "bash");
        foreach (var remote in new[] { "ext::fixture\nargument", "https://example.invalid/repo", "ssh://example.invalid/repo", "git@example.invalid:repo" })
        {
            var allowed = !remote.StartsWith("ext::", StringComparison.Ordinal);
            ImmutableArray<string> arguments = command switch
            {
                "submodule" => [command, "add", remote],
                "remote" => [command, "add", "fixture", remote],
                _ => [command, remote],
            };
            var facts = new CommandFacts("linux", "bash", "git", Path.GetFullPath("git"), "external", arguments, Path.GetFullPath("."));
            var decision = PolicyEvaluator.Evaluate(snapshot, facts);
            Assert.Equal(allowed ? PolicyOutcome.Allow : PolicyOutcome.AlwaysDeny, decision.Outcome);
        }
    }

    [Theory]
    [InlineData("push", "--repo")]
    [InlineData("push", "--rep")]
    [InlineData("archive", "--remote")]
    [InlineData("archive", "--rem")]
    public static void RemoteTransportOptionValuesDenyExplicitExecution(string command, string option)
    {
        var snapshot = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, null, "linux", "bash");
        foreach (var attached in new[] { false, true })
        {
            ImmutableArray<string> arguments = attached ? [command, option + "=ext::fixture"] : [command, option, "ext::fixture"];
            var facts = new CommandFacts("linux", "bash", "git", Path.GetFullPath("git"), "external", arguments, Path.GetFullPath("."));
            Assert.Equal(PolicyOutcome.AlwaysDeny, PolicyEvaluator.Evaluate(snapshot, facts).Outcome);
        }
    }

    [Theory]
    [InlineData("core.sshCommand", false)]
    [InlineData("alias.fixture", false)]
    [InlineData("protocol.ext.allow", false)]
    [InlineData("core.hooksPath", false)]
    [InlineData("core.fsmonitor", false)]
    [InlineData("CoRe.HoOkSpAtH", false)]
    [InlineData("credential.helper", false)]
    [InlineData("credential.https://example.invalid.helper", false)]
    [InlineData("credential.username", true)]
    [InlineData("credential.useHttpPath", true)]
    [InlineData("credential.https://example.invalid.username", true)]
    [InlineData("credential.https://example.invalid.useHttpPath", true)]
    [InlineData("credential.helperSuffix", true)]
    [InlineData("core.autocrlf", true)]
    [InlineData("remote.origin.uploadpack", false)]
    [InlineData("remote.origin.receivepack", false)]
    [InlineData("ReMoTe.OrIgIn.UpLoAdPaCk", false)]
    [InlineData("ReMoTe.OrIgIn.ReCeIvEpAcK", false)]
    [InlineData("remote.team.origin.uploadpack", false)]
    [InlineData("remote.team.origin.receivepack", false)]
    [InlineData("remote.origin.url", true)]
    [InlineData("remote.team.origin.fetch", true)]
    [InlineData("remote.origin.uploadpackSuffix", true)]
    [InlineData("remote.origin.receivepackSuffix", true)]
    public static void ConfigurationSelectorsUseTheSameExceptionForEverySupportedForm(string key, bool allowed)
    {
        // Arrange: these are literal policy facts, not commands executed by the fixture.
        var setting = key + "=fixture\n#";
        ImmutableArray<string>[] forms =
        [
            ["-c", setting, "fetch"],
            ["-c" + setting, "fetch"],
            ["--config-env", key + "=FIXTURE_VALUE", "fetch"],
            ["--config-env=" + key + "=FIXTURE_VALUE", "fetch"],
            ["clone", "-c", setting, "https://example.invalid/repo"],
            ["clone", "-c" + setting, "https://example.invalid/repo"],
            ["clone", "--config", setting, "https://example.invalid/repo"],
            ["clone", "--config=" + setting, "https://example.invalid/repo"],
            ["clone", "--conf", setting, "https://example.invalid/repo"],
            ["clone", "--conf=" + setting, "https://example.invalid/repo"],
        ];
        var snapshot = PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, null, "linux", "bash");
        foreach (var arguments in forms)
        {
            var facts = new CommandFacts("linux", "bash", "git", Path.GetFullPath("git"), "external", arguments, Path.GetFullPath("."));

            // Act through the shared evaluator's text searches.
            var decision = PolicyEvaluator.Evaluate(snapshot, facts);

            // Assert: denial comes from the Git rule's text restrictions.
            Assert.Equal(allowed, decision.IsAllowed);
            Assert.Equal(allowed ? PolicyOutcome.Allow : PolicyOutcome.AlwaysDeny, decision.Outcome);
            Assert.False(decision.RequiresApproval);
            Assert.Contains("git-standard", decision.RuleIds);
        }
    }
}
