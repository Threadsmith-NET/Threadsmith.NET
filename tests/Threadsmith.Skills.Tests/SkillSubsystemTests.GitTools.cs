namespace Threadsmith.Skills.Tests;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>Model-invoked skill failure retains its diagnostic receipt in the shared tool envelope.</summary>
    [Theory]
    [InlineData(SkillInvocationStatus.Failed, OperationActivityOutcome.Failed)]
    [InlineData(SkillInvocationStatus.Cancelled, OperationActivityOutcome.Cancelled)]
    public async Task InvokeSkillTool_FailureRetainsPayloadAndEmitsOneFailedCompletion(SkillInvocationStatus status, OperationActivityOutcome outcome)
    {
        var workflows = new CapturingWorkflowOrchestrator { ResultStatus = status };
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Add(item);
            return Task.CompletedTask;
        });
        var pipeline = new ToolInvocationPipeline(
            new ToolRegistry([new InvokeSkillTool(workflows, TestPromptLoader.Instance)]),
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            new SecretOutputSanitizer(),
            NullLogger<ToolInvocationPipeline>.Instance,
            timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var result = await pipeline.InvokeAsync(new ToolInvocationRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            ToolId = "invoke_skill",
            ArgumentsJson = "{\"selector\":\"test-skill\",\"input\":{}}",
            Context = new ToolInvocationContext { RepositoryPath = Environment.CurrentDirectory, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model" },
        });
        Assert.False(result.Succeeded);
        Assert.NotNull(result.ResultJson);
        Assert.NotNull(result.ModelResultContent);
        var completed = Assert.Single(observed.OfType<ToolInvocationCompleted>());
        Assert.False(completed.Succeeded);
        Assert.Equal(outcome, completed.Outcome);
        Assert.Equal(result.ResultJson, completed.ResultJson);
        Assert.Equal(result.ToolInvocationId, Assert.Single(observed.OfType<ToolInvocationStarted>()).ToolInvocationId);
    }

    private sealed class GitToolRepositoryFixture : IDisposable, IAsyncDisposable
    {
        private readonly string _container = Path.Combine(PhysicalTemporaryPath(), "threadsmith-git-tool-" + Guid.NewGuid().ToString("N"));

        public GitToolRepositoryFixture()
        {
            Root = Path.Combine(_container, "repo");
            Cache = Path.Combine(_container, "cache");
            State = Path.Combine(_container, "state");
            Directory.CreateDirectory(Root);
            Processes = new ProcessManager(new SecretOutputSanitizer(), NullLogger<ProcessManager>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            Tools = new ToolRegistry([new GitShowTool(new GitQueryService(timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)), TestPromptLoader.Instance), new GitDiffTool(new GitQueryService(timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)), TestPromptLoader.Instance), new GitBranchComparisonTool(new GitQueryService(timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)), TestPromptLoader.Instance), new ReadFileTool(TestPromptLoader.Instance, new SecretOutputSanitizer())]);
            Pipeline = new ToolInvocationPipeline(Tools, new DefaultPolicyEngine(), new DenyApprovalPolicy(), Events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            _toolSubscription = Events.Subscribe((item, _) =>
            {
                ToolEvents.Add(item);
                return Task.CompletedTask;
            });
        }

        public string Root { get; }

        public string Cache { get; }

        public string State { get; }

        private readonly IDomainEventSubscription _toolSubscription;

        public DomainEventStream Events { get; } = new();

        public List<IDomainEvent> ToolEvents { get; } = [];

        public ToolRegistry Tools { get; }

        public IToolInvocationPipeline Pipeline { get; }

        public IProcessManager Processes { get; }

        public ToolInvocationContext Authority => new() { RepositoryPath = Root, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "user", AllowedExecutables = ["git"] };

        public async Task InitializeAsync()
        {
            await GitAsync("init", "--initial-branch=main");
            await GitAsync("config", "user.email", "fixture@example.invalid");
            await GitAsync("config", "user.name", "Review fixture");
            await File.WriteAllTextAsync(Path.Combine(Root, "tracked.txt"), "original content\n");
            await GitAsync("add", "tracked.txt");
            await GitAsync("commit", "-m", "baseline");
            await GitAsync("switch", "-c", "feature");
        }

        public async Task<string> GitAsync(
            params string[] arguments)
        {
            var result = await Processes.RunAsync(
                new ProcessExecutionRequest
                {
                    ToolInvocationId = ToolInvocationId.New(),
                    RunId = RunId.New(),
                    FileName = "git",
                    Arguments = arguments,
                    WorkingDirectory = Root,
                    Origin = ProcessRequestOrigin.Host,
                });
            Assert.Equal(0, result.ExitCode);
            return result.StandardOutput;
        }

        public async ValueTask DisposeAsync()
        {
            await _toolSubscription.DisposeAsync();
            await Events.DisposeAsync();
            Dispose();
        }

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(_container, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_container, true);
        }

        private static string PhysicalTemporaryPath()
        {
            var path = Path.GetFullPath(Path.GetTempPath());
            var resolved = Path.GetPathRoot(path)!;
            foreach (var segment in path[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                var directory = new DirectoryInfo(Path.Combine(resolved, segment));
                resolved = directory.ResolveLinkTarget(true)?.FullName ?? directory.FullName;
            }

            return Path.TrimEndingDirectorySeparator(resolved);
        }
    }
}
