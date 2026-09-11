namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Interaction.Commands;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using Xunit;

/// <summary>Checks the public fuzzy-list adapter, query limits, themes and modal boundaries.</summary>
public static class CommandPaletteModalTests
{
    /// <summary>Palette selection returns stable identity and invokes only its injected local edit.</summary>
    [Fact]
    public static async Task FuzzySearchAcceptsIdentityAndRecoversFromNoMatches()
    {
        string? accepted = null;
        var entry = new InteractiveCommandDescriptor("/fixture", "/fixture [argument]", "Unique constellation operation");
        var modal = Create(new TuiKitCommandDiscovery([entry], value => accepted = value));
        modal.HandlePaste("no such result");
        modal.HandleKey(KeyEvent.Special(KeyCode.Down));
        modal.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.False(modal.IsClosed);
        Assert.Null(accepted);
        modal.HandleKey(KeyEvent.Char('a', KeyModifiers.Ctrl));
        modal.HandlePaste("cnstltn");
        modal.HandleKey(KeyEvent.Special(KeyCode.Enter));
        Assert.Equal(entry.Name, accepted);
        Assert.Equal(entry.Name, await modal.Completion.WaitAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Centered clearing keeps surrounding rows intact; labels use whole Unicode graphemes and live theme roles.</summary>
    [Theory]
    [InlineData(40, 12)]
    [InlineData(80, 24)]
    [InlineData(120, 40)]
    public static void PaletteRendersUnicodeAndCurrentThemeWithinFrame(int width, int height)
    {
        var entry = new InteractiveCommandDescriptor("/fixture", "/fixture [value]", "\u754ce\u0301\U0001f469\u200d\U0001f4bb description");
        var normal = CellStyle.Default;
        var modal = new CommandPaletteModal(
            new TuiKitCommandDiscovery([entry], _ => { }),
            string.Empty,
            () => new Size(width, height),
            _ => normal,
            () => { },
            () => { },
            () => { });
        var cells = new CellBuffer(width, height);
        cells.Fill(new Rect(0, 0, width, height), Cell.Glyph("X", normal, 1));
        modal.Render(new BufferSurface(cells));
        var text = TUIKit.Testing.Snapshot.ToText(cells);
        Assert.Contains(entry.Usage, text, StringComparison.Ordinal);
        Assert.Contains(entry.Description, text, StringComparison.Ordinal);
        var left = ((width - Math.Min(90, width - 4)) / 2) + 2;
        var top = ((height - 1 - Math.Min(24, height - 3)) / 2) + 1;
        Assert.Equal("\u754c", cells.Get(left + 11, top + 3).Grapheme);
        Assert.Equal(2, cells.Get(left + 11, top + 3).Width);
        Assert.True(cells.Get(left + 12, top + 3).IsContinuation);
        Assert.Equal("e\u0301", cells.Get(left + 13, top + 3).Grapheme);
        Assert.True(cells.Get(left, top + 3).Style.Attributes.HasFlag(CellAttributes.Reverse));
        Assert.Equal("X", cells.Get(0, 0).Grapheme);

        normal = CellStyle.Default.WithBackground(Color.FromPalette(2));
        modal.Render(new BufferSurface(cells));
        Assert.Equal(normal, cells.Get(left, top).Style);
        Assert.Equal(normal.Background, cells.Get(left, top + 3).Style.Background);
        Assert.Equal("X", cells.Get(0, height - 1).Grapheme);
    }

    /// <summary>Transient queries are bounded, grapheme-aware, safe to render and reject invalid Unicode atomically.</summary>
    [Fact]
    public static void QueryBoundsAndGraphemeDeletionPreserveState()
    {
        var modal = Create(new TuiKitCommandDiscovery(InteractiveCommandCatalog.All, _ => { }));
        modal.HandlePaste("e\u0301\U0001f469\u200d\U0001f4bb");
        modal.HandleKey(KeyEvent.Special(KeyCode.Backspace));
        Assert.Equal("e\u0301", modal.FilterText);
        modal.HandleKey(KeyEvent.Special(KeyCode.Backspace));
        Assert.Empty(modal.FilterText);
        modal.HandlePaste("a\r\nb\u001b");
        Assert.Equal("a b?", modal.FilterText);
        modal.HandlePaste(new string('z', 257));
        modal.HandlePaste("\ud800");
        Assert.Equal("a b?", modal.FilterText);
    }

    /// <summary>Minimum size is checked on input, before rendering can observe a resize.</summary>
    [Fact]
    public static void TooSmallPaletteRejectsInputButKeepsSafetyKeys()
    {
        var size = new Size(80, 24);
        var interrupts = 0;
        var toggles = 0;
        var accepted = false;
        var modal = new CommandPaletteModal(
            new TuiKitCommandDiscovery(InteractiveCommandCatalog.All, _ => accepted = true),
            string.Empty,
            () => size,
            _ => CellStyle.Default,
            () => interrupts++,
            () => toggles++,
            () => { });
        size = new Size(39, 11);
        modal.HandlePaste("reasoning");
        modal.HandleKey(KeyEvent.Special(KeyCode.Enter));
        modal.HandleKey(KeyEvent.Char('x'));
        modal.HandleKey(KeyEvent.Char('c', KeyModifiers.Ctrl));
        modal.HandleKey(KeyEvent.Special(KeyCode.F12));
        Assert.Empty(modal.FilterText);
        Assert.False(accepted);
        Assert.Equal(1, interrupts);
        Assert.Equal(1, toggles);
        modal.HandleKey(KeyEvent.Special(KeyCode.Escape));
        Assert.True(modal.IsClosed);
    }

    /// <summary>Palette title limits do not alter exact metadata used for completion and details.</summary>
    [Fact]
    public static void SearchTitlesAreBoundedAtGraphemeBoundaries()
    {
        var entry = new InteractiveCommandDescriptor("/fixture", "exact usage", new string('a', 500) + "\U0001f469\u200d\U0001f4bb\n\u001b");
        var discovery = new TuiKitCommandDiscovery([entry], _ => { });
        var selected = Assert.IsType<Command>(discovery.BuildPalette().SelectedItem);
        Assert.True(selected.Title.Length <= TuiKitCommandDiscovery.MaximumTitleLength);
        Assert.DoesNotContain('\u001b', selected.Title);
        Assert.DoesNotContain('\n', selected.Title);
        Assert.False(char.IsSurrogate(selected.Title[^1]));
        Assert.True(discovery.TryGet(entry.Name, out var retained));
        Assert.Same(entry, retained);
    }

    private static CommandPaletteModal Create(TuiKitCommandDiscovery discovery)
    {
        return new CommandPaletteModal(discovery, string.Empty, () => new Size(80, 24), _ => CellStyle.Default, () => { }, () => { }, () => { });
    }
}
