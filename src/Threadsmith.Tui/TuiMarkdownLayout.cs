namespace Threadsmith.Tui;

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using PrettyPrompt.Rendering;
using Threadsmith.Interaction.Markdown;
using Threadsmith.Interaction.Presentation;

/// <summary>Deterministic terminal-neutral layout for semantic markdown documents.</summary>
internal static class TuiMarkdownLayout
{
    /// <summary>Formats a document into themed semantic segments for the active terminal width.</summary>
    internal static IReadOnlyList<PresentationTextSegment> Format(MarkdownDocument document, int width)
    {
        ArgumentNullException.ThrowIfNull(document);
        MarkdownValidator.Validate(document);
        var segments = new List<PresentationTextSegment>();
        var safeWidth = Math.Max(20, width);
        for (var index = 0; index < document.Blocks.Length; index++)
        {
            if (index > 0)
            {
                segments.Add(new PresentationTextSegment("\n", PresentationTextRole.Default));
            }

            AppendBlock(segments, document.Blocks[index], safeWidth, string.Empty);
        }

        if (segments.Count > 0 && !segments[^1].Text.EndsWith('\n'))
        {
            segments.Add(new PresentationTextSegment("\n", PresentationTextRole.Default));
        }

        return segments;
    }

    private static void AppendBlock(
        List<PresentationTextSegment> segments,
        MarkdownBlock block,
        int width,
        string prefix)
    {
        switch (block)
        {
            case MarkdownParagraph paragraph:
                AppendWrappedSpans(
                    segments,
                    paragraph.Spans,
                    width,
                    prefix,
                    prefix,
                    PresentationTextRole.MarkdownQuote);
                break;
            case MarkdownHeading heading:
                AppendWrappedSpans(
                    segments,
                    heading.Spans,
                    width,
                    prefix,
                    prefix,
                    PresentationTextRole.MarkdownHeading,
                    PresentationTextRole.MarkdownHeading);
                AppendHeadingUnderline(segments, heading, width, prefix);
                break;
            case MarkdownQuote quote:
                foreach (var child in quote.Blocks)
                {
                    AppendBlock(segments, child, width, prefix + "> ");
                }

                break;
            case MarkdownList list:
                AppendList(segments, list, width, prefix);
                break;
            case MarkdownCodeBlock code:
                var language = code.Language is null ? string.Empty : code.Language;
                segments.Add(new PresentationTextSegment($"{prefix}```{language}\n", PresentationTextRole.MarkdownCode));
                foreach (var line in code.Code.Split('\n'))
                {
                    segments.Add(new PresentationTextSegment($"{prefix}{line}\n", PresentationTextRole.MarkdownCode));
                }

                segments.Add(new PresentationTextSegment($"{prefix}```\n", PresentationTextRole.MarkdownCode));
                break;
            case MarkdownTable table:
                AppendTable(segments, table, width, prefix);
                break;
            case MarkdownThematicBreak:
                segments.Add(new PresentationTextSegment($"{prefix}---\n", PresentationTextRole.MarkdownTableBorder));
                break;
            default:
                throw new InvalidOperationException($"Unsupported semantic markdown block '{block.GetType().Name}'.");
        }
    }

    private static void AppendHeadingUnderline(
        List<PresentationTextSegment> segments,
        MarkdownHeading heading,
        int width,
        string prefix)
    {
        if (heading.Level > 2)
        {
            return;
        }

        var boundedPrefix = BoundPrefix(prefix, width);
        var availableWidth = Math.Max(1, width - GetWidth(boundedPrefix));
        var headingWidth = heading.Spans.Sum(span => GetWidth(span.Text));
        var underlineWidth = Math.Min(availableWidth, Math.Max(3, headingWidth));
        var underline = heading.Level == 1 ? '═' : '─';
        segments.Add(new PresentationTextSegment(
            boundedPrefix + new string(underline, underlineWidth) + "\n",
            PresentationTextRole.MarkdownHeading));
    }

    private static void AppendList(
        List<PresentationTextSegment> segments,
        MarkdownList list,
        int width,
        string prefix)
    {
        for (var index = 0; index < list.Items.Length; index++)
        {
            var item = list.Items[index];
            var marker = list.IsOrdered
                ? ((long)list.Start + index).ToString(CultureInfo.InvariantCulture) + ". "
                : "- ";
            var task = item.IsChecked switch
            {
                true => "[x] ",
                false => "[ ] ",
                null => string.Empty,
            };
            var itemPrefix = prefix + marker + task;
            if (item.Blocks.Length == 0)
            {
                segments.Add(new PresentationTextSegment(itemPrefix + "\n", PresentationTextRole.MarkdownListMarker));
                continue;
            }

            var continuation = prefix + new string(' ', GetWidth(marker + task));
            AppendListFirstBlock(segments, item.Blocks[0], width, itemPrefix, continuation);
            for (var blockIndex = 1; blockIndex < item.Blocks.Length; blockIndex++)
            {
                AppendBlock(segments, item.Blocks[blockIndex], width, continuation);
            }
        }
    }

