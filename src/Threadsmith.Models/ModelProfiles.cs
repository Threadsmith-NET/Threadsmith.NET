namespace Threadsmith.Models;

using Microsoft.Extensions.Configuration;
using Threadsmith.Core;

/// <summary>Workload categories used by host-owned model-selection policy.</summary>
public enum WorkloadClass
{
    /// <summary>General work without a more specific classification.</summary>
    General,

    /// <summary>Task analysis and change planning.</summary>
    Planning,

    /// <summary>Source-code mutation work.</summary>
    CodeEdit,

    /// <summary>Review and verification work.</summary>
    Review,

    /// <summary>Summarization and context compaction.</summary>
    Summary,
}

/// <summary>Controls whether a model endpoint may receive sensitive content.</summary>
public enum ModelSensitiveDataPolicy
{
    /// <summary>Sensitive content must not be sent to the model.</summary>
    Prohibited,

    /// <summary>Sensitive content may be sent when host policy also permits it.</summary>
    Allowed,
}

/// <summary>Capabilities advertised by a configured model profile.</summary>
public sealed record ModelCapabilitySet
{
    /// <summary>Whether streaming responses are supported.</summary>
    public bool Streaming { get; init; }

    /// <summary>Whether typed tool calls are supported.</summary>
    public bool ToolCalls { get; init; }

    /// <summary>Whether schema-constrained structured output is supported.</summary>
    public bool StructuredOutput { get; init; }
}

/// <summary>Per-million-token prices used for conservative cost accounting.</summary>
public sealed record ModelCostMetadata
{
    /// <summary>Cost per million input tokens.</summary>
    public decimal InputPerMillionTokens { get; init; }

    /// <summary>Cost per million output tokens.</summary>
    public decimal OutputPerMillionTokens { get; init; }

    /// <summary>Calculates cost for the supplied usage.</summary>
    public decimal Calculate(long inputTokens, long outputTokens)
    {
        if (inputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputTokens));
        }

        if (outputTokens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outputTokens));
        }

        return ((inputTokens * InputPerMillionTokens)
            + (outputTokens * OutputPerMillionTokens)) / 1_000_000m;
    }
}

/// <summary>Bounded retry settings for a model endpoint.</summary>
public sealed record ModelRetryPolicy
{
    /// <summary>Total attempts, including the initial request.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Delay between retryable attempts.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>A host-configured model endpoint and its policy metadata.</summary>
public sealed record ModelProfile
{
    /// <summary>Stable profile identifier recorded in execution history.</summary>
    public required ModelProfileId Id { get; init; }

    /// <summary>Human-readable configuration name.</summary>
    public required string Name { get; init; }

    /// <summary>Provider family, such as <c>openai-compatible</c>.</summary>
    public required string Provider { get; init; }

    /// <summary>Full chat-completions endpoint.</summary>
    public required Uri Endpoint { get; init; }

    /// <summary>Provider model identifier.</summary>
    public required string ModelId { get; init; }

    /// <summary>Logical secret reference resolved outside repository configuration.</summary>
    public string? SecretKeyReference { get; init; }

    /// <summary>Maximum context window in tokens.</summary>
    public int ContextWindow { get; init; }

    /// <summary>Provider-advertised hard maximum response size in tokens.</summary>
    public int MaximumOutputTokens { get; init; }

    /// <summary>Optional default output-token reserve requested for one turn.</summary>
    public int? RequestOutputTokenReserve { get; init; }

    /// <summary>Gets the effective per-request output-token reserve.</summary>
    public int EffectiveRequestOutputTokenReserve => RequestOutputTokenReserve ?? MaximumOutputTokens;

    /// <summary>Provider features available to the host.</summary>
    public ModelCapabilitySet Capabilities { get; init; } = new();

    /// <summary>Pricing metadata used for budget checks.</summary>
    public ModelCostMetadata Cost { get; init; } = new();

    /// <summary>Whether sensitive content may be sent to this endpoint.</summary>
    public ModelSensitiveDataPolicy SensitiveDataPolicy { get; init; }

