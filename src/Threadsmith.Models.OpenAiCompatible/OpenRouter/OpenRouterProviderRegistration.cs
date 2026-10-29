namespace Threadsmith.Models.OpenAiCompatible.OpenRouter;

using System.Security.Cryptography;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Explicit secrets-authenticated OpenRouter connection, with a compiled HTTPS authority.</summary>
public sealed record OpenRouterProviderConfiguration : ModelProviderConfiguration;

/// <summary>Manually verified capabilities for one exact OpenRouter model and upstream.</summary>
public sealed record OpenRouterModelConfiguration : ModelConfiguration
{
    /// <summary>Exact vendor/model slug; dynamic routers and model fallback lists are not supported.</summary>
    public required string ModelId { get; init; }

    /// <summary>Whether reasoning is selectable, intrinsic, or unavailable.</summary>
    public ReasoningControllability ReasoningControl { get; init; } = ReasoningControllability.Unsupported;

    /// <summary>Maps enabled host levels to OpenRouter effort values.</summary>
    public IReadOnlyDictionary<ReasoningLevel, string> EffortMap { get; init; } = new Dictionary<ReasoningLevel, string>();

    /// <summary>Maps enabled host levels to reasoning budgets, mutually exclusive with effort mappings.</summary>
    public IReadOnlyDictionary<ReasoningLevel, int> ReasoningBudgets { get; init; } = new Dictionary<ReasoningLevel, int>();

    /// <summary>Whether complete reasoning details can be replayed to the pinned upstream.</summary>
    public bool SupportsReasoningReplay { get; init; }

    /// <summary>Optional exact upstream identifier; required for private reasoning replay.</summary>
    public string? Upstream { get; init; }

    /// <summary>Expected response provider display name, distinct from an endpoint routing slug; required for replay.</summary>
    public string? ExpectedProviderName { get; init; }

    /// <summary>Whether this exact model/upstream performs automatic prefix caching.</summary>
    public bool AutomaticPrefixCaching { get; init; }

    /// <summary>Minimum prefix tokens for an explicit ephemeral tail cache marker; null disables markers.</summary>
    public int? MinimumCacheableTokens { get; init; }

    /// <summary>Whether this upstream accepts the optional parallel_tool_calls request parameter; batch admission remains host-owned.</summary>
    public bool ParallelToolCalls { get; init; }

    /// <summary>Whether this exact upstream supports deterministic sampling seeds.</summary>
    public bool SeedSupported { get; init; }

    /// <summary>Whether this exact upstream accepts strict function schemas.</summary>
    public bool StrictToolArguments { get; init; }

    /// <summary>Whether this exact upstream accepts a JSON-schema response format.</summary>
    public bool JsonSchemaResponses { get; init; }
}

/// <summary>Registers manually configured OpenRouter models on the shared chat-completions lifecycle.</summary>
public sealed class OpenRouterProviderRegistration : IModelProviderRegistration, IModelRequestPreparation
{
    /// <summary>Compiled OpenRouter API authority.</summary>
    internal static readonly Uri Endpoint = new("https://openrouter.ai/api/v1/chat/completions");

    /// <inheritdoc />
    public string TypeDiscriminator => "openrouter";

    /// <inheritdoc />
    public ModelProviderInstructionAsset? ProviderInstructionAsset => null;

    /// <inheritdoc />
    public Type ProviderConfigurationType => typeof(OpenRouterProviderConfiguration);

    /// <inheritdoc />
    public Type ModelConfigurationType => typeof(OpenRouterModelConfiguration);

    /// <inheritdoc />
    public IReadOnlyList<string> RepositoryProtectedModelProperties { get; } = ["modelId", "upstream", "expectedProviderName", "reasoningControl", "supportsReasoningReplay", "effortMap", "reasoningBudgets"];

