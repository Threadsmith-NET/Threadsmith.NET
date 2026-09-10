namespace Threadsmith.Models;

/// <summary>Canonical final-response schema for a host workflow that requires JSON.</summary>
public sealed record ModelResponseFormat
{
    /// <summary>Contract version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Stable host schema identity.</summary>
    public required string SchemaId { get; init; }

    /// <summary>Bounded canonical object-schema JSON.</summary>
    public required string JsonSchema { get; init; }
}

/// <summary>Credential-free inputs to compiled provider preparation.</summary>
public sealed record ModelRequestPreparationContext
{
    /// <summary>Canonical request to project.</summary>
    public required ModelStreamRequest Request { get; init; }

    /// <summary>Resolved immutable profile.</summary>
    public required ModelProfile Profile { get; init; }

    /// <summary>Trusted effective provider descriptor.</summary>
    public required ModelProviderConfiguration ProviderConfiguration { get; init; }

    /// <summary>Resolved effective model descriptor.</summary>
    public required ModelConfiguration ModelConfiguration { get; init; }
}

/// <summary>Safe deterministic preparation metadata, without wire or replay payloads.</summary>
public sealed record ModelRequestPreparationResult
{
    /// <summary>Whether subsequent correction guidance must use chronological messages instead of new system/developer instructions.</summary>
    public bool RequiresInitialInstructionPrefix { get; init; }

    /// <summary>Provider-aware capacity estimate including framing and retained replay.</summary>
    public required ModelWireEstimate WireEstimate { get; init; }

    /// <summary>Digest of the exact projected request shape checked again before submission.</summary>
    public required string WireDigest { get; init; }

    /// <summary>Effective provider caching capabilities.</summary>
    public ModelCacheCapabilities CacheCapabilities { get; init; } = new();

    /// <summary>Validated explicit cache boundaries.</summary>
    public ModelCachePlan? CachePlan { get; init; }
}

/// <summary>Optional compiled registration boundary for deterministic request preparation.</summary>
public interface IModelRequestPreparation
{
    /// <summary>Projects and validates a request without resolving credentials or performing I/O.</summary>
    ModelRequestPreparationResult Prepare(ModelRequestPreparationContext context);
}

/// <summary>Resolves preparation through the same immutable catalog as model dispatch.</summary>
public interface IModelRequestPreparationResolver
{
    /// <summary>Returns a prepared request, preserving existing providers' neutral behavior.</summary>
    ModelStreamRequest Prepare(ModelStreamRequest request);
}

/// <summary>Shared preparation entry points for host model loops.</summary>
public static class ModelRequestPreparation
{
    /// <summary>Prepares a request through its dispatcher when supported.</summary>
    public static ModelStreamRequest Prepare(IModelProvider provider, ModelStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(request);
        return provider is IModelRequestPreparationResolver resolver ? resolver.Prepare(request) : request;
    }

    /// <summary>Applies a compiled registration's deterministic metadata to its canonical request.</summary>
    public static ModelStreamRequest Apply(ConfiguredModelDefinition definition, ModelStreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(request);
        if (definition.Registration is not IModelRequestPreparation preparer)
        {
            return request;
        }

        var result = preparer.Prepare(new ModelRequestPreparationContext
        {
            Request = request,
            Profile = definition.Profile,
            ProviderConfiguration = definition.ProviderConfiguration,
            ModelConfiguration = definition.ModelConfiguration,
        });
        ArgumentException.ThrowIfNullOrWhiteSpace(result.WireDigest);
        ArgumentOutOfRangeException.ThrowIfNegative(result.WireEstimate.WireInputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(result.WireEstimate.OutputReserveTokens);
        return request with
        {
            Preparation = result,
            WireEstimate = result.WireEstimate,
            CacheCapabilities = result.CacheCapabilities,
            CachePlan = result.CachePlan,
        };
    }
}
