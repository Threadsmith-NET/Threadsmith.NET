namespace Threadsmith.Execution;

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

    /// <summary>Starts a new correction sequence after the model makes accepted progress.</summary>
    public void Reset()
    {
        AttemptsUsed = 0;
    }
}
