namespace Threadsmith.Tui;

using Threadsmith.Core;

/// <summary>Maps live host events and their terminal-neutral transcript deltas to semantic segments.</summary>
internal static class TuiEventSegments
{
    /// <summary>Adds the visible output owned by one event without changing its text.</summary>
    /// <param name="segments">Destination segment list.</param>
    /// <param name="domainEvent">Rendered domain event.</param>
    /// <param name="transcriptDelta">Text appended through the conversation transcript boundary.</param>
    /// <param name="showOperationDurations">Whether authoritative completion duration is displayed.</param>
    internal static void Append(
        IList<TuiTextSegment> segments,
        IDomainEvent domainEvent,
        string transcriptDelta,
        bool showOperationDurations = true)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(domainEvent);
        ArgumentNullException.ThrowIfNull(transcriptDelta);

        if (transcriptDelta.Length > 0)
        {
            AppendTranscriptDelta(segments, domainEvent, transcriptDelta);
        }

        switch (domainEvent)
        {
            case ActiveTurnCompactionCompleted completed:
                var compactionRole = completed.Status switch
                {
                    ActiveTurnCompactionInspectionStatus.Completed => TuiTextRole.Success,
                    ActiveTurnCompactionInspectionStatus.ProviderFailure
                        or ActiveTurnCompactionInspectionStatus.Cancelled
                        or ActiveTurnCompactionInspectionStatus.CapacityExceeded => TuiTextRole.Error,
                    _ => TuiTextRole.Warning,
                };
                Add(
                    segments,
                    ConversationalShell.FormatActiveTurnCompactionCompletion(
                        completed,
                        showOperationDurations),
                    compactionRole);
                break;
            case DiagnosticObserved diagnostic:
                var diagnosticRole = diagnostic.StructuredDiagnostic?.Severity switch
                {
                    DiagnosticSeverity.Error => TuiTextRole.Error,
                    DiagnosticSeverity.Warning => TuiTextRole.Warning,
                    null => TuiTextRole.Warning,
                    _ => TuiTextRole.Status,
                };
                Add(segments, $"[{diagnostic.Code}] {diagnostic.Message}{Environment.NewLine}", diagnosticRole);
                break;
            case TestRunCompleted tests:
                var outcomeRole = tests.Failed == 0
                    && tests.StructuredResult is not { Completed: false }
                        ? TuiTextRole.Success
                        : TuiTextRole.Error;
                Add(
                    segments,
                    $"Tests: {tests.Passed} passed, {tests.Failed} failed, {tests.Skipped} skipped{Environment.NewLine}",
                    outcomeRole);
                if (tests.StructuredResult is { } validation)
                {
                    foreach (var reason in validation.Selection.Rationale)
                    {
                        Add(segments, $"Selection: {reason}{Environment.NewLine}", TuiTextRole.Muted);
                    }
                }

                break;
        }
    }

    /// <summary>Adds a shared lifecycle block while preserving header and muted detail roles.</summary>
    /// <param name="segments">Destination segment list.</param>
    /// <param name="text">Lifecycle block text.</param>
    /// <param name="summaryRole">Role for the first non-blank summary line.</param>
    internal static void AppendLifecycleBlock(
        IList<TuiTextSegment> segments,
        string text,
        TuiTextRole summaryRole)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(text);

        var summaryLineSeen = false;
        AppendLines(
            segments,
            text,
            line =>
            {
                if (!summaryLineSeen && line.Length == 0)
                {
                    return summaryRole;
                }

                if (!summaryLineSeen)
                {
                    summaryLineSeen = true;
                    return summaryRole;
                }

                return TuiTextRole.Muted;
            });
    }

    private static void AppendTranscriptDelta(
        IList<TuiTextSegment> segments,
        IDomainEvent domainEvent,
        string text)
    {
        switch (domainEvent)
        {
            case ToolInvocationCompleted completed:
                AppendLifecycleBlock(
                    segments,
                    text,
                    GetToolCompletionRole(completed));
                return;
            case SemanticCheckCompleted completed:
                AppendLifecycleBlock(
                    segments,
                    text,
                    GetSemanticCheckRole(completed.Outcome));
                return;
            case PlanProposed:
                AppendLifecycleBlock(segments, text, TuiTextRole.Status);
                return;
            case PlanAutoApproved:
                AppendLifecycleBlock(segments, text, TuiTextRole.Success);
                return;
            case MutationProposalStarted:
                AppendLifecycleBlock(segments, text, TuiTextRole.Status);
                return;
            case MutationProposalRepairAttempted:
                AppendLifecycleBlock(segments, text, TuiTextRole.Warning);
                return;
            case MutationApplied:
                AppendLifecycleBlock(segments, text, TuiTextRole.Success);
                return;
            case TaskIntentRecorded:
                Add(segments, text, TuiTextRole.UserPrompt);
                return;
            case ModelReasoningObserved:
                Add(segments, text, TuiTextRole.Reasoning);
                return;
            case ModelOutputObserved:
                Add(segments, text, TuiTextRole.Default);
                return;
            case ToolInvocationStarted:
                Add(segments, text, TuiTextRole.Reasoning);
                return;
            case MutationSetProposed:
                AppendLines(segments, text, static line =>
                    line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)
                        ? TuiTextRole.DiffAdded
                        : line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal)
                            ? TuiTextRole.DiffRemoved
                            : IsDiffContextLine(line)
                                ? TuiTextRole.DiffContext
                                : TuiTextRole.Default);
                return;
            case RepositoryOpened or SolutionLoaded:
                AppendLines(segments, text, static line =>
                    line.StartsWith("Repository: ", StringComparison.Ordinal)
                        || line.StartsWith("Solution: ", StringComparison.Ordinal)
                        ? TuiTextRole.Hyperlink
                        : TuiTextRole.Status);
                return;
            case MutationSetRolledBack:
                Add(segments, text, TuiTextRole.Success);
                return;
            default:
                Add(segments, text, TuiTextRole.Status);
                return;
        }
    }

    private static void AppendLines(
        IList<TuiTextSegment> segments,
        string text,
        Func<string, TuiTextRole> selectRole,
        Action? afterLine = null)
    {
        var start = 0;
        while (start < text.Length)
        {
            var newline = text.IndexOf('\n', start);
            var end = newline < 0 ? text.Length : newline + 1;
            var line = text[start..end];
            var roleText = line.TrimStart('\r', '\n');
            Add(segments, line, selectRole(roleText));
            afterLine?.Invoke();
            start = end;
        }
    }

    private static TuiTextRole GetToolCompletionRole(ToolInvocationCompleted completed)
    {
        return completed.Outcome switch
        {
            OperationActivityOutcome.Completed => TuiTextRole.ToolSuccess,
            OperationActivityOutcome.Failed or OperationActivityOutcome.Cancelled or OperationActivityOutcome.TimedOut => TuiTextRole.ToolFailure,
            _ => completed.Succeeded ? TuiTextRole.ToolSuccess : TuiTextRole.ToolFailure,
        };
    }

    private static TuiTextRole GetSemanticCheckRole(SemanticCheckOutcome outcome)
    {
        return outcome switch
        {
            SemanticCheckOutcome.Completed or SemanticCheckOutcome.Skipped => TuiTextRole.ToolSuccess,
            SemanticCheckOutcome.Degraded => TuiTextRole.Warning,
            SemanticCheckOutcome.Failed or SemanticCheckOutcome.Cancelled => TuiTextRole.ToolFailure,
            _ => TuiTextRole.Status,
        };
    }

    private static bool IsDiffContextLine(string line)
    {
        return line.Length == 0
            || line.StartsWith("@@", StringComparison.Ordinal)
            || line.StartsWith("diff ", StringComparison.Ordinal)
            || line.StartsWith("index ", StringComparison.Ordinal)
            || line.StartsWith("---", StringComparison.Ordinal)
            || line.StartsWith("+++", StringComparison.Ordinal)
            || line.StartsWith(' ');
    }

    private static void Add(IList<TuiTextSegment> segments, string text, TuiTextRole role)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (segments.Count > 0
            && segments[^1] is { LinkTarget: null } previous
            && previous.Role == role)
        {
            segments[^1] = previous with { Text = previous.Text + text };
            return;
        }

        segments.Add(new TuiTextSegment(text, role));
    }
}
