namespace Threadsmith.CoreRuntime.Tests;

using Microsoft.Extensions.Configuration;
using Threadsmith.Interaction.Markdown;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Rendering;
using TUIKit.Terminal;
using Xunit;

/// <summary>Verifies bounded semantic Markdown parsing, layout, fallback, and display configuration.</summary>
public static class TuiMarkdownRenderingTests
{
    /// <summary>Actual transcript cells emit native targets only when the terminal supports them.</summary>
    [Theory]
    [InlineData(true, HyperlinkMode.Auto, true)]
    [InlineData(false, HyperlinkMode.Auto, false)]
    [InlineData(true, HyperlinkMode.Never, false)]
    public static void Render_LabeledLinks_UsesNativeTargets(bool supported, HyperlinkMode mode, bool emitsLinks)
    {
        const string source = "[View pull request #771](https://example.com/pr/771) plain";
        var document = Assert.IsType<MarkdownDocument>(new MarkdownParser().Parse(source).Document);
        var view = new TranscriptView();
        view.Present(new PresentationBatch([new PresentationMarkdownItem(document, source, source, true)]));
        var capabilities = new TerminalCapabilities(TerminalColorDepth.None, false, false, supported, false, false, false, false, false);
        using var backend = new HeadlessBackend(80, 6, capabilities);
        var renderer = new TerminalRenderer(80, 6, TerminalColorDepth.None) { Hyperlinks = mode };
        var cells = new CellBuffer(80, 6);

        view.Render(new BufferSurface(cells));
        renderer.Render(backend, view.Render);
        var output = backend.TakeOutput();

        Assert.Contains("View pull request #771 plain", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        Assert.All(
            Enumerable.Range(0, "View pull request #771".Length).Select(x => cells.Get(x, 1)),
            cell => Assert.Equal("https://example.com/pr/771", cell.Style.Hyperlink?.AbsoluteUri));
        Assert.Null(cells.Get("View pull request #771".Length, 1).Style.Hyperlink);
        Assert.DoesNotContain(" (https://", output, StringComparison.Ordinal);
        Assert.Equal(emitsLinks, output.Contains("\u001b]8;", StringComparison.Ordinal));
        Assert.Equal(emitsLinks, output.Contains("https://example.com/pr/771", StringComparison.Ordinal));
        if (emitsLinks)
        {
            Assert.Contains(Ansi.CloseHyperlink, output, StringComparison.Ordinal);
        }

        Assert.Equal("https://example.com/pr/771", Assert.Single(view.Links).AbsoluteUri);
    }

    /// <summary>Wrapping, adjacent fragment targets, and clearing preserve exact cell ownership.</summary>
    [Fact]
    public static void Render_WrappedUnicodeLinks_ClearTargetsWithViewport()
    {
        const string source = "[界abcdef](https://example.com/#one)[two](https://example.com/#two) plain";
        var document = Assert.IsType<MarkdownDocument>(new MarkdownParser().Parse(source).Document);
        var view = new TranscriptView();
        view.Present(new PresentationBatch([new PresentationMarkdownItem(document, source, source, true)]));
        var cells = new CellBuffer(6, 12);

        view.Render(new BufferSurface(cells));

        Assert.Equal("https://example.com/#one", cells.Get(0, 1).Style.Hyperlink?.AbsoluteUri);
        Assert.Equal(cells.Get(0, 1).Style.Hyperlink, cells.Get(1, 1).Style.Hyperlink);
        var targets = Enumerable.Range(0, 12).SelectMany(y => Enumerable.Range(0, 6)
            .Select(x => cells.Get(x, y).Style.Hyperlink?.AbsoluteUri)).OfType<string>().Distinct().ToArray();
        Assert.Equal(["https://example.com/#one", "https://example.com/#two"], targets);
        view.ClearView();
        view.Render(new BufferSurface(cells));
        Assert.All(
            Enumerable.Range(0, 12).SelectMany(y => Enumerable.Range(0, 6).Select(x => cells.Get(x, y))),
            cell => Assert.Null(cell.Style.Hyperlink));
        Assert.Equal(2, view.Links.Count);
    }

    /// <summary>Reusing renderer cells cannot retain a prior target when only the destination changes.</summary>
    [Fact]
    public static void Render_ReusedCells_ReplaceAndRemoveNativeTargets()
    {
        using var backend = new HeadlessBackend(20, 4, TerminalCapabilities.Full);
        var renderer = new TerminalRenderer(20, 4, TerminalColorDepth.TrueColor);
        foreach (var target in new[] { "https://example.com/#one", "https://example.com/#two" })
        {
            var source = $"[same label]({target})";
            var document = Assert.IsType<MarkdownDocument>(new MarkdownParser().Parse(source).Document);
            var view = new TranscriptView();
            view.Present(new PresentationBatch([new PresentationMarkdownItem(document, source, source, true)]));
            renderer.Render(backend, view.Render);
            Assert.Contains(target, backend.TakeOutput(), StringComparison.Ordinal);
        }

        var plain = new TranscriptView();
        plain.Present(new PresentationBatch([new PresentationTextItem([new("same label", PresentationTextRole.Default)])]));
        renderer.Render(backend, plain.Render);
        var output = backend.TakeOutput();
        Assert.Contains("same label", output, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", output, StringComparison.Ordinal);
        Assert.Empty(plain.Links);
    }

    /// <summary>Unsupported or unsafe autolink targets preserve surrounding Markdown without becoming active.</summary>
    [Theory]
    [InlineData("<user@example.com>")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("<https://example.com/%1B>")]
    public static void Parse_UnsafeAutolink_RemainsInertWithoutDiscardingMarkdown(string source)
    {
        var document = Assert.IsType<MarkdownDocument>(new MarkdownParser().Parse("**before** " + source).Document);
        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(document.Blocks));

        Assert.Contains(paragraph.Spans, span => span.Style.HasFlag(MarkdownSpanStyle.Strong));
        Assert.DoesNotContain(paragraph.Spans, span => span.LinkTarget is not null);
    }

    /// <summary>Distinct fragment targets survive both layout and retained link selection.</summary>
    [Theory]
    [InlineData("[one](https://example.com/docs#one)[two](https://example.com/docs#two)")]
    [InlineData("[**one**](https://example.com/docs#one)[`two`](https://example.com/docs#two)")]
    public static void Format_AdjacentFragmentLinks_PreservesBothDestinations(string source)
    {
        var document = Assert.IsType<MarkdownDocument>(new MarkdownParser().Parse(source).Document);
        var segments = TuiMarkdownLayout.Format(document, 120);

        Assert.Equal(
            "onetwo\n",
            string.Concat(segments.Select(segment => segment.Text)));
        var view = new TranscriptView();
        view.Present(new PresentationBatch([new PresentationMarkdownItem(document, source, source, true)]));
        Assert.Equal(["https://example.com/docs#one", "https://example.com/docs#two"], view.Links.Select(link => link.AbsoluteUri));
    }

    /// <summary>Wide tables and responsive table projections retain hyperlink metadata.</summary>
    [Theory]
    [InlineData(20)]
    [InlineData(120)]
    public static void Format_TableLinks_RemainSelectableAcrossWidths(int width)
    {
        const string source = "| Resource |\n| --- |\n| [one](https://example.com/docs#one) |\n| [two](https://example.com/docs#two) |";
        var document = Assert.IsType<MarkdownDocument>(new MarkdownParser().Parse(source).Document);
        var segments = TuiMarkdownLayout.Format(document, width);
        var targets = segments.Where(segment => segment.LinkTarget is not null)
            .Select(segment => segment.LinkTarget!.AbsoluteUri).Distinct(StringComparer.Ordinal);

        Assert.Equal(["https://example.com/docs#one", "https://example.com/docs#two"], targets);
        var view = new TranscriptView();
        view.Present(new PresentationBatch([new PresentationTextItem(segments)]));
        Assert.Equal(["https://example.com/docs#one", "https://example.com/docs#two"], view.Links.Select(link => link.AbsoluteUri));
    }

    /// <summary>Reference links decode labels and destinations while deduplicating only complete matching targets.</summary>
    [Fact]
    public static void Format_ReferenceLinksAndEscapedTargets_PreservesLabelsAndTargets()
    {
        const string source = "- [**one** &amp; two][ref]\n- [again][ref]\n- [section](https://example.com/docs?q=a&amp;b=c#two)\n\n[ref]: https://example.com/docs?q=a&b=c#one";
        var document = Assert.IsType<MarkdownDocument>(new MarkdownParser().Parse(source).Document);
        var view = new TranscriptView();
        view.Present(new PresentationBatch([new PresentationMarkdownItem(document, source, source, true)]));

        Assert.Equal(
            ["https://example.com/docs?q=a&b=c#one", "https://example.com/docs?q=a&b=c#two"],
            view.Links.Select(link => link.AbsoluteUri));
        var visible = string.Concat(TuiMarkdownLayout.Format(document, 120).Select(segment => segment.Text));
        Assert.Contains("one & two", visible, StringComparison.Ordinal);
    }

    /// <summary>Configuration cannot raise recursive traversal beyond its stack-safety ceiling.</summary>
    [Theory]
    [InlineData(33)]
    [InlineData(int.MaxValue)]
    public static void ConfiguredDepthRejectsStackOverflowRisk(int depth)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["tui:limits:markdown:maximumDepth"] = depth.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();
        Assert.Throws<ArgumentOutOfRangeException>(() => TuiDisplayOptions.Load(configuration));
    }

