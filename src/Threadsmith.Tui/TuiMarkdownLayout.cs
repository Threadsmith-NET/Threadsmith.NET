namespace Threadsmith.Tui;

using PrettyPrompt.Rendering;
using Threadsmith.Interaction.Markdown;
using Threadsmith.Interaction.Presentation;

/// <summary>Adapts shared Markdown layout to PrettyPrompt display-cell measurements.</summary>
internal static class TuiMarkdownLayout
{
    /// <summary>Formats semantic Markdown using the original frontend's width model.</summary>
    internal static IReadOnlyList<PresentationTextSegment> Format(MarkdownDocument document, int width)
    {
        return TerminalMarkdownLayout<PrettyPromptTextMetrics>.Format(document, width);
    }
}

/// <summary>Supplies PrettyPrompt's Unicode display-cell measurements without leaking its types.</summary>
internal readonly struct PrettyPromptTextMetrics : IDisplayTextMetrics<PrettyPromptTextMetrics>
{
    /// <inheritdoc />
    public static int GetWidth(string text) => UnicodeWidth.GetWidth(text.AsSpan());

    /// <inheritdoc />
    public static int GetLengthThatFits(string text, int width) => UnicodeWidth.GetLengthThatFits(text.AsSpan(), width);
}
