namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Policies.Commands;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Evaluation;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises script admission through the existing governed native launch path.</summary>
public static class ShellScriptPolicyTests
{
    /// <summary>Every CI platform must execute both script formats; the required-case gate rejects skips.</summary>
    [Fact]
    public static async Task RequiredPlatformScriptsUseGovernedFileInvocations()
    {
        await RealScriptsPreserveLiteralArgumentsAndBothBoundTargets("pwsh", "pwsh");
        await RealScriptsPreserveLiteralArgumentsAndBothBoundTargets("bash", "bash");
    }

    /// <summary>Direct and interpreter-based calls launch the exact authorized file and literal arguments.</summary>
    [Theory]
    [InlineData("powershell", "powershell")]
    [InlineData("pwsh", "pwsh")]
    [InlineData("bash", "bash")]
    [InlineData("sh", "sh")]
    [InlineData("zsh", "zsh")]
    [InlineData("bash", "pwsh")]
    [InlineData("pwsh", "bash")]
    [InlineData("cmd", "pwsh")]
    [InlineData("cmd", "bash")]
    public static async Task RealScriptsPreserveLiteralArgumentsAndBothBoundTargets(string shell, string interpreter)
    {
        // Arrange: the file is intentionally not executable; admission must select its interpreter.
        await using var fixture = new ScriptFixture(shell, interpreter);
        var policy = fixture.Policy();
        string[] expected = ["hello world", "-Message", "--literal=/path with spaces", "native,comma", "literal;|&"];
        var arguments = " " + string.Join(" ", expected.Select(fixture.Quote));
        var relative = "./" + fixture.ScriptName;
        var commands = new[]
        {
            fixture.Direct(relative) + arguments,
            fixture.Direct(fixture.ScriptPath) + arguments,
            fixture.Wrapper(relative) + arguments,
            fixture.Wrapper(fixture.ScriptPath, absoluteInterpreter: true) + arguments,
            fixture.Interpreter + (fixture.IsPowerShell ? " -nologo -NonINTERACTIVE -noprofile -fIlE "
                : fixture.Interpreter == "bash" ? " --noprofile --norc -e -u -- " : " -f -e -u -- ") + fixture.Quote(relative) + arguments,
        };
        for (var index = 0; index < commands.Length; index++)
        {
            var prepared = fixture.Prepare(commands[index], policy);
            Assert.Equal(2, prepared.Actions.Length);
            var script = Assert.Single(prepared.Actions.Where(action => action.CommandKind == CommandPolicyNames.Script));
            var engine = Assert.Single(prepared.Actions.Where(action => action.CommandKind != CommandPolicyNames.Script));
            Assert.Equal(fixture.ScriptName, script.ExecutableName);
            Assert.Equal(fixture.ScriptPath, script.ResolvedExecutable);
            Assert.Equal(fixture.Root, script.WorkingDirectory);
            Assert.Equal(expected, script.Arguments.ToArray());
            Assert.Equal(fixture.InterpreterPath, engine.ResolvedExecutable, ignoreCase: OperatingSystem.IsWindows());
            Assert.Equal(index == 3 ? CommandPolicyNames.ExplicitExternal : CommandPolicyNames.External, engine.CommandKind);
            var scriptIndex = engine.Arguments.IndexOf(OperatingSystem.IsWindows() && !fixture.IsPowerShell ? fixture.ScriptPath.Replace('\\', '/') : fixture.ScriptPath);
            Assert.True(scriptIndex >= 0);
            Assert.Equal(expected, engine.Arguments.Skip(scriptIndex + 1));
            if (fixture.IsPowerShell)
            {
                Assert.Contains("-File", engine.Arguments);
                Assert.Contains("-NoProfile", engine.Arguments);
                Assert.Contains("-NonInteractive", engine.Arguments);
                Assert.DoesNotContain("-Command", engine.Arguments);
            }

            // Act: use the real process manager and pipeline, not ExecuteAsync directly.
            var result = await fixture.InvokeAsync(commands[index], policy, cancellationToken: TestContext.Current.CancellationToken);

            // Assert: compare admitted script argv to what the script actually received.
            var lines = OutputLines(result);
            Assert.Equal("authorized", lines[0]);
            Assert.Equal(script.Arguments.ToArray(), lines.Skip(1));
            Assert.Equal(index + 1, fixture.Manager.Launches);
            Assert.Empty(fixture.Manager.ActiveProcesses);
        }
    }

