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

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_profile.Timeout);
        string accessToken = AccessToken;
        bool replayedAfterAuthenticationRejection = false;
        int attempt = 0;
        while (true)
        {
            attempt++;
            using HttpRequestMessage message = CreateRequest(request, accessToken);
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
                    await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    using StreamReader reader = new(stream);
                    await foreach (ModelChunk chunk in ReadEventsAsync(reader, timeout.Token).ConfigureAwait(false))
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

                throw CreateFailure(response.StatusCode);
            }
        }
    }

    private HttpRequestMessage CreateRequest(ModelStreamRequest request, string accessToken)
    {
        JsonObject body = new()
        {
            ["model"] = _profile.ModelId,
            ["store"] = false,
            ["stream"] = true,
            ["max_output_tokens"] = _profile.EffectiveRequestOutputTokenReserve,
            ["instructions"] = "You are Threadsmith.NET's coding model. Follow the host-owned tool and repository policy.",
            ["input"] = CreateInput(request),
            ["include"] = new JsonArray("reasoning.encrypted_content"),
            ["parallel_tool_calls"] = true,
            ["reasoning"] = new JsonObject
            {
                ["effort"] = ToProviderReasoning(request.ReasoningLevel),
                ["summary"] = "auto",
            },
            ["text"] = new JsonObject { ["verbosity"] = "medium" },
        };

        if (request.Tools.Count > 0)
        {
            bool hasStrictTools = false;
            JsonArray tools = [];
            foreach (ModelToolDefinition tool in ModelToolCanonicalizer.Canonicalize(request.Tools))
            {
                string? strictSchema = ModelToolStrictSchemaProjector.TryCreateStrictFunctionSchema(
                    tool.Name,
                    tool.ArgumentsJsonSchema);
                hasStrictTools |= strictSchema is not null;
                tools.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = JsonNode.Parse(strictSchema ?? tool.ArgumentsJsonSchema),
                    ["strict"] = strictSchema is not null,
                });
            }

            if (hasStrictTools)
            {
                body["parallel_tool_calls"] = false;
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
        string? accountId = OpenAiCodexTokenClaims.TryGetAccountId(accessToken);
        if (accountId is not null)
        {
            message.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
        }

        return message;
    }

    private static JsonArray CreateInput(ModelStreamRequest request)
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
        foreach (ModelMessage message in request.Messages)
        {
            string content = string.Concat(message.Content.Select(part => part.Content));
            if (message.Role == ModelMessageRole.Assistant
                && message.ToolCallId is not null
                && message.ToolName is not null)
            {
                input.Add(new JsonObject
                {
                    ["type"] = "function_call",
                    ["call_id"] = message.ToolCallId,
                    ["name"] = message.ToolName,
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

            string role = message.Role switch
            {
                ModelMessageRole.System => "system",
                ModelMessageRole.Developer => "developer",
                ModelMessageRole.User => "user",
                ModelMessageRole.Assistant => "assistant",
                _ => throw new InvalidOperationException(
                    $"Structured model message '{message.SectionId}' has invalid tool correlation."),
            };
            string contentType = message.Role == ModelMessageRole.Assistant
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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        bool sawToolCall = false;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string payload = line.AsSpan(5).TrimStart().ToString();
            if (payload.Length == 0 || string.Equals(payload, "[DONE]", StringComparison.Ordinal))
            {
                continue;
            }

            using var document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            string? type = GetString(root, "type");
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
                    if (root.TryGetProperty("item", out JsonElement item)
                        && string.Equals(GetString(item, "type"), "function_call", StringComparison.Ordinal)
                        && GetString(item, "name") is { Length: > 0 } name)
                    {
                        sawToolCall = true;
                        yield return new ModelChunk
                        {
                            Output = new ToolRequestModelOutput(name, GetString(item, "arguments") ?? "{}"),
                        };
                    }

                    break;
                case "response.completed":
                    ModelUsage? usage = TryReadUsage(root);
                    if (usage is not null)
                    {
                        yield return new ModelChunk { Usage = usage };
                    }

                    yield return new ModelChunk
                    {
                        FinishReason = sawToolCall ? ModelFinishReason.ToolCalls : ModelFinishReason.Stop,
                    };
                    break;
                case "response.failed":
                case "error":
                    throw new ModelProviderException("The Codex Responses stream reported a provider error.");
            }
        }
    }

    private static ModelUsage? TryReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("response", out JsonElement response)
            || !response.TryGetProperty("usage", out JsonElement usage))
        {
            return null;
        }

        long input = GetInt64(usage, "input_tokens");
        long output = GetInt64(usage, "output_tokens");
        long? cachedInput = null;
        if (usage.TryGetProperty("input_tokens_details", out JsonElement details))
        {
            cachedInput = GetNullableInt64(details, "cached_tokens");
        }

        if (cachedInput is < 0)
        {
            throw new ModelProviderException("The Codex stream returned negative cache token usage.");
        }

        ModelCacheUsage? cache = cachedInput is null
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

    private static Exception CreateFailure(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout =>
                new TransientModelException($"Codex returned transient HTTP {(int)statusCode}."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new ModelProviderException("Codex authentication is missing, expired, or unauthorized."),
            _ => new ModelProviderException($"Codex rejected the request with HTTP {(int)statusCode}."),
        };
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
        return element.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static long GetInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) && value.TryGetInt64(out long result) ? result : 0;
    }

    private static long? GetNullableInt64(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) && value.TryGetInt64(out long result)
            ? result
            : null;
    }
}
