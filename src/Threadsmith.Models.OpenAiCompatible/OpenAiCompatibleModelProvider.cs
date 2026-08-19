namespace Threadsmith.Models.OpenAiCompatible;

using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Streams chat completions from a configured OpenAI-compatible HTTP endpoint.</summary>
internal sealed class OpenAiCompatibleModelProvider : IModelProvider
{
    private const int DefaultMaximumStreamedCharacters = 8 * 1024 * 1024;
    private const int MaximumAccumulatedToolCalls = 256;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string? _apiKey;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly HttpClient _httpClient;
    private readonly int _maximumStreamedCharacters;
    private readonly ModelProfile _profile;
    private readonly OpenAiReasoningCompatibilityConfiguration? _reasoningCompatibility;

    /// <summary>Initializes a new instance of the <see cref="OpenAiCompatibleModelProvider"/> class.</summary>
    /// <remarks>The API key is resolved by the composition root and is never persisted by this type.</remarks>
    public OpenAiCompatibleModelProvider(
        HttpClient httpClient,
        ModelProfile profile,
        string? apiKey = null,
        IReadOnlyDictionary<string, string>? headers = null,
        OpenAiReasoningCompatibilityConfiguration? reasoningCompatibility = null,
        int maximumStreamedCharacters = DefaultMaximumStreamedCharacters)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumStreamedCharacters, 1);
        if (!string.Equals(profile.Provider, "openai-compatible", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(profile.Provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Provider '{profile.Provider}' is not OpenAI-compatible.",
                nameof(profile));
        }

        _httpClient = httpClient;
        _maximumStreamedCharacters = maximumStreamedCharacters;
        _profile = profile;
        _apiKey = apiKey;
        _headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _reasoningCompatibility = reasoningCompatibility;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ModelChunk> StreamAsync(
        ModelStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ResolvedProfileId is { } resolvedProfileId
            && resolvedProfileId != _profile.Id)
        {
            throw new ModelProviderException(
                $"Resolved model profile '{resolvedProfileId}' does not match adapter profile "
                + $"'{_profile.Id}'.");
        }

        if (!_profile.Capabilities.Streaming)
        {
            throw new InvalidOperationException(
                $"Model profile '{_profile.Name}' does not advertise streaming support.");
        }

        if (request.Tools.Count > 0 && !_profile.Capabilities.ToolCalls)
        {
            throw new InvalidOperationException(
                $"Model profile '{_profile.Name}' does not advertise tool-call support.");
        }

        if (request.ContainsSensitiveData
            && _profile.SensitiveDataPolicy != ModelSensitiveDataPolicy.Allowed)
        {
            throw new ModelProviderException(
                $"Model profile '{_profile.Name}' prohibits sensitive request content.");
        }

        var canonicalTools = ModelToolCanonicalizer.Canonicalize(request.Tools);
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(_profile.Timeout);
        HttpResponseMessage? response = null;
        for (int attempt = 1; attempt <= _profile.RetryPolicy.MaxAttempts; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, _profile.Endpoint);
            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            foreach ((string name, string value) in _headers)
            {
                message.Headers.TryAddWithoutValidation(name, value);
            }

            bool hasStrictTools = false;
            var tools = new List<OpenAiTool>(canonicalTools.Count);
            foreach (var definition in canonicalTools)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);
                ArgumentException.ThrowIfNullOrWhiteSpace(definition.Description);
                ArgumentException.ThrowIfNullOrWhiteSpace(definition.ArgumentsJsonSchema);
                JsonDocument schema;
                try
                {
                    schema = JsonDocument.Parse(definition.ArgumentsJsonSchema);
                }
                catch (JsonException exception)
                {
                    throw new InvalidOperationException(
                        $"Tool '{definition.Name}' has an invalid JSON argument schema.",
                        exception);
                }

                using (schema)
                {
                    if (schema.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidOperationException(
                            $"Tool '{definition.Name}' argument schema must be a JSON object.");
                    }

                    string? strictSchema = ModelToolStrictSchemaProjector.TryCreateStrictFunctionSchema(
                        definition.Name,
                        definition.ArgumentsJsonSchema);
                    using var providerSchema = strictSchema is null
                        ? schema
                        : JsonDocument.Parse(strictSchema);
                    hasStrictTools |= strictSchema is not null;
                    tools.Add(new OpenAiTool
                    {
                        Function = new OpenAiFunction
                        {
                            Name = definition.Name,
                            Description = definition.Description,
                            Parameters = providerSchema.RootElement.Clone(),
                            Strict = strictSchema is null ? null : true,
                        },
                    });
                }
            }

            var body = new OpenAiChatRequest
            {
                Model = _profile.ModelId,
                Messages = CreateMessages(request),
                Stream = true,
                StreamOptions = new OpenAiStreamOptions { IncludeUsage = true },
                MaximumOutputTokens = _profile.MaximumOutputTokens,
                Temperature = _profile.Temperature,
                Seed = request.Seed,
                ResponseFormat = request.RequiredCapabilities.StructuredOutput
                    ? new OpenAiResponseFormat { Type = "json_object" }
                    : null,
                ReasoningEffort = _reasoningCompatibility is null
                    ? ResolveReasoningEffort(request)
                    : null,
                Tools = tools.Count == 0 ? null : tools,
                ToolChoice = tools.Count == 0 ? null : "auto",
                ParallelToolCalls = hasStrictTools ? false : null,
            };
            var requestBody = JsonSerializer.SerializeToNode(body, _jsonOptions)?.AsObject()
                ?? throw new InvalidOperationException("The model request could not be serialized.");
            ApplyReasoningCompatibility(requestBody, request);
            message.Content = new StringContent(
                requestBody.ToJsonString(_jsonOptions),
                Encoding.UTF8,
                "application/json");
            try
            {
                response = await _httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCancellation.Token);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw CreateTimeoutException(exception);
            }
            catch (HttpRequestException exception) when (IsTransientTransportFailure(exception))
            {
                if (attempt == _profile.RetryPolicy.MaxAttempts)
                {
                    throw new TransientModelException(
                        $"Model endpoint transport remained unavailable after {attempt} attempts.",
                        exception);
                }

                await DelayBeforeRetryAsync(requestCancellation.Token, cancellationToken);
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                break;
            }

            var statusCode = response.StatusCode;
            response.Dispose();
            response = null;
            bool isTransient = statusCode is HttpStatusCode.TooManyRequests
                or HttpStatusCode.ServiceUnavailable
                || (int)statusCode == 529;
            if (!isTransient || attempt == _profile.RetryPolicy.MaxAttempts)
            {
                if (isTransient)
                {
                    throw new TransientModelException(
                        $"Model endpoint remained unavailable after {attempt} attempts "
                        + $"(HTTP {(int)statusCode}).");
                }

                throw new ModelProviderException(
                    $"Model endpoint rejected the request with HTTP {(int)statusCode}.");
            }

            await DelayBeforeRetryAsync(requestCancellation.Token, cancellationToken);
        }

        if (response is null)
        {
            throw new InvalidOperationException("Model endpoint returned no response.");
        }

        Stream stream;
        try
        {
            stream = await response.Content.ReadAsStreamAsync(requestCancellation.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            response.Dispose();
            throw CreateTimeoutException(exception);
        }

        using (response)
        await using (stream)
        using (var reader = new StreamReader(stream, Encoding.UTF8))
        {
            var toolCalls = new Dictionary<int, ToolCallAccumulator>();
            int completionCharacters = 0;
            int streamedOutputCharacters = 0;
            bool usageReported = false;
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(requestCancellation.Token);
                }
                catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    throw CreateTimeoutException(exception);
                }

                if (line is null)
                {
                    break;
                }

                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string payload = line.AsSpan(5).Trim().ToString();
                if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
                {
                    break;
                }

                OpenAiStreamEnvelope envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<OpenAiStreamEnvelope>(payload, _jsonOptions)
                        ?? throw new JsonException("The provider chunk was empty.");
                }
                catch (JsonException exception)
                {
                    throw new MalformedModelOutputException(
                        "The provider returned a malformed streaming chunk.",
                        exception);
                }

                if (envelope.Usage is { } reportedUsage)
                {
                    if (reportedUsage.PromptTokens < 0 || reportedUsage.CompletionTokens < 0)
                    {
                        throw new MalformedModelOutputException(
                            "The provider returned negative token usage.");
                    }

                    usageReported = true;
                    long conservativeInputTokens = Math.Max(
                        reportedUsage.PromptTokens,
                        request.WireEstimate?.WireInputTokens ?? EstimateTokenCount(request.Input.Length));
                    long conservativeOutputTokens = Math.Max(
                        reportedUsage.CompletionTokens,
                        EstimateTokenCount(completionCharacters));
                    yield return new ModelChunk
                    {
                        Usage = new ModelUsage(
                            reportedUsage.PromptTokens,
                            reportedUsage.CompletionTokens,
                            _profile.Cost.Calculate(
                                conservativeInputTokens,
                                conservativeOutputTokens),
                            Cache: CreateCacheUsage(reportedUsage)),
                    };
                }

                foreach (var choice in envelope.Choices)
                {
                    string? reasoning = ResolveReasoningDelta(choice.Delta);
                    if (reasoning is { Length: > 0 })
                    {
                        AddCompletionCharacters(
                            reasoning.Length,
                            _maximumStreamedCharacters,
                            ref streamedOutputCharacters);
                        completionCharacters += reasoning.Length;
                        yield return new ModelChunk { Reasoning = reasoning };
                    }

                    if (choice.Delta.Content is { Length: > 0 } content)
                    {
                        AddCompletionCharacters(
                            content.Length,
                            _maximumStreamedCharacters,
                            ref streamedOutputCharacters);
                        completionCharacters += content.Length;
                        yield return new ModelChunk { Text = content };
                    }

                    foreach (var toolCall in choice.Delta.ToolCalls)
                    {
                        if (!toolCalls.TryGetValue(toolCall.Index, out var accumulator))
                        {
                            if (toolCalls.Count >= MaximumAccumulatedToolCalls)
                            {
                                throw new MalformedModelOutputException(
                                    "The provider exceeded the maximum accumulated tool-call count.");
                            }

                            accumulator = new ToolCallAccumulator();
                            toolCalls.Add(toolCall.Index, accumulator);
                        }

                        accumulator.AppendId(
                            toolCall.Id,
                            _maximumStreamedCharacters,
                            ref streamedOutputCharacters);
                        if (toolCall.Function is { } function)
                        {
                            accumulator.AppendName(
                                function.Name,
                                _maximumStreamedCharacters,
                                ref streamedOutputCharacters);
                            accumulator.AppendArguments(
                                function.Arguments,
                                _maximumStreamedCharacters,
                                ref streamedOutputCharacters);
                            completionCharacters += (function.Name?.Length ?? 0)
                                + (function.Arguments?.Length ?? 0);
                        }
                    }

                    if (choice.FinishReason is { } finishReason)
                    {
                        foreach (var toolChunk in DrainToolCalls(toolCalls, canonicalTools))
                        {
                            yield return toolChunk;
                        }

                        yield return new ModelChunk
                        {
                            FinishReason = finishReason switch
                            {
                                "stop" => ModelFinishReason.Stop,
                                "tool_calls" => ModelFinishReason.ToolCalls,
                                "length" => ModelFinishReason.Length,
                                _ => ModelFinishReason.Other,
                            },
                        };
                    }
                }
            }

            foreach (var toolChunk in DrainToolCalls(toolCalls, canonicalTools))
            {
                yield return toolChunk;
            }

            if (!usageReported)
            {
                long inputTokens = request.WireEstimate?.WireInputTokens
                    ?? EstimateTokenCount(request.Input.Length);
                long outputTokens = EstimateTokenCount(completionCharacters);
                yield return new ModelChunk
                {
                    Usage = new ModelUsage(
                        inputTokens,
                        outputTokens,
                        _profile.Cost.Calculate(inputTokens, outputTokens),
                        IsEstimate: true),
                };
            }
        }
    }

    private static IReadOnlyList<OpenAiMessage> CreateMessages(ModelStreamRequest request)
    {
        if (request.Messages.Count == 0)
        {
            return [new OpenAiMessage { Role = "user", Content = request.Input }];
        }

        var messages = new List<OpenAiMessage>(request.Messages.Count);
        foreach (var message in request.Messages)
        {
            string content = string.Concat(message.Content.Select(part => part.Content));
            switch (message.Role)
            {
                case ModelMessageRole.System:
                    messages.Add(new OpenAiMessage { Role = "system", Content = content });
                    break;
                case ModelMessageRole.Developer:
                    AddUserMessage(
                        messages,
                        $"<threadsmith_host_context>\n{content}\n</threadsmith_host_context>");
                    break;
                case ModelMessageRole.User:
                    AddUserMessage(messages, content);
                    break;
                case ModelMessageRole.Assistant when message.ToolCallId is not null
                    && message.ToolName is not null:
                    messages.Add(new OpenAiMessage
                    {
                        Role = "assistant",
                        ToolCalls =
                        [
                            new OpenAiMessageToolCall
                            {
                                Id = message.ToolCallId,
                                Function = new OpenAiMessageFunctionCall
                                {
                                    Name = message.ToolName,
                                    Arguments = content,
                                },
                            },
                        ],
                    });
                    break;
                case ModelMessageRole.Assistant:
                    messages.Add(new OpenAiMessage { Role = "assistant", Content = content });
                    break;
                case ModelMessageRole.Tool when message.ToolCallId is not null:
                    messages.Add(new OpenAiMessage
                    {
                        Role = "tool",
                        Content = content,
                        ToolCallId = message.ToolCallId,
                    });
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Structured model message '{message.SectionId}' has invalid tool correlation.");
            }
        }

        return messages;
    }

    private static void AddUserMessage(List<OpenAiMessage> messages, string content)
    {
        if (messages.Count > 0 && messages[^1].Role == "user")
        {
            var previous = messages[^1];
            messages[^1] = previous with
            {
                Content = string.Concat(previous.Content, "\n\n", content),
            };
            return;
        }

        messages.Add(new OpenAiMessage { Role = "user", Content = content });
    }

    private static ModelCacheUsage CreateCacheUsage(OpenAiUsage usage)
    {
        long? cacheReadTokens = usage.PromptTokenDetails?.CachedTokens
            ?? usage.CacheReadInputTokens;
        long? cacheWriteTokens = usage.CacheCreationInputTokens;
        if (cacheReadTokens is < 0 || cacheWriteTokens is < 0)
        {
            throw new MalformedModelOutputException("The provider returned negative cache token usage.");
        }

        bool reported = cacheReadTokens is not null || cacheWriteTokens is not null;
        return new ModelCacheUsage
        {
            Availability = reported
                ? CacheUsageAvailability.Reported
                : CacheUsageAvailability.Unavailable,
            CacheReadTokens = cacheReadTokens,
            CacheWriteTokens = cacheWriteTokens,
            ReadInputSemantics = cacheReadTokens is not null
                ? CacheReadInputSemantics.IncludedInInput
                : CacheReadInputSemantics.Unknown,
            Provenance = reported ? "openai-compatible:usage" : null,
        };
    }

    private static long EstimateTokenCount(long characterCount)
    {
        return characterCount == 0 ? 0 : Math.Max(1, (characterCount + 3L) / 4L);
    }

    /// <summary>
    /// Resolves the <c>reasoning_effort</c> string to send in the request body, or <see langword="null"/>
    /// to omit the field when the profile is a non-reasoning model (supports only <see cref="ReasoningLevel.None"/>)
    /// or the requested level is not supported by the profile.
    /// </summary>
    /// <param name="request">The stream request carrying the reasoning level.</param>
    /// <returns>The lowercase reasoning effort string, or <see langword="null"/> to omit.</returns>
    private string? ResolveReasoningEffort(ModelStreamRequest request)
    {
        var requested = request.ReasoningLevel;

        // Non-reasoning model (supports only None): omit the field entirely.
        if (_profile.SupportedReasoningLevels.Count == 1)
        {
            return null;
        }

        // Reasoning model but the requested level is not supported: clamp to None.
        if (!_profile.SupportsReasoningLevel(requested))
        {
            requested = ReasoningLevel.None;
        }

        return requested.ToString().ToLowerInvariant();
    }

    private void ApplyReasoningCompatibility(JsonObject body, ModelStreamRequest request)
    {
        var compatibility = _reasoningCompatibility;
        if (compatibility is null)
        {
            return;
        }

        if (!_profile.SupportsReasoningLevel(request.ReasoningLevel))
        {
            string supported = string.Join(", ", _profile.SupportedReasoningLevels);
            throw new ModelProviderException(
                $"Reasoning level '{request.ReasoningLevel}' is unsupported by model profile "
                + $"'{_profile.Name}'. Supported levels: {supported}.");
        }

        switch (compatibility.Mode)
        {
            case OpenAiReasoningControlMode.StandardEffort:
                if (request.ReasoningLevel != ReasoningLevel.None)
                {
                    body["reasoning_effort"] = request.ReasoningLevel.ToString().ToLowerInvariant();
                }

                break;
            case OpenAiReasoningControlMode.MappedEffort:
                body["reasoning_effort"] = compatibility.LevelMap[request.ReasoningLevel];
                break;
            case OpenAiReasoningControlMode.ChatTemplate:
                bool enabled = request.ReasoningLevel != ReasoningLevel.None;
                body["chat_template_kwargs"] = compatibility.ChatTemplateKind switch
                {
                    OpenAiChatTemplateKind.EnableThinkingWithPreservation => new JsonObject
                    {
                        ["enable_thinking"] = enabled,
                        ["preserve_thinking"] = true,
                    },
                    OpenAiChatTemplateKind.ThinkingWithEffort => new JsonObject
                    {
                        ["thinking"] = enabled,
                        ["reasoning_effort"] = compatibility.LevelMap[request.ReasoningLevel],
                    },
                    _ => throw new InvalidOperationException("The configured chat-template kind is invalid."),
                };
                break;
            case OpenAiReasoningControlMode.Fixed:
                ApplyFixedRequest(body, compatibility.FixedRequestKind);
                break;
            case OpenAiReasoningControlMode.Unsupported:
                if (compatibility.FixedRequestKind == OpenAiFixedRequestKind.DisableThinkingWithPreservation)
                {
                    ApplyFixedRequest(body, compatibility.FixedRequestKind);
                }

                break;
            case OpenAiReasoningControlMode.AlwaysOn:
                break;
            default:
                throw new InvalidOperationException("The configured reasoning mode is invalid.");
        }
    }

    private static void ApplyFixedRequest(JsonObject body, OpenAiFixedRequestKind? kind)
    {
        switch (kind)
        {
            case OpenAiFixedRequestKind.ThinkingEnvironmentBudget4096:
                body["LLM_ENABLE_THINKING"] = true;
                body["LLM_REASONING_BUDGET"] = 4096;
                return;
            case OpenAiFixedRequestKind.DisableThinkingWithPreservation:
                body["chat_template_kwargs"] = new JsonObject
                {
                    ["enable_thinking"] = false,
                    ["preserve_thinking"] = true,
                };
                return;
            default:
                throw new InvalidOperationException("The configured fixed reasoning request kind is invalid.");
        }
    }

    private string? ResolveReasoningDelta(OpenAiDelta delta)
    {
        return _reasoningCompatibility?.ResponseMode switch
        {
            OpenAiReasoningResponseMode.None => null,
            OpenAiReasoningResponseMode.ReasoningContent => delta.ReasoningContent,
            OpenAiReasoningResponseMode.Reasoning => delta.Reasoning,
            null => delta.Reasoning ?? delta.ReasoningContent,
            _ => throw new MalformedModelOutputException("The configured reasoning response mode is invalid."),
        };
    }

    private static IReadOnlyList<ModelChunk> DrainToolCalls(
        Dictionary<int, ToolCallAccumulator> toolCalls,
        IReadOnlyList<ModelToolDefinition> availableTools)
    {
        var chunks = new List<ModelChunk>();
        foreach (var call in toolCalls.OrderBy(item => item.Key).Select(item => item.Value))
        {
            string arguments = NormalizeToolArguments(call.Name, call.Arguments, availableTools);
            var output = new ToolRequestModelOutput(call.Name, arguments);
            ModelOutputValidator.Validate(output);
            chunks.Add(new ModelChunk { Output = output });
        }

        toolCalls.Clear();
        return chunks;
    }

    private static string NormalizeToolArguments(
        string toolName,
        string? arguments,
        IReadOnlyList<ModelToolDefinition> availableTools)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return AcceptsNoArguments(toolName, availableTools) ? "{}" : arguments ?? string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(arguments);
            return arguments;
        }
        catch (JsonException)
        {
            return AcceptsNoArguments(toolName, availableTools) ? "{}" : arguments;
        }
    }

    private static bool AcceptsNoArguments(
        string toolName,
        IReadOnlyList<ModelToolDefinition> availableTools)
    {
        var definition = availableTools.FirstOrDefault(tool =>
            string.Equals(tool.Name, toolName, StringComparison.Ordinal));
        if (definition is null)
        {
            return false;
        }

        try
        {
            using JsonDocument schema = JsonDocument.Parse(definition.ArgumentsJsonSchema);
            var root = schema.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String
                || !string.Equals(type.GetString(), "object", StringComparison.Ordinal)
                || !root.TryGetProperty("properties", out var properties)
                || properties.ValueKind != JsonValueKind.Object
                || properties.EnumerateObject().Any()
                || !root.TryGetProperty("additionalProperties", out var additionalProperties)
                || additionalProperties.ValueKind != JsonValueKind.False)
            {
                return false;
            }

            if (root.TryGetProperty("required", out var required)
                && (required.ValueKind != JsonValueKind.Array || required.GetArrayLength() > 0))
            {
                return false;
            }

            HashSet<string> acceptedKeywords = new(
            [
                "$anchor",
                "$comment",
                "$id",
                "$schema",
                "additionalProperties",
                "deprecated",
                "description",
                "examples",
                "properties",
                "readOnly",
                "required",
                "title",
                "type",
                "writeOnly",
            ],
            StringComparer.Ordinal);
            return root.EnumerateObject().All(property => acceptedKeywords.Contains(property.Name));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task DelayBeforeRetryAsync(
        CancellationToken requestCancellationToken,
        CancellationToken callerCancellationToken)
    {
        try
        {
            await Task.Delay(_profile.RetryPolicy.Delay, requestCancellationToken);
        }
        catch (OperationCanceledException exception) when (!callerCancellationToken.IsCancellationRequested)
        {
            throw CreateTimeoutException(exception);
        }
    }

    private static bool IsTransientTransportFailure(HttpRequestException exception)
    {
        return exception.HttpRequestError is HttpRequestError.NameResolutionError
            or HttpRequestError.ConnectionError
            or HttpRequestError.HttpProtocolError
            or HttpRequestError.ResponseEnded;
    }

    private ModelProviderTimeoutException CreateTimeoutException(OperationCanceledException exception)
    {
        return new(
                $"Model profile '{_profile.Name}' exceeded its {_profile.Timeout} request timeout.",
                exception);
    }

    private static void AddCompletionCharacters(
        int additionalCharacters,
        int maximumCompletionCharacters,
        ref int completionCharacters)
    {
        if (additionalCharacters > maximumCompletionCharacters - completionCharacters)
        {
            throw new MalformedModelOutputException(
                "The provider exceeded the configured maximum streamed output size.");
        }

        completionCharacters += additionalCharacters;
    }

    private sealed class ToolCallAccumulator
    {
        private readonly StringBuilder _arguments = new();
        private readonly StringBuilder _id = new();
        private readonly StringBuilder _name = new();

        public string Arguments => _arguments.ToString();

        public string Id => _id.ToString();

        public string Name => _name.ToString();

        public void AppendArguments(
            string? value,
            int maximumCompletionCharacters,
            ref int completionCharacters)
        {
            Append(_arguments, value, maximumCompletionCharacters, ref completionCharacters);
        }

        public void AppendId(
            string? value,
            int maximumCompletionCharacters,
            ref int completionCharacters)
        {
            Append(_id, value, maximumCompletionCharacters, ref completionCharacters);
        }

        public void AppendName(
            string? value,
            int maximumCompletionCharacters,
            ref int completionCharacters)
        {
            Append(_name, value, maximumCompletionCharacters, ref completionCharacters);
        }

        private static void Append(
            StringBuilder builder,
            string? value,
            int maximumCompletionCharacters,
            ref int completionCharacters)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            AddCompletionCharacters(value.Length, maximumCompletionCharacters, ref completionCharacters);
            builder.Append(value);
        }
    }

    private sealed record OpenAiChatRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("messages")]
        public IReadOnlyList<OpenAiMessage> Messages { get; init; } = [];

        [JsonPropertyName("stream")]
        public bool Stream { get; init; }

        [JsonPropertyName("stream_options")]
        public OpenAiStreamOptions? StreamOptions { get; init; }

        [JsonPropertyName("max_completion_tokens")]
        public int MaximumOutputTokens { get; init; }

        [JsonPropertyName("temperature")]
        public decimal? Temperature { get; init; }

        [JsonPropertyName("seed")]
        public int Seed { get; init; }

        [JsonPropertyName("response_format")]
        public OpenAiResponseFormat? ResponseFormat { get; init; }

        [JsonPropertyName("reasoning_effort")]
        public string? ReasoningEffort { get; init; }

        [JsonPropertyName("tools")]
        public IReadOnlyList<OpenAiTool>? Tools { get; init; }

        [JsonPropertyName("tool_choice")]
        public string? ToolChoice { get; init; }

        [JsonPropertyName("parallel_tool_calls")]
        public bool? ParallelToolCalls { get; init; }
    }

    private sealed record OpenAiTool
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = "function";

        [JsonPropertyName("function")]
        public required OpenAiFunction Function { get; init; }
    }

    private sealed record OpenAiFunction
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("description")]
        public required string Description { get; init; }

        [JsonPropertyName("parameters")]
        public required JsonElement Parameters { get; init; }

        [JsonPropertyName("strict")]
        public bool? Strict { get; init; }
    }

    private sealed record OpenAiResponseFormat
    {
        [JsonPropertyName("type")]
        public required string Type { get; init; }
    }

    private sealed record OpenAiMessage
    {
        [JsonPropertyName("role")]
        public required string Role { get; init; }

        [JsonPropertyName("content")]
        public string? Content { get; init; }

        [JsonPropertyName("tool_calls")]
        public IReadOnlyList<OpenAiMessageToolCall>? ToolCalls { get; init; }

        [JsonPropertyName("tool_call_id")]
        public string? ToolCallId { get; init; }
    }

    private sealed record OpenAiMessageToolCall
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("type")]
        public string Type { get; init; } = "function";

        [JsonPropertyName("function")]
        public required OpenAiMessageFunctionCall Function { get; init; }
    }

    private sealed record OpenAiMessageFunctionCall
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("arguments")]
        public required string Arguments { get; init; }
    }

    private sealed record OpenAiStreamOptions
    {
        [JsonPropertyName("include_usage")]
        public bool IncludeUsage { get; init; }
    }

    private sealed record OpenAiStreamEnvelope
    {
        [JsonPropertyName("choices")]
        public IReadOnlyList<OpenAiChoice> Choices { get; init; } = [];

        [JsonPropertyName("usage")]
        public OpenAiUsage? Usage { get; init; }
    }

    private sealed record OpenAiChoice
    {
        [JsonPropertyName("delta")]
        public OpenAiDelta Delta { get; init; } = new();

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; init; }
    }

    private sealed record OpenAiDelta
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }

        [JsonPropertyName("reasoning")]
        public string? Reasoning { get; init; }

        [JsonPropertyName("reasoning_content")]
        public string? ReasoningContent { get; init; }

        [JsonPropertyName("tool_calls")]
        public IReadOnlyList<OpenAiToolCallDelta> ToolCalls { get; init; } = [];
    }

    private sealed record OpenAiToolCallDelta
    {
        [JsonPropertyName("index")]
        public int Index { get; init; }

        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("function")]
        public OpenAiFunctionDelta? Function { get; init; }
    }

    private sealed record OpenAiFunctionDelta
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("arguments")]
        public string? Arguments { get; init; }
    }

    private sealed record OpenAiUsage
    {
        [JsonPropertyName("prompt_tokens")]
        public long PromptTokens { get; init; }

        [JsonPropertyName("completion_tokens")]
        public long CompletionTokens { get; init; }

        [JsonPropertyName("prompt_tokens_details")]
        public OpenAiPromptTokenDetails? PromptTokenDetails { get; init; }

        [JsonPropertyName("cache_read_input_tokens")]
        public long? CacheReadInputTokens { get; init; }

        [JsonPropertyName("cache_creation_input_tokens")]
        public long? CacheCreationInputTokens { get; init; }
    }

    private sealed record OpenAiPromptTokenDetails
    {
        [JsonPropertyName("cached_tokens")]
        public long? CachedTokens { get; init; }
    }
}
