namespace Threadsmith.ParallelAgents.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

/// <summary>Verifies ordinary responses remain opaque while legacy and approved mutation results retain their contracts.</summary>
public sealed class AgentNaturalResponseClassificationTests
{
    /// <summary>All ordinary roles can complete with any present response, including empty text, and no structured fields.</summary>
    [Theory]
    [InlineData(AgentRole.Explorer)]
    [InlineData(AgentRole.Implementer)]
    [InlineData(AgentRole.SecurityReviewer)]
    [InlineData(AgentRole.TestReviewer)]
    [InlineData(AgentRole.PerformanceReviewer)]
    [InlineData(AgentRole.ArchitectureReviewer)]
    public void OrdinaryRole_AcceptsAnyPresentResponseWithoutSemanticGrading(AgentRole role)
    {
        var (plan, outcome) = CreateCase(role);
        foreach (var response in new[] { string.Empty, " \n\t", "A plain response.", "{}", "I changed files and ran tests." })
        {
            var returned = outcome with { Response = response };
            Assert.True(DelegationOutcomeClassifier.HasNaturalResponse(plan.Assignments[0], returned));
            Assert.True(DelegationOutcomeClassifier.HasUsableResult(plan, returned));
            var normalized = DelegationOutcomeClassifier.Normalize(plan, returned);
            Assert.Same(returned, normalized);
            Assert.Equal(response, normalized.Response);
            Assert.Null(normalized.Findings);
            Assert.Null(normalized.Review);
            Assert.Null(normalized.Implementation);
            Assert.Null(normalized.ChangeSet);
            var status = DelegationOutcomeClassifier.ResolveStatus(plan, [returned], DelegationCheckpointPhase.ResearchJoined);
            Assert.Equal(DelegateAgentsStatus.Completed, status);
        }
    }

    /// <summary>Transport failure, cancellation, and discard cannot become successful responses.</summary>
    [Theory]
    [InlineData(AgentRunStatus.Failed)]
    [InlineData(AgentRunStatus.Cancelled)]
    [InlineData(AgentRunStatus.Discarded)]
    public void FailedTerminal_ClearsResponseAndPreservesFailureMetadata(AgentRunStatus status)
    {
        var (plan, outcome) = CreateCase(AgentRole.Explorer);
        outcome = outcome with { Status = status, Response = "Partial output", ModelProfileId = ModelProfileId.New() };

        Assert.False(DelegationOutcomeClassifier.HasNaturalResponse(plan.Assignments[0], outcome));
        Assert.False(DelegationOutcomeClassifier.HasUsableResult(plan, outcome));
        var normalized = DelegationOutcomeClassifier.Normalize(plan, outcome);
        Assert.Null(normalized.Response);
        Assert.Equal(status, normalized.Status);
        Assert.Equal(outcome.Reason, normalized.Reason);
        Assert.Equal(outcome.ModelProfileId, normalized.ModelProfileId);
        Assert.Equal(outcome.Usage, normalized.Usage);
    }

    /// <summary>A response is accepted only at its own completed generation and assignment boundary.</summary>
    [Fact]
    public void Response_DoesNotOverrideHostIdentityOrTerminalStatus()
    {
        var (plan, outcome) = CreateCase(AgentRole.Explorer);
        outcome = outcome with { Response = string.Empty };
        Assert.False(DelegationOutcomeClassifier.HasUsableResult(plan, outcome with { AssignmentId = AgentAssignmentId.New() }));
        Assert.False(DelegationOutcomeClassifier.HasUsableResult(plan, outcome with { ChildRunId = RunId.New() }));
        Assert.False(DelegationOutcomeClassifier.HasUsableResult(plan, outcome with { Generation = outcome.Generation + 1 }));
        Assert.False(DelegationOutcomeClassifier.HasUsableResult(plan, outcome with { Status = AgentRunStatus.Running }));
        Assert.False(DelegationOutcomeClassifier.HasUsableResult(plan, outcome with { Status = AgentRunStatus.Queued }));
    }

    /// <summary>Legacy fields cannot make a foreign or stale ordinary response usable.</summary>
    [Fact]
    public static void OrdinaryResponse_LegacyPayloadCannotBypassIdentityOrGeneration()
    {
        var (plan, outcome) = CreateCase(AgentRole.TestReviewer);
        outcome = outcome with
        {
            Response = "A response.",
            Review = new ReviewFindingSet
            {
                AssignmentId = outcome.AssignmentId,
                Role = outcome.Role,
                Generation = outcome.Generation,
                Findings = [],
            },
        };
        Assert.False(DelegationOutcomeClassifier.HasUsableResult(plan, outcome with { Generation = outcome.Generation + 1 }));
        Assert.False(DelegationOutcomeClassifier.HasUsableResult(plan, outcome with { ChildRunId = RunId.New() }));
        Assert.False(DelegationOutcomeClassifier.HasUsableResult(plan, outcome with { Response = null }));
    }

