namespace Threadsmith.Models;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;

/// <summary>Provider-neutral configuration shared by every compiled model provider.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
public abstract record ModelProviderConfiguration
{
    /// <summary>Stable provider identifier used for catalog layering.</summary>
    public required string Id { get; init; }

    /// <summary>Human-readable provider name.</summary>
    public required string Name { get; init; }

    /// <summary>Whether this provider contributes selectable models.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Logical secret reference resolved only during provider activation.</summary>
    public string? SecretKeyReference { get; init; }

    /// <summary>Models supplied by this provider.</summary>
    public required IReadOnlyList<ModelConfiguration> Models { get; init; }
}

/// <summary>Provider-neutral model metadata used by host selection and policy.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
public abstract record ModelConfiguration
{
    /// <summary>Stable model profile identifier recorded in execution history.</summary>
    public required ModelProfileId Id { get; init; }

    /// <summary>Human-readable model name.</summary>
    public required string Name { get; init; }

    /// <summary>Whether this model is selectable.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Maximum context window in tokens.</summary>
    public int ContextWindow { get; init; }

    /// <summary>Provider-advertised hard maximum response size in tokens.</summary>
    public int MaximumOutputTokens { get; init; }

    /// <summary>
    /// Optional default output-token reserve requested for one turn. When omitted, the provider maximum is used
    /// to preserve existing catalog behavior.
    /// </summary>
    public int? RequestOutputTokenReserve { get; init; }

    /// <summary>Gets the effective per-request output-token reserve.</summary>
    public int EffectiveRequestOutputTokenReserve => RequestOutputTokenReserve ?? MaximumOutputTokens;

    /// <summary>Provider features available to the host.</summary>
    public ModelCapabilitySet Capabilities { get; init; } = new();

    /// <summary>Pricing metadata used for budget checks.</summary>
    public ModelCostMetadata Cost { get; init; } = new();

    /// <summary>Whether sensitive content may be sent to the model.</summary>
    public ModelSensitiveDataPolicy SensitiveDataPolicy { get; init; }

    /// <summary>Workloads for which the model is intended; empty means any workload.</summary>
    public IReadOnlyList<WorkloadClass> IntendedWorkloadClasses { get; init; } = [];

    /// <summary>Validated default reasoning level.</summary>
    public ReasoningLevel DefaultReasoningLevel { get; init; }

    /// <summary>Supported reasoning levels, always including <see cref="ReasoningLevel.None"/>.</summary>
    public IReadOnlyList<ReasoningLevel> SupportedReasoningLevels { get; init; } = [ReasoningLevel.None];

    /// <summary>Optional sampling temperature.</summary>
    public decimal? Temperature { get; init; }

    /// <summary>Request timeout in seconds.</summary>
    public int TimeoutSeconds { get; init; } = 120;

    /// <summary>Maximum retry attempts, including the initial request.</summary>
    public int RetryMaxAttempts { get; init; } = 3;

    /// <summary>Delay between retryable attempts in milliseconds.</summary>
    public int RetryDelayMilliseconds { get; init; } = 250;
}

/// <summary>Versioned root for a model-provider catalog.</summary>
public sealed record ModelProviderCatalogConfiguration
{
    /// <summary>Current catalog schema version.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Optional stable default provider identifier.</summary>
    public string? DefaultProviderId { get; init; }

    /// <summary>Optional stable default model identifier.</summary>
    public ModelProfileId? DefaultModelId { get; init; }

    /// <summary>Configured compiled providers.</summary>
    public IReadOnlyList<ModelProviderConfiguration> Providers { get; init; } = [];
}

/// <summary>Bounds applied before untrusted provider catalogs are activated.</summary>
public sealed record ModelProviderCatalogLimits
{
    /// <summary>Maximum bytes accepted from one catalog file.</summary>
    public long MaximumFileBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum JSON nesting depth.</summary>
    public int MaximumDepth { get; init; } = 32;

