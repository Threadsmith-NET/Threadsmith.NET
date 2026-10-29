namespace Threadsmith.ModelTooling.Tests;

using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible.OpenRouter;
using Xunit;

/// <summary>Exercises compiled OpenRouter protocol behavior without remote credentials.</summary>
public static class OpenRouterProviderTests
{
    /// <summary>Batch responses follow host admission even when the upstream rejects the optional parallel-control parameter.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, null)]
    [InlineData(true, null)]
    public static async Task ToolBatches_UseHostAdmissionIndependentlyOfWireParameter(bool parallelParameterSupported, bool? allowMultiple)
    {
        var model = Model() with
        {
            ParallelToolCalls = parallelParameterSupported,
            ReasoningControl = ReasoningControllability.AlwaysOn,
            SupportsReasoningReplay = true,
            Upstream = "fixture-upstream",
            ExpectedProviderName = "Fixture Provider",
        };
        var request = Request() with { AllowMultipleToolCalls = allowMultiple };
        using var handler = new Handler(
            Frame("""{"provider":"Fixture Provider","choices":[{"delta":{"reasoning_details":[{"type":"reasoning.encrypted","index":0,"data":"opaque"}],"tool_calls":[{"index":0,"id":"first","function":{"name":"inspect","arguments":"{}"}},{"index":1,"id":"second","function":{"name":"inspect","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""")
            + Trailer("tool_calls") + Frame("[DONE]"));
        using var client = new HttpClient(handler);
        var chunks = new List<ModelChunk>();
        async Task CollectAsync()
        {
            await foreach (var chunk in Provider(client, model).StreamAsync(request, TestContext.Current.CancellationToken))
            {
                chunks.Add(chunk);
            }
        }

        if (allowMultiple == false)
        {
            await Assert.ThrowsAsync<MalformedModelOutputException>(CollectAsync);
            Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.ResponseEnvelope is not null);
        }
        else
        {
            await CollectAsync();
            Assert.Equal(2, chunks.Count(chunk => chunk.Output is ToolRequestModelOutput));
            Assert.Single(chunks, chunk => chunk.ResponseEnvelope is not null);
            Assert.True(chunks.FindIndex(chunk => chunk.ResponseEnvelope is not null) < chunks.FindIndex(chunk => chunk.Output is not null));
        }

