namespace Threadsmith.ModelTooling.Tests;

using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible.OpenRouter;
using Xunit;

/// <summary>Exercises compiled OpenRouter protocol behavior without remote credentials.</summary>
public static partial class OpenRouterProviderTests
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
        using var handler = new Handler(Completed(ReasoningItem("opaque"), FunctionCall("first"), FunctionCall("second")));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
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
        var terminal = Completed(ReasoningItem("opaque", "thinking"), FunctionCall("original-id"));
        using var handler = new Handler(ReasoningDelta("thinking") + terminal + terminal + Frame("[DONE]"));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
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
    [InlineData("incomplete")]
    [InlineData("failed")]
    public static async Task UnsuccessfulToolTermination_PreservesUsageButReleasesNoTools(string reason)
    {
        using var handler = new Handler(Terminal("response." + reason, reason, new JsonArray(FunctionCall("id")), Usage()));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();
        await Assert.ThrowsAnyAsync<ModelProviderException>(async () =>
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
        using var handler = new Handler(Completed(FunctionCall("id")) + Failed(402));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
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
        using var handler = new Handler(Completed());
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        await Provider(client, model).StreamAsync(Request() with { ReasoningLevel = ReasoningLevel.High }, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(handler.Body!);
        var body = json.RootElement;
        Assert.Equal("high", body.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.True(body.TryGetProperty("max_output_tokens", out _));
        Assert.Equal(new Uri("https://openrouter.ai/api/v1/responses"), handler.RequestUri);
        Assert.False(body.GetProperty("store").GetBoolean());
        Assert.False(body.TryGetProperty("messages", out _));
        Assert.False(body.TryGetProperty("max_completion_tokens", out _));
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
        var function = body.GetProperty("tools")[0];
        Assert.False(function.GetProperty("strict").GetBoolean());
        Assert.False(function.GetProperty("parameters").TryGetProperty("required", out _));
    }

    /// <summary>Rejects invalid protocol data without executing tools.</summary>
    [Theory]
    [InlineData("{\"type\":\"reasoning\",\"encrypted_content\":{},\"summary\":[]}")]
    [InlineData("{\"type\":\"reasoning\",\"summary\":{}}")]
    public static async Task InvalidDetails_ProduceClassifiedFailure(string detail)
    {
        using var handler = new Handler(Completed(JsonNode.Parse(detail)!));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        await Assert.ThrowsAsync<MalformedModelOutputException>(async () => await Provider(client, Model()).StreamAsync(Request(), TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Validates the OpenRouter request and response boundary.</summary>
    [Fact]
    public static void Preparation_IncludesResponseSchemaAsTokens()
    {
        var registration = new OpenRouterProviderRegistration();
        var model = Model() with { JsonSchemaResponses = true };
        var config = Configuration(model);
        var profile = Assert.Single(registration.CreateProfiles(config));
        var context = new ModelRequestPreparationContext { ProviderConfiguration = config, ModelConfiguration = model, Profile = profile, Request = Request() with { Tools = [] } };
        var plain = registration.Prepare(context);
        var schema = registration.Prepare(context with { Request = context.Request with { ResponseFormat = new ModelResponseFormat { SchemaId = "test", JsonSchema = "{\"type\":\"object\",\"description\":\"" + new string('x', 5000) + "\"}" } } });
        Assert.InRange(schema.WireEstimate.WireInputTokens - plain.WireEstimate.WireInputTokens, 1250, 1300);
        Assert.NotEqual(plain.WireDigest, schema.WireDigest);
    }

    /// <summary>Transport escaping and UTF-8 byte counts must not trigger premature context pressure.</summary>
    [Theory]
    [InlineData("sample text ")]
    [InlineData("<>\"\\\n")]
    [InlineData("æ—¥æœ¬èªž")]
    public static void Preparation_UsesTokenUnitsAndPreservesAttribution(string fragment)
    {
        var registration = new OpenRouterProviderRegistration();
        var model = Model();
        var config = Configuration(model);
        var profile = Assert.Single(registration.CreateProfiles(config));
        var text = string.Concat(Enumerable.Repeat(fragment, 1000));
        var request = Request() with
        {
            Tools = [],
            Messages = [new ModelMessage { Role = ModelMessageRole.User, SectionId = "user", Content = [new() { Content = text }] }],
        };
        var context = new ModelRequestPreparationContext { ProviderConfiguration = config, ModelConfiguration = model, Profile = profile, Request = request };
        var prepared = registration.Prepare(context);
        Assert.InRange(prepared.WireEstimate.WireInputTokens, text.Length / 4, (text.Length / 4) + 300);
        Assert.Equal(prepared.WireEstimate.WireInputTokens, prepared.WireEstimate.Components.Sum(component => component.Tokens));
        Assert.True(prepared.WireEstimate.SectionTokens["user"] > 0);
        var repeated = registration.Prepare(context with { Request = request with { WireEstimate = prepared.WireEstimate, Preparation = prepared } });
        Assert.Equal(prepared.WireEstimate.WireInputTokens, repeated.WireEstimate.WireInputTokens);
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public static async Task CacheProjection_IsDeterministicAndSupportsTextTools(bool textTools, bool toolTail)
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
        if (toolTail)
        {
            request = request with
            {
                Messages = [.. request.Messages,
                    Message(ModelMessageRole.Assistant, "{}") with { ToolCallId = "call", ToolName = "inspect" },
                    Message(ModelMessageRole.Tool, "result") with { ToolCallId = "call" }],
            };
        }

        var context = new ModelRequestPreparationContext { Request = request, Profile = profile, ProviderConfiguration = config, ModelConfiguration = model };
        var prepared = registration.Prepare(context);
        Assert.Single(prepared.CachePlan!.Breakpoints);
        Assert.Equal(prepared.WireDigest, registration.Prepare(context with { Request = request with { WireEstimate = prepared.WireEstimate, Preparation = prepared } }).WireDigest);
        using var handler = new Handler(Completed());
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        await Provider(client, model).StreamAsync(request, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(handler.Body!);
        var tail = json.RootElement.GetProperty("input")[toolTail ? 3 : 1];
        var block = tail.GetProperty(toolTail ? "output" : "content")[0];
        Assert.Equal("input_text", block.GetProperty("type").GetString());
        Assert.Equal(toolTail ? "result" : "hello", block.GetProperty("text").GetString());
        Assert.Equal("explicit", block.GetProperty("prompt_cache_breakpoint").GetProperty("mode").GetString());
        Assert.DoesNotContain("cache_control", handler.Body, StringComparison.Ordinal);
        Assert.Equal(1000, json.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(request.RunId.Value.ToString("D"), json.RootElement.GetProperty("session_id").GetString());
    }

    /// <summary>Automatic caching and ineligible prefixes do not introduce explicit boundaries.</summary>
    [Theory]
    [InlineData("disabled")]
    [InlineData("automatic-only")]
    [InlineData("no-layout")]
    [InlineData("short-prefix")]
    [InlineData("no-boundary")]
    public static async Task CacheProjection_OmitsUnplannedMarkers(string scenario)
    {
        var model = Model() with
        {
            MinimumCacheableTokens = scenario is "disabled" or "automatic-only" ? null : scenario == "short-prefix" ? 10000 : 1,
            AutomaticPrefixCaching = scenario == "automatic-only",
        };
        var registration = new OpenRouterProviderRegistration();
        var config = Configuration(model);
        var profile = Assert.Single(registration.CreateProfiles(config));
        var request = Request() with
        {
            Layout = scenario == "no-layout" ? null : new ModelRequestLayout
            {
                CacheFamily = "test",
                StablePrefixDigest = "test",
                StablePrefixMessageCount = scenario == "no-boundary" ? 0 : 1,
            },
            Messages = [Message(ModelMessageRole.System, new string('x', 100)), Message(ModelMessageRole.User, "hello")],
        };
        using var handler = new Handler(Completed());
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        var prepared = registration.Prepare(new ModelRequestPreparationContext { Request = request, Profile = profile, ProviderConfiguration = config, ModelConfiguration = model });
        await CollectAsync(Provider(client, model), request, []);

        Assert.Empty(prepared.CachePlan!.Breakpoints);
        Assert.DoesNotContain("prompt_cache_breakpoint", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("cache_control", handler.Body, StringComparison.Ordinal);
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
        var wireUsage = JsonNode.Parse("""{"input_tokens":10,"output_tokens":2,"input_tokens_details":{"cached_tokens":0,"cache_write_tokens":10}}""")!.AsObject();
        var body = error ? Failed(402, wireUsage) : Terminal("response.completed", "completed", [], wireUsage);
        using var handler = new Handler(body);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
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
        using var handler = new Handler(Terminal("response.completed", "completed", [], Usage(), "other"));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
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
        using var handler = new Handler(Completed(ReasoningItem("opaque"), FunctionCall("id")));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
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
        using var handler = new Handler(ReasoningDelta("summary") + Completed(ReasoningItem("opaque", "summary")));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = await Provider(client, Model()).StreamAsync(Request(), TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal("summary", string.Concat(chunks.Select(chunk => chunk.Reasoning)));
        Assert.DoesNotContain("opaque", JsonSerializer.Serialize(chunks), StringComparison.Ordinal);
    }

    /// <summary>Replay preserves ordering, metadata and null/empty fields while joining all fragment kinds.</summary>
    [Fact]
    public static async Task FragmentedReasoning_PreservesReplayAndDisplay()
    {
        var model = Model() with { ReasoningControl = ReasoningControllability.AlwaysOn, SupportsReasoningReplay = true, Upstream = "test", ExpectedProviderName = "test" };
        var reasoning = ReasoningItem("opaque-state", "thinking");
        reasoning["signature"] = null;
        reasoning["format"] = string.Empty;
        reasoning["extra"] = new JsonObject { ["key"] = 1 };
        using var handler = new Handler(ReasoningDelta("think") + ReasoningDelta("ing") + Completed(reasoning, FunctionCall("id")));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
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
                replay = payload.RootElement.GetProperty("response").Clone();
            }
        }

        Assert.Equal("thinking", display.ToString());
        var blocks = replay!.Value;
        Assert.Equal(2, blocks.GetArrayLength());
        Assert.Equal("reasoning", blocks[0].GetProperty("type").GetString());
        Assert.Equal("reasoning", blocks[0].GetProperty("id").GetString());
        Assert.Equal("thinking", blocks[0].GetProperty("summary")[0].GetProperty("text").GetString());
        Assert.Equal(JsonValueKind.Null, blocks[0].GetProperty("signature").ValueKind);
        Assert.Equal(string.Empty, blocks[0].GetProperty("format").GetString());
        Assert.Equal(1, blocks[0].GetProperty("extra").GetProperty("key").GetInt32());
        Assert.Equal("opaque-state", blocks[0].GetProperty("encrypted_content").GetString());
        Assert.Equal("function_call", blocks[1].GetProperty("type").GetString());
        Assert.Equal("id", blocks[1].GetProperty("call_id").GetString());
    }

    /// <summary>OpenRouter uses shared rate-limit recovery and its configured attempt limit.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public static async Task RateLimitStream_UsesSharedRetriesAndOneBusyNotice(int maximumAttempts)
    {
        using var handler = new Handler(Failed(429));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var model = Model() with { RetryMaxAttempts = maximumAttempts, RetryDelayMilliseconds = 0 };
        var chunks = new List<ModelChunk>();
        var exception = await Assert.ThrowsAsync<TransientModelException>(async () =>
        {
            await foreach (var chunk in Provider(client, model).StreamAsync(Request(), TestContext.Current.CancellationToken))
            {
                chunks.Add(chunk);
            }
        });
        Assert.Equal(maximumAttempts, handler.Calls);
        Assert.Equal(maximumAttempts > 1 ? 1 : 0, chunks.Count(chunk => chunk.ProviderBusy));
        Assert.DoesNotContain("private", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Reported charges and cache counters survive each rate-limited attempt.</summary>
    [Fact]
    public static async Task RateLimit_PreservesChargesAndCacheUsage()
    {
        using var handler = new Handler(Failed(429, JsonNode.Parse("""{"input_tokens":10,"output_tokens":2,"cost":0.2,"input_tokens_details":{"cached_tokens":3,"cache_write_tokens":1}}""")!.AsObject()));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var model = Model() with { RetryMaxAttempts = 3, RetryDelayMilliseconds = 0 };
        var usages = new List<ModelUsage>();
        await Assert.ThrowsAsync<TransientModelException>(async () =>
        {
            await foreach (var chunk in Provider(client, model).StreamAsync(Request(), TestContext.Current.CancellationToken))
            {
                if (chunk.Usage is { } usage)
                {
                    usages.Add(usage);
                }
            }
        });
        Assert.Equal(new decimal?[] { 0.2m, 0.4m, 0.6m }, usages.Select(usage => usage.ReportedCost));
        Assert.Equal(9, usages[^1].Cache?.CacheReadTokens);
        Assert.Equal(3, usages[^1].Cache?.CacheWriteTokens);
    }

    private static string Frame(string value) => "data: " + value + "\n\n";

    private static string Completed(params JsonNode[] items) => Terminal("response.completed", "completed", [.. items], Usage());

    private static string Terminal(string type, string status, JsonArray output, JsonObject? usage, string? provider = null)
        => Frame(new JsonObject
        {
            ["type"] = type,
            ["response"] = new JsonObject { ["id"] = "response", ["status"] = status, ["model"] = "vendor/model", ["provider"] = provider, ["output"] = output, ["usage"] = usage },
        }.ToJsonString());

    private static string Failed(int code, JsonObject? usage = null) => Frame(new JsonObject
    {
        ["type"] = "response.failed",
        ["response"] = new JsonObject { ["status"] = "failed", ["usage"] = usage, ["error"] = new JsonObject { ["code"] = code, ["message"] = "private" } },
    }.ToJsonString());

    private static string ReasoningDelta(string delta) => Frame(new JsonObject
    {
        ["type"] = "response.reasoning_summary_text.delta",
        ["item_id"] = "reasoning",
        ["output_index"] = 0,
        ["summary_index"] = 0,
        ["delta"] = delta,
    }.ToJsonString());

    private static JsonObject Usage() => new()
    {
        ["input_tokens"] = 10,
        ["output_tokens"] = 8,
        ["cost"] = 0.2m,
        ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 2, ["cache_write_tokens"] = 3 },
    };

    private static JsonObject FunctionCall(string id) => new()
    {
        ["type"] = "function_call",
        ["id"] = "item-" + id,
        ["call_id"] = id,
        ["status"] = "completed",
        ["name"] = "inspect",
        ["arguments"] = "{}",
    };

    private static JsonObject ReasoningItem(string encrypted, string? summary = null) => new()
    {
        ["type"] = "reasoning",
        ["id"] = "reasoning",
        ["encrypted_content"] = encrypted,
        ["summary"] = summary is null ? [] : new JsonArray(new JsonObject { ["type"] = "summary_text", ["text"] = summary }),
    };

    private static OpenRouterModelConfiguration Model() => new()
    {
        Id = ModelProfileId.New(),
        Name = "test",
        ModelId = "vendor/model",
        TimeoutSeconds = 0,
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
            TimeProvider = new AdvancingTimeProvider(),
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
        private readonly string[] _responses;

        public Handler(params string[] responses) => _responses = responses;

        public List<string> Bodies { get; } = [];

        public int Calls { get; private set; }

        public string? Body { get; private set; }

        public Uri? RequestUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            RequestUri = request.RequestUri;
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Bodies.Add(Body);
            var body = _responses[Math.Min(Calls - 1, _responses.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };
        }
    }
}
