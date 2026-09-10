namespace Threadsmith.Models.Anthropic;

using System.Globalization;
using Threadsmith.Core;

/// <summary>Detached, secret-free metadata returned by bounded authenticated model discovery.</summary>
public sealed record AnthropicDiscoveredModel
{
    /// <summary>Exact provider model identifier.</summary>
    public required string ModelId { get; init; }

    /// <summary>Bounded sanitized provider display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Advertised maximum input tokens, or null if unavailable.</summary>
    public int? MaximumInputTokens { get; init; }

    /// <summary>Advertised maximum output tokens, or null if unavailable.</summary>
    public int? MaximumOutputTokens { get; init; }

    /// <summary>Nullable API capabilities; absent fields never imply support.</summary>
    public AnthropicDiscoveredCapabilities? Capabilities { get; init; }
}

/// <summary>Detached capability facts understood by this adapter.</summary>
public sealed record AnthropicDiscoveredCapabilities
{
    /// <summary>API structured output support, when declared.</summary>
    public bool? StructuredOutputs { get; init; }

    /// <summary>API thinking support, when declared.</summary>
    public bool? Thinking { get; init; }

    /// <summary>API adaptive thinking support, when declared.</summary>
    public bool? AdaptiveThinking { get; init; }

    /// <summary>API manual thinking support, when declared.</summary>
    public bool? ManualThinking { get; init; }

    /// <summary>API effort support, when declared.</summary>
    public bool? Effort { get; init; }

    /// <summary>Per-level API support; null retains reviewed fallback values.</summary>
    public IReadOnlyDictionary<string, bool>? EffortLevels { get; init; }
}

/// <summary>One bounded eligibility diagnostic, including the stable selectable identity.</summary>
public sealed record AnthropicModelEligibility
{
    /// <summary>Exact returned provider model identifier.</summary>
    public required string ModelId { get; init; }

    /// <summary>Deterministic profile identity for explicit model configuration.</summary>
    public required ModelProfileId ProfileId { get; init; }

    /// <summary>Whether the model was admitted to the immutable startup catalog.</summary>
    public bool Eligible { get; init; }

    /// <summary>Safe bounded eligibility explanation.</summary>
    public required string Reason { get; init; }
}

/// <summary>Process-local acquisition evidence and detached model eligibility.</summary>
public sealed record AnthropicCatalogSnapshot
{
    /// <summary>Whether this process completed authenticated discovery.</summary>
    public bool Authenticated { get; init; }

    /// <summary>Whether discovery rejected the current credential.</summary>
    public bool CredentialRejected { get; init; }

    /// <summary>Whether metadata was used after its freshness interval.</summary>
    public bool Stale { get; init; }

    /// <summary>Successful metadata fetch timestamp, if any.</summary>
    public DateTimeOffset? FetchedAt { get; init; }

    /// <summary>Safe acquisition summary.</summary>
    public required string Status { get; init; }

    /// <summary>Every returned model and its eligibility outcome.</summary>
    public IReadOnlyList<AnthropicModelEligibility> Models { get; init; } = [];
}

