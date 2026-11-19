namespace Threadsmith.Mutations.Tests;

using Threadsmith.Core;
using Xunit;

/// <summary>Checks exact edit reconstruction and shortest edits across large and repeated-line inputs.</summary>
public static class UnifiedTextDiffTests
{
    /// <summary>Missing files, blank lines, and line-ending normalization retain their existing semantics.</summary>
    [Theory]
    [InlineData(null, "")]
    [InlineData("", null)]
    [InlineData("a\r\nb\r\n", "a\nb changed\n")]
    [InlineData("a\nb", "b\na")]
    [InlineData("a", "b\na\nc")]
    [InlineData("a\nb\na", "b")]
    public static void BoundaryCases_ReconstructInputs(string? before, string? after)
    {
        var diff = UnifiedTextDiff.Create("example.cs", before, after, 512, out _, out _);
        AssertReconstructs(before, after, diff);
    }

    /// <summary>Comparison observes caller cancellation before performing synchronous work.</summary>
    [Fact]
    public static void Comparison_ObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => UnifiedTextDiff.Create(
            "example.cs",
            "before",
            "after",
            512,
            out _,
            out _,
            cancellation.Token));
    }

    private static void AssertReconstructs(string? before, string? after, string diff)
    {
        if (before == after)
        {
            Assert.Empty(diff);
            return;
        }

        var body = diff.ReplaceLineEndings("\n").Split('\n').Skip(3).SkipLast(1).ToArray();
        Assert.Equal(before?.ReplaceLineEndings("\n").Split('\n') ?? [], body.Where(line => line[0] != '+').Select(line => line[1..]).ToArray());
        Assert.Equal(after?.ReplaceLineEndings("\n").Split('\n') ?? [], body.Where(line => line[0] != '-').Select(line => line[1..]).ToArray());
    }
}
