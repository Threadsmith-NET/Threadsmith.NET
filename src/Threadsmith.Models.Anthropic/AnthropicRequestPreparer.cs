namespace Threadsmith.Models.Anthropic;

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
        request = request with { ImageData = new Dictionary<string, ReadOnlyMemory<byte>>() };
        AnthropicReplayIdentity.Validate(request, profile, compatibility, providerId);
        var capabilities = new ModelCacheCapabilities
        {
            ExplicitCacheControl = compatibility.PromptCachingEnabled && compatibility.MinimumCacheableTokens is > 0,
            MaximumBreakpoints = 4,
            MinimumCacheablePrefixTokens = compatibility.MinimumCacheableTokens ?? 0,
            ReportsCachedTokens = true,
        };
        var messages = request.Messages.Count > 0 ? request.Messages
            : [new ModelMessage { Role = ModelMessageRole.User, SectionId = "legacy-input", Content = [new() { Content = request.Input }] }];
        var stableMessageCount = request.Layout?.StablePrefixMessageCount
            ?? request.Messages.TakeWhile(message => message.Role is ModelMessageRole.System or ModelMessageRole.Developer).Count();
        var instructions = request.ProviderInstructions is { } supplied && request.Messages.Any(message => message.SectionId == supplied.SectionId)
            ? null : request.ProviderInstructions;
        var estimate = ProviderResponseReplay.IncludeAllowance(request, ModelWireEstimator.Estimate(
            messages,
            ModelToolCanonicalizer.Canonicalize(request.Tools),
            ToolTransportMode.Native,
            stableMessageCount,
            request.MaximumOutputTokens ?? profile.EffectiveRequestOutputTokenReserve,
            instructions));
        var stableSections = request.Messages.TakeWhile(message => message.Role is ModelMessageRole.System or ModelMessageRole.Developer)
            .Where(message => message.Content.Any(part => part.IsModelVisible))
            .Select(message => message.SectionId).ToHashSet(StringComparer.Ordinal);
        var breakpoints = new List<ModelCacheBreakpoint>();

        // Keep reusable instructions and tools, then move the final breakpoint with the conversation.
        // Legacy planner indices are never treated as native block indices.
        if (capabilities.ExplicitCacheControl && estimate.StablePrefixTokens >= capabilities.MinimumCacheablePrefixTokens)
        {
            var historyEnd = request.Messages.Select((message, index) => (message, index))
                .Where(item => item.message.SectionId is "recent-user" or "recent-assistant"
                    && item.message.Role is ModelMessageRole.User or ModelMessageRole.Assistant
                    && item.message.Content.Any(part => part.IsModelVisible))
                .Select(item => (int?)item.index).LastOrDefault();
            foreach (var candidate in new[]
            {
                (ModelCacheBreakpointClass.ToolInventory, string.Empty),
                (ModelCacheBreakpointClass.RepositoryInstructions, "repository-instructions"),
            })
            {
                if (candidate.Item1 == ModelCacheBreakpointClass.ToolInventory ? request.Tools.Count > 0 : stableSections.Contains(candidate.Item2))
                {
                    breakpoints.Add(new ModelCacheBreakpoint(candidate.Item1, -1));
                }
            }

            if (historyEnd is { } index)
            {
                breakpoints.Add(new ModelCacheBreakpoint(ModelCacheBreakpointClass.ConversationHistory, index));
            }
            else if (stableSections.Contains("conversation-policy"))
            {
                breakpoints.Add(new ModelCacheBreakpoint(ModelCacheBreakpointClass.PhasePolicy, -1));
            }

            breakpoints.Add(new ModelCacheBreakpoint(ModelCacheBreakpointClass.RequestTail, -1));
        }

        var plan = new ModelCachePlan(breakpoints.AsReadOnly());
        var projected = request with { CacheCapabilities = capabilities, CachePlan = plan };
        var body = AnthropicRequestMapper.CreateBody(projected, profile, compatibility);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        try
        {
            // Native schema projection may expand the supplied schema. Count the projected output contract in token units.
            var outputTokens = ((long)(body["output_config"]?.ToJsonString().Length ?? 0) + 3) / 4;
            var framing = FramingAllowanceTokens + (request.ResponseFormat is null ? 0 : StructuredOutputAllowanceTokens);
            var estimatedTotal = estimate.WireInputTokens + outputTokens + framing;
            var total = Math.Min(int.MaxValue, estimatedTotal);
            return new ModelRequestPreparationResult
            {
                RequiresInitialInstructionPrefix = true,
                WireDigest = AnthropicRequestMapper.Hash(bytes),
                CacheCapabilities = capabilities,
                CachePlan = plan,
                WireEstimate = estimate with
                {
                    WireInputTokens = (int)total,
                    Components = total == estimatedTotal
                        ? [.. estimate.Components,
                            new("output-config", "Output contract", "Output format / reasoning settings", "Native output contract", outputTokens),
                            new("anthropic-framing", "Provider overhead/replay", "Anthropic framing allowance", "Capacity allowances", framing)] : [],
                    StablePrefixComponentCount = total == estimatedTotal ? estimate.StablePrefixComponentCount : 0,
                    FramingTokens = estimate.FramingTokens + framing,
                    EstimationBasis = estimate.EstimationBasis + " Includes Anthropic framing and projected output-contract token allowances; transport bytes are not tokens.",
                },
            };
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
