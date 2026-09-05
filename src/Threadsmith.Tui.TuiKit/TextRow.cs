namespace Threadsmith.Tui.TuiKit;

using TUIKit;
using TUIKit.Widgets;

/// <summary>Renders one fixed retained row from cached graphemes.</summary>
internal sealed class TextRow : IWidget
{
    private readonly CachedTextRun _run = new();
    private readonly Func<string> _text;
    private readonly Func<CellStyle> _style;

    /// <summary>Initializes a new instance of the <see cref="TextRow"/> class.</summary>
    internal TextRow(Func<string> text, Func<CellStyle> style)
    {
        _text = text;
        _style = style;
    }

    /// <inheritdoc />
    public Size Measure(Size available) => new(available.Width, 1);

    /// <inheritdoc />
    public void Render(ISurface surface)
    {
        var style = _style();
        surface.Fill(new Rect(0, 0, surface.Size.Width, surface.Size.Height), Cell.Blank(style));
        _run.Draw(surface, 0, 0, _text(), style);
    }
}
