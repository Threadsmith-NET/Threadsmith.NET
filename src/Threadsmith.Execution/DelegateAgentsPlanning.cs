namespace Threadsmith.Execution;

using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Builds one immutable Plan 91 delegation from validated model-facing input.</summary>
public sealed class DelegateAgentsPlanFactory
{
    private const string ContextPolicyVersion = "agent-context/2";
    private const string InheritToolPolicyVersion = "delegate-agents-inherit/1";
    private const string ReadOnlyToolPolicyVersion = "delegate-agents-read-only/1";
    private readonly DelegateAgentsOptions _options;
    private readonly SessionModelPreferences _preferences;
    private readonly IConversationToolSnapshotStore _toolSnapshots;
    private readonly ITransactionalWorkspaceResolver _workspaces;

    /// <summary>Initializes a new instance of the <see cref="DelegateAgentsPlanFactory"/> class.</summary>
    public DelegateAgentsPlanFactory(
        ITransactionalWorkspaceResolver workspaces,
        SessionModelPreferences preferences,
        IConversationToolSnapshotStore toolSnapshots,
        DelegateAgentsOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(toolSnapshots);
        _options = options ?? new DelegateAgentsOptions();
        _options.Validate();
        _workspaces = workspaces;
        _preferences = preferences;
        _toolSnapshots = toolSnapshots;
    }

    /// <summary>Validates, captures, and freezes one fork/join plan.</summary>
    public DelegationPlan Create(DelegateAgentsInput input, ToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        DelegateAgentsInputValidator.Validate(input, _options);
        var workspaceId = context.Invocation.WorkspaceId
            ?? throw new InvalidOperationException("Agent delegation requires an opened workspace.");
        var baseline = _workspaces.GetWorkspace(workspaceId).Baseline;
        if (baseline.WorkspaceId != workspaceId
            || !PathsEqual(baseline.RepositoryPath, context.Invocation.RepositoryPath))
        {
            throw new InvalidOperationException("Agent delegation workspace context does not match the active baseline.");
        }

        var acceptedAt = DateTimeOffset.UtcNow;
        var preference = _preferences.Capture();
        AgentAssignment[] assignments = [.. input.Agents.Select(request => CreateAssignment(
            request,
            context,
            preference,
            acceptedAt))];
        var plan = new DelegationPlan
        {
            DelegationId = DelegationId.New(),
            Provenance = new DelegationProvenance
            {
                SessionId = context.SessionId,
                ParentRunId = context.RunId,
                RepositoryIdentity = baseline.RepositoryPath,
                BaselineIdentity = WorkspaceBaselineIdentity.Create(baseline),
                WorkspaceId = workspaceId,
            },
            Assignments = assignments,
            ParentBudget = SumBudgets(assignments),
            AcceptedAt = acceptedAt,
        };
        DelegationPlanValidator.Validate(plan);
        return plan;
    }

