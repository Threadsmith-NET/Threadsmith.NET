namespace Threadsmith.AnthropicProvider.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Threadsmith.Models;
using Threadsmith.Models.Anthropic;

/// <summary>Malformed native event fields and interrupted output retain safe, conservative accounting.</summary>
public sealed class AnthropicFailureAccountingTests
{
    /// <summary>Missing and wrongly typed required fields fail safely after preserving initial usage.</summary>
    [Theory]
    [InlineData("content_block_start", "{\"type\":\"content_block_start\",\"index\":0}", false)]
    [InlineData("content_block_start", "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":null}", false)]
    [InlineData("content_block_start", "{\"type\":\"content_block_start\",\"index\":0,\"content_block\":[]}", false)]
    [InlineData("content_block_start", "{\"type\":\"content_block_start\",\"content_block\":{\"type\":\"text\",\"text\":\"\"}}", false)]
    [InlineData("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0}", true)]
    [InlineData("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":null}", true)]
    [InlineData("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":[]}", true)]
    [InlineData("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\"}}", true)]
    [InlineData("content_block_delta", "{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":42}}", true)]
    [InlineData("message_delta", "{\"type\":\"message_delta\",\"usage\":{\"output_tokens\":25}}", false)]
    [InlineData("message_delta", "{\"type\":\"message_delta\",\"delta\":null}", false)]
    [InlineData("message_delta", "{\"type\":\"message_delta\",\"delta\":[]}", false)]
    [InlineData("message_delta", "{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":[]}", false)]
    public async Task StreamAsync_InvalidRequiredEventData_PreservesSafePartialUsage(string eventType, string json, bool openBlock)
    {
        using var body = JsonDocument.Parse(json);
        var fixture = TestAnthropic.Start()
            + (openBlock ? TestAnthropic.Block(0, new { type = "text", text = string.Empty }) : string.Empty)
            + TestAnthropic.Event(eventType, body.RootElement);
        var handler = new TestAnthropicHandler(fixture);
        using var client = new HttpClient(handler);
        var chunks = new List<ModelChunk>();
        var error = await Assert.ThrowsAsync<MalformedModelOutputException>(async () =>
        {
            await foreach (var chunk in Provider(client).StreamAsync(TestAnthropic.Request()))
            {
                chunks.Add(chunk);
            }
        });
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.ResponseEnvelope is not null || chunk.FinishReason is not null);
        var usage = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage;
        Assert.NotNull(usage);
        Assert.Equal(100, usage.InputTokens);
        Assert.True(usage.IsEstimate);
        Assert.Null(error.InnerException);
        Assert.Single(handler.Requests);
    }

    /// <summary>Invalid initial event structure cannot escape the safe protocol exception boundary.</summary>
    [Theory]
    [InlineData("{\"type\":\"message_start\"}")]
    [InlineData("{\"type\":\"message_start\",\"message\":null}")]
    [InlineData("{\"type\":\"message_start\",\"message\":{\"role\":\"assistant\"}}")]
    [InlineData("{\"type\":\"message_start\",\"message\":{\"content\":[]}}")]
    public async Task StreamAsync_InvalidInitialEvent_FailsSafely(string json)
    {
        using var body = JsonDocument.Parse(json);
        var handler = new TestAnthropicHandler(TestAnthropic.Event("message_start", body.RootElement));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<MalformedModelOutputException>(() => TestAnthropic.CollectAsync(Provider(client), TestAnthropic.Request()));
        Assert.Null(error.InnerException);
        Assert.Single(handler.Requests);
    }

    /// <summary>Provisional output counters never undercount visible output when a response is interrupted.</summary>
    [Theory]
    [InlineData("eof")]
    [InlineData("malformed")]
    [InlineData("error")]
    public async Task StreamAsync_InterruptedVisibleOutput_AccountsEstimatedOutputOnce(string failure)
    {
        var fixture = TestAnthropic.Start(new { input_tokens = 100, output_tokens = 0, cache_creation_input_tokens = 0, cache_read_input_tokens = 0 })
            + TestAnthropic.Block(0, new { type = "text", text = string.Empty })
            + TestAnthropic.Delta(0, new { type = "text_delta", text = new string('x', 2000) })
            + failure switch
            {
                "malformed" => TestAnthropic.Event("content_block_delta", new { type = "content_block_delta", index = 0 }),
                "error" => TestAnthropic.Event("error", new { type = "error", error = new { type = "overloaded_error", message = "REMOTE_BODY_CANARY" } }),
                _ => string.Empty,
            };
        var handler = new TestAnthropicHandler(fixture);
        using var client = new HttpClient(handler);
        var chunks = new List<ModelChunk>();
        var error = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var chunk in Provider(client).StreamAsync(TestAnthropic.Request()))
            {
                chunks.Add(chunk);
            }
        });
        Assert.True(error is ModelProviderException or MalformedModelOutputException or TransientModelException);
        Assert.DoesNotContain("REMOTE_BODY_CANARY", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
        Assert.Equal(2000, string.Concat(chunks.Select(chunk => chunk.Text)).Length);
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.ResponseEnvelope is not null || chunk.FinishReason is not null);
        var usage = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage;
        Assert.NotNull(usage);
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(500, usage.OutputTokens);
        Assert.True(usage.IsEstimate);
        Assert.Equal(0.0052m, usage.EstimatedCost);
        Assert.Single(handler.Requests);
    }

