namespace Threadsmith.Execution;

using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>Builds one immutable Plan 91 delegation from validated model-facing input.</summary>
public sealed class DelegateAgentsPlanFactory
{
    private const string ContextPolicyVersion = "agent-context/2";
    private const string InheritToolPolicyVersion = "delegate-agents-inherit/2";
    private const string ReadOnlyToolPolicyVersion = "delegate-agents-read-only/3";
    private readonly DelegateAgentsOptions _options;
    private readonly SessionModelPreferences _preferences;
    private readonly IPromptLoader _prompts;
    private readonly IConversationToolSnapshotStore _toolSnapshots;
    private readonly ITransactionalWorkspaceResolver _workspaces;
    private readonly AgentModelSelector? _models;

    /// <summary>Initializes a new instance of the <see cref="DelegateAgentsPlanFactory"/> class.</summary>
    public DelegateAgentsPlanFactory(
        ITransactionalWorkspaceResolver workspaces,
        SessionModelPreferences preferences,
        IConversationToolSnapshotStore toolSnapshots,
        IPromptLoader prompts,
        DelegateAgentsOptions? options = null,
        AgentModelSelector? models = null)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(preferences);
        ArgumentNullException.ThrowIfNull(toolSnapshots);
        ArgumentNullException.ThrowIfNull(prompts);
        _options = options ?? new DelegateAgentsOptions();
        _options.Validate();
        _workspaces = workspaces;
        _preferences = preferences;
        _prompts = prompts;
        _toolSnapshots = toolSnapshots;
        _models = models;
    }

    /// <summary>Validates, captures, and freezes one fork/join plan.</summary>
    public DelegationPlan Create(DelegateAgentsInput input, ToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(context.Invocation.RequestedBy, "model", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Subagents require a model-requested delegate_agents tool call.");
        }

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
                ToolInvocationId = context.ToolInvocationId,
                RepositoryIdentity = baseline.RepositoryPath,
                BaselineIdentity = WorkspaceBaselineIdentity.Create(baseline),
                WorkspaceId = workspaceId,
            },
            Assignments = assignments,
            AssignmentLimits = _options.CreateAssignmentLimits(),
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
        var inherit = request.ToolAccess == DelegateAgentToolAccess.Inherit;
        var allowedNetworkToolIds = definitions
            .Where(definition => inherit || definition.ReadOnlySubagentNetworkAvailable)
            .Select(definition => definition.Id)
            .ToArray();
        var assignment = new AgentAssignment
        {
            AssignmentId = AgentAssignmentId.New(),
            ChildRunId = RunId.New(),
            Role = request.Role,
            Mode = inherit ? AgentRunMode.SharedWorkspace
                : request.Role is AgentRole.Explorer or AgentRole.Implementer
                    ? AgentRunMode.ReadOnlyBaseline
                    : AgentRunMode.ReadOnlyReview,
            Objective = request.Task.Trim(),
            Tasks = [_prompts.Get(PromptFileNames.ContextChildAgentStructuredFindingsTask)],
            InitialContext = request.Context.Trim(),
            OutputSchema = DelegateAgentsContract.ResponseSchema,
            StoppingCondition = "Stop after the assigned question is answered or the bounded evidence surface is exhausted.",
            Deadline = _options.EffectiveChildBudget.CreateDeadline(acceptedAt),
            Scope = CreateScope(context.Invocation),
            Policy = new AgentPolicySnapshot
            {
                AllowedToolIds = definitions.Select(definition => definition.Id).ToArray(),
                AllowedNetworkToolIds = allowedNetworkToolIds,
                TrustCeiling = !inherit && context.Invocation.TrustLevel > RepositoryTrustLevel.TrustedBuild
                    ? RepositoryTrustLevel.TrustedBuild
                    : context.Invocation.TrustLevel,
                AllowNetwork = inherit || allowedNetworkToolIds.Length > 0,
                AllowProcesses = inherit && definitions.Any(definition =>
                    definition.Category is ToolCategory.ProcessExecution or ToolCategory.CodeExecution),
                ProhibitedPaths = context.Invocation.ProhibitedPaths.ToArray(),
                Sensitivity = context.Invocation.Sensitivity,
                ResultLimits = _options.ResultLimits,
                ModelProfileId = context.Invocation.ModelProfileId ?? preference.ProfileId ?? default,
                ReasoningLevel = context.Invocation.ModelReasoningLevel ?? preference.Reasoning.ToString(),
                ModelSelectionRationale = context.Invocation.ModelProfileId is null && preference.ProfileId is null
                    ? "Select a compatible profile for the child role."
                    : "Prefer the frozen parent profile, subject to role configuration and request compatibility.",
                ContextPolicyVersion = ContextPolicyVersion,
                ToolPolicyVersion = request.ToolAccess == DelegateAgentToolAccess.ReadOnly
                    ? ReadOnlyToolPolicyVersion
                    : InheritToolPolicyVersion,
            },
            Budget = _options.EffectiveChildBudget,
        };
        return _models is null ? assignment : assignment with
        {
            Policy = _models.FreezePolicy(assignment, inheritTrustedModelCatalog: context.Invocation.ModelUsesTrustedCatalog),
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

                // Each child gets its own evidence reader bound to its evidence ownership.
                .Where(registration => registration.Implementation is not ChildAgentEvidenceTool)
                .Select(registration => registration.Tool.Definition)
                .Where(definition => definition.SubagentAvailable
                    && ConversationToolAvailability.IsAdvertised(definition, context.Invocation)
                    && (access == DelegateAgentToolAccess.Inherit
                    || (definition.Category is not ToolCategory.Workflow
                        and not ToolCategory.ProcessExecution
                        and not ToolCategory.CodeExecution
                        && (definition.Category is not ToolCategory.ExternalSearch
                            || definition.ReadOnlySubagentNetworkAvailable)
                        && definition.SideEffect == ToolSideEffect.ReadOnly
                        && definition.RequiredApproval == ApprovalLevel.None)))
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
