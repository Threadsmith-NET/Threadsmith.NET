namespace Threadsmith.ParallelAgents.Tests;

using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Persistence;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Verifies bounded in-process delegation, partitioning, persistence, policy, and worktree isolation.</summary>
public sealed class ParallelAgentTests
{
    /// <summary>Verifies bounded scheduling executes children concurrently without creating an agent process.</summary>
    [Fact]
    public async Task Scheduler_RunsInProcessAndHonorsGlobalLimit()
    {
        // Arrange
        var plan = CreatePlan(
            CreateAssignment(AgentRole.Explorer, "src/A.cs"),
            CreateAssignment(AgentRole.Explorer, "src/B.cs"),
            CreateAssignment(AgentRole.Explorer, "src/C.cs"));
        var runner = new TrackingRunner(TimeSpan.FromMilliseconds(75));
        await using var scheduler = new AgentRunScheduler(new AgentSchedulerOptions
        {
            QueueCapacity = 8,
            MaximumActiveChildren = 2,
            MaximumActiveChildrenPerParent = 2,
            MaximumActiveImplementers = 1,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });
        var processId = Environment.ProcessId;

        // Act
        var outcomes = await scheduler.RunAsync(plan, runner);

        // Assert
        Assert.Equal(processId, Environment.ProcessId);
        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, item => Assert.Equal(AgentRunStatus.Completed, item.Status));
        Assert.Equal(2, runner.MaximumActive);
    }

    /// <summary>Verifies separate delegations from one parent share the same concurrency ceiling.</summary>
    [Fact]
    public async Task Scheduler_ConcurrentDelegations_HonorSharedParentLimit()
    {
        // Arrange
        var first = CreatePlan(CreateAssignment(AgentRole.Explorer, "src/A.cs"));
        var second = CreatePlan(CreateAssignment(AgentRole.Explorer, "src/B.cs"));
        second = second with
        {
            Provenance = second.Provenance with
            {
                ParentRunId = first.Provenance.ParentRunId,
            },
        };
        var runner = new TrackingRunner(TimeSpan.FromMilliseconds(100));
        await using var scheduler = new AgentRunScheduler(new AgentSchedulerOptions
        {
            QueueCapacity = 8,
            MaximumActiveChildren = 2,
            MaximumActiveChildrenPerParent = 1,
            MaximumActiveImplementers = 1,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });

        // Act
        await Task.WhenAll(
            scheduler.RunAsync(first, runner),
            scheduler.RunAsync(second, runner));

        // Assert
        Assert.Equal(1, runner.MaximumActive);
    }

    /// <summary>Verifies parent cancellation reaches all child runs and every child becomes terminal.</summary>
    [Fact]
    public async Task Scheduler_ParentCancellation_ObservesEveryChild()
    {
        // Arrange
        var plan = CreatePlan(
            CreateAssignment(AgentRole.Explorer, "src/A.cs"),
            CreateAssignment(AgentRole.Explorer, "src/B.cs"));
        var runner = new TrackingRunner(TimeSpan.FromMinutes(1));
        await using var scheduler = new AgentRunScheduler();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // Act
        var outcomes = await scheduler.RunAsync(
            plan,
            runner,
            cancellation.Token);

        // Assert
        Assert.All(outcomes, item => Assert.Equal(AgentRunStatus.Cancelled, item.Status));
        Assert.All(outcomes, item => Assert.Equal(plan.Provenance.Generation, item.Generation));
        Assert.Equal(0, runner.Active);
    }

    /// <summary>Verifies a result from another generation is drained and cannot become authoritative.</summary>
    [Fact]
    public async Task Scheduler_LateGeneration_IsDiscarded()
    {
        // Arrange
        var assignment = CreateAssignment(AgentRole.Explorer, "src/A.cs");
        var plan = CreatePlan(assignment);
        await using var scheduler = new AgentRunScheduler();
        var runner = new FixedRunner(CreateFindingOutcome(assignment, generation: 99));

        // Act
        var outcome = Assert.Single(await scheduler.RunAsync(plan, runner));

        // Assert
        Assert.Equal(AgentRunStatus.Discarded, outcome.Status);
        Assert.Null(outcome.Findings);
    }

    /// <summary>Verifies a returned failed outcome applies the assignment's delegation failure policy.</summary>
    [Fact]
    public async Task Scheduler_ReturnedFailure_AppliesFailDelegationPolicy()
    {
        // Arrange
        var failed = CreateAssignment(AgentRole.Explorer, "src/A.cs") with
        {
            FailurePolicy = AgentFailurePolicy.FailDelegation,
        };
        var sibling = CreateAssignment(AgentRole.Explorer, "src/B.cs");
        var plan = CreatePlan(failed, sibling);
        var runner = new ReturningFailureRunner(failed.AssignmentId);
        await using var scheduler = new AgentRunScheduler();

        // Act
        var outcomes = await scheduler.RunAsync(plan, runner);

        // Assert
        Assert.Contains(outcomes, item => item.AssignmentId == failed.AssignmentId
            && item.Status == AgentRunStatus.Failed);
        Assert.Contains(outcomes, item => item.AssignmentId == sibling.AssignmentId
            && item.Status == AgentRunStatus.Cancelled);
        Assert.All(outcomes, item => Assert.Equal(plan.Provenance.Generation, item.Generation));
    }

    /// <summary>Verifies FailDelegation clears an already-completed sibling before the parent boundary.</summary>
    [Fact]
    public async Task Coordinator_FailDelegation_DiscardsCompletedSiblingFindings()
    {
        // Arrange
        var completed = CreateAssignment(AgentRole.Explorer, "src/A.cs");
        var failed = CreateAssignment(AgentRole.Explorer, "src/B.cs") with
        {
            FailurePolicy = AgentFailurePolicy.FailDelegation,
        };
        var plan = CreatePlan(completed, failed);
        var runner = new CompletedBeforeFailureRunner(
            completed.AssignmentId,
            failed.AssignmentId);
        await using var scheduler = new AgentRunScheduler();
        await using var events = new DomainEventStream();
        var store = new RecordingCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, store, events);

        // Act
        var terminal = await coordinator.StartAsync(plan, runner);

        // Assert
        Assert.Equal(DelegationCheckpointPhase.Failed, terminal.Phase);
        Assert.All(terminal.ChildOutcomes, outcome => Assert.Null(outcome.Findings));
        var completedSibling = Assert.Single(terminal.ChildOutcomes, outcome =>
            outcome.AssignmentId == completed.AssignmentId);
        Assert.Equal(AgentRunStatus.Failed, completedSibling.Status);
        Assert.Equal(plan.Provenance.Generation, completedSibling.Generation);
        Assert.Same(terminal, store.Latest);
    }

    /// <summary>Verifies static validation rejects recursive/cyclic dependency graphs.</summary>
    [Fact]
    public void Validator_RejectsDependencyCycle()
    {
        // Arrange
        var first = CreateAssignment(AgentRole.Explorer, "src/A.cs");
        var second = CreateAssignment(AgentRole.Explorer, "src/B.cs");
        first = first with { Dependencies = [second.AssignmentId] };
        second = second with { Dependencies = [first.AssignmentId] };
        var plan = CreatePlan(first, second);

        // Act / Assert
        Assert.Throws<InvalidDataException>(() => DelegationPlanValidator.Validate(plan));
    }

    /// <summary>Verifies dot segments cannot canonicalize a frozen assignment into a broader scope.</summary>
    [Theory]
    [InlineData("src/..")]
    [InlineData("./src/A.cs")]
    [InlineData("src/./A.cs")]
    public void Validator_RejectsDotPathSegments(string path)
    {
        // Arrange
        var plan = CreatePlan(CreateAssignment(AgentRole.Explorer, path));

        // Act / Assert
        Assert.Throws<UnauthorizedAccessException>(() => DelegationPlanValidator.Validate(plan));
    }

    /// <summary>Verifies read-only assignments cannot receive mutating tools or mutation trust.</summary>
    [Fact]
    public void Validator_RejectsReadOnlyAuthorityElevation()
    {
        // Arrange
        var assignment = CreateAssignment(AgentRole.Explorer, "src/A.cs") with
        {
            Policy = CreatePolicy() with
            {
                AllowedToolIds = ["read_file", "apply_mutation"],
                TrustCeiling = RepositoryTrustLevel.TrustedMutation,
            },
        };
        var plan = CreatePlan(assignment);

        // Act / Assert
        Assert.Throws<UnauthorizedAccessException>(() => DelegationPlanValidator.Validate(plan));
    }

    /// <summary>Verifies conservative partitioning rejects path and shared-surface ambiguity.</summary>
    [Fact]
    public void Partitioner_OverlappingAndSharedAssignments_FallBackToSerial()
    {
        // Arrange
        var first = CreateAssignment(AgentRole.Implementer, "src/A.cs");
        var second = CreateAssignment(AgentRole.Implementer, "src/A.cs");
        var shared = CreateAssignment(AgentRole.Implementer, "Directory.Packages.props");
        var plan = CreatePlan(first, second, shared) with { ImplementationAuthorized = true };

        // Act
        var decision = new AssignmentPartitioner().Partition(plan);

        // Assert
        Assert.False(decision.IsParallelSafe);
        Assert.Equal(3, decision.SerialAssignments.Count);
        Assert.Contains(decision.Conflicts, item => item.Code == "assignment-overlap");
        Assert.Contains(decision.Conflicts, item => item.Code == "shared-surface");
    }

    /// <summary>Verifies disjoint proven implementation ownership can run in parallel.</summary>
    [Fact]
    public void Partitioner_DisjointAssignments_AreParallelSafe()
    {
        // Arrange
        var first = CreateAssignment(AgentRole.Implementer, "src/A.cs");
        var second = CreateAssignment(AgentRole.Implementer, "tests/B.cs");
        var plan = CreatePlan(first, second) with { ImplementationAuthorized = true };

        // Act
        var decision = new AssignmentPartitioner().Partition(plan);

        // Assert
        Assert.True(decision.IsParallelSafe);
        Assert.Equal(2, decision.ParallelAssignments.Count);
        Assert.Empty(decision.Conflicts);
    }

    /// <summary>Verifies child tool policy can narrow but never widen parent authority.</summary>
    [Fact]
    public void ToolPolicy_NarrowsTrustToolsNetworkAndProcesses()
    {
        // Arrange
        var assignment = CreateAssignment(AgentRole.Explorer, "src/A.cs") with
        {
            Policy = CreatePolicy() with
            {
                AllowedToolIds = ["read_file", "search"],
                DeniedToolIds = ["search"],
                TrustCeiling = RepositoryTrustLevel.TrustedRead,
                AllowNetwork = false,
                AllowProcesses = false,
            },
        };
        var plan = CreatePlan(assignment);
        var parent = new ToolInvocationContext
        {
            RepositoryPath = Environment.CurrentDirectory,
            TrustLevel = RepositoryTrustLevel.FullyTrustedAutomation,
            AllowedToolIds = ["read_file", "search", "run_process"],
            AllowedExecutables = ["dotnet"],
            AllowedNetworkHosts = ["example.test"],
            AllowedSecretReferences = ["secrets:key"],
            RequestedBy = "parent",
        };

        // Act
        var child = AgentToolPolicy.Scope(
            parent,
            plan,
            assignment,
            Environment.CurrentDirectory);

        // Assert
        Assert.Equal(RepositoryTrustLevel.TrustedRead, child.TrustLevel);
        Assert.Equal(["read_file", "search"], child.AllowedToolIds);
        Assert.Contains("search", child.DeniedToolIds);
        Assert.Equal(["dotnet"], child.AllowedExecutables);
        Assert.Empty(child.AllowedNetworkHosts);
        Assert.Empty(child.AllowedSecretReferences);
    }

    /// <summary>Verifies child path scope can only narrow the parent's approved roots.</summary>
    [Fact]
    public void ToolPolicy_IntersectsChildAndParentRoots()
    {
        // Arrange
        var assignment = CreateAssignment(AgentRole.Explorer, "src/A.cs");
        var plan = CreatePlan(assignment);
        var parent = new ToolInvocationContext
        {
            RepositoryPath = Environment.CurrentDirectory,
            TrustLevel = RepositoryTrustLevel.TrustedRead,
            ApprovedRoots = ["tests"],
            RequestedBy = "parent",
        };

        // Act / Assert
        Assert.Throws<UnauthorizedAccessException>(() => AgentToolPolicy.Scope(
            parent,
            plan,
            assignment,
            Environment.CurrentDirectory));
    }

    /// <summary>Verifies an empty child allowlist remains an explicit deny-all policy.</summary>
    [Fact]
    public void ToolPolicy_EmptyChildAllowlist_DeniesEveryTool()
    {
        // Arrange
        var assignment = CreateAssignment(AgentRole.Explorer, "src/A.cs") with
        {
            Policy = CreatePolicy() with { AllowedToolIds = [] },
        };
        var plan = CreatePlan(assignment);
        var parent = new ToolInvocationContext
        {
            RepositoryPath = Environment.CurrentDirectory,
            TrustLevel = RepositoryTrustLevel.TrustedRead,
            RequestedBy = "parent",
        };

        // Act
        var child = AgentToolPolicy.Scope(
            parent,
            plan,
            assignment,
            Environment.CurrentDirectory);
        var decision = new DefaultPolicyEngine().Evaluate(
            new ReadFileTool(),
            new ReadFileInput { Path = "src/A.cs" },
            child);

        // Assert
        Assert.True(child.DenyAllTools);
        Assert.Empty(child.AllowedToolIds);
        Assert.False(decision.IsAllowed);
    }

    /// <summary>Verifies a disjoint parent/child allowlist intersection remains deny-all.</summary>
    [Fact]
    public void ToolPolicy_EmptyAllowlistIntersection_DeniesEveryTool()
    {
        // Arrange
        var assignment = CreateAssignment(AgentRole.Explorer, "src/A.cs");
        var plan = CreatePlan(assignment);
        var parent = new ToolInvocationContext
        {
            RepositoryPath = Environment.CurrentDirectory,
            TrustLevel = RepositoryTrustLevel.TrustedRead,
            AllowedToolIds = ["search"],
            RequestedBy = "parent",
        };

        // Act
        var child = AgentToolPolicy.Scope(
            parent,
            plan,
            assignment,
            Environment.CurrentDirectory);

        // Assert
        Assert.True(child.DenyAllTools);
        Assert.Empty(child.AllowedToolIds);
    }

    /// <summary>Verifies hierarchical usage accounting rejects oversubscription atomically.</summary>
    [Fact]
    public void BudgetLedger_RejectsExhaustionWithoutChangingUsage()
    {
        // Arrange
        var ledger = new AgentBudgetLedger(new AgentResourceBudget
        {
            ModelTokens = 10,
            ToolCalls = 1,
            WallTime = TimeSpan.FromMinutes(1),
        });
        ledger.Charge(new AgentResourceUsage { ModelTokens = 5, ToolCalls = 1 });

        // Act / Assert
        Assert.Throws<InvalidOperationException>(() =>
            ledger.Charge(new AgentResourceUsage { ModelTokens = 6 }));
        Assert.Equal(5, ledger.Snapshot.ModelTokens);
        Assert.Equal(1, ledger.Snapshot.ToolCalls);
    }

    /// <summary>Verifies every resource dimension rejects negative refund attempts.</summary>
    [Fact]
    public void BudgetLedger_RejectsNegativeChargesWithoutChangingUsage()
    {
        // Arrange
        var ledger = new AgentBudgetLedger(new AgentResourceBudget());
        AgentResourceUsage[] negativeCharges =
        [
            new() { ModelTokens = -1 },
            new() { ToolCalls = -1 },
            new() { EvidenceItems = -1 },
            new() { Files = -1 },
            new() { Bytes = -1 },
            new() { Mutations = -1 },
            new() { Processes = -1 },
            new() { Builds = -1 },
            new() { Tests = -1 },
            new() { Corrections = -1 },
            new() { WallTime = TimeSpan.FromTicks(-1) },
        ];

        // Act / Assert
        Assert.All(negativeCharges, delta =>
            Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Charge(delta)));
        Assert.Equal(new AgentResourceUsage(), ledger.Snapshot);
    }

    /// <summary>Verifies migration 4 and delegation checkpoints round-trip safely.</summary>
    [Fact]
    public async Task Persistence_Migration4_RoundTripsDelegationCheckpoint()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"threadsmith-agents-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        try
        {
            await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync();
            var plan = CreatePlan(CreateAssignment(AgentRole.Explorer, "src/A.cs"));
            var checkpoint = new DelegationCheckpoint
            {
                DelegationId = plan.DelegationId,
                Provenance = plan.Provenance,
                Phase = DelegationCheckpointPhase.ResearchJoined,
                ChildOutcomes = [],
                NextAction = "synthesize findings",
                RecordedAt = DateTimeOffset.UtcNow,
            };
            var store = new DelegationCheckpointStore(connectionString);

            // Act
            Assert.True(await store.SaveAsync(checkpoint));
            var restored = await store.GetAsync(plan.DelegationId);

            // Assert
            Assert.NotNull(restored);
            Assert.Equal(DelegationCheckpointPhase.ResearchJoined, restored.Phase);
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM delegation_worktree_leases;";
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync() ?? -1L));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Verifies a stale progress revision cannot replace a terminal SQLite checkpoint.</summary>
    [Fact]
    public async Task Persistence_StaleProgressRevision_DoesNotReplaceTerminalCheckpoint()
    {
        // Arrange
        var path = Path.Combine(Path.GetTempPath(), $"threadsmith-agents-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Pooling=False";
        try
        {
            await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync();
            var plan = CreatePlan(CreateAssignment(AgentRole.Explorer, "src/A.cs"));
            var terminal = new DelegationCheckpoint
            {
                DelegationId = plan.DelegationId,
                Provenance = plan.Provenance,
                Phase = DelegationCheckpointPhase.ResearchJoined,
                ChildOutcomes = [],
                NextAction = "synthesize findings",
                RecordedAt = DateTimeOffset.UtcNow,
                Revision = 4,
            };
            var staleProgress = terminal with
            {
                Phase = DelegationCheckpointPhase.ChildrenRunning,
                NextAction = "observe active children",
                RecordedAt = terminal.RecordedAt.AddMilliseconds(1),
                Revision = 3,
            };
            var store = new DelegationCheckpointStore(connectionString);

            // Act
            Assert.True(await store.SaveAsync(terminal));
            Assert.False(await store.SaveAsync(staleProgress));
            var restored = await store.GetAsync(plan.DelegationId);

            // Assert
            Assert.NotNull(restored);
            Assert.Equal(DelegationCheckpointPhase.ResearchJoined, restored.Phase);
            Assert.Equal(4, restored.Revision);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Verifies integration rejects stale parent state, out-of-scope paths, and worker overlap.</summary>
    [Fact]
    public void Integration_DetectsStaleScopeAndWorkerConflicts()
    {
        // Arrange
        var first = CreateAssignment(AgentRole.Implementer, "src/A.cs");
        var second = CreateAssignment(AgentRole.Implementer, "tests/B.cs");
        var plan = CreatePlan(first, second) with { ImplementationAuthorized = true };
        var left = CreateChangeSet(plan, first, ["src/A.cs", "outside.txt"]);
        var right = CreateChangeSet(plan, second, ["src/A.cs"]);

        // Act
        var conflicts = new WorkerIntegrationCoordinator().DetectConflicts(
            plan,
            [left, right],
            "changed-parent");

        // Assert
        Assert.Contains(conflicts, item => item.Code == "stale-parent-baseline");
        Assert.Contains(conflicts, item => item.Code == "assignment-scope-exceeded");
        Assert.Contains(conflicts, item => item.Code == "worker-path-conflict");
    }

    /// <summary>Verifies worker path aliases cannot escape scope or evade overlap detection.</summary>
    [Theory]
    [InlineData("src/../outside.cs")]
    [InlineData("./src/A.cs")]
    [InlineData("/src/A.cs")]
    public void Integration_RejectsNonCanonicalWorkerPaths(string path)
    {
        // Arrange
        var assignment = CreateAssignment(AgentRole.Implementer, "src/A.cs");
        var plan = CreatePlan(assignment) with { ImplementationAuthorized = true };
        var changeSet = CreateChangeSet(plan, assignment, [path]);

        // Act
        var conflicts = new WorkerIntegrationCoordinator().DetectConflicts(
            plan,
            [changeSet],
            plan.Provenance.BaselineIdentity);

        // Assert
        Assert.Contains(conflicts, item => item.Code == "invalid-worker-path");
    }

    /// <summary>Verifies real detached worker worktrees are confined, frozen, and explicitly removed.</summary>
    [Fact]
    public async Task WorktreeCoordinator_CreatesFreezesAndRemovesRealDetachedWorktree()
    {
        // Arrange
        var repository = Path.Combine(Path.GetTempPath(), $"threadsmith-parent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repository);
        try
        {
            await RunGitAsync(repository, "init");
            await File.WriteAllTextAsync(Path.Combine(repository, "A.txt"), "baseline\n");
            await RunGitAsync(repository, "add", "A.txt");
            await RunGitAsync(repository, "-c", "user.name=Threadsmith", "-c", "user.email=test@example.invalid", "commit", "-m", "baseline");
            var revision = (await RunGitAsync(repository, "rev-parse", "HEAD")).Trim();
            var assignment = CreateAssignment(AgentRole.Implementer, "A.txt");
            var plan = CreatePlan(assignment) with { ImplementationAuthorized = true };
            var coordinator = new WorkerWorktreeCoordinator(new GitWorktreeManager());

            // Act
            var lease = await coordinator.CreateAsync(
                plan,
                assignment,
                repository,
                revision);
            var frozen = await coordinator.FreezeAsync(lease);
            await coordinator.RemoveAsync(frozen);

            // Assert
            Assert.True(frozen.IsFrozen);
            Assert.False(Directory.Exists(frozen.RepositoryPath));
        }
        finally
        {
            if (Directory.Exists(repository))
            {
                foreach (var file in Directory.EnumerateFiles(repository, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(repository, recursive: true);
            }
        }
    }

    /// <summary>Verifies callers cannot forge implementer authority with another assignment's id.</summary>
    [Fact]
    public async Task WorktreeCoordinator_RejectsForgedImplementerAssignment()
    {
        // Arrange
        var explorer = CreateAssignment(AgentRole.Explorer, "src/A.cs");
        var plan = CreatePlan(explorer) with { ImplementationAuthorized = true };
        var forged = explorer with
        {
            ChildRunId = RunId.New(),
            Role = AgentRole.Implementer,
            Mode = AgentRunMode.IsolatedWorktreeMutation,
        };
        var coordinator = new WorkerWorktreeCoordinator(new GitWorktreeManager());

        // Act / Assert
        _ = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => coordinator.CreateAsync(
            plan,
            forged,
            Environment.CurrentDirectory,
            "HEAD"));
    }

    private static DelegationPlan CreatePlan(params AgentAssignment[] assignments)
    {
        var budget = new AgentResourceBudget
        {
            ModelTokens = assignments.Sum(item => item.Budget.ModelTokens),
            ToolCalls = assignments.Sum(item => item.Budget.ToolCalls),
            EvidenceItems = assignments.Sum(item => item.Budget.EvidenceItems),
            Files = assignments.Sum(item => item.Budget.Files),
            Bytes = assignments.Sum(item => item.Budget.Bytes),
            Mutations = assignments.Sum(item => item.Budget.Mutations),
            Processes = assignments.Sum(item => item.Budget.Processes),
            Builds = assignments.Sum(item => item.Budget.Builds),
            Tests = assignments.Sum(item => item.Budget.Tests),
            Corrections = assignments.Sum(item => item.Budget.Corrections),
            WallTime = TimeSpan.FromTicks(assignments.Sum(item => item.Budget.WallTime.Ticks)),
        };
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
            ParentBudget = budget,
            ImplementationAuthorized = assignments.Any(item => item.Role == AgentRole.Implementer),
            AcceptedAt = DateTimeOffset.UtcNow,
        };
    }

    private static AgentAssignment CreateAssignment(AgentRole role, string path)
    {
        var implementer = role == AgentRole.Implementer;
        var reviewer = role is AgentRole.SecurityReviewer
            or AgentRole.TestReviewer
            or AgentRole.PerformanceReviewer
            or AgentRole.ArchitectureReviewer;
        return new AgentAssignment
        {
            AssignmentId = AgentAssignmentId.New(),
            ChildRunId = RunId.New(),
            Role = role,
            Mode = implementer
                ? AgentRunMode.IsolatedWorktreeMutation
                : reviewer ? AgentRunMode.ReadOnlyReview : AgentRunMode.ReadOnlyBaseline,
            Objective = $"Inspect {path}",
            Tasks = ["Return structured evidence"],
            OutputSchema = role switch
            {
                AgentRole.Explorer => "agent-findings/1",
                AgentRole.Implementer => "worker-change-set/1",
                _ => "review-findings/1",
            },
            StoppingCondition = "Stop after the assigned scope is covered.",
            Deadline = DateTimeOffset.UtcNow.AddMinutes(5),
            Scope = new AgentAssignmentScope
            {
                Files = [path],
                IsOwnershipProven = true,
            },
            Policy = CreatePolicy(),
            Budget = new AgentResourceBudget
            {
                ModelTokens = 1_000,
                ToolCalls = 4,
                EvidenceItems = 8,
                Files = 8,
                Bytes = 1_024,
                Mutations = implementer ? 2 : 0,
                Processes = implementer ? 1 : 0,
                Builds = implementer ? 1 : 0,
                Tests = implementer ? 1 : 0,
                Corrections = implementer ? 1 : 0,
                WallTime = TimeSpan.FromMinutes(5),
            },
            PlanStepIds = implementer ? [StepId.New()] : [],
        };
    }

    private static AgentPolicySnapshot CreatePolicy()
    {
        return new AgentPolicySnapshot
        {
            AllowedToolIds = ["read_file"],
            TrustCeiling = RepositoryTrustLevel.TrustedRead,
            ModelSelectionRationale = "test configured model",
            ContextPolicyVersion = "agent-context/1",
            ToolPolicyVersion = "agent-tools/1",
        };
    }

    private static AgentRunOutcome CreateFindingOutcome(AgentAssignment assignment, int generation)
    {
        return new AgentRunOutcome
        {
            AssignmentId = assignment.AssignmentId,
            ChildRunId = assignment.ChildRunId,
            Role = assignment.Role,
            Generation = generation,
            Status = AgentRunStatus.Completed,
            Usage = new AgentResourceUsage(),
            Reason = "completed",
            Findings = new AgentFindingSet
            {
                AssignmentId = assignment.AssignmentId,
                ChildRunId = assignment.ChildRunId,
                Generation = generation,
            },
        };
    }

    private static AgentRunOutcome CreateUsableFindingOutcome(
        AgentAssignment assignment,
        int generation)
    {
        return CreateFindingOutcome(assignment, generation) with
        {
            Findings = new AgentFindingSet
            {
                AssignmentId = assignment.AssignmentId,
                ChildRunId = assignment.ChildRunId,
                Generation = generation,
                Summary = "usable sibling finding",
                Findings =
                [
                    new AgentFinding
                    {
                        FindingId = Guid.NewGuid(),
                        Category = "behavior",
                        Summary = "usable sibling finding",
                        Confidence = 1,
                    },
                ],
            },
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

    private static async Task<string> RunGitAsync(string directory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    private sealed class FixedRunner : IAgentAssignmentRunner
    {
        private readonly AgentRunOutcome _outcome;

        public FixedRunner(AgentRunOutcome outcome)
        {
            _outcome = outcome;
        }

        public Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_outcome);
        }
    }

    private sealed class TrackingRunner : IAgentAssignmentRunner
    {
        private readonly TimeSpan _delay;
        private int _active;
        private int _maximumActive;

        public TrackingRunner(TimeSpan delay)
        {
            _delay = delay;
        }

        public int Active => Volatile.Read(ref _active);

        public int MaximumActive => Volatile.Read(ref _maximumActive);

        public async Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            int observed;
            do
            {
                observed = Volatile.Read(ref _maximumActive);
            }
            while (active > observed
                && Interlocked.CompareExchange(ref _maximumActive, active, observed) != observed);

            try
            {
                await Task.Delay(_delay, cancellationToken);
                return CreateFindingOutcome(assignment, plan.Provenance.Generation);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class ReturningFailureRunner : IAgentAssignmentRunner
    {
        private readonly AgentAssignmentId _failedAssignmentId;

        public ReturningFailureRunner(AgentAssignmentId failedAssignmentId)
        {
            _failedAssignmentId = failedAssignmentId;
        }

        public async Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            if (assignment.AssignmentId == _failedAssignmentId)
            {
                return new AgentRunOutcome
                {
                    AssignmentId = assignment.AssignmentId,
                    ChildRunId = assignment.ChildRunId,
                    Role = assignment.Role,
                    Generation = plan.Provenance.Generation,
                    Status = AgentRunStatus.Failed,
                    Usage = new AgentResourceUsage(),
                    Reason = "runner returned failure",
                };
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            return CreateFindingOutcome(assignment, plan.Provenance.Generation);
        }
    }

    private sealed class CompletedBeforeFailureRunner : IAgentAssignmentRunner
    {
        private readonly AgentAssignmentId _completedAssignmentId;
        private readonly TaskCompletionSource _completed = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly AgentAssignmentId _failedAssignmentId;

        public CompletedBeforeFailureRunner(
            AgentAssignmentId completedAssignmentId,
            AgentAssignmentId failedAssignmentId)
        {
            _completedAssignmentId = completedAssignmentId;
            _failedAssignmentId = failedAssignmentId;
        }

        public async Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            if (assignment.AssignmentId == _completedAssignmentId)
            {
                var outcome = CreateUsableFindingOutcome(
                    assignment,
                    plan.Provenance.Generation);
                _completed.TrySetResult();
                return outcome;
            }

            if (assignment.AssignmentId != _failedAssignmentId)
            {
                throw new InvalidOperationException("The runner received an unexpected assignment.");
            }

            await _completed.Task.WaitAsync(cancellationToken);
            return new AgentRunOutcome
            {
                AssignmentId = assignment.AssignmentId,
                ChildRunId = assignment.ChildRunId,
                Role = assignment.Role,
                Generation = plan.Provenance.Generation,
                Status = AgentRunStatus.Failed,
                Usage = new AgentResourceUsage(),
                Reason = "runner returned failure",
            };
        }
    }

    private sealed class RecordingCheckpointStore : IDelegationCheckpointStore
    {
        public DelegationCheckpoint? Latest { get; private set; }

        public Task<bool> SaveAsync(
            DelegationCheckpoint checkpoint,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Latest is not null && Latest.Revision >= checkpoint.Revision)
            {
                return Task.FromResult(false);
            }

            Latest = checkpoint;
            return Task.FromResult(true);
        }

        public Task<DelegationCheckpoint?> GetAsync(
            DelegationId delegationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Latest?.DelegationId == delegationId ? Latest : null);
        }
    }
}
