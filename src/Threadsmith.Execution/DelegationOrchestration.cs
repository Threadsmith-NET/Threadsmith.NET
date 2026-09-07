namespace Threadsmith.Execution;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Threadsmith.Core;

/// <summary>Conservative configured limits for in-process child scheduling.</summary>
public sealed record AgentSchedulerOptions
{
    /// <summary>Maximum admitted and queued assignments; zero disables this admission backpressure.</summary>
    public int QueueCapacity { get; init; } = 32;

    /// <summary>Maximum active children across all parents; zero disables this concurrency limit.</summary>
    public int MaximumActiveChildren { get; init; } = 4;

    /// <summary>Maximum active children within one parent; zero disables this concurrency limit.</summary>
    public int MaximumActiveChildrenPerParent { get; init; } = 3;

    /// <summary>Maximum active implementation children; zero disables this concurrency limit.</summary>
    public int MaximumActiveImplementers { get; init; } = 2;

    /// <summary>Maximum shutdown join duration; zero waits until children finish or caller cancellation is observed.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Rejects negative scheduler settings without imposing a compiled operational ceiling.</summary>
    public void Validate()
    {
        if (QueueCapacity < 0 || MaximumActiveChildren < 0 || MaximumActiveChildrenPerParent < 0
            || MaximumActiveImplementers < 0 || ShutdownTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AgentSchedulerOptions), "Agent scheduler limits must be non-negative.");
        }
    }
}

/// <summary>Validates and freezes one-level delegation contracts.</summary>
public static class DelegationPlanValidator
{
    /// <summary>Validates identities, graph shape, modes, authority, budgets, and bounds.</summary>
    public static void Validate(DelegationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.AssignmentLimits);
        plan.AssignmentLimits.Validate();
        var maximumText = plan.AssignmentLimits.EffectiveLimit(plan.AssignmentLimits.MaximumTextCharacters);
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

        ValidateText(plan.Provenance.RepositoryIdentity, nameof(plan.Provenance.RepositoryIdentity), maximumText);
        ValidateText(plan.Provenance.BaselineIdentity, nameof(plan.Provenance.BaselineIdentity), maximumText);
        var approvedPlanIdentity = plan.Provenance.ApprovedPlanIdentity;
        var hasApprovedPlanIdentity = approvedPlanIdentity is not null;
        var hasApprovedPlanRevision = plan.Provenance.ApprovedPlanRevision.HasValue;
        if (hasApprovedPlanIdentity != hasApprovedPlanRevision
            || plan.Provenance.ApprovedPlanRevision is <= 0)
        {
            throw new InvalidDataException(
                "Approved-plan delegation provenance requires a paired positive identity and revision.");
        }

        if (approvedPlanIdentity is not null)
        {
            ValidateText(
                approvedPlanIdentity,
                nameof(plan.Provenance.ApprovedPlanIdentity),
                maximumText);
        }

        ValidateBudget(plan.ParentBudget);
        if (plan.Assignments.Count < 1
            || Exceeds(plan.Assignments.Count, plan.AssignmentLimits.EffectiveLimit(plan.AssignmentLimits.MaximumAssignments)))
        {
            throw new InvalidDataException("A delegation must contain assignments within its configured count limit.");
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

        if (!Enum.IsDefined(assignment.Role)
            || !Enum.IsDefined(assignment.Mode)
            || !Enum.IsDefined(assignment.FailurePolicy)
            || assignment.RoleRunnerVersion != 1)
        {
            throw new InvalidDataException("The assignment role, mode, failure policy, or runner version is unsupported.");
        }

        var limits = plan.AssignmentLimits;
        var maximumText = limits.EffectiveLimit(limits.MaximumTextCharacters);
        ValidateText(assignment.Objective, nameof(assignment.Objective), maximumText);
        ValidateText(assignment.OutputSchema, nameof(assignment.OutputSchema), maximumText);
        ValidateText(assignment.StoppingCondition, nameof(assignment.StoppingCondition), maximumText);
        ValidateText(assignment.Policy.ModelSelectionRationale, nameof(assignment.Policy.ModelSelectionRationale), maximumText);
        ValidateText(assignment.Policy.ContextPolicyVersion, nameof(assignment.Policy.ContextPolicyVersion), maximumText);
        ValidateText(assignment.Policy.ToolPolicyVersion, nameof(assignment.Policy.ToolPolicyVersion), maximumText);
        assignment.Policy.ResultLimits.Validate();
        if (assignment.Deadline <= plan.AcceptedAt || assignment.Tasks.Count < 1
            || Exceeds(assignment.Tasks.Count, limits.EffectiveLimit(limits.MaximumTasksPerAssignment)))
        {
            throw new InvalidDataException("Every assignment needs tasks and a deadline after plan acceptance.");
        }

        if (assignment.Tasks.Any(item => string.IsNullOrWhiteSpace(item) || Exceeds(item.Length, maximumText)))
        {
            throw new InvalidDataException("Assignment tasks must be bounded non-empty text.");
        }

        if (Exceeds(assignment.InitialContext.Length, limits.EffectiveLimit(limits.MaximumContextCharacters)))
        {
            throw new InvalidDataException("Assignment context exceeds the bounded text limit.");
        }

        var implementer = assignment.Role == AgentRole.Implementer;
        var mutationWorker = assignment.Mode == AgentRunMode.IsolatedWorktreeMutation;
        if (mutationWorker && (!implementer || !plan.ImplementationAuthorized || assignment.PlanStepIds.Count == 0))
        {
            throw new UnauthorizedAccessException(
                "Only explicitly authorized implementers may use isolated-worktree mutation mode.");
        }

        var reviewer = assignment.Role is AgentRole.SecurityReviewer
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

        if (!mutationWorker && assignment.Policy.TrustCeiling > RepositoryTrustLevel.TrustedBuild)
        {
            throw new UnauthorizedAccessException("Read-only children cannot receive mutation trust.");
        }

        if (!mutationWorker && assignment.Policy.AllowedToolIds.Any(IsMutationTool))
        {
            throw new UnauthorizedAccessException("Read-only children cannot receive mutation tools.");
        }

        ValidateBudget(assignment.Budget);
        ValidateScope(assignment.Scope, limits.EffectiveLimit(limits.MaximumScopeCharacters));
    }

