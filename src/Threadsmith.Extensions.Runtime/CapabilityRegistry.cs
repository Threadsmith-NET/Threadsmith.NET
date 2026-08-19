namespace Threadsmith.Extensions.Runtime;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;
using Threadsmith.Extensions.Abstractions;
using Threadsmith.Tools;

/// <summary>Host-owned capability registry keyed by capability id and scoped per extension generation (strategy §17.14).</summary>
public interface ICapabilityRegistry
{
    /// <summary>All active registrations in deterministic order.</summary>
    IReadOnlyList<CapabilityRegistration> Registrations { get; }

    /// <summary>Resolves a capability by id, or returns null when unknown.</summary>
    /// <param name="capabilityId">The host-assigned capability identifier.</param>
    /// <returns>The registration, or null.</returns>
    CapabilityRegistration? Get(string capabilityId);

    /// <summary>Registers a tool capability contributed by the given generation.</summary>
    /// <param name="generation">The owning extension generation.</param>
    /// <param name="capability">The extension tool capability.</param>
    /// <returns>The host-assigned capability registration.</returns>
    CapabilityRegistration RegisterTool(ExtensionGeneration generation, IToolCapability capability);

    /// <summary>Atomically registers every capability contributed by the given generation.</summary>
    /// <param name="generation">The owning extension generation.</param>
    /// <param name="replacedGenerationId">The predecessor generation explicitly authorized for replacement, or null for an independent load.</param>
    void RegisterGeneration(
        ExtensionGeneration generation,
        ExtensionGenerationId? replacedGenerationId = null);

    /// <summary>Registers a model-preference contributor for the given generation.</summary>
    /// <param name="generation">The owning extension generation.</param>
    /// <param name="contributor">The advisory model-preference contributor.</param>
    void RegisterModelPreferenceContributor(ExtensionGeneration generation, IModelPreferenceContributor contributor);

    /// <summary>Removes every capability registered for the given generation (deactivation/unload, plan-16 §9).</summary>
    /// <param name="generationId">The generation whose capabilities should be removed.</param>
    /// <returns>The number of registrations removed.</returns>
    int RemoveGeneration(ExtensionGenerationId generationId);

    /// <summary>The active model-preference contributors across all generations.</summary>
    IReadOnlyList<ModelPreferenceContributorRegistration> ModelPreferenceContributors { get; }
}

/// <summary>One registered capability (§17.14).</summary>
public sealed record CapabilityRegistration
{
    /// <summary>Host-assigned capability identifier.</summary>
    public required CapabilityId CapabilityId { get; init; }

    /// <summary>The owning generation.</summary>
    public required ExtensionGenerationId GenerationId { get; init; }

    /// <summary>The owning extension id.</summary>
    public required ExtensionId ExtensionId { get; init; }

    /// <summary>Capability kind.</summary>
    public required CapabilityKind Kind { get; init; }

    /// <summary>The extension capability contract (held only inside the runtime; never projected across boundaries).</summary>
    public required ICapability Capability { get; init; }

    /// <summary>The host-owned tool proxy when this is a tool capability; otherwise null.</summary>
    public Threadsmith.Tools.ITool? ToolProxy { get; init; }
}

/// <summary>A registered model-preference contributor with its owning generation.</summary>
public sealed record ModelPreferenceContributorRegistration
{
    /// <summary>The owning generation.</summary>
    public required ExtensionGenerationId GenerationId { get; init; }

    /// <summary>The owning extension id.</summary>
    public required ExtensionId ExtensionId { get; init; }

    /// <summary>The contributor (held only inside the runtime).</summary>
    public required IModelPreferenceContributor Contributor { get; init; }
}

