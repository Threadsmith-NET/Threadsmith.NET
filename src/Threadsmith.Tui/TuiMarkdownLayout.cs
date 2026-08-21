namespace Threadsmith.Tui;

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using PrettyPrompt.Rendering;

/// <summary>Deterministic terminal-neutral layout for semantic markdown documents.</summary>
internal static class TuiMarkdownLayout
{
    /// <summary>Formats a document into themed semantic segments for the active terminal width.</summary>
    internal static IReadOnlyList<TuiTextSegment> Format(TuiMarkdownDocument document, int width)
    {
        ArgumentNullException.ThrowIfNull(document);
        TuiMarkdownValidator.Validate(document);
        var segments = new List<TuiTextSegment>();
        var safeWidth = Math.Max(20, width);
        for (var index = 0; index < document.Blocks.Length; index++)
        {
            if (index > 0)
            {
                segments.Add(new TuiTextSegment("\n", TuiTextRole.Default));
            }

            AppendBlock(segments, document.Blocks[index], safeWidth, string.Empty);
        }

        if (segments.Count > 0 && !segments[^1].Text.EndsWith('\n'))
        {
            segments.Add(new TuiTextSegment("\n", TuiTextRole.Default));
        }

        return segments;
    }

    private static void AppendBlock(
        List<TuiTextSegment> segments,
        TuiMarkdownBlock block,
        int width,
        string prefix)
    {
        switch (block)
        {
            case TuiMarkdownParagraph paragraph:
                AppendWrappedSpans(
                    segments,
                    paragraph.Spans,
                    width,
                    prefix,
                    prefix,
                    TuiTextRole.MarkdownQuote);
                break;
            case TuiMarkdownHeading heading:
                AppendWrappedSpans(
                    segments,
                    heading.Spans,
                    width,
                    prefix,
                    prefix,
                    TuiTextRole.MarkdownHeading,
                    TuiTextRole.MarkdownHeading);
                AppendHeadingUnderline(segments, heading, width, prefix);
                break;
            case TuiMarkdownQuote quote:
                foreach (var child in quote.Blocks)
                {
                    AppendBlock(segments, child, width, prefix + "> ");
                }

                break;
            case TuiMarkdownList list:
                AppendList(segments, list, width, prefix);
                break;
            case TuiMarkdownCodeBlock code:
                var language = code.Language is null ? string.Empty : code.Language;
                segments.Add(new TuiTextSegment($"{prefix}```{language}\n", TuiTextRole.MarkdownCode));
                foreach (var line in code.Code.Split('\n'))
                {
                    segments.Add(new TuiTextSegment($"{prefix}{line}\n", TuiTextRole.MarkdownCode));
                }

                segments.Add(new TuiTextSegment($"{prefix}```\n", TuiTextRole.MarkdownCode));
                break;
            case TuiMarkdownTable table:
                AppendTable(segments, table, width, prefix);
                break;
            case TuiMarkdownThematicBreak:
                segments.Add(new TuiTextSegment($"{prefix}---\n", TuiTextRole.MarkdownTableBorder));
                break;
            default:
                throw new InvalidOperationException($"Unsupported semantic markdown block '{block.GetType().Name}'.");
        }
    }

    private static void AppendHeadingUnderline(
        List<TuiTextSegment> segments,
        TuiMarkdownHeading heading,
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
        segments.Add(new TuiTextSegment(
            boundedPrefix + new string(underline, underlineWidth) + "\n",
            TuiTextRole.MarkdownHeading));
    }

    private static void AppendList(
        List<TuiTextSegment> segments,
        TuiMarkdownList list,
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
                segments.Add(new TuiTextSegment(itemPrefix + "\n", TuiTextRole.MarkdownListMarker));
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
        List<TuiTextSegment> segments,
        TuiMarkdownBlock block,
        int width,
        string prefix,
        string continuationPrefix)
    {
        if (block is TuiMarkdownParagraph paragraph)
        {
            AppendWrappedSpans(
                segments,
                paragraph.Spans,
                width,
                prefix,
                continuationPrefix,
                TuiTextRole.MarkdownListMarker);
            return;
        }

        AppendBlock(segments, block, width, prefix);
    }

