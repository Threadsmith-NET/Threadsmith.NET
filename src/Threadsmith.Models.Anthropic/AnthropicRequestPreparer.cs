namespace Threadsmith.Models.Anthropic;

using System.Text;
using System.Text.Json;
using Threadsmith.Models;

/// <summary>Prepares cache boundaries and conservative native capacity before credential activation.</summary>
internal static class AnthropicRequestPreparer
{
    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal const int FramingAllowanceTokens = 256;

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal const int StructuredOutputAllowanceTokens = 512;

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal static ModelRequestPreparationResult Prepare(ModelRequestPreparationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ProviderConfiguration is not AnthropicProviderConfiguration provider
            || context.ModelConfiguration is not AnthropicModelConfiguration model)
        {
            throw new ModelProviderException("Anthropic preparation received incompatible configuration.");
        }

        return Prepare(context.Request, context.Profile, model.Compatibility, provider.Id);
    }

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal static ModelRequestPreparationResult Prepare(ModelStreamRequest request, ModelProfile profile, AnthropicModelCompatibility compatibility, string providerId)
    {
        AnthropicReplayIdentity.Validate(request, profile, compatibility, providerId);
        var capabilities = new ModelCacheCapabilities
        {
            ExplicitCacheControl = compatibility.PromptCachingEnabled && compatibility.MinimumCacheableTokens is > 0,
            MaximumBreakpoints = 4,
            MinimumCacheablePrefixTokens = compatibility.MinimumCacheableTokens ?? 0,
            ReportsCachedTokens = true,
        };
        var stableSections = request.Messages.TakeWhile(message => message.Role is ModelMessageRole.System or ModelMessageRole.Developer)
            .Where(message => message.Content.Any(part => part.IsModelVisible))
            .GroupBy(message => message.SectionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Sum(message => Encoding.UTF8.GetByteCount(message.GetModelVisibleContent())), StringComparer.Ordinal);
        var toolBytes = request.Tools.Sum(tool => Encoding.UTF8.GetByteCount(tool.ArgumentsJsonSchema)
            + Encoding.UTF8.GetByteCount(tool.Name) + Encoding.UTF8.GetByteCount(tool.Description));
        var stableBytes = stableSections.Values.Sum() + toolBytes;
        var breakpoints = new List<ModelCacheBreakpoint>();

        // Only known stable prefix sections qualify. Legacy planner indices are never treated as native block indices.
        if (capabilities.ExplicitCacheControl && stableBytes / 4 >= capabilities.MinimumCacheablePrefixTokens)
        {
            foreach (var candidate in new[]
            {
                (ModelCacheBreakpointClass.ToolInventory, string.Empty),
                (ModelCacheBreakpointClass.HostPolicy, "host-policy"),
                (ModelCacheBreakpointClass.RepositoryInstructions, "repository-instructions"),
                (ModelCacheBreakpointClass.PhasePolicy, "phase-policy"),
            })
            {
                if (candidate.Item1 == ModelCacheBreakpointClass.ToolInventory ? request.Tools.Count > 0 : stableSections.ContainsKey(candidate.Item2))
                {
                    breakpoints.Add(new ModelCacheBreakpoint(candidate.Item1, -1));
                }
            }
        }

        var plan = new ModelCachePlan(breakpoints.AsReadOnly());
        var projected = request with { CacheCapabilities = capabilities, CachePlan = plan };
        var body = AnthropicRequestMapper.CreateBody(projected, profile, compatibility);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        try
        {
            var framing = FramingAllowanceTokens + (request.ResponseFormat is null ? 0 : StructuredOutputAllowanceTokens);
            var retained = request.TransientState?.RetainedOutputTokens ?? 0;
            var total = Math.Min(int.MaxValue, (long)bytes.Length + framing + retained);

            // One token per UTF-8 byte is deliberately conservative until an exact model tokenizer is reviewed.
            // Signed bytes additionally retain reported output usage: ciphertext length does not measure hidden thinking.
            return new ModelRequestPreparationResult
            {
                RequiresInitialInstructionPrefix = true,
                WireDigest = AnthropicRequestMapper.Hash(bytes),
                CacheCapabilities = capabilities,
                CachePlan = plan,
                WireEstimate = new ModelWireEstimate
                {
                    LogicalTokens = request.WireEstimate?.LogicalTokens ?? bytes.Length,
                    WireInputTokens = (int)total,
                    StablePrefixTokens = stableBytes,
                    NativeToolTokens = toolBytes,
                    FramingTokens = framing,
                    ProviderInstructionTokens = Encoding.UTF8.GetByteCount(request.ProviderInstructions?.Content ?? string.Empty),
                    OutputReserveTokens = request.MaximumOutputTokens ?? profile.EffectiveRequestOutputTokenReserve,
                    SectionTokens = stableSections,
                },
            };
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }
}

