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

    /// <summary>Frozen caller-supplied context treated as untrusted task data.</summary>
    public string InitialContext { get; init; } = string.Empty;

    /// <summary>Governed evidence selected by the assignment policy.</summary>
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

    /// <summary>Builds a governed snapshot containing no raw conversation transcript.</summary>
    public AgentContextSnapshot Assemble(
        DelegationPlan plan,
        AgentAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(assignment);
        var frozenAssignment = plan.Assignments.SingleOrDefault(
            item => item.AssignmentId == assignment.AssignmentId);
        if (frozenAssignment is null
            || frozenAssignment.ChildRunId != assignment.ChildRunId
            || frozenAssignment.Role != assignment.Role
            || frozenAssignment.Mode != assignment.Mode)
        {
            throw new UnauthorizedAccessException("The assignment does not belong to this delegation.");
        }

        assignment = frozenAssignment;

        var maximumTokens = assignment.Budget.EnforceLimits
            ? (int)Math.Min(assignment.Budget.ModelTokens, int.MaxValue)
            : int.MaxValue;
        var used = TokenEstimator.Estimate(assignment.Objective)
            + TokenEstimator.Estimate(assignment.InitialContext)
            + assignment.Tasks.Sum(TokenEstimator.Estimate);
        var selected = new List<Evidence>();
        foreach (var evidence in _evidence.Snapshot(plan.Provenance.SessionId)
            .Where(item => !item.IsStale)
            .Where(item => item.RunId is null || item.RunId == plan.Provenance.ParentRunId)
            .Where(item => assignment.Policy.Sensitivity == ConversationSensitivity.Sensitive
                || item.Sensitivity != EvidenceSensitivity.Sensitive)
            .OrderByDescending(item => item.Relevance)
            .ThenBy(item => item.EvidenceId.Value))
        {
            var tokens = evidence.EstimatedTokens > 0
                ? evidence.EstimatedTokens
                : TokenEstimator.Estimate(evidence.Content);
            if (assignment.Budget.EnforceLimits
                && ((long)used + tokens > maximumTokens
                    || selected.Count >= assignment.Budget.EvidenceItems))
            {
                continue;
            }

            selected.Add(evidence);
            used = (int)Math.Min((long)used + tokens, int.MaxValue);
        }

        return new AgentContextSnapshot
        {
            AssignmentId = assignment.AssignmentId,
            BaselineIdentity = plan.Provenance.BaselineIdentity,
            Objective = assignment.Objective,
            Tasks = [.. assignment.Tasks],
            InitialContext = assignment.InitialContext,
            Evidence = selected,
            AllowedToolIds = assignment.Policy.AllowedToolIds
                .Except(assignment.Policy.DeniedToolIds, StringComparer.OrdinalIgnoreCase)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray(),
            EstimatedTokens = used,
        };
    }
}

