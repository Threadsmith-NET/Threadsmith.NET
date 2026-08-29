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
    public async IAsyncEnumerable<ModelChunk> StreamAsync(
        ModelStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ContainsSensitiveData
            && _profile.SensitiveDataPolicy != ModelSensitiveDataPolicy.Allowed)
        {
            throw new ModelProviderException(
                $"Model profile '{_profile.Name}' prohibits sensitive request content.");
        }

        var profileOutputLimit = _profile.EffectiveRequestOutputTokenReserve;
        if (request.MaximumOutputTokens is { } maximumOutputTokens
            && (maximumOutputTokens <= 0 || maximumOutputTokens > profileOutputLimit))
        {
            throw new ModelProviderException(
                $"The requested output ceiling must be between 1 and the resolved profile request reserve of "
                + $"{profileOutputLimit} tokens.");
        }

        var canonicalTools = ModelToolCanonicalizer.Canonicalize(request.Tools);
        var toolNameMap = ModelToolWireNameMap.Create(canonicalTools);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_profile.Timeout);
        var accessToken = AccessToken;
        var replayedAfterAuthenticationRejection = false;
        var attempt = 0;
        while (true)
        {
            attempt++;
            using var message = CreateRequest(
                request,
                accessToken,
                canonicalTools,
                toolNameMap);
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new ModelProviderTimeoutException("The Codex Responses request timed out.", exception);
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    using StreamReader reader = new(stream);
                    await foreach (var chunk in ReadEventsAsync(reader, toolNameMap, timeout.Token).ConfigureAwait(false))
                    {
                        yield return chunk;
                    }

                    yield break;
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
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

                if (IsTransient(response.StatusCode) && attempt < _profile.RetryPolicy.MaxAttempts)
                {
                    try
                    {
                        await Task.Delay(_profile.RetryPolicy.Delay, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new ModelProviderTimeoutException("The Codex Responses request timed out.", exception);
                    }

                    continue;
                }

                throw await CreateFailureAsync(response, timeout.Token).ConfigureAwait(false);
            }
        }
    }

    private HttpRequestMessage CreateRequest(
        ModelStreamRequest request,
        string accessToken,
        IReadOnlyList<ModelToolDefinition> canonicalTools,
        ModelToolWireNameMap toolNameMap)
    {
        JsonObject body = new()
        {
            ["model"] = _profile.ModelId,
            ["store"] = false,
            ["stream"] = true,
            ["instructions"] = "You are Threadsmith.NET's coding model. Follow the host-owned tool and repository policy.",
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
        var accountId = OpenAiCodexTokenClaims.TryGetAccountId(accessToken);
        if (accountId is not null)
        {
            message.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
        }

        return message;
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
        foreach (var message in request.Messages)
        {
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
                    ["call_id"] = message.ToolCallId,
                    ["output"] = content,
                });
                continue;
            }

            var role = message.Role switch
            {
                ModelMessageRole.System => "system",
                ModelMessageRole.Developer => "developer",
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
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = contentType, ["text"] = content },
                },
            });
        }

        return input;
    }

    private static async IAsyncEnumerable<ModelChunk> ReadEventsAsync(
        StreamReader reader,
        ModelToolWireNameMap toolNameMap,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var pendingToolCalls = new List<PendingCodexToolCall>();
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
                        yield return new ModelChunk { Text = text };
                    }

                    break;
                case "response.reasoning_summary_text.delta":
                case "response.reasoning_text.delta":
                    if (GetString(root, "delta") is { Length: > 0 } reasoning)
                    {
                        yield return new ModelChunk { Reasoning = reasoning };
                    }

                    break;
                case "response.output_item.done":
                    if (root.TryGetProperty("item", out var item)
                        && string.Equals(GetString(item, "type"), "function_call", StringComparison.Ordinal))
                    {
                        pendingToolCalls.Add(new PendingCodexToolCall(
                            GetString(item, "name"),
                            GetString(item, "arguments")));
                    }

                    break;
                case "response.completed":
                    var usage = TryReadUsage(root);
                    if (usage is not null)
                    {
                        yield return new ModelChunk { Usage = usage };
                    }

                    var toolOutputs = CreateCodexToolOutputs(pendingToolCalls, toolNameMap);
                    foreach (var output in toolOutputs)
                    {
                        yield return new ModelChunk { Output = output };
                    }

                    yield return new ModelChunk
                    {
                        FinishReason = toolOutputs.Count > 0 ? ModelFinishReason.ToolCalls : ModelFinishReason.Stop,
                    };
                    pendingToolCalls.Clear();
                    break;
                case "response.failed":
                case "error":
                    throw new ModelProviderException("The Codex Responses stream reported a provider error.");
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
        return new ModelUsage(input, output, Cache: cache);
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
            var root = document.RootElement;
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
            var message = GetString(error, "message") ?? GetString(root, "detail");
            var parts = new[]
            {
                code is null ? null : $"Code: {code}.",
                parameter is null ? null : $"Parameter: {parameter}.",
                message,
            }.Where(part => !string.IsNullOrWhiteSpace(part));
            var details = string.Join(' ', parts);
            return details.Length == 0 ? null : details;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            return null;
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode)
    {
        return statusCode is
        HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
    }

    private static string ToProviderReasoning(ReasoningLevel level)
    {
        return level switch
        {
            ReasoningLevel.None => "none",
            ReasoningLevel.Minimal => "minimal",
            ReasoningLevel.Low => "low",
            ReasoningLevel.Medium => "medium",
            ReasoningLevel.High => "high",
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unknown reasoning level."),
        };
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
