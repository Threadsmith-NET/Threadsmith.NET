namespace Threadsmith.Execution;

using System.Collections.Concurrent;
using System.Threading.Channels;
using Threadsmith.Core;

/// <summary>Conservative configured limits for in-process child scheduling.</summary>
public sealed record AgentSchedulerOptions
{
    /// <summary>Maximum queued assignments accepted for one scheduler.</summary>
    public int QueueCapacity { get; init; } = 32;

    /// <summary>Maximum active children across all parents.</summary>
    public int MaximumActiveChildren { get; init; } = 4;

    /// <summary>Maximum active children within one parent.</summary>
    public int MaximumActiveChildrenPerParent { get; init; } = 3;

    /// <summary>Maximum active implementation workers.</summary>
    public int MaximumActiveImplementers { get; init; } = 2;

    /// <summary>Maximum duration allowed for bounded shutdown joins.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Validates and freezes one-level delegation contracts.</summary>
public static class DelegationPlanValidator
{
    private const int MaximumAssignments = 16;
    private const int MaximumTextCharacters = 4_096;

    /// <summary>Validates identities, graph shape, modes, authority, budgets, and bounds.</summary>
    public static void Validate(DelegationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SchemaVersion != 1
            || plan.DelegationId == default
            || plan.Provenance.SessionId == default
            || plan.Provenance.ParentRunId == default
            || plan.Provenance.WorkspaceId == default
            || plan.Provenance.Attempt < 1
            || plan.Provenance.Generation < 1)
        {
            throw new InvalidDataException("Delegation identity, schema, attempt, or generation is invalid.");
        }

        ValidateText(plan.Provenance.RepositoryIdentity, nameof(plan.Provenance.RepositoryIdentity));
        ValidateText(plan.Provenance.BaselineIdentity, nameof(plan.Provenance.BaselineIdentity));
        ValidateBudget(plan.ParentBudget);
        if (plan.Assignments.Count is < 1 or > MaximumAssignments)
        {
            throw new InvalidDataException($"A delegation must contain 1-{MaximumAssignments} assignments.");
        }

        var assignments = new Dictionary<AgentAssignmentId, AgentAssignment>();
        var childRuns = new HashSet<RunId>();
        foreach (var assignment in plan.Assignments)
        {
            ValidateAssignment(plan, assignment);
            if (!assignments.TryAdd(assignment.AssignmentId, assignment)
                || !childRuns.Add(assignment.ChildRunId))
            {
                throw new InvalidDataException("Assignment and child-run identities must be unique.");
            }
        }

        foreach (var assignment in plan.Assignments)
        {
            if (assignment.Dependencies.Count != assignment.Dependencies.Distinct().Count()
                || assignment.Dependencies.Any(id => id == assignment.AssignmentId || !assignments.ContainsKey(id)))
            {
                throw new InvalidDataException("Assignment dependencies must be unique, external to self, and present.");
            }
        }

        DetectCycles(assignments);
        ValidateAggregateBudget(plan);
    }

