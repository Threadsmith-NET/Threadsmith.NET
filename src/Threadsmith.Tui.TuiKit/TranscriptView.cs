namespace Threadsmith.Tui.TuiKit;

using System.Globalization;
using System.Text;
using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Input;
using TUIKit.Unicode;
using TUIKit.Widgets;

// This bounded projection is deliberately independent of durable session history.

/// <summary>Retains a bounded, selectable transcript independently of durable session history.</summary>
internal sealed class TranscriptView : IWidget, IFocusable, IMouseAware
{
    /// <summary>Maximum retained logical line chunks.</summary>
    internal const int LineLimit = 1024;

    /// <summary>Maximum retained UTF-8 text size.</summary>
    internal const int ByteLimit = 512 * 1024;

    private const int RowGlyphCacheLimit = 512;
    private static readonly string[] _asciiGlyphs = CreateAsciiGlyphs();
    private readonly CachedTextRun _evictionNotice = new();
    private readonly List<Line> _lines = [];
    private readonly List<Row> _rows = [];
    private readonly Dictionary<RowKey, DisplayGlyph[]> _rowGlyphs = [];
    private readonly List<RowKey> _rowGlyphRemovals = [];
    private readonly Dictionary<long, List<StyledRange>> _styles = [];
    private int _width;
    private int _height = 12;
    private int _top;
    private long _nextId;
    private Point? _anchor;
    private Point? _end;
    private long? _clearBeforeId;

    /// <inheritdoc/>
    public Size Measure(Size available) => available;

    /// <inheritdoc/>
    public void Render(ISurface surface)
    {
        if (surface.Size.Width < 2 || surface.Size.Height < 1)
        {
            return;
        }

        var topId = TopId;
        surface.Fill(new Rect(0, 0, surface.Size.Width, surface.Size.Height), Cell.Blank(CellStyle.Default));
        if (_width != surface.Size.Width)
        {
            _width = surface.Size.Width;
            _rows.Clear();
            _rowGlyphs.Clear();
            foreach (var line in _lines)
            {
                Wrap(line);
            }

            _top = Math.Max(0, topId is { } retainedTopId ? FindFirstRow(retainedTopId) : -1);
        }

        var notice = Evicted > 0 ? 1 : 0;
        _height = Math.Max(1, surface.Size.Height - notice);
        _top = AtBottom ? Math.Max(0, _rows.Count - _height) : Math.Min(_top, Math.Max(0, _rows.Count - _height));
        if (_clearBeforeId is { } clearBefore)
        {
            var firstNew = _rows.FindIndex(row => row.Line.Id >= clearBefore);
            _top = Math.Max(_top, firstNew < 0 ? _rows.Count : firstNew);
        }

        if (notice > 0)
        {
            _evictionNotice.Draw(surface, 0, 0, "[Older visible output omitted; session history is authoritative]", CellStyle.Default);
        }

        var hasSelection = _anchor is not null && _end is not null;
        var (first, last) = OrderedSelection();
        for (var index = _top; index < Math.Min(_rows.Count, _top + _height); index++)
        {
            var row = _rows[index];
            var selected = hasSelection && row.Line.Id >= first.Line && row.Line.Id <= last.Line;
            DrawRow(surface, index - _top + notice, row, selected, first, last);
        }
    }

