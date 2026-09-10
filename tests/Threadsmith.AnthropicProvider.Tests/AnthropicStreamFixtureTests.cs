namespace Threadsmith.AnthropicProvider.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.Anthropic;

/// <summary>Native SDK HTTP/SSE regression fixtures for complete, truncated, and hostile streams.</summary>
public sealed class AnthropicStreamFixtureTests
{
    /// <summary>Streams fragmented text and emits normalized cumulative usage exactly once.</summary>
    [Fact]
    public async Task StreamAsync_TextAndCacheUsage_NormalizesOnce()
    {
        var handler = new TestAnthropicHandler(TestAnthropic.TextStream("hello", new { input_tokens = 100, cache_creation_input_tokens = 200, cache_read_input_tokens = 300, output_tokens = 10 }));
        using var client = new HttpClient(handler);
        var chunks = await TestAnthropic.CollectAsync(Provider(client), TestAnthropic.Request());
        Assert.Equal("hello", string.Concat(chunks.Select(chunk => chunk.Text)));
        var usage = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage;
        Assert.NotNull(usage);
        Assert.Equal(600, usage.InputTokens);
        Assert.Equal(25, usage.OutputTokens);
        Assert.Equal(0.00101m, usage.EstimatedCost);
        Assert.Equal(CacheReadInputSemantics.IncludedInInput, usage.Cache?.ReadInputSemantics);
        Assert.Equal(ModelFinishReason.Stop, chunks[^1].FinishReason);
    }

    /// <summary>Preserves the distinction between missing and reported-zero cache counters.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamAsync_CacheCounterAvailability_PreservesNull(bool reported)
    {
        object usage = reported ? new { input_tokens = 100, output_tokens = 0, cache_creation_input_tokens = 0, cache_read_input_tokens = 0 } : new { input_tokens = 100, output_tokens = 0 };
        using var client = new HttpClient(new TestAnthropicHandler(TestAnthropic.TextStream(startUsage: usage)));
        var chunks = await TestAnthropic.CollectAsync(Provider(client), TestAnthropic.Request());
        var cache = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage?.Cache;
        Assert.NotNull(cache);
        Assert.Equal(reported ? CacheUsageAvailability.Reported : CacheUsageAvailability.Unavailable, cache.Availability);
        Assert.Equal(reported ? 0L : null, cache.CacheReadTokens);
    }

    /// <summary>Empty native tool input needs no argument delta and remains an executable object.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"a\":\"fragmented\"}")]
    public async Task StreamAsync_CompleteTool_EnvelopePrecedesTools(string arguments)
    {
        using var client = new HttpClient(new TestAnthropicHandler(TestAnthropic.ToolStream(arguments, secondTool: true)));
        var chunks = await TestAnthropic.CollectAsync(Provider(client), TestAnthropic.Request() with { Tools = [TestAnthropic.Tool()] });
        var envelopeIndex = chunks.FindIndex(chunk => chunk.ResponseEnvelope is not null);
        var toolIndex = chunks.FindIndex(chunk => chunk.Output is ToolRequestModelOutput);
        Assert.True(envelopeIndex >= 0 && envelopeIndex < toolIndex);
        Assert.Equal(2, chunks.Count(chunk => chunk.Output is ToolRequestModelOutput));
        Assert.Equal(new[] { "wire_one", "wire_two" }, chunks[envelopeIndex].ResponseEnvelope?.WireToolCallIds);
        Assert.Equal(ModelFinishReason.ToolCalls, chunks[^1].FinishReason);
        chunks[envelopeIndex].ResponseEnvelope?.Dispose();
    }

