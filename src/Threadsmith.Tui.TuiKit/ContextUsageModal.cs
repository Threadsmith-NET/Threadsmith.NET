namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Core;
using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Content;
using TUIKit.Input;
using TUIKit.Modals;
using TUIKit.Widgets;

/// <summary>Bounded viewport over a frozen request, with graphs rendered by native TUIKit widgets.</summary>
internal sealed class ContextUsageModal : Modal
{
    private readonly ContextUsageSnapshot? _snapshot;
    private readonly IReadOnlyList<ContextUsageComponent> _categories;
    private readonly IReadOnlyList<ContextUsageComponent> _components;
    private readonly int _stablePrefixRows;
    private readonly Func<PresentationTextRole, CellStyle> _style;
    private readonly Action _interrupt;
    private readonly Action _toggleMouse;
    private readonly Func<bool> _canHandleMouse;
    private readonly BarChart _chart = new();
    private readonly ProgressBar _progress = new();
    private readonly CellBuffer _chartBuffer = new(1, 2);
    private readonly CachedTextRun[] _text = [.. Enumerable.Range(0, 100).Select(_ => new CachedTextRun())];
    private IReadOnlyList<ContextUsageComponent> _rows = [];
    private int _textIndex;
    private int _selected;
    private int _top;
    private int _page = 1;
    private bool _categoryView;
    private double _maximum;
    private int _basisWidth;
    private IReadOnlyList<string> _basisLines = [];

    /// <summary>Initializes a new instance of the <see cref="ContextUsageModal"/> class with frozen metadata.</summary>
    internal ContextUsageModal(ContextUsageSnapshot? snapshot, Func<PresentationTextRole, CellStyle> style, Action interrupt, Action toggleMouse, Func<bool>? canHandleMouse = null)
    {
        _snapshot = snapshot;
        _categories = snapshot is null ? [] : ContextUsageFormatter.Categories(snapshot);
        _components = snapshot is null ? [] : ContextUsageFormatter.OrderedContributions(snapshot).ToArray();
        _stablePrefixRows = snapshot is null ? 0 : ContextUsageFormatter.StablePrefixContributionCount(snapshot);
        _style = style;
        _interrupt = interrupt;
        _toggleMouse = toggleMouse;
        _canHandleMouse = canHandleMouse ?? (() => true);
        Reflow();
    }

