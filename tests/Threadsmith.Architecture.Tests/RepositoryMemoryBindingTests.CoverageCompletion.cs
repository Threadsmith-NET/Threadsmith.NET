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
                new ScriptedTurn { ToolName = "memories", ArgumentsJson = "{\"action\":\"updateCoverage\",\"coverage\":{\"key\":\"feature\",\"area\":\"Fixture\",\"question\":\"What implementation guidance exists?\",\"status\":\"investigated\",\"conclusion\":\"Only fixture text; no useful memory warranted.\",\"limitations\":\"No tests were run.\",\"supportingPaths\":[\"feature.txt\"]}}" },
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
        var collection = new RepositoryMemoryCollection(new RepositoryMemoryReadSnapshot("repo", 1, [], [], []), true, 0);
        var item = new RepositoryIntelligenceCoverage
        {
            Key = "tools", Area = "Tools", Question = "How do tools execute?", Status = "investigated", Conclusion = "Reuse the pipeline.",
            Gaps = "MCP integration remains unread.", Supports = [new RepositoryMemorySupport { Path = "pipeline.cs", ContentHash = "hash", ObservedAt = DateTimeOffset.UtcNow }],
        };
        Assert.Throws<ArgumentException>(() => collection.UpdateCoverage(item));
        collection.UpdateCoverage(item with { Gaps = string.Empty, Limitations = "History before requested depth excluded.", FollowUpKeys = ["mcp"] });
        Assert.True(collection.HasOpenCoverage);
        collection.UpdateCoverage(item with { Key = "mcp", Status = "investigating" });
        Assert.True(collection.HasOpenCoverage);
        collection.UpdateCoverage(item with { Key = "mcp", Gaps = string.Empty });
        Assert.False(collection.HasOpenCoverage);
    }
}
