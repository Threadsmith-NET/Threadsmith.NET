namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Policies.Contracts;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public static partial class ShellBuiltinPolicyTests
{
    /// <summary>Setup cannot introduce unreviewed interpreter startup code or options.</summary>
    [Theory]
    [InlineData("bash", "BASH_ENV='/repo/unapproved.sh' bash /repo/approved.sh")]
    [InlineData("bash", "SAFE=value BASH_ENV='/repo/unapproved.sh' bash /repo/approved.sh")]
    [InlineData("bash", "BASH_ENV='/repo/unapproved.sh'")]
    [InlineData("bash", "BASH_ENV='/repo/unapproved.sh'; bash /repo/approved.sh")]
    [InlineData("sh", "ENV='/repo/unapproved.sh' sh /repo/approved.sh")]
    [InlineData("zsh", "ZDOTDIR='/repo/unapproved' zsh /repo/approved.sh")]
    [InlineData("bash", "SHELLOPTS='xtrace' bash /repo/approved.sh")]
    [InlineData("bash", "BASHOPTS='extdebug' bash /repo/approved.sh")]
    [InlineData("pwsh", "$env:BASH_ENV='/repo/unapproved.sh'; bash /repo/approved.sh")]
    [InlineData("powershell", "${EnV:bash_env} = '/repo/unapproved.sh'; bash /repo/approved.sh")]
    [InlineData("pwsh", "$env:BASH_ENV=$startup; ./approved.sh")]
    [InlineData("pwsh", "$env:ENV='/repo/unapproved.sh'")]
    [InlineData("pwsh", "$env:ZDOTDIR='/repo/unapproved'; ./approved.sh")]
    [InlineData("pwsh", "$env:SHELLOPTS='xtrace'; ./approved.sh")]
    [InlineData("pwsh", "$env:BASHOPTS='extdebug'; ./approved.sh")]
    public static void InterpreterStartupAssignmentsAreDenied(string shell, string command)
    {
        var exception = Assert.Throws<UnauthorizedAccessException>(() => ShellCommandPreparation.Parse(command, shell));

        Assert.Equal(ShellCommandPreparation.DenialReasonCode, exception.Message);
    }

    /// <summary>A rejected startup assignment prevents even earlier allowed actions and approval.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task InterpreterStartupSetupPreventsWholeSequenceLaunch(bool setupFirst)
    {
        // Arrange
        var shell = OperatingSystem.IsWindows() ? "pwsh" : "bash";
        var name = shell == "pwsh" ? "Write-Output" : "fixture-builtin";
        var root = Path.GetFullPath(".");
        var policy = CreatePolicy(root, shell, name, PolicyOutcome.ApprovalRequired);
        var context = ShellCommandPolicyTests.Context(root, policy);
        var manager = new RecordingProcessManager();
        var tool = new RunProcessTool(manager, TestPromptLoader.Instance, requireApproval: false, shellExecutable: ResolveShell(shell))
        {
            PreparationEnvironment = new Dictionary<string, string> { ["PATH"] = string.Empty },
        };
        var allowed = name + " fixture-value";
        Assert.Single(tool.Prepare(new RunProcessInput { Command = allowed }, context).PreparedCommand!.Actions);
        var setup = shell == "pwsh" ? "$env:BASH_ENV='unapproved.sh'" : "BASH_ENV='unapproved.sh'";
        var command = setupFirst ? setup + "; " + allowed : allowed + "; " + setup;
        var coordinator = new CommandPolicyCoordinator();
        using var handler = coordinator.Attach((_, _) => throw new InvalidOperationException("Rejected setup must not request approval."));
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(new Threadsmith.Policies.Matching.PolicyMatcherCatalog(System.Text.RegularExpressions.Regex.InfiniteMatchTimeout)), coordinator, events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider());

        // Act
        var result = await pipeline.InvokeAsync(Request(root, command, context), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
        Assert.Equal(0, manager.Launches);
    }
}
