namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Policies.Contracts;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Checks non-file commands through the normal policy and tracked shell launch.</summary>
public static class ShellBuiltinPolicyTests
{
    /// <summary>Allowed shell names run, while a denied stage prevents the whole launch.</summary>
    [Theory]
    [InlineData("cmd", "echo", "echo fixture-value")]
    [InlineData("powershell", "Write-Output", "Write-Output -InputObject 'fixture-value'")]
    [InlineData("pwsh", "Write-Output", "Write-Output -InputObject 'fixture-value'")]
    [InlineData("bash", "pwd", "pwd")]
    [InlineData("sh", "pwd", "pwd")]
    [InlineData("zsh", "pwd", "pwd")]
    public static async Task RealShellCommandsUseNormalPolicyAndRejectWholeDeniedSequences(string shell, string name, string command)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var shellPath = ResolveShell(shell);
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-builtin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new FakeTimeProvider();
            var manager = new RecordingProcessManager(new ProcessManager(new SecretOutputSanitizer(), NullLogger<ProcessManager>.Instance, clock));
            var tool = new RunProcessTool(manager, TestPromptLoader.Instance, requireApproval: false, shellExecutable: shellPath)
            {
                // No external command can be selected by the host from this PATH.
                PreparationEnvironment = new Dictionary<string, string> { ["PATH"] = root },
            };
            var policy = CreatePolicy(root, shell, name, PolicyOutcome.Allow);
            var context = ShellCommandPolicyTests.Context(root, policy);
            var facts = Assert.Single(tool.Prepare(new RunProcessInput { Command = command, WorkingDirectory = root }, context).PreparedCommand!.Actions);
            Assert.Equal(CommandPolicyNames.ShellCommand, facts.CommandKind);
            Assert.Equal(string.Empty, facts.ResolvedExecutable);
            Assert.Equal(name, facts.ExecutableName);
            await using var events = new DomainEventStream();
            var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), new CommandPolicyCoordinator(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: clock);
            var request = Request(root, command, context);

            var result = await pipeline.InvokeAsync(request, cancellationToken);

            Assert.True(result.Succeeded, result.Error);
            var output = JsonSerializer.Deserialize<ProcessExecutionResult>(result.ResultJson!);
            Assert.NotNull(output);
            Assert.True(output.ExitCode == 0, output.StandardError);
            if (shell is "cmd" or "powershell" or "pwsh")
            {
                Assert.Equal("fixture-value", output.StandardOutput.TrimEnd('\r', '\n'));
            }
            else
            {
                Assert.Contains(Path.GetFileName(root), output.StandardOutput, StringComparison.Ordinal);
            }

            Assert.Equal(1, manager.Launches);

            // Unknown commands, even in later stages, prevent all execution.
            var separator = shell == "cmd" ? " & " : "; ";
            foreach (var blocked in new[] { command + separator + "unapproved-command", command + " | unapproved-command" })
            {
                var denied = await pipeline.InvokeAsync(request with { ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = blocked, WorkingDirectory = root }) }, cancellationToken);
                Assert.False(denied.Succeeded);
                Assert.Equal(ToolErrorClassification.PolicyDenied, denied.ErrorClassification);
                Assert.Equal(1, manager.Launches);
            }

            Assert.Empty(manager.ActiveProcesses);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Non-file actions retain command outcomes, approval and normalized argument filters.</summary>
    [Theory]
    [InlineData(PolicyOutcome.Allow, false, 0, 1)]
    [InlineData(PolicyOutcome.AlwaysDeny, false, 0, 0)]
    [InlineData(PolicyOutcome.ApprovalRequired, false, 1, 1)]
    [InlineData(PolicyOutcome.ApprovalRequiredSession, false, 1, 1)]
    [InlineData(PolicyOutcome.ApprovalAlwaysRequired, false, 1, 1)]
    [InlineData(PolicyOutcome.ApprovalRequired, true, 0, 0)]
    public static async Task ShellCommandsShareApprovalsAndArgumentDenials(PolicyOutcome outcome, bool denyArguments, int prompts, int launches)
    {
        var shell = OperatingSystem.IsWindows() ? "pwsh" : "bash";
        var name = shell == "pwsh" ? "Write-Output" : "fixture-builtin";
        var root = Path.GetFullPath(".");
        var policy = CreatePolicy(root, shell, name, outcome, denyArguments ? ["^-Message hello world$"] : []);
        var coordinator = new CommandPolicyCoordinator();
        var promptCount = 0;
        using var handler = coordinator.Attach((approval, token) =>
        {
            token.ThrowIfCancellationRequested();
            promptCount++;
            Assert.Contains(name + " (shell command)", approval.Summary, StringComparison.Ordinal);
            return Task.FromResult(new PolicyApprovalResponse(approval.RequestId, PolicyApprovalChoice.Once));
        });
        var manager = new RecordingProcessManager();
        var tool = new RunProcessTool(manager, TestPromptLoader.Instance, requireApproval: false, shellExecutable: ResolveShell(shell))
        {
            PreparationEnvironment = new Dictionary<string, string> { ["PATH"] = string.Empty },
        };
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), coordinator, events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider());
        foreach (var value in new[] { "'hello world'", "\"hello world\"" })
        {
            var result = await pipeline.InvokeAsync(Request(root, name + " -Message " + value, ShellCommandPolicyTests.Context(root, policy)), TestContext.Current.CancellationToken);
            Assert.Equal(launches == 1, result.Succeeded);
        }

        Assert.Equal(prompts * 2, promptCount);
        Assert.Equal(launches * 2, manager.Launches);
    }

    /// <summary>Denial at any compound position prevents prompting and execution; allowed decisions keep their grant fingerprint.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public static async Task CompoundDenialsShortCircuitWithoutChangingAdmissionIdentity(int deniedIndex)
    {
        var shell = OperatingSystem.IsWindows() ? "pwsh" : "bash";
        const string name = "dotnet";
        var root = Path.GetFullPath(".");
        var policy = CreatePolicy(root, shell, name, PolicyOutcome.ApprovalRequired, ["^denied$"]);
        var manager = new RecordingProcessManager();
        var tool = new RunProcessTool(manager, TestPromptLoader.Instance, requireApproval: false, shellExecutable: ResolveShell(shell));
        var command = string.Join("; ", Enumerable.Range(0, 3).Select(index => name + (index == deniedIndex ? " denied" : " allowed")));
        var context = ShellCommandPolicyTests.Context(root, policy);
        var decision = new DefaultPolicyEngine().Evaluate(tool, new RunProcessInput { Command = command, WorkingDirectory = root }, context);
        if (deniedIndex == -1)
        {
            var actions = tool.Prepare(new RunProcessInput { Command = command, WorkingDirectory = root }, context).PreparedCommand!.Actions;
            var expected = CommandPolicyCoordinator.Combine([.. actions.Select(action => Threadsmith.Policies.Evaluation.PolicyEvaluator.Evaluate(policy.CommandPolicies[shell], action))], false);
            var actual = Assert.IsType<PolicyDecision>(decision.CommandDecision);
            Assert.Equal(expected.Outcome, actual.Outcome);
            Assert.Equal(expected.RuleFingerprint, actual.RuleFingerprint);
            Assert.Equal(expected.RuleIds.AsEnumerable(), actual.RuleIds.AsEnumerable());
            Assert.Equal(expected.Choices.AsEnumerable(), actual.Choices.AsEnumerable());
        }
        else
        {
            Assert.False(decision.IsAllowed);
            Assert.Equal(ApprovalLevel.None, decision.RequiredApproval);
        }

        var prompts = 0;
        var coordinator = new CommandPolicyCoordinator();
        using var handler = coordinator.Attach((approval, token) =>
        {
            token.ThrowIfCancellationRequested();
            prompts++;
            return Task.FromResult(new PolicyApprovalResponse(approval.RequestId, PolicyApprovalChoice.Once));
        });
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), coordinator, events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider());

        var result = await pipeline.InvokeAsync(Request(root, command, context), TestContext.Current.CancellationToken);

        Assert.Equal(deniedIndex == -1, result.Succeeded);
        Assert.Equal(deniedIndex == -1 ? 1 : 0, prompts);
        Assert.Equal(deniedIndex == -1 ? 1 : 0, manager.Launches);
        if (deniedIndex != -1)
        {
            Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
        }
    }

    /// <summary>Either executable ceiling denies the whole compound before any approval or launch.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CompoundExecutableDenialPreventsApprovalAndLaunch(bool securityCeiling)
    {
        var shell = OperatingSystem.IsWindows() ? "pwsh" : "bash";
        const string name = "dotnet";
        var root = Path.GetFullPath(".");
        var policy = CreatePolicy(root, shell, name, PolicyOutcome.ApprovalRequired);
        var context = ShellCommandPolicyTests.Context(root, policy) with
        {
            AllowedExecutables = securityCeiling ? policy.AllowedExecutables : [shell],
            SecurityPolicy = securityCeiling ? policy with { AllowedExecutables = [shell] } : policy,
        };
        var manager = new RecordingProcessManager();
        var tool = new RunProcessTool(manager, TestPromptLoader.Instance, requireApproval: false, shellExecutable: ResolveShell(shell));
        const string command = name + " first; " + name + " second";
        Assert.Equal(2, tool.Prepare(new RunProcessInput { Command = command, WorkingDirectory = root }, context).PreparedCommand!.Actions.Length);
        var coordinator = new CommandPolicyCoordinator();
        using var handler = coordinator.Attach((_, _) => throw new InvalidOperationException("A denied compound must not request approval."));
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), coordinator, events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider());

        var result = await pipeline.InvokeAsync(Request(root, command, context), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
        Assert.Equal(0, manager.Launches);
    }

    /// <summary>Generic PowerShell syntax preserves parameters and literal argument values.</summary>
    [Theory]
    [InlineData("-InputObject 'fixture-value'", "fixture-value")]
    [InlineData("-InputObject 'one,two'", "one,two")]
    [InlineData("-InputObject \"one,two\"", "one,two")]
    [InlineData("-InputObject -1", "-1")]
    [InlineData("-InputObject:'fixture-value'", "fixture-value")]
    [InlineData("'-InputObject'", "-InputObject")]
    [InlineData("'--'", "--")]
    [InlineData("-InputObject ''", "")]
    public static async Task PowerShellPreservesParametersAndQuotedParameterLookingValues(string arguments, string expected)
    {
        var root = Path.GetFullPath(".");
        var clock = new FakeTimeProvider();
        var tool = new RunProcessTool(new ProcessManager(new SecretOutputSanitizer(), NullLogger<ProcessManager>.Instance, clock), TestPromptLoader.Instance, requireApproval: false, shellExecutable: ResolveShell("pwsh"))
        {
            PreparationEnvironment = new Dictionary<string, string> { ["PATH"] = string.Empty },
        };
        await using var events = new DomainEventStream();
        var policy = CreatePolicy(root, "pwsh", "Write-Output", PolicyOutcome.Allow);
        var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), new CommandPolicyCoordinator(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: clock);

        var result = await pipeline.InvokeAsync(Request(root, "Write-Output " + arguments, ShellCommandPolicyTests.Context(root, policy)), TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded, result.Error);
        var output = JsonSerializer.Deserialize<ProcessExecutionResult>(result.ResultJson!);
        Assert.NotNull(output);
        Assert.True(output.ExitCode == 0, output.StandardError);
        Assert.Equal(expected, output.StandardOutput.TrimEnd('\r', '\n'));
    }

    /// <summary>Unbound shell stages cannot share mutable shell state, even when individually permitted.</summary>
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public static async Task PowerShellCmdletPipelineIsRejectedBeforeLaunch(string shell)
    {
        var root = Path.GetFullPath(".");
        var policy = CreatePolicy(root, shell, "Get-Process", PolicyOutcome.Allow);
        var snapshot = policy.CommandPolicies[shell];
        var projection = snapshot.Rules[0] with
        {
            Id = "fixture-projection",
            Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Executable, Value = "Select-Object" }],
        };
        policy = policy with
        {
            AllowedExecutables = [shell, "Get-Process", "Select-Object"],
            CommandPolicies = policy.CommandPolicies.SetItem(shell, snapshot with { Rules = [.. snapshot.Rules, projection] }),
        };
        var clock = new FakeTimeProvider();
        var manager = new RecordingProcessManager(new ProcessManager(new SecretOutputSanitizer(), NullLogger<ProcessManager>.Instance, clock));
        var tool = new RunProcessTool(manager, TestPromptLoader.Instance, requireApproval: false, shellExecutable: ResolveShell(shell))
        {
            PreparationEnvironment = new Dictionary<string, string> { ["PATH"] = string.Empty },
        };
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), new CommandPolicyCoordinator(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: clock);
        var command = "Get-Process -Id " + Environment.ProcessId + " | Select-Object -ExpandProperty Id";

        var result = await pipeline.InvokeAsync(Request(root, command, ShellCommandPolicyTests.Context(root, policy)), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
        Assert.Equal(0, manager.Launches);
        Assert.Empty(manager.ActiveProcesses);
    }

    /// <summary>Unsupported PowerShell parameter terminators fail before approval or launch.</summary>
    [Fact]
    public static void PowerShellUnquotedParameterTerminatorIsRejected()
    {
        var shell = ResolveShell("pwsh");
        var environment = new Dictionary<string, string> { ["PATH"] = string.Empty };
        Assert.Throws<UnauthorizedAccessException>(() => ShellCommandPreparation.Prepare("Write-Output -- -NoEnumerate", shell, Path.GetFullPath("."), environment));
    }

    /// <summary>Unsupported cmdlet arrays cannot silently become literal string arguments.</summary>
    [Theory]
    [InlineData("Write-Output one,two")]
    [InlineData("Get-Item -Path src,tests")]
    [InlineData("Write-Output safe; Write-Output one,two")]
    public static void PowerShellCommaArraysAreRejectedBeforeLaunch(string command)
    {
        var shell = ResolveShell("pwsh");
        var environment = new Dictionary<string, string> { ["PATH"] = string.Empty };

        Assert.Throws<UnauthorizedAccessException>(() => ShellCommandPreparation.Prepare(command, shell, Path.GetFullPath("."), environment));
    }

    /// <summary>A failed file binding never becomes permission for a shell name.</summary>
    [Fact]
    public static void ExplicitOrRelativeFilePathsNeverBecomeShellNames()
    {
        var shell = OperatingSystem.IsWindows() ? "pwsh" : "bash";
        var environment = new Dictionary<string, string>();
        var shellPath = ResolveShell(shell);
        Assert.Throws<FileNotFoundException>(() => ShellCommandPreparation.Prepare(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), shellPath, Path.GetFullPath("."), environment));
        Assert.Throws<InvalidOperationException>(() => ShellCommandPreparation.Prepare("./missing-command", shellPath, Path.GetFullPath("."), environment));
    }

    /// <summary>Prompt overrides control denial wording without changing admission.</summary>
    [Theory]
    [InlineData("unsupported-shell")]
    [InlineData("pwsh")]
    public static async Task CommandDenialUsesCatalogOverrideWithoutExposingPolicy(string shell)
    {
        var root = Path.GetFullPath(".");
        var manager = new RecordingProcessManager();
        var prompts = TestPromptLoader.Instance.WithPrompt(PromptFileNames.CorrectionRunProcessDenied, "fixture denial override");
        var tool = new RunProcessTool(manager, prompts, requireApproval: false, shellExecutable: shell == "pwsh" ? ResolveShell(shell) : shell);
        var policy = CreatePolicy(root, "pwsh", "Write-Output", PolicyOutcome.Allow);
        policy = policy with { AllowedExecutables = [.. policy.AllowedExecutables, shell] };
        var context = ShellCommandPolicyTests.Context(root, policy);
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), new CommandPolicyCoordinator(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider());

        var result = await pipeline.InvokeAsync(Request(root, "Write-Output $(dynamic)", context), TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
        Assert.Equal("fixture denial override", result.Error);
        Assert.Equal(0, manager.Launches);
    }

    private static EffectiveSessionPolicy CreatePolicy(string root, string shell, string name, PolicyOutcome outcome, ImmutableArray<string> deny = default)
    {
        var basis = ShellCommandPolicyTests.Policy(root, shell);
        var snapshot = basis.CommandPolicies[shell] with
        {
            Rules =
            [
                new PolicyRule
                {
                    Id = "fixture-command", Policy = outcome, DenyArgumentsMatching = deny.IsDefault ? [] : deny,
                    Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Executable, Value = name }],
                },
            ],
        };
        return basis with { AllowedExecutables = [shell, name], CommandPolicies = basis.CommandPolicies.SetItem(shell, snapshot) };
    }

    private static ToolInvocationRequest Request(string root, string command, ToolInvocationContext context) => new()
    {
        SessionId = SessionId.New(),
        RunId = RunId.New(),
        ToolId = "run_process",
        ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = command, WorkingDirectory = root }),
        Context = context,
    };

    private static string ResolveShell(string shell)
    {
        if (!OperatingSystem.IsWindows() && shell is "cmd" or "powershell")
        {
            Assert.Skip("This shell is not native to the current platform.");
        }

        try
        {
            var configured = OperatingSystem.IsWindows() && shell is "bash" or "sh"
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", shell + ".exe") : shell;
            return ProcessLaunchResolver.Resolve(configured, ProcessLaunchResolver.EnvironmentFor(new Dictionary<string, string>()));
        }
        catch (FileNotFoundException)
        {
            Assert.Skip("Shell unavailable; this OS/shell pair remains unvalidated.");
            throw;
        }
    }

    private sealed class RecordingProcessManager : IProcessManager
    {
        private readonly IProcessManager? _inner;

        internal RecordingProcessManager(IProcessManager? inner = null)
        {
            _inner = inner;
        }

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => _inner?.ActiveProcesses ?? [];

        internal int Launches { get; private set; }

        public Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Launches++;
            return _inner?.RunAsync(request, cancellationToken)
                ?? Task.FromResult(new ProcessExecutionResult(42, 0, "fixture", string.Empty, false, false, false, TimeSpan.Zero));
        }
    }
}
