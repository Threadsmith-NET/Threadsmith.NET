namespace Threadsmith.Skills;

using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>Resolves skill requirements against current host, tool, model, trust, and phase facts.</summary>
public sealed class SkillCompatibilityEvaluator : ISkillCompatibilityEvaluator
{
    private static readonly HashSet<RunPhase> EligiblePhases =
    [
        RunPhase.Intake,
        RunPhase.EvidenceCollection,
        RunPhase.Completion,
    ];

    private readonly ConfiguredModelCatalog _models;
    private readonly IModelSelectionPolicy _selection;
    private readonly ToolRegistry _tools;
    private readonly string _hostVersion;

    /// <summary>Initializes a new instance of the <see cref="SkillCompatibilityEvaluator"/> class.</summary>
    public SkillCompatibilityEvaluator(
        ToolRegistry tools,
        ConfiguredModelCatalog models,
        string hostVersion,
        IModelSelectionPolicy? selection = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostVersion);
        _tools = tools;
        _models = models;
        _hostVersion = hostVersion;
        _selection = selection ?? new DefaultModelSelectionPolicy(models);
    }

    /// <inheritdoc />
    public SkillCompatibilityResult Evaluate(
        SkillCatalogCandidate candidate,
        SkillInvocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(request);
        var denials = new List<string>();
        if (!candidate.Enabled)
        {
            denials.Add("package-not-enabled");
        }

        if (!EligiblePhases.Contains(request.Phase))
        {
            denials.Add("phase-ineligible");
        }

        var requirements = candidate.Metadata.Requirements;
        if (request.Trust < requirements.MinimumTrust)
        {
            denials.Add("insufficient-repository-trust");
        }

        if (SemanticVersionComparer.Instance.Compare(
                _hostVersion,
                requirements.MinimumHostVersion) < 0
            || SemanticVersionComparer.Instance.Compare(
                _hostVersion,
                requirements.MaximumHostVersion) > 0)
        {
            denials.Add("host-version-incompatible");
        }

        IReadOnlyDictionary<string, ToolDefinition> definitions = _tools.Definitions
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var availableRequired = new List<string>();
        foreach (var toolId in requirements.RequiredTools)
        {
            if (!definitions.TryGetValue(toolId, out var definition))
            {
                denials.Add($"required-tool-unavailable:{toolId}");
                continue;
            }

            if (requirements.ToolContractVersions.TryGetValue(toolId, out var minimumVersion)
                && SemanticVersionComparer.Instance.Compare(definition.Version, minimumVersion) < 0)
            {
                denials.Add($"tool-contract-incompatible:{toolId}");
                continue;
            }

            availableRequired.Add(toolId);
        }

        string[] unavailableOptional =
        [
            .. requirements.OptionalTools
                .Where(tool => !definitions.ContainsKey(tool))
                .OrderBy(item => item, StringComparer.Ordinal),
        ];
        ModelProfile[] compatibleModels =
        [
            .. _models.Profiles
                .Where(profile => MeetsModelRequirements(
                    profile,
                    requirements.Model,
                    request.Sensitivity))
                .OrderBy(profile => profile.Name, StringComparer.Ordinal),
        ];
        var requiresModel = candidate.Metadata.Workflow.Steps.Any(step =>
            step.Kind is SkillWorkflowStepKind.InvokeProcedure
                or SkillWorkflowStepKind.CollectEvidence
                or SkillWorkflowStepKind.Summarize);
        if (requiresModel && compatibleModels.Length == 0)
        {
            denials.Add("no-compatible-model");
        }
        else if (requiresModel)
        {
            compatibleModels = OrderByHostSelection(
                compatibleModels,
                requirements.Model,
                request.Sensitivity);
        }

        return new SkillCompatibilityResult
        {
            IsCompatible = denials.Count == 0,
            DenialReasons = denials,
            AvailableRequiredTools = availableRequired,
            UnavailableOptionalTools = unavailableOptional,
            CompatibleModels = compatibleModels.Select(item => item.Id).ToArray(),
        };
    }

    /// <summary>Caps a package budget at the already-authoritative host ceiling.</summary>
    public static SkillBudget CapBudget(SkillBudget package, SkillBudget host)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(host);
        return new SkillBudget
        {
            ContentTokens = Math.Min(package.ContentTokens, host.ContentTokens),
            WorkflowSteps = Math.Min(package.WorkflowSteps, host.WorkflowSteps),
            ModelTurns = Math.Min(package.ModelTurns, host.ModelTurns),
            ToolCalls = Math.Min(package.ToolCalls, host.ToolCalls),
            Mutations = Math.Min(package.Mutations, host.Mutations),
            ValidationAttempts = Math.Min(package.ValidationAttempts, host.ValidationAttempts),
            DelegatedChildren = Math.Min(package.DelegatedChildren, host.DelegatedChildren),
            ParallelChildren = Math.Min(package.ParallelChildren, host.ParallelChildren),
            Worktrees = Math.Min(package.Worktrees, host.Worktrees),
            ReviewerFindings = Math.Min(package.ReviewerFindings, host.ReviewerFindings),
            WallTime = package.WallTime < host.WallTime ? package.WallTime : host.WallTime,
        };
    }

    private ModelProfile[] OrderByHostSelection(
        IReadOnlyList<ModelProfile> compatible,
        SkillModelRequirements requirements,
        ConversationSensitivity sensitivity)
    {
        var workload = ParseWorkloads(requirements).FirstOrDefault(WorkloadClass.General);
        var selection = _selection.Resolve(
            new ModelSelectionRequest
            {
                WorkloadClass = workload,
                RequiredCapabilities = new ModelCapabilitySet
                {
                    Streaming = true,
                    ToolCalls = requirements.RequiresToolCalls,
                    StructuredOutput = requirements.RequiresStructuredOutput,
                },
                Constraints = new ModelSelectionConstraints
                {
                    MinimumContextWindow = requirements.MinimumContextWindow,
                    ContainsSensitiveData = sensitivity == ConversationSensitivity.Sensitive,
                },
                PreferredProfileId = requirements.AllowedProfiles.FirstOrDefault() is { } preferred
                    && preferred != default
                        ? preferred
                        : null,
            });
        return
        [
            .. compatible.OrderBy(
                profile => profile.Id == selection.ProfileId ? 0 : 1),
        ];
    }

    private static bool MeetsModelRequirements(
        ModelProfile profile,
        SkillModelRequirements requirements,
        ConversationSensitivity sensitivity)
    {
        if (profile.ContextWindow < requirements.MinimumContextWindow
            || (requirements.RequiresToolCalls && !profile.Capabilities.ToolCalls)
            || (requirements.RequiresStructuredOutput && !profile.Capabilities.StructuredOutput)
            || (requirements.AllowedProfiles.Count > 0
                && !requirements.AllowedProfiles.Contains(profile.Id))
            || requirements.DeniedProfiles.Contains(profile.Id)
            || (sensitivity == ConversationSensitivity.Sensitive
                && profile.SensitiveDataPolicy != ModelSensitiveDataPolicy.Allowed))
        {
            return false;
        }

        var workloads = ParseWorkloads(requirements);
        if (workloads.Length != requirements.Workloads.Count)
        {
            return false;
        }

        return workloads.Length == 0
            || profile.IntendedWorkloadClasses.Count == 0
            || workloads.Any(profile.IntendedWorkloadClasses.Contains);
    }

    private static WorkloadClass[] ParseWorkloads(SkillModelRequirements requirements)
    {
        return
        [
            .. requirements.Workloads
                .Select(item => Enum.TryParse(item, ignoreCase: true, out WorkloadClass parsed)
                    ? parsed
                    : (WorkloadClass?)null)
                .Where(item => item is not null)
                .Select(item => item ?? WorkloadClass.General),
        ];
    }
}
