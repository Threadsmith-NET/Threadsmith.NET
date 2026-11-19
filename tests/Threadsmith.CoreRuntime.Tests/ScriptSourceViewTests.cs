namespace Threadsmith.CoreRuntime.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Interaction.Agents;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Themes;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Hosting;
using TUIKit.Input;
using Xunit;

/// <summary>Exercises bounded source display and exact source copy.</summary>
[Collection("TUIKit terminal")]
public sealed class ScriptSourceViewTests
{
    /// <summary>Character bounds preserve surrogate pairs.</summary>
    [Theory]
    [InlineData(8192, false)]
    [InlineData(8193, true)]
    public void PreviewHasIndependentCharacterLimit(int length, bool truncated)
    {
        var preview = SourceDocument.Preview(new string('x', length));
        Assert.Equal(Math.Min(length, 8192), preview.Text.Length);
        Assert.Equal(truncated, preview.Truncated);
        Assert.Equal(8191, SourceDocument.Preview(new string('x', 8191) + "😀").Text.Length);
    }

    /// <summary>Bracket-induced continuation chunks stay clickable without adding keyboard focus stops.</summary>
    [Fact]
    public void BracketedScriptLinkRemainsOneActionAcrossChunks()
    {
        var view = new TranscriptView(new TuiResourceLimits { MaximumTranscriptLineCharacters = 18 });
        var first = Item("first");
        var second = Item("second");
        var opened = new List<ToolSourceDetail>();
        view.SourceRequested = opened.Add;
        view.Present(new([first, second]));

        foreach (var width in new[] { 110, 16, 110 })
        {
            Click(view, Render(view, width), "ipt…");
            Assert.Same(first.SourceDetail, opened[^1]);
        }

        view.HandleKey(KeyEvent.Special(KeyCode.Down, KeyModifiers.Ctrl));
        view.HandleKey(KeyEvent.Special(KeyCode.Down, KeyModifiers.Ctrl));
        view.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.Equal(4, opened.Count);
        Assert.Same(first.SourceDetail, opened[^1]);
        view.HandleKey(KeyEvent.Special(KeyCode.Down, KeyModifiers.Ctrl));
        view.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Render(view);
        Assert.Contains("   │ second", view.Lines);
        Assert.Equal(4, opened.Count);
        view.HandleKey(KeyEvent.Special(KeyCode.Up, KeyModifiers.Ctrl));
        view.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.Equal(5, opened.Count);
        Assert.Same(first.SourceDetail, opened[^1]);

        Click(view, Render(view, 16), "ution");
        Assert.Contains("   │ first", view.Lines);
    }

    /// <summary>Full view has dedicated capacity and respects clipboard policy.</summary>
    [Fact]
    public void SourceModalRetainsMoreThanNormalTranscriptLinesAndReportsClipboardLimit()
    {
        var source = string.Concat(Enumerable.Repeat("// é\r\n", 1400)) + "// end";
        var copyCalls = 0;
        var modal = new ChoiceModal("script", [], new TuiResourceLimits { MaximumClipboardCopyBytes = 10 }, sourceDetail: Item(source).SourceDetail)
        {
            CopyRequested = _ =>
            {
                copyCalls++;
                return true;
            },
        };
        Render(modal);
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        Assert.Contains("// end", Render(modal), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Special(KeyCode.F4));
        Assert.Equal(0, copyCalls);
        Assert.Contains("Copy blocked", Render(modal), StringComparison.Ordinal);
    }

    /// <summary>Failed highlighting does not change copy offsets.</summary>
    [Fact]
    public void FallbackAndOffsetMappingKeepSourceReadableAndExact()
    {
        const string source = "\tfirst\r\nsecond\rthird\n";
        var document = new SourceDocument(source, "csharp", new ThrowingHighlighter());
        Assert.Equal("    first\nsecond\nthird\n", document.DisplaySource);
        Assert.Equal(new SourceHighlightSpan(0, document.DisplaySource.Length, PresentationTextRole.MarkdownCode), Assert.Single(document.Ranges));
        Assert.Equal(source, document.CopyRange(0, document.DisplaySource.Length));
        Assert.Equal("\tfirst\r\n", document.CopyRange(0, 10));
        var unknown = new SourceDocument(source, "unknown", new TuiKitSourceHighlighter());
        Assert.Equal(document.DisplaySource, unknown.DisplaySource);
        var highlighted = new TuiKitSourceHighlighter().Highlight("class Example { string value = \"text\"; } // comment", "csharp");
        Assert.Contains(highlighted, range => range.Role == PresentationTextRole.Status);
        Assert.Contains(highlighted, range => range.Role == PresentationTextRole.Muted);
    }

