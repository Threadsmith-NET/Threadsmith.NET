namespace Threadsmith.ModelTooling.Tests;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies advertised search arguments and the Brave HTTP parameter contract.</summary>
public sealed class WebSearchContractTests
{
    /// <summary>Omitted or null optional fields preserve defaults without adding provider filters.</summary>
    [Theory]
    [InlineData("{\"query\":\"release notes\"}")]
    [InlineData("{\"query\":\"release notes\",\"maximumResults\":null,\"locale\":null,\"freshnessDays\":null}")]
    public async Task OptionalArguments_PreserveDefaults(string arguments)
    {
        var handler = new CapturingHttpHandler();
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = CreateClient(httpClient, new CapturingSecretResolver());
        var input = Assert.IsType<WebSearchRequest>(CreateTool(client).DeserializeInput(arguments));

        await client.SearchAsync(input);

        var parameters = ReadParameters(handler);
        Assert.Equal(5, input.MaximumResults);
        Assert.Equal("5", parameters["count"]);
        Assert.Equal(2, parameters.Count);
    }

    /// <summary>Host bounds fail locally with the field name before resolving credentials or sending HTTP.</summary>
    [Theory]
    [InlineData("{\"query\":\" \"}", "query")]
    [InlineData("{\"query\":\"a\\nb\"}", "query")]
    [InlineData("{\"query\":\"test\",\"maximumResults\":0}", "maximumResults")]
    [InlineData("{\"query\":\"test\",\"maximumResults\":21}", "maximumResults")]
    [InlineData("{\"query\":\"test\",\"freshnessDays\":0}", "freshnessDays")]
    [InlineData("{\"query\":\"test\",\"freshnessDays\":366}", "freshnessDays")]
    [InlineData("{\"query\":\"test\",\"locale\":\"xx\"}", "locale")]
    [InlineData("{\"query\":\"test\",\"locale\":\"en_US\"}", "locale")]
    [InlineData("{\"query\":\"test\",\"locale\":\"\"}", "locale")]
    public async Task InvalidArguments_FailAtBothBoundaries(string arguments, string field)
    {
        var handler = new CapturingHttpHandler();
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var resolver = new CapturingSecretResolver();
        var client = CreateClient(httpClient, resolver);
        var tool = CreateTool(client);
        var input = JsonSerializer.Deserialize<WebSearchRequest>(
            arguments,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(input);

        var toolError = Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(arguments));
        var adapterError = await Assert.ThrowsAsync<ToolArgumentValidationException>(() => client.SearchAsync(input));

        Assert.Contains(field, toolError.Message, StringComparison.Ordinal);
        Assert.Contains(field, adapterError.Message, StringComparison.Ordinal);
        Assert.Equal(0, resolver.CallCount);
        Assert.Null(handler.RequestUri);
    }

    /// <summary>Word and character limits are independent and enforced before HTTP.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedQuery_FailsBeforeProviderCall(bool exceedWordCount)
    {
        var query = exceedWordCount ? string.Join(' ', Enumerable.Repeat("a", 76)) : new string('a', 501);
        var handler = new CapturingHttpHandler();
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var resolver = new CapturingSecretResolver();
        var client = CreateClient(httpClient, resolver);

        Assert.Throws<ToolArgumentValidationException>(
            () => CreateTool(client).DeserializeInput(JsonSerializer.Serialize(new { query })));
        await Assert.ThrowsAsync<ToolArgumentValidationException>(
            () => client.SearchAsync(new WebSearchRequest { Query = query }));

        Assert.Equal(0, resolver.CallCount);
        Assert.Null(handler.RequestUri);
    }

    /// <summary>Queries at either published upper bound still pass schema and transport validation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAtUpperBound_IsAccepted(bool useWordBound)
    {
        var query = useWordBound ? string.Join(' ', Enumerable.Repeat("a", 75)) : new string('a', 500);
        var handler = new CapturingHttpHandler();
        using var httpClient = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var client = CreateClient(httpClient, new CapturingSecretResolver());
        var tool = CreateTool(client);
        var input = Assert.IsType<WebSearchRequest>(tool.DeserializeInput(
            JsonSerializer.Serialize(new { query, maximumResults = 20, freshnessDays = 365 })));
        using var schema = JsonDocument.Parse(tool.Definition.InputSchema.JsonSchema);
        var pattern = schema.RootElement.GetProperty("properties").GetProperty("query").GetProperty("pattern").GetString();
        Assert.NotNull(pattern);

        await client.SearchAsync(input);

        Assert.True(Regex.IsMatch(query, pattern, RegexOptions.CultureInvariant));
        Assert.Equal(query, ReadParameters(handler)["q"]);
        Assert.Equal("20", ReadParameters(handler)["count"]);
    }

