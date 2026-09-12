namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Markdown;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Runs;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using TUIKit.Terminal;
using Xunit;

/// <summary>Fast checks of the TUIKit frontend's input and rendering boundaries.</summary>
[Collection("TUIKit terminal")]
public static class TuiKitFrontendTests
{
    /// <summary>Pane fills and ordinary text agree while explicit text backgrounds remain configurable.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void PaneBackgroundsOverrideInheritedTextBackgrounds(bool suppress)
    {
        var semanticTheme = new TuiTheme(
            "pane-test",
            [
                new(PresentationTextRole.Default, new(TuiColor.Parse("cyan"), TuiColor.Parse("#242121"), TuiTextDecoration.Italic)),
                new(PresentationTextRole.ComposerBackgroundPaneRole, new(Background: TuiColor.Parse("black"))),
                new(PresentationTextRole.OutputStreamPaneRole, new(Background: TuiColor.Parse("blue"))),
                new(PresentationTextRole.ComposerPrompt, new(TuiColor.Parse("yellow"), TuiColor.Parse("red"), TuiTextDecoration.Bold)),
                new(PresentationTextRole.Error, new(TuiColor.Parse("white"), TuiColor.Parse("red"))),
                new(PresentationTextRole.Status, new(TuiColor.Parse("green"))),
            ]);
        var theme = new ConfiguredTheme("Pane test", semanticTheme, TuiThemeUi.Default, false);
        var styles = new TuiKitStyles(theme, suppress);
        var composerStyle = styles.ResolveInPane(PresentationTextRole.Default, PresentationTextRole.ComposerBackgroundPaneRole);
        Assert.Equal(suppress ? Color.Default : Color.FromPalette(0), composerStyle.Background);
        Assert.Equal(styles.Resolve(PresentationTextRole.Default).Foreground, composerStyle.Foreground);
        Assert.Equal(styles.Resolve(PresentationTextRole.Default).Attributes, composerStyle.Attributes);
        Assert.Equal(
            styles.Resolve(PresentationTextRole.ComposerPrompt),
            styles.ResolveInPane(PresentationTextRole.ComposerPrompt, PresentationTextRole.ComposerBackgroundPaneRole));

        var composer = new TuiKitComposer { Style = composerStyle, Text = "draft" };
        var cells = new CellBuffer(40, 5);
        composer.Render(new BufferSurface(cells));
        Assert.Equal(composerStyle.Background, cells.Get(0, 0).Style.Background);
        Assert.Equal(composerStyle.Background, cells.Get(30, 3).Style.Background);

        var view = new TranscriptView
        {
            ResolveStyle = role => styles.ResolveInPane(role, PresentationTextRole.OutputStreamPaneRole),
        };
        view.Present(new PresentationBatch([new PresentationTextItem([
            new("plain", PresentationTextRole.Default),
            new("status", PresentationTextRole.Status),
            new("error", PresentationTextRole.Error),
        ])]));
        view.Render(new BufferSurface(cells));
        var paneBackground = suppress ? Color.Default : Color.FromPalette(4);
        Assert.Equal(paneBackground, cells.Get(0, 0).Style.Background);
        Assert.Equal(paneBackground, cells.Get(5, 0).Style.Background);
        Assert.Equal(paneBackground, cells.Get(30, 3).Style.Background);
        Assert.Equal(suppress ? Color.Default : Color.FromPalette(1), cells.Get(11, 0).Style.Background);
        Assert.Equal(styles.Resolve(PresentationTextRole.Status).Foreground, cells.Get(5, 0).Style.Foreground);
        Assert.Equal(styles.Resolve(PresentationTextRole.Error).Attributes, cells.Get(11, 0).Style.Attributes);
    }

