namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Content;

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
        var titleRows = Wrap(title, frame.Size.Width);
        var subtitleRows = Wrap(subtitle, frame.Size.Width);
        var statusRows = Wrap(status, frame.Size.Width);
        var footerRows = Wrap(footer, frame.Size.Width);
        var body = BodyBounds(frame.Size, titleRows.Length + subtitleRows.Length, statusRows.Length + footerRows.Length, spaceAfterSubtitle);
        var gap = body.Y >= titleRows.Length + subtitleRows.Length + 1 ? 1 : 0;
        var heading = frame.CreateView(new Rect(0, 0, frame.Size.Width, body.Y));
        DrawLines(heading, text, 0, titleRows, resolveStyle(PresentationTextRole.SelectionPrompt));
        DrawLines(heading, text, titleRows.Length + gap, subtitleRows, resolveStyle(PresentationTextRole.Muted));
        DrawLines(frame, text, body.Bottom, statusRows, resolveStyle(PresentationTextRole.Status));
        DrawLines(frame, text, body.Bottom + statusRows.Length, footerRows, resolveStyle(PresentationTextRole.Muted));
        return frame.CreateView(body);
    }

    /// <summary>Measures chrome at the current cell width so render and input share a body rectangle.</summary>
    internal static Rect BodyBounds(Size size, string title, string subtitle, string status, string footer, bool spaceAfterSubtitle = false)
    {
        var heading = Wrap(title, size.Width).Length + Wrap(subtitle, size.Width).Length;
        var bottom = Wrap(status, size.Width).Length + Wrap(footer, size.Width).Length;
        return BodyBounds(size, heading, bottom, spaceAfterSubtitle);
    }

    /// <summary>Wraps sanitized paragraphs by terminal cells, retaining explicit blank lines.</summary>
    internal static string[] Wrap(string value, int width, int maximumRows = int.MaxValue)
    {
        if (value.Length == 0 || width <= 0 || maximumRows <= 0)
        {
            return [];
        }

        var rows = new List<string>();
        foreach (var paragraph in TranscriptView.Safe(value).ReplaceLineEndings("\n").Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                rows.Add(string.Empty);
            }
            else
            {
                foreach (var line in TextWrapper.Wrap(StyledText.From(paragraph), width))
                {
                    rows.Add(line.ToPlainString());
                    if (rows.Count == maximumRows)
                    {
                        return [.. rows];
                    }
                }
            }

            if (rows.Count == maximumRows)
            {
                break;
            }
        }

        return [.. rows];
    }

    /// <summary>Draws measured prose without painting outside its allocated surface.</summary>
    internal static void DrawLines(ISurface surface, CachedTextRun text, int y, IReadOnlyList<string> rows, CellStyle style)
    {
        for (var index = Math.Max(0, -y); index < rows.Count && y + index < surface.Size.Height; index++)
        {
            text.Draw(surface, 0, y + index, rows[index], style);
        }
    }

    /// <summary>Draws wrapped prose and returns its measured row count.</summary>
    internal static int DrawWrapped(ISurface surface, CachedTextRun text, int y, string value, CellStyle style)
    {
        var rows = Wrap(value, surface.Size.Width);
        DrawLines(surface, text, y, rows, style);
        return rows.Length;
    }

    /// <summary>Returns the same centered frame geometry for rendering and pointer translation.</summary>
    internal static Rect Bounds(Size size, bool large = false, bool expandHeight = false, Size? preferredSize = null)
    {
        var width = Math.Min(preferredSize?.Width ?? (large ? 140 : 90), size.Width - 4);
        var maximumHeight = preferredSize?.Height ?? (large ? 40 : expandHeight ? 26 : 24);
        var height = Math.Min(maximumHeight, size.Height - (expandHeight || size.Height < 15 ? 1 : 3));
        return new Rect((size.Width - width) / 2, (size.Height - 1 - height) / 2, width, height);
    }

    /// <summary>Derives shared body geometry from already measured chrome.</summary>
    private static Rect BodyBounds(Size size, int heading, int bottom, bool spaceAfterSubtitle)
    {
        var spacing = Math.Min(spaceAfterSubtitle ? 2 : 1, Math.Max(0, size.Height - heading - bottom - 1));
        var top = Math.Min(size.Height, heading + spacing);
        return new Rect(0, top, size.Width, Math.Max(0, size.Height - top - bottom));
    }
}
