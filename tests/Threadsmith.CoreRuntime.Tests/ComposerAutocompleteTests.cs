namespace Threadsmith.CoreRuntime.Tests;

using Threadsmith.Interaction.Commands;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Tui.TuiKit;
using TUIKit;
using TUIKit.Input;
using Xunit;

/// <summary>Checks transient overlay state without depending on terminal frame timing.</summary>
public static class ComposerAutocompleteTests
{
    /// <summary>Redraws preserve navigation and dismissal until the destination changes.</summary>
    [Fact]
    public static void SelectionAndDismissalSurviveUnchangedFrames()
    {
        string? accepted = null;
        var discovery = new TuiKitCommandDiscovery([new("/first", "first", "first"), new("/final", "final", "final")], _ => { });
        var buffer = new ComposerBuffer();
        buffer.Insert("/fi");
        using var overlay = new ComposerAutocomplete(discovery, new ComposerCommandCompletion(discovery), value => accepted = value);
        overlay.Refresh(buffer, ComposerPurpose.Conversation, 1, true);
        overlay.HandleKey(KeyEvent.Special(KeyCode.Down));
        overlay.Refresh(buffer, ComposerPurpose.Conversation, 1, true);
        overlay.HandleKey(KeyEvent.Special(KeyCode.Tab));
        Assert.Equal("/final", accepted);
        Assert.False(overlay.IsVisible);

        overlay.Refresh(buffer, ComposerPurpose.Conversation, 2, true);
        Assert.True(overlay.IsVisible);
        overlay.HandleKey(KeyEvent.Special(KeyCode.Escape));
        overlay.Refresh(buffer, ComposerPurpose.Conversation, 2, true);
        Assert.False(overlay.IsVisible);
        buffer.Insert("r");
        overlay.Refresh(buffer, ComposerPurpose.Conversation, 2, true);
        Assert.True(overlay.IsVisible);
        overlay.Refresh(buffer, ComposerPurpose.Conversation, 2, false);
        Assert.False(overlay.IsVisible);
        overlay.Refresh(buffer, ComposerPurpose.Secondary, 3, true);
        Assert.False(overlay.IsVisible);
    }

    /// <summary>Modified keys are never stolen from the editor by toolkit navigation or acceptance.</summary>
    [Theory]
    [InlineData(KeyCode.Enter, KeyModifiers.Ctrl)]
    [InlineData(KeyCode.Enter, KeyModifiers.Shift)]
    [InlineData(KeyCode.Tab, KeyModifiers.Shift)]
    [InlineData(KeyCode.Left, KeyModifiers.Shift)]
    [InlineData(KeyCode.Home, KeyModifiers.Ctrl)]
    public static void ModifiedKeysFallThrough(KeyCode code, KeyModifiers modifiers)
    {
        var discovery = new TuiKitCommandDiscovery(InteractiveCommandCatalog.All, _ => { });
        var buffer = new ComposerBuffer();
        buffer.Insert("/rea");
        using var overlay = new ComposerAutocomplete(discovery, new ComposerCommandCompletion(discovery), _ => Assert.Fail("Modified key accepted."));
        overlay.Refresh(buffer, ComposerPurpose.Conversation, 1, true);
        Assert.False(overlay.HandleKey(KeyEvent.Special(code, modifiers)));
        Assert.True(overlay.IsVisible);
    }

    /// <summary>Short and flipped lists preserve reserved rows and show whole names near the right edge.</summary>
    [Theory]
    [InlineData("/rea", 1)]
    [InlineData("/", 6)]
    public static void OverlayDoesNotCoverActivityOrStatus(string prefix, int expectedRows)
    {
        var discovery = new TuiKitCommandDiscovery(InteractiveCommandCatalog.All, _ => { });
        var buffer = new ComposerBuffer();
        buffer.Insert(prefix);
        using var overlay = new ComposerAutocomplete(discovery, new ComposerCommandCompletion(discovery), _ => { });
        overlay.Refresh(buffer, ComposerPurpose.Conversation, 1, true);
        var cells = new CellBuffer(40, 12);
        cells.Fill(new Rect(0, 0, 40, 12), Cell.Glyph("X", CellStyle.Default, 1));
        overlay.Render(new BufferSurface(cells), new Rect(0, 7, 40, 4), new Rect(0, 6, 40, 1), (39, 0), CellStyle.Default, CellStyle.Default);
        var rows = TUIKit.Testing.Snapshot.ToText(cells).Split('\n');
        Assert.Equal(expectedRows, rows.Count(row => row.Contains('/')));
        Assert.Equal(new string('X', 40), rows[6]);
        Assert.Equal(new string('X', 40), rows[11]);
        Assert.Contains(discovery.Suggest(prefix)[0], string.Join('\n', rows), StringComparison.Ordinal);
    }

    /// <summary>The composer reports the actual visible caret after Unicode wrapping and scroll-to-caret.</summary>
    [Fact]
    public static void ComposerCaretUsesRenderedViewport()
    {
        var composer = new TuiKitComposer { FirstRowOffset = 3 };
        composer.InsertText("\u754ce\u0301\nsecond\nthird");
        composer.Render(new BufferSurface(new CellBuffer(10, 2)));
        Assert.Equal((5, 1), composer.VisibleCaret);
        composer.Buffer.MoveTo(3, false);
        composer.Render(new BufferSurface(new CellBuffer(10, 2)));
        Assert.Equal((6, 0), composer.VisibleCaret);
    }
}
