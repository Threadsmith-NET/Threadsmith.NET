namespace Threadsmith.Architecture.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

public static partial class RepositoryMemoryBindingTests
{
    /// <summary>Optional research survives persistence without blocking required guidance or silently disappearing.</summary>
    [Fact]
    public static async Task Coverage_OptionalResearchSurvivesCheckpointAndAllowsPublication()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await using var intelligence = await IntelligenceFixture.CreateAsync(fixture, ct);
        await using (var operations = intelligence.CreateOperations())
        await using (var run = await operations.BeginAsync(SessionId.New(), RunId.New(), fixture.InitialRoot, RepositoryIntelligenceOperation.Onboard, ct))
        {
            await run.AcceptHistoryPageAsync(0, new GitLogResult([], false) { IsCollectionPage = true }, "{}", ct);
            var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), new ToolInvocationContext
            {
                RepositoryPath = fixture.InitialRoot, RequestedBy = "model", IntelligenceRun = run, TrustLevel = RepositoryTrustLevel.TrustedRead,
            });
            var tool = new MemoriesTool(new UnusedMemories(), intelligence.Options, TestPromptLoader.Instance);
            var optional = new MemoryCoverageInput("internals", "Output", "How does the formatter allocate its buffers?", "uninvestigated", Gaps: "Buffer strategy unread.", Limitations: "Adding a command uses the public formatter; its allocation strategy does not affect that recipe.", Priority: "optional");
            var result = await tool.ExecuteAsync(new MemoriesInput("updateCoverage", Coverage: optional), context, ct);
            Assert.Equal("optional", Assert.Single(result.Value.Coverage!.Items).Priority);
            Assert.True(run.Collection!.HasOpenCoverage);
            await Assert.ThrowsAsync<ArgumentException>(() => tool.ExecuteAsync(new MemoriesInput("updateCoverage", Coverage: optional with { Limitations = string.Empty }), context, ct));
            await Assert.ThrowsAsync<ArgumentException>(() => tool.ExecuteAsync(new MemoriesInput("updateCoverage", Coverage: optional with { Priority = "unknown" }), context, ct));
            run.Collection.UpdateCoverage(new RepositoryIntelligenceCoverage
            {
                Key = "recipe", Area = "Output", Question = "How is output shared?", Status = "investigating", Gaps = "Verify the caller.", FollowUpKeys = ["internals"],
            });
            InitializeCoverageSurvey(run.Collection);
            await run.ContinueCoverageAsync("Required recipe and optional buffer research.", ct);
        }

        await using var resumedOperations = intelligence.CreateOperations();
        await using var resumed = await resumedOperations.BeginAsync(SessionId.New(), RunId.New(), fixture.InitialRoot, RepositoryIntelligenceOperation.Onboard, ct);
        await resumed.AcceptHistoryPageAsync(0, new GitLogResult([], false) { IsCollectionPage = true }, "{}", ct);
        Assert.Equal("optional", resumed.Collection!.Coverage.Single(item => item.Key == "internals").Priority);
        await Assert.ThrowsAsync<InvalidOperationException>(() => resumed.CompleteAsync(ct));
        var required = resumed.Collection.Coverage.Single(item => item.Key == "recipe");
        resumed.Collection.UpdateCoverage(required with
        {
            Status = "investigated", Gaps = string.Empty, Conclusion = "Use the formatter from the command caller.", NoMemoryReason = "Fixture-only assertion used to exercise optional checklist state.",
            Supports = [new RepositoryMemorySupport { Path = "formatter.cs", ContentHash = "hash", ObservedAt = DateTimeOffset.UtcNow }],
        });
        Assert.Null(await resumed.ContinueCoverageAsync("Required guidance complete; buffer research optional.", ct));
        var diagnostic = new TaskCompletionSource<DiagnosticObserved>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = intelligence.Events.Subscribe((item, _) =>
        {
            if (item is DiagnosticObserved { Code: "intelligence.onboarding.optional" } observed)
            {
                diagnostic.TrySetResult(observed);
            }

            return Task.CompletedTask;
        });
        await resumed.CompleteAsync(ct);
        Assert.Null(await intelligence.Store.ReadOnboardingCheckpointAsync(intelligence.Identity, ct));
        Assert.Contains("allocation strategy", (await diagnostic.Task.WaitAsync(ct)).Message, StringComparison.Ordinal);
    }

    /// <summary>A final-pass provider failure retains completed tool preparation and resumes without repeating history investigation.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task FinalCoverage_ProviderFailurePreservesOpenQuestionsAndResumes(bool hasCommit)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await HistoryGitAsync(fixture.InitialRoot, ct, "init");
        await File.WriteAllTextAsync(Path.Combine(fixture.InitialRoot, "feature.txt"), "A fixture with no reusable implementation recipe.", ct);
        if (hasCommit)
        {
            await HistoryGitAsync(fixture.InitialRoot, ct, "add", "feature.txt");
            await HistoryGitAsync(fixture.InitialRoot, ct, "commit", "-m", "fixture");
        }

        await using var intelligence = await IntelligenceFixture.CreateAsync(fixture, ct);
        await using var operations = intelligence.CreateOperations();
        var before = await intelligence.Store.GetSnapshotAsync(intelligence.Identity, [], ct);
        var registry = new ToolRegistry([
            new MemoriesTool(new UnusedMemories(), intelligence.Options, TestPromptLoader.Instance),
            new ReadFileTool(TestPromptLoader.Instance, new SecretOutputSanitizer()),
            new GitLogTool(new GitQueryService(), TestPromptLoader.Instance),
        ]);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new AllowApprovalPolicy(), intelligence.Events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance);
        var context = new ToolInvocationContext { RepositoryPath = fixture.InitialRoot, RequestedBy = "model", TrustLevel = RepositoryTrustLevel.TrustedRead, AllowedExecutables = ["git"] };
        SessionApplication CreateApplication(ScriptedSession script) => new(
            intelligence.Events,
            new FakeModelProvider(script),
            UnboundedBudget.Instance,
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            toolPipeline: pipeline,
            toolContextFactory: (_, _) => Task.FromResult(context),
            toolRegistry: registry,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            intelligenceOperations: operations,
            limits: new ExecutionLimits { MaxModelRounds = 8 });
        var first = CreateApplication(new ScriptedSession
        {
            Turns = [
                new ScriptedTurn { ToolName = "memories", ArgumentsJson = """{"action":"initializeCoverage","survey":[{"key":"feature","area":"Fixture","question":"What implementation guidance exists?","status":"uninvestigated"}]}""" },
                new ScriptedTurn { Text = "History complete; ready to publish." },
                new ScriptedTurn { ToolName = "memories", ArgumentsJson = "{\"action\":\"updateCoverage\",\"coverage\":{\"key\":\"feature\",\"area\":\"Fixture\",\"question\":\"What implementation guidance exists?\",\"status\":\"investigating\",\"gaps\":\"Read the implementation.\"}}" },
                new ScriptedTurn { Failure = ScriptFailureKind.TransientProvider },
            ],
        });
        var session = await first.HandleAsync(new CreateSessionCommand("coverage failure"), ct);
        var run = await first.HandleAsync(new SubmitRequestCommand(session, "Onboard") { IntelligenceOperation = RepositoryIntelligenceOperation.Onboard }, ct);
        await Assert.ThrowsAsync<TransientModelException>(() => first.HandleAsync(new WaitForRunCommand(run), ct));
        var checkpoint = await intelligence.Store.ReadOnboardingCheckpointAsync(intelligence.Identity, ct);
        Assert.NotNull(checkpoint);
        Assert.True(checkpoint.HistoryComplete);
        Assert.True(checkpoint.CoverageReviewStarted);
        Assert.Equal(0, checkpoint.NextHistoryPage);
        Assert.Equal("investigating", Assert.Single(checkpoint.Coverage).Status);
        Assert.Equal(before.Entries.Select(entry => entry.Id), (await intelligence.Store.GetSnapshotAsync(intelligence.Identity, [], ct)).Entries.Select(entry => entry.Id));
        var resumed = CreateApplication(new ScriptedSession
        {
            Turns = [
                new ScriptedTurn { Text = "Try to finish with the open question." },
                new ScriptedTurn { ToolName = "read_file", ArgumentsJson = "{\"path\":\"feature.txt\"}" },
                new ScriptedTurn { ToolName = "memories", ArgumentsJson = "{\"action\":\"updateCoverage\",\"coverage\":{\"key\":\"feature\",\"area\":\"Fixture\",\"question\":\"What implementation guidance exists?\",\"status\":\"investigated\",\"conclusion\":\"Only fixture text; no useful memory warranted.\",\"noMemoryReason\":\"Only fixture text; no reusable implementation.\",\"limitations\":\"No tests were run.\",\"supportingPaths\":[\"feature.txt\"]}}" },
                new ScriptedTurn { Text = "Coverage complete." },
            ],
        });
        var nextSession = await resumed.HandleAsync(new CreateSessionCommand("coverage resume"), ct);
        var nextRun = await resumed.HandleAsync(new SubmitRequestCommand(nextSession, "Resume") { IntelligenceOperation = RepositoryIntelligenceOperation.Onboard }, ct);
        Assert.True(await resumed.HandleAsync(new WaitForRunCommand(nextRun), ct));
        Assert.Null(await intelligence.Store.ReadOnboardingCheckpointAsync(intelligence.Identity, ct));
    }

    /// <summary>Unfinished work cannot be hidden in a closed item or a missing follow-up reference.</summary>
    [Fact]
    public static void Coverage_RequiresSeparateOpenFollowupsAndAllowsRealLimitations()
    {
        var legacy = System.Text.Json.JsonSerializer.Deserialize<RepositoryIntelligenceCoverage>("""
            {"Key":"legacy","Area":"Tools","Question":"How do tools execute?","Status":"investigating"}
            """);
        Assert.Equal("required", legacy!.Priority);
        var collection = new RepositoryMemoryCollection(new RepositoryMemoryReadSnapshot("repo", 1, [], [], []), true, 0);
        var item = new RepositoryIntelligenceCoverage
        {
            Key = "tools", Area = "Tools", Question = "How do tools execute?", Status = "investigated", Conclusion = "Reuse the pipeline.", NoMemoryReason = "Synthetic checklist fixture without a real implementation.",
            Gaps = "MCP integration remains unread.", Supports = [new RepositoryMemorySupport { Path = "pipeline.cs", ContentHash = "hash", ObservedAt = DateTimeOffset.UtcNow }],
        };
        Assert.Throws<ArgumentException>(() => collection.UpdateCoverage(item));
        collection.UpdateCoverage(item with { Gaps = string.Empty, Limitations = "History before requested depth excluded.", FollowUpKeys = ["mcp"] });
        Assert.True(collection.HasOpenCoverage);
        collection.UpdateCoverage(item with { Key = "mcp", Status = "investigating" });
        Assert.True(collection.HasOpenCoverage);
        collection.UpdateCoverage(item with { Key = "mcp", Gaps = string.Empty });
        InitializeCoverageSurvey(collection);
        Assert.False(collection.HasOpenCoverage);
    }
}
