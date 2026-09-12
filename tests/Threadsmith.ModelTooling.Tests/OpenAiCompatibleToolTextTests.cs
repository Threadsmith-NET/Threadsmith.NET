namespace Threadsmith.ModelTooling.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Xunit;

/// <summary>Exercises tool argument tails at the provider boundary used by every agent display.</summary>
public static class OpenAiCompatibleToolTextTests
{
    private const string Orphan = "<parameter name=\"path\">src/Threadsmith.sln\n</parameter>\n</function>\n</tool_call>";

    /// <summary>The captured chunk boundaries cannot leak text or release a valid sibling, and usage remains reported.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task LoggedFragmentsRejectEntireBatchAndRetainUsage(bool toolsFirst)
    {
        string[] fragments = ["<parameter name", "=\"path", "\">src", "/Threadsmith.s", "ln\n", "</parameter>\n", "</function>\n</tool_call>"];
        var handler = new StreamHandler(Stream(fragments, toolsFirst: toolsFirst, siblings: true));
        using var client = new HttpClient(handler);
        var chunks = new List<ModelChunk>();

        var exception = await Assert.ThrowsAsync<MalformedInvocationException>(async () =>
        {
            await foreach (var chunk in Provider(client).StreamAsync(Request(), TestContext.Current.CancellationToken))
            {
                chunks.Add(chunk);
            }
        });

        Assert.Equal(string.Empty, string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.FinishReason is not null);
        var usage = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage;
        Assert.NotNull(usage);
        Assert.Equal(40801, usage.InputTokens);
        Assert.Equal(161, usage.OutputTokens);
        Assert.Equal(88, usage.ReasoningTokens);
        Assert.False(usage.IsEstimate);
        Assert.Equal(2, exception.Diagnostic.ToolCallCount);
        Assert.Equal("openai-compatible", exception.Diagnostic.ProviderFamily);
        Assert.DoesNotContain("src/Threadsmith.sln", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Fragmentation and a missing finish event do not bypass validation.</summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(17, true)]
    [InlineData(200, true)]
    [InlineData(1, false)]
    public static async Task OrphanDetectionSurvivesChunkBoundariesAndMissingFinish(int chunkSize, bool finish)
    {
        var fragments = Orphan.Chunk(chunkSize).Select(chars => new string(chars));
        using var client = new HttpClient(new StreamHandler(Stream(fragments, finish: finish)));

        await Assert.ThrowsAsync<MalformedInvocationException>(async () =>
            await Provider(client).StreamAsync(Request(), TestContext.Current.CancellationToken)
                .ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Equivalent XML whitespace, quoting, and multiple parameters are recognized.</summary>
    [Theory]
    [InlineData("<parameter name='path'>file.cs</parameter>\r\n</function>\r\n</tool_call>")]
    [InlineData(" \n<parameter\n name='path'>file.cs</parameter>\n</function>\n</tool_call>\n")]
    [InlineData("<parameter name='path'>file.cs</parameter><parameter name='query'>x</parameter></function></tool_call>")]
    public static async Task OrphanDetectionUsesXmlStructure(string text)
    {
        using var client = new HttpClient(new StreamHandler(Stream(text.Select(character => character.ToString()))));

        await Assert.ThrowsAsync<MalformedInvocationException>(async () =>
            await Provider(client).StreamAsync(Request(), TestContext.Current.CancellationToken)
                .ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>XML examples and ambiguous fragments retain their exact original text.</summary>
    [Theory]
    [InlineData("<parameter name=\"path\">src/Threadsmith.sln</parameter>")]
    [InlineData("<parameterization>normal XML</parameterization>")]
    [InlineData("<parameter name='x'>value</parameter> extra prose </function></tool_call>")]
    [InlineData("<parameter name='x'>value</parameter></function></tool_call><second/>")]
    [InlineData("<parameter name='x'>value</parameter></function></tool_call> commentary </tool_call>")]
    [InlineData("<parameter name='x'>value</parameter></function></tool_call><tool_call></tool_call>")]
    [InlineData("<tool_call><function><parameter name='x'>value</parameter></function></tool_call>")]
    [InlineData("<parameter")]
    [InlineData("Example:\n" + Orphan)]
    [InlineData("```xml\n" + Orphan + "\n```")]
    [InlineData("~~~xml\n" + Orphan + "\n~~~")]
    [InlineData("`" + Orphan + "`")]
    public static async Task XmlExamplesAndUnconfirmedFragmentsRemainVerbatim(string content)
    {
        using var client = new HttpClient(new StreamHandler(Stream(content.Select(character => character.ToString()))));
        var chunks = await Provider(client).StreamAsync(Request(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(content, string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.Single(chunks, chunk => chunk.Output is ToolRequestModelOutput);
    }

    /// <summary>Parameter text alone does not establish a malformed native invocation.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public static async Task RequiresBothAdvertisedToolsAndANativeCall(bool advertiseTools, bool nativeCall)
    {
        using var client = new HttpClient(new StreamHandler(Stream([Orphan], nativeCall: nativeCall)));
        var request = Request();
        if (!advertiseTools)
        {
            request = request with { Tools = [] };
        }

        var chunks = await Provider(client).StreamAsync(request, TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Orphan, string.Concat(chunks.Select(chunk => chunk.Text)));
    }

    /// <summary>Normal prose is delivered without waiting for response completion.</summary>
    [Fact]
    public static async Task OrdinaryTextKeepsItsStreamingChunks()
    {
        string[] fragments = ["I will ", "inspect ", "the source."];
        using var client = new HttpClient(new StreamHandler(Stream(fragments)));
        var chunks = await Provider(client).StreamAsync(Request(), TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(fragments, chunks.Where(chunk => chunk.Text is not null).Select(chunk => chunk.Text));
    }

    private static string Stream(
        IEnumerable<string> fragments,
        bool nativeCall = true,
        bool toolsFirst = false,
        bool siblings = false,
        bool finish = true)
    {
        var toolCalls = new List<object>();
        if (nativeCall)
        {
            toolCalls.Add(new
            {
                index = 0,
                id = "call-0",
                function = new
                {
                    name = "search",
                    arguments = JsonSerializer.Serialize(new { query = "Project(\\\"", useRegularExpression = true }),
                },
            });
        }

        if (siblings)
        {
            toolCalls.Add(new
            {
                index = 1,
                id = "call-1",
                function = new
                {
                    name = "search",
                    arguments = JsonSerializer.Serialize(new { query = "valid sibling" }),
                },
            });
        }

        var calls = toolCalls.Count > 0
            ? "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { tool_calls = toolCalls } } },
            }) + "\n\n"
            : string.Empty;
        var text = string.Concat(fragments.Select(fragment => "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content = fragment } } },
        }) + "\n\n"));
        return (toolsFirst ? calls + text : text + calls)
            + (finish ? "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" : string.Empty)
            + "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":40801,\"completion_tokens\":161,\"completion_tokens_details\":{\"reasoning_tokens\":88}}}\n\ndata: [DONE]\n\n";
    }

    private static ModelStreamRequest Request() => new()
    {
        RunId = RunId.New(),
        Input = "Synthetic provider protocol check.",
        Tools = [new ModelToolDefinition { Name = "search", Description = "Search synthetic data.", ArgumentsJsonSchema = "{\"type\":\"object\",\"properties\":{}}" }],
    };

    private static OpenAiCompatibleModelProvider Provider(HttpClient client) => new(client, new ModelProfile
    {
        Id = ModelProfileId.New(),
        Name = "synthetic",
        Provider = "openai-compatible",
        ModelId = "synthetic",
        Endpoint = new Uri("https://models.example/v1/chat/completions"),
        ContextWindow = 64000,
        MaximumOutputTokens = 4000,
        Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true },
        RetryPolicy = new ModelRetryPolicy { MaxAttempts = 3, Delay = TimeSpan.Zero },
    });

    private sealed class StreamHandler : HttpMessageHandler
    {
        private readonly string _body;

        internal StreamHandler(string body)
        {
            _body = body;
        }

        internal int Calls { get; private set; }

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
