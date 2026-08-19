namespace Threadsmith.Tui;

/// <summary>Validates semantic Markdown values before they reach a terminal adapter.</summary>
internal static class TuiMarkdownValidator
{
    private const TuiMarkdownSpanStyle AllSpanStyles = TuiMarkdownSpanStyle.Emphasis
        | TuiMarkdownSpanStyle.Strong
        | TuiMarkdownSpanStyle.Strikethrough
        | TuiMarkdownSpanStyle.Code;

    /// <summary>Validates the complete closed document graph and all terminal-visible text.</summary>
    internal static void Validate(TuiMarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var state = new ValidationState();
        ValidateBlocks(document.Blocks, state, 0);
    }

    private static void ValidateBlocks(
        IReadOnlyList<TuiMarkdownBlock> blocks,
        ValidationState state,
        int depth)
    {
        state.CheckDepth(depth);
        foreach (var block in blocks)
        {
            ArgumentNullException.ThrowIfNull(block);
            state.AddNode();
            switch (block)
            {
                case TuiMarkdownParagraph paragraph:
                    ValidateSpans(paragraph.Spans, state);
                    break;
                case TuiMarkdownHeading heading when heading.Level is >= 1 and <= 6:
                    ValidateSpans(heading.Spans, state);
                    break;
                case TuiMarkdownHeading:
                    throw new ArgumentException("Markdown heading levels must be between one and six.", nameof(blocks));
                case TuiMarkdownQuote quote:
                    ValidateBlocks(quote.Blocks, state, depth + 1);
                    break;
                case TuiMarkdownList list:
                    ValidateList(list, state, depth + 1);
                    break;
                case TuiMarkdownCodeBlock code:
                    ValidateText(code.Code, TuiMarkdownParser.MaximumCodeCharacters, state);
                    if (code.Language is not null
                        && (code.Language.Length > 64
                            || code.Language.Any(character => !(char.IsAsciiLetterOrDigit(character)
                                || character is '-' or '_' or '+' or '#'))))
                    {
                        throw new ArgumentException("Markdown code language metadata is invalid.", nameof(blocks));
                    }

                    break;
                case TuiMarkdownTable table:
                    ValidateTable(table, state);
                    break;
                case TuiMarkdownThematicBreak:
                    break;
                default:
                    throw new ArgumentException(
                        $"Unsupported semantic Markdown block '{block.GetType().Name}'.",
                        nameof(blocks));
            }
        }
    }

    private static void ValidateList(TuiMarkdownList list, ValidationState state, int depth)
    {
        state.CheckDepth(depth);
        if (list.Start < 0 || list.Items.Length > TuiMarkdownParser.MaximumListItems)
        {
            throw new ArgumentException("Markdown list metadata exceeded its bounds.", nameof(list));
        }

        foreach (var item in list.Items)
        {
            ArgumentNullException.ThrowIfNull(item);
            state.AddNode();
            ValidateBlocks(item.Blocks, state, depth + 1);
        }
    }

    private static void ValidateTable(TuiMarkdownTable table, ValidationState state)
    {
        if (table.Rows.Length > TuiMarkdownParser.MaximumTableRows)
        {
            throw new ArgumentException("Markdown table exceeded its row bound.", nameof(table));
        }

        foreach (var row in table.Rows)
        {
            ArgumentNullException.ThrowIfNull(row);
            state.AddNode();
            if (row.Cells.Length > TuiMarkdownParser.MaximumTableColumns)
            {
                throw new ArgumentException("Markdown table exceeded its column bound.", nameof(table));
            }

            foreach (IReadOnlyList<TuiMarkdownSpan> cell in row.Cells)
            {
                state.AddNode();
                int before = state.CharacterCount;
                ValidateSpans(cell, state);
                if (state.CharacterCount - before > TuiMarkdownParser.MaximumCellCharacters)
                {
                    throw new ArgumentException("Markdown table cell exceeded its character bound.", nameof(table));
                }
            }
        }
    }

    private static void ValidateSpans(IReadOnlyList<TuiMarkdownSpan> spans, ValidationState state)
    {
        foreach (var span in spans)
        {
            ArgumentNullException.ThrowIfNull(span);
            state.AddNode();
            if ((span.Style & ~AllSpanStyles) != TuiMarkdownSpanStyle.None)
            {
                throw new ArgumentException("Markdown span contains unsupported style flags.", nameof(spans));
            }

            ValidateText(span.Text, TuiMarkdownParser.MaximumSourceBytes, state);
            if (span.LinkTarget is { } target)
            {
                string value = target.OriginalString;
                if (!target.IsAbsoluteUri
                    || target.Scheme is not "http" and not "https"
                    || string.IsNullOrEmpty(target.Host)
                    || !string.IsNullOrEmpty(target.UserInfo)
                    || value.Length > TuiMarkdownParser.MaximumLinkCharacters
                    || value.Any(char.IsControl)
                    || Uri.UnescapeDataString(value).Any(char.IsControl))
                {
                    throw new ArgumentException("Markdown link target is unsafe.", nameof(spans));
                }
            }
        }
    }

    private static void ValidateText(string value, int maximumCharacters, ValidationState state)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length > maximumCharacters
            || !string.Equals(value, TerminalControlEncoder.Encode(value), StringComparison.Ordinal))
        {
            throw new ArgumentException("Markdown visible text is unsafe or unbounded.", nameof(value));
        }

        state.AddCharacters(value.Length);
    }

    private sealed class ValidationState
    {
        private int _nodeCount;

        internal int CharacterCount { get; private set; }

        internal void AddCharacters(int count)
        {
            CharacterCount = checked(CharacterCount + count);
            if (CharacterCount > TuiMarkdownParser.MaximumSourceBytes)
            {
                throw new ArgumentException("Markdown semantic text exceeded its total bound.", nameof(count));
            }
        }

        internal void AddNode()
        {
            _nodeCount++;
            if (_nodeCount > TuiMarkdownParser.MaximumNodes)
            {
                throw new ArgumentException("Markdown semantic nodes exceeded their total bound.");
            }
        }

        internal void CheckDepth(int depth)
        {
            if (_nodeCount > TuiMarkdownParser.MaximumNodes || depth > TuiMarkdownParser.MaximumDepth)
            {
                throw new ArgumentException("Markdown semantic nesting exceeded its bound.", nameof(depth));
            }
        }
    }
}
