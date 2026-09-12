namespace Threadsmith.Interaction.Markdown;

/// <summary>Resource limits for interactive presentation.</summary>
public sealed record MarkdownRenderingLimits
{
    /// <summary>Maximum UTF-8 answer bytes parsed as Markdown; longer answers stream as escaped source.</summary>
    public int MaximumSourceBytes { get; init; } = 262144;

    /// <summary>Maximum parser and semantic nodes.</summary>
    public int MaximumNodes { get; init; } = 10000;

    /// <summary>Maximum semantic nesting depth, up to the recursive renderer's 32-level safety ceiling.</summary>
    public int MaximumDepth { get; init; } = 32;

    /// <summary>Maximum items per list.</summary>
    public int MaximumListItems { get; init; } = 1000;

    /// <summary>Maximum rows per table.</summary>
    public int MaximumTableRows { get; init; } = 200;

    /// <summary>Maximum columns per table.</summary>
    public int MaximumTableColumns { get; init; } = 20;

    /// <summary>Maximum text characters per table cell.</summary>
    public int MaximumCellCharacters { get; init; } = 4096;

    /// <summary>Maximum code block characters.</summary>
    public int MaximumCodeCharacters { get; init; } = 131072;

    /// <summary>Maximum link target characters.</summary>
    public int MaximumLinkCharacters { get; init; } = 2048;

    /// <summary>Maximum code language identifier characters.</summary>
    public int MaximumLanguageCharacters { get; init; } = 64;

    /// <summary>Rejects nonpositive limits and unsafe recursive nesting.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSourceBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumNodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaximumDepth, MarkdownParser.MaximumDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumListItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTableRows);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTableColumns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCellCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCodeCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumLinkCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumLanguageCharacters);
    }
}