    /// <inheritdoc />
    public override bool HandleKey(KeyEvent key)
    {
        key = TuiKitInput.Normalize(key);
        if (key.Code == KeyCode.Escape)
        {
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
        else if (key.Code == KeyCode.Tab)
        {
            _categoryView = !_categoryView;
            _selected = _top = 0;
            Reflow();
        }
        else
        {
            _selected = Math.Clamp(
                key.Code switch
            {
                KeyCode.Up => _selected - 1,
                KeyCode.Down => _selected + 1,
                KeyCode.PageUp => _selected - _page,
                KeyCode.PageDown => _selected + _page,
                KeyCode.Home => 0,
                KeyCode.End => _rows.Count - 1,
                _ => _selected,
            },
                0,
                Math.Max(0, _rows.Count - 1));
        }

        return true;
    }

    /// <inheritdoc />
    public override bool HandlePaste(string text) => true;

    /// <inheritdoc />
    public override bool HandleMouse(MouseEvent mouse)
    {
        if (!_canHandleMouse())
        {
            return true;
        }

        if (mouse.Kind == MouseEventKind.Wheel)
        {
            _selected = Math.Clamp(_selected + (mouse.Button == MouseButton.WheelUp ? -3 : 3), 0, Math.Max(0, _rows.Count - 1));
        }

        return true;
    }

    /// <inheritdoc />
    public override void Render(ISurface surface)
    {
        _textIndex = 0;
        var view = ModalFrame.Create(surface, _style(PresentationTextRole.Default), large: true);
        if (view is null)
        {
            return;
        }

        Draw(view, 0, "Context usage — Estimated", PresentationTextRole.SelectionPrompt);
        if (_snapshot is not { } snapshot)
        {
            Draw(view, 2, "No captured request usage available.");
            Draw(view, 3, "Send a request first; Esc closes.");
            return;
        }

        Draw(view, 1, $"{snapshot.ModelName ?? snapshot.ModelProfileId?.Value.ToString() ?? "Model unknown"} · {snapshot.Stage} #{snapshot.Round + 1} · {snapshot.CapturedAt:HH:mm:ss} UTC · {(snapshot.DispatchStarted ? "submitted" : "prepared; submission unobserved")}");
        Draw(view, 2, $"Host-stable prefix {snapshot.StablePrefixTokens:N0} tokens · cache eligible · actual cache use requires provider telemetry");
        if (_basisWidth != view.Size.Width)
        {
            _basisWidth = view.Size.Width;
            _basisLines = TextWrapper.Wrap(StyledText.From(ContextUsageFormatter.Label(snapshot.EstimationBasis)), _basisWidth).Select(line => line.ToPlainString()).ToArray();
        }

        var footerHeight = _basisLines.Count + 2;
        if (view.Size.Height < footerHeight + 9)
        {
            Draw(view, 3, $"Input {snapshot.InputTokens:N0} · Used {ContextUsageFormatter.Percent(snapshot.InputTokens, snapshot.ContextWindow)}");
            Draw(view, 4, "Enlarge terminal for graphs; Esc closes.");
            return;
        }

        var sideBySide = view.Size.Width >= 100;
        var detailWidth = sideBySide ? view.Size.Width - 39 : view.Size.Width;
        var summary = sideBySide ? view.CreateView(new Rect(detailWidth + 1, 3, 38, view.Size.Height - 3 - footerHeight))
            : view.CreateView(new Rect(0, 3, view.Size.Width, 3));
        Draw(summary, 0, $"Input {snapshot.InputTokens:N0} tokens · {ContextUsageFormatter.Percent(snapshot.InputTokens, snapshot.ContextWindow)} used");
        Draw(summary, 1, $"Window {snapshot.ContextWindow?.ToString("N0") ?? "?"} · Reserve {snapshot.OutputReserve:N0}");
        if (snapshot.ContextWindow is > 0)
        {
            _progress.Value = (double)snapshot.InputTokens / snapshot.ContextWindow.Value;
            _progress.FillColor = _style(PresentationTextRole.Status).Foreground;
            _progress.Render(summary.CreateView(new Rect(0, 2, summary.Size.Width, 1)));
        }
        else
        {
            Draw(summary, 2, "Window usage unavailable");
        }

        if (sideBySide)
        {
            Draw(summary, 3, "Categories — % of input (rounded)", PresentationTextRole.SelectionPrompt);
            var y = 4;
            var visible = Math.Max(0, (summary.Size.Height - y) / 2);
            if (visible < _categories.Count)
            {
                visible = Math.Max(0, (summary.Size.Height - y - 1) / 2);
            }

            foreach (var item in _categories.Take(visible))
            {
                Draw(summary, y++, $"{item.Label} {ContextUsageFormatter.Percent(item.Tokens, snapshot.InputTokens)}");
                Graph(summary, y++, item.Tokens, _categories.Count > 0 ? _categories[0].Tokens : 0);
            }

            if (visible < _categories.Count)
            {
                Draw(summary, y, $"+{_categories.Count - visible} categories · Tab for all");
            }
        }

        var start = sideBySide ? 3 : 7;
        var detail = view.CreateView(new Rect(0, start, detailWidth, view.Size.Height - start - footerHeight));
        _page = Math.Max(1, detail.Size.Height / 2);
        _top = Math.Clamp(_top, Math.Max(0, _selected - _page + 1), _selected);
        for (var index = _top; index < Math.Min(_rows.Count, _top + _page); index++)
        {
            var item = _rows[index];
            var y = (index - _top) * 2;
            var prefixMarker = !_categoryView && index < _stablePrefixRows
                ? index == _stablePrefixRows - 1 ? '└' : '│'
                : ' ';
            Draw(detail, y, $"{(index == _selected ? ">" : " ")}{prefixMarker}[{item.Container}] {item.Label}", index == _selected ? PresentationTextRole.SelectionPrompt : PresentationTextRole.Default);
            Graph(detail, y + 1, item.Tokens, _maximum, leftPadding: 2);
            var prefixContinuation = !_categoryView && index < _stablePrefixRows - 1 ? '│' : ' ';
            Draw(detail, y + 1, $" {prefixContinuation}");
        }

        for (var index = 0; index < _basisLines.Count; index++)
        {
            Draw(view, view.Size.Height - footerHeight + index, _basisLines[index]);
        }

        var stableSuffix = !_categoryView && _selected < _stablePrefixRows ? " · stable prefix" : string.Empty;
        Draw(view, view.Size.Height - 2, _rows.Count > 0 ? $"{ContextUsageFormatter.Label(_rows[_selected].Label)} · {_rows[_selected].Tokens:N0} tokens · {ContextUsageFormatter.Percent(_rows[_selected].Tokens, snapshot.InputTokens)} of input{stableSuffix}" : "No included content.");
        Draw(view, view.Size.Height - 1, "↑↓ PgUp/Dn Home/End · Tab categories/order · Esc close");
    }

    private void Draw(ISurface surface, int y, string text, PresentationTextRole role = PresentationTextRole.Default)
    {
        _text[_textIndex++ % _text.Length].Draw(surface, 0, y, ContextUsageFormatter.Label(text), _style(role));
    }

    private void Graph(BufferSurface surface, int y, long tokens, double maximum, int leftPadding = 0)
    {
        // A clipped second row fixes the native chart's scale across scrolling.
        // The scratch surface is always two rows, independent of the source inventory size.
        var width = Math.Max(1, surface.Size.Width - leftPadding);
        if (_chartBuffer.Width != width)
        {
            _chartBuffer.Resize(width, 2);
        }

        _chart.Clear();
        _chart.Add(string.Empty, tokens);
        _chart.Add(string.Empty, maximum);
        _chartBuffer.Clear(_style(PresentationTextRole.Default));
        _chart.Render(new BufferSurface(_chartBuffer));
        for (var x = 0; x < width && x + leftPadding < surface.Size.Width; x++)
        {
            var cell = _chartBuffer.Get(x, 0);
            surface.Set(x + leftPadding, y, Cell.Glyph(cell.Grapheme, _style(PresentationTextRole.Status), 1));
        }
    }

    private void Reflow()
    {
        _rows = _categoryView ? _categories : _components;
        _maximum = _rows.Select(item => (double)item.Tokens).DefaultIfEmpty().Max();
        _selected = Math.Min(_selected, Math.Max(0, _rows.Count - 1));
    }
}
