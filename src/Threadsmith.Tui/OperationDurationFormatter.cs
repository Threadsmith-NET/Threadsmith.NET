namespace Threadsmith.Tui;

using System.Globalization;

/// <summary>Formats validated monotonic operation durations for transient and completed TUI activity.</summary>
internal static class OperationDurationFormatter
{
    private const long MillisecondsPerSecond = 1000;
    private const long MillisecondsPerMinute = 60 * MillisecondsPerSecond;
    private const long MillisecondsPerHour = 60 * MillisecondsPerMinute;
    private static readonly long _maximumElapsedMilliseconds =
        TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond;

    /// <summary>Formats elapsed time measured from a monotonic timestamp.</summary>
    /// <param name="timeProvider">Clock that produced <paramref name="startedTimestamp"/>.</param>
    /// <param name="startedTimestamp">Monotonic start timestamp.</param>
    /// <returns>Formatted duration, or null when the elapsed value is invalid.</returns>
    internal static string? FormatElapsed(TimeProvider timeProvider, long startedTimestamp)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return TryFormat(timeProvider.GetElapsedTime(startedTimestamp), out var formatted)
            ? formatted
            : null;
    }

    /// <summary>Formats a validated duration using invariant compact display rules.</summary>
    /// <param name="duration">Duration to format.</param>
    /// <param name="formatted">Formatted duration when valid.</param>
    /// <returns>True when the duration is non-negative and representable.</returns>
    internal static bool TryFormat(TimeSpan duration, out string? formatted)
    {
        if (duration < TimeSpan.Zero)
        {
            formatted = null;
            return false;
        }

        var elapsedMilliseconds = duration.Ticks / TimeSpan.TicksPerMillisecond;
        return TryFormat(elapsedMilliseconds, out formatted);
    }

    /// <summary>Formats validated elapsed milliseconds using invariant compact display rules.</summary>
    /// <param name="elapsedMilliseconds">Elapsed milliseconds.</param>
    /// <param name="formatted">Formatted duration when valid.</param>
    /// <returns>True when the duration is non-negative and representable.</returns>
    internal static bool TryFormat(long elapsedMilliseconds, out string? formatted)
    {
        if (elapsedMilliseconds < 0 || elapsedMilliseconds > _maximumElapsedMilliseconds)
        {
            formatted = null;
            return false;
        }

        if (elapsedMilliseconds < MillisecondsPerSecond)
        {
            formatted = elapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms";
            return true;
        }

        if (elapsedMilliseconds < MillisecondsPerMinute)
        {
            var tenths = elapsedMilliseconds / 100;
            formatted = string.Create(
                CultureInfo.InvariantCulture,
                $"{tenths / 10}.{tenths % 10}s");
            return true;
        }

        var totalSeconds = elapsedMilliseconds / MillisecondsPerSecond;
        var seconds = totalSeconds % 60;
        var totalMinutes = totalSeconds / 60;
        if (totalMinutes < 60)
        {
            formatted = string.Create(CultureInfo.InvariantCulture, $"{totalMinutes}:{seconds:00}");
            return true;
        }

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        formatted = string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}");
        return true;
    }
}