    /// <summary>Workloads for which the profile is intended; empty means any workload.</summary>
    public IReadOnlyList<WorkloadClass> IntendedWorkloadClasses { get; init; } = [];

    /// <summary>Optional provider-specific reasoning effort as configured.</summary>
    public string? ReasoningEffort { get; init; }

    /// <summary>Validated default reasoning level for this profile.</summary>
    public ReasoningLevel DefaultReasoningLevel { get; init; } = ReasoningLevel.None;

    /// <summary>
    /// Reasoning levels this profile supports; defaults to only <see cref="ReasoningLevel.None"/>.
    /// Always includes <see cref="ReasoningLevel.None"/>.
    /// </summary>
    public IReadOnlyList<ReasoningLevel> SupportedReasoningLevels { get; init; } = [ReasoningLevel.None];

    /// <summary>Effective provider-neutral reasoning behavior.</summary>
    public EffectiveReasoningCapability ReasoningCapability { get; init; } = new();

    /// <summary>Returns <see langword="true"/> when the profile supports the given reasoning level.</summary>
    public bool SupportsReasoningLevel(ReasoningLevel level)
    {
        return SupportedReasoningLevels.Contains(level);
    }

    /// <summary>Optional sampling temperature.</summary>
    public decimal? Temperature { get; init; }

    /// <summary>Per-request timeout.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Bounded transient retry policy.</summary>
    public ModelRetryPolicy RetryPolicy { get; init; } = new();
}

/// <summary>Hard constraints applied during model selection.</summary>
public sealed record ModelSelectionConstraints
{
    /// <summary>Minimum acceptable context window.</summary>
    public int MinimumContextWindow { get; init; }

    /// <summary>Maximum combined input and output price per million tokens.</summary>
    public decimal? MaximumCombinedCostPerMillionTokens { get; init; }

    /// <summary>Whether the request contains sensitive content.</summary>
    public bool ContainsSensitiveData { get; init; }
}

/// <summary>A host selection request for one configured model.</summary>
public sealed record ModelSelectionRequest
{
    /// <summary>Workload the model will perform.</summary>
    public WorkloadClass WorkloadClass { get; init; }

    /// <summary>Capabilities required before the run may start.</summary>
    public ModelCapabilitySet RequiredCapabilities { get; init; } = new();

    /// <summary>Hard host and session constraints.</summary>
    public ModelSelectionConstraints Constraints { get; init; } = new();

    /// <summary>Optional user or session preference, constrained to the configured catalog.</summary>
    public ModelProfileId? PreferredProfileId { get; init; }
}

/// <summary>An advisory preference over the host's configured model catalog.</summary>
public sealed record ModelPreferenceHint
{
    /// <summary>Workload for which the hint applies.</summary>
    public WorkloadClass WorkloadClass { get; init; }

    /// <summary>Preferred configured profile.</summary>
    public required ModelProfileId PreferredProfileId { get; init; }

    /// <summary>Host-owned identifier for the hint source.</summary>
    public required string Source { get; init; }

    /// <summary>Advisory priority; higher values are considered first.</summary>
    public int Priority { get; init; }