    /// <inheritdoc />
    public void Validate(ModelProviderConfiguration provider)
    {
        if (provider is not OpenRouterProviderConfiguration configured)
        {
            throw new ArgumentException("Incompatible OpenRouter provider configuration.", nameof(provider));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(configured.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(configured.Name);
        if (configured.SecretKeyReference is not { Length: > 8 } secret
            || !secret.StartsWith("secrets:", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("OpenRouter requires a nonempty secrets: reference.");
        }

        ModelImages.ValidateConfiguration(configured);
        var ids = new HashSet<ModelProfileId>();
        foreach (var entry in configured.Models)
        {
            if (entry is not OpenRouterModelConfiguration model || model.Id == default || !ids.Add(model.Id))
            {
                throw new InvalidOperationException("OpenRouter contains an incompatible or duplicate model.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(model.Name);
            if (string.IsNullOrWhiteSpace(model.ModelId) || model.ModelId.Length > 256
                || model.ModelId.Split('/').Length != 2 || model.ModelId.Any(char.IsWhiteSpace)
                || model.ModelId.Contains(':') || model.ModelId.StartsWith("openrouter/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("OpenRouter requires an exact model slug without dynamic routing variants.");
            }

            OpenAiCompatibleProviderRegistration.ValidateModelLimits(model, false);
            if ((model.Capabilities.StructuredOutput && !model.JsonSchemaResponses)
                || ((model.StrictToolArguments || model.ParallelToolCalls) && !model.Capabilities.ToolCalls))
            {
                throw new InvalidOperationException("OpenRouter capability declarations do not match their protocol support metadata.");
            }

            if (model.MinimumCacheableTokens is <= 0 || !Enum.IsDefined(model.ReasoningControl)
                || model.ReasoningControl is not (ReasoningControllability.Selectable or ReasoningControllability.AlwaysOn or ReasoningControllability.Unsupported)
                || (model.Upstream is { } upstream && (string.IsNullOrWhiteSpace(upstream) || upstream.Length > 128 || upstream.Any(char.IsControl)))
                || (model.ExpectedProviderName is { } expected && (expected.Length > 128 || expected.Any(char.IsControl)))
                || (model.SupportsReasoningReplay && (model.Upstream is null || string.IsNullOrWhiteSpace(model.ExpectedProviderName) || model.ReasoningControl == ReasoningControllability.Unsupported)))
            {
                throw new InvalidOperationException("OpenRouter reasoning replay requires a supported mode and pinned upstream.");
            }

            var enabled = model.SupportedReasoningLevels.Where(level => level != ReasoningLevel.None).ToArray();
            if (model.ReasoningControl == ReasoningControllability.Selectable)
            {
                var effort = model.EffortMap;
                var budgets = model.ReasoningBudgets;
                if (enabled.Length == 0 || (effort.Count > 0) == (budgets.Count > 0)
                    || (effort.Count > 0 && (effort.Count != enabled.Length || enabled.Any(level => !effort.ContainsKey(level))
                        || effort.Values.Any(value => value is not ("minimal" or "low" or "medium" or "high" or "xhigh" or "max"))))
                    || (budgets.Count > 0 && (budgets.Count != enabled.Length || enabled.Any(level => !budgets.ContainsKey(level))
                        || budgets.Values.Any(value => value <= 0 || value >= model.EffectiveRequestOutputTokenReserve))))
                {
                    throw new InvalidOperationException("OpenRouter reasoning levels require complete effort or budget mappings with visible output headroom.");
                }
            }
            else if (enabled.Length != 0 || model.EffortMap.Count != 0 || model.ReasoningBudgets.Count != 0)
            {
                throw new InvalidOperationException("Uncontrollable OpenRouter reasoning cannot advertise selectable mappings.");
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ModelProfile> CreateProfiles(ModelProviderConfiguration provider)
    {
        Validate(provider);
        return provider.Models.Cast<OpenRouterModelConfiguration>().Where(model => model.Enabled)
            .Select(model => OpenAiCompatibleProviderRegistration.CreateProfile(model, model.ModelId, Endpoint, TypeDiscriminator, provider.SecretKeyReference, new EffectiveReasoningCapability
            {
                Controllability = model.ReasoningControl,
                SupportedLevels = model.ReasoningControl == ReasoningControllability.Selectable ? model.SupportedReasoningLevels : [],
                DefaultLevel = model.ReasoningControl == ReasoningControllability.Selectable ? model.DefaultReasoningLevel : null,
                SupportsReasoningOff = model.ReasoningControl == ReasoningControllability.Selectable ? model.SupportedReasoningLevels.Contains(ReasoningLevel.None) : null,
                RequestMode = "OpenRouter",
                ResponseMode = "ReasoningDetails",
                SchemaVersion = 1,
                Provenance = $"{provider.Id}/{model.Id.Value:D}",
            })).ToArray();
    }

    /// <inheritdoc />
    public IModelProvider CreateProvider(ModelProviderActivationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ProviderConfiguration is not OpenRouterProviderConfiguration provider
            || context.ModelConfiguration is not OpenRouterModelConfiguration model
            || context.Profile.Endpoint != Endpoint || string.IsNullOrWhiteSpace(context.ResolvedSecret))
        {
            throw new ModelProviderException("OpenRouter activation requires its fixed endpoint, typed configuration, and resolved secret.");
        }

        return new OpenRouterModelProvider(context.HttpClient, context.Profile, context.ResolvedSecret, provider.Id, model);
    }

    /// <inheritdoc />
    public ModelRequestPreparationResult Prepare(ModelRequestPreparationContext context)
    {
        if (context.ModelConfiguration is not OpenRouterModelConfiguration model)
        {
            throw new ModelProviderException("Incompatible OpenRouter preparation configuration.");
        }

        var prepared = ProviderResponseReplay.Prepare(context);
        var capabilities = CacheCapabilities(model);
        var plan = CachePlan(context.Request, model);
        var bytes = OpenRouterModelProvider.PrepareWire(context.Request with { CachePlan = plan, CacheCapabilities = capabilities }, context.Profile, model);
        try
        {
            var imageTokens = context.Request.Messages.SelectMany(message => message.Content)
                .Where(part => part.IsModelVisible && part.Image is not null)
                .Sum(part => part.Image is { } image ? ModelImages.EstimateTokens(image) : 0);

            // Only add hidden-output allowance beyond the replay bytes already counted in the wire.
            long retained = 0;
            foreach (var envelope in context.Request.TransientState?.Responses ?? [])
            {
                var payload = envelope.CopyProtocolPayload();
                try
                {
                    using var document = JsonDocument.Parse(payload);
                    var serializedResponseBytes = System.Text.Encoding.UTF8.GetByteCount(document.RootElement.GetProperty("response").GetRawText());
                    retained = checked(retained + Math.Max(0, envelope.RetainedOutputTokens - serializedResponseBytes));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }
            }

            var total = Math.Min(int.MaxValue, bytes.LongLength + imageTokens + retained + 256);
            return prepared with
            {
                WireDigest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    Wire = Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    prepared.WireDigest,
                    model,
                    context.ProviderConfiguration.Id,
                    Images = context.Request.Messages.SelectMany(message => message.Content).Select(part => part.Image),
                }))),
                WireEstimate = prepared.WireEstimate with
                {
                    WireInputTokens = (int)total,
                    Components = [new("openrouter-wire", "Provider overhead/replay", "OpenRouter wire", "Request framing", total - retained), new("retained-allowance", "Provider overhead/replay", "Retained output allowance", "Capacity allowances", retained)],
                    StablePrefixComponentCount = 0,
                    EstimationBasis = "Conservative serialized UTF-8 bytes plus dimension-based image tokens, framing, and retained output allowance.",
                },
                CacheCapabilities = capabilities,
                CachePlan = plan,
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>Declares one conservative explicit tail breakpoint for configured upstreams.</summary>
    internal static ModelCacheCapabilities CacheCapabilities(OpenRouterModelConfiguration model) => new()
    {
        AutomaticPrefixCaching = model.AutomaticPrefixCaching,
        ReportsCachedTokens = true,
        ExplicitCacheControl = model.MinimumCacheableTokens is > 0,
        MaximumBreakpoints = model.MinimumCacheableTokens is > 0 ? 1 : 0,
        MinimumCacheablePrefixTokens = model.MinimumCacheableTokens ?? 0,
    };

    /// <summary>Reuses host cache admission and projects a single movable tail marker.</summary>
    internal static ModelCachePlan CachePlan(ModelStreamRequest request, OpenRouterModelConfiguration model)
    {
        var capabilities = CacheCapabilities(model);
        var layout = request.Layout;
        if (layout is null || !capabilities.ExplicitCacheControl)
        {
            return new ModelCachePlan([]);
        }

        var toolEstimate = request.ToolTransportMode == ToolTransportMode.Text
            ? new ModelWireToolEstimate { TextToolTokens = request.WireEstimate?.TextToolTokens ?? throw new ModelProviderException("Missing text-tool capacity estimate.") }
            : ModelWireEstimator.EstimateTools(ModelToolCanonicalizer.Canonicalize(request.Tools), request.ToolTransportMode);
        var stableTokens = ModelWireEstimator.Estimate(request.Messages, toolEstimate, layout.StablePrefixMessageCount, request.MaximumOutputTokens ?? model.EffectiveRequestOutputTokenReserve, request.ProviderInstructions).StablePrefixTokens;
        var plan = ModelCachePlanner.CreatePlan(layout, capabilities, stableTokens);
        return new ModelCachePlan(plan.Breakpoints.Count > 0 ? [new(ModelCacheBreakpointClass.RequestTail, -1)] : []);
    }
}
