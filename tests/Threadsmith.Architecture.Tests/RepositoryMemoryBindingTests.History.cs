namespace Threadsmith.Architecture.Tests;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

public static partial class RepositoryMemoryBindingTests
{
    /// <summary>Fixture commits remain reproducible despite configured signing and commit hooks.</summary>
    [Fact]
    public static async Task History_fixture_suppresses_signing_and_hooks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await HistoryGitAsync(fixture.InitialRoot, ct, "init");
        var hooks = Directory.CreateDirectory(Path.Combine(fixture.InitialRoot, "fixture-hooks")).FullName;
        var hook = Path.Combine(hooks, "pre-commit");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\nexit 1\n", ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await HistoryGitAsync(fixture.InitialRoot, ct, "config", "core.hooksPath", hooks);
        await HistoryGitAsync(fixture.InitialRoot, ct, "config", "commit.gpgsign", "true");
        await HistoryGitAsync(fixture.InitialRoot, ct, "config", "gpg.program", "threadsmith-nonexistent-signing-program");
        await File.WriteAllTextAsync(Path.Combine(fixture.InitialRoot, "history.txt"), "fixture history", ct);
        await HistoryGitAsync(fixture.InitialRoot, ct, "add", "history.txt");

        await HistoryGitAsync(fixture.InitialRoot, ct, "commit", "-m", "isolated fixture");

