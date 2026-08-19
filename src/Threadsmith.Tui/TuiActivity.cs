namespace Threadsmith.Tui;

/// <summary>Terminal-neutral description of one transient operation activity.</summary>
/// <param name="Label">Bounded host-owned activity label.</param>
/// <param name="StartedTimestamp">Monotonic start timestamp.</param>
/// <param name="ShowDuration">Whether elapsed duration is displayed.</param>
/// <param name="TimeProvider">Clock that owns the timestamp.</param>
internal sealed record TuiActivity(
    string Label,
    long StartedTimestamp,
    bool ShowDuration,
    TimeProvider TimeProvider)
{
    /// <summary>Formats the current activity text without terminal control sequences.</summary>
    internal string Format()
    {
        if (!ShowDuration)
        {
            return Label;
        }

        var elapsed = OperationDurationFormatter.FormatElapsed(TimeProvider, StartedTimestamp);
        return elapsed is null ? Label : $"{Label} · {elapsed}";
    }
}