    /// <summary>Older serialized outcomes without Response still use their structured findings.</summary>
    [Fact]
    public void LegacyCheckpoint_OmittedResponseRestoresAsNullAndKeepsFindings()
    {
        var (plan, outcome) = CreateCase(AgentRole.Explorer, schema: DelegateAgentsContract.FindingSchema);
        outcome = outcome with
        {
            Findings = CreateFindings(outcome) with
            {
                Findings =
                [
                    new AgentFinding
                    {
                        FindingId = Guid.NewGuid(),
                        Category = "behavior",
                        Summary = "A legacy finding.",
                        EvidenceIds = [EvidenceId.New()],
                        Confidence = 1,
                    },
                ],
            },
        };
        var json = JsonSerializer.SerializeToNode(outcome)?.AsObject()
            ?? throw new InvalidOperationException("Outcome serialization failed.");
        Assert.True(json.Remove(nameof(AgentRunOutcome.Response)));
        var restored = json.Deserialize<AgentRunOutcome>();

        Assert.NotNull(restored);
        Assert.Null(restored.Response);
        Assert.True(DelegationOutcomeClassifier.HasUsableResult(plan, restored));
        Assert.Same(restored, DelegationOutcomeClassifier.Normalize(plan, restored));
        Assert.False(DelegationOutcomeClassifier.HasUsableResult(plan, restored with { Findings = null }));
    }

    /// <summary>Legacy reviewer results remain usable without a new natural response field.</summary>
    [Fact]
    public void LegacyReviewer_StillAcceptsStructuredReview()
    {
        var (plan, outcome) = CreateCase(AgentRole.TestReviewer, schema: "agent-test-review/1");
        outcome = outcome with
        {
            Review = new ReviewFindingSet
            {
                AssignmentId = outcome.AssignmentId,
                Role = outcome.Role,
                Generation = outcome.Generation,
                Findings = [],
            },
        };
        Assert.Null(outcome.Response);
        Assert.True(DelegationOutcomeClassifier.HasUsableResult(plan, outcome));
    }

    /// <summary>Approved mutation preparation does not replace its real proposal handoff with a textual claim.</summary>
    [Fact]
    public void ApprovedPreparation_StillRequiresImplementationAndCommonFindings()
    {
        var (plan, outcome) = CreateCase(AgentRole.Implementer, schema: "approved-implementer-preparation/1");
        plan = plan with
        {
            ImplementationAuthorized = true,
            Provenance = plan.Provenance with { ApprovedPlanIdentity = "approved-plan", ApprovedPlanRevision = 1 },
        };
        outcome = outcome with { Response = "The changes are ready." };

        Assert.False(DelegationOutcomeClassifier.HasNaturalResponse(plan.Assignments[0], outcome));
        Assert.False(DelegationOutcomeClassifier.HasUsableResult(plan, outcome));
        Assert.Null(DelegationOutcomeClassifier.Normalize(plan, outcome).Response);
        outcome = outcome with { Response = null, Implementation = CreateImplementation(outcome) };
        Assert.False(DelegationOutcomeClassifier.HasUsableResult(plan, outcome));
        outcome = outcome with { Findings = CreateFindings(outcome) };
        Assert.True(DelegationOutcomeClassifier.HasUsableResult(plan, outcome));
    }

    /// <summary>Isolated workers retain their completed change-set requirement even if they also return prose.</summary>
    [Fact]
    public void IsolatedWorker_StillRequiresCompleteChangeSet()
    {
        var (plan, outcome) = CreateCase(AgentRole.Implementer);
        plan = plan with
        {
            ImplementationAuthorized = true,
            Assignments = [plan.Assignments[0] with { Mode = AgentRunMode.IsolatedWorktreeMutation }],
        };
        outcome = outcome with { Response = string.Empty };
        Assert.False(DelegationOutcomeClassifier.HasNaturalResponse(plan.Assignments[0], outcome));
        Assert.False(DelegationOutcomeClassifier.HasUsableResult(plan, outcome));
        var changes = new WorkerChangeSet
        {
            AssignmentId = outcome.AssignmentId,
            ChildRunId = outcome.ChildRunId,
            Generation = outcome.Generation,
            ParentBaselineIdentity = plan.Provenance.BaselineIdentity,
            WorktreeIdentity = "test-worktree",
            DiffArtifact = new ExecutionArtifactReference("hash", "diff", 4),
            Validation = new MutationValidationResult(
                new BuildValidationResult(true, [], [], TimeSpan.Zero),
                [],
                new TestValidationResult { Selection = new TestSelection(), Completed = true },
                new AcceptanceGateResult(AcceptanceGateStatus.Passed, [])),
            ApprovalProvenance = "test",
            IsComplete = true,
        };
        Assert.True(DelegationOutcomeClassifier.HasUsableResult(plan, outcome with { ChangeSet = changes }));
        Assert.False(DelegationOutcomeClassifier.HasUsableResult(plan, outcome with { ChangeSet = changes with { IsComplete = false } }));
    }