    /// <summary>A final cumulative output counter remains authoritative even when message_stop is lost.</summary>
    [Fact]
    public async Task StreamAsync_EofAfterFinalUsage_DoesNotDoubleCountOutput()
    {
        var fixture = TestAnthropic.Start(new { input_tokens = 100, output_tokens = 0, cache_creation_input_tokens = 0, cache_read_input_tokens = 0 })
            + TestAnthropic.Block(0, new { type = "text", text = new string('x', 2000) }) + TestAnthropic.Close(0)
            + TestAnthropic.End(usage: new { output_tokens = 25 }, stop: false);
        using var client = new HttpClient(new TestAnthropicHandler(fixture));
        var chunks = new List<ModelChunk>();
        await Assert.ThrowsAsync<TransientModelException>(async () =>
        {
            await foreach (var chunk in Provider(client).StreamAsync(TestAnthropic.Request()))
            {
                chunks.Add(chunk);
            }
        });
        var usage = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage;
        Assert.NotNull(usage);
        Assert.Equal(100, usage.InputTokens);
        Assert.Equal(25, usage.OutputTokens);
        Assert.False(usage.IsEstimate);
        Assert.Equal(0.00045m, usage.EstimatedCost);
    }

    /// <summary>Failed generations remain charged across success, exhaustion, later header failure and cancellation.</summary>
    [Theory]
    [InlineData("success")]
    [InlineData("exact")]
    [InlineData("exhausted")]
    [InlineData("headers")]
    [InlineData("cancel")]
    public async Task StreamAsync_RetryUsage_IsCumulativeAndAvailableBeforeNextAttempt(string outcome)
    {
        var attempts = 0;
        var partial = TestAnthropic.Start(new { input_tokens = 100, output_tokens = 0, cache_creation_input_tokens = 200, cache_read_input_tokens = 300 })
            + TestAnthropic.Block(0, new { type = "thinking", thinking = new string('x', 2000), signature = string.Empty });
        if (outcome == "exact")
        {
            partial += TestAnthropic.Close(0) + TestAnthropic.End(usage: new { output_tokens = 500 }, stop: false);
        }

        var handler = new TestAnthropicHandler((_, _) =>
        {
            attempts++;
            if (attempts > 1 && outcome == "headers")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            var body = attempts == 1 || outcome == "exhausted" ? partial
                : TestAnthropic.TextStream(startUsage: new { input_tokens = 10, output_tokens = 0, cache_creation_input_tokens = 20, cache_read_input_tokens = 30 });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
        });
        using var client = new HttpClient(handler);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var reports = new List<ModelUsage>();
        async Task CollectAsync()
        {
            await foreach (var chunk in Provider(client).StreamAsync(TestAnthropic.Request() with { IncludeReasoningText = true }, cancellation.Token))
            {
                if (chunk.Usage is { } usage)
                {
                    reports.Add(usage);
                    if (reports.Count == 1)
                    {
                        Assert.Equal(1, attempts);
                        if (outcome == "cancel")
                        {
                            await cancellation.CancelAsync();
                        }
                    }
                }
            }
        }

        if (outcome is "success" or "exact")
        {
            await CollectAsync();
        }
        else if (outcome == "cancel")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(CollectAsync);
        }
        else
        {
            await Assert.ThrowsAsync<TransientModelException>(CollectAsync);
        }

        Assert.Equal(600, reports[0].InputTokens);
        Assert.Equal(500, reports[0].OutputTokens);
        Assert.Equal(0.00576m, reports[0].EstimatedCost);
        var final = reports[^1];
        var failedAttempts = outcome == "exhausted" ? 3 : 1;
        var succeeded = outcome is "success" or "exact";
        Assert.Equal((600 * failedAttempts) + (succeeded ? 60 : 0), final.InputTokens);
        Assert.Equal((500 * failedAttempts) + (succeeded ? 25 : 0), final.OutputTokens);
        Assert.Equal((300 * failedAttempts) + (succeeded ? 30 : 0), final.Cache?.CacheReadTokens);
        Assert.Equal((200 * failedAttempts) + (succeeded ? 20 : 0), final.Cache?.CacheWriteTokens);
        Assert.Equal((0.00576m * failedAttempts) + (succeeded ? 0.000326m : 0), final.EstimatedCost);
        Assert.Equal(outcome != "exact", final.IsEstimate);
        Assert.Equal(reports.Count > 1 ? 500L : null, final.MaximumAttemptOutputTokens);
        Assert.Null(final.ReasoningTokens);
        Assert.Equal(succeeded ? 2 : failedAttempts, reports.Count);
        Assert.Equal(outcome == "cancel" ? 1 : succeeded ? 2 : 3, attempts);
    }

    private static AnthropicModelProvider Provider(HttpClient client)
    {
        return new(client, TestAnthropic.Profile(), "key", TestAnthropic.Compatibility());
    }
}

