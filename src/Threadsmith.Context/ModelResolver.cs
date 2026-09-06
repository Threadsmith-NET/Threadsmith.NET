namespace Threadsmith.Context;

using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Mutable host-owned hint source used until the extension capability registry is available.</summary>
public sealed class InMemoryModelPreferenceSnapshotProvider : IModelPreferenceSnapshotProvider
{
    private readonly Lock _gate = new();
    private IReadOnlyList<ModelPreferenceHint> _hints = [];

    /// <summary>Replaces the active hint snapshot; the next assembly observes the change.</summary>
    public void Replace(IEnumerable<ModelPreferenceHint> hints)
    {
        ArgumentNullException.ThrowIfNull(hints);
        lock (_gate)
        {
            _hints = hints.ToArray();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ModelPreferenceHint> Snapshot(WorkloadClass workloadClass)
    {
        lock (_gate)
        {
            return _hints
                .Where(hint => hint.WorkloadClass == workloadClass)
                .OrderByDescending(hint => hint.Priority)
                .ThenBy(hint => hint.Source, StringComparer.Ordinal)
                .ToArray();
        }
    }
}

/// <summary>Default host model resolver with inspectable advisory-hint outcomes.</summary>
public sealed class ModelResolver : IModelResolver
{
    private readonly ConfiguredModelCatalog _catalog;
    private readonly IModelPreferenceSnapshotProvider _hints;
    private readonly IModelSelectionPolicy _selectionPolicy;

    /// <summary>Initializes a new instance of the <see cref="ModelResolver"/> class.</summary>
    public ModelResolver(
        ConfiguredModelCatalog catalog,
        IModelPreferenceSnapshotProvider hints)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(hints);
        _catalog = catalog;
        _hints = hints;
        _selectionPolicy = new DefaultModelSelectionPolicy(catalog);
    }

    /// <inheritdoc />
    public int MaximumInputTokenBudget => _catalog.Profiles.Max(
        profile => checked(profile.ContextWindow - profile.EffectiveRequestOutputTokenReserve));

    /// <inheritdoc />
    public ModelResolution Resolve(
        WorkloadClass workloadClass,
        ModelCapabilitySet requiredCapabilities,
        ModelSelectionConstraints constraints,
        ModelProfileId? defaultModelProfileId = null)
    {
        ArgumentNullException.ThrowIfNull(requiredCapabilities);
        ArgumentNullException.ThrowIfNull(constraints);
        var request = new ModelSelectionRequest
        {
            WorkloadClass = workloadClass,
            RequiredCapabilities = requiredCapabilities,
            Constraints = constraints,
            PreferredProfileId = defaultModelProfileId,
        };
        var ignored = new List<ModelHintResolution>();
        var eligible = new List<ModelPreferenceHint>();
        foreach (var hint in _hints.Snapshot(workloadClass))
        {
            if (defaultModelProfileId is not null)
            {
                ignored.Add(new ModelHintResolution(
                    hint.Source,
                    hint.PreferredProfileId,
                    "Ignored because the user or session pinned a default model."));
                continue;
            }

            ModelProfile profile;
            try
            {
                profile = _catalog.Get(hint.PreferredProfileId);
            }
            catch (KeyNotFoundException)
            {
                ignored.Add(new ModelHintResolution(
                    hint.Source,
                    hint.PreferredProfileId,
                    "Ignored because the profile is not in the configured catalog."));
                continue;
            }

            var negotiation = ModelCapabilityNegotiator.Negotiate(profile, request);
            if (!negotiation.IsCompatible)
            {
                ignored.Add(new ModelHintResolution(
                    hint.Source,
                    hint.PreferredProfileId,
                    "Ignored because " + string.Join("; ", negotiation.RejectionReasons) + "."));
                continue;
            }

            eligible.Add(hint);
        }

        var selection = _selectionPolicy.Resolve(request, eligible);
        ModelHintResolution[] applied = [.. eligible
            .Where(hint => hint.PreferredProfileId == selection.ProfileId)
            .Take(1)
            .Select(hint =>
            {
                var reason = string.IsNullOrWhiteSpace(hint.Rationale)
                    ? "Applied as a compatible advisory preference."
                    : hint.Rationale;
                return new ModelHintResolution(hint.Source, hint.PreferredProfileId, reason);
            })];
        ignored.AddRange(eligible
            .Where(hint => hint.PreferredProfileId != selection.ProfileId)
            .Select(hint => new ModelHintResolution(
                hint.Source,
                hint.PreferredProfileId,
                "Ignored because a higher-precedence compatible choice won.")));
        var selectedProfile = _catalog.Get(selection.ProfileId);
        return new ModelResolution(
            selection.ProfileId,
            selectedProfile.ContextWindow,
            selectedProfile.MaximumOutputTokens,
            applied,
            ignored,
            selection.Rationale,
            selectedProfile.EffectiveRequestOutputTokenReserve);
    }
}