    /// <summary>Human-readable reason supplied by the contributor.</summary>
    public string Rationale { get; init; } = string.Empty;
}

/// <summary>Outcome of capability negotiation against one profile.</summary>
public sealed record ModelCapabilityNegotiationResult(
    bool IsCompatible,
    IReadOnlyList<string> RejectionReasons);

/// <summary>Selected configured profile and an inspectable policy rationale.</summary>
public sealed record ModelSelectionResult(
    ModelProfileId ProfileId,
    IReadOnlyList<string> Rationale);

/// <summary>Resolves a configured profile under host-owned policy.</summary>
public interface IModelSelectionPolicy
{
    /// <summary>Selects a compatible profile or throws when none is available.</summary>
    ModelSelectionResult Resolve(
        ModelSelectionRequest request,
        IReadOnlyList<ModelPreferenceHint>? hints = null);
}

/// <summary>An immutable catalog containing every model the host may select.</summary>
public sealed class ConfiguredModelCatalog
{
    private readonly IReadOnlyList<ModelProfile> _profiles;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfiguredModelCatalog"/> class.
    /// </summary>
    /// <param name="profiles">The model profiles available for host selection.</param>
    /// <param name="enforceHttps">
    /// When <see langword="true"/> (default), HTTP endpoints are rejected unless they
    /// are loopback. When <see langword="false"/>, any HTTP endpoint is permitted
    /// (for trusted local-network providers).
    /// </param>
    public ConfiguredModelCatalog(IEnumerable<ModelProfile> profiles, bool enforceHttps = true)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ModelProfile[] configured = [.. profiles.Select(DeriveDefaultReasoningCapability)];
        foreach (var profile in configured)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(profile.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(profile.Provider);
            ArgumentException.ThrowIfNullOrWhiteSpace(profile.ModelId);
            var isPermittedEndpoint = string.Equals(
                    profile.Endpoint.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase)
                || (string.Equals(
                        profile.Endpoint.Scheme,
                        Uri.UriSchemeHttp,
                        StringComparison.OrdinalIgnoreCase)
                    && (!enforceHttps || profile.Endpoint.IsLoopback));
            if (profile.Id == default
                || !profile.Endpoint.IsAbsoluteUri
                || !isPermittedEndpoint
                || (profile.SecretKeyReference is { } secretKeyReference
                    && !secretKeyReference.StartsWith("secrets:", StringComparison.OrdinalIgnoreCase))
                || profile.ContextWindow <= 0
                || profile.MaximumOutputTokens <= 0
                || profile.MaximumOutputTokens > profile.ContextWindow
                || profile.EffectiveRequestOutputTokenReserve <= 0
                || profile.EffectiveRequestOutputTokenReserve >= profile.ContextWindow
                || profile.EffectiveRequestOutputTokenReserve > profile.MaximumOutputTokens
                || profile.Cost.InputPerMillionTokens < 0
                || profile.Cost.OutputPerMillionTokens < 0
                || profile.Timeout <= TimeSpan.Zero
                || profile.RetryPolicy.MaxAttempts <= 0
                || profile.RetryPolicy.Delay < TimeSpan.Zero
                || profile.SupportedReasoningLevels.Count == 0
                || profile.SupportedReasoningLevels.Any(level => !Enum.IsDefined(level))
                || profile.SupportedReasoningLevels.Distinct().Count()
                    != profile.SupportedReasoningLevels.Count
                || !profile.SupportedReasoningLevels.Contains(ReasoningLevel.None)
                || !Enum.IsDefined(profile.DefaultReasoningLevel)
                || !profile.SupportedReasoningLevels.Contains(profile.DefaultReasoningLevel))
            {
                throw new ArgumentException(
                    $"Model profile '{profile.Name}' contains invalid limits or identifiers.",
                    nameof(profiles));
            }
        }

        if (configured.Select(profile => profile.Id).Distinct().Count() != configured.Length
            || configured.Select(profile => profile.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                != configured.Length)
        {
            throw new ArgumentException("Configured model profile ids and names must be unique.", nameof(profiles));
        }

        _profiles = configured;
    }

    /// <summary>Profiles available for host selection.</summary>
    public IReadOnlyList<ModelProfile> Profiles => _profiles;

    /// <summary>Gets a profile by stable id.</summary>
    public ModelProfile Get(ModelProfileId profileId)
    {
        return _profiles.FirstOrDefault(profile => profile.Id == profileId)
                ?? throw new KeyNotFoundException($"Model profile '{profileId.Value:D}' is not configured.");
    }

