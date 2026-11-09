namespace Threadsmith.Interaction.Presentation;

using System.Globalization;
using System.Text;
using Threadsmith.Core;

/// <summary>Formats stored statistics without consulting the current tool catalog.</summary>
public static class ToolStatisticsFormatter
{
    /// <summary>Formats stored rows for surfaces without a retained browser.</summary>
    public static string Format(ToolStatisticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Rows.IsDefaultOrEmpty)
        {
            return "No completed tool invocations have been recorded.\n";
        }

        var output = new StringBuilder();
        output.AppendLine("Repository tool statistics");
        output.AppendLine("Counts include terminal rejections, cancellations, and timeouts.");
        foreach (var row in snapshot.Rows)
        {
            output.AppendLine();
            foreach (var line in Details(row))
            {
                output.AppendLine(line);
            }
        }

        return output.ToString();
    }

    /// <summary>Formats one row with unknown measurements and measurement coverage.</summary>
    public static IReadOnlyList<string> Details(ToolStatisticsRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var average = row.MeasuredInvocationCount > 0
            ? ((decimal)row.TotalDurationMilliseconds / row.MeasuredInvocationCount).ToString("0.############################", CultureInfo.InvariantCulture) + " ms"
            : "unavailable";
        var last = row.LastDurationMilliseconds is { } milliseconds ? Duration(milliseconds) : "unavailable";
        var coverage = row.MeasuredInvocationCount == row.TotalCount ? string.Empty : " (not every invocation has a duration)";
        return
        [
            "Tool ID: " + row.ToolId,
            string.Create(CultureInfo.InvariantCulture, $"Total: {row.TotalCount:N0} | Success: {row.SuccessCount:N0} | Failure: {row.FailureCount:N0}"),
            "Last invocation (UTC): " + row.LastInvokedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            "Total duration: " + Duration(row.TotalDurationMilliseconds),
            "Average duration: " + average,
            "Last-run duration: " + last,
            string.Create(CultureInfo.InvariantCulture, $"Measured invocations: {row.MeasuredInvocationCount:N0}/{row.TotalCount:N0}{coverage}"),
        ];
    }

    private static string Duration(long milliseconds)
    {
        var precise = milliseconds.ToString(CultureInfo.InvariantCulture) + " ms";
        return milliseconds < 1000 || !OperationDurationFormatter.TryFormat(milliseconds, out var compact) || compact is null
            ? precise : compact + " (" + precise + ")";
    }
}
