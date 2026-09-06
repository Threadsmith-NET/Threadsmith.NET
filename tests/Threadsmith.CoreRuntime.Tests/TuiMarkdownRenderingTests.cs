namespace Threadsmith.CoreRuntime.Tests;

using System.Text;
using Microsoft.Extensions.Configuration;
using PrettyPrompt.Consoles;
using Spectre.Console;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Markdown;
using Threadsmith.Interaction.Presentation;
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
        Assert.Equal(
            "\n",
            Assert.IsType<PresentationTextSegment>(PrettyPromptConsoleSurface
                .ProjectInteractiveOutputItem(renderedOutput, 120)[0])
                .Text);
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
        Assert.Equal(
            "\n",
            PrettyPromptConsoleSurface.ProjectInteractiveOutputItem(first, 120)[0].Text);
        Assert.NotEqual(
            "\n",
            PrettyPromptConsoleSurface.ProjectInteractiveOutputItem(second, 120)[0].Text);
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
        var collector = new ModelAnswerCollector(renderMarkdown: true);
        Assert.Null(collector.Append(source));
        var output = Assert.IsType<PresentationMarkdownItem>(collector.Flush());
        var rawOutput = Assert.IsType<PresentationRawSourceItem>(
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
            PresentationTextRole.Status,
            TestContext.Current.CancellationToken);

        Assert.True(writer.WasFlushed);
        Assert.Contains("Plan review:", writer.ToString(), StringComparison.Ordinal);
    }

    /// <summary>An empty live composer yields and redraws around background output without a physical key.</summary>
    [Fact]
    public static async Task WriteOutput_EmptyComposer_RendersWithoutPhysicalInput()
    {
        using var writer = new StringWriter();
        var ansiConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        var promptConsole = new QueuePromptConsole();
        var surface = new PrettyPromptConsoleSurface(
            isOutputRedirected: false,
            ansiConsole: ansiConsole,
            promptConsole: promptConsole);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = Task.Run(() => surface.ReadAsync(cancellation.Token), cancellation.Token);
        await promptConsole.PollingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var write = surface.WriteAsync(
            "External changes detected; updating semantic model...\n",
            PresentationTextRole.Status,
            cancellation.Token);

        await write.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains("External changes detected", writer.ToString(), StringComparison.Ordinal);
        var yielded = await read.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(yielded.IsSubmitted);
        Assert.Equal(InteractionInputKind.IdleOutputYield, yielded.Kind);

        var resumedRead = Task.Run(() => surface.ReadAsync(cancellation.Token), cancellation.Token);
        await promptConsole.EnqueueAsync(CreateKey('\r', ConsoleKey.Enter));
        var input = await resumedRead.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(input.IsSubmitted);
        Assert.Empty(input.Text);
    }

    /// <summary>Repeated semantic composer reads retain PrettyPrompt's Up-arrow history.</summary>
    [Fact]
    public static async Task ReadComposer_RepeatedPrompt_RetainsHistory()
    {
        using var writer = new StringWriter();
        var ansiConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        var promptConsole = new QueuePromptConsole();
        IInteractionSurface surface = new PrettyPromptConsoleSurface(
            isOutputRedirected: false,
            ansiConsole: ansiConsole,
            promptConsole: promptConsole);
        var request = new ComposerRequest("threadsmith > ", ComposerPurpose.Conversation);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var firstRead = Task.Run(
            () => surface.ReadComposerAsync(request, cancellation.Token),
            cancellation.Token);
        await promptConsole.EnqueueAsync(CreateKey('h', ConsoleKey.H));
        await promptConsole.EnqueueAsync(CreateKey('i', ConsoleKey.I));
        await promptConsole.EnqueueAsync(CreateKey('\r', ConsoleKey.Enter));
        var first = await firstRead.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("hi", first.Text);

        var historyRead = Task.Run(
            () => surface.ReadComposerAsync(request, cancellation.Token),
            cancellation.Token);
        await promptConsole.EnqueueAsync(CreateKey('\0', ConsoleKey.UpArrow));
        await promptConsole.EnqueueAsync(CreateKey('\r', ConsoleKey.Enter));
        var recalled = await historyRead.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("hi", recalled.Text);
    }

    /// <summary>A draft holds background output until deleting its last character makes the composer empty.</summary>
    [Fact]
    public static async Task WriteOutput_DraftClearedToEmpty_RendersWithoutSubmission()
    {
        using var writer = new StringWriter();
        var ansiConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        var promptConsole = new QueuePromptConsole();
        var surface = new PrettyPromptConsoleSurface(
            isOutputRedirected: false,
            ansiConsole: ansiConsole,
            promptConsole: promptConsole);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var read = Task.Run(() => surface.ReadAsync(cancellation.Token), cancellation.Token);
        await promptConsole.PollingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await promptConsole.EnqueueAsync(CreateKey('a', ConsoleKey.A));

        var write = surface.WriteAsync(
            "Semantic model updated (1 file).\n",
            PresentationTextRole.Status,
            cancellation.Token);
        Assert.False(write.IsCompleted);
        await promptConsole.EnqueueAsync(CreateKey('\b', ConsoleKey.Backspace));

        await write.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains("Semantic model updated", writer.ToString(), StringComparison.Ordinal);
        var yielded = await read.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(yielded.IsSubmitted);
        Assert.Equal(InteractionInputKind.IdleOutputYield, yielded.Kind);

        var resumedRead = Task.Run(() => surface.ReadAsync(cancellation.Token), cancellation.Token);
        await promptConsole.EnqueueAsync(CreateKey('\r', ConsoleKey.Enter));
        var input = await resumedRead.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(input.IsSubmitted);
        Assert.Empty(input.Text);
    }

    /// <summary>Shutdown flushes an incomplete Markdown answer as source rather than parsing it as complete.</summary>
    [Fact]
    public static void FlushFinalAnswerForShutdown_IncompleteMarkdown_UsesSourceFallback()
    {
        var collector = new ModelAnswerCollector(renderMarkdown: true);
        Assert.Null(collector.Append("**partial"));

        var output = Assert.IsType<PresentationSourceItem>(
            ConversationalShell.FlushFinalAnswerForShutdown(collector));

        Assert.Equal("**partial", output.SafeSource);
        Assert.Null(ConversationalShell.FlushFinalAnswerForShutdown(collector));
    }

    /// <summary>A cancelled surface admission retries the buffered answer as source without losing it.</summary>
    [Fact]
    public static async Task WriteOutput_CancelledAdmission_TerminalizesBufferedSource()
    {
        var collector = new ModelAnswerCollector(renderMarkdown: true);
        Assert.Null(collector.Append("**buffered**\u001b"));
        var markdown = Assert.IsType<PresentationMarkdownItem>(collector.Flush());
        var surface = new CancellationRejectingSurface();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ConversationalShell.WriteOutputWithCancellationFallbackAsync(
                surface,
                [markdown],
                cancellation.Token));

        Assert.Equal(2, surface.Attempts);
        var source = Assert.IsType<PresentationSourceItem>(Assert.Single(surface.WrittenItems));
        Assert.Equal("**buffered**\u001b", source.RawSource);
        Assert.Equal("**buffered**\\u001B", source.SafeSource);
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

    /// <summary>Every compiled theme inherits semantic Markdown decoration and style suppression removes only style.</summary>
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
        Assert.True(customResolver
            .Resolve(PresentationTextRole.MarkdownHeading)
            .Decorations?.HasFlag(TuiTextDecoration.Bold));

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

    private sealed class CancellationRejectingSurface : IConsoleSurface
    {
        internal int Attempts { get; private set; }

        internal IReadOnlyList<PresentationItem> WrittenItems { get; private set; } = [];

        public Task SetPromptAsync(string prompt, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<InteractionInput> ReadAsync(CancellationToken cancellationToken = default)
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
            PresentationTextRole role = PresentationTextRole.Default,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task WriteOutputAsync(
            IReadOnlyList<PresentationItem> items,
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

    private static ConsoleKeyInfo CreateKey(char character, ConsoleKey key)
    {
        return new ConsoleKeyInfo(character, key, shift: false, alt: false, control: false);
    }

    private sealed class QueuePromptConsole : IConsole
    {
        private readonly Lock _gate = new();
        private readonly Queue<(ConsoleKeyInfo Key, TaskCompletionSource Read)> _keys = [];

        public TaskCompletionSource PollingStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int CursorTop => 0;

        public int BufferWidth => 120;

        public int WindowHeight => 40;

        public int WindowTop => 0;

        public bool KeyAvailable
        {
            get
            {
                PollingStarted.TrySetResult();
                lock (_gate)
                {
                    return _keys.Count > 0;
                }
            }
        }

        public bool IsErrorRedirected => false;

        public bool CaptureControlC { get; set; }

#pragma warning disable CS0067 // Required by PrettyPrompt's console contract.
        public event ConsoleCancelEventHandler? CancelKeyPress;
#pragma warning restore CS0067

        public Task EnqueueAsync(ConsoleKeyInfo key)
        {
            var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _keys.Enqueue((key, read));
            }

            return read.Task;
        }

        public ConsoleKeyInfo ReadKey(bool intercept)
        {
            (ConsoleKeyInfo Key, TaskCompletionSource Read) entry;
            lock (_gate)
            {
                entry = _keys.Dequeue();
            }

            entry.Read.TrySetResult();
            return entry.Key;
        }

        public void Clear()
        {
        }

        public void InitVirtualTerminalProcessing()
        {
        }

        public void SetNewlineAutoReturn(bool enabled)
        {
        }

        public void SetModifyOtherKeys(bool enabled)
        {
        }

        public void Write(StringBuilder value, bool hideCursor)
        {
        }

        public void WriteLine(StringBuilder value, bool hideCursor)
        {
        }

        public void WriteError(StringBuilder value, bool hideCursor)
        {
        }

        public void WriteErrorLine(StringBuilder value, bool hideCursor)
        {
        }

        public void Write(string? value)
        {
        }

        public void WriteLine(string? value)
        {
        }

        public void WriteError(string? value)
        {
        }

        public void WriteErrorLine(string? value)
        {
        }

        public void Write(ReadOnlySpan<char> value)
        {
        }

        public void WriteLine(ReadOnlySpan<char> value)
        {
        }

        public void WriteError(ReadOnlySpan<char> value)
        {
        }

        public void WriteErrorLine(ReadOnlySpan<char> value)
        {
        }

        public void ShowCursor()
        {
        }

        public void HideCursor()
        {
        }
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
