namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Interaction.Sessions;

/// <summary>Adapts shared status formatting to TUIKit display-cell measurements.</summary>
internal static class TuiSessionStatusFormatter
{
    /// <summary>Formats one bounded status row using TUIKit's width model.</summary>
    internal static string Format(SessionStatusSnapshot status, int availableWidth, string separator)
    {
        return TerminalSessionStatusFormatter<TuiKitTextMetrics>.Format(status, availableWidth, separator);
    }
}