    /// <summary>Every malformed tool response is rejected without an executable partial tool.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"a\":1,\"a\":2}")]
    [InlineData("{\"a\":")]
    public async Task StreamAsync_InvalidArguments_EmitsNoTool(string arguments)
    {
        using var client = new HttpClient(new TestAnthropicHandler(TestAnthropic.ToolStream(arguments)));
        var chunks = new List<ModelChunk>();
        await Assert.ThrowsAsync<MalformedInvocationException>(async () =>
        {
            await foreach (var chunk in Provider(client).StreamAsync(TestAnthropic.Request() with { Tools = [TestAnthropic.Tool()] }))
            {
                chunks.Add(chunk);
            }
        });
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.ResponseEnvelope is not null);
        Assert.Single(chunks, chunk => chunk.Usage is not null);
    }

    /// <summary>Missing message_stop never commits the signed envelope or any tool.</summary>
    [Fact]
    public async Task StreamAsync_EofAfterStopReason_RejectsIncompleteResponse()
    {
        using var client = new HttpClient(new TestAnthropicHandler(TestAnthropic.ToolStream(messageStop: false)));
        var chunks = new List<ModelChunk>();
        await Assert.ThrowsAsync<MalformedModelOutputException>(async () =>
        {
            await foreach (var chunk in Provider(client).StreamAsync(TestAnthropic.Request() with { Tools = [TestAnthropic.Tool()] }))
            {
                chunks.Add(chunk);
            }
        });
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.ResponseEnvelope is not null);
    }

    /// <summary>Length and unsupported terminal outcomes release no tools, including malformed partial input.</summary>
    [Theory]
    [InlineData("max_tokens")]
    [InlineData("model_context_window_exceeded")]
    [InlineData("pause_turn")]
    [InlineData("refusal")]
    [InlineData("future_reason")]
    public async Task StreamAsync_NonToolTerminalReason_NeverExecutesCalls(string reason)
    {
        var handler = new TestAnthropicHandler(TestAnthropic.ToolStream("{", stopReason: reason));
        using var client = new HttpClient(handler);
        var chunks = new List<ModelChunk>();
        var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var chunk in Provider(client).StreamAsync(TestAnthropic.Request() with { Tools = [TestAnthropic.Tool()] }))
            {
                chunks.Add(chunk);
            }
        });
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.ResponseEnvelope is not null || chunk.FinishReason is not null);
        var usage = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage;
        Assert.NotNull(usage);
        Assert.Equal(600, usage.InputTokens);
        Assert.Equal(25, usage.OutputTokens);
        Assert.False(usage.IsEstimate);
        Assert.Single(handler.Requests);
        Assert.Null(error.InnerException);
    }

    /// <summary>Rejects out-of-order blocks, mismatched deltas, unknown semantics, and duplicate tool identities.</summary>
    [Theory]
    [InlineData("index")]
    [InlineData("delta")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("name")]
    [InlineData("signature")]
    public async Task StreamAsync_InvalidSequence_FailsBeforeAnyTool(string kind)
    {
        var fixture = kind switch
        {
            "index" => TestAnthropic.Start() + TestAnthropic.Block(1, new { type = "text", text = string.Empty }),
            "delta" => TestAnthropic.Start() + TestAnthropic.Block(0, new { type = "text", text = string.Empty }) + TestAnthropic.Delta(0, new { type = "input_json_delta", partial_json = "{}" }),
            "unknown" => TestAnthropic.Start() + TestAnthropic.Block(0, new { type = "server_tool_use", id = "unsupported" }),
            "duplicate" => TestAnthropic.ToolStream(secondTool: true).Replace("wire_two", "wire_one", StringComparison.Ordinal),
            "name" => TestAnthropic.ToolStream().Replace("lookup", "unadvertised", StringComparison.Ordinal),
            _ => TestAnthropic.ToolStream(thinking: true).Replace("signed-canary-A", string.Empty, StringComparison.Ordinal).Replace("\"signature\":\"B\"", "\"signature\":\"\"", StringComparison.Ordinal),
        };
        using var client = new HttpClient(new TestAnthropicHandler(fixture));
        var chunks = new List<ModelChunk>();
        await Assert.ThrowsAnyAsync<MalformedModelOutputException>(async () =>
        {
            await foreach (var chunk in Provider(client).StreamAsync(TestAnthropic.Request() with { Tools = [TestAnthropic.Tool()] }))
            {
                chunks.Add(chunk);
            }
        });
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.ResponseEnvelope is not null);
    }

    /// <summary>API SSE failures after HTTP success stay sanitized and never retry an observed response.</summary>
    [Fact]
    public async Task StreamAsync_SseErrorAfterText_NoRetryAndNoLeak()
    {
        var fixture = TestAnthropic.Start() + TestAnthropic.Block(0, new { type = "text", text = "visible" })
            + TestAnthropic.Event("error", new { type = "error", error = new { type = "overloaded_error", message = "SECRET_BODY_CANARY" } });
        var handler = new TestAnthropicHandler(fixture);
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => TestAnthropic.CollectAsync(Provider(client), TestAnthropic.Request()));
        Assert.DoesNotContain("SECRET_BODY_CANARY", error.ToString(), StringComparison.Ordinal);
        Assert.Single(handler.Requests);
        Assert.Null(error.InnerException);
    }

    /// <summary>The host retries only its reviewed HTTP status set and disposes no shared transport.</summary>
    [Theory]
    [InlineData(408, 3)]
    [InlineData(409, 3)]
    [InlineData(429, 3)]
    [InlineData(500, 3)]
    [InlineData(400, 1)]
    [InlineData(401, 1)]
    [InlineData(403, 1)]
    [InlineData(404, 1)]
    [InlineData(422, 1)]
    [InlineData(302, 1)]
    public async Task StreamAsync_HttpFailure_BoundedHostRetries(int status, int attempts)
    {
        var handler = new TestAnthropicHandler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("SECRET_ERROR_BODY") }));
        using var client = new HttpClient(handler);
        var exception = await Assert.ThrowsAnyAsync<Exception>(() => TestAnthropic.CollectAsync(Provider(client), TestAnthropic.Request()));
        Assert.Equal(attempts, handler.Requests.Count);
        Assert.DoesNotContain("SECRET_ERROR_BODY", exception.ToString(), StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
    }

    /// <summary>The bridge bounds large content before native deserialization.</summary>
    [Fact]
    public async Task StreamAsync_ResponseBound_FailsBeforeTools()
    {
        var handler = new TestAnthropicHandler(TestAnthropic.ToolStream());
        using var client = new HttpClient(handler);
        var profile = TestAnthropic.Profile() with { MaximumStreamedBytes = 128 };
        await Assert.ThrowsAsync<ModelProviderException>(() => TestAnthropic.CollectAsync(new AnthropicModelProvider(client, profile, "test-key", TestAnthropic.Compatibility()), TestAnthropic.Request() with { Tools = [TestAnthropic.Tool()] }));
        Assert.Single(handler.Requests);
    }

    /// <summary>Model usage counters cannot decrease or overflow normalized totals.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamAsync_InvalidUsage_FailsSafely(bool overflow)
    {
        object initial = overflow ? new { input_tokens = long.MaxValue, cache_creation_input_tokens = 1, output_tokens = 10 } : new { input_tokens = 100, cache_creation_input_tokens = 0, output_tokens = 50 };
        using var client = new HttpClient(new TestAnthropicHandler(TestAnthropic.TextStream(startUsage: initial)));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => TestAnthropic.CollectAsync(Provider(client), TestAnthropic.Request()));
        Assert.True(error is ModelProviderException or MalformedModelOutputException);
        Assert.Null(error.InnerException);
    }

    /// <summary>A violated host single-call policy never releases either returned call.</summary>
    [Fact]
    public async Task StreamAsync_SingleToolPolicy_RejectsMultipleCalls()
    {
        using var client = new HttpClient(new TestAnthropicHandler(TestAnthropic.ToolStream(secondTool: true)));
        await Assert.ThrowsAsync<MalformedInvocationException>(() => TestAnthropic.CollectAsync(Provider(client), TestAnthropic.Request() with { Tools = [TestAnthropic.Tool()], AllowMultipleToolCalls = false }));
    }

    /// <summary>Tool-call resource bounds fail before response-envelope handoff.</summary>
    [Fact]
    public async Task StreamAsync_ToolCallBound_RejectsSecondCall()
    {
        using var client = new HttpClient(new TestAnthropicHandler(TestAnthropic.ToolStream(secondTool: true)));
        var profile = TestAnthropic.Profile() with { MaximumToolCalls = 1 };
        await Assert.ThrowsAsync<ModelProviderException>(() => TestAnthropic.CollectAsync(new AnthropicModelProvider(client, profile, "key", TestAnthropic.Compatibility()), TestAnthropic.Request() with { Tools = [TestAnthropic.Tool()] }));
    }

    /// <summary>An envelope the consumer never transfers to host state is zeroed on iterator disposal.</summary>
    [Fact]
    public async Task StreamAsync_UnclaimedEnvelope_IsReleasedWhenEnumerationStops()
    {
        using var client = new HttpClient(new TestAnthropicHandler(TestAnthropic.ToolStream()));
        ModelResponseReplayEnvelope? unclaimed = null;
        await using (var iterator = Provider(client).StreamAsync(TestAnthropic.Request() with { Tools = [TestAnthropic.Tool()] }).GetAsyncEnumerator())
        {
            while (await iterator.MoveNextAsync())
            {
                if (iterator.Current.ResponseEnvelope is { } envelope)
                {
                    unclaimed = envelope;
                    Assert.True(envelope.ByteCount > 0);
                    break;
                }
            }
        }

        Assert.NotNull(unclaimed);
        Assert.Equal(0, unclaimed.ByteCount);
    }

    private static AnthropicModelProvider Provider(HttpClient client) => new(client, TestAnthropic.Profile(), "test-key", TestAnthropic.Compatibility());
}