    /// <summary>Both frontends project the same Markdown content and semantic roles.</summary>
    [Theory]
    [InlineData(40)]
    [InlineData(120)]
    public static void MarkdownMatchesExistingFrontend(int width)
    {
        const string source = "# Title\n\n**bold** *italic* ~~strike~~ `code` [link](https://example.com)\n\n> quote\n\n- [x] checked\n- plain\n\n1. ordered\n\n---\n\n```cs\nvar value = 1;\n```\n\n| Name | Value |\n|---|---|\n| first | detail |\n";
        var document = Assert.IsType<MarkdownDocument>(new MarkdownParser().Parse(source).Document);
        Assert.Equal(Threadsmith.Tui.TuiMarkdownLayout.Format(document, width), TuiMarkdownLayout.Format(document, width));
    }

    /// <summary>Grapheme edits and undo preserve exact Unicode and multiline paste.</summary>
    [Fact]
    public static void ComposerEditsWholeGraphemes()
    {
        var buffer = new ComposerBuffer();
        buffer.Insert("line\r\n界e\u0301👩‍💻");
        buffer.Delete(true);
        Assert.Equal("line\n界e\u0301", buffer.Text);
        buffer.Undo();
        Assert.Equal("line\n界e\u0301👩‍💻", buffer.Text);
        buffer.Redo();
        buffer.SelectAll();
        buffer.Insert("replacement");
        buffer.Undo();
        Assert.Equal("line\n界e\u0301", buffer.Text);
    }

