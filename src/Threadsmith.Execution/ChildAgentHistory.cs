namespace Threadsmith.Execution;

using System.Diagnostics;
using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Keeps exact child instructions and recent exchanges while replacing older exchanges atomically.</summary>
internal sealed class ChildAgentHistory
{
    private static readonly ActivitySource Activities = new("Threadsmith.Execution.ChildAgentCompaction");
    private readonly List<ActiveTurnContinuationGroup> _groups = [];
    private readonly List<ActiveTurnSourceReference> _archivedSources = [];
    private readonly ChildAgentCompactionOptions _options;
    private readonly IPromptLoader _prompts;
    private readonly ActiveTurnCompactionCandidateProfile? _compactionProfile;
    private ActiveTurnCompactionSummary? _summary;
    private ModelMessage? _summaryMessage;
    private ModelMessage? _indexMessage;
    private long _sequence;
    private int? _lastAttemptRound;

    /// <summary>Initializes a new instance of the <see cref="ChildAgentHistory"/> class.</summary>
    public ChildAgentHistory(
        List<ModelMessage> messages,
        ChildAgentCompactionOptions options,
        IPromptLoader prompts,
        ActiveTurnCompactionCandidateProfile? compactionProfile = null)
    {
        Messages = messages;
        _options = options;
        _prompts = prompts;
        _compactionProfile = compactionProfile;
    }

    /// <summary>The active request messages, including the unchanged initial instructions.</summary>
    public List<ModelMessage> Messages { get; }

    /// <summary>Provider continuation invalidation generation.</summary>
    public long RewriteGeneration { get; private set; }

    /// <summary>Records an entire completed tool batch and its progress or correction message.</summary>
    public void RecordExchange(int start, int round, IReadOnlyList<EvidenceId> evidenceIds)
    {
        var messages = Messages.Skip(start).ToArray();
        RecordGroup(messages, round, evidenceIds);
    }

    /// <summary>Includes inherited evidence in history without making it eligible before delivery.</summary>
    public void RecordInitialEvidence(IReadOnlyList<EvidenceId> evidenceIds)
    {
        if (evidenceIds.Count > 0)
        {
            var message = Messages.Single(message => message.SectionId == "child-initial-evidence");
            RecordGroup([message], 0, evidenceIds);
        }
    }

    /// <summary>Marks complete exchanges as seen by the child model.</summary>
    public void MarkDelivered()
    {
        for (var index = 0; index < _groups.Count; index++)
        {
            _groups[index] = _groups[index] with { WasDeliveredVerbatim = true };
        }
    }

    /// <summary>Attempts a smaller request without changing any history on failure.</summary>
    public async Task CompactAsync(
        AgentAssignment assignment,
        AgentModelSelection model,
        ModelWireToolEstimate tools,
        int round,
        IActiveTurnCompactor compactor,
        IActiveTurnCompactionAttemptObserver observer,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled || !_options.Summary.Enabled
            || (_lastAttemptRound is { } last && round - last < _options.MinimumRoundsBetweenAttempts))
        {
            return;
        }

        int Estimate(IReadOnlyList<ModelMessage> messages) => ModelWireEstimator.Estimate(
            messages, tools, 0, model.OutputReserveTokens, model.ProviderInstructions).WireInputTokens;
        var before = Estimate(Messages);
        var capacity = model.ContextWindowTokens - model.OutputReserveTokens;
        if (!(_options.TriggerTokens > 0 && before >= _options.TriggerTokens)
            && !(_options.TriggerPercent > 0 && before >= (long)capacity * _options.TriggerPercent / 100))
        {
            return;
        }

        var prefix = ActiveTurnCompactionCutSelector.SelectEligiblePrefix(
            _groups, _options.Summary, Math.Max(1, _options.RecentTokens));
        if (prefix.Count == 0 || prefix.Sum(group => (long)group.EstimatedTokens) <= _options.MinimumSavingsTokens)
        {
            return;
        }

