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
public static class TuiKitFrontendTests
{
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
        modal.Render(new BufferSurface(cells));
        var text = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.Contains("> same label", text, StringComparison.Ordinal);
        Assert.True(modal.HandleKey(KeyEvent.Special(KeyCode.Down)));
        Assert.True(modal.HandleKey(KeyEvent.Special(KeyCode.F2)));
        modal.Render(new BufferSurface(cells));
        Assert.Contains("same label", TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
    }

    /// <summary>Keyboard help is static and conceals the application frame beneath it.</summary>
    [Fact]
    public static void KeyHelpIsNotSelectableAndCoversUnderlyingFrame()
    {
        var cells = new CellBuffer(40, 12);
        var surface = new BufferSurface(cells);
        surface.Fill(new Rect(0, 0, 40, 12), Cell.Glyph("X", CellStyle.Default, 1));
        var modal = new KeyHelpModal("Key help — Esc closes", ["F7 — focus", "Esc — close"]);

        modal.Render(surface);

        var lines = TUIKit.Testing.Snapshot.ToText(cells).Split('\n');
        Assert.StartsWith(" Key help", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("  F7 — focus", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("> F7", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("X", lines[^1], StringComparison.Ordinal);
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

    /// <summary>The real UI loop preserves draft ownership across cancellation, prompts, and selectors.</summary>
    [Fact]
    public static async Task InputOwnershipAndModalCancellation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
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

    /// <summary>An early startup submission waits for the first conversation read without losing the next draft.</summary>
    [Fact]
    public static async Task StartupSubmissionIsQueued()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var backend = new HeadlessBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await surface.RunAsync(
            async token =>
        {
            backend.FeedInput("hello");
            await Task.Delay(TimeSpan.FromMilliseconds(100), token);
            _ = backend.TakeOutput();
            backend.FeedInput("\rnext draft");
            await Task.Delay(TimeSpan.FromMilliseconds(100), token);
            var committedOutput = backend.TakeOutput();
            Assert.Contains("message queued", committedOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("hello", committedOutput, StringComparison.Ordinal);

            var queued = await surface.ReadComposerAsync(new ComposerRequest("ordinary"), token);
            Assert.Equal("hello", queued.Text);

            var next = surface.ReadComposerAsync(new ComposerRequest("ordinary"), token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput("\r");
            Assert.Equal("next draft", (await next).Text);
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
                backend.FeedInput("draft\r\u001b[27u\u001b[27u");
                Assert.Equal(ActiveRunInputSignal.SteeringRequested, await lease.ReadAsync(token));
                Assert.Equal(ActiveRunInputSignal.CancellationArmed, await lease.ReadAsync(token));
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
            Assert.Equal("draft", (await read).Text);
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
            backend.FeedInput("\u001b[<0;1;1M\u001b[<32;4;1M\u001b[<0;16;1m\u0003\u001b[18~copy me\u0001\u0003\r");

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
