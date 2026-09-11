namespace Threadsmith.Models.Anthropic;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using global::Anthropic.Models.Messages;
using Threadsmith.Models;

/// <summary>Projects host authority and chronology into one deterministic native request.</summary>
internal static class AnthropicRequestMapper
{
    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal static MessageCreateParams Create(ModelStreamRequest request, ModelProfile profile, AnthropicModelCompatibility compatibility)
    {
        var body = CreateBody(request, profile, compatibility);
        return MessageCreateParams.FromRawUnchecked(new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement>(), body.ToDictionary(
            item => item.Key,
            item => JsonSerializer.SerializeToElement(item.Value),
            StringComparer.Ordinal));
    }

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal static JsonObject CreateBody(ModelStreamRequest request, ModelProfile profile, AnthropicModelCompatibility compatibility)
    {
        ValidateRequest(request, profile, compatibility);
        var body = new JsonObject
        {
            ["model"] = profile.ModelId,
            ["max_tokens"] = request.MaximumOutputTokens ?? profile.EffectiveRequestOutputTokenReserve,
            ["stream"] = true,
        };
        var names = ModelToolWireNameMap.Create(request.Tools);
        var system = new JsonArray();
        var sections = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var initial = true;
        foreach (var message in request.Messages)
        {
            if (!message.Content.Any(part => part.IsModelVisible))
            {
                continue;
            }

            if (message.Role is not (ModelMessageRole.System or ModelMessageRole.Developer))
            {
                initial = false;
                continue;
            }

            if (!initial)
            {
                throw new ModelProviderException("Anthropic requires system and developer instructions in the initial prefix; reassemble the request.");
            }

            var block = Text(message.GetModelVisibleContent());
            system.Add(block);
            sections[message.SectionId] = block;
        }

        if (request.ProviderInstructions is { Content.Length: > 0 } instructions)
        {
            var existing = request.Messages.Where(message => message.SectionId == instructions.SectionId).ToArray();
            if (existing.Length > 0 && (existing.Length != 1
                || existing[0].Role is not (ModelMessageRole.System or ModelMessageRole.Developer)
                || existing[0].GetModelVisibleContent() != instructions.Content))
            {
                throw new ModelProviderException("Anthropic provider instructions conflict with the canonical section.");
            }

            if (existing.Length == 0)
            {
                var block = Text(instructions.Content);
                system.Add(block);
                sections[instructions.SectionId] = block;
            }
        }

        if (system.Count > 0)
        {
            body["system"] = system;
        }

        var messageBlocks = new Dictionary<int, JsonObject>();
        body["messages"] = CreateMessages(request, names, messageBlocks);
        var output = new JsonObject();
        AddThinking(body, output, request, profile, compatibility);
        if (request.ResponseFormat is { } format)
        {
            if (!compatibility.SupportsStrictSchemas || format.SchemaVersion != 1 || string.IsNullOrWhiteSpace(format.SchemaId))
            {
                throw new ModelProviderException("Anthropic cannot honor the required final-response schema.");
            }

            var projected = AnthropicStrictSchema.ProjectRequired(format.JsonSchema);
            output["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = projected };
        }

        AddTools(body, request, compatibility, names, output);
        if (output.Count > 0)
        {
            body["output_config"] = output;
        }

        AddCacheControls(body, request, compatibility, sections, messageBlocks);
        return body;
    }

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal static string Digest(JsonObject body) => Hash(JsonSerializer.SerializeToUtf8Bytes(body));

    /// <summary>Provides the bounded native protocol operation or metadata for this adapter.</summary>
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static JsonArray CreateMessages(ModelStreamRequest request, ModelToolWireNameMap names, Dictionary<int, JsonObject> messageBlocks)
    {
        var messages = new JsonArray();
        var replay = request.TransientState?.Responses.ToDictionary(item => item.Binding.ModelRound) ?? [];
        request.TransientState?.ValidateHistory(request);
        var usedReplay = new HashSet<int>();
        for (var messageIndex = 0; messageIndex < request.Messages.Count; messageIndex++)
        {
            var message = request.Messages[messageIndex];
            if (message.Role is ModelMessageRole.System or ModelMessageRole.Developer)
            {
                continue;
            }

            if (message.ModelRound is { } requiredRound && message.Role is ModelMessageRole.Assistant or ModelMessageRole.Tool
                && !replay.ContainsKey(requiredRound))
            {
                throw new ModelProviderException("Anthropic active continuation lost its private response; start a fresh turn.");
            }

            if (message.Role == ModelMessageRole.Assistant && message.ModelRound is { } round && replay.TryGetValue(round, out var envelope))
            {
                if (usedReplay.Add(round))
                {
                    var bytes = envelope.CopyProtocolPayload();
                    try
                    {
                        if (AnthropicRequestMapper.Hash(bytes) != envelope.Binding.NormalizedRoundDigest)
                        {
                            throw new ModelProviderException("Anthropic private response content changed; start a fresh turn.");
                        }

                        var payload = JsonNode.Parse(bytes) as JsonObject
                            ?? throw new ModelProviderException("Anthropic private response is invalid.");
                        var count = payload["history_count"]?.GetValue<int>() ?? -1;
                        if (payload["version"]?.GetValue<int>() != 1 || count < 0 || count > request.Messages.Count || (count > 0 && messageIndex != count)
                            || payload["history_digest"]?.GetValue<string>() != AnthropicReplayIdentity.HistoryDigest(request.Messages.Take(count))
                            || envelope.Binding.ModelRound != replay.Keys.Order().ElementAt(usedReplay.Count - 1))
                        {
                            throw new ModelProviderException("Anthropic continuation preceding history changed; start a fresh turn.");
                        }

                        if (payload["legacy_input_digest"]?.GetValue<string>() is { } legacy
                            && (request.Messages.FirstOrDefault() is not { Role: ModelMessageRole.User } first
                                || legacy != Hash(Encoding.UTF8.GetBytes(first.GetModelVisibleContent()))))
                        {
                            throw new ModelProviderException("Anthropic legacy continuation lost its initial user input.");
                        }

                        messages.Add(payload["assistant"]?.DeepClone() as JsonObject
                            ?? throw new ModelProviderException("Anthropic private assistant response is missing."));
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(bytes);
                    }
                }

                continue;
            }

            if (!message.Content.Any(part => part.IsModelVisible) && message.Role != ModelMessageRole.Tool)
            {
                continue;
            }

            var content = message.GetModelVisibleContent();
            var role = message.Role == ModelMessageRole.Assistant ? "assistant" : "user";
            JsonObject block;
            if (message.Role == ModelMessageRole.Tool)
            {
                var id = message.ToolCallId ?? throw new ModelProviderException("Anthropic tool result has no correlation identity.");
                if (message.ModelRound is { } resultRound && replay.ContainsKey(resultRound))
                {
                    id = request.TransientState?.GetWireToolCallId(resultRound, id)
                        ?? throw new ModelProviderException("Anthropic private result correlation is missing.");
                }

                block = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = content, ["is_error"] = message.IsError ?? false };
            }
            else if (message.Role == ModelMessageRole.Assistant && message.ToolName is { } name)
            {
                var id = message.ToolCallId ?? throw new ModelProviderException("Anthropic historical tool call has no identity.");
                var input = JsonNode.Parse(content) as JsonObject
                    ?? throw new ModelProviderException("Anthropic historical tool input must be a JSON object.");
                block = new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = names.ToWireName(name), ["input"] = input };
            }
            else
            {
                block = Text(message.Role == ModelMessageRole.HostContext
                    ? $"<threadsmith_host_context>\n{content}\n</threadsmith_host_context>"
                    : content);
            }

            messageBlocks[messageIndex] = block;
            if (messages.LastOrDefault() is JsonObject last && last["role"]?.GetValue<string>() == role)
            {
                var blocks = last["content"] as JsonArray ?? throw new ModelProviderException("Invalid Anthropic message content.");
                if (message.Role == ModelMessageRole.Tool && blocks.Any(item => item?["type"]?.GetValue<string>() != "tool_result"))
                {
                    throw new ModelProviderException("Anthropic tool results must precede correction or steering text.");
                }

                blocks.Add(block);
            }
            else
            {
                messages.Add(new JsonObject { ["role"] = role, ["content"] = new JsonArray(block) });
            }
        }

