namespace Threadsmith.Mutations.Tests;

using Threadsmith.Core;
using Xunit;

/// <summary>Checks exact edit reconstruction and shortest edits across large and repeated-line inputs.</summary>
public static class UnifiedTextDiffTests
{
    /// <summary>Sparse edits in large files preserve all unchanged lines.</summary>
    [Fact]
    public static void LargeFile_SparseChangesRemainSmall()
    {
        var lines = Enumerable.Range(0, 50000).Select(index => $"line {index}").ToArray();
        var before = string.Join("\n", lines);
        lines[20] = "first change";
        lines[49000] = "second change";
        var after = string.Join("\n", lines);
        var diff = UnifiedTextDiff.Create("large.cs", before, after, 512, out var added, out var removed);
        Assert.Equal(2, added);
        Assert.Equal(2, removed);
        AssertReconstructs(before, after, diff);
    }

    /// <summary>Repeated lines and arbitrary insertions/deletions match an independent LCS oracle.</summary>
    [Fact]
    public static void RandomEdits_ReconstructBothInputsWithMinimalChanges()
    {
        var random = new Random(8128);
        for (var sample = 0; sample < 2000; sample++)
        {
            var oldLines = Enumerable.Range(0, random.Next(1, 35)).Select(_ => random.Next(6).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var newLines = Enumerable.Range(0, random.Next(1, 35)).Select(_ => random.Next(6).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var before = string.Join("\n", oldLines);
            var after = string.Join("\n", newLines);
            var diff = UnifiedTextDiff.Create("example.cs", before, after, 512, out var added, out var removed);
            var lengths = new int[oldLines.Length + 1, newLines.Length + 1];
            for (var i = 1; i <= oldLines.Length; i++)
            {
                for (var j = 1; j <= newLines.Length; j++)
                {
                    lengths[i, j] = oldLines[i - 1] == newLines[j - 1]
                        ? lengths[i - 1, j - 1] + 1 : Math.Max(lengths[i - 1, j], lengths[i, j - 1]);
                }
            }

            Assert.Equal(oldLines.Length + newLines.Length - (2 * lengths[oldLines.Length, newLines.Length]), added + removed);
            AssertReconstructs(before, after, diff);
        }
    }

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

    /// <summary>High-churn input falls back to exact replacement output when comparison work is exhausted.</summary>
    [Fact]
    public static void HighChurnComparison_UsesBoundedFallback()
    {
        var before = string.Join('\n', Enumerable.Range(0, 1000).Select(index => $"before {index}"));
        var afterLines = Enumerable.Range(0, 1000).Select(index => $"after {index}").ToArray();
        afterLines[500] = "before 500";
        var after = string.Join('\n', afterLines);

        var diff = UnifiedTextDiff.Create("example.cs", before, after, 8, out var added, out var removed);

        Assert.Equal(1000, added);
        Assert.Equal(1000, removed);
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
