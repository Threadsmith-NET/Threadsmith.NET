namespace Threadsmith.Interaction.Markdown;

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

/// <summary>Host-owned parser boundary for complete untrusted model-answer blocks.</summary>
internal interface IMarkdownParser
{
    /// <summary>Parses one complete answer or returns visibly escaped source on any failure.</summary>
    MarkdownParseResult Parse(string source);
}

/// <summary>Adapts Markdig to Threadsmith's closed, bounded TUI document model.</summary>
internal sealed class MarkdownParser : IMarkdownParser
{
    /// <summary>Stable closed syntax-profile schema used by focused fixtures.</summary>
    internal const int SyntaxProfileVersion = 1;

    /// <summary>Maximum UTF-8 source size accepted for semantic rendering.</summary>
    internal const int MaximumSourceBytes = 256 * 1024;

    /// <summary>Maximum semantic and parser-node count.</summary>
    internal const int MaximumNodes = 10_000;

    /// <summary>Maximum supported block or inline nesting depth.</summary>
    internal const int MaximumDepth = 32;

    /// <summary>Maximum item count for one list.</summary>
    internal const int MaximumListItems = 1_000;

    /// <summary>Maximum row count for one table.</summary>
    internal const int MaximumTableRows = 200;

    /// <summary>Maximum column count for one table.</summary>
    internal const int MaximumTableColumns = 20;

    /// <summary>Maximum visible characters for one table cell.</summary>
    internal const int MaximumCellCharacters = 4_096;

    /// <summary>Maximum visible characters for one code block.</summary>
    internal const int MaximumCodeCharacters = 128 * 1024;

    /// <summary>Maximum characters for one link target.</summary>
    internal const int MaximumLinkCharacters = 2_048;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .Build();

    /// <summary>Parses one complete answer or returns visibly escaped source on any failure.</summary>
    public MarkdownParseResult Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var safeSource = TerminalControlEncoder.Encode(source);
        if (Encoding.UTF8.GetByteCount(source) > MaximumSourceBytes)
        {
            return new MarkdownParseResult(null, safeSource, "markdown source exceeded the rendering limit");
        }

