namespace Threadsmith.Tui.TuiKit;

using System.Text;
using Threadsmith.Interaction.Presentation;

/// <summary>Owns one bounded safe display and its mapping to the unchanged source.</summary>
internal sealed class SourceDocument
{
    /// <summary>Maximum preview characters, matching the transient presentation ceiling.</summary>
    internal const int PreviewCharacterLimit = 8192;

    /// <summary>Maximum logical preview lines.</summary>
    internal const int PreviewLineLimit = 20;

    private readonly int[] _sourceOffsets;

    /// <summary>Initializes a new instance of the <see cref="SourceDocument"/> class, with cached safe highlighting.</summary>
    internal SourceDocument(string source, string language, ISourceHighlighter highlighter)
    {
        RawSource = source;
        var display = new StringBuilder(source.Length);
        var offsets = new List<int>(source.Length + 1) { 0 };
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            if (character == '\r' && index + 1 < source.Length && source[index + 1] == '\n')
            {
                index++;
                character = '\n';
            }

            if (IsLineEnding(character))
            {
                display.Append('\n');
                offsets.Add(index + 1);
            }
            else if (character == '\t')
            {
                display.Append("    ");
                offsets.Add(index);
                offsets.Add(index);
                offsets.Add(index);
                offsets.Add(index + 1);
            }
            else
            {
                display.Append(char.IsControl(character) ? '?' : character);
                offsets.Add(index + 1);
            }
        }

        DisplaySource = display.ToString();
        _sourceOffsets = [.. offsets];
        try
        {
            var ranges = highlighter.Highlight(DisplaySource, language);
            var segments = new List<SourceHighlightSpan>();
            var position = 0;
            foreach (var range in ranges)
            {
                if (range.Start < position || range.Length < 0 || range.Start > DisplaySource.Length - range.Length)
                {
                    throw new InvalidOperationException("Invalid source highlighting range.");
                }

                if (range.Start > position)
                {
                    AddRange(segments, position, range.Start - position, PresentationTextRole.MarkdownCode);
                }

                AddRange(segments, range.Start, range.Length, range.Role);
                position = range.Start + range.Length;
            }

            AddRange(segments, position, DisplaySource.Length - position, PresentationTextRole.MarkdownCode);
            Ranges = segments;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // This boundary owns readable fallback. Never include untrusted source in a log.
            Ranges = [new(0, DisplaySource.Length, PresentationTextRole.MarkdownCode)];
        }
    }

    /// <summary>Gets the original copy authority.</summary>
    internal string RawSource { get; }

    /// <summary>Gets terminal-safe text, separate from the copy authority.</summary>
    internal string DisplaySource { get; }

    /// <summary>Gets cached host-owned offsets and styles without source substrings.</summary>
    internal IReadOnlyList<SourceHighlightSpan> Ranges { get; }

    /// <summary>Returns a source prefix bounded before sanitizing and highlighting.</summary>
    internal static (string Text, bool Truncated) Preview(string source)
    {
        var end = Math.Min(source.Length, PreviewCharacterLimit);
        var lines = 1;
        for (var index = 0; index < end; index++)
        {
            if (!IsLineEnding(source[index]))
            {
                continue;
            }

            if (lines == PreviewLineLimit)
            {
                end = index;
                break;
            }

            lines++;
            if (source[index] == '\r' && index + 1 < end && source[index + 1] == '\n')
            {
                index++;
            }
        }

        if (end > 0 && end < source.Length && char.IsHighSurrogate(source[end - 1]) && char.IsLowSurrogate(source[end]))
        {
            end--;
        }

        return (source[..end], end < source.Length);
    }

    /// <summary>Copies an original range selected in normalized display coordinates.</summary>
    internal string CopyRange(int first, int last)
    {
        var start = _sourceOffsets[Math.Clamp(first, 0, DisplaySource.Length)];
        var end = _sourceOffsets[Math.Clamp(last, 0, DisplaySource.Length)];
        return RawSource[start..Math.Max(start, end)];
    }

    private static void AddRange(List<SourceHighlightSpan> ranges, int start, int length, PresentationTextRole role)
    {
        if (length == 0)
        {
            return;
        }

        if (ranges.Count > 0 && ranges[^1].Start + ranges[^1].Length == start && ranges[^1].Role == role)
        {
            ranges[^1] = ranges[^1] with { Length = ranges[^1].Length + length };
        }
        else
        {
            ranges.Add(new(start, length, role));
        }
    }

    private static bool IsLineEnding(char character)
    {
        return character is '\r' or '\n' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029';
    }
}