/// <summary>One validated child finding set awaiting parent-boundary admission.</summary>
public sealed record AgentFindingAdmissionRequest(
    AgentAssignment Assignment,
    AgentFindingSet Findings,
    ModelProfileId? EffectiveModelProfileId,
    IReadOnlySet<EvidenceId> DeliveredEvidenceIds);

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

    /// <summary>Validates the complete finding set without changing parent evidence.</summary>
    public static void Validate(
        DelegationPlan plan,
        AgentAssignment assignment,
        AgentFindingSet findings,
        IReadOnlySet<EvidenceId> deliveredEvidenceIds)
    {
        _ = Prepare(
            plan,
            assignment,
            findings,
            effectiveModelProfileId: null,
            deliveredEvidenceIds);
    }

    /// <summary>Admits schema-valid findings whose citations were delivered to the child.</summary>
    public async Task<IReadOnlyList<EvidenceId>> AdmitAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        AgentFindingSet findings,
        IReadOnlySet<EvidenceId> deliveredEvidenceIds,
        CancellationToken cancellationToken = default)
    {
        var prepared = Prepare(
            plan,
            assignment,
            findings,
            effectiveModelProfileId: null,
            deliveredEvidenceIds);
        await _evidence.AddBatchAsync(prepared, cancellationToken);
        return prepared.Select(item => item.EvidenceId).ToArray();
    }

    /// <summary>Conditionally admits all validated child finding sets at the parent join commit gate.</summary>
    public async Task<bool> TryAdmitAsync(
        DelegationPlan plan,
        IReadOnlyList<AgentFindingAdmissionRequest> requests,
        Func<bool> tryCommit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(tryCommit);
        var prepared = requests.SelectMany(request =>
        {
            ArgumentNullException.ThrowIfNull(request);
            return Prepare(
                plan,
                request.Assignment,
                request.Findings,
                request.EffectiveModelProfileId,
                request.DeliveredEvidenceIds);
        }).ToArray();
        return await _evidence.TryAddBatchAsync(prepared, tryCommit, cancellationToken);
    }

    private static IReadOnlyList<Evidence> Prepare(
        DelegationPlan plan,
        AgentAssignment assignment,
        AgentFindingSet findings,
        ModelProfileId? effectiveModelProfileId,
        IReadOnlySet<EvidenceId> deliveredEvidenceIds)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(deliveredEvidenceIds);
        var frozenAssignment = plan.Assignments.SingleOrDefault(
            item => item.AssignmentId == assignment.AssignmentId);
        if (frozenAssignment is null
            || frozenAssignment.ChildRunId != assignment.ChildRunId
            || frozenAssignment.Role != assignment.Role
            || frozenAssignment.Mode != assignment.Mode)
        {
            throw new UnauthorizedAccessException("The assignment does not belong to this delegation.");
        }

        if (findings.SchemaVersion != 1
            || findings.AssignmentId != frozenAssignment.AssignmentId
            || findings.ChildRunId != frozenAssignment.ChildRunId
            || findings.Generation != plan.Provenance.Generation
            || frozenAssignment.Role != AgentRole.Explorer
            || findings.Findings.Count == 0)
        {
            throw new InvalidDataException("Finding-set identity, generation, schema, or role is invalid.");
        }

        foreach (var finding in findings.Findings)
        {
            if (finding.FindingId == Guid.Empty
                || string.IsNullOrWhiteSpace(finding.Category)
                || string.IsNullOrWhiteSpace(finding.Summary)
                || finding.Summary.Length > MaximumSummaryCharacters
                || finding.Confidence is < 0 or > 1
                || finding.EvidenceIds.Count == 0
                || finding.EvidenceIds.Any(id => !deliveredEvidenceIds.Contains(id)))
            {
                throw new InvalidDataException("A child finding is malformed or cites evidence not admitted by the parent.");
            }
        }

        return findings.Findings.Select(finding =>
            new Evidence
            {
                EvidenceId = EvidenceId.New(),
                SessionId = plan.Provenance.SessionId,
                RunId = plan.Provenance.ParentRunId,
                Kind = EvidenceKind.ToolResult,
                Content = finding.Summary,
                Provenance = new EvidenceProvenance
                {
                    Source = $"agent:{frozenAssignment.Role}:{frozenAssignment.AssignmentId.Value:D}",
                    SourcePath = finding.Locations.FirstOrDefault(),
                    RepositoryRevision = plan.Provenance.BaselineIdentity,
                    ChildRunId = frozenAssignment.ChildRunId,
                    AgentAssignmentId = frozenAssignment.AssignmentId,
                    ModelProfileId = effectiveModelProfileId,
                    BaselineIdentity = plan.Provenance.BaselineIdentity,
                },
                CollectedAt = DateTimeOffset.UtcNow,
                Relevance = finding.Confidence,
                EstimatedTokens = TokenEstimator.Estimate(finding.Summary),
                Sensitivity = frozenAssignment.Policy.Sensitivity == ConversationSensitivity.Sensitive
                    ? EvidenceSensitivity.Sensitive
                    : EvidenceSensitivity.None,
                InvalidationKeys = ["repository"],
            }).ToArray();
    }
}
