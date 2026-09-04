namespace Threadsmith.Interaction.Coordination;

using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Runs;
using Threadsmith.Interaction.Sessions;

/// <summary>Provides coordinator conveniences over the public semantic surface.</summary>
internal sealed class InteractionSessionSurface
{
    private readonly IInteractionSurface _surface;
    private string _prompt = "threadsmith > ";

    /// <summary>Initializes a new instance of the <see cref="InteractionSessionSurface" /> class.</summary>
    internal InteractionSessionSurface(IInteractionSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _surface = surface;
    }

    /// <summary>Gets the public surface for fixed frontend-local contributions.</summary>
    internal IInteractionSurface Surface => _surface;

    /// <summary>Updates the next composer request label.</summary>
    internal Task SetPromptAsync(string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        cancellationToken.ThrowIfCancellationRequested();
        _prompt = prompt;
        return Task.CompletedTask;
    }

    /// <summary>Reads the current composer request.</summary>
    internal Task<InteractionInput> ReadAsync(CancellationToken cancellationToken = default)
    {
        var purpose = string.Equals(_prompt, "steer > ", StringComparison.Ordinal)
            ? ComposerPurpose.Steering
            : ComposerPurpose.Conversation;
        return _surface.ReadComposerAsync(new ComposerRequest(_prompt, purpose), cancellationToken);
    }

    /// <summary>Begins active-run input when supported.</summary>
    internal IActiveRunInputLease? BeginActiveRunInput(TimeProvider timeProvider)
    {
        return _surface.BeginActiveRunInput(timeProvider);
    }

    /// <summary>Selects one ordered option and validates the returned stable identity.</summary>
    internal async Task<int> SelectAsync(
        string title,
        IReadOnlyList<string> choices,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(choices);
        if (choices.Count == 0)
        {
            throw new ArgumentException("At least one choice is required.", nameof(choices));
        }

        var options = choices
            .Select((label, index) => new InteractionSelectionOption(index.ToString(System.Globalization.CultureInfo.InvariantCulture), label))
            .ToArray();
        var result = await _surface.SelectAsync(
            new InteractionSelectionRequest(title, options),
            cancellationToken);
        if (result.IsCancelled || result.SelectedOptionId is null)
        {
            throw new OperationCanceledException("The interaction selection was cancelled.", cancellationToken);
        }

        if (!int.TryParse(
                result.SelectedOptionId,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var selected)
            || selected < 0
            || selected >= choices.Count)
        {
            throw new InvalidOperationException("The interaction surface returned an unknown selection identity.");
        }

        return selected;
    }

    /// <summary>Presents a simple transient status.</summary>
    internal Task ShowStatusUntilAsync(
        string text,
        Task operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
#pragma warning disable VSTHRD003 // The coordinator owns and separately observes the operation controlling activity.
        return _surface.PresentActivityUntilAsync(
            new InteractionActivity(text, 0, ShowDuration: false, TimeProvider.System),
            operation,
            cancellationToken);
#pragma warning restore VSTHRD003
    }

    /// <summary>Presents a dynamic activity.</summary>
    internal Task ShowActivityUntilAsync(
        InteractionActivity activity,
        Task operation,
        CancellationToken cancellationToken = default)
    {
#pragma warning disable VSTHRD003 // The coordinator owns and separately observes the operation controlling activity.
        return _surface.PresentActivityUntilAsync(activity, operation, cancellationToken);
#pragma warning restore VSTHRD003
    }

    /// <summary>Presents one plain semantic segment.</summary>
    internal Task WriteAsync(
        string text,
        PresentationTextRole role = PresentationTextRole.Default,
        CancellationToken cancellationToken = default)
    {
        return WriteSegmentsAsync([new PresentationTextSegment(text, role)], cancellationToken);
    }

    /// <summary>Presents ordered semantic segments.</summary>
    internal Task WriteSegmentsAsync(
        IReadOnlyList<PresentationTextSegment> segments,
        CancellationToken cancellationToken = default)
    {
        return WriteOutputAsync([new PresentationTextItem(segments)], cancellationToken);
    }

    /// <summary>Presents ordered semantic output items.</summary>
    internal Task WriteOutputAsync(
        IReadOnlyList<PresentationItem> items,
        CancellationToken cancellationToken = default)
    {
        return _surface.PresentAsync(new PresentationBatch(items), cancellationToken);
    }

    /// <summary>Presents the structured status snapshot.</summary>
    internal Task ShowSessionStatusAsync(
        SessionStatusSnapshot status,
        CancellationToken cancellationToken = default)
    {
        return _surface.PresentSessionStatusAsync(status, cancellationToken);
    }
}

/// <summary>Coordinates presentation-only yields for non-primary composer prompts.</summary>
internal static class InteractionInputReader
{
    /// <summary>Reads a user interaction, retrying an empty-composer output yield.</summary>
    internal static async Task<InteractionInput> ReadSecondaryAsync(
        InteractionSessionSurface surface,
        string? prompt,
        PresentationTextRole promptRole,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(surface);
        while (true)
        {
            if (prompt is not null)
            {
                await surface.WriteAsync(prompt, promptRole, cancellationToken);
            }

            var input = await surface.ReadAsync(cancellationToken);
            if (input.Kind != InteractionInputKind.IdleOutputYield)
            {
                return input;
            }
        }
    }
}