    private static void AppendTable(
        List<TuiTextSegment> segments,
        TuiMarkdownTable table,
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
                segments.Add(new TuiTextSegment(prefix + "| ", TuiTextRole.MarkdownTableBorder));
                for (var column = 0; column < columns; column++)
                {
                    segments.Add(new TuiTextSegment(
                        Pad(textRows[rowIndex][column], columnWidths[column]),
                        table.Rows[rowIndex].IsHeader ? TuiTextRole.MarkdownStrong : TuiTextRole.Default));
                    segments.Add(new TuiTextSegment(" | ", TuiTextRole.MarkdownTableBorder));
                }

                segments.Add(new TuiTextSegment("\n", TuiTextRole.Default));
                if (table.Rows[rowIndex].IsHeader)
                {
                    var separator = prefix + "|" + string.Join("|", columnWidths.Select(value => new string('-', value + 2))) + "|\n";
                    segments.Add(new TuiTextSegment(separator, TuiTextRole.MarkdownTableBorder));
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
                    [new TuiMarkdownSpan(headers[column])],
                    width,
                    valuePrefix,
                    continuationPrefix,
                    TuiTextRole.MarkdownTableBorder);
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
                    [new TuiMarkdownSpan(textRows[rowIndex][column])],
                    width,
                    valuePrefix,
                    continuationPrefix,
                    TuiTextRole.MarkdownTableBorder);
            }

            if (rowIndex + 1 < textRows.Length)
            {
                segments.Add(new TuiTextSegment("\n", TuiTextRole.Default));
            }
        }
    }

    private static void AppendWrappedSpans(
        List<TuiTextSegment> output,
        ImmutableArray<TuiMarkdownSpan> spans,
        int width,
        string firstPrefix,
        string continuationPrefix,
        TuiTextRole prefixRole,
        TuiTextRole? forcedRole = null)
    {
        firstPrefix = BoundPrefix(firstPrefix, width);
        continuationPrefix = BoundPrefix(continuationPrefix, width);
        var inlineSegments = CreateInlineSegments(spans, forcedRole);
        output.Add(new TuiTextSegment(firstPrefix, prefixRole));
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
                    output.Add(new TuiTextSegment("\n" + continuationPrefix, prefixRole));
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
                    output.Add(new TuiTextSegment("\n" + continuationPrefix, prefixRole));
                    column = continuationWidth;
                    pendingWhitespace = string.Empty;
                }
                else if (pendingWhitespace.Length > 0 && column > continuationWidth)
                {
                    output.Add(new TuiTextSegment(pendingWhitespace, TuiTextRole.Default));
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
                        output.Add(new TuiTextSegment("\n" + continuationPrefix, prefixRole));
                        column = continuationWidth;
                        continue;
                    }

                    var fitted = token[..length];
                    output.Add(new TuiTextSegment(fitted, segment.Role, segment.LinkTarget));
                    token = token[length..];
                    column += GetWidth(fitted);
                    if (token.Length > 0)
                    {
                        output.Add(new TuiTextSegment("\n" + continuationPrefix, prefixRole));
                        column = continuationWidth;
                    }
                }

                if (token.Length > 0)
                {
                    output.Add(new TuiTextSegment(token, segment.Role, segment.LinkTarget));
                    column += GetWidth(token);
                }

                index = end;
            }
        }

        output.Add(new TuiTextSegment("\n", TuiTextRole.Default));
    }

    private static IReadOnlyList<TuiTextSegment> CreateInlineSegments(
        ImmutableArray<TuiMarkdownSpan> spans,
        TuiTextRole? forcedRole)
    {
        var segments = new List<TuiTextSegment>();
        Uri? previousLink = null;
        foreach (var span in spans)
        {
            if (previousLink is not null && span.LinkTarget != previousLink)
            {
                AppendLinkDestination(segments, previousLink);
            }

            var role = forcedRole ?? GetRole(span);
            segments.Add(new TuiTextSegment(span.Text, role, span.LinkTarget));
            previousLink = span.LinkTarget;
        }

        if (previousLink is not null)
        {
            AppendLinkDestination(segments, previousLink);
        }

        return segments;
    }

    private static void AppendLinkDestination(List<TuiTextSegment> segments, Uri link)
    {
        segments.Add(new TuiTextSegment($" ({link.AbsoluteUri})", TuiTextRole.Hyperlink, link));
    }

    private static TuiTextRole GetRole(TuiMarkdownSpan span)
    {
        return span.LinkTarget is not null
                ? TuiTextRole.Hyperlink
                : span.Style.HasFlag(TuiMarkdownSpanStyle.Code)
                    ? TuiTextRole.MarkdownCode
                    : span.Style.HasFlag(TuiMarkdownSpanStyle.Strong)
                        ? TuiTextRole.MarkdownStrong
                        : span.Style.HasFlag(TuiMarkdownSpanStyle.Strikethrough)
                            ? TuiTextRole.MarkdownStrikethrough
                            : span.Style.HasFlag(TuiMarkdownSpanStyle.Emphasis)
                                ? TuiTextRole.MarkdownEmphasis
                                : TuiTextRole.Default;
    }

    private static string Flatten(ImmutableArray<TuiMarkdownSpan> spans)
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

