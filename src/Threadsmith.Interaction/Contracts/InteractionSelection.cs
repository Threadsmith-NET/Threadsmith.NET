namespace Threadsmith.Interaction.Contracts;

/// <summary>One immutable selection option.</summary>
/// <param name="Id">Stable host-owned identity returned on selection.</param>
/// <param name="Label">Bounded visible label.</param>
public sealed record InteractionSelectionOption(string Id, string Label)
{
    /// <summary>Gets the optional display heading; headings are not selectable options.</summary>
    public string? Group { get; init; }

    /// <summary>Gets optional searchable text shown only in the selected option's full review.</summary>
    public string? Details { get; init; }
}

/// <summary>Requests one ordered, sequential selection.</summary>
/// <param name="Title">Bounded visible title.</param>
/// <param name="Options">Ordered immutable choices.</param>
public sealed record InteractionSelectionRequest(
    string Title,
    IReadOnlyList<InteractionSelectionOption> Options)
{
    /// <summary>Opens the full review before offering the selection.</summary>
    public bool OpenReview { get; init; }

    /// <summary>Gets a value indicating whether to separate list entries with an empty row.</summary>
    public bool SpaceBetweenOptions { get; init; }

    /// <summary>Gets bounded review evidence shown by the existing full-details view.</summary>
    public string? Details { get; init; }
}

/// <summary>Represents a selected stable identity or fail-closed cancellation.</summary>
/// <param name="SelectedOptionId">Selected identity, or <see langword="null" /> when cancelled.</param>
/// <param name="IsCancelled">Whether selection was cancelled.</param>
public sealed record InteractionSelectionResult(string? SelectedOptionId, bool IsCancelled = false);
