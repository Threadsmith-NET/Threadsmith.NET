namespace Threadsmith.ModelTooling.Tests;

using Threadsmith.Policies.Contracts;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies Git completion evidence using bound commands and process results.</summary>
public static class RunProcessGitObservationTests
{
    /// <summary>Execution success alone is insufficient to confirm a commit.</summary>
    [Theory]
    [InlineData("commit", "-m", 0, "[main 9708c98] A useful subject", true)]
    [InlineData("commit", "--file", 0, "[main (root-commit) 9708c98] A useful subject", false)]
    [InlineData("status", "--short", 0, "[main 9708c98] A useful subject", null)]
    [InlineData("commit", "--dry-run", 0, "[main 9708c98] A useful subject", null)]
    [InlineData("commit", "-m", 1, "[main 9708c98] A useful subject", null)]
    [InlineData("commit", "-m", 0, "Success", null)]
    public static void OnlySuccessfulCommitWithNativeSummaryProducesEvidence(string subcommand, string option, int exitCode, string output, bool? expected)
    {
        const string repository = "fixture";
        var action = new CommandFacts("linux", "bash", "git", "/usr/bin/git", CommandPolicyNames.External, [subcommand, option, "A useful subject"], repository);
        var launch = new PreparedProcessLaunch("/usr/bin/bash", [], []);
        var input = new RunProcessInput { Command = "fixture", PreparedCommand = new("fixture", repository, "bash", "fixture", [action], launch) };
        var result = new ProcessExecutionResult(42, exitCode, output, string.Empty, false, false, false, TimeSpan.Zero);

        var observed = RunProcessTool.ObserveGitCommit(input, result);

        Assert.Equal(expected, observed);
    }
}
