namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Presentation;
using Xunit;

/// <summary>Verifies that real large-file diffs produce focused output-window previews.</summary>
public static class UnifiedDiffPresentationTests
{
    /// <summary>A field insertion in a large file displays the change with local context.</summary>
    [Fact]
    public static void LargeFileInsertion_ShowsOnlyChangedRegion()
    {
        var lines = Enumerable.Range(0, 1522).Select(index => $"unchanged line {index}").ToArray();
        var before = string.Join("\n", lines);
        var after = string.Join("\n", lines.Take(23).Concat(["    private static readonly string _test = \"tested\";", string.Empty]).Concat(lines.Skip(23)));
        var diff = UnifiedTextDiff.Create("ApplicationComposition.cs", before, after, 512, out var added, out var removed);

        var display = InteractionPresentationFormatter.FormatUnifiedDiffForDisplay(diff);

        Assert.Equal(2, added);
        Assert.Equal(0, removed);
        Assert.Contains("+    private static readonly string _test", display, StringComparison.Ordinal);
        Assert.DoesNotContain("-unchanged line", display, StringComparison.Ordinal);
        Assert.DoesNotContain("unchanged line 1000", display, StringComparison.Ordinal);
        Assert.True(display.Split('\n').Length < 30, display);
    }
}
