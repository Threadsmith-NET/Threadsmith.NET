namespace Threadsmith.Models.OpenAiCompatible;

using System.Text.Json.Serialization;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Typed connection configuration for an OpenAI-compatible provider.</summary>
public sealed record OpenAiCompatibleProviderConfiguration : ModelProviderConfiguration
{
    /// <summary>Absolute base URI beneath which the chat-completions path is resolved.</summary>
    public required Uri BaseUri { get; init; }

    /// <summary>Bounded relative path for chat-completions requests.</summary>
    public string ChatCompletionsPath { get; init; } = "chat/completions";

    /// <summary>Non-credential request headers applied to each request.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Typed model configuration for an OpenAI-compatible provider.</summary>
public sealed record OpenAiCompatibleModelConfiguration : ModelConfiguration
{
    /// <summary>Provider-specific model identifier sent on requests.</summary>
    public required string ModelId { get; init; }

    /// <summary>Optional explicit reasoning compatibility; absence preserves legacy request behavior.</summary>
    public OpenAiReasoningCompatibilityConfiguration? ReasoningCompatibility { get; init; }

    /// <summary>Compatibility-only full endpoint retained in memory for a legacy profile.</summary>
    [JsonIgnore]
    internal Uri? LegacyEndpointOverride { get; init; }

    /// <summary>Compatibility-only effective capability retained for an adapted legacy profile.</summary>
    [JsonIgnore]
    internal EffectiveReasoningCapability? LegacyReasoningCapability { get; init; }
}

/// <summary>Compiled registration and migration boundary for the OpenAI-compatible provider.</summary>
public sealed class OpenAiCompatibleProviderRegistration : IModelProviderRegistration
{
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Connection",
        "Cookie",
        "Host",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "Proxy-Connection",
        "Set-Cookie",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",
    };

    /// <inheritdoc />
    public string TypeDiscriminator => "openai-compatible";

    /// <inheritdoc />
    public Type ProviderConfigurationType => typeof(OpenAiCompatibleProviderConfiguration);

    /// <inheritdoc />
    public Type ModelConfigurationType => typeof(OpenAiCompatibleModelConfiguration);

