namespace Threadsmith.Models.Anthropic;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using global::Anthropic.Models.Messages;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Consumes SDK event unions and releases executable calls only after a complete valid response.</summary>
internal sealed class AnthropicStreamAdapter
{
    private const int MaximumContentBlocks = 4096;
    private readonly List<AnthropicResponseBlock> _blocks = [];
    private readonly ModelStreamRequest _request;
    private readonly ModelProfile _profile;
    private readonly AnthropicModelCompatibility _compatibility;
    private readonly string _providerId;
    private readonly string _apiKey;
    private readonly ModelToolWireNameMap _names;
    private readonly HashSet<string> _toolNames;
    private readonly HashSet<string> _wireIds = new(StringComparer.Ordinal);
    private readonly AnthropicUsageAccumulator _usage;
    private bool _started;
    private bool _stopped;
    private string? _stopReason;
    private int? _openBlock;

    /// <summary>Initializes a new instance of the <see cref="AnthropicStreamAdapter"/> class.</summary>
    internal AnthropicStreamAdapter(ModelStreamRequest request, ModelProfile profile, AnthropicModelCompatibility compatibility, string providerId, string apiKey)
    {
        _request = request;
        _profile = profile;
        _compatibility = compatibility;
        _providerId = providerId;
        _apiKey = apiKey;
        _names = ModelToolWireNameMap.Create(request.Tools);
        _toolNames = request.Tools.Select(tool => _names.ToWireName(tool.Name)).ToHashSet(StringComparer.Ordinal);
        _usage = new AnthropicUsageAccumulator(profile, compatibility, request.WireEstimate?.WireInputTokens ?? 0);
    }

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal IReadOnlyList<ModelChunk> Accept(RawMessageStreamEvent item)
    {
        var root = item.Json;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new MalformedModelOutputException("Anthropic emitted a non-object stream event.");
        }

        if (_stopped)
        {
            throw new MalformedModelOutputException("Anthropic emitted data after the terminal stream event.");
        }

        if (item.TryPickStart(out _))
        {
            var message = RequiredProperty(root, "message", JsonValueKind.Object);
            if (_started || String(message, "role") != "assistant")
            {
                throw new MalformedModelOutputException("Anthropic emitted an invalid message start.");
            }

            if (RequiredProperty(message, "content", JsonValueKind.Array).GetArrayLength() != 0)
            {
                throw new MalformedModelOutputException("Anthropic streaming message started with unsupported prefilled content.");
            }

            _started = true;
            UpdateUsage(message);
            return [];
        }

        if (!_started)
        {
            throw new MalformedModelOutputException("Anthropic stream has no initial message event.");
        }

        if (item.TryPickContentBlockStart(out _))
        {
            var index = Index(root);
            if (_stopReason is not null || _openBlock is not null || index != _blocks.Count || index >= MaximumContentBlocks)
            {
                throw new MalformedModelOutputException("Anthropic content blocks are out of order or exceed their count ceiling.");
            }

            var block = new AnthropicResponseBlock(RequiredProperty(root, "content_block", JsonValueKind.Object));
            if (block.Type == "tool_use")
            {
                if (!_toolNames.Contains(block.Name))
                {
                    throw new MalformedInvocationException("Anthropic returned a missing or unadvertised tool name.");
                }

                if (string.IsNullOrWhiteSpace(block.WireId) || block.WireId.Length > 256
                    || block.WireId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-'))
                    || !_wireIds.Add(block.WireId))
                {
                    throw new MalformedInvocationException("Anthropic returned invalid or duplicate tool identities.");
                }

                if (_profile.MaximumToolCalls > 0 && _wireIds.Count > _profile.MaximumToolCalls)
                {
                    throw new ModelProviderException("Anthropic response exceeded its tool-call ceiling.");
                }
            }

            _blocks.Add(block);
            _openBlock = index;
            return block.Type switch
            {
                "text" when block.VisibleText.Length > 0 => [new ModelChunk { Text = block.VisibleText }],
                "thinking" when _request.IncludeReasoningText == true && block.ThinkingText.Length > 0 => [new ModelChunk { Reasoning = block.ThinkingText, SuppressReasoningDiagnostics = true, IsDisplayOnlyReasoning = true }],
                _ => [],
            };
        }

