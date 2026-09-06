namespace Threadsmith.Interaction.Contracts;

/// <summary>One immutable selection option.</summary>
/// <param name="Id">Stable host-owned identity returned on selection.</param>
/// <param name="Label">Bounded visible label.</param>
public sealed record InteractionSelectionOption(string Id, string Label);

/// <summary>Requests one ordered, sequential selection.</summary>
/// <param name="Title">Bounded visible title.</param>
/// <param name="Options">Ordered immutable choices.</param>
public sealed record InteractionSelectionRequest(
    string Title,
    IReadOnlyList<InteractionSelectionOption> Options);

/// <summary>Represents a selected stable identity or fail-closed cancellation.</summary>
/// <param name="SelectedOptionId">Selected identity, or <see langword="null" /> when cancelled.</param>
/// <param name="IsCancelled">Whether selection was cancelled.</param>
public sealed record InteractionSelectionResult(string? SelectedOptionId, bool IsCancelled = false);
