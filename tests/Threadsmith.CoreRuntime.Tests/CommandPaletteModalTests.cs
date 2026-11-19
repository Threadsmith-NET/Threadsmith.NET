namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Interaction.Commands;
using Threadsmith.Interaction.Contracts;
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

    /// <summary>An effectively unbounded title setting does not preallocate the configured ceiling.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static void LargeTitleLimitAllocatesOnlyRegisteredLabels(bool empty)
    {
        var entry = new InteractiveCommandDescriptor("/fixture", "/fixture", "Short title");
        var discovery = new TuiKitCommandDiscovery(empty ? [] : [entry], _ => { }, new TuiResourceLimits
        {
            MaximumCommandTitleCharacters = int.MaxValue,
        });
        Assert.InRange(discovery.MaximumRenderedTitleLength, 1, 32);
        var cells = new CellBuffer(80, 24);
        Create(discovery).Render(new BufferSurface(cells));
        if (!empty)
        {
            Assert.Contains(entry.Description, TUIKit.Testing.Snapshot.ToText(cells), StringComparison.Ordinal);
        }
    }

    private static CommandPaletteModal Create(TuiKitCommandDiscovery discovery)
    {
        return new CommandPaletteModal(discovery, string.Empty, () => new Size(80, 24), _ => CellStyle.Default, () => { }, () => { }, () => { });
    }
}