/// <summary>Hydrates an inert trusted descriptor after complete discovery, excluding incomplete models individually.</summary>
public static class AnthropicCatalogHydrator
{
    /// <summary>Applies exact-model trusted policy with valid API facts taking precedence.</summary>
    public static AnthropicProviderConfiguration Hydrate(
        AnthropicProviderConfiguration provider,
        IReadOnlyList<AnthropicDiscoveredModel> discoveredModels,
        IReadOnlyDictionary<string, AnthropicModelCompatibility> reviewedCompatibility)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(discoveredModels);
        ArgumentNullException.ThrowIfNull(reviewedCompatibility);
        ValidateDescriptor(provider);
        if (discoveredModels.Count > 128)
        {
            throw new ModelProviderException("Anthropic discovery exceeds the model count limit.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var overrides = provider.ModelOverrides.ToDictionary(item => item.ModelId, StringComparer.Ordinal);
        var hydrated = new List<ModelConfiguration>(discoveredModels.Count);
        var diagnostics = new List<AnthropicModelEligibility>(discoveredModels.Count);
        foreach (var discovered in discoveredModels)
        {
            if (!IsSafeMetadata(discovered) || !seen.Add(discovered.ModelId))
            {
                throw new ModelProviderException("Anthropic discovery returned unsafe or duplicate metadata.");
            }

            overrides.TryGetValue(discovered.ModelId, out var modelOverride);
            var compatibility = modelOverride?.Compatibility
                ?? (reviewedCompatibility.TryGetValue(discovered.ModelId, out var known) ? known : null);
            var defaults = modelOverride?.Defaults ?? provider.Defaults;
            var model = CreateModel(provider.Id, discovered, compatibility, defaults, modelOverride, out var reason);
            if (model is not null)
            {
                hydrated.Add(model);
            }

            diagnostics.Add(new AnthropicModelEligibility
            {
                ModelId = discovered.ModelId,
                ProfileId = AnthropicProfileIdentifiers.Create(provider.Id, discovered.ModelId),
                Eligible = model is { Enabled: true },
                Reason = reason,
            });
        }

        var snapshot = provider.CatalogSnapshot ?? new AnthropicCatalogSnapshot { Status = "metadata hydrated" };
        return provider with
        {
            Enabled = provider.Enabled && hydrated.Any(model => model.Enabled),
            Models = hydrated.AsReadOnly(),
            CatalogSnapshot = snapshot with { Models = diagnostics.AsReadOnly() },
        };
    }

    /// <summary>Validates trusted configuration before any credential resolution or network activity.</summary>
    public static void ValidateDescriptor(AnthropicProviderConfiguration provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (!IsSafeIdentity(provider.Id) || string.IsNullOrWhiteSpace(provider.Name)
            || provider.SecretKeyReference is not { Length: > 8 } reference
            || !reference.StartsWith("secrets:", StringComparison.OrdinalIgnoreCase)
            || reference.Length > 256 || reference.Any(char.IsControl)
            || reference.Split(':').Any(segment => !IsSafeIdentity(segment) || segment is "." or "..")
            || provider.Defaults is null || provider.ModelOverrides is null
            || provider.ModelOverrides.Count > 128)
        {
            throw new InvalidOperationException("Anthropic requires a valid provider identity and user-owned secrets: reference.");
        }

        ValidateDefaults(provider.Defaults);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in provider.ModelOverrides)
        {
            if (model is null || !IsSafeIdentity(model.ModelId) || !seen.Add(model.ModelId)
                || (model.Name is not null && (string.IsNullOrWhiteSpace(model.Name) || model.Name.Length > 512 || model.Name.Any(char.IsControl)))
                || (model.Compatibility is { } policy && !string.Equals(policy.ModelId, model.ModelId, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException("Anthropic exact-model overrides contain invalid or duplicate identities.");
            }

            if (model.Defaults is { } defaults)
            {
                ValidateDefaults(defaults);
            }
        }
    }

    /// <summary>Accepts bounded ASCII identifiers that cannot contain controls or path separators.</summary>
    internal static bool IsSafeIdentity(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 256
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    }

    /// <summary>Validates detached cache and discovery values without requiring optional API fields.</summary>
    internal static bool IsSafeMetadata(AnthropicDiscoveredModel? model)
    {
        return model is not null && IsSafeIdentity(model.ModelId)
            && !string.IsNullOrWhiteSpace(model.DisplayName) && model.DisplayName.Length <= 512
            && !model.DisplayName.Any(char.IsControl)
            && model.MaximumInputTokens is not <= 0 && model.MaximumOutputTokens is not <= 0
            && (model.Capabilities?.EffortLevels is not { } levels ||
                (levels.Count <= 16 && levels.Keys.All(IsSafeIdentity)));
    }

    private static void ValidateDefaults(AnthropicModelDefaults defaults)
    {
        if (defaults.RequestOutputTokenReserve <= 0 || defaults.TimeoutSeconds < 0
            || defaults.MaximumStreamedBytes < 0 || defaults.MaximumToolCalls < 0
            || defaults.RetryMaxAttempts <= 0 || defaults.RetryDelayMilliseconds < 0
            || defaults.IntendedWorkloadClasses is null || !Enum.IsDefined(defaults.SensitiveDataPolicy))
        {
            throw new InvalidOperationException("Anthropic request defaults contain invalid bounds or policy.");
        }
    }

    private static AnthropicModelConfiguration? CreateModel(
        string providerId, AnthropicDiscoveredModel discovered, AnthropicModelCompatibility? policy, AnthropicModelDefaults defaults, AnthropicModelOverrideConfiguration? modelOverride, out string reason)
    {
        reason = "reviewed compatibility or pricing unavailable";
        if (policy is null || !string.Equals(policy.ModelId, discovered.ModelId, StringComparison.Ordinal)
            || !DateOnly.TryParseExact(policy.SourceDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || date > DateOnly.FromDateTime(DateTime.UtcNow)
            || string.IsNullOrWhiteSpace(policy.SourceUrl)
            || !policy.Prices.IsComplete)
        {
            return null;
        }

        var input = discovered.MaximumInputTokens ?? policy.MaximumInputTokens;
        var output = discovered.MaximumOutputTokens ?? policy.MaximumOutputTokens;
        reason = "input/output limits or request reserve incompatible";
        if (input is not > 0 || output is not > 0 || output > input
            || defaults.RequestOutputTokenReserve <= 0 || defaults.RequestOutputTokenReserve >= input
            || defaults.RequestOutputTokenReserve > output)
        {
            return null;
        }

        var api = discovered.Capabilities;
        var thinking = api?.Thinking == false ? AnthropicThinkingMode.Disabled : policy.ThinkingMode;
        if ((thinking == AnthropicThinkingMode.Adaptive && api?.AdaptiveThinking == false)
            || (thinking == AnthropicThinkingMode.Manual && api?.ManualThinking == false))
        {
            reason = "API thinking capability conflicts with reviewed protocol";
            return null;
        }

        ReasoningLevel[] levels = thinking switch
        {
            AnthropicThinkingMode.Disabled => [ReasoningLevel.None],
            AnthropicThinkingMode.Manual => [.. policy.ManualThinkingBudgets.Keys],
            _ => [.. policy.AdaptiveEffortLevels.Where(level => api?.Effort != false
                && (api?.EffortLevels is null || !api.EffortLevels.TryGetValue(level.Value, out var supported) || supported))],
        };
        reason = "reviewed thinking or caching policy incomplete";
        if (levels.Length == 0 || levels.Length > 16 || levels.Distinct().Count() != levels.Length
            || (thinking == AnthropicThinkingMode.Manual && policy.ManualThinkingBudgets.Any(pair =>
                pair.Key == ReasoningLevel.None || pair.Value < 1024 || pair.Value >= defaults.RequestOutputTokenReserve))
            || (policy.PromptCachingEnabled && policy.MinimumCacheableTokens is not > 0))
        {
            return null;
        }

        if (thinking != AnthropicThinkingMode.Disabled && policy.SupportsReasoningOff)
        {
            levels = [ReasoningLevel.None, .. levels.Where(level => level != ReasoningLevel.None)];
        }

        var defaultLevel = defaults.DefaultReasoningLevel
            ?? (levels.Contains(ReasoningLevel.High) ? ReasoningLevel.High : levels[0]);
        if (!levels.Contains(defaultLevel))
        {
            reason = "configured default reasoning is unsupported";
            return null;
        }

        policy = policy with
        {
            SupportsStrictSchemas = api?.StructuredOutputs ?? policy.SupportsStrictSchemas,
            ThinkingMode = thinking,
            SupportsReasoningOff = thinking == AnthropicThinkingMode.Disabled || policy.SupportsReasoningOff,
            AdaptiveEffortLevels = thinking == AnthropicThinkingMode.Adaptive
                ? Array.AsReadOnly(levels.Where(level => level != ReasoningLevel.None).ToArray()) : [],
        };
        var inputPrice = policy.Prices.InputPerMillionTokens ?? throw new InvalidOperationException("Validated input price is missing.");
        var outputPrice = policy.Prices.OutputPerMillionTokens ?? throw new InvalidOperationException("Validated output price is missing.");
        var writePrice = policy.Prices.CacheWritePerMillionTokens ?? throw new InvalidOperationException("Validated cache price is missing.");
        var readPrice = policy.Prices.CacheReadPerMillionTokens ?? throw new InvalidOperationException("Validated cache price is missing.");
        reason = modelOverride?.Enabled == false ? "disabled by trusted model policy" : "eligible";
        return new AnthropicModelConfiguration
        {
            Id = AnthropicProfileIdentifiers.Create(providerId, discovered.ModelId),
            Name = modelOverride?.Name ?? $"{discovered.DisplayName} [{providerId}/{discovered.ModelId}]",
            ModelId = discovered.ModelId,
            Enabled = modelOverride?.Enabled ?? true,
            ContextWindow = input.Value,
            MaximumOutputTokens = output.Value,
            RequestOutputTokenReserve = defaults.RequestOutputTokenReserve,
            Capabilities = new ModelCapabilitySet { Streaming = policy.SupportsStreaming, ToolCalls = policy.SupportsToolCalls, StructuredOutput = policy.SupportsStrictSchemas },
            Cost = new ModelCostMetadata
            {
                InputPerMillionTokens = inputPrice,
                OutputPerMillionTokens = outputPrice,
                CachePricing = new ModelCachePricing { ReadPerMillionTokens = readPrice, WritePerMillionTokens = writePrice, SourceDate = policy.SourceDate },
            },
            SensitiveDataPolicy = defaults.SensitiveDataPolicy,
            IntendedWorkloadClasses = Array.AsReadOnly(defaults.IntendedWorkloadClasses.ToArray()),
            DefaultReasoningLevel = defaultLevel,
            SupportedReasoningLevels = Array.AsReadOnly(levels),
            TimeoutSeconds = defaults.TimeoutSeconds,
            MaximumStreamedBytes = defaults.MaximumStreamedBytes,
            MaximumToolCalls = defaults.MaximumToolCalls,
            RetryMaxAttempts = defaults.RetryMaxAttempts,
            RetryDelayMilliseconds = defaults.RetryDelayMilliseconds,
            Compatibility = policy,
        };
    }
}
