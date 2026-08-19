namespace Threadsmith.Context;

using Threadsmith.Core;

/// <summary>Bounded child context assembled without parent or sibling transcripts.</summary>
public sealed record AgentContextSnapshot
{
    /// <summary>Owning assignment.</summary>
    public required AgentAssignmentId AssignmentId { get; init; }

    /// <summary>Immutable baseline identity.</summary>
    public required string BaselineIdentity { get; init; }

    /// <summary>Host-approved objective.</summary>
    public required string Objective { get; init; }

    /// <summary>Explicit tasks.</summary>
    public IReadOnlyList<string> Tasks { get; init; } = [];

    /// <summary>Bounded relevant governed evidence.</summary>
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];

    /// <summary>Explicit tool definitions eligible for this child.</summary>
    public IReadOnlyList<string> AllowedToolIds { get; init; } = [];

    /// <summary>Estimated context tokens.</summary>
    public int EstimatedTokens { get; init; }
}

/// <summary>Assembles narrow role-specific child context from immutable governed evidence.</summary>
public sealed class AgentContextAssembler
{
    private readonly IEvidenceStore _evidence;

    /// <summary>Initializes a new instance of the <see cref="AgentContextAssembler"/> class.</summary>
    public AgentContextAssembler(IEvidenceStore evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        _evidence = evidence;
    }

    /// <summary>Builds a bounded snapshot containing no raw conversation transcript.</summary>
    public AgentContextSnapshot Assemble(
        DelegationPlan plan,
        AgentAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(assignment);
        if (!plan.Assignments.Any(item => item.AssignmentId == assignment.AssignmentId))
        {
            throw new UnauthorizedAccessException("The assignment does not belong to this delegation.");
        }

        var maximumTokens = (int)Math.Min(assignment.Budget.ModelTokens, int.MaxValue);
        var used = TokenEstimator.Estimate(assignment.Objective)
            + assignment.Tasks.Sum(TokenEstimator.Estimate);
        var selected = new List<Evidence>();
        foreach (Evidence evidence in _evidence.Snapshot(plan.Provenance.SessionId)
            .Where(item => !item.IsStale)
            .Where(item => item.RunId is null || item.RunId == plan.Provenance.ParentRunId)
            .OrderByDescending(item => item.Relevance)
            .ThenBy(item => item.EvidenceId.Value))
        {
            var tokens = evidence.EstimatedTokens > 0
                ? evidence.EstimatedTokens
                : TokenEstimator.Estimate(evidence.Content);
            if (used + tokens > maximumTokens || selected.Count >= assignment.Budget.EvidenceItems)
            {
                continue;
            }

            selected.Add(evidence);
            used += tokens;
        }

        return new AgentContextSnapshot
        {
            AssignmentId = assignment.AssignmentId,
            BaselineIdentity = plan.Provenance.BaselineIdentity,
            Objective = assignment.Objective,
            Tasks = [.. assignment.Tasks],
            Evidence = selected,
            AllowedToolIds = assignment.Policy.AllowedToolIds
                .Except(assignment.Policy.DeniedToolIds, StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray(),
            EstimatedTokens = used,
        };
    }
}

/// <summary>Validates cited child findings and admits bounded provenance-linked parent evidence.</summary>
public sealed class AgentFindingAdmission
{
    private const int MaximumSummaryCharacters = 4_096;
    private readonly IEvidenceStore _evidence;

    /// <summary>Initializes a new instance of the <see cref="AgentFindingAdmission"/> class.</summary>
    public AgentFindingAdmission(IEvidenceStore evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        _evidence = evidence;
    }

    /// <summary>Admits schema-valid findings whose citations exist in the parent evidence store.</summary>
    public async Task<IReadOnlyList<EvidenceId>> AdmitAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        AgentFindingSet findings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(findings);
        if (findings.SchemaVersion != 1
            || findings.AssignmentId != assignment.AssignmentId
            || findings.ChildRunId != assignment.ChildRunId
            || findings.Generation != plan.Provenance.Generation
            || assignment.Role != AgentRole.Explorer)
        {
            throw new InvalidDataException("Finding-set identity, generation, schema, or role is invalid.");
        }

        AgentContextSnapshot snapshot = new AgentContextAssembler(_evidence).Assemble(plan, assignment);
        IReadOnlySet<EvidenceId> admitted = snapshot.Evidence
            .Select(item => item.EvidenceId)
            .ToHashSet();
        var added = new List<EvidenceId>();
        foreach (AgentFinding finding in findings.Findings)
        {
            if (finding.FindingId == Guid.Empty
                || string.IsNullOrWhiteSpace(finding.Category)
                || string.IsNullOrWhiteSpace(finding.Summary)
                || finding.Summary.Length > MaximumSummaryCharacters
                || finding.Confidence is < 0 or > 1
                || finding.EvidenceIds.Count == 0
                || finding.EvidenceIds.Any(id => !admitted.Contains(id)))
            {
                throw new InvalidDataException("A child finding is malformed or cites evidence not admitted by the parent.");
            }

            EvidenceId evidenceId = EvidenceId.New();
            await _evidence.AddAsync(
                new Evidence
                {
                    EvidenceId = evidenceId,
                    SessionId = plan.Provenance.SessionId,
                    RunId = plan.Provenance.ParentRunId,
                    Kind = EvidenceKind.ToolResult,
                    Content = finding.Summary,
                    Provenance = new EvidenceProvenance
                    {
                        Source = $"agent:{assignment.Role}:{assignment.AssignmentId.Value:D}",
                        SourcePath = finding.Locations.FirstOrDefault(),
                        RepositoryRevision = plan.Provenance.BaselineIdentity,
                        ChildRunId = assignment.ChildRunId,
                        AgentAssignmentId = assignment.AssignmentId,
                        ModelProfileId = assignment.Policy.ModelProfileId == default
                            ? null
                            : assignment.Policy.ModelProfileId,
                        BaselineIdentity = plan.Provenance.BaselineIdentity,
                    },
                    CollectedAt = DateTimeOffset.UtcNow,
                    Relevance = finding.Confidence,
                    EstimatedTokens = TokenEstimator.Estimate(finding.Summary),
                    Sensitivity = assignment.Policy.Sensitivity == ConversationSensitivity.Sensitive
                        ? EvidenceSensitivity.Sensitive
                        : EvidenceSensitivity.None,
                    InvalidationKeys = ["repository"],
                },
                cancellationToken);
            added.Add(evidenceId);
        }

        return added;
    }
}
