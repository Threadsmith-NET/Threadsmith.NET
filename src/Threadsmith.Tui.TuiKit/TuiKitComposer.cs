namespace Threadsmith.Tui.TuiKit;

using TUIKit;
using TUIKit.Input;
using TUIKit.Widgets;

/// <summary>A small retained composer; text is independent of its wrapped cell layout.</summary>
internal sealed class TuiKitComposer : IWidget, IFocusable, IFocusAware, IMouseAware
{
    private readonly List<Position> _positions = [];
    private readonly List<Glyph> _glyphs = [];
    private int _firstRowOffset;
    private int _layoutRevision = -1;
    private int _width = 80;
    private int _height = 4;
    private int _top;
    private int? _preferredColumn;

    /// <summary>Gets the bounded editable draft.</summary>
    internal ComposerBuffer Buffer { get; } = new();

    /// <summary>Gets this composer purpose's independent submission history.</summary>
    internal ComposerHistory History { get; } = new();

    /// <summary>Gets whether this composer owns keyboard focus.</summary>
    internal bool IsFocused { get; private set; }

    /// <summary>Gets or sets the current theme style.</summary>
    internal CellStyle Style { get; set; } = CellStyle.Default;

    /// <summary>Gets or sets the cells reserved before editable text on the first row.</summary>
    internal int FirstRowOffset
    {
        get => _firstRowOffset;
        set
        {
            var normalized = Math.Max(0, value);
            if (_firstRowOffset == normalized)
            {
                return;
            }

            _firstRowOffset = normalized;
            _layoutRevision = -1;
        }
    }

    /// <summary>Gets or replaces the exact draft.</summary>
    internal string Text { get => Buffer.Text; set => Buffer.Reset(value); }

    /// <summary>Gets or sets the clipboard writer; false leaves cut text intact.</summary>
    internal Func<string, bool>? CopyRequested { get; set; }

    /// <summary>Gets or sets the explicit clipboard request callback.</summary>
    internal Action? PasteRequested { get; set; }

    /// <inheritdoc />
    public void OnFocusChanged(bool focused) => IsFocused = focused;

    /// <inheritdoc />
    public Size Measure(Size available) => new(available.Width, Math.Min(4, available.Height));

