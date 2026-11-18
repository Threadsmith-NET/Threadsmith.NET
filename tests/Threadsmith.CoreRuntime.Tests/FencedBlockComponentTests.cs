namespace Threadsmith.CoreRuntime.Tests;

using TUIKit;
using TUIKit.Input;
using TUIKit.Widgets;
using Xunit;

/// <summary>Verifies the pinned drawing primitives used by retained fenced blocks.</summary>
public static class FencedBlockComponentTests
{
    /// <summary>Clipping a box retains the real top, side, and bottom edges.</summary>
    [Theory]
    [InlineData(BorderStyle.Rounded, "╭", "│", "╰")]
    [InlineData(BorderStyle.Ascii, "+", "|", "+")]
    public static void DrawBox_ClippedRows_PreserveActualEdges(BorderStyle border, string top, string side, string bottom)
    {
        var cells = new CellBuffer(20, 3);
        var surface = new BufferSurface(cells);
        for (var row = 0; row < 3; row++)
        {
            var strip = new SurfaceView(surface, new Rect(0, row, 20, 1));
            strip.DrawBox(new Rect(0, -row, 20, 3), CellStyle.Default, border, row == 0 ? "text" : null, CellStyle.Default, TitleAlignment.Left, 1);
        }

        Assert.Equal(top, cells.Get(0, 0).Grapheme);
        Assert.Equal(side, cells.Get(0, 1).Grapheme);
        Assert.Equal(bottom, cells.Get(0, 2).Grapheme);
        Assert.Equal("t", cells.Get(3, 0).Grapheme);
    }

    /// <summary>Only complete visible controls have viewport-relative hit targets.</summary>
    [Fact]
    public static void InlineButton_VisibleHeaderOnly_UsesViewportCoordinates()
    {
        var cells = new CellBuffer(20, 4);
        var surface = new BufferSurface(cells);
        var map = new ClickRegionMap<string>();
        var invoked = 0;
        map.Invoked += _ => invoked++;
        var width = InlineButton.Draw(surface, 12, 2, "Copy", null, "block", map);

        Assert.Equal(6, width);
        Assert.NotNull(map.HitTest(12, 2));
        Assert.Null(map.HitTest(12, 1));
        Assert.True(map.HandleMouse(new MouseEvent(MouseEventKind.Press, MouseButton.Left, 13, 2, KeyModifiers.None, 1)));
        Assert.Equal(1, invoked);
        map.Clear(); // Scroll/reflow removes the header before another input event.
        Assert.Null(map.HitTest(12, 2));
        Assert.Equal(0, InlineButton.Draw(surface, 16, 2, "Copy", null, "block", map));
        Assert.Empty(map.Regions);
    }
}
