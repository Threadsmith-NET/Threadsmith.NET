namespace Threadsmith.Tui.TuiKit;

using System.Threading.Channels;
using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Input;
using TUIKit.Modals;
using TUIKit.Widgets;

/// <summary>Retained browser for stored tool counts and measured durations.</summary>
internal sealed class ToolStatisticsModal : Modal
{
    private readonly Func<PresentationTextRole, CellStyle> _style;
    private readonly Action _interrupt;
    private readonly Action _toggleMouse;
    private readonly Func<bool> _canHandleMouse;
    private readonly Channel<bool> _requests = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true,
        AllowSynchronousContinuations = false,
    });

    private readonly CachedTextRun[] _text = [.. Enumerable.Range(0, 128).Select(_ => new CachedTextRun())];
    private readonly DataTable<ToolStatisticsTableRow> _table = new();
    private readonly DataColumn<ToolStatisticsTableRow>[] _columns;
    private readonly ScrollView _scroll;
    private ToolStatisticsTableSnapshot? _snapshot;
    private string? _error;
    private Rect _tableBounds;
    private int _textIndex;
    private bool _busy = true;
    private bool _dismissed;
    private bool _fits;

    /// <summary>Initializes a new instance of the <see cref="ToolStatisticsModal"/> class.</summary>
    internal ToolStatisticsModal(Func<PresentationTextRole, CellStyle> style, Action interrupt, Action toggleMouse, Func<bool> canHandleMouse)
    {
        _style = style;
        _interrupt = interrupt;
        _toggleMouse = toggleMouse;
        _canHandleMouse = canHandleMouse;
        _columns = new DataColumn<ToolStatisticsTableRow>[ToolStatisticsTableSnapshot.ColumnNames.Length];
        for (var index = 0; index < _columns.Length; index++)
        {
            var columnIndex = index;
            var column = new DataColumn<ToolStatisticsTableRow>(ToolStatisticsTableSnapshot.ColumnNames[index], row => row.Cells[columnIndex]);
            _columns[index] = column;
            _table.Column(column);
        }

        _scroll = new ScrollView(_table, 1, 1) { AutoScrollToFocus = false };
        _requests.Writer.TryWrite(false);
    }

    /// <summary>Gets requests: true resets, false reads a snapshot.</summary>
    internal ChannelReader<bool> Requests => _requests.Reader;

    /// <summary>Gets the horizontal viewport offset in terminal cells.</summary>
    internal int HorizontalOffset => _scroll.ScrollX;

    /// <summary>Gets the finite horizontal content extent in terminal cells.</summary>
    internal int TableWidth { get; private set; }

    /// <inheritdoc />
    public override bool HandleKey(KeyEvent key)
    {
        key = TuiKitInput.Normalize(key);
        if (_dismissed)
        {
            return true;
        }

        if (key.Code == KeyCode.Escape)
        {
            _dismissed = true;
            RequestClose(null);
        }
        else if (key.Code == KeyCode.Character && key.Rune == 'c' && key.Modifiers == KeyModifiers.Ctrl)
        {
            _interrupt();
        }
        else if (key.Code == KeyCode.F12)
        {
            _toggleMouse();
        }
        else if (_busy)
        {
            return true;
        }
        else if (key.Code == KeyCode.F5 || (key.Code == KeyCode.Character && key.Rune is 'r' or 'R' && key.Modifiers == KeyModifiers.None))
        {
            _busy = true;
            _error = null;
            _requests.Writer.TryWrite(key.Code != KeyCode.F5);
        }
        else if (_fits)
        {
            if (key.Code is KeyCode.Left or KeyCode.Right)
            {
                _scroll.HandleKey(key);
            }
            else
            {
                _table.HandleKey(key);
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool HandlePaste(string text) => true;

    /// <inheritdoc />
    public override bool HandleMouse(MouseEvent mouse)
    {
        if (!_dismissed && !_busy && _fits && _canHandleMouse()
            && mouse.X >= _tableBounds.Left && mouse.X < _tableBounds.Left + _tableBounds.Width
            && mouse.Y >= _tableBounds.Top && mouse.Y < _tableBounds.Top + _tableBounds.Height)
        {
            var local = WorkspaceLayout.Translate(mouse, _tableBounds);
            if (mouse.Kind == MouseEventKind.Wheel && mouse.Button is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                var key = KeyEvent.Special(mouse.Button == MouseButton.WheelUp ? KeyCode.Up : KeyCode.Down);
                for (var step = 0; step < 3; step++)
                {
                    _table.HandleKey(key);
                }
            }
            else
            {
                _scroll.HandleMouse(local);
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override void Render(ISurface surface)
    {
        _textIndex = 0;
        var view = ModalFrame.Create(surface, _style(PresentationTextRole.Default), large: true, expandHeight: true);
        _fits = view is not null;
        if (view is null)
        {
            return;
        }

        Draw(view, 0, "Repository tool statistics", PresentationTextRole.SelectionPrompt);
        var footer = _busy ? "Storage pending | Esc close (admitted reset may still commit)"
            : "R reset | F5 refresh | Arrows scroll | PgUp/PgDn Home/End | Esc close";
        Draw(view, view.Size.Height - 1, footer, PresentationTextRole.Status);
        if (_error is { } error)
        {
            Draw(view, 2, "Statistics unavailable: " + error, PresentationTextRole.Error);
            Draw(view, 3, "No reset or exact snapshot confirmed. F5 retries.");
            return;
        }

        if (_snapshot is null)
        {
            Draw(view, 2, "Loading tool statistics...");
            return;
        }

        if (RowCount == 0)
        {
            Draw(view, 2, "No completed tool invocations have been recorded.");
            return;
        }

        Draw(view, 1, "Stored tools, including unavailable tools. Counts include rejections.");
        var tableArea = new Rect(0, 3, view.Size.Width, Math.Max(1, view.Size.Height - 4));
        var frame = ModalFrame.Bounds(surface.Size, large: true, expandHeight: true);
        var content = WorkspaceLayout.ModalContent(new Size(frame.Width, frame.Height));
        _tableBounds = new Rect(frame.Left + content.Left, frame.Top + content.Top + tableArea.Top, tableArea.Width, tableArea.Height);
        var rowStyle = _style(PresentationTextRole.Default);
        _table.HeaderStyle = _style(PresentationTextRole.Status)
            .WithBackground(_style(PresentationTextRole.OutputStreamPaneRole).Background);
        _table.RowStyle = _ => rowStyle;
        _table.HighlightStyle = _style(PresentationTextRole.SelectionPrompt);
        _table.InactiveHighlightStyle = _style(PresentationTextRole.SelectionPrompt);
        _scroll.TrackStyle = _style(PresentationTextRole.Status);
        _scroll.ThumbStyle = _style(PresentationTextRole.SelectionPrompt);

        // Only horizontal overflow is hosted here; DataTable virtualizes the rows at viewport height.
        var contentHeight = Math.Max(1, tableArea.Height - (TableWidth > tableArea.Width ? 1 : 0));
        _scroll.SetContentSize(Math.Max(TableWidth, tableArea.Width), contentHeight);
        _scroll.Render(view.CreateView(tableArea));
    }

    /// <summary>Applies an acknowledgement on the terminal owner unless dismissed.</summary>
    internal void CompleteRequest(ToolStatisticsTableSnapshot? snapshot, string? error)
    {
        if (_dismissed)
        {
            return;
        }

        var selectedId = _snapshot is not null && RowCount > 0 ? _snapshot.Rows[_table.SelectedIndex].ToolId : null;
        _busy = false;
        _error = error;
        _snapshot = error is null ? snapshot : null;
        _table.Bind(_snapshot?.Rows ?? []);
        _table.Select(0);
        if (_snapshot is { } refreshed && selectedId is not null && refreshed.RowIndices.TryGetValue(selectedId, out var index))
        {
            _table.Select(index);
        }

        TableWidth = 0;
        for (var column = 0; column < _columns.Length; column++)
        {
            var width = _snapshot?.ColumnWidths[column] ?? ToolStatisticsTableSnapshot.ColumnNames[column].Length + 1;
            _columns[column].Width = width;
            TableWidth += width;
        }
    }

    private int RowCount => _snapshot?.Rows.Length ?? 0;

    private void Draw(ISurface surface, int y, string text, PresentationTextRole role = PresentationTextRole.Default)
    {
        _text[_textIndex++ % _text.Length].Draw(surface, 0, y, TranscriptView.Safe(text), _style(role));
    }
}