        if (item.TryPickContentBlockDelta(out _))
        {
            var block = OpenBlock(root);
            var delta = RequiredProperty(root, "delta", JsonValueKind.Object);
            var type = String(delta, "type");
            var propertyName = type switch
            {
                "text_delta" => "text",
                "thinking_delta" => "thinking",
                "signature_delta" => "signature",
                "input_json_delta" => "partial_json",
                _ => throw new MalformedModelOutputException("Anthropic emitted an unsupported content delta."),
            };
            var content = String(delta, propertyName);
            block.Append(type, content);
            return type switch
            {
                "text_delta" when content.Length > 0 => [new ModelChunk { Text = content }],
                "thinking_delta" when _request.IncludeReasoningText == true && content.Length > 0 => [new ModelChunk { Reasoning = content, SuppressReasoningDiagnostics = true, IsDisplayOnlyReasoning = true }],
                _ => [],
            };
        }

        if (item.TryPickContentBlockStop(out _))
        {
            OpenBlock(root).Closed = true;
            _openBlock = null;
            return [];
        }

        if (item.TryPickDelta(out _))
        {
            if (_openBlock is not null)
            {
                throw new MalformedModelOutputException("Anthropic message ended with an unclosed content block.");
            }

            var delta = RequiredProperty(root, "delta", JsonValueKind.Object);
            if (delta.TryGetProperty("stop_reason", out var reason) && reason.ValueKind != JsonValueKind.Null)
            {
                if (_stopReason is not null || reason.ValueKind != JsonValueKind.String)
                {
                    throw new MalformedModelOutputException("Anthropic emitted duplicate or malformed stop reasons.");
                }

                _stopReason = reason.GetString();
            }

            UpdateUsage(root, _stopReason is not null);
            return [];
        }

        if (item.TryPickStop(out _))
        {
            if (_stopReason is null || _openBlock is not null)
            {
                throw new MalformedModelOutputException("Anthropic stream stopped before a complete terminal response.");
            }

            _stopped = true;
            return [];
        }

