namespace Threadsmith.Hooks;

/// <summary>Application-owned hook declaration, aggregate budget, and result limits.</summary>
public sealed record HookResourceLimits
{
    /// <summary>Maximum configured hook handlers.</summary>
    public int MaximumHandlers { get; init; } = 64;

    /// <summary>Maximum declared points per handler.</summary>
    public int MaximumHookPoints { get; init; } = 16;

    /// <summary>Maximum sum of enabled hook timeouts including retries.</summary>
    public int MaximumAggregateTimeoutMilliseconds { get; init; } = 120000;

    /// <summary>Maximum hook target characters.</summary>
    public int MaximumTargetCharacters { get; init; } = 2048;

    /// <summary>Maximum handler identifier characters.</summary>
    public int MaximumIdCharacters { get; init; } = 128;

    /// <summary>Maximum version characters.</summary>
    public int MaximumVersionCharacters { get; init; } = 64;

    /// <summary>Maximum secret references per handler.</summary>
    public int MaximumSecretReferences { get; init; } = 16;

    /// <summary>Maximum secret reference characters.</summary>
    public int MaximumSecretReferenceCharacters { get; init; } = 128;

    /// <summary>Maximum advice findings.</summary>
    public int MaximumFindings { get; init; } = 32;

    /// <summary>Maximum advice or failure explanation characters.</summary>
    public int MaximumFindingCharacters { get; init; } = 1024;

    /// <summary>Maximum failure/denial code characters.</summary>
    public int MaximumCodeCharacters { get; init; } = 64;

    /// <summary>Maximum lifecycle metadata entries.</summary>
    public int MaximumPayloadEntries { get; init; } = 32;

    /// <summary>Maximum lifecycle metadata key characters.</summary>
    public int MaximumPayloadKeyCharacters { get; init; } = 64;

    /// <summary>Maximum lifecycle metadata value characters.</summary>
    public int MaximumPayloadValueCharacters { get; init; } = 2048;

    /// <summary>Rejects nonpositive limits.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumHandlers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumHookPoints);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAggregateTimeoutMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTargetCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumIdCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumVersionCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSecretReferences);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSecretReferenceCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumFindings);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumFindingCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumCodeCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPayloadEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPayloadKeyCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPayloadValueCharacters);
    }
}
