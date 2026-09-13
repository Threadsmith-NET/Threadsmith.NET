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
        using var fixture = new ReviewRepositoryFixture();
        await fixture.InitializeAsync();
        var before = await fixture.GitAsync("status", "--porcelain=v2");
        var processes = new RemoteReviewProcessFixture();
        var capture = new FocusedReviewTargetCapture(processes, new SecretOutputSanitizer(), fixture.Cache);
        var input = new FocusedReviewInput
        {
            Mode = "remoteBranch",
            Repository = "https://github.com/example/review.git",
            Branch = "feature/exact",
            BaseBranch = compare ? "release/exact" : null,
        };
        var target = await capture.CaptureAsync(input, ReviewRequest(), fixture.Authority with { AllowedNetworkHosts = ["github.com"] });
        Assert.Equal(RemoteReviewProcessFixture.Revision, target.Revision);
        Assert.Equal(compare ? RemoteReviewProcessFixture.Baseline : null, target.MergeBase);
        Assert.Equal(fixture.Root, target.InvokingRepository);
        Assert.Equal(input.Repository, target.Repository);
        Assert.Contains("remote content", Assert.Single(target.Files).Content, StringComparison.Ordinal);
        var fetch = Assert.Single(processes.Requests, request => request.Arguments.Contains("fetch"));
        Assert.Contains("refs/heads/feature/exact:refs/heads/review-target", fetch.Arguments);
        Assert.Equal(compare, fetch.Arguments.Contains("refs/heads/release/exact:refs/heads/review-base"));
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
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".inbox")));
        processes.RewrittenUrl = "https://unapproved.example/review.git";
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => capture.CaptureAsync(input, ReviewRequest(), fixture.Authority with { AllowedNetworkHosts = ["github.com"] }));
        Assert.Single(processes.Requests, request => request.Arguments.Contains("fetch"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => capture.CaptureAsync(input, ReviewRequest(), fixture.Authority));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => capture.CaptureAsync(input, ReviewRequest(), fixture.Authority with { AllowedNetworkHosts = ["different.example"] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => capture.CaptureAsync(input with { Branch = "feature:injected" }, ReviewRequest(), fixture.Authority with { AllowedNetworkHosts = ["github.com"] }));
    }

    /// <summary>Verifies the focused review boundary and its observable result.</summary>
    [Fact]
    public async Task FocusedReview_RepairsFailedDeliveryWithoutRepeatingInference()
    {
        using var fixture = new ReviewRepositoryFixture();
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
            new FocusedReviewTargetCapture(fixture.Processes, sanitizer, fixture.Cache),
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

    private sealed class RemoteReviewProcessFixture : IProcessManager
    {
        public static readonly string Revision = new('a', 40);
        public static readonly string Baseline = new('b', 40);

        public string? RewrittenUrl { get; set; }

        public List<ProcessExecutionRequest> Requests { get; } = [];

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            var args = request.Arguments;
            var result = string.Empty;
            if (args.Contains("ls-remote"))
            {
                result = RewrittenUrl ?? args[^1];
            }
            else if (args.Contains("rev-parse"))
            {
                result = args.Any(arg => arg.Contains("review-base", StringComparison.Ordinal)) ? Baseline : Revision;
            }
            else if (args.Contains("merge-base"))
            {
                result = Baseline;
            }
            else if (args.Contains("ls-tree"))
            {
                result = JsonSerializer.Serialize(new[] { $"100644 blob {new string('c', 40)}\tremote.txt" });
            }
            else if (args.Contains("cat-file"))
            {
                result = args.Contains("-s") ? "14" : "remote content";
            }
            else if (args.Contains("diff"))
            {
                result = "@@ -1 +1 @@\n-previous content\n+remote content\n";
            }

            return Task.FromResult(new ProcessExecutionResult(1, 0, result, string.Empty, false, false, false, TimeSpan.Zero));
        }
    }
}
