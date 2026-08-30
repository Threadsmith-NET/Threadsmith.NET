namespace Threadsmith.Models;

using Threadsmith.Core;

/// <summary>Host-owned selected child model and reasoning rationale.</summary>
public sealed record AgentModelSelection(
    ModelProfileId ProfileId,
    ReasoningLevel ReasoningLevel,
    IReadOnlyList<string> Rationale)
{
    /// <summary>Selected profile context-window authority.</summary>
    public required int ContextWindowTokens { get; init; }

    /// <summary>Selected profile request output reserve.</summary>
    public required int OutputReserveTokens { get; init; }

    /// <summary>Selected profile hard output limit.</summary>
    public required int MaximumOutputTokens { get; init; }
}

/// <summary>Selects a configured model for one frozen child role and workload.</summary>
public sealed class AgentModelSelector
{
    private readonly ConfiguredModelCatalog _catalog;
    private readonly IModelSelectionPolicy _selection;

    /// <summary>Initializes a new instance of the <see cref="AgentModelSelector"/> class.</summary>
    public AgentModelSelector(
        ConfiguredModelCatalog catalog,
        IModelSelectionPolicy selection)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(selection);
        _catalog = catalog;
        _selection = selection;
    }

    /// <summary>Selects a compatible configured model without allowing the child to switch it.</summary>
    public AgentModelSelection Select(AgentAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ModelProfileId? preferredProfileId = assignment.Policy.ModelProfileId == default
            ? null
            : assignment.Policy.ModelProfileId;
        var request = CreateRequest(
            assignment.Role,
            assignment.Budget,
            assignment.Policy.Sensitivity,
            assignment.Policy.AllowedToolIds.Count > 0,
            preferredProfileId);
        var selected = _selection.Resolve(request);
        var profile = _catalog.Profiles.Single(item => item.Id == selected.ProfileId);
        var retainedPreferredProfile = preferredProfileId == selected.ProfileId;
        var reasoning = retainedPreferredProfile
            ? ParseReasoning(assignment.Policy.ReasoningLevel, profile)
            : profile.DefaultReasoningLevel;
        var reasoningSource = retainedPreferredProfile
            ? "frozen-preference"
            : "effective-profile-default";
        return new AgentModelSelection(
            selected.ProfileId,
            reasoning,
            [
                .. selected.Rationale,
                $"role={assignment.Role}",
                $"reasoning={reasoning}",
                $"reasoningSource={reasoningSource}",
            ])
        {
            ContextWindowTokens = profile.ContextWindow,
            OutputReserveTokens = profile.EffectiveRequestOutputTokenReserve,
            MaximumOutputTokens = profile.MaximumOutputTokens,
        };
    }

    /// <summary>Returns whether the catalog can satisfy the model-callable Explorer contract.</summary>
    public bool CanSelectExplorer(
        AgentResourceBudget budget,
        ConversationSensitivity sensitivity,
        bool requireToolCalls = true)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var request = CreateRequest(
            AgentRole.Explorer,
            budget,
            sensitivity,
            requireToolCalls,
            preferredProfileId: null);
        return _catalog.Profiles.Any(profile =>
            ModelCapabilityNegotiator.Negotiate(profile, request).IsCompatible);
    }

    private static ModelSelectionRequest CreateRequest(
        AgentRole role,
        AgentResourceBudget budget,
        ConversationSensitivity sensitivity,
        bool requireToolCalls,
        ModelProfileId? preferredProfileId)
    {
        return new ModelSelectionRequest
        {
            WorkloadClass = role switch
            {
                AgentRole.Implementer => WorkloadClass.CodeEdit,
                AgentRole.SecurityReviewer
                    or AgentRole.TestReviewer
                    or AgentRole.PerformanceReviewer
                    or AgentRole.ArchitectureReviewer => WorkloadClass.Review,
                _ => WorkloadClass.General,
            },
            RequiredCapabilities = new ModelCapabilitySet
            {
                Streaming = true,
                ToolCalls = requireToolCalls,
                StructuredOutput = true,
            },
            Constraints = new ModelSelectionConstraints
            {
                MinimumContextWindow = budget.EnforceLimits
                    ? checked((int)Math.Min(budget.ModelTokens, int.MaxValue))
                    : 0,
                ContainsSensitiveData = sensitivity == ConversationSensitivity.Sensitive,
            },
            PreferredProfileId = preferredProfileId,
        };
    }

    private static ReasoningLevel ParseReasoning(string configured, ModelProfile profile)
    {
        var reasoning = string.IsNullOrWhiteSpace(configured)
            ? profile.DefaultReasoningLevel
            : Enum.TryParse(configured, ignoreCase: true, out ReasoningLevel parsed)
                ? parsed
                : throw new InvalidDataException("Child reasoning level is invalid.");
        if (!profile.SupportsReasoningLevel(reasoning))
        {
            throw new InvalidOperationException("Selected child model does not support the requested reasoning level.");
        }

        return reasoning;
    }
}