    private AgentAssignment CreateAssignment(
        DelegateAgentRequest request,
        ToolExecutionContext context,
        SessionModelPreferenceSnapshot preference,
        DateTimeOffset acceptedAt)
    {
        var definitions = ResolveDefinitions(request.ToolAccess, context);
        var allowNetwork = request.ToolAccess == DelegateAgentToolAccess.Inherit
            && definitions.Any(definition => definition.Category == ToolCategory.ExternalSearch);
        return new AgentAssignment
        {
            AssignmentId = AgentAssignmentId.New(),
            ChildRunId = RunId.New(),
            Role = AgentRole.Explorer,
            Mode = AgentRunMode.ReadOnlyBaseline,
            Objective = request.Task.Trim(),
            Tasks = ["Return cited structured findings, explicit omissions, uncertainty, and coverage notes."],
            InitialContext = request.Context.Trim(),
            OutputSchema = DelegateAgentsContract.FindingSchema,
            StoppingCondition = "Stop after the assigned question is answered or the bounded evidence surface is exhausted.",
            Deadline = acceptedAt + _options.ChildBudget.WallTime,
            Scope = CreateScope(context.Invocation),
            Policy = new AgentPolicySnapshot
            {
                AllowedToolIds = definitions.Select(definition => definition.Id).ToArray(),
                DeniedToolIds = [DelegateAgentsContract.ToolId],
                TrustCeiling = context.Invocation.TrustLevel > RepositoryTrustLevel.TrustedBuild
                    ? RepositoryTrustLevel.TrustedBuild
                    : context.Invocation.TrustLevel,
                AllowNetwork = allowNetwork,
                AllowProcesses = false,
                ProhibitedPaths = context.Invocation.ProhibitedPaths.ToArray(),
                Sensitivity = context.Invocation.Sensitivity,
                ModelProfileId = preference.ProfileId ?? default,
                ReasoningLevel = preference.Reasoning.ToString(),
                ModelSelectionRationale = preference.ProfileId is null
                    ? "Host model selection will resolve an Explorer-compatible profile."
                    : "Prefer the frozen parent profile, subject to Explorer capability and sensitivity policy.",
                ContextPolicyVersion = ContextPolicyVersion,
                ToolPolicyVersion = request.ToolAccess == DelegateAgentToolAccess.ReadOnly
                    ? ReadOnlyToolPolicyVersion
                    : InheritToolPolicyVersion,
            },
            Budget = _options.ChildBudget,
        };
    }

    private ToolDefinition[] ResolveDefinitions(
        DelegateAgentToolAccess access,
        ToolExecutionContext context)
    {
        var snapshotId = context.Invocation.ModelVisibleToolSnapshotId
            ?? throw new InvalidOperationException(
                "Agent delegation requires the exact parent model-visible tool snapshot.");
        var registrations = _toolSnapshots.Resolve(snapshotId, context.SessionId, context.RunId);
        return
        [
            .. registrations
                .Select(registration => registration.Tool.Definition)
                .Where(definition => definition.Category != ToolCategory.Workflow)
                .Where(definition => definition.Category is not ToolCategory.ProcessExecution
                    and not ToolCategory.CodeExecution)
                .Where(definition => definition.SideEffect == ToolSideEffect.ReadOnly)
                .Where(definition => !string.Equals(
                    definition.Id,
                    DelegateAgentsContract.ToolId,
                    StringComparison.OrdinalIgnoreCase))
                .Where(definition => definition.RequiredApproval == ApprovalLevel.None)
                .Where(definition => access == DelegateAgentToolAccess.Inherit
                    || definition.Category != ToolCategory.ExternalSearch)
                .OrderBy(definition => definition.Id, StringComparer.Ordinal),
        ];
    }

    private static AgentAssignmentScope CreateScope(ToolInvocationContext context)
    {
        var repositoryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.RepositoryPath));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var roots = context.ApprovedRoots.Count == 0 ? ["."] : context.ApprovedRoots;
        string[] directories =
        [
            .. roots.Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root, repositoryRoot)))
                .Select(root => root.Equals(repositoryRoot, comparison)
                    ? string.Empty
                    : root.StartsWith(repositoryRoot + Path.DirectorySeparatorChar, comparison)
                        ? Path.GetRelativePath(repositoryRoot, root).Replace('\\', '/')
                        : throw new UnauthorizedAccessException("A parent approved root escapes the repository."))
                .Where(root => root.Length > 0)
                .Distinct(comparison == StringComparison.OrdinalIgnoreCase
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal),
        ];
        return new AgentAssignmentScope
        {
            Directories = directories,
            IsOwnershipProven = true,
        };
    }

    private static AgentResourceBudget SumBudgets(IReadOnlyList<AgentAssignment> assignments)
    {
        return AgentResourceBudget.Aggregate(assignments.Select(item => item.Budget).ToArray());
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)).Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            comparison);
    }
}
