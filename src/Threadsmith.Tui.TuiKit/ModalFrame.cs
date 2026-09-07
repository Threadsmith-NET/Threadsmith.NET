namespace Threadsmith.Tui.TuiKit;

using TUIKit;

/// <summary>Owns the common minimum size and full-frame modal clearing boundary.</summary>
internal static class ModalFrame
{
    /// <summary>Checks the shared minimum terminal size before editing or painting.</summary>
    internal static bool Fits(Size size) => size.Width >= 40 && size.Height >= 12;

    /// <summary>Clears the application frame while preserving its final status row.</summary>
    internal static BufferSurface? Create(ISurface surface, CellStyle background)
    {
        if (!Fits(surface.Size) || surface is not BufferSurface buffer)
        {
            return null;
        }

        var view = buffer.CreateView(new Rect(0, 0, surface.Size.Width, surface.Size.Height - 1));
        view.Fill(new Rect(0, 0, view.Size.Width, view.Size.Height), Cell.Blank(background));
        return view;
    }
}
