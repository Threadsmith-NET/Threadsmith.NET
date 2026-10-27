namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Interaction.Markdown;
using Threadsmith.Interaction.Presentation;

/// <summary>Adapts shared Markdown layout to TUIKit display-cell measurements.</summary>
internal static class TuiMarkdownLayout
{
    /// <summary>Formats semantic Markdown using TUIKit's width model.</summary>
    internal static IReadOnlyList<PresentationTextSegment> Format(MarkdownDocument document, int width, string? source = null)
    {
        var segments = TerminalMarkdownLayout<TuiKitTextMetrics>.Format(document, width);
        if (source is null)
        {
            return segments;
        }

        // Transcript separators are calculated from source line endings. Markdown layout
        // must retain that boundary rather than replace it with its own final newline.
        var result = segments.ToList();
        while (result.Count > 0)
        {
            var last = result[^1];
            var trimmed = last.Text.TrimEnd('\r', '\n');
            if (trimmed.Length > 0)
            {
                result[^1] = last with { Text = trimmed };
                break;
            }

            result.RemoveAt(result.Count - 1);
        }

        var endingStart = source.Length;
        while (endingStart > 0 && source[endingStart - 1] is '\r' or '\n')
        {
            endingStart--;
        }

        if (endingStart < source.Length)
        {
            result.Add(new PresentationTextSegment(source[endingStart..], PresentationTextRole.Default));
        }

        return result;
    }
}

/// <summary>Supplies TUIKit's Unicode display-cell measurements without leaking its types.</summary>
internal readonly struct TuiKitTextMetrics : IDisplayTextMetrics<TuiKitTextMetrics>
{
    /// <inheritdoc />
    public static int GetWidth(string text)
    {
        return UnicodeWidth.GetWidth(text);
    }

    /// <inheritdoc />
    public static int GetLengthThatFits(string text, int width)
    {
        return UnicodeWidth.GetLengthThatFits(text, width);
    }
}
