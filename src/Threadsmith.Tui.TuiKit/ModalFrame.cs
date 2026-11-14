namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Interaction.Presentation;
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

    /// <summary>Draws the standard heading and footer, returning the scrollable body viewport.</summary>
    internal static BufferSurface CreateBody(
        BufferSurface frame,
        CachedTextRun text,
        Func<PresentationTextRole, CellStyle> resolveStyle,
        string title,
        string subtitle,
        string status,
        string footer,
        bool spaceAfterSubtitle = false)
    {
        text.Draw(frame, 0, 0, title, resolveStyle(PresentationTextRole.SelectionPrompt));
        text.Draw(frame, 0, 2, subtitle, resolveStyle(PresentationTextRole.Muted));
        text.Draw(frame, 0, frame.Size.Height - 2, status, resolveStyle(PresentationTextRole.Status));
        text.Draw(frame, 0, frame.Size.Height - 1, footer, resolveStyle(PresentationTextRole.Muted));
        return frame.CreateView(WorkspaceLayout.ModalBody(frame.Size, spaceAfterSubtitle));
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
