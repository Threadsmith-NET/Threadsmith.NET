namespace Threadsmith.Execution;

using System.Buffers;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>One bounded result plus exact structured-projection truncation state.</summary>
internal sealed record DelegateAgentsProjection(DelegateAgentsResult Result, bool IsTruncated);

/// <summary>Immutable host-owned byte bound for delegated structured-result projection.</summary>
internal sealed record DelegateAgentsProjectionLimits
{
    /// <summary>Initializes a new instance of the <see cref="DelegateAgentsProjectionLimits"/> class.</summary>
    public DelegateAgentsProjectionLimits(int maximumStructuredResultBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumStructuredResultBytes);
        MaximumStructuredResultBytes = maximumStructuredResultBytes;
    }

    /// <summary>Gets the maximum serialized structured-result byte count; zero means disabled.</summary>
    public int MaximumStructuredResultBytes { get; }

    /// <summary>Gets the production structured-result projection limit.</summary>
    public static DelegateAgentsProjectionLimits Production { get; } = new(
        DelegateAgentsContract.MaximumStructuredResultBytes);
}

/// <summary>Projects joined child outcomes into a bounded structured result.</summary>
internal sealed class DelegateAgentsResultProjector
{
    private readonly DelegateAgentsProjectionLimits _limits;
    private readonly DelegateAgentsOptions _options;
    private readonly string _implementationOmission;

    /// <summary>Initializes a new instance of the <see cref="DelegateAgentsResultProjector"/> class.</summary>
    public DelegateAgentsResultProjector(DelegateAgentsOptions options, IPromptLoader prompts)
        : this(options, new DelegateAgentsProjectionLimits(options.EffectiveStructuredResultBytes()), prompts)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DelegateAgentsResultProjector"/> class under explicit immutable host bounds.</summary>
    internal DelegateAgentsResultProjector(
        DelegateAgentsOptions options,
        DelegateAgentsProjectionLimits limits,
        IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(prompts);
        _options = options;
        _limits = limits;
        _implementationOmission = ProjectText(
            prompts.Get(PromptFileNames.ToolDelegateAgentsImplementationOmitted).Trim(),
            options.EffectiveLimit(options.MaximumProjectedOmissionCharacters)).Value;
    }

