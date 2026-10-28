namespace Threadsmith.Tools;

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Threadsmith.Core;

/// <summary>Creates assignment-scoped tool contexts that can only narrow parent authority.</summary>
public static class AgentToolPolicy
{
    /// <summary>Applies child tool, trust, network, root, and requester ceilings.</summary>
    /// <remarks>Host-provided request-local tools are admitted independently of the inherited catalog,
    /// but remain subject to explicit denials and the parent's immutable security ceiling.</remarks>
    public static ToolInvocationContext Scope(
        ToolInvocationContext parent,
        DelegationPlan plan,
        AgentAssignment assignment,
        string repositoryPath,
        IReadOnlyList<string>? requestLocalToolIds = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var frozenAssignment = plan.Assignments.SingleOrDefault(
            item => item.AssignmentId == assignment.AssignmentId);
        if (frozenAssignment is null
            || frozenAssignment.ChildRunId != assignment.ChildRunId
            || frozenAssignment.Role != assignment.Role
            || frozenAssignment.Mode != assignment.Mode)
        {
            throw new UnauthorizedAccessException("The assignment does not belong to this delegation.");
        }

        var trust = parent.TrustLevel < frozenAssignment.Policy.TrustCeiling
            ? parent.TrustLevel
            : frozenAssignment.Policy.TrustCeiling;
        string[] parentAllowed = parent.AllowedToolIds.Count == 0
            ? [.. frozenAssignment.Policy.AllowedToolIds]
            : [.. parent.AllowedToolIds.Intersect(
                frozenAssignment.Policy.AllowedToolIds,
                StringComparer.OrdinalIgnoreCase)];
        var denyAllTools = parent.DenyAllTools
            || frozenAssignment.Policy.AllowedToolIds.Count == 0
            || parentAllowed.Length == 0;
        string[] denied =
        [
            .. parent.DeniedToolIds
                .Concat(frozenAssignment.Policy.DeniedToolIds)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
        string[] allowed =
        [
            .. parentAllowed.Concat((requestLocalToolIds ?? []).Where(toolId =>
                !denyAllTools
                && !denied.Contains(toolId, StringComparer.OrdinalIgnoreCase)
                && (parent.SecurityPolicy?.AllowsTool(toolId) ?? true)))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
        string[] allowedNetworkToolIds = frozenAssignment.Policy.AllowNetwork
            ? [.. frozenAssignment.Policy.AllowedNetworkToolIds.Intersect(parentAllowed, StringComparer.OrdinalIgnoreCase)]
            : [];
        if (parent.AllowedNetworkToolIds is { } parentNetworkToolIds)
        {
            allowedNetworkToolIds =
            [
                .. allowedNetworkToolIds.Intersect(parentNetworkToolIds, StringComparer.OrdinalIgnoreCase),
            ];
        }

        var approvedRoots = IntersectApprovedRoots(
            parent.ApprovedRoots,
            frozenAssignment.Scope.Files.Concat(frozenAssignment.Scope.Directories),
            repositoryPath);
        if (approvedRoots.Length == 0)
        {
            throw new UnauthorizedAccessException(
                "The assignment scope does not intersect the parent approved roots.");
        }

        return parent with
        {
            SecurityPolicy = DerivePolicy(),
            IsChildAgent = true,
            AllowDirectWrites = parent.AllowDirectWrites && frozenAssignment.Mode == AgentRunMode.SharedWorkspace,
            RepositoryPath = Path.GetFullPath(repositoryPath),
            TrustLevel = trust,
            ApprovedRoots = approvedRoots,
            AllowedToolIds = allowed,
            DenyAllTools = denyAllTools,
            DeniedToolIds = denied,
            ProhibitedPaths = parent.ProhibitedPaths
                .Concat(frozenAssignment.Policy.ProhibitedPaths)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            AllowedExecutables = parent.AllowedExecutables,
            AllowedNetworkHosts = frozenAssignment.Policy.AllowNetwork ? parent.AllowedNetworkHosts : [],
            AllowedNetworkToolIds = allowedNetworkToolIds,
            ModelVisibleToolSnapshotId = null,
            ModelContextWindowTokens = null,
            ModelRequestOutputReserveTokens = null,
            ModelEffectiveInputBudgetTokens = null,
            VisibleSourceFrontier = null,
            RequestedBy = $"agent:{plan.DelegationId.Value:D}:{frozenAssignment.AssignmentId.Value:D}",
            ActivityOrigin = parent.ActivityOrigin ?? $"agent:{plan.DelegationId.Value:D}:{frozenAssignment.AssignmentId.Value:D}",
        };

        EffectiveSessionPolicy? DerivePolicy()
        {
            if (parent.SecurityPolicy is not { } policy)
            {
                return null;
            }

            parent.PolicyBinding?.Validate(policy);
            var childRoot = Path.GetFullPath(repositoryPath);
            var scope = approvedRoots.Select(root => Path.GetFullPath(root, childRoot)).ToArray();
            ImmutableArray<string> NarrowRoots(ImmutableArray<string> roots) =>
            [
                .. roots.Select(root => RepositoryPathPolicy.ContainsPath(policy.RepositoryPath, root)
                        ? Path.GetFullPath(Path.GetRelativePath(policy.RepositoryPath, root), childRoot) : root)
                    .SelectMany(root => scope.Select(restriction => RepositoryPathPolicy.ContainsPath(root, restriction)
                        ? restriction : RepositoryPathPolicy.ContainsPath(restriction, root) ? root : null))
                    .OfType<string>().Distinct(RepositoryPathPolicy.GetPathComparer(childRoot)),
            ];
            return policy with
            {
                ParentFingerprint = policy.Fingerprint,
                MaximumTrust = trust < policy.MaximumTrust ? trust : policy.MaximumTrust,
                Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    policy.Fingerprint + ":" + plan.DelegationId.Value + ":" + frozenAssignment.AssignmentId.Value))),
                RepositoryPath = childRoot,
                AllowedTools = denyAllTools ? [] : policy.AllowedTools.IsDefault
                    ? [.. allowed] : [.. policy.AllowedTools.Intersect(allowed, StringComparer.OrdinalIgnoreCase)],
                DeniedTools = [.. policy.DeniedTools.Concat(denied).Distinct(StringComparer.OrdinalIgnoreCase)],
                ProhibitedPaths = [.. policy.ProhibitedPaths.Concat(frozenAssignment.Policy.ProhibitedPaths).Distinct(StringComparer.Ordinal)],
                WriteFolders = parent.AllowDirectWrites && frozenAssignment.Mode == AgentRunMode.SharedWorkspace ? NarrowRoots(policy.WriteFolders) : [],
                EditableRoots = frozenAssignment.Mode is AgentRunMode.ReadOnlyBaseline or AgentRunMode.ReadOnlyReview ? [] : NarrowRoots(policy.EditableRoots),
                MutationRoots = frozenAssignment.Mode is AgentRunMode.ReadOnlyBaseline or AgentRunMode.ReadOnlyReview ? [] : NarrowRoots(policy.MutationRoots),
                NetworkHostRestriction = frozenAssignment.Policy.AllowNetwork ? policy.NetworkHostRestriction : [],
                AllowedNetworkHosts = frozenAssignment.Policy.AllowNetwork ? policy.AllowedNetworkHosts : [],
            };
        }
    }

    private static string[] IntersectApprovedRoots(
        IReadOnlyList<string> parentRoots,
        IEnumerable<string> childRoots,
        string repositoryPath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        string[] normalizedParents =
        [
            .. parentRoots.Select(root => NormalizeRoot(root, repositoryRoot)),
        ];
        string[] normalizedChildren =
        [
            .. childRoots.DefaultIfEmpty(".").Select(root => NormalizeRoot(root, repositoryRoot)),
        ];
        return
        [
            .. normalizedParents
            .SelectMany(parentRoot => normalizedChildren.Select(childRoot =>
                IsWithin(childRoot, parentRoot, comparison)
                    ? childRoot
                    : IsWithin(parentRoot, childRoot, comparison) ? parentRoot : null))
            .Where(root => root is not null)
            .Select(root => Path.GetRelativePath(repositoryRoot, root ?? repositoryRoot).Replace('\\', '/'))
            .Distinct(comparison == StringComparison.OrdinalIgnoreCase
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal),
        ];
    }

    private static string NormalizeRoot(string root, string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root, repositoryRoot));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!IsWithin(normalized, repositoryRoot, comparison))
        {
            throw new UnauthorizedAccessException("An approved root escapes the repository.");
        }

        return normalized;
    }

    private static bool IsWithin(string candidate, string root, StringComparison comparison)
    {
        return candidate.Equals(root, comparison)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}