    /// <inheritdoc />
    public bool HandleKey(KeyEvent key)
    {
        key = TuiKitInput.Normalize(key);
        var ctrl = (key.Modifiers & KeyModifiers.Ctrl) != 0;
        var shift = (key.Modifiers & KeyModifiers.Shift) != 0;
        var alt = (key.Modifiers & KeyModifiers.Alt) != 0;
        if ((key.Modifiers == KeyModifiers.None && key.Code is KeyCode.Up or KeyCode.Down)
            || (key.Modifiers == KeyModifiers.Ctrl && key.Code == KeyCode.Character && key.Rune is 'p' or 'n'))
        {
            EnsureLayout(_width);
            var previous = key.Code == KeyCode.Up || key.Rune == 'p';
            var row = _positions[Array.BinarySearch(Buffer.Boundaries, Buffer.Caret)].Row;
            if (History.Navigate(Buffer, previous, previous ? row == 0 : row == _positions[^1].Row))
            {
                _preferredColumn = null;
                return true;
            }

            MoveVertical(previous ? KeyCode.Up : KeyCode.Down, false);
            return true;
        }

        History.LeaveNavigationKey();
        if (key.Code == KeyCode.Character && alt && !ctrl)
        {
            switch (key.Rune)
            {
                case 'b':
                    Buffer.MoveHorizontal(true, shift, true);
                    break;
                case 'f':
                    Buffer.MoveHorizontal(false, shift, true);
                    break;
                case 'd':
                    Buffer.Delete(false, true);
                    break;
                default:
                    return false;
            }

            _preferredColumn = null;
            return true;
        }

        if (key.Code == KeyCode.Character && ctrl)
        {
            switch (key.Rune)
            {
                case 'a':
                    Buffer.SelectAll();
                    break;
                case 'z' when shift:
                    Buffer.Redo();
                    break;
                case 'z':
                    Buffer.Undo();
                    break;
                case 'y':
                    Buffer.Redo();
                    break;
                case 'c':
                    CopyRequested?.Invoke(shift ? Text : Buffer.Selection);
                    break;
                case 'b':
                    Buffer.MoveHorizontal(true, false);
                    break;
                case 'f':
                    Buffer.MoveHorizontal(false, false);
                    break;
                case 'h':
                    Buffer.Delete(true, true);
                    break;
                case 'd':
                    Buffer.Delete(false);
                    break;
                case 'k':
                    Buffer.DeleteToLineBoundary(true);
                    break;
                case 'u':
                    Buffer.DeleteToLineBoundary(false);
                    break;
                case 'x':
                    var selected = Buffer.SelectionStart != Buffer.SelectionEnd;
                    if (CopyRequested?.Invoke(selected ? Buffer.Selection : Buffer.CurrentLine()) == true)
                    {
                        if (selected)
                        {
                            Buffer.Delete(false);
                        }
                        else
                        {
                            Buffer.DeleteLine();
                        }
                    }

                    break;
                case 'v':
                    PasteRequested?.Invoke();
                    break;
                default:
                    return false;
            }

            _preferredColumn = null;
            return true;
        }

        switch (key.Code)
        {
            case KeyCode.Character:
                if (key.Rune is < 32 or (>= 127 and <= 159))
                {
                    return false;
                }

                Buffer.Insert(char.ConvertFromUtf32(key.Rune));
                break;
            case KeyCode.Enter:
                Buffer.Insert("\n");
                break;
            case KeyCode.Left:
                Buffer.MoveHorizontal(true, shift, ctrl);
                break;
            case KeyCode.Right:
                Buffer.MoveHorizontal(false, shift, ctrl);
                break;
            case KeyCode.Backspace:
                Buffer.Delete(true, ctrl || alt);
                break;
            case KeyCode.Delete when shift:
                if (Buffer.SelectionStart != Buffer.SelectionEnd)
                {
                    Buffer.Delete(false);
                }
                else
                {
                    Buffer.DeleteLine();
                }

                break;
            case KeyCode.Delete:
                Buffer.Delete(false, ctrl || alt);
                break;
            case KeyCode.Tab:
                Buffer.Indent(shift);
                break;
            case KeyCode.Insert when shift:
                PasteRequested?.Invoke();
                break;
            case KeyCode.Insert when ctrl:
                CopyRequested?.Invoke(Buffer.Selection);
                break;
            case KeyCode.Up:
            case KeyCode.Down:
            case KeyCode.PageUp:
            case KeyCode.PageDown:
                MoveVertical(key.Code, shift);
                return true;
            case KeyCode.Home:
            case KeyCode.End:
                var end = key.Code == KeyCode.End;
                var target = ctrl ? end ? Buffer.Text.Length : 0 : Buffer.LineBoundary(end, smartHome: !end);
                Buffer.MoveTo(target, shift);
                break;
            default:
                return false;
        }

        _preferredColumn = null;
        return true;
    }

    /// <inheritdoc />
    public bool HandleMouse(MouseEvent mouse)
    {
        if (mouse.Kind == MouseEventKind.Release)
        {
            return true;
        }

        if (mouse.Button != MouseButton.Left)
        {
            return false;
        }

        EnsureLayout(_width);
        var row = Math.Clamp(mouse.Y + _top, 0, _positions[^1].Row);
        if (FindClosestPosition(row, mouse.X) is { } position)
        {
            var extend = mouse.Kind == MouseEventKind.Move || (mouse.Modifiers & KeyModifiers.Shift) != 0;
            Buffer.MoveTo(position.Offset, extend);
        }

        return true;
    }

    /// <inheritdoc />
    public void Render(ISurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        if (surface.Size.Width < 2 || surface.Size.Height < 1)
        {
            return;
        }

        _height = surface.Size.Height;
        EnsureLayout(surface.Size.Width);
        var caret = _positions[Array.BinarySearch(Buffer.Boundaries, Buffer.Caret)];
        _top = Math.Clamp(_top, Math.Max(0, caret.Row - _height + 1), caret.Row);
        surface.Fill(new Rect(0, 0, _width, _height), Cell.Blank(Style));
        foreach (var glyph in _glyphs)
        {
            if (glyph.Row < _top)
            {
                continue;
            }

            if (glyph.Row >= _top + _height)
            {
                break;
            }

            var selected = glyph.Offset >= Buffer.SelectionStart && glyph.Offset < Buffer.SelectionEnd;
            var atCaret = IsFocused && glyph.Offset == Buffer.Caret;
            var style = selected || atCaret ? Style.WithAttribute(CellAttributes.Reverse, true) : Style;
            surface.Set(glyph.Column, glyph.Row - _top, Cell.Glyph(glyph.Text, style, glyph.Width));
            if (glyph.Width == 2)
            {
                surface.Set(glyph.Column + 1, glyph.Row - _top, Cell.Continuation(style));
            }
        }

        if (IsFocused && (Buffer.Caret == Text.Length || Text[Buffer.Caret] == '\n'))
        {
            surface.Set(
                caret.Column,
                caret.Row - _top,
                Cell.Blank(Style.WithAttribute(CellAttributes.Reverse, true)));
        }
    }

