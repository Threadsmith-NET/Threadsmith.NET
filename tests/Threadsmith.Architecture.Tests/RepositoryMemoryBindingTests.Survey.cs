namespace Threadsmith.Architecture.Tests;

using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Initial survey completeness and published guidance remain distinct enforced contracts.</summary>
public static partial class RepositoryMemoryBindingTests
{
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
            Status = "investigated",
            Conclusion = "The fixture has no reusable implementation.",
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
            RepositoryIdentity = "repo",
            HistoryRevision = null,
            InitialSurvey = collection.InitialSurvey,
            Coverage = collection.Coverage,
        });
        Assert.All(restored.Coverage, item => Assert.Equal("investigating", item.Status));
        Assert.True(restored.HasOpenCoverage);
    }
}
