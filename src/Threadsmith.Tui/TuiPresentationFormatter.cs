namespace Threadsmith.Tui;

using System.Text;
using Threadsmith.Core;

/// <summary>Formats host-owned terminal presentation fragments without changing durable event authority.</summary>
internal static class TuiPresentationFormatter
{
    private const int MaximumToolDetailLength = 240;
    private const int DiffContextLines = 2;
    private const string BlockOuterIndent = " ";

    private enum TuiBlockLineKind
    {
        Body,
        Item,
    }

    private sealed record TuiBlockPresentation(
        TuiBlockHeader Header,
        IReadOnlyList<TuiBlockLine> Lines,
        string ChildIndent = "",
        string OuterIndent = BlockOuterIndent);

    private sealed record TuiBlockHeader(
        string Label,
        string Title,
        string? Outcome,
        string? ElapsedText,
        TuiTextRole Role,
        TuiTextRole OutcomeRole);

    private sealed record TuiBlockLine(
        TuiBlockLineKind Kind,
        string Text,
        TuiTextRole Role);

    /// <summary>Formats one completed tool invocation as the compact interactive tools block.</summary>
    /// <param name="started">The matching invocation start event.</param>
    /// <param name="completed">The invocation completion event.</param>
    /// <param name="showOperationDurations">Whether valid host-measured durations should be shown.</param>
    /// <returns>A two-line terminal-neutral TUI presentation block.</returns>
    internal static string FormatToolCompletion(
        ToolInvocationStarted started,
        ToolInvocationCompleted completed,
        bool showOperationDurations)
    {
        ArgumentNullException.ThrowIfNull(started);
        ArgumentNullException.ThrowIfNull(completed);

        var source = completed.Source ?? started.Source;
        var block = new TuiBlockPresentation(
            new TuiBlockHeader(
                "TOOLS",
                GetToolIdentity(started.ToolName, source),
                GetOutcomeText(completed),
                GetElapsedText(completed.ElapsedMilliseconds, showOperationDurations),
                TuiTextRole.ToolSuccess,
                GetToolOutcomeRole(completed)),
            [new TuiBlockLine(TuiBlockLineKind.Item, GetToolDetail(started, completed, source), TuiTextRole.Muted)],
            ChildIndent: "  ");

        return FormatBlock(block);
    }

    /// <summary>Formats one completed semantic check as the compact interactive semantic-checks block.</summary>
    /// <param name="started">The matching check start event.</param>
    /// <param name="completed">The check completion event.</param>
    /// <param name="showOperationDurations">Whether valid host-measured durations should be shown.</param>
    /// <returns>A two-line terminal-neutral TUI presentation block.</returns>
    internal static string FormatSemanticCheckCompletion(
        SemanticCheckStarted started,
        SemanticCheckCompleted completed,
        bool showOperationDurations)
    {
        ArgumentNullException.ThrowIfNull(started);
        ArgumentNullException.ThrowIfNull(completed);

        string checkName = string.IsNullOrWhiteSpace(completed.CheckName)
            ? started.CheckName
            : completed.CheckName;
        var block = new TuiBlockPresentation(
            new TuiBlockHeader(
                "SEMANTIC CHECKS",
                GetSemanticCheckTitle(completed.Phase, checkName),
                GetSemanticOutcomeText(completed.Outcome),
                GetElapsedText(completed.ElapsedMilliseconds, showOperationDurations),
                GetSemanticOutcomeRole(completed.Outcome),
                GetSemanticOutcomeRole(completed.Outcome)),
            [new TuiBlockLine(TuiBlockLineKind.Item, GetSemanticCheckDetail(completed), TuiTextRole.Muted)],
            ChildIndent: "  ");

        return FormatBlock(block);
    }

    /// <summary>Formats one structured implementation-plan proposal as a guided interactive lifecycle block.</summary>
    /// <param name="proposed">The host-owned plan proposal event.</param>
    /// <returns>A terminal-neutral TUI presentation block.</returns>
    internal static string FormatPlanProposal(PlanProposed proposed)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(proposed.Plan);

        var lines = new List<TuiBlockLine>
        {
            new(TuiBlockLineKind.Body, proposed.Plan.Summary, TuiTextRole.Muted),
            new(TuiBlockLineKind.Body, string.Empty, TuiTextRole.Muted),
            new(TuiBlockLineKind.Body, "Steps:", TuiTextRole.Muted),
        };
        lines.AddRange(proposed.Plan.Steps.Select((step, index) => new TuiBlockLine(
            TuiBlockLineKind.Item,
            $"{index + 1}. {step.Title} - {step.ExpectedOutcome}",
            TuiTextRole.Muted)));

