namespace Threadsmith.Tui.TuiKit;

using TUIKit;

/// <summary>Paints persistence health in the existing bottom row without changing layout or output.</summary>
internal sealed class ToolStatisticsHealthIndicator
{
    /// <summary>The fixed host-owned failure text.</summary>
    internal const string Message = "Tool statistics persistence failure: disabled";
    private readonly CachedTextRun _run = new();

    /// <summary>Right-aligns the warning while preserving the terminal's final-cell safety margin.</summary>
    internal void Render(ISurface surface, bool suppressStyles)
    {
        var width = Math.Max(0, surface.Size.Width - 2);
        if (width == 0 || surface.Size.Height == 0)
        {
            return;
        }

        // Preserve the disabled suffix when the full wording cannot fit on a narrow terminal.
        var text = width >= Message.Length ? Message : width > 3 ? "..." + Message[^(width - 3)..] : Message[^width..];
        var style = suppressStyles ? CellStyle.Default : CellStyle.Default.WithForeground(Color.FromPalette(1));
        var x = surface.Size.Width - 1 - text.Length;
        var y = surface.Size.Height - 1;
        surface.Fill(new Rect(x, y, text.Length, 1), Cell.Blank(style));
        _run.Draw(surface, x, y, text, style);
    }
}
