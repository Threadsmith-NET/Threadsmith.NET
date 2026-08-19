namespace Threadsmith.Tui;

using System.Collections.Immutable;

/// <summary>Closed terminal-neutral semantic document produced from one model answer.</summary>
/// <param name="Blocks">Validated top-level blocks in source order.</param>
internal sealed record TuiMarkdownDocument(ImmutableArray<TuiMarkdownBlock> Blocks);

/// <summary>Base type for the bounded markdown block vocabulary owned by the TUI.</summary>
internal abstract record TuiMarkdownBlock;

/// <summary>Ordinary paragraph content.</summary>
internal sealed record TuiMarkdownParagraph(ImmutableArray<TuiMarkdownSpan> Spans) : TuiMarkdownBlock;

/// <summary>Heading content and CommonMark level.</summary>
internal sealed record TuiMarkdownHeading(int Level, ImmutableArray<TuiMarkdownSpan> Spans) : TuiMarkdownBlock;

/// <summary>Quoted child blocks.</summary>
internal sealed record TuiMarkdownQuote(ImmutableArray<TuiMarkdownBlock> Blocks) : TuiMarkdownBlock;

/// <summary>Ordered or unordered list with immutable items.</summary>
internal sealed record TuiMarkdownList(
    bool IsOrdered,
    int Start,
    ImmutableArray<TuiMarkdownListItem> Items) : TuiMarkdownBlock;

/// <summary>One list item, optionally carrying task-list state.</summary>
internal sealed record TuiMarkdownListItem(
    bool? IsChecked,
    ImmutableArray<TuiMarkdownBlock> Blocks);

/// <summary>Literal fenced or indented code.</summary>
internal sealed record TuiMarkdownCodeBlock(string Code, string? Language) : TuiMarkdownBlock;

/// <summary>Bounded table rows and cells.</summary>
internal sealed record TuiMarkdownTable(ImmutableArray<TuiMarkdownTableRow> Rows) : TuiMarkdownBlock;

/// <summary>One table row.</summary>
internal sealed record TuiMarkdownTableRow(
    bool IsHeader,
    ImmutableArray<ImmutableArray<TuiMarkdownSpan>> Cells);

/// <summary>Horizontal separator.</summary>
internal sealed record TuiMarkdownThematicBreak : TuiMarkdownBlock;

/// <summary>Closed inline styling vocabulary.</summary>
[Flags]
internal enum TuiMarkdownSpanStyle
{
    None = 0,
    Emphasis = 1,
    Strong = 2,
    Strikethrough = 4,
    Code = 8,
}

/// <summary>One immutable inline span.</summary>
/// <param name="Text">Visible, terminal-neutral text.</param>
/// <param name="Style">Semantic inline styles.</param>
/// <param name="LinkTarget">Validated HTTP(S) link target, when present.</param>
/// <param name="IsHardBreak">Whether the span represents an explicit line break.</param>
internal sealed record TuiMarkdownSpan(
    string Text,
    TuiMarkdownSpanStyle Style = TuiMarkdownSpanStyle.None,
    Uri? LinkTarget = null,
    bool IsHardBreak = false);

/// <summary>Success or safe-source fallback from bounded markdown parsing.</summary>
internal sealed record TuiMarkdownParseResult(
    TuiMarkdownDocument? Document,
    string SafeSource,
    string? FallbackReason)
{
    /// <summary>Gets whether semantic rendering may proceed.</summary>
    internal bool Succeeded => Document is not null;
}
