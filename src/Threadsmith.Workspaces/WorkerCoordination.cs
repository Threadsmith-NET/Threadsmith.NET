namespace Threadsmith.Workspaces;

using System.Collections.Concurrent;
using Threadsmith.Core;

/// <summary>Owns confined managed worktree leases for isolated implementation children.</summary>
public sealed class WorkerWorktreeCoordinator : IWorkerWorktreeCoordinator
{
    private readonly ConcurrentDictionary<(DelegationId, AgentAssignmentId), OwnedLease> _leases = new();
    private readonly GitWorktreeManager _worktrees;

    /// <summary>Initializes a new instance of the <see cref="WorkerWorktreeCoordinator"/> class.</summary>
    public WorkerWorktreeCoordinator(GitWorktreeManager worktrees)
    {
        ArgumentNullException.ThrowIfNull(worktrees);
        _worktrees = worktrees;
    }

    /// <inheritdoc />
    public async Task<WorkerWorktreeLease> CreateAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        string repositoryPath,
        string revision,
        CancellationToken cancellationToken = default)
    {
        var frozenAssignment = DelegationPlanValidatorProxy.ValidateWorker(plan, assignment);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        var key = (plan.DelegationId, frozenAssignment.AssignmentId);
        if (_leases.ContainsKey(key))
        {
            throw new InvalidOperationException("A worktree lease already exists for this assignment.");
        }

        var isolation = await _worktrees.CreateAsync(
            repositoryPath,
            revision: revision,
            cancellationToken: cancellationToken);
        var managedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Threadsmith", "worktrees"));
        var isolatedRoot = Path.GetFullPath(isolation.RepositoryPath);
        if (!IsUnder(isolatedRoot, managedRoot) || IsReparsePoint(isolatedRoot))
        {
            await _worktrees.RemoveAsync(repositoryPath, isolation, CancellationToken.None);
            throw new UnauthorizedAccessException("Worker worktree is outside the managed root or uses a reparse point.");
        }

        var lease = new WorkerWorktreeLease
        {
            DelegationId = plan.DelegationId,
            AssignmentId = frozenAssignment.AssignmentId,
            ChildRunId = frozenAssignment.ChildRunId,
            RepositoryPath = isolatedRoot,
            Revision = revision,
            BaselineIdentity = plan.Provenance.BaselineIdentity,
        };
        if (!_leases.TryAdd(key, new OwnedLease(Path.GetFullPath(repositoryPath), isolation, lease)))
        {
            await _worktrees.RemoveAsync(repositoryPath, isolation, CancellationToken.None);
            throw new InvalidOperationException("A concurrent worktree lease already exists.");
        }

        return lease;
    }

    /// <inheritdoc />
    public Task<WorkerWorktreeLease> FreezeAsync(
        WorkerWorktreeLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        cancellationToken.ThrowIfCancellationRequested();
        var key = (lease.DelegationId, lease.AssignmentId);
        if (!_leases.TryGetValue(key, out var owned)
            || owned.Lease.ChildRunId != lease.ChildRunId
            || !Directory.Exists(owned.Isolation.RepositoryPath))
        {
            throw new InvalidOperationException("The worker worktree lease is not active or no longer exists.");
        }

        var frozen = owned.Lease with { IsFrozen = true };
        _leases[key] = owned with { Lease = frozen };
        return Task.FromResult(frozen);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(
        WorkerWorktreeLease lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var key = (lease.DelegationId, lease.AssignmentId);
        if (!_leases.TryGetValue(key, out var owned)
            || owned.Lease.ChildRunId != lease.ChildRunId)
        {
            throw new UnauthorizedAccessException("The worktree is not owned by this coordinator.");
        }

        await _worktrees.RemoveAsync(owned.ParentRepository, owned.Isolation, cancellationToken);
        _leases.TryRemove(key, out _);
    }

    private static bool IsUnder(string candidate, string root)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, comparison);
    }

    private static bool IsReparsePoint(string path)
    {
        return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    }

    private sealed record OwnedLease(
        string ParentRepository,
        WorkspaceIsolation Isolation,
        WorkerWorktreeLease Lease);
}

