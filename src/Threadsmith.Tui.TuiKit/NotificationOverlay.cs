namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Modals;

/// <summary>Keeps native toast placement and expiration while applying current host theme roles.</summary>
internal sealed class NotificationOverlay : ISurface
{
    private readonly ISurface _surface;
    private readonly IReadOnlyList<Notification> _notifications;
    private readonly Func<PresentationTextRole, CellStyle> _style;

    private NotificationOverlay(ISurface surface, IReadOnlyList<Notification> notifications, Func<PresentationTextRole, CellStyle> style)
    {
        _surface = surface;
        _notifications = notifications;
        _style = style;
    }

    /// <inheritdoc />
    public Size Size => _surface.Size;

    /// <inheritdoc />
    public void Set(int x, int y, Cell cell)
    {
        if (y >= 0 && y < _notifications.Count)
        {
            _surface.Set(x, y, Restyle(cell, y));
        }
    }

    /// <inheritdoc />
    public void Fill(Rect region, Cell cell)
    {
        for (var y = Math.Max(0, region.Top); y < Math.Min(region.Bottom, _notifications.Count); y++)
        {
            _surface.Fill(new Rect(region.Left, y, region.Width, 1), Restyle(cell, y));
        }
    }

    /// <summary>Draws native notifications only within the supplied output-content rectangle.</summary>
    internal static void Render(ISurface surface, NotificationCenter notifications, long nowMilliseconds, Func<PresentationTextRole, CellStyle> style)
    {
        var active = notifications.Active(nowMilliseconds);
        if (active.Count > 0)
        {
            notifications.Render(new NotificationOverlay(surface, active, style), nowMilliseconds);
        }
    }

    private Cell Restyle(Cell cell, int row)
    {
        var role = _notifications[row].Severity switch
        {
            NotificationSeverity.Success => PresentationTextRole.Success,
            NotificationSeverity.Warning => PresentationTextRole.Warning,
            NotificationSeverity.Error => PresentationTextRole.Error,
            _ => PresentationTextRole.Status,
        };
        var style = _style(role);
        return cell.IsContinuation ? Cell.Continuation(style) : Cell.Glyph(cell.Grapheme, style, cell.Width);
    }
}