    private static void AppendListFirstBlock(
        List<PresentationTextSegment> segments,
        MarkdownBlock block,
        int width,
        string prefix,
        string continuationPrefix)
    {
        if (block is MarkdownParagraph paragraph)
        {
            AppendWrappedSpans(
                segments,
                paragraph.Spans,
                width,
                prefix,
                continuationPrefix,
                PresentationTextRole.MarkdownListMarker);
            return;
        }

        AppendBlock(segments, block, width, prefix);
    }

    private static void AppendTable(
        List<PresentationTextSegment> segments,
        MarkdownTable table,
        int width,
        string prefix)
    {
        if (table.Rows.Length == 0)
        {
            return;
        }

        var columns = table.Rows.Max(row => row.Cells.Length);
        string[][] textRows =
        [
            .. table.Rows.Select(row => row.Cells
                .Select(Flatten)
                .Concat(Enumerable.Repeat(string.Empty, columns - row.Cells.Length))
                .ToArray()),
        ];
        int[] columnWidths =
        [
            .. Enumerable.Range(0, columns)
                .Select(column => textRows.Max(row => GetWidth(row[column]))),
        ];
        var requiredWidth = GetWidth(prefix) + columnWidths.Sum() + (columns * 3) + 1;
        if (requiredWidth <= width)
        {
            for (var rowIndex = 0; rowIndex < textRows.Length; rowIndex++)
            {
                segments.Add(new PresentationTextSegment(prefix + "| ", PresentationTextRole.MarkdownTableBorder));
                for (var column = 0; column < columns; column++)
                {
                    segments.Add(new PresentationTextSegment(
                        Pad(textRows[rowIndex][column], columnWidths[column]),
                        table.Rows[rowIndex].IsHeader ? PresentationTextRole.MarkdownStrong : PresentationTextRole.Default));
                    segments.Add(new PresentationTextSegment(" | ", PresentationTextRole.MarkdownTableBorder));
                }

                segments.Add(new PresentationTextSegment("\n", PresentationTextRole.Default));
                if (table.Rows[rowIndex].IsHeader)
                {
                    var separator = prefix + "|" + string.Join("|", columnWidths.Select(value => new string('-', value + 2))) + "|\n";
                    segments.Add(new PresentationTextSegment(separator, PresentationTextRole.MarkdownTableBorder));
                }
            }

            return;
        }

        var headers = textRows[0];
        if (table.Rows.Length == 1 && table.Rows[0].IsHeader)
        {
            for (var column = 0; column < columns; column++)
            {
                var valuePrefix = $"{prefix}- Column {column + 1}: ";
                var continuationPrefix = prefix + new string(' ', GetWidth($"- Column {column + 1}: "));
                AppendWrappedSpans(
                    segments,
                    [new MarkdownSpan(headers[column])],
                    width,
                    valuePrefix,
                    continuationPrefix,
                    PresentationTextRole.MarkdownTableBorder);
            }

            return;
        }

        var firstDataRow = table.Rows[0].IsHeader ? 1 : 0;
        for (var rowIndex = firstDataRow; rowIndex < textRows.Length; rowIndex++)
        {
            for (var column = 0; column < columns; column++)
            {
                var label = table.Rows[0].IsHeader && !string.IsNullOrWhiteSpace(headers[column])
                    ? headers[column]
                    : $"Column {column + 1}";
                var valuePrefix = $"{prefix}- {label}: ";
                var continuationPrefix = prefix + new string(' ', GetWidth($"- {label}: "));
                AppendWrappedSpans(
                    segments,
                    [new MarkdownSpan(textRows[rowIndex][column])],
                    width,
                    valuePrefix,
                    continuationPrefix,
                    PresentationTextRole.MarkdownTableBorder);
            }

            if (rowIndex + 1 < textRows.Length)
            {
                segments.Add(new PresentationTextSegment("\n", PresentationTextRole.Default));
            }
        }
    }

