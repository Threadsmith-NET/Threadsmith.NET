namespace Threadsmith.CoreRuntime.Tests;

using Microsoft.Extensions.Configuration;
using Spectre.Console;
using Threadsmith.Tui;
using Xunit;

/// <summary>Verifies bounded semantic Markdown parsing, layout, fallback, and display configuration.</summary>
public static class TuiMarkdownRenderingTests
{
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

        var result = new TuiMarkdownParser().Parse(source);

        Assert.True(result.Succeeded);
        var document = Assert.IsType<TuiMarkdownDocument>(result.Document);
        Assert.Contains(document.Blocks, block => block is TuiMarkdownHeading { Level: 1 });
        Assert.Contains(document.Blocks, block => block is TuiMarkdownQuote);
        Assert.Contains(document.Blocks, block => block is TuiMarkdownCodeBlock { Language: "csharp" });
        Assert.Contains(document.Blocks, block => block is TuiMarkdownTable);
        var list = Assert.IsType<TuiMarkdownList>(document.Blocks.Single(block => block is TuiMarkdownList));
        Assert.Equal([true, false], list.Items.Select(item => item.IsChecked));
        var paragraph = Assert.IsType<TuiMarkdownParagraph>(document.Blocks[1]);
        Assert.Contains(paragraph.Spans, span => span.Style.HasFlag(TuiMarkdownSpanStyle.Strong));
        Assert.Contains(paragraph.Spans, span => span.LinkTarget?.Host == "example.com");
        var layout = string.Concat(TuiMarkdownLayout.Format(document, 120).Select(segment => segment.Text));
        Assert.Contains("link (https://example.com/path)", layout, StringComparison.Ordinal);
    }

    /// <summary>Renders heading text without exposing ATX source delimiters.</summary>
    [Fact]
    public static void Format_Headings_OmitsMarkdownDelimiters()
    {
        var document = new TuiMarkdownDocument(
        [
            new TuiMarkdownHeading(1, [new TuiMarkdownSpan("Primary")]),
            new TuiMarkdownHeading(2, [new TuiMarkdownSpan("Secondary")]),
            new TuiMarkdownHeading(6, [new TuiMarkdownSpan("Minor")]),
        ]);

        var segments = TuiMarkdownLayout.Format(document, 120);
        var visible = string.Concat(segments.Select(segment => segment.Text));

        Assert.Equal("Primary\n═══════\n\nSecondary\n─────────\n\nMinor\n", visible);
        Assert.DoesNotContain('#', visible);
        Assert.All(
            segments.Where(segment => !string.Equals(segment.Text, "\n", StringComparison.Ordinal)),
            segment => Assert.Equal(TuiTextRole.MarkdownHeading, segment.Role));
    }

    /// <summary>Keeps HTML and unsafe link destinations inert and visibly recoverable.</summary>
    [Fact]
    public static void Parse_ActiveOrUnsafeContent_ProducesOnlyInertText()
    {
        const string source = "<script>bad()</script> [run](javascript:alert(1)) \u001b[31m";

        var result = new TuiMarkdownParser().Parse(source);

        var document = Assert.IsType<TuiMarkdownDocument>(result.Document);
        var paragraph = Assert.IsType<TuiMarkdownParagraph>(Assert.Single(document.Blocks));
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
        Assert.Equal(1, TuiMarkdownParser.SyntaxProfileVersion);
    }

    /// <summary>Falls back to visibly escaped source when the configured source bound is exceeded.</summary>
    [Fact]
    public static void Parse_OversizedSource_FallsBackWithoutPartialDocument()
    {
        var source = "\u001b" + new string('a', TuiMarkdownParser.MaximumSourceBytes + 1);

        var result = new TuiMarkdownParser().Parse(source);

        Assert.False(result.Succeeded);
        Assert.Null(result.Document);
        Assert.Contains("\\u001B", result.SafeSource, StringComparison.Ordinal);
        Assert.NotNull(result.FallbackReason);
    }

    /// <summary>Uses a readable key/value projection when a table cannot fit the available width.</summary>
    [Fact]
    public static void Format_NarrowTable_UsesResponsiveKeyValueLayout()
    {
        var table = new TuiMarkdownTable(
        [
            new TuiMarkdownTableRow(
                true,
                [[new TuiMarkdownSpan("Name")], [new TuiMarkdownSpan("Description")]]),
            new TuiMarkdownTableRow(
                false,
                [[new TuiMarkdownSpan("one")], [new TuiMarkdownSpan(new string('x', 30))]]),
        ]);

        var segments = TuiMarkdownLayout.Format(
            new TuiMarkdownDocument([table]),
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
        var document = Assert.IsType<TuiMarkdownDocument>(new TuiMarkdownParser().Parse(source).Document);

        var visible = string.Concat(TuiMarkdownLayout.Format(document, 120).Select(segment => segment.Text));

        Assert.Contains("docs (https://example.com/docs)", visible, StringComparison.Ordinal);
    }

    /// <summary>Wraps an oversized header-only table through the same bounded responsive layout as data rows.</summary>
    [Fact]
    public static void Format_HeaderOnlyTable_WrapsAtTerminalWidth()
    {
        var table = new TuiMarkdownTable(
        [
            new TuiMarkdownTableRow(
                true,
                [[new TuiMarkdownSpan(new string('x', 50))]]),
        ]);

        var visible = string.Concat(TuiMarkdownLayout.Format(new TuiMarkdownDocument([table]), 20).Select(segment => segment.Text));

        Assert.All(
            visible.Split('\n', StringSplitOptions.RemoveEmptyEntries),
            line => Assert.True(line.Length <= 20, $"Table line exceeded the layout width: '{line}'."));
    }

    /// <summary>Retains a valid zero-based CommonMark ordered-list start.</summary>
    [Fact]
    public static void Parse_ZeroBasedOrderedList_PreservesStart()
    {
        var document = Assert.IsType<TuiMarkdownDocument>(new TuiMarkdownParser().Parse("0. zero").Document);
        var list = Assert.IsType<TuiMarkdownList>(Assert.Single(document.Blocks));

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
        var document = new TuiMarkdownDocument(
        [
            new TuiMarkdownList(
                false,
                1,
                [new TuiMarkdownListItem(null, [new TuiMarkdownParagraph([new TuiMarkdownSpan("one two three four five six")])])]),
            new TuiMarkdownQuote(
                [new TuiMarkdownParagraph([new TuiMarkdownSpan("quoted words continue across lines")])]),
        ]);

        var visible = string.Concat(TuiMarkdownLayout.Format(document, 20).Select(segment => segment.Text));

        Assert.Contains("- one two three four\n  five six", visible, StringComparison.Ordinal);
        Assert.Contains("> quoted words\n> continue", visible, StringComparison.Ordinal);
    }

    /// <summary>Buffers rendered answers until a boundary while source mode preserves chunk cadence.</summary>
    [Fact]
    public static void Collector_RenderedAndSourceModes_HonorTheirCadenceContracts()
    {
        var rendered = new TuiModelAnswerCollector(renderMarkdown: true);
        Assert.Null(rendered.Append("**hel"));
        Assert.Null(rendered.Append("lo**"));
        var renderedOutput = Assert.IsType<TuiMarkdownOutput>(rendered.Flush());
        Assert.True(renderedOutput.StartsAnswerBlock);
        Assert.Equal(
            "\n",
            Assert.IsType<TuiTextSegment>(PrettyPromptConsoleSurface
                .ProjectInteractiveOutputItem(renderedOutput, 120)[0])
                .Text);
        var paragraph = Assert.IsType<TuiMarkdownParagraph>(Assert.Single(renderedOutput.Document.Blocks));
        Assert.Equal("hello", string.Concat(paragraph.Spans.Select(span => span.Text)));
        Assert.Contains(paragraph.Spans, span => span.Style.HasFlag(TuiMarkdownSpanStyle.Strong));

        var source = new TuiModelAnswerCollector(renderMarkdown: false);
        var first = Assert.IsType<TuiSourceOutput>(source.Append("a"));
        var second = Assert.IsType<TuiSourceOutput>(source.Append("\u001bb"));
        Assert.Equal("a", first.SafeSource);
        Assert.Equal("\\u001Bb", second.SafeSource);
        Assert.True(first.StartsAnswerBlock);
        Assert.False(second.StartsAnswerBlock);
        Assert.Equal(
            "\n",
            PrettyPromptConsoleSurface.ProjectInteractiveOutputItem(first, 120)[0].Text);
        Assert.NotEqual(
            "\n",
            PrettyPromptConsoleSurface.ProjectInteractiveOutputItem(second, 120)[0].Text);
        Assert.Null(source.Flush());
        var afterBoundary = Assert.IsType<TuiSourceOutput>(source.Append("next"));
        Assert.True(afterBoundary.StartsAnswerBlock);
    }

    /// <summary>Suppresses empty accepted deltas without opening a source-mode answer.</summary>
    [Fact]
    public static void Collector_SourceModeEmptyDelta_DoesNotBecomeVisible()
    {
        var source = new TuiModelAnswerCollector(renderMarkdown: false);

        Assert.Null(source.Append(string.Empty));
        Assert.Null(source.Flush());
        var firstVisible = Assert.IsType<TuiSourceOutput>(source.Append("accepted"));

        Assert.True(firstVisible.StartsAnswerBlock);
        Assert.Equal("accepted", firstVisible.SafeSource);
    }

    /// <summary>An oversized buffered answer starts its source projection when it first becomes visible.</summary>
    [Fact]
    public static void Collector_OversizedRenderedAnswer_SourceFallbackStartsAnswerBlock()
    {
        var collector = new TuiModelAnswerCollector(renderMarkdown: true);
        string buffered = new('a', TuiMarkdownParser.MaximumSourceBytes);

        Assert.Null(collector.Append(buffered));
        var fallback = Assert.IsType<TuiSourceOutput>(collector.Append("b"));
        var continuation = Assert.IsType<TuiSourceOutput>(collector.Append("c"));

        Assert.True(fallback.StartsAnswerBlock);
        Assert.Equal(buffered + "b", fallback.SafeSource);
        Assert.Equal(
            "\n",
            PrettyPromptConsoleSurface.ProjectInteractiveOutputItem(fallback, 120)[0].Text);
        Assert.False(continuation.StartsAnswerBlock);
        Assert.NotEqual(
            "\n",
            PrettyPromptConsoleSurface.ProjectInteractiveOutputItem(continuation, 120)[0].Text);
    }

    /// <summary>Redirected projection preserves printable Markdown source without semantic layout or reflow.</summary>
    [Fact]
    public static async Task WriteOutput_Redirected_PreservesSourceMarkersExactly()
    {
        const string source = "# Heading\n\nA **strong** [link](https://example.com).\u0085\ud800";
        var collector = new TuiModelAnswerCollector(renderMarkdown: true);
        Assert.Null(collector.Append(source));
        var output = Assert.IsType<TuiMarkdownOutput>(collector.Flush());
        var rawOutput = Assert.IsType<TuiRawSourceOutput>(
            PrettyPromptConsoleSurface.ProjectOutputItem(output, isOutputRedirected: true));
        using var writer = new StringWriter();

        await PrettyPromptConsoleSurface.WriteRedirectedMarkdownAsync(
            writer,
            rawOutput.RawSource,
            TestContext.Current.CancellationToken);

        Assert.Equal(source, writer.ToString());
        Assert.Throws<InvalidOperationException>(() =>
            PrettyPromptConsoleSurface.ProjectOutputItem(rawOutput, isOutputRedirected: false));
    }

    /// <summary>Flushes terminal output before a subsequent interactive read can begin.</summary>
    [Fact]
    public static async Task WriteOutput_Interactive_FlushesConsoleWriter()
    {
        using var writer = new FlushTrackingWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        var surface = new PrettyPromptConsoleSurface(
            isOutputRedirected: false,
            ansiConsole: console);

        await surface.WriteAsync(
            "Plan review: 1 approve, 2 reject, 3 revise, 4 cancel run\n",
            TuiTextRole.Status,
            TestContext.Current.CancellationToken);

        Assert.True(writer.WasFlushed);
        Assert.Contains("Plan review:", writer.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Shutdown flushes an incomplete Markdown answer as source rather than parsing it as complete.</summary>
    [Fact]
    public static void FlushFinalAnswerForShutdown_IncompleteMarkdown_UsesSourceFallback()
    {
        var collector = new TuiModelAnswerCollector(renderMarkdown: true);
        Assert.Null(collector.Append("**partial"));

        var output = Assert.IsType<TuiSourceOutput>(
            ConversationalShell.FlushFinalAnswerForShutdown(collector));

        Assert.Equal("**partial", output.SafeSource);
        Assert.Null(ConversationalShell.FlushFinalAnswerForShutdown(collector));
    }

    /// <summary>A cancelled surface admission retries the buffered answer as source without losing it.</summary>
    [Fact]
    public static async Task WriteOutput_CancelledAdmission_TerminalizesBufferedSource()
    {
        var collector = new TuiModelAnswerCollector(renderMarkdown: true);
        Assert.Null(collector.Append("**buffered**\u001b"));
        var markdown = Assert.IsType<TuiMarkdownOutput>(collector.Flush());
        var surface = new CancellationRejectingSurface();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ConversationalShell.WriteOutputWithCancellationFallbackAsync(
                surface,
                [markdown],
                cancellation.Token));

        Assert.Equal(2, surface.Attempts);
        var source = Assert.IsType<TuiSourceOutput>(Assert.Single(surface.WrittenItems));
        Assert.Equal("**buffered**\u001b", source.RawSource);
        Assert.Equal("**buffered**\\u001B", source.SafeSource);
    }

    /// <summary>Uses terminal-safe source when parsing fails or cancellation is already requested.</summary>
    [Fact]
    public static void Collector_ParserFailureOrCancellation_FallsBackExactlyOnce()
    {
        var failing = new TuiModelAnswerCollector(renderMarkdown: true, new FailingParser());
        Assert.Null(failing.Append("a\u001bb"));
        var failure = Assert.IsType<TuiSourceOutput>(failing.Flush());
        Assert.Equal("a\\u001Bb", failure.SafeSource);
        Assert.Null(failing.Flush());

        var unsafeDocument = new TuiModelAnswerCollector(renderMarkdown: true, new UnsafeDocumentParser());
        Assert.Null(unsafeDocument.Append("accepted"));
        var unsafeFallback = Assert.IsType<TuiSourceOutput>(unsafeDocument.Flush());
        Assert.Equal("accepted", unsafeFallback.SafeSource);

        var cancelled = new TuiModelAnswerCollector(renderMarkdown: true);
        Assert.Null(cancelled.Append("**partial"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancellationOutput = Assert.IsType<TuiSourceOutput>(cancelled.Flush(cancellation.Token));
        Assert.Equal("**partial", cancellationOutput.SafeSource);
    }

    /// <summary>Every compiled theme inherits semantic Markdown decoration and style suppression removes only style.</summary>
    [Fact]
    public static void MarkdownRoles_CompiledThemesAndNoColor_HaveSafeFallbacks()
    {
        foreach (var configured in BuiltInThemes.Create())
        {
            var resolver = new TuiThemeResolver(configured.Theme);
            Assert.True(resolver.Resolve(TuiTextRole.MarkdownHeading).Decorations?.HasFlag(TuiTextDecoration.Bold));
            Assert.True(resolver.Resolve(TuiTextRole.MarkdownEmphasis).Decorations?.HasFlag(TuiTextDecoration.Italic));
        }

        var customTheme = new TuiTheme(
            "custom",
            [KeyValuePair.Create(
                TuiTextRole.Default,
                new TuiTextStyle(Decorations: TuiTextDecoration.None))]);
        var customResolver = new TuiThemeResolver(customTheme);
        Assert.True(customResolver
            .Resolve(TuiTextRole.MarkdownHeading)
            .Decorations?.HasFlag(TuiTextDecoration.Bold));

        var suppressed = new TuiThemeResolver(BuiltInThemes.Create()[1].Theme, suppressStyles: true);
        Assert.Equal(new TuiTextStyle(), suppressed.Resolve(TuiTextRole.MarkdownHeading));
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

    private sealed class CancellationRejectingSurface : IConsoleSurface
    {
        internal int Attempts { get; private set; }

        internal IReadOnlyList<TuiOutputItem> WrittenItems { get; private set; } = [];

        public Task SetPromptAsync(string prompt, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<ConsoleInput> ReadAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<int> SelectAsync(
            string title,
            IReadOnlyList<string> choices,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task ShowStatusUntilAsync(
            string text,
            Task operation,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task WriteAsync(
            string text,
            TuiTextRole role = TuiTextRole.Default,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task WriteOutputAsync(
            IReadOnlyList<TuiOutputItem> items,
            CancellationToken cancellationToken = default)
        {
            Attempts++;
            cancellationToken.ThrowIfCancellationRequested();
            WrittenItems = items.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class FlushTrackingWriter : StringWriter
    {
        internal bool WasFlushed { get; private set; }

        public override Task FlushAsync()
        {
            WasFlushed = true;
            return base.FlushAsync();
        }
    }

    private sealed class FailingParser : ITuiMarkdownParser
    {
        /// <inheritdoc />
        public TuiMarkdownParseResult Parse(string source)
        {
            throw new InvalidOperationException("Injected parser failure.");
        }
    }

    private sealed class UnsafeDocumentParser : ITuiMarkdownParser
    {
        /// <inheritdoc />
        public TuiMarkdownParseResult Parse(string source)
        {
            return new(
                        new TuiMarkdownDocument(
                            [new TuiMarkdownParagraph([new TuiMarkdownSpan("unsafe\u001b")])]),
                        source,
                        null);
        }
    }
}
