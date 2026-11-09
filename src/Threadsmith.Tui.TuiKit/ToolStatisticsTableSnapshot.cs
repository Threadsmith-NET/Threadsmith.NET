namespace Threadsmith.Tui.TuiKit;

using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using Threadsmith.Core;
using Threadsmith.Interaction.Presentation;
using TUIKit.Unicode;

/// <summary>Immutable display values and layout prepared before publication to the terminal owner.</summary>
internal sealed record ToolStatisticsTableSnapshot(
    ImmutableArray<ToolStatisticsTableRow> Rows,
    ImmutableArray<int> ColumnWidths,
    FrozenDictionary<string, int> RowIndices)
{
    /// <summary>Gets the ordered column labels shared by preparation and rendering.</summary>
    internal static ImmutableArray<string> ColumnNames { get; } =
    [
        "Tool ID", "Total", "Success", "Failure",
        "Total duration", "Average duration", "Last-run duration", "Measured invocations", "Last invocation (UTC)",
    ];

    /// <summary>Formats each cell once and measures only the Unicode identifier with grapheme segmentation.</summary>
    internal static ToolStatisticsTableSnapshot Create(ToolStatisticsSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        var widths = ColumnNames.Select(name => name.Length + 1).ToArray();
        var rows = ImmutableArray.CreateBuilder<ToolStatisticsTableRow>(snapshot.Rows.Length);
        var indices = new Dictionary<string, int>(snapshot.Rows.Length, StringComparer.Ordinal);
        foreach (var row in snapshot.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<string> cells =
            [
                TranscriptView.Safe(row.ToolId).Replace('\n', ' '),
                row.TotalCount.ToString("N0", CultureInfo.InvariantCulture),
                row.SuccessCount.ToString("N0", CultureInfo.InvariantCulture),
                row.FailureCount.ToString("N0", CultureInfo.InvariantCulture),
                row.TotalDurationMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms",
                ToolStatisticsFormatter.AverageDuration(row),
                row.LastDurationMilliseconds is { } duration ? duration.ToString(CultureInfo.InvariantCulture) + " ms" : "unavailable",
                string.Create(CultureInfo.InvariantCulture, $"{row.MeasuredInvocationCount:N0}/{row.TotalCount:N0}"),
                row.LastInvokedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
            ];
            var identifierWidth = 0;
            foreach (var grapheme in Graphemes.Split(cells[0]))
            {
                identifierWidth += grapheme.Width;
            }

            // DataTable reserves the final cell of each fixed-width column for its separator.
            widths[0] = Math.Max(widths[0], identifierWidth + 1);
            for (var column = 1; column < cells.Length; column++)
            {
                // All remaining cells use invariant ASCII formatting.
                widths[column] = Math.Max(widths[column], cells[column].Length + 1);
            }

            indices.TryAdd(row.ToolId, rows.Count);
            rows.Add(new(row.ToolId, cells));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(rows.MoveToImmutable(), [.. widths], indices.ToFrozenDictionary(StringComparer.Ordinal));
    }
}

/// <summary>Retains the original selection identity separately from sanitized, cached cell text.</summary>
internal sealed record ToolStatisticsTableRow(string ToolId, ImmutableArray<string> Cells);
