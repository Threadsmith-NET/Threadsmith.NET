namespace Threadsmith.Extensions.Runtime;

using Threadsmith.Core;

/// <summary>Per-extension invocation budget for one turn (strategy §17.15, §22.2). Defense-in-depth against a malicious
/// trusted extension exhausting host resources — an ALC is not a security boundary (§17.24).</summary>
public sealed class ExtensionInvocationBudget
{
    private readonly int _maxInvocationsPerTurn;
    private readonly ExtensionGenerationId _generationId;
    private int _reserved;
    private bool _blocked;

    /// <summary>Initializes a new instance of the <see cref="ExtensionInvocationBudget"/> class.</summary>
    /// <param name="generationId">The owning generation.</param>
    /// <param name="maxInvocationsPerTurn">The maximum invocations allowed per turn; defaults to 256.</param>
    public ExtensionInvocationBudget(ExtensionGenerationId generationId, int maxInvocationsPerTurn = 256)
    {
        if (maxInvocationsPerTurn <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxInvocationsPerTurn),
                "The maximum invocations per turn must be positive.");
        }

        _generationId = generationId;
        _maxInvocationsPerTurn = maxInvocationsPerTurn;
    }

    /// <summary>The owning generation.</summary>
    public ExtensionGenerationId GenerationId => _generationId;

    /// <summary>The maximum invocations per turn.</summary>
    public int MaxInvocationsPerTurn => _maxInvocationsPerTurn;

    /// <summary>The number of invocations reserved this turn.</summary>
    public int Reserved => Volatile.Read(ref _reserved);

    /// <summary>Whether the budget is exhausted and further invocations are blocked for the remainder of the turn.</summary>
    public bool IsExhausted => Volatile.Read(ref _blocked);

    /// <summary>Reserves one invocation slot. Returns false (and blocks the remainder of the turn) when the budget is exhausted.</summary>
    /// <returns>True when an invocation slot was reserved; false when the budget is exhausted.</returns>
    public bool TryReserve()
    {
        var current = Interlocked.Increment(ref _reserved);
        if (current > _maxInvocationsPerTurn)
        {
            Volatile.Write(ref _blocked, true);
            return false;
        }

        return true;
    }

    /// <summary>Releases a reserved slot (the invocation completed).</summary>
    public void Release()
    {
        Interlocked.Decrement(ref _reserved);
    }

    /// <summary>Resets the budget for a new turn.</summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _reserved, 0);
        Volatile.Write(ref _blocked, false);
    }
}