namespace Threadsmith.NativeTools.Tests;

using Threadsmith.Core;
using Threadsmith.Validation;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Verifies Plan-44 lifecycle endpoints remain integrated with validation and isolated workers.</summary>
public sealed class Plan44LifecycleIntegrationTests
{
    /// <summary>Move source and destination paths select direct owners and transitive dependent tests.</summary>
    [Fact]
    public void LifecycleEndpoints_SelectAffectedProjectsAndDependents()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-plan44-impact-{Guid.NewGuid():N}");
        var library = Path.Combine(root, "src", "Library", "Library.csproj");
        var app = Path.Combine(root, "src", "App", "App.csproj");
        var tests = Path.Combine(root, "tests", "App.Tests", "App.Tests.csproj");
        SemanticProjectInfo[] projects =
        [
            new("Library", library, ["net10.0"], SemanticConfidenceLevel.FullSemantic, [], []),
            new("App", app, ["net10.0"], SemanticConfidenceLevel.FullSemantic, [library], []),
            new("App.Tests", tests, ["net10.0"], SemanticConfidenceLevel.FullSemantic, [app], []),
        ];

        var affected = AffectedProjectCalculator.Calculate(
            root,
            ["src/Library/Before.cs", "src/App/After.cs"],
            projects);

        Assert.Contains(affected.Projects, project => project.Name == "Library" && project.IsDirectlyChanged);
        Assert.Contains(affected.Projects, project => project.Name == "App" && project.IsDirectlyChanged);
        Assert.Contains(affected.Projects, project => project.Name == "App.Tests" && !project.IsDirectlyChanged);
        Assert.Empty(affected.UnmappedFiles);
    }

    /// <summary>A worker must own both move endpoints and overlapping destinations remain conflicts.</summary>
    [Fact]
    public void LifecycleEndpoints_RequireWorkerOwnershipAndRemainOverlapKeys()
    {
        var sourceOwner = CreateAssignment("src/Before.cs");
        var singlePlan = CreatePlan([sourceOwner]);
        var scopeExcess = CreateChangeSet(
            singlePlan,
            sourceOwner,
            ["src/Before.cs", "src/After.cs"]);

        var scopeConflicts = new WorkerIntegrationCoordinator().DetectConflicts(
            singlePlan,
            [scopeExcess],
            singlePlan.Provenance.BaselineIdentity);

        Assert.Contains(scopeConflicts, conflict =>
            conflict.Code == "assignment-scope-exceeded"
            && conflict.Paths.SequenceEqual(["src/After.cs"]));

        var destinationOwner = CreateAssignment("src/After.cs");
        var overlapPlan = CreatePlan([sourceOwner, destinationOwner]);
        var move = CreateChangeSet(
            overlapPlan,
            sourceOwner,
            ["src/Before.cs", "src/After.cs"]);
        var edit = CreateChangeSet(
            overlapPlan,
            destinationOwner,
            ["src/After.cs"]);

        var overlapConflicts = new WorkerIntegrationCoordinator().DetectConflicts(
            overlapPlan,
            [move, edit],
            overlapPlan.Provenance.BaselineIdentity);

        Assert.Contains(overlapConflicts, conflict =>
            conflict.Code == "worker-path-conflict"
            && conflict.Paths.Contains("src/After.cs", StringComparer.OrdinalIgnoreCase));
    }

    private static DelegationPlan CreatePlan(IReadOnlyList<AgentAssignment> assignments)
    {
        return new DelegationPlan
        {
            DelegationId = DelegationId.New(),
            Provenance = new DelegationProvenance
            {
                SessionId = SessionId.New(),
                ParentRunId = RunId.New(),
                RepositoryIdentity = "repository-id",
                BaselineIdentity = "baseline-id",
                WorkspaceId = WorkspaceId.New(),
            },
            Assignments = assignments,
            ParentBudget = new AgentResourceBudget(),
            ImplementationAuthorized = true,
            AcceptedAt = DateTimeOffset.UtcNow,
        };
    }

    private static AgentAssignment CreateAssignment(string path)
    {
        return new AgentAssignment
        {
            AssignmentId = AgentAssignmentId.New(),
            ChildRunId = RunId.New(),
            Role = AgentRole.Implementer,
            Mode = AgentRunMode.IsolatedWorktreeMutation,
            Objective = $"Change {path}",
            OutputSchema = "worker-change-set/1",
            StoppingCondition = "Stop after the assigned file is changed.",
            Deadline = DateTimeOffset.UtcNow.AddMinutes(5),
            Scope = new AgentAssignmentScope
            {
                Files = [path],
                IsOwnershipProven = true,
            },
            Policy = new AgentPolicySnapshot
            {
                TrustCeiling = RepositoryTrustLevel.TrustedMutation,
                ModelSelectionRationale = "test model",
                ContextPolicyVersion = "agent-context/1",
                ToolPolicyVersion = "agent-tools/1",
            },
            Budget = new AgentResourceBudget(),
            PlanStepIds = [StepId.New()],
        };
    }

    private static WorkerChangeSet CreateChangeSet(
        DelegationPlan plan,
        AgentAssignment assignment,
        IReadOnlyList<string> paths)
    {
        return new WorkerChangeSet
        {
            AssignmentId = assignment.AssignmentId,
            ChildRunId = assignment.ChildRunId,
            Generation = plan.Provenance.Generation,
            ParentBaselineIdentity = plan.Provenance.BaselineIdentity,
            WorktreeIdentity = "worktree",
            TouchedPaths = paths,
            DiffArtifact = new ExecutionArtifactReference("hash", "diff", 4),
            Validation = new MutationValidationResult(
                new BuildValidationResult(true, [], [], TimeSpan.Zero),
                [],
                new TestValidationResult
                {
                    Selection = new TestSelection(),
                    Completed = true,
                },
                new AcceptanceGateResult(AcceptanceGateStatus.Passed, [])),
            ApprovalProvenance = "test policy",
            IsComplete = true,
        };
    }
}
