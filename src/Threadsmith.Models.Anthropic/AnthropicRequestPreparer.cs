namespace Threadsmith.Models.Anthropic;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;
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

        // Keep reusable instructions and tools, then move the final breakpoint with the conversation.
        // Legacy planner indices are never treated as native block indices.
        if (capabilities.ExplicitCacheControl && stableBytes / 4 >= capabilities.MinimumCacheablePrefixTokens)
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
                if (candidate.Item1 == ModelCacheBreakpointClass.ToolInventory ? request.Tools.Count > 0 : stableSections.ContainsKey(candidate.Item2))
                {
                    breakpoints.Add(new ModelCacheBreakpoint(candidate.Item1, -1));
                }
            }

            if (historyEnd is { } index)
            {
                breakpoints.Add(new ModelCacheBreakpoint(ModelCacheBreakpointClass.ConversationHistory, index));
            }
            else if (stableSections.ContainsKey("phase-policy"))
            {
                breakpoints.Add(new ModelCacheBreakpoint(ModelCacheBreakpointClass.PhasePolicy, -1));
            }

            breakpoints.Add(new ModelCacheBreakpoint(ModelCacheBreakpointClass.RequestTail, -1));
        }

        var plan = new ModelCachePlan(breakpoints.AsReadOnly());
        var projected = request with { CacheCapabilities = capabilities, CachePlan = plan };
        var attribution = new Dictionary<JsonNode, ModelMessage>(ReferenceEqualityComparer.Instance);
        var body = AnthropicRequestMapper.CreateBody(projected, profile, compatibility, attribution);
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
                    Components = Attribute(body, bytes, attribution, request, framing, retained, total),
                    EstimationBasis = "Conservative provider capacity: one token per serialized UTF-8 byte, plus framing and retained-output allowances. Separate fields have no exposed cross-field prompt order.",
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

    private static IReadOnlyList<ContextUsageComponent> Attribute(JsonObject body, byte[] bytes, Dictionary<JsonNode, ModelMessage> attribution, ModelStreamRequest request, int framing, long retained, long total)
    {
        using var document = JsonDocument.Parse(bytes);
        var components = new List<ContextUsageComponent>();
        var toolNames = ModelToolCanonicalizer.Canonicalize(request.Tools).Select(tool => tool.Name).ToArray();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (body[property.Name] is { } node)
            {
                Visit(node, property.Value, property.Name);
            }
        }

        var remainder = bytes.LongLength - components.Sum(item => item.Tokens);
        components.Add(new("wire-framing", "Provider overhead/replay", "Serialization / request settings", "Request framing", remainder));
        components.Add(new("framing-allowance", "Provider overhead/replay", "Provider framing allowance", "Capacity allowances", framing));
        components.Add(new("retained-allowance", "Provider overhead/replay", "Retained output allowance", "Capacity allowances", retained));

        // Saturated admission estimates cannot be honestly subdivided into an unsaturated inventory.
        return components.Sum(item => item.Tokens) == total ? components.AsReadOnly() : [];

        void Visit(JsonNode node, JsonElement element, string container)
        {
            if (attribution.TryGetValue(node, out var message))
            {
                var content = message.GetModelVisibleContent();
                components.Add(ContextUsageAttribution.Message(
                    message,
                    components.Count,
                    container,
                    Encoding.UTF8.GetByteCount(element.GetRawText()),
                    length => JsonEncodedText.Encode(content.AsSpan(0, length)).EncodedUtf8Bytes.Length));
            }
            else if (container == "tools" && element.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var tool in element.EnumerateArray())
                {
                    components.Add(new($"tools:{index}", "Tools", toolNames[index], container, Encoding.UTF8.GetByteCount(tool.GetRawText())));
                    index++;
                }
            }
            else if (container == "output_config")
            {
                components.Add(new("output-config", "Output contract", "Output format / reasoning settings", container, Encoding.UTF8.GetByteCount(element.GetRawText())));
            }
            else if (node is JsonArray array)
            {
                var index = 0;
                foreach (var child in element.EnumerateArray())
                {
                    if (array[index++] is { } childNode)
                    {
                        Visit(childNode, child, container);
                    }
                }
            }
            else if (node is JsonObject obj && container is "messages" or "system")
            {
                foreach (var child in element.EnumerateObject())
                {
                    if (obj[child.Name] is { } childNode)
                    {
                        Visit(childNode, child.Value, container);
                    }
                }
            }
        }
    }
}