    /// <inheritdoc/>
    public bool HandleKey(KeyEvent key)
    {
        if (key.Modifiers.HasFlag(KeyModifiers.Shift) && _rows.Count > _top
            && key.Code is KeyCode.Left or KeyCode.Right or KeyCode.Up or KeyCode.Down
                or KeyCode.Home or KeyCode.End or KeyCode.PageUp or KeyCode.PageDown)
        {
            ExtendSelection(key);
            return true;
        }

        switch (key.Code)
        {
            case KeyCode.Escape:
                _anchor = _end = null;
                return true;
            case KeyCode.PageUp:
                Scroll(-_height);
                return true;
            case KeyCode.PageDown:
                Scroll(_height);
                return true;
            case KeyCode.Up:
                Scroll(-1);
                return true;
            case KeyCode.Down:
                Scroll(1);
                return true;
            case KeyCode.Home:
                _clearBeforeId = null;
                AtBottom = false;
                _top = 0;
                return true;
            case KeyCode.End:
                _clearBeforeId = null;
                AtBottom = true;
                NewCount = 0;
                return true;
            case KeyCode.Character when key.Rune == 'a' && key.Modifiers.HasFlag(KeyModifiers.Ctrl):
                if (_lines.Count > 0)
                {
                    _anchor = new Point(_lines[0].Id, 0);
                    _end = new Point(_lines[^1].Id, _lines[^1].Text.Length);
                }

                return true;
            default:
                return false;
        }
    }

    /// <inheritdoc/>
    public bool HandleMouse(MouseEvent mouse)
    {
        if (mouse.Kind == MouseEventKind.Wheel)
        {
            Scroll(mouse.Button == MouseButton.WheelUp ? -3 : 3);
            return true;
        }

        if (mouse.Kind == MouseEventKind.Release)
        {
            if (_anchor is not null && mouse.Button == MouseButton.Left && _rows.Count > _top)
            {
                MoveSelectionEnd(mouse);
            }

            return true;
        }

        if (mouse.Button != MouseButton.Left || _rows.Count <= _top)
        {
            return false;
        }

        MoveSelectionEnd(mouse);
        if (mouse.Kind == MouseEventKind.Press && !mouse.Modifiers.HasFlag(KeyModifiers.Shift))
        {
            _anchor = _end;
        }

        return true;
    }

    /// <summary>Gets or sets semantic role resolution for the current theme.</summary>
    internal Func<PresentationTextRole, CellStyle> ResolveStyle { get; set; } = _ => CellStyle.Default;

    /// <summary>Appends semantic output while preserving streaming chunk continuity.</summary>
    internal void Present(PresentationBatch batch)
    {
        foreach (var item in batch.Items)
        {
            foreach (var segment in Project(item, Math.Max(40, _width)))
            {
                segment.Validate();
                AppendContent(segment);
            }
        }
    }

    /// <summary>Projects shared semantic items using the existing Markdown layout rules.</summary>
    internal static IReadOnlyList<PresentationTextSegment> Project(PresentationItem item, int width)
    {
        IReadOnlyList<PresentationTextSegment> segments;
        bool startsAnswer;
        switch (item)
        {
            case PresentationTextItem text:
                return text.Segments;
            case PresentationSourceItem source:
                segments = [new(source.SafeSource, PresentationTextRole.Default)];
                startsAnswer = source.StartsAnswerBlock;
                break;
            case PresentationMarkdownItem markdown:
                try
                {
                    segments = TuiMarkdownLayout.Format(markdown.Document, width);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    segments = [new(markdown.SafeSource, PresentationTextRole.Default)];
                }

                startsAnswer = markdown.StartsAnswerBlock;
                break;
            default:
                throw new InvalidOperationException("Interactive output requires safe semantic presentation.");
        }

        return startsAnswer ? [new("\n", PresentationTextRole.Default), .. segments] : segments;
    }

    /// <summary>Gets the retained UTF-8 text size.</summary>
    internal int RetainedBytes { get; private set; }

    /// <summary>Gets the retained logical chunk count.</summary>
    internal int Count => _lines.Count;

    /// <summary>Gets the number of evicted chunks.</summary>
    internal long Evicted { get; private set; }

    /// <summary>Gets whether the viewport follows incoming output.</summary>
    internal bool AtBottom { get; private set; } = true;

    /// <summary>Gets the number of unseen output updates.</summary>
    internal int NewCount { get; private set; }

    /// <summary>Gets the stable identifier of the top visible chunk.</summary>
    internal long? TopId => _rows.Count > _top ? _rows[_top].Line.Id : null;

    /// <summary>Gets retained logical text for diagnostics.</summary>
    internal IReadOnlyList<string> Lines => _lines.Select(line => line.Text).ToArray();

