namespace Threadsmith.Interaction.Presentation;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Interaction.Markdown;

/// <summary>Formats host-owned terminal presentation fragments without changing durable event authority.</summary>
internal static class InteractionPresentationFormatter
{
    private const int MaximumToolDetailLength = 240;
    private const int MaximumToolInspectionCharacters = 96 * 1024;
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
        PresentationTextRole Role,
        PresentationTextRole OutcomeRole);

    private sealed record TuiBlockLine(
        TuiBlockLineKind Kind,
        string Text,
        PresentationTextRole Role,
        bool PreserveText = false);

    /// <summary>Creates a live tool block with the same identity and detail as its eventual completion.</summary>
    internal static InteractionActivity CreateToolActivity(
        ToolInvocationStarted started,
        TimeProvider timeProvider,
        bool showOperationDurations)
    {
        ArgumentNullException.ThrowIfNull(started);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var label = started.Source?.Kind == ToolActivitySourceKind.Mcp ? "MCP" : "TOOLS";
        var identity = GetToolRequestorPrefix(started.RequestedBy) + GetToolIdentity(started.ToolName, started.Source);
        return new InteractionActivity(
            $"{label}: {identity} - running",
            timeProvider.GetTimestamp(),
            showOperationDurations,
            timeProvider)
        {
            ToolDetail = GetToolDetail(started, null, started.Source),
        };
    }

    /// <summary>Formats one completed tool invocation as the compact interactive tools block.</summary>
    /// <param name="started">The matching invocation start event.</param>
    /// <param name="completed">The invocation completion event.</param>
    /// <param name="showOperationDurations">Whether valid host-measured durations should be shown.</param>
    /// <param name="inspectCodeExploreOutput">Whether successful code_explore blocks include final model-visible output.</param>
    /// <param name="progress">Final host-owned progress entries collected for this invocation.</param>
    /// <param name="maximumInspectionCharacters">Configured expanded-output character limit.</param>
    /// <returns>A terminal-neutral TUI presentation block.</returns>
    internal static string FormatToolCompletion(
        ToolInvocationStarted started,
        ToolInvocationCompleted completed,
        bool showOperationDurations,
        bool inspectCodeExploreOutput = false,
        IReadOnlyList<PresentationTextSegment>? progress = null,
        int maximumInspectionCharacters = MaximumToolInspectionCharacters)
    {
        ArgumentNullException.ThrowIfNull(started);
        ArgumentNullException.ThrowIfNull(completed);

        var source = completed.Source ?? started.Source;
        var lines = new List<TuiBlockLine>
        {
            new(TuiBlockLineKind.Item, GetToolDetail(started, completed, source), PresentationTextRole.Muted),
        };
        foreach (var entry in progress ?? [])
        {
            lines.Add(new TuiBlockLine(TuiBlockLineKind.Body, entry.Text, entry.Role));
        }

        if (IsBuiltInMemoryTool(started, source)
            && GetMemoryOutput(started, completed, maximumInspectionCharacters) is { } memoryOutput)
        {
            lines.Add(new TuiBlockLine(
                TuiBlockLineKind.Body,
                memoryOutput,
                PresentationTextRole.Muted,
                PreserveText: true));
        }

        if (ShouldInspectCodeExploreOutput(started, completed, inspectCodeExploreOutput))
        {
            lines.Add(new TuiBlockLine(TuiBlockLineKind.Body, "Output:", PresentationTextRole.Muted));
            lines.Add(new TuiBlockLine(
                TuiBlockLineKind.Body,
                PrepareInspectionOutput(
                    completed.ModelResultContent ?? completed.ResultJson ?? string.Empty,
                    completed.ModelResultContent is null,
                    maximumInspectionCharacters),
                PresentationTextRole.Muted,
                PreserveText: true));
        }

        var block = new TuiBlockPresentation(
            new TuiBlockHeader(
                source?.Kind == ToolActivitySourceKind.Mcp ? "MCP" : "TOOLS",
                GetToolRequestorPrefix(started.RequestedBy) + GetToolIdentity(started.ToolName, source),
                GetOutcomeText(completed),
                GetElapsedText(completed.ElapsedMilliseconds, showOperationDurations),
                PresentationTextRole.ToolSuccess,
                GetToolOutcomeRole(completed)),
            lines,
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

        var checkName = string.IsNullOrWhiteSpace(completed.CheckName)
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
            [new TuiBlockLine(TuiBlockLineKind.Item, GetSemanticCheckDetail(completed), PresentationTextRole.Muted)],
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
            new(TuiBlockLineKind.Body, proposed.Plan.Summary, PresentationTextRole.Muted),
            new(TuiBlockLineKind.Body, string.Empty, PresentationTextRole.Muted),
            new(TuiBlockLineKind.Body, "Steps:", PresentationTextRole.Muted),
        };
        lines.AddRange(proposed.Plan.Steps.Select((step, index) => new TuiBlockLine(
            TuiBlockLineKind.Item,
            $"{index + 1}. {step.Title} - {step.ExpectedOutcome}",
            PresentationTextRole.Muted)));

        return FormatBlock(new TuiBlockPresentation(
            new TuiBlockHeader(
                "PLAN",
                $"revision {proposed.Plan.Revision}",
                Outcome: null,
                ElapsedText: null,
                PresentationTextRole.Status,
                PresentationTextRole.Status),
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
            new(TuiBlockLineKind.Body, $"Revision: {approved.Revision}", PresentationTextRole.Muted),
            new(TuiBlockLineKind.Body, $"Risk: {approved.Risk}", PresentationTextRole.Muted),
        };
        if (!string.IsNullOrWhiteSpace(riskBasis))
        {
            lines.Add(new TuiBlockLine(TuiBlockLineKind.Body, $"Risk basis: {riskBasis}", PresentationTextRole.Muted));
        }

        lines.Add(new TuiBlockLine(TuiBlockLineKind.Body, $"Policy: {approved.Policy}", PresentationTextRole.Muted));
        lines.Add(new TuiBlockLine(TuiBlockLineKind.Item, $"Reason: {approved.Reason}", PresentationTextRole.Muted));

        return FormatBlock(new TuiBlockPresentation(
            new TuiBlockHeader(
                "PLAN",
                "auto-approved",
                Outcome: null,
                ElapsedText: null,
                PresentationTextRole.Success,
                PresentationTextRole.Success),
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
                "Generating edits",
                Outcome: null,
                ElapsedText: null,
                PresentationTextRole.Status,
                PresentationTextRole.Status),
            [new TuiBlockLine(TuiBlockLineKind.Item, FormatAttempt(started.AttemptNumber, started.MaximumAttempts), PresentationTextRole.Muted)]));
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
                PresentationTextRole.Warning,
                PresentationTextRole.Warning),
            [
                new TuiBlockLine(TuiBlockLineKind.Body, FormatAttempt(repair.AttemptNumber, repair.MaximumAttempts), PresentationTextRole.Muted),
                new TuiBlockLine(TuiBlockLineKind.Item, $"Reason: {repair.Reason}", PresentationTextRole.Muted),
            ]));
    }

    /// <summary>Formats one generic model correction attempt as a guided interactive lifecycle block.</summary>
    /// <param name="correction">The host-owned model correction attempt event.</param>
    /// <returns>A terminal-neutral TUI presentation block.</returns>
    internal static string FormatModelCorrectionAttempt(ModelCorrectionAttempted correction)
    {
        ArgumentNullException.ThrowIfNull(correction);

        return FormatBlock(new TuiBlockPresentation(
            new TuiBlockHeader(
                "CORRECTION",
                "Retrying model request",
                Outcome: null,
                ElapsedText: null,
                PresentationTextRole.Warning,
                PresentationTextRole.Warning),
            [
                new TuiBlockLine(TuiBlockLineKind.Body, FormatAttempt(correction.AttemptNumber, correction.MaximumAttempts), PresentationTextRole.Muted),
                new TuiBlockLine(TuiBlockLineKind.Item, $"Category: {correction.Category}", PresentationTextRole.Muted),
                new TuiBlockLine(TuiBlockLineKind.Item, $"Reason: {correction.SafeReason}", PresentationTextRole.Muted),
            ]));
    }

    /// <summary>Formats post-apply mutation validation start as a guided interactive lifecycle block.</summary>
    /// <param name="stages">Configured validation stages that will provide post-apply evidence.</param>
    /// <returns>A terminal-neutral TUI presentation block.</returns>
    internal static string FormatMutationValidationStarted(IReadOnlyList<MutationValidationStage> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);

        var stageList = stages.Count == 0
            ? "configured validation"
            : string.Join(", ", stages.Select(stage => stage.ToString().ToLowerInvariant()));
        return FormatBlock(new TuiBlockPresentation(
            new TuiBlockHeader(
                "MUTATION",
                "Validating applied mutation",
                Outcome: null,
                ElapsedText: null,
                PresentationTextRole.Status,
                PresentationTextRole.Status),
            [new TuiBlockLine(TuiBlockLineKind.Item, $"Stages: {stageList}", PresentationTextRole.Muted)]));
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

        var path = applied.RelativePath ?? applied.MutationId.ToString();
        var title = requiredApproval == MutationApprovalLevel.PolicyAutoApproved
            ? "Applied under the active approval policy"
            : "Applied";
        var lines = new List<TuiBlockLine>
        {
            new(TuiBlockLineKind.Body, $"Mutation applied: {path}", PresentationTextRole.Muted),
        };
        if (!string.IsNullOrWhiteSpace(detail))
        {
            lines.Add(new TuiBlockLine(TuiBlockLineKind.Item, detail, PresentationTextRole.Muted));
        }

        return FormatBlock(new TuiBlockPresentation(
            new TuiBlockHeader(
                "MUTATION",
                title,
                Outcome: null,
                ElapsedText: null,
                PresentationTextRole.Success,
                PresentationTextRole.Success),
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

        var lines = SplitLines(unifiedDiff);
        var builder = new StringBuilder(unifiedDiff.Length + 16);
        var index = 0;
        while (index < lines.Length)
        {
            var line = lines[index];
            if (!IsHunkHeader(line))
            {
                builder.Append(line);
                index++;
                continue;
            }

            builder.Append(line);
            builder.AppendLine();
            index++;

            var hunkStart = index;
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
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var isLast = index == lines.Count - 1;
            var hasFollowingBody = !isLast && lines[index + 1].Kind == TuiBlockLineKind.Body;
            if (line.Kind == TuiBlockLineKind.Item && !hasFollowingBody)
            {
                AppendItemLine(builder, line.Text, isLast, childIndent, outerIndent);
                continue;
            }

            AppendBodyLine(builder, line.Text, childIndent, outerIndent, line.PreserveText, isLast);
        }
    }

    private static void AppendBodyLine(
        StringBuilder builder,
        string text,
        string childIndent,
        string outerIndent,
        bool preserveText,
        bool isLast)
    {
        var lines = SplitBlockText(text, preserveText);
        var lastContentIndex = lines.Count - 1;
        while (isLast && lastContentIndex > 0 && lines[lastContentIndex].Length == 0)
        {
            lastContentIndex--;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            if (isLast && index > lastContentIndex)
            {
                builder.AppendLine();
                continue;
            }

            var line = lines[index];
            builder.Append(outerIndent);
            builder.Append(childIndent);
            builder.Append(isLast && index == lastContentIndex ? '\u2514' : '\u2502');
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
        var itemText = TruncateForDisplay(text);
        if (itemText.Length > 0)
        {
            builder.Append(' ');
            builder.Append(itemText);
        }

        builder.AppendLine();
    }

    private static IReadOnlyList<string> SplitBlockText(string text, bool preserveText = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return [string.Empty];
        }

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        return preserveText
            ? lines
            : [.. lines.Select(TruncateForDisplay)];
    }

    private static bool IsBuiltInMemoryTool(ToolInvocationStarted started, ToolActivitySource? source)
    {
        return string.Equals(started.ToolName, "memories", StringComparison.Ordinal)
            && source is not { Kind: not ToolActivitySourceKind.BuiltIn };
    }

    private static string? GetMemoryOutput(ToolInvocationStarted started, ToolInvocationCompleted completed, int maximumInspectionCharacters)
    {
        if (!completed.Succeeded)
        {
            return string.IsNullOrWhiteSpace(started.TransientActivityDetail)
                ? null
                : PrepareMemoryOutput("Requested memory:\n" + started.TransientActivityDetail, maximumInspectionCharacters);
        }

        if (string.IsNullOrWhiteSpace(completed.ResultJson)
            || completed.ResultJson.Length > maximumInspectionCharacters)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(completed.ResultJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("Outcome", out var outcome)
                || outcome.ValueKind != JsonValueKind.String
                || !root.TryGetProperty("Entries", out var entries)
                || entries.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var output = new StringBuilder();
            output.Append("Outcome: ").AppendLine(outcome.GetString());
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("Id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || !entry.TryGetProperty("Text", out var text)
                    || text.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                var memoryType = entry.TryGetProperty("MemoryType", out var suppliedMemoryType)
                    ? suppliedMemoryType.ValueKind == JsonValueKind.String
                        ? suppliedMemoryType.GetString()
                        : null
                    : "situational";
                if (memoryType is not ("standingPreference" or "situational"))
                {
                    return null;
                }

                output.Append("Memory ").Append(id.GetString()).Append(" [")
                    .Append(memoryType).AppendLine("]:");
                output.AppendLine(text.GetString());
            }

            if (entries.GetArrayLength() == 0)
            {
                output.AppendLine("No memories returned.");
            }

            if (root.TryGetProperty("OmittedEntries", out var omitted)
                && omitted.ValueKind == JsonValueKind.Number
                && omitted.TryGetInt32(out var count)
                && count > 0)
            {
                output.Append("Omitted memories: ").Append(count).AppendLine(". Use /memory inspect <id> for an individual note.");
            }

            if (root.TryGetProperty("StandingPreferenceWarning", out var warning)
                && warning.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(warning.GetString()))
            {
                output.AppendLine();
                output.AppendLine(warning.GetString());
            }

            return PrepareMemoryOutput(output.ToString().TrimEnd(), maximumInspectionCharacters);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string PrepareMemoryOutput(string output, int maximumInspectionCharacters)
    {
        return PrepareBoundedOutput(output, maximumInspectionCharacters, "\n[memory output truncated by console display bound]");
    }

    private static string PrepareBoundedOutput(string output, int maximumInspectionCharacters, string truncationMarker)
    {
        var encoded = TerminalControlEncoder.Encode(NormalizeInspectionLineEndings(output));
        if (encoded.Length <= maximumInspectionCharacters)
        {
            return encoded;
        }

        if (maximumInspectionCharacters <= truncationMarker.Length)
        {
            return truncationMarker[..maximumInspectionCharacters];
        }

        var length = maximumInspectionCharacters - truncationMarker.Length;
        if (length > 0 && char.IsHighSurrogate(encoded[length - 1]))
        {
            length--;
        }

        return encoded[..length] + truncationMarker;
    }

    private static bool ShouldInspectCodeExploreOutput(
        ToolInvocationStarted started,
        ToolInvocationCompleted completed,
        bool inspectCodeExploreOutput)
    {
        return inspectCodeExploreOutput
            && completed.Succeeded
            && string.Equals(started.ToolName, "code_explore", StringComparison.Ordinal)
            && (!string.IsNullOrWhiteSpace(completed.ModelResultContent)
                || !string.IsNullOrWhiteSpace(completed.ResultJson));
    }

    private static string PrepareInspectionOutput(string output, bool isJson, int maximumInspectionCharacters)
    {
        return PrepareBoundedOutput(
            isJson ? FormatJsonForInspection(output) : output,
            maximumInspectionCharacters,
            "\n[code_explore inspection truncated by TUI display bound]");
    }

    private static string NormalizeInspectionLineEndings(string output)
    {
        return output.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static string FormatJsonForInspection(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            return JsonSerializer.Serialize(
                document.RootElement,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException)
        {
            return output;
        }
    }

    private static void AppendCompactHunkBody(StringBuilder builder, IReadOnlyList<string> body)
    {
        if (body.Count == 0)
        {
            return;
        }

        var keep = new bool[body.Count];
        var hasChangedLine = false;
        for (var index = 0; index < body.Count; index++)
        {
            if (!IsChangedDiffLine(body[index]))
            {
                continue;
            }

            hasChangedLine = true;
            var start = Math.Max(0, index - DiffContextLines);
            var end = Math.Min(body.Count - 1, index + DiffContextLines);
            for (var keepIndex = start; keepIndex <= end; keepIndex++)
            {
                keep[keepIndex] = true;
            }
        }

        if (!hasChangedLine)
        {
            foreach (var line in body)
            {
                builder.Append(line);
            }

            return;
        }

        var hidden = 0;
        for (var index = 0; index < body.Count; index++)
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
        var start = 0;
        while (start < value.Length)
        {
            var newline = value.IndexOf('\n', start);
            var end = newline < 0 ? value.Length : newline + 1;
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

        var line = lines[index];
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
        var content = line.TrimStart('\r', '\n');
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

    private static string GetToolRequestorPrefix(string? requestedBy)
    {
        if (string.IsNullOrWhiteSpace(requestedBy)
            || string.Equals(requestedBy, "model", StringComparison.OrdinalIgnoreCase)
            || string.Equals(requestedBy, "host", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return $"({TruncateForDisplay(requestedBy)}) ";
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
            && OperationDurationFormatter.TryFormat(elapsed, out var formatted)
                ? formatted
                : null;
    }

    private static PresentationTextRole GetToolOutcomeRole(ToolInvocationCompleted completed)
    {
        return completed.Outcome switch
        {
            OperationActivityOutcome.Completed => PresentationTextRole.ToolSuccess,
            OperationActivityOutcome.Failed or OperationActivityOutcome.Cancelled or OperationActivityOutcome.TimedOut => PresentationTextRole.ToolFailure,
            _ => completed.Succeeded ? PresentationTextRole.ToolSuccess : PresentationTextRole.ToolFailure,
        };
    }

    private static string GetToolDetail(
        ToolInvocationStarted started,
        ToolInvocationCompleted? completed,
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

        var activityDetail = IsBuiltInMemoryTool(started, source)
            ? started.ActivityDetail
            : completed?.TransientActivityDetail ?? started.TransientActivityDetail ?? started.ActivityDetail;
        var resultDetail = completed is null ? null : GetBuiltInSearchResultDetail(started, completed, source);
        if (resultDetail is not null)
        {
            activityDetail = string.IsNullOrWhiteSpace(activityDetail)
                ? resultDetail
                : $"{activityDetail} · {resultDetail}";
        }

        AppendDetailPart(detail, activityDetail);
        if (completed is { Succeeded: false })
        {
            AppendDetailPart(detail, completed.Error);
        }

        return detail.Length == 0
            ? "no additional detail"
            : TruncateForDisplay(detail.ToString());
    }

    private static string? GetBuiltInSearchResultDetail(
        ToolInvocationStarted started,
        ToolInvocationCompleted completed,
        ToolActivitySource? source)
    {
        if (!completed.Succeeded
            || string.IsNullOrWhiteSpace(completed.ResultJson)
            || !string.Equals(started.ToolName, "search", StringComparison.Ordinal)
            || source is { Kind: not ToolActivitySourceKind.BuiltIn })
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(completed.ResultJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Matches", out var matches)
                || matches.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var count = matches.GetArrayLength();
            var summary = count == 1 ? "1 match" : $"{count} matches";
            return completed.IsTruncated ? $"{summary}, truncated" : summary;
        }
        catch (JsonException)
        {
            return null;
        }
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

    private static PresentationTextRole GetSemanticOutcomeRole(SemanticCheckOutcome outcome)
    {
        return outcome switch
        {
            SemanticCheckOutcome.Completed or SemanticCheckOutcome.Skipped => PresentationTextRole.ToolSuccess,
            SemanticCheckOutcome.Degraded => PresentationTextRole.Warning,
            SemanticCheckOutcome.Failed or SemanticCheckOutcome.Cancelled => PresentationTextRole.ToolFailure,
            _ => PresentationTextRole.Status,
        };
    }

    private static string GetSemanticCheckTitle(SemanticCheckPhase phase, string checkName)
    {
        var sanitizedCheckName = TruncateForDisplay(checkName);
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
        var sanitized = CollapseControls(value);
        return sanitized.Length <= MaximumToolDetailLength
            ? sanitized
            : sanitized[..MaximumToolDetailLength] + "...";
    }

    private static string CollapseControls(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }
}
