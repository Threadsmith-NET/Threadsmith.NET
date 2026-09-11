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
            + "data: {\"error\":{\"message\":\"Invalid grammar specification: 'triggers'; private request content\",\"type\":\"BadRequestError\",\"code\":400}}\n\ndata: [DONE]\n\n");
        using var client = new HttpClient(handler);
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
        Assert.Contains("error inside the response stream (code 400)", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private request content", exception.Message, StringComparison.Ordinal);
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
        using var client = new HttpClient(new StreamHandler("data: {\"error\":" + error + "}\n\ndata: [DONE]\n\n"));

        var exception = await Assert.ThrowsAsync<ModelProviderException>(async () =>
            await CreateProvider(client).StreamAsync(Request(), TestContext.Current.CancellationToken)
                .ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal("The model endpoint reported an error inside the response stream. Check the provider server logs for details.", exception.Message);
    }

    /// <summary>A null optional error field does not change normal text or usage delivery.</summary>
    [Fact]
    public static async Task NullError_AllowsNormalCompletion()
    {
        using var client = new HttpClient(new StreamHandler(
            "data: {\"error\":null,\"choices\":[{\"delta\":{\"content\":\"hello\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"));

        var chunks = await CreateProvider(client).StreamAsync(Request(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal("hello", string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.Contains(chunks, chunk => chunk.FinishReason == ModelFinishReason.Stop);
        Assert.Single(chunks, chunk => chunk.Usage is not null);
    }

    private static ModelStreamRequest Request() => new()
    {
        RunId = RunId.New(),
        Input = "Synthetic test request.",
        Tools = [new ModelToolDefinition { Name = "inspect", Description = "Inspect synthetic data.", ArgumentsJsonSchema = "{\"type\":\"object\",\"properties\":{}}" }],
    };

    private static OpenAiCompatibleModelProvider CreateProvider(HttpClient client) => new(client, new ModelProfile
    {
        Id = ModelProfileId.New(),
        Name = "synthetic",
        Provider = "openai-compatible",
        ModelId = "synthetic",
        Endpoint = new Uri("https://models.example/v1/chat/completions"),
        ContextWindow = 32000,
        MaximumOutputTokens = 4000,
        Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true },
        RetryPolicy = new ModelRetryPolicy { MaxAttempts = 3, Delay = TimeSpan.Zero },
    });

    private sealed class StreamHandler : HttpMessageHandler
    {
        private readonly string _body;

        public StreamHandler(string body)
        {
            _body = body;
        }

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "text/event-stream"),
            });
        }
    }
}
