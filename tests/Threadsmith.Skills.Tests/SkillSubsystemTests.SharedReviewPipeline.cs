namespace Threadsmith.Skills.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>Model-invoked skill failure retains its diagnostic receipt in the shared tool envelope.</summary>
    [Theory]
    [InlineData(SkillInvocationStatus.Failed, OperationActivityOutcome.Failed)]
    [InlineData(SkillInvocationStatus.Cancelled, OperationActivityOutcome.Cancelled)]
    public async Task InvokeSkillTool_FailureRetainsPayloadAndEmitsOneFailedCompletion(SkillInvocationStatus status, OperationActivityOutcome outcome)
    {
        var receipt = new FocusedReviewDelivery("failed", "inbox", new string('a', 64), "review.md", null, null);
        var workflows = new CapturingWorkflowOrchestrator { ResultStatus = status, Delivery = receipt };
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
        Assert.Equal(receipt, result.ReviewDelivery);
        Assert.NotNull(result.ResultJson);
        Assert.NotNull(result.ModelResultContent);
        var completed = Assert.Single(observed.OfType<ToolInvocationCompleted>());
        Assert.False(completed.Succeeded);
        Assert.Equal(outcome, completed.Outcome);
        Assert.Equal(result.ResultJson, completed.ResultJson);
        Assert.Equal(result.ToolInvocationId, Assert.Single(observed.OfType<ToolInvocationStarted>()).ToolInvocationId);
    }

    /// <summary>Narrow remote reviews do not preload unrelated files or hit their changed-path count.</summary>
    [Fact]
    public async Task FocusedReview_RemoteComparisonFiltersBeforeGitBoundsAndSkipsUnchangedTrees()
    {
        LocalRemoteProcessFixture? processes = null;
        await using var fixture = new ReviewRepositoryFixture(inner => processes = new LocalRemoteProcessFixture(inner));
        processes!.RemoteRoot = fixture.Root;
        await fixture.InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "selected.txt"), "baseline\n");
        for (var index = 0; index < 550; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, $"other-{index}.txt"), $"baseline {index}\n");
        }

        await fixture.GitAsync("add", "--all");
        await fixture.GitAsync("commit", "-m", "large baseline");
        await fixture.GitAsync("branch", "-f", "main", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "selected.txt"), "changed\n");
        for (var index = 0; index < 250; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, $"other-{index}.txt"), $"changed {index}\n");
        }

        await fixture.GitAsync("add", "--all");
        await fixture.GitAsync("commit", "-m", "large comparison");
        var capture = new FocusedReviewTargetCapture(new SecretOutputSanitizer(), fixture.Tools, fixture.Pipeline);
        var input = new FocusedReviewInput { Mode = "remoteBranch", Repository = "https://example.invalid/review.git", Branch = "feature", BaseBranch = "main", Paths = ["selected.txt"] };
        var authority = fixture.Authority with { AllowedNetworkHosts = ["example.invalid"] };
        var target = await capture.CaptureAsync(input, ReviewRequest(), authority);
        Assert.Equal("selected.txt", Assert.Single(target.Files).Path);
        var inventories = fixture.ToolEvents.OfType<ToolInvocationStarted>().Where(item => item.ToolName == "git_show" && item.ActivityDetail!.Contains("inventory", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, inventories.Length);
        Assert.All(inventories, item => Assert.Contains("2 path(s)", item.ActivityDetail, StringComparison.Ordinal));
        Assert.All(fixture.ToolEvents.OfType<ToolInvocationStarted>(), item => Assert.False(string.IsNullOrWhiteSpace(item.ActivityDetail), item.ToolName));
        var empty = await capture.CaptureAsync(input with { Branch = "main", Paths = ["."] }, ReviewRequest(), authority);
        Assert.Empty(empty.Files);
    }

    /// <summary>Policy omissions remain explicit after the ordinary pipeline projects metadata to the model.</summary>
    [Fact]
    public async Task GitDiff_MetadataPolicyOmissionsRemainVisibleToModel()
    {
        await using var fixture = new ReviewRepositoryFixture();
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

    /// <summary>Preflight checks only required tools and blocks missing readers before transport starts.</summary>
    [Fact]
    public async Task FocusedReview_PreflightChecksComparisonIntentBeforeNetwork()
    {
        LocalRemoteProcessFixture? processes = null;
        await using var fixture = new ReviewRepositoryFixture(inner => processes = new LocalRemoteProcessFixture(inner));
        processes!.RemoteRoot = fixture.Root;
        await fixture.InitializeAsync();
        var capture = new FocusedReviewTargetCapture(new SecretOutputSanitizer(), fixture.Tools, fixture.Pipeline);
        var input = new FocusedReviewInput { Mode = "remoteBranch", Repository = "https://example.invalid/review.git", Branch = "feature" };
        var authority = fixture.Authority with { AllowedNetworkHosts = ["example.invalid"], DeniedToolIds = ["git_show"] };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => capture.CaptureAsync(input, ReviewRequest(), authority));
        Assert.Empty(processes.Requests);
        var unbound = await fixture.Pipeline.InvokeAsync(new ToolInvocationRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            ToolId = "git_fetch",
            ArgumentsJson = "{\"repository\":\"https://example.invalid/review.git\",\"branch\":\"feature\"}",
            Context = authority with { RequestedBy = "model", DeniedToolIds = [] },
        });
        Assert.False(unbound.Succeeded);
        Assert.Equal(ToolErrorClassification.PolicyDenied, unbound.ErrorClassification);
        Assert.Empty(processes.Requests);
        var target = await capture.CaptureAsync(input, ReviewRequest(), authority with { DeniedToolIds = ["git_diff"] });
        Assert.NotEmpty(target.Files);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => capture.CaptureAsync(new FocusedReviewInput { Mode = "specialInstructions", Instructions = "Review", BaseBranch = "main" }, ReviewRequest(), fixture.Authority with { DeniedToolIds = ["git_compare_branches"] }));
    }
}