    private static void ValidateAssignment(DelegationPlan plan, AgentAssignment assignment)
    {
        if (assignment.AssignmentId == default || assignment.ChildRunId == default)
        {
            throw new InvalidDataException("Every assignment requires stable assignment and child-run ids.");
        }

        ValidateText(assignment.Objective, nameof(assignment.Objective));
        ValidateText(assignment.OutputSchema, nameof(assignment.OutputSchema));
        ValidateText(assignment.StoppingCondition, nameof(assignment.StoppingCondition));
        ValidateText(assignment.Policy.ModelSelectionRationale, nameof(assignment.Policy.ModelSelectionRationale));
        ValidateText(assignment.Policy.ContextPolicyVersion, nameof(assignment.Policy.ContextPolicyVersion));
        ValidateText(assignment.Policy.ToolPolicyVersion, nameof(assignment.Policy.ToolPolicyVersion));
        if (assignment.Deadline <= plan.AcceptedAt || assignment.Tasks.Count is < 1 or > 32)
        {
            throw new InvalidDataException("Every assignment needs tasks and a deadline after plan acceptance.");
        }

        if (assignment.Tasks.Any(item => string.IsNullOrWhiteSpace(item) || item.Length > MaximumTextCharacters))
        {
            throw new InvalidDataException("Assignment tasks must be bounded non-empty text.");
        }

        bool implementer = assignment.Role == AgentRole.Implementer;
        if (implementer != (assignment.Mode == AgentRunMode.IsolatedWorktreeMutation)
            || (implementer && (!plan.ImplementationAuthorized || assignment.PlanStepIds.Count == 0)))
        {
            throw new UnauthorizedAccessException(
                "Only explicitly authorized implementers may use isolated-worktree mutation mode.");
        }

        bool reviewer = assignment.Role is AgentRole.SecurityReviewer
            or AgentRole.TestReviewer
            or AgentRole.PerformanceReviewer
            or AgentRole.ArchitectureReviewer;
        if (reviewer != (assignment.Mode == AgentRunMode.ReadOnlyReview))
        {
            throw new InvalidDataException("Reviewer roles require read-only review mode.");
        }

        if (assignment.Role == AgentRole.Explorer && assignment.Mode != AgentRunMode.ReadOnlyBaseline)
        {
            throw new InvalidDataException("Explorers require read-only baseline mode.");
        }

        if (!implementer && assignment.Policy.TrustCeiling > RepositoryTrustLevel.TrustedBuild)
        {
            throw new UnauthorizedAccessException("Read-only children cannot receive mutation trust.");
        }

        if (!implementer && assignment.Policy.AllowedToolIds.Any(IsMutationTool))
        {
            throw new UnauthorizedAccessException("Read-only children cannot receive mutation tools.");
        }

        ValidateBudget(assignment.Budget);
        ValidateScope(assignment.Scope);
    }

    private static void ValidateScope(AgentAssignmentScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var values = scope.Files
            .Concat(scope.Directories)
            .Concat(scope.Projects)
            .Concat(scope.Symbols)
            .Concat(scope.SharedSurfaces);
        foreach (string value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 1_024 || Path.IsPathRooted(value))
            {
                throw new InvalidDataException("Assignment ownership must be bounded and repository-relative.");
            }

            string normalized = value.Replace('\\', '/');
            if (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Any(segment => segment is "." or "..")
                || normalized.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(".git/", StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Assignment ownership escapes or includes Git metadata.");
            }
        }
    }

    private static bool IsMutationTool(string toolId)
    {
        return toolId.Contains("write", StringComparison.OrdinalIgnoreCase)
            || toolId.Contains("mutation", StringComparison.OrdinalIgnoreCase)
            || toolId.Contains("apply", StringComparison.OrdinalIgnoreCase);
    }

    private static void DetectCycles(IReadOnlyDictionary<AgentAssignmentId, AgentAssignment> assignments)
    {
        var visiting = new HashSet<AgentAssignmentId>();
        var visited = new HashSet<AgentAssignmentId>();
        foreach (var id in assignments.Keys)
        {
            Visit(id, assignments, visiting, visited);
        }
    }

    private static void Visit(
        AgentAssignmentId id,
        IReadOnlyDictionary<AgentAssignmentId, AgentAssignment> assignments,
        HashSet<AgentAssignmentId> visiting,
        HashSet<AgentAssignmentId> visited)
    {
        if (visited.Contains(id))
        {
            return;
        }

        if (!visiting.Add(id))
        {
            throw new InvalidDataException("Delegation dependency graph contains a cycle.");
        }

        foreach (var dependency in assignments[id].Dependencies)
        {
            Visit(dependency, assignments, visiting, visited);
        }

        visiting.Remove(id);
        visited.Add(id);
    }