    /// <summary>Maximum provider count.</summary>
    public int MaximumProviders { get; init; } = 32;

    /// <summary>Maximum models beneath one provider.</summary>
    public int MaximumModelsPerProvider { get; init; } = 128;

    /// <summary>Maximum aggregate model count.</summary>
    public int MaximumModels { get; init; } = 512;

    /// <summary>Maximum length of any JSON string or property name.</summary>
    public int MaximumStringLength { get; init; } = 4096;

    /// <summary>Maximum properties accepted in one JSON object.</summary>
    public int MaximumPropertiesPerObject { get; init; } = 128;
}

/// <summary>Ephemeral host-owned inputs supplied to a compiled provider factory.</summary>
public sealed record ModelProviderActivationContext
{
    /// <summary>Shared host-owned HTTP client.</summary>
    public required HttpClient HttpClient { get; init; }

    /// <summary>Selected projected host model profile.</summary>
    public required ModelProfile Profile { get; init; }

    /// <summary>Typed provider configuration from the immutable snapshot.</summary>
    public required ModelProviderConfiguration ProviderConfiguration { get; init; }

    /// <summary>Typed model configuration from the immutable snapshot.</summary>
    public required ModelConfiguration ModelConfiguration { get; init; }

    /// <summary>Just-in-time resolved credential, when configured.</summary>
    public string? ResolvedSecret { get; init; }

    /// <summary>Refreshes a rejected credential when the provider can safely replay the request.</summary>
    public Func<string, CancellationToken, Task<string?>>? RefreshResolvedSecretAsync { get; init; }
}

/// <summary>Explicit compiled registration for one provider discriminator.</summary>
public interface IModelProviderRegistration
{
    /// <summary>Allowlisted JSON discriminator.</summary>
    string TypeDiscriminator { get; }

    /// <summary>Concrete provider configuration record type.</summary>
    Type ProviderConfigurationType { get; }

    /// <summary>Concrete model configuration record type.</summary>
    Type ModelConfigurationType { get; }

    /// <summary>Validates one typed provider and all provider-specific settings.</summary>
    void Validate(ModelProviderConfiguration provider);

    /// <summary>Projects enabled typed models into host-owned selection profiles.</summary>
    IReadOnlyList<ModelProfile> CreateProfiles(ModelProviderConfiguration provider);

    /// <summary>Creates an ephemeral provider adapter for one selected model.</summary>
    IModelProvider CreateProvider(ModelProviderActivationContext context);
}

