namespace Threadsmith.ModelTooling.Tests;

using System.Net;
using System.Text;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Xunit;

/// <summary>Provider errors carried by successful HTTP streams must not become empty-answer corrections.</summary>
public static class OpenAiCompatibleStreamErrorTests
{
    /// <summary>Failed attempts retain usage both during recovery and after committed answer text.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task RateLimit_PreservesCumulativeUsage(bool afterText)
    {
        var prefix = afterText ? "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n" : string.Empty;
        var failure = prefix + "data: {\"error\":{\"code\":429},\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2}}\n\n";
        const string success = "data: {\"choices\":[{\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":3}}\n\ndata: [DONE]\n\n";
        var handler = new StreamHandler(failure, failure, success);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();
        async Task CollectAsync()
        {
            await foreach (var chunk in CreateProvider(client).StreamAsync(Request(), TestContext.Current.CancellationToken))
            {
                chunks.Add(chunk);
            }
        }

        if (afterText)
        {
            await Assert.ThrowsAsync<TransientModelException>(CollectAsync);
        }
        else
        {
            await CollectAsync();
        }

        var usage = chunks.Where(chunk => chunk.Usage is not null).Select(chunk => chunk.Usage!).ToArray();
        Assert.Equal(afterText ? 1 : 3, handler.Calls);
        Assert.Equal(afterText ? [10L] : new[] { 10L, 20L, 40L }, usage.Select(item => item.InputTokens));
        Assert.Equal(afterText ? 2 : 7, usage[^1].OutputTokens);
        Assert.Equal(afterText ? 2 : 3, usage[^1].MaximumAttemptOutputTokens ?? usage[^1].OutputTokens);
    }

    /// <summary>Every attempt, including the first request, counts against the configured limit.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public static async Task RateLimit_UsesConfiguredAttemptLimit(int maximumAttempts)
    {
        var handler = new StreamHandler("data: {\"error\":{\"code\":429}}\n\n");
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();
        await Assert.ThrowsAsync<TransientModelException>(async () =>
        {
            await foreach (var chunk in CreateProvider(client, maximumAttempts).StreamAsync(Request(), TestContext.Current.CancellationToken))
            {
                chunks.Add(chunk);
            }
        });
        Assert.Equal(maximumAttempts, handler.Calls);
        Assert.Equal(maximumAttempts > 1 ? 1 : 0, chunks.Count(chunk => chunk.ProviderBusy));
    }

    /// <summary>Rate-limit variants recover or exhaust retries without duplicate busy notices or failed-frame usage.</summary>
    [Theory]
    [InlineData("{\"code\":429}", false)]
    [InlineData("{\"code\":\"429\"}", false)]
    [InlineData("{\"type\":\"rate_limit_error\"}", false)]
    [InlineData("{\"code\":\"rate_limit_exceeded\"}", true)]
    public static async Task RateLimit_RetriesWithOneBusyNotice(string error, bool exhaust)
    {
        var failed = "data: {\"error\":" + error + ",\"usage\":{\"prompt_tokens\":0,\"completion_tokens\":0}}\n\n";
        const string success = "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n";
        var handler = new StreamHandler(failed, failed, exhaust ? failed : success);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();
        async Task CollectAsync()
        {
            await foreach (var chunk in CreateProvider(client).StreamAsync(Request(), TestContext.Current.CancellationToken))
            {
                chunks.Add(chunk);
            }
        }

        if (exhaust)
        {
            var exception = await Assert.ThrowsAsync<TransientModelException>(CollectAsync);
            Assert.True(exception.IsRateLimit);
        }
        else
        {
            await CollectAsync();
            Assert.Equal("hello", string.Concat(chunks.Select(chunk => chunk.Text)));
            Assert.Single(chunks, chunk => chunk.Usage is not null);
        }

        Assert.Equal(3, handler.Calls);
        Assert.Single(chunks, chunk => chunk.ProviderBusy);
    }

    /// <summary>Cancellation while entering backoff stops resubmission.</summary>
    [Fact]
    public static async Task RateLimit_CancelAtBusyNotice_DoesNotResubmit()
    {
        var handler = new StreamHandler("data: {\"error\":{\"code\":429}}\n\n");
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var chunk in CreateProvider(client).StreamAsync(Request(), cancellation.Token))
            {
                Assert.True(chunk.ProviderBusy);
                await cancellation.CancelAsync();
            }
        });
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Once visible text is committed it is never replayed automatically.</summary>
    [Fact]
    public static async Task RateLimit_AfterText_IsTransientWithoutDuplicatingOutput()
    {
        var handler = new StreamHandler("data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\ndata: {\"error\":{\"code\":429}}\n\n");
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var exception = await Assert.ThrowsAsync<TransientModelException>(async () =>
            await CreateProvider(client).StreamAsync(Request(), TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
        Assert.True(exception.IsRateLimit);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Errors terminate the stream before usage estimates or unfinished tools are released, without replay.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ErrorEnvelope_StopsWithoutRetryOrToolDispatch(bool afterPartialOutput)
    {
        var initial = afterPartialOutput
            ? "data: {\"choices\":[{\"delta\":{\"content\":\"Starting\",\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"function\":{\"name\":\"inspect\",\"arguments\":\"{}\"}}]}}]}\n\n"
            : string.Empty;
        var handler = new StreamHandler(initial
            + "data: {\"error\":{\"message\":\"Invalid grammar specification: 'triggers'; credential-canary\",\"type\":\"BadRequestError\",\"code\":400}}\n\ndata: [DONE]\n\n");
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var provider = CreateProvider(client);
        var chunks = new List<ModelChunk>();

        var exception = await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var chunk in provider.StreamAsync(Request(), TestContext.Current.CancellationToken))
            {
                chunks.Add(chunk);
            }
        });

        Assert.Equal(1, handler.Calls);
        Assert.Equal(RetryClassification.Permanent, ModelFailureClassifier.Classify(exception));
        Assert.DoesNotContain("error inside the response stream", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Invalid grammar specification", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-canary", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.Usage is not null || chunk.FinishReason is not null);
        Assert.Equal(afterPartialOutput ? "Starting" : string.Empty, string.Concat(chunks.Select(chunk => chunk.Text)));
    }

    /// <summary>Untrusted error metadata cannot cause a parsing exception or leak arbitrary provider strings.</summary>
    [Theory]
    [InlineData("{\"code\":\"private code\"}")]
    [InlineData("{\"code\":null}")]
    [InlineData("{\"code\":{\"private\":true}}")]
    [InlineData("{\"code\":99999999999999999999999999999}")]
    [InlineData("{\"code\":200}")]
    [InlineData("{\"code\":401.5}")]
    [InlineData("\"private error message\"")]
    [InlineData("{}")]
    public static async Task ErrorEnvelope_HandlesUnexpectedMetadata(string error)
    {
        using var client = new HttpClient(new StreamHandler("data: {\"error\":" + error + "}\n\ndata: [DONE]\n\n")) { Timeout = Timeout.InfiniteTimeSpan };

        var exception = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await CreateProvider(client).StreamAsync(Request(), TestContext.Current.CancellationToken)
                .ToListAsync(TestContext.Current.CancellationToken));

        Assert.StartsWith("The model endpoint reported an error inside the response stream.", exception.Message);
    }

    /// <summary>Code-only errors retain safe envelope correlation while malformed identifiers and messages stay absent.</summary>
    [Theory]
    [InlineData("\"req-1\"", "\"generation-1\"", true)]
    [InlineData("{}", "[]", false)]
    [InlineData("\"credential-canary\"", "null", false)]
    public static async Task ErrorEnvelope_CodeOnlyRetainsSafeCorrelation(string requestId, string generationId, bool expected)
    {
        var body = "data: {\"request_id\":" + requestId + ",\"id\":" + generationId + ",\"error\":{\"code\":\"invalid_request\",\"message\":123}}\n\n";
        using var client = new HttpClient(new StreamHandler(body)) { Timeout = Timeout.InfiniteTimeSpan };

        var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await CreateProvider(client, 1).StreamAsync(Request(), TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));

        Assert.StartsWith("The model endpoint reported an error inside the response stream.", error.Message, StringComparison.Ordinal);
        Assert.Contains("Code: invalid_request", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("credential-canary", error.Message, StringComparison.Ordinal);
        Assert.Equal(expected, error.Message.Contains("Request ID: req-1", StringComparison.Ordinal));
        Assert.Equal(expected, error.Message.Contains("Generation ID: generation-1", StringComparison.Ordinal));
        Assert.DoesNotContain("123", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A null optional error field does not change normal text or usage delivery.</summary>
    [Fact]
    public static async Task NullError_AllowsNormalCompletion()
    {
        using var client = new HttpClient(new StreamHandler(
            "data: {\"error\":null,\"choices\":[{\"delta\":{\"content\":\"hello\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"))
        { Timeout = Timeout.InfiniteTimeSpan };

        var chunks = await CreateProvider(client).StreamAsync(Request(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("hello", string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.Contains(chunks, chunk => chunk.FinishReason == ModelFinishReason.Stop);
        Assert.Single(chunks, chunk => chunk.Usage is not null);
    }

    /// <summary>HTTP rejection messages use the same safe explanatory fields as stream errors.</summary>
    [Theory]
    [InlineData(400, false)]
    [InlineData(503, false)]
    [InlineData(503, true)]
    public static async Task HttpFailure_PreservesSafeExplanation(int status, bool chunked)
    {
        using var client = new HttpClient(new StreamHandler("{\"error\":{\"code\":\"capacity\",\"message\":\"Capacity detail for credential-canary\"}}") { StatusCode = (HttpStatusCode)status, Chunked = chunked });
        var error = await Record.ExceptionAsync(() => CreateProvider(client, 1).StreamAsync(Request(), TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.NotNull(error);
        Assert.Contains("Capacity detail for [REDACTED]", error.Message, StringComparison.Ordinal);
        Assert.Equal("Capacity detail for [REDACTED]" + Environment.NewLine + $"HTTP {status} · Code: capacity", error.Message);
        Assert.DoesNotContain("credential-canary", error.Message, StringComparison.Ordinal);
    }

    private static ModelStreamRequest Request()
    {
        return new()
        {
            RunId = RunId.New(),
            Input = "Synthetic test request.",
            Tools = [new ModelToolDefinition { Name = "inspect", Description = "Inspect synthetic data.", ArgumentsJsonSchema = "{\"type\":\"object\",\"properties\":{}}" }],
        };
    }

    private static OpenAiCompatibleModelProvider CreateProvider(HttpClient client, int maximumAttempts = 3)
    {
        return new(
        client,
        new ModelProfile
        {
            Id = ModelProfileId.New(),
            Name = "synthetic",
            Timeout = TimeSpan.Zero,
            Provider = "openai-compatible",
            ModelId = "synthetic",
            Endpoint = new Uri("https://models.example/v1/chat/completions"),
            ContextWindow = 32000,
            MaximumOutputTokens = 4000,
            Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true },
            RetryPolicy = new ModelRetryPolicy { MaxAttempts = maximumAttempts, Delay = TimeSpan.Zero },
        },
        apiKey: "credential-canary",
        timeProvider: new AdvancingTimeProvider());
    }

    private sealed class StreamHandler : HttpMessageHandler
    {
        private readonly string[] _bodies;

        public StreamHandler(params string[] bodies)
        {
            _bodies = bodies;
        }

        public int Calls { get; private set; }

        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;

        public bool Chunked { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            var body = _bodies[Math.Min(Calls - 1, _bodies.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(StatusCode)
            {
                Content = Chunked ? new StreamContent(new NonSeekableBody(Encoding.UTF8.GetBytes(body))) : new StringContent(body, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private sealed class NonSeekableBody(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}