    private static void ValidateAggregateBudget(DelegationPlan plan)
    {
        AgentResourceBudget total = new()
        {
            ModelTokens = plan.Assignments.Sum(item => item.Budget.ModelTokens),
            ToolCalls = plan.Assignments.Sum(item => item.Budget.ToolCalls),
            EvidenceItems = plan.Assignments.Sum(item => item.Budget.EvidenceItems),
            Files = plan.Assignments.Sum(item => item.Budget.Files),
            Bytes = plan.Assignments.Sum(item => item.Budget.Bytes),
            Mutations = plan.Assignments.Sum(item => item.Budget.Mutations),
            Processes = plan.Assignments.Sum(item => item.Budget.Processes),
            Builds = plan.Assignments.Sum(item => item.Budget.Builds),
            Tests = plan.Assignments.Sum(item => item.Budget.Tests),
            Corrections = plan.Assignments.Sum(item => item.Budget.Corrections),
            WallTime = TimeSpan.FromTicks(plan.Assignments.Sum(item => item.Budget.WallTime.Ticks)),
        };
        if (total.ModelTokens > plan.ParentBudget.ModelTokens
            || total.ToolCalls > plan.ParentBudget.ToolCalls
            || total.EvidenceItems > plan.ParentBudget.EvidenceItems
            || total.Files > plan.ParentBudget.Files
            || total.Bytes > plan.ParentBudget.Bytes
            || total.Mutations > plan.ParentBudget.Mutations
            || total.Processes > plan.ParentBudget.Processes
            || total.Builds > plan.ParentBudget.Builds
            || total.Tests > plan.ParentBudget.Tests
            || total.Corrections > plan.ParentBudget.Corrections
            || total.WallTime > plan.ParentBudget.WallTime)
        {
            throw new InvalidDataException("Child budget reservations exceed the dominating parent budget.");
        }
    }

    private static void ValidateBudget(AgentResourceBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.ModelTokens < 0 || budget.ToolCalls < 0 || budget.EvidenceItems < 0
            || budget.Files < 0 || budget.Bytes < 0 || budget.Mutations < 0
            || budget.Processes < 0 || budget.Builds < 0 || budget.Tests < 0
            || budget.Corrections < 0 || budget.WallTime <= TimeSpan.Zero)
        {
            throw new InvalidDataException("Agent resource budgets must be finite and non-negative.");
        }
    }

    private static void ValidateText(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > MaximumTextCharacters)
        {
            throw new InvalidDataException($"{name} exceeds the bounded text limit.");
        }
    }
}

/// <summary>Conservatively partitions assignment ownership and falls back to serial execution.</summary>
public sealed class AssignmentPartitioner : IAssignmentPartitioner
{
    private static readonly string[] ExclusiveNames =
    [
        "directory.build.props",
        "directory.build.targets",
        "directory.packages.props",
        "global.json",
        "nuget.config",
    ];

    /// <inheritdoc />
    public AssignmentPartitionDecision Partition(DelegationPlan plan)
    {
        DelegationPlanValidator.Validate(plan);
        AgentAssignment[] workers =
        [
            .. plan.Assignments
                .Where(item => item.Role == AgentRole.Implementer)
                .OrderBy(item => item.AssignmentId.Value),
        ];
        var conflicts = new List<AgentConflict>();
        var serial = new HashSet<AgentAssignmentId>();
        for (int leftIndex = 0; leftIndex < workers.Length; leftIndex++)
        {
            var left = workers[leftIndex];
            if (!left.Scope.IsOwnershipProven)
            {
                AddConflict(conflicts, serial, "ownership-unproven", "Assignment ownership is not proven.", left);
            }

            if (HasExclusiveSurface(left.Scope))
            {
                AddConflict(conflicts, serial, "shared-surface", "Assignment owns an exclusive shared surface.", left);
            }

            for (int rightIndex = leftIndex + 1; rightIndex < workers.Length; rightIndex++)
            {
                var right = workers[rightIndex];
                var paths = FindOverlap(left.Scope, right.Scope);
                if (paths.Count == 0)
                {
                    continue;
                }

                serial.Add(left.AssignmentId);
                serial.Add(right.AssignmentId);
                conflicts.Add(new AgentConflict(
                    "assignment-overlap",
                    "Implementation ownership overlaps or is ambiguous.",
                    [left.AssignmentId, right.AssignmentId],
                    paths));
            }
        }

        AgentAssignmentId[] serialIds = [.. serial.OrderBy(item => item.Value)];
        AgentAssignmentId[] parallelIds =
        [
            .. workers
                .Select(item => item.AssignmentId)
                .Where(id => !serial.Contains(id)),
        ];
        return new AssignmentPartitionDecision
        {
            ParallelAssignments = parallelIds,
            SerialAssignments = serialIds,
            Conflicts = conflicts,
            IsParallelSafe = serialIds.Length == 0,
        };
    }

    private static void AddConflict(
        List<AgentConflict> conflicts,
        HashSet<AgentAssignmentId> serial,
        string code,
        string summary,
        AgentAssignment assignment)
    {
        serial.Add(assignment.AssignmentId);
        conflicts.Add(new AgentConflict(
            code,
            summary,
            [assignment.AssignmentId],
            [.. assignment.Scope.Files.Concat(assignment.Scope.SharedSurfaces)]));
    }