    /// <summary>Retention and mismatched identities cannot expose stale source.</summary>
    [Fact]
    public void RetentionDropsSourceWithoutReadingSavedPathsOrResurrectingActions()
    {
        var item = Item(new string('x', 1000));
        var view = new TranscriptView(new TuiResourceLimits { MaximumTranscriptBytes = 256 });
        view.Present(new PresentationBatch([item]));
        Assert.Contains("source unavailable", Render(view), StringComparison.Ordinal);
        Assert.DoesNotContain("View full", Render(view), StringComparison.Ordinal);
        view.Present(new PresentationBatch([]) { ReplaceOutput = true });
        Assert.DoesNotContain("C# script", Render(view), StringComparison.Ordinal);
        var completion = new ToolInvocationCompleted(SessionId.New(), DateTimeOffset.UtcNow, ToolInvocationId.New(), true) { SourceDetail = item.SourceDetail };
        Assert.Null(InteractionEventSegments.GetSourceDetail(completion));
    }

    /// <summary>The minimum omission marker uses no more bytes or rows than collapsed controls.</summary>
    [Fact]
    public void TightByteBudgetUsesExistingHeaderForOmission()
    {
        var view = new TranscriptView(new TuiResourceLimits { MaximumTranscriptBytes = 256 });
        var item = Item(string.Join('\n', Enumerable.Repeat("x", 20))) with
        {
            Segments = [new("Result" + new string('r', 187) + "\n", PresentationTextRole.Success)],
        };
        ToolSourceDetail? opened = null;
        view.SourceRequested = detail => opened = detail;
        view.Present(new([item]));
        Click(view, Render(view), "[+]");

        foreach (var width in new[] { 110, 70, 110 })
        {
            var screen = Render(view, width);
            Assert.Contains("Result", screen, StringComparison.Ordinal);
            Assert.Contains("[!] C# script", screen, StringComparison.Ordinal);
            Assert.Equal(0, view.Evicted);
        }

        Click(view, Render(view), "View full");
        Assert.Same(item.SourceDetail, opened);
        Click(view, Render(view), "[!]");
        Assert.Contains("[+] C# script", Render(view), StringComparison.Ordinal);
    }

    /// <summary>Batched styles track normalized offsets across continued chunks and logical newlines.</summary>
    [Fact]
    public void BatchedSourceStylesSurviveChunkingAndResize()
    {
        var styles = new TuiKitStyles(BuiltInThemes.Create()[0], false);
        var view = new TranscriptView(new TuiResourceLimits { MaximumTranscriptLineCharacters = 4 })
        {
            ResolveStyle = styles.Resolve,
        };
        view.PresentSource(new SourceDocument("\tclass\r\n42", "csharp", new TuiKitSourceHighlighter()));
        foreach (var width in new[] { 80, 60, 80 })
        {
            var cells = new CellBuffer(width, 12);
            view.Render(new BufferSurface(cells));
            Assert.Equal(["    ", "clas", "s", "42"], view.Lines);
            Assert.Equal(styles.Resolve(PresentationTextRole.Status).Foreground, cells.Get(0, 1).Style.Foreground);
            Assert.Equal(styles.Resolve(PresentationTextRole.Status).Foreground, cells.Get(0, 2).Style.Foreground);
            Assert.Equal(styles.Resolve(PresentationTextRole.Warning).Foreground, cells.Get(0, 3).Style.Foreground);
        }
    }

    /// <summary>Token-dense source uses bounded existing chunks and remains exact across width changes.</summary>
    [Fact]
    public void NearLimitTokenDenseSourceSurvivesChunkingAndReflow()
    {
        var source = "var values = new[]{" + string.Concat(Enumerable.Repeat("1,", 32740)) + "0};";
        Assert.InRange(source.Length, 65000, ToolSourceDetail.MaximumCharacters);
        string? copied = null;
        var modal = new ChoiceModal("script", [], sourceDetail: Item(source).SourceDetail)
        {
            CopyRequested = text =>
            {
                copied = text;
                return true;
            },
        };
        Render(modal);
        Render(modal, 70);
        modal.HandleKey(KeyEvent.Special(KeyCode.End));
        Assert.Contains("0};", Render(modal, 70), StringComparison.Ordinal);
        modal.HandleKey(KeyEvent.Char('a', KeyModifiers.Ctrl));
        modal.HandleKey(KeyEvent.Special(KeyCode.F6));
        Assert.Equal(source, copied);
        modal.HandleKey(KeyEvent.Special(KeyCode.F4));
        Assert.Equal(source, copied);
    }

