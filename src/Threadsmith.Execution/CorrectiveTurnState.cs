namespace Threadsmith.Execution;

using Threadsmith.Models;

/// <summary>Counts bounded active-turn corrective attempts for one logical model turn.</summary>
internal sealed class CorrectiveTurnState
{
    /// <summary>Initializes a new instance of the <see cref="CorrectiveTurnState"/> class.</summary>
    public CorrectiveTurnState(int maximumTurns)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumTurns);
        MaximumTurns = maximumTurns;
    }

    /// <summary>Gets the number of attempts already consumed.</summary>
    public int AttemptsUsed { get; private set; }

    /// <summary>Gets the maximum allowed corrective attempts.</summary>
    public int MaximumTurns { get; }

    /// <summary>Consumes one corrective attempt when budget remains.</summary>
    public bool TryBeginAttempt(out int attemptNumber)
    {
        if (AttemptsUsed >= MaximumTurns)
        {
            attemptNumber = 0;
            return false;
        }

        AttemptsUsed++;
        attemptNumber = AttemptsUsed;
        return true;
    }

    /// <summary>Admits a recoverable invocation correction or reports exhausted correction/model rounds.</summary>
    public int BeginAttemptOrThrow(
        MalformedInvocationDiagnostic diagnostic,
        int modelRound = 0,
        int maximumModelRounds = 0)
    {
        if ((maximumModelRounds > 0 && modelRound >= maximumModelRounds)
            || !TryBeginAttempt(out var attemptNumber))
        {
            throw new MalformedInvocationException(diagnostic);
        }

        return attemptNumber;
    }

    /// <summary>Starts a new correction sequence after the model makes accepted progress.</summary>
    public void Reset()
    {
        AttemptsUsed = 0;
    }
}
