namespace Threadsmith.Tui.TuiKit;

using System.Threading.Channels;
using Threadsmith.Core;
using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Content;
using TUIKit.Input;
using TUIKit.Modals;

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
    private ToolStatisticsSnapshot? _snapshot;
    private IReadOnlyList<string> _detailLines = [];
    private string? _error;
    private int _selected;
    private int _top;
    private int _page = 1;
    private int _detailTop;
    private int _detailPage = 1;
    private int _detailWidth;
    private int _textIndex;
    private bool _detailsFocused;
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
        _requests.Writer.TryWrite(false);
    }

    /// <summary>Gets requests: true resets, false reads a snapshot.</summary>
    internal ChannelReader<bool> Requests => _requests.Reader;

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
        else if (key.Code == KeyCode.Tab)
        {
            _detailsFocused = !_detailsFocused;
        }
        else if (_detailsFocused)
        {
            _detailTop = Math.Clamp(Navigate(key.Code, _detailTop, _detailPage, _detailLines.Count), 0, Math.Max(0, _detailLines.Count - _detailPage));
        }
        else
        {
            Select(Navigate(key.Code, _selected, _page, RowCount));
        }

        return true;
    }

    /// <inheritdoc />
    public override bool HandlePaste(string text) => true;

    /// <inheritdoc />
    public override bool HandleMouse(MouseEvent mouse)
    {
        if (!_dismissed && !_busy && _fits && _canHandleMouse() && mouse.Kind == MouseEventKind.Wheel)
        {
            var delta = mouse.Button == MouseButton.WheelUp ? -3 : 3;
            if (_detailsFocused)
            {
                _detailTop = Math.Clamp(_detailTop + delta, 0, Math.Max(0, _detailLines.Count - _detailPage));
            }
            else
            {
                Select(_selected + delta);
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override void Render(ISurface surface)
    {
        _textIndex = 0;
        var view = ModalFrame.Create(surface, _style(PresentationTextRole.Default), large: true);
        _fits = view is not null;
        if (view is null)
        {
            return;
        }

        Draw(view, 0, "Repository tool statistics", PresentationTextRole.SelectionPrompt);
        var footer = _busy ? "Storage pending | Esc close (admitted reset may still commit)"
            : "R: Reset all statistics | F5 refresh | Tab list/details | Esc close";
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
        _page = Math.Max(1, (view.Size.Height - 6) / 3);
        _top = Math.Clamp(_top, Math.Max(0, _selected - _page + 1), _selected);
        for (var index = _top; index < Math.Min(RowCount, _top + _page); index++)
        {
            Draw(
                view,
                2 + index - _top,
                (index == _selected ? "> " : "  ") + _snapshot.Rows[index].ToolId,
                index == _selected ? PresentationTextRole.SelectionPrompt : PresentationTextRole.Default);
        }

        var detailStart = 3 + _page;
        Draw(view, detailStart - 1, _detailsFocused ? "Details | Up/Down PgUp/PgDn Home/End scroll" : "List | Up/Down PgUp/PgDn Home/End | Tab details", PresentationTextRole.Status);
        _detailPage = Math.Max(1, view.Size.Height - detailStart - 1);
        if (_detailWidth != view.Size.Width)
        {
            _detailWidth = view.Size.Width;
            _detailLines = ToolStatisticsFormatter.Details(_snapshot.Rows[_selected])
                .SelectMany(line => TextWrapper.Wrap(StyledText.From(TranscriptView.Safe(line)), _detailWidth).Select(wrapped => wrapped.ToPlainString()))
                .ToArray();
        }

        _detailTop = Math.Clamp(_detailTop, 0, Math.Max(0, _detailLines.Count - _detailPage));
        for (var index = 0; index < Math.Min(_detailPage, _detailLines.Count - _detailTop); index++)
        {
            Draw(view, detailStart + index, _detailLines[_detailTop + index]);
        }
    }

    /// <summary>Applies an acknowledgement on the terminal owner unless dismissed.</summary>
    internal void CompleteRequest(ToolStatisticsSnapshot? snapshot, string? error)
    {
        if (_dismissed)
        {
            return;
        }

        var selectedId = _snapshot is not null && RowCount > 0 ? _snapshot.Rows[_selected].ToolId : null;
        _busy = false;
        _error = error;
        _snapshot = error is null ? snapshot : null;
        _selected = 0;
        if (_snapshot is { } refreshed && selectedId is not null)
        {
            for (var index = 0; index < RowCount; index++)
            {
                if (string.Equals(refreshed.Rows[index].ToolId, selectedId, StringComparison.Ordinal))
                {
                    _selected = index;
                    break;
                }
            }
        }

        _top = _detailTop = _detailWidth = 0;
        _detailLines = [];
    }

    private int RowCount => _snapshot?.Rows.Length ?? 0;

    private static int Navigate(KeyCode key, int position, int page, int count)
    {
        return key switch
        {
            KeyCode.Up => position - 1,
            KeyCode.Down => position + 1,
            KeyCode.PageUp => position - page,
            KeyCode.PageDown => position + page,
            KeyCode.Home => 0,
            KeyCode.End => Math.Max(0, count - 1),
            _ => position,
        };
    }

    private void Select(int selected)
    {
        selected = Math.Clamp(selected, 0, Math.Max(0, RowCount - 1));
        if (selected != _selected)
        {
            _selected = selected;
            _detailTop = _detailWidth = 0;
            _detailLines = [];
        }
    }

    private void Draw(ISurface surface, int y, string text, PresentationTextRole role = PresentationTextRole.Default)
    {
        _text[_textIndex++ % _text.Length].Draw(surface, 0, y, TranscriptView.Safe(text), _style(role));
    }
}
