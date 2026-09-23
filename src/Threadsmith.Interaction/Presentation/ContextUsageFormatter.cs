namespace Threadsmith.Interaction.Presentation;

using System.Globalization;
using System.Text;
using Threadsmith.Core;
using Threadsmith.Interaction.Markdown;

/// <summary>Shared size-only labels, percentages and category accounting for both frontends.</summary>
public static class ContextUsageFormatter
{
    /// <summary>Explains why no actual request can yet be inspected.</summary>
    public const string Unavailable = "No captured request usage is available. Send a request first; resumed sessions need a new request.";

    /// <summary>Enumerates included contributions in prepared order without parent totals or empty rows.</summary>
    public static IEnumerable<ContextUsageComponent> OrderedContributions(ContextUsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Leaves(snapshot.Components).Where(item => item.Tokens > 0);
    }

    /// <summary>Aggregates non-overlapping contributions independently of the viewport.</summary>
    public static IReadOnlyList<ContextUsageComponent> Categories(ContextUsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return OrderedContributions(snapshot).GroupBy(item => item.Category, StringComparer.Ordinal)
            .Select(group => new ContextUsageComponent(group.Key, group.Key, group.Key, "% of input", group.Sum(item => item.Tokens)))
            .Where(item => item.Tokens > 0).OrderByDescending(item => item.Tokens).ThenBy(item => item.Label, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Formats an overflow-safe share with honest tiny and unknown values.</summary>
    public static string Percent(long amount, long? total) => total is not > 0 ? "?%"
        : amount > 0 && (double)amount / total.Value < 0.001 ? "<0.1%"
        : (100d * amount / total.Value).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>Bounds and neutralizes external display labels.</summary>
    public static string Label(string value) => TerminalControlEncoder.Encode(value.Length > 512 ? value[..512] + "…" : value).ReplaceLineEndings(" ").Replace('\t', ' ');

    /// <summary>Formats the identical frozen snapshot for interactive presentation.</summary>
    public static string Format(ContextUsageSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return Unavailable + "\n";
        }

        var text = new StringBuilder();
        text.AppendLine($"Context usage — Estimated — {Label(snapshot.ModelName ?? snapshot.ModelProfileId?.Value.ToString() ?? "Model unknown")}");
        text.AppendLine($"{snapshot.Stage}, round {snapshot.Round + 1}; {snapshot.CapturedAt:O}; {(snapshot.DispatchStarted ? "submitted" : "prepared; submission not observed")}");
        text.AppendLine($"Input: {snapshot.InputTokens:N0}; Window: {snapshot.ContextWindow?.ToString("N0", CultureInfo.InvariantCulture) ?? "?"}; Used: {Percent(snapshot.InputTokens, snapshot.ContextWindow)}; Output reserve: {snapshot.OutputReserve:N0}");
        text.AppendLine(snapshot.EstimationBasis);
        foreach (var item in OrderedContributions(snapshot))
        {
            text.AppendLine($"[{Label(item.Container)}] {Label(item.Label)}: {item.Tokens:N0} ({Percent(item.Tokens, snapshot.InputTokens)})");
        }

        text.AppendLine("Categories — % of input (rounded)");
        foreach (var item in Categories(snapshot))
        {
            text.AppendLine($"  {item.Label}: {item.Tokens:N0} ({Percent(item.Tokens, snapshot.InputTokens)})");
        }

        return text.ToString();
    }

    private static IEnumerable<ContextUsageComponent> Leaves(IReadOnlyList<ContextUsageComponent> components) =>
        components.SelectMany(item => item.Children.Count == 0 ? [item] : Leaves(item.Children));
}