    /// <summary>Clipboard policy counts UTF-8 bytes, not source characters.</summary>
    [Theory]
    [InlineData('x', 65536, true)]
    [InlineData('é', 32768, true)]
    [InlineData('é', 32769, false)]
    public void FullCopyHonorsUtf8Boundary(char character, int length, bool allowed)
    {
        var source = new string(character, length);
        string? copied = null;
        var modal = new ChoiceModal("script", [], sourceDetail: Item(source).SourceDetail)
        {
            CopyRequested = value =>
            {
                copied = value;
                return true;
            },
        };
        modal.HandleKey(KeyEvent.Special(KeyCode.F4));
        Assert.Equal(allowed ? source : null, copied);
        if (!allowed)
        {
            Assert.Contains("Copy blocked", Render(modal), StringComparison.Ordinal);
        }
    }

    /// <summary>The utility modal preserves the draft and uses the existing clipboard transport.</summary>
    [Fact]
    public async Task SurfaceOpensSourceAndRestoresComposer()
    {
        const string source = "// full-source-copy\r\n\t42";
        using var backend = new ObservedBackend(100, 30);
        await using var surface = new TuiKitSurface(BuiltInThemes.Create()[0], () => throw new InvalidOperationException("Unexpected interrupt"), backend);
        await surface.RunAsync(
            async token =>
            {
                await surface.PresentAsync(new([Item(source)]), token);
                var input = surface.ReadComposerAsync(new ComposerRequest("ordinary"), token);
                await surface.PresentAsync(new([]), token);
                backend.FeedInput("draft\u001b[18~\u001b[1;5B\u001b[1;5B\r\u001bOS\u001b[27u\u001b[18~\r");
                Assert.Equal("draft", (await input).Text);
            },
            TestContext.Current.CancellationToken);
        Assert.Contains(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(source)), backend.TakeOutput(), StringComparison.Ordinal);
    }

    private static PresentationTextItem Item(string source)
    {
        return new([new("Result\n", PresentationTextRole.Success)])
        {
            SourceDetail = new ToolSourceDetail
            {
                ToolInvocationId = ToolInvocationId.New(),
                Language = "csharp",
                Availability = ToolSourceAvailability.Available,
                Source = source,
            },
        };
    }

    private static string Render(TranscriptView view, int width = 110)
    {
        var buffer = new CellBuffer(width, 40);
        view.Render(new BufferSurface(buffer));
        return TUIKit.Testing.Snapshot.ToText(buffer);
    }

    private static string Render(ChoiceModal modal, int width = 110, int height = 40)
    {
        var buffer = new CellBuffer(width, height);
        modal.Render(new BufferSurface(buffer));
        return TUIKit.Testing.Snapshot.ToText(buffer);
    }

    private static void Click(TranscriptView view, string screen, string label, bool firstMatch = true)
    {
        var rows = screen.Split('\n');
        var index = firstMatch ? Array.FindIndex(rows, row => row.Contains(label, StringComparison.Ordinal))
            : Array.FindLastIndex(rows, row => row.Contains(label, StringComparison.Ordinal));
        Assert.True(index >= 0, screen);
        ClickAt(view, rows[index].IndexOf(label, StringComparison.Ordinal), index);
    }

    private static void ClickAt(TranscriptView view, int x, int y)
    {
        view.HandleMouse(new(MouseEventKind.Press, MouseButton.Left, x, y, KeyModifiers.None, 1));
        view.HandleMouse(new(MouseEventKind.Release, MouseButton.Left, x, y, KeyModifiers.None, 1));
    }

    private sealed class ThrowingHighlighter : ISourceHighlighter
    {
        public IReadOnlyList<SourceHighlightSpan> Highlight(string source, string language)
        {
            throw new InvalidOperationException("unsupported display");
        }
    }
}