    /// <summary>Gets only validated targets belonging to retained, projected text.</summary>
    internal IReadOnlyList<Uri> Links => _styles.Values.SelectMany(ranges => ranges)
        .Select(range => range.Link).OfType<Uri>().Distinct().Take(512).ToArray();

    /// <summary>Starts a fresh viewport while keeping bounded history available above it.</summary>
    internal void ClearView()
    {
        _clearBeforeId = _nextId;
        _anchor = _end = null;
        AtBottom = true;
        NewCount = 0;
    }

    /// <summary>Appends standalone text split only at grapheme boundaries.</summary>
    internal void Append(string text, CellStyle style = default)
    {
        foreach (var part in Safe(text).Split('\n'))
        {
            // Oversized logical lines are split without dropping graphemes or source text.
            var chunk = new StringBuilder();
            var bytes = 0;
            var continued = false;
            var elements = StringInfo.GetTextElementEnumerator(part);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                var size = Encoding.UTF8.GetByteCount(element);
                if (bytes + size > 16 * 1024 && chunk.Length > 0)
                {
                    AddLine(chunk.ToString(), style, continued);
                    continued = true;
                    chunk.Clear();
                    bytes = 0;
                }

                chunk.Append(element);
                bytes += size;
            }

            AddLine(chunk.ToString(), style, continued);
        }
    }

    /// <summary>Returns selected source text without inserting soft-wrap line breaks.</summary>
    internal string SelectedText()
    {
        if (_anchor is null || _end is null)
        {
            return string.Empty;
        }

        var (first, last) = OrderedSelection();
        var selected = new StringBuilder();
        var firstLine = true;
        foreach (var line in _lines)
        {
            if (line.Id < first.Line || line.Id > last.Line)
            {
                continue;
            }

            if (!firstLine && !line.Continued)
            {
                selected.Append('\n');
            }

            firstLine = false;
            selected.Append(line.Text.AsSpan(
                line.Id == first.Line ? first.Offset : 0,
                (line.Id == last.Line ? last.Offset : line.Text.Length) - (line.Id == first.Line ? first.Offset : 0)));
        }

        return selected.ToString();
    }

    /// <summary>Removes terminal control sequences while preserving text and line endings.</summary>
    internal static string Safe(string text)
    {
        var normalized = text.ReplaceLineEndings("\n");
        var unsafeIndex = normalized.AsSpan().IndexOfAnyInRange('\0', '\u001f');
        if (unsafeIndex < 0 && normalized.AsSpan().IndexOfAnyInRange('\u007f', '\u009f') < 0)
        {
            return normalized;
        }

        var safe = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (!char.IsControl(character) || character == '\n')
            {
                safe.Append(character);
            }
            else
            {
                safe.Append(character == '\t' ? "    " : "?");
            }
        }

        return safe.ToString();
    }

    private void DrawRow(ISurface surface, int y, Row row, bool selected, Point first, Point last)
    {
        var selectionStart = selected ? Math.Max(row.Start, row.Line.Id == first.Line ? first.Offset : row.Start) : -1;
        var selectionEnd = selected ? Math.Min(row.End, row.Line.Id == last.Line ? last.Offset : row.End) : -1;
        _styles.TryGetValue(row.Line.Id, out var ranges);
        var rangeIndex = 0;
        if (ranges is not null)
        {
            while (rangeIndex < ranges.Count && ranges[rangeIndex].End <= row.Start)
            {
                rangeIndex++;
            }
        }

        var x = 0;
        foreach (var glyph in GetRowGlyphs(row))
        {
            if (x + glyph.Width > surface.Size.Width)
            {
                break;
            }

            while (ranges is not null && rangeIndex < ranges.Count && ranges[rangeIndex].End <= glyph.Offset)
            {
                rangeIndex++;
            }

            var style = ranges is not null && rangeIndex < ranges.Count && ranges[rangeIndex].Start <= glyph.Offset
                ? ResolveStyle(ranges[rangeIndex].Role)
                : row.Line.Style;
            if (glyph.Offset >= selectionStart && glyph.Offset < selectionEnd)
            {
                style = style.WithAttribute(CellAttributes.Reverse, true);
            }

            var text = glyph.Text ?? _asciiGlyphs[row.Line.Text[glyph.Offset]];
            surface.Set(x, y, Cell.Glyph(text, style, glyph.Width));
            if (glyph.Width == 2)
            {
                surface.Set(x + 1, y, Cell.Continuation(style));
            }

            x += glyph.Width;
        }
    }

    private DisplayGlyph[] GetRowGlyphs(Row row)
    {
        var key = new RowKey(row.Line.Id, row.Start, row.End);
        if (_rowGlyphs.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_rowGlyphs.Count >= RowGlyphCacheLimit)
        {
            _rowGlyphs.Clear();
        }

        var graphemes = Graphemes.Split(row.Line.Text[row.Start..row.End]);
        var glyphs = new DisplayGlyph[graphemes.Count];
        var offset = row.Start;
        for (var index = 0; index < graphemes.Count; index++)
        {
            var grapheme = graphemes[index];
            var width = Math.Clamp(grapheme.Width, 1, 2);
            string? text = grapheme.Text.Length == 1 && grapheme.Text[0] < 128
                ? null
                : grapheme.Width == 0 ? "\u25cc" + grapheme.Text : grapheme.Text;
            glyphs[index] = new DisplayGlyph(offset, width, text);
            offset += grapheme.Text.Length;
        }

        _rowGlyphs.Add(key, glyphs);
        return glyphs;
    }

    private void MoveSelectionEnd(MouseEvent mouse)
    {
        var row = _rows[Math.Clamp(_top + mouse.Y - (Evicted > 0 ? 1 : 0), 0, _rows.Count - 1)];
        var offset = row.Start;
        var column = 0;
        foreach (var (element, width) in Elements(row.Line.Text, row.Start, row.End))
        {
            if (column + width > mouse.X)
            {
                break;
            }

            offset += element.Length;
            column += width;
        }

        _end = new Point(row.Line.Id, offset);
    }

    private static IEnumerable<(string Element, int Width)> Elements(string text, int start = 0, int? end = null)
    {
        var elements = StringInfo.GetTextElementEnumerator(text, start);
        var limit = end ?? text.Length;
        while (elements.MoveNext() && elements.ElementIndex < limit)
        {
            var element = elements.GetTextElement();
            yield return (element, Math.Clamp(Graphemes.MeasureWidth(element), 1, 2));
        }
    }

    private void ExtendSelection(KeyEvent key)
    {
        _end ??= new Point(_rows[_top].Line.Id, _rows[_top].Start);
        _anchor ??= _end;
        var end = _end.Value;
        var rowIndex = FindRow(end);
        if (rowIndex < 0)
        {
            // Eviction removed the previous endpoint; start at the retained viewport.
            rowIndex = _top;
            _anchor = _end = new Point(_rows[rowIndex].Line.Id, _rows[rowIndex].Start);
            end = _end.Value;
        }

        var current = _rows[rowIndex];
        var offset = end.Offset;
        if (key.Code is KeyCode.Left or KeyCode.Right)
        {
            var boundaries = StringInfo.ParseCombiningCharacters(current.Line.Text);
            if (key.Code == KeyCode.Left && offset > 0)
            {
                offset = boundaries.Last(value => value < offset);
            }
            else if (key.Code == KeyCode.Right && offset < current.Line.Text.Length)
            {
                offset = boundaries.FirstOrDefault(value => value > offset, current.Line.Text.Length);
            }
            else
            {
                rowIndex = Math.Clamp(rowIndex + (key.Code == KeyCode.Left ? -1 : 1), 0, _rows.Count - 1);
                current = _rows[rowIndex];
                offset = key.Code == KeyCode.Left ? current.End : current.Start;
            }
        }
        else if (key.Code is KeyCode.Home or KeyCode.End)
        {
            offset = key.Code == KeyCode.Home ? current.Start : current.End;
        }
        else
        {
            var column = Elements(current.Line.Text, current.Start, offset).Sum(element => element.Width);
            var direction = key.Code is KeyCode.Up or KeyCode.PageUp ? -1 : 1;
            var distance = key.Code is KeyCode.PageUp or KeyCode.PageDown ? _height : 1;
            rowIndex = Math.Clamp(rowIndex + (direction * distance), 0, _rows.Count - 1);
            current = _rows[rowIndex];
            offset = current.Start;
            foreach (var (element, width) in Elements(current.Line.Text, current.Start, current.End))
            {
                if (width > column)
                {
                    break;
                }

                offset += element.Length;
                column -= width;
            }
        }

        _end = new Point(current.Line.Id, offset);
        var caretRow = FindRow(new Point(current.Line.Id, offset));
        _top = Math.Clamp(_top, Math.Max(0, caretRow - _height + 1), caretRow);
        AtBottom = false;
    }

    private (Point First, Point Last) OrderedSelection() => _anchor is { } anchor && _end is { } end
        ? (anchor.Line < end.Line || (anchor.Line == end.Line && anchor.Offset <= end.Offset) ? (anchor, end) : (end, anchor))
        : (new Point(0, 0), new Point(0, 0));

    private void Scroll(int amount)
    {
        _clearBeforeId = null;
        _top = Math.Clamp(_top + amount, 0, Math.Max(0, _rows.Count - _height));
        AtBottom = _top == Math.Max(0, _rows.Count - _height);
        if (AtBottom)
        {
            NewCount = 0;
        }
    }

    private void Wrap(Line line)
    {
        var start = 0;
        var end = 0;
        var column = 0;
        foreach (var grapheme in Graphemes.Split(line.Text))
        {
            var width = Math.Clamp(grapheme.Width, 1, 2);
            if (column + width > _width && end > start)
            {
                _rows.Add(new Row(line, start, end));
                start = end;
                column = 0;
            }

            end += grapheme.Text.Length;
            column += width;
        }

        _rows.Add(new Row(line, start, end));
    }

    private void AppendContent(PresentationTextSegment segment)
    {
        var safe = Safe(segment.Text);
        var partStart = 0;
        var partIndex = 0;
        while (true)
        {
            if (_lines.Count == 0 || partIndex > 0)
            {
                AddLine(string.Empty, CellStyle.Default, false);
            }

            var newline = safe.IndexOf('\n', partStart);
            var partLength = (newline < 0 ? safe.Length : newline) - partStart;
            var text = safe.AsSpan(partStart, partLength);
            var offset = 0;
            do
            {
                if (_lines.Count == 0)
                {
                    AddLine(string.Empty, CellStyle.Default, true);
                }

                if (_lines[^1].Text.Length >= 16 * 1024)
                {
                    AddLine(string.Empty, CellStyle.Default, true);
                }

                if (text.IsEmpty)
                {
                    break;
                }

                var line = _lines[^1];
                var remaining = text[offset..];
                var length = Math.Min((16 * 1024) - line.Text.Length, remaining.Length);
                if (length < remaining.Length)
                {
                    var boundaries = StringInfo.ParseCombiningCharacters(remaining.ToString());
                    length = boundaries.LastOrDefault(value => value <= length);
                    if (length == 0)
                    {
                        length = boundaries.Length > 1 ? boundaries[1] : remaining.Length;
                    }
                }

                var fragment = remaining[..length].ToString();

                // A pathological combining sequence must not defeat bounded retention.
                if (Encoding.UTF8.GetByteCount(fragment) > ByteLimit)
                {
                    fragment = "[oversized grapheme omitted]";
                }

                var appendedBytes = Encoding.UTF8.GetByteCount(fragment);
                if (fragment.Length > 0)
                {
                    if (!_styles.TryGetValue(line.Id, out var ranges))
                    {
                        ranges = [];
                        _styles.Add(line.Id, ranges);
                    }

                    var range = new StyledRange(line.Text.Length, line.Text.Length + fragment.Length, segment.Role, segment.LinkTarget);
                    if (ranges.Count > 0 && ranges[^1].End == range.Start && ranges[^1].Role == range.Role && ranges[^1].Link == range.Link)
                    {
                        ranges[^1] = ranges[^1] with { End = range.End };
                    }
                    else
                    {
                        ranges.Add(range);
                    }

                    if (!AtBottom)
                    {
                        NewCount++;
                    }
                }

                var replacement = line with { Text = line.Text + fragment, Bytes = line.Bytes + appendedBytes };
                _lines[^1] = replacement;
                RetainedBytes += appendedBytes;
                RemoveRows(line.Id);
                if (_width > 0)
                {
                    Wrap(replacement);
                }

                offset += length;
                Trim();
            }
            while (offset < text.Length);

            if (newline < 0)
            {
                break;
            }

            partStart = newline + 1;
            partIndex++;
        }
    }

    private void AddLine(string text, CellStyle style, bool continued)
    {
        var line = new Line(_nextId++, text, style, Encoding.UTF8.GetByteCount(text), continued);
        _lines.Add(line);
        RetainedBytes += line.Bytes;
        if (_width > 0)
        {
            Wrap(line);
        }

        if (!AtBottom)
        {
            NewCount++;
        }

        Trim();
    }

    private void Trim()
    {
        while (_lines.Count > LineLimit || RetainedBytes > ByteLimit)
        {
            var first = _lines[0];
            RetainedBytes -= first.Bytes;
            _lines.RemoveAt(0);
            _styles.Remove(first.Id);
            var removed = RemoveRows(first.Id);
            _top = Math.Max(0, _top - removed);
            Evicted++;
        }
    }

    private sealed record Line(long Id, string Text, CellStyle Style, int Bytes, bool Continued);

    private int RemoveRows(long lineId)
    {
        var first = FindFirstRow(lineId);
        if (first < 0)
        {
            RemoveCachedRows(lineId);
            return 0;
        }

        var low = first;
        var high = _rows.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_rows[middle].Line.Id <= lineId)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        var count = low - first;
        _rows.RemoveRange(first, count);
        RemoveCachedRows(lineId);

        return count;
    }

    private int FindFirstRow(long lineId)
    {
        var low = 0;
        var high = _rows.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_rows[middle].Line.Id < lineId)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low < _rows.Count && _rows[low].Line.Id == lineId ? low : -1;
    }

    private int FindRow(Point point)
    {
        var first = FindFirstRow(point.Line);
        if (first < 0)
        {
            return -1;
        }

        var low = first;
        var high = _rows.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            var row = _rows[middle];
            if (row.Line.Id < point.Line || (row.Line.Id == point.Line && row.End < point.Offset))
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low < _rows.Count
            && _rows[low].Line.Id == point.Line
            && _rows[low].Start <= point.Offset
            && _rows[low].End >= point.Offset
                ? low
                : -1;
    }

    private void RemoveCachedRows(long lineId)
    {
        _rowGlyphRemovals.Clear();
        foreach (var key in _rowGlyphs.Keys)
        {
            if (key.LineId == lineId)
            {
                _rowGlyphRemovals.Add(key);
            }
        }

        foreach (var key in _rowGlyphRemovals)
        {
            _rowGlyphs.Remove(key);
        }
    }

    private static string[] CreateAsciiGlyphs()
    {
        var glyphs = new string[128];
        for (var index = 0; index < glyphs.Length; index++)
        {
            glyphs[index] = new string((char)index, 1);
        }

        return glyphs;
    }

    private readonly record struct Row(Line Line, int Start, int End);

    private readonly record struct RowKey(long LineId, int Start, int End);

    private readonly record struct DisplayGlyph(int Offset, int Width, string? Text);

    private readonly record struct Point(long Line, int Offset);

    private readonly record struct StyledRange(int Start, int End, PresentationTextRole Role, Uri? Link);
}