/// <summary>Immutable allowlist of compiled provider registrations.</summary>
public sealed class ModelProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IModelProviderRegistration> _registrations;

    /// <summary>Initializes a new instance of the <see cref="ModelProviderRegistry"/> class.</summary>
    public ModelProviderRegistry(IEnumerable<IModelProviderRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var configured = new Dictionary<string, IModelProviderRegistration>(StringComparer.OrdinalIgnoreCase);
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            ArgumentException.ThrowIfNullOrWhiteSpace(registration.TypeDiscriminator);
            if (!typeof(ModelProviderConfiguration).IsAssignableFrom(registration.ProviderConfigurationType)
                || registration.ProviderConfigurationType.IsAbstract
                || !typeof(ModelConfiguration).IsAssignableFrom(registration.ModelConfigurationType)
                || registration.ModelConfigurationType.IsAbstract)
            {
                throw new ArgumentException(
                    $"Provider registration '{registration.TypeDiscriminator}' has invalid configuration types.",
                    nameof(registrations));
            }

            if (!configured.TryAdd(registration.TypeDiscriminator, registration))
            {
                throw new ArgumentException(
                    $"Model provider discriminator '{registration.TypeDiscriminator}' is registered more than once.",
                    nameof(registrations));
            }
        }

        _registrations = configured;
    }

    /// <summary>Registered compiled providers keyed by discriminator.</summary>
    public IReadOnlyDictionary<string, IModelProviderRegistration> Registrations => _registrations;

    /// <summary>Gets the registration for an allowlisted discriminator.</summary>
    public IModelProviderRegistration Get(string discriminator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discriminator);
        return _registrations.TryGetValue(discriminator, out var registration)
            ? registration
            : throw new InvalidOperationException($"Unknown model provider type '{discriminator}'.");
    }

    /// <summary>Builds serializer options containing only explicitly registered derived types.</summary>
    public JsonSerializerOptions CreateSerializerOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeInfo.Type == typeof(ModelProviderConfiguration))
            {
                typeInfo.PolymorphismOptions = new JsonPolymorphismOptions
                {
                    TypeDiscriminatorPropertyName = "type",
                    IgnoreUnrecognizedTypeDiscriminators = false,
                    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
                };
                foreach (var registration in _registrations.Values)
                {
                    typeInfo.PolymorphismOptions.DerivedTypes.Add(
                        new JsonDerivedType(registration.ProviderConfigurationType, registration.TypeDiscriminator));
                }
            }
            else if (typeInfo.Type == typeof(ModelConfiguration))
            {
                typeInfo.PolymorphismOptions = new JsonPolymorphismOptions
                {
                    TypeDiscriminatorPropertyName = "type",
                    IgnoreUnrecognizedTypeDiscriminators = false,
                    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization,
                };
                foreach (var registration in _registrations.Values)
                {
                    typeInfo.PolymorphismOptions.DerivedTypes.Add(
                        new JsonDerivedType(registration.ModelConfigurationType, registration.TypeDiscriminator));
                }
            }
        });

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            TypeInfoResolver = resolver,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new ModelProviderConfigurationLoader.ModelProfileIdJsonConverter());
        return options;
    }
}

/// <summary>One immutable association between a selectable profile and its compiled provider.</summary>
public sealed record ConfiguredModelDefinition
{
    /// <summary>Projected host-owned model profile.</summary>
    public required ModelProfile Profile { get; init; }

    /// <summary>Stable provider id.</summary>
    public required string ProviderId { get; init; }

    /// <summary>Compiled provider registration.</summary>
    public required IModelProviderRegistration Registration { get; init; }

    /// <summary>Typed provider configuration.</summary>
    public required ModelProviderConfiguration ProviderConfiguration { get; init; }

    /// <summary>Typed model configuration.</summary>
    public required ModelConfiguration ModelConfiguration { get; init; }
}

/// <summary>Immutable effective provider catalog plus host selection projection.</summary>
public sealed class EffectiveModelProviderCatalog
{
    private readonly IReadOnlyDictionary<ModelProfileId, ConfiguredModelDefinition> _definitions;

    /// <summary>Initializes a new instance of the <see cref="EffectiveModelProviderCatalog"/> class.</summary>
    public EffectiveModelProviderCatalog(
        ModelProviderCatalogConfiguration configuration,
        ModelProviderRegistry registry,
        bool enforceHttps = true)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(registry);
        if (configuration.SchemaVersion != 1)
        {
            throw new InvalidOperationException(
                $"Unsupported model provider catalog schema version '{configuration.SchemaVersion}'.");
        }

