namespace Threadsmith.Core;

/// <summary>Identifies a user operation on the goal of one live session.</summary>
public enum SessionGoalAction
{
    /// <summary>Read without changing the goal.</summary>
    Show,

    /// <summary>Replace the current goal with new text.</summary>
    Set,

    /// <summary>Remove the goal without stopping unrelated work.</summary>
    Cancel,
}

/// <summary>Controls a goal without submitting a model request.</summary>
public sealed record SessionGoalCommand(SessionId SessionId, SessionGoalAction Action, string? Text = null) : ICommand<SessionGoalResult>
{
    /// <summary>Maximum number of UTF-16 code units in the decoded goal.</summary>
    public const int MaximumGoalLength = 4096;
}

/// <summary>Reports the sanitized current text, or whether a cancellation removed a goal.</summary>
public sealed record SessionGoalResult(string? DisplayText, bool WasCancelled = false);