    private static ModelProfile DeriveDefaultReasoningCapability(ModelProfile profile)
    {
        var capability = profile.ReasoningCapability;
        if (capability.SchemaVersion != 0
            || !string.Equals(capability.RequestMode, "legacy-standard", StringComparison.Ordinal)
            || !string.Equals(capability.Provenance, "configured model profile", StringComparison.Ordinal))
        {
            return profile;
        }

        var selectable = profile.SupportedReasoningLevels.Count > 1;
        return profile with
        {
            ReasoningCapability = capability with
            {
                Controllability = selectable
                    ? ReasoningControllability.Selectable
                    : ReasoningControllability.Unsupported,
                SupportedLevels = profile.SupportedReasoningLevels,
                DefaultLevel = selectable ? profile.DefaultReasoningLevel : null,
            },
        };
    }
}

/// <summary>Selects and creates a configured provider for each model request.</summary>
public sealed class ConfiguredModelProvider : IModelProvider
{
    private readonly ConfiguredModelCatalog _catalog;
    private readonly EffectiveModelProviderCatalog _effectiveCatalog;
    private readonly HttpClient _httpClient;
    private readonly ModelProfileId? _preferredProfileId;
    private readonly Func<ModelProfileId?>? _preferredProfileResolver;
    private readonly Func<string, string, CancellationToken, Task<string?>>? _refreshSecretAsync;
    private readonly Func<string, CancellationToken, Task<string?>> _resolveSecretAsync;
    private readonly IModelSelectionPolicy _selectionPolicy;

    /// <summary>Initializes a new instance of the <see cref="ConfiguredModelProvider"/> class for compiled-provider dispatch.</summary>
    public ConfiguredModelProvider(
        HttpClient httpClient,
        EffectiveModelProviderCatalog catalog,
        Func<string, CancellationToken, Task<string?>> resolveSecretAsync,
        ModelProfileId? preferredProfileId = null,
        Func<string, string, CancellationToken, Task<string?>>? refreshSecretAsync = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(resolveSecretAsync);
        _httpClient = httpClient;
        _effectiveCatalog = catalog;
        _catalog = catalog.ModelCatalog;
        _resolveSecretAsync = resolveSecretAsync;
        _refreshSecretAsync = refreshSecretAsync;
        _preferredProfileId = preferredProfileId ?? catalog.DefaultModelId;
        _selectionPolicy = new DefaultModelSelectionPolicy(_catalog);
    }