        Assert.Single(chunks, chunk => chunk.Usage is not null);
        using var json = JsonDocument.Parse(handler.Body!);
        if (parallelParameterSupported && allowMultiple is { } expected)
        {
            Assert.Equal(expected, json.RootElement.GetProperty("parallel_tool_calls").GetBoolean());
        }
        else
        {
            Assert.False(json.RootElement.TryGetProperty("parallel_tool_calls", out _));
        }
    }

    /// <summary>Validates the OpenRouter request and response boundary.</summary>
    [Fact]
    public static async Task RepeatedTerminalTrailer_ReleasesOneBatchAfterUsageAndReplay()
    {
        var model = Model() with { ReasoningControl = ReasoningControllability.AlwaysOn, SupportsReasoningReplay = true, Upstream = "test", ExpectedProviderName = "test" };
        using var handler = new Handler(
            Frame("""{"choices":[{"delta":{"reasoning":"thinking","reasoning_details":[{"type":"reasoning.text","index":0,"text":"thinking","signature":"sig"}]}}]}""")
            + Frame("""{"choices":[{"delta":{"reasoning_details":[{"type":"reasoning.text","index":0,"signature":"nature"}],"tool_calls":[{"index":0,"id":"original-id","function":{"name":"inspect","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""")
            + Trailer("tool_calls") + Frame("[DONE]"));
        using var client = new HttpClient(handler);
        var chunks = await Provider(client, model).StreamAsync(Request(), TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal("thinking", string.Concat(chunks.Select(chunk => chunk.Reasoning)));
            Assert.Single(chunks, chunk => chunk.Output is not null);
            Assert.Single(chunks, chunk => chunk.FinishReason is not null);
            var usage = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage!;
            Assert.Equal(0.2m, usage.ReportedCost);
            Assert.Equal(3, usage.Cache!.CacheWriteTokens);
            Assert.True(chunks.FindIndex(chunk => chunk.Usage is not null) < chunks.FindIndex(chunk => chunk.ResponseEnvelope is not null));
            Assert.True(chunks.FindIndex(chunk => chunk.ResponseEnvelope is not null) < chunks.FindIndex(chunk => chunk.Output is not null));
            var replay = Assert.Single(chunks, chunk => chunk.ResponseEnvelope is not null).ResponseEnvelope!;
            // A direct consumer does not retain envelopes; the provider clears them on iteration advance.
            Assert.DoesNotContain("signature", JsonSerializer.Serialize(chunks), StringComparison.Ordinal);
            Assert.Equal(1, handler.Calls);
        }
        finally
        {
            foreach (var chunk in chunks)
            {
                chunk.ResponseEnvelope?.Dispose();
            }
        }
    }

    /// <summary>Rejects invalid protocol data without executing tools.</summary>
    [Theory]
    [InlineData("length")]
    [InlineData("stop")]
    public static async Task UnsuccessfulToolTermination_PreservesUsageButReleasesNoTools(string reason)
    {
        using var handler = new Handler(Frame("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"id","function":{"name":"inspect","arguments":"{}"}}]}}]}""") + Trailer(reason) + Frame("[DONE]"));
        using var client = new HttpClient(handler);
        var chunks = new List<ModelChunk>();
        await Assert.ThrowsAsync<MalformedModelOutputException>(async () =>
        {
            await foreach (var chunk in Provider(client, Model()).StreamAsync(Request(), TestContext.Current.CancellationToken))
            {
                chunks.Add(chunk);
            }
        });
        Assert.Single(chunks, chunk => chunk.Usage is not null);
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null);
    }

    /// <summary>Validates the OpenRouter request and response boundary.</summary>
    [Fact]
    public static async Task LateError_ReleasesNoTools()
    {
        using var handler = new Handler(Frame("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"id","function":{"name":"inspect","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""")
            + Frame("""{"error":{"code":402,"message":"private"}}"""));
        using var client = new HttpClient(handler);
        var chunks = new List<ModelChunk>();
        var error = await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var chunk in Provider(client, Model()).StreamAsync(Request(), TestContext.Current.CancellationToken))
            {
                chunks.Add(chunk);
            }
        });
        Assert.DoesNotContain("private", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null);
    }

    /// <summary>Validates the OpenRouter request and response boundary.</summary>
    [Fact]
    public static async Task RequestProjection_UsesNestedReasoningAndPreservesNonStrictSchema()
    {
        var model = Model() with
        {
            ReasoningControl = ReasoningControllability.Selectable,
            SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.High],
            EffortMap = new Dictionary<ReasoningLevel, string> { [ReasoningLevel.High] = "high" },
        };
        using var handler = new Handler(Trailer("stop") + Frame("[DONE]"));
        using var client = new HttpClient(handler);
        await Provider(client, model).StreamAsync(Request() with { ReasoningLevel = ReasoningLevel.High }, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(handler.Body!);
        var body = json.RootElement;
        Assert.Equal("high", body.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.True(body.TryGetProperty("max_tokens", out _));
        Assert.False(body.TryGetProperty("max_completion_tokens", out _));
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
        var function = body.GetProperty("tools")[0].GetProperty("function");
        Assert.False(function.TryGetProperty("strict", out _));
        Assert.False(function.GetProperty("parameters").TryGetProperty("required", out _));
    }

    /// <summary>Rejects invalid protocol data without executing tools.</summary>
    [Theory]
    [InlineData("{\"type\":\"reasoning.text\",\"index\":\"bad\",\"text\":\"x\"}")]
    [InlineData("{\"type\":\"reasoning.text\",\"text\":{}}")]
    public static async Task InvalidDetails_ProduceClassifiedFailure(string detail)
    {
        using var handler = new Handler(Frame("{\"choices\":[{\"delta\":{\"reasoning_details\":[" + detail + "]}}]}"));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<MalformedModelOutputException>(async () => await Provider(client, Model()).StreamAsync(Request(), TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Validates the OpenRouter request and response boundary.</summary>
    [Fact]
    public static void Preparation_IncludesSchemaAndRejectsBudgetWithoutHeadroom()
    {
        var registration = new OpenRouterProviderRegistration();
        var model = Model() with { JsonSchemaResponses = true };
        var config = Configuration(model);
        var profile = Assert.Single(registration.CreateProfiles(config));
        var context = new ModelRequestPreparationContext { ProviderConfiguration = config, ModelConfiguration = model, Profile = profile, Request = Request() with { Tools = [] } };
        var plain = registration.Prepare(context);
        var schema = registration.Prepare(context with { Request = context.Request with { ResponseFormat = new ModelResponseFormat { SchemaId = "test", JsonSchema = "{\"type\":\"object\",\"description\":\"" + new string('x', 5000) + "\"}" } } });
        Assert.True(schema.WireEstimate.WireInputTokens >= plain.WireEstimate.WireInputTokens + 5000);
        Assert.NotEqual(plain.WireDigest, schema.WireDigest);
    }

    /// <summary>Validates the OpenRouter request and response boundary.</summary>
    [Fact]
    public static void Validation_RequiresPinnedReplayAndSecret()
    {
        var registration = new OpenRouterProviderRegistration();
        Assert.Throws<InvalidOperationException>(() => registration.Validate(Configuration(Model()) with { SecretKeyReference = null }));
        Assert.Throws<InvalidOperationException>(() => registration.Validate(Configuration(Model() with { Capabilities = new ModelCapabilitySet { Streaming = true, StructuredOutput = true } })));
        Assert.Throws<InvalidOperationException>(() => registration.Validate(Configuration(Model() with { SupportsReasoningReplay = true, ReasoningControl = ReasoningControllability.AlwaysOn })));
    }

    /// <summary>Explicit markers share preparation with dispatch, including text-tool mode.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CacheProjection_IsDeterministicAndSupportsTextTools(bool textTools)
    {
        var model = Model() with { MinimumCacheableTokens = 1, RequestOutputTokenReserve = 1000 };
        var config = Configuration(model);
        var registration = new OpenRouterProviderRegistration();
        var profile = Assert.Single(registration.CreateProfiles(config));
        var request = Request() with
        {
            Tools = [],
            ToolTransportMode = textTools ? ToolTransportMode.Text : ToolTransportMode.Native,
            WireEstimate = new ModelWireEstimate { TextToolTokens = 25 },
            Layout = new ModelRequestLayout { CacheFamily = "test", StablePrefixDigest = "test", StablePrefixMessageCount = 1 },
            Messages = [new ModelMessage { Role = ModelMessageRole.System, SectionId = "host-policy", Content = [new() { Content = new string('x', 100) }] },
                new ModelMessage { Role = ModelMessageRole.User, SectionId = "user", Content = [new() { Content = "hello" }] }],
        };
        var context = new ModelRequestPreparationContext { Request = request, Profile = profile, ProviderConfiguration = config, ModelConfiguration = model };
        var prepared = registration.Prepare(context);
        Assert.Single(prepared.CachePlan!.Breakpoints);
        Assert.Equal(prepared.WireDigest, registration.Prepare(context with { Request = request with { WireEstimate = prepared.WireEstimate, Preparation = prepared } }).WireDigest);
        using var handler = new Handler(Trailer("stop") + Frame("[DONE]"));
        using var client = new HttpClient(handler);
        await Provider(client, model).StreamAsync(request, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal("ephemeral", json.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal(1000, json.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal(request.RunId.Value.ToString("D"), json.RootElement.GetProperty("session_id").GetString());
    }

    /// <summary>Vision admission is independent of compressed transport size.</summary>
    [Fact]
    public static void Preparation_CountsImageDimensions()
    {
        var model = Model() with { ContextWindow = 1000000, SupportedInputContentTypes = ModelContentTypes.Text | ModelContentTypes.Image, SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed };
        var config = Configuration(model);
        var registration = new OpenRouterProviderRegistration();
        var profile = Assert.Single(registration.CreateProfiles(config));
        var image = new ImageArtifact { Id = "image", ContentHash = "hash", MediaType = "image/png", ByteLength = 100, Width = 4096, Height = 4096, Source = "fixture" };
        var request = Request() with { Messages = [new ModelMessage { Role = ModelMessageRole.User, SectionId = "image", Content = [new() { Kind = ModelContentPartKind.Image, Image = image }] }] };
        var result = registration.Prepare(new ModelRequestPreparationContext { Request = request, Profile = profile, ProviderConfiguration = config, ModelConfiguration = model });
        Assert.True(result.WireEstimate.WireInputTokens >= ModelImages.EstimateTokens(image));
    }

    /// <summary>Charges and cache estimates survive partial usage and failed responses.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task UsageWithoutCharge_PreservesCacheWritePricing(bool error)
    {
        var model = Model() with { Cost = new ModelCostMetadata { InputPerMillionTokens = 1, OutputPerMillionTokens = 1, CachePricing = new ModelCachePricing { ReadPerMillionTokens = 0.1m, WritePerMillionTokens = 10, SourceDate = "2026-10-28" } } };
        var body = Frame("{\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":2,\"prompt_tokens_details\":{\"cached_tokens\":0,\"cache_write_tokens\":10}}," + (error ? "\"error\":{\"code\":402}" : "\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]") + "}") + Frame("[DONE]");
        using var handler = new Handler(body);
        using var client = new HttpClient(handler);
        var chunks = new List<ModelChunk>();
        async Task CollectAsync()
        {
            await foreach (var chunk in Provider(client, model).StreamAsync(Request(), TestContext.Current.CancellationToken))
            {
                chunks.Add(chunk);
            }
        }

        if (error)
        {
            await Assert.ThrowsAsync<ModelProviderException>(CollectAsync);
        }
        else
        {
            await CollectAsync();
        }

        var usage = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage!;
        Assert.Null(usage.ReportedCost);
        Assert.True(usage.EstimatedCost >= 0.000102m);
    }

    /// <summary>Provider identity mismatches cannot release a completed batch.</summary>
    [Fact]
    public static async Task ReturnedProviderMismatch_IsRejected()
    {
        using var handler = new Handler(Frame("{\"provider\":\"other\",\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}") + Frame("[DONE]"));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<MalformedModelOutputException>(async () => await Provider(client, Model() with { Upstream = "test/region", ExpectedProviderName = "Test" }).StreamAsync(Request(), TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Repository layers cannot change trusted upstream routing under an inherited model id.</summary>
    [Fact]
    public static void Catalog_RoundTripsAndProtectsRouting()
    {
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-router-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var model = Model() with { Upstream = "trusted" };
            var registry = new ModelProviderRegistry([new OpenRouterProviderRegistration()]);
            var user = Path.Combine(root, "user.json");
            var repository = Path.Combine(root, "repository.json");
            File.WriteAllText(user, JsonSerializer.Serialize(new ModelProviderCatalogConfiguration { Providers = [Configuration(model)] }, registry.CreateSerializerOptions()));
            Assert.Single(ModelProviderConfigurationLoader.Load(user, repository, registry).ModelCatalog.Profiles);
            File.WriteAllText(repository, JsonSerializer.Serialize(new { providers = new[] { new { id = "router", models = new[] { new { id = model.Id.Value, upstream = "untrusted" } } } } }));
            Assert.Throws<InvalidOperationException>(() => ModelProviderConfigurationLoader.Load(user, repository, registry));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Unexpected required private state cannot be dropped after tool effects are released.</summary>
    [Fact]
    public static async Task ReplayDisabled_SignedToolsAreRejectedAfterAccounting()
    {
        using var handler = new Handler(Frame("""{"choices":[{"delta":{"reasoning_details":[{"type":"reasoning.encrypted","index":0,"data":"opaque"}],"tool_calls":[{"index":0,"id":"id","function":{"name":"inspect","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""")
            + Trailer("tool_calls") + Frame("[DONE]"));
        using var client = new HttpClient(handler);
        var chunks = new List<ModelChunk>();
        await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var chunk in Provider(client, Model() with { ReasoningControl = ReasoningControllability.AlwaysOn }).StreamAsync(Request(), TestContext.Current.CancellationToken))
            {
                chunks.Add(chunk);
            }
        });
        Assert.Single(chunks, chunk => chunk.Usage is not null);
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.ResponseEnvelope is not null);
    }

    /// <summary>Opaque protocol state does not suppress a separately permitted plain summary.</summary>
    [Fact]
    public static async Task EncryptedDetails_PreservePlainDisplay()
    {
        using var handler = new Handler(Frame("""{"choices":[{"delta":{"reasoning":"summary","reasoning_details":[{"type":"reasoning.encrypted","index":0,"data":"opaque"}]}}]}""")
            + Trailer("stop") + Frame("[DONE]"));
        using var client = new HttpClient(handler);
        var chunks = await Provider(client, Model()).StreamAsync(Request(), TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal("summary", string.Concat(chunks.Select(chunk => chunk.Reasoning)));
        Assert.DoesNotContain("opaque", JsonSerializer.Serialize(chunks), StringComparison.Ordinal);
    }

    /// <summary>Replay preserves ordering, metadata and null/empty fields while joining all fragment kinds.</summary>
    [Fact]
    public static async Task FragmentedReasoning_PreservesReplayAndDisplay()
    {
        var model = Model() with { ReasoningControl = ReasoningControllability.AlwaysOn, SupportsReasoningReplay = true, Upstream = "test", ExpectedProviderName = "test" };
        using var handler = new Handler(
            Frame("""{"choices":[{"delta":{"reasoning_details":[{"type":"reasoning.text","index":2,"id":"text","text":null,"signature":null,"format":"test","extra":{"key":1}},{"type":"reasoning.summary","id":"summary","summary":""},{"type":"reasoning.encrypted","index":0,"data":"opaque-"}]}}]}""")
            + Frame("""{"choices":[{"delta":{"reasoning_details":[{"type":"reasoning.text","index":2,"text":"think","signature":"sig"},{"type":"reasoning.summary","id":"summary","summary":"sum","text":null},{"type":"reasoning.encrypted","index":0,"data":null}]}}]}""")
            + Frame("""{"choices":[{"delta":{"reasoning_details":[{"type":"reasoning.text","index":2,"text":"ing","signature":"nature","format":"test","extra":{"key":1}},{"type":"reasoning.summary","id":"summary","summary":"mary","signature":""},{"type":"reasoning.encrypted","index":0,"data":"state"}],"tool_calls":[{"index":0,"id":"id","function":{"name":"inspect","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""")
            + Trailer("tool_calls") + Frame("[DONE]"));
        using var client = new HttpClient(handler);
        var display = new StringBuilder();
        JsonElement? replay = null;
        await foreach (var chunk in Provider(client, model).StreamAsync(Request(), TestContext.Current.CancellationToken))
        {
            display.Append(chunk.Reasoning);
            if (chunk.ResponseEnvelope is { } envelope)
            {
                var copyPayload = typeof(ModelResponseReplayEnvelope).GetMethod("CopyProtocolPayload", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .CreateDelegate<Func<byte[]>>(envelope);
                using var payload = JsonDocument.Parse(copyPayload());
                replay = payload.RootElement.GetProperty("response").GetProperty("reasoning_details").Clone();
            }
        }

        Assert.Equal("thinksumingmary", display.ToString());
        var blocks = replay!.Value;
        Assert.Equal(3, blocks.GetArrayLength());
        Assert.Equal(2, blocks[0].GetProperty("index").GetInt32());
        Assert.Equal("text", blocks[0].GetProperty("id").GetString());
        Assert.Equal("thinking", blocks[0].GetProperty("text").GetString());
        Assert.Equal("signature", blocks[0].GetProperty("signature").GetString());
        Assert.Equal("test", blocks[0].GetProperty("format").GetString());
        Assert.Equal(1, blocks[0].GetProperty("extra").GetProperty("key").GetInt32());
        Assert.False(blocks[0].TryGetProperty("summary", out _));
        Assert.Equal("summary", blocks[1].GetProperty("summary").GetString());
        Assert.Equal(JsonValueKind.Null, blocks[1].GetProperty("text").ValueKind);
        Assert.Equal(string.Empty, blocks[1].GetProperty("signature").GetString());
        Assert.Equal(0, blocks[2].GetProperty("index").GetInt32());
        Assert.Equal("opaque-state", blocks[2].GetProperty("data").GetString());
    }

    private static string Frame(string value) => "data: " + value + "\n\n";

    private static string Trailer(string reason) => Frame("{\"choices\":[{\"delta\":{\"content\":\"\"},\"finish_reason\":\"" + reason + "\"}],\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":8,\"cost\":0.2,\"prompt_tokens_details\":{\"cached_tokens\":2,\"cache_write_tokens\":3}}}");

    private static OpenRouterModelConfiguration Model() => new()
    {
        Id = ModelProfileId.New(),
        Name = "test",
        ModelId = "vendor/model",
        ContextWindow = 32000,
        MaximumOutputTokens = 4000,
        Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true },
        RetryMaxAttempts = 1,
    };

    private static OpenRouterProviderConfiguration Configuration(OpenRouterModelConfiguration model) => new()
    {
        Id = "router",
        Name = "Router",
        SecretKeyReference = "secrets:router",
        Models = [model],
    };

    private static IModelProvider Provider(HttpClient client, OpenRouterModelConfiguration model)
    {
        var registration = new OpenRouterProviderRegistration();
        var configuration = Configuration(model);
        return registration.CreateProvider(new ModelProviderActivationContext
        {
            ProviderConfiguration = configuration,
            ModelConfiguration = model,
            Profile = Assert.Single(registration.CreateProfiles(configuration)),
            HttpClient = client,
            ResolvedSecret = "test-key",
        });
    }

    private static ModelStreamRequest Request() => new()
    {
        RunId = RunId.New(),
        Input = "test",
        Tools = [new ModelToolDefinition { Name = "inspect", Description = "Inspect", PreferStrictArguments = true, ArgumentsJsonSchema = "{\"type\":\"object\",\"properties\":{\"optional\":{\"type\":\"string\"}}}" }],
    };

    private sealed class Handler : HttpMessageHandler
    {
        private readonly string _body;

        public Handler(string body) => _body = body;

        public int Calls { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body, Encoding.UTF8, "text/event-stream") };
        }
    }
}