    /// <summary>Creates the complete bounded host result.</summary>
    public DelegateAgentsProjection Project(DelegationPlan plan, DelegationCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(checkpoint);
        var outcomeById = checkpoint.ChildOutcomes
            .Select(outcome => DelegationOutcomeClassifier.Normalize(plan, outcome))
            .ToDictionary(outcome => outcome.AssignmentId);
        var findingsByContent = new Dictionary<string, AgentAssignmentId>(StringComparer.Ordinal);
        ChildProjection[] projections = [.. plan.Assignments.OrderBy(assignment => assignment.Role).Select(assignment =>
        {
            var outcome = outcomeById.TryGetValue(assignment.AssignmentId, out var resolved)
                ? resolved
                : CreateMissingOutcome(plan, assignment);
            var duplicateAssignments = new HashSet<AgentAssignmentId>();
            AgentFinding[] uniqueFindings = [.. (outcome.Findings?.Findings ?? []).Where(finding =>
            {
                var review = outcome.Review?.Findings.FirstOrDefault(item => item.FindingId == finding.FindingId);
                var key = JsonSerializer.Serialize(new
                {
                    finding.Category,
                    finding.Summary,
                    finding.Confidence,
                    finding.Recommendation,
                    finding.Risk,
                    finding.Uncertainty,
                    evidence = finding.EvidenceIds.OrderBy(id => id.Value),
                    locations = finding.Locations.Order(StringComparer.Ordinal),
                    symbols = finding.Symbols.Order(StringComparer.Ordinal),
                    severity = review?.Severity,
                    line = review?.StartLine,
                });
                if (findingsByContent.TryAdd(key, assignment.AssignmentId))
                {
                    return true;
                }

                duplicateAssignments.Add(findingsByContent[key]);
                return false;
            })];
            FindingProjection[] findingProjections = [.. uniqueFindings.Select(finding =>
            {
                var review = outcome.Review?.Findings.FirstOrDefault(item => item.FindingId == finding.FindingId);
                return ProjectFinding(finding, review);
            })];
            DelegateAgentFindingSummary[] candidates =
            [
                .. findingProjections.Select(projection => projection.Value),
            ];
            var omissions = ResolveOmissions(outcome);
            if (duplicateAssignments.Count > 0)
            {
                string[] references = [.. duplicateAssignments.OrderBy(id => id.Value)
                    .Select(id => $"Matching finding details are included under assignment {id.Value:D}.")];
                omissions = omissions with
                {
                    Values = [.. omissions.Values, .. references],
                    TotalCount = omissions.TotalCount + references.Length,
                };
            }

            var summary = ResolveSummary(outcome);
            return new ChildProjection(
                assignment,
                outcome,
                summary.Value,
                summary.IsTruncated,
                candidates,
                uniqueFindings.Length,
                omissions.Values,
                omissions.TotalCount,
                findingProjections.Any(projection => projection.IsTruncated),
                omissions.IsTruncated,
                _implementationOmission);
        })];
        var outcomes = projections.Select(item => item.Outcome).ToArray();
        var disagreementCandidates = DelegateAgentDisagreementDetector.Detect(outcomes);
        var retainedDisagreements = new List<string>();
        var omissions = outcomes
            .Where(outcome => outcome.Status != AgentRunStatus.Completed)
            .Select(outcome => $"Child {outcome.AssignmentId.Value:D} ended with status {outcome.Status}.")
            .ToArray();
        var serializationBuffer = new ArrayBufferWriter<byte>();
        foreach (var projection in projections)
        {
            projection.RetainAll();
        }

        var completeResult = CreateResult(
            plan,
            checkpoint.Phase,
            outcomes,
            projections,
            disagreementCandidates,
            omissions);
        if (Fits(completeResult, serializationBuffer))
        {
            return new DelegateAgentsProjection(
                completeResult,
                projections.Any(projection => projection.HasOmittedContent));
        }

        foreach (var projection in projections)
        {
            projection.ResetRetainedContent();
        }

        var truncated = projections.Any(projection => projection.WasPreTruncated);

        var result = CreateResult(
            plan,
            checkpoint.Phase,
            outcomes,
            projections,
            retainedDisagreements,
            omissions);
        if (!Fits(result, serializationBuffer))
        {
            return new DelegateAgentsProjection(
                CreateStatusOnlyResult(plan, checkpoint.Phase, outcomes, projections),
                true);
        }

        foreach (var projection in projections)
        {
            projection.SummaryRetained = true;
            if (!Fits(
                CreateResult(
                    plan,
                    checkpoint.Phase,
                    outcomes,
                    projections,
                    retainedDisagreements,
                    omissions),
                serializationBuffer))
            {
                projection.SummaryRetained = false;
                truncated = true;
            }
        }

        foreach (var disagreement in disagreementCandidates)
        {
            retainedDisagreements.Add(disagreement);
            if (!Fits(
                CreateResult(
                    plan,
                    checkpoint.Phase,
                    outcomes,
                    projections,
                    retainedDisagreements,
                    omissions),
                serializationBuffer))
            {
                retainedDisagreements.RemoveAt(retainedDisagreements.Count - 1);
                truncated = true;
            }
        }

        foreach (var projection in projections.Where(item => item.Outcome.Implementation is not null))
        {
            projection.ImplementationRetained = true;
            if (!Fits(CreateResult(plan, checkpoint.Phase, outcomes, projections, retainedDisagreements, omissions), serializationBuffer))
            {
                projection.ImplementationRetained = false;
                truncated = true;
            }
        }

        var maximumFindings = projections.Max(projection => projection.Candidates.Count);
        for (var index = 0; index < maximumFindings; index++)
        {
            foreach (var projection in projections.Where(item => index < item.Candidates.Count))
            {
                projection.RetainedFindings.Add(projection.Candidates[index]);
                if (!Fits(
                    CreateResult(
                        plan,
                        checkpoint.Phase,
                        outcomes,
                        projections,
                        retainedDisagreements,
                        omissions),
                    serializationBuffer))
                {
                    projection.RetainedFindings.RemoveAt(projection.RetainedFindings.Count - 1);
                    truncated = true;
                }
            }
        }

        var maximumOmissions = projections.Max(projection => projection.OmissionCandidates.Count);
        for (var index = 0; index < maximumOmissions; index++)
        {
            foreach (var projection in projections.Where(item => index < item.OmissionCandidates.Count))
            {
                projection.RetainedOmissions.Add(projection.OmissionCandidates[index]);
                if (!Fits(
                    CreateResult(
                        plan,
                        checkpoint.Phase,
                        outcomes,
                        projections,
                        retainedDisagreements,
                        omissions),
                    serializationBuffer))
                {
                    projection.RetainedOmissions.RemoveAt(projection.RetainedOmissions.Count - 1);
                    truncated = true;
                }
            }
        }

        result = CreateResult(
            plan,
            checkpoint.Phase,
            outcomes,
            projections,
            retainedDisagreements,
            omissions);
        truncated |= projections.Any(projection => projection.HasOmittedContent)
            || retainedDisagreements.Count != disagreementCandidates.Count;
        return new DelegateAgentsProjection(result, truncated);
    }