    /// <summary>Renders heading text without exposing ATX source delimiters.</summary>
    [Fact]
    public static void Format_Headings_OmitsMarkdownDelimiters()
    {
        var document = new MarkdownDocument(
        [
            new MarkdownHeading(1, [new MarkdownSpan("Primary")]),
            new MarkdownHeading(2, [new MarkdownSpan("Secondary")]),
            new MarkdownHeading(6, [new MarkdownSpan("Minor")]),
        ]);

        var segments = TuiMarkdownLayout.Format(document, 120);
        var visible = string.Concat(segments.Select(segment => segment.Text));

        Assert.Equal("Primary\n═══════\n\nSecondary\n─────────\n\nMinor\n", visible);
        Assert.DoesNotContain('#', visible);
        Assert.All(
            segments.Where(segment => !string.Equals(segment.Text, "\n", StringComparison.Ordinal)),
            segment => Assert.Equal(PresentationTextRole.MarkdownHeading, segment.Role));
    }

    /// <summary>Keeps HTML and unsafe link destinations inert and visibly recoverable.</summary>
    [Fact]
    public static void Parse_ActiveOrUnsafeContent_ProducesOnlyInertText()
    {
        const string source = "<script>bad()</script> [run](javascript:alert(1)) \u001b[31m";

        var result = new MarkdownParser().Parse(source);

        var document = Assert.IsType<MarkdownDocument>(result.Document);
        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(document.Blocks));
        var visible = string.Concat(paragraph.Spans.Select(span => span.Text));
        Assert.Contains("<script>bad()</script>", visible, StringComparison.Ordinal);
        Assert.Contains("javascript:alert(1)", visible, StringComparison.Ordinal);
        Assert.Contains("\\u001B", visible, StringComparison.Ordinal);
        Assert.DoesNotContain(paragraph.Spans, span => span.LinkTarget is not null);
    }

    /// <summary>Preserves printable Unicode, line feeds, and tabs while visibly escaping unsafe code units.</summary>
    [Fact]
    public static void TerminalEncoder_ControlsAndMalformedUnicode_UsesDeterministicEscapes()
    {
        const string input = "a\tb\n\r\u001b\u0085\ud800";

        var encoded = TerminalControlEncoder.Encode(input);

        Assert.Equal("a\tb\n\\u000D\\u001B\\u0085\\uD800", encoded);
        Assert.Equal(1, MarkdownParser.SyntaxProfileVersion);
    }

    /// <summary>Falls back to visibly escaped source when the configured source bound is exceeded.</summary>
    [Fact]
    public static void Parse_OversizedSource_FallsBackWithoutPartialDocument()
    {
        var source = "\u001b" + new string('a', MarkdownParser.MaximumSourceBytes + 1);

        var result = new MarkdownParser().Parse(source);

        Assert.False(result.Succeeded);
        Assert.Null(result.Document);
        Assert.Contains("\\u001B", result.SafeSource, StringComparison.Ordinal);
        Assert.NotNull(result.FallbackReason);
    }

    /// <summary>Preserves validated link destinations when table cells are flattened for layout.</summary>
    [Fact]
    public static void Format_TableLink_PreservesDestination()
    {
        const string source = "| Resource |\n| --- |\n| [docs](https://example.com/docs) |";
        var document = Assert.IsType<MarkdownDocument>(new MarkdownParser().Parse(source).Document);

        var segments = TuiMarkdownLayout.Format(document, 120);
        var visible = string.Concat(segments.Select(segment => segment.Text));

        Assert.Contains("docs", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", visible, StringComparison.Ordinal);
        Assert.Contains(segments, segment => segment.Text.Contains("docs", StringComparison.Ordinal)
            && segment.LinkTarget?.AbsoluteUri == "https://example.com/docs");
    }

    /// <summary>An oversized buffered answer starts its source projection when it first becomes visible.</summary>
    [Fact]
    public static void Collector_OversizedRenderedAnswer_SourceFallbackStartsAnswerBlock()
    {
        var collector = new ModelAnswerCollector(renderMarkdown: true);
        string buffered = new('a', MarkdownParser.MaximumSourceBytes);

        Assert.Null(collector.Append(buffered));
        var fallback = Assert.IsType<PresentationSourceItem>(collector.Append("b"));
        var continuation = Assert.IsType<PresentationSourceItem>(collector.Append("c"));

        Assert.True(fallback.StartsAnswerBlock);
        Assert.Equal(buffered + "b", fallback.SafeSource);
        Assert.False(continuation.StartsAnswerBlock);
    }

    /// <summary>Uses terminal-safe source when parsing fails or cancellation is already requested.</summary>
    [Fact]
    public static void Collector_ParserFailureOrCancellation_FallsBackExactlyOnce()
    {
        var failing = new ModelAnswerCollector(renderMarkdown: true, new FailingParser());
        Assert.Null(failing.Append("a\u001bb"));
        var failure = Assert.IsType<PresentationSourceItem>(failing.Flush());
        Assert.Equal("a\\u001Bb", failure.SafeSource);
        Assert.Null(failing.Flush());

        var unsafeDocument = new ModelAnswerCollector(renderMarkdown: true, new UnsafeDocumentParser());
        Assert.Null(unsafeDocument.Append("accepted"));
        var unsafeFallback = Assert.IsType<PresentationSourceItem>(unsafeDocument.Flush());
        Assert.Equal("accepted", unsafeFallback.SafeSource);

        var cancelled = new ModelAnswerCollector(renderMarkdown: true);
        Assert.Null(cancelled.Append("**partial"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancellationOutput = Assert.IsType<PresentationSourceItem>(cancelled.Flush(cancellation.Token));
        Assert.Equal("**partial", cancellationOutput.SafeSource);
    }

    /// <summary>Loads markdown rendering through normal layered configuration with safe malformed-value recovery.</summary>
    [Fact]
    public static void DisplayOptions_MarkdownSetting_UsesDefaultOverrideAndDiagnostic()
    {
        var defaults = TuiDisplayOptions.Load(null);
        IConfiguration disabledConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["tui:renderMarkdown"] = "false" })
            .Build();
        IConfiguration malformedConfiguration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["tui:renderMarkdown"] = "sometimes" })
            .Build();

        Assert.True(defaults.RenderMarkdown);
        Assert.False(TuiDisplayOptions.Load(disabledConfiguration).RenderMarkdown);
        var malformed = TuiDisplayOptions.Load(malformedConfiguration);
        Assert.True(malformed.RenderMarkdown);
        Assert.Contains(malformed.Diagnostics, diagnostic => diagnostic.Contains("tui:renderMarkdown", StringComparison.Ordinal));
    }

    private sealed class FailingParser : IMarkdownParser
    {
        /// <inheritdoc />
        public MarkdownParseResult Parse(string source)
        {
            throw new InvalidOperationException("Injected parser failure.");
        }
    }

    private sealed class UnsafeDocumentParser : IMarkdownParser
    {
        /// <inheritdoc />
        public MarkdownParseResult Parse(string source)
        {
            return new(
                        new MarkdownDocument(
                            [new MarkdownParagraph([new MarkdownSpan("unsafe\u001b")])]),
                        source,
                        null);
        }
    }
}
