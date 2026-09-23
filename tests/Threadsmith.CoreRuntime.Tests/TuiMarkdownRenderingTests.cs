namespace Threadsmith.CoreRuntime.Tests;

using System.Text;
using Microsoft.Extensions.Configuration;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Markdown;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using Xunit;

/// <summary>Verifies bounded semantic Markdown parsing, layout, fallback, and display configuration.</summary>
public static class TuiMarkdownRenderingTests
{
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

    /// <summary>Deep valid Markdown falls back to source instead of unbounded recursive traversal.</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(32)]
    public static void Parse_ExcessiveNesting_PreservesSource(int depth)
    {
        var source = string.Concat(Enumerable.Repeat("> ", 100)) + "deep";
        var result = new MarkdownParser(new MarkdownRenderingLimits { MaximumDepth = depth }).Parse(source);
        Assert.False(result.Succeeded);
        Assert.Equal(source, result.SafeSource);
    }

    /// <summary>Maps supported CommonMark and selected extensions into the closed host document model.</summary>
    [Fact]
    public static void Parse_SupportedMarkdown_ProducesSemanticDocument()
    {
        const string source = """
            # Heading

            A **strong** and *soft* [link](https://example.com/path).

            - [x] done
            - [ ] open

            > quoted

            ```csharp
            Console.WriteLine("safe");
            ```

            | Name | Value |
            | --- | --- |
            | one | two |
            """;

        var result = new MarkdownParser().Parse(source);

        Assert.True(result.Succeeded);
        var document = Assert.IsType<MarkdownDocument>(result.Document);
        Assert.Contains(document.Blocks, block => block is MarkdownHeading { Level: 1 });
        Assert.Contains(document.Blocks, block => block is MarkdownQuote);
        Assert.Contains(document.Blocks, block => block is MarkdownCodeBlock { Language: "csharp" });
        Assert.Contains(document.Blocks, block => block is MarkdownTable);
        var list = Assert.IsType<MarkdownList>(document.Blocks.Single(block => block is MarkdownList));
        Assert.Equal([true, false], list.Items.Select(item => item.IsChecked));
        var paragraph = Assert.IsType<MarkdownParagraph>(document.Blocks[1]);
        Assert.Contains(paragraph.Spans, span => span.Style.HasFlag(MarkdownSpanStyle.Strong));
        Assert.Contains(paragraph.Spans, span => span.LinkTarget?.Host == "example.com");
        var layout = string.Concat(TuiMarkdownLayout.Format(document, 120).Select(segment => segment.Text));
        Assert.Contains("link (https://example.com/path)", layout, StringComparison.Ordinal);
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
        var input = "a\tb\n\r\u001b\u0085\ud800";

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

    /// <summary>Uses a readable key/value projection when a table cannot fit the available width.</summary>
    [Fact]
    public static void Format_NarrowTable_UsesResponsiveKeyValueLayout()
    {
        var table = new MarkdownTable(
        [
            new MarkdownTableRow(
                true,
                [[new MarkdownSpan("Name")], [new MarkdownSpan("Description")]]),
            new MarkdownTableRow(
                false,
                [[new MarkdownSpan("one")], [new MarkdownSpan(new string('x', 30))]]),
        ]);

        var segments = TuiMarkdownLayout.Format(
            new MarkdownDocument([table]),
            20);
        var visible = string.Concat(segments.Select(segment => segment.Text));

        Assert.Contains("- Name: one", visible, StringComparison.Ordinal);
        Assert.Contains("- Description: xxxxx", visible, StringComparison.Ordinal);
        Assert.Contains("\n               xxxxx", visible, StringComparison.Ordinal);
        Assert.All(
            visible.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => Assert.True(line.Length <= 20, $"Table line exceeded the layout width: '{line}'."));
    }

    /// <summary>Preserves validated link destinations when table cells are flattened for layout.</summary>
    [Fact]
    public static void Format_TableLink_PreservesDestination()
    {
        const string source = "| Resource |\n| --- |\n| [docs](https://example.com/docs) |";
        var document = Assert.IsType<MarkdownDocument>(new MarkdownParser().Parse(source).Document);

        var visible = string.Concat(TuiMarkdownLayout.Format(document, 120).Select(segment => segment.Text));

        Assert.Contains("docs (https://example.com/docs)", visible, StringComparison.Ordinal);
    }

    /// <summary>Wraps an oversized header-only table through the same bounded responsive layout as data rows.</summary>
    [Fact]
    public static void Format_HeaderOnlyTable_WrapsAtTerminalWidth()
    {
        var table = new MarkdownTable(
        [
            new MarkdownTableRow(
                true,
                [[new MarkdownSpan(new string('x', 50))]]),
        ]);

        var visible = string.Concat(TuiMarkdownLayout.Format(new MarkdownDocument([table]), 20).Select(segment => segment.Text));

        Assert.All(
            visible.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => Assert.True(line.Length <= 20, $"Table line exceeded the layout width: '{line}'."));
    }

    /// <summary>Retains a valid zero-based CommonMark ordered-list start.</summary>
    [Fact]
    public static void Parse_ZeroBasedOrderedList_PreservesStart()
    {
        var document = Assert.IsType<MarkdownDocument>(new MarkdownParser().Parse("0. zero").Document);
        var list = Assert.IsType<MarkdownList>(Assert.Single(document.Blocks));

        Assert.Equal(0, list.Start);
        Assert.StartsWith(
            "0. zero",
            string.Concat(TuiMarkdownLayout.Format(document, 20).Select(segment => segment.Text)),
            StringComparison.Ordinal);
    }

    /// <summary>Wraps prose at Unicode cell widths with hanging list and quote indentation.</summary>
    [Fact]
    public static void Format_LongListAndQuote_UsesHangingIndentation()
    {
        var document = new MarkdownDocument(
        [
            new MarkdownList(
                false,
                1,
                [new MarkdownListItem(null, [new MarkdownParagraph([new MarkdownSpan("one two three four five six")])])]),
            new MarkdownQuote(
                [new MarkdownParagraph([new MarkdownSpan("quoted words continue across lines")])]),
        ]);

        var visible = string.Concat(TuiMarkdownLayout.Format(document, 20).Select(segment => segment.Text));

        Assert.Contains("- one two three four\n  five six", visible, StringComparison.Ordinal);
        Assert.Contains("> quoted words\n> continue", visible, StringComparison.Ordinal);
    }

    /// <summary>Buffers rendered answers until a boundary while source mode preserves chunk cadence.</summary>
    [Fact]
    public static void Collector_RenderedAndSourceModes_HonorTheirCadenceContracts()
    {
        var rendered = new ModelAnswerCollector(renderMarkdown: true);
        Assert.Null(rendered.Append("**hel"));
        Assert.Null(rendered.Append("lo**"));
        var renderedOutput = Assert.IsType<PresentationMarkdownItem>(rendered.Flush());
        Assert.True(renderedOutput.StartsAnswerBlock);
        var paragraph = Assert.IsType<MarkdownParagraph>(Assert.Single(renderedOutput.Document.Blocks));
        Assert.Equal("hello", string.Concat(paragraph.Spans.Select(span => span.Text)));
        Assert.Contains(paragraph.Spans, span => span.Style.HasFlag(MarkdownSpanStyle.Strong));

        var source = new ModelAnswerCollector(renderMarkdown: false);
        var first = Assert.IsType<PresentationSourceItem>(source.Append("a"));
        var second = Assert.IsType<PresentationSourceItem>(source.Append("\u001bb"));
        Assert.Equal("a", first.SafeSource);
        Assert.Equal("\\u001Bb", second.SafeSource);
        Assert.True(first.StartsAnswerBlock);
        Assert.False(second.StartsAnswerBlock);
        Assert.Null(source.Flush());
        var afterBoundary = Assert.IsType<PresentationSourceItem>(source.Append("next"));
        Assert.True(afterBoundary.StartsAnswerBlock);
    }

    /// <summary>Suppresses empty accepted deltas without opening a source-mode answer.</summary>
    [Fact]
    public static void Collector_SourceModeEmptyDelta_DoesNotBecomeVisible()
    {
        var source = new ModelAnswerCollector(renderMarkdown: false);

        Assert.Null(source.Append(string.Empty));
        Assert.Null(source.Flush());
        var firstVisible = Assert.IsType<PresentationSourceItem>(source.Append("accepted"));

        Assert.True(firstVisible.StartsAnswerBlock);
        Assert.Equal("accepted", firstVisible.SafeSource);
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

    /// <summary>Compiled themes declare Markdown styles explicitly; custom themes never inherit those styles.</summary>
    [Fact]
    public static void MarkdownRoles_CompiledThemesAndNoColor_HaveSafeFallbacks()
    {
        foreach (var configured in BuiltInThemes.Create())
        {
            var resolver = new TuiThemeResolver(configured.Theme);
            Assert.True(resolver.Resolve(PresentationTextRole.MarkdownHeading).Decorations?.HasFlag(TuiTextDecoration.Bold));
            Assert.True(resolver.Resolve(PresentationTextRole.MarkdownEmphasis).Decorations?.HasFlag(TuiTextDecoration.Italic));
        }

        var customTheme = new TuiTheme(
            "custom",
            [KeyValuePair.Create(
                PresentationTextRole.Default,
                new TuiTextStyle(Decorations: TuiTextDecoration.None))]);
        var customResolver = new TuiThemeResolver(customTheme);
        var emptyResolver = new TuiThemeResolver(new TuiTheme("empty", []));
        Assert.All(Enum.GetValues<PresentationTextRole>(), role =>
        {
            Assert.Equal(new TuiTextStyle(Decorations: TuiTextDecoration.None), customResolver.Resolve(role));
            Assert.Equal(new TuiTextStyle(Decorations: TuiTextDecoration.None), emptyResolver.Resolve(role));
        });

        var suppressed = new TuiThemeResolver(BuiltInThemes.Create()[1].Theme, suppressStyles: true);
        Assert.Equal(new TuiTextStyle(), suppressed.Resolve(PresentationTextRole.MarkdownHeading));
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
