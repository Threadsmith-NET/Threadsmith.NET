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
