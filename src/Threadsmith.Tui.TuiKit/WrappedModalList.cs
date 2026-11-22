namespace Threadsmith.Tui.TuiKit;

using System.Text;
using TUIKit;
using TUIKit.Widgets;

/// <summary>Adapts native modal lists to wrapped display rows without taking ownership of selection.</summary>
internal sealed class WrappedModalList
{
    private readonly CellBuffer _buffer = new(1, 1);
    private readonly CachedTextRun _text = new();
    private readonly List<(string Text, CellStyle Style)> _rows = [];

    /// <summary>Reflows a bounded native viewport using modal styles for selected, ordinary and unused rows.</summary>
    internal void Render(IWidget widget, BufferSurface surface, int labelWidth, CellStyle normal, CellStyle highlight, int headerRows = 0)
    {
        if (surface.Size.Height == 0 || surface.Size.Width == 0)
        {
            return;
        }

        var logicalRows = surface.Size.Height;
        while (logicalRows > 0)
        {
            _buffer.Resize(Math.Max(surface.Size.Width, labelWidth), logicalRows + headerRows);
            _buffer.Clear(normal);
            widget.Render(new BufferSurface(_buffer));
            _rows.Clear();
            var text = new StringBuilder(_buffer.Width);
            for (var row = headerRows; row < _buffer.Height; row++)
            {
                text.Clear();
                for (var column = 0; column < _buffer.Width; column++)
                {
                    text.Append(_buffer.Get(column, row).Grapheme);
                }

                // Native widgets may paint default colors even over a themed buffer.
                var style = _buffer.Get(0, row).Style == highlight ? highlight : normal;
                var value = text.ToString().TrimEnd();
                var lines = value.Length == 0 ? [string.Empty] : ModalFrame.Wrap(value, surface.Size.Width);
                foreach (var line in lines)
                {
                    _rows.Add((line, style));
                }
            }

            if (_rows.Count <= surface.Size.Height || logicalRows == 1)
            {
                break;
            }

            // Let the native widget recompute selected-item scrolling at geometrically smaller
            // viewports instead of rendering every intermediate height in the same frame.
            logicalRows = Math.Max(1, logicalRows / 2);
        }

        for (var row = 0; row < Math.Min(surface.Size.Height, _rows.Count); row++)
        {
            var entry = _rows[row];
            surface.Fill(new Rect(0, row, surface.Size.Width, 1), Cell.Blank(entry.Style));
            _text.Draw(surface, 0, row, entry.Text, entry.Style);
        }
    }
}
