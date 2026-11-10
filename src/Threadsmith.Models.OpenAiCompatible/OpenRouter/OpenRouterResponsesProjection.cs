namespace Threadsmith.Models.OpenAiCompatible.OpenRouter;

using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Projects host requests directly into the OpenRouter Responses protocol.</summary>
internal static class OpenRouterResponsesProjection
{
    /// <summary>Creates the same deterministic projection for admission and dispatch.</summary>
    internal static byte[] Create(ModelStreamRequest request, ModelProfile profile, OpenRouterModelConfiguration model)
    {
        var tools = ModelToolCanonicalizer.Canonicalize(request.Tools);
        var names = ModelToolWireNameMap.Create(tools);
        var input = CreateInput(request, names);
        var routing = new JsonObject { ["require_parameters"] = true, ["allow_fallbacks"] = model.Upstream is null };
        var body = new JsonObject
        {
            ["model"] = model.ModelId,
            ["stream"] = true,
            ["store"] = false,
            ["max_output_tokens"] = request.MaximumOutputTokens ?? model.EffectiveRequestOutputTokenReserve,
            ["input"] = input,
            ["session_id"] = (request.CacheAffinityId ?? request.RunId.Value).ToString("D"),
            ["provider"] = routing,
        };
        if (model.Upstream is { } upstream)
        {
            routing["only"] = new JsonArray(upstream);
            routing["order"] = new JsonArray(upstream);
        }

        if (profile.Temperature is { } temperature)
        {
            body["temperature"] = temperature;
        }

        if (model.SeedSupported)
        {
            body["seed"] = request.Seed;
        }

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

        if (model.SupportsReasoningReplay)
        {
            body["include"] = new JsonArray("reasoning.encrypted_content");
        }

        if (tools.Count > 0)
        {
            var definitions = new JsonArray();
            foreach (var tool in tools)
            {
                var strict = model.StrictToolArguments && tool.PreferStrictArguments
                    ? ModelToolStrictSchemaProjector.TryCreateStrictFunctionSchema(tool.Name, tool.ArgumentsJsonSchema) : null;
                var schema = JsonNode.Parse(strict ?? tool.ArgumentsJsonSchema);
                if (schema is not JsonObject)
                {
                    throw new ModelProviderException("Tool arguments require an object schema.");
                }

                // Responses may normalize omitted strict settings; preserve the host's opt-out explicitly.
                var definition = new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = names.ToWireName(tool.Name),
                    ["description"] = tool.Description,
                    ["parameters"] = schema,
                    ["strict"] = strict is not null,
                };

                definitions.Add(definition);
            }

            body["tools"] = definitions;
            body["tool_choice"] = "auto";
            if (model.ParallelToolCalls && request.AllowMultipleToolCalls is { } parallel)
            {
                body["parallel_tool_calls"] = parallel;
            }
        }
        else if (request.ResponseFormat is { } format)
        {
            if (!model.JsonSchemaResponses)
            {
                throw new ModelProviderException("This OpenRouter profile does not support JSON-schema responses.");
            }

            body["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["name"] = format.SchemaId,
                    ["strict"] = true,
                    ["schema"] = JsonNode.Parse(format.JsonSchema),
                },
            };
        }
        else if (request.RequiredCapabilities.StructuredOutput)
        {
            body["text"] = new JsonObject { ["format"] = new JsonObject { ["type"] = "json_object" } };
        }

        if (OpenRouterProviderRegistration.CachePlan(request, model).Breakpoints.Count > 0)
        {
            for (var index = input.Count - 1; index >= 0; index--)
            {
                if (input[index] is not JsonObject tail)
                {
                    continue;
                }

                var field = tail["type"]?.GetValue<string>() switch
                {
                    "function_call_output" => "output",
                    "message" when tail["role"]?.GetValue<string>() != "assistant" => "content",
                    _ => null,
                };
                if (field is null)
                {
                    continue;
                }

                if (tail[field] is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    tail[field] = Text("input_text", text);
                }

                if (tail[field] is JsonArray blocks)
                {
                    // Responses accepts breakpoints only on input_text, not images or output_text.
                    // An empty text block retains a boundary after the image without duplicating content.
                    var last = blocks.LastOrDefault() as JsonObject;
                    if (last?["type"]?.GetValue<string>() != "input_text")
                    {
                        last = new JsonObject { ["type"] = "input_text", ["text"] = string.Empty };
                        blocks.Add(last);
                    }

                    last["prompt_cache_breakpoint"] = new JsonObject { ["mode"] = "explicit" };
                    break;
                }
            }
        }

        return JsonSerializer.SerializeToUtf8Bytes(body);
    }

    private static JsonArray CreateInput(ModelStreamRequest request, ModelToolWireNameMap names)
    {
        if (request.Messages.Count == 0)
        {
            return [Message("user", Text("input_text", request.Input))];
        }

        var input = new JsonArray();
        var pendingImages = new List<JsonObject>();
        var replay = request.TransientState?.Responses.ToDictionary(item => item.Binding.ModelRound) ?? [];
        var usedReplay = new HashSet<int>();
        for (var index = 0; index < request.Messages.Count; index++)
        {
            var message = request.Messages[index];
            if (message.Role != ModelMessageRole.Tool)
            {
                foreach (var image in pendingImages)
                {
                    input.Add(image);
                }

                pendingImages.Clear();
            }

            if (message.Role == ModelMessageRole.Assistant && message.ModelRound is { } round && replay.TryGetValue(round, out var envelope))
            {
                if (usedReplay.Add(round))
                {
                    var response = ProviderResponseReplay.Read(envelope, request, index).AsArray();
                    var items = response.ToArray();

                    // Read owns an independent tree; detach its children before reparenting.
                    response.Clear();
                    foreach (var item in items)
                    {
                        input.Add(item);
                    }
                }

                continue;
            }

            var content = message.GetModelVisibleContent();
            switch (message.Role)
            {
                case ModelMessageRole.Assistant when message.ToolCallId is not null && message.ToolName is not null:
                    {
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = message.ToolCallId,
                            ["name"] = names.ToWireName(message.ToolName),
                            ["arguments"] = content,
                        });
                        break;
                    }

                case ModelMessageRole.Tool when message.ToolCallId is not null:
                    {
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call_output",
                            ["output"] = content,
                            ["call_id"] = message.ModelRound is { } resultRound && replay.ContainsKey(resultRound)
                                ? request.TransientState?.GetWireToolCallId(resultRound, message.ToolCallId) : message.ToolCallId,
                        });
                        if (message.Content.Any(part => part.IsModelVisible && part.Kind == ModelContentPartKind.Image))
                        {
                            pendingImages.Add(Message("user", CreateContent(request, message)));
                        }

                        break;
                    }

                case ModelMessageRole.Developer:
                case ModelMessageRole.HostContext:
                    {
                        input.Add(Message("user", Text("input_text", $"<threadsmith_host_context>\n{content}\n</threadsmith_host_context>")));
                        break;
                    }

                case ModelMessageRole.System:
                case ModelMessageRole.User:
                case ModelMessageRole.Assistant:
                    {
                        var projected = Message(message.Role.ToString().ToLowerInvariant(), CreateContent(request, message));
                        if (message.Role == ModelMessageRole.Assistant)
                        {
                            projected["id"] = "msg_threadsmith_" + index;
                            projected["status"] = "completed";
                        }

                        input.Add(projected);
                        break;
                    }

                default:
                    throw new ModelProviderException("Structured model history has invalid tool correlation.");
            }
        }

        foreach (var image in pendingImages)
        {
            input.Add(image);
        }

        if (usedReplay.Count != replay.Count)
        {
            throw new ModelProviderException("Private response has no chronological history position.");
        }

        return input;
    }

    private static JsonObject Message(string role, JsonArray content) => new() { ["type"] = "message", ["role"] = role, ["content"] = content };

    private static JsonArray Text(string type, string text) => [new JsonObject { ["type"] = type, ["text"] = text }];

    private static JsonArray CreateContent(ModelStreamRequest request, ModelMessage message)
    {
        var content = new JsonArray();
        if (message.Role == ModelMessageRole.Tool)
        {
            content.Add(new JsonObject
            {
                ["type"] = "input_text",
                ["text"] = JsonSerializer.Serialize(new { tool_call_id = message.ToolCallId, source = "tool_evidence" }),
            });
        }

        foreach (var part in message.Content.Where(part => part.IsModelVisible))
        {
            if (part.Kind == ModelContentPartKind.Image && part.Image is { } image)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "input_image",
                    ["detail"] = "high",
                    ["image_url"] = $"data:{image.MediaType};base64,{ModelImages.GetBase64(request, image)}",
                });
            }
            else if (message.Role != ModelMessageRole.Tool)
            {
                content.Add(new JsonObject { ["type"] = message.Role == ModelMessageRole.Assistant ? "output_text" : "input_text", ["text"] = part.Content });
            }
        }

        return content;
    }
}