    private static DelegateAgentsResult CreateResult(
        DelegationPlan plan,
        DelegationCheckpointPhase phase,
        IReadOnlyList<AgentRunOutcome> outcomes,
        IReadOnlyList<ChildProjection> projections,
        IReadOnlyList<string> disagreements,
        IReadOnlyList<string> omissions)
    {
        return new DelegateAgentsResult(
            plan.DelegationId.Value.ToString("D"),
            DelegationOutcomeClassifier.ResolveStatus(plan, outcomes, phase),
            projections.Select(projection => projection.ToSummary()).ToArray(),
            new DelegationSteeringSummary(0, 0, 0),
            disagreements,
            omissions);
    }

    private static DelegateAgentsResult CreateStatusOnlyResult(
        DelegationPlan plan,
        DelegationCheckpointPhase phase,
        IReadOnlyList<AgentRunOutcome> outcomes,
        IReadOnlyList<ChildProjection> projections)
    {
        return new DelegateAgentsResult(
            plan.DelegationId.Value.ToString("D"),
            DelegationOutcomeClassifier.ResolveStatus(plan, outcomes, phase),
            projections.Select(projection => projection.ToStatusOnlySummary()).ToArray(),
            new DelegationSteeringSummary(0, 0, 0),
            [],
            []);
    }

    private FindingProjection ProjectFinding(AgentFinding finding, ReviewFinding? review)
    {
        var confidence = finding.Confidence switch
        {
            >= 0.8 => "High",
            >= 0.5 => "Medium",
            _ => "Low",
        };
        var detailLimit = _options.EffectiveLimit(_options.MaximumProjectedDetailCharacters);
        var omissionLimit = _options.EffectiveLimit(_options.MaximumProjectedOmissionCharacters);
        var title = ProjectText(finding.Summary, detailLimit);
        var location = ProjectOptionalText(
            finding.Locations.FirstOrDefault(),
            detailLimit);
        var symbol = ProjectOptionalText(
            finding.Symbols.FirstOrDefault(),
            detailLimit);
        var evidence = ProjectText(
            string.Join(',', finding.EvidenceIds.Select(id => id.Value.ToString("D"))),
            detailLimit);
        var uncertainty = ProjectOptionalText(
            finding.Uncertainty,
            omissionLimit);
        var recommendation = ProjectOptionalText(review?.Recommendation, omissionLimit);
        var consequence = ProjectOptionalText(review?.Consequence, omissionLimit);
        var isTruncated = title.IsTruncated
            || location.IsTruncated
            || symbol.IsTruncated
            || evidence.IsTruncated
            || uncertainty.IsTruncated
            || recommendation.IsTruncated
            || consequence.IsTruncated
            || finding.Locations.Count > 1
            || finding.Symbols.Count > 1;
        return new FindingProjection(
            new DelegateAgentFindingSummary(
                title.Value,
                location.Value,
                symbol.Value,
                evidence.Value,
                confidence,
                uncertainty.Value)
            {
                Category = review?.Category,
                Severity = review?.Severity,
                Line = review?.StartLine,
                Recommendation = recommendation.Value,
                Consequence = consequence.Value,
            },
            isTruncated);
    }

    private OmissionProjection ResolveOmissions(AgentRunOutcome outcome)
    {
        string[] values = [.. outcome.Findings is null
            ? outcome.Status == AgentRunStatus.Completed ? [] : [outcome.Reason]
            : outcome.Findings.UnresolvedQuestions.Concat(outcome.Findings.CoverageNotes)];
        var omissionLimit = _options.EffectiveLimit(_options.MaximumProjectedOmissionCharacters);
        TextProjection[] projected =
        [
            .. values.Select(value => ProjectText(value, omissionLimit)),
        ];
        return new OmissionProjection(
            projected.Select(item => item.Value).ToArray(),
            values.Length,
            projected.Any(item => item.IsTruncated));
    }