    /// <summary>Initializes a new instance of the <see cref="ConfiguredModelProvider"/> class with a request-bound active-profile resolver.</summary>
    public ConfiguredModelProvider(
        HttpClient httpClient,
        EffectiveModelProviderCatalog catalog,
        Func<string, CancellationToken, Task<string?>> resolveSecretAsync,
        Func<ModelProfileId?> preferredProfileResolver,
        Func<string, string, CancellationToken, Task<string?>>? refreshSecretAsync = null)
        : this(httpClient, catalog, resolveSecretAsync, refreshSecretAsync: refreshSecretAsync)
    {
        ArgumentNullException.ThrowIfNull(preferredProfileResolver);
        _preferredProfileResolver = preferredProfileResolver;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ModelChunk> StreamAsync(
        ModelStreamRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requiredCapabilities = request.RequiredCapabilities with { Streaming = true };
        var selectionConstraints = request.SelectionConstraints with
        {
            ContainsSensitiveData = request.SelectionConstraints.ContainsSensitiveData
                || request.ContainsSensitiveData,
        };
        var selection = _selectionPolicy.Resolve(new ModelSelectionRequest
        {
            WorkloadClass = request.WorkloadClass,
            RequiredCapabilities = requiredCapabilities,
            Constraints = selectionConstraints,
            PreferredProfileId = request.ResolvedProfileId
                ?? _preferredProfileResolver?.Invoke()
                ?? _preferredProfileId,
        });
        if (request.ResolvedProfileId is { } resolvedProfileId
            && selection.ProfileId != resolvedProfileId)
        {
            throw new InvalidOperationException(
                $"Resolved model profile '{resolvedProfileId.Value:D}' no longer satisfies host policy.");
        }

        var profile = _catalog.Get(selection.ProfileId);
        if (request.WireEstimate is { } wireEstimate
            && wireEstimate.TotalCapacityTokens > profile.ContextWindow)
        {
            throw new ModelProviderException(
                $"The complete model request requires {wireEstimate.TotalCapacityTokens} tokens but profile "
                + $"'{profile.Name}' permits {profile.ContextWindow}.");
        }

        var resolvedSecret = profile.SecretKeyReference is { } secretReference
            ? await _resolveSecretAsync(secretReference, cancellationToken)
            : null;
        var definition = _effectiveCatalog.Get(selection.ProfileId);
        var provider = definition.Registration.CreateProvider(new ModelProviderActivationContext
        {
            HttpClient = _httpClient,
            Profile = profile,
            ProviderConfiguration = definition.ProviderConfiguration,
            ModelConfiguration = definition.ModelConfiguration,
            ResolvedSecret = resolvedSecret,
            RefreshResolvedSecretAsync = profile.SecretKeyReference is { } refreshSecretReference
                && _refreshSecretAsync is not null
                    ? (rejectedSecret, token) => _refreshSecretAsync(
                        refreshSecretReference,
                        rejectedSecret,
                        token)
                    : null,
        });

        await foreach (var chunk in provider.StreamAsync(request, cancellationToken))
        {
            yield return chunk;
        }
    }
}

/// <summary>Loads the configured model catalog from Microsoft.Extensions.Configuration.</summary>
public static class ModelProfileConfigurationLoader
{
    /// <summary>Loads <c>model:profiles</c> without resolving any referenced secrets.</summary>
    public static ConfiguredModelCatalog Load(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var profiles = new List<ModelProfile>();
        foreach (var section in configuration.GetSection("model:profiles").GetChildren())
        {
            var idText = GetRequired(section, "id");
            if (!Guid.TryParse(idText, out var id))
            {
                throw new InvalidOperationException($"Model profile id '{idText}' is not a GUID.");
            }

            var endpointText = GetRequired(section, "endpoint");
            if (!Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint))
            {
                throw new InvalidOperationException(
                    $"Model profile endpoint '{endpointText}' is not an absolute URI.");
            }

            WorkloadClass[] workloads = [.. section.GetSection("intendedWorkloadClasses")
                .GetChildren()
                .Select(item => Enum.TryParse<WorkloadClass>(item.Value, true, out var workload)
                    && Enum.IsDefined(workload)
                        ? workload
                        : throw new InvalidOperationException(
                            $"Unknown workload class '{item.Value}' in profile '{idText}'."))];
            var policyText = section["sensitiveDataPolicy"] ?? nameof(ModelSensitiveDataPolicy.Prohibited);
            if (!Enum.TryParse<ModelSensitiveDataPolicy>(policyText, true, out var sensitiveDataPolicy)
                || !Enum.IsDefined(sensitiveDataPolicy))
            {
                throw new InvalidOperationException(
                    $"Unknown sensitive-data policy '{policyText}' in profile '{idText}'.");
            }

            var reasoningEffort = section["reasoningEffort"];
            var defaultReasoningLevel = ReasoningLevel.None;
            if (!string.IsNullOrWhiteSpace(reasoningEffort)
                && (!Enum.TryParse(reasoningEffort, true, out defaultReasoningLevel)
                    || !Enum.IsDefined(defaultReasoningLevel)))
            {
                throw new InvalidOperationException(
                    $"Unknown reasoning effort '{reasoningEffort}' in profile '{idText}'.");
            }

            var supportedReasoningLevels = ParseSupportedReasoningLevels(
                section,
                idText,
                defaultReasoningLevel);
            if (!supportedReasoningLevels.Contains(defaultReasoningLevel))
            {
                throw new InvalidOperationException(
                    $"Default reasoning effort '{reasoningEffort}' is not supported by profile '{idText}'.");
            }

            profiles.Add(new ModelProfile
            {
                Id = new ModelProfileId(id),
                Name = GetRequired(section, "name"),
                Provider = GetRequired(section, "provider"),
                Endpoint = endpoint,
                ModelId = GetRequired(section, "modelId"),
                SecretKeyReference = section["secretKeyReference"],
                ContextWindow = section.GetValue<int>("contextWindow"),
                MaximumOutputTokens = section.GetValue<int>("maximumOutputTokens"),
                RequestOutputTokenReserve = section.GetValue<int?>("requestOutputTokenReserve")
                    ?? section.GetValue<int>("maximumOutputTokens"),
                Capabilities = new ModelCapabilitySet
                {
                    Streaming = section.GetValue<bool>("capabilities:streaming"),
                    ToolCalls = section.GetValue<bool>("capabilities:toolCalls"),
                    StructuredOutput = section.GetValue<bool>("capabilities:structuredOutput"),
                },
                Cost = new ModelCostMetadata
                {
                    InputPerMillionTokens = section.GetValue<decimal>("cost:inputPerMillionTokens"),
                    OutputPerMillionTokens = section.GetValue<decimal>("cost:outputPerMillionTokens"),
                },
                SensitiveDataPolicy = sensitiveDataPolicy,
                IntendedWorkloadClasses = workloads,
                ReasoningEffort = reasoningEffort,
                DefaultReasoningLevel = defaultReasoningLevel,
                SupportedReasoningLevels = supportedReasoningLevels,
                ReasoningCapability = new EffectiveReasoningCapability
                {
                    Controllability = supportedReasoningLevels.Count > 1
                        ? ReasoningControllability.Selectable
                        : ReasoningControllability.Unsupported,
                    SupportedLevels = supportedReasoningLevels,
                    DefaultLevel = supportedReasoningLevels.Count > 1
                        ? defaultReasoningLevel
                        : null,
                    Provenance = $"legacy model profile/{id:D}",
                },
                Temperature = section.GetValue<decimal?>("temperature"),
                Timeout = TimeSpan.FromSeconds(section.GetValue("timeoutSeconds", 120)),
                RetryPolicy = new ModelRetryPolicy
                {
                    MaxAttempts = section.GetValue("retry:maxAttempts", 3),
                    Delay = TimeSpan.FromMilliseconds(section.GetValue("retry:delayMilliseconds", 250)),
                },
            });
        }