    private static bool HasExclusiveSurface(AgentAssignmentScope scope)
    {
        return scope.SharedSurfaces.Count > 0
            || scope.Files.Any(file => ExclusiveNames.Contains(
                Path.GetFileName(file),
                StringComparer.OrdinalIgnoreCase))
            || scope.Files.Any(file => Path.GetExtension(file).Equals(".sln", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> FindOverlap(
        AgentAssignmentScope left,
        AgentAssignmentScope right)
    {
        var overlaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] leftFiles = [.. left.Files.Select(Normalize)];
        string[] rightFiles = [.. right.Files.Select(Normalize)];
        foreach (string file in leftFiles.Intersect(rightFiles, StringComparer.OrdinalIgnoreCase))
        {
            overlaps.Add(file);
        }

        foreach (string file in leftFiles)
        {
            foreach (string directory in right.Directories.Select(Normalize))
            {
                if (IsUnder(file, directory))
                {
                    overlaps.Add(file);
                }
            }
        }

        foreach (string file in rightFiles)
        {
            foreach (string directory in left.Directories.Select(Normalize))
            {
                if (IsUnder(file, directory))
                {
                    overlaps.Add(file);
                }
            }
        }

        foreach (string directory in left.Directories.Select(Normalize))
        {
            foreach (string other in right.Directories.Select(Normalize))
            {
                if (IsUnder(directory, other) || IsUnder(other, directory))
                {
                    overlaps.Add(directory.Length <= other.Length ? directory : other);
                }
            }
        }

        foreach (string symbol in left.Symbols.Intersect(right.Symbols, StringComparer.Ordinal))
        {
            overlaps.Add($"symbol:{symbol}");
        }

        foreach (string project in left.Projects.Intersect(right.Projects, StringComparer.OrdinalIgnoreCase))
        {
            overlaps.Add($"project:{Normalize(project)}");
        }

        return [.. overlaps.OrderBy(item => item, StringComparer.Ordinal)];
    }

    private static bool IsUnder(string path, string directory)
    {
        string prefix = directory.EndsWith("/", StringComparison.Ordinal) ? directory : directory + "/";
        return path.Equals(directory, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        return path.Replace('\\', '/').Trim('/');
    }
}

/// <summary>Runs bounded child assignments as observed in-process asynchronous operations.</summary>
public sealed class AgentRunScheduler : IAgentRunScheduler, IAsyncDisposable
{
    private readonly ConcurrentDictionary<(DelegationId, AgentAssignmentId), CancellationTokenSource> _children = new();
    private readonly SemaphoreSlim _global;
    private readonly SemaphoreSlim _implementers;
    private readonly ConcurrentDictionary<RunId, ParentLimiter> _parents = new();
    private readonly AgentSchedulerOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private int _admittedOrQueued;
    private int _stopped;

    /// <summary>Initializes a new instance of the <see cref="AgentRunScheduler"/> class.</summary>
    public AgentRunScheduler(AgentSchedulerOptions? options = null)
    {
        _options = options ?? new AgentSchedulerOptions();
        ValidateOptions(_options);
        _global = new SemaphoreSlim(_options.MaximumActiveChildren, _options.MaximumActiveChildren);
        _implementers = new SemaphoreSlim(
            _options.MaximumActiveImplementers,
            _options.MaximumActiveImplementers);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentRunOutcome>> RunAsync(
        DelegationPlan plan,
        IAgentAssignmentRunner runner,
        CancellationToken cancellationToken = default)
    {
        DelegationPlanValidator.Validate(plan);
        ArgumentNullException.ThrowIfNull(runner);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopped) != 0, this);
        int admitted = Interlocked.Add(ref _admittedOrQueued, plan.Assignments.Count);
        if (admitted > _options.QueueCapacity)
        {
            Interlocked.Add(ref _admittedOrQueued, -plan.Assignments.Count);
            throw new InvalidOperationException("The bounded agent queue is full.");
        }

        using var parentCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        var completions = plan.Assignments.ToDictionary(
            item => item.AssignmentId,
            static _ => new TaskCompletionSource<AgentRunOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously));
        Channel<AgentRunOutcome> terminalResults = Channel.CreateBounded<AgentRunOutcome>(
            new BoundedChannelOptions(plan.Assignments.Count)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
        var parentLimiter = AcquireParentLimiter(plan.Provenance.ParentRunId);
        Task<AgentRunOutcome>[] tasks =
        [
            .. plan.Assignments.Select(async assignment =>
            {
                var outcome = await RunObservedAsync(
                    plan,
                    assignment,
                    runner,
                    completions,
                    parentCancellation,
                    parentLimiter.Semaphore,
                    parentCancellation.Token);
                await terminalResults.Writer.WriteAsync(outcome, CancellationToken.None);
                return outcome;
            }),
        ];
        try
        {
            var outcomes = new List<AgentRunOutcome>(plan.Assignments.Count);
            while (outcomes.Count < plan.Assignments.Count)
            {
                outcomes.Add(await terminalResults.Reader.ReadAsync(CancellationToken.None));
            }

            _ = await Task.WhenAll(tasks);
            terminalResults.Writer.TryComplete();
            return [.. outcomes.OrderBy(item => item.AssignmentId.Value)];
        }
        finally
        {
            Interlocked.Add(ref _admittedOrQueued, -plan.Assignments.Count);
            ReleaseParentLimiter(plan.Provenance.ParentRunId, parentLimiter);
        }
    }

    /// <inheritdoc />
    public async Task<bool> CancelAssignmentAsync(
        DelegationId delegationId,
        AgentAssignmentId assignmentId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_children.TryGetValue((delegationId, assignmentId), out var source))
        {
            return false;
        }

        await source.CancelAsync();
        return true;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync();
        foreach (var child in _children.Values)
        {
            await child.CancelAsync();
        }

        var deadline = DateTimeOffset.UtcNow + _options.ShutdownTimeout;
        while (!_children.IsEmpty && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _shutdown.Dispose();
        _global.Dispose();
        _implementers.Dispose();
        foreach (var limiter in _parents.Values)
        {
            limiter.Dispose();
        }

        foreach (var child in _children.Values)
        {
            child.Dispose();
        }
    }

    private async Task<AgentRunOutcome> RunObservedAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        IAgentAssignmentRunner runner,
        IReadOnlyDictionary<AgentAssignmentId, TaskCompletionSource<AgentRunOutcome>> completions,
        CancellationTokenSource parentCancellation,
        SemaphoreSlim parentLimiter,
        CancellationToken cancellationToken)
    {
        AgentRunOutcome outcome;
        try
        {
            foreach (var dependency in assignment.Dependencies)
            {
                var dependencyOutcome = await completions[dependency].Task.WaitAsync(cancellationToken);
                if (dependencyOutcome.Status != AgentRunStatus.Completed)
                {
                    outcome = CreateTerminal(assignment, AgentRunStatus.Cancelled, "dependency did not complete");
                    completions[assignment.AssignmentId].TrySetResult(outcome);
                    return outcome;
                }
            }

            outcome = await RunAdmittedAsync(
                plan,
                assignment,
                runner,
                parentLimiter,
                cancellationToken);
            if (outcome.Generation != plan.Provenance.Generation)
            {
                outcome = outcome with
                {
                    Status = AgentRunStatus.Discarded,
                    Reason = "late result from an obsolete generation",
                    Findings = null,
                    ChangeSet = null,
                    Review = null,
                };
            }

            ValidateOutcome(assignment, outcome);
            if (outcome.Status == AgentRunStatus.Failed)
            {
                await ApplyFailurePolicyAsync(plan, assignment, parentCancellation);
            }
        }
        catch (OperationCanceledException)
        {
            outcome = CreateTerminal(assignment, AgentRunStatus.Cancelled, "child cancellation observed");
        }
        catch (Exception exception)
        {
            outcome = CreateTerminal(
                assignment,
                AgentRunStatus.Failed,
                $"{exception.GetType().Name}: child execution failed");
            await ApplyFailurePolicyAsync(plan, assignment, parentCancellation);
        }

        completions[assignment.AssignmentId].TrySetResult(outcome);
        return outcome;
    }

    private async Task<AgentRunOutcome> RunAdmittedAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        IAgentAssignmentRunner runner,
        SemaphoreSlim parent,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var remaining = assignment.Deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return CreateTerminal(assignment, AgentRunStatus.Cancelled, "assignment deadline elapsed");
        }