    /// <summary>Selection highlights remain visible with color suppressed and preserve stable IDs.</summary>
    [Fact]
    public static void SelectorUsesStableIdsAndPlainMarker()
    {
        var modal = new ChoiceModal("Choose", [new("one", "same label"), new("two", "same label")]);
        var cells = new CellBuffer(40, 12);
        var surface = new BufferSurface(cells);
        surface.Fill(new Rect(0, 0, 40, 12), Cell.Glyph("X", CellStyle.Default, 1));
        modal.Render(surface);
        var text = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.Contains("> same label", text, StringComparison.Ordinal);
        var lines = text.Split('\n');
        Assert.StartsWith("X", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("XX", lines[2], StringComparison.Ordinal);
        Assert.Contains("╭", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("X", lines[^1], StringComparison.Ordinal);
        Assert.True(modal.HandleKey(KeyEvent.Special(KeyCode.Down)));
        Assert.True(modal.HandleKey(KeyEvent.Special(KeyCode.F2)));
        modal.Render(new BufferSurface(cells));
        Assert.Contains("same label", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
    }

    /// <summary>Keyboard help is static within a centered frame with visible surrounding application rows.</summary>
    [Fact]
    public static void KeyHelpIsNotSelectableAndCentersOverUnderlyingFrame()
    {
        var cells = new CellBuffer(40, 12);
        var surface = new BufferSurface(cells);
        surface.Fill(new Rect(0, 0, 40, 12), Cell.Glyph("X", CellStyle.Default, 1));
        var modal = new KeyHelpModal("Key help — Esc closes", ["F7 — focus", "Esc — close"]);

        modal.Render(surface);

        var lines = TUIKit.Testing.Snapshot.ToText(cells).Split('\n');
        Assert.Contains("Key help", lines[2], StringComparison.Ordinal);
        Assert.Contains("F7 — focus", lines[4], StringComparison.Ordinal);
        Assert.DoesNotContain("> F7", lines[4], StringComparison.Ordinal);
        Assert.StartsWith("X", lines[^1], StringComparison.Ordinal);
    }

    /// <summary>Every modal keeps its heading flush to the top, a blank row below it, and side and bottom padding.</summary>
    [Theory]
    [InlineData(40, 12)]
    [InlineData(80, 24)]
    [InlineData(120, 35)]
    public static void AllModalsKeepHeadingSpacingAndInteriorPadding(int width, int height)
    {
        var size = new Size(width, height);
        var discovery = new TuiKitCommandDiscovery(Threadsmith.Interaction.Commands.InteractiveCommandCatalog.All, _ => { });
        TUIKit.Modals.Modal[] modals =
        [
            new StartupModal("Logo", "Loading", [], () => { }, _ => CellStyle.Default),
            new ChoiceModal("Models", [new("one", "Model one")]),
            new ToggleModal(new InteractionToggleRequest("Tools", [new("one", "Tool one", "Tools", true)]), _ => CellStyle.Default, () => { }, () => { }),
            new KeyHelpModal("Help", ["F7 focuses output"]),
            new CommandHelpModal(Threadsmith.Interaction.Commands.InteractiveCommandCatalog.All, _ => CellStyle.Default, () => { }, () => { }),
            new CommandPaletteModal(discovery, string.Empty, () => size, _ => CellStyle.Default, () => { }, () => { }, () => { }),
        ];
        var frameWidth = Math.Min(90, width - 4);
        var frameHeight = Math.Min(24, height - 3);
        var left = (width - frameWidth) / 2;
        var top = (height - 1 - frameHeight) / 2;
        foreach (var modal in modals)
        {
            var cells = new CellBuffer(width, height);
            cells.Fill(new Rect(0, 0, width, height), Cell.Glyph("X", CellStyle.Default, 1));
            modal.Render(new BufferSurface(cells));

            Assert.Equal("╭", cells.Get(left, top).Grapheme);
            Assert.Equal("╮", cells.Get(left + frameWidth - 1, top).Grapheme);
            Assert.Equal("╰", cells.Get(left, top + frameHeight - 1).Grapheme);
            Assert.Equal("╯", cells.Get(left + frameWidth - 1, top + frameHeight - 1).Grapheme);
            Assert.NotEqual(" ", cells.Get(left + 2, top + 1).Grapheme);
            for (var x = left + 1; x < left + frameWidth - 1; x++)
            {
                Assert.Equal(" ", cells.Get(x, top + 2).Grapheme);
                Assert.Equal(" ", cells.Get(x, top + frameHeight - 2).Grapheme);
            }

            for (var y = top + 1; y < top + frameHeight - 1; y++)
            {
                Assert.Equal(" ", cells.Get(left + 1, y).Grapheme);
                Assert.Equal(" ", cells.Get(left + frameWidth - 2, y).Grapheme);
            }

            Assert.Equal("X", cells.Get(0, height - 1).Grapheme);
        }
    }

    /// <summary>Streaming fragments append continuously and stay bounded with pathological Unicode.</summary>
    [Fact]
    public static void TranscriptPreservesChunksAndBounds()
    {
        var view = new TranscriptView();
        view.Present(new PresentationBatch([new PresentationTextItem([new("hel", PresentationTextRole.Default), new("lo\nworld", PresentationTextRole.Default)])]));
        Assert.Equal(["hello", "world"], view.Lines);
        view.Present(new PresentationBatch([new PresentationTextItem([new("a" + new string('\u0301', TranscriptView.ByteLimit) + "done", PresentationTextRole.Default)])]));
        Assert.InRange(view.RetainedBytes, 0, TranscriptView.ByteLimit);
        Assert.Contains("done", string.Join(string.Empty, view.Lines), StringComparison.Ordinal);
    }

    /// <summary>Tiny line windows preserve graphemes without repeatedly scanning the remaining input.</summary>
    [Fact]
    public static void TranscriptTinyLineWindowPreservesLongUnicodeInput()
    {
        var text = new string('x', 20000) + "a\u0301😀end";
        var view = new TranscriptView(new TuiResourceLimits
        {
            MaximumTranscriptLineCharacters = 1,
            MaximumTranscriptLines = 30000,
        });
        view.Present(new PresentationBatch([new PresentationTextItem([new(text, PresentationTextRole.Default)])]));

        Assert.Equal(text, string.Concat(view.Lines));
        Assert.Contains("a\u0301", view.Lines);
        Assert.Contains("😀", view.Lines);
    }

    /// <summary>Input echoes have exactly one blank row regardless of prior line endings, including after resize.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Run cancelled.")]
    [InlineData("Run cancelled.\n")]
    [InlineData("Run cancelled.\n\n")]
    [InlineData("Run cancelled.\n\n\n\n")]
    [InlineData("Model set.\r\nUse /reasoning to select.\r\n")]
    [InlineData("Model set.\rUse /reasoning to select.\r\r\r")]
    [InlineData("Previous response.\n  \n\t\n")]
    public static void InputEchoNormalizesBlankLinesAndSurvivesResize(string previous)
    {
        var view = new TranscriptView();
        view.Present(new PresentationBatch([new PresentationTextItem([new(previous, PresentationTextRole.Status)])]));
        view.Render(new BufferSurface(new CellBuffer(80, 24)));

        view.EchoInput("repo > ", "/models");
        view.EchoInput("repo > ", "Next question\ncontinued");

        var prefix = previous.ReplaceLineEndings("\n").TrimEnd();
        var expected = (prefix.Length > 0 ? prefix + "\n" : string.Empty)
            + "\nrepo > /models\n\nrepo > Next question\ncontinued\n";
        Assert.Equal(expected, string.Join('\n', view.Lines));
        foreach (var width in new[] { 40, 120, 80 })
        {
            var cells = new CellBuffer(width, 24);
            view.Render(new BufferSurface(cells));
            Assert.Equal(expected, string.Join('\n', view.Lines));
            var rows = TUIKit.Testing.Snapshot.ToText(cells).Split('\n');
            var promptRow = Array.FindIndex(rows, row => row.Contains("repo > /models", StringComparison.Ordinal));
            Assert.True(promptRow >= 1);
            Assert.True(string.IsNullOrWhiteSpace(rows[promptRow - 1]));
            if (promptRow > 1)
            {
                Assert.False(string.IsNullOrWhiteSpace(rows[promptRow - 2]));
            }
        }

        view.HandleKey(KeyEvent.Char('a', KeyModifiers.Ctrl));
        Assert.Equal(expected, view.SelectedText());
    }

    /// <summary>Markdown layout cannot reintroduce extra trailing blank rows before a retained input echo.</summary>
    [Fact]
    public static void InputEchoNormalizesMarkdownBoundaryAndRetainsStyles()
    {
        const string source = "## Finished\n\nThe answer contains enough words to wrap in a narrow window.\n\n\n";
        var document = Assert.IsType<MarkdownDocument>(new MarkdownParser().Parse(source).Document);
        var promptStyle = CellStyle.Default.WithAttribute(CellAttributes.Bold, true);
        var view = new TranscriptView
        {
            ResolveStyle = role => role == PresentationTextRole.ComposerPrompt ? promptStyle : CellStyle.Default,
        };
        view.Present(new PresentationBatch([new PresentationMarkdownItem(document, source, source, true)]));
        view.EchoInput("repo > ", "Next question");

        foreach (var width in new[] { 40, 120 })
        {
            var cells = new CellBuffer(width, 24);
            view.Render(new BufferSurface(cells));
            var rows = TUIKit.Testing.Snapshot.ToText(cells).Split('\n');
            var promptRow = Array.FindIndex(rows, row => row.Contains("repo > Next question", StringComparison.Ordinal));
            Assert.True(promptRow >= 2);
            Assert.True(string.IsNullOrWhiteSpace(rows[promptRow - 1]));
            Assert.False(string.IsNullOrWhiteSpace(rows[promptRow - 2]));
            Assert.Equal(promptStyle, cells.Get(0, promptRow).Style);
        }
    }

    /// <summary>Retained Markdown reruns semantic layout when the viewport widens.</summary>
    [Fact]
    public static void TranscriptReflowsMarkdownAfterResize()
    {
        const string source = "This paragraph contains enough words to wrap at forty columns but remain on one line at a wider terminal width.";
        var document = Assert.IsType<MarkdownDocument>(new MarkdownParser().Parse(source).Document);
        var view = new TranscriptView();
        view.Present(new PresentationBatch([new PresentationMarkdownItem(document, source, source, false)]));

        view.Render(new BufferSurface(new CellBuffer(40, 12)));
        var narrowLines = view.Lines.Count;
        view.Render(new BufferSurface(new CellBuffer(120, 12)));

        Assert.True(narrowLines > view.Lines.Count);
        Assert.Contains("wider terminal width", string.Join(' ', view.Lines), StringComparison.Ordinal);
    }

    /// <summary>Detached output is counted until End reattaches the viewport.</summary>
    [Fact]
    public static void TranscriptCountsUnseenOutputUntilReattached()
    {
        var view = new TranscriptView();
        view.Present(new PresentationBatch([new PresentationTextItem([new("first\nsecond", PresentationTextRole.Default)])]));
        view.Render(new BufferSurface(new CellBuffer(40, 1)));
        Assert.True(view.HandleKey(KeyEvent.Special(KeyCode.Home)));

        view.Present(new PresentationBatch([new PresentationTextItem([new("\nnew", PresentationTextRole.Default)])]));

        Assert.True(view.NewCount > 0);
        Assert.True(view.HandleKey(KeyEvent.Special(KeyCode.End)));
        Assert.Equal(0, view.NewCount);
    }

    /// <summary>A ready composer keeps the offscreen-answer notice and working navigation visible at narrow widths.</summary>
    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    public static async Task OffscreenAnswerNoticeSurvivesReadyPrompt(int width)
    {
        // Arrange a submitted request while the transcript is scrolled away from its tail.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var backend = new HeadlessBackend(width + 1, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
            {
                var firstRead = surface.ReadComposerAsync(new ComposerRequest("Threadsmith > "), token);
                var priorOutput = string.Join('\n', Enumerable.Range(0, 30).Select(index => $"Earlier tool output {index}"));
                await surface.PresentAsync(new PresentationBatch([new PresentationTextItem([new(priorOutput, PresentationTextRole.Default)])]), token);
                backend.FeedInput("\u001b[18~\u001b[H\u001b[18~request\r");
                Assert.Equal("request", (await firstRead).Text);

                // Act: finish an answer, reopen the composer, and force a complete frame.
                const string answer = "Recovered final answer";
                var document = Assert.IsType<MarkdownDocument>(new MarkdownParser().Parse(answer).Document);
                await surface.PresentAsync(new PresentationBatch([new PresentationMarkdownItem(document, answer, answer, true)]), token);
                var nextRead = surface.ReadComposerAsync(new ComposerRequest("Threadsmith > "), token);
                await surface.PresentAsync(new PresentationBatch([]), token);
                _ = backend.TakeOutput();
                backend.Resize(width, 24);
                await surface.PresentAsync(new PresentationBatch([]), token);
                await surface.PresentAsync(new PresentationBatch([]), token);
                var offscreenFrame = backend.TakeOutput();

                // Assert: unseen output remains visible on the border without reserving a hint row.
                Assert.Contains("new output", offscreenFrame, StringComparison.Ordinal);
                Assert.DoesNotContain("F7, End to follow", offscreenFrame, StringComparison.Ordinal);
                Assert.DoesNotContain("Ctrl+C copy selected text", offscreenFrame, StringComparison.Ordinal);
                Assert.DoesNotContain(answer, offscreenFrame, StringComparison.Ordinal);
                backend.FeedInput("\u001b[18~\u001b[F\u001b[18~\r");
                Assert.Equal(string.Empty, (await nextRead).Text);
                await surface.PresentAsync(new PresentationBatch([]), token);
                Assert.Contains(answer, backend.TakeOutput(), StringComparison.Ordinal);
            },
            timeout.Token);
    }

    /// <summary>The real UI loop preserves draft ownership across cancellation, prompts, and selectors.</summary>
    [Fact]
    public static async Task InputOwnershipAndModalCancellation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var backend = new HeadlessBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            var ordinary = surface.ReadComposerAsync(new ComposerRequest("ordinary"), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("\u001b[200~pending\r\ndraft\u001b[201~\u001b[27u");
            Assert.False((await ordinary).IsSubmitted);

            var secondary = surface.ReadComposerAsync(new ComposerRequest("secondary", ComposerPurpose.Secondary), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("answer\r");
            Assert.Equal("answer", (await secondary).Text);

            var selection = surface.SelectAsync(new InteractionSelectionRequest("Choose", [new("one", "duplicate"), new("two", "duplicate")]), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("\u001b[B\r");
            Assert.Equal("two", (await selection).SelectedOptionId);

            var cancelled = surface.SelectAsync(new InteractionSelectionRequest("Cancel", [new("one", "one")]), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("\u001b[27u");
            Assert.True((await cancelled).IsCancelled);

            var resumed = surface.ReadComposerAsync(new ComposerRequest("ordinary"), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("\ncontinued\r");
            Assert.Equal("pending\ndraft\ncontinued", (await resumed).Text);
        },
            timeout.Token);
        Assert.True(backend.IsStopped);
    }

    /// <summary>Input before the first composer read is discarded rather than queued.</summary>
    [Fact]
    public static async Task InputBeforeFirstReadIsDiscarded()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var backend = new HeadlessBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            backend.FeedInput("hello\rdiscard");
            await surface.PresentAsync(new PresentationBatch([]), token);
            var next = surface.ReadComposerAsync(new ComposerRequest("ordinary"), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("ready\r");
            Assert.Equal("ready", (await next).Text);
        },
            timeout.Token);
        Assert.True(backend.IsStopped);
    }

    /// <summary>A committed ordinary entry moves from the editor into the retained transcript.</summary>
    [Fact]
    public static async Task SubmittedConversationRemainsInRetainedTranscript()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var backend = new HeadlessBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            var read = surface.ReadComposerAsync(new ComposerRequest("repo > "), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("submitted text retained");
            await Task.Delay(TimeSpan.FromMilliseconds(100), token);
            _ = backend.TakeOutput();

            backend.FeedInput("\r");
            Assert.Equal("submitted text retained", (await read).Text);
            await Task.Delay(TimeSpan.FromMilliseconds(100), token);

            Assert.Contains("submitted text retained", backend.TakeOutput(), StringComparison.Ordinal);
        },
            timeout.Token);
        Assert.True(backend.IsStopped);
    }

