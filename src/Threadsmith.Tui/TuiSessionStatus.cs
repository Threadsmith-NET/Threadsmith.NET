namespace Threadsmith.Tui;

using Threadsmith.Interaction.Sessions;

/// <summary>Adapts shared status formatting to PrettyPrompt display-cell measurements.</summary>
internal static class TuiSessionStatusFormatter
{
    /// <summary>Formats one bounded status row using the original frontend's width model.</summary>
    internal static string Format(SessionStatusSnapshot status, int availableWidth, string separator)
    {
        return TerminalSessionStatusFormatter<PrettyPromptTextMetrics>.Format(status, availableWidth, separator);
    }
}
