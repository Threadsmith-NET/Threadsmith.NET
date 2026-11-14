namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using Xunit;

/// <summary>Checks compact styles without changing source or copy authority.</summary>
public sealed class SourceHighlightingTests
{
    /// <summary>Blank lines do not allocate styles or shift following tokens.</summary>
    [Fact]
    public void BlankLinesDoNotCreateRangesOrChangeAbsoluteOffsets()
    {
        var highlighter = new TuiKitSourceHighlighter();
        var ranges = highlighter.Highlight("\n\nclass\n\n42\n", "csharp");

        Assert.Contains(new SourceHighlightSpan(2, 5, PresentationTextRole.Status), ranges);
        Assert.Contains(new SourceHighlightSpan(9, 2, PresentationTextRole.Warning), ranges);
        Assert.All(ranges, range => Assert.True(range.Length > 0));
        Assert.Empty(highlighter.Highlight(new string('\n', ToolSourceDetail.MaximumCharacters), "csharp"));
    }

    /// <summary>Blank-line allocation stays independent of the source line count.</summary>
    [Fact]
    public void EmptyLineHighlightingAllocationsDoNotGrowWithLineCount()
    {
        var highlighter = new TuiKitSourceHighlighter();
        var large = new string('\n', ToolSourceDetail.MaximumCharacters);
        highlighter.Highlight("\n", "csharp");
        highlighter.Highlight(large, "csharp");

        var before = GC.GetAllocatedBytesForCurrentThread();
        var smallRanges = highlighter.Highlight("\n", "csharp");
        var smallBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var largeRanges = highlighter.Highlight(large, "csharp");
        var largeBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(smallRanges);
        Assert.Empty(largeRanges);
        Assert.True(largeBytes <= smallBytes + 1024, $"Blank lines allocated {largeBytes} bytes versus {smallBytes} bytes.");
    }

    /// <summary>Logical line count bounds tokenization independently of source length.</summary>
    [Fact]
    public void HighlightingFallsBackAboveLineBudgetWithoutChangingSource()
    {
        // Arrange
        var highlighter = new TuiKitSourceHighlighter();
        var atLimit = string.Join('\n', Enumerable.Repeat("class", TuiKitSourceHighlighter.MaximumHighlightedLines));
        var maximumSource = string.Concat(Enumerable.Repeat("1\n", ToolSourceDetail.MaximumCharacters / 2));

        // Act
        var highlighted = highlighter.Highlight(atLimit, "csharp");
        var overLimit = highlighter.Highlight(atLimit + "\nclass", "csharp");
        var document = new SourceDocument(maximumSource, "csharp", highlighter);

        // Assert
        Assert.Equal(TuiKitSourceHighlighter.MaximumHighlightedLines, highlighted.Count);
        Assert.Empty(overLimit);
        Assert.Equal(new SourceHighlightSpan(0, maximumSource.Length, PresentationTextRole.MarkdownCode), Assert.Single(document.Ranges));
        Assert.Equal(maximumSource, document.DisplaySource);
        Assert.Equal(maximumSource, document.CopyRange(0, document.DisplaySource.Length));
    }

    /// <summary>Adjacent equal styles and plain gaps share compact ranges.</summary>
    [Fact]
    public void DocumentMergesAdjacentStylesAndKeepsOffsetsInsteadOfSubstrings()
    {
        var document = new SourceDocument("abcde", "csharp", new FixedHighlighter([
            new(0, 1, PresentationTextRole.MarkdownCode),
            new(1, 1, PresentationTextRole.MarkdownCode),
            new(3, 1, PresentationTextRole.Warning),
        ]));

        SourceHighlightSpan[] expected = [
            new(0, 3, PresentationTextRole.MarkdownCode),
            new(3, 1, PresentationTextRole.Warning),
            new(4, 1, PresentationTextRole.MarkdownCode),
        ];
        Assert.Equal(expected, document.Ranges);
        Assert.Equal("abcde", document.CopyRange(0, 5));
    }

    /// <summary>Invalid styles cannot change display coverage or copy authority.</summary>
    [Fact]
    public void InvalidRangesFallBackWithoutChangingRawCopy()
    {
        const string source = "\ttext\r\n";
        var document = new SourceDocument(source, "csharp", new FixedHighlighter([
            new(2, 2, PresentationTextRole.Status),
            new(1, 1, PresentationTextRole.Warning),
        ]));

        Assert.Equal(new SourceHighlightSpan(0, document.DisplaySource.Length, PresentationTextRole.MarkdownCode), Assert.Single(document.Ranges));
        Assert.Equal(source, document.CopyRange(0, document.DisplaySource.Length));
    }

    private sealed class FixedHighlighter : ISourceHighlighter
    {
        private readonly IReadOnlyList<SourceHighlightSpan> _ranges;

        public FixedHighlighter(IReadOnlyList<SourceHighlightSpan> ranges)
        {
            _ranges = ranges;
        }

        public IReadOnlyList<SourceHighlightSpan> Highlight(string source, string language) => _ranges;
    }
}
