namespace Threadsmith.Tui.TuiKit;

using TUIKit;
using TUIKit.Unicode;

/// <summary>Caches grapheme segmentation for text that is repainted across retained frames.</summary>
internal sealed class CachedTextRun
{
    private readonly List<Glyph> _glyphs = [];
    private string? _text;

    /// <summary>Draws cached safe graphemes with the current style and clipping width.</summary>
    internal void Draw(ISurface surface, int x, int y, string text, CellStyle style)
    {
        if (_text != text)
        {
            _text = text;
            _glyphs.Clear();
            foreach (var grapheme in Graphemes.Split(TranscriptView.Safe(text).Replace('\n', ' ')))
            {
                var width = Math.Clamp(grapheme.Width, 1, 2);
                _glyphs.Add(new Glyph(grapheme.Width == 0 ? "\u25cc" + grapheme.Text : grapheme.Text, width));
            }
        }

        foreach (var glyph in _glyphs)
        {
            if (x + glyph.Width > surface.Size.Width)
            {
                break;
            }

            surface.Set(x, y, Cell.Glyph(glyph.Text, style, glyph.Width));
            if (glyph.Width == 2)
            {
                surface.Set(x + 1, y, Cell.Continuation(style));
            }

            x += glyph.Width;
        }
    }

    private readonly record struct Glyph(string Text, int Width);
}
