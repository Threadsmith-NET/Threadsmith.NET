namespace Threadsmith.Interaction.Coordination;

using System.Text;
using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;

/// <summary>Owns the ordered conversation text produced from live domain events.</summary>
internal sealed class ConversationTranscript
{
    private readonly TuiResourceLimits _limits;

    private readonly StringBuilder _reasoning = new();
    private readonly bool _showOperationDurations;
    private readonly Func<bool> _inspectCodeExploreOutput;
    private readonly StringBuilder _text;
    private readonly PresentationBlockSpacing _blockSpacing = new();
    private readonly Dictionary<(RunId RunId, int Revision), PlanSanityCheckCompleted> _planSanityChecks = [];
    private readonly Dictionary<(RunId RunId, int Revision), string> _planRiskBases = [];
    private readonly Dictionary<RunId, Dictionary<string, string>> _planStepDetailsByRun = [];
    private readonly Dictionary<MutationSetId, RunId> _mutationSetRuns = [];
    private readonly Dictionary<MutationSetId, Dictionary<string, string>> _planStepDetailsByMutationSet = [];
    private readonly Dictionary<MutationSetId, MutationApprovalLevel> _mutationApprovalLevels = [];
    private readonly Dictionary<(RunId RunId, SemanticCheckId SemanticCheckId), SemanticCheckStarted> _pendingSemanticChecks = [];
    private readonly Dictionary<SemanticRefreshId, LinkedListNode<SemanticRefreshId>> _renderedSemanticRefreshStarts = [];
    private readonly LinkedList<SemanticRefreshId> _renderedSemanticRefreshStartOrder = [];
    private readonly Dictionary<ToolInvocationId, ToolInvocationStarted> _pendingTools = [];
    private RunId? _activeMutationProposalRunId;
    private bool _answerActive;
    private bool _reasoningActive;
    private bool _awaitingFirstResponseBoundary;
    private bool _lastVisibleWasLifecycleBlock;

    /// <summary>Initializes a new instance of the <see cref="ConversationTranscript"/> class.</summary>
    /// <param name="initialText">Previously projected conversation text.</param>
    /// <param name="showOperationDurations">Whether valid authoritative durations are appended.</param>
    /// <param name="inspectCodeExploreOutput">Returns whether code_explore output should be included in future tool blocks.</param>
    /// <param name="limits">Configured transcript and presentation limits.</param>
    internal ConversationTranscript(
        string initialText,
        bool showOperationDurations = true,
        Func<bool>? inspectCodeExploreOutput = null,
        TuiResourceLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(initialText);
        _limits = limits ?? new();
        _limits.Validate();
        _text = new StringBuilder(initialText);
        _blockSpacing.Observe(initialText);
        _showOperationDurations = showOperationDurations;
        _inspectCodeExploreOutput = inspectCodeExploreOutput ?? (() => false);
    }

    /// <summary>Gets the latest completed or active reasoning text.</summary>
    internal string LatestReasoning => _reasoning.ToString();

    /// <summary>Gets the complete conversation text.</summary>
    internal string Text => _text.ToString();

