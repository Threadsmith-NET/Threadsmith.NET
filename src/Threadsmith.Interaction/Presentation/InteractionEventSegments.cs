namespace Threadsmith.Interaction.Presentation;

using Threadsmith.Core;
using Threadsmith.Interaction.Coordination;

/// <summary>Maps live host events and their terminal-neutral transcript deltas to semantic segments.</summary>
internal static class InteractionEventSegments
{
    /// <summary>Adds the visible output owned by one event without changing its text.</summary>
    /// <param name="segments">Destination segment list.</param>
    /// <param name="domainEvent">Rendered domain event.</param>
    /// <param name="transcriptDelta">Text appended through the conversation transcript boundary.</param>
    /// <param name="showOperationDurations">Whether authoritative completion duration is displayed.</param>
    internal static void Append(
        IList<PresentationTextSegment> segments,
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
            case DelegationCheckpointWritten { Phase: DelegationCheckpointPhase.Accepted } accepted:
                Add(
                    segments,
                    DelegationActivityRegistry.FormatAccepted(accepted),
                    PresentationTextRole.Status);
                break;
            case ActiveTurnCompactionCompleted completed:
                var compactionRole = completed.Status switch
                {
                    ActiveTurnCompactionInspectionStatus.Completed => PresentationTextRole.Success,
                    ActiveTurnCompactionInspectionStatus.ProviderFailure
                        or ActiveTurnCompactionInspectionStatus.Cancelled
                        or ActiveTurnCompactionInspectionStatus.CapacityExceeded => PresentationTextRole.Error,
                    _ => PresentationTextRole.Warning,
                };
                Add(
                    segments,
                    InteractionCoordinator.FormatActiveTurnCompactionCompletion(
                        completed,
                        showOperationDurations),
                    compactionRole);
                break;
            case DiagnosticObserved diagnostic:
                var diagnosticRole = diagnostic.StructuredDiagnostic?.Severity switch
                {
                    DiagnosticSeverity.Error => PresentationTextRole.Error,
                    DiagnosticSeverity.Warning => PresentationTextRole.Warning,
                    null => PresentationTextRole.Warning,
                    _ => PresentationTextRole.Status,
                };
                Add(segments, $"[{diagnostic.Code}] {diagnostic.Message}{Environment.NewLine}", diagnosticRole);
                break;
            case TestRunCompleted tests:
                var outcomeRole = tests.Failed == 0
                    && tests.StructuredResult is not { Completed: false }
                        ? PresentationTextRole.Success
                        : PresentationTextRole.Error;
                Add(
                    segments,
                    $"Tests: {tests.Passed} passed, {tests.Failed} failed, {tests.Skipped} skipped{Environment.NewLine}",
                    outcomeRole);
                if (tests.StructuredResult is { } validation)
                {
                    foreach (var reason in validation.Selection.Rationale)
                    {
                        Add(segments, $"Selection: {reason}{Environment.NewLine}", PresentationTextRole.Muted);
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
        IList<PresentationTextSegment> segments,
        string text,
        PresentationTextRole summaryRole)
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

                return PresentationTextRole.Muted;
            });
    }

    private static void AppendTranscriptDelta(
        IList<PresentationTextSegment> segments,
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
                AppendLifecycleBlock(segments, text, PresentationTextRole.Status);
                return;
            case PlanAutoApproved:
                AppendLifecycleBlock(segments, text, PresentationTextRole.Success);
                return;
            case MutationProposalStarted:
                AppendLifecycleBlock(segments, text, PresentationTextRole.Status);
                return;
            case MutationProposalRepairAttempted or ModelCorrectionAttempted:
                AppendLifecycleBlock(segments, text, PresentationTextRole.Warning);
                return;
            case MutationApplied:
                AppendLifecycleBlock(segments, text, PresentationTextRole.Success);
                return;
            case ModelFallbackSelected:
                Add(segments, text, PresentationTextRole.Warning);
                return;
            case TaskIntentRecorded:
                Add(segments, text, PresentationTextRole.UserPrompt);
                return;
            case ModelReasoningObserved:
                Add(segments, text, PresentationTextRole.Reasoning);
                return;
            case ModelOutputObserved:
                Add(segments, text, PresentationTextRole.Default);
                return;
            case ToolInvocationStarted:
                Add(segments, text, PresentationTextRole.Reasoning);
                return;
            case MutationSetProposed:
                AppendLines(segments, text, static line =>
                    line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal)
                        ? PresentationTextRole.DiffAdded
                        : line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal)
                            ? PresentationTextRole.DiffRemoved
                            : IsDiffContextLine(line)
                                ? PresentationTextRole.DiffContext
                                : PresentationTextRole.Default);
                return;
            case RepositoryOpened or SolutionLoaded:
                AppendLines(segments, text, static line =>
                    line.StartsWith("Repository: ", StringComparison.Ordinal)
                        || line.StartsWith("Solution: ", StringComparison.Ordinal)
                        ? PresentationTextRole.Hyperlink
                        : PresentationTextRole.Status);
                return;
            case MutationSetRolledBack:
                Add(segments, text, PresentationTextRole.Success);
                return;
            default:
                Add(segments, text, PresentationTextRole.Status);
                return;
        }
    }

    private static void AppendLines(
        IList<PresentationTextSegment> segments,
        string text,
        Func<string, PresentationTextRole> selectRole,
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

    private static PresentationTextRole GetToolCompletionRole(ToolInvocationCompleted completed)
    {
        return completed.Outcome switch
        {
            OperationActivityOutcome.Completed => PresentationTextRole.ToolSuccess,
            OperationActivityOutcome.Failed or OperationActivityOutcome.Cancelled or OperationActivityOutcome.TimedOut => PresentationTextRole.ToolFailure,
            _ => completed.Succeeded ? PresentationTextRole.ToolSuccess : PresentationTextRole.ToolFailure,
        };
    }

    private static PresentationTextRole GetSemanticCheckRole(SemanticCheckOutcome outcome)
    {
        return outcome switch
        {
            SemanticCheckOutcome.Completed or SemanticCheckOutcome.Skipped => PresentationTextRole.ToolSuccess,
            SemanticCheckOutcome.Degraded => PresentationTextRole.Warning,
            SemanticCheckOutcome.Failed or SemanticCheckOutcome.Cancelled => PresentationTextRole.ToolFailure,
            _ => PresentationTextRole.Status,
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

    private static void Add(IList<PresentationTextSegment> segments, string text, PresentationTextRole role)
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

        segments.Add(new PresentationTextSegment(text, role));
    }
}