/// <summary>Default host-owned capability registry (§17.14).</summary>
public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private readonly ConcurrentDictionary<string, CapabilityRegistration> _capabilities = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ModelPreferenceContributorRegistration> _contributors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ExtensionGenerationId, List<CapabilityRegistration>> _generationRegistrations = [];
    private readonly List<CapabilityRegistration> _registrationOrder = [];
    private readonly ILogger<CapabilityRegistry> _logger;
    private readonly InvocationLeaseAuthority _leaseAuthority;
    private readonly ToolRegistry? _toolRegistry;
    private readonly Lock _gate = new();

    /// <summary>Initializes a new instance of the <see cref="CapabilityRegistry"/> class.</summary>
    /// <param name="leaseAuthority">The lease authority used by tool proxies.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="toolRegistry">The shared tool registry that receives dynamic extension proxies.</param>
    public CapabilityRegistry(
        InvocationLeaseAuthority leaseAuthority,
        ILogger<CapabilityRegistry> logger,
        ToolRegistry? toolRegistry = null)
    {
        ArgumentNullException.ThrowIfNull(leaseAuthority);
        ArgumentNullException.ThrowIfNull(logger);
        _leaseAuthority = leaseAuthority;
        _logger = logger;
        _toolRegistry = toolRegistry;
    }

    /// <inheritdoc />
    public IReadOnlyList<CapabilityRegistration> Registrations
    {
        get
        {
            lock (_gate)
            {
                return [.. _capabilities.Values.OrderBy(c => c.CapabilityId.Value.ToString("N"), StringComparer.Ordinal)];
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ModelPreferenceContributorRegistration> ModelPreferenceContributors
    {
        get
        {
            lock (_gate)
            {
                return [.. _contributors.Values.OrderBy(c => c.GenerationId.Value.ToString("N"), StringComparer.Ordinal)];
            }
        }
    }

    /// <inheritdoc />
    public CapabilityRegistration? Get(string capabilityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capabilityId);
        lock (_gate)
        {
            return _capabilities.TryGetValue(capabilityId, out var registration) ? registration : null;
        }
    }

    /// <inheritdoc />
    public CapabilityRegistration RegisterTool(ExtensionGeneration generation, IToolCapability capability)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(capability);
        var registration = CreateToolRegistration(generation, capability);
        lock (_gate)
        {
            _toolRegistry?.RegisterOrReplace(
                registration.ToolProxy
                    ?? throw new InvalidOperationException("A tool registration did not contain a proxy."),
                new ToolActivitySource(
                    ToolActivitySourceKind.Extension,
                    generation.ExtensionId.Value.ToString("D")));
            _capabilities.AddOrUpdate(capability.Descriptor.Id, registration, (_, _) => registration);
            if (!_generationRegistrations.TryGetValue(
                generation.GenerationId,
                out var generationRegistrations))
            {
                generationRegistrations = [];
                _generationRegistrations[generation.GenerationId] = generationRegistrations;
            }

            generationRegistrations.Add(registration);
            _registrationOrder.Add(registration);
        }

        return registration;
    }

    /// <inheritdoc />
    public void RegisterGeneration(
        ExtensionGeneration generation,
        ExtensionGenerationId? replacedGenerationId = null)
    {
        ArgumentNullException.ThrowIfNull(generation);
        CapabilityRegistration[] registrations = [.. generation.Tools
            .Select(tool => CreateToolRegistration(generation, tool))];
        var capabilityKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in generation.Tools)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tool.Descriptor.Id);
            if (!capabilityKeys.Add(tool.Descriptor.Id))
            {
                throw new ArgumentException(
                    $"Tool capability '{tool.Descriptor.Id}' is registered more than once for this generation.",
                    nameof(generation));
            }
        }

        var contributorKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var contributor in generation.ModelPreferenceContributors)
        {
            ArgumentNullException.ThrowIfNull(contributor);
            ArgumentNullException.ThrowIfNull(contributor.Descriptor);
            ArgumentException.ThrowIfNullOrWhiteSpace(contributor.Descriptor.Id);
            if (!contributorKeys.Add(contributor.Descriptor.Id))
            {
                throw new ArgumentException(
                    $"Model-preference contributor '{contributor.Descriptor.Id}' is registered more than once for this generation.",
                    nameof(generation));
            }
        }

        lock (_gate)
        {
            if (_generationRegistrations.ContainsKey(generation.GenerationId))
            {
                throw new ArgumentException(
                    $"Generation '{generation.GenerationId}' is already registered.",
                    nameof(generation));
            }

            var expectedReplacements = replacedGenerationId is not null
                && _generationRegistrations.TryGetValue(
                    replacedGenerationId.Value,
                    out var replacedRegistrations)
                    ? replacedRegistrations
                        .Where(registration => registration.ToolProxy is not null)
                        .Select(registration => registration.ToolProxy
                            ?? throw new InvalidOperationException("A tool registration did not contain a proxy."))
                    : [];
            _toolRegistry?.RegisterOrReplace(
                registrations.Select(registration => registration.ToolProxy
                    ?? throw new InvalidOperationException("A tool registration did not contain a proxy.")),
                new ToolActivitySource(
                    ToolActivitySourceKind.Extension,
                    generation.ExtensionId.Value.ToString("D")),
                expectedReplacements);
            _generationRegistrations[generation.GenerationId] = [.. registrations];
            _registrationOrder.AddRange(registrations);
            for (int index = 0; index < registrations.Length; index++)
            {
                _capabilities.AddOrUpdate(
                    generation.Tools[index].Descriptor.Id,
                    registrations[index],
                    (_, _) => registrations[index]);
            }

            foreach (var contributor in generation.ModelPreferenceContributors)
            {
                string key = $"{generation.GenerationId.Value:N}:{contributor.Descriptor.Id}";
                _contributors[key] = new ModelPreferenceContributorRegistration
                {
                    GenerationId = generation.GenerationId,
                    ExtensionId = generation.ExtensionId,
                    Contributor = contributor,
                };
            }
        }
    }

    /// <inheritdoc />
    public void RegisterModelPreferenceContributor(ExtensionGeneration generation, IModelPreferenceContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(contributor);
        var registration = new ModelPreferenceContributorRegistration
        {
            GenerationId = generation.GenerationId,
            ExtensionId = generation.ExtensionId,
            Contributor = contributor,
        };
        string key = $"{generation.GenerationId.Value:N}:{contributor.Descriptor.Id}";
        if (!_contributors.TryAdd(key, registration))
        {
            throw new ArgumentException(
                $"Model-preference contributor '{contributor.Descriptor.Id}' is already registered for this generation.",
                nameof(contributor));
        }
    }

    /// <inheritdoc />
    public int RemoveGeneration(ExtensionGenerationId generationId)
    {
        int removed = 0;
        lock (_gate)
        {
            if (_generationRegistrations.Remove(
                generationId,
                out var generationRegistrations))
            {
                _registrationOrder.RemoveAll(registration => registration.GenerationId == generationId);
                foreach (var registration in generationRegistrations)
                {
                    string capabilityId = registration.Capability.Descriptor.Id;
                    if (_capabilities.TryGetValue(capabilityId, out var current)
                        && ReferenceEquals(current, registration))
                    {
                        var prior = _registrationOrder.FindLast(candidate =>
                            string.Equals(
                                candidate.Capability.Descriptor.Id,
                                capabilityId,
                                StringComparison.OrdinalIgnoreCase));
                        if (prior is null)
                        {
                            _capabilities.TryRemove(capabilityId, out _);
                        }
                        else
                        {
                            _capabilities.AddOrUpdate(capabilityId, prior, (_, _) => prior);
                        }
                    }

                    if (registration.ToolProxy is not null)
                    {
                        _toolRegistry?.Remove(registration.ToolProxy.Definition.Id, registration.ToolProxy);
                    }

                    removed++;
                }
            }

            foreach (var entry in _contributors
                .Where(kvp => kvp.Value.GenerationId == generationId).ToArray())
            {
                _contributors.TryRemove(entry.Key, out _);
            }
        }

        return removed;
    }

    private CapabilityRegistration CreateToolRegistration(
        ExtensionGeneration generation,
        IToolCapability capability)
    {
        ArgumentNullException.ThrowIfNull(capability.Descriptor);
        ArgumentNullException.ThrowIfNull(capability.Definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability.Descriptor.Id);
        var capabilityId = CapabilityId.New();
        var proxy = new CapabilityProxy(capability, capabilityId, generation, _leaseAuthority, _logger);
        return new CapabilityRegistration
        {
            CapabilityId = capabilityId,
            GenerationId = generation.GenerationId,
            ExtensionId = generation.ExtensionId,
            Kind = CapabilityKind.Tool,
            Capability = capability,
            ToolProxy = proxy,
        };
    }
}