/// <summary>Detects scope, package, overlap, and stale-parent conflicts before parent restaging.</summary>
public sealed class WorkerIntegrationCoordinator : IWorkerIntegrationCoordinator
{
    /// <inheritdoc />
    public IReadOnlyList<AgentConflict> DetectConflicts(
        DelegationPlan plan,
        IReadOnlyList<WorkerChangeSet> changeSets,
        string currentParentBaselineIdentity)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(changeSets);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentParentBaselineIdentity);
        var assignments = plan.Assignments.ToDictionary(item => item.AssignmentId);
        var conflicts = new List<AgentConflict>();
        if (!string.Equals(
            plan.Provenance.BaselineIdentity,
            currentParentBaselineIdentity,
            StringComparison.Ordinal))
        {
            conflicts.Add(new AgentConflict(
                "stale-parent-baseline",
                "The primary workspace no longer matches the delegation baseline.",
                changeSets.Select(item => item.AssignmentId).ToArray(),
                []));
        }

        foreach (var changeSet in changeSets)
        {
            if (!assignments.TryGetValue(changeSet.AssignmentId, out var assignment)
                || assignment.Role != AgentRole.Implementer
                || changeSet.ChildRunId != assignment.ChildRunId
                || changeSet.Generation != plan.Provenance.Generation
                || !changeSet.IsComplete)
            {
                conflicts.Add(new AgentConflict(
                    "invalid-worker-package",
                    "A selected worker package is incomplete or has mismatched immutable identity.",
                    [changeSet.AssignmentId],
                    changeSet.TouchedPaths));
                continue;
            }

            var normalizedPaths = NormalizePaths(changeSet.TouchedPaths);
            if (normalizedPaths is null)
            {
                conflicts.Add(new AgentConflict(
                    "invalid-worker-path",
                    "A worker returned a rooted path or a path containing dot segments.",
                    [changeSet.AssignmentId],
                    changeSet.TouchedPaths));
                continue;
            }

            if (!string.Equals(
                changeSet.ParentBaselineIdentity,
                plan.Provenance.BaselineIdentity,
                StringComparison.Ordinal))
            {
                conflicts.Add(new AgentConflict(
                    "worker-baseline-mismatch",
                    "A worker result was produced from a different parent baseline.",
                    [changeSet.AssignmentId],
                    changeSet.TouchedPaths));
            }

            string[] unauthorized =
            [
                .. normalizedPaths
                    .Where(path => !Owns(assignment.Scope, path))
                    .OrderBy(path => path, StringComparer.Ordinal),
            ];
            if (unauthorized.Length > 0)
            {
                conflicts.Add(new AgentConflict(
                    "assignment-scope-exceeded",
                    "A worker touched paths outside its frozen ownership.",
                    [changeSet.AssignmentId],
                    unauthorized));
            }
        }

        for (var left = 0; left < changeSets.Count; left++)
        {
            for (var right = left + 1; right < changeSets.Count; right++)
            {
                var leftPaths = NormalizePaths(changeSets[left].TouchedPaths);
                var rightPaths = NormalizePaths(changeSets[right].TouchedPaths);
                if (leftPaths is null || rightPaths is null)
                {
                    continue;
                }

                string[] overlap =
                [
                    .. leftPaths
                        .Intersect(rightPaths, StringComparer.OrdinalIgnoreCase)
                        .OrderBy(path => path, StringComparer.Ordinal),
                ];
                if (overlap.Length > 0)
                {
                    conflicts.Add(new AgentConflict(
                        "worker-path-conflict",
                        "Selected worker change sets touch the same path.",
                        [changeSets[left].AssignmentId, changeSets[right].AssignmentId],
                        overlap));
                }
            }
        }

        return conflicts;
    }

    private static bool Owns(AgentAssignmentScope scope, string path)
    {
        var normalized = Normalize(path);
        if (scope.Files.Select(Normalize).Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return scope.Directories.Select(Normalize).Any(directory =>
            normalized.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string path)
    {
        return path.Replace('\\', '/').Trim('/');
    }

    private static string[]? NormalizePaths(IReadOnlyList<string> paths)
    {
        var normalized = new string[paths.Count];
        for (var index = 0; index < paths.Count; index++)
        {
            var path = paths[index];
            if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            {
                return null;
            }

            var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            {
                return null;
            }

            normalized[index] = string.Join('/', segments);
        }

        return normalized;
    }
}

/// <summary>Local worker validation that avoids a dependency from Workspaces to Execution.</summary>
internal static class DelegationPlanValidatorProxy
{
    /// <summary>Validates only immutable worker facts needed at the worktree boundary.</summary>
    internal static AgentAssignment ValidateWorker(DelegationPlan plan, AgentAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(assignment);
        var frozenAssignment = plan.Assignments.SingleOrDefault(
            item => item.AssignmentId == assignment.AssignmentId);
        if (plan.DelegationId == default
            || assignment.AssignmentId == default
            || assignment.ChildRunId == default
            || !plan.ImplementationAuthorized
            || frozenAssignment is null
            || frozenAssignment.ChildRunId != assignment.ChildRunId
            || frozenAssignment.Role != assignment.Role
            || frozenAssignment.Mode != assignment.Mode
            || frozenAssignment.Role != AgentRole.Implementer
            || frozenAssignment.Mode != AgentRunMode.IsolatedWorktreeMutation)
        {
            throw new UnauthorizedAccessException("Only an authorized frozen implementer may acquire a worktree.");
        }

        return frozenAssignment;
    }
}
