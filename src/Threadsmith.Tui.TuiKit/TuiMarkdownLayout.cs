namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Interaction.Markdown;
using Threadsmith.Interaction.Presentation;

/// <summary>Adapts shared Markdown layout to TUIKit display-cell measurements.</summary>
internal static class TuiMarkdownLayout
{
    /// <summary>Formats semantic Markdown using TUIKit's width model.</summary>
    internal static IReadOnlyList<PresentationTextSegment> Format(MarkdownDocument document, int width)
    {
        return TerminalMarkdownLayout<TuiKitTextMetrics>.Format(document, width);
    }
}

/// <summary>Supplies TUIKit's Unicode display-cell measurements without leaking its types.</summary>
internal readonly struct TuiKitTextMetrics : IDisplayTextMetrics<TuiKitTextMetrics>
{
    /// <inheritdoc />
    public static int GetWidth(string text) => UnicodeWidth.GetWidth(text);

    /// <inheritdoc />
    public static int GetLengthThatFits(string text, int width) => UnicodeWidth.GetLengthThatFits(text, width);
}
