namespace Threadsmith.Architecture.Tests;

using Threadsmith.Core;
using Xunit;

/// <summary>Durable checkpoints are detached preparation and are fenced by scope and published intelligence.</summary>
public static partial class RepositoryMemoryBindingTests
{
    /// <summary>Restart restores preparation only when the pinned history and baseline remain compatible.</summary>
    [Theory]
    [InlineData("same")]
    [InlineData("head")]
    [InlineData("depth")]
    [InlineData("baseline")]
    public static async Task OnboardingCheckpoint_RestoresPreparedEntriesWithoutPublishingAndFencesChanges(string change)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await BindingFixture.CreateAsync(false);
        await using var intelligence = await IntelligenceFixture.CreateAsync(fixture, ct);
        var before = await intelligence.Store.GetSnapshotAsync(intelligence.Identity, [], ct);
        var revision = new string('a', 40);
        var model = new TextEmbeddingModelDescriptor("fixture", 1, 256);
        var embedding = new TextEmbeddingResult(new float[] { 1f }, 5, false);
        RepositoryMemoryEntry prepared;
        await using (var operations = intelligence.CreateOperations())
        await using (var run = await operations.BeginAsync(SessionId.New(), RunId.New(), fixture.InitialRoot, RepositoryIntelligenceOperation.Onboard, ct))
        {
            await run.AcceptHistoryPageAsync(0, new GitLogResult([], false) { IsCollectionPage = true, HistoryRevision = revision, NextCollectionPage = 1 }, "{}", ct);
            var result = await intelligence.Store.ForCollection(run.Collection!).AddAsync(
                intelligence.Identity,
                new RepositoryMemoryWrite
                {
                    Text = "Reuse the shared formatter at output boundaries.", Origin = RepositoryMemoryOrigin.Model, Ownership = RepositoryMemoryOwnership.Intelligence,
                    Supports = [new RepositoryMemorySupport { Path = "formatter.cs", ContentHash = new string('a', 64), ObservedAt = DateTimeOffset.UtcNow }],
                },
                model,
                embedding,
                intelligence.Options.CaptureCurrent(),
                ct);
            prepared = result.Entry!;
            run.Collection!.Accept(new RepositoryMemoryOperationResult("added", prepared.Id, prepared, [], []));
            run.Collection.UpdateCoverage(new RepositoryIntelligenceCoverage
            {
                Key = "output", Area = "Presentation", Question = "How should features format output?", Status = "investigated",
                Conclusion = "Use the shared formatter.", MemoryIds = [prepared.Id], Supports = prepared.Supports,
            });
            InitializeCoverageSurvey(run.Collection);
            await run.SaveCheckpointAsync("Completed formatter investigation.", ct);
        }

        Assert.Equal(before.Entries.Select(entry => entry.Id), (await intelligence.Store.GetSnapshotAsync(intelligence.Identity, [], ct)).Entries.Select(entry => entry.Id));
        var checkpoint = await intelligence.Store.ReadOnboardingCheckpointAsync(intelligence.Identity, ct);
        Assert.NotNull(checkpoint);
        Assert.Equal(1f, checkpoint.Entries.Single(entry => entry.Id == prepared.Id).Embedding.Span[0]);
        Assert.Contains(prepared.Id, checkpoint.AcceptedIds);
        Assert.Equal("investigated", Assert.Single(checkpoint.Coverage).Status);
        if (change == "baseline")
        {
            await intelligence.Store.AddAsync(intelligence.Identity, new RepositoryMemoryWrite { Text = "New published guidance", Origin = RepositoryMemoryOrigin.Model, Ownership = RepositoryMemoryOwnership.Intelligence }, model, embedding, intelligence.Options.CaptureCurrent(), ct);
        }

        int? depth = change == "depth" ? 10 : null;
        await using var resumedOperations = intelligence.CreateOperations();
        await using var resumed = await resumedOperations.BeginAsync(SessionId.New(), RunId.New(), fixture.InitialRoot, RepositoryIntelligenceOperation.Onboard, depth, ct);
        var requestedPage = resumed.PendingHistoryPage!.Value;
        Assert.Equal(change is "depth" or "baseline" ? 0 : 1, requestedPage);
        var deliveredPage = change == "same" ? 1 : 0;
        await resumed.AcceptHistoryPageAsync(
            requestedPage,
            new GitLogResult([], false)
            {
                IsCollectionPage = true, CollectionPage = deliveredPage, HistoryDepth = depth, HistoryRevision = change == "head" ? new string('b', 40) : revision,
            },
            "{}",
            ct);
        if (change == "same")
        {
            var restored = Assert.Single(resumed.Collection!.Snapshot(forPublication: true));
            Assert.Equal(prepared.Id, restored.Id);
            Assert.Equal(prepared.Revision, restored.Revision);
            Assert.Equal(1f, restored.Embedding.Span[0]);
            Assert.True(Assert.Single(restored.Supports).IsUnknown);
            var coverage = Assert.Single(resumed.Collection.Coverage);
            Assert.Equal("investigating", coverage.Status);
            Assert.True(Assert.Single(coverage.Supports).IsUnknown);
            Assert.Equal(prepared.Id, Assert.Single(coverage.MemoryIds));
            Assert.Contains("Completed formatter investigation.", resumed.Instructions, StringComparison.Ordinal);
            Assert.NotNull(await resumed.ContinueCoverageAsync("Final pass", ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => resumed.CompleteAsync(ct));
            resumed.Collection.UpdateCoverage(coverage with { Status = "investigated", Supports = prepared.Supports });
            Assert.Null(await resumed.ContinueCoverageAsync("Final pass complete", ct));
            await resumed.CompleteAsync(ct);
            Assert.Equal(prepared.Id, Assert.Single((await intelligence.Store.GetSnapshotAsync(intelligence.Identity, [], ct)).Entries).Id);
            Assert.Null(await intelligence.Store.ReadOnboardingCheckpointAsync(intelligence.Identity, ct));
        }
        else
        {
            Assert.Empty(resumed.Collection!.Snapshot(forPublication: true));
            Assert.Empty(resumed.Collection.Coverage);
            Assert.DoesNotContain("Completed formatter investigation.", resumed.Instructions, StringComparison.Ordinal);
        }
    }
}