    /// <summary>Control characters at the end remain forbidden even where regex dollar anchors allow a final newline.</summary>
    [Theory]
    [InlineData("a\n")]
    [InlineData("a\r\n")]
    [InlineData("a\t")]
    [InlineData("a\u0085")]
    public void QueryBinding_RejectsTrailingControlCharacters(string query)
    {
        using var httpClient = new HttpClient(new CapturingHttpHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var tool = CreateTool(CreateClient(httpClient, new CapturingSecretResolver()));

        Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(JsonSerializer.Serialize(new { query })));
    }

    /// <summary>Unadvertised aliases and incorrectly typed arguments are rejected by binding.</summary>
    [Theory]
    [InlineData("{\"q\":\"test\"}")]
    [InlineData("{\"query\":\"test\",\"count\":5}")]
    [InlineData("{\"query\":\"test\",\"freshnessDays\":\"7d\"}")]
    [InlineData("{\"query\":[\"test\"]}")]
    public void SchemaMismatch_IsRejected(string arguments)
    {
        using var httpClient = new HttpClient(new CapturingHttpHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var tool = CreateTool(CreateClient(httpClient, new CapturingSecretResolver()));

        Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(arguments));
    }

    /// <summary>The model sees numeric bounds, defaults and a valid canonical invocation example.</summary>
    [Fact]
    public void AdvertisedContract_DescribesAndBoundsCanonicalArguments()
    {
        using var httpClient = new HttpClient(new CapturingHttpHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var tool = CreateTool(CreateClient(httpClient, new CapturingSecretResolver()));
        using var schema = JsonDocument.Parse(tool.Definition.InputSchema.JsonSchema);
        var root = schema.RootElement;
        var properties = root.GetProperty("properties");

        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("query", Assert.Single(root.GetProperty("required").EnumerateArray()).GetString());
        Assert.Equal(4, properties.EnumerateObject().Count());
        Assert.Equal(1, properties.GetProperty("query").GetProperty("minLength").GetInt32());
        Assert.Equal(500, properties.GetProperty("query").GetProperty("maxLength").GetInt32());
        Assert.Equal(1, properties.GetProperty("maximumResults").GetProperty("minimum").GetInt32());
        Assert.Equal(20, properties.GetProperty("maximumResults").GetProperty("maximum").GetInt32());
        Assert.Equal(5, properties.GetProperty("maximumResults").GetProperty("default").GetInt32());
        Assert.Equal(1, properties.GetProperty("freshnessDays").GetProperty("minimum").GetInt32());
        Assert.Equal(365, properties.GetProperty("freshnessDays").GetProperty("maximum").GetInt32());
        foreach (var property in properties.EnumerateObject())
        {
            Assert.Contains(property.Name, tool.Definition.Description, StringComparison.Ordinal);
        }

        var strictSchema = ModelToolStrictSchemaProjector.TryCreateStrictFunctionSchema(
            tool.Definition.Id,
            tool.Definition.InputSchema.JsonSchema);
        Assert.NotNull(strictSchema);
    }

    private static BraveWebSearchClient CreateClient(HttpClient httpClient, ISecretResolver resolver)
    {
        return new BraveWebSearchClient(
            httpClient,
            resolver,
            new WebSearchOptions { MinimumRequestInterval = TimeSpan.Zero },
            TestPromptLoader.Instance,
            timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
    }

    private static WebSearchTool CreateTool(IWebSearchClient client)
    {
        return new WebSearchTool(client, new WebSearchOptions(), new SecretOutputSanitizer(), TestPromptLoader.Instance);
    }

    private static Dictionary<string, string> ReadParameters(CapturingHttpHandler handler)
    {
        Assert.NotNull(handler.RequestUri);
        return handler.RequestUri.Query.TrimStart('?').Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => Uri.UnescapeDataString(pair[1]));
    }

    private sealed class CapturingHttpHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("{\"web\":{\"results\":[]}}", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class CapturingSecretResolver : ISecretResolver
    {
        public int CallCount { get; private set; }

        public Task<SecretResolutionResult> ResolveAsync(SecretResolutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new SecretResolutionResult
            {
                Value = new SecretValue("test-key"),
                ProviderId = "test-user",
                Failure = SecretResolutionFailure.None,
            });
        }
    }
}
