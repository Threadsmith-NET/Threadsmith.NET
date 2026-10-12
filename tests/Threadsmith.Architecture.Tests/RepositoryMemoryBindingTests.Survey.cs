namespace Threadsmith.Architecture.Tests;

using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Initial survey completeness and published guidance remain distinct enforced contracts.</summary>
public static partial class RepositoryMemoryBindingTests
{
    /// <summary>Coverage cannot remain complete after its accepted assertion leaves preparation.</summary>
    [Fact]
    public static async Task Coverage_links_require_accepted_surviving_assertions()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await using var intelligence = await IntelligenceFixture.CreateAsync(fixture, ct);
        var baseline = await intelligence.Store.GetSnapshotAsync(intelligence.Identity, [], ct);
        var entry = Assert.Single(baseline.Entries);
        var collection = new RepositoryMemoryCollection(baseline, true, 0);
        var area = new RepositoryIntelligenceCoverage { Key = "fixture", Area = "Fixture", Question = "What guidance is reusable?", Status = "uninvestigated" };
        collection.InitializeSurvey([area]);
        var closed = area with
        {
            Status = "investigated", Conclusion = "The accepted assertion records this recipe.", MemoryIds = [entry.Id],
            Supports = [new RepositoryMemorySupport { Path = "fixture.txt", ContentHash = new string('a', 64), ObservedAt = DateTimeOffset.UtcNow }],
        };
        Assert.Throws<ArgumentException>(() => collection.UpdateCoverage(closed));
        collection.Accept(new RepositoryMemoryOperationResult("unchanged", entry.Id, entry, [], []));
        collection.UpdateCoverage(closed);
        Assert.False(collection.HasOpenCoverage);
        collection.SetPrepared([]);
        Assert.True(collection.HasOpenCoverage);
        Assert.Throws<ArgumentException>(() => collection.UpdateCoverage(closed));
    }

    /// <summary>A rejected survey creates no partial map; closing one area cannot erase another.</summary>
    [Fact]
    public static void Survey_is_atomic_preserves_area_identity_and_requires_dispositions()
    {
        var collection = new RepositoryMemoryCollection(new("repo", 0, [], [], []), true, 0);
        var first = new RepositoryIntelligenceCoverage { Key = "tools", Area = "Tools", Question = "How are tools added?", Status = "uninvestigated" };
        var second = first with { Key = "providers", Area = "Providers", Question = "How are adapters added?" };
        Assert.Throws<ArgumentException>(() => collection.InitializeSurvey([first, second with { Question = string.Empty }]));
        Assert.Empty(collection.Coverage);
        Assert.Empty(collection.InitialSurvey);
        collection.InitializeSurvey([first, second]);
        Assert.Throws<InvalidOperationException>(() => collection.InitializeSurvey([first]));
        Assert.Throws<ArgumentException>(() => collection.UpdateCoverage(first with { Question = "Only inspect one tool." }));
        Assert.Throws<ArgumentException>(() => collection.UpdateCoverage(second with { Priority = "optional", Limitations = "Finish early." }));
        var closed = first with
        {
            Status = "investigated", Conclusion = "The fixture has no reusable implementation.",
            Supports = [new RepositoryMemorySupport { Path = "fixture.txt", ContentHash = "hash", ObservedAt = DateTimeOffset.UtcNow }],
        };
        Assert.Throws<ArgumentException>(() => collection.UpdateCoverage(closed));
        collection.UpdateCoverage(closed with { NoMemoryReason = "Only a placeholder declaration exists in this fixture." });
        Assert.True(collection.HasOpenCoverage);
        collection.UpdateCoverage(second with { Status = "outOfScope", Conclusion = "This is an external dependency, not an extension point.", Limitations = "Its implementation belongs to another repository.", Supports = closed.Supports });
        Assert.False(collection.HasOpenCoverage);
        collection.InitializeSurvey([first, second]);
        Assert.Equal("investigated", collection.Coverage.Single(item => item.Key == first.Key).Status);

        var restored = new RepositoryMemoryCollection(collection.Baseline, true, 0);
        restored.Restore(new RepositoryIntelligenceCheckpoint
        {
            RepositoryIdentity = "repo", HistoryRevision = null, InitialSurvey = collection.InitialSurvey, Coverage = collection.Coverage,
        });
        Assert.All(restored.Coverage, item => Assert.Equal("investigating", item.Status));
        Assert.True(restored.HasOpenCoverage);
    }

    /// <summary>Unresolved additions cannot disappear into checklist prose, including after a checkpoint.</summary>
    [Fact]
    public static async Task Pending_proposals_are_inspectable_and_require_explicit_disposition()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await using var intelligence = await IntelligenceFixture.CreateAsync(fixture, ct);
        await using var operations = intelligence.CreateOperations();
        await using var run = await operations.BeginAsync(SessionId.New(), RunId.New(), fixture.InitialRoot, RepositoryIntelligenceOperation.Onboard, ct);
        var collection = run.Collection!;
        var area = new RepositoryIntelligenceCoverage { Key = "fixture", Area = "Fixture", Question = "What reusable guidance exists?", Status = "uninvestigated" };
        collection.InitializeSurvey([area]);
        var key = new string('a', 64);
        var text = new string('x', 3999) + "\U0001F600Distinct final claim.";
        collection.ConfirmReconciliation(key, text, [], []);
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), new ToolInvocationContext
        {
            RepositoryPath = fixture.InitialRoot, RequestedBy = "model", IntelligenceRun = run, TrustLevel = RepositoryTrustLevel.TrustedRead,
        });
        var tool = new MemoriesTool(new UnusedMemories(), intelligence.Options, TestPromptLoader.Instance);
        var first = (await tool.ExecuteAsync(new MemoriesInput("coverage", ProposalKey: key), context, ct)).Value.Coverage!.PendingReconciliations.Single();
        Assert.Equal(3999, first.NextTextOffset);
        var last = (await tool.ExecuteAsync(new MemoriesInput("coverage", ProposalKey: key, ProposalOffset: first.NextTextOffset!.Value), context, ct)).Value.Coverage!.PendingReconciliations.Single();
        var wireFirst = System.Text.Json.JsonSerializer.Deserialize<RepositoryMemoryReconciliation>(System.Text.Json.JsonSerializer.Serialize(first))!;
        var wireLast = System.Text.Json.JsonSerializer.Deserialize<RepositoryMemoryReconciliation>(System.Text.Json.JsonSerializer.Serialize(last))!;
        Assert.Equal(text, wireFirst.Text + wireLast.Text);
        Assert.Equal("situational", wireFirst.MemoryType);
        Assert.Equal("unspecified", wireFirst.Kind);
        await Assert.ThrowsAsync<ToolArgumentValidationException>(() => tool.ExecuteAsync(new MemoriesInput("coverage", ProposalKey: key, ProposalOffset: 4000), context, ct));
        Assert.Null(last.NextTextOffset);
        var closed = area with
        {
            Status = "investigated", Conclusion = "The proposed assertion describes an obsolete placeholder.", NoMemoryReason = "The fixture has no implemented behavior warranting retained guidance.",
            Supports = [new RepositoryMemorySupport { Path = "fixture.txt", ContentHash = "hash", ObservedAt = DateTimeOffset.UtcNow }],
        };
        collection.UpdateCoverage(closed);
        Assert.True(collection.HasOpenCoverage);
        Assert.Throws<ArgumentException>(() => collection.UpdateCoverage(closed, [new string('b', 64)]));
        var checkpoint = new RepositoryIntelligenceCheckpoint
        {
            RepositoryIdentity = collection.Baseline.RepositoryIdentity, HistoryRevision = null,
            InitialSurvey = collection.InitialSurvey, Coverage = collection.Coverage, PendingReconciliations = collection.PendingReconciliations,
        };
        var restored = new RepositoryMemoryCollection(collection.Baseline, true, 0);
        restored.Restore(checkpoint);
        Assert.Equal(text, Assert.Single(restored.PendingReconciliations).Text);
        restored.UpdateCoverage(closed, [key]);
        Assert.False(restored.HasOpenCoverage);
        Assert.Empty(restored.PendingReconciliations);
    }
}
