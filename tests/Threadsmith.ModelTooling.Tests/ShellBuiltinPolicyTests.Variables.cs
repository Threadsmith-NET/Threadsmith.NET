namespace Threadsmith.ModelTooling.Tests;

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
    /// <summary>Variable setup and expansion use ordinary policy, approval, and process tracking.</summary>
    [Theory]
    [InlineData("powershell", "Write-Output", "$env:THREADSMITH_VARIABLE_FIXTURE='fixture-value'; $value = $env:THREADSMITH_VARIABLE_FIXTURE; Write-Output \"${value}\"")]
    [InlineData("pwsh", "Write-Output", "$env:THREADSMITH_VARIABLE_FIXTURE = 'fixture-value'; $value='fixture-value'; Write-Output \"$value\"")]
    [InlineData("cmd", "echo", "echo %THREADSMITH_VARIABLE_FIXTURE%")]
    [InlineData("bash", "printf", "THREADSMITH_VARIABLE_FIXTURE='fixture-value'; printf '%s' \"${THREADSMITH_VARIABLE_FIXTURE}\"")]
    [InlineData("sh", "printf", "THREADSMITH_VARIABLE_FIXTURE=fixture-value; printf '%s' \"$THREADSMITH_VARIABLE_FIXTURE\"")]
    [InlineData("zsh", "printf", "THREADSMITH_VARIABLE_FIXTURE=fixture-value; printf '%s' \"${THREADSMITH_VARIABLE_FIXTURE}\"")]
    public static async Task RealShellVariablesRetainPolicyAndWholeSequenceAdmission(string shell, string name, string command)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var shellPath = ResolveShell(shell);
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "threadsmith-variables-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var clock = new FakeTimeProvider();
            var manager = new RecordingProcessManager(new ProcessManager(new SecretOutputSanitizer(), NullLogger<ProcessManager>.Instance, clock));
            var tool = new RunProcessTool(manager, TestPromptLoader.Instance, requireApproval: false, shellExecutable: shellPath)
            {
                PreparationEnvironment = new Dictionary<string, string>
                {
                    ["PATH"] = root,
                    ["THREADSMITH_VARIABLE_FIXTURE"] = "fixture-value",
                },
            };
            var policy = CreatePolicy(root, shell, name, PolicyOutcome.Allow);
            var context = ShellCommandPolicyTests.Context(root, policy);
            await using var events = new DomainEventStream();
            var coordinator = new CommandPolicyCoordinator();
            var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), coordinator, events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: clock);
            var request = Request(root, command, context);
            var facts = Assert.Single(tool.Prepare(new RunProcessInput { Command = command }, context).PreparedCommand!.Actions);
            Assert.Equal(name, facts.ExecutableName);

            var result = await pipeline.InvokeAsync(request, cancellationToken);

            Assert.True(result.Succeeded, result.Error);
            var output = JsonSerializer.Deserialize<ProcessExecutionResult>(result.ResultJson!);
            Assert.NotNull(output);
            Assert.True(output.ExitCode == 0, output.StandardError);
            Assert.Equal("fixture-value", output.StandardOutput.TrimEnd('\r', '\n'));
            Assert.Empty(manager.ActiveProcesses);
            Assert.Equal(1, manager.Launches);

            // All named actions are admitted before setup, output, or redirection can run.
            var separator = shell == "cmd" ? " & " : "; ";
            var denied = await pipeline.InvokeAsync(
                request with
                {
                    ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = command + separator + "unapproved-command" }),
                },
                cancellationToken);
            Assert.Equal(ToolErrorClassification.PolicyDenied, denied.ErrorClassification);
            Assert.Equal(1, manager.Launches);

            var restricted = CreatePolicy(root, shell, name, PolicyOutcome.Allow, ["THREADSMITH_VARIABLE_FIXTURE|value"]);
            var deniedArguments = await pipeline.InvokeAsync(request with { Context = ShellCommandPolicyTests.Context(root, restricted) }, cancellationToken);
            Assert.Equal(ToolErrorClassification.PolicyDenied, deniedArguments.ErrorClassification);
            Assert.Equal(1, manager.Launches);

            var reviewed = CreatePolicy(root, shell, name, PolicyOutcome.ApprovalAlwaysRequired);
            var prompted = false;
            using var handler = coordinator.Attach((approval, token) =>
            {
                token.ThrowIfCancellationRequested();
                prompted = true;
                return Task.FromResult(new PolicyApprovalResponse(approval.RequestId, PolicyApprovalChoice.Deny));
            });
            var rejectedReview = await pipeline.InvokeAsync(request with { Context = ShellCommandPolicyTests.Context(root, reviewed) }, cancellationToken);
            Assert.True(prompted);
            Assert.Equal(ToolErrorClassification.ApprovalDenied, rejectedReview.ErrorClassification);
            Assert.Equal(1, manager.Launches);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Assignments never turn unreviewed command output or substitutions into setup data.</summary>
    [Theory]
    [InlineData("pwsh", "$value = unapproved-command; Write-Output $value")]
    [InlineData("pwsh", "$value = 'literal' unapproved-command; Write-Output $value")]
    [InlineData("pwsh", "$function:Write-Output='unapproved-command'; Write-Output fixture")]
    [InlineData("pwsh", "$env:VALUE = $(unapproved-command); Write-Output $env:VALUE")]
    [InlineData("pwsh", "Write-Output \"$(unapproved-command)\"")]
    [InlineData("bash", "VALUE=$(unapproved-command) git status")]
    [InlineData("sh", "VALUE=`unapproved-command` git status")]
    [InlineData("zsh", "git status ${VALUE:-$(unapproved-command)}")]
    [InlineData("cmd", "%COMMAND% argument")]
    public static void VariablesDoNotAdmitCalculatedCommandsOrPrograms(string shell, string command)
        => Assert.Throws<UnauthorizedAccessException>(() => ShellCommandPreparation.Parse(command, shell));

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
        var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), coordinator, events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider());

        // Act
        var result = await pipeline.InvokeAsync(Request(root, command, context), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.Succeeded);
        Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
        Assert.Equal(0, manager.Launches);
    }

    /// <summary>Startup-variable names remain valid as ordinary variables and literal data.</summary>
    [Theory]
    [InlineData("pwsh", "$BASH_ENV='literal'; Write-Output $BASH_ENV")]
    [InlineData("pwsh", "$script:ENV='literal'; Write-Output $script:ENV")]
    [InlineData("bash", "printf '%s' 'BASH_ENV=literal'")]
    [InlineData("bash", "GIT_AUTHOR_DATE='fixture' git status")]
    public static void OrdinaryVariableSetupAndArgumentsRemainAdmitted(string shell, string command)
        => Assert.Single(ShellCommandPreparation.Parse(command, shell).Where(item => !item.IsAssignment));

    /// <summary>Variable references retain their source spelling for policy instead of host-side evaluation.</summary>
    [Theory]
    [InlineData("pwsh", "$value='fixture' # setup\nWrite-Output ${value}", "Write-Output", "${value}")]
    [InlineData("pwsh", "# setup\n$value='fixture'; Write-Output $value", "Write-Output", "$value")]
    [InlineData("powershell", "Write-Output \"${env:ProgramFiles(x86)}\"", "Write-Output", "${env:ProgramFiles(x86)}")]
    [InlineData("cmd", "echo \"%ProgramFiles(x86)%\"", "echo", "%ProgramFiles(x86)%")]
    [InlineData("cmd", "echo !VALUE!", "echo", "!VALUE!")]
    [InlineData("bash", "VALUE='fixture' git status \"${VALUE}\"", "git", "${VALUE}")]
    [InlineData("sh", "VALUE=fixture; git status \"$VALUE\"", "git", "$VALUE")]
    [InlineData("zsh", "VALUE=fixture git status \"$VALUE\"", "git", "$VALUE")]
    public static void VariablePolicyFactsRemainSourceBased(string shell, string command, string name, string argument)
    {
        var parsed = Assert.Single(ShellCommandPreparation.Parse(command, shell).Where(item => !item.IsAssignment));

        Assert.Equal(name, parsed.Tokens[0]);
        Assert.Equal(argument, parsed.Tokens[^1]);
    }
}