        if (usedReplay.Count != replay.Count)
        {
            throw new ModelProviderException("Anthropic private response has no chronological history position.");
        }

        if (request.Messages.Count == 0)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = new JsonArray(Text(request.Input)) });
        }

        if (messages.Count == 0 || messages[0]?["role"]?.GetValue<string>() != "user")
        {
            throw new ModelProviderException("Anthropic requires a model-visible initial user message.");
        }

        ValidateToolChronology(messages);
        return messages;
    }

    private static void ValidateToolChronology(JsonArray messages)
    {
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        var pending = new List<string>();
        foreach (var message in messages.OfType<JsonObject>())
        {
            var blocks = message["content"] as JsonArray ?? throw new ModelProviderException("Invalid Anthropic message content.");
            var results = blocks.OfType<JsonObject>().Where(block => block["type"]?.GetValue<string>() == "tool_result").ToArray();
            if (pending.Count > 0)
            {
                if (message["role"]?.GetValue<string>() != "user" || results.Length != pending.Count
                    || !results.Select(block => block["tool_use_id"]?.GetValue<string>()).ToHashSet(StringComparer.Ordinal).SetEquals(pending))
                {
                    throw new ModelProviderException("Anthropic requires every tool result immediately after its assistant response.");
                }

                // Correlation is authoritative; user result blocks follow the model's tool ordinal order.
                var orderedResults = pending.Select(id => results.Single(block => block["tool_use_id"]?.GetValue<string>() == id).DeepClone()).ToArray();
                var text = blocks.Skip(results.Length).Select(block => block?.DeepClone()).ToArray();
                blocks.Clear();
                foreach (var block in orderedResults.Concat(text))
                {
                    blocks.Add(block);
                }

                pending.Clear();
            }
            else if (results.Length > 0)
            {
                throw new ModelProviderException("Anthropic tool result has no preceding assistant call.");
            }

            foreach (var tool in blocks.OfType<JsonObject>().Where(block => block["type"]?.GetValue<string>() == "tool_use"))
            {
                var id = tool["id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || !usedIds.Add(id))
                {
                    throw new ModelProviderException("Anthropic tool identities must be bounded and unique.");
                }

                pending.Add(id);
            }
        }

        if (pending.Count != 0)
        {
            throw new ModelProviderException("Anthropic request ends before required tool results; complete the host tool boundary first.");
        }
    }

    private static void ValidateRequest(ModelStreamRequest request, ModelProfile profile, AnthropicModelCompatibility compatibility)
    {
        if (request.ResolvedProfileId is { } id && id != profile.Id)
        {
            throw new ModelProviderException("Anthropic request selected a different profile.");
        }

        var output = request.MaximumOutputTokens ?? profile.EffectiveRequestOutputTokenReserve;
        if (output <= 0 || output > profile.MaximumOutputTokens || profile.Endpoint != AnthropicProviderRegistration.MessagesEndpoint
            || profile.ModelId != compatibility.ModelId || !profile.SupportsReasoningLevel(request.ReasoningLevel)
            || (request.ContainsSensitiveData && profile.SensitiveDataPolicy != ModelSensitiveDataPolicy.Allowed)
            || (request.Tools.Count > 0 && (!profile.Capabilities.ToolCalls || request.ToolTransportMode != ToolTransportMode.Native)))
        {
            throw new ModelProviderException("Anthropic request is incompatible with its selected profile or authority.");
        }
    }

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    private static void AddThinking(JsonObject body, JsonObject output, ModelStreamRequest request, ModelProfile profile, AnthropicModelCompatibility compatibility)
    {
        if (request.ReasoningLevel == ReasoningLevel.None)
        {
            if (compatibility.ThinkingMode != AnthropicThinkingMode.Disabled)
            {
                if (!compatibility.SupportsReasoningOff)
                {
                    throw new ModelProviderException("The selected Anthropic model cannot disable thinking.");
                }

                body["thinking"] = new JsonObject { ["type"] = "disabled" };
            }
        }
        else
        {
            var thinking = new JsonObject { ["display"] = request.IncludeReasoningText == true ? "summarized" : "omitted" };
            if (compatibility.ThinkingMode == AnthropicThinkingMode.Adaptive)
            {
                thinking["type"] = "adaptive";
                output["effort"] = profile.SupportedReasoningLevels.Single(level => level == request.ReasoningLevel).Value;
            }
            else if (compatibility.ThinkingMode == AnthropicThinkingMode.Manual
                && compatibility.ManualThinkingBudgets.TryGetValue(request.ReasoningLevel, out var budget)
                && budget >= 1024 && budget < (request.MaximumOutputTokens ?? profile.EffectiveRequestOutputTokenReserve))
            {
                thinking["type"] = "enabled";
                thinking["budget_tokens"] = budget;
            }
            else
            {
                throw new ModelProviderException("Anthropic thinking selection or manual budget is incompatible with the admitted output ceiling.");
            }

            body["thinking"] = thinking;
        }

        if (profile.Temperature is { } temperature)
        {
            if (temperature is < 0 or > 1 || (request.ReasoningLevel != ReasoningLevel.None && temperature != 1))
            {
                throw new ModelProviderException("Anthropic temperature is incompatible with the selected thinking mode.");
            }

            body["temperature"] = temperature;
        }
    }

    private static void AddTools(JsonObject body, ModelStreamRequest request, AnthropicModelCompatibility compatibility, ModelToolWireNameMap names, JsonObject output)
    {
        if (request.Tools.Count == 0)
        {
            return;
        }

        var canonical = ModelToolCanonicalizer.Canonicalize(request.Tools);
        var tools = new JsonArray();
        var strictCount = 0;
        var unionCount = AnthropicStrictSchema.CountUnions(output);
        foreach (var tool in canonical)
        {
            var schema = JsonNode.Parse(tool.ArgumentsJsonSchema) as JsonObject
                ?? throw new ModelProviderException("Anthropic tool schemas must be JSON objects.");
            var strict = tool.PreferStrictArguments && compatibility.SupportsStrictSchemas && strictCount < 20
                ? AnthropicStrictSchema.TryProject(tool.Name, tool.ArgumentsJsonSchema)
                : null;
            if (strict is not null && unionCount + AnthropicStrictSchema.CountUnions(strict) > 16)
            {
                strict = null;
            }

            var item = new JsonObject { ["name"] = names.ToWireName(tool.Name), ["description"] = tool.Description, ["input_schema"] = strict ?? schema };
            if (strict is not null)
            {
                strictCount++;
                unionCount += AnthropicStrictSchema.CountUnions(strict);
                item["strict"] = true;
            }

            tools.Add(item);
        }

        body["tools"] = tools;
        if (request.AllowMultipleToolCalls is { } multiple)
        {
            body["tool_choice"] = new JsonObject { ["type"] = "auto", ["disable_parallel_tool_use"] = !multiple };
        }
    }

    private static void AddCacheControls(JsonObject body, ModelStreamRequest request, AnthropicModelCompatibility compatibility, IReadOnlyDictionary<string, JsonObject> sections, IReadOnlyDictionary<int, JsonObject> messageBlocks)
    {
        if (!compatibility.PromptCachingEnabled || request.CacheCapabilities.ExplicitCacheControl != true || request.CachePlan is null)
        {
            return;
        }

        var locations = new HashSet<JsonObject>(ReferenceEqualityComparer.Instance);
        foreach (var breakpoint in request.CachePlan.Breakpoints)
        {
            var section = breakpoint.Class switch
            {
                ModelCacheBreakpointClass.HostPolicy => "host-policy",
                ModelCacheBreakpointClass.RepositoryInstructions => "repository-instructions",
                ModelCacheBreakpointClass.PhasePolicy => "phase-policy",
                _ => string.Empty,
            };
            var target = breakpoint.Class switch
            {
                ModelCacheBreakpointClass.ToolInventory => (body["tools"] as JsonArray)?.LastOrDefault() as JsonObject,
                ModelCacheBreakpointClass.RequestTail => ((body["messages"] as JsonArray)?.LastOrDefault()?["content"] as JsonArray)?.LastOrDefault() as JsonObject,
                ModelCacheBreakpointClass.ConversationHistory => messageBlocks.GetValueOrDefault(breakpoint.AfterMessageIndex),
                _ => sections.GetValueOrDefault(section),
            };
            if (target is not null && locations.Count < 4 && locations.Add(target))
            {
                target["cache_control"] = new JsonObject { ["type"] = "ephemeral", ["ttl"] = "5m" };
            }
        }
    }
}
