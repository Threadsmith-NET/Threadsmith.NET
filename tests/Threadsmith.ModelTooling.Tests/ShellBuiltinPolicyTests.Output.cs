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
    /// <summary>Original CI log command forms use real shells with an offline native GitHub CLI substitute.</summary>
    [Theory]
    [InlineData("pwsh")]
    [InlineData("powershell")]
    [InlineData("cmd")]
    [InlineData("bash")]
    [InlineData("sh")]
    [InlineData("zsh")]
    public static async Task CiLogCommandsFilterAndRedirectWithoutPerCommandExecutableEntries(string shell)
    {
        var shellPath = ResolveShell(shell);
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-error-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var source in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fixtures", "command-marker")))
            {
                File.Copy(source, Path.Combine(root, Path.GetFileName(source)));
            }

            var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            var executable = Path.Combine(root, "gh" + extension);
            File.Move(Path.Combine(root, "Threadsmith.CommandMarker" + extension), executable);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Directory.CreateDirectory(Path.Combine(root, ".threadsmith", ".scratchpad"));
            var policy = CreatePolicy(root, shell, "gh", PolicyOutcome.Allow);
            var snapshot = policy.CommandPolicies[shell];
            policy = policy with
            {
                AllowedExecutables = [shell],
                CommandPolicies = policy.CommandPolicies.SetItem(shell, snapshot with
                {
                    Rules =
                    [
                        .. snapshot.Rules,
                        new PolicyRule
                        {
                            Id = "select-group",
                            Policy = PolicyOutcome.Allow,
                            Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Glob, Fact = PolicyFact.ExecutableName, Value = "Select-*", IgnoreCase = true }],
                        },
                    ],
                }),
            };
            var clock = new FakeTimeProvider();
            var manager = new RecordingProcessManager(new ProcessManager(new SecretOutputSanitizer(), NullLogger<ProcessManager>.Instance, clock));
            var tool = new RunProcessTool(manager, TestPromptLoader.Instance, requireApproval: false, shellExecutable: shellPath)
            {
                PreparationEnvironment = new Dictionary<string, string> { ["PATH"] = root },
            };
            await using var events = new DomainEventStream();
            var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), new CommandPolicyCoordinator(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: clock);
            var context = ShellCommandPolicyTests.Context(root, policy);
            var cancellationToken = TestContext.Current.CancellationToken;
            const string redirect = "gh run view --job 131155720059 --repo Threadsmith-NET/Threadsmith.NET --log > .threadsmith/.scratchpad/ci-linux.log";

            var redirected = await pipeline.InvokeAsync(Request(root, redirect, context), cancellationToken);

            Assert.True(redirected.Succeeded, redirected.Error);
            var process = JsonSerializer.Deserialize<ProcessExecutionResult>(redirected.ResultJson!);
            Assert.NotNull(process);
            Assert.Equal(0, process.ExitCode);
            Assert.True(string.IsNullOrWhiteSpace(process.StandardOutput));
            var destination = Path.Combine(root, ".threadsmith", ".scratchpad", "ci-linux.log");
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(destination, cancellationToken));
            Assert.Equal<string?>(["run", "view", "--job", "131155720059", "--repo", "Threadsmith-NET/Threadsmith.NET", "--log"], saved.RootElement.GetProperty("Arguments").EnumerateArray().Select(value => value.GetString()));

            if (shell is "pwsh" or "powershell")
            {
                const string filter = "gh run view 43210252869 --repo Threadsmith-NET/Threadsmith.NET --job 131155719715 --log | Select-String -Pattern 'fail|error|Expected:|Actual:|Stack Trace:' -Context 3,6";
                var filtered = await pipeline.InvokeAsync(Request(root, filter, context), cancellationToken);
                Assert.True(filtered.Succeeded, filtered.Error);
                var output = JsonSerializer.Deserialize<ProcessExecutionResult>(filtered.ResultJson!);
                Assert.NotNull(output);
                Assert.Equal(0, output.ExitCode);
                Assert.Contains("131155719715", output.StandardOutput, StringComparison.Ordinal);
            }

            var append = await pipeline.InvokeAsync(Request(root, "gh second >> .threadsmith/.scratchpad/ci-linux.log", context), cancellationToken);
            Assert.True(append.Succeeded, append.Error);
            Assert.Equal(2, (await File.ReadAllLinesAsync(destination, cancellationToken)).Length);
            var launches = manager.Launches;
            var denied = await pipeline.InvokeAsync(Request(root, "gh denied > denied.log | unapproved-command", context), cancellationToken);
            Assert.False(denied.Succeeded);
            Assert.Equal(ToolErrorClassification.PolicyDenied, denied.ErrorClassification);
            Assert.Equal(launches, manager.Launches);
            Assert.False(File.Exists(Path.Combine(root, "denied.log")));
            Assert.Empty(manager.ActiveProcesses);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
