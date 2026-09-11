namespace Threadsmith.Tui.TuiKit;

using TUIKit;

/// <summary>Owns the shared minimum size and centered modal geometry.</summary>
internal static class ModalFrame
{
    /// <summary>Checks the shared minimum terminal size before editing or painting.</summary>
    internal static bool Fits(Size size) => size.Width >= 40 && size.Height >= 12;

    /// <summary>Clears a centered bordered popup while preserving surrounding application rows.</summary>
    internal static BufferSurface? Create(ISurface surface, CellStyle background)
    {
        if (!Fits(surface.Size) || surface is not BufferSurface buffer)
        {
            return null;
        }

        var width = Math.Min(90, surface.Size.Width - 4);
        var height = Math.Min(24, surface.Size.Height - 3);
        var left = (surface.Size.Width - width) / 2;
        var top = (surface.Size.Height - 1 - height) / 2;
        var view = buffer.CreateView(new Rect(left, top, width, height));
        WorkspaceLayout.DrawFrame(view, background);
        return view.CreateView(WorkspaceLayout.ModalContent(view.Size));
    }
}
