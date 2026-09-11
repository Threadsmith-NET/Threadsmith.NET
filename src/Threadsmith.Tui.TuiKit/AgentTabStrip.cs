namespace Threadsmith.Tui.TuiKit;

using System.Globalization;
using TUIKit;
using TUIKit.Input;
using TUIKit.Widgets;

/// <summary>Adapts public TabView APIs with stable membership, overflow, and cell-correct hit testing.</summary>
internal sealed class AgentTabStrip : IWidget, IMouseAware
{
    private readonly AgentViews _views;
    private readonly Action<AgentView> _select;
    private readonly Func<Threadsmith.Interaction.Presentation.PresentationTextRole, CellStyle> _style;
    private readonly List<(AgentView View, int Left, int Width)> _hits = [];
    private TabView _tabs = new();
    private long _revision = -1;
    private int _width = -1;
    private int _start;
    private int _end;

    /// <summary>Initializes a new instance of the <see cref="AgentTabStrip"/> class.</summary>
    internal AgentTabStrip(AgentViews views, Action<AgentView> select, Func<Threadsmith.Interaction.Presentation.PresentationTextRole, CellStyle> style)
    {
        _views = views;
        _select = select;
        _style = style;
    }

    /// <inheritdoc />
    public Size Measure(Size available) => new(available.Width, 1);

    /// <inheritdoc />
    public void Render(ISurface surface)
    {
        if (_revision != _views.Revision || _width != surface.Size.Width)
        {
            Rebuild(surface.Size.Width);
        }

        _tabs.ActiveStyle = _style(Threadsmith.Interaction.Presentation.PresentationTextRole.AgentSelectedTabRole);
        _tabs.InactiveStyle = _style(Threadsmith.Interaction.Presentation.PresentationTextRole.AgentNotSelectedTabRole);
        surface.Fill(new Rect(0, 0, surface.Size.Width, surface.Size.Height), Cell.Blank(_style(Threadsmith.Interaction.Presentation.PresentationTextRole.AgentTabHeaderRole)));
        if (surface is BufferSurface buffer && surface.Size.Width > 1)
        {
            _tabs.Render(buffer.CreateView(new Rect(0, 0, surface.Size.Width - 1, 1)));
            if (_start > 0)
            {
                surface.DrawText(0, 0, "‹", _tabs.InactiveStyle);
            }

            if (_end < _views.Ordered.Count)
            {
                surface.DrawText(surface.Size.Width - 1, 0, "›", _tabs.InactiveStyle);
            }
        }
    }

    /// <inheritdoc />
    public bool HandleMouse(MouseEvent mouse)
    {
        if (mouse.Kind != MouseEventKind.Press || mouse.Button != MouseButton.Left || mouse.Y != 0)
        {
            return false;
        }

        if (mouse.X == 0 && _start > 0)
        {
            _select(_views.Ordered[_start - 1]);
            return true;
        }

        if (mouse.X == _width - 1 && _end < _views.Ordered.Count)
        {
            _select(_views.Ordered[_end]);
            return true;
        }

        foreach (var hit in _hits)
        {
            if (mouse.X >= hit.Left && mouse.X < hit.Left + hit.Width)
            {
                _select(hit.View);
                return true;
            }
        }

        return true;
    }

    /// <summary>Clips whole graphemes, retaining a distinguishing numeric suffix and role marker.</summary>
    internal static string Label(AgentView view, int width)
    {
        if (view.Snapshot is not { } snapshot)
        {
            return "MAIN";
        }

        var role = snapshot.Role switch
        {
            Threadsmith.Core.AgentRole.SecurityReviewer => "Sec",
            Threadsmith.Core.AgentRole.PerformanceReviewer => "Perf",
            Threadsmith.Core.AgentRole.ArchitectureReviewer => "Arch",
            Threadsmith.Core.AgentRole.TestReviewer => "Test",
            Threadsmith.Core.AgentRole.Implementer => "Impl",
            _ => "Exp",
        };
        var full = snapshot.Name + " · " + role;
        if (UnicodeWidth.GetWidth(full) <= width)
        {
            return full;
        }

        var suffixStart = snapshot.Name.LastIndexOf('_');
        var candidate = suffixStart >= 0 ? snapshot.Name[suffixStart..] : string.Empty;
        var suffix = candidate.Length is >= 2 and <= 11 && candidate[1..].All(char.IsAsciiDigit) ? candidate : string.Empty;
        if (UnicodeWidth.GetWidth("…" + suffix + "·" + role) > width)
        {
            suffix = string.Empty;
        }

        var budget = Math.Max(0, width - UnicodeWidth.GetWidth("…" + suffix + "·" + role));
        var prefix = string.Empty;
        var elements = StringInfo.GetTextElementEnumerator(snapshot.Name);
        while (elements.MoveNext() && UnicodeWidth.GetWidth(prefix + elements.GetTextElement()) <= budget)
        {
            prefix += elements.GetTextElement();
        }

        return prefix + "…" + suffix + "·" + role;
    }

    private void Rebuild(int width)
    {
        _width = width;
        _revision = _views.Revision;
        _tabs = new TabView();
        _hits.Clear();
        var selected = _views.Ordered.ToList().IndexOf(_views.Selected);
        var available = Math.Max(1, width - 1);
        var labelWidth = Math.Clamp(available - 3, 8, 25);
        _start = Math.Min(_start, selected);
        if (selected >= _end)
        {
            _start = selected;
        }

        var x = 0;
        _end = _start;
        for (var index = _start; index < _views.Ordered.Count; index++)
        {
            var view = _views.Ordered[index];
            var label = Label(view, labelWidth);
            var cells = UnicodeWidth.GetWidth(label) + 3;
            if (x + cells > width - 1 && _hits.Count > 0)
            {
                break;
            }

            _tabs.Add(label, view.Transcript);
            _hits.Add((view, x, cells - 1));
            if (index == selected)
            {
                _tabs.Activate(_hits.Count - 1);
            }

            x += cells;
            _end = index + 1;
        }

        if (selected >= _end && _start != selected)
        {
            _start = selected;
            Rebuild(width);
        }
    }
}