        try
        {
            var state = new ParseState();
            var markdown = Markdown.Parse(source, Pipeline);
            var blocks = ParseBlocks(markdown, state, 0);
            var document = new MarkdownDocument(blocks);
            MarkdownValidator.Validate(document);
            return new MarkdownParseResult(document, safeSource, null);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or OverflowException)
        {
            return new MarkdownParseResult(null, safeSource, "markdown could not be rendered safely");
        }
    }

    private static ImmutableArray<MarkdownBlock> ParseBlocks(ContainerBlock container, ParseState state, int depth)
    {
        state.CheckDepth(depth);
        var blocks = ImmutableArray.CreateBuilder<MarkdownBlock>();
        foreach (var block in container)
        {
            state.AddNode();
            switch (block)
            {
                case ParagraphBlock paragraph:
                    blocks.Add(new MarkdownParagraph(ParseInlines(paragraph.Inline, state, depth + 1)));
                    break;
                case HeadingBlock heading:
                    blocks.Add(new MarkdownHeading(
                        Math.Clamp(heading.Level, 1, 6),
                        ParseInlines(heading.Inline, state, depth + 1)));
                    break;
                case QuoteBlock quote:
                    blocks.Add(new MarkdownQuote(ParseBlocks(quote, state, depth + 1)));
                    break;
                case ListBlock list:
                    blocks.Add(ParseList(list, state, depth + 1));
                    break;
                case CodeBlock code:
                    var codeText = code.Lines.ToString() ?? string.Empty;
                    if (codeText.Length > MaximumCodeCharacters)
                    {
                        throw new InvalidOperationException("Code block exceeded its limit.");
                    }

                    var language = code is FencedCodeBlock fenced
                        ? BoundLanguage(fenced.Info)
                        : null;
                    blocks.Add(new MarkdownCodeBlock(TerminalControlEncoder.Encode(codeText), language));
                    break;
                case Table table:
                    blocks.Add(ParseTable(table, state, depth + 1));
                    break;
                case ThematicBreakBlock:
                    blocks.Add(new MarkdownThematicBreak());
                    break;
                case LinkReferenceDefinitionGroup:
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported markdown block '{block.GetType().Name}'.");
            }
        }

        return blocks.ToImmutable();
    }

    private static MarkdownList ParseList(ListBlock list, ParseState state, int depth)
    {
        state.CheckDepth(depth);
        if (list.Count > MaximumListItems)
        {
            throw new InvalidOperationException("List exceeded its item limit.");
        }

        var items = ImmutableArray.CreateBuilder<MarkdownListItem>(list.Count);
        foreach (var child in list)
        {
            state.AddNode();
            if (child is not ListItemBlock item)
            {
                throw new InvalidOperationException("List contained an unexpected block.");
            }

            bool? isChecked = null;
            if (item.FirstOrDefault() is ParagraphBlock paragraph
                && paragraph.Inline?.FirstChild is TaskList task)
            {
                isChecked = task.Checked;
            }

            items.Add(new MarkdownListItem(isChecked, ParseBlocks(item, state, depth + 1)));
        }

        var start = int.TryParse(list.OrderedStart, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 1;
        return new MarkdownList(list.IsOrdered, start, items.ToImmutable());
    }

    private static MarkdownTable ParseTable(Table table, ParseState state, int depth)
    {
        state.CheckDepth(depth);
        if (table.Count > MaximumTableRows)
        {
            throw new InvalidOperationException("Table exceeded its row limit.");
        }

        var rows = ImmutableArray.CreateBuilder<MarkdownTableRow>(table.Count);
        foreach (var child in table)
        {
            state.AddNode();
            if (child is not TableRow row || row.Count > MaximumTableColumns)
            {
                throw new InvalidOperationException("Table structure exceeded its limits.");
            }

            var cells = ImmutableArray.CreateBuilder<ImmutableArray<MarkdownSpan>>(row.Count);
            foreach (var cellBlock in row)
            {
                state.AddNode();
                if (cellBlock is not TableCell cell)
                {
                    throw new InvalidOperationException("Table contained an unexpected cell.");
                }

                var spans = ParseCell(cell, state, depth + 1);
                if (spans.Sum(span => span.Text.Length) > MaximumCellCharacters)
                {
                    throw new InvalidOperationException("Table cell exceeded its character limit.");
                }

                cells.Add(spans);
            }

            rows.Add(new MarkdownTableRow(row.IsHeader, cells.ToImmutable()));
        }

        return new MarkdownTable(rows.ToImmutable());
    }

    private static ImmutableArray<MarkdownSpan> ParseCell(TableCell cell, ParseState state, int depth)
    {
        state.CheckDepth(depth);
        var spans = ImmutableArray.CreateBuilder<MarkdownSpan>();
        foreach (var block in cell)
        {
            if (block is not ParagraphBlock paragraph)
            {
                throw new InvalidOperationException("Only inline table-cell content is supported.");
            }

            if (spans.Count > 0)
            {
                AddSpan(spans, state, new MarkdownSpan(" "));
            }

            spans.AddRange(ParseInlines(paragraph.Inline, state, depth + 1));
        }

        return spans.ToImmutable();
    }

    private static ImmutableArray<MarkdownSpan> ParseInlines(
        ContainerInline? container,
        ParseState state,
        int depth)
    {
        state.CheckDepth(depth);
        var spans = ImmutableArray.CreateBuilder<MarkdownSpan>();
        if (container is not null)
        {
            ParseInlineChildren(container, state, depth, MarkdownSpanStyle.None, null, spans);
        }

        return spans.ToImmutable();
    }

    private static void ParseInlineChildren(
        ContainerInline container,
        ParseState state,
        int depth,
        MarkdownSpanStyle inheritedStyle,
        Uri? inheritedLink,
        ImmutableArray<MarkdownSpan>.Builder spans)
    {
        state.CheckDepth(depth);
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            state.AddNode();
            switch (inline)
            {
                case LiteralInline literal:
                    AddSpan(spans, state, new MarkdownSpan(
                        TerminalControlEncoder.Encode(literal.Content.ToString()),
                        inheritedStyle,
                        inheritedLink));
                    break;
                case CodeInline code:
                    AddSpan(spans, state, new MarkdownSpan(
                        TerminalControlEncoder.Encode(code.Content),
                        inheritedStyle | MarkdownSpanStyle.Code,
                        inheritedLink));
                    break;
                case LineBreakInline lineBreak:
                    AddSpan(spans, state, new MarkdownSpan(
                        lineBreak.IsHard ? "\n" : " ",
                        inheritedStyle,
                        inheritedLink,
                        lineBreak.IsHard));
                    break;
                case HtmlEntityInline entity:
                    AddSpan(spans, state, new MarkdownSpan(
                        TerminalControlEncoder.Encode(entity.Transcoded.ToString()),
                        inheritedStyle,
                        inheritedLink));
                    break;
                case TaskList:
                    break;
                case EmphasisInline emphasis:
                    var emphasisStyle = emphasis.DelimiterChar == '~'
                        ? MarkdownSpanStyle.Strikethrough
                        : emphasis.DelimiterCount >= 2
                            ? MarkdownSpanStyle.Strong
                            : MarkdownSpanStyle.Emphasis;
                    ParseInlineChildren(
                        emphasis,
                        state,
                        depth + 1,
                        inheritedStyle | emphasisStyle,
                        inheritedLink,
                        spans);
                    break;
                case LinkInline link:
                    ParseLink(link, state, depth + 1, inheritedStyle, spans);
                    break;
                case ContainerInline nested:
                    ParseInlineChildren(nested, state, depth + 1, inheritedStyle, inheritedLink, spans);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported markdown inline '{inline.GetType().Name}'.");
            }
        }
    }

    private static void ParseLink(
        LinkInline link,
        ParseState state,
        int depth,
        MarkdownSpanStyle inheritedStyle,
        ImmutableArray<MarkdownSpan>.Builder spans)
    {
        var rawTarget = link.GetDynamicUrl?.Invoke() ?? link.Url;
        var target = TryCreateSafeLink(rawTarget);
        if (link.IsImage)
        {
            AddSpan(spans, state, new MarkdownSpan("[image: ", inheritedStyle));
        }

        ParseInlineChildren(link, state, depth, inheritedStyle, target, spans);
        if (link.IsImage)
        {
            AddSpan(spans, state, new MarkdownSpan("]", inheritedStyle));
        }

        if (target is null && !string.IsNullOrWhiteSpace(rawTarget))
        {
            AddSpan(spans, state, new MarkdownSpan(
                $" ({TerminalControlEncoder.Encode(rawTarget)})",
                inheritedStyle));
        }
    }

    private static Uri? TryCreateSafeLink(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > MaximumLinkCharacters
            || value.Any(char.IsControl)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme is not "http" and not "https"
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || Uri.UnescapeDataString(uri.OriginalString).Any(char.IsControl))
        {
            return null;
        }

        return uri;
    }

    private static string? BoundLanguage(string? language)
    {
        var trimmed = language?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? null
            : trimmed.Length <= 64 && trimmed.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '+' or '#')
                ? trimmed
                : null;
    }

    private static void AddSpan(
        ImmutableArray<MarkdownSpan>.Builder spans,
        ParseState state,
        MarkdownSpan span)
    {
        state.AddNode();
        spans.Add(span);
    }

    private sealed class ParseState
    {
        private int _nodeCount;

        internal void AddNode()
        {
            _nodeCount++;
            if (_nodeCount > MaximumNodes)
            {
                throw new InvalidOperationException("Markdown exceeded its semantic node limit.");
            }
        }

        internal void CheckDepth(int depth)
        {
            if (_nodeCount > MaximumNodes || depth > MaximumDepth)
            {
                throw new InvalidOperationException("Markdown exceeded its nesting limit.");
            }
        }
    }
}

/// <summary>Neutralizes terminal control characters while preserving ordinary Unicode text.</summary>
internal static class TerminalControlEncoder
{
    /// <summary>Returns text that can be passed to a console renderer without active terminal controls.</summary>
    internal static string Encode(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length;)
        {
            if (!Rune.TryGetRuneAt(value, index, out var rune))
            {
                AppendEscape(result, value[index]);
                index++;
                continue;
            }

            index += rune.Utf16SequenceLength;
            var scalar = rune.Value;
            if (scalar is < 0x20 and not '\n' and not '\t' or >= 0x7F and <= 0x9F)
            {
                AppendEscape(result, scalar);
            }
            else
            {
                result.Append(rune.ToString());
            }
        }

        return result.ToString();
    }

    private static void AppendEscape(StringBuilder result, int scalar)
    {
        result.Append(scalar <= ushort.MaxValue ? "\\u" : "\\U");
        result.Append(scalar.ToString(
            scalar <= ushort.MaxValue ? "X4" : "X8",
            CultureInfo.InvariantCulture));
    }
}
