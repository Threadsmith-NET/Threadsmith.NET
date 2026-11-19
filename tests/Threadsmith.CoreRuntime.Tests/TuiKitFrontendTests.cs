namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
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

    /// <summary>Broad Markdown fixtures retain their content and semantic roles at both widths.</summary>
    [Theory]
    [InlineData(40)]
    [InlineData(120)]
    public static void MarkdownPreservesContentAndSemanticRoles(int width)
    {
        const string source = "# Title\n\n**bold** *italic* ~~strike~~ `code` [link](https://example.com)\n\n> quote\n\n- [x] checked\n- plain\n\n1. ordered\n\n---\n\n```cs\nvar value = 1;\n```\n\n| Name | Value |\n|---|---|\n| first | detail |\n";
        var document = Assert.IsType<MarkdownDocument>(new MarkdownParser().Parse(source).Document);
        var segments = TuiMarkdownLayout.Format(document, width);
        Assert.NotEmpty(segments);
        var visible = string.Concat(segments.Select(segment => segment.Text));
        foreach (var text in new[] { "Title", "bold", "italic", "strike", "code", "link", "quote", "checked", "plain", "ordered", "var value = 1;", "Name", "Value", "first", "detail" })
        {
            Assert.Contains(text, visible, StringComparison.Ordinal);
        }

        Assert.Contains(segments, segment => segment.Text == "Title" && segment.Role == PresentationTextRole.MarkdownHeading);
        Assert.Contains(segments, segment => segment.Text == "bold" && segment.Role == PresentationTextRole.MarkdownStrong);
        Assert.Contains(segments, segment => segment.Text == "italic" && segment.Role == PresentationTextRole.MarkdownEmphasis);
        Assert.Contains(segments, segment => segment.Text == "strike" && segment.Role == PresentationTextRole.MarkdownStrikethrough);
        Assert.Contains(segments, segment => segment.Text == "code" && segment.Role == PresentationTextRole.MarkdownCode);
        Assert.Contains(segments, segment => segment.Text == "link" && segment.Role == PresentationTextRole.Hyperlink);
        Assert.DoesNotContain("https://example.com/", visible, StringComparison.Ordinal);
        Assert.Contains(segments, segment => segment.Text == "link" && segment.LinkTarget?.AbsoluteUri == "https://example.com/");
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

    /// <summary>The real UI loop preserves draft ownership across cancellation, prompts, and selectors.</summary>
    [Fact]
    public static async Task InputOwnershipAndModalCancellation()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new ObservedBackend(80, 24);
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

    /// <summary>Grouped choices retain stable IDs through real UI filtering, navigation, and cancellation.</summary>
    [Theory]
    [InlineData("\u001b[B\r", "beta", false)]
    [InlineData("Beta\r", "beta", false)]
    [InlineData("\u001b[27u", null, true)]
    public static async Task GroupedSelectionPreservesIdentityAndCancellation(string input, string? expectedId, bool cancelled)
    {
        using var backend = new ObservedBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], () => { }, backend);
        await surface.RunAsync(
            async token =>
        {
            var selection = surface.SelectAsync(
                new InteractionSelectionRequest(
                    "Models",
                    [
                        new("alpha", "Shared") { Group = "Alpha" },
                        new("beta", "Shared") { Group = "Beta" },
                    ]),
                token);
            await surface.PresentAsync(new PresentationBatch([]), token);
            backend.FeedInput(input);
            var result = await selection;
            Assert.Equal(expectedId, result.SelectedOptionId);
            Assert.Equal(cancelled, result.IsCancelled);
        },
            TestContext.Current.CancellationToken);
        Assert.True(backend.IsStopped);
    }

    /// <summary>Ctrl+C copies selected transcript and draft text without invoking process cancellation.</summary>
    [Fact]
    public static async Task ControlCCopiesSelection()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new ObservedBackend(80, 24);
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new ObservedBackend(80, 24);
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var backend = new ObservedBackend(80, 24);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], timeout.Cancel, backend);
        await Assert.ThrowsAsync<InvalidOperationException>(() => surface.RunAsync(
            token => surface.PresentAsync(new PresentationBatch([new PresentationRawSourceItem("not admitted")]), token),
            timeout.Token));
        Assert.True(backend.IsStopped);
        var output = backend.TakeOutput();
        var end = output.LastIndexOf(Ansi.EndSynchronizedUpdate, StringComparison.Ordinal);
        var alternateScreenExit = output.LastIndexOf(Ansi.ExitAltScreen, StringComparison.Ordinal);
        Assert.True(end >= 0);
        Assert.True(end < alternateScreenExit);
    }
}
