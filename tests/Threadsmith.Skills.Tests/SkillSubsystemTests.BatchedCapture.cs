namespace Threadsmith.Skills.Tests;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>Verifies the shared tool and skill activity contract.</summary>
    [Fact]
    public async Task FocusedReview_BatchesBlobsAndSkipsUnchangedFileDiffProcesses()
    {
        await using var fixture = new ReviewRepositoryFixture();
        await fixture.InitializeAsync();
        for (var i = 0; i < 130; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, $"source-{i}.txt"), $"unique source {i}\n");
        }

        await fixture.GitAsync("add", "--all");
        await fixture.GitAsync("commit", "-m", "many sources");
        var processes = new CaptureProcessCounter(fixture.Processes);
        var capture = new FocusedReviewTargetCapture(new SecretOutputSanitizer(), fixture.Tools, fixture.Pipeline);
        var target = await capture.CaptureAsync(new FocusedReviewInput { BaseBranch = "HEAD" }, ReviewRequest(), fixture.Authority);
        Assert.Empty(target.Files);
        Assert.All(target.Files, file => Assert.False(file.InScope));
        var reads = fixture.ToolEvents.OfType<ToolInvocationStarted>().Where(item => item.ToolName == "git_show").ToArray();
        Assert.Equal(4, reads.Length);
        Assert.All(fixture.ToolEvents.OfType<ToolInvocationStarted>(), item => Assert.False(string.IsNullOrWhiteSpace(item.ActivityDetail), item.ToolName));
        Assert.Single(fixture.ToolEvents.OfType<ToolInvocationStarted>(), item => item.ToolName == "git_diff");
        Assert.DoesNotContain(fixture.ToolEvents.OfType<ToolInvocationStarted>(), item => item.ToolName == "read_file");
        Assert.DoesNotContain(processes.Requests, request => request.Arguments.Contains("diff"));
        Assert.True(processes.Requests.Count < 20);
    }

    /// <summary>Verifies the shared tool and skill activity contract.</summary>
    [Fact]
    public async Task FocusedReview_BlobBatchPreservesUtf8AndRejectsBinaryWithoutCorruptingTheNextBlob()
    {
        await using var fixture = new ReviewRepositoryFixture();
        await fixture.InitializeAsync();
        var text = "café 漢字 😀\n" + new string('a', 40) + " blob 9\nnot a header\n";
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "binary.dat"), [0, 255, 128, 10]);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "unicode.txt"), text);
        var binary = (await fixture.GitAsync("hash-object", "-w", "binary.dat")).Trim();
        var unicode = (await fixture.GitAsync("hash-object", "-w", "unicode.txt")).Trim();
        await fixture.GitAsync("add", "binary.dat", "unicode.txt");
        await fixture.GitAsync("commit", "-m", "binary and unicode");
        var service = new GitQueryService();
        var output = await service.ShowAsync(fixture.Root, new GitShowRequest { Revision = "HEAD", Paths = ["binary.dat", "unicode.txt"] });
        Assert.False(output.IsTruncated);
        var blobs = output.Files;
        Assert.Equal(new[] { binary, unicode }, blobs.Select(blob => blob.ObjectId));
        Assert.Null(blobs[0].Content);
        Assert.True(blobs[0].IsBinary);
        Assert.Equal(text, blobs[1].Content);
        var bounded = new GitQueryService(new GitResourceLimits { MaximumShowCharacters = 16 });
        var limited = await bounded.ShowAsync(fixture.Root, new GitShowRequest { Revision = "HEAD", Paths = ["unicode.txt"] });
        Assert.True(Assert.Single(limited.Files).IsTruncated);
    }

    /// <summary>Large escaped blobs cannot abort an otherwise valid shared tool batch.</summary>
    [Fact]
    public async Task GitShowBatch_BoundsTheSerializedEnvelope()
    {
        await using var fixture = new ReviewRepositoryFixture();
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
        await using var fixture = new ReviewRepositoryFixture();
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

    /// <summary>Shared snapshot paging preserves exact UTF-8 and requirements beyond the per-result limit.</summary>
    [Fact]
    public async Task FocusedReview_PagesLargeWorkingTreeSourcesAndRequirements()
    {
        await using var fixture = new ReviewRepositoryFixture();
        await fixture.InitializeAsync();
        var content = new string('x', (50 * 1024) - 1) + "😀\r\n" + new string('<', 60 * 1024);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "large.txt"), content);
        var capture = new FocusedReviewTargetCapture(new SecretOutputSanitizer(), fixture.Tools, fixture.Pipeline);
        var target = await capture.CaptureAsync(new FocusedReviewInput { BaseBranch = "HEAD", RequirementsDocumentPath = "large.txt", RequirementsSource = "workspace" }, ReviewRequest(), fixture.Authority);
        Assert.Equal(content, target.Files.Single(file => file.Path == "large.txt").Content);
        Assert.Equal(content, target.Requirements!.Content);
        Assert.True(fixture.ToolEvents.OfType<ToolInvocationStarted>().Count(item => item.ToolName == "read_file") >= 6);
    }

    /// <summary>Detached heads require an explicit base even when a remote default is available.</summary>
    [Fact]
    public async Task FocusedReview_DetachedHeadDoesNotInferTheRemoteDefault()
    {
        await using var fixture = new ReviewRepositoryFixture();
        await fixture.InitializeAsync();
        await fixture.GitAsync("update-ref", "refs/remotes/origin/main", "HEAD");
        await fixture.GitAsync("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        await fixture.GitAsync("checkout", "--detach", "HEAD");
        var capture = new FocusedReviewTargetCapture(new SecretOutputSanitizer(), fixture.Tools, fixture.Pipeline);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => capture.CaptureAsync(new FocusedReviewInput(), ReviewRequest(), fixture.Authority));
        Assert.Contains("Detached HEAD", error.Message, StringComparison.Ordinal);
        Assert.Empty((await capture.CaptureAsync(new FocusedReviewInput { BaseBranch = "main" }, ReviewRequest(), fixture.Authority)).Files);
    }

    private static async Task<T> InvokeFixtureToolAsync<T>(ReviewRepositoryFixture fixture, string tool, object input)
    {
        var request = ReviewRequest();
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

    private sealed class CaptureProcessCounter : IProcessManager
    {
        private readonly IProcessManager _inner;

        internal CaptureProcessCounter(IProcessManager inner)
        {
            _inner = inner;
        }

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => _inner.ActiveProcesses;

        internal List<ProcessExecutionRequest> Requests { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return _inner.RunAsync(request, cancellationToken);
        }
    }
}