    /// <summary>File-mode invocation retains PowerShell script parameter and switch binding.</summary>
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    public static async Task PowerShellFileLaunchBindsNamedParametersAndSwitches(string shell)
    {
        await using var fixture = new ScriptFixture(shell, shell);
        await File.WriteAllTextAsync(
            fixture.ScriptPath,
            "param([string]$Message, [switch]$Enabled)\nWrite-Output $Message\nWrite-Output $Enabled.IsPresent\n",
            TestContext.Current.CancellationToken);
        var policy = fixture.Policy();
        foreach (var command in new[] { fixture.Direct("./" + fixture.ScriptName), fixture.Wrapper(fixture.ScriptPath) })
        {
            var invocation = command + " -Message 'path and argument with spaces' -Enabled";
            var script = Assert.Single(fixture.Prepare(invocation, policy).Actions.Where(action => action.CommandKind == CommandPolicyNames.Script));
            Assert.Collection(script.Arguments, value => Assert.Equal("-Message", value), value => Assert.Equal("path and argument with spaces", value), value => Assert.Equal("-Enabled", value));

            var result = await fixture.InvokeAsync(invocation, policy, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Collection(OutputLines(result), value => Assert.Equal("path and argument with spaces", value), value => Assert.Equal("True", value));
        }
    }

    /// <summary>Either action can allow, deny, or require the normal combined approval.</summary>
    [Theory]
    [InlineData(false, PolicyOutcome.Allow, PolicyApprovalChoice.Once, false, 0, true)]
    [InlineData(false, PolicyOutcome.AlwaysDeny, PolicyApprovalChoice.Once, false, 0, false)]
    [InlineData(false, PolicyOutcome.ApprovalRequired, PolicyApprovalChoice.Once, false, 1, true)]
    [InlineData(false, PolicyOutcome.ApprovalRequiredSession, PolicyApprovalChoice.Once, false, 1, true)]
    [InlineData(false, PolicyOutcome.ApprovalAlwaysRequired, PolicyApprovalChoice.Once, false, 1, true)]
    [InlineData(false, PolicyOutcome.ApprovalRequired, PolicyApprovalChoice.Deny, false, 1, false)]
    [InlineData(false, PolicyOutcome.ApprovalRequiredSession, PolicyApprovalChoice.Deny, false, 1, false)]
    [InlineData(false, PolicyOutcome.ApprovalAlwaysRequired, PolicyApprovalChoice.Deny, false, 1, false)]
    [InlineData(false, PolicyOutcome.ApprovalRequired, PolicyApprovalChoice.Once, true, 0, false)]
    [InlineData(true, PolicyOutcome.Allow, PolicyApprovalChoice.Once, false, 0, true)]
    [InlineData(true, PolicyOutcome.AlwaysDeny, PolicyApprovalChoice.Once, false, 0, false)]
    [InlineData(true, PolicyOutcome.ApprovalRequired, PolicyApprovalChoice.Once, false, 1, true)]
    [InlineData(true, PolicyOutcome.ApprovalRequiredSession, PolicyApprovalChoice.Once, false, 1, true)]
    [InlineData(true, PolicyOutcome.ApprovalAlwaysRequired, PolicyApprovalChoice.Once, false, 1, true)]
    [InlineData(true, PolicyOutcome.ApprovalRequired, PolicyApprovalChoice.Deny, false, 1, false)]
    [InlineData(true, PolicyOutcome.ApprovalRequired, PolicyApprovalChoice.Once, true, 0, false)]
    public static async Task ScriptAndInterpreterIndependentlyRetainPolicyOutcomes(
        bool interpreterRule,
        PolicyOutcome outcome,
        PolicyApprovalChoice response,
        bool denyArguments,
        int expectedPrompts,
        bool allowed)
    {
        var shell = OperatingSystem.IsWindows() ? "powershell" : "bash";
        await using var fixture = new ScriptFixture(shell, shell);
        var policy = fixture.Policy(
            scriptOutcome: interpreterRule ? PolicyOutcome.Allow : outcome,
            interpreterOutcome: interpreterRule ? outcome : PolicyOutcome.Allow,
            scriptDeny: denyArguments && !interpreterRule ? ["^hello world$"] : [],
            interpreterDeny: denyArguments && interpreterRule ? ["hello world$"] : []);
        var prompts = 0;
        using var handler = fixture.Coordinator.Attach((approval, token) =>
        {
            token.ThrowIfCancellationRequested();
            prompts++;
            Assert.Contains(fixture.ScriptPath, approval.Summary, StringComparison.Ordinal);
            Assert.Contains(PolicyApprovalChoice.Once, approval.Choices);
            Assert.Contains(PolicyApprovalChoice.Deny, approval.Choices);
            Assert.Equal(outcome != PolicyOutcome.ApprovalAlwaysRequired, approval.Choices.Contains(PolicyApprovalChoice.Session));
            return Task.FromResult(new PolicyApprovalResponse(approval.RequestId, response));
        });

        var result = await fixture.InvokeAsync(fixture.Direct("./" + fixture.ScriptName) + " 'hello world'", policy, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(allowed, result.Succeeded);
        Assert.Equal(expectedPrompts, prompts);
        Assert.Equal(allowed ? 1 : 0, fixture.Manager.Launches);
        if (allowed)
        {
            Assert.Collection(OutputLines(result), value => Assert.Equal("authorized", value), value => Assert.Equal("hello world", value));
        }
        else
        {
            Assert.Equal(expectedPrompts == 1 ? ToolErrorClassification.ApprovalDenied : ToolErrorClassification.PolicyDenied, result.ErrorClassification);
        }
    }

    /// <summary>Executable ceilings do not grant unmatched script or interpreter permission.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task MissingScriptOrInterpreterRuleIsDefaultDenied(bool omitInterpreter)
    {
        var shell = OperatingSystem.IsWindows() ? "powershell" : "bash";
        await using var fixture = new ScriptFixture(shell, shell);
        var policy = fixture.Policy(omitScript: !omitInterpreter, omitInterpreter: omitInterpreter);
        using var handler = fixture.Coordinator.Attach((_, _) => throw new InvalidOperationException("Unmatched actions must not prompt."));

        var result = await fixture.InvokeAsync(fixture.Direct(fixture.ScriptPath), policy, cancellationToken: TestContext.Current.CancellationToken);

        AssertDenied(result, fixture, 0);
    }

    /// <summary>Both captured and effective ceilings restrict both admitted actions.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task BothOuterCeilingsIndependentlyRestrictScriptAndInterpreter(bool securityCeiling, bool removeInterpreter)
    {
        // Different outer/inner engines ensure interpreter denial is not merely outer-shell denial.
        var shell = OperatingSystem.IsWindows() ? "powershell" : "bash";
        var interpreter = OperatingSystem.IsWindows() ? "pwsh" : "sh";
        await using var fixture = new ScriptFixture(shell, interpreter);
        var policy = fixture.Policy(scriptOutcome: PolicyOutcome.ApprovalRequired);
        var removed = removeInterpreter ? interpreter : fixture.ScriptName;
        var names = policy.AllowedExecutables.Where(name => name != removed).ToImmutableArray();
        var context = ShellCommandPolicyTests.Context(fixture.Root, policy) with
        {
            AllowedExecutables = securityCeiling ? policy.AllowedExecutables : names,
            SecurityPolicy = securityCeiling ? policy with { AllowedExecutables = names } : policy,
        };
        using var handler = fixture.Coordinator.Attach((_, _) => throw new InvalidOperationException("Ceiling denial must not prompt."));

        var result = await fixture.InvokeAsync(fixture.Wrapper(fixture.ScriptPath), policy, context: context, cancellationToken: TestContext.Current.CancellationToken);

        AssertDenied(result, fixture, 0);
    }

    /// <summary>Denied stages prevent every process in a compound from starting.</summary>
    [Theory]
    [InlineData(0, ";")]
    [InlineData(1, ";")]
    [InlineData(2, ";")]
    [InlineData(0, "|")]
    [InlineData(1, "|")]
    [InlineData(2, "|")]
    public static async Task AForbiddenScriptStagePreventsTheEntireCompoundLaunch(int deniedIndex, string separator)
    {
        var shell = OperatingSystem.IsWindows() ? "powershell" : "bash";
        await using var fixture = new ScriptFixture(shell, shell);
        var policy = fixture.Policy(scriptDeny: ["^blocked$"]);
        var single = fixture.Direct(fixture.ScriptPath);
        var control = await fixture.InvokeAsync(single + " allowed", policy, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Collection(OutputLines(control), value => Assert.Equal("authorized", value), value => Assert.Equal("allowed", value));
        var command = string.Join(" " + separator + " ", Enumerable.Range(0, 3).Select(index => single + (index == deniedIndex ? " blocked" : " allowed")));
        using var handler = fixture.Coordinator.Attach((_, _) => throw new InvalidOperationException("A denied compound must not prompt."));

        var result = await fixture.InvokeAsync(command, policy, cancellationToken: TestContext.Current.CancellationToken);

        AssertDenied(result, fixture, 1);
    }

    /// <summary>Unsupported interpreter modes and nonexistent script operands fail before execution.</summary>
    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [InlineData("bash")]
    [InlineData("sh")]
    [InlineData("zsh")]
    public static async Task InlineInterpreterProgramsAndMissingScriptsNeverLaunch(string shell)
    {
        await using var fixture = new ScriptFixture(shell, shell);
        var policy = fixture.Policy();
        var interpreter = fixture.Interpreter;
        string[] commands = fixture.IsPowerShell
            ? [interpreter + " -Command 'Write-Output unsafe'", interpreter + " -EncodedCommand VwByAGkAdABlAC0ATwB1AHQAcAB1AHQA", interpreter + " -c 'Write-Output unsafe'", interpreter + " -File -", interpreter + " -File"]
            : [interpreter + " -c 'printf unsafe'", interpreter + " -Command 'printf unsafe'", interpreter + " -EncodedCommand fixture", interpreter + " --", interpreter + " --rcfile fixture", interpreter + " -ec 'printf unsafe'"];
        string[] rejectedOptions = fixture.IsPowerShell
            ? [interpreter + " -ExecutionPolicy Bypass -File " + fixture.Quote(fixture.ScriptPath), interpreter + " -F " + fixture.Quote(fixture.ScriptPath)]
            : [interpreter + " -s " + fixture.Quote(fixture.ScriptPath), interpreter + " -eu " + fixture.Quote(fixture.ScriptPath), interpreter + " -O extglob " + fixture.Quote(fixture.ScriptPath)];
        commands = [.. commands, .. rejectedOptions,
            fixture.Direct("./missing script" + fixture.Extension), fixture.Wrapper("./missing script" + fixture.Extension)];
        using var handler = fixture.Coordinator.Attach((_, _) => throw new InvalidOperationException("Unanalyzed code must not prompt."));
        foreach (var command in commands)
        {
            var result = await fixture.InvokeAsync(command, policy, cancellationToken: TestContext.Current.CancellationToken);

            AssertDenied(result, fixture, 0);
        }
    }

    /// <summary>Cwd normalization and full-path rules do not follow PATH or same-name file shadows.</summary>
    [Theory]
    [InlineData("powershell")]
    [InlineData("bash")]
    public static async Task RelativeScriptsBindToAdmittedCwdNotPathAndAbsoluteRulesDoNotFollowShadows(string shell)
    {
        await using var fixture = new ScriptFixture(shell, shell);
        var shadow = Directory.CreateDirectory(Path.Combine(fixture.Root, "different cwd")).FullName;
        var shadowScript = Path.Combine(shadow, fixture.ScriptName);
        await File.WriteAllTextAsync(shadowScript, fixture.Marker("shadow"), TestContext.Current.CancellationToken);
        var pathOnly = Path.Combine(fixture.Root, "path-only" + fixture.Extension);
        await File.WriteAllTextAsync(pathOnly, fixture.Marker("path-only"), TestContext.Current.CancellationToken);
        var policy = fixture.Policy(fullPathRule: true);
        var relative = fixture.Direct("./" + fixture.ScriptName);
        var authorized = fixture.Prepare(relative, policy);
        var other = fixture.Prepare(relative, policy, shadow);
        Assert.NotEqual(authorized.Identity, other.Identity);
        Assert.Equal(shadowScript, Assert.Single(other.Actions.Where(action => action.CommandKind == CommandPolicyNames.Script)).ResolvedExecutable);

        var blocked = await fixture.InvokeAsync(relative, policy, directory: shadow, cancellationToken: TestContext.Current.CancellationToken);
        AssertDenied(blocked, fixture, 0);
        var absolute = await fixture.InvokeAsync(fixture.Direct(fixture.ScriptPath), policy, directory: shadow, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("authorized", Assert.Single(OutputLines(absolute)));

        // The file exists on the frozen PATH, but not in the requested cwd.
        var pathPolicy = fixture.Policy(scriptName: Path.GetFileName(pathOnly));
        foreach (var command in new[] { fixture.Direct(Path.GetFileName(pathOnly)), fixture.Wrapper(Path.GetFileName(pathOnly)) })
        {
            var result = await fixture.InvokeAsync(command, pathPolicy, directory: shadow, cancellationToken: TestContext.Current.CancellationToken);
            AssertDenied(result, fixture, 1);
        }
    }

    /// <summary>Machine constraints still apply independently despite ordinary user Allows.</summary>
    [Theory]
    [InlineData(false, PolicyOutcome.AlwaysDeny, false, 0)]
    [InlineData(true, PolicyOutcome.AlwaysDeny, false, 0)]
    [InlineData(false, PolicyOutcome.ApprovalAlwaysRequired, false, 1)]
    [InlineData(true, PolicyOutcome.ApprovalAlwaysRequired, false, 1)]
    [InlineData(false, PolicyOutcome.Allow, true, 0)]
    [InlineData(true, PolicyOutcome.Allow, true, 0)]
    public static async Task MachineConstraintsCannotBeBypassedByUserAllows(bool interpreterRule, PolicyOutcome outcome, bool closedSet, int expectedPrompts)
    {
        var shell = OperatingSystem.IsWindows() ? "powershell" : "bash";
        await using var fixture = new ScriptFixture(shell, shell);
        var machine = new PolicyDocument
        {
            Common = new PolicyProfile
            {
                RequireEnforcedMatch = closedSet,
                Enforced =
                [
                    new PolicyRule
                    {
                        Id = "machine-script-constraint",
                        Policy = outcome,
                        Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Executable, Value = interpreterRule ? shell : fixture.ScriptName }],
                    },
                ],
            },
        };
        var policy = fixture.Policy(machine: machine);
        var prompts = 0;
        using var handler = fixture.Coordinator.Attach((request, token) =>
        {
            token.ThrowIfCancellationRequested();
            prompts++;
            Assert.DoesNotContain(PolicyApprovalChoice.Always, request.Choices);
            Assert.DoesNotContain(PolicyApprovalChoice.Session, request.Choices);
            return Task.FromResult(new PolicyApprovalResponse(request.RequestId, PolicyApprovalChoice.Deny));
        });

        var result = await fixture.InvokeAsync(fixture.Direct(fixture.ScriptPath), policy, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedPrompts, prompts);
        Assert.Equal(expectedPrompts == 0 ? ToolErrorClassification.PolicyDenied : ToolErrorClassification.ApprovalDenied, result.ErrorClassification);
        Assert.Equal(0, fixture.Manager.Launches);
    }

    /// <summary>A session grant reuses only the exact script, arguments and invocation identity.</summary>
    [Theory]
    [InlineData("powershell")]
    [InlineData("bash")]
    public static async Task SessionApprovalCannotFollowScriptShadowsOrChangedArguments(string shell)
    {
        await using var fixture = new ScriptFixture(shell, shell);
        var policy = fixture.Policy(scriptOutcome: PolicyOutcome.ApprovalRequiredSession);
        var prompts = 0;
        using var handler = fixture.Coordinator.Attach((request, token) =>
        {
            token.ThrowIfCancellationRequested();
            prompts++;
            return Task.FromResult(new PolicyApprovalResponse(request.RequestId, prompts == 1 ? PolicyApprovalChoice.Session : PolicyApprovalChoice.Deny));
        });
        var command = fixture.Direct(fixture.ScriptPath);
        Assert.Equal("authorized", Assert.Single(OutputLines(await fixture.InvokeAsync(command, policy, cancellationToken: TestContext.Current.CancellationToken))));
        Assert.Equal("authorized", Assert.Single(OutputLines(await fixture.InvokeAsync(command, policy, cancellationToken: TestContext.Current.CancellationToken))));
        Assert.Equal(1, prompts);
        var other = Directory.CreateDirectory(Path.Combine(fixture.Root, "shadow")).FullName;
        var otherPath = Path.Combine(other, fixture.ScriptName);
        await File.WriteAllTextAsync(otherPath, fixture.Marker("shadow"), TestContext.Current.CancellationToken);
        foreach (var variant in new[] { fixture.Direct(otherPath), command + " changed", fixture.Wrapper(fixture.ScriptPath) })
        {
            var result = await fixture.InvokeAsync(variant, policy, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(result.Succeeded);
            Assert.Equal(ToolErrorClassification.ApprovalDenied, result.ErrorClassification);
        }

        Assert.Equal(4, prompts);
        Assert.Equal(2, fixture.Manager.Launches);
    }

    private static string[] OutputLines(ToolInvocationResult result)
    {
        Assert.True(result.Succeeded, result.Error);
        var output = JsonSerializer.Deserialize<ProcessExecutionResult>(result.ResultJson!);
        Assert.NotNull(output);
        Assert.True(output.ExitCode == 0, output.StandardError);
        Assert.False(output.TimedOut);
        return output.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
    }

    private static void AssertDenied(ToolInvocationResult result, ScriptFixture fixture, int launches)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
        Assert.Equal(launches, fixture.Manager.Launches);
        Assert.Empty(fixture.Manager.ActiveProcesses);
    }

    private sealed class ScriptFixture : IAsyncDisposable
    {
        private readonly DomainEventStream _events = new();
        private readonly ToolInvocationPipeline _pipeline;
        private readonly string _shell;
        private readonly SessionId _session = SessionId.New();

        internal ScriptFixture(string shell, string interpreter)
        {
            _shell = shell;
            Interpreter = interpreter;
            var shellPath = ResolveShell(shell);
            InterpreterPath = ResolveShell(interpreter);
            Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "threadsmith's script policy " + Guid.NewGuid().ToString("N"))).FullName;
            ScriptPath = Path.Combine(Root, ScriptName);
            File.WriteAllText(ScriptPath, Marker("authorized"));
            var clock = new FakeTimeProvider();
            Manager = new RecordingProcessManager(new ProcessManager(new SecretOutputSanitizer(), NullLogger<ProcessManager>.Instance, clock));
            Tool = new RunProcessTool(Manager, TestPromptLoader.Instance, requireApproval: false, shellExecutable: shellPath)
            {
                PreparationEnvironment = new Dictionary<string, string>
                {
                    ["PATH"] = string.Join(Path.PathSeparator, Root, Path.GetDirectoryName(InterpreterPath), Environment.GetEnvironmentVariable("PATH")),
                },
            };
            _pipeline = new ToolInvocationPipeline(new ToolRegistry([Tool]), new DefaultPolicyEngine(), Coordinator, _events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: clock);
        }

        internal string Root { get; }

        internal string Interpreter { get; }

        internal string InterpreterPath { get; }

        internal bool IsPowerShell => Interpreter is "powershell" or "pwsh";

        internal string Extension => IsPowerShell ? ".ps1" : ".sh";

        internal string ScriptName => "fixture's script" + Extension;

        internal string ScriptPath { get; }

        internal RecordingProcessManager Manager { get; }

        internal RunProcessTool Tool { get; }

        internal CommandPolicyCoordinator Coordinator { get; } = new();

        public async ValueTask DisposeAsync()
        {
            await _events.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }

        internal string Marker(string identity) => IsPowerShell
            ? "Write-Output '" + identity + "'\nforeach ($value in $args) { Write-Output $value }\n"
            : "printf '%s\\n' '" + identity + "'\nif [ \"$#\" -gt 0 ]; then printf '%s\\n' \"$@\"; fi\n";

        internal string Direct(string path) => (_shell is "powershell" or "pwsh" ? "& " : string.Empty) + Quote(path);

        internal string Wrapper(string path, bool absoluteInterpreter = false)
        {
            var engine = absoluteInterpreter ? Direct(InterpreterPath) : Interpreter;
            return engine + (IsPowerShell ? " -NoProfile -File " : " -- ") + Quote(path);
        }

        internal PreparedShellCommand Prepare(string command, EffectiveSessionPolicy policy, string? directory = null) =>
            Tool.Prepare(new RunProcessInput { Command = command, WorkingDirectory = directory ?? Root }, ShellCommandPolicyTests.Context(Root, policy)).PreparedCommand!;

        internal Task<ToolInvocationResult> InvokeAsync(string command, EffectiveSessionPolicy policy, string? directory = null, ToolInvocationContext? context = null, CancellationToken cancellationToken = default) =>
            _pipeline.InvokeAsync(
                new ToolInvocationRequest
                {
                    SessionId = _session,
                    RunId = RunId.New(),
                    ToolId = "run_process",
                    ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = command, WorkingDirectory = directory ?? Root }),
                    Context = context ?? ShellCommandPolicyTests.Context(Root, policy),
                },
                cancellationToken);

        internal EffectiveSessionPolicy Policy(
            PolicyOutcome scriptOutcome = PolicyOutcome.Allow,
            PolicyOutcome interpreterOutcome = PolicyOutcome.Allow,
            ImmutableArray<string> scriptDeny = default,
            ImmutableArray<string> interpreterDeny = default,
            bool omitScript = false,
            bool omitInterpreter = false,
            bool fullPathRule = false,
            string? scriptName = null,
            PolicyDocument? machine = null)
        {
            var name = scriptName ?? ScriptName;
            var rules = ImmutableArray.CreateBuilder<PolicyRule>();
            if (!omitInterpreter)
            {
                rules.Add(new PolicyRule
                {
                    Id = "fixture-interpreter",
                    Policy = interpreterOutcome,
                    Matchers =
                    [
                        new PolicyMatcher
                        {
                            Type = PolicyMatcherType.AnyOf,
                            Matchers =
                            [
                                new PolicyMatcher { Type = PolicyMatcherType.Executable, Value = Interpreter },
                                new PolicyMatcher { Type = PolicyMatcherType.Executable, Value = InterpreterPath },
                            ],
                        },
                    ],
                    DenyArgumentsMatching = interpreterDeny.IsDefault ? [] : interpreterDeny,
                });
            }

            if (!omitScript)
            {
                rules.Add(new PolicyRule
                {
                    Id = "fixture-script",
                    Policy = scriptOutcome,
                    Matchers =
                    [
                        new PolicyMatcher { Type = PolicyMatcherType.Executable, Value = fullPathRule ? ScriptPath : name },
                        new PolicyMatcher { Type = PolicyMatcherType.Exact, Fact = PolicyFact.CommandKind, Value = CommandPolicyNames.Script },
                    ],
                    DenyArgumentsMatching = scriptDeny.IsDefault ? [] : scriptDeny,
                });
            }

            // Exercise the actual user-document compiler, never replace compiled Rules in a snapshot.
            var user = new PolicyDocument { Common = new PolicyProfile { Rules = rules.ToImmutable() } };
            var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
            var basis = ShellCommandPolicyTests.Policy(Root, _shell);
            return basis with
            {
                AllowedExecutables = [_shell, Interpreter, name],
                CommandPolicies = basis.CommandPolicies.SetItem(_shell, PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), machine, user, platform, _shell)),
            };
        }

        internal string Quote(string value)
        {
            var normalized = value.Replace("\\", "/", StringComparison.Ordinal);
            return _shell == "cmd" ? "\"" + normalized + "\""
                : "'" + normalized.Replace("'", _shell is "powershell" or "pwsh" ? "''" : "'\\''", StringComparison.Ordinal) + "'";
        }

        private static string ResolveShell(string shell)
        {
            if (!OperatingSystem.IsWindows() && shell is "powershell" or "cmd")
            {
                Assert.Skip("Windows PowerShell is not native to this platform.");
            }

            try
            {
                var configured = OperatingSystem.IsWindows() && shell is "bash" or "sh"
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", shell + ".exe") : shell;
                return ProcessLaunchResolver.Resolve(configured, ProcessLaunchResolver.EnvironmentFor(new Dictionary<string, string>()));
            }
            catch (FileNotFoundException)
            {
                Assert.Skip("Optional shell is unavailable; this OS/shell pair remains unvalidated.");
                throw;
            }
        }
    }

    private sealed class RecordingProcessManager : IProcessManager
    {
        private readonly IProcessManager _inner;

        internal RecordingProcessManager(IProcessManager inner)
        {
            _inner = inner;
        }

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => _inner.ActiveProcesses;

        internal int Launches { get; private set; }

        public Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Launches++;
            return _inner.RunAsync(request, cancellationToken);
        }
    }
}