        deadline.CancelAfter(remaining < assignment.Budget.WallTime ? remaining : assignment.Budget.WallTime);
        _children[(plan.DelegationId, assignment.AssignmentId)] = deadline;
        bool globalHeld = false;
        bool parentHeld = false;
        bool implementerHeld = false;
        try
        {
            await _global.WaitAsync(deadline.Token);
            globalHeld = true;
            await parent.WaitAsync(deadline.Token);
            parentHeld = true;
            if (assignment.Role == AgentRole.Implementer)
            {
                await _implementers.WaitAsync(deadline.Token);
                implementerHeld = true;
            }

            return await runner.RunAsync(plan, assignment, deadline.Token);
        }
        finally
        {
            if (implementerHeld)
            {
                _implementers.Release();
            }

            if (parentHeld)
            {
                parent.Release();
            }

            if (globalHeld)
            {
                _global.Release();
            }

            _children.TryRemove((plan.DelegationId, assignment.AssignmentId), out _);
        }
    }

    private ParentLimiter AcquireParentLimiter(RunId parentRunId)
    {
        while (true)
        {
            var limiter = _parents.GetOrAdd(
                parentRunId,
                _ => new ParentLimiter(_options.MaximumActiveChildrenPerParent));
            if (limiter.TryAcquireReference())
            {
                return limiter;
            }

            _parents.TryRemove(
                new KeyValuePair<RunId, ParentLimiter>(parentRunId, limiter));
        }
    }

    private void ReleaseParentLimiter(RunId parentRunId, ParentLimiter limiter)
    {
        if (!limiter.ReleaseReference())
        {
            return;
        }

        _parents.TryRemove(new KeyValuePair<RunId, ParentLimiter>(parentRunId, limiter));
        limiter.Dispose();
    }

    private async Task ApplyFailurePolicyAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        CancellationTokenSource parentCancellation)
    {
        if (assignment.FailurePolicy == AgentFailurePolicy.FailDelegation)
        {
            await parentCancellation.CancelAsync();
        }
        else if (assignment.FailurePolicy == AgentFailurePolicy.CancelDependents)
        {
            await CancelDependentsAsync(plan, assignment.AssignmentId);
        }
    }

    private async Task CancelDependentsAsync(DelegationPlan plan, AgentAssignmentId failed)
    {
        var pending = new Queue<AgentAssignmentId>();
        pending.Enqueue(failed);
        var visited = new HashSet<AgentAssignmentId> { failed };
        while (pending.TryDequeue(out var current))
        {
            foreach (var dependent in plan.Assignments.Where(item => item.Dependencies.Contains(current)))
            {
                if (!visited.Add(dependent.AssignmentId))
                {
                    continue;
                }

                if (_children.TryGetValue(
                    (plan.DelegationId, dependent.AssignmentId),
                    out var source))
                {
                    await source.CancelAsync();
                }

                pending.Enqueue(dependent.AssignmentId);
            }
        }
    }

    private static void ValidateOutcome(AgentAssignment assignment, AgentRunOutcome outcome)
    {
        if (outcome.AssignmentId != assignment.AssignmentId
            || outcome.ChildRunId != assignment.ChildRunId
            || outcome.Role != assignment.Role)
        {
            throw new InvalidDataException("Child outcome identity does not match its frozen assignment.");
        }

        if (outcome.Status == AgentRunStatus.Completed)
        {
            int resultCount = (outcome.Findings is null ? 0 : 1)
                + (outcome.ChangeSet is null ? 0 : 1)
                + (outcome.Review is null ? 0 : 1);
            if (resultCount != 1)
            {
                throw new InvalidDataException("A completed child must return exactly one structured result.");
            }

            if ((assignment.Role == AgentRole.Explorer && outcome.Findings is null)
                || (assignment.Role == AgentRole.Implementer && outcome.ChangeSet is null)
                || (assignment.Role is AgentRole.SecurityReviewer
                    or AgentRole.TestReviewer
                    or AgentRole.PerformanceReviewer
                    or AgentRole.ArchitectureReviewer && outcome.Review is null))
            {
                throw new InvalidDataException("Child result type does not match the assigned role.");
            }
        }
    }

    private static AgentRunOutcome CreateTerminal(
        AgentAssignment assignment,
        AgentRunStatus status,
        string reason)
    {
        return new AgentRunOutcome
        {
            AssignmentId = assignment.AssignmentId,
            ChildRunId = assignment.ChildRunId,
            Role = assignment.Role,
            Generation = 0,
            Status = status,
            Usage = new AgentResourceUsage(),
            Reason = reason,
        };
    }

    private static void ValidateOptions(AgentSchedulerOptions options)
    {
        if (options.QueueCapacity < 1 || options.MaximumActiveChildren < 1
            || options.MaximumActiveChildrenPerParent < 1
            || options.MaximumActiveChildrenPerParent > options.MaximumActiveChildren
            || options.MaximumActiveImplementers < 1
            || options.MaximumActiveImplementers > options.MaximumActiveChildren
            || options.ShutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Agent scheduler limits are invalid.");
        }
    }

    private sealed class ParentLimiter : IDisposable
    {
        private readonly Lock _gate = new();
        private int _references;
        private bool _retired;

        internal ParentLimiter(int maximumConcurrency)
        {
            Semaphore = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        }

        internal SemaphoreSlim Semaphore { get; }

        public void Dispose()
        {
            Semaphore.Dispose();
        }

        internal bool TryAcquireReference()
        {
            lock (_gate)
            {
                if (_retired)
                {
                    return false;
                }

                _references++;
                return true;
            }
        }

        internal bool ReleaseReference()
        {
            lock (_gate)
            {
                _references--;
                if (_references > 0)
                {
                    return false;
                }

                _retired = true;
                return true;
            }
        }
    }
}

