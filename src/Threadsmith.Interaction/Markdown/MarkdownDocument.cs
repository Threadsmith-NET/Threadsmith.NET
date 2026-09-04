namespace Threadsmith.Interaction.Markdown;

using System.Collections.Immutable;

/// <summary>Closed terminal-neutral semantic document produced from one model answer.</summary>
/// <param name="Blocks">Validated top-level blocks in source order.</param>
public sealed record MarkdownDocument(ImmutableArray<MarkdownBlock> Blocks);

/// <summary>Base type for the bounded markdown block vocabulary owned by the TUI.</summary>
public abstract record MarkdownBlock;

/// <summary>Ordinary paragraph content.</summary>
public sealed record MarkdownParagraph(ImmutableArray<MarkdownSpan> Spans) : MarkdownBlock;

/// <summary>Heading content and CommonMark level.</summary>
public sealed record MarkdownHeading(int Level, ImmutableArray<MarkdownSpan> Spans) : MarkdownBlock;

/// <summary>Quoted child blocks.</summary>
public sealed record MarkdownQuote(ImmutableArray<MarkdownBlock> Blocks) : MarkdownBlock;

/// <summary>Ordered or unordered list with immutable items.</summary>
public sealed record MarkdownList(
    bool IsOrdered,
    int Start,
    ImmutableArray<MarkdownListItem> Items) : MarkdownBlock;

/// <summary>One list item, optionally carrying task-list state.</summary>
public sealed record MarkdownListItem(
    bool? IsChecked,
    ImmutableArray<MarkdownBlock> Blocks);

/// <summary>Literal fenced or indented code.</summary>
public sealed record MarkdownCodeBlock(string Code, string? Language) : MarkdownBlock;

/// <summary>Bounded table rows and cells.</summary>
public sealed record MarkdownTable(ImmutableArray<MarkdownTableRow> Rows) : MarkdownBlock;

/// <summary>One table row.</summary>
public sealed record MarkdownTableRow(
    bool IsHeader,
    ImmutableArray<ImmutableArray<MarkdownSpan>> Cells);

/// <summary>Horizontal separator.</summary>
public sealed record MarkdownThematicBreak : MarkdownBlock;

/// <summary>Closed inline styling vocabulary.</summary>
[Flags]
public enum MarkdownSpanStyle
{
    /// <summary>No inline style.</summary>
    None = 0,

    /// <summary>Emphasized text.</summary>
    Emphasis = 1,

    /// <summary>Strong text.</summary>
    Strong = 2,

    /// <summary>Struck-through text.</summary>
    Strikethrough = 4,

    /// <summary>Code text.</summary>
    Code = 8,
}

/// <summary>One immutable inline span.</summary>
/// <param name="Text">Visible, terminal-neutral text.</param>
/// <param name="Style">Semantic inline styles.</param>
/// <param name="LinkTarget">Validated HTTP(S) link target, when present.</param>
/// <param name="IsHardBreak">Whether the span represents an explicit line break.</param>
public sealed record MarkdownSpan(
    string Text,
    MarkdownSpanStyle Style = MarkdownSpanStyle.None,
    Uri? LinkTarget = null,
    bool IsHardBreak = false);

/// <summary>Success or safe-source fallback from bounded markdown parsing.</summary>
internal sealed record MarkdownParseResult(
    MarkdownDocument? Document,
    string SafeSource,
    string? FallbackReason)
{
    /// <summary>Gets whether semantic rendering may proceed.</summary>
    internal bool Succeeded => Document is not null;
}