        var enforceHttps = configuration.GetValue("model:enforceModelEndpointHttps", true);
        return new ConfiguredModelCatalog(profiles, enforceHttps);
    }

    private static string GetRequired(IConfigurationSection section, string key)
    {
        return section[key] is { } value && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new InvalidOperationException(
                    $"Model profile '{section.Path}' is missing required key '{key}'.");
    }

    /// <summary>
    /// Parses <paramref name="section"/>'s <c>reasoning:supportedLevels</c> array and/or
    /// <c>reasoningEffort</c> default, always including <see cref="ReasoningLevel.None"/>.
    /// </summary>
    /// <param name="section">The configuration section for this profile.</param>
    /// <param name="profileIdText">The profile id text for error messages.</param>
    /// <param name="defaultReasoningLevel">The independently parsed configured default.</param>
    /// <returns>A deduplicated list of supported reasoning levels, always including <see cref="ReasoningLevel.None"/>.</returns>
    private static IReadOnlyList<ReasoningLevel> ParseSupportedReasoningLevels(
        IConfigurationSection section,
        string profileIdText,
        ReasoningLevel defaultReasoningLevel)
    {
        var supportedLevels = new List<ReasoningLevel> { ReasoningLevel.None };

        var levelsSection = section.GetSection("reasoning:supportedLevels");
        if (levelsSection.GetChildren().Any())
        {
            foreach (var item in levelsSection.GetChildren())
            {
                if (!Enum.TryParse<ReasoningLevel>(item.Value, true, out var level)
                    || !Enum.IsDefined(level))
                {
                    throw new InvalidOperationException(
                        $"Unknown reasoning level '{item.Value}' in profile '{profileIdText}'.");
                }

                if (!supportedLevels.Contains(level))
                {
                    supportedLevels.Add(level);
                }
            }
        }
        else if (defaultReasoningLevel != ReasoningLevel.None)
        {
            supportedLevels.Add(defaultReasoningLevel);
        }

        return supportedLevels;
    }
}

/// <summary>Negotiates hard requirements before model work begins.</summary>
public static class ModelCapabilityNegotiator
{
    /// <summary>Returns every reason the profile cannot satisfy the request.</summary>
    public static ModelCapabilityNegotiationResult Negotiate(
        ModelProfile profile,
        ModelSelectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);
        var reasons = new List<string>();
        if (request.RequiredCapabilities.Streaming && !profile.Capabilities.Streaming)
        {
            reasons.Add("streaming is required but unsupported");
        }