/// <summary>Thread-safe hierarchical usage ledger that rejects budget exhaustion without sibling borrowing.</summary>
public sealed class AgentBudgetLedger
{
    private readonly AgentResourceBudget _budget;
    private readonly Lock _gate = new();
    private AgentResourceUsage _usage = new();

    /// <summary>Initializes a new instance of the <see cref="AgentBudgetLedger"/> class.</summary>
    public AgentBudgetLedger(AgentResourceBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        _budget = budget;
    }

    /// <summary>Gets an immutable current usage snapshot.</summary>
    public AgentResourceUsage Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _usage;
            }
        }
    }

    /// <summary>Charges one usage delta atomically or rejects the operation before oversubscription.</summary>
    public void Charge(AgentResourceUsage delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.ModelTokens < 0
            || delta.ToolCalls < 0
            || delta.EvidenceItems < 0
            || delta.Files < 0
            || delta.Bytes < 0
            || delta.Mutations < 0
            || delta.Processes < 0
            || delta.Builds < 0
            || delta.Tests < 0
            || delta.Corrections < 0
            || delta.WallTime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(delta),
                "Resource usage deltas cannot be negative.");
        }

        lock (_gate)
        {
            var next = new AgentResourceUsage
            {
                ModelTokens = checked(_usage.ModelTokens + delta.ModelTokens),
                ToolCalls = checked(_usage.ToolCalls + delta.ToolCalls),
                EvidenceItems = checked(_usage.EvidenceItems + delta.EvidenceItems),
                Files = checked(_usage.Files + delta.Files),
                Bytes = checked(_usage.Bytes + delta.Bytes),
                Mutations = checked(_usage.Mutations + delta.Mutations),
                Processes = checked(_usage.Processes + delta.Processes),
                Builds = checked(_usage.Builds + delta.Builds),
                Tests = checked(_usage.Tests + delta.Tests),
                Corrections = checked(_usage.Corrections + delta.Corrections),
                WallTime = _usage.WallTime + delta.WallTime,
            };
            if (_budget.EnforceLimits
                && (next.ModelTokens > _budget.ModelTokens
                    || next.ToolCalls > _budget.ToolCalls
                    || next.EvidenceItems > _budget.EvidenceItems
                    || next.Files > _budget.Files
                    || next.Bytes > _budget.Bytes
                    || next.Mutations > _budget.Mutations
                    || next.Processes > _budget.Processes
                    || next.Builds > _budget.Builds
                    || next.Tests > _budget.Tests
                    || next.Corrections > _budget.Corrections
                    || next.WallTime > _budget.WallTime))
            {
                throw new InvalidOperationException("The child resource budget is exhausted.");
            }

            _usage = next;
        }
    }
}