    /// <summary>Applies one event through the single conversation-append boundary.</summary>
    /// <param name="domainEvent">Event to project into the conversation.</param>
    /// <param name="toolProgress">Final progress entries for a completed tool invocation.</param>
    /// <returns>True when the visible transcript changed.</returns>
    internal bool Apply(IDomainEvent domainEvent, IReadOnlyList<PresentationTextSegment>? toolProgress = null)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        switch (domainEvent)
        {
            case TaskIntentRecorded:
                _answerActive = false;
                _reasoningActive = false;
                _awaitingFirstResponseBoundary = true;
                _lastVisibleWasLifecycleBlock = false;
                _reasoning.Clear();
                return false;
            case ModelReasoningObserved reasoning:
                if (!_reasoningActive)
                {
                    _reasoning.Clear();
                    _answerActive = false;
                    _reasoningActive = true;
                }

                _reasoning.Append(reasoning.Text);
                return false;
            case ModelOutputObserved output:
                if (!_answerActive && string.IsNullOrWhiteSpace(output.Text))
                {
                    return false;
                }

                if (!_answerActive)
                {
                    _reasoningActive = false;
                    _answerActive = true;
                    _awaitingFirstResponseBoundary = false;
                }

                AppendText(output.Text);
                _lastVisibleWasLifecycleBlock = false;
                return true;
            case ToolInvocationStarted started:
                _reasoningActive = false;
                _answerActive = false;
                _pendingTools[started.ToolInvocationId] = started;
                return false;
            case ToolInvocationCompleted completed when _pendingTools.Remove(completed.ToolInvocationId, out var toolStart):
                AppendToolCompletion(toolStart, completed, toolProgress);
                _answerActive = false;
                return true;
            case SemanticCheckStarted started:
                _reasoningActive = false;
                _answerActive = false;
                _pendingSemanticChecks[CreateSemanticCheckKey(started)] = started;
                return false;
            case SemanticCheckCompleted completed:
                var key = CreateSemanticCheckKey(completed);
                var matchedStart = _pendingSemanticChecks.Remove(key, out var pending)
                    ? pending
                    : CreateSyntheticSemanticCheckStart(completed);
                AppendSemanticCheckCompletion(matchedStart, completed);
                _answerActive = false;
                return true;
            case RunCompleted completed:
                foreach (var id in _pendingTools.Where(pair => pair.Value.RunId == completed.RunId).Select(pair => pair.Key).ToArray())
                {
                    _pendingTools.Remove(id);
                }

                RemovePendingSemanticChecks(completed.RunId);
                RemoveRunCorrelationState(completed.RunId);
                _answerActive = false;
                _reasoningActive = false;
                _awaitingFirstResponseBoundary = false;
                if (_lastVisibleWasLifecycleBlock)
                {
                    return false;
                }

                AppendText(Environment.NewLine);
                AppendText(Environment.NewLine);
                return true;
            case ModelFallbackSelected fallback:
                AppendSystemResponse(
                    "The selected model could not satisfy this request. Switched to fallback model "
                    + $"'{fallback.SelectedModelName}' ({fallback.SelectedProviderId}); it is now the active model"
                    + (fallback.Persisted
                        ? "."
                        : ". Repository selection was not persisted: "
                            + (fallback.PersistenceDiagnostic ?? "No persistence diagnostic was available.")));
                return true;
            case RepositoryOpened repository:
                AppendSystemResponse(
                    $"Repository opened.\nRepository: {repository.Path}\nTrust: {repository.TrustLevel}");
                return true;
            case SolutionLoaded solution:
                AppendSystemResponse(
                    $"Solution: {solution.Path}\nTarget frameworks: "
                    + string.Join(", ", solution.TargetFrameworks ?? []));
                return true;
            case SemanticConfidenceChanged confidence:
                AppendSystemResponse($"Semantic confidence: {confidence.Confidence}");
                return true;
            case SemanticLoadCompleted completion when string.Equals(
                completion.Confidence,
                nameof(SemanticConfidenceLevel.None),
                StringComparison.Ordinal):
                AppendSystemResponse("Semantic confidence: Unavailable");
                return true;
            case SemanticRefreshStarted started when IsVisibleSemanticRefreshStart(started.Reason):
                AppendLifecycleBlock(FormatSemanticRefreshStart(started.Reason));
                TrackRenderedSemanticRefreshStart(started.RefreshId);
                return true;
            case SemanticRefreshCompleted completed:
                return AppendSemanticRefreshCompletion(completed);
            case SemanticRefreshFailed failed:
                return AppendSemanticRefreshFailure(failed);
            case PlanSanityCheckCompleted completed:
                _planSanityChecks[(completed.RunId, completed.Revision)] = completed;
                return false;
            case PlanProposed proposed when proposed.Plan is not null:
                RecordPlanRiskBasis(proposed);
                RecordPlanStepDetails(proposed);
                AppendLifecycleBlock(InteractionPresentationFormatter.FormatPlanProposal(proposed));
                return true;
            case PlanRevisionRequested revision:
                AppendSystemResponse($"Plan revision requested: {revision.Instructions}");
                return true;
            case PlanAutoApproved approved:
                AppendLifecycleBlock(InteractionPresentationFormatter.FormatPlanAutoApproval(
                    approved,
                    GetPlanRiskBasis(approved)));
                return true;
            case MutationProposalStarted started:
                _activeMutationProposalRunId = started.RunId;
                AppendLifecycleBlock(InteractionPresentationFormatter.FormatMutationProposalStarted(started));
                return true;
            case MutationProposalRepairAttempted repair:
                _activeMutationProposalRunId = repair.RunId;
                AppendLifecycleBlock(InteractionPresentationFormatter.FormatMutationProposalRepairAttempt(repair));
                return true;
            case ModelCorrectionAttempted correction:
                _activeMutationProposalRunId = correction.RunId;
                AppendLifecycleBlock(InteractionPresentationFormatter.FormatModelCorrectionAttempt(correction));
                return true;
            case MutationSetProposed proposed:
                _mutationApprovalLevels[proposed.MutationSetId] = proposed.RequiredApproval;
                _planStepDetailsByMutationSet[proposed.MutationSetId] = CreateMutationSetPlanStepDetails();
                if (_activeMutationProposalRunId is { } proposalRunId)
                {
                    _mutationSetRuns[proposed.MutationSetId] = proposalRunId;
                }

                if (proposed.Preview is null)
                {
                    return false;
                }

                AppendSystemResponse(
                    $"Mutation preview ({proposed.Preview.AddedLines} added, "
                    + $"{proposed.Preview.RemovedLines} removed):\n"
                    + InteractionPresentationFormatter.FormatUnifiedDiffForDisplay(proposed.Preview.UnifiedDiff)
                    + (proposed.RequiredApproval == MutationApprovalLevel.PolicyAutoApproved
                        ? string.Empty
                        : "Choose apply or discard at the mutation review prompt."));
                return true;
            case MutationApplied applied:
                AppendLifecycleBlock(InteractionPresentationFormatter.FormatMutationApplied(
                    applied,
                    GetMutationApprovalLevel(applied),
                    GetPlanStepDetail(applied)));
                return true;
            case MutationSetRolledBack rolledBack:
                RemoveMutationSetCorrelationState(rolledBack.MutationSetId);
                AppendSystemResponse(
                    $"Mutation set rolled back; restored {rolledBack.RestoredFiles.Count} files.");
                return true;
            default:
                return false;
        }
    }

    private void RecordPlanRiskBasis(PlanProposed proposed)
    {
        if (proposed.Plan is null)
        {
            return;
        }

        var key = (proposed.RunId, proposed.Plan.Revision);
        var basis = CreatePlanRiskBasis(
            proposed.Plan.Risks.Count,
            _planSanityChecks.TryGetValue(key, out var sanity) ? sanity : null);
        if (!string.IsNullOrWhiteSpace(basis))
        {
            _planRiskBases[key] = basis;
        }
    }

    private void RecordPlanStepDetails(PlanProposed proposed)
    {
        if (proposed.Plan is null)
        {
            return;
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in proposed.Plan.Steps)
        {
            foreach (var path in step.GetAffectedPaths())
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    details[path] = step.ExpectedOutcome;
                }
            }
        }

        _planStepDetailsByRun[proposed.RunId] = details;
    }

    private string? GetPlanRiskBasis(PlanAutoApproved approved)
    {
        return _planRiskBases.TryGetValue((approved.RunId, approved.Revision), out var basis)
            ? basis
            : null;
    }

    private Dictionary<string, string> CreateMutationSetPlanStepDetails()
    {
        return _activeMutationProposalRunId is { } runId
            && _planStepDetailsByRun.TryGetValue(runId, out var details)
                ? new Dictionary<string, string>(details, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private MutationApprovalLevel? GetMutationApprovalLevel(MutationApplied applied)
    {
        return _mutationApprovalLevels.TryGetValue(applied.MutationSetId, out var requiredApproval)
            ? requiredApproval
            : null;
    }

    private string? GetPlanStepDetail(MutationApplied applied)
    {
        return applied.RelativePath is not null
            && _planStepDetailsByMutationSet.TryGetValue(applied.MutationSetId, out var detailsByPath)
            && detailsByPath.TryGetValue(applied.RelativePath, out var detail)
                ? detail
                : null;
    }

    private void RemoveRunCorrelationState(RunId runId)
    {
        foreach ((var pendingRunId, var revision) in _planSanityChecks.Keys.ToArray())
        {
            if (pendingRunId == runId)
            {
                _planSanityChecks.Remove((pendingRunId, revision));
            }
        }

        foreach ((var pendingRunId, var revision) in _planRiskBases.Keys.ToArray())
        {
            if (pendingRunId == runId)
            {
                _planRiskBases.Remove((pendingRunId, revision));
            }
        }

        _planStepDetailsByRun.Remove(runId);
        foreach ((var mutationSetId, var mutationRunId) in _mutationSetRuns.ToArray())
        {
            if (mutationRunId == runId)
            {
                RemoveMutationSetCorrelationState(mutationSetId);
            }
        }

        if (_activeMutationProposalRunId == runId)
        {
            _activeMutationProposalRunId = null;
        }
    }

    private void RemoveMutationSetCorrelationState(MutationSetId mutationSetId)
    {
        _mutationSetRuns.Remove(mutationSetId);
        _planStepDetailsByMutationSet.Remove(mutationSetId);
        _mutationApprovalLevels.Remove(mutationSetId);
    }

    private static string CreatePlanRiskBasis(int declaredRiskCount, PlanSanityCheckCompleted? sanity)
    {
        var parts = new List<string>();
        if (declaredRiskCount > 0)
        {
            parts.Add(declaredRiskCount == 1
                ? "model declared 1 risk"
                : $"model declared {declaredRiskCount} risks");
        }

        if (sanity is { IssueCount: > 0 })
        {
            parts.Add(sanity.IssueCount == 1
                ? "sanity checks reported 1 issue"
                : $"sanity checks reported {sanity.IssueCount} issues");
        }

        if (parts.Count > 0 && sanity is { AffectedFileCount: > 0 })
        {
            parts.Add(sanity.AffectedFileCount == 1
                ? "1 file affected"
                : $"{sanity.AffectedFileCount} files affected");
        }

        return string.Join("; ", parts);
    }

    private void AppendSystemResponse(string response)
    {
        AppendLifecycleBlock("Threadsmith: " + response + Environment.NewLine);
    }

    private void AppendLifecycleBlock(string text)
    {
        EnsureEventPresentationBoundary();
        AppendText(text);
        _lastVisibleWasLifecycleBlock = true;
    }

    private void AppendToolCompletion(
        ToolInvocationStarted started,
        ToolInvocationCompleted completed,
        IReadOnlyList<PresentationTextSegment>? progress)
    {
        AppendLifecycleBlock(InteractionPresentationFormatter.FormatToolCompletion(
            started,
            completed,
            _showOperationDurations,
            _inspectCodeExploreOutput(),
            progress,
            _limits.MaximumToolInspectionCharacters));
    }

    private void AppendSemanticCheckCompletion(
        SemanticCheckStarted started,
        SemanticCheckCompleted completed)
    {
        AppendLifecycleBlock(InteractionPresentationFormatter.FormatSemanticCheckCompletion(
            started,
            completed,
            _showOperationDurations));
    }

    private bool AppendSemanticRefreshCompletion(SemanticRefreshCompleted completed)
    {
        var startWasRendered = CompleteSemanticRefreshStartCorrelation(
            completed.RefreshId,
            completed.Reason);
        if (completed.Reason == SemanticRefreshReason.HostMutation && !startWasRendered)
        {
            return false;
        }

        AppendLifecycleBlock(FormatSemanticRefreshCompletion(completed));
        return true;
    }

    private bool AppendSemanticRefreshFailure(SemanticRefreshFailed failed)
    {
        _ = CompleteSemanticRefreshStartCorrelation(failed.RefreshId, failed.Reason);
        AppendLifecycleBlock(FormatSemanticRefreshFailure(failed));
        return true;
    }

    private bool CompleteSemanticRefreshStartCorrelation(
        SemanticRefreshId refreshId,
        SemanticRefreshReason terminalReason)
    {
        var startWasRendered = RemoveRenderedSemanticRefreshStart(refreshId);
        if (!startWasRendered && IsVisibleSemanticRefreshStart(terminalReason))
        {
            AppendLifecycleBlock(FormatSemanticRefreshStart(terminalReason));
        }

        return startWasRendered;
    }

    private static bool IsVisibleSemanticRefreshStart(SemanticRefreshReason reason)
    {
        return reason is SemanticRefreshReason.ExternalChange or SemanticRefreshReason.Recovery;
    }

    private static string FormatSemanticRefreshStart(SemanticRefreshReason reason)
    {
        return reason == SemanticRefreshReason.Recovery
            ? "External changes require semantic recovery; updating semantic model..." + Environment.NewLine
            : "External changes detected; updating semantic model..." + Environment.NewLine;
    }

    private string FormatSemanticRefreshCompletion(SemanticRefreshCompleted completed)
    {
        var fileText = completed.ChangedFileCount == 1
            ? "1 file"
            : $"{completed.ChangedFileCount} files";
        var duration = _showOperationDurations
            && OperationDurationFormatter.TryFormat(completed.ElapsedMilliseconds, out var formatted)
                ? $", {formatted}"
                : string.Empty;
        var confidence = completed.Reason == SemanticRefreshReason.Manual
            || completed.Confidence != SemanticConfidenceLevel.FullSemantic
                ? $"; confidence {completed.Confidence}"
                : string.Empty;
        return $"Semantic model updated ({fileText}{duration}{confidence}).{Environment.NewLine}";
    }

    private string FormatSemanticRefreshFailure(SemanticRefreshFailed failed)
    {
        var duration = _showOperationDurations
            && OperationDurationFormatter.TryFormat(failed.ElapsedMilliseconds, out var formatted)
                ? $" after {formatted}"
                : string.Empty;
        return $"Semantic model refresh failed ({failed.FailureKind}){duration}: "
            + failed.SafeReason
            + Environment.NewLine;
    }

    private void TrackRenderedSemanticRefreshStart(SemanticRefreshId refreshId)
    {
        if (_renderedSemanticRefreshStarts.ContainsKey(refreshId))
        {
            return;
        }

        var node = _renderedSemanticRefreshStartOrder.AddLast(refreshId);
        _renderedSemanticRefreshStarts.Add(refreshId, node);
        if (_renderedSemanticRefreshStarts.Count <= _limits.MaximumTrackedRefreshStarts)
        {
            return;
        }

        var oldest = _renderedSemanticRefreshStartOrder.First;
        if (oldest is not null)
        {
            _renderedSemanticRefreshStartOrder.RemoveFirst();
            _renderedSemanticRefreshStarts.Remove(oldest.Value);
        }
    }

    private bool RemoveRenderedSemanticRefreshStart(SemanticRefreshId refreshId)
    {
        if (!_renderedSemanticRefreshStarts.Remove(refreshId, out var node))
        {
            return false;
        }

        _renderedSemanticRefreshStartOrder.Remove(node);
        return true;
    }

    private void RemovePendingSemanticChecks(RunId runId)
    {
        foreach ((var pendingRunId, var pendingCheckId) in _pendingSemanticChecks.Keys.ToArray())
        {
            if (pendingRunId == runId)
            {
                _pendingSemanticChecks.Remove((pendingRunId, pendingCheckId));
            }
        }
    }

    private static (RunId RunId, SemanticCheckId SemanticCheckId) CreateSemanticCheckKey(
        SemanticCheckStarted started)
    {
        return (started.RunId, started.SemanticCheckId);
    }

    private static (RunId RunId, SemanticCheckId SemanticCheckId) CreateSemanticCheckKey(
        SemanticCheckCompleted completed)
    {
        return (completed.RunId, completed.SemanticCheckId);
    }

    private static SemanticCheckStarted CreateSyntheticSemanticCheckStart(SemanticCheckCompleted completed)
    {
        return new SemanticCheckStarted(
            completed.SessionId,
            completed.OccurredAt,
            completed.RunId,
            completed.SemanticCheckId,
            completed.Phase,
            completed.CheckName);
    }

    private void EnsureEventPresentationBoundary()
    {
        AppendText(_blockSpacing.BeforeBlock(_awaitingFirstResponseBoundary));
        _awaitingFirstResponseBoundary = false;
    }

    private void AppendText(string text)
    {
        _text.Append(text);
        _blockSpacing.Observe(text);
    }
}
