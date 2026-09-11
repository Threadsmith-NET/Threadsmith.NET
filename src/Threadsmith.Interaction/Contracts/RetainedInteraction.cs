namespace Threadsmith.Interaction.Contracts;

using Threadsmith.Interaction.Commands;
using Threadsmith.Interaction.Presentation;

/// <summary>Optional display of independently timed concurrent tool invocations.</summary>
public interface IInteractionToolActivitySurface
{
    /// <summary>Replaces the current live tool blocks; completed blocks arrive through normal output.</summary>
    Task PresentToolActivitiesAsync(IReadOnlyList<InteractionActivity> activities, CancellationToken cancellationToken = default);
}

/// <summary>Optional retained command help, presented without adding help text to the conversation output.</summary>
public interface IInteractionHelpSurface
{
    /// <summary>Shows the authoritative command catalog until the user dismisses it.</summary>
    Task ShowCommandHelpAsync(IReadOnlyList<InteractiveCommandDescriptor> commands, CancellationToken cancellationToken = default);
}

/// <summary>Optional post-choice startup presentation, with cancellation owned by the host operation.</summary>
public interface IStartupProgressSurface
{
    /// <summary>Sets informational lines shown only in subsequent startup modals.</summary>
    Task SetStartupDetailsAsync(IReadOnlyList<string> details, CancellationToken cancellationToken = default);

    /// <summary>Shows a timed startup operation and discards ordinary input until the first composer read.</summary>
    Task ShowStartupAsync(string logo, string label, Task operation, CancellationToken cancellationToken = default);
}

/// <summary>One stable setting in an immutable availability catalog.</summary>
public sealed record InteractionToggleOption(string Id, string Label, string Group, bool Enabled, bool Locked = false, string? Reason = null)
{
    /// <summary>Explicit actions offered only for this individual item.</summary>
    public IReadOnlyList<InteractionSelectionOption> Actions { get; init; } = [];
}

/// <summary>Current host state after one immediate toggle, including bounded denial or failure detail.</summary>
public sealed record InteractionToggleResult(bool Enabled, string? Reason = null)
{
    /// <summary>Refreshed item metadata, when the host operation changes status or available actions.</summary>
    public InteractionToggleOption? UpdatedOption { get; init; }
}

/// <summary>A bounded immutable settings view with immediate per-setting host reconciliation.</summary>
public sealed record InteractionToggleRequest(string Title, IReadOnlyList<InteractionToggleOption> Options);

/// <summary>Optional retained multi-setting selector. Closing never rolls back successful host changes.</summary>
public interface IInteractionToggleSurface
{
    /// <summary>Applies serialized keyboard toggles through the supplied host authority.</summary>
    Task SelectTogglesAsync(InteractionToggleRequest request, Func<string, bool, CancellationToken, Task<InteractionToggleResult>> change, CancellationToken cancellationToken = default);
}

/// <summary>Optional individual actions alongside a retained multi-setting selector.</summary>
public interface IInteractionActionToggleSurface : IInteractionToggleSurface
{
    /// <summary>Offers per-item actions with cancellation independent of the surrounding command session.</summary>
    Task SelectActionTogglesAsync(
        InteractionToggleRequest request,
        Func<string, bool, CancellationToken, Task<InteractionToggleResult>> change,
        Func<string, string, CancellationToken, Task<InteractionToggleResult>> action,
        CancellationToken cancellationToken = default);
}
