namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Interaction.Markdown;
using Threadsmith.Interaction.Presentation;

/// <summary>Adapts shared Markdown layout to TUIKit display-cell measurements.</summary>
internal static class TuiMarkdownLayout
{
    /// <summary>Formats semantic Markdown using TUIKit's width model.</summary>
    internal static IReadOnlyList<PresentationTextSegment> Format(
        MarkdownDocument document,
        int width,
        string? source = null,
        Action<PresentationTextSegment, MarkdownCodeBlock, string, int>? frameRow = null)
    {
        var frames = new Dictionary<PresentationTextSegment, (MarkdownCodeBlock Block, string Prefix, int Part)>(ReferenceEqualityComparer.Instance);
        var segments = TerminalMarkdownLayout<TuiKitTextMetrics>.Format(
            document,
            width,
            (output, code, _, prefix) => AppendFence(output, code, width, prefix, (segment, block, indent, part) => frames.Add(segment, (block, indent, part))));
        if (source is null)
        {
            PublishFrames(segments);
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
                if (frames.TryGetValue(last, out var frame))
                {
                    frames[result[^1]] = frame;
                }

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

        PublishFrames(result);
        return result;

        void PublishFrames(IReadOnlyList<PresentationTextSegment> projected)
        {
            if (frameRow is null)
            {
                return;
            }

            foreach (var segment in projected)
            {
                if (frames.TryGetValue(segment, out var frame))
                {
                    frameRow(segment, frame.Block, frame.Prefix, frame.Part);
                }
            }
        }
    }

    private static void AppendFence(
        List<PresentationTextSegment> output,
        MarkdownCodeBlock code,
        int width,
        string prefix,
        Action<PresentationTextSegment, MarkdownCodeBlock, string, int>? frameRow)
    {
        var available = width - UnicodeWidth.GetWidth(prefix);
        if (available < 17)
        {
            // Compact plain-text source: no partial frame or invisible action target.
            output.Add(new($"{prefix}```{code.Language ?? "text"}\n{code.Code}\n{prefix}```\n", PresentationTextRole.MarkdownCode));
            return;
        }

        AddRow(" ", 0);
        var body = TranscriptView.Safe(code.Code);
        var lines = body.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var wrapped = false;
            do
            {
                var length = TuiKitTextMetrics.GetLengthThatFits(line, available - 4);
                var fragment = line[..length];
                AddRow("  " + fragment, wrapped ? 3 : 1);
                line = line[length..];
                wrapped = true;
            }
            while (line.Length > 0);
        }

        AddRow(" ", 2);

        void AddRow(string text, int part)
        {
            var segment = new PresentationTextSegment(prefix + text + "\n", PresentationTextRole.MarkdownCode);
            output.Add(segment);
            frameRow?.Invoke(segment, code, prefix, part);
        }
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
