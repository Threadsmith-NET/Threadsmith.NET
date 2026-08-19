namespace Threadsmith.Models;

using Threadsmith.Core;

/// <summary>Host-owned selected child model and reasoning rationale.</summary>
public sealed record AgentModelSelection(
    ModelProfileId ProfileId,
    ReasoningLevel ReasoningLevel,
    IReadOnlyList<string> Rationale);

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
        var request = new ModelSelectionRequest
        {
            WorkloadClass = assignment.Role switch
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
                ToolCalls = assignment.Policy.AllowedToolIds.Count > 0,
                StructuredOutput = true,
            },
            Constraints = new ModelSelectionConstraints
            {
                MinimumContextWindow = checked((int)Math.Min(assignment.Budget.ModelTokens, int.MaxValue)),
                ContainsSensitiveData = assignment.Policy.Sensitivity == ConversationSensitivity.Sensitive,
            },
            PreferredProfileId = assignment.Policy.ModelProfileId == default
                ? null
                : assignment.Policy.ModelProfileId,
        };
        var selected = _selection.Resolve(request);
        var profile = _catalog.Profiles.Single(item => item.Id == selected.ProfileId);
        var reasoning = ParseReasoning(assignment.Policy.ReasoningLevel, profile);
        return new AgentModelSelection(
            selected.ProfileId,
            reasoning,
            [.. selected.Rationale, $"role={assignment.Role}", $"reasoning={reasoning}"]);
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