    /// <summary>Partial joins distinguish an empty successful reply from a failed sibling.</summary>
    [Fact]
    public void MixedJoin_CountsEmptyResponseAsCompletedButRetainsFailureStatus()
    {
        var (plan, outcome) = CreateCase(AgentRole.Explorer);
        var sibling = plan.Assignments[0] with { AssignmentId = AgentAssignmentId.New(), ChildRunId = RunId.New() };
        plan = plan with { Assignments = [plan.Assignments[0], sibling] };
        var completed = outcome with { Response = string.Empty };
        var failed = outcome with
        {
            AssignmentId = sibling.AssignmentId,
            ChildRunId = sibling.ChildRunId,
            Status = AgentRunStatus.Failed,
            Response = null,
        };
        var partial = DelegationOutcomeClassifier.ResolveStatus(plan, [completed, failed], DelegationCheckpointPhase.ResearchJoined);
        var cancelled = DelegationOutcomeClassifier.ResolveStatus(plan, [completed], DelegationCheckpointPhase.Cancelled);
        Assert.Equal(DelegateAgentsStatus.Partial, partial);
        Assert.Equal(DelegateAgentsStatus.Cancelled, cancelled);
    }

    private static AgentFindingSet CreateFindings(AgentRunOutcome outcome)
    {
        return new AgentFindingSet
        {
            AssignmentId = outcome.AssignmentId,
            ChildRunId = outcome.ChildRunId,
            Generation = outcome.Generation,
        };
    }

    private static AgentImplementationHandoff CreateImplementation(AgentRunOutcome outcome)
    {
        return new AgentImplementationHandoff
        {
            AssignmentId = outcome.AssignmentId,
            ChildRunId = outcome.ChildRunId,
            Generation = outcome.Generation,
            Summary = "Prepared candidate.",
            InspectedFiles = [],
            ProposedChanges = [],
            ValidationPlan = [],
            Risks = [],
            Handoff = "Separate exact approval remains required.",
        };
    }

    private static (DelegationPlan Plan, AgentRunOutcome Outcome) CreateCase(
        AgentRole role, string schema = AgentAssignment.ResponseSchema)
    {
        var budget = AgentResourceBudget.CreateTelemetryOnly(TimeSpan.FromMinutes(1));
        var assignment = new AgentAssignment
        {
            AssignmentId = AgentAssignmentId.New(),
            ChildRunId = RunId.New(),
            Role = role,
            Mode = role is AgentRole.Explorer or AgentRole.Implementer ? AgentRunMode.ReadOnlyBaseline : AgentRunMode.ReadOnlyReview,
            Objective = "Inspect the assigned area.",
            Tasks = ["Respond to the task."],
            OutputSchema = schema,
            StoppingCondition = "Return a response.",
            Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
            Scope = new AgentAssignmentScope(),
            Policy = new AgentPolicySnapshot
            {
                ModelSelectionRationale = "test",
                ContextPolicyVersion = "test",
                ToolPolicyVersion = "read-only",
            },
            Budget = budget,
        };
        var plan = new DelegationPlan
        {
            DelegationId = DelegationId.New(),
            Provenance = new DelegationProvenance
            {
                SessionId = SessionId.New(),
                ParentRunId = RunId.New(),
                WorkspaceId = WorkspaceId.New(),
                RepositoryIdentity = "repository",
                BaselineIdentity = "baseline",
            },
            Assignments = [assignment],
            ParentBudget = budget,
            AcceptedAt = DateTimeOffset.UtcNow,
        };
        return (plan, new AgentRunOutcome
        {
            AssignmentId = assignment.AssignmentId,
            ChildRunId = assignment.ChildRunId,
            Role = role,
            Generation = plan.Provenance.Generation,
            Status = AgentRunStatus.Completed,
            Usage = new AgentResourceUsage(),
            Reason = "Transport completed.",
        });
    }
}
