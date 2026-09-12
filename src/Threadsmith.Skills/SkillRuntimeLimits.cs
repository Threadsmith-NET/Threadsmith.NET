namespace Threadsmith.Skills;

/// <summary>Skill invocation and model procedure limits.</summary>
public sealed record SkillRuntimeLimits
{
    /// <summary>Maximum invocation input JSON characters.</summary>
    public int MaximumInputCharacters { get; init; } = 1048576;

    /// <summary>Maximum invocation selector characters.</summary>
    public int MaximumSelectorCharacters { get; init; } = 1024;

    /// <summary>Maximum accumulated model procedure output characters.</summary>
    public int MaximumModelOutputCharacters { get; init; } = 1048576;

    /// <summary>Validates positive limits.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumInputCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumSelectorCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumModelOutputCharacters);
    }
}
