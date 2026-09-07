namespace Threadsmith.Execution;

using System.Diagnostics;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Owns one private candidate until its child result is durably joined.</summary>
internal sealed class ApprovedImplementerAssignmentRunner : IAgentAssignmentRunner
{
    private const string ApprovalHandoff = "Review the exact staged diff, authorize it separately, then apply and validate through the parent execution.";
    private readonly ProposeMutationSetCommand _command;
    private readonly ApprovedImplementerPreparationContext _context;
    private readonly DelegationPlan _plan;
    private readonly DelegateAgentsOptions _options;
    private readonly MutationProposalApplication _proposals;
    private PreparedMutationProposal? _prepared;

    /// <summary>Initializes a new instance of the <see cref="ApprovedImplementerAssignmentRunner"/> class.</summary>
    public ApprovedImplementerAssignmentRunner(
        MutationProposalApplication proposals,
        ProposeMutationSetCommand command,
        DelegationPlan plan,
        AgentModelSelector selection,
        DelegateAgentsOptions? options = null)
    {
        _proposals = proposals;
        _command = command;
        _plan = plan;
        _context = new ApprovedImplementerPreparationContext(plan.Assignments[0], selection);
        _options = options ?? new DelegateAgentsOptions();
        _options.Validate();
    }

    /// <inheritdoc />
    public async Task<AgentRunOutcome> RunAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(assignment);
        var frozen = _plan.Assignments[0];
        if (plan.DelegationId != _plan.DelegationId
            || plan.Provenance != _plan.Provenance
            || assignment.AssignmentId != frozen.AssignmentId
            || assignment.ChildRunId != frozen.ChildRunId
            || !plan.ImplementationAuthorized)
        {
            throw new UnauthorizedAccessException("The preparation child does not belong to this approved workflow.");
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            return await PrepareOutcomeAsync(frozen, started, cancellationToken);
        }
        catch (Exception exception)
        {
            var safeReason = exception is OperationCanceledException
                ? "Approved Implementer preparation was cancelled."
                : "Approved Implementer preparation failed before staging.";
            ChildAgentFailureDetails.Attach(
                exception,
                safeReason,
                _context.CreateUsage(Stopwatch.GetElapsedTime(started)),
                _context.Selection.ProfileId,
                _context.Selection.Provenance);
            throw;
        }
    }

    /// <summary>Returns the private candidate only after its exact child result has authoritatively joined.</summary>
    internal PreparedMutationProposal GetJoinedProposal(DelegationCheckpoint checkpoint)
    {
        var assignment = _plan.Assignments[0];
        if (checkpoint.Phase == DelegationCheckpointPhase.Cancelled)
        {
            throw new OperationCanceledException("Approved Implementer preparation was cancelled before staging.");
        }

        if (checkpoint.DelegationId != _plan.DelegationId
            || checkpoint.Provenance != _plan.Provenance
            || checkpoint.Phase != DelegationCheckpointPhase.ResearchJoined
            || checkpoint.ChildOutcomes.Count != 1
            || checkpoint.ChildOutcomes[0] is not { Status: AgentRunStatus.Completed, Role: AgentRole.Implementer } outcome
            || outcome.AssignmentId != assignment.AssignmentId
            || outcome.ChildRunId != assignment.ChildRunId
            || outcome.Generation != _plan.Provenance.Generation
            || outcome.Implementation is null
            || outcome.Findings is null)
        {
            throw new InvalidOperationException("The approved Implementer child did not complete an authoritative proposal join.");
        }

        return _prepared ?? throw new InvalidOperationException("The joined child has no prepared mutation proposal.");
    }

    private async Task<AgentRunOutcome> PrepareOutcomeAsync(
        AgentAssignment frozen,
        long started,
        CancellationToken cancellationToken)
    {
        var prepared = await _proposals.PrepareAsync(_command, _context, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _prepared = prepared;
        var mutations = prepared.MutationSet;
        var paths = mutations.Mutations.SelectMany(mutation => mutation.DestinationRelativePath is { } destination
                ? new[] { mutation.RelativePath, destination }
                : [mutation.RelativePath])
            .Distinct(StringComparer.Ordinal).ToArray();
        var validation = mutations.ExpectedTests.Select(item =>
            ProjectText(item, _options.MaximumPreparedValidationCharacters));
        if (_options.EffectiveLimit(_options.MaximumPreparedValidationItems) is > 0 and var maximumItems)
        {
            validation = validation.Take(maximumItems);
        }

        return new AgentRunOutcome
        {
            AssignmentId = frozen.AssignmentId,
            ChildRunId = frozen.ChildRunId,
            Role = AgentRole.Implementer,
            Generation = _plan.Provenance.Generation,
            Status = AgentRunStatus.Completed,
            Reason = "Approved implementation candidate prepared; separate mutation approval and validation remain required.",
            ModelProfileId = _context.Selection.ProfileId,
            ModelSelection = _context.Selection.Provenance,
            Usage = _context.CreateUsage(Stopwatch.GetElapsedTime(started), paths.Length, mutations: 1),
            DeliveredEvidenceIds = _context.DeliveredEvidenceIds,
            Findings = new AgentFindingSet
            {
                AssignmentId = frozen.AssignmentId,
                ChildRunId = frozen.ChildRunId,
                Generation = _plan.Provenance.Generation,
                CoverageNotes = ["Candidate operations passed the approved-plan mutation preparation checks; no files have been applied."],
            },
            Implementation = new AgentImplementationHandoff
            {
                AssignmentId = frozen.AssignmentId,
                ChildRunId = frozen.ChildRunId,
                Generation = _plan.Provenance.Generation,
                Summary = ProjectText(mutations.Rationale, _options.MaximumProjectedDetailCharacters),
                InspectedFiles = [],
                ProposedChanges = paths.Select(path => new AgentProposedFileChange
                {
                    RelativePath = path,
                    EvidenceIds = [],
                    IntendedChange = "Candidate operation prepared within the approved plan; inspect the exact diff before authorization.",
                    ValidationPlan = [],
                    Risks = [],
                    Handoff = ApprovalHandoff,
                }).ToArray(),
                ValidationPlan = validation.ToArray(),
                Risks = ["Post-apply validation has not run."],
                Handoff = ApprovalHandoff,
            },
        };
    }

    private string ProjectText(string value, int configuredLimit)
    {
        var limit = _options.EffectiveLimit(configuredLimit);
        return limit == 0 ? value : BoundedText.Truncate(value, limit, out _);
    }
}
