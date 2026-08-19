namespace Threadsmith.Extensions.Runtime;

using Microsoft.Extensions.Logging;
using Threadsmith.Extensions.Abstractions;

/// <summary>Host-owned aggregator of active model-preference contributors' hints, keyed by workload class (plan-16 §7, §8.1).
/// The snapshot contains only host-owned DTOs; no extension implementation type is projected.</summary>
public interface IModelPreferenceAggregator
{
    /// <summary>Returns the advisory hints for the given workload name across all active contributors.</summary>
    /// <param name="workloadName">The workload name.</param>
    /// <param name="cancellationToken">A token that cancels the request.</param>
    /// <returns>The aggregated advisory hints.</returns>
    Task<IReadOnlyList<ExtensionModelPreferenceHint>> GetHintsAsync(
        string workloadName,
        CancellationToken cancellationToken = default);

    /// <summary>A host-owned snapshot of which contributors are active and which workloads they hint for.</summary>
    IReadOnlyList<ModelPreferenceContributorSnapshot> Contributors { get; }
}

/// <summary>A host-owned snapshot of one active model-preference contributor (no extension types, §7.1).</summary>
public sealed record ModelPreferenceContributorSnapshot
{
    /// <summary>The contributor capability id.</summary>
    public required string CapabilityId { get; init; }

    /// <summary>The display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>The owning extension id.</summary>
    public required string ExtensionId { get; init; }
}

/// <summary>Default model-preference aggregator backed by the capability registry.</summary>
public sealed class ModelPreferenceAggregator : IModelPreferenceAggregator
{
    private readonly ICapabilityRegistry _registry;
    private readonly ILogger<ModelPreferenceAggregator> _logger;

    /// <summary>Initializes a new instance of the <see cref="ModelPreferenceAggregator"/> class.</summary>
    /// <param name="registry">The capability registry.</param>
    /// <param name="logger">The logger.</param>
    public ModelPreferenceAggregator(ICapabilityRegistry registry, ILogger<ModelPreferenceAggregator> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<ModelPreferenceContributorSnapshot> Contributors => _registry.ModelPreferenceContributors
        .Select(c => new ModelPreferenceContributorSnapshot
        {
            CapabilityId = c.Contributor.Descriptor.Id,
            DisplayName = c.Contributor.Descriptor.DisplayName,
            ExtensionId = c.ExtensionId.Value.ToString(),
        })
        .OrderBy(s => s.CapabilityId, StringComparer.Ordinal)
        .ToArray();

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExtensionModelPreferenceHint>> GetHintsAsync(
        string workloadName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadName);
        var all = new List<ExtensionModelPreferenceHint>();
        foreach (var registration in _registry.ModelPreferenceContributors)
        {
            try
            {
                var hints =
                    await registration.Contributor.GetHintsAsync(workloadName, cancellationToken);
                all.AddRange(hints);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    "Model-preference contributor {ContributorId} failed for workload {Workload}: {Error}",
                    registration.Contributor.Descriptor.Id,
                    workloadName,
                    exception.Message);
            }
        }

        // Aggregate: highest priority first, deterministic by priority then preferred profile name.
        return all
            .OrderByDescending(h => h.Priority)
            .ThenBy(h => h.PreferredProfileName, StringComparer.Ordinal)
            .ToArray();
    }
}