        _lastAttemptRound = round;
        using var activity = Activities.StartActivity("child.compact");
        activity?.SetTag("threadsmith.run.id", assignment.ChildRunId.Value.ToString("D"));
        activity?.SetTag("threadsmith.compaction.before_input_tokens", before);
        try
        {
            var task = ActiveTurnTaskContextProjector.Project(new TaskSpecification(assignment.Objective, []), _options.Summary);
            var request = new ActiveTurnCompactionRequest
            {
                RunId = assignment.ChildRunId,
                IncludeFileLists = false,
                ProfileId = model.ProfileId,
                CandidateProfile = _compactionProfile,
                WorkloadClass = assignment.Role switch
                {
                    AgentRole.Explorer => WorkloadClass.General,
                    AgentRole.Implementer => WorkloadClass.CodeEdit,
                    _ => WorkloadClass.Review,
                },
                ReasoningLevel = model.ReasoningLevel,
                ProviderInstructions = model.ProviderInstructions,
                ToolContinuationRound = round,
                FrozenContextIdentity = assignment.AssignmentId.Value.ToString("D"),
                TaskObjective = task.Objective,
                TaskObjectiveWasTruncated = task.ObjectiveWasTruncated,
                TaskContext = Messages.FirstOrDefault(message => message.SectionId == "child-assignment")?.GetModelVisibleContent(),
                AcceptanceIntent = task.AcceptanceIntent,
                PriorSummary = _summary,

                // The child index retains every ID. The generator needs only the sanitized exchanges,
                // not a redundant provenance inventory with independent metadata limits.
                EligiblePrefix = prefix.Select(group => group with { Sources = [] }).ToArray(),
                SelectionConstraints = new ModelSelectionConstraints { ContainsSensitiveData = true },
                ContainsSensitiveData = true,
                ProfileContextWindowTokens = model.ContextWindowTokens,
                ProfileOutputReserveTokens = Math.Min(_options.Summary.SummaryBudgetTokens, Math.Min(model.OutputReserveTokens, model.MaximumOutputTokens)),
                BeforeInputTokens = before,
                PressureTargetTokens = _options.TargetTokens,
            };
            var result = await compactor.CompactAsync(request, observer, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            activity?.SetTag("threadsmith.compaction.outcome", result.Outcome.ToString());
            if (result.Summary is not { } summary)
            {
                return;
            }

            var selected = prefix.TakeWhile(group => group.Sequence <= summary.ThroughGroupSequence).ToArray();
            if (selected.Length == 0
                || !summary.CoveredGroupSequences.SequenceEqual(
                    (_summary?.CoveredGroupSequences ?? []).Concat(selected.Select(group => group.Sequence))))
            {
                return;
            }

            var sources = _archivedSources.Concat(selected.SelectMany(group => group.Sources)).Distinct().ToArray();
            var summaryMessage = ActiveTurnSummaryFormatter.CreateMessage(summary.Version, summary.Content, _prompts);
            var indexMessage = new ModelMessage
            {
                Role = ModelMessageRole.Assistant,
                SectionId = "child-evidence-index",
                Content =
                [
                    new ModelContentPart
                    {
                        Kind = ModelContentPartKind.Json,
                        Content = JsonSerializer.Serialize(new { archivedEvidenceIds = sources.Select(source => source.Id).Distinct() }),
                    },
                ],
            };
            var removed = new HashSet<ModelMessage>(selected.SelectMany(group => group.Messages), ReferenceEqualityComparer.Instance);
            if (_summaryMessage is not null)
            {
                removed.Add(_summaryMessage);
            }

            if (_indexMessage is not null)
            {
                removed.Add(_indexMessage);
            }

            var replacement = new List<ModelMessage>();
            var inserted = false;
            foreach (var message in Messages)
            {
                if (!removed.Contains(message))
                {
                    replacement.Add(message);
                }
                else if (!inserted)
                {
                    replacement.Add(summaryMessage);
                    replacement.Add(indexMessage);
                    inserted = true;
                }
            }

            var after = Estimate(replacement);
            activity?.SetTag("threadsmith.compaction.after_input_tokens", after);
            if (before - after < Math.Max(1, _options.MinimumSavingsTokens))
            {
                activity?.SetTag("threadsmith.compaction.outcome", "insufficient_savings");
                return;
            }

            Messages.Clear();
            Messages.AddRange(replacement);
            _groups.RemoveRange(0, selected.Length);
            _archivedSources.Clear();
            _archivedSources.AddRange(sources);
            _summary = summary;
            _summaryMessage = summaryMessage;
            _indexMessage = indexMessage;
            RewriteGeneration++;
            activity?.SetTag("threadsmith.compaction.outcome", "activated");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Compaction is an optimization. Failed attempts leave every original message active.
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
        }
    }

    private void RecordGroup(IReadOnlyList<ModelMessage> messages, int round, IReadOnlyList<EvidenceId> evidenceIds)
    {
        var sequence = ++_sequence;
        _groups.Add(new ActiveTurnContinuationGroup
        {
            Sequence = sequence,
            CompletedModelRound = round,
            Messages = messages,
            Sources = evidenceIds.Select(id => new ActiveTurnSourceReference(
                ActiveTurnSourceKind.Evidence, id.Value.ToString("D"), sequence)).ToArray(),
            FilesRead = [],
            FilesChanged = [],
            EstimatedTokens = ModelWireEstimator.Estimate(messages, [], ToolTransportMode.Native, 0, 0).WireInputTokens,
            Sensitivity = ConversationSensitivity.Sensitive,
        });
    }
}