        if (request.RequiredCapabilities.ToolCalls && !profile.Capabilities.ToolCalls)
        {
            reasons.Add("tool calls are required but unsupported");
        }

        if (request.RequiredCapabilities.StructuredOutput && !profile.Capabilities.StructuredOutput)
        {
            reasons.Add("structured output is required but unsupported");
        }

        if (profile.ContextWindow < request.Constraints.MinimumContextWindow)
        {
            reasons.Add(
                $"context window {profile.ContextWindow} is below required "
                + request.Constraints.MinimumContextWindow);
        }

        var combinedCost = profile.Cost.InputPerMillionTokens
            + profile.Cost.OutputPerMillionTokens;
        if (request.Constraints.MaximumCombinedCostPerMillionTokens is { } costCeiling
            && combinedCost > costCeiling)
        {
            reasons.Add($"combined token cost {combinedCost} exceeds ceiling {costCeiling}");
        }

        if (request.Constraints.ContainsSensitiveData
            && profile.SensitiveDataPolicy != ModelSensitiveDataPolicy.Allowed)
        {
            reasons.Add("sensitive data is prohibited by the profile");
        }

        if (profile.IntendedWorkloadClasses.Count > 0
            && !profile.IntendedWorkloadClasses.Contains(request.WorkloadClass))
        {
            reasons.Add($"profile is not intended for {request.WorkloadClass}");
        }

        return new ModelCapabilityNegotiationResult(reasons.Count == 0, reasons);
    }
}

/// <summary>Selects the lowest-cost compatible profile while honoring bounded preferences.</summary>
public sealed class DefaultModelSelectionPolicy : IModelSelectionPolicy
{
    private readonly ConfiguredModelCatalog _catalog;

    /// <summary>Initializes a new instance of the <see cref="DefaultModelSelectionPolicy"/> class.</summary>
    public DefaultModelSelectionPolicy(ConfiguredModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
    }

    /// <inheritdoc />
    public ModelSelectionResult Resolve(
        ModelSelectionRequest request,
        IReadOnlyList<ModelPreferenceHint>? hints = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rationale = new List<string>();
        var compatible = new List<ModelProfile>();
        foreach (var profile in _catalog.Profiles)
        {
            var negotiation = ModelCapabilityNegotiator.Negotiate(profile, request);
            if (negotiation.IsCompatible)
            {
                compatible.Add(profile);
            }
            else
            {
                rationale.Add(
                    $"Rejected {profile.Name}: {string.Join("; ", negotiation.RejectionReasons)}.");
            }
        }

        if (compatible.Count == 0)
        {
            throw new InvalidOperationException(
                "No configured model satisfies the request. " + string.Join(' ', rationale));
        }

        ModelProfile? selected = null;
        if (request.PreferredProfileId is { } preferredId)
        {
            selected = compatible.FirstOrDefault(profile => profile.Id == preferredId);
            rationale.Add(selected is null
                ? $"Ignored preferred profile {preferredId.Value:D} because it is absent or incompatible."
                : $"Selected user/session preference {selected.Name}.");
        }

        if (selected is null && hints is not null)
        {
            foreach (var hint in hints.Where(hint => hint.WorkloadClass == request.WorkloadClass))
            {
                selected = compatible.FirstOrDefault(profile => profile.Id == hint.PreferredProfileId);
                if (selected is not null)
                {
                    rationale.Add($"Applied advisory hint from {hint.Source} for {selected.Name}.");
                    break;
                }

                rationale.Add(
                    $"Ignored advisory hint from {hint.Source}; its profile is absent or incompatible.");
            }
        }

        selected ??= compatible
            .OrderBy(profile => profile.Cost.InputPerMillionTokens
                + profile.Cost.OutputPerMillionTokens)
            .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .First();
        if (!rationale.Any(item => item.StartsWith("Selected", StringComparison.Ordinal)
            || item.StartsWith("Applied", StringComparison.Ordinal)))
        {
            rationale.Add($"Selected lowest-cost compatible profile {selected.Name}.");
        }

        return new ModelSelectionResult(selected.Id, rationale);
    }
}