    private SummaryProjection ResolveSummary(AgentRunOutcome outcome)
    {
        if (outcome.Response is { } response)
        {
            return new SummaryProjection(response, false);
        }

        var summary = outcome.Findings?.Summary;
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = outcome.Findings?.Findings.FirstOrDefault()?.Summary ?? outcome.Reason;
        }

        if (_options.MaximumSummaryCharacters == 0)
        {
            return new SummaryProjection(summary, false);
        }

        var value = BoundedText.Truncate(summary, _options.MaximumSummaryCharacters, out var isTruncated);
        return new SummaryProjection(value, isTruncated);
    }

    private static AgentRunOutcome CreateMissingOutcome(
        DelegationPlan plan,
        AgentAssignment assignment)
    {
        return new AgentRunOutcome
        {
            AssignmentId = assignment.AssignmentId,
            ChildRunId = assignment.ChildRunId,
            Role = assignment.Role,
            Generation = plan.Provenance.Generation,
            Status = AgentRunStatus.Failed,
            Usage = new AgentResourceUsage(),
            Reason = "No terminal child outcome was recorded.",
        };
    }

    private bool Fits(
        DelegateAgentsResult result,
        ArrayBufferWriter<byte> buffer)
    {
        buffer.Clear();
        using var writer = new Utf8JsonWriter(buffer);
        JsonSerializer.Serialize(writer, result);
        writer.Flush();
        return _limits.MaximumStructuredResultBytes == 0
            || buffer.WrittenCount <= _limits.MaximumStructuredResultBytes;
    }

    private static TextProjection ProjectText(string value, int maximumCharacters)
    {
        if (maximumCharacters == 0)
        {
            return new TextProjection(value, false);
        }

        var projected = BoundedText.Truncate(value, maximumCharacters, out var isTruncated);
        return new TextProjection(projected, isTruncated);
    }

    private static OptionalTextProjection ProjectOptionalText(
        string? value,
        int maximumCharacters)
    {
        if (value is null)
        {
            return new OptionalTextProjection(null, false);
        }

        if (maximumCharacters == 0)
        {
            return new OptionalTextProjection(value, false);
        }

        return new OptionalTextProjection(
            BoundedText.Truncate(value, maximumCharacters, out var isTruncated),
            isTruncated);
    }

    private sealed record FindingProjection(DelegateAgentFindingSummary Value, bool IsTruncated);

    private sealed record OmissionProjection(
        IReadOnlyList<string> Values,
        int TotalCount,
        bool IsTruncated);

    private sealed record OptionalTextProjection(string? Value, bool IsTruncated);

    private sealed record SummaryProjection(string Value, bool IsTruncated);

    private sealed record TextProjection(string Value, bool IsTruncated);

    private sealed class ChildProjection
    {
        private readonly string _implementationOmission;

        public ChildProjection(
            AgentAssignment assignment,
            AgentRunOutcome outcome,
            string summary,
            bool summaryWasTruncated,
            IReadOnlyList<DelegateAgentFindingSummary> candidates,
            int totalFindingCount,
            IReadOnlyList<string> omissionCandidates,
            int totalOmissionCount,
            bool findingFieldsWereTruncated,
            bool omissionFieldsWereTruncated,
            string implementationOmission)
        {
            Assignment = assignment;
            Outcome = outcome;
            Summary = summary;
            SummaryWasTruncated = summaryWasTruncated;
            Candidates = candidates;
            TotalFindingCount = totalFindingCount;
            OmissionCandidates = omissionCandidates;
            TotalOmissionCount = totalOmissionCount;
            FindingFieldsWereTruncated = findingFieldsWereTruncated;
            OmissionFieldsWereTruncated = omissionFieldsWereTruncated;
            _implementationOmission = implementationOmission;
        }

        public AgentAssignment Assignment { get; }

        public IReadOnlyList<DelegateAgentFindingSummary> Candidates { get; }

        public bool HasOmittedContent => SummaryWasTruncated
            || (Outcome.Implementation is not null && !ImplementationRetained)
            || FindingFieldsWereTruncated
            || OmissionFieldsWereTruncated
            || !SummaryRetained
            || RetainedFindings.Count != TotalFindingCount
            || RetainedOmissions.Count != TotalOmissionCount;

        public IReadOnlyList<string> OmissionCandidates { get; }

        public bool OmissionFieldsWereTruncated { get; }

        public AgentRunOutcome Outcome { get; }

        public bool FindingFieldsWereTruncated { get; }

        public List<DelegateAgentFindingSummary> RetainedFindings { get; } = [];

        public List<string> RetainedOmissions { get; } = [];

        public string Summary { get; }

        public bool SummaryWasTruncated { get; }

        public bool SummaryRetained { get; set; }

        public bool ImplementationRetained { get; set; }

        public int TotalFindingCount { get; }

        public int TotalOmissionCount { get; }

        public bool WasPreTruncated => SummaryWasTruncated
            || FindingFieldsWereTruncated
            || OmissionFieldsWereTruncated
            || Candidates.Count != TotalFindingCount
            || OmissionCandidates.Count != TotalOmissionCount;

        public void ResetRetainedContent()
        {
            SummaryRetained = false;
            ImplementationRetained = false;
            RetainedFindings.Clear();
            RetainedOmissions.Clear();
        }

        public void RetainAll()
        {
            SummaryRetained = true;
            ImplementationRetained = true;
            RetainedFindings.AddRange(Candidates);
            RetainedOmissions.AddRange(OmissionCandidates);
        }

        public DelegateAgentOutcomeSummary ToSummary()
        {
            var omissions = RetainedOmissions.ToList();
            if (Outcome.Implementation is not null && !ImplementationRetained)
            {
                omissions.Add(_implementationOmission);
            }

            if (!SummaryRetained)
            {
                omissions.Add(ModelVisibleStructuredFact.Exact(
                    "Child summary omitted by the structured output bound."));
            }
            else if (SummaryWasTruncated)
            {
                omissions.Add(ModelVisibleStructuredFact.Exact(
                    "Child summary truncated by the structured output bound."));
            }

            if (FindingFieldsWereTruncated)
            {
                omissions.Add(ModelVisibleStructuredFact.Exact(
                    "One or more finding fields were truncated by their field bounds."));
            }

            if (OmissionFieldsWereTruncated)
            {
                omissions.Add(ModelVisibleStructuredFact.Exact(
                    "One or more omission details were truncated by their field bounds."));
            }

            if (RetainedFindings.Count != TotalFindingCount)
            {
                omissions.Add(ModelVisibleStructuredFact.Exact(
                    $"Finding projection retained {RetainedFindings.Count} of {TotalFindingCount}; "
                        + $"{TotalFindingCount - RetainedFindings.Count} omitted."));
            }

            if (RetainedOmissions.Count != TotalOmissionCount)
            {
                omissions.Add(ModelVisibleStructuredFact.Exact(
                    $"Detail projection retained {RetainedOmissions.Count} of {TotalOmissionCount}; "
                        + $"{TotalOmissionCount - RetainedOmissions.Count} omitted."));
            }

            return CreateSummary(SummaryRetained ? Summary : string.Empty, RetainedFindings, omissions);
        }

        public DelegateAgentOutcomeSummary ToStatusOnlySummary()
        {
            return CreateSummary(string.Empty, [], []);
        }

        private DelegateAgentOutcomeSummary CreateSummary(
            string summary,
            IReadOnlyList<DelegateAgentFindingSummary> findings,
            IReadOnlyList<string> omissions)
        {
            var toolAccess = Assignment.Policy.ToolPolicyVersion.Contains(
                "inherit",
                StringComparison.Ordinal)
                ? "inherit"
                : "readOnly";
            return new DelegateAgentOutcomeSummary(
                Assignment.AssignmentId.Value.ToString("D"),
                Assignment.Role.ToString(),
                toolAccess,
                Outcome.Status.ToString(),
                summary,
                findings,
                omissions,
                new DelegateAgentUsageSummary(
                    Outcome.Usage.ModelTokens,
                    Outcome.Usage.ToolCalls))
            {
                ModelSelection = Outcome.ModelSelection is { } model ? new DelegateAgentModelSummary(
                    model.EffectiveProviderId,
                    model.EffectiveProfileId.Value.ToString("D"),
                    model.EffectiveReasoningLevel,
                    model.Source.ToString(),
                    model.ConfiguredProviderId,
                    model.ConfiguredProfileId?.Value.ToString("D"),
                    model.ConfiguredReasoningLevel,
                    model.FallbackReason) : null,
                Implementation = ImplementationRetained ? Outcome.Implementation : null,
            };
        }
    }
}