        return FormatBlock(new TuiBlockPresentation(
            new TuiBlockHeader(
                "PLAN",
                $"revision {proposed.Plan.Revision}",
                Outcome: null,
                ElapsedText: null,
                TuiTextRole.Status,
                TuiTextRole.Status),
            lines));
    }

    /// <summary>Formats one host-owned plan auto-approval event as a guided interactive lifecycle block.</summary>
    /// <param name="approved">The host-owned plan auto-approval event.</param>
    /// <param name="riskBasis">Optional concise explanation for the projected risk classification.</param>
    /// <returns>A terminal-neutral TUI presentation block.</returns>
    internal static string FormatPlanAutoApproval(PlanAutoApproved approved, string? riskBasis = null)
    {
        ArgumentNullException.ThrowIfNull(approved);

        var lines = new List<TuiBlockLine>
        {
            new(TuiBlockLineKind.Body, $"Revision: {approved.Revision}", TuiTextRole.Muted),
            new(TuiBlockLineKind.Body, $"Risk: {approved.Risk}", TuiTextRole.Muted),
        };
        if (!string.IsNullOrWhiteSpace(riskBasis))
        {
            lines.Add(new TuiBlockLine(TuiBlockLineKind.Body, $"Risk basis: {riskBasis}", TuiTextRole.Muted));
        }

        lines.Add(new TuiBlockLine(TuiBlockLineKind.Body, $"Policy: {approved.Policy}", TuiTextRole.Muted));
        lines.Add(new TuiBlockLine(TuiBlockLineKind.Item, $"Reason: {approved.Reason}", TuiTextRole.Muted));

        return FormatBlock(new TuiBlockPresentation(
            new TuiBlockHeader(
                "PLAN",
                "auto-approved",
                Outcome: null,
                ElapsedText: null,
                TuiTextRole.Success,
                TuiTextRole.Success),
            lines));
    }

    /// <summary>Formats one mutation proposal start event as a guided interactive lifecycle block.</summary>
    /// <param name="started">The host-owned mutation proposal start event.</param>
    /// <returns>A terminal-neutral TUI presentation block.</returns>
    internal static string FormatMutationProposalStarted(MutationProposalStarted started)
    {
        ArgumentNullException.ThrowIfNull(started);

        return FormatBlock(new TuiBlockPresentation(
            new TuiBlockHeader(
                "MUTATION",
                "Preparing preview",
                Outcome: null,
                ElapsedText: null,
                TuiTextRole.Status,
                TuiTextRole.Status),
            [new TuiBlockLine(TuiBlockLineKind.Item, FormatAttempt(started.AttemptNumber, started.MaximumAttempts), TuiTextRole.Muted)]));
    }

    /// <summary>Formats one mutation proposal repair attempt event as a guided interactive lifecycle block.</summary>
    /// <param name="repair">The host-owned mutation proposal repair attempt event.</param>
    /// <returns>A terminal-neutral TUI presentation block.</returns>
    internal static string FormatMutationProposalRepairAttempt(MutationProposalRepairAttempted repair)
    {
        ArgumentNullException.ThrowIfNull(repair);

        return FormatBlock(new TuiBlockPresentation(
            new TuiBlockHeader(
                "MUTATION",
                "Retrying proposal with correction evidence",
                Outcome: null,
                ElapsedText: null,
                TuiTextRole.Warning,
                TuiTextRole.Warning),
            [
                new TuiBlockLine(TuiBlockLineKind.Body, FormatAttempt(repair.AttemptNumber, repair.MaximumAttempts), TuiTextRole.Muted),
                new TuiBlockLine(TuiBlockLineKind.Item, $"Reason: {repair.Reason}", TuiTextRole.Muted),
            ]));
    }

    /// <summary>Formats post-apply mutation validation start as a guided interactive lifecycle block.</summary>
    /// <param name="stages">Configured validation stages that will provide post-apply evidence.</param>
    /// <returns>A terminal-neutral TUI presentation block.</returns>
    internal static string FormatMutationValidationStarted(IReadOnlyList<MutationValidationStage> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);

        string stageList = stages.Count == 0
            ? "configured validation"
            : string.Join(", ", stages.Select(stage => stage.ToString().ToLowerInvariant()));
        return FormatBlock(new TuiBlockPresentation(
            new TuiBlockHeader(
                "MUTATION",
                "Validating applied mutation",
                Outcome: null,
                ElapsedText: null,
                TuiTextRole.Status,
                TuiTextRole.Status),
            [new TuiBlockLine(TuiBlockLineKind.Item, $"Stages: {stageList}", TuiTextRole.Muted)]));
    }

    /// <summary>Formats one applied mutation as a guided interactive lifecycle block.</summary>
    /// <param name="applied">The host-owned applied mutation event.</param>
    /// <param name="requiredApproval">The previously rendered approval mode for the mutation set, when known.</param>
    /// <param name="detail">Optional concise plan-derived detail for the applied mutation.</param>
    /// <returns>A terminal-neutral TUI presentation block.</returns>
    internal static string FormatMutationApplied(
        MutationApplied applied,
        MutationApprovalLevel? requiredApproval = null,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(applied);

        string path = applied.RelativePath ?? applied.MutationId.ToString();
        string title = requiredApproval == MutationApprovalLevel.PolicyAutoApproved
            ? "Applied under the active approval policy"
            : "Applied";
        var lines = new List<TuiBlockLine>
        {
            new(TuiBlockLineKind.Body, $"Mutation applied: {path}", TuiTextRole.Muted),
        };
        if (!string.IsNullOrWhiteSpace(detail))
        {
            lines.Add(new TuiBlockLine(TuiBlockLineKind.Item, detail, TuiTextRole.Muted));
        }

        return FormatBlock(new TuiBlockPresentation(
            new TuiBlockHeader(
                "MUTATION",
                title,
                Outcome: null,
                ElapsedText: null,
                TuiTextRole.Success,
                TuiTextRole.Success),
            lines));
    }

    /// <summary>Formats a unified diff as compact presentation-owned hunks.</summary>
    /// <param name="unifiedDiff">Authoritative unified diff text.</param>
    /// <returns>Display-only diff text with compact unchanged context and one blank line after each hunk header.</returns>
    internal static string FormatUnifiedDiffForDisplay(string unifiedDiff)
    {
        ArgumentNullException.ThrowIfNull(unifiedDiff);
        if (unifiedDiff.Length == 0)
        {
            return string.Empty;
        }

        string[] lines = SplitLines(unifiedDiff);
        var builder = new StringBuilder(unifiedDiff.Length + 16);
        int index = 0;
        while (index < lines.Length)
        {
            string line = lines[index];
            if (!IsHunkHeader(line))
            {
                builder.Append(line);
                index++;
                continue;
            }

            builder.Append(line);
            builder.AppendLine();
            index++;

            int hunkStart = index;
            while (index < lines.Length
                && !IsHunkHeader(lines[index])
                && !IsDiffFileBoundary(lines, index))
            {
                index++;
            }

            AppendCompactHunkBody(builder, lines[hunkStart..index]);
        }

        return builder.ToString();
    }

    private static string FormatAttempt(int attemptNumber, int maximumAttempts)
    {
        return $"Attempt: {attemptNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}/"
            + maximumAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatBlock(TuiBlockPresentation block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var builder = new StringBuilder();
        AppendHeader(builder, block.Header, block.OuterIndent);
        AppendLines(builder, block.Lines, block.ChildIndent, block.OuterIndent);
        return builder.ToString();
    }

    private static void AppendHeader(
        StringBuilder builder,
        TuiBlockHeader header,
        string outerIndent)
    {
        builder.Append(outerIndent);
        builder.Append(TruncateForDisplay(header.Label));
        builder.Append(": ");
        builder.Append(TruncateForDisplay(header.Title));
        if (!string.IsNullOrWhiteSpace(header.Outcome))
        {
            builder.Append(" - ");
            builder.Append(TruncateForDisplay(header.Outcome));
        }

        if (!string.IsNullOrWhiteSpace(header.ElapsedText))
        {
            builder.Append(" \u00B7 ");
            builder.Append(TruncateForDisplay(header.ElapsedText));
        }

        builder.AppendLine();
    }

    private static void AppendLines(
        StringBuilder builder,
        IReadOnlyList<TuiBlockLine> lines,
        string childIndent,
        string outerIndent)
    {
        int itemCount = lines.Count(line => line.Kind == TuiBlockLineKind.Item);
        int itemIndex = 0;
        foreach (var line in lines)
        {
            if (line.Kind == TuiBlockLineKind.Item)
            {
                itemIndex++;
                AppendItemLine(builder, line.Text, itemIndex == itemCount, childIndent, outerIndent);
                continue;
            }

            AppendBodyLine(builder, line.Text, childIndent, outerIndent);
        }
    }

    private static void AppendBodyLine(
        StringBuilder builder,
        string text,
        string childIndent,
        string outerIndent)
    {
        foreach (string line in SplitBlockText(text))
        {
            builder.Append(outerIndent);
            builder.Append(childIndent);
            builder.Append('\u2502');
            if (line.Length > 0)
            {
                builder.Append(' ');
                builder.Append(line);
            }

            builder.AppendLine();
        }
    }

    private static void AppendItemLine(
        StringBuilder builder,
        string text,
        bool isLast,
        string childIndent,
        string outerIndent)
    {
        builder.Append(outerIndent);
        builder.Append(childIndent);
        builder.Append(isLast ? '\u2514' : '\u251C');
        string itemText = TruncateForDisplay(text);
        if (itemText.Length > 0)
        {
            builder.Append(' ');
            builder.Append(itemText);
        }

        builder.AppendLine();
    }

    private static IReadOnlyList<string> SplitBlockText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return [string.Empty];
        }

        return [.. text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(TruncateForDisplay)];
    }

    private static void AppendCompactHunkBody(StringBuilder builder, IReadOnlyList<string> body)
    {
        if (body.Count == 0)
        {
            return;
        }

        var keep = new bool[body.Count];
        bool hasChangedLine = false;
        for (int index = 0; index < body.Count; index++)
        {
            if (!IsChangedDiffLine(body[index]))
            {
                continue;
            }

            hasChangedLine = true;
            int start = Math.Max(0, index - DiffContextLines);
            int end = Math.Min(body.Count - 1, index + DiffContextLines);
            for (int keepIndex = start; keepIndex <= end; keepIndex++)
            {
                keep[keepIndex] = true;
            }
        }

        if (!hasChangedLine)
        {
            foreach (string line in body)
            {
                builder.Append(line);
            }

            return;
        }

        int hidden = 0;
        for (int index = 0; index < body.Count; index++)
        {
            if (keep[index])
            {
                AppendHiddenMarker(builder, ref hidden);
                builder.Append(body[index]);
                continue;
            }

            hidden++;
        }

        AppendHiddenMarker(builder, ref hidden);
    }

    private static void AppendHiddenMarker(StringBuilder builder, ref int hidden)
    {
        if (hidden == 0)
        {
            return;
        }

        builder.Append("  ... ");
        builder.Append(hidden.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(hidden == 1 ? " unchanged line hidden ..." : " unchanged lines hidden ...");
        builder.AppendLine();
        hidden = 0;
    }

    private static string[] SplitLines(string value)
    {
        var lines = new List<string>();
        int start = 0;
        while (start < value.Length)
        {
            int newline = value.IndexOf('\n', start);
            int end = newline < 0 ? value.Length : newline + 1;
            lines.Add(value[start..end]);
            start = end;
        }

        return [.. lines];
    }

    private static bool IsHunkHeader(string line)
    {
        return line.TrimEnd('\r', '\n').StartsWith("@@", StringComparison.Ordinal);
    }

    private static bool IsDiffFileBoundary(IReadOnlyList<string> lines, int index)
    {
        if (index < 0 || index >= lines.Count)
        {
            return false;
        }

        string line = lines[index];
        if (line.StartsWith("diff ", StringComparison.Ordinal))
        {
            return true;
        }

        return IsUnifiedFileFromHeader(line)
            && index + 1 < lines.Count
            && IsUnifiedFileToHeader(lines[index + 1]);
    }

    private static bool IsUnifiedFileFromHeader(string line)
    {
        return line.StartsWith("--- a/", StringComparison.Ordinal)
                || line.StartsWith("--- /dev/null", StringComparison.Ordinal);
    }

    private static bool IsUnifiedFileToHeader(string line)
    {
        return line.StartsWith("+++ b/", StringComparison.Ordinal)
                || line.StartsWith("+++ /dev/null", StringComparison.Ordinal);
    }

    private static bool IsChangedDiffLine(string line)
    {
        string content = line.TrimStart('\r', '\n');
        return (content.StartsWith('+') && !content.StartsWith("+++", StringComparison.Ordinal))
            || (content.StartsWith('-') && !content.StartsWith("---", StringComparison.Ordinal));
    }

    private static string GetToolIdentity(string toolName, ToolActivitySource? source)
    {
        if (source?.Kind == ToolActivitySourceKind.Mcp && !string.IsNullOrWhiteSpace(source.DisplayName))
        {
            return TruncateForDisplay(source.DisplayName) + "/" + TruncateForDisplay(toolName);
        }

        return TruncateForDisplay(toolName);
    }

    private static string GetOutcomeText(ToolInvocationCompleted completed)
    {
        return completed.Outcome switch
        {
            OperationActivityOutcome.Cancelled => "cancelled",
            OperationActivityOutcome.TimedOut => "timed out",
            OperationActivityOutcome.Completed => "completed",
            OperationActivityOutcome.Failed => "failed",
            _ => completed.Succeeded ? "completed" : "failed",
        };
    }

    private static string? GetElapsedText(long? elapsedMilliseconds, bool showOperationDurations)
    {
        return showOperationDurations
            && elapsedMilliseconds is { } elapsed
            && OperationDurationFormatter.TryFormat(elapsed, out string? formatted)
                ? formatted
                : null;
    }

    private static TuiTextRole GetToolOutcomeRole(ToolInvocationCompleted completed)
    {
        return completed.Outcome switch
        {
            OperationActivityOutcome.Completed => TuiTextRole.ToolSuccess,
            OperationActivityOutcome.Failed or OperationActivityOutcome.Cancelled or OperationActivityOutcome.TimedOut => TuiTextRole.ToolFailure,
            _ => completed.Succeeded ? TuiTextRole.ToolSuccess : TuiTextRole.ToolFailure,
        };
    }

    private static string GetToolDetail(
        ToolInvocationStarted started,
        ToolInvocationCompleted completed,
        ToolActivitySource? source)
    {
        var detail = new StringBuilder();
        if (source is { Kind: not ToolActivitySourceKind.Unknown } && !string.IsNullOrWhiteSpace(source.DisplayName))
        {
            detail.Append(source.Kind switch
            {
                ToolActivitySourceKind.Mcp => "mcp ",
                ToolActivitySourceKind.Extension => "extension ",
                ToolActivitySourceKind.BuiltIn => "built-in ",
                _ => string.Empty,
            });
            detail.Append(source.DisplayName);
        }

        AppendDetailPart(detail, started.ActivityDetail);
        if (!completed.Succeeded)
        {
            AppendDetailPart(detail, completed.Error);
        }

        return detail.Length == 0
            ? "no additional detail"
            : TruncateForDisplay(detail.ToString());
    }

    private static string GetSemanticOutcomeText(SemanticCheckOutcome outcome)
    {
        return outcome switch
        {
            SemanticCheckOutcome.Completed => "completed",
            SemanticCheckOutcome.Failed => "failed",
            SemanticCheckOutcome.Degraded => "degraded",
            SemanticCheckOutcome.Skipped => "skipped",
            SemanticCheckOutcome.Cancelled => "cancelled",
            _ => "unknown",
        };
    }

    private static TuiTextRole GetSemanticOutcomeRole(SemanticCheckOutcome outcome)
    {
        return outcome switch
        {
            SemanticCheckOutcome.Completed or SemanticCheckOutcome.Skipped => TuiTextRole.ToolSuccess,
            SemanticCheckOutcome.Degraded => TuiTextRole.Warning,
            SemanticCheckOutcome.Failed or SemanticCheckOutcome.Cancelled => TuiTextRole.ToolFailure,
            _ => TuiTextRole.Status,
        };
    }

    private static string GetSemanticCheckTitle(SemanticCheckPhase phase, string checkName)
    {
        string sanitizedCheckName = TruncateForDisplay(checkName);
        return phase == SemanticCheckPhase.Baseline
            && !sanitizedCheckName.Contains("pre-apply", StringComparison.OrdinalIgnoreCase)
                ? sanitizedCheckName + " (pre-apply baseline capture)"
                : sanitizedCheckName;
    }

    private static string GetSemanticCheckDetail(SemanticCheckCompleted completed)
    {
        return string.IsNullOrWhiteSpace(completed.Detail)
            ? "no additional detail"
            : TruncateForDisplay(completed.Detail);
    }

    private static void AppendDetailPart(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append("; ");
        }

        builder.Append(value);
    }

    private static string TruncateForDisplay(string value)
    {
        string sanitized = CollapseControls(value);
        return sanitized.Length <= MaximumToolDetailLength
            ? sanitized
            : sanitized[..MaximumToolDetailLength] + "...";
    }

    private static string CollapseControls(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }
}
