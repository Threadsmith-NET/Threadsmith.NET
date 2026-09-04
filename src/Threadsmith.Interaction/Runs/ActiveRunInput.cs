namespace Threadsmith.Interaction.Runs;

/// <summary>Hot-key signals admitted while an active run owns the conversation.</summary>
public enum ActiveRunInputSignal
{
    /// <summary>The first Escape key armed the bounded cancellation chord.</summary>
    CancellationArmed,

    /// <summary>A second Escape key requested active-run cancellation.</summary>
    CancellationRequested,

    /// <summary>Enter requested one idempotent safe-boundary steering prompt.</summary>
    SteeringRequested,
}

/// <summary>One exclusive semantic active-run input lease.</summary>
public interface IActiveRunInputLease : IAsyncDisposable
{
    /// <summary>Waits for the next admitted active-run signal.</summary>
    /// <param name="cancellationToken">Stops the pending read.</param>
    /// <returns>The next semantic input signal.</returns>
    Task<ActiveRunInputSignal> ReadAsync(CancellationToken cancellationToken = default);
}
