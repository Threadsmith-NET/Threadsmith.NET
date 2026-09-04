namespace Threadsmith.Interaction.Contracts;

/// <summary>Identifies why a composer interaction completed.</summary>
public enum InteractionInputKind
{
    /// <summary>The composer submitted user text.</summary>
    Submission,

    /// <summary>The user requested a reasoning-visibility toggle.</summary>
    ToggleThinking,

    /// <summary>Background output yielded and reopened an empty composer.</summary>
    IdleOutputYield,
}

/// <summary>Identifies the semantic purpose of a composer read.</summary>
public enum ComposerPurpose
{
    /// <summary>The ordinary conversation composer.</summary>
    Conversation,

    /// <summary>A secondary host workflow prompt.</summary>
    Secondary,

    /// <summary>An active-run steering prompt.</summary>
    Steering,
}

/// <summary>Requests one semantic composer interaction.</summary>
/// <param name="Prompt">Bounded prompt label.</param>
/// <param name="Purpose">Purpose of the read.</param>
public sealed record ComposerRequest(string Prompt, ComposerPurpose Purpose = ComposerPurpose.Conversation);

/// <summary>Represents one completed or cancelled composer interaction.</summary>
/// <param name="IsSubmitted">Whether the user submitted input.</param>
/// <param name="Text">Exact submitted text.</param>
/// <param name="OperationCancellationToken">Token that cancels work started from this input.</param>
/// <param name="Kind">Reason the interaction completed.</param>
public sealed record InteractionInput(
    bool IsSubmitted,
    string Text,
    CancellationToken OperationCancellationToken,
    InteractionInputKind Kind = InteractionInputKind.Submission);
