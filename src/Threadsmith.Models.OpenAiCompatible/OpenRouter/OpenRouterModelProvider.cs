namespace Threadsmith.Models.OpenAiCompatible.OpenRouter;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>OpenRouter wire differences on the shared, nonvirtual transport and model lifecycle.</summary>
internal sealed class OpenRouterModelProvider : OpenAiCompatibleModelProvider
{
    private readonly OpenRouterModelConfiguration _model;

    /// <summary>Initializes a new instance of the <see cref="OpenRouterModelProvider"/> class.</summary>
    internal OpenRouterModelProvider(HttpClient client, ModelProfile profile, string secret, string providerId, OpenRouterModelConfiguration model)
        : base(client, profile, secret)
    {
        _model = model;
        ReplayProtocol = "openrouter-v1:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { providerId, model })));
    }

    private OpenRouterModelProvider(ModelProfile profile, OpenRouterModelConfiguration model)
        : base(profile)
    {
        _model = model;
        ReplayProtocol = "openrouter-projection";
    }

    /// <summary>Uses dispatch projection with size-only image placeholders before credentials are resolved.</summary>
    internal static byte[] PrepareWire(ModelStreamRequest request, ModelProfile profile, OpenRouterModelConfiguration model)
    {
        var images = request.Messages.SelectMany(message => message.Content)
            .Where(part => part.IsModelVisible && part.Image is not null)
            .Select(part => part.Image ?? throw new ModelProviderException("Missing image metadata."))
            .DistinctBy(image => image.Id).ToDictionary(image => image.Id, _ => ReadOnlyMemory<byte>.Empty);
        return new OpenRouterModelProvider(profile, model).ProjectWireRequest(request with { ImageData = images }).ToArray();
    }

    /// <summary>Shared deterministic controls for preparation and serialization.</summary>
    internal static void ProjectControls(JsonObject body, ModelStreamRequest request, OpenRouterModelConfiguration model)
    {
        if (!model.SupportedReasoningLevels.Contains(request.ReasoningLevel))
        {
            throw new ModelProviderException("The selected reasoning level is unsupported by this OpenRouter profile.");
        }

        if (model.ReasoningControl == ReasoningControllability.Selectable)
        {
            var reasoning = new JsonObject();
            if (request.ReasoningLevel == ReasoningLevel.None)
            {
                reasoning["enabled"] = false;
            }
            else if (model.ReasoningBudgets.TryGetValue(request.ReasoningLevel, out var budget))
            {
                if (budget >= (request.MaximumOutputTokens ?? model.EffectiveRequestOutputTokenReserve))
                {
                    throw new ModelProviderException("Reasoning budget leaves no visible completion headroom.");
                }

                reasoning["max_tokens"] = budget;
            }
            else
            {
                reasoning["effort"] = model.EffortMap[request.ReasoningLevel];
            }

            body["reasoning"] = reasoning;
        }
        else if (model.ReasoningControl == ReasoningControllability.AlwaysOn)
        {
            body["reasoning"] = new JsonObject { ["enabled"] = true };
        }

        body["session_id"] = (request.CacheAffinityId ?? request.RunId.Value).ToString("D");
        body["provider"] = new JsonObject
        {
            ["require_parameters"] = true,
            ["allow_fallbacks"] = model.Upstream is null,
        };
        if (model.Upstream is { } upstream)
        {
            body["provider"]?["only"] = new JsonArray(upstream);
            body["provider"]?["order"] = new JsonArray(upstream);
        }
    }

    /// <inheritdoc />
    protected override bool HasRequestProjection => true;

    /// <inheritdoc />
    protected override bool RequiresDoneMarker => true;

    /// <inheritdoc />
    protected override int DefaultOutputCeiling => _model.EffectiveRequestOutputTokenReserve;

    /// <inheritdoc />
    protected override bool SupportsStrictArguments => _model.StrictToolArguments;

    /// <inheritdoc />
    protected override string ProviderFamily => "openrouter";

    /// <inheritdoc />
    protected override string ReplayProtocol { get; }

    /// <inheritdoc />
    protected override bool SupportsReasoningReplay => _model.SupportsReasoningReplay;

    /// <inheritdoc />
    protected override ReasoningAccumulator CreateReasoningAccumulator() => new OpenRouterReasoningAccumulator();

