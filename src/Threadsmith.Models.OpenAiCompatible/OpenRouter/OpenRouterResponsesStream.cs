namespace Threadsmith.Models.OpenAiCompatible.OpenRouter;

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Translates bounded Responses events and commits only validated completed tool batches.</summary>
internal sealed class OpenRouterResponsesStream
{
    private readonly ModelProfile _profile;
    private readonly OpenRouterModelConfiguration _model;
    private readonly string _secret;
    private readonly string _protocol;

    /// <summary>Initializes a new instance of the <see cref="OpenRouterResponsesStream"/> class.</summary>
    internal OpenRouterResponsesStream(ModelProfile profile, OpenRouterModelConfiguration model, string secret, string protocol)
    {
        _profile = profile;
        _model = model;
        _secret = secret;
        _protocol = protocol;
    }

    /// <summary>Reports available usage before rejecting output or releasing tools.</summary>
    internal async IAsyncEnumerable<ModelChunk> ReadAsync(HttpResponseMessage response, ModelStreamRequest request, ModelToolWireNameMap names, int maximumOutputTokens, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        var textGuard = request.Tools.Count > 0 ? new NativeToolTextGuard() : null;
        var items = new SortedDictionary<int, JsonNode>();
        var text = new Dictionary<(int Item, int Part, int Channel), StringBuilder>();
        var argumentDeltas = new Dictionary<int, StringBuilder>();
        var toolIndices = new HashSet<int>();
        JsonObject? completed = null;
        ModelUsage? usage = null;
        var outputLimitReached = false;
        long streamedBytes = 0;
        long characters = 0;
        long retainedBytes = 0;
        var done = false;
        await foreach (var payload in ReadFramesAsync(reader, cancellationToken))
        {
            if (payload == "[DONE]")
            {
                if (completed is null)
                {
                    throw new TransientModelException("OpenRouter ended without a completed response.");
                }

                done = true;
                continue;
            }

            var root = Parse(payload);
            var type = String(root, "type");
            var result = root["response"] as JsonObject ?? root;
            var status = String(result, "status");
            var isError = type is "response.failed" or "response.incomplete" or "error" || status is "failed" or "incomplete" || result["error"] is not null;
            var rateLimited = ModelStreamRetry.IsRateLimitError(result);
            if (result["usage"] is JsonObject report)
            {
                var nextUsage = OpenRouterResponsesUsage.Read(report, request, _profile, Math.Max(characters, OutputCharacters(result)));
                if (usage is null || usage != nextUsage)
                {
                    if (completed is not null)
                    {
                        throw new MalformedModelOutputException("OpenRouter changed completed usage.");
                    }

                    usage = nextUsage;
                    yield return new ModelChunk { Usage = usage, IsInterruptedUsage = rateLimited };
                }

                outputLimitReached |= report["output_tokens"] is not null && nextUsage.OutputTokens == maximumOutputTokens;
                if (report["output_tokens"] is not null && nextUsage.OutputTokens > maximumOutputTokens)
                {
                    throw new ModelProviderException("OpenRouter exceeded the requested output-token ceiling.");
                }
            }

            ValidateIdentity(result);
            if (isError)
            {
                if (rateLimited)
                {
                    throw new TransientModelException("OpenRouter Responses reported a rate limit (code 429).") { IsRateLimit = true };
                }

                if ((type == "response.incomplete" || status == "incomplete")
                    && type is not ("response.failed" or "error") && status != "failed" && result["error"] is null
                    && result["incomplete_details"] is JsonObject details && String(details, "reason") == "max_output_tokens")
                {
                    if (result["output"] is JsonArray partialOutput)
                    {
                        for (var index = 0; index < partialOutput.Count; index++)
                        {
                            if (partialOutput[index] is JsonObject item && String(item, "type") == "function_call")
                            {
                                toolIndices.Add(index);
                            }
                        }
                    }

                    if (toolIndices.Count > 0)
                    {
                        throw new MalformedInvocationException(new MalformedInvocationDiagnostic
                        {
                            Kind = MalformedInvocationFailureKind.TruncatedArguments,
                            ProviderFamily = "openrouter",
                            ToolCallCount = toolIndices.Count,
                            SafeMessage = ModelOutputValidator.TruncatedInvocationMessage,
                        });
                    }
                }

                throw new ModelProviderException("OpenRouter Responses reported a failed or incomplete response.");
            }

            if (type == "response.completed")
            {
                if (String(result, "status") != "completed" || result["output"] is not JsonArray)
                {
                    throw new MalformedModelOutputException("OpenRouter returned an invalid terminal response.");
                }

                if (completed is not null && !JsonNode.DeepEquals(completed, result))
                {
                    throw new MalformedModelOutputException("OpenRouter changed its completed response.");
                }

                completed = result;
                continue;
            }

            if (completed is not null || done)
            {
                throw new MalformedModelOutputException("OpenRouter sent events after completion.");
            }

            if (type is "response.output_text.delta" or "response.reasoning_text.delta" or "response.reasoning_summary_text.delta")
            {
                var delta = String(root, "delta") ?? throw new MalformedModelOutputException("OpenRouter returned an invalid text delta.");
                AddBytes(delta);
                characters += delta.Length;
                var channel = type == "response.reasoning_summary_text.delta" ? 2 : type == "response.reasoning_text.delta" ? 1 : 0;
                var key = (Index(root, "output_index"), Index(root, channel == 2 ? "summary_index" : "content_index", 0), channel);
                if (!text.TryGetValue(key, out var buffer))
                {
                    buffer = new StringBuilder();
                    text.Add(key, buffer);
                }

                buffer.Append(delta);
                if (channel != 0)
                {
                    if (request.IncludeReasoningText != false)
                    {
                        yield return new ModelChunk { Reasoning = delta, IsDisplayOnlyReasoning = channel == 2 };
                    }
                }
                else
                {
                    var visible = textGuard?.Append(delta) ?? delta;
                    if (visible.Length > 0)
                    {
                        yield return new ModelChunk { Text = visible };
                    }
                }
            }
            else if (type == "response.output_item.added")
            {
                if (root["item"] is JsonObject item && String(item, "type") == "function_call")
                {
                    toolIndices.Add(Index(root, "output_index"));
                }
            }
            else if (type == "response.output_item.done")
            {
                var item = root["item"] as JsonObject ?? throw new MalformedModelOutputException("OpenRouter omitted a completed output item.");
                var index = Index(root, "output_index");
                if (items.TryGetValue(index, out var existing) && !JsonNode.DeepEquals(existing, item))
                {
                    throw new MalformedModelOutputException("OpenRouter changed a completed output item.");
                }

                if (String(item, "type") == "function_call")
                {
                    toolIndices.Add(index);
                }

                if (!items.ContainsKey(index))
                {
                    retainedBytes += SerializedBytes(item);
                    CheckRetained(retainedBytes);
                    root.Remove("item");
                    items.Add(index, item);
                }
            }
            else if (type is "response.refusal.delta" or "response.refusal.done")
            {
                throw new ModelProviderException("OpenRouter refused the response.");
            }
            else if (type == "response.function_call_arguments.delta")
            {
                var delta = String(root, "delta") ?? throw new MalformedModelOutputException("Invalid tool argument delta.");
                AddBytes(delta);
                var index = Index(root, "output_index");
                toolIndices.Add(index);
                if (!argumentDeltas.TryGetValue(index, out var buffer))
                {
                    buffer = new StringBuilder();
                    argumentDeltas.Add(index, buffer);
                }

                buffer.Append(delta);
            }
        }

        if (completed is null)
        {
            throw new TransientModelException("OpenRouter Responses ended without a completed response.");
        }

        var finalItems = completed["output"]?.AsArray() ?? throw new MalformedModelOutputException("OpenRouter omitted terminal output.");
        for (var index = 0; index < finalItems.Count; index++)
        {
            if (finalItems[index] is not JsonObject item)
            {
                throw new MalformedModelOutputException("OpenRouter returned invalid output items.");
            }

            if (items.TryGetValue(index, out var existing) && !JsonNode.DeepEquals(existing, item))
            {
                throw new MalformedModelOutputException("OpenRouter terminal output conflicts with completed items.");
            }
        }

        if (items.Keys.Any(index => index >= finalItems.Count))
        {
            throw new MalformedModelOutputException("OpenRouter omitted a completed output item.");
        }

        items.Clear();
        retainedBytes = 0;
        foreach (var item in finalItems)
        {
            var value = item ?? throw new MalformedModelOutputException("Missing output item.");
            retainedBytes += SerializedBytes(value);
            CheckRetained(retainedBytes);
            items.Add(items.Count, value);
        }

        if (usage is null)
        {
            usage = OpenRouterResponsesUsage.Estimate(request, _profile, Math.Max(characters, OutputCharacters(completed)));
            yield return new ModelChunk { Usage = usage };
        }

        var calls = new List<JsonObject>();
        var seenCallIds = new HashSet<string>(StringComparer.Ordinal);
        var requiresPrivate = false;
        var seenText = new HashSet<(int Item, int Part, int Channel)>();
        foreach (var (index, node) in items)
        {
            var item = node.AsObject();
            if (String(item, "status") is { } itemStatus && itemStatus != "completed")
            {
                throw new MalformedModelOutputException("OpenRouter returned an unfinished output item.");
            }

            switch (String(item, "type"))
            {
                case "function_call":
                    {
                        var id = String(item, "call_id");
                        if (string.IsNullOrWhiteSpace(id) || !seenCallIds.Add(id))
                        {
                            throw new MalformedModelOutputException("OpenRouter returned missing or duplicate tool call IDs.");
                        }

                        var arguments = String(item, "arguments") ?? string.Empty;
                        if (argumentDeltas.TryGetValue(index, out var fragments))
                        {
                            if (!arguments.StartsWith(fragments.ToString(), StringComparison.Ordinal))
                            {
                                throw new MalformedModelOutputException("OpenRouter final tool arguments conflict with deltas.");
                            }

                            AddBytes(arguments[fragments.Length..]);
                            argumentDeltas.Remove(index);
                        }
                        else
                        {
                            AddBytes(arguments);
                        }

                        AddBytes(String(item, "name") ?? string.Empty);
                        calls.Add(item);
                        break;
                    }

                case "message":
                    {
                        if (String(item, "role") != "assistant" || item["content"] is not JsonArray content)
                        {
                            throw new MalformedModelOutputException("OpenRouter returned an invalid assistant message.");
                        }

                        for (var partIndex = 0; partIndex < content.Count; partIndex++)
                        {
                            var part = content[partIndex] as JsonObject ?? throw new MalformedModelOutputException("Invalid output content.");
                            if (String(part, "type") != "output_text")
                            {
                                throw new ModelProviderException("OpenRouter returned refusal or unsupported output content.");
                            }

                            var key = (index, partIndex, 0);
                            seenText.Add(key);
                            var suffix = Reconcile(key, String(part, "text") ?? throw new MalformedModelOutputException("Invalid final output text."));
                            if (suffix.Length > 0)
                            {
                                AddBytes(suffix);
                                var visible = textGuard?.Append(suffix) ?? suffix;
                                if (visible.Length > 0)
                                {
                                    yield return new ModelChunk { Text = visible };
                                }
                            }
                        }

                        break;
                    }

                case "reasoning":
                    {
                        var encrypted = String(item, "encrypted_content");
                        var signature = String(item, "signature");
                        requiresPrivate |= encrypted is not null || signature is not null;
                        AddBytes(encrypted ?? string.Empty);
                        AddBytes(signature ?? string.Empty);
                        foreach (var (field, channel) in new[] { ("content", 1), ("summary", 2) })
                        {
                            if (item[field] is null && field == "content")
                            {
                                continue;
                            }

                            if (item[field] is not JsonArray parts)
                            {
                                throw new MalformedModelOutputException("Invalid reasoning content or summary.");
                            }

                            for (var partIndex = 0; partIndex < parts.Count; partIndex++)
                            {
                                var part = parts[partIndex] as JsonObject ?? throw new MalformedModelOutputException("Invalid reasoning content.");
                                if (String(part, "type") != (channel == 1 ? "reasoning_text" : "summary_text"))
                                {
                                    throw new MalformedModelOutputException("Invalid reasoning content type.");
                                }

                                var key = (index, partIndex, channel);
                                seenText.Add(key);
                                var suffix = Reconcile(key, String(part, "text") ?? throw new MalformedModelOutputException("Invalid reasoning text."));
                                if (suffix.Length > 0)
                                {
                                    AddBytes(suffix);
                                    if (request.IncludeReasoningText != false)
                                    {
                                        yield return new ModelChunk { Reasoning = suffix, IsDisplayOnlyReasoning = channel == 2 };
                                    }
                                }
                            }
                        }

                        break;
                    }

                default:
                    throw new MalformedModelOutputException("OpenRouter returned an unsupported output item.");
            }
        }

        if (text.Keys.Any(key => !seenText.Contains(key)) || argumentDeltas.Count > 0)
        {
            throw new MalformedModelOutputException("OpenRouter omitted streamed content from its completed output.");
        }

        if ((_profile.MaximumToolCalls > 0 && calls.Count > _profile.MaximumToolCalls) || (request.AllowMultipleToolCalls == false && calls.Count > 1))
        {
            throw new MalformedModelOutputException("OpenRouter exceeded the permitted tool-call batch size.");
        }

        if (textGuard is not null)
        {
            if (textGuard.Complete(calls.Count > 0, out var remaining))
            {
                throw new MalformedModelOutputException("OpenRouter returned orphaned native tool arguments.");
            }

            if (remaining.Length > 0)
            {
                yield return new ModelChunk { Text = remaining };
            }
        }

        var outputs = new List<ToolRequestModelOutput>(calls.Count);
        for (var index = 0; index < calls.Count; index++)
        {
            var output = new ToolRequestModelOutput(names.ToCanonicalName(String(calls[index], "name") ?? string.Empty), String(calls[index], "arguments") ?? string.Empty);
            ModelOutputValidator.ValidateInvocation(output, providerFamily: "openrouter", toolOrdinal: index, toolCallCount: calls.Count, outputLimitReached: outputLimitReached);
            outputs.Add(output);
        }

        if (calls.Count > 0 && requiresPrivate && !_model.SupportsReasoningReplay)
        {
            throw new ModelProviderException("This OpenRouter profile does not support required private reasoning replay; start a fresh turn.");
        }

        if (calls.Count > 0 && _model.SupportsReasoningReplay)
        {
            if (retainedBytes > ModelProfile.DefaultMaximumStreamedBytes)
            {
                throw new MalformedModelOutputException("OpenRouter exceeded the private replay limit.");
            }

            // Transfer the validated tree to the replay serializer; no second terminal/item tree is needed.
            completed.Remove("output");
            var replay = ProviderResponseReplay.Create(request, _profile, _secret, _protocol, finalItems, calls.Select(call => String(call, "call_id") ?? string.Empty).ToArray(), usage.OutputTokens);
            try
            {
                yield return new ModelChunk { ResponseEnvelope = replay };
            }
            finally
            {
                if (!replay.IsRetained)
                {
                    replay.Dispose();
                }
            }
        }

        foreach (var output in outputs)
        {
            yield return new ModelChunk { Output = output };
        }

        yield return new ModelChunk { FinishReason = calls.Count > 0 ? ModelFinishReason.ToolCalls : ModelFinishReason.Stop };

        string Reconcile((int Item, int Part, int Channel) key, string final)
        {
            var prefix = text.TryGetValue(key, out var buffer) ? buffer.ToString() : string.Empty;
            if (!final.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new MalformedModelOutputException("OpenRouter final text conflicts with its streamed deltas.");
            }

            return final[prefix.Length..];
        }

        void AddBytes(string value)
        {
            streamedBytes = checked(streamedBytes + Encoding.UTF8.GetByteCount(value));
            if (_profile.MaximumStreamedBytes > 0 && streamedBytes > _profile.MaximumStreamedBytes)
            {
                throw new MalformedModelOutputException("OpenRouter exceeded the configured streamed-output limit.");
            }
        }
    }