    /// <inheritdoc />
    public void Validate(ModelProviderConfiguration provider)
    {
        if (provider is not OpenAiCompatibleProviderConfiguration configured)
        {
            throw new ArgumentException("The provider configuration type does not match its registration.", nameof(provider));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(configured.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(configured.Name);
        if (!configured.BaseUri.IsAbsoluteUri
            || !string.IsNullOrEmpty(configured.BaseUri.Query)
            || !string.IsNullOrEmpty(configured.BaseUri.Fragment))
        {
            throw new InvalidOperationException(
                $"Provider '{configured.Id}' base URI must be absolute and contain no query or fragment.");
        }

        if (configured.SecretKeyReference is { } secretReference
            && !secretReference.StartsWith("secrets:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Provider '{configured.Id}' secret reference must use the secrets: scope.");
        }

        _ = ComposeEndpoint(configured.BaseUri, configured.ChatCompletionsPath, configured.Id);
        if (configured.Headers.Count > 32)
        {
            throw new InvalidOperationException($"Provider '{configured.Id}' configures too many headers.");
        }

        var headerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((var name, var value) in configured.Headers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(value);
            var validName = name.Length <= 128 && name.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.'
                    or '^' or '_' or '`' or '|' or '~');
            var credentialLike = name.Contains("Api-Key", StringComparison.OrdinalIgnoreCase)
                || name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Credential", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Token", StringComparison.OrdinalIgnoreCase);
            if (!validName
                || !headerNames.Add(name)
                || ForbiddenHeaders.Contains(name)
                || credentialLike
                || value.Length > 1024
                || value.Any(char.IsControl))
            {
                throw new InvalidOperationException(
                    $"Provider '{configured.Id}' contains a forbidden or invalid configured header.");
            }
        }

        var ids = new HashSet<ModelProfileId>();
        foreach (var model in configured.Models)
        {
            if (model is not OpenAiCompatibleModelConfiguration openAiModel)
            {
                throw new InvalidOperationException(
                    $"Provider '{configured.Id}' contains an incompatible model configuration.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(openAiModel.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(openAiModel.ModelId);
            if (openAiModel.Id == default || !ids.Add(openAiModel.Id))
            {
                throw new InvalidOperationException(
                    $"Provider '{configured.Id}' contains a duplicate or empty model id.");
            }

            if (openAiModel.LegacyEndpointOverride is { } legacyEndpoint && !legacyEndpoint.IsAbsoluteUri)
            {
                throw new InvalidOperationException(
                    $"Legacy model '{openAiModel.Id.Value:D}' endpoint must be absolute.");
            }

            ValidateReasoningCompatibility(configured.Id, openAiModel);
            if (openAiModel.ContextWindow <= 0
                || openAiModel.MaximumOutputTokens <= 0
                || openAiModel.MaximumOutputTokens > openAiModel.ContextWindow
                || openAiModel.EffectiveRequestOutputTokenReserve <= 0
                || openAiModel.EffectiveRequestOutputTokenReserve >= openAiModel.ContextWindow
                || openAiModel.EffectiveRequestOutputTokenReserve > openAiModel.MaximumOutputTokens
                || openAiModel.Cost.InputPerMillionTokens < 0
                || openAiModel.Cost.OutputPerMillionTokens < 0
                || openAiModel.TimeoutSeconds <= 0
                || openAiModel.RetryMaxAttempts <= 0
                || openAiModel.RetryDelayMilliseconds < 0
                || openAiModel.SupportedReasoningLevels.Count == 0
                || openAiModel.SupportedReasoningLevels.Any(level => !Enum.IsDefined(level))
                || openAiModel.SupportedReasoningLevels.Distinct().Count()
                    != openAiModel.SupportedReasoningLevels.Count
                || !openAiModel.SupportedReasoningLevels.Contains(ReasoningLevel.None)
                || !Enum.IsDefined(openAiModel.DefaultReasoningLevel)
                || !openAiModel.SupportedReasoningLevels.Contains(openAiModel.DefaultReasoningLevel))
            {
                throw new InvalidOperationException(
                    $"Model '{openAiModel.Id.Value:D}' contains invalid limits or policy metadata.");
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ModelProfile> CreateProfiles(ModelProviderConfiguration provider)
    {
        if (provider is not OpenAiCompatibleProviderConfiguration configured)
        {
            throw new ArgumentException("The provider configuration type does not match its registration.", nameof(provider));
        }

        var profiles = new List<ModelProfile>();
        foreach (var model in configured.Models
            .Where(model => model.Enabled)
            .Cast<OpenAiCompatibleModelConfiguration>())
        {
            var endpoint = model.LegacyEndpointOverride
                ?? ComposeEndpoint(configured.BaseUri, configured.ChatCompletionsPath, configured.Id);
            profiles.Add(new ModelProfile
            {
                Id = model.Id,
                Name = model.Name,
                Provider = TypeDiscriminator,
                Endpoint = endpoint,
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
                    : model.DefaultReasoningLevel.ToString().ToLowerInvariant(),
                DefaultReasoningLevel = model.DefaultReasoningLevel,
                SupportedReasoningLevels = model.SupportedReasoningLevels,
                ReasoningCapability = CreateEffectiveReasoningCapability(configured.Id, model),
                Temperature = model.Temperature,
                Timeout = TimeSpan.FromSeconds(model.TimeoutSeconds),
                RetryPolicy = new ModelRetryPolicy
                {
                    MaxAttempts = model.RetryMaxAttempts,
                    Delay = TimeSpan.FromMilliseconds(model.RetryDelayMilliseconds),
                },
            });
        }

        return profiles;
    }

    /// <inheritdoc />
    public IModelProvider CreateProvider(ModelProviderActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ProviderConfiguration is not OpenAiCompatibleProviderConfiguration provider
            || context.ModelConfiguration is not OpenAiCompatibleModelConfiguration model)
        {
            throw new ArgumentException(
                "The activation context does not match the OpenAI-compatible registration.",
                nameof(context));
        }

        return new OpenAiCompatibleModelProvider(
            context.HttpClient,
            context.Profile,
            context.ResolvedSecret,
            provider.Headers,
            model.ReasoningCompatibility);
    }

    /// <summary>Rejects simultaneous dedicated and legacy model configuration.</summary>
    public static void EnsureConfigurationIsUnambiguous(
        bool hasProviderCatalog,
        bool hasLegacyProfiles)
    {
        if (hasProviderCatalog && hasLegacyProfiles)
        {
            throw new InvalidOperationException(
                "Dedicated providers.json and legacy model:profiles configuration cannot be used together. "
                + "Migrate the legacy profiles or remove the provider catalog.");
        }
    }

    /// <summary>Adapts legacy profiles into an immutable compiled-provider catalog without writing files.</summary>
    public EffectiveModelProviderCatalog CreateLegacyCatalog(
        ConfiguredModelCatalog legacyCatalog,
        bool enforceHttps = true)
    {
        ArgumentNullException.ThrowIfNull(legacyCatalog);
        var providers = new List<ModelProviderConfiguration>(legacyCatalog.Profiles.Count);
        foreach (var profile in legacyCatalog.Profiles)
        {
            if (!string.Equals(profile.Provider, TypeDiscriminator, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(profile.Provider, "openai", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Legacy model profile '{profile.Name}' is not OpenAI-compatible.");
            }

            var model = new OpenAiCompatibleModelConfiguration
            {
                Id = profile.Id,
                Name = profile.Name,
                ModelId = profile.ModelId,
                ContextWindow = profile.ContextWindow,
                MaximumOutputTokens = profile.MaximumOutputTokens,
                RequestOutputTokenReserve = profile.RequestOutputTokenReserve,
                Capabilities = profile.Capabilities,
                Cost = profile.Cost,
                SensitiveDataPolicy = profile.SensitiveDataPolicy,
                IntendedWorkloadClasses = profile.IntendedWorkloadClasses,
                DefaultReasoningLevel = profile.DefaultReasoningLevel,
                SupportedReasoningLevels = profile.SupportedReasoningLevels,
                Temperature = profile.Temperature,
                TimeoutSeconds = checked((int)profile.Timeout.TotalSeconds),
                RetryMaxAttempts = profile.RetryPolicy.MaxAttempts,
                RetryDelayMilliseconds = checked((int)profile.RetryPolicy.Delay.TotalMilliseconds),
                LegacyEndpointOverride = profile.Endpoint,
                LegacyReasoningCapability = profile.ReasoningCapability,
            };
            providers.Add(new OpenAiCompatibleProviderConfiguration
            {
                Id = "legacy-" + profile.Id.Value.ToString("N"),
                Name = "Legacy " + profile.Name,
                BaseUri = new Uri(profile.Endpoint.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute),
                SecretKeyReference = profile.SecretKeyReference,
                Models = [model],
            });
        }

        var registry = new ModelProviderRegistry([this]);
        return new EffectiveModelProviderCatalog(
            new ModelProviderCatalogConfiguration
            {
                Providers = providers,
            },
            registry,
            enforceHttps);
    }

    private static EffectiveReasoningCapability CreateEffectiveReasoningCapability(
        string providerId,
        OpenAiCompatibleModelConfiguration model)
    {
        var compatibility = model.ReasoningCompatibility;
        if (compatibility is null)
        {
            if (model.LegacyReasoningCapability is { } legacyCapability)
            {
                return legacyCapability;
            }

            return new EffectiveReasoningCapability
            {
                Controllability = model.SupportedReasoningLevels.Count > 1
                    ? ReasoningControllability.Selectable
                    : ReasoningControllability.Unsupported,
                SupportedLevels = model.SupportedReasoningLevels,
                DefaultLevel = model.DefaultReasoningLevel,
                Provenance = $"{providerId}/{model.Id.Value:D}",
            };
        }

        var controllability = compatibility.Mode switch
        {
            OpenAiReasoningControlMode.AlwaysOn or OpenAiReasoningControlMode.Fixed
                => ReasoningControllability.AlwaysOn,
            OpenAiReasoningControlMode.Unsupported => ReasoningControllability.Unsupported,
            _ => ReasoningControllability.Selectable,
        };
        return new EffectiveReasoningCapability
        {
            Controllability = controllability,
            SupportedLevels = controllability == ReasoningControllability.AlwaysOn
                ? []
                : model.SupportedReasoningLevels,
            DefaultLevel = controllability == ReasoningControllability.Selectable
                ? model.DefaultReasoningLevel
                : null,
            RequestMode = compatibility.Mode.ToString(),
            SchemaVersion = compatibility.SchemaVersion,
            ResponseMode = compatibility.ResponseMode.ToString(),
            Provenance = $"{providerId}/{model.Id.Value:D}",
        };
    }

    private static void ValidateReasoningCompatibility(
        string providerId,
        OpenAiCompatibleModelConfiguration model)
    {
        var compatibility = model.ReasoningCompatibility;
        if (compatibility is null)
        {
            return;
        }

        var identity = $"Provider '{providerId}' model '{model.Id.Value:D}'";
        if (compatibility.SchemaVersion != 1
            || !Enum.IsDefined(compatibility.Mode)
            || !Enum.IsDefined(compatibility.ResponseMode))
        {
            throw new InvalidOperationException($"{identity} has an unsupported reasoning compatibility mode or version.");
        }

        if (compatibility.LevelMap.Count > 5
            || compatibility.LevelMap.Any(item => !Enum.IsDefined(item.Key)
                || string.IsNullOrWhiteSpace(item.Value)
                || item.Value.Length > 32
                || item.Value.Any(char.IsControl)))
        {
            throw new InvalidOperationException($"{identity} has an invalid bounded reasoning level map.");
        }

        var selectable = compatibility.Mode is OpenAiReasoningControlMode.StandardEffort
            or OpenAiReasoningControlMode.MappedEffort
            or OpenAiReasoningControlMode.ChatTemplate;
        if (selectable && model.SupportedReasoningLevels.Count < 2)
        {
            throw new InvalidOperationException($"{identity} declares selectable reasoning without an enabled level.");
        }

        if (compatibility.Mode == OpenAiReasoningControlMode.MappedEffort
            && model.SupportedReasoningLevels.Any(level => !compatibility.LevelMap.ContainsKey(level)))
        {
            throw new InvalidOperationException($"{identity} must map every supported reasoning level.");
        }

        if (compatibility.Mode == OpenAiReasoningControlMode.ChatTemplate
            && (compatibility.ChatTemplateKind is null
                || !Enum.IsDefined(compatibility.ChatTemplateKind.Value)
                || (compatibility.ChatTemplateKind == OpenAiChatTemplateKind.ThinkingWithEffort
                    && model.SupportedReasoningLevels.Any(level => !compatibility.LevelMap.ContainsKey(level)))))
        {
            throw new InvalidOperationException(
                $"{identity} must select a compiled chat-template kind and completely map its required levels.");
        }

        if (compatibility.Mode == OpenAiReasoningControlMode.Fixed
            && (compatibility.FixedRequestKind is null
                || !Enum.IsDefined(compatibility.FixedRequestKind.Value)))
        {
            throw new InvalidOperationException($"{identity} must select a compiled fixed request kind.");
        }

        var invalidShape = compatibility.Mode switch
        {
            OpenAiReasoningControlMode.StandardEffort => compatibility.LevelMap.Count != 0
                || compatibility.ChatTemplateKind is not null
                || compatibility.FixedRequestKind is not null,
            OpenAiReasoningControlMode.MappedEffort => compatibility.ChatTemplateKind is not null
                || compatibility.FixedRequestKind is not null,
            OpenAiReasoningControlMode.ChatTemplate => compatibility.FixedRequestKind is not null,
            OpenAiReasoningControlMode.Fixed => compatibility.LevelMap.Count != 0
                || compatibility.ChatTemplateKind is not null
                || compatibility.FixedRequestKind != OpenAiFixedRequestKind.ThinkingEnvironmentBudget4096,
            OpenAiReasoningControlMode.AlwaysOn => compatibility.LevelMap.Count != 0
                || compatibility.ChatTemplateKind is not null
                || compatibility.FixedRequestKind is not null,
            OpenAiReasoningControlMode.Unsupported => compatibility.LevelMap.Count != 0
                || compatibility.ChatTemplateKind is not null
                || (compatibility.FixedRequestKind is not null
                    && compatibility.FixedRequestKind != OpenAiFixedRequestKind.DisableThinkingWithPreservation),
            _ => true,
        };
        if (invalidShape)
        {
            throw new InvalidOperationException($"{identity} contains settings that do not belong to its reasoning mode.");
        }

        if (compatibility.Mode is OpenAiReasoningControlMode.AlwaysOn or OpenAiReasoningControlMode.Fixed
            && (model.SupportedReasoningLevels.Count != 1
                || model.SupportedReasoningLevels[0] != ReasoningLevel.None
                || model.DefaultReasoningLevel != ReasoningLevel.None))
        {
            throw new InvalidOperationException($"{identity} cannot advertise selectable levels for uncontrollable reasoning.");
        }

        if (compatibility.Mode == OpenAiReasoningControlMode.Unsupported
            && (model.SupportedReasoningLevels.Count != 1
                || model.SupportedReasoningLevels[0] != ReasoningLevel.None
                || model.DefaultReasoningLevel != ReasoningLevel.None
                || compatibility.ResponseMode != OpenAiReasoningResponseMode.None))
        {
            throw new InvalidOperationException($"{identity} has inconsistent unsupported reasoning metadata.");
        }
    }

    private static Uri ComposeEndpoint(Uri baseUri, string path, string providerId)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Length > 256
            || path.StartsWith('/')
            || path.Contains('\\')
            || path.Contains('?')
            || path.Contains('#')
            || path.Any(char.IsControl)
            || Uri.UnescapeDataString(path).Split('/').Any(segment => segment is "." or ".."))
        {
            throw new InvalidOperationException(
                $"Provider '{providerId}' chat-completions path must be a bounded relative path.");
        }

        var baseBuilder = new UriBuilder(baseUri)
        {
            Path = baseUri.AbsolutePath.TrimEnd('/') + "/",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        var endpoint = new Uri(baseBuilder.Uri, path);
        if (!string.Equals(endpoint.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(endpoint.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Provider '{providerId}' chat-completions path cannot change endpoint authority.");
        }

        return endpoint;
    }
}
