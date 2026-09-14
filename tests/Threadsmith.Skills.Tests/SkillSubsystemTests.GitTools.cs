namespace Threadsmith.Skills.Tests;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>Large escaped blobs cannot abort an otherwise valid shared tool batch.</summary>
    [Fact]
    public async Task GitShowBatch_BoundsTheSerializedEnvelope()
    {
        await using var fixture = new GitToolRepositoryFixture();
        await fixture.InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "escaped.txt"), new string('<', 100 * 1024));
        await fixture.GitAsync("add", "escaped.txt");
        await fixture.GitAsync("commit", "-m", "escaped source");
        var result = await InvokeFixtureToolAsync<GitShowResult>(fixture, "git_show", new GitShowRequest { Revision = "HEAD", Paths = ["escaped.txt", "tracked.txt"] });
        Assert.True(result.Files[0].IsTruncated);
        Assert.Null(result.Files[0].Content);
        Assert.Equal("original content\n", result.Files[1].Content);
    }

    /// <summary>Page limits do not reject a bounded inventory larger than one process capture.</summary>
    [Fact]
    public async Task GitShowInventory_StreamsLargeTreesBeforeSelectingThePage()
    {
        await using var fixture = new GitToolRepositoryFixture();
        await fixture.InitializeAsync();
        var suffix = new string('x', 75);
        for (var index = 0; index < 5500; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, $"{index:D5}-{suffix}.txt"), "x");
        }

        await fixture.GitAsync("add", "--all");
        await fixture.GitAsync("commit", "-m", "large inventory");
        var first = await InvokeFixtureToolAsync<GitShowResult>(fixture, "git_show", new GitShowRequest { Revision = "HEAD", Inventory = true, InventoryMaximumEntries = 1 });
        var inventory = JsonSerializer.Deserialize<GitShowInventory>(first.Content)!;
        Assert.Single(inventory.Files);
        Assert.Equal(1, inventory.NextOffset);
        var last = await InvokeFixtureToolAsync<GitShowResult>(fixture, "git_show", new GitShowRequest { Revision = inventory.Revision, Inventory = true, InventoryOffset = 5500 });
        var tail = JsonSerializer.Deserialize<GitShowInventory>(last.Content)!;
        Assert.Equal("tracked.txt", Assert.Single(tail.Files).Path);
        Assert.Null(tail.NextOffset);
        for (var index = 0; index < 5500; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, $"{index:D5}-{suffix}.txt"), "changed");
        }

        var dirty = await InvokeFixtureToolAsync<GitShowResult>(fixture, "git_show", new GitShowRequest { Revision = inventory.Revision, Inventory = true, IncludeWorkingTree = true, InventoryMaximumEntries = 1 });
        Assert.NotNull(JsonSerializer.Deserialize<GitShowInventory>(dirty.Content)!.StatusDigest);
    }

    /// <summary>Policy omissions remain explicit after the ordinary pipeline projects metadata to the model.</summary>
    [Fact]
    public async Task GitDiff_MetadataPolicyOmissionsRemainVisibleToModel()
    {
        await using var fixture = new GitToolRepositoryFixture();
        await fixture.InitializeAsync();
        Directory.CreateDirectory(Path.Combine(fixture.Root, "private"));
        var path = Path.Combine(fixture.Root, "private", "file.txt");
        await File.WriteAllTextAsync(path, "baseline");
        await fixture.GitAsync("add", "private/file.txt");
        await fixture.GitAsync("commit", "-m", "private baseline");
        await File.WriteAllTextAsync(path, "changed");
        var result = await fixture.Pipeline.InvokeAsync(new ToolInvocationRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            ToolId = "git_diff",
            ArgumentsJson = "{\"includePatch\":false}",
            Context = fixture.Authority with { RequestedBy = "model", ProhibitedPaths = ["private/**"] },
        });
        Assert.True(result.Succeeded, result.Error);
        using var projection = System.Text.Json.JsonDocument.Parse(result.ModelResultContent!);
        Assert.Equal(1, projection.RootElement.GetProperty("omittedPaths").GetInt32());
        Assert.Empty(projection.RootElement.GetProperty("changedPaths").EnumerateArray());
        Assert.False(projection.RootElement.GetProperty("truncated").GetBoolean());
    }

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
            NullLogger<ToolInvocationPipeline>.Instance);
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

    private static async Task<T> InvokeFixtureToolAsync<T>(GitToolRepositoryFixture fixture, string tool, object input)
    {
        var request = PermissionPlan().Request;
        var result = await fixture.Pipeline.InvokeAsync(new ToolInvocationRequest
        {
            SessionId = request.SessionId, RunId = request.RunId, Phase = request.Phase, ToolId = tool,
            ArgumentsJson = JsonSerializer.SerializeToElement(input).GetRawText(), Context = fixture.Authority,
            ExpectedRegistration = fixture.Tools.GetRegistrations(request.SessionId, request.RunId).Single(item => item.Tool.Definition.Id == tool),
        });
        Assert.True(result.Succeeded, result.ErrorClassification.ToString());
        Assert.False(result.IsTruncated);
        return JsonSerializer.Deserialize<T>(result.ResultJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private sealed class GitToolRepositoryFixture : IDisposable, IAsyncDisposable
    {
        private readonly string _container = Path.Combine(Path.GetTempPath(), "threadsmith-git-tool-" + Guid.NewGuid().ToString("N"));

        public GitToolRepositoryFixture()
        {
            Root = Path.Combine(_container, "repo");
            Cache = Path.Combine(_container, "cache");
            State = Path.Combine(_container, "state");
            Directory.CreateDirectory(Root);
            Processes = new ProcessManager(new SecretOutputSanitizer(), NullLogger<ProcessManager>.Instance);
            Tools = new ToolRegistry([new GitShowTool(new GitQueryService(), TestPromptLoader.Instance), new GitDiffTool(new GitQueryService(), TestPromptLoader.Instance), new GitBranchComparisonTool(new GitQueryService(), TestPromptLoader.Instance), new ReadFileTool(TestPromptLoader.Instance, new SecretOutputSanitizer())]);
            Pipeline = new ToolInvocationPipeline(Tools, new DefaultPolicyEngine(), new DenyApprovalPolicy(), Events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance);
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
    }
}
