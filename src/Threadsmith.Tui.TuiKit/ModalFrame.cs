namespace Threadsmith.Tui.TuiKit;

using TUIKit;

/// <summary>Owns the shared minimum size and centered modal geometry.</summary>
internal static class ModalFrame
{
    /// <summary>Checks the shared minimum terminal size before editing or painting.</summary>
    internal static bool Fits(Size size)
    {
        return size.Width >= 40 && size.Height >= 12;
    }

    /// <summary>Clears a centered bordered popup while preserving surrounding application rows.</summary>
    internal static BufferSurface? Create(ISurface surface, CellStyle background, bool large = false, bool expandHeight = false, Size? preferredSize = null)
    {
        if (!Fits(surface.Size) || surface is not BufferSurface buffer)
        {
            return null;
        }

        var view = buffer.CreateView(Bounds(surface.Size, large, expandHeight, preferredSize));
        WorkspaceLayout.DrawFrame(view, background);
        return view.CreateView(WorkspaceLayout.ModalContent(view.Size));
    }

    /// <summary>Returns the same centered frame geometry for rendering and pointer translation.</summary>
    internal static Rect Bounds(Size size, bool large = false, bool expandHeight = false, Size? preferredSize = null)
    {
        var width = Math.Min(preferredSize?.Width ?? (large ? 140 : 90), size.Width - 4);
        var maximumHeight = preferredSize?.Height ?? (large ? 40 : expandHeight ? 26 : 24);
        var height = Math.Min(maximumHeight, size.Height - (expandHeight ? 1 : 3));
        return new Rect((size.Width - width) / 2, (size.Height - 1 - height) / 2, width, height);
    }
}
