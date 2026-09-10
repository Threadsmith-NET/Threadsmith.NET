namespace Threadsmith.AnthropicProvider.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.Anthropic;

/// <summary>Deterministic native HTTP/SSE fixtures shared only within this test project.</summary>
internal static class TestAnthropic
{
    /// <summary>Creates or captures a deterministic native protocol fixture.</summary>
    internal static ModelProfile Profile(bool thinking = false) => new()
    {
        Id = new ModelProfileId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        Name = "native fixture",
        Provider = "anthropic",
        ModelId = "claude-test",
        Endpoint = AnthropicProviderRegistration.MessagesEndpoint,
        ContextWindow = 200000,
        MaximumOutputTokens = 8192,
        RequestOutputTokenReserve = 4096,
        Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true, StructuredOutput = true },
        SupportedReasoningLevels = thinking ? [ReasoningLevel.None, ReasoningLevel.Low, ReasoningLevel.High] : [ReasoningLevel.None],
        RetryPolicy = new ModelRetryPolicy { MaxAttempts = 3, Delay = TimeSpan.Zero },
    };

    /// <summary>Creates or captures a deterministic native protocol fixture.</summary>
    internal static AnthropicModelCompatibility Compatibility(bool thinking = false) => new()
    {
        ModelId = "claude-test",
        SupportsStrictSchemas = true,
        ThinkingMode = thinking ? AnthropicThinkingMode.Adaptive : AnthropicThinkingMode.Disabled,
        SupportsReasoningOff = true,
        Prices = new AnthropicModelPrices
        {
            InputPerMillionTokens = 2,
            CacheWritePerMillionTokens = 2.5m,
            CacheReadPerMillionTokens = 0.2m,
            OutputPerMillionTokens = 10,
        },
    };

    /// <summary>Creates or captures a deterministic native protocol fixture.</summary>
    internal static ModelStreamRequest Request(bool thinking = false) => new()
    {
        RunId = new RunId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
        ResolvedProfileId = Profile().Id,
        Input = "legacy must not duplicate",
        Messages = [Message(ModelMessageRole.User, "current-user", "hello")],
        ReasoningLevel = thinking ? ReasoningLevel.Low : ReasoningLevel.None,
    };

    /// <summary>Creates or captures a deterministic native protocol fixture.</summary>
    internal static ModelToolDefinition Tool(string name = "lookup", string schema = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}") => new()
    {
        Name = name,
        Description = "Fixture lookup.",
        ArgumentsJsonSchema = schema,
    };

    /// <summary>Creates or captures a deterministic native protocol fixture.</summary>
    internal static ModelMessage Message(ModelMessageRole role, string section, string text, string? id = null, string? tool = null, int? round = null, bool? error = null) => new()
    {
        Role = role,
        SectionId = section,
        Content = [new ModelContentPart { Content = text }],
        ToolCallId = id,
        ToolName = tool,
        ModelRound = round,
        IsError = error,
    };

    /// <summary>Creates or captures a deterministic native protocol fixture.</summary>
    internal static string Event(string type, object body) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(body) + "\n\n";

    /// <summary>Creates or captures a deterministic native protocol fixture.</summary>
    internal static string Start(object? usage = null) => Event("message_start", new
    {
        type = "message_start",
        message = new { id = "message_fixture", type = "message", role = "assistant", model = "claude-test", content = Array.Empty<object>(), usage = usage ?? new { input_tokens = 100, output_tokens = 0 } },
    });

    /// <summary>Creates or captures a deterministic native protocol fixture.</summary>
    internal static string Block(int index, object block) => Event("content_block_start", new { type = "content_block_start", index, content_block = block });

    /// <summary>Creates or captures a deterministic native protocol fixture.</summary>
    internal static string Delta(int index, object delta) => Event("content_block_delta", new { type = "content_block_delta", index, delta });

    /// <summary>Creates or captures a deterministic native protocol fixture.</summary>
    internal static string Close(int index) => Event("content_block_stop", new { type = "content_block_stop", index });

    /// <summary>Creates or captures a deterministic native protocol fixture.</summary>
    internal static string End(string reason = "end_turn", object? usage = null, bool stop = true) => Event("message_delta", new { type = "message_delta", delta = new { stop_reason = reason, stop_sequence = (string?)null }, usage = usage ?? new { output_tokens = 25 } })
        + (stop ? Event("message_stop", new { type = "message_stop" }) : string.Empty);

    /// <summary>Creates or captures a deterministic native protocol fixture.</summary>
    internal static string TextStream(string text = "hello", object? startUsage = null, object? finalUsage = null) => Start(startUsage)
        + Block(0, new { type = "text", text = string.Empty })
        + Delta(0, new { type = "text_delta", text }) + Close(0) + End(usage: finalUsage);

    /// <summary>Creates or captures a deterministic native protocol fixture.</summary>
    internal static string ToolStream(string arguments = "{}", bool thinking = false, bool secondTool = false, string stopReason = "tool_use", bool messageStop = true)
    {
        var body = new StringBuilder(Start(new { input_tokens = 100, cache_creation_input_tokens = 200, cache_read_input_tokens = 300, output_tokens = 10 }));
        var index = 0;
        if (thinking)
        {
            body.Append(Block(index, new { type = "thinking", thinking = string.Empty, signature = string.Empty }));
            body.Append(Delta(index, new { type = "thinking_delta", thinking = "private summary" }));
            body.Append(Delta(index, new { type = "signature_delta", signature = "signed-canary-A" }));
            body.Append(Delta(index, new { type = "signature_delta", signature = "B" }));
            body.Append(Close(index++));
            body.Append(Block(index, new { type = "redacted_thinking", data = "redacted-canary" })).Append(Close(index++));
            body.Append(Block(index, new { type = "text", text = "original visible text" })).Append(Close(index++));
        }

        body.Append(Block(index, new { type = "tool_use", id = "wire_one", name = "lookup", input = new { } }));
        if (arguments.Length > 0)
        {
            var middle = arguments.Length / 2;
            body.Append(Delta(index, new { type = "input_json_delta", partial_json = arguments[..middle] }));
            body.Append(Delta(index, new { type = "input_json_delta", partial_json = arguments[middle..] }));
        }

        body.Append(Close(index++));
        if (secondTool)
        {
            body.Append(Block(index, new { type = "tool_use", id = "wire_two", name = "lookup", input = new { } })).Append(Close(index));
        }

        return body.Append(End(stopReason, stop: messageStop)).ToString();
    }

    /// <summary>Creates or captures a deterministic native protocol fixture.</summary>
    internal static async Task<List<ModelChunk>> CollectAsync(IModelProvider provider, ModelStreamRequest request, CancellationToken cancellationToken = default)
    {
        var result = new List<ModelChunk>();
        await foreach (var chunk in provider.StreamAsync(request, cancellationToken))
        {
            if (chunk.ResponseEnvelope is { } envelope)
            {
                request.TransientState?.Accept(request, envelope);
            }

            result.Add(chunk);
        }

        return result;
    }
}

/// <summary>Captures SDK-created requests and returns synthetic response content without sockets.</summary>
internal sealed class TestAnthropicHandler : HttpMessageHandler
{
    private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> _respond;

    /// <summary>Initializes a new instance of the <see cref="TestAnthropicHandler"/> class.</summary>
    internal TestAnthropicHandler(params string[] streams)
        : this((attempt, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(streams[Math.Min(attempt, streams.Length - 1)], Encoding.UTF8, "text/event-stream") }))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="TestAnthropicHandler"/> class.</summary>
    internal TestAnthropicHandler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    /// <summary>Creates or captures a deterministic native protocol fixture.</summary>
    internal List<JsonElement> Requests { get; } = [];

    /// <summary>Creates or captures a deterministic native protocol fixture.</summary>
    internal List<Uri?> Uris { get; } = [];

    /// <summary>Creates or captures a deterministic native protocol fixture.</summary>
    internal List<string?> Keys { get; } = [];

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        using var body = JsonDocument.Parse(text);
        Requests.Add(body.RootElement.Clone());
        Uris.Add(request.RequestUri);
        Keys.Add(request.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : null);
        return await _respond(Requests.Count - 1, cancellationToken);
    }
}