    /// <inheritdoc />
    protected override void ProjectRequest(JsonObject body, ModelStreamRequest request)
    {
        body["max_tokens"] = body["max_completion_tokens"]?.DeepClone();
        body.Remove("max_completion_tokens");
        body.Remove("reasoning_effort");
        body.Remove("prompt_cache_key");
        ProjectControls(body, request, _model);
        if (!_model.SeedSupported)
        {
            body.Remove("seed");
        }

        if (!_model.ParallelToolCalls)
        {
            body.Remove("parallel_tool_calls");
        }

        if (OpenRouterProviderRegistration.CachePlan(request, _model).Breakpoints.Count > 0
            && body["messages"] is JsonArray messages && messages.LastOrDefault() is JsonObject tail)
        {
            if (tail["content"] is JsonValue text && text.TryGetValue<string>(out var content))
            {
                tail["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = content });
            }

            if (tail["content"] is JsonArray blocks && blocks.LastOrDefault() is JsonObject last)
            {
                last["cache_control"] = new JsonObject { ["type"] = "ephemeral" };
            }
        }

        if (request.ResponseFormat is { } format && request.Tools.Count == 0)
        {
            if (!_model.JsonSchemaResponses)
            {
                throw new ModelProviderException("This OpenRouter profile does not support JSON-schema responses.");
            }

            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = format.SchemaId, ["strict"] = true, ["schema"] = JsonNode.Parse(format.JsonSchema) },
            };
        }
    }

    /// <inheritdoc />
    protected override void ProjectReplay(JsonObject response)
    {
        if (response["reasoning_details"] is null)
        {
            response["reasoning"] = response["reasoning_content"]?.DeepClone();
        }

        response.Remove("reasoning_content");
    }

    /// <inheritdoc />
    protected override void ValidateResponseIdentity(string? model, string? provider)
    {
        if ((model is not null && model != _model.ModelId)
            || (_model.ExpectedProviderName is not null && provider is not null && !string.Equals(provider, _model.ExpectedProviderName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new MalformedModelOutputException("OpenRouter returned an unexpected model or upstream identity.");
        }
    }

    /// <inheritdoc />
    protected override ModelUsage NormalizeUsage(ModelUsage usage, JsonElement? charge)
    {
        usage = usage with { EstimatedCost = Math.Max(usage.EstimatedCost, _model.Cost.CalculateUsage(usage)) };
        if (charge is null || charge.Value.ValueKind == JsonValueKind.Null)
        {
            return usage;
        }

        if (charge.Value.ValueKind != JsonValueKind.Number || !charge.Value.TryGetDecimal(out var cost) || cost < 0)
        {
            throw new MalformedModelOutputException("OpenRouter returned invalid charge metadata.");
        }

        return usage with { ReportedCost = cost, CostProvenance = "openrouter:usage.cost" };
    }

    /// <summary>Accumulates ordered detail blocks without exposing signatures or opaque data.</summary>
    private sealed class OpenRouterReasoningAccumulator : ReasoningAccumulator
    {
        private readonly List<ReasoningBlock> _blocks = [];
        private readonly System.Text.StringBuilder _plain = new();
        private bool _hasDisplayDetails;
        private bool _requiresPrivateReplay;
        private int _plainOffset;
        private long _bytes;

        /// <inheritdoc />
        public override bool RequiresPrivateReplay => _requiresPrivateReplay;

        /// <inheritdoc />
        public override string? Append(JsonElement? details, string? text)
        {
            if (details is null || (details.Value.ValueKind == JsonValueKind.Array && details.Value.GetArrayLength() == 0))
            {
                if (!_hasDisplayDetails)
                {
                    _plain.Append(text);
                }

                return _hasDisplayDetails ? null : text;
            }

            if (details.Value.ValueKind != JsonValueKind.Array)
            {
                throw new MalformedModelOutputException("OpenRouter reasoning details must be an array.");
            }

            _bytes += System.Text.Encoding.UTF8.GetByteCount(details.Value.GetRawText());
            if (_bytes > 1024 * 1024)
            {
                throw new MalformedModelOutputException("OpenRouter reasoning details exceed the private replay bound.");
            }

            var display = new System.Text.StringBuilder();
            foreach (var element in details.Value.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw new MalformedModelOutputException("OpenRouter reasoning details exceed the supported structure.");
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name is "type" or "id" or "format" or "text" or "summary" or "signature" or "data"
                        && property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    {
                        throw new MalformedModelOutputException("Invalid OpenRouter reasoning scalar.");
                    }

                    if (property.Name == "index" && property.Value.ValueKind != JsonValueKind.Null
                        && (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out _)))
                    {
                        throw new MalformedModelOutputException("Invalid OpenRouter reasoning index.");
                    }
                }

                var fragment = JsonNode.Parse(element.GetRawText())?.AsObject()
                    ?? throw new MalformedModelOutputException("Missing OpenRouter reasoning block.");
                var type = fragment["type"]?.GetValue<string>();
                if (type is not ("reasoning.text" or "reasoning.summary" or "reasoning.encrypted"))
                {
                    throw new MalformedModelOutputException("Unsupported OpenRouter reasoning block type.");
                }

                _requiresPrivateReplay |= type == "reasoning.encrypted" || fragment["signature"] is not null;
                var index = fragment["index"]?.GetValue<int>();
                if (index is < 0)
                {
                    throw new MalformedModelOutputException("Invalid OpenRouter reasoning block index.");
                }

                var id = fragment["id"]?.GetValue<string>();
                var existing = index is not null
                    ? _blocks.FirstOrDefault(block => block.Metadata["index"]?.GetValue<int>() == index)
                    : id is not null ? _blocks.FirstOrDefault(block => block.Metadata["id"]?.GetValue<string>() == id) : null;
                if (existing is null)
                {
                    if (_blocks.Count >= 128)
                    {
                        throw new MalformedModelOutputException("OpenRouter reasoning contains too many blocks.");
                    }

                    existing = new ReasoningBlock();
                    _blocks.Add(existing);
                }

                existing.Append(fragment);

                if (type == "reasoning.text")
                {
                    display.Append(fragment["text"]?.GetValue<string>());
                }
                else if (type == "reasoning.summary")
                {
                    display.Append(fragment["summary"]?.GetValue<string>());
                }
            }

            if (display.Length == 0)
            {
                if (!_hasDisplayDetails)
                {
                    _plain.Append(text);
                    return text;
                }

                return null;
            }

            _hasDisplayDetails = true;
            var displayed = display.ToString();
            var overlap = 0;
            while (overlap < displayed.Length && _plainOffset < _plain.Length
                && displayed[overlap] == _plain[_plainOffset])
            {
                overlap++;
                _plainOffset++;
            }

            if (overlap < displayed.Length)
            {
                _plainOffset = _plain.Length;
            }

            return overlap < displayed.Length ? displayed[overlap..] : null;
        }

        /// <inheritdoc />
        public override JsonElement? Complete() => _blocks.Count > 0
            ? JsonSerializer.SerializeToElement(_blocks.Select(block => block.Complete())) : null;

        /// <summary>Keeps block identity separate from incrementally buffered replay fields.</summary>
        private sealed class ReasoningBlock
        {
            private readonly Dictionary<string, StringBuilder> _fields = new(StringComparer.Ordinal);

            /// <summary>Preserves metadata and field presence in their original order.</summary>
            public JsonObject Metadata { get; } = [];

            /// <summary>Validates stable metadata and appends payload fragments without copying their prefixes.</summary>
            public void Append(JsonObject fragment)
            {
                foreach (var pair in fragment)
                {
                    if (pair.Key is "text" or "summary" or "data" or "signature")
                    {
                        Metadata.TryAdd(pair.Key, null);
                        if (pair.Value is not null)
                        {
                            if (!_fields.TryGetValue(pair.Key, out var buffer))
                            {
                                buffer = new StringBuilder();
                                _fields.Add(pair.Key, buffer);
                            }

                            buffer.Append(pair.Value.GetValue<string>());
                        }
                    }
                    else if (Metadata[pair.Key] is null)
                    {
                        Metadata[pair.Key] = pair.Value?.DeepClone();
                    }
                    else if (!JsonNode.DeepEquals(Metadata[pair.Key], pair.Value))
                    {
                        throw new MalformedModelOutputException("OpenRouter reasoning block identity changed.");
                    }
                }
            }

            /// <summary>Materializes buffered fields only when replay is completed.</summary>
            public JsonObject Complete()
            {
                foreach (var (field, buffer) in _fields)
                {
                    Metadata[field] = buffer.ToString();
                }

                return Metadata;
            }
        }
    }
}
