namespace Threadsmith.Interaction.Contracts;

using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Runs;
using Threadsmith.Interaction.Sessions;

/// <summary>Provides frontend input and semantic presentation without exposing terminal libraries.</summary>
public interface IInteractionSurface
{
    /// <summary>Gets immutable optional behavior supported by this frontend.</summary>
    InteractionSurfaceCapabilities Capabilities { get; }

    /// <summary>Reads one composer interaction.</summary>
    /// <param name="request">Semantic composer request.</param>
    /// <param name="cancellationToken">Stops the pending read.</param>
    /// <returns>The completed composer interaction.</returns>
    Task<InteractionInput> ReadComposerAsync(
        ComposerRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Lets the user select one stable option.</summary>
    /// <param name="request">Ordered selection request.</param>
    /// <param name="cancellationToken">Stops the pending selection.</param>
    /// <returns>The selected option identity, or a cancelled result.</returns>
    Task<InteractionSelectionResult> SelectAsync(
        InteractionSelectionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Presents one ordered semantic output batch.</summary>
    /// <param name="batch">Output items in authoritative order.</param>
    /// <param name="cancellationToken">Cancels admission before the batch is accepted.</param>
    Task PresentAsync(PresentationBatch batch, CancellationToken cancellationToken = default);

    /// <summary>Presents structured status at the frontend's ordinary composer boundary.</summary>
    /// <param name="status">Immutable host-derived status.</param>
    /// <param name="cancellationToken">Stops the pending presentation.</param>
    Task PresentSessionStatusAsync(
        SessionStatusSnapshot status,
        CancellationToken cancellationToken = default);

    /// <summary>Presents bounded activity until an existing operation completes.</summary>
    /// <param name="activity">Semantic activity state.</param>
    /// <param name="operation">Existing operation controlling the activity lifetime.</param>
    /// <param name="cancellationToken">Stops the pending activity presentation.</param>
    Task PresentActivityUntilAsync(
        InteractionActivity activity,
        Task operation,
        CancellationToken cancellationToken = default);

    /// <summary>Begins exclusive semantic hot-key capture for an active run.</summary>
    /// <param name="timeProvider">Clock used for bounded key chords.</param>
    /// <returns>An active-run input lease, or <see langword="null" /> when unsupported.</returns>
    IActiveRunInputLease? BeginActiveRunInput(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return null;
    }
}