    private static long SerializedBytes(JsonNode value)
    {
        // Count emitted bytes without retaining another JSON string or serialized DOM.
        // The writer may still grow its buffer for a large individual value.
        using var writer = new Utf8JsonWriter(Stream.Null);
        value.WriteTo(writer);
        writer.Flush();
        return writer.BytesCommitted;
    }

    private void ValidateIdentity(JsonObject response)
    {
        if ((String(response, "model") is { } model && model != _model.ModelId)
            || (_model.ExpectedProviderName is { } expected && String(response, "provider") is { } provider && !string.Equals(expected, provider, StringComparison.OrdinalIgnoreCase)))
        {
            throw new MalformedModelOutputException("OpenRouter returned an unexpected model or upstream identity.");
        }
    }

    private static long OutputCharacters(JsonObject response)
    {
        if (response["output"] is not JsonArray items)
        {
            return 0;
        }

        long total = 0;
        foreach (var item in items.OfType<JsonObject>())
        {
            total += String(item, "arguments")?.Length ?? 0;
            foreach (var part in (item["content"] as JsonArray ?? []).Concat(item["summary"] as JsonArray ?? []).OfType<JsonObject>())
            {
                total += String(part, "text")?.Length ?? 0;
            }
        }

        return total;
    }

    private void CheckRetained(long bytes)
    {
        // Framing may repeat escaped payloads. This is not the private-envelope limit.
        if (bytes > FrameLimit)
        {
            throw new MalformedModelOutputException("OpenRouter exceeded the Responses framing limit.");
        }
    }

