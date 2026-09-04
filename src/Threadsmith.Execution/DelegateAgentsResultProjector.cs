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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumStructuredResultBytes);
        MaximumStructuredResultBytes = maximumStructuredResultBytes;
    }

    /// <summary>Gets the maximum serialized structured-result byte count.</summary>
    public int MaximumStructuredResultBytes { get; }

    /// <summary>Gets the production structured-result projection limit.</summary>
    public static DelegateAgentsProjectionLimits Production { get; } = new(
        DelegateAgentsContract.MaximumStructuredResultBytes);
}

/// <summary>Projects joined child outcomes into a bounded structured result.</summary>
internal sealed class DelegateAgentsResultProjector
{
    private const int MaximumEvidenceCharacters = 2_048;
    private const int MaximumFindingTitleCharacters = 1_024;
    private const int MaximumLocationCharacters = 1_024;
    private const int MaximumOmissionCharacters = 512;
    private const int MaximumProjectedSummaryCharacters = 1_024;
    private const int MaximumSymbolCharacters = 1_024;
    private readonly DelegateAgentsProjectionLimits _limits;
    private readonly DelegateAgentsOptions _options;

    /// <summary>Initializes a new instance of the <see cref="DelegateAgentsResultProjector"/> class.</summary>
    public DelegateAgentsResultProjector(DelegateAgentsOptions options)
        : this(options, DelegateAgentsProjectionLimits.Production)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="DelegateAgentsResultProjector"/> class under explicit immutable host bounds.</summary>
    internal DelegateAgentsResultProjector(
        DelegateAgentsOptions options,
        DelegateAgentsProjectionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(limits);
        _options = options;
        _limits = limits;
    }

    /// <summary>Creates the complete bounded host result.</summary>
    public DelegateAgentsProjection Project(DelegationPlan plan, DelegationCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(checkpoint);
        var outcomeById = checkpoint.ChildOutcomes
            .Select(outcome => DelegationOutcomeClassifier.Normalize(plan, outcome))
            .ToDictionary(outcome => outcome.AssignmentId);
        ChildProjection[] projections = [.. plan.Assignments.Select(assignment =>
        {
            var outcome = outcomeById.TryGetValue(assignment.AssignmentId, out var resolved)
                ? resolved
                : CreateMissingOutcome(plan, assignment);
            FindingProjection[] findingProjections = [.. outcome.Findings?.Findings
                .Select(ProjectFinding)
                ?? []];
            DelegateAgentFindingSummary[] candidates =
            [
                .. findingProjections.Select(projection => projection.Value),
            ];
            var omissions = ResolveOmissions(outcome);
            var summary = ResolveSummary(outcome);
            return new ChildProjection(
                assignment,
                outcome,
                summary.Value,
                summary.IsTruncated,
                candidates,
                outcome.Findings?.Findings.Count ?? 0,
                omissions.Values,
                omissions.TotalCount,
                findingProjections.Any(projection => projection.IsTruncated),
                omissions.IsTruncated);
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

    private static FindingProjection ProjectFinding(AgentFinding finding)
    {
        var confidence = finding.Confidence switch
        {
            >= 0.8 => "High",
            >= 0.5 => "Medium",
            _ => "Low",
        };
        var title = ProjectText(finding.Summary, MaximumFindingTitleCharacters);
        var location = ProjectOptionalText(
            finding.Locations.FirstOrDefault(),
            MaximumLocationCharacters);
        var symbol = ProjectOptionalText(
            finding.Symbols.FirstOrDefault(),
            MaximumSymbolCharacters);
        var evidence = ProjectText(
            string.Join(',', finding.EvidenceIds.Select(id => id.Value.ToString("D"))),
            MaximumEvidenceCharacters);
        var uncertainty = ProjectOptionalText(
            finding.Uncertainty,
            MaximumOmissionCharacters);
        var isTruncated = title.IsTruncated
            || location.IsTruncated
            || symbol.IsTruncated
            || evidence.IsTruncated
            || uncertainty.IsTruncated
            || finding.Locations.Count > 1
            || finding.Symbols.Count > 1;
        return new FindingProjection(
            new DelegateAgentFindingSummary(
                title.Value,
                location.Value,
                symbol.Value,
                evidence.Value,
                confidence,
                uncertainty.Value),
            isTruncated);
    }

    private static OmissionProjection ResolveOmissions(AgentRunOutcome outcome)
    {
        string[] values = [.. outcome.Findings is null
            ? outcome.Status == AgentRunStatus.Completed ? [] : [outcome.Reason]
            : outcome.Findings.UnresolvedQuestions.Concat(outcome.Findings.CoverageNotes)];
        TextProjection[] projected =
        [
            .. values.Select(value => ProjectText(value, MaximumOmissionCharacters)),
        ];
        return new OmissionProjection(
            projected.Select(item => item.Value).ToArray(),
            values.Length,
            projected.Any(item => item.IsTruncated));
    }

    private SummaryProjection ResolveSummary(AgentRunOutcome outcome)
    {
        var summary = outcome.Findings?.Summary;
        if (string.IsNullOrWhiteSpace(summary))
        {
            summary = outcome.Findings?.Findings.FirstOrDefault()?.Summary ?? outcome.Reason;
        }

        var value = BoundedText.Truncate(
            summary,
            Math.Min(_options.MaximumSummaryCharacters, MaximumProjectedSummaryCharacters),
            out var isTruncated);
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
        return buffer.WrittenCount <= _limits.MaximumStructuredResultBytes;
    }

    private static TextProjection ProjectText(string value, int maximumCharacters)
    {
        var projected = BoundedText.Truncate(value, maximumCharacters, out var isTruncated);
        return new TextProjection(projected, isTruncated);
    }

    private static OptionalTextProjection ProjectOptionalText(
        string? value,
        int maximumCharacters)
    {
        return value is null
            ? new OptionalTextProjection(null, false)
            : new OptionalTextProjection(
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
            bool omissionFieldsWereTruncated)
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
        }

        public AgentAssignment Assignment { get; }

        public IReadOnlyList<DelegateAgentFindingSummary> Candidates { get; }

        public bool HasOmittedContent => SummaryWasTruncated
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
            RetainedFindings.Clear();
            RetainedOmissions.Clear();
        }

        public void RetainAll()
        {
            SummaryRetained = true;
            RetainedFindings.AddRange(Candidates);
            RetainedOmissions.AddRange(OmissionCandidates);
        }

        public DelegateAgentOutcomeSummary ToSummary()
        {
            var omissions = RetainedOmissions.ToList();
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
                    Outcome.Usage.ToolCalls));
        }
    }
}