        var definitions = new Dictionary<ModelProfileId, ConfiguredModelDefinition>();
        var providerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var provider in configuration.Providers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.Name);
            if (!providerIds.Add(provider.Id))
            {
                throw new InvalidOperationException($"Duplicate model provider id '{provider.Id}'.");
            }

            var discriminator = provider.GetType() == typeof(ModelProviderConfiguration)
                ? throw new InvalidOperationException($"Provider '{provider.Id}' has no registered type.")
                : registry.Registrations.Values
                    .SingleOrDefault(item => item.ProviderConfigurationType == provider.GetType())?.TypeDiscriminator
                    ?? throw new InvalidOperationException($"Provider '{provider.Id}' has an unregistered type.");
            var registration = registry.Get(discriminator);
            registration.Validate(provider);
            if (!provider.Enabled)
            {
                continue;
            }

            var profiles = registration.CreateProfiles(provider);
            var models = provider.Models
                .Where(model => model.Enabled)
                .ToDictionary(model => model.Id);
            foreach (var profile in profiles)
            {
                if (!models.TryGetValue(profile.Id, out var model))
                {
                    throw new InvalidOperationException(
                        $"Provider '{provider.Id}' projected an unknown or disabled model '{profile.Id.Value:D}'.");
                }

                if (!definitions.TryAdd(profile.Id, new ConfiguredModelDefinition
                {
                    Profile = profile,
                    ProviderId = provider.Id,
                    Registration = registration,
                    ProviderConfiguration = provider,
                    ModelConfiguration = model,
                }))
                {
                    throw new InvalidOperationException(
                        $"Model id '{profile.Id.Value:D}' is configured more than once.");
                }
            }
        }

        Configuration = configuration;
        ModelCatalog = new ConfiguredModelCatalog(definitions.Values.Select(item => item.Profile), enforceHttps);
        _definitions = definitions;
        DefaultModelId = configuration.DefaultModelId;
        if (DefaultModelId is { } defaultModelId)
        {
            if (!_definitions.TryGetValue(defaultModelId, out var defaultDefinition))
            {
                throw new InvalidOperationException(
                    $"Default model '{defaultModelId.Value:D}' is missing or disabled.");
            }

            if (configuration.DefaultProviderId is { } defaultProviderId
                && !string.Equals(defaultProviderId, defaultDefinition.ProviderId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The configured default provider and model do not match.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(configuration.DefaultProviderId))
        {
            throw new InvalidOperationException("A default provider requires a default model id.");
        }
    }

    /// <summary>Typed immutable configuration snapshot.</summary>
    public ModelProviderCatalogConfiguration Configuration { get; }

    /// <summary>Existing host-owned selection catalog.</summary>
    public ConfiguredModelCatalog ModelCatalog { get; }

    /// <summary>Configured default model id.</summary>
    public ModelProfileId? DefaultModelId { get; }

    /// <summary>Enabled provider/model bindings in immutable catalog order.</summary>
    public IReadOnlyList<ConfiguredModelDefinition> Definitions => _definitions.Values.ToArray();

    /// <summary>Gets the compiled binding for a selected profile.</summary>
    public ConfiguredModelDefinition Get(ModelProfileId profileId)
    {
        return _definitions.TryGetValue(profileId, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Model profile '{profileId.Value:D}' is not configured.");
    }
}

/// <summary>Secret-free diagnostic emitted while constructing a provider catalog snapshot.</summary>
public sealed record ModelProviderCatalogDiagnostic(
    string Kind,
    string Message);

/// <summary>Bounded shared HTTP transport settings loaded through normal configuration layering.</summary>
public sealed record ModelHttpTransportOptions
{
    /// <summary>Gets the maximum age of a pooled connection before DNS-aware recycling.</summary>
    public TimeSpan PooledConnectionLifetime { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Gets how long an unused pooled connection remains available.</summary>
    public TimeSpan PooledConnectionIdleTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets the maximum duration allowed to establish a transport connection.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets the maximum concurrent connections allowed for one endpoint.</summary>
    public int MaxConnectionsPerServer { get; init; } = 16;

    /// <summary>Loads and validates transport settings from the effective layered configuration.</summary>
    public static ModelHttpTransportOptions Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var pooledLifetimeSeconds = ReadBounded(
            configuration,
            "model:http:pooledConnectionLifetimeSeconds",
            900,
            60,
            86400);
        var pooledIdleSeconds = ReadBounded(
            configuration,
            "model:http:pooledConnectionIdleTimeoutSeconds",
            120,
            10,
            3600);
        var connectTimeoutSeconds = ReadBounded(
            configuration,
            "model:http:connectTimeoutSeconds",
            30,
            1,
            300);
        var maxConnectionsPerServer = ReadBounded(
            configuration,
            "model:http:maxConnectionsPerServer",
            16,
            1,
            1024);
        return new ModelHttpTransportOptions
        {
            PooledConnectionLifetime = TimeSpan.FromSeconds(pooledLifetimeSeconds),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(pooledIdleSeconds),
            ConnectTimeout = TimeSpan.FromSeconds(connectTimeoutSeconds),
            MaxConnectionsPerServer = maxConnectionsPerServer,
        };
    }

    private static int ReadBounded(
        IConfiguration configuration,
        string key,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var value = configuration.GetValue(key, defaultValue);
        if (value < minimum || value > maximum)
        {
            throw new InvalidOperationException(
                $"Configuration '{key}' must be between {minimum} and {maximum}.");
        }

        return value;
    }
}

/// <summary>Loads and deterministically merges bounded user and repository provider catalogs.</summary>
public static class ModelProviderConfigurationLoader
{
    private static readonly HashSet<string> CredentialPropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "apiKey",
        "authorization",
        "bearerToken",
        "credential",
        "credentials",
        "password",
        "secret",
        "token",
    };

    /// <summary>Loads optional normalized catalog paths into one immutable effective catalog.</summary>
    public static EffectiveModelProviderCatalog Load(
        string userCatalogPath,
        string repositoryCatalogPath,
        ModelProviderRegistry registry,
        ModelProviderCatalogLimits? limits = null,
        bool enforceHttps = true,
        Action<ModelProviderCatalogDiagnostic>? observeDiagnostic = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userCatalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryCatalogPath);
        ArgumentNullException.ThrowIfNull(registry);
        if (!Path.IsPathFullyQualified(userCatalogPath)
            || !Path.IsPathFullyQualified(repositoryCatalogPath))
        {
            throw new ArgumentException("Model provider catalog paths must be normalized absolute paths.");
        }

        limits ??= new ModelProviderCatalogLimits();
        var user = File.Exists(userCatalogPath) ? ParseLayer(userCatalogPath, limits, "user") : null;
        var repository = File.Exists(repositoryCatalogPath)
            ? ParseLayer(repositoryCatalogPath, limits, "repository")
            : null;
        observeDiagnostic?.Invoke(new ModelProviderCatalogDiagnostic(
            "layers",
            $"Provider catalog layers discovered: user={user is not null}, repository={repository is not null}."));
        var effective = user is null
            ? repository?.DeepClone().AsObject() ?? new JsonObject
            {
                ["schemaVersion"] = 1,
                ["providers"] = new JsonArray(),
            }
            : repository is null
                ? user.DeepClone().AsObject()
                : MergeObjects(user, repository, "$", limits);
        ValidateTree(effective, limits, "effective");

        try
        {
            var configuration = effective.Deserialize<ModelProviderCatalogConfiguration>(
                    registry.CreateSerializerOptions())
                ?? throw new InvalidOperationException("The effective model provider catalog is empty.");
            var catalog = new EffectiveModelProviderCatalog(configuration, registry, enforceHttps);
            var disabledProviders = configuration.Providers.Count(provider => !provider.Enabled);
            var disabledModels = configuration.Providers.Sum(provider => provider.Models.Count(model => !model.Enabled));
            var diagnosticMessage = $"Effective provider catalog contains {configuration.Providers.Count} providers, "
                + $"{catalog.ModelCatalog.Profiles.Count} selectable models, {disabledProviders} disabled providers, "
                + $"and {disabledModels} disabled models; default provider='{configuration.DefaultProviderId ?? "none"}', "
                + $"default model='{configuration.DefaultModelId?.Value.ToString("D") ?? "none"}'.";
            observeDiagnostic?.Invoke(new ModelProviderCatalogDiagnostic("effective", diagnosticMessage));
            return catalog;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"The effective model provider catalog is invalid at '{exception.Path ?? "$"}'.",
                exception);
        }
    }

    private static JsonObject ParseLayer(
        string path,
        ModelProviderCatalogLimits limits,
        string layerName)
    {
        var file = new FileInfo(path);
        if (file.Length > limits.MaximumFileBytes)
        {
            throw new InvalidOperationException(
                $"The {layerName} model provider catalog exceeds the configured byte limit.");
        }

        var bytes = File.ReadAllBytes(path);
        try
        {
            var node = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions
            {
                MaxDepth = limits.MaximumDepth,
            });
            var root = node as JsonObject
                ?? throw new InvalidOperationException($"The {layerName} model provider catalog root must be an object.");
            ValidateTree(root, limits, layerName);
            return root;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                $"The {layerName} model provider catalog contains invalid JSON.",
                exception);
        }
    }

    private static void ValidateTree(JsonObject root, ModelProviderCatalogLimits limits, string layerName)
    {
        var pending = new Stack<(JsonNode Node, int Depth, string Path)>();
        pending.Push((root, 1, "$"));
        while (pending.Count > 0)
        {
            (var node, var depth, var path) = pending.Pop();
            if (depth > limits.MaximumDepth)
            {
                throw new InvalidOperationException($"The {layerName} catalog exceeds the nesting limit at '{path}'.");
            }

            if (node is JsonObject item)
            {
                if (item.Count > limits.MaximumPropertiesPerObject)
                {
                    throw new InvalidOperationException($"The {layerName} catalog has too many settings at '{path}'.");
                }

                var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach ((var name, var value) in item)
                {
                    if (!propertyNames.Add(name))
                    {
                        throw new InvalidOperationException(
                            $"The {layerName} catalog contains duplicate property '{name}' at '{path}'.");
                    }

                    if (name.Length > limits.MaximumStringLength)
                    {
                        throw new InvalidOperationException($"The {layerName} catalog contains an excessive property name.");
                    }

                    var isSecretReference = string.Equals(
                        name,
                        "secretKeyReference",
                        StringComparison.OrdinalIgnoreCase);
                    var isCredentialName = CredentialPropertyNames.Contains(name)
                        || name.EndsWith("ApiKey", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith("Authorization", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith("Credential", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith("Password", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith("Secret", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith("Token", StringComparison.OrdinalIgnoreCase);
                    if (!isSecretReference && isCredentialName)
                    {
                        throw new InvalidOperationException(
                            $"Inline credential setting '{name}' is not permitted in the {layerName} catalog.");
                    }

                    if (value is not null)
                    {
                        pending.Push((value, depth + 1, path + "." + name));
                    }
                }
            }
            else if (node is JsonArray array)
            {
                for (var index = 0; index < array.Count; index++)
                {
                    if (array[index] is { } child)
                    {
                        pending.Push((child, depth + 1, $"{path}[{index}]"));
                    }
                }
            }
            else if (node is JsonValue value
                && value.TryGetValue<string>(out var text)
                && text is not null
                && text.Length > limits.MaximumStringLength)
            {
                throw new InvalidOperationException($"The {layerName} catalog contains an excessive string at '{path}'.");
            }
        }

        var providers = GetArray(root, "providers") ?? [];
        if (providers.Count > limits.MaximumProviders)
        {
            throw new InvalidOperationException($"The {layerName} catalog exceeds the provider-count limit.");
        }

        var providerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelIds = new HashSet<Guid>();
        var modelCount = 0;
        foreach (var providerNode in providers)
        {
            var provider = providerNode as JsonObject
                ?? throw new InvalidOperationException($"The {layerName} catalog contains a non-object provider.");
            var providerId = GetRequiredString(provider, "id", layerName + " provider");
            if (!providerIds.Add(providerId))
            {
                throw new InvalidOperationException($"Duplicate provider id '{providerId}' in the {layerName} catalog.");
            }

            var models = GetArray(provider, "models") ?? [];
            if (models.Count > limits.MaximumModelsPerProvider)
            {
                throw new InvalidOperationException(
                    $"Provider '{providerId}' exceeds the models-per-provider limit.");
            }

            modelCount += models.Count;
            foreach (var modelNode in models)
            {
                var model = modelNode as JsonObject
                    ?? throw new InvalidOperationException(
                        $"Provider '{providerId}' contains a non-object model in the {layerName} catalog.");
                var modelIdText = GetRequiredString(model, "id", layerName + " model");
                if (!Guid.TryParse(modelIdText, out var modelId))
                {
                    throw new InvalidOperationException(
                        $"Model id '{modelIdText}' in the {layerName} catalog is not a GUID.");
                }

                if (!modelIds.Add(modelId))
                {
                    throw new InvalidOperationException(
                        $"Duplicate model id '{modelId:D}' in the {layerName} catalog.");
                }
            }
        }

        if (modelCount > limits.MaximumModels)
        {
            throw new InvalidOperationException($"The {layerName} catalog exceeds the aggregate model-count limit.");
        }
    }

    private static JsonObject MergeObjects(
        JsonObject inherited,
        JsonObject overrides,
        string path,
        ModelProviderCatalogLimits limits)
    {
        var result = inherited.DeepClone().AsObject();
        foreach ((var overrideName, var overrideValue) in overrides)
        {
            var existing = result
                .FirstOrDefault(item => string.Equals(item.Key, overrideName, StringComparison.OrdinalIgnoreCase));
            var resultName = existing.Key ?? overrideName;
            var inheritedValue = existing.Key is null ? null : existing.Value;
            if (overrideValue is JsonObject overrideObject && inheritedValue is JsonObject inheritedObject)
            {
                result[resultName] = MergeObjects(inheritedObject, overrideObject, path + "." + overrideName, limits);
            }
            else if (overrideValue is JsonArray overrideArray
                && inheritedValue is JsonArray inheritedArray
                && (string.Equals(overrideName, "providers", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(overrideName, "models", StringComparison.OrdinalIgnoreCase)))
            {
                result[resultName] = MergeKeyedArray(
                    inheritedArray,
                    overrideArray,
                    path + "." + overrideName,
                    limits);
            }
            else
            {
                result[resultName] = overrideValue?.DeepClone();
            }
        }

        return result;
    }

    private static JsonArray MergeKeyedArray(
        JsonArray inherited,
        JsonArray overrides,
        string path,
        ModelProviderCatalogLimits limits)
    {
        var result = new JsonArray([.. inherited.Select(item => item?.DeepClone())]);
        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < result.Count; index++)
        {
            var item = result[index] as JsonObject
                ?? throw new InvalidOperationException($"Catalog entry at '{path}[{index}]' must be an object.");
            positions.Add(GetRequiredString(item, "id", path), index);
        }

        foreach (var overrideNode in overrides)
        {
            var overrideItem = overrideNode as JsonObject
                ?? throw new InvalidOperationException($"Catalog override at '{path}' must be an object.");
            var id = GetRequiredString(overrideItem, "id", path);
            if (!positions.TryGetValue(id, out var position))
            {
                GetRequiredString(overrideItem, "type", path + "['" + id + "']");
                positions.Add(id, result.Count);
                result.Add(overrideItem.DeepClone());
                continue;
            }

            var inheritedItem = result[position]?.AsObject()
                ?? throw new InvalidOperationException($"Inherited catalog entry '{id}' is invalid.");
            var inheritedType = GetRequiredString(inheritedItem, "type", path + "['" + id + "']");
            var overrideType = GetString(overrideItem, "type");
            if (overrideType is not null
                && !string.Equals(inheritedType, overrideType, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Catalog override for '{id}' cannot change type '{inheritedType}' to '{overrideType}'.");
            }

            if (path.EndsWith(".models", StringComparison.OrdinalIgnoreCase)
                && TryGetPropertyValue(overrideItem, "reasoningCompatibility", out var overrideReasoningNode))
            {
                _ = TryGetPropertyValue(
                    inheritedItem,
                    "reasoningCompatibility",
                    out var inheritedReasoningNode);
                var inheritedReasoning = inheritedReasoningNode as JsonObject;
                var overrideReasoning = overrideReasoningNode as JsonObject;
                if ((inheritedReasoning is null) != (overrideReasoning is null))
                {
                    throw new InvalidOperationException(
                        $"Repository catalog cannot add or remove reasoning compatibility for inherited model '{id}'.");
                }

                if (inheritedReasoning is null || overrideReasoning is null)
                {
                    result[position] = MergeObjects(inheritedItem, overrideItem, path + "['" + id + "']", limits);
                    continue;
                }

                var inheritedVersion = GetInt32(inheritedReasoning, "schemaVersion");
                var overrideVersion = GetInt32(overrideReasoning, "schemaVersion");
                var inheritedMode = GetString(inheritedReasoning, "mode");
                var overrideMode = GetString(overrideReasoning, "mode");
                if ((overrideVersion is not null && overrideVersion != inheritedVersion)
                    || (overrideMode is not null
                        && !string.Equals(inheritedMode, overrideMode, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"Repository catalog cannot change reasoning compatibility mode or version for inherited model '{id}'.");
                }
            }

            if (string.Equals(path, "$.providers", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(GetString(inheritedItem, "secretKeyReference")))
            {
                foreach ((var name, var value) in overrideItem)
                {
                    var isRepositoryMutableProperty = string.Equals(name, "id", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "type", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "name", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "enabled", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "models", StringComparison.OrdinalIgnoreCase);
                    var inheritedValue = inheritedItem
                        .FirstOrDefault(property => string.Equals(
                            property.Key,
                            name,
                            StringComparison.OrdinalIgnoreCase))
                        .Value;
                    if (!isRepositoryMutableProperty && !JsonNode.DeepEquals(inheritedValue, value))
                    {
                        throw new InvalidOperationException(
                            $"Repository catalog cannot override provider connection or authentication setting "
                            + $"'{name}' for inherited credentialed provider '{id}'.");
                    }
                }
            }

            result[position] = MergeObjects(inheritedItem, overrideItem, path + "['" + id + "']", limits);
        }

        return result;
    }

    private static bool TryGetPropertyValue(JsonObject item, string name, out JsonNode? value)
    {
        foreach ((var propertyName, var propertyValue) in item)
        {
            if (string.Equals(propertyName, name, StringComparison.OrdinalIgnoreCase))
            {
                value = propertyValue;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static int? GetInt32(JsonObject item, string name)
    {
        var node = item
            .FirstOrDefault(property => string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            .Value;
        return node is JsonValue value && value.TryGetValue<int>(out var number)
            ? number
            : null;
    }

    private static JsonArray? GetArray(JsonObject item, string name)
    {
        var node = item
            .FirstOrDefault(property => string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            .Value;
        return node as JsonArray;
    }

    private static string GetRequiredString(JsonObject item, string name, string location)
    {
        return GetString(item, name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Catalog entry at '{location}' is missing required '{name}'.");
    }

    private static string? GetString(JsonObject item, string name)
    {
        var node = item
            .FirstOrDefault(property => string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase))
            .Value;
        return node is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : null;
    }

    /// <summary>Serializes stable model identifiers as GUID strings.</summary>
    internal sealed class ModelProfileIdJsonConverter : JsonConverter<ModelProfileId>
    {
        /// <inheritdoc />
        public override ModelProfileId Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var value = reader.GetString();
            return Guid.TryParse(value, out var id)
                ? new ModelProfileId(id)
                : throw new JsonException("Model id must be a GUID.");
        }

        /// <inheritdoc />
        public override void Write(
            Utf8JsonWriter writer,
            ModelProfileId value,
            JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.Value);
        }
    }
}