    private static JsonObject Parse(string payload)
    {
        try
        {
            return JsonNode.Parse(payload) as JsonObject ?? throw new MalformedModelOutputException("OpenRouter returned a non-object event.");
        }
        catch (JsonException)
        {
            throw new MalformedModelOutputException("OpenRouter returned malformed Responses JSON.");
        }
    }

    private static string? String(JsonObject node, string property)
    {
        if (node[property] is null)
        {
            return null;
        }

        return node[property] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text : throw new MalformedModelOutputException("OpenRouter returned an invalid protocol scalar.");
    }

    private static int Index(JsonObject node, string property, int? fallback = null)
        => node[property] is JsonValue value && value.TryGetValue<int>(out var index) && index >= 0 ? index
            : fallback ?? throw new MalformedModelOutputException("OpenRouter returned an invalid output index.");

    private long FrameLimit => _profile.MaximumStreamedBytes == 0 ? int.MaxValue
        : Math.Max(ModelProfile.DefaultMaximumStreamedBytes, Math.Min(int.MaxValue, _profile.MaximumStreamedBytes > int.MaxValue / 6 ? int.MaxValue : (_profile.MaximumStreamedBytes * 6) + 65536));

    private async IAsyncEnumerable<string> ReadFramesAsync(StreamReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        var data = new StringBuilder();
        var carriageReturn = false;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
            {
                if (line.Length > 0 || data.Length > 0)
                {
                    throw new MalformedModelOutputException("OpenRouter ended in a truncated SSE frame.");
                }

                yield break;
            }

            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (character == '\n' && carriageReturn)
                {
                    carriageReturn = false;
                    continue;
                }

                carriageReturn = character == '\r';
                if (character is '\r' or '\n')
                {
                    if (line.Length == 0)
                    {
                        if (data.Length > 0)
                        {
                            yield return data.ToString(0, data.Length - 1);
                            data.Clear();
                        }
                    }
                    else
                    {
                        var value = line.ToString();
                        if (value.StartsWith("data:", StringComparison.Ordinal))
                        {
                            var offset = value.Length > 5 && value[5] == ' ' ? 6 : 5;
                            data.Append(value.AsSpan(offset)).Append('\n');
                        }

                        line.Clear();
                    }
                }
                else
                {
                    line.Append(character);
                }

                if ((long)line.Length + data.Length > FrameLimit)
                {
                    throw new MalformedModelOutputException("OpenRouter exceeded the SSE frame limit.");
                }
            }
        }
    }
}