    private static void AppendWrappedSpans(
        List<PresentationTextSegment> output,
        ImmutableArray<MarkdownSpan> spans,
        int width,
        string firstPrefix,
        string continuationPrefix,
        PresentationTextRole prefixRole,
        PresentationTextRole? forcedRole = null)
    {
        firstPrefix = BoundPrefix(firstPrefix, width);
        continuationPrefix = BoundPrefix(continuationPrefix, width);
        var inlineSegments = CreateInlineSegments(spans, forcedRole);
        output.Add(new PresentationTextSegment(firstPrefix, prefixRole));
        var column = GetWidth(firstPrefix);
        var pendingWhitespace = string.Empty;
        foreach (var segment in inlineSegments)
        {
            var index = 0;
            while (index < segment.Text.Length)
            {
                if (segment.Text[index] == '\n')
                {
                    pendingWhitespace = string.Empty;
                    output.Add(new PresentationTextSegment("\n" + continuationPrefix, prefixRole));
                    column = GetWidth(continuationPrefix);
                    index++;
                    continue;
                }

                var whitespace = char.IsWhiteSpace(segment.Text[index]);
                var end = index + 1;
                while (end < segment.Text.Length
                    && segment.Text[end] != '\n'
                    && char.IsWhiteSpace(segment.Text[end]) == whitespace)
                {
                    end++;
                }

                var token = segment.Text[index..end];
                if (whitespace)
                {
                    pendingWhitespace += token;
                    index = end;
                    continue;
                }

                var tokenWidth = GetWidth(token);
                var continuationWidth = GetWidth(continuationPrefix);
                var whitespaceWidth = GetWidth(pendingWhitespace);
                if (column > continuationWidth && column + whitespaceWidth + tokenWidth > width)
                {
                    output.Add(new PresentationTextSegment("\n" + continuationPrefix, prefixRole));
                    column = continuationWidth;
                    pendingWhitespace = string.Empty;
                }
                else if (pendingWhitespace.Length > 0 && column > continuationWidth)
                {
                    output.Add(new PresentationTextSegment(pendingWhitespace, PresentationTextRole.Default));
                    column += whitespaceWidth;
                    pendingWhitespace = string.Empty;
                }
                else
                {
                    pendingWhitespace = string.Empty;
                }

                while (token.Length > 0 && column + GetWidth(token) > width)
                {
                    var available = width - column;
                    var length = available > 0
                        ? UnicodeWidth.GetLengthThatFits(token.AsSpan(), available)
                        : 0;
                    if (length == 0)
                    {
                        output.Add(new PresentationTextSegment("\n" + continuationPrefix, prefixRole));
                        column = continuationWidth;
                        continue;
                    }

                    var fitted = token[..length];
                    output.Add(new PresentationTextSegment(fitted, segment.Role, segment.LinkTarget));
                    token = token[length..];
                    column += GetWidth(fitted);
                    if (token.Length > 0)
                    {
                        output.Add(new PresentationTextSegment("\n" + continuationPrefix, prefixRole));
                        column = continuationWidth;
                    }
                }

                if (token.Length > 0)
                {
                    output.Add(new PresentationTextSegment(token, segment.Role, segment.LinkTarget));
                    column += GetWidth(token);
                }

                index = end;
            }
        }

        output.Add(new PresentationTextSegment("\n", PresentationTextRole.Default));
    }

    private static IReadOnlyList<PresentationTextSegment> CreateInlineSegments(
        ImmutableArray<MarkdownSpan> spans,
        PresentationTextRole? forcedRole)
    {
        var segments = new List<PresentationTextSegment>();
        Uri? previousLink = null;
        foreach (var span in spans)
        {
            if (previousLink is not null && span.LinkTarget != previousLink)
            {
                AppendLinkDestination(segments, previousLink);
            }

            var role = forcedRole ?? GetRole(span);
            segments.Add(new PresentationTextSegment(span.Text, role, span.LinkTarget));
            previousLink = span.LinkTarget;
        }

        if (previousLink is not null)
        {
            AppendLinkDestination(segments, previousLink);
        }

        return segments;
    }

    private static void AppendLinkDestination(List<PresentationTextSegment> segments, Uri link)
    {
        segments.Add(new PresentationTextSegment($" ({link.AbsoluteUri})", PresentationTextRole.Hyperlink, link));
    }

    private static PresentationTextRole GetRole(MarkdownSpan span)
    {
        return span.LinkTarget is not null
                ? PresentationTextRole.Hyperlink
                : span.Style.HasFlag(MarkdownSpanStyle.Code)
                    ? PresentationTextRole.MarkdownCode
                    : span.Style.HasFlag(MarkdownSpanStyle.Strong)
                        ? PresentationTextRole.MarkdownStrong
                        : span.Style.HasFlag(MarkdownSpanStyle.Strikethrough)
                            ? PresentationTextRole.MarkdownStrikethrough
                            : span.Style.HasFlag(MarkdownSpanStyle.Emphasis)
                                ? PresentationTextRole.MarkdownEmphasis
                                : PresentationTextRole.Default;
    }

    private static string Flatten(ImmutableArray<MarkdownSpan> spans)
    {
        var builder = new StringBuilder();
        Uri? previousLink = null;
        foreach (var span in spans)
        {
            if (previousLink is not null && span.LinkTarget != previousLink)
            {
                builder.Append(" (").Append(previousLink.AbsoluteUri).Append(')');
            }

            builder.Append(span.Text.Replace('\n', ' '));
            previousLink = span.LinkTarget;
        }

        if (previousLink is not null)
        {
            builder.Append(" (").Append(previousLink.AbsoluteUri).Append(')');
        }

        return builder.ToString();
    }

    private static int GetWidth(string value)
    {
        return UnicodeWidth.GetWidth(value.AsSpan());
    }

    private static string BoundPrefix(string value, int width)
    {
        var maximumWidth = Math.Max(0, width - 2);
        var length = UnicodeWidth.GetLengthThatFits(value.AsSpan(), maximumWidth);
        return value[..length];
    }

    private static string Pad(string value, int width)
    {
        var padding = width - GetWidth(value);
        return padding > 0 ? value + new string(' ', padding) : value;
    }
}
