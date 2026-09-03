namespace Threadsmith.Core;

/// <summary>Identifies one idempotent safe-boundary steering pause.</summary>
public readonly record struct SteeringPauseId(Guid Value) : IStableIdentifier
{
    /// <summary>Creates an identifier.</summary>
    public static SteeringPauseId New()
    {
        return new(Guid.NewGuid());
    }
}

/// <summary>Outcome of requesting a steering pause for an active run.</summary>
public enum RunSteeringPauseRequestStatus
{
    /// <summary>A new pause request was accepted.</summary>
    Accepted,

    /// <summary>The run already has the same pending pause request.</summary>
    AlreadyPending,

    /// <summary>The run reached a terminal state before the request arrived.</summary>
    TooLate,

    /// <summary>The run does not belong to the supplied session.</summary>
    WrongSession,
}

/// <summary>Result of one idempotent steering-pause request.</summary>
public sealed record RunSteeringPauseRequestResult(
    RunSteeringPauseRequestStatus Status,
    SteeringPauseId PauseId);

/// <summary>Outcome of waiting for a requested steering pause.</summary>
public enum RunSteeringPauseWaitStatus
{
    /// <summary>The run and every active child reached a safe boundary.</summary>
    Ready,

    /// <summary>The run completed before the pause became ready.</summary>
    TooLate,

    /// <summary>The request identity or session did not match the active run.</summary>
    Rejected,
}

/// <summary>Result of waiting for a steering pause to reach a safe boundary.</summary>
public sealed record RunSteeringPauseWaitResult(
    RunSteeringPauseWaitStatus Status,
    SteeringPauseId PauseId);

/// <summary>Outcome of submitting or dismissing a ready steering prompt.</summary>
public enum RunSteeringSubmissionStatus
{
    /// <summary>Non-empty steering text was accepted.</summary>
    Accepted,

    /// <summary>The prompt was dismissed without adding model context.</summary>
    Dismissed,

    /// <summary>The run completed before submission.</summary>
    TooLate,

    /// <summary>The pause, run, or session identity did not match.</summary>
    Rejected,
}

/// <summary>Result of submitting one steering prompt.</summary>
public sealed record RunSteeringSubmissionResult(
    RunSteeringSubmissionStatus Status,
    long? Sequence);

/// <summary>Requests one idempotent pause at the active run's next safe boundary.</summary>
public sealed record RequestRunSteeringPauseCommand(SessionId SessionId, RunId RunId)
    : ICommand<RunSteeringPauseRequestResult>;

/// <summary>Waits until a steering pause is safe to display or becomes too late.</summary>
public sealed record WaitForRunSteeringPauseCommand(
    SessionId SessionId,
    RunId RunId,
    SteeringPauseId PauseId) : ICommand<RunSteeringPauseWaitResult>;

/// <summary>Submits user steering text, or dismisses the prompt when text is empty.</summary>
public sealed record SubmitRunSteeringCommand(
    SessionId SessionId,
    RunId RunId,
    SteeringPauseId PauseId,
    string? Text) : ICommand<RunSteeringSubmissionResult>;