        Assert.Equal("isolated fixture", (await HistoryGitAsync(fixture.InitialRoot, ct, "log", "-1", "--format=%s")).Trim());
        Assert.Equal("fixture history", await HistoryGitAsync(fixture.InitialRoot, ct, "show", "HEAD:history.txt"));
    }

    /// <summary>Host paging crosses normal tool events and preserves prior collection on mid-history failure.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public static async Task Onboarding_supplies_history_without_model_requests_and_requires_every_page(bool revokeHistory, bool roundLimit)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await HistoryGitAsync(fixture.InitialRoot, ct, "init");
        for (var index = 0; index < 17; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.InitialRoot, "feature.txt"), $"implementation {index}", ct);
            await HistoryGitAsync(fixture.InitialRoot, ct, "add", "feature.txt");
            await HistoryGitAsync(fixture.InitialRoot, ct, "commit", "-m", $"design step {index:D2}");
        }

        await using var intelligence = await IntelligenceFixture.CreateAsync(fixture, ct);
        await using var operations = intelligence.CreateOperations();
        var before = await intelligence.Store.GetSnapshotAsync(intelligence.Identity, [], ct);
        var context = new ToolInvocationContext
        {
            RepositoryPath = fixture.InitialRoot,
            RequestedBy = "model",
            TrustLevel = RepositoryTrustLevel.FullyTrustedAutomation,
            AllowedExecutables = ["git"],
        };
        ITool[] tools = [
            new MemoriesTool(new UnusedMemories(), intelligence.Options, TestPromptLoader.Instance),
            new ReadFileTool(TestPromptLoader.Instance, new SecretOutputSanitizer()),
            new GitLogTool(new GitQueryService(timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)), TestPromptLoader.Instance),
            new GitShowTool(new GitQueryService(timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)), TestPromptLoader.Instance),
            new GitDiffTool(new GitQueryService(timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)), TestPromptLoader.Instance),
            new IntelligenceForbiddenTool("fixture_writer", ToolSideEffect.WritesFiles, ToolCategory.FileWrite),
            new IntelligenceForbiddenTool("fixture_executor", ToolSideEffect.ExecutesCode, ToolCategory.ProcessExecution),
        ];
        var state = new ToolStateManager(tools.Select(tool => tool.Definition), new ConfigurationBuilder().Build(), Path.Combine(fixture.InitialRoot, ".threadsmith", "config.json"));
        foreach (var id in new[] { "git_log", "git_show", "git_diff" })
        {
            await state.DisableAsync(id, ct);
        }

        var registry = new ToolRegistry(tools, state);
        foreach (var id in new[] { "git_log", "git_show", "git_diff" })
        {
            await state.EnableAsync(id, ct);
        }

        var events = new List<ToolInvocationCompleted>();
        var started = new List<ToolInvocationStarted>();
        await using var subscription = intelligence.Events.Subscribe((item, _) =>
        {
            if (item is ToolInvocationCompleted completed)
            {
                events.Add(completed);
            }

            if (item is ToolInvocationStarted invocation)
            {
                started.Add(invocation);
            }

            return Task.CompletedTask;
        });
        var revokeOnce = revokeHistory;
        var model = new CallbackModel(
            () =>
            {
                if (revokeOnce)
                {
                    revokeOnce = false;
                    context = context with { DeniedToolIds = ["git_log"] };
                }
            },
            "api_key=onboarding-secret-value",
            finishCoverage: true);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new AllowApprovalPolicy(), intelligence.Events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var application = new SessionApplication(
            intelligence.Events,
            model,
            UnboundedBudget.Instance,
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            toolPipeline: pipeline,
            toolContextFactory: (_, _) => Task.FromResult(context),
            toolRegistry: registry,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            intelligenceOperations: operations,
            limits: roundLimit ? new ExecutionLimits { MaxModelRounds = 1 } : null);
        var session = await application.HandleAsync(new CreateSessionCommand("history"), ct);
        var run = await application.HandleAsync(new SubmitRequestCommand(session, "Collect guidance") { IntelligenceOperation = RepositoryIntelligenceOperation.Onboard }, ct);
        if (revokeHistory || roundLimit)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => application.HandleAsync(new WaitForRunCommand(run), ct));
            Assert.Equal(roundLimit ? 1 : 2, model.Requests);
            Assert.Single(started, item => item.ToolName == "git_log");
            var after = await intelligence.Store.GetSnapshotAsync(intelligence.Identity, [], ct);
            Assert.Equal(before.Entries.Select(item => (item.Id, item.Text, item.Revision)), after.Entries.Select(item => (item.Id, item.Text, item.Revision)));
            var checkpoint = await intelligence.Store.ReadOnboardingCheckpointAsync(intelligence.Identity, ct);
            if (roundLimit)
            {
                // A final answer without an initial survey cannot acknowledge the first history page.
                Assert.Null(checkpoint);
                Assert.Single(model.CapturedRequests);
            }
            else
            {
                Assert.NotNull(checkpoint);
                Assert.Equal(1, checkpoint.NextHistoryPage);
                Assert.DoesNotContain("onboarding-secret-value", checkpoint.WorkingContext, StringComparison.Ordinal);
                context = context with { DeniedToolIds = [] };
                var resumed = await application.HandleAsync(new SubmitRequestCommand(session, "Continue onboarding") { IntelligenceOperation = RepositoryIntelligenceOperation.Onboard }, ct);
                Assert.True(await application.HandleAsync(new WaitForRunCommand(resumed), ct));
                Assert.Null(await intelligence.Store.ReadOnboardingCheckpointAsync(intelligence.Identity, ct));

                var resumedHistory = Assert.Single(model.CapturedRequests[^1].Messages, message => message.SectionId.StartsWith("repository-intelligence-history-", StringComparison.Ordinal)).GetModelVisibleContent();
                Assert.Contains("design step 16", resumedHistory, StringComparison.Ordinal);
                Assert.DoesNotContain("design step 00", resumedHistory, StringComparison.Ordinal);
            }
        }
        else
        {
            Assert.True(await application.HandleAsync(new WaitForRunCommand(run), ct));
            Assert.Equal(2, started.Count(item => item.ToolName == "git_log"));
            Assert.Contains(started, item => item.ToolName == "memories");
            var first = Assert.Single(model.CapturedRequests[0].Messages, message => message.SectionId.StartsWith("repository-intelligence-history-", StringComparison.Ordinal)).GetModelVisibleContent();
            Assert.Contains("design step 00", first, StringComparison.Ordinal);
            Assert.DoesNotContain("design step 16", first, StringComparison.Ordinal);
            var last = model.CapturedRequests[^1].Messages.Last(message => message.SectionId.StartsWith("repository-intelligence-history-", StringComparison.Ordinal)).GetModelVisibleContent();
            Assert.Contains("design step 16", last, StringComparison.Ordinal);
        }

        Assert.All(model.CapturedRequests, request => Assert.All(new[] { "git_log", "git_show", "git_diff" }, id => Assert.Contains(request.Tools, tool => tool.Name == id)));
        Assert.All(model.CapturedRequests, request => Assert.DoesNotContain(request.Tools, tool => tool.Name.StartsWith("fixture_", StringComparison.Ordinal)));
        foreach (var id in new[] { "git_log", "git_show", "git_diff" })
        {
            await state.DisableAsync(id, ct);
            Assert.False(state.IsEnabled(id));
            Assert.DoesNotContain(registry.GetRegistrations(session, RunId.New()), item => item.Tool.Definition.Id == id);
            Assert.Throws<KeyNotFoundException>(() => registry.Get(id));
        }

        var ordinary = await application.HandleAsync(new SubmitRequestCommand(session, "Ordinary request"), ct);
        Assert.True(await application.HandleAsync(new WaitForRunCommand(ordinary), ct));
        Assert.DoesNotContain(model.CapturedRequests[^1].Tools, tool => tool.Name.StartsWith("git_", StringComparison.Ordinal));
        Assert.Contains(model.CapturedRequests[^1].Tools, tool => tool.Name == "fixture_writer");
        Assert.Contains(model.CapturedRequests[^1].Tools, tool => tool.Name == "fixture_executor");
    }

    /// <summary>The operation retains cancellation ownership between pages and cannot be advanced by model calls.</summary>
    [Fact]
    public static async Task History_tool_retains_operation_cancellation_and_confines_pages()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await using var intelligence = await IntelligenceFixture.CreateAsync(fixture, ct);
        await using var operations = intelligence.CreateOperations();
        await using var run = await operations.BeginAsync(SessionId.New(), RunId.New(), fixture.InitialRoot, RepositoryIntelligenceOperation.Onboard, ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await using var scope = new ToolOperationScope(cancellation.Token);
        var service = new HistoryCancellationService();
        var tool = new GitLogTool(service, TestPromptLoader.Instance);
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), new ToolInvocationContext
        {
            RepositoryPath = fixture.InitialRoot,
            RequestedBy = GitLogRequest.CollectionRequestor,
            IntelligenceRun = run,
            OperationScope = scope,
            ProhibitedPaths = ["secret.txt"],
        });
        Assert.DoesNotContain("collectionPage", tool.Definition.InputSchema.JsonSchema, StringComparison.OrdinalIgnoreCase);
        var first = await tool.ExecuteAsync(new GitLogRequest(), context, ct);
        var commit = Assert.Single(first.Value.Commits);
        Assert.Equal(["allowed.cs"], commit.ChangedPaths);
        Assert.Equal(1, commit.OmittedPaths);
        Assert.DoesNotContain("secret.txt", first.ModelResultContent, StringComparison.Ordinal);
        await tool.ExecuteAsync(new GitLogRequest(), context, ct);
        Assert.Equal(1, service.Reads);
        await tool.ExecuteAsync(new GitLogRequest(), context with { Invocation = context.Invocation with { RequestedBy = "model" } }, ct);
        Assert.Equal(1, service.Reads);
        Assert.Equal(1, service.OrdinaryReads);
        await cancellation.CancelAsync();
        Assert.True(service.ReaderCancellation.IsCancellationRequested);
        await scope.DisposeAsync();
        Assert.True(service.Disposed);
    }

    /// <summary>Forged calls cannot bypass the collection advertisement restriction, even at maximum trust.</summary>
    [Theory]
    [InlineData(ToolSideEffect.WritesFiles, ToolCategory.FileWrite)]
    [InlineData(ToolSideEffect.ExecutesCode, ToolCategory.ProcessExecution)]
    [InlineData(ToolSideEffect.ExecutesCode, ToolCategory.RepositoryInspection)]
    [InlineData(ToolSideEffect.ReadOnly, ToolCategory.Workflow)]
    public static async Task Onboarding_rejects_forced_write_and_execution_calls(ToolSideEffect effect, ToolCategory category)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await using var intelligence = await IntelligenceFixture.CreateAsync(fixture, ct);
        await using var operations = intelligence.CreateOperations();
        var session = SessionId.New();
        var runId = RunId.New();
        await using var run = await operations.BeginAsync(session, runId, fixture.InitialRoot, RepositoryIntelligenceOperation.Onboard, ct);
        var tool = new IntelligenceForbiddenTool("fixture_forbidden", effect, category);
        var registry = new ToolRegistry([tool]);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new AllowApprovalPolicy(), intelligence.Events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var invocation = new ToolInvocationRequest
        {
            SessionId = session,
            RunId = runId,
            ToolId = tool.Definition.Id,
            ArgumentsJson = "{}",
            Context = new ToolInvocationContext
            {
                RepositoryPath = fixture.InitialRoot,
                RequestedBy = "model",
                TrustLevel = RepositoryTrustLevel.FullyTrustedAutomation,
                IntelligenceRun = run,
            },
        };
        var result = await pipeline.InvokeAsync(invocation, ct);
        Assert.False(result.Succeeded);
        Assert.Equal(ToolErrorClassification.PolicyDenied, result.ErrorClassification);
    }

    private sealed record IntelligenceForbiddenInput;

    private sealed class IntelligenceForbiddenTool : Tool<IntelligenceForbiddenInput, string>
    {
        public IntelligenceForbiddenTool(string id, ToolSideEffect effect, ToolCategory category)
        {
            Definition = new ToolDefinition
            {
                Id = id,
                Version = "1",
                Description = "Must remain inaccessible during onboarding.",
                Category = category,
                SideEffect = effect,
                InputSchema = new ToolSchema("input", 1, "{\"type\":\"object\",\"properties\":{}}"),
                OutputSchema = new ToolSchema("output", 1, "{\"type\":\"string\"}"),
                Timeout = TimeSpan.FromSeconds(30),
                MaximumOutputBytes = 1024,
            };
        }

        public override ToolDefinition Definition { get; }

        public override Task<ToolExecution<string>> ExecuteAsync(IntelligenceForbiddenInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Forbidden tool reached execution.");
        }

        protected override void ValidateInput(IntelligenceForbiddenInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
        }
    }

    private static async Task<string> HistoryGitAsync(string root, CancellationToken ct, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true },
        };
        // Fixture history must not inherit signing, hooks, templates or Git directory overrides.
        foreach (var key in process.StartInfo.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            process.StartInfo.Environment.Remove(key);
        }

        process.StartInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        process.StartInfo.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in new[]
        {
            "-c", "user.name=History Test", "-c", "user.email=history@example.invalid",
            "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false",
            "-c", "core.hooksPath=" + Path.Combine(root, ".threadsmith", "no-hooks"),
            "-c", "init.templateDir=",
        }.Concat(arguments))
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        await using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between the cancellation check and termination.
            }
        });
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct);
            await Task.WhenAll(output, error).WaitAsync(ct);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited while termination was being requested.
            }

            // Join the terminated process and drain its pipes before fixture disposal.
            await Task.WhenAll(process.WaitForExitAsync(CancellationToken.None), output, error)
                .WaitAsync(TestContext.Current.CancellationToken);
            throw;
        }

        Assert.True(process.ExitCode == 0, await error);
        return await output;
    }

    private sealed class HistoryCancellationService : IGitQueryService, IGitHistoryReader
    {
        public CancellationToken ReaderCancellation { get; private set; }

        public int Reads { get; private set; }

        public int OrdinaryReads { get; private set; }

        public bool Disposed { get; private set; }

        public Task<IGitHistoryReader> OpenHistoryAsync(string repositoryPath, CancellationToken cancellationToken = default)
        {
            ReaderCancellation = cancellationToken;
            return Task.FromResult<IGitHistoryReader>(this);
        }

        public Task<GitLogResult> ReadPageAsync(int maximumCommits, int maximumCharacters, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(new GitLogResult([new GitCommitSummary(new string('a', 40), [], string.Empty, string.Empty, DateTimeOffset.UtcNow, string.Empty) { ChangedPaths = ["allowed.cs", "secret.txt"] }], false)
            {
                IsCollectionPage = true,
                NextCollectionPage = 1,
            });
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public Task<string?> GetCurrentBranchAsync(string repositoryPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string?> GetRevisionAsync(string repositoryPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitDiffResult> DiffAsync(string repositoryPath, GitDiffRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitLogResult> LogAsync(string repositoryPath, GitLogRequest request, CancellationToken cancellationToken = default)
        {
            OrdinaryReads++;
            return Task.FromResult(new GitLogResult([], false));
        }

        public Task<GitShowResult> ShowAsync(string repositoryPath, GitShowRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitBlameResult> BlameAsync(string repositoryPath, GitBlameRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<GitBranchComparisonResult> CompareBranchesAsync(string repositoryPath, GitBranchComparisonRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
