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

    /// <summary>Gets the exact provider-owned instruction contribution for the selected profile.</summary>
    public ModelProviderInstructions? ProviderInstructions { get; init; }

    /// <summary>Frozen preference and effective request route for checkpoints and diagnostics.</summary>
    public AgentModelProvenance? Provenance { get; init; }

    /// <summary>Whether this selection must dispatch through the repository-excluding provider.</summary>
    public bool UsesTrustedCatalog => Provenance?.UsesTrustedCatalog == true;
}

/// <summary>Selects a configured model for one frozen child role and workload.</summary>
public sealed class AgentModelSelector
{
    private readonly ConfiguredModelCatalog _catalog;
    private readonly IModelProviderInstructionResolver? _providerInstructionResolver;
    private readonly IModelProviderInstructionResolver? _trustedProviderInstructionResolver;
    private readonly IModelSelectionPolicy _selection;
    private readonly IModelSelectionPolicy _trustedSelection;
    private readonly AgentRoleModelPolicy _roleModels;

    /// <summary>Initializes a new instance of the <see cref="AgentModelSelector"/> class.</summary>
    public AgentModelSelector(
        ConfiguredModelCatalog catalog,
        IModelSelectionPolicy selection,
        IModelProviderInstructionResolver? providerInstructionResolver = null,
        AgentRoleModelPolicy? roleModels = null,
        IModelProviderInstructionResolver? trustedProviderInstructionResolver = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(selection);
        _catalog = catalog;
        _selection = selection;
        _providerInstructionResolver = providerInstructionResolver;
        _roleModels = roleModels ?? new AgentRoleModelPolicy();
        _trustedProviderInstructionResolver = trustedProviderInstructionResolver;
        _trustedSelection = new DefaultModelSelectionPolicy(_roleModels.TrustedCatalog);
    }

