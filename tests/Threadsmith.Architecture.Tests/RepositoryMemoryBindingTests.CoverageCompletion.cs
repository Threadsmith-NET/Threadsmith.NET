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
    /// <summary>Resume publishes completed unchanged evidence without inference and reopens changed or missing sources.</summary>
    [Theory]
    [InlineData("unchanged")]
    [InlineData("changed")]
    [InlineData("missing")]
    public static async Task CompletedCoverage_ResumeChecksEvidenceBeforeModelAdmission(string sourceState)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await using var intelligence = await IntelligenceFixture.CreateAsync(fixture, ct);
        var path = Path.Combine(fixture.InitialRoot, "feature.txt");
        await File.WriteAllTextAsync(path, "Inspected fixture content.", ct);
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path, ct)));
        await using var operations = intelligence.CreateOperations();
        await using (var preparation = await operations.BeginAsync(SessionId.New(), RunId.New(), fixture.InitialRoot, RepositoryIntelligenceOperation.Onboard, ct))
        {
            await preparation.AcceptHistoryPageAsync(0, new GitLogResult([], false) { IsCollectionPage = true }, "{}", ct);
            var collection = preparation.Collection!;
            var added = await intelligence.Store.ForCollection(collection).AddAsync(
                intelligence.Identity,
                new RepositoryMemoryWrite
                {
                    Text = "Reuse the inspected implementation.",
                    Origin = RepositoryMemoryOrigin.Model,
                    Ownership = RepositoryMemoryOwnership.Intelligence,
                    Concepts = ["output", "tools"],
                    Supports = [new RepositoryMemorySupport { Path = "feature.txt", ContentHash = hash, ObservedAt = DateTimeOffset.UtcNow }],
                },
                new TextEmbeddingModelDescriptor("fixture", 1, 256),
                new TextEmbeddingResult(new float[] { 1 }, 5, false),
                intelligence.Options.CaptureCurrent(),
                ct);
            collection.Accept(new RepositoryMemoryOperationResult("added", added.Entry!.Id, added.Entry, [], []));
            collection.InitializeSurvey([new RepositoryIntelligenceCoverage { Key = "feature", Area = "Feature", Question = "What guidance applies?", Status = "uninvestigated" }]);
            collection.UpdateCoverage(collection.Coverage[0] with
            {
                Status = "investigated",
                Conclusion = "Inspected the fixture.",
                MemoryIds = [added.Entry.Id],
                Supports = [new RepositoryMemorySupport { Path = "feature.txt", ContentHash = hash, ObservedAt = DateTimeOffset.UtcNow }],
            });
            await preparation.ContinueCoverageAsync("Investigation complete.", ct);
        }

        if (sourceState == "changed")
        {
            await File.WriteAllTextAsync(path, "Changed content.", ct);
        }
        else if (sourceState == "missing")
        {
            File.Delete(path);
        }

        var model = new CallbackModel(() => throw new InvalidOperationException("Model revalidation required."));
        var registry = new ToolRegistry([new MemoriesTool(new UnusedMemories(), intelligence.Options, TestPromptLoader.Instance), new ReadFileTool(TestPromptLoader.Instance, new SecretOutputSanitizer()), new GitLogTool(new GitQueryService(timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)), TestPromptLoader.Instance)]);
        var application = new SessionApplication(
            intelligence.Events,
            model,
            UnboundedBudget.Instance,
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            toolPipeline: new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new AllowApprovalPolicy(), intelligence.Events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)),
            toolContextFactory: (_, _) => Task.FromResult(new ToolInvocationContext { RepositoryPath = fixture.InitialRoot, RequestedBy = "test", TrustLevel = RepositoryTrustLevel.TrustedRead, AllowedExecutables = ["git"] }),
            toolRegistry: registry,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            intelligenceOperations: operations);
        var session = await application.HandleAsync(new CreateSessionCommand("resume completed"), ct);
        var run = await application.HandleAsync(new SubmitRequestCommand(session, "Resume onboarding") { IntelligenceOperation = RepositoryIntelligenceOperation.Onboard }, ct);
        if (sourceState == "unchanged")
        {
            Assert.True(await application.HandleAsync(new WaitForRunCommand(run), ct));
            Assert.Equal(0, model.Requests);
            Assert.Null(await intelligence.Store.ReadOnboardingCheckpointAsync(intelligence.Identity, ct));
            var published = Assert.Single((await intelligence.Store.GetSnapshotAsync(intelligence.Identity, [], ct)).Entries);
            Assert.Equal("Reuse the inspected implementation.", published.Text);
            Assert.Equal(new[] { "output", "tools" }, published.Concepts);
            Assert.False(Assert.Single(published.Supports).IsUnknown);
            Assert.Equal(1f, published.Embedding.Span[0]);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => application.HandleAsync(new WaitForRunCommand(run), ct));
            Assert.Equal("Model revalidation required.", error.Message);
            Assert.Equal(1, model.Requests);
            Assert.NotNull(await intelligence.Store.ReadOnboardingCheckpointAsync(intelligence.Identity, ct));
        }
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
            Key = "tools",
            Area = "Tools",
            Question = "How do tools execute?",
            Status = "investigated",
            Conclusion = "Reuse the pipeline.",
            NoMemoryReason = "Synthetic checklist fixture without a real implementation.",
            Gaps = "MCP integration remains unread.",
            Supports = [new RepositoryMemorySupport { Path = "pipeline.cs", ContentHash = "hash", ObservedAt = DateTimeOffset.UtcNow }],
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
