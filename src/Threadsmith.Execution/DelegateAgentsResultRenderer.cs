namespace Threadsmith.Execution;

using System.Globalization;
using System.Text;

/// <summary>Renders complete model-visible detail blocks within a fixed character budget.</summary>
internal static class DelegateAgentsResultRenderer
{
    private const int MaximumModelProjectionCharacters = 48 * 1024;
    private const int TruncationFooterReserveCharacters = 128;

    /// <summary>Creates a compact projection without cutting fields or child status lines.</summary>
    public static string Render(DelegateAgentsResult result, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder(MaximumModelProjectionCharacters);
        _ = TryAppend(builder, $"Delegation {result.DelegationId} joined with status {result.Status}\n");
        foreach (var child in result.Children)
        {
            _ = TryAppend(
                builder,
                $"Child {child.AssignmentId} ({child.Role}, {child.ToolAccess}): {child.Status}\n");
        }

        var detailLimit = MaximumModelProjectionCharacters - TruncationFooterReserveCharacters;
        var omittedBlocks = 0;
        foreach (var child in result.Children)
        {
            var summaryBlock = $"Summary {child.AssignmentId}: {child.Summary}\nUsage: "
                + $"{child.Usage.ModelTokens} model tokens, {child.Usage.ToolCalls} tool calls\n";
            if (!TryAppend(builder, summaryBlock, detailLimit))
            {
                omittedBlocks++;
            }
        }

        var maximumFindings = result.Children.Max(child => child.Findings.Count);
        for (var index = 0; index < maximumFindings; index++)
        {
            foreach (var child in result.Children.Where(item => index < item.Findings.Count))
            {
                if (!TryAppend(builder, RenderFinding(child, child.Findings[index]), detailLimit))
                {
                    omittedBlocks++;
                }
            }
        }

        foreach (var child in result.Children)
        {
            foreach (var omission in child.Omissions)
            {
                if (!TryAppend(builder, $"Omission {child.AssignmentId}: {omission}\n", detailLimit))
                {
                    omittedBlocks++;
                }
            }
        }

        foreach (var disagreement in result.Disagreements)
        {
            if (!TryAppend(builder, $"Disagreement: {disagreement}\n", detailLimit))
            {
                omittedBlocks++;
            }
        }

        foreach (var omission in result.Omissions)
        {
            if (!TryAppend(builder, $"Delegation omission: {omission}\n", detailLimit))
            {
                omittedBlocks++;
            }
        }

        var steeringBlock = $"Steering: submitted={result.Steering.Submitted} "
            + $"delivered={result.Steering.Delivered} undelivered={result.Steering.Undelivered}\n";
        if (!TryAppend(builder, steeringBlock, detailLimit))
        {
            omittedBlocks++;
        }

        truncated = omittedBlocks > 0;
        if (truncated)
        {
            builder.Append(omittedBlocks.ToString(CultureInfo.InvariantCulture))
                .Append(" complete detail block(s) omitted by the model projection bound.")
                .AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string RenderFinding(
        DelegateAgentOutcomeSummary child,
        DelegateAgentFindingSummary finding)
    {
        var builder = new StringBuilder();
        builder.Append("Finding ").Append(child.AssignmentId).Append(": ").Append(finding.Title);
        if (finding.FilePath is not null)
        {
            builder.Append(" [").Append(finding.FilePath).Append(']');
        }

        if (finding.Symbol is not null)
        {
            builder.Append(" symbol=").Append(finding.Symbol);
        }

        builder.Append(" evidence=").Append(finding.Evidence)
            .Append(" confidence=").Append(finding.Confidence).AppendLine();
        if (finding.Uncertainty is not null)
        {
            builder.Append("Uncertainty: ").AppendLine(finding.Uncertainty);
        }

        return builder.ToString();
    }

    private static bool TryAppend(
        StringBuilder builder,
        string block,
        int maximumCharacters = MaximumModelProjectionCharacters)
    {
        if (builder.Length + block.Length > maximumCharacters)
        {
            return false;
        }

        builder.Append(block);
        return true;
    }
}
