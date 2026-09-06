namespace Threadsmith.Interaction.Presentation;

/// <summary>Identifies the semantic purpose of text rendered by the interactive terminal.</summary>
public enum PresentationTextRole
{
    /// <summary>Ordinary output.</summary>
    Default,

    /// <summary>Product branding.</summary>
    Brand,

    /// <summary>De-emphasized text.</summary>
    Muted,

    /// <summary>General status text.</summary>
    Status,

    /// <summary>Composer-adjacent session status.</summary>
    SessionStatus,

    /// <summary>Hyperlink text.</summary>
    Hyperlink,

    /// <summary>Successful tool output.</summary>
    ToolSuccess,

    /// <summary>Failed tool output.</summary>
    ToolFailure,

    /// <summary>Selection prompt text.</summary>
    SelectionPrompt,

    /// <summary>Unselected selection item.</summary>
    SelectionItem,

    /// <summary>Selected selection item.</summary>
    SelectionHighlight,

    /// <summary>Successful operation text.</summary>
    Success,

    /// <summary>Warning text.</summary>
    Warning,

    /// <summary>Error text.</summary>
    Error,

    /// <summary>User-authored prompt text.</summary>
    UserPrompt,

    /// <summary>Composer prompt text.</summary>
    ComposerPrompt,

    /// <summary>Active thinking indicator.</summary>
    ThinkingIndicator,

    /// <summary>Model reasoning text.</summary>
    Reasoning,

    /// <summary>Added diff text.</summary>
    DiffAdded,

    /// <summary>Removed diff text.</summary>
    DiffRemoved,

    /// <summary>Unchanged diff context.</summary>
    DiffContext,

    /// <summary>Markdown heading text.</summary>
    MarkdownHeading,

    /// <summary>Strong Markdown text.</summary>
    MarkdownStrong,

    /// <summary>Emphasized Markdown text.</summary>
    MarkdownEmphasis,

    /// <summary>Struck-through Markdown text.</summary>
    MarkdownStrikethrough,

    /// <summary>Inline or fenced Markdown code.</summary>
    MarkdownCode,

    /// <summary>Markdown quotation text.</summary>
    MarkdownQuote,

    /// <summary>Markdown list marker.</summary>
    MarkdownListMarker,

    /// <summary>Markdown table border.</summary>
    MarkdownTableBorder,
}

/// <summary>Represents one terminal-neutral text fragment and its semantic rendering role.</summary>
/// <param name="Text">Visible text.</param>
/// <param name="Role">Semantic role.</param>
/// <param name="LinkTarget">Optional validated target for host-owned hyperlink text.</param>
public sealed record PresentationTextSegment(string Text, PresentationTextRole Role, Uri? LinkTarget = null)
{
    private const int MaximumLinkLength = 2048;

    /// <summary>Validates text and any optional hyperlink metadata.</summary>
    /// <exception cref="ArgumentException">The hyperlink target is unsafe or unbounded.</exception>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Text);
        if (LinkTarget is null)
        {
            return;
        }

        var target = LinkTarget.OriginalString;
        if (!LinkTarget.IsAbsoluteUri
            || target.Length > MaximumLinkLength
            || target.Any(char.IsControl)
            || Uri.UnescapeDataString(target).Any(char.IsControl)
            || LinkTarget.Scheme is not "file" and not "http" and not "https")
        {
            throw new ArgumentException("Hyperlink targets must be bounded absolute file, HTTP, or HTTPS URIs.", nameof(LinkTarget));
        }
    }
}