    /// <summary>Steering hints follow the live timer and disappear after active input ends, without entering the transcript.</summary>
    [Fact]
    public static async Task ActiveRunHintsAreTransientAndFollowThinking()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var backend = new HeadlessBackend(120, 28);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            Assert.True(surface.Capabilities.SupportsRetainedRunHints);
            await surface.PresentAsync(new PresentationBatch([]), token);
            Assert.DoesNotContain("ENTER to steer", backend.TakeOutput(), StringComparison.Ordinal);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var activity = new InteractionActivity("THINKING", TimeProvider.System.GetTimestamp(), true, TimeProvider.System);
            var display = surface.PresentActivityUntilAsync(activity, completion.Task, token);
            var output = string.Empty;
            while (!output.Contains("THINKING", StringComparison.Ordinal))
            {
                await Task.Delay(10, token);
                output += backend.TakeOutput();
            }

            await using (var lease = Assert.IsAssignableFrom<IActiveRunInputLease>(surface.BeginActiveRunInput(TimeProvider.System)))
            {
                while (!output.Contains("ENTER to steer; ESC-ESC to cancel", StringComparison.Ordinal))
                {
                    await Task.Delay(10, token);
                    output += backend.TakeOutput();
                }

                Assert.Contains("THINKING", output, StringComparison.Ordinal);
                Assert.True(output.IndexOf("THINKING", StringComparison.Ordinal) < output.IndexOf("ENTER to steer", StringComparison.Ordinal));
            }

