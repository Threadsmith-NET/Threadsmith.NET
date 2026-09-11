namespace Threadsmith.Interaction.Presentation;

/// <summary>Terminal-neutral description of one transient operation activity.</summary>
/// <param name="Label">Bounded host-owned activity label.</param>
/// <param name="StartedTimestamp">Monotonic start timestamp.</param>
/// <param name="ShowDuration">Whether elapsed duration is displayed.</param>
/// <param name="TimeProvider">Clock that owns the timestamp.</param>
public sealed record InteractionActivity(
    string Label,
    long StartedTimestamp,
    bool ShowDuration,
    TimeProvider TimeProvider)
{
    /// <summary>Gets the detail for a live tool block, or null for an ordinary status activity.</summary>
    public string? ToolDetail { get; init; }

    /// <summary>Gets current, single-line progress entries beneath the tool detail.</summary>
    public IReadOnlyList<PresentationTextSegment> ToolProgress { get; init; } = [];

    /// <summary>Formats the current activity text without terminal control sequences.</summary>
    public string Format()
    {
        if (!ShowDuration)
        {
            return Label;
        }

        var elapsed = OperationDurationFormatter.FormatElapsed(TimeProvider, StartedTimestamp);
        return elapsed is null ? Label : $"{Label} · {elapsed}";
    }
}