/// <summary>One serialized output operation accepted by a console surface.</summary>
internal abstract record TuiOutputItem;

/// <summary>Already projected semantic text segments.</summary>
internal sealed record TuiSegmentOutput(IReadOnlyList<TuiTextSegment> Segments) : TuiOutputItem;

/// <summary>Exact model source paired with its terminal-safe presentation copy.</summary>
internal sealed record TuiSourceOutput(
    string RawSource,
    string SafeSource,
    bool StartsAnswerBlock) : TuiOutputItem;

/// <summary>Validated semantic markdown retaining exact and terminal-safe source representations.</summary>
internal sealed record TuiMarkdownOutput(
    TuiMarkdownDocument Document,
    string RawSource,
    string SafeSource,
    bool StartsAnswerBlock) : TuiOutputItem;

/// <summary>Capability-gated exact source admitted only by a redirected console surface.</summary>
internal sealed record TuiRawSourceOutput(string RawSource) : TuiOutputItem;

/// <summary>Collects one answer until an ordered visible-state boundary.</summary>
internal sealed class TuiModelAnswerCollector
{
    private readonly bool _renderMarkdown;
    private readonly ITuiMarkdownParser _parser;
    private readonly StringBuilder _source = new();
    private long _sourceBytes;
    private bool _answerVisible;
    private bool _sourceStreaming;

    /// <summary>Initializes a new instance of the <see cref="TuiModelAnswerCollector"/> class.</summary>
    internal TuiModelAnswerCollector(bool renderMarkdown, ITuiMarkdownParser? parser = null)
    {
        _renderMarkdown = renderMarkdown;
        _parser = parser ?? new TuiMarkdownParser();
    }

    /// <summary>Appends a model delta and returns immediate safe-source output only in source mode.</summary>
    internal TuiOutputItem? Append(string delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.Length == 0)
        {
            return null;
        }

        if (!_renderMarkdown || _sourceStreaming)
        {
            var startsSourceBlock = !_answerVisible;
            _answerVisible = true;
            return CreateSourceOutput(delta, startsSourceBlock);
        }

        _source.Append(delta);
        _sourceBytes += Encoding.UTF8.GetByteCount(delta);
        if (_sourceBytes <= TuiMarkdownParser.MaximumSourceBytes)
        {
            return null;
        }

        _sourceStreaming = true;
        var source = _source.ToString();
        _source.Clear();
        _sourceBytes = 0;
        var startsAnswerBlock = !_answerVisible;
        _answerVisible = true;
        return CreateSourceOutput(source, startsAnswerBlock);
    }

    /// <summary>Closes the current answer before the next ordered boundary.</summary>
    internal TuiOutputItem? Flush(CancellationToken cancellationToken = default)
    {
        if (_sourceStreaming)
        {
            _answerVisible = false;
            _sourceStreaming = false;
            return null;
        }

        if (_source.Length == 0)
        {
            _answerVisible = false;
            return null;
        }

        var source = _source.ToString();
        _answerVisible = false;
        _source.Clear();
        _sourceBytes = 0;
        if (!_renderMarkdown || cancellationToken.IsCancellationRequested)
        {
            return CreateSourceOutput(source, startsAnswerBlock: true);
        }

        try
        {
            var parsed = _parser.Parse(source);
            if (parsed.Document is { } document)
            {
                TuiMarkdownValidator.Validate(document);
                return new TuiMarkdownOutput(
                    document,
                    source,
                    TerminalControlEncoder.Encode(source),
                    StartsAnswerBlock: true);
            }

            return new TuiSourceOutput(
                source,
                TerminalControlEncoder.Encode(parsed.SafeSource),
                StartsAnswerBlock: true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return CreateSourceOutput(source, startsAnswerBlock: true);
        }
    }

    private static TuiSourceOutput CreateSourceOutput(string source, bool startsAnswerBlock)
    {
        return new(source, TerminalControlEncoder.Encode(source), startsAnswerBlock);
    }
}