            completion.SetResult();
            await display;
            await surface.PresentAsync(new PresentationBatch([new PresentationTextItem([new("Response arrived", PresentationTextRole.Default)])]), token);
            backend.TakeOutput();
            backend.Resize(121, 28);
            var repaint = string.Empty;
            while (!repaint.Contains("Response arrived", StringComparison.Ordinal))
            {
                await Task.Delay(10, token);
                repaint += backend.TakeOutput();
            }

            Assert.DoesNotContain("ENTER to steer", repaint, StringComparison.Ordinal);
            Assert.DoesNotContain("THINKING", repaint, StringComparison.Ordinal);
        },
            timeout.Token);
    }

    /// <summary>Active-run chords emit semantic signals without consuming the ordinary draft.</summary>
    [Fact]
    public static async Task ActiveRunInputAndShutdown()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var backend = new HeadlessBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            await using (var lease = Assert.IsAssignableFrom<IActiveRunInputLease>(surface.BeginActiveRunInput(TimeProvider.System)))
            {
                backend.FeedInput("draft\r\u001b[27ux\u001b[27u");
                Assert.Equal(ActiveRunInputSignal.SteeringRequested, await lease.ReadAsync(token));
                Assert.Equal(ActiveRunInputSignal.CancellationArmed, await lease.ReadAsync(token));
                Assert.Equal(ActiveRunInputSignal.CancellationArmed, await lease.ReadAsync(token));
                backend.FeedInput("\u001b[27u");
                Assert.Equal(ActiveRunInputSignal.CancellationRequested, await lease.ReadAsync(token));
            }

            var pendingLease = Assert.IsAssignableFrom<IActiveRunInputLease>(surface.BeginActiveRunInput(TimeProvider.System));
            var pendingRead = pendingLease.ReadAsync(token);
            await pendingLease.DisposeAsync();
