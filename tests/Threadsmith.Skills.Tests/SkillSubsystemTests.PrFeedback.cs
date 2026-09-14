namespace Threadsmith.Skills.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>Verifies a concrete PR review regression through the existing implementation.</summary>
    [Theory]
    [InlineData("Acceptance criteria:")]
    [InlineData("## Acceptance criteria")]
    public static void FocusedReview_AcceptanceSectionStopsAtNextHeading(string heading)
    {
        var criteria = FocusedReviewTargetCapture.ExtractCriteria(heading + "\n- first\n## Implementation\n- unrelated\n");
        Assert.Equal("- first", Assert.Single(criteria).Text);
    }

    /// <summary>Verifies a concrete PR review regression through the existing implementation.</summary>
    [Fact]
    public static void FocusedReview_ReportPreservesDifferentAssessmentsAndDeduplicatesExactMatches()
    {
        var target = ReviewTarget();
        static AgentRunOutcome Outcome(AgentRole role, bool variant)
        {
            var output = JsonNode.Parse(EmptyFocusedReview)!.AsObject();
            var issue = new
            {
                title = "Shared defect", priority = "P2", confidence = variant ? 0.6 : 0.9,
                location = new { path = "src/A.cs", startLine = 1, endLine = variant ? 2 : 1 },
                trigger = "Same trigger", consequence = "Same consequence", recommendation = "Same remedy",
                uncertainty = variant ? "Second caveat" : "First caveat",
                evidence = new[] { new { path = "src/A.cs", startLine = variant ? 2 : 1, endLine = variant ? 2 : 1 } },
                details = new { mechanism = variant ? "Second <mechanism>" : "First mechanism", prerequisite = variant ? "Second prerequisite" : "First prerequisite" },
            };
            output["issues"] = JsonSerializer.SerializeToNode(new[] { issue });
            return new AgentRunOutcome
            {
                Usage = new AgentResourceUsage(), AssignmentId = AgentAssignmentId.New(), ChildRunId = RunId.New(), Role = role,
                Generation = 1, Status = AgentRunStatus.Completed, FocusedReviewValidated = true, Response = output.ToJsonString(), Reason = "validated",
            };
        }

        var report = FocusedReviewReportFormatter.Render(
            target,
            [Outcome(AgentRole.SecurityReviewer, false), Outcome(AgentRole.TestReviewer, true), Outcome(AgentRole.ArchitectureReviewer, false)],
            false);
        Assert.Equal(2, report.Split("Shared defect", StringSplitOptions.None).Length - 1);
        foreach (var detail in new[] { "Confidence: 0.9", "Confidence: 0.6", "First caveat", "Second caveat", "First mechanism", "Second &lt;mechanism&gt;", "First prerequisite", "Second prerequisite", "src/A.cs:L1-L1", "src/A.cs:L2-L2", "SecurityReviewer", "TestReviewer", "ArchitectureReviewer" })
        {
            Assert.Contains(detail, report, StringComparison.Ordinal);
        }
    }

    /// <summary>Verifies a concrete PR review regression through the existing implementation.</summary>
    [Fact]
    public async Task FocusedReview_LocalChangeSkipsLargeUnchangedSourcesAndKeepsContext()
    {
        await using var fixture = new ReviewRepositoryFixture();
        await fixture.InitializeAsync();
        var text = new string('x', 400 * 1024);
        for (var index = 0; index < 85; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, $"unchanged-{index}.txt"), index + text);
        }

        Directory.CreateDirectory(Path.Combine(fixture.Root, "src"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "src", "changed.cs"), "before\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "src", "AGENTS.md"), "Nested instructions");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "requirements.md"), "AC-1: Static requirement");
        await fixture.GitAsync("add", "--all");
        await fixture.GitAsync("commit", "-m", "large baseline");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "src", "changed.cs"), "after\n");
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, ".git", "info", "exclude"), "\nAGENTS.md\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "Ignored root instructions");
        var capture = new FocusedReviewTargetCapture(new SecretOutputSanitizer(), fixture.Tools, fixture.Pipeline);
        var input = new FocusedReviewInput { BaseBranch = "HEAD", RequirementsDocumentPath = "requirements.md", RequirementsSource = "reviewTarget" };
        foreach (var paths in new IReadOnlyList<string>[] { Array.Empty<string>(), new[] { "src/changed.cs" } })
        {
            fixture.ToolEvents.Clear();
            var target = await capture.CaptureAsync(input with { Paths = paths }, ReviewRequest(), fixture.Authority);
            Assert.Equal(new[] { "AGENTS.md", "requirements.md", "src/AGENTS.md", "src/changed.cs" }, target.Files.Select(file => file.Path));
            Assert.True(target.Files.Single(file => file.Path == "src/changed.cs").InScope);
            Assert.Single(target.Requirements!.Criteria);
            Assert.InRange(fixture.ToolEvents.OfType<ToolInvocationStarted>().Count(), 1, 15);
        }
    }

    /// <summary>Verifies a concrete PR review regression through the existing implementation.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task FocusedReview_ModeOnlyChangesRemainInFrozenScope(bool remote, bool commit)
    {
        LocalRemoteProcessFixture? processes = null;
        await using var fixture = new ReviewRepositoryFixture(inner => processes = new LocalRemoteProcessFixture(inner));
        processes!.RemoteRoot = fixture.Root;
        await fixture.InitializeAsync();
        await fixture.GitAsync("config", "core.fileMode", "false");
        await fixture.GitAsync("update-index", "--chmod=+x", "tracked.txt");
        if (commit)
        {
            await fixture.GitAsync("commit", "-m", "executable bit");
        }

        var capture = new FocusedReviewTargetCapture(new SecretOutputSanitizer(), fixture.Tools, fixture.Pipeline);
        var input = new FocusedReviewInput { BaseBranch = "main", Mode = remote ? "remoteBranch" : "currentBranchChanges", Repository = remote ? "https://example.invalid/review.git" : null, Branch = remote ? "feature" : null };
        var target = await capture.CaptureAsync(input, ReviewRequest(), fixture.Authority with { AllowedNetworkHosts = ["example.invalid"] });
        var file = Assert.Single(target.Files);
        Assert.True(file.InScope);
        Assert.NotEmpty(file.ChangedRanges);
        Assert.Equal(file.BaselineContent, file.Content);
        Assert.Equal("old mode 100644; new mode 100755", file.ModeChange);
    }

    /// <summary>Verifies a concrete PR review regression through the existing implementation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FocusedReview_ScpSshUsesOrdinaryFetchAndHostAuthority(bool named)
    {
        LocalRemoteProcessFixture? processes = null;
        await using var fixture = new ReviewRepositoryFixture(inner => processes = new LocalRemoteProcessFixture(inner));
        processes!.RemoteRoot = fixture.Root;
        await fixture.InitializeAsync();
        const string repository = "git@example.invalid:org/review.git";
        await fixture.GitAsync("remote", "add", "origin", repository);
        var capture = new FocusedReviewTargetCapture(new SecretOutputSanitizer(), fixture.Tools, fixture.Pipeline);
        var input = new FocusedReviewInput { Mode = "remoteBranch", Repository = named ? "origin" : repository, Branch = "feature" };
        var target = await capture.CaptureAsync(input, ReviewRequest(), fixture.Authority with { AllowedNetworkHosts = ["example.invalid"] });
        Assert.Equal("ssh://example.invalid/org/review.git", target.Repository);
        Assert.Single(target.Files);
        Assert.Contains(repository, Assert.Single(processes.Requests, request => request.Arguments.Contains("fetch")).Arguments);
        await Assert.ThrowsAsync<InvalidDataException>(() => capture.CaptureAsync(input, ReviewRequest(), fixture.Authority));
        Assert.Single(processes.Requests, request => request.Arguments.Contains("fetch"));
    }

    /// <summary>Verifies a concrete PR review regression through the existing implementation.</summary>
    [Fact]
    public async Task FocusedReview_RemoteWorkflowIgnoresLocalJunctionAndRechecksProhibitedPathsOnResume()
    {
        await using var remote = new ReviewRepositoryFixture();
        await remote.InitializeAsync();
        Directory.CreateDirectory(Path.Combine(remote.Root, "foreign"));
        await File.WriteAllTextAsync(Path.Combine(remote.Root, "foreign", "code.cs"), "Remote source");
        await remote.GitAsync("add", "--all");
        await remote.GitAsync("commit", "-m", "remote change");
        await using var fixture = new ReviewRepositoryFixture(inner => new LocalRemoteProcessFixture(inner) { RemoteRoot = remote.Root });
        await fixture.InitializeAsync();
        var link = Path.Combine(fixture.Root, "foreign");
        if (OperatingSystem.IsWindows())
        {
            var result = await fixture.Processes.RunAsync(new ProcessExecutionRequest
            {
                ToolInvocationId = ToolInvocationId.New(), RunId = RunId.New(), FileName = "cmd.exe",
                WorkingDirectory = fixture.Root, Origin = ProcessRequestOrigin.Host,
                Arguments = ["/c", "mklink", "/J", link, remote.Root],
            });
            Assert.Equal(0, result.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(link, remote.Root);
        }

        try
        {
            var (candidate, initialPlan) = await ReviewPackageAsync();
            var input = new FocusedReviewInput { Mode = "remoteBranch", Repository = "https://example.invalid/review.git", Branch = "feature", BaseBranch = "main" };
            var plan = initialPlan with { Request = initialPlan.Request with { InputJson = JsonSerializer.Serialize(input, FocusedReviewInput.JsonOptions) } };
            var authority = fixture.Authority with { AllowedNetworkHosts = ["example.invalid"] };
            var executor = new ReviewExecutorStub();
            var handler = new FocusedReviewWorkflow(
                ReviewResolver(new SkillPackageVerifier(new SkillTrustPolicySnapshot())),
                new FocusedReviewTargetCapture(new SecretOutputSanitizer(), fixture.Tools, fixture.Pipeline),
                executor,
                (_, _) => Task.FromResult(authority),
                fixture.State);
            var checkpoint = new SkillWorkflowCheckpoint
            {
                WorkflowId = SkillWorkflowId.New(), InvocationId = plan.Request.InvocationId, SessionId = plan.Request.SessionId,
                RunId = plan.Request.RunId, Package = plan.Package, InputJson = plan.Request.InputJson,
                EffectiveBudget = plan.EffectiveBudget, NextAction = "review", RecordedAt = DateTimeOffset.UtcNow,
            };
            var delivery = JsonSerializer.Deserialize<FocusedReviewDelivery>(await handler.ExecuteAsync(candidate, plan, checkpoint), FocusedReviewInput.JsonOptions)!;
            Assert.Equal("complete", delivery.Status);
            Assert.Equal(4, executor.ExecutedRoles.Count);
            authority = authority with { ProhibitedPaths = ["foreign/**"] };
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => handler.ExecuteAsync(candidate, plan, checkpoint));
            Assert.Equal(4, executor.ExecutedRoles.Count);
            var local = ReviewTarget() with { Files = [new FocusedReviewFile("foreign/code.cs", "digest", "source", true, [])] };
            Assert.Throws<UnauthorizedAccessException>(() => ReviewPathAccess.ValidateTarget(local, fixture.Authority));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    /// <summary>Generated context candidates do not prove an explicit target exists, while real ignored instructions remain eligible.</summary>
    [Theory]
    [InlineData("specialInstructions")]
    [InlineData("currentBranchChanges")]
    public async Task FocusedReview_ExplicitInstructionScopeMustExist(string mode)
    {
        await using var fixture = new ReviewRepositoryFixture();
        await fixture.InitializeAsync();
        var capture = new FocusedReviewTargetCapture(new SecretOutputSanitizer(), fixture.Tools, fixture.Pipeline);
        var input = new FocusedReviewInput { Mode = mode, BaseBranch = mode == "currentBranchChanges" ? "main" : null, Instructions = "Inspect instructions", Paths = ["AGENTS.md"] };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => capture.CaptureAsync(input, ReviewRequest(), fixture.Authority));
        Assert.Contains("requested review path does not exist", error.Message, StringComparison.Ordinal);
        await File.AppendAllTextAsync(Path.Combine(fixture.Root, ".git", "info", "exclude"), "\nAGENTS.md\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "Existing ignored guidance");
        var target = await capture.CaptureAsync(input, ReviewRequest(), fixture.Authority);
        Assert.Equal("Existing ignored guidance", Assert.Single(target.Files).Content);
    }
}
