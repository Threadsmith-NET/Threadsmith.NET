namespace Threadsmith.Interaction.Commands;

using Threadsmith.Interaction.Contracts;

/// <summary>Result of one fixed frontend-local command attempt.</summary>
public enum FrontendCommandOutcome
{
    /// <summary>The contribution did not recognize the invocation.</summary>
    NotHandled,

    /// <summary>The contribution handled the invocation.</summary>
    Handled,
}

/// <summary>Handles a fixed presentation-local command without receiving host authority.</summary>
public interface IFrontendCommandContribution
{
    /// <summary>Handles one catalogued frontend-local command.</summary>
    /// <param name="invocation">Parsed fixed invocation.</param>
    /// <param name="surface">Terminal-neutral presentation surface.</param>
    /// <param name="cancellationToken">Stops the pending frontend operation.</param>
    /// <returns>Whether the invocation was handled.</returns>
    Task<FrontendCommandOutcome> HandleAsync(
        InteractiveCommandInvocation invocation,
        IInteractionSurface surface,
        CancellationToken cancellationToken = default);
}
