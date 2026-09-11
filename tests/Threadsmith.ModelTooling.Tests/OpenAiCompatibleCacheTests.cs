namespace Threadsmith.ModelTooling.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Xunit;

/// <summary>Checks exact wire prefixes and the distinct meanings of missing, zero, and reported cache counters.</summary>
public static class OpenAiCompatibleCacheTests
{
    /// <summary>Changed memories and evidence follow history, and native tool continuations append without rewriting it.</summary>
    [Fact]
    public static async Task RequestContextPreservesWirePrefix()
    {
        var handler = new CaptureHandler("{}");
        using var client = new HttpClient(handler);
        var provider = Provider(client);
        var request = new ModelStreamRequest
        {
            RunId = RunId.New(), Input = "legacy must not leak",
            Messages =
            [
                Message(ModelMessageRole.System, "host-policy", "stable host"),
                Message(ModelMessageRole.Developer, "repository-instructions", "stable repository"),
                Message(ModelMessageRole.User, "recent-user", "prior question"),
                Message(ModelMessageRole.Assistant, "recent-assistant", "prior answer"),
                Message(ModelMessageRole.HostContext, "repository-memory", "old memory"),
                Message(ModelMessageRole.HostContext, "governed-request-state", "old evidence"),
                Message(ModelMessageRole.User, "current-user", "current question"),
            ],
            Tools = [new ModelToolDefinition { Name = "read", Description = "Synthetic read", ArgumentsJsonSchema = "{\"type\":\"object\",\"properties\":{}}" }],
        };
        _ = await provider.StreamAsync(request, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        var changed = request with
        {
            Messages = [.. request.Messages.Take(4), Message(ModelMessageRole.HostContext, "governed-request-state", "fresh evidence"), request.Messages[^1]],
        };
        _ = await provider.StreamAsync(changed, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        var continued = changed with
        {
            Messages =
            [
                .. changed.Messages,
                Message(ModelMessageRole.Assistant, "call", "{}") with { ToolCallId = "call-1", ToolName = "read" },
                Message(ModelMessageRole.Tool, "result", "synthetic result") with { ToolCallId = "call-1", ToolName = "read" },
            ],
        };
        _ = await provider.StreamAsync(continued, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        var first = handler.Requests[0].GetProperty("messages");
        var next = handler.Requests[1].GetProperty("messages");
        var tail = handler.Requests[2].GetProperty("messages");
        for (var index = 0; index < 3; index++)
        {
            Assert.Equal(first[index].GetRawText(), next[index].GetRawText());
        }

        Assert.DoesNotContain("old memory", handler.Requests[1].GetRawText(), StringComparison.Ordinal);
        Assert.Contains("<threadsmith_host_context>\nfresh evidence\n</threadsmith_host_context>\n\ncurrent question", next[3].GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal(next.EnumerateArray().Select(item => item.GetRawText()), tail.EnumerateArray().Take(next.GetArrayLength()).Select(item => item.GetRawText()));
        Assert.Equal("call-1", tail[4].GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("call-1", tail[5].GetProperty("tool_call_id").GetString());
        Assert.All(handler.Requests, body => Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean()));
    }

    /// <summary>Both standard read counters and vLLM creation counters survive the final streaming usage chunk.</summary>
    [Theory]
    [InlineData("{}", null, null)]
    [InlineData("{\"prompt_tokens_details\":null}", null, null)]
    [InlineData("{\"prompt_tokens_details\":{\"cached_tokens\":0}}", 0L, null)]
    [InlineData("{\"prompt_tokens_details\":{\"cached_tokens\":800,\"created_cache_tokens\":200}}", 800L, 200L)]
    [InlineData("{\"prompt_tokens_details\":{\"created_cache_tokens\":200}}", null, 200L)]
    [InlineData("{\"cache_read_input_tokens\":600,\"cache_creation_input_tokens\":400}", 600L, 400L)]
    public static async Task CacheCountersRetainAvailability(string fields, long? reads, long? writes)
    {
        using var client = new HttpClient(new CaptureHandler(fields));
        var chunks = await Provider(client).StreamAsync(new ModelStreamRequest { RunId = RunId.New(), Input = "synthetic" }, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);
        var usage = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage!;
        Assert.Equal(1000, usage.InputTokens);
        Assert.Equal(10, usage.OutputTokens);
        Assert.False(usage.IsEstimate);
        Assert.Equal(reads, usage.Cache?.CacheReadTokens);
        Assert.Equal(writes, usage.Cache?.CacheWriteTokens);
        Assert.Equal(reads is null && writes is null ? CacheUsageAvailability.Unavailable : CacheUsageAvailability.Reported, usage.Cache?.Availability);
    }

    /// <summary>Negative creation or read counters cannot corrupt usage projections.</summary>
    [Theory]
    [InlineData("cached_tokens")]
    [InlineData("created_cache_tokens")]
    public static async Task NegativeCacheCountersAreRejected(string field)
    {
        using var client = new HttpClient(new CaptureHandler("{\"prompt_tokens_details\":{\"" + field + "\":-1}}"));
        await Assert.ThrowsAsync<MalformedModelOutputException>(async () =>
            await Provider(client).StreamAsync(new ModelStreamRequest { RunId = RunId.New(), Input = "synthetic" }, TestContext.Current.CancellationToken)
                .ToListAsync(TestContext.Current.CancellationToken));
    }

    private static ModelMessage Message(ModelMessageRole role, string section, string text) => new()
    {
        Role = role, SectionId = section, Content = [new ModelContentPart { Content = text }],
    };

    private static OpenAiCompatibleModelProvider Provider(HttpClient client) => new(client, new ModelProfile
    {
        Id = ModelProfileId.New(), Name = "synthetic", Provider = "openai-compatible", ModelId = "synthetic",
        Endpoint = new Uri("https://models.example/v1/chat/completions"), ContextWindow = 32000, MaximumOutputTokens = 4000,
        Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true },
        RetryPolicy = new ModelRetryPolicy { MaxAttempts = 1 },
    });

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly string _fields;

        public CaptureHandler(string fields) => _fields = fields;

        public List<JsonElement> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(body.RootElement.Clone());
            var fields = _fields[1..^1];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"},\"finish_reason\":\"stop\"}]}\n\n"
                    + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":1000,\"completion_tokens\":10"
                    + (fields.Length == 0 ? string.Empty : "," + fields) + "}}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        }
    }
}
