namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Interaction.Presentation;
using TUIKit.Content;

/// <summary>A host-owned style range in unchanged display source.</summary>
internal readonly record struct SourceHighlightSpan(int Start, int Length, PresentationTextRole Role);

/// <summary>Highlights source without owning its text or copy behavior.</summary>
internal interface ISourceHighlighter
{
    /// <summary>Returns ordered source offsets; an empty list means plain text.</summary>
    IReadOnlyList<SourceHighlightSpan> Highlight(string source, string language);
}

/// <summary>Adapts the pinned line-oriented TUIKit highlighter to host style ranges.</summary>
internal sealed class TuiKitSourceHighlighter : ISourceHighlighter
{
    /// <inheritdoc/>
    public IReadOnlyList<SourceHighlightSpan> Highlight(string source, string language)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(language);
        if (!string.Equals(language, "csharp", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        // TUIKit exposes colored runs, not token kinds. Match its own representative styles
        // rather than exposing library colors or depending on a copied keyword list.
        var keyword = SyntaxHighlighter.HighlightLine("class", "csharp").Spans[0].Style;
        var comment = SyntaxHighlighter.HighlightLine("// comment", "csharp").Spans[0].Style;
        var literal = SyntaxHighlighter.HighlightLine("\"text\"", "csharp").Spans[0].Style;
        var number = SyntaxHighlighter.HighlightLine("123", "csharp").Spans[0].Style;
        var ranges = new List<SourceHighlightSpan>();
        var offset = 0;
        while (offset < source.Length)
        {
            var newline = source.IndexOf('\n', offset);
            var length = (newline < 0 ? source.Length : newline) - offset;
            if (length == 0)
            {
                offset++;
                continue;
            }

            // The pinned API requires a string, but blank lines need no allocation or tokenization.
            var line = source.Substring(offset, length);
            var position = offset;
            foreach (var span in SyntaxHighlighter.HighlightLine(line, "csharp").Spans)
            {
                if (!source.AsSpan(position, span.Text.Length).SequenceEqual(span.Text))
                {
                    throw new InvalidOperationException("Highlighting changed source text.");
                }

                var role = span.Style == keyword ? PresentationTextRole.Status
                    : span.Style == comment ? PresentationTextRole.Muted
                    : span.Style == literal ? PresentationTextRole.Success
                    : span.Style == number ? PresentationTextRole.Warning
                    : PresentationTextRole.MarkdownCode;
                if (role != PresentationTextRole.MarkdownCode && span.Text.Length > 0)
                {
                    ranges.Add(new(position, span.Text.Length, role));
                }

                position += span.Text.Length;
            }

            if (position != offset + line.Length)
            {
                throw new InvalidOperationException("Highlighting omitted source text.");
            }

            offset += line.Length + 1;
        }

        return ranges;
    }
}