    private static void ValidateScope(AgentAssignmentScope scope, int maximumCharacters)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var values = scope.Files
            .Concat(scope.Directories)
            .Concat(scope.Projects)
            .Concat(scope.Symbols)
            .Concat(scope.SharedSurfaces);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || Exceeds(value.Length, maximumCharacters) || Path.IsPathRooted(value))
            {
                throw new InvalidDataException("Assignment ownership must be bounded and repository-relative.");
            }

            var normalized = value.Replace('\\', '/');
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
        var remaining = assignments.ToDictionary(item => item.Key, item => item.Value.Dependencies.Count);
        var dependents = assignments.Keys.ToDictionary(id => id, _ => new List<AgentAssignmentId>());
        foreach (var assignment in assignments.Values)
        {
            foreach (var dependency in assignment.Dependencies)
            {
                dependents[dependency].Add(assignment.AssignmentId);
            }
        }

        var pending = new Queue<AgentAssignmentId>(remaining.Where(item => item.Value == 0).Select(item => item.Key));
        var visited = 0;
        while (pending.TryDequeue(out var id))
        {
            visited++;
            foreach (var dependent in dependents[id])
            {
                if (--remaining[dependent] == 0)
                {
                    pending.Enqueue(dependent);
                }
            }
        }

        if (visited != assignments.Count)
        {
            throw new InvalidDataException("Delegation dependency graph contains a cycle.");
        }
    }

    private static void ValidateAggregateBudget(DelegationPlan plan)
    {
        var total = AgentResourceBudget.Aggregate(
            plan.Assignments.Select(item => item.Budget).ToArray());
        var hasUnboundedChildUsage = !total.EnforceLimits;
        if ((plan.ParentBudget.EnforceLimits
                && (hasUnboundedChildUsage
                    || total.ModelTokens > plan.ParentBudget.ModelTokens
                    || total.ToolCalls > plan.ParentBudget.ToolCalls
                    || total.EvidenceItems > plan.ParentBudget.EvidenceItems
                    || total.Files > plan.ParentBudget.Files
                    || total.Bytes > plan.ParentBudget.Bytes
                    || total.Mutations > plan.ParentBudget.Mutations
                    || total.Processes > plan.ParentBudget.Processes
                    || total.Builds > plan.ParentBudget.Builds
                    || total.Tests > plan.ParentBudget.Tests
                    || total.Corrections > plan.ParentBudget.Corrections))
            || (plan.ParentBudget.WallTime > TimeSpan.Zero
                && (total.WallTime == TimeSpan.Zero || total.WallTime > plan.ParentBudget.WallTime)))
        {
            throw new InvalidDataException("Child budget reservations exceed the dominating parent budget.");
        }
    }

    private static void ValidateBudget(AgentResourceBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.ModelTokens < 0
            || budget.ToolCalls < 0
            || budget.EvidenceItems < 0
            || budget.Files < 0 || budget.Bytes < 0 || budget.Mutations < 0
            || budget.Processes < 0 || budget.Builds < 0 || budget.Tests < 0
            || budget.Corrections < 0 || budget.WallTime < TimeSpan.Zero
            || (!budget.EnforceLimits
                && (budget.ModelTokens != 0
                    || budget.ToolCalls != 0
                    || budget.EvidenceItems != 0
                    || budget.Files != 0
                    || budget.Bytes != 0
                    || budget.Mutations != 0
                    || budget.Processes != 0
                    || budget.Builds != 0
                    || budget.Tests != 0
                    || budget.Corrections != 0)))
        {
            throw new InvalidDataException("Agent resource budgets must be non-negative and telemetry-only quotas must be zero.");
        }
    }

    private static void ValidateText(string value, string name, int maximumCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (Exceeds(value.Length, maximumCharacters))
        {
            throw new InvalidDataException($"{name} exceeds the bounded text limit.");
        }
    }

    private static bool Exceeds(int value, int maximum)
    {
        return maximum > 0 && value > maximum;
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
        for (var leftIndex = 0; leftIndex < workers.Length; leftIndex++)
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

            for (var rightIndex = leftIndex + 1; rightIndex < workers.Length; rightIndex++)
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
        foreach (var file in leftFiles.Intersect(rightFiles, StringComparer.OrdinalIgnoreCase))
        {
            overlaps.Add(file);
        }

        foreach (var file in leftFiles)
        {
            foreach (var directory in right.Directories.Select(Normalize))
            {
                if (IsUnder(file, directory))
                {
                    overlaps.Add(file);
                }
            }
        }

        foreach (var file in rightFiles)
        {
            foreach (var directory in left.Directories.Select(Normalize))
            {
                if (IsUnder(file, directory))
                {
                    overlaps.Add(file);
                }
            }
        }

        foreach (var directory in left.Directories.Select(Normalize))
        {
            foreach (var other in right.Directories.Select(Normalize))
            {
                if (IsUnder(directory, other) || IsUnder(other, directory))
                {
                    overlaps.Add(directory.Length <= other.Length ? directory : other);
                }
            }
        }

        foreach (var symbol in left.Symbols.Intersect(right.Symbols, StringComparer.Ordinal))
        {
            overlaps.Add($"symbol:{symbol}");
        }

        foreach (var project in left.Projects.Intersect(right.Projects, StringComparer.OrdinalIgnoreCase))
        {
            overlaps.Add($"project:{Normalize(project)}");
        }

        return [.. overlaps.OrderBy(item => item, StringComparer.Ordinal)];
    }

    private static bool IsUnder(string path, string directory)
    {
        var prefix = directory.EndsWith("/", StringComparison.Ordinal) ? directory : directory + "/";
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
    private readonly SemaphoreSlim? _global;
    private readonly SemaphoreSlim? _implementers;
    private readonly ConcurrentDictionary<RunId, ParentLimiter> _parents = new();
    private readonly AgentSchedulerOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private long _admittedOrQueued;
    private int _stopped;

    /// <summary>Initializes a new instance of the <see cref="AgentRunScheduler"/> class.</summary>
    public AgentRunScheduler(AgentSchedulerOptions? options = null)
    {
        _options = options ?? new AgentSchedulerOptions();
        _options.Validate();
        _global = CreateLimiter(_options.MaximumActiveChildren);
        _implementers = CreateLimiter(_options.MaximumActiveImplementers);
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
        var admitted = Interlocked.Add(ref _admittedOrQueued, plan.Assignments.Count);
        if (_options.QueueCapacity > 0 && admitted > _options.QueueCapacity)
        {
            Interlocked.Add(ref _admittedOrQueued, -plan.Assignments.Count);
            throw new AgentQueueCapacityException();
        }

        using var parentCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        var completions = plan.Assignments.ToDictionary(
            item => item.AssignmentId,
            static _ => new TaskCompletionSource<AgentRunOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously));
        var terminalResults = Channel.CreateBounded<AgentRunOutcome>(
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

        var started = Stopwatch.GetTimestamp();
        while (!_children.IsEmpty
            && (_options.ShutdownTimeout == TimeSpan.Zero || Stopwatch.GetElapsedTime(started) < _options.ShutdownTimeout))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _shutdown.Dispose();
        _global?.Dispose();
        _implementers?.Dispose();
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
        SemaphoreSlim? parentLimiter,
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
                    outcome = CreateTerminal(
                        plan,
                        assignment,
                        AgentRunStatus.Cancelled,
                        "dependency did not complete");
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
                    Response = null,
                    Findings = null,
                    ChangeSet = null,
                    Review = null,
                    Implementation = null,
                };
            }

            ValidateOutcome(assignment, outcome);
            if (outcome.Status == AgentRunStatus.Failed)
            {
                await ApplyFailurePolicyAsync(plan, assignment, parentCancellation);
            }
        }
        catch (OperationCanceledException exception)
        {
            outcome = CreateTerminal(
                plan,
                assignment,
                AgentRunStatus.Cancelled,
                "child cancellation observed");
            if (ChildAgentFailureDetails.TryGet(exception, out var failure))
            {
                outcome = outcome with
                {
                    Usage = failure.Usage,
                    ModelProfileId = failure.ModelProfileId,
                    ModelSelection = failure.ModelSelection,
                };
            }
        }
        catch (Exception exception)
        {
            outcome = ChildAgentFailureDetails.TryGet(exception, out var failure)
                ? CreateTerminal(
                    plan,
                    assignment,
                    AgentRunStatus.Failed,
                    failure.SafeReason) with
                {
                    Usage = failure.Usage,
                    ModelProfileId = failure.ModelProfileId,
                    ModelSelection = failure.ModelSelection,
                }
                : CreateTerminal(
                    plan,
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
        SemaphoreSlim? parent,
        CancellationToken cancellationToken)
    {
        var remaining = assignment.Deadline - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return CreateTerminal(
                plan,
                assignment,
                AgentRunStatus.Cancelled,
                "assignment deadline elapsed");
        }

        var timeout = assignment.Deadline == DateTimeOffset.MaxValue ? TimeSpan.Zero : remaining;
        if (assignment.Budget.WallTime > TimeSpan.Zero
            && (timeout == TimeSpan.Zero || assignment.Budget.WallTime < timeout))
        {
            timeout = assignment.Budget.WallTime;
        }

        await using var deadline = new AgentTimeoutScope(timeout, cancellationToken);
        _children[(plan.DelegationId, assignment.AssignmentId)] = deadline.Source;
        var globalHeld = false;
        var parentHeld = false;
        var implementerHeld = false;
        try
        {
            if (_global is not null)
            {
                await _global.WaitAsync(deadline.Token);
                globalHeld = true;
            }

            if (parent is not null)
            {
                await parent.WaitAsync(deadline.Token);
                parentHeld = true;
            }

            if (assignment.Role == AgentRole.Implementer && _implementers is not null)
            {
                await _implementers.WaitAsync(deadline.Token);
                implementerHeld = true;
            }

            var outcome = await runner.RunAsync(plan, assignment, deadline.Token);
            return deadline.Token.IsCancellationRequested
                ? outcome with
                {
                    Status = AgentRunStatus.Cancelled,
                    Reason = "child cancellation observed",
                    Response = null,
                    Findings = null,
                    ChangeSet = null,
                    Review = null,
                    Implementation = null,
                }
                : outcome;
        }
        finally
        {
            if (implementerHeld)
            {
                _implementers?.Release();
            }

            if (parentHeld)
            {
                parent?.Release();
            }

            if (globalHeld)
            {
                _global?.Release();
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
            if (DelegationOutcomeClassifier.HasNaturalResponse(assignment, outcome))
            {
                return;
            }

            var validResult = assignment.Role switch
            {
                AgentRole.Explorer => outcome.Findings is not null
                    && outcome.Review is null && outcome.ChangeSet is null && outcome.Implementation is null,
                AgentRole.Implementer when assignment.Mode == AgentRunMode.IsolatedWorktreeMutation =>
                    outcome.ChangeSet is not null && outcome.Findings is null
                    && outcome.Review is null && outcome.Implementation is null,
                AgentRole.Implementer => outcome.Implementation is not null && outcome.Findings is not null
                    && outcome.ChangeSet is null && outcome.Review is null,
                AgentRole.SecurityReviewer or AgentRole.TestReviewer
                    or AgentRole.PerformanceReviewer or AgentRole.ArchitectureReviewer =>
                    outcome.Review is not null && outcome.ChangeSet is null && outcome.Implementation is null,
                _ => false,
            };
            if (!validResult)
            {
                throw new InvalidDataException("Child result type does not match the assigned role.");
            }
        }
    }

    private static AgentRunOutcome CreateTerminal(
        DelegationPlan plan,
        AgentAssignment assignment,
        AgentRunStatus status,
        string reason)
    {
        return new AgentRunOutcome
        {
            AssignmentId = assignment.AssignmentId,
            ChildRunId = assignment.ChildRunId,
            Role = assignment.Role,
            Generation = plan.Provenance.Generation,
            Status = status,
            Usage = new AgentResourceUsage(),
            Reason = reason,
            ModelSelection = assignment.Policy.ModelSelection,
            ModelProfileId = assignment.Policy.ModelSelection?.EffectiveProfileId,
        };
    }

    private static SemaphoreSlim? CreateLimiter(int limit)
    {
        return limit == 0 ? null : new SemaphoreSlim(limit, limit);
    }

    private sealed class ParentLimiter : IDisposable
    {
        private readonly Lock _gate = new();
        private int _references;
        private bool _retired;

        internal ParentLimiter(int maximumConcurrency)
        {
            Semaphore = CreateLimiter(maximumConcurrency);
        }

        internal SemaphoreSlim? Semaphore { get; }

        public void Dispose()
        {
            Semaphore?.Dispose();
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
    private const string CancellationReason = "parent cancellation observed before child completion";
    private const string FailurePolicyReason = "delegation failure policy prevented parent result join";
    private const string InfrastructureFailureReason =
        "delegation infrastructure failed before child results became authoritative";

    private const string QueueCapacityFailureReason =
        "bounded agent queue rejected the delegation before child execution";

    private static readonly TimeSpan DefaultProgressCheckpointTimeout = TimeSpan.FromSeconds(2);
    private readonly ConcurrentDictionary<DelegationId, ActiveDelegation> _active = new();
    private readonly IDelegationCheckpointStore _checkpoints;
    private readonly IDomainEventStream _events;
    private readonly TimeSpan _progressCheckpointTimeout;
    private readonly IAgentRunScheduler _scheduler;

    /// <summary>Initializes a new instance of the <see cref="DelegationCoordinator"/> class.</summary>
    public DelegationCoordinator(
        IAgentRunScheduler scheduler,
        IDelegationCheckpointStore checkpoints,
        IDomainEventStream events,
        TimeSpan? progressCheckpointTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(checkpoints);
        ArgumentNullException.ThrowIfNull(events);
        var effectiveProgressTimeout = progressCheckpointTimeout ?? DefaultProgressCheckpointTimeout;
        if (effectiveProgressTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(progressCheckpointTimeout));
        }

        _scheduler = scheduler;
        _checkpoints = checkpoints;
        _events = events;
        _progressCheckpointTimeout = effectiveProgressTimeout;
    }

    /// <inheritdoc />
    public async Task<DelegationCheckpoint> StartAsync(
        DelegationPlan plan,
        IAgentAssignmentRunner runner,
        CancellationToken cancellationToken = default)
    {
        DelegationPlanValidator.Validate(plan);
        ArgumentNullException.ThrowIfNull(runner);
        using var active = new ActiveDelegation();
        if (!_active.TryAdd(plan.DelegationId, active))
        {
            throw new InvalidOperationException("The delegation is already active.");
        }

        using var cancellationRegistration = cancellationToken.Register(active.Cancel);
        var revisions = new CheckpointRevisionSequence();
        AgentRunOutcome[] queuedOutcomes =
        [
            .. plan.Assignments.Select(assignment => CreateProgressOutcome(
                plan,
                assignment,
                AgentRunStatus.Queued,
                "child assignment queued")),
        ];
        var accepted = CreateCheckpoint(
            plan,
            DelegationCheckpointPhase.Accepted,
            [],
            "queue validated child assignments",
            revisions.Next());
        var observedOutcomes = queuedOutcomes;
        try
        {
            await SaveRequiredAsync(accepted, cancellationToken);
            await SaveRequiredAsync(
                accepted with
                {
                    Phase = DelegationCheckpointPhase.ChildrenQueued,
                    ChildOutcomes = queuedOutcomes,
                    NextAction = "run bounded in-process child assignments",
                    RecordedAt = DateTimeOffset.UtcNow,
                    Revision = revisions.Next(),
                },
                cancellationToken);
            var observedRunner = new CheckpointingAssignmentRunner(
                this,
                plan,
                runner,
                queuedOutcomes,
                revisions);
            var scheduledOutcomes = await _scheduler.RunAsync(plan, observedRunner, active.Token);
            AgentRunOutcome[] outcomes =
            [
                .. scheduledOutcomes.Select(outcome => DelegationOutcomeClassifier.Normalize(plan, outcome)),
            ];
            var failurePolicyTriggered = HasFailedDelegationPolicy(plan, outcomes);
            if (failurePolicyTriggered)
            {
                outcomes = CreateUnjoinedFailureOutcomes(outcomes, FailurePolicyReason);
            }

            observedOutcomes = outcomes;
            var phase = ResolveTerminalPhase(
                plan,
                outcomes,
                active.IsCancellationRequested,
                failurePolicyTriggered);
            var terminal = CreateCheckpoint(
                plan,
                phase,
                outcomes,
                ResolveNextAction(phase),
                revisions.Next());
            await SaveRequiredAsync(terminal, CancellationToken.None);
            if (phase == DelegationCheckpointPhase.Cancelled)
            {
                active.MarkCompleted();
                return terminal;
            }

            if (IsJoinedPhase(phase) && runner is IAgentOutcomeJoiner joiner)
            {
                if (active.IsCancellationRequested)
                {
                    return await SaveCancellationAsync(plan, outcomes, revisions, active);
                }

                var joinCommitted = 0;
                bool TryCommitJoin()
                {
                    if (!active.TryComplete())
                    {
                        return false;
                    }

                    Volatile.Write(ref joinCommitted, 1);
                    return true;
                }

                try
                {
                    _ = await joiner.JoinAsync(
                        plan,
                        outcomes,
                        TryCommitJoin,
                        active.Token);
                    if (Volatile.Read(ref joinCommitted) == 1)
                    {
                        return terminal;
                    }

                    if (active.IsCancellationRequested)
                    {
                        return await SaveCancellationAsync(plan, outcomes, revisions, active);
                    }

                    throw new InvalidOperationException(
                        "The parent evidence join completed without committing its disposition.");
                }
                catch (Exception) when (Volatile.Read(ref joinCommitted) == 1)
                {
                    return terminal;
                }
                catch (Exception) when (active.IsCancellationRequested)
                {
                    return await SaveCancellationAsync(plan, outcomes, revisions, active);
                }
                catch
                {
                    var joinFailureOutcomes = CreateJoinFailureOutcomes(outcomes);
                    var failed = CreateCheckpoint(
                        plan,
                        DelegationCheckpointPhase.Failed,
                        joinFailureOutcomes,
                        "inspect the parent evidence join failure before retrying delegation",
                        revisions.Next());
                    await SaveRequiredAsync(failed, CancellationToken.None);
                    if (!active.TryComplete())
                    {
                        return await SaveCancellationAsync(plan, outcomes, revisions, active);
                    }

                    return failed;
                }
            }

            if (!active.TryComplete())
            {
                return await SaveCancellationAsync(plan, outcomes, revisions, active);
            }

            return terminal;
        }
        catch (OperationCanceledException) when (active.IsCancellationRequested)
        {
            return await SaveCancellationAsync(plan, observedOutcomes, revisions, active);
        }
        catch (AgentQueueCapacityException)
        {
            if (active.IsCancellationRequested)
            {
                return await SaveCancellationAsync(plan, observedOutcomes, revisions, active);
            }

            var failedOutcomes = CreateInfrastructureFailureOutcomes(
                observedOutcomes,
                QueueCapacityFailureReason);
            var failed = CreateCheckpoint(
                plan,
                DelegationCheckpointPhase.Failed,
                failedOutcomes,
                "retry after another delegation leaves the bounded agent queue",
                revisions.Next());
            await SaveRequiredAsync(failed, CancellationToken.None);
            if (!active.TryComplete())
            {
                return await SaveCancellationAsync(plan, observedOutcomes, revisions, active);
            }

            return failed;
        }
        catch
        {
            if (active.IsCancellationRequested)
            {
                return await SaveCancellationAsync(plan, observedOutcomes, revisions, active);
            }

            var failedOutcomes = CreateInfrastructureFailureOutcomes(
                observedOutcomes,
                InfrastructureFailureReason);
            var failed = CreateCheckpoint(
                plan,
                DelegationCheckpointPhase.Failed,
                failedOutcomes,
                "inspect child failures and revise or serialize the delegation",
                revisions.Next());
            _ = await SaveAsync(failed, CancellationToken.None);
            if (!active.TryComplete())
            {
                return await SaveCancellationAsync(plan, observedOutcomes, revisions, active);
            }

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
        if (!_active.TryGetValue(delegationId, out var active))
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(active.TryCancel());
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

    private async Task<bool> SaveAsync(
        DelegationCheckpoint checkpoint,
        CancellationToken cancellationToken,
        IReadOnlyList<AgentRunOutcome>? lifecycleOutcomes = null)
    {
        if (!await _checkpoints.SaveAsync(checkpoint, cancellationToken))
        {
            return false;
        }

        await _events.PublishAsync(
            new DelegationCheckpointWritten(
                checkpoint.Provenance.SessionId,
                DateTimeOffset.UtcNow,
                checkpoint.DelegationId,
                checkpoint.Provenance.ParentRunId,
                checkpoint.Phase,
                checkpoint.Provenance.Generation,
                checkpoint.NextAction,
                checkpoint.Revision),
            cancellationToken);
        foreach (var outcome in lifecycleOutcomes ?? checkpoint.ChildOutcomes)
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
                    outcome.Reason,
                    checkpoint.Revision),
                cancellationToken);
        }

        return true;
    }

    private async Task SaveRequiredAsync(
        DelegationCheckpoint checkpoint,
        CancellationToken cancellationToken)
    {
        if (!await SaveAsync(checkpoint, cancellationToken))
        {
            throw new InvalidOperationException(
                "A delegation checkpoint revision was rejected because newer state is already durable.");
        }
    }

    private Task<DelegationCheckpoint> SaveCancellationAsync(
        DelegationPlan plan,
        IReadOnlyList<AgentRunOutcome> outcomes,
        CheckpointRevisionSequence revisions,
        ActiveDelegation active)
    {
        var terminalOutcomes = outcomes
            .Select(outcome => outcome.Status is AgentRunStatus.Queued or AgentRunStatus.Running
                ? outcome with
                {
                    Status = AgentRunStatus.Cancelled,
                    Reason = CancellationReason,
                    Response = null,
                    Findings = null,
                    ChangeSet = null,
                    Review = null,
                    Implementation = null,
                }
                : outcome)
            .ToArray();
        return SaveTerminalAsync(
            CreateCheckpoint(
                plan,
                DelegationCheckpointPhase.Cancelled,
                terminalOutcomes,
                "inspect the observed cancellation outcome before retrying delegation",
                revisions.Next()),
            active);
    }

    private async Task<DelegationCheckpoint> SaveTerminalAsync(
        DelegationCheckpoint checkpoint,
        ActiveDelegation active)
    {
        await SaveRequiredAsync(checkpoint, CancellationToken.None);
        active.MarkCompleted();
        return checkpoint;
    }

    private static AgentRunOutcome[] CreateJoinFailureOutcomes(
        IReadOnlyList<AgentRunOutcome> outcomes)
    {
        return CreateUnjoinedFailureOutcomes(
            outcomes,
            "parent evidence join failed before child results became authoritative");
    }

    private static AgentRunOutcome[] CreateUnjoinedFailureOutcomes(
        IReadOnlyList<AgentRunOutcome> outcomes,
        string reason)
    {
        return
        [
            .. outcomes.Select(outcome => outcome with
            {
                Status = outcome.Status == AgentRunStatus.Completed
                    ? AgentRunStatus.Failed
                    : outcome.Status,
                Reason = outcome.Status == AgentRunStatus.Completed ? reason : outcome.Reason,
                Response = null,
                Findings = null,
                ChangeSet = null,
                Review = null,
                Implementation = null,
            }),
        ];
    }

    private static AgentRunOutcome[] CreateInfrastructureFailureOutcomes(
        IReadOnlyList<AgentRunOutcome> outcomes,
        string reason)
    {
        return
        [
            .. outcomes.Select(outcome => outcome with
            {
                Status = outcome.Status is AgentRunStatus.Queued
                    or AgentRunStatus.Running
                    or AgentRunStatus.Completed
                        ? AgentRunStatus.Failed
                        : outcome.Status,
                Reason = outcome.Status is AgentRunStatus.Queued
                    or AgentRunStatus.Running
                    or AgentRunStatus.Completed
                        ? reason
                        : outcome.Reason,
                Response = null,
                Findings = null,
                ChangeSet = null,
                Review = null,
                Implementation = null,
            }),
        ];
    }

    private static DelegationCheckpoint CreateCheckpoint(
        DelegationPlan plan,
        DelegationCheckpointPhase phase,
        IReadOnlyList<AgentRunOutcome> outcomes,
        string nextAction,
        long revision)
    {
        return new DelegationCheckpoint
        {
            DelegationId = plan.DelegationId,
            Provenance = plan.Provenance,
            Phase = phase,
            ChildOutcomes = outcomes,
            AssignmentLimits = plan.AssignmentLimits,
            Assignments = plan.Assignments.Select(assignment =>
            {
                var selection = outcomes.FirstOrDefault(outcome =>
                    outcome.AssignmentId == assignment.AssignmentId)?.ModelSelection;
                return selection is null ? assignment : assignment with
                {
                    Policy = assignment.Policy with
                    {
                        ModelSelection = selection,
                        ModelProfileId = selection.EffectiveProfileId,
                        ReasoningLevel = selection.EffectiveReasoningLevel,
                    },
                };
            }).ToArray(),
            NextAction = nextAction,
            RecordedAt = DateTimeOffset.UtcNow,
            Revision = revision,
        };
    }

    private static AgentRunOutcome CreateProgressOutcome(
        DelegationPlan plan,
        AgentAssignment assignment,
        AgentRunStatus status,
        string reason)
    {
        return new AgentRunOutcome
        {
            AssignmentId = assignment.AssignmentId,
            ChildRunId = assignment.ChildRunId,
            Role = assignment.Role,
            Generation = plan.Provenance.Generation,
            Status = status,
            Usage = new AgentResourceUsage(),
            Reason = reason,
            ModelSelection = assignment.Policy.ModelSelection,
            ModelProfileId = assignment.Policy.ModelSelection?.EffectiveProfileId,
        };
    }

    private static DelegationCheckpointPhase ResolveJoinPhase(DelegationPlan plan)
    {
        if (plan.Assignments.Any(item => item.Mode == AgentRunMode.IsolatedWorktreeMutation))
        {
            return DelegationCheckpointPhase.WorkersFrozen;
        }

        return plan.Assignments.Any(item => item.Mode == AgentRunMode.ReadOnlyReview)
            ? DelegationCheckpointPhase.ReviewsJoined
            : DelegationCheckpointPhase.ResearchJoined;
    }

    private static DelegationCheckpointPhase ResolveTerminalPhase(
        DelegationPlan plan,
        IReadOnlyList<AgentRunOutcome> outcomes,
        bool cancellationRequested,
        bool failurePolicyTriggered)
    {
        if (cancellationRequested)
        {
            return DelegationCheckpointPhase.Cancelled;
        }

        if (failurePolicyTriggered)
        {
            return DelegationCheckpointPhase.Failed;
        }

        if (outcomes.Any(outcome => DelegationOutcomeClassifier.HasUsableResult(plan, outcome)))
        {
            return ResolveJoinPhase(plan);
        }

        return outcomes.All(outcome => outcome.Status == AgentRunStatus.Cancelled)
            ? DelegationCheckpointPhase.Cancelled
            : DelegationCheckpointPhase.Failed;
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

    private static bool IsJoinedPhase(DelegationCheckpointPhase phase)
    {
        return phase is DelegationCheckpointPhase.ResearchJoined
            or DelegationCheckpointPhase.WorkersFrozen
            or DelegationCheckpointPhase.ReviewsJoined;
    }

    private static bool HasFailedDelegationPolicy(
        DelegationPlan plan,
        IReadOnlyList<AgentRunOutcome> outcomes)
    {
        return outcomes.Any(outcome => outcome.Status == AgentRunStatus.Failed
            && plan.Assignments.Any(assignment =>
                assignment.AssignmentId == outcome.AssignmentId
                && assignment.ChildRunId == outcome.ChildRunId
                && assignment.FailurePolicy == AgentFailurePolicy.FailDelegation));
    }

    private sealed class CheckpointingAssignmentRunner : IAgentAssignmentRunner
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly IAgentAssignmentRunner _inner;
        private readonly DelegationCoordinator _owner;
        private readonly DelegationPlan _plan;
        private readonly Dictionary<AgentAssignmentId, AgentRunOutcome> _progress;
        private readonly CheckpointRevisionSequence _revisions;

        public CheckpointingAssignmentRunner(
            DelegationCoordinator owner,
            DelegationPlan plan,
            IAgentAssignmentRunner inner,
            IReadOnlyList<AgentRunOutcome> queuedOutcomes,
            CheckpointRevisionSequence revisions)
        {
            _owner = owner;
            _plan = plan;
            _inner = inner;
            _progress = queuedOutcomes.ToDictionary(outcome => outcome.AssignmentId);
            _revisions = revisions;
        }

        public async Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            var running = CreateProgressOutcome(
                plan,
                assignment,
                AgentRunStatus.Running,
                "child assignment running");
            await RecordAsync(running, cancellationToken);
            try
            {
                var outcome = await _inner.RunAsync(plan, assignment, cancellationToken);
                await RecordAsync(outcome, cancellationToken);
                return outcome;
            }
            catch (OperationCanceledException)
            {
                var cancelled = CreateProgressOutcome(
                    plan,
                    assignment,
                    AgentRunStatus.Cancelled,
                    "child cancellation observed");
                await RecordAsync(cancelled, cancellationToken);
                throw;
            }
            catch (Exception exception)
            {
                var outcome = ChildAgentFailureDetails.TryGet(exception, out var failure)
                    ? CreateProgressOutcome(
                        plan,
                        assignment,
                        AgentRunStatus.Failed,
                        failure.SafeReason) with
                    {
                        Usage = failure.Usage,
                        ModelProfileId = failure.ModelProfileId,
                        ModelSelection = failure.ModelSelection,
                    }
                    : CreateProgressOutcome(
                        plan,
                        assignment,
                        AgentRunStatus.Failed,
                        $"{exception.GetType().Name}: child execution failed");
                await RecordAsync(outcome, cancellationToken);
                throw;
            }
        }

        private async Task RecordAsync(AgentRunOutcome outcome, CancellationToken cancellationToken)
        {
            await using var gateTimeout = new AgentTimeoutScope(_owner._progressCheckpointTimeout, cancellationToken);
            try
            {
                await _gate.WaitAsync(gateTimeout.Token);
            }
            catch (OperationCanceledException) when (gateTimeout.Token.IsCancellationRequested)
            {
                return;
            }

            try
            {
                outcome = DelegationOutcomeClassifier.Normalize(_plan, outcome);
                _progress[outcome.AssignmentId] = outcome;
                await using var timeout = new AgentTimeoutScope(_owner._progressCheckpointTimeout, cancellationToken);
                var saveTask = _owner.SaveAsync(
                    CreateCheckpoint(
                        _plan,
                        DelegationCheckpointPhase.ChildrenRunning,
                        [.. _progress.Values.OrderBy(item => item.AssignmentId.Value)],
                        "observe active children or cancel the delegation",
                        _revisions.Next()),
                    timeout.Token,
                    [outcome]);
                try
                {
                    await saveTask.WaitAsync(timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.Token.IsCancellationRequested)
                {
                    _ = ObserveLateAsync(saveTask);
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        private static async Task ObserveLateAsync(Task task)
        {
            try
            {
#pragma warning disable VSTHRD003 // This helper intentionally observes a task started by RecordAsync.
                await task;
#pragma warning restore VSTHRD003
            }
            catch
            {
                // Best-effort progress persistence must not outlive child cancellation.
            }
        }
    }

    private sealed class CheckpointRevisionSequence
    {
        private long _revision;

        public long Next()
        {
            return Interlocked.Increment(ref _revision);
        }
    }

    private sealed class ActiveDelegation : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly CancellationTokenSource _source = new();
        private int _activeCancellations;
        private bool _cancellationRequested;
        private bool _completed;
        private bool _disposeRequested;
        private bool _sourceDisposed;

        public bool IsCancellationRequested
        {
            get
            {
                lock (_gate)
                {
                    return _cancellationRequested;
                }
            }
        }

        public CancellationToken Token => _source.Token;

        public void Cancel()
        {
            _ = TryCancel();
        }

        public void Dispose()
        {
            var disposeSource = false;
            lock (_gate)
            {
                _disposeRequested = true;
                if (_activeCancellations == 0 && !_sourceDisposed)
                {
                    _sourceDisposed = true;
                    disposeSource = true;
                }
            }

            if (disposeSource)
            {
                _source.Dispose();
            }
        }

        public void MarkCompleted()
        {
            lock (_gate)
            {
                _completed = true;
            }
        }

        public bool TryCancel()
        {
            var cancelSource = false;
            lock (_gate)
            {
                if (_completed || _disposeRequested)
                {
                    return false;
                }

                if (!_cancellationRequested)
                {
                    _cancellationRequested = true;
                    _activeCancellations++;
                    cancelSource = true;
                }
            }

            if (cancelSource)
            {
                try
                {
                    _source.Cancel();
                }
                finally
                {
                    CompleteCancellation();
                }
            }

            return true;
        }

        public bool TryComplete()
        {
            lock (_gate)
            {
                if (_completed || _cancellationRequested)
                {
                    return false;
                }

                _completed = true;
                return true;
            }
        }

        private void CompleteCancellation()
        {
            var disposeSource = false;
            lock (_gate)
            {
                _activeCancellations--;
                if (_disposeRequested && _activeCancellations == 0 && !_sourceDisposed)
                {
                    _sourceDisposed = true;
                    disposeSource = true;
                }
            }

            if (disposeSource)
            {
                _source.Dispose();
            }
        }
    }
}
