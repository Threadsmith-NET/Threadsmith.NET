namespace Threadsmith.Interaction.Presentation;

using Threadsmith.Interaction.Markdown;

/// <summary>Base type for one bounded semantic presentation item.</summary>
public abstract record PresentationItem;

/// <summary>Already projected semantic text segments.</summary>
/// <param name="Segments">Ordered terminal-neutral segments.</param>
public sealed record PresentationTextItem(IReadOnlyList<PresentationTextSegment> Segments) : PresentationItem;

/// <summary>Exact model source paired with its safe presentation copy.</summary>
/// <param name="RawSource">Authoritative raw model source.</param>
/// <param name="SafeSource">Control-neutralized presentation source.</param>
/// <param name="StartsAnswerBlock">Whether this item starts a visible answer block.</param>
public sealed record PresentationSourceItem(
    string RawSource,
    string SafeSource,
    bool StartsAnswerBlock) : PresentationItem;

/// <summary>Validated semantic Markdown retaining exact and safe source.</summary>
/// <param name="Document">Closed immutable Markdown document.</param>
/// <param name="RawSource">Authoritative raw model source.</param>
/// <param name="SafeSource">Control-neutralized presentation source.</param>
/// <param name="StartsAnswerBlock">Whether this item starts a visible answer block.</param>
public sealed record PresentationMarkdownItem(
    MarkdownDocument Document,
    string RawSource,
    string SafeSource,
    bool StartsAnswerBlock) : PresentationItem;

/// <summary>Capability-gated exact source for redirected output.</summary>
/// <param name="RawSource">Exact source.</param>
public sealed record PresentationRawSourceItem(string RawSource) : PresentationItem;

/// <summary>One immutable ordered presentation operation.</summary>
/// <param name="Items">Items in authoritative event order.</param>
public sealed record PresentationBatch(IReadOnlyList<PresentationItem> Items);
