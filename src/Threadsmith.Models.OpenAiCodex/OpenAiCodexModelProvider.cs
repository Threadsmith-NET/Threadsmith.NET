namespace Threadsmith.Models.OpenAiCodex;

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Native authenticated Codex Responses streaming adapter.</summary>
internal sealed class OpenAiCodexModelProvider : IModelProvider
{
    private readonly HttpClient _httpClient;
    private readonly ModelProfile _profile;
    private readonly Func<string, CancellationToken, Task<string?>>? _refreshAccessTokenAsync;

    /// <summary>Initializes a new instance of the <see cref="OpenAiCodexModelProvider"/> class.</summary>
    public OpenAiCodexModelProvider(
        HttpClient httpClient,
        ModelProfile profile,
        string accessToken,
        Func<string, CancellationToken, Task<string?>>? refreshAccessTokenAsync)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        _httpClient = httpClient;
        _profile = profile;
        AccessToken = accessToken;
        _refreshAccessTokenAsync = refreshAccessTokenAsync;
    }

    private string AccessToken { get; }

    /// <inheritdoc />
    public IAsyncEnumerable<ModelChunk> StreamAsync(
        ModelStreamRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContainsSensitiveData
            && _profile.SensitiveDataPolicy != ModelSensitiveDataPolicy.Allowed)
        {
            throw new ModelProviderException(
                $"Model profile '{_profile.Name}' prohibits sensitive request content.");
        }

        var profileOutputLimit = _profile.MaximumOutputTokens;
        if (request.MaximumOutputTokens is { } maximumOutputTokens
            && (maximumOutputTokens <= 0 || maximumOutputTokens > profileOutputLimit))
        {
            throw new ModelProviderException(
                "The requested output ceiling must be between 1 and the resolved profile maximum of "
                + $"{profileOutputLimit} tokens.");
        }

        return ClassifyStreamFailureAsync(StreamCoreAsync(request, profileOutputLimit, cancellationToken), cancellationToken);
    }

    private static async IAsyncEnumerable<ModelChunk> ClassifyStreamFailureAsync(
        IAsyncEnumerable<ModelChunk> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = source.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ModelProviderTimeoutException("The Codex Responses stream timed out.", exception);
            }
            catch (Exception exception) when (exception is IOException
                || (exception is HttpRequestException transport && IsTransientTransportFailure(transport)))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Never replay consumed output: callers may already have observed a tool or text chunk.
                throw new TransientModelException($"The Codex Responses connection ended before completion ({TransportFailureKind(exception)}).", exception);
            }

            if (!hasNext)
            {
                yield break;
            }

            yield return enumerator.Current;
        }
    }

    private static string TransportFailureKind(Exception exception)
    {
        return exception switch
        {
            HttpIOException io => $"HTTP {io.HttpRequestError}",
            HttpRequestException http => $"HTTP {http.HttpRequestError}",
            _ => "I/O failure",
        };
    }

    private static bool IsTransientTransportFailure(HttpRequestException exception)
    {
        return exception.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.HttpProtocolError
            or HttpRequestError.ResponseEnded;
    }

    private async IAsyncEnumerable<ModelChunk> StreamCoreAsync(
        ModelStreamRequest request,
        int profileOutputLimit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        request = request with { ResolvedProfileId = request.ResolvedProfileId ?? _profile.Id };
        if (request.ResolvedProfileId != _profile.Id)
        {
            throw new ModelProviderException("Resolved model profile does not match the Codex adapter.");
        }

        var canonicalTools = ModelToolCanonicalizer.Canonicalize(request.Tools);

        // Codex cannot enforce a per-request token limit. Reserve input capacity here;
        // completed usage is checked against the separate profile maximum.
        var outputReserve = request.MaximumOutputTokens ?? _profile.EffectiveRequestOutputTokenReserve;
        ValidateCapacity(request, canonicalTools, outputReserve);
        request = await ModelImages.ResolveAsync(request, _profile, cancellationToken);
        var toolNameMap = ModelToolWireNameMap.Create(canonicalTools);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_profile.Timeout > TimeSpan.Zero)
        {
            timeout.CancelAfter(_profile.Timeout);
        }

        var accessToken = AccessToken;
        var replayedAfterAuthenticationRejection = false;
        var attempt = 0;
        while (true)
        {
            attempt++;
            ProviderResponseReplay.Validate(request, _profile, accessToken, "openai-codex-reasoning");
            using var message = CreateRequest(
                request,
                accessToken,
                canonicalTools,
                toolNameMap);
            HttpResponseMessage response;
            try
            {
                timeout.Token.ThrowIfCancellationRequested();
                var pendingResponse = _httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
                request.SubmissionObserver?.Invoke();
                response = await pendingResponse.ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ModelProviderTimeoutException("The Codex Responses request timed out.", exception);
            }
            catch (HttpRequestException exception) when (IsTransientTransportFailure(exception)
                && attempt < _profile.RetryPolicy.MaxAttempts)
            {
                await Task.Delay(_profile.RetryPolicy.Delay, timeout.Token).ConfigureAwait(false);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    await using var chunks = ReadResponseAsync(
                        response, request, accessToken, toolNameMap, profileOutputLimit, timeout.Token).GetAsyncEnumerator(timeout.Token);
                    var deliveredOutput = false;
                    var deliveredReasoning = false;
                    var retry = false;
                    while (true)
                    {
                        bool hasNext;
                        try
                        {
                            hasNext = await chunks.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (Exception exception) when (!deliveredOutput
                            && attempt < _profile.RetryPolicy.MaxAttempts
                            && (exception is IOException or TransientModelException
                                || (exception is HttpRequestException transport && IsTransientTransportFailure(transport))))
                        {
                            timeout.Token.ThrowIfCancellationRequested();
                            retry = true;
                            break;
                        }

                        if (!hasNext)
                        {
                            break;
                        }

                        // Answer, tool, usage and replay data commit this attempt; preliminary reasoning does not.
                        deliveredOutput |= !ModelStreamRetry.IsReasoningOnly(chunks.Current);
                        deliveredReasoning |= chunks.Current.Reasoning is not null;
                        yield return chunks.Current;
                    }

                    if (!retry)
                    {
                        yield break;
                    }

                    if (deliveredReasoning)
                    {
                        yield return ModelStreamRetry.ReasoningBoundary();
                    }
                }
                else if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    && !replayedAfterAuthenticationRejection
                    && _refreshAccessTokenAsync is not null)
                {
                    string? refreshedToken;
                    try
                    {
                        refreshedToken = await _refreshAccessTokenAsync(accessToken, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new ModelProviderTimeoutException("The Codex credential refresh timed out.", exception);
                    }

                    replayedAfterAuthenticationRejection = true;
                    if (!string.IsNullOrWhiteSpace(refreshedToken))
                    {
                        accessToken = refreshedToken;
                        attempt--;
                        continue;
                    }
                }

                if ((response.IsSuccessStatusCode || IsTransient(response.StatusCode)) && attempt < _profile.RetryPolicy.MaxAttempts)
                {
                    try
                    {
                        response.Dispose();
                        await Task.Delay(_profile.RetryPolicy.Delay, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new ModelProviderTimeoutException("The Codex Responses request timed out.", exception);
                    }

                    continue;
                }

                try
                {
                    throw await CreateFailureAsync(response, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new ModelProviderTimeoutException("The Codex error response timed out.", exception);
                }
            }
        }
    }

    private async IAsyncEnumerable<ModelChunk> ReadResponseAsync(
        HttpResponseMessage response,
        ModelStreamRequest request,
        string accessToken,
        ModelToolWireNameMap toolNameMap,
        int profileOutputLimit,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        await foreach (var chunk in ReadEventsAsync(
            reader,
            request,
            accessToken,
            toolNameMap,
            profileOutputLimit,
            _profile.MaximumStreamedBytes,
            _profile.MaximumToolCalls,
            cancellationToken).ConfigureAwait(false))
        {
            yield return chunk;
        }
    }

    private HttpRequestMessage CreateRequest(
        ModelStreamRequest request,
        string accessToken,
        IReadOnlyList<ModelToolDefinition> canonicalTools,
        ModelToolWireNameMap toolNameMap)
    {
        var cacheAffinityKey = (request.CacheAffinityId ?? request.RunId.Value).ToString("D");
        JsonObject body = new()
        {
            ["model"] = _profile.ModelId,
            ["store"] = false,
            ["stream"] = true,
            ["prompt_cache_key"] = cacheAffinityKey,
            ["instructions"] = request.ProviderInstructions?.Content
                ?? throw new InvalidOperationException(
                    "Native Codex requests require request-owned provider instructions."),
            ["input"] = CreateInput(request, toolNameMap),
            ["include"] = new JsonArray("reasoning.encrypted_content"),
            ["reasoning"] = new JsonObject
            {
                ["effort"] = ToProviderReasoning(request.ReasoningLevel),
                ["summary"] = "auto",
            },
            ["text"] = new JsonObject { ["verbosity"] = "medium" },
        };

        if (request.AllowMultipleToolCalls is { } allowMultipleToolCalls)
        {
            body["parallel_tool_calls"] = allowMultipleToolCalls;
        }

        if (canonicalTools.Count > 0)
        {
            JsonArray tools = [];
            foreach (var tool in canonicalTools)
            {
                var strictSchema = tool.PreferStrictArguments
                    ? ModelToolStrictSchemaProjector.TryCreateStrictFunctionSchema(
                        tool.Name,
                        tool.ArgumentsJsonSchema)
                    : null;
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = toolNameMap.ToWireName(tool.Name),
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(strictSchema ?? tool.ArgumentsJsonSchema),
                    ["strict"] = strictSchema is not null,
                });
            }

            body["tools"] = tools;
            body["tool_choice"] = "auto";
        }

        HttpRequestMessage message = new(HttpMethod.Post, OpenAiCodexProviderRegistration.ResponsesEndpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Headers.TryAddWithoutValidation("originator", "threadsmith");
        message.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
        message.Headers.TryAddWithoutValidation("session-id", cacheAffinityKey);
        message.Headers.TryAddWithoutValidation("x-client-request-id", Guid.NewGuid().ToString("D"));
        var accountId = OpenAiCodexTokenClaims.TryGetAccountId(accessToken);
        if (accountId is not null)
        {
            message.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
        }

        return message;
    }

    private void ValidateCapacity(
        ModelStreamRequest request,
        IReadOnlyList<ModelToolDefinition> canonicalTools,
        int outputReserveTokens)
    {
        var providerInstructions = request.ProviderInstructions
            ?? throw new ModelProviderException(
                "Native Codex requests require request-owned provider instructions.");
        var suppliedEstimate = request.WireEstimate
            ?? throw new ModelProviderException(
                "Native Codex requests require a complete provider-wire capacity estimate.");
        var messages = request.Messages.Count == 0
            ?
            [
                new ModelMessage
                {
                    Role = ModelMessageRole.User,
                    SectionId = "legacy-input",
                    Content = [new ModelContentPart { Content = request.Input }],
                },
            ]
            : request.Messages;
        var stablePrefixMessageCount = request.Layout?.StablePrefixMessageCount ?? 0;
        if (stablePrefixMessageCount > messages.Count)
        {
            throw new ModelProviderException(
                "The Codex request layout exceeds the provider-visible message count.");
        }

        var expectedEstimate = ModelWireEstimator.Estimate(
            messages,
            canonicalTools,
            request.ToolTransportMode,
            stablePrefixMessageCount,
            outputReserveTokens,
            providerInstructions);
        var replayEstimate = ProviderResponseReplay.IncludeAllowance(request, expectedEstimate);

        // Direct adapter callers may supply the neutral estimate; catalog callers supply prepared capacity.
        var suppliedInputTokens = request.Preparation is null ? expectedEstimate.WireInputTokens : replayEstimate.WireInputTokens;
        if (suppliedEstimate.LogicalTokens != expectedEstimate.LogicalTokens
            || suppliedEstimate.WireInputTokens != suppliedInputTokens
            || suppliedEstimate.StablePrefixTokens != expectedEstimate.StablePrefixTokens
            || suppliedEstimate.StablePrefixComponentCount != expectedEstimate.StablePrefixComponentCount
            || suppliedEstimate.NativeToolTokens != expectedEstimate.NativeToolTokens
            || suppliedEstimate.TextToolTokens != expectedEstimate.TextToolTokens
            || suppliedEstimate.FramingTokens != expectedEstimate.FramingTokens
            || suppliedEstimate.ProviderInstructionTokens != expectedEstimate.ProviderInstructionTokens
            || suppliedEstimate.OutputReserveTokens != expectedEstimate.OutputReserveTokens
            || suppliedEstimate.SectionTokens.Count != expectedEstimate.SectionTokens.Count
            || expectedEstimate.SectionTokens.Any(expected =>
                !suppliedEstimate.SectionTokens.TryGetValue(expected.Key, out var supplied)
                || supplied != expected.Value))
        {
            throw new ModelProviderException(
                "The Codex request provider-wire capacity estimate does not match its provider-visible content.");
        }

        var requiredTokens = replayEstimate.TotalCapacityTokens;
        if (requiredTokens > _profile.ContextWindow)
        {
            throw new ModelProviderException(
                $"The complete Codex request requires {requiredTokens} tokens but profile "
                + $"'{_profile.Name}' permits {_profile.ContextWindow}.");
        }
    }

    private static JsonArray CreateInput(
        ModelStreamRequest request,
        ModelToolWireNameMap toolNameMap)
    {
        if (request.Messages.Count == 0)
        {
            return
            [
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "input_text", ["text"] = request.Input },
                    },
                },
            ];
        }

        JsonArray input = [];
        var replay = request.TransientState?.Responses.ToDictionary(item => item.Binding.ModelRound) ?? [];
        var usedReplay = new HashSet<int>();
        for (var messageIndex = 0; messageIndex < request.Messages.Count; messageIndex++)
        {
            var message = request.Messages[messageIndex];
            if (message.Role == ModelMessageRole.Assistant && message.ModelRound is { } round && replay.TryGetValue(round, out var envelope))
            {
                if (usedReplay.Add(round))
                {
                    foreach (var item in ProviderResponseReplay.Read(envelope, request, messageIndex).AsArray())
                    {
                        input.Add(item?.DeepClone());
                    }
                }

                continue;
            }

            var content = message.GetModelVisibleContent();
            if (message.Role == ModelMessageRole.Assistant
                && message.ToolCallId is not null
                && message.ToolName is not null)
            {
                input.Add(new JsonObject
                {
                    ["type"] = "function_call",
                    ["call_id"] = message.ToolCallId,
                    ["name"] = toolNameMap.ToWireName(message.ToolName),
                    ["arguments"] = content,
                });
                continue;
            }

            if (message.Role == ModelMessageRole.Tool && message.ToolCallId is not null)
            {
                input.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = message.ModelRound is { } resultRound && replay.ContainsKey(resultRound)
                        ? request.TransientState?.GetWireToolCallId(resultRound, message.ToolCallId) : message.ToolCallId,
                    ["output"] = message.Content.Any(part => part.IsModelVisible && part.Kind == ModelContentPartKind.Image)
                        ? CreateContent(request, message, "input_text") : JsonValue.Create(content),
                });
                continue;
            }

            var role = message.Role switch
            {
                ModelMessageRole.System => "system",
                ModelMessageRole.Developer => "developer",
                ModelMessageRole.HostContext => "developer",
                ModelMessageRole.User => "user",
                ModelMessageRole.Assistant => "assistant",
                _ => throw new InvalidOperationException(
                    $"Structured model message '{message.SectionId}' has invalid tool correlation."),
            };
            var contentType = message.Role == ModelMessageRole.Assistant
                ? "output_text"
                : "input_text";
            input.Add(new JsonObject
            {
                ["role"] = role,
                ["content"] = message.Content.Any(part => part.IsModelVisible && part.Kind == ModelContentPartKind.Image)
                    ? CreateContent(request, message, contentType)
                    : new JsonArray { new JsonObject { ["type"] = contentType, ["text"] = content } },
            });
        }

        if (usedReplay.Count != replay.Count)
        {
            throw new ModelProviderException("Private response has no chronological history position.");
        }

        return input;
    }

    private static JsonArray CreateContent(ModelStreamRequest request, ModelMessage message, string textType)
    {
        return new JsonArray(message.Content.Where(part => part.IsModelVisible).Select(part => (JsonNode)(part.Image is { } image && part.Kind == ModelContentPartKind.Image
            ? new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:{image.MediaType};base64,{ModelImages.GetBase64(request, image)}", ["detail"] = "high" }
            : new JsonObject { ["type"] = textType, ["text"] = part.Content })).ToArray());
    }

    private async IAsyncEnumerable<ModelChunk> ReadEventsAsync(
        StreamReader reader,
        ModelStreamRequest request,
        string accessToken,
        ModelToolWireNameMap toolNameMap,
        int maximumOutputTokens,
        long maximumStreamedBytes,
        int maximumToolCalls,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var completedItems = new SortedDictionary<int, JsonElement>();
        long retainedItemBytes = 0;
        long streamedOutputBytes = 0;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = line.AsSpan(5).TrimStart().ToString();
            if (payload.Length == 0 || string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                continue;
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var type = GetString(root, "type");
            switch (type)
            {
                case "response.output_text.delta":
                    if (GetString(root, "delta") is { Length: > 0 } text)
                    {
                        AddStreamedOutput(text);
                        yield return new ModelChunk { Text = text };
                    }

                    break;
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                    if (GetString(root, "delta") is { Length: > 0 } reasoning)
                    {
                        AddStreamedOutput(reasoning);
                        yield return new ModelChunk { Reasoning = reasoning };
                    }

                    break;
                case "response.output_item.done":
                    if (root.TryGetProperty("item", out var item))
                    {
                        var index = root.TryGetProperty("output_index", out var outputIndex) ? outputIndex.GetInt32() : completedItems.Count;
                        if (index < 0 || (completedItems.TryGetValue(index, out var existing) && !JsonElement.DeepEquals(existing, item)))
                        {
                            throw new MalformedModelOutputException("Conflicting completed Codex output items.");
                        }

                        if (!completedItems.ContainsKey(index))
                        {
                            RetainItem(item);
                            completedItems.Add(index, item.Clone());
                        }
                    }

                    break;
                case "response.completed":
                    var usage = TryReadUsage(root);
                    if (usage is not null)
                    {
                        // Completed requests consume tokens even when their output is rejected.
                        yield return new ModelChunk { Usage = usage };
                        if (usage.OutputTokens > maximumOutputTokens)
                        {
                            throw new ModelProviderException("The Codex response usage exceeded the configured profile output-token maximum.");
                        }
                    }

                    // Completed response output is authoritative when supplied; added/delta events
                    // are not substitutes for the endpoint's final opaque reasoning items.
                    if (root.TryGetProperty("response", out var finalResponse)
                        && finalResponse.TryGetProperty("output", out var finalOutput) && finalOutput.ValueKind == JsonValueKind.Array)
                    {
                        completedItems.Clear();
                        retainedItemBytes = 0;
                        foreach (var outputItem in finalOutput.EnumerateArray())
                        {
                            RetainItem(outputItem);
                            completedItems.Add(completedItems.Count, outputItem.Clone());
                        }
                    }

                    var calls = completedItems.Values.Where(value => GetString(value, "type") == "function_call").ToArray();
                    if (maximumToolCalls > 0 && calls.Length > maximumToolCalls)
                    {
                        throw new ModelProviderException("The Codex response exceeded the configured tool-call limit.");
                    }

                    foreach (var call in calls)
                    {
                        AddStreamedOutput(GetString(call, "name"));
                        AddStreamedOutput(GetString(call, "arguments"));
                    }

                    foreach (var reasoningItem in completedItems.Values.Where(value => GetString(value, "type") == "reasoning"))
                    {
                        AddStreamedOutput(GetString(reasoningItem, "encrypted_content"));
                    }

                    var pendingToolCalls = calls.Select(call => new PendingCodexToolCall(GetString(call, "name"), GetString(call, "arguments"))).ToArray();
                    var toolOutputs = CreateCodexToolOutputs(pendingToolCalls, toolNameMap);
                    if (toolOutputs.Count > 0 && completedItems.Values.Any(value => GetString(value, "type") == "reasoning"))
                    {
                        var responseItems = new JsonArray([.. completedItems.Values.Select(value => JsonNode.Parse(value.GetRawText()))]);
                        var callIds = calls.Select(call => GetString(call, "call_id") ?? string.Empty).ToArray();
                        var replay = ProviderResponseReplay.Create(request, _profile, accessToken, "openai-codex-reasoning", responseItems, callIds, usage?.OutputTokens ?? 0);
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

                    foreach (var output in toolOutputs)
                    {
                        yield return new ModelChunk { Output = output };
                    }

                    yield return new ModelChunk
                    {
                        FinishReason = toolOutputs.Count > 0 ? ModelFinishReason.ToolCalls : ModelFinishReason.Stop,
                    };
                    yield break;
                case "response.failed":
                case "error":
                    var errorRoot = root.TryGetProperty("response", out var failedResponse) && failedResponse.ValueKind == JsonValueKind.Object ? failedResponse : root;
                    var errorDetails = ReadErrorIdentifiers(errorRoot);
                    throw new ModelProviderException("The Codex Responses stream reported a provider error." + (errorDetails is null ? string.Empty : $" {errorDetails}"));
            }
        }

        throw new TransientModelException("The Codex Responses connection ended without a completed response.");

        void RetainItem(JsonElement item)
        {
            retainedItemBytes = checked(retainedItemBytes + Encoding.UTF8.GetByteCount(item.GetRawText()));
            if (retainedItemBytes > ModelProfile.DefaultMaximumStreamedBytes)
            {
                throw new ModelProviderException("The Codex response exceeded the private item retention limit.");
            }
        }

        void AddStreamedOutput(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            streamedOutputBytes = checked(streamedOutputBytes + Encoding.UTF8.GetByteCount(value));
            if (maximumStreamedBytes > 0 && streamedOutputBytes > maximumStreamedBytes)
            {
                throw new ModelProviderException(
                    $"The Codex response exceeded the configured stream limit of {maximumStreamedBytes} bytes.");
            }
        }
    }

    private static IReadOnlyList<ToolRequestModelOutput> CreateCodexToolOutputs(
        IReadOnlyList<PendingCodexToolCall> pendingToolCalls,
        ModelToolWireNameMap toolNameMap)
    {
        if (pendingToolCalls.Count == 0)
        {
            return [];
        }

        var outputs = new ToolRequestModelOutput[pendingToolCalls.Count];
        for (var index = 0; index < pendingToolCalls.Count; index++)
        {
            var pending = pendingToolCalls[index];
            var output = new ToolRequestModelOutput(
                toolNameMap.ToCanonicalName(pending.Name ?? string.Empty),
                pending.Arguments ?? string.Empty);
            ModelOutputValidator.ValidateInvocation(
                output,
                providerFamily: "openai-codex",
                toolOrdinal: index,
                toolCallCount: pendingToolCalls.Count);
            outputs[index] = output;
        }

        return outputs;
    }

    private static ModelUsage? TryReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("response", out var response)
            || !response.TryGetProperty("usage", out var usage))
        {
            return null;
        }

        var input = GetInt64(usage, "input_tokens");
        var output = GetInt64(usage, "output_tokens");
        long? cachedInput = null;
        if (usage.TryGetProperty("input_tokens_details", out var details))
        {
            cachedInput = GetNullableInt64(details, "cached_tokens");
        }

        if (cachedInput is < 0)
        {
            throw new ModelProviderException("The Codex stream returned negative cache token usage.");
        }

        var cache = cachedInput is null
            ? null
            : new ModelCacheUsage
            {
                Availability = CacheUsageAvailability.Reported,
                CacheReadTokens = cachedInput,
                ReadInputSemantics = CacheReadInputSemantics.IncludedInInput,
                Provenance = "openai-codex:input_tokens_details",
            };
        usage.TryGetProperty("output_tokens_details", out var outputDetails);
        return new ModelUsage(input, output, Cache: cache)
        {
            ReasoningTokens = ReasoningTokenUsage.Read(outputDetails, "reasoning_tokens", output),
        };
    }

    private static async Task<Exception> CreateFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        var statusCode = response.StatusCode;
        var details = await ReadErrorDetailsAsync(response.Content, cancellationToken).ConfigureAwait(false);
        var suffix = details is null ? string.Empty : $" {details}";
        return statusCode switch
        {
            HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                new TransientModelException($"Codex returned transient HTTP {(int)statusCode}.{suffix}"),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new ModelProviderException($"Codex authentication is missing, expired, or unauthorized.{suffix}"),
            _ => new ModelProviderException($"Codex rejected the request with HTTP {(int)statusCode}.{suffix}"),
        };
    }

    private static async Task<string?> ReadErrorDetailsAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        const int maximumCharacters = 4096;
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using StreamReader reader = new(stream, Encoding.UTF8);
            var body = new StringBuilder(maximumCharacters);
            var buffer = new char[1024];
            while (body.Length <= maximumCharacters)
            {
                var remaining = maximumCharacters + 1 - body.Length;
                var count = await reader.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (count is 0)
                {
                    break;
                }

                body.Append(buffer, 0, count);
            }

            if (body.Length is 0 or > maximumCharacters)
            {
                return null;
            }

            using var document = JsonDocument.Parse(body.ToString());
            return ReadErrorIdentifiers(document.RootElement);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            return null;
        }
    }

    private static string? ReadErrorIdentifiers(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var error = root.TryGetProperty("error", out var nestedError)
            && nestedError.ValueKind == JsonValueKind.Object
            ? nestedError
            : root;
        var code = GetString(error, "code");
        var parameter = GetString(error, "param");
        var parts = new[]
        {
            IsSafeErrorIdentifier(code, 64) ? $"Code: {code}." : null,
            IsSafeErrorIdentifier(parameter, 128) ? $"Parameter: {parameter}." : null,
        }.Where(part => !string.IsNullOrWhiteSpace(part));
        var details = string.Join(' ', parts);
        return details.Length == 0 ? null : details;
    }

    private static bool IsSafeErrorIdentifier(string? value, int maximumLength)
    {
        return value is { Length: > 0 } && value.Length <= maximumLength
            && value.All(character => char.IsAsciiLetterOrDigit(character)
                || character is '_' or '-' or '.');
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        return statusCode is
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
    }

    private static string ToProviderReasoning(ReasoningLevel level)
    {
        return level.Value;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static long GetInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var result) ? result : 0;
    }

    private static long? GetNullableInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var result)
            ? result
            : null;
    }

    private sealed record PendingCodexToolCall(string? Name, string? Arguments);
}