    /// <summary>Selects a compatible configured model without allowing the child to switch it.</summary>
    public AgentModelSelection Select(AgentAssignment assignment, bool? requireToolCalls = null)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var preference = assignment.Policy.ModelSelection ?? CreatePreference(assignment);
        var request = CreateRequest(
            assignment.Role,
            assignment.Budget,
            assignment.Policy.Sensitivity,
            requireToolCalls ?? assignment.Policy.AllowedToolIds.Count > 0,
            preference.EffectiveProfileId == default ? null : preference.EffectiveProfileId);
        return Resolve(assignment.Role, preference, request);
    }

    /// <summary>Freezes precedence and initial compatible selection before persisting an assignment.</summary>
    /// <remarks>Existing provenance is retained unchanged across restore and process configuration changes.</remarks>
    public AgentPolicySnapshot FreezePolicy(
        AgentAssignment assignment,
        ModelProfileId? applicationProfileId = null,
        ReasoningLevel? applicationReasoningLevel = null,
        bool inheritModelPreference = true,
        bool? requireToolCalls = null)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        if (assignment.Policy.ModelSelection is not null)
        {
            return assignment.Policy;
        }

        var preference = CreatePreference(
            assignment,
            applicationProfileId,
            applicationReasoningLevel,
            inheritModelPreference);
        var selected = Select(
            assignment with { Policy = assignment.Policy with { ModelSelection = preference } },
            requireToolCalls);
        return assignment.Policy with
        {
            ModelProfileId = selected.ProfileId,
            ReasoningLevel = selected.ReasoningLevel.ToString(),
            ModelSelectionRationale = string.Join("; ", selected.Rationale),
            ModelSelection = selected.Provenance,
        };
    }

    /// <summary>Checks the complete assembled request before every child call and selects a compatible fallback.</summary>
    /// <remarks>
    /// A changed profile requires the caller to rebuild provider instructions, output reserve, and wire estimates,
    /// then repeat this check. The caller retains deadline cancellation through provider enumeration.
    /// When <paramref name="useProfileOutputReserve"/> is true, each candidate supplies its own output reserve and provider instructions;
    /// the request's output ceiling is a provisional profile default. Selection constraints remain independent
    /// hard requirements and must not include the provisional wire estimate, which is checked separately.
    /// </remarks>
    public AgentModelSelection SelectForRequest(
        AgentAssignment assignment,
        ModelStreamRequest request,
        bool useProfileOutputReserve = false)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentNullException.ThrowIfNull(request);
        if (request.RunId != assignment.ChildRunId)
        {
            throw new InvalidOperationException("Child model request does not belong to the frozen assignment.");
        }

        if (assignment.Deadline <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("Child model request deadline has expired.");
        }

        var preference = assignment.Policy.ModelSelection ?? Select(assignment).Provenance
            ?? throw new InvalidOperationException("Child model selection provenance is missing.");
        var estimate = request.WireEstimate
            ?? throw new InvalidOperationException("Child model request requires a complete wire-capacity estimate.");
        var wireTokens = estimate.TotalCapacityTokens;
        if (estimate.WireInputTokens < 0 || estimate.OutputReserveTokens < 0
            || (!useProfileOutputReserve && wireTokens > int.MaxValue)
            || request.MaximumOutputTokens is <= 0)
        {
            throw new InvalidOperationException("Child model request capacity is invalid or exceeds supported limits.");
        }

        var requirements = new ModelSelectionRequest
        {
            WorkloadClass = GetWorkload(assignment.Role),
            RequiredCapabilities = request.RequiredCapabilities with
            {
                Streaming = true,
                ToolCalls = request.RequiredCapabilities.ToolCalls || request.Tools.Count > 0,
            },
            Constraints = request.SelectionConstraints with
            {
                MinimumContextWindow = useProfileOutputReserve
                    ? request.SelectionConstraints.MinimumContextWindow
                    : Math.Max(request.SelectionConstraints.MinimumContextWindow, (int)wireTokens),
                ContainsSensitiveData = request.ContainsSensitiveData
                    || request.SelectionConstraints.ContainsSensitiveData
                    || assignment.Policy.Sensitivity == ConversationSensitivity.Sensitive,
            },
            PreferredProfileId = preference.EffectiveProfileId,
        };
        return Resolve(
            assignment.Role,
            preference,
            requirements,
            useProfileOutputReserve ? null : request.MaximumOutputTokens,
            useProfileOutputReserve ? GetProfileNeutralInputTokens(request, estimate) : null);
    }

    /// <summary>Returns whether the catalog can satisfy the model-callable Explorer contract.</summary>
    public bool CanSelectExplorer(
        AgentResourceBudget budget,
        ConversationSensitivity sensitivity,
        bool requireToolCalls = true)
    {
        return CanSelectRole(AgentRole.Explorer, budget, sensitivity, requireToolCalls);
    }

    /// <summary>Returns whether an ordinary role has a compatible model for its workload and tools.</summary>
    public bool CanSelectRole(
        AgentRole role,
        AgentResourceBudget budget,
        ConversationSensitivity sensitivity,
        bool requireToolCalls = true)
    {
        ArgumentNullException.ThrowIfNull(budget);
        var request = CreateRequest(
            role,
            budget,
            sensitivity,
            requireToolCalls,
            preferredProfileId: null);
        var catalog = _roleModels.Get(role) is null ? _catalog : _roleModels.TrustedCatalog;
        return catalog.Profiles.Any(profile =>
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
            WorkloadClass = GetWorkload(role),
            RequiredCapabilities = new ModelCapabilitySet
            {
                Streaming = true,
                ToolCalls = requireToolCalls,
                StructuredOutput = false,
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

    private static WorkloadClass GetWorkload(AgentRole role)
    {
        return role switch
        {
            AgentRole.Explorer => WorkloadClass.General,
            AgentRole.Implementer => WorkloadClass.CodeEdit,
            AgentRole.SecurityReviewer or AgentRole.TestReviewer
                or AgentRole.PerformanceReviewer or AgentRole.ArchitectureReviewer => WorkloadClass.Review,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
    }

    private AgentModelProvenance CreatePreference(
        AgentAssignment assignment,
        ModelProfileId? applicationProfileId = null,
        ReasoningLevel? applicationReasoningLevel = null,
        bool inheritModelPreference = true)
    {
        var route = _roleModels.Get(assignment.Role);
        var source = AgentModelSelectionSource.Default;
        ModelProfileId? profileId = null;
        string? reasoning = null;
        if (applicationProfileId is { } pin)
        {
            source = AgentModelSelectionSource.ApplicationPin;
            profileId = pin;
            reasoning = applicationReasoningLevel?.ToString();
        }
        else if (route is not null)
        {
            source = AgentModelSelectionSource.RoleConfiguration;
            profileId = route.ProfileId;
            reasoning = route.ReasoningLevel.ToString();
        }
        else if (inheritModelPreference && assignment.Policy.ModelProfileId != default)
        {
            source = AgentModelSelectionSource.Inherited;
            profileId = assignment.Policy.ModelProfileId;
            reasoning = assignment.Policy.ReasoningLevel;
        }

        var trusted = source == AgentModelSelectionSource.RoleConfiguration;
        var catalog = trusted ? _roleModels.TrustedCatalog : _catalog;
        var profile = catalog.Profiles.FirstOrDefault(item => item.Id == profileId);
        if (applicationProfileId is not null && profile is null)
        {
            throw new InvalidOperationException("Application child model pin refers to a missing or disabled profile.");
        }

        var providerId = profile is null ? null : _roleModels.GetProviderId(profile, trusted);
        var parsedReasoning = profile is null ? ReasoningLevel.None : ParseReasoning(reasoning, profile);
        return new AgentModelProvenance
        {
            Source = source,
            ConfiguredProviderId = providerId,
            ConfiguredProfileId = profileId,
            ConfiguredReasoningLevel = profileId is null ? null : parsedReasoning.ToString(),
            EffectiveProviderId = providerId ?? string.Empty,
            EffectiveProfileId = profileId ?? default,
            EffectiveReasoningLevel = parsedReasoning.ToString(),
            UsesTrustedCatalog = trusted,
        };
    }

    private AgentModelSelection Resolve(
        AgentRole role,
        AgentModelProvenance preference,
        ModelSelectionRequest request,
        int? maximumOutputTokens = null,
        int? wireInputTokens = null)
    {
        if (!Enum.IsDefined(preference.Source)
            || (preference.Source == AgentModelSelectionSource.RoleConfiguration && !preference.UsesTrustedCatalog))
        {
            throw new InvalidOperationException("Frozen child model selection authority is invalid.");
        }

        var catalog = preference.UsesTrustedCatalog ? _roleModels.TrustedCatalog : _catalog;
        var preferred = catalog.Profiles.FirstOrDefault(item => item.Id == preference.EffectiveProfileId);
        if (preferred is not null && !string.IsNullOrEmpty(preference.EffectiveProviderId)
            && !string.Equals(
                _roleModels.GetProviderId(preferred, preference.UsesTrustedCatalog),
                preference.EffectiveProviderId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Frozen child model provider binding has changed; create a new assignment.");
        }

        // Retain complete message/tool input while substituting only candidate-owned instructions and reserves.
        var candidates = catalog.Profiles.Where(profile =>
            (maximumOutputTokens is null || profile.MaximumOutputTokens >= maximumOutputTokens)
            && NegotiateRequest(profile, request, wireInputTokens, preference.UsesTrustedCatalog).IsCompatible).ToArray();
        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                "No configured child model satisfies the request within the frozen routing authority.");
        }

        var policy = candidates.Length != catalog.Profiles.Count
            ? new DefaultModelSelectionPolicy(new ConfiguredModelCatalog(candidates, enforceHttps: false))
            : preference.UsesTrustedCatalog ? _trustedSelection : _selection;
        var selected = policy.Resolve(request);
        var profile = catalog.Get(selected.ProfileId);
        if (!NegotiateRequest(profile, request, wireInputTokens, preference.UsesTrustedCatalog).IsCompatible
            || (maximumOutputTokens is { } outputTokens && outputTokens > profile.MaximumOutputTokens))
        {
            throw new InvalidOperationException("Child model selection did not satisfy the actual request.");
        }

        var retained = selected.ProfileId == preference.EffectiveProfileId;
        var reasoning = retained
            ? ParseReasoning(preference.EffectiveReasoningLevel, profile)
            : profile.DefaultReasoningLevel;
        var fallbackReason = preference.FallbackReason;
        if (!retained && preference.EffectiveProfileId != default)
        {
            List<string> reasons = preferred is null
                ? ["frozen profile is missing or disabled"]
                : [.. NegotiateRequest(preferred, request, wireInputTokens, preference.UsesTrustedCatalog).RejectionReasons];
            if (preferred is not null && maximumOutputTokens > preferred.MaximumOutputTokens)
            {
                reasons.Add("requested output exceeds the profile limit");
            }

            fallbackReason = reasons.Count == 0
                ? "Host policy selected another compatible profile."
                : string.Join("; ", reasons);
        }

        var provenance = preference with
        {
            EffectiveProviderId = _roleModels.GetProviderId(profile, preference.UsesTrustedCatalog),
            EffectiveProfileId = profile.Id,
            EffectiveReasoningLevel = reasoning.ToString(),
            FallbackReason = fallbackReason,
        };
        return new AgentModelSelection(
            profile.Id,
            reasoning,
            [
                $"role={role}",
                $"source={provenance.Source}",
                $"reasoning={reasoning}",
                fallbackReason ?? "Selected compatible frozen preference or host default.",
            ])
        {
            ContextWindowTokens = profile.ContextWindow,
            OutputReserveTokens = profile.EffectiveRequestOutputTokenReserve,
            MaximumOutputTokens = profile.MaximumOutputTokens,
            ProviderInstructions = ResolveProviderInstructions(profile.Id, preference.UsesTrustedCatalog),
            Provenance = provenance,
        };
    }

    private ModelCapabilityNegotiationResult NegotiateRequest(
        ModelProfile profile,
        ModelSelectionRequest request,
        int? wireInputTokens,
        bool usesTrustedCatalog)
    {
        if (wireInputTokens is { } inputTokens)
        {
            var instructions = ResolveProviderInstructions(profile.Id, usesTrustedCatalog);
            var capacity = (long)inputTokens + GetProviderInstructionContribution(instructions)
                + profile.EffectiveRequestOutputTokenReserve;
            if (capacity > int.MaxValue)
            {
                return new ModelCapabilityNegotiationResult(false, ["complete request capacity exceeds supported limits"]);
            }

            request = request with
            {
                Constraints = request.Constraints with
                {
                    MinimumContextWindow = Math.Max(request.Constraints.MinimumContextWindow, (int)capacity),
                },
            };
        }

        return ModelCapabilityNegotiator.Negotiate(profile, request);
    }

    private ModelProviderInstructions? ResolveProviderInstructions(ModelProfileId profileId, bool usesTrustedCatalog)
    {
        return (usesTrustedCatalog ? _trustedProviderInstructionResolver : _providerInstructionResolver)?.Resolve(profileId);
    }

    private static int GetProfileNeutralInputTokens(ModelStreamRequest request, ModelWireEstimate estimate)
    {
        var providerEstimate = ModelWireEstimator.Estimate(
            [], default, 0, 0, request.ProviderInstructions);
        var contribution = GetProviderInstructionContribution(request.ProviderInstructions);
        if (estimate.ProviderInstructionTokens != providerEstimate.ProviderInstructionTokens
            || estimate.WireInputTokens < contribution)
        {
            throw new InvalidOperationException("Child model request provider-instruction estimate is inconsistent.");
        }

        return estimate.WireInputTokens - contribution;
    }

    private static int GetProviderInstructionContribution(ModelProviderInstructions? instructions)
    {
        if (instructions is null)
        {
            return 0;
        }

        var withInstructions = ModelWireEstimator.Estimate([], default, 0, 0, instructions);
        var withoutInstructions = ModelWireEstimator.Estimate([], default, 0, 0);
        return withInstructions.WireInputTokens - withoutInstructions.WireInputTokens;
    }

    private static ReasoningLevel ParseReasoning(string? configured, ModelProfile profile)
    {
        var reasoning = string.IsNullOrWhiteSpace(configured)
            ? profile.DefaultReasoningLevel
            : Enum.GetNames<ReasoningLevel>().Any(name => string.Equals(name, configured, StringComparison.OrdinalIgnoreCase))
                && Enum.TryParse(configured, ignoreCase: true, out ReasoningLevel parsed)
                ? parsed
                : throw new InvalidDataException("Child reasoning level is invalid.");
        if (!profile.SupportsReasoningLevel(reasoning))
        {
            throw new InvalidOperationException("Selected child model does not support the requested reasoning level.");
        }

        return reasoning;
    }
}
