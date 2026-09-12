namespace Threadsmith.Models.Anthropic;

using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Compiled direct-API registration for the allowlisted Anthropic provider discriminator.</summary>
public sealed class AnthropicProviderRegistration : IModelProviderRegistration, IModelRequestPreparation
{
    /// <summary>Fixed direct Messages endpoint; configuration cannot alter this authority.</summary>
    public static readonly Uri MessagesEndpoint = new("https://api.anthropic.com/v1/messages", UriKind.Absolute);

    /// <inheritdoc />
    public string TypeDiscriminator => "anthropic";

    /// <inheritdoc />
    public Type ProviderConfigurationType => typeof(AnthropicProviderConfiguration);

    /// <inheritdoc />
    public Type ModelConfigurationType => typeof(AnthropicModelConfiguration);

    /// <inheritdoc />
    public void Validate(ModelProviderConfiguration provider)
    {
        if (provider is not AnthropicProviderConfiguration configured)
        {
            throw new ArgumentException("The provider configuration type does not match its registration.", nameof(provider));
        }

        AnthropicCatalogHydrator.ValidateDescriptor(configured);
        ArgumentException.ThrowIfNullOrWhiteSpace(configured.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(configured.Name);
        if (configured.SecretKeyReference is null
            || !configured.SecretKeyReference.StartsWith("secrets:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Anthropic provider '{configured.Id}' requires a secrets: key reference.");
        }

        if (configured.ModelOverrides.Count > 128
            || configured.ModelOverrides.Any(overrideConfiguration => string.IsNullOrWhiteSpace(overrideConfiguration.ModelId)
                || overrideConfiguration.ModelId.Length > 256
                || overrideConfiguration.ModelId.Any(char.IsControl)))
        {
            throw new InvalidOperationException(
                $"Anthropic provider '{configured.Id}' contains invalid exact-model overrides.");
        }

        if (configured.ModelOverrides.Select(item => item.ModelId).Distinct(StringComparer.Ordinal).Count()
            != configured.ModelOverrides.Count)
        {
            throw new InvalidOperationException(
                $"Anthropic provider '{configured.Id}' contains duplicate exact-model overrides.");
        }

        if (configured.Enabled && configured.Models.Count == 0)
        {
            throw new InvalidOperationException(
                $"Anthropic provider '{configured.Id}' must be hydrated before it can become selectable.");
        }

        var profileIds = new HashSet<ModelProfileId>();
        foreach (var model in configured.Models)
        {
            if (model is not AnthropicModelConfiguration anthropicModel)
            {
                throw new InvalidOperationException(
                    $"Anthropic provider '{configured.Id}' contains an incompatible model configuration.");
            }

            ValidateModel(configured.Id, anthropicModel, profileIds);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ModelProfile> CreateProfiles(ModelProviderConfiguration provider)
    {
        if (provider is not AnthropicProviderConfiguration configured)
        {
            throw new ArgumentException("The provider configuration type does not match its registration.", nameof(provider));
        }

        return configured.Models
            .OfType<AnthropicModelConfiguration>()
            .Where(model => model.Enabled)
            .Select(model => new ModelProfile
            {
                Id = model.Id,
                Name = model.Name,
                Provider = TypeDiscriminator,
                Endpoint = MessagesEndpoint,
                ModelId = model.ModelId,
                SecretKeyReference = configured.SecretKeyReference,
                ContextWindow = model.ContextWindow,
                MaximumOutputTokens = model.MaximumOutputTokens,
                RequestOutputTokenReserve = model.RequestOutputTokenReserve,
                Capabilities = model.Capabilities,
                Cost = model.Cost,
                SensitiveDataPolicy = model.SensitiveDataPolicy,
                IntendedWorkloadClasses = model.IntendedWorkloadClasses,
                ReasoningEffort = model.DefaultReasoningLevel == ReasoningLevel.None
                    ? null
                    : model.DefaultReasoningLevel.Value,
                DefaultReasoningLevel = model.DefaultReasoningLevel,
                SupportedReasoningLevels = model.SupportedReasoningLevels,
                ReasoningCapability = new EffectiveReasoningCapability
                {
                    Controllability = model.Compatibility.ThinkingMode == AnthropicThinkingMode.Disabled
                        ? ReasoningControllability.Unsupported
                        : ReasoningControllability.Selectable,
                    SupportedLevels = model.Compatibility.ThinkingMode == AnthropicThinkingMode.Disabled
                        ? [ReasoningLevel.None]
                        : model.SupportedReasoningLevels,
                    DefaultLevel = model.Compatibility.ThinkingMode == AnthropicThinkingMode.Disabled
                        ? null
                        : model.DefaultReasoningLevel,
                    SupportsReasoningOff = model.Compatibility.SupportsReasoningOff,
                    RequestMode = "anthropic-" + model.Compatibility.ThinkingMode.ToString().ToLowerInvariant(),
                    SchemaVersion = 1,
                    ResponseMode = "anthropic-thinking-summary",
                    Provenance = $"{configured.Id}/{model.ModelId}",
                },
                Temperature = model.Temperature,
                Timeout = TimeSpan.FromSeconds(model.TimeoutSeconds),
                MaximumStreamedBytes = model.MaximumStreamedBytes,
                MaximumToolCalls = model.MaximumToolCalls,
                RetryPolicy = new ModelRetryPolicy
                {
                    MaxAttempts = model.RetryMaxAttempts,
                    Delay = TimeSpan.FromMilliseconds(model.RetryDelayMilliseconds),
                },
            })
            .ToArray();
    }

    /// <inheritdoc />
    public IModelProvider CreateProvider(ModelProviderActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ProviderConfiguration is not AnthropicProviderConfiguration configured
            || context.ModelConfiguration is not AnthropicModelConfiguration model
            || string.IsNullOrWhiteSpace(context.ResolvedSecret))
        {
            throw new ArgumentException("The activation context does not match the Anthropic registration.", nameof(context));
        }

        return new AnthropicModelProvider(context.HttpClient, context.Profile, context.ResolvedSecret, model.Compatibility, configured.Id, configured.ResourceLimits);
    }

    /// <inheritdoc />
    public ModelRequestPreparationResult Prepare(ModelRequestPreparationContext context)
    {
        return AnthropicRequestPreparer.Prepare(context);
    }

    private static void ValidateModel(
        string providerId,
        AnthropicModelConfiguration model,
        ISet<ModelProfileId> profileIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.ModelId);
        if (model.Id == default || !profileIds.Add(model.Id)
            || !string.Equals(model.ModelId, model.Compatibility.ModelId, StringComparison.Ordinal)
            || model.ContextWindow <= 0
            || model.MaximumOutputTokens <= 0
            || model.MaximumOutputTokens > model.ContextWindow
            || model.EffectiveRequestOutputTokenReserve <= 0
            || model.EffectiveRequestOutputTokenReserve >= model.ContextWindow
            || model.EffectiveRequestOutputTokenReserve > model.MaximumOutputTokens
            || model.TimeoutSeconds < 0
            || model.MaximumStreamedBytes < 0
            || model.MaximumToolCalls < 0
            || model.RetryMaxAttempts <= 0
            || model.RetryDelayMilliseconds < 0
            || model.SupportedReasoningLevels.Count == 0
            || model.SupportedReasoningLevels.Distinct().Count() != model.SupportedReasoningLevels.Count
            || !model.SupportedReasoningLevels.Contains(model.DefaultReasoningLevel))
        {
            throw new InvalidOperationException(
                $"Anthropic model '{providerId}/{model.ModelId}' contains invalid reviewed metadata.");
        }

        var compatibility = model.Compatibility;
        if (compatibility.MinimumCacheableTokens is <= 0
            || (!compatibility.PromptCachingEnabled && compatibility.MinimumCacheableTokens is not null)
            || compatibility.ManualThinkingBudgets.Count > 16
            || compatibility.ManualThinkingBudgets.Any(item => item.Key == ReasoningLevel.None || item.Value < 1024
                || item.Value >= model.EffectiveRequestOutputTokenReserve)
            || !compatibility.Prices.IsComplete)
        {
            throw new InvalidOperationException(
                $"Anthropic model '{providerId}/{model.ModelId}' contains incomplete cache, thinking, or price policy.");
        }

        if (compatibility.ThinkingMode == AnthropicThinkingMode.Disabled
            && (model.SupportedReasoningLevels.Count != 1 || model.SupportedReasoningLevels[0] != ReasoningLevel.None))
        {
            throw new InvalidOperationException(
                $"Anthropic model '{providerId}/{model.ModelId}' cannot advertise reasoning levels when thinking is disabled.");
        }

        if (compatibility.ThinkingMode != AnthropicThinkingMode.Disabled
            && !compatibility.SupportsReasoningOff
            && model.SupportedReasoningLevels.Contains(ReasoningLevel.None))
        {
            throw new InvalidOperationException(
                $"Anthropic model '{providerId}/{model.ModelId}' advertises unsupported reasoning-off selection.");
        }
    }
}