        throw new MalformedModelOutputException("Anthropic emitted an unsupported stream event.");
    }

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal IReadOnlyList<ModelChunk> Complete()
    {
        if (!_stopped || !_started || _stopReason is null)
        {
            throw new MalformedModelOutputException("Anthropic stream ended without message_stop; no tools were released.");
        }

        var finish = _stopReason switch
        {
            "end_turn" or "stop_sequence" => ModelFinishReason.Stop,
            "tool_use" => ModelFinishReason.ToolCalls,
            "max_tokens" or "model_context_window_exceeded" => ModelFinishReason.Length,
            _ => ModelFinishReason.Other,
        };
        if (_usage.ReportedOutputTokens > (_request.MaximumOutputTokens ?? _profile.EffectiveRequestOutputTokenReserve))
        {
            throw new ModelProviderException("Anthropic reported output exceeded the admitted response ceiling.");
        }

        var usage = _usage.Snapshot(EstimatedOutput());
        if (finish == ModelFinishReason.Length)
        {
            throw new ModelProviderException("Anthropic stopped before completing the response because its token or context limit was reached.");
        }

        if (finish == ModelFinishReason.Other)
        {
            throw new ModelProviderException("Anthropic did not complete the request; it was refused, paused, or returned an unsupported terminal outcome.");
        }

        if (finish != ModelFinishReason.ToolCalls)
        {
            if (finish == ModelFinishReason.Stop && _wireIds.Count > 0)
            {
                throw new MalformedModelOutputException("Anthropic tool blocks did not receive a tool-use terminal outcome.");
            }

            if (finish == ModelFinishReason.Stop)
            {
                foreach (var block in _blocks)
                {
                    block.Complete();
                }
            }

            return [new ModelChunk { Usage = usage, FinishReason = finish }];
        }

        if (_request.AllowMultipleToolCalls == false && _wireIds.Count > 1)
        {
            throw new MalformedInvocationException("Anthropic returned multiple calls despite the host single-call policy.");
        }

        if (_wireIds.Count == 0)
        {
            throw new MalformedModelOutputException("Anthropic tool-use outcome contained no calls.");
        }

        var content = new JsonArray();
        var tools = new List<ModelChunk>();
        var ids = new List<string>();
        foreach (var block in _blocks)
        {
            var replay = block.Complete();
            content.Add(replay);
            if (block.Type == "tool_use")
            {
                var arguments = replay["input"]?.ToJsonString() ?? throw new MalformedInvocationException("Anthropic tool input was missing.");
                tools.Add(new ModelChunk { Output = new ToolRequestModelOutput(_names.ToCanonicalName(block.Name), arguments) });
                ids.Add(block.WireId);
            }
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["version"] = 1,
            ["history_count"] = _request.Messages.Count,
            ["history_digest"] = AnthropicReplayIdentity.HistoryDigest(_request.Messages),
            ["legacy_input_digest"] = _request.Messages.Count == 0 ? AnthropicRequestMapper.Hash(System.Text.Encoding.UTF8.GetBytes(_request.Input)) : null,
            ["assistant"] = new JsonObject { ["role"] = "assistant", ["content"] = content },
        });
        try
        {
            var maximumReplay = _profile.MaximumStreamedBytes == 0 ? ModelProfile.DefaultMaximumStreamedBytes : Math.Min(_profile.MaximumStreamedBytes, ModelProfile.DefaultMaximumStreamedBytes);
            if (payload.Length > maximumReplay - (_request.TransientState?.RetainedBytes ?? 0))
            {
                throw new ModelProviderException("Anthropic active tool-turn replay exceeded its retained-byte ceiling.");
            }

            var retained = usage.OutputTokens;
            var binding = AnthropicReplayIdentity.Create(_request, _profile, _compatibility, _providerId, _apiKey, payload);
            var envelope = new ModelResponseReplayEnvelope(binding, payload, ids, retained);
            return [new ModelChunk { ResponseEnvelope = envelope }, .. tools, new ModelChunk { Usage = usage, FinishReason = finish }];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal ModelUsage? PartialUsage() => _usage.HasReportedUsage ? _usage.Snapshot(EstimatedOutput()) : null;

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal static string String(JsonElement element, string property) => RequiredProperty(element, property, JsonValueKind.String).GetString()
        ?? throw new MalformedModelOutputException("Anthropic emitted null stream data.");

    private static JsonElement RequiredProperty(JsonElement element, string property, JsonValueKind kind) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value) && value.ValueKind == kind
            ? value : throw new MalformedModelOutputException("Anthropic emitted missing or invalid required stream data.");

    private static int Index(JsonElement element) => RequiredProperty(element, "index", JsonValueKind.Number).TryGetInt32(out var index) && index >= 0
        ? index : throw new MalformedModelOutputException("Anthropic emitted an invalid stream index.");

    private AnthropicResponseBlock OpenBlock(JsonElement root)
    {
        var index = Index(root);
        return _openBlock == index && index < _blocks.Count && !_blocks[index].Closed
            ? _blocks[index] : throw new MalformedModelOutputException("Anthropic emitted a delta or stop for a block that is not open.");
    }

    private void UpdateUsage(JsonElement root, bool finalOutput = false)
    {
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind != JsonValueKind.Null)
        {
            _usage.Update(usage, finalOutput);
        }
    }

    private long EstimatedOutput() => _request.ReasoningLevel != ReasoningLevel.None
        ? _request.MaximumOutputTokens ?? _profile.EffectiveRequestOutputTokenReserve
        : _blocks.Sum(block => block.EstimatedOutputTokens);
}