    /// <summary>Inserts pasted or typed text as a reversible edit.</summary>
    internal void InsertText(string text)
    {
        Buffer.Insert(text);
        _preferredColumn = null;
    }

    private void MoveVertical(KeyCode key, bool extend)
    {
        EnsureLayout(_width);
        var current = _positions[Array.BinarySearch(Buffer.Boundaries, Buffer.Caret)];
        _preferredColumn ??= current.Column;
        var rows = key is KeyCode.PageUp or KeyCode.PageDown ? _height : 1;
        var direction = key is KeyCode.Up or KeyCode.PageUp ? -1 : 1;
        var row = Math.Clamp(current.Row + (rows * direction), 0, _positions[^1].Row);
        if (FindClosestPosition(row, _preferredColumn.Value) is { } target)
        {
            Buffer.MoveTo(target.Offset, extend);
        }
    }

    private void EnsureLayout(int width)
    {
        if (_layoutRevision == Buffer.Revision && width == _width)
        {
            return;
        }

        _width = width;
        _layoutRevision = Buffer.Revision;
        _glyphs.Clear();
        _positions.Clear();
        var row = 0;
        var column = Math.Min(_firstRowOffset, Math.Max(0, width - 1));
        for (var index = 0; index < Buffer.Boundaries.Length - 1; index++)
        {
            var offset = Buffer.Boundaries[index];
            var element = Buffer.Elements[index];
            var cluster = element.Text;
            var newline = cluster == "\n";
            var tab = cluster == "\t";
            var measuredWidth = newline || tab ? 0 : element.Width;
            var cells = newline ? 0 : tab ? 4 - (column % 4) : Math.Clamp(measuredWidth, 1, 2);
            if (!newline && column + cells > width)
            {
                row++;
                column = 0;
                if (tab)
                {
                    cells = Math.Min(4, width);
                }
            }

            if (newline)
            {
                _positions.Add(new Position(offset, column == width ? row + 1 : row, column == width ? 0 : column));
                row++;
                column = 0;
                continue;
            }

            _positions.Add(new Position(offset, row, column));

            if (tab)
            {
                for (var cell = 0; cell < cells; cell++)
                {
                    _glyphs.Add(new Glyph(offset, row, column + cell, " ", 1));
                }
            }
            else
            {
                // Unsafe controls are visible/inert; a leading mark gets a display-only base.
                if (ContainsControl(cluster))
                {
                    cluster = "?";
                }
                else if (measuredWidth == 0)
                {
                    cluster = "\u25cc" + cluster;
                }

                _glyphs.Add(new Glyph(offset, row, column, cluster, cells));
            }

            column += cells;
        }

        if (column == width)
        {
            row++;
            column = 0;
        }

        _positions.Add(new Position(Text.Length, row, column));
    }

    private Position? FindClosestPosition(int row, int column)
    {
        var low = 0;
        var high = _positions.Count;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (_positions[middle].Row < row)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        Position? closest = null;
        var distance = int.MaxValue;
        for (var index = low; index < _positions.Count && _positions[index].Row == row; index++)
        {
            var position = _positions[index];
            var candidateDistance = Math.Abs(position.Column - column);
            if (candidateDistance < distance)
            {
                closest = position;
                distance = candidateDistance;
            }

            if (position.Column >= column)
            {
                break;
            }
        }

        return closest;
    }

    private static bool ContainsControl(string value)
    {
        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct Position(int Offset, int Row, int Column);

    private readonly record struct Glyph(int Offset, int Row, int Column, string Text, int Width);
}
