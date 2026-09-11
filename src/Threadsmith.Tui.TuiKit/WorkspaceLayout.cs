namespace Threadsmith.Tui.TuiKit;

using TUIKit;
using TUIKit.Layout;

/// <summary>Single source of border/content geometry for render, input, selection, and overlays.</summary>
internal static class WorkspaceLayout
{
    /// <summary>Builds the fixed workspace rows while preserving two output rows at 40 by 12.</summary>
    internal static Layout Create(Size size)
    {
        if (!ModalFrame.Fits(size))
        {
            return new LayoutBuilder().Fill("transcript").Build();
        }

        var composerHeight = Math.Clamp(size.Height - 9, 3, 8);
        return new LayoutBuilder().DockTop("title", 1).DockTop("tabs", 1)
            .DockBottom("status", 1).DockBottom("composer", composerHeight).Fill("transcript").Build();
    }

    /// <summary>Gets the unpadded status row immediately inside the output border.</summary>
    internal static Rect OutputHeader(Size size) => new(1, 1, Math.Max(0, size.Width - 2), 1);

    /// <summary>Gets streamed content below the header separator, with no reserved hint row.</summary>
    internal static Rect OutputContent(Size size) => new(2, 3, Math.Max(0, size.Width - 4), Math.Max(0, size.Height - (size.Height >= 7 ? 5 : 4)));

    /// <summary>Gets the existing border area used only for transient activity and actionable notices.</summary>
    internal static Rect OutputNotice(Size size) => new(2, Math.Max(0, size.Height - 1), Math.Max(0, size.Width - 4), 1);

    /// <summary>Gets the exact editor rectangle within the composer border.</summary>
    internal static Rect ComposerContent(Size size) => new Padding(2, 1, 2, size.Height >= 4 ? 2 : 1).Deflate(new Rect(0, 0, size.Width, size.Height));

    /// <summary>Gets the content of a single-row bar with horizontal padding.</summary>
    internal static Rect RowContent(Size size) => Padding.Symmetric(1, 0).Deflate(new Rect(0, 0, size.Width, Math.Min(1, size.Height)));

    /// <summary>Places modal headings immediately below the border, retaining side and bottom padding.</summary>
    internal static Rect ModalContent(Size size) => new Padding(2, 1, 2, 2).Deflate(new Rect(0, 0, size.Width, size.Height));

    /// <summary>Draws one clipped frame without altering the inner widget's origin.</summary>
    internal static void DrawFrame(ISurface surface, CellStyle style)
    {
        var frame = new Rect(0, 0, surface.Size.Width, surface.Size.Height);
        surface.Fill(frame, Cell.Blank(style));
        surface.DrawBox(frame, style, BorderStyle.Rounded);
    }

    /// <summary>Translates a mouse event into a measured content rectangle.</summary>
    internal static TUIKit.Input.MouseEvent Translate(TUIKit.Input.MouseEvent mouse, Rect rect) =>
        new(mouse.Kind, mouse.Button, mouse.X - rect.Left, mouse.Y - rect.Top, mouse.Modifiers, mouse.ClickCount);
}
