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

/// <summary>Exercises bounded shell parsing and the real governed native launch path.</summary>
public static class ShellCommandPolicyTests
{
    /// <summary>A cwd executable cannot replace the absolute target admitted from the child PATH.</summary>
    [Theory]
    [InlineData("cmd")]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [InlineData("bash")]
    [InlineData("sh")]
    [InlineData("zsh")]
    public static async Task RealShellBindsAuthorizedNativeExecutableInsteadOfCwdShadow(string shell)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        if (!OperatingSystem.IsWindows() && shell is "cmd" or "powershell")
        {
            Assert.Skip("This shell is not native to the current platform.");
        }

        string shellPath;
        try
        {
            var configuredShell = OperatingSystem.IsWindows() && shell is "bash" or "sh"
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", shell + ".exe") : shell;
            shellPath = ProcessLaunchResolver.Resolve(configuredShell, ProcessLaunchResolver.EnvironmentFor(new Dictionary<string, string>()));
        }
        catch (FileNotFoundException)
        {
            Assert.Skip("Shell is not installed; this platform/shell pair is unvalidated.");
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), "threadsmith-command-policy-" + Guid.NewGuid().ToString("N"));
        var authorized = Path.Combine(root, "authorized");
        var shadow = Path.Combine(root, "shadow");
        Directory.CreateDirectory(authorized);
        Directory.CreateDirectory(shadow);
        try
        {
            InstallMarker(authorized);
            InstallMarker(shadow);
            await using var events = new DomainEventStream();
            var clock = new FakeTimeProvider();
            var processManager = new ProcessManager(new SecretOutputSanitizer(), NullLogger<ProcessManager>.Instance, clock);
            var coordinator = new CommandPolicyCoordinator();
            var recording = new RecordingProcessManager(processManager);
            var tool = new RunProcessTool(recording, TestPromptLoader.Instance, requireApproval: false, shellExecutable: shellPath)
            {
                PreparationEnvironment = new Dictionary<string, string>
                {
                    ["PATH"] = authorized,
                    ["MSYS2_ARG_CONV_EXCL"] = string.Empty,
                    ["MSYS_NO_PATHCONV"] = string.Empty,
                },
            };
            var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), coordinator, events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: clock);
            var policy = Policy(root, shell);
            var context = Context(root, policy);
            var request = new ToolInvocationRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                ToolId = "run_process",
                ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = "git status", WorkingDirectory = shadow }),
                Context = context,
            };

            var control = await pipeline.InvokeAsync(request with { ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = "git status", WorkingDirectory = root }) }, cancellationToken);
            Assert.True(control.Succeeded, control.Error);
            var controlOutput = JsonSerializer.Deserialize<ProcessExecutionResult>(control.ResultJson ?? throw new InvalidOperationException("Missing control result."));
            Assert.NotNull(controlOutput);
            Assert.True(controlOutput.ExitCode == 0, controlOutput.StandardError);
            Assert.Contains("authorized", controlOutput.StandardOutput, StringComparison.Ordinal);
            var result = await pipeline.InvokeAsync(request, cancellationToken);

            Assert.True(result.Succeeded, result.Error);
            var output = JsonSerializer.Deserialize<ProcessExecutionResult>(result.ResultJson ?? throw new InvalidOperationException("Missing process result."));
            Assert.NotNull(output);
            Assert.True(output.ExitCode == 0, output.StandardError);
            Assert.DoesNotContain("shadow", output.StandardOutput, StringComparison.Ordinal);
            using var marker = JsonDocument.Parse(output.StandardOutput.Trim());
            Assert.Equal("authorized", marker.RootElement.GetProperty("Identity").GetString());
            Assert.Equal("status", Assert.Single(marker.RootElement.GetProperty("Arguments").EnumerateArray()).GetString());
            Assert.Empty(processManager.ActiveProcesses);

            // Compare authorized facts with the native argv, including MSYS path-like arguments.
            var literalInput = new RunProcessInput
            {
                Command = "git status \"/literal-fixture\" \"--output=/literal-fixture\" \"/path with spaces\" \"C:/literal-fixture\" FullyQualifiedName~ShellCommandPolicy native,comma",
                WorkingDirectory = root,
            };
            var prepared = tool.Prepare(literalInput, context).PreparedCommand;
            Assert.NotNull(prepared);
            if (OperatingSystem.IsWindows() && shell is "bash" or "sh" or "zsh")
            {
                Assert.Equal("*", prepared.Launch.Environment["MSYS2_ARG_CONV_EXCL"]);
                Assert.Equal("1", prepared.Launch.Environment["MSYS_NO_PATHCONV"]);
            }

            var literalResult = await pipeline.InvokeAsync(request with { ArgumentsJson = JsonSerializer.Serialize(literalInput) }, cancellationToken);
            Assert.True(literalResult.Succeeded, literalResult.Error);
            var literalOutput = JsonSerializer.Deserialize<ProcessExecutionResult>(literalResult.ResultJson ?? throw new InvalidOperationException("Missing literal result."));
            Assert.NotNull(literalOutput);
            Assert.True(literalOutput.ExitCode == 0, literalOutput.StandardError);
            using var literalMarker = JsonDocument.Parse(literalOutput.StandardOutput.Trim());
            Assert.Equal(
                Assert.Single(prepared.Actions).Arguments,
                literalMarker.RootElement.GetProperty("Arguments").EnumerateArray().Select(argument => argument.GetString()));

            // The exact model-submitted test command must prepare and reach native argv unchanged.
            const string filterCommand = "dotnet test tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Debug --filter FullyQualifiedName~ShellCommandPolicy";
            var filterInput = new RunProcessInput { Command = filterCommand, WorkingDirectory = root };
            var filterFacts = Assert.Single(tool.Prepare(filterInput, context).PreparedCommand!.Actions);
            var filterResult = await pipeline.InvokeAsync(request with { ArgumentsJson = JsonSerializer.Serialize(filterInput) }, cancellationToken);
            Assert.True(filterResult.Succeeded, filterResult.Error);
            var filterOutput = JsonSerializer.Deserialize<ProcessExecutionResult>(filterResult.ResultJson!);
            Assert.NotNull(filterOutput);
            Assert.True(filterOutput.ExitCode == 0, filterOutput.StandardError);
            using var filterMarker = JsonDocument.Parse(filterOutput.StandardOutput.Trim());
            Assert.Equal(filterFacts.Arguments, filterMarker.RootElement.GetProperty("Arguments").EnumerateArray().Select(argument => argument.GetString()));
            Assert.Equal("FullyQualifiedName~ShellCommandPolicy", filterFacts.Arguments[^1]);

            // The same legitimate syntax is still subject to normal argument-denial policy.
            var filteredSnapshot = policy.CommandPolicies[shell];
            var restricted = policy with
            {
                CommandPolicies = policy.CommandPolicies.SetItem(shell, filteredSnapshot with
                {
                    Rules = [.. filteredSnapshot.Rules.Select(rule => rule.Id == "dotnet-standard" ? rule with { DenyArgumentsMatching = ["FullyQualifiedName~ShellCommandPolicy"] } : rule)],
                }),
            };
            var filterLaunches = recording.Launches;
            var deniedFilter = await pipeline.InvokeAsync(request with { ArgumentsJson = JsonSerializer.Serialize(filterInput), Context = Context(root, restricted) }, cancellationToken);
            Assert.False(deniedFilter.Succeeded);
            Assert.Equal(ToolErrorClassification.PolicyDenied, deniedFilter.ErrorClassification);
            Assert.Equal(filterLaunches, recording.Launches);

            // Positive binding is not a rejection-only test; now verify whole-compound denial.
            var denied = await pipeline.InvokeAsync(
                request with
                {
                    ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = "git status | unapproved-command", WorkingDirectory = shadow }),
                },
                cancellationToken);
            Assert.False(denied.Succeeded);
            Assert.Equal(ToolErrorClassification.PolicyDenied, denied.ErrorClassification);

            // Marker-backed Git never contacts a remote. Ordinary transports still reach it.
            foreach (var remote in new[] { "https://example.invalid/repo", "ssh://example.invalid/repo", "git@example.invalid:repo" })
            {
                var positive = await pipeline.InvokeAsync(request with { ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = "git ls-remote \"" + remote + "\"", WorkingDirectory = root }) }, cancellationToken);
                Assert.True(positive.Succeeded, positive.Error);
            }

            var launches = recording.Launches;
            var commands = new List<string>
            {
                "git -c protocol.ext.allow=always ls-remote \"ext::fixture-marker\"",
                "git ls-remote \"ext::fixture-marker\"",
                "git status | git fetch --upload-pack=fixture-command",
                "git commit -m \"literal --ext-diff text\"",
                "git push --repo=\"ext::fixture-marker\"",
                "git archive --remote=\"ext::fixture-marker\" HEAD",
                "git -c remote.fixture.url=/local/repository -c remote.fixture.uploadpack=fixture-command ls-remote fixture",
                "git -cremote.fixture.receivepack=fixture-command push fixture",
            };
            if (shell is "powershell" or "pwsh")
            {
                commands.AddRange([
                    "git fetch --% '--depth=1 --upload-pack=fixture-command' 'https://example.invalid/repo'",
                    "git fetch '--%' '--depth=1 --upload-pack=fixture-command' 'https://example.invalid/repo'",
                    "git status; git fetch \"--%\" '--depth=1 --upload-pack=fixture-command' 'https://example.invalid/repo'",
                ]);
            }

            if (shell != "cmd")
            {
                commands.AddRange([
                    "git -c 'core.sshCommand=fixture\n#' fetch",
                    "git -c'core.sshCommand=fixture\n#' fetch",
                    "git clone --config 'core.sshCommand=fixture\n#' https://example.invalid/repo",
                    "git clone --config='core.sshCommand=fixture\n#' https://example.invalid/repo",
                ]);
                var parsed = ShellCommandPreparation.Parse(commands[^1], shell);
                Assert.Contains("--config=core.sshCommand=fixture\n#", Assert.Single(parsed).Tokens);
            }

            foreach (var command in commands)
            {
                var blocked = await pipeline.InvokeAsync(request with { ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = command, WorkingDirectory = root }) }, cancellationToken);
                Assert.False(blocked.Succeeded);
                Assert.Equal(ToolErrorClassification.PolicyDenied, blocked.ErrorClassification);
                Assert.Equal(launches, recording.Launches);
            }

            // Explicitly permitting a directory-changing builtin cannot invalidate later facts.
            var directoryCommand = shell is "powershell" or "pwsh" ? "Set-Location" : "cd";
            var directoryRule = new PolicyRule
            {
                Id = "fixture-directory",
                Policy = PolicyOutcome.Allow,
                Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Executable, Value = directoryCommand }],
            };
            var directoryPolicy = policy with
            {
                AllowedExecutables = [.. policy.AllowedExecutables, directoryCommand],
                CommandPolicies = policy.CommandPolicies.SetItem(shell, policy.CommandPolicies[shell] with
                {
                    Rules = [.. policy.CommandPolicies[shell].Rules, directoryRule],
                    Enforced =
                    [
                        new PolicyRule
                        {
                            Id = "fixture-restricted-directory", Policy = PolicyOutcome.AlwaysDeny,
                            Matchers = [new PolicyMatcher { Type = PolicyMatcherType.Path, Fact = PolicyFact.WorkingDirectory, Values = [shadow] }],
                        },
                    ],
                }),
            };
            var directoryRequest = request with { Context = Context(root, directoryPolicy) };
            var directoryChange = directoryCommand + " \"" + shadow.Replace('\\', '/') + "\"";
            var singleChange = await pipeline.InvokeAsync(directoryRequest with { ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = directoryChange, WorkingDirectory = root }) }, cancellationToken);
            Assert.True(singleChange.Succeeded, singleChange.Error);
            var changeOutput = JsonSerializer.Deserialize<ProcessExecutionResult>(singleChange.ResultJson!);
            Assert.NotNull(changeOutput);
            Assert.True(changeOutput.ExitCode == 0, changeOutput.StandardError);
            var separator = shell == "cmd" ? " & " : "; ";
            var externalCompound = await pipeline.InvokeAsync(directoryRequest with { ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = "git status" + separator + "git status", WorkingDirectory = root }) }, cancellationToken);
            Assert.True(externalCompound.Succeeded, externalCompound.Error);
            var compoundOutput = JsonSerializer.Deserialize<ProcessExecutionResult>(externalCompound.ResultJson!);
            Assert.NotNull(compoundOutput);
            Assert.True(compoundOutput.ExitCode == 0, compoundOutput.StandardError);
            var beforeBlocked = recording.Launches;
            var restrictedDirectory = await pipeline.InvokeAsync(directoryRequest with { ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = "git status", WorkingDirectory = shadow }) }, cancellationToken);
            Assert.False(restrictedDirectory.Succeeded);
            Assert.Equal(ToolErrorClassification.PolicyDenied, restrictedDirectory.ErrorClassification);
            foreach (var compoundSeparator in new[] { separator, " | ", " && " })
            {
                var blocked = await pipeline.InvokeAsync(directoryRequest with { ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = directoryChange + compoundSeparator + "git status", WorkingDirectory = root }) }, cancellationToken);
                Assert.False(blocked.Succeeded);
                Assert.Equal(ToolErrorClassification.PolicyDenied, blocked.ErrorClassification);
                Assert.Equal(beforeBlocked, recording.Launches);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Unproven syntax is rejected before any action runs.</summary>
    [Theory]
    [InlineData("bash", "git status $(bad)")]
    [InlineData("sh", "git status > marker")]
    [InlineData("zsh", "git status; bad $name")]
    [InlineData("powershell", "git status; Invoke-Expression $value")]
    [InlineData("pwsh", "git status @arguments")]
    [InlineData("powershell", "git fetch --% argument")]
    [InlineData("powershell", "git fetch '--%' argument")]
    [InlineData("powershell", "git fetch \"--%\" argument")]
    [InlineData("powershell", "git fetch --'%' argument")]
    [InlineData("pwsh", "git fetch --% argument")]
    [InlineData("pwsh", "git fetch '--%' argument")]
    [InlineData("pwsh", "git fetch \"--%\" argument")]
    [InlineData("pwsh", "git fetch --'%' argument")]
    [InlineData("pwsh", "git status; git fetch '--%' argument")]
    [InlineData("bash", "git status ~/private")]
    [InlineData("sh", "git status --path=~/private")]
    [InlineData("zsh", "git status path:~/private")]
    [InlineData("pwsh", "git status ~/private")]
    [InlineData("cmd", "git status & %dynamic%")]
    public static void DynamicOrUnsupportedSyntaxRejectsEntirePreparation(string shell, string command)
    {
        Assert.Throws<UnauthorizedAccessException>(() => ShellCommandPreparation.Parse(command, shell));
    }

    /// <summary>A normal unquoted model-submitted test filter is literal in every supported parser.</summary>
    [Theory]
    [InlineData("cmd")]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [InlineData("bash")]
    [InlineData("sh")]
    [InlineData("zsh")]
    public static void ExactModelSubmittedTestFilterIsLiteral(string shell)
    {
        const string command = "dotnet test tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Debug --filter FullyQualifiedName~ShellCommandPolicy";

        var parsed = Assert.Single(ShellCommandPreparation.Parse(command, shell));

        Assert.Equal("dotnet", parsed.Tokens[0]);
        Assert.Equal("FullyQualifiedName~ShellCommandPolicy", parsed.Tokens[^1]);
    }

    /// <summary>Quoted literal operators do not create executable stages.</summary>
    [Fact]
    public static void LiteralOperatorsInsideQuotesAreNotAdditionalActions()
    {
        var parsed = ShellCommandPreparation.Parse("git status 'literal;|&' # comment\ngit --version", "bash");
        Assert.Equal(2, parsed.Length);
        Assert.Equal("literal;|&", parsed[0].Tokens[2]);
    }

    /// <summary>Equivalent literal quoting produces the same argument-denial input.</summary>
    [Theory]
    [InlineData("bash", "git commit -m hello\\ world")]
    [InlineData("bash", "git commit -m 'hello world'")]
    [InlineData("bash", "git commit -m \"hello world\"")]
    [InlineData("pwsh", "git commit -m 'hello world'")]
    [InlineData("pwsh", "git commit -m \"hello world\"")]
    [InlineData("cmd", "git commit -m \"hello world\"")]
    public static void ArgumentDenialIgnoresLiteralQuoteStyle(string shell, string command)
    {
        // Arrange: use parsed facts, not a new launch path.
        var parsed = Assert.Single(ShellCommandPreparation.Parse(command, shell));
        var defaults = ShippedCommandPolicy.CreateDefaults();
        var git = defaults.Common.Rules.Single(rule => rule.Id == "git-standard") with { DenyArgumentsMatching = ["^commit -m hello world$"] };
        var document = defaults with { Common = defaults.Common with { Rules = [git] } };
        var platform = shell == "bash" ? "linux" : "windows";
        var snapshot = PolicyCompiler.Compile(document, null, null, platform, shell);
        var facts = new CommandFacts(platform, shell, "git", Path.GetFullPath("git"), "external", [.. parsed.Tokens.Skip(1)], Path.GetFullPath("."));

        // Act / Assert.
        Assert.Collection(
            facts.Arguments,
            argument => Assert.Equal("commit", argument),
            argument => Assert.Equal("-m", argument),
            argument => Assert.Equal("hello world", argument));
        Assert.False(PolicyEvaluator.Evaluate(snapshot, facts).IsAllowed);
    }

    internal static EffectiveSessionPolicy Policy(string repository, string shell)
    {
        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        return new EffectiveSessionPolicy
        {
            BindingId = Guid.NewGuid(),
            Generation = 1,
            Fingerprint = "fixture-policy",
            RepositoryPath = repository,
            MaximumTrust = RepositoryTrustLevel.TrustedBuild,
            ReadFolders = [repository],
            AllowedExecutables = [shell, "git", "dotnet"],
            CommandPolicies = ImmutableDictionary<string, PolicySnapshot>.Empty.Add(shell, PolicyCompiler.Compile(ShippedCommandPolicy.CreateDefaults(), null, null, platform, shell)),
        };
    }

    internal static ToolInvocationContext Context(string repository, EffectiveSessionPolicy policy) => new()
    {
        RepositoryPath = repository,
        RequestedBy = "fixture",
        TrustLevel = RepositoryTrustLevel.TrustedBuild,
        ApprovedRoots = [repository],
        AllowedExecutables = policy.AllowedExecutables,
        SecurityPolicy = policy,
    };

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
            Launches++;
            return _inner.RunAsync(request, cancellationToken);
        }
    }

    private static void InstallMarker(string directory)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "fixtures", "command-marker");
        Assert.True(Directory.Exists(source), "Native command-marker build output is missing.");
        foreach (var path in Directory.EnumerateFiles(source))
        {
            File.Copy(path, Path.Combine(directory, Path.GetFileName(path)));
        }

        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        var executable = Path.Combine(directory, "git" + extension);
        File.Move(Path.Combine(directory, "Threadsmith.CommandMarker" + extension), executable);
        File.Copy(executable, Path.Combine(directory, "dotnet" + extension));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(Path.Combine(directory, "dotnet"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
