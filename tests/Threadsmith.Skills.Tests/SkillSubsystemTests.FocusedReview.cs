namespace Threadsmith.Skills.Tests;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private const string EmptyFocusedReview = "{\"strengths\":[],\"architecture\":[],\"issues\":[],\"observations\":[],\"criteria\":[],\"coverage\":[]}";
    private static readonly string[] ExpectedCriteria = ["criterion-001", "AC-02"];

    /// <summary>Git text normalization must not change any hash-pinned review payload bytes.</summary>
    [Fact]
    public static void FocusedReview_PinnedAssetsUseRepositoryLineEndings()
    {
        var source = Path.GetDirectoryName(MaintainedRoot())
            ?? throw new InvalidOperationException("Maintained catalog has no parent.");
        var files = Directory.EnumerateFiles(Path.Combine(MaintainedRoot(), "review"), "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(source, "ReviewSkills"), "*", SearchOption.AllDirectories));
        foreach (var file in files)
        {
            Assert.DoesNotContain((byte)'\r', File.ReadAllBytes(file));
        }
    }

    /// <summary>Verifies the focused review boundary and its observable result.</summary>
    [Theory]
    [InlineData("{\"mode\":\"unknown\"}")]
    [InlineData("{\"mode\":\"remoteBranch\",\"branch\":\"feature\"}")]
    [InlineData("{\"mode\":\"specialInstructions\"}")]
    [InlineData("{\"paths\":[\"../outside\"]}")]
    [InlineData("{\"requirementsSource\":\"reviewTarget\"}")]
    [InlineData("{\"repository\":\"https://example.com/r.git\"}")]
    public static void FocusedReview_RejectsInvalidInput(
        string json)
    {
        Assert.ThrowsAny<Exception>(() => FocusedReviewInput.Parse(json));
        Assert.Equal("currentBranchChanges", FocusedReviewInput.Parse("{}").Mode);
    }

    /// <summary>Verifies the focused review boundary and its observable result.</summary>
    [Fact]
    public async Task FocusedReview_PrivateDiscoveryAndBindingRemainHostOnly()
    {
        var catalog = new SkillCatalog([new SkillCatalogSource(SkillScope.Maintained, MaintainedRoot(), "maintained", IsMaintained: true)]);
        var candidates = (await catalog.RefreshAsync()).Candidates;
        Assert.Equal(5, candidates.Count);
        Assert.Equal(catalog.Resolve("review").Identity, catalog.Resolve("Maintained:review").Identity);
        Assert.DoesNotContain(candidates, item => item.Metadata.SkillId.Value.Contains("private", StringComparison.Ordinal));
        Assert.ThrowsAny<Exception>(() => catalog.Resolve("review-security-private"));
        var verifier = new SkillPackageVerifier(new SkillTrustPolicySnapshot());
        var candidate = await verifier.VerifyAsync(catalog.Resolve("review"));
        var plan = ReviewPlan(candidate);
        var resolver = ReviewResolver(verifier);
        var procedures = await resolver.ResolveAsync(candidate, plan, ReviewTarget(), 1, 0);
        Assert.Equal(4, procedures.Count);
        Assert.Equal(4, procedures.Select(item => item.Binding.Role).Distinct().Count());
        Assert.All(procedures, procedure => Assert.Contains("Return exactly one JSON", procedure.Instructions, StringComparison.Ordinal));
        Assert.False(resolver.Handles(candidate with { Provenance = candidate.Provenance with { Scope = SkillScope.Repository } }, candidate.Metadata.Workflow.Steps[0]));
        Assert.False(resolver.Handles(candidate with { Provenance = candidate.Provenance with { PackageRoot = Path.GetTempPath() } }, candidate.Metadata.Workflow.Steps[0]));
        var revoked = new SkillPackageVerifier(new SkillTrustPolicySnapshot { DeniedSkillIds = new HashSet<string> { procedures[0].Binding.Package.SkillId.Value } });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ReviewResolver(revoked).ResolveAsync(candidate, plan, ReviewTarget(), 1, 0));
        Assert.DoesNotContain("private", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies the focused review boundary and its observable result.</summary>
    [Fact]
    public async Task FocusedReview_RequiresDeliveredScopedCitationsAndRuntimeEvidence()
    {
        var (candidate, plan) = await ReviewPackageAsync();
        var target = ReviewTarget() with { MergeBase = new string('b', 40), Requirements = new FocusedReviewRequirements("workspace", "requirements.md", "digest", "AC-1 runtime tests", [new FocusedReviewCriterion("AC-1", "runtime tests", 1, true)]) };
        var policy = (await ReviewResolver(new SkillPackageVerifier(new SkillTrustPolicySnapshot())).ResolveAsync(candidate, plan, target, 1, 0))[0];
        var assignment = ReviewAssignment(policy.Binding);
        Assert.Equal(EmptyFocusedReview.Length, policy.Validate(assignment, EmptyFocusedReview).Length);
        var output = JsonNode.Parse(EmptyFocusedReview)!.AsObject();
        var citation = new { path = "src/A.cs", startLine = 2, endLine = 2 };
        output["strengths"] = JsonSerializer.SerializeToNode(new[] { new { text = "Supported", evidence = new[] { citation } } });
        Assert.Throws<InvalidDataException>(() => policy.Validate(assignment, output.ToJsonString()));
        policy.RecordRead("src/A.cs", 1, 3);
        _ = policy.Validate(assignment, output.ToJsonString());
        output["criteria"] = JsonSerializer.SerializeToNode(new[] { new { criterionId = "AC-1", status = "Met", evidence = new[] { citation }, assessment = "Static claim" } });
        Assert.Throws<InvalidDataException>(() => policy.Validate(assignment, output.ToJsonString()));
        output["criteria"]![0]!["status"] = "Not assessed";
        _ = policy.Validate(assignment, output.ToJsonString());
        output["strengths"]![0]!["text"] = policy.Binding.Package.SkillId.Value;
        Assert.Throws<InvalidDataException>(() => policy.Validate(assignment, output.ToJsonString()));
        Assert.Throws<InvalidDataException>(() => policy.Validate(assignment with { FocusedReview = null }, EmptyFocusedReview));
        Assert.ThrowsAny<Exception>(() => policy.Validate(assignment, "{\"issues\":[]}"));
    }

    /// <summary>Verifies the focused review boundary and its observable result.</summary>
    [Fact]
    public static void FocusedReview_ExtractsOnlyExplicitCriteriaInOrder()
    {
        var criteria = FocusedReviewTargetCapture.ExtractCriteria("# Notes\n- not acceptance\n## Acceptance criteria\n- first condition\n| AC-02 | run tests manually |\n## Implementation\n- not acceptance\n");
        Assert.Equal(ExpectedCriteria, criteria.Select(item => item.Id));
        Assert.Equal(4, criteria[0].Line);
        Assert.True(criteria[1].RequiresExecution);
        Assert.Throws<InvalidDataException>(() => FocusedReviewTargetCapture.ExtractCriteria("AC-1 first\nAC-1 second"));
    }

    /// <summary>Verifies the focused review boundary and its observable result.</summary>
    [Fact]
    public async Task FocusedReview_RejectsDecodedPrivateInstructionsAndPrettyOrMinifiedSchemas()
    {
        var (candidate, plan) = await ReviewPackageAsync();
        var policy = (await ReviewResolver(new SkillPackageVerifier(new SkillTrustPolicySnapshot())).ResolveAsync(candidate, plan, ReviewTarget(), 1, 0))[0];
        using var schema = JsonDocument.Parse(policy.OutputSchema);
        string[] secrets =
        [
            policy.Instructions, policy.OutputSchema, JsonSerializer.Serialize(schema.RootElement),
            System.Security.SecurityElement.Escape(policy.OutputSchema), System.Security.SecurityElement.Escape(policy.Instructions),
            policy.OutputSchema.Replace("\"", "&#34;", StringComparison.Ordinal),
        ];
        foreach (var secret in secrets)
        {
            var output = JsonNode.Parse(EmptyFocusedReview)!.AsObject();
            output["observations"] = JsonSerializer.SerializeToNode(new[] { new { text = secret[..Math.Min(secret.Length, 3000)], assumption = "quoted evidence", evidence = Array.Empty<object>() } });
            var error = Assert.Throws<InvalidDataException>(() => policy.Validate(ReviewAssignment(policy.Binding), output.ToJsonString()));
            Assert.Contains("Private procedure definitions", error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>Verifies the focused review boundary and its observable result.</summary>
    [Fact]
    public static void FocusedReview_ExtractsMultilineDefinitionsWithoutTreatingReferencesAsDuplicates()
    {
        var criteria = FocusedReviewTargetCapture.ExtractCriteria("Acceptance criteria:\n- **AC-1** Supported behavior\n  must also pass a manual verification.\nThe implementation for AC-1 appears below.\nSee AC-1 for the requirement.\n");
        var criterion = Assert.Single(criteria);
        Assert.Equal("AC-1", criterion.Id);
        Assert.Equal(2, criterion.Line);
        Assert.Contains("must also pass", criterion.Text, StringComparison.Ordinal);
        Assert.True(criterion.RequiresExecution);
    }

    /// <summary>Verifies the focused review boundary and its observable result.</summary>
    [Fact]
    public async Task FocusedReview_SelectedScopeSkipsOversizedUnrelatedContentAndFreezesIgnoredRequirements()
    {
        await using var fixture = new ReviewRepositoryFixture();
        await fixture.InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "Root guidance");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, ".gitignore"), "requirements.md\n");
        var requirements = string.Join('\n', Enumerable.Range(1, 700).Select(index => $"- AC-{index}: " + new string('x', 50)));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "requirements.md"), requirements);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "unrelated"));
        var large = new string('x', 500000);
        for (var index = 0; index < 68; index++)
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, "unrelated", $"{index}.txt"), large);
        }

        var capture = new FocusedReviewTargetCapture(new SecretOutputSanitizer(), fixture.Tools, fixture.Pipeline);
        var input = FocusedReviewInput.Parse("{\"mode\":\"specialInstructions\",\"instructions\":\"Inspect selected source\",\"paths\":[\"tracked.txt\"],\"requirementsDocumentPath\":\"requirements.md\"}");
        var progress = new List<string>();
        var target = await capture.CaptureAsync(input, ReviewRequest(), fixture.Authority, reportProgress: (message, token) =>
        {
            token.ThrowIfCancellationRequested();
            progress.Add(message);
            return Task.CompletedTask;
        });
        Assert.Equal("Inspecting source revisions", progress[0]);
        Assert.Contains(progress, message => message.StartsWith("Preparing source snapshot:", StringComparison.Ordinal));
        Assert.Equal("Source snapshot ready: 2 files", progress[^1]);
        Assert.DoesNotContain(progress, message => message.Contains("tracked.txt", StringComparison.Ordinal) || message.Contains(fixture.Root, StringComparison.Ordinal));
        Assert.Equal(2, target.Files.Count);
        Assert.Contains(target.Files, file => file.Path == "AGENTS.md" && !file.InScope);
        Assert.Contains(target.Files, file => file.Path == "tracked.txt" && file.InScope);
        Assert.Equal(requirements, target.Requirements!.Content);
        Assert.Equal(700, target.Requirements.Criteria.Count);
        Assert.Equal("workspace", target.Requirements.Source);
    }

    /// <summary>Verifies the focused review boundary and its observable result.</summary>
    [Fact]
    public async Task FocusedReview_CapturedFilenamesAreLiteralGitPaths()
    {
        await using var fixture = new ReviewRepositoryFixture();
        await fixture.InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a[1].cs"), "unchanged\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a1.cs"), "original\n");
        await fixture.GitAsync("add", "--all");
        await fixture.GitAsync("commit", "-m", "literal path fixture");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "a1.cs"), "modified\n");
        var capture = new FocusedReviewTargetCapture(new SecretOutputSanitizer(), fixture.Tools, fixture.Pipeline);
        var target = await capture.CaptureAsync(FocusedReviewInput.Parse("{\"baseBranch\":\"HEAD\"}"), ReviewRequest(), fixture.Authority);
        Assert.DoesNotContain(target.Files, file => file.Path == "a[1].cs");
        Assert.NotEmpty(target.Files.Single(file => file.Path == "a1.cs").ChangedRanges);
    }

    /// <summary>Git identities cannot be rewritten by generic secret-output redaction.</summary>
    [Fact]
    public async Task FocusedReview_RejectsRedactedGitFilenameIdentity()
    {
        await using var fixture = new ReviewRepositoryFixture();
        await fixture.InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "password=secret.cs"), "source");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Processes.RunAsync(new ProcessExecutionRequest
        {
            ToolInvocationId = ToolInvocationId.New(),
            RunId = RunId.New(),
            FileName = "git",
            Arguments = ["ls-files", "-z", "--others", "--exclude-standard"],
            WorkingDirectory = fixture.Root,
            Origin = ProcessRequestOrigin.Host,
            StandardOutputFormat = ProcessStandardOutputFormat.ReviewRecords,
        }));
        var capture = new FocusedReviewTargetCapture(new SecretOutputSanitizer(), fixture.Tools, fixture.Pipeline);
        await Assert.ThrowsAsync<InvalidDataException>(() => capture.CaptureAsync(FocusedReviewInput.Parse("{\"baseBranch\":\"HEAD\"}"), ReviewRequest(), fixture.Authority));
    }

    /// <summary>Redaction cannot silently turn valid source into a different review target.</summary>
    [Fact]
    public async Task FocusedReview_ExcludesRedactedSourceAndRejectsAlteredRequirements()
    {
        await using var fixture = new ReviewRepositoryFixture();
        await fixture.InitializeAsync();
        const string source = "void Login(string password) { log(\"Login password: \" + password); Authenticate(); }";
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "dirty.cs"), source);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "committed.cs"), source);
        await fixture.GitAsync("add", "committed.cs");
        await fixture.GitAsync("commit", "-m", "redaction fixture");
        var capture = new FocusedReviewTargetCapture(new SecretOutputSanitizer(), fixture.Tools, fixture.Pipeline);
        var input = FocusedReviewInput.Parse("{\"baseBranch\":\"HEAD\"}");
        var target = await capture.CaptureAsync(input, ReviewRequest(), fixture.Authority);
        Assert.DoesNotContain(target.Files, file => file.Path is "dirty.cs" or "committed.cs");
        Assert.Contains(target.Exclusions, exclusion => exclusion.StartsWith("dirty.cs:", StringComparison.Ordinal) && exclusion.Contains("redacted", StringComparison.Ordinal));
        Assert.DoesNotContain(target.Exclusions, exclusion => exclusion.StartsWith("committed.cs:", StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "requirements.md"), "AC-1: password: synthetic-fixture-value");
        await Assert.ThrowsAsync<InvalidDataException>(() => capture.CaptureAsync(input with { RequirementsDocumentPath = "requirements.md" }, ReviewRequest(), fixture.Authority));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "AGENTS.md"), "Policy password: synthetic-fixture-value");
        await Assert.ThrowsAsync<InvalidOperationException>(() => capture.CaptureAsync(input with { Paths = ["tracked.txt"] }, ReviewRequest(), fixture.Authority));
    }

    /// <summary>Verifies the focused review boundary and its observable result.</summary>
    [Fact]
    public static void FocusedReview_CanonicalReportKeepsSectionsAndEscapesSource()
    {
        var target = ReviewTarget() with { Requirements = new FocusedReviewRequirements("workspace", "requirements.md", "digest", "AC-1", [new FocusedReviewCriterion("AC-1", "manual verification", 1, true)]) };
        var output = JsonNode.Parse(EmptyFocusedReview)!.AsObject();
        output["observations"] = JsonSerializer.SerializeToNode(new[] { new { text = "\n## injected\n<script>|", assumption = "unknown intent", evidence = Array.Empty<object>() } });
        var result = new AgentRunOutcome
        {
            Usage = new AgentResourceUsage(),
            AssignmentId = AgentAssignmentId.New(),
            ChildRunId = RunId.New(),
            Role = AgentRole.SecurityReviewer,
            Generation = 1,
            Status = AgentRunStatus.Completed,
            FocusedReviewValidated = true,
            Response = output.ToJsonString(),
            Reason = "validated",
        };
        var markdown = FocusedReviewReportFormatter.Render(target, [result], false);
        var headings = markdown.Split('\n').Where(line => line.StartsWith('#')).ToArray();
        Assert.Equal(new[] { "# Code review", "## What's good", "## Overall architectural soundness", "## Possible issues", "### P1", "### P2", "### P3", "## Observations", "## Acceptance criteria assessment" }, headings);
        Assert.Contains("Review status: partial", markdown, StringComparison.Ordinal);
        Assert.Contains("No positive verdict is assumed", markdown, StringComparison.Ordinal);
        Assert.Contains("Not assessed", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("Acceptance criteria assessment", FocusedReviewReportFormatter.Render(target with { Requirements = null }, [], false), StringComparison.Ordinal);
    }

    /// <summary>Verifies the focused review boundary and its observable result.</summary>
    [Fact]
    public async Task FocusedReview_CapturesCommittedStagedUnstagedAndUntrackedChangesWithoutMutation()
    {
        await using var fixture = new ReviewRepositoryFixture();
        await fixture.InitializeAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "committed.txt"), "new committed content\n");
        await fixture.GitAsync("add", "committed.txt");
        await fixture.GitAsync("commit", "-m", "feature commit");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "staged.txt"), "staged content\n");
        await fixture.GitAsync("add", "staged.txt");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "tracked.txt"), "edited content\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "untracked.txt"), "untracked content\n");
        var before = await fixture.GitAsync("status", "--porcelain=v2");
        var invocation = ReviewRequest();
        var capture = new FocusedReviewTargetCapture(new SecretOutputSanitizer(), fixture.Tools, fixture.Pipeline);
        var target = await capture.CaptureAsync(FocusedReviewInput.Parse("{\"baseBranch\":\"main\"}"), invocation, fixture.Authority);
        Assert.All(new[] { "committed.txt", "staged.txt", "tracked.txt", "untracked.txt" }, path => Assert.Contains(target.Files, file => file.Path == path && file.InScope));
        Assert.NotNull(target.MergeBase);
        Assert.Equal(before, await fixture.GitAsync("status", "--porcelain=v2"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "tracked.txt"), "later live drift\n");
        Assert.Equal("edited content\n", target.Files.Single(file => file.Path == "tracked.txt").Content);
        await Assert.ThrowsAsync<InvalidDataException>(() => capture.CaptureAsync(FocusedReviewInput.Parse("{}"), invocation, fixture.Authority));
        await Assert.ThrowsAsync<InvalidDataException>(() => capture.CaptureAsync(FocusedReviewInput.Parse("{\"mode\":\"specialInstructions\",\"instructions\":\"inspect\",\"paths\":[\"missing\"]}"), invocation, fixture.Authority));
    }

    /// <summary>Verifies the focused review boundary and its observable result.</summary>
    [Fact]
    public async Task FocusedReview_InboxPublicationIsConfinedAtomicAndIdempotent()
    {
        await using var fixture = new ReviewRepositoryFixture();
        var target = ReviewTarget() with { InvokingRepository = fixture.Root };
        Assert.Null(FocusedReviewReportWriter.ResolveInbox(target, fixture.Authority));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".inbox")));
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".inbox"));
        var path = Path.Combine(fixture.Root, ".inbox", "review-test.md");
        const string markdown = "# Code review\n\nUTF-8: café\n";
        var digest = FocusedReviewTargetCapture.Hash(Encoding.UTF8.GetBytes(markdown));
        await FocusedReviewReportWriter.PublishAsync(path, markdown, digest, target, fixture.Authority);
        await FocusedReviewReportWriter.PublishAsync(path, markdown, digest, target, fixture.Authority);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!));
        Assert.Equal(markdown, await File.ReadAllTextAsync(path));
        await Assert.ThrowsAsync<IOException>(() => FocusedReviewReportWriter.PublishAsync(path, "other", FocusedReviewTargetCapture.Hash(Encoding.UTF8.GetBytes("other")), target, fixture.Authority));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => FocusedReviewReportWriter.PublishAsync(Path.Combine(fixture.Root, "review-escape.md"), markdown, digest, target, fixture.Authority));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var cancelledPath = Path.Combine(fixture.Root, ".inbox", "review-cancelled.md");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FocusedReviewReportWriter.PublishAsync(cancelledPath, markdown, digest, target, fixture.Authority, cancelled.Token));
        Assert.False(File.Exists(cancelledPath));
    }

    /// <summary>Verifies the focused review boundary and its observable result.</summary>
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 3)]
    [InlineData(false, false, 4)]
    [InlineData(true, false, 4)]
    public async Task FocusedReview_RealWorkflowJoinsAndProjectsWithoutGenericProcedureCalls(
        bool inbox,
        bool defaultBudget,
        int failedReviewers)
    {
        await using var fixture = new ReviewRepositoryFixture();
        await fixture.InitializeAsync();
        if (inbox)
        {
            Directory.CreateDirectory(Path.Combine(fixture.Root, ".inbox"));
        }

        var catalog = new SkillCatalog([new SkillCatalogSource(SkillScope.Maintained, MaintainedRoot(), "maintained", IsMaintained: true)]);
        await catalog.RefreshAsync();
        var verifier = new SkillPackageVerifier(new SkillTrustPolicySnapshot());
        var executor = new ReviewExecutorStub { FailedReviewers = failedReviewers };
        var sanitizer = new SecretOutputSanitizer();
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Add(item);
            return Task.CompletedTask;
        });
        var handler = new FocusedReviewWorkflow(
            ReviewResolver(verifier),
            new FocusedReviewTargetCapture(sanitizer, fixture.Tools, fixture.Pipeline),
            executor,
            (_, _) => Task.FromResult(fixture.Authority),
            fixture.State,
            events: events);
        var state = new InMemorySkillStateStore();
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
        var result = await workflow.InvokeAsync(ReviewRequest() with
        {
            InputJson = "{\"mode\":\"specialInstructions\",\"instructions\":\"Inspect captured source\"}",
            UseDefaultBudget = defaultBudget,
            HostBudget = new SkillBudget { ModelTurns = 12, ToolCalls = 20 },
        });
        Assert.Equal(defaultBudget ? 64 : 12, executor.PreparedBudget!.ModelTurns);
        Assert.Equal(defaultBudget ? 128 : 20, executor.PreparedBudget.ToolCalls);
        var expectedStatus = failedReviewers == 4 ? SkillInvocationStatus.Failed : SkillInvocationStatus.Completed;
        Assert.Equal(expectedStatus, result.Status);
        Assert.Empty(result.HostActions);
        var checkpoints = observed.OfType<SkillWorkflowCheckpointWritten>().ToArray();
        Assert.Equal(SkillInvocationStatus.Accepted, checkpoints[0].Status);
        Assert.Equal(expectedStatus, checkpoints[^1].Status);
        Assert.DoesNotContain(checkpoints, item => item.Status == SkillInvocationStatus.AwaitingHost);
        Assert.All(checkpoints[1..^1], item => Assert.Equal(SkillInvocationStatus.Running, item.Status));
        Assert.Contains(observed.OfType<SkillInvocationProgressObserved>(), item => item.Message == "Running reviewers");
        Assert.Equal(4, executor.ExecutedRoles.Count);
        var delivery = Assert.IsType<FocusedReviewDelivery>(result.ReviewDelivery);
        Assert.Equal(failedReviewers == 4 ? "failed" : failedReviewers > 0 ? "partial" : "complete", delivery.Status);
        Assert.Equal(inbox ? "inbox" : "console", delivery.DeliveryMode);
        Assert.Equal(inbox, delivery.Markdown is null);
        Assert.DoesNotContain("private", result.OutputJson ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(delivery, (await state.GetCheckpointAsync(result.InvocationId))!.ReviewDelivery);
        if (inbox)
        {
            Assert.StartsWith("# Code review", await File.ReadAllTextAsync(delivery.SavedPath!), StringComparison.Ordinal);
        }
        else
        {
            Assert.StartsWith("# Code review", delivery.Markdown, StringComparison.Ordinal);
        }
    }

    private static FocusedReviewPrivateResolver ReviewResolver(ISkillPackageVerifier verifier) => new(
        Path.GetDirectoryName(MaintainedRoot())!,
        verifier,
        new SkillContentLoader(new SecretOutputSanitizer()),
        new BoundedJsonSchemaValidator());

    private static async Task<(SkillCatalogCandidate Candidate, SkillInvocationPlan Plan)> ReviewPackageAsync()
    {
        var catalog = new SkillCatalog([new SkillCatalogSource(SkillScope.Maintained, MaintainedRoot(), "maintained", IsMaintained: true)]);
        await catalog.RefreshAsync();
        var candidate = await new SkillPackageVerifier(new SkillTrustPolicySnapshot()).VerifyAsync(catalog.Resolve("review"));
        return (candidate, ReviewPlan(candidate));
    }

    private static SkillInvocationPlan ReviewPlan(SkillCatalogCandidate candidate) => new()
    {
        Request = ReviewRequest(),
        Package = candidate.Identity,
        Scope = SkillScope.Maintained,
        Verification = candidate.Verification,
        Compatibility = new CompatibleEvaluator().Evaluate(candidate, ReviewRequest()),
        EffectiveBudget = new SkillBudget(),
    };

    private static SkillInvocationRequest ReviewRequest() => new()
    {
        InvocationId = SkillInvocationId.New(),
        SessionId = SessionId.New(),
        RunId = RunId.New(),
        Selector = "review",
        InputJson = "{}",
        Trust = RepositoryTrustLevel.TrustedRead,
        Phase = RunPhase.EvidenceCollection,
        HostBudget = new SkillBudget(),
    };

    private static FocusedReviewTarget ReviewTarget() => new()
    {
        Mode = "specialInstructions",
        Repository = "repo",
        Revision = new string('a', 40),
        Identity = "captured-target",
        Files = [new FocusedReviewFile("src/A.cs", "file-digest", "first\nsecond\nthird", true, [new FocusedReviewRange(2, 2)])],
    };

    private static AgentAssignment ReviewAssignment(FocusedReviewBinding binding) => new()
    {
        AssignmentId = AgentAssignmentId.New(),
        ChildRunId = RunId.New(),
        Role = binding.Role,
        FocusedReview = binding,
        Mode = AgentRunMode.ReadOnlyReview,
        Objective = "inspect",
        OutputSchema = "focused-review/1",
        StoppingCondition = "done",
        Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
        Scope = new AgentAssignmentScope(),
        Budget = new AgentResourceBudget(),
        Policy = new AgentPolicySnapshot { ModelSelectionRationale = "test", ContextPolicyVersion = "test", ToolPolicyVersion = "test" },
    };

    private sealed class RejectReviewProcedureRunner : ISkillProcedureRunner
    {
        public Task<SkillProcedureResult> RunAsync(
            SkillInvocationPlan plan,
            SkillWorkflowStep step,
            int iteration,
            IReadOnlyList<SkillContextSegment> content,
            string inputJson,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Focused review must not start a generic root or nested procedure model loop.");
    }

    private sealed class ReviewExecutorStub : IFocusedReviewExecutor
    {
        public Task PreflightAsync(SkillInvocationRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public int FailedReviewers { get; init; }

        public SkillBudget? PreparedBudget { get; private set; }

        public List<AgentRole> ExecutedRoles { get; } = [];

        public Task<IReadOnlyList<DelegationPlan>> PrepareAsync(
            SkillInvocationPlan invocation,
            FocusedReviewTarget target,
            IReadOnlyList<IFocusedReviewCompletionPolicy> procedures,
            CancellationToken cancellationToken = default)
        {
            PreparedBudget = invocation.EffectiveBudget;
            IReadOnlyList<DelegationPlan> plans = [new DelegationPlan
            {
                DelegationId = DelegationId.New(), Assignments = procedures.Select(procedure => ReviewAssignment(procedure.Binding)).ToArray(),
                AcceptedAt = DateTimeOffset.UtcNow, ParentBudget = new AgentResourceBudget(),
                Provenance = new DelegationProvenance
                {
                    SessionId = invocation.Request.SessionId, ParentRunId = invocation.Request.RunId,
                    RepositoryIdentity = target.Repository, BaselineIdentity = target.Identity, WorkspaceId = WorkspaceId.New(), ReviewInvocationId = invocation.Request.InvocationId,
                },
            }
            ];
            return Task.FromResult(plans);
        }

        public Task<IReadOnlyList<AgentRunOutcome>> ExecuteAsync(
            SkillInvocationPlan invocation,
            DelegationPlan plan,
            FocusedReviewTarget target,
            IReadOnlyList<IFocusedReviewCompletionPolicy> procedures,
            bool restoreOnly,
            CancellationToken cancellationToken = default)
        {
            Assert.False(restoreOnly);
            IReadOnlyList<AgentRunOutcome> outcomes = plan.Assignments.Select(
                assignment =>
            {
                ExecutedRoles.Add(assignment.Role);
                var response = procedures.Single(procedure => procedure.Binding.Role == assignment.Role).Validate(assignment, EmptyFocusedReview);
                return new AgentRunOutcome
                {
                    Usage = new AgentResourceUsage(),
                    AssignmentId = assignment.AssignmentId,
                    ChildRunId = assignment.ChildRunId,
                    Role = assignment.Role,
                    Generation = assignment.FocusedReview!.Generation,
                    Status = ExecutedRoles.Count <= FailedReviewers ? AgentRunStatus.Failed : AgentRunStatus.Completed,
                    FocusedReviewValidated = ExecutedRoles.Count > FailedReviewers,
                    Response = ExecutedRoles.Count <= FailedReviewers ? null : response,
                    Reason = ExecutedRoles.Count <= FailedReviewers ? "provider unavailable" : "validated",
                };
            }).ToArray();
            return Task.FromResult(outcomes);
        }
    }

    private sealed class ReviewRepositoryFixture : IDisposable, IAsyncDisposable
    {
        private readonly string _container = Path.Combine(Path.GetTempPath(), "threadsmith-focused-review-" + Guid.NewGuid().ToString("N"));

        public ReviewRepositoryFixture(Func<IProcessManager, IProcessManager>? fetchProcesses = null)
        {
            Root = Path.Combine(_container, "repo");
            Cache = Path.Combine(_container, "cache");
            State = Path.Combine(_container, "state");
            Directory.CreateDirectory(Root);
            Processes = new ProcessManager(new SecretOutputSanitizer(), NullLogger<ProcessManager>.Instance);
            Tools = new ToolRegistry([new GitFetchTool(fetchProcesses?.Invoke(Processes) ?? Processes, Cache, TestPromptLoader.Instance), new GitShowTool(new GitQueryService(), TestPromptLoader.Instance), new GitDiffTool(new GitQueryService(), TestPromptLoader.Instance), new GitBranchComparisonTool(new GitQueryService(), TestPromptLoader.Instance), new ReadFileTool(TestPromptLoader.Instance)]);
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

        public IToolRegistry Tools { get; }

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