#pragma warning disable VSTHRD003 // The lease started and owns this pending read; disposal must cancel it.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingRead);
#pragma warning restore VSTHRD003

            var read = surface.ReadComposerAsync(new ComposerRequest("ordinary"), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("\r");
            Assert.Equal("draftx", (await read).Text);
        },
            timeout.Token);
        Assert.True(backend.IsStopped);
    }

    /// <summary>Ctrl+C copies selected transcript and draft text without invoking process cancellation.</summary>
    [Fact]
    public static async Task ControlCCopiesSelection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var backend = new HeadlessBackend(80, 24);
        var interruptions = 0;
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], () => interruptions++, backend);
        await surface.RunAsync(
            async token =>
        {
            await surface.PresentAsync(
                new PresentationBatch([
                    new PresentationTextItem([new("transcript copy", PresentationTextRole.Default)]),
                ]),
                token);
            var read = surface.ReadComposerAsync(new ComposerRequest("ordinary"), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            _ = backend.TakeOutput();
            backend.FeedInput("\u001b[<0;3;6M\u001b[<32;6;6M\u001b[<0;18;6m\u0003\u001b[18~copy me\u0001\u0003\r");

            Assert.Equal("copy me", (await read).Text);
            Assert.Equal(0, interruptions);
            var output = backend.TakeOutput();
            Assert.Contains(
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("copy me")),
                output,
                StringComparison.Ordinal);
            Assert.Contains(
                Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("transcript copy")),
                output,
                StringComparison.Ordinal);
        },
            timeout.Token);
        Assert.True(backend.IsStopped);
    }

    /// <summary>Ctrl+C without a selection keeps the existing process-cancellation behavior.</summary>
    [Fact]
    public static async Task ControlCWithoutSelectionCancels()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var backend = new HeadlessBackend(80, 24);
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], () => interrupted.TrySetResult(), backend);
        await surface.RunAsync(
            async token =>
        {
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("\u0003");
            await interrupted.Task.WaitAsync(token);
        },
            timeout.Token);
        Assert.True(backend.IsStopped);
    }

    /// <summary>A rejected UI mutation propagates its failure and restores the backend.</summary>
    [Fact]
    public static async Task RenderingFailureStopsBackend()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var backend = new HeadlessBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await Assert.ThrowsAsync<InvalidOperationException>(() => surface.RunAsync(
            token => surface.PresentAsync(new PresentationBatch([new PresentationRawSourceItem("not admitted")]), token),
            timeout.Token));
        Assert.True(backend.IsStopped);
    }
}
