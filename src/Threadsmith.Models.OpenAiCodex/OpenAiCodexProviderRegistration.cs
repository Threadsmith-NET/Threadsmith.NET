namespace Threadsmith.Models.OpenAiCodex;

using Threadsmith.Models;

/// <summary>Protected typed configuration for Threadsmith's native Codex provider.</summary>
public sealed record OpenAiCodexProviderConfiguration : ModelProviderConfiguration;

/// <summary>One model discovered from the authenticated Codex account.</summary>
public sealed record OpenAiCodexModelConfiguration : ModelConfiguration
{
    /// <summary>Provider model identifier.</summary>
    public required string ModelId { get; init; }
}

/// <summary>Registers native Codex Responses models discovered after authentication.</summary>
public sealed class OpenAiCodexProviderRegistration : IModelProviderRegistration
{
    /// <summary>Logical Threadsmith-owned OAuth credential reference.</summary>
    public const string OAuthSecretReference = "secrets:openai-codex:oauth";

    /// <summary>Compiled Codex backend base address.</summary>
    public static readonly Uri BackendEndpoint = new("https://chatgpt.com/backend-api/codex/");

    /// <summary>Compiled Responses endpoint.</summary>
    public static readonly Uri ResponsesEndpoint = new(BackendEndpoint, "responses");

    /// <summary>Compiled authenticated models endpoint.</summary>
    public static readonly Uri ModelsEndpoint = new(BackendEndpoint, "models");

    /// <inheritdoc />
    public string TypeDiscriminator => "openai-codex";

    /// <inheritdoc />
    public Type ProviderConfigurationType => typeof(OpenAiCodexProviderConfiguration);

    /// <inheritdoc />
    public Type ModelConfigurationType => typeof(OpenAiCodexModelConfiguration);

    /// <inheritdoc />
    public void Validate(ModelProviderConfiguration provider)
    {
        if (provider is not OpenAiCodexProviderConfiguration configured)
        {
            throw new ArgumentException("The provider configuration type does not match its registration.", nameof(provider));
        }

        if (!string.Equals(configured.SecretKeyReference, OAuthSecretReference, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Codex provider requires the compiled Threadsmith OAuth reference.");
        }

        if (configured.Models.Count is 0 or > 256
            || configured.Models.Any(model => model is not OpenAiCodexModelConfiguration codex
                || string.IsNullOrWhiteSpace(codex.ModelId)
                || codex.ContextWindow <= 0
                || codex.MaximumOutputTokens <= 0
                || codex.MaximumOutputTokens > codex.ContextWindow
                || codex.EffectiveRequestOutputTokenReserve <= 0
                || codex.EffectiveRequestOutputTokenReserve >= codex.ContextWindow
                || codex.EffectiveRequestOutputTokenReserve > codex.MaximumOutputTokens
                || codex.TimeoutSeconds <= 0
                || codex.RetryMaxAttempts <= 0))
        {
            throw new InvalidOperationException("The authenticated Codex model catalog is invalid or empty.");
        }

        if (configured.Models.Select(model => ((OpenAiCodexModelConfiguration)model).ModelId)
            .Distinct(StringComparer.Ordinal).Count() != configured.Models.Count)
        {
            throw new InvalidOperationException("The authenticated Codex model catalog contains duplicate identifiers.");
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ModelProfile> CreateProfiles(ModelProviderConfiguration provider)
    {
        Validate(provider);
        return provider.Models.Where(model => model.Enabled).Cast<OpenAiCodexModelConfiguration>().Select(model =>
            new ModelProfile
            {
                Id = model.Id,
                Name = model.Name,
                Provider = TypeDiscriminator,
                Endpoint = ResponsesEndpoint,
                ModelId = model.ModelId,
                SecretKeyReference = OAuthSecretReference,
                ContextWindow = model.ContextWindow,
                MaximumOutputTokens = model.MaximumOutputTokens,
                RequestOutputTokenReserve = model.RequestOutputTokenReserve,
                Capabilities = model.Capabilities,
                Cost = model.Cost,
                SensitiveDataPolicy = model.SensitiveDataPolicy,
                IntendedWorkloadClasses = model.IntendedWorkloadClasses,
                DefaultReasoningLevel = model.DefaultReasoningLevel,
                SupportedReasoningLevels = model.SupportedReasoningLevels,
                ReasoningCapability = new EffectiveReasoningCapability
                {
                    Controllability = ReasoningControllability.Selectable,
                    SupportedLevels = model.SupportedReasoningLevels,
                    DefaultLevel = model.DefaultReasoningLevel,
                },
                Timeout = TimeSpan.FromSeconds(model.TimeoutSeconds),
                RetryPolicy = new ModelRetryPolicy
                {
                    MaxAttempts = model.RetryMaxAttempts,
                    Delay = TimeSpan.FromMilliseconds(model.RetryDelayMilliseconds),
                },
            }).ToArray();
    }

    /// <inheritdoc />
    public IModelProvider CreateProvider(ModelProviderActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ProviderConfiguration is not OpenAiCodexProviderConfiguration
            || context.ModelConfiguration is not OpenAiCodexModelConfiguration)
        {
            throw new ArgumentException("The activation context does not contain Codex configuration.", nameof(context));
        }

        var accessToken = context.ResolvedSecret
            ?? throw new InvalidOperationException("Codex authentication is required before provider activation.");
        return new OpenAiCodexModelProvider(
            context.HttpClient,
            context.Profile,
            accessToken,
            context.RefreshResolvedSecretAsync);
    }
}