/// <summary>Coordinates validated delegation scheduling and durable boundaries.</summary>
public sealed class DelegationCoordinator :
    IDelegationCoordinator,
    ICommandHandler<StartDelegationCommand, DelegationCheckpoint>,
    ICommandHandler<GetDelegationCommand, DelegationCheckpoint?>,
    ICommandHandler<CancelDelegationCommand, bool>,
    ICommandHandler<CancelAgentAssignmentCommand, bool>
{
    private readonly ConcurrentDictionary<DelegationId, CancellationTokenSource> _active = new();
    private readonly IDelegationCheckpointStore _checkpoints;
    private readonly IDomainEventStream _events;
    private readonly IAgentRunScheduler _scheduler;

    /// <summary>Initializes a new instance of the <see cref="DelegationCoordinator"/> class.</summary>
    public DelegationCoordinator(
        IAgentRunScheduler scheduler,
        IDelegationCheckpointStore checkpoints,
        IDomainEventStream events)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(events);
        _scheduler = scheduler;
        _checkpoints = checkpoints;
        _events = events;
    }

    /// <inheritdoc />
    public async Task<DelegationCheckpoint> StartAsync(
        DelegationPlan plan,
        IAgentAssignmentRunner runner,
        CancellationToken cancellationToken = default)
    {
        DelegationPlanValidator.Validate(plan);
        ArgumentNullException.ThrowIfNull(runner);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_active.TryAdd(plan.DelegationId, source))
        {
            throw new InvalidOperationException("The delegation is already active.");
        }

        var accepted = CreateCheckpoint(
            plan,
            DelegationCheckpointPhase.Accepted,
            [],
            "queue validated child assignments");
        await SaveAsync(accepted, cancellationToken);
        try
        {
            await SaveAsync(
                accepted with
                {
                    Phase = DelegationCheckpointPhase.ChildrenQueued,
                    NextAction = "run bounded in-process child assignments",
                    RecordedAt = DateTimeOffset.UtcNow,
                },
                cancellationToken);
            var outcomes = await _scheduler.RunAsync(plan, runner, source.Token);
            var phase = outcomes.All(item => item.Status == AgentRunStatus.Completed)
                ? ResolveJoinPhase(plan)
                : outcomes.Any(item => item.Status == AgentRunStatus.Failed)
                    ? DelegationCheckpointPhase.Failed
                    : DelegationCheckpointPhase.Cancelled;
            var terminal = CreateCheckpoint(
                plan,
                phase,
                outcomes,
                ResolveNextAction(phase));
            await SaveAsync(terminal, CancellationToken.None);
            return terminal;
        }
        catch (OperationCanceledException)
        {
            var cancelled = CreateCheckpoint(
                plan,
                DelegationCheckpointPhase.Cancelled,
                [],
                "resume from the last durable boundary after revalidation");
            await SaveAsync(cancelled, CancellationToken.None);
            throw;
        }
        catch
        {
            var failed = CreateCheckpoint(
                plan,
                DelegationCheckpointPhase.Failed,
                [],
                "inspect child failures and revise or serialize the delegation");
            await SaveAsync(failed, CancellationToken.None);
            throw;
        }
        finally
        {
            _active.TryRemove(plan.DelegationId, out _);
        }
    }

    /// <inheritdoc />
    public Task<DelegationCheckpoint?> GetAsync(
        DelegationId delegationId,
        CancellationToken cancellationToken = default)
    {
        return _checkpoints.GetAsync(delegationId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> CancelAsync(
        DelegationId delegationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_active.TryGetValue(delegationId, out var source))
        {
            return Task.FromResult(false);
        }

        source.Cancel();
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public Task<DelegationCheckpoint> HandleAsync(
        StartDelegationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return StartAsync(command.Plan, command.Runner, cancellationToken);
    }

    /// <inheritdoc />
    public Task<DelegationCheckpoint?> HandleAsync(
        GetDelegationCommand command,
        CancellationToken cancellationToken = default)
    {
        return GetAsync(command.DelegationId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(
        CancelDelegationCommand command,
        CancellationToken cancellationToken = default)
    {
        return CancelAsync(command.DelegationId, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> HandleAsync(
        CancelAgentAssignmentCommand command,
        CancellationToken cancellationToken = default)
    {
        return _scheduler.CancelAssignmentAsync(
            command.DelegationId,
            command.AssignmentId,
            cancellationToken);
    }

    private async Task SaveAsync(
        DelegationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        await _checkpoints.SaveAsync(checkpoint, cancellationToken);
        await _events.PublishAsync(
            new DelegationCheckpointWritten(
                checkpoint.Provenance.SessionId,
                DateTimeOffset.UtcNow,
                checkpoint.DelegationId,
                checkpoint.Provenance.ParentRunId,
                checkpoint.Phase,
                checkpoint.Provenance.Generation,
                checkpoint.NextAction),
            cancellationToken);
        foreach (var outcome in checkpoint.ChildOutcomes)
        {
            await _events.PublishAsync(
                new AgentRunLifecycleObserved(
                    checkpoint.Provenance.SessionId,
                    DateTimeOffset.UtcNow,
                    checkpoint.DelegationId,
                    outcome.AssignmentId,
                    outcome.ChildRunId,
                    outcome.Role,
                    outcome.Status,
                    outcome.Generation,
                    outcome.Reason),
                cancellationToken);
        }
    }

    private static DelegationCheckpoint CreateCheckpoint(
        DelegationPlan plan,
        DelegationCheckpointPhase phase,
        IReadOnlyList<AgentRunOutcome> outcomes,
        string nextAction)
    {
        return new DelegationCheckpoint
        {
            DelegationId = plan.DelegationId,
            Provenance = plan.Provenance,
            Phase = phase,
            ChildOutcomes = outcomes,
            NextAction = nextAction,
            RecordedAt = DateTimeOffset.UtcNow,
        };
    }

    private static DelegationCheckpointPhase ResolveJoinPhase(DelegationPlan plan)
    {
        if (plan.Assignments.Any(item => item.Role == AgentRole.Implementer))
        {
            return DelegationCheckpointPhase.WorkersFrozen;
        }

        return plan.Assignments.Any(item => item.Mode == AgentRunMode.ReadOnlyReview)
            ? DelegationCheckpointPhase.ReviewsJoined
            : DelegationCheckpointPhase.ResearchJoined;
    }

    private static string ResolveNextAction(DelegationCheckpointPhase phase)
    {
        return phase switch
        {
            DelegationCheckpointPhase.ResearchJoined => "synthesize validated findings at the parent boundary",
            DelegationCheckpointPhase.WorkersFrozen => "run independent reviews and select worker change sets",
            DelegationCheckpointPhase.ReviewsJoined => "resolve required findings before integration",
            DelegationCheckpointPhase.Failed => "inspect failures and revise or serialize the delegation",
            DelegationCheckpointPhase.Cancelled => "resume from the last durable boundary after revalidation",
            _ => "inspect delegation state",
        };
    }
}
