namespace Threadsmith.Skills.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>Verifies the focused review boundary and its observable result.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FocusedReview_RemoteRetrievalPinsExactRefsAndNeverWritesInvokingGit(bool compare)
    {
        LocalRemoteProcessFixture? processes = null;
        await using var fixture = new ReviewRepositoryFixture(inner => processes = new LocalRemoteProcessFixture(inner));
        processes!.RemoteRoot = fixture.Root;
        await fixture.InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "remote.txt"), "remote content");
        await fixture.GitAsync("add", "remote.txt");
        await fixture.GitAsync("commit", "-m", "source change");
        var revision = (await fixture.GitAsync("rev-parse", "HEAD")).Trim();
        var baseline = (await fixture.GitAsync("rev-parse", "main")).Trim();
        var before = await fixture.GitAsync("status", "--porcelain=v2");
        var capture = new FocusedReviewTargetCapture(new SecretOutputSanitizer(), fixture.Tools, fixture.Pipeline);
        var input = new FocusedReviewInput
        {
            Mode = "remoteBranch",
            Repository = "https://github.com/example/review.git",
            Branch = "feature",
            BaseBranch = compare ? "main" : null,
        };
        var target = await capture.CaptureAsync(input, ReviewRequest(), fixture.Authority with { AllowedNetworkHosts = ["github.com"] });
        Assert.Equal(revision, target.Revision);
        Assert.Equal(compare ? baseline : null, target.ComparisonRevision);
        Assert.Null(target.MergeBase);
        Assert.Equal(fixture.Root, target.InvokingRepository);
        Assert.Equal(input.Repository, target.Repository);
        Assert.Contains("remote content", target.Files.Single(file => file.Path == "remote.txt").Content, StringComparison.Ordinal);
        Assert.Equal(!compare, target.Files.Any(file => file.Path == "tracked.txt"));
        var fetch = Assert.Single(processes.Requests, request => request.Arguments.Contains("fetch"));
        Assert.Contains("--depth=1", fetch.Arguments);
        Assert.DoesNotContain(processes.Requests, request => request.Arguments.Contains("merge-base"));
        Assert.Contains("refs/heads/feature:refs/heads/review-target", fetch.Arguments);
        Assert.Equal(compare, fetch.Arguments.Contains("refs/heads/main:refs/heads/review-base"));
        Assert.All(processes.Requests, request =>
        {
            Assert.StartsWith(fixture.Cache + Path.DirectorySeparatorChar, request.WorkingDirectory, StringComparison.Ordinal);
            Assert.Equal(ProcessRequestOrigin.Host, request.Origin);
            Assert.Contains("core.hooksPath=", request.Arguments);
            Assert.Contains("http.followRedirects=false", request.Arguments);
            Assert.Contains("protocol.ext.allow=never", request.Arguments);
            Assert.DoesNotContain("checkout", request.Arguments);
        });
        Assert.Equal(before, await fixture.GitAsync("status", "--porcelain=v2"));
        var fetchStarted = Assert.Single(fixture.ToolEvents.OfType<ToolInvocationStarted>(), item => item.ToolName == "git_fetch");
        Assert.Contains("depth 1", fetchStarted.ActivityDetail, StringComparison.Ordinal);
        Assert.All(processes.Requests, request => Assert.Equal(fetchStarted.ToolInvocationId, request.ToolInvocationId));
        Assert.Contains(fixture.ToolEvents.OfType<ToolInvocationCompleted>(), item => item.ToolInvocationId == fetchStarted.ToolInvocationId && item.Succeeded);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".inbox")));
        processes.RewrittenUrl = "https://unapproved.example/review.git";
        await Assert.ThrowsAsync<InvalidDataException>(() => capture.CaptureAsync(input, ReviewRequest(), fixture.Authority with { AllowedNetworkHosts = ["github.com"] }));
        Assert.Single(processes.Requests, request => request.Arguments.Contains("fetch"));
        await Assert.ThrowsAsync<InvalidDataException>(() => capture.CaptureAsync(input, ReviewRequest(), fixture.Authority));
        await Assert.ThrowsAsync<InvalidDataException>(() => capture.CaptureAsync(input, ReviewRequest(), fixture.Authority with { AllowedNetworkHosts = ["different.example"] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => capture.CaptureAsync(input with { Branch = "feature:injected" }, ReviewRequest(), fixture.Authority with { AllowedNetworkHosts = ["github.com"] }));
    }

    /// <summary>Verifies the focused review boundary and its observable result.</summary>
    [Fact]
    public async Task FocusedReview_RepairsFailedDeliveryWithoutRepeatingInference()
    {
        await using var fixture = new ReviewRepositoryFixture();
        await fixture.InitializeAsync();
        var inbox = Path.Combine(fixture.Root, ".inbox");
        await File.WriteAllTextAsync(inbox, "blocking destination");
        var catalog = new SkillCatalog([new SkillCatalogSource(SkillScope.Maintained, MaintainedRoot(), "maintained", IsMaintained: true)]);
        await catalog.RefreshAsync();
        var verifier = new SkillPackageVerifier(new SkillTrustPolicySnapshot());
        var executor = new ReviewExecutorStub();
        var sanitizer = new SecretOutputSanitizer();
        var handler = new FocusedReviewWorkflow(
            ReviewResolver(verifier),
            new FocusedReviewTargetCapture(sanitizer, fixture.Tools, fixture.Pipeline),
            executor,
            (_, _) => Task.FromResult(fixture.Authority),
            fixture.State);
        var state = new InMemorySkillStateStore();
        await using var events = new DomainEventStream();
        await using var workflow = new SkillWorkflowOrchestrator(
            catalog,
            verifier,
            new CompatibleEvaluator(),
            new SkillContentLoader(sanitizer),
            new BoundedJsonSchemaValidator(),
            new RejectReviewProcedureRunner(),
            TestPromptLoader.Instance,
            state,
            (_, _) => Task.FromResult(new SkillInvocationHostContext { Trust = RepositoryTrustLevel.TrustedRead, Phase = RunPhase.EvidenceCollection }),
            events,
            handler);
        var request = ReviewRequest() with { InputJson = "{\"mode\":\"specialInstructions\",\"instructions\":\"Inspect source\"}" };
        await Assert.ThrowsAsync<IOException>(() => workflow.InvokeAsync(request));
        Assert.Equal(4, executor.ExecutedRoles.Count);
        Assert.Equal(SkillInvocationStatus.Failed, (await state.GetCheckpointAsync(request.InvocationId))!.Status);
        var recordPath = Path.Combine(fixture.State, request.InvocationId.Value.ToString("N") + ".json");
        var currentRecord = await File.ReadAllTextAsync(recordPath);
        var obsoleteRecord = System.Text.Json.Nodes.JsonNode.Parse(currentRecord)!;
        obsoleteRecord["Version"] = 1;
        await File.WriteAllTextAsync(recordPath, obsoleteRecord.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() => workflow.ResumeAsync(request.InvocationId));
        Assert.Equal(4, executor.ExecutedRoles.Count);
        await File.WriteAllTextAsync(recordPath, currentRecord);
        File.Delete(inbox);
        Directory.CreateDirectory(inbox);
        var result = await workflow.ResumeAsync(request.InvocationId);
        Assert.Equal(SkillInvocationStatus.Completed, result.Status);
        Assert.Equal(4, executor.ExecutedRoles.Count);
        Assert.Single(Directory.GetFiles(inbox));
        Assert.NotNull(result.ReviewDelivery?.SavedPath);
    }

    private sealed class LocalRemoteProcessFixture(IProcessManager inner) : IProcessManager
    {
        public string RemoteRoot { get; set; } = string.Empty;

        public string? RewrittenUrl { get; set; }

        public List<ProcessExecutionRequest> Requests { get; } = [];

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => inner.ActiveProcesses;

        public Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.Arguments.Contains("ls-remote") && RewrittenUrl is not null)
            {
                return Task.FromResult(new ProcessExecutionResult(1, 0, RewrittenUrl, string.Empty, false, false, false, TimeSpan.Zero));
            }

            if (request.Arguments.Contains("fetch"))
            {
                var arguments = request.Arguments.Select(value => value == "protocol.file.allow=never" ? "protocol.file.allow=always" : value).ToArray();
                arguments[Array.LastIndexOf(arguments, "--") + 1] = new Uri(Path.GetFullPath(RemoteRoot) + Path.DirectorySeparatorChar).AbsoluteUri;
                request = request with { Arguments = arguments };
            }

            return inner.RunAsync(request, cancellationToken);
        }
    }
}
