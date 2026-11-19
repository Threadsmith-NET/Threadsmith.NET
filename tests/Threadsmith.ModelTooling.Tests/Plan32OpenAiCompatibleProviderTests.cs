namespace Threadsmith.ModelTooling.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Xunit;

/// <summary>Verifies Plan 32 provider extraction, endpoint safety, activation, and legacy migration.</summary>
public static class Plan32OpenAiCompatibleProviderTests
{
    private static readonly ModelProfileId FirstModelId = new(
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));

    private static readonly ModelProfileId SecondModelId = new(
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

    /// <summary>Custom names survive catalog serialization and reach effort-based providers verbatim.</summary>
    [Theory]
    [InlineData("xhigh")]
    [InlineData("provider-Custom")]
    public static async Task ReasoningCompatibility_CustomEffort_PreservesProviderValue(string reasoning)
    {
        string? requestJson = null;
        using var client = new HttpClient(new RecordingHandler(async (request, cancellationToken) =>
        {
            requestJson = await ReadRequestBodyAsync(request, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n", Encoding.UTF8, "text/event-stream"),
            };
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        var level = new ReasoningLevel(reasoning);
        var model = CreateModel(FirstModelId, "custom-model") with
        {
            DefaultReasoningLevel = level,
            SupportedReasoningLevels = [ReasoningLevel.None, level],
            ReasoningCompatibility = new OpenAiReasoningCompatibilityConfiguration
            {
                Mode = OpenAiReasoningControlMode.StandardEffort,
            },
        };
        var registry = new ModelProviderRegistry([new OpenAiCompatibleProviderRegistration()]);
        var options = registry.CreateSerializerOptions();
        var json = JsonSerializer.Serialize<ModelConfiguration>(model, options);
        var reloaded = Assert.IsType<OpenAiCompatibleModelConfiguration>(
            JsonSerializer.Deserialize<ModelConfiguration>(json, options));
        Assert.Equal(reasoning, reloaded.DefaultReasoningLevel.Value);
        var provider = new ConfiguredModelProvider(client, CreateEffectiveCatalog(reloaded), (_, _) => Task.FromResult<string?>(null), timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));

        await foreach (var chunk in provider.StreamAsync(new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "hello",
            ReasoningLevel = reloaded.DefaultReasoningLevel,
        }))
        {
            Assert.Null(chunk.Output);
        }

        Assert.NotNull(requestJson);
        using var request = JsonDocument.Parse(requestJson);
        Assert.Equal(reasoning, request.RootElement.GetProperty("reasoning_effort").GetString());
    }

    /// <summary>Relative paths remain beneath the configured base path and may not traverse or replace authority.</summary>
    [Theory]
    [InlineData("../chat/completions")]
    [InlineData("%2e%2e/chat/completions")]
    [InlineData("//attacker.example/chat/completions")]
    [InlineData("chat/completions?key=value")]
    public static void Registration_UnsafeChatPath_IsRejected(string path)
    {
        var provider = CreateProvider(
            "unsafe",
            new Uri("https://models.example/v1/"),
            [CreateModel(FirstModelId, "model")]) with
        {
            ChatCompletionsPath = path,
        };

        Assert.Throws<InvalidOperationException>(() => new EffectiveModelProviderCatalog(
            new ModelProviderCatalogConfiguration { Providers = [provider] },
            new ModelProviderRegistry([new OpenAiCompatibleProviderRegistration()])));
    }

    /// <summary>Credential-like, hop-by-hop, and control-character configured headers fail closed.</summary>
    [Theory]
    [InlineData("Authorization", "value")]
    [InlineData("X-Api-Key", "value")]
    [InlineData("Connection", "keep-alive")]
    [InlineData("Proxy-Connection", "keep-alive")]
    [InlineData("X-Safe", "unsafe\rvalue")]
    public static void Registration_ForbiddenHeader_IsRejected(string name, string value)
    {
        var provider = CreateProvider(
            "headers",
            new Uri("https://models.example/v1/"),
            [CreateModel(FirstModelId, "model")]) with
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [name] = value },
        };

        Assert.Throws<InvalidOperationException>(() => new EffectiveModelProviderCatalog(
            new ModelProviderCatalogConfiguration { Providers = [provider] },
            new ModelProviderRegistry([new OpenAiCompatibleProviderRegistration()])));
    }

    /// <summary>Secrets resolve once at activation and all authentication/configured headers remain request-local.</summary>
    [Fact]
    public static async Task ConfiguredProvider_SecretAndHeaders_AreAppliedRequestLocally()
    {
        HttpRequestMessage? observedRequest = null;
        var handler = new RecordingHandler(request =>
        {
            observedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n", Encoding.UTF8, "text/event-stream"),
            };
        });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var configured = CreateProvider(
            "headers",
            new Uri("https://models.example/v1/"),
            [CreateModel(FirstModelId, "model")]) with
        {
            SecretKeyReference = "secrets:models:first",
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Tenant"] = "one" },
        };
        var catalog = new EffectiveModelProviderCatalog(
            new ModelProviderCatalogConfiguration
            {
                DefaultProviderId = "headers",
                DefaultModelId = FirstModelId,
                Providers = [configured],
            },
            new ModelProviderRegistry([new OpenAiCompatibleProviderRegistration()]));
        var secretResolutions = 0;
        var provider = new ConfiguredModelProvider(
            client,
            catalog,
            (_, _) =>
            {
                secretResolutions++;
                return Task.FromResult<string?>("canary-secret");
            },
            timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));

        await foreach (var chunk in provider.StreamAsync(new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "hello",
        }))
        {
            Assert.NotNull(chunk);
        }

        Assert.Equal(1, secretResolutions);
        Assert.NotNull(observedRequest);
        Assert.Equal("Bearer", observedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("canary-secret", observedRequest.Headers.Authorization?.Parameter);
        Assert.Equal("one", Assert.Single(observedRequest.Headers.GetValues("X-Tenant")));
        Assert.Null(client.DefaultRequestHeaders.Authorization);
        Assert.False(client.DefaultRequestHeaders.Contains("X-Tenant"));
    }

    /// <summary>Official OpenAI endpoints get cache affinity by default while other compatible servers require opt-in.</summary>
    [Theory]
    [InlineData("https://api.openai.com/v1/", null, true)]
    [InlineData("https://models.example/v1/", null, false)]
    [InlineData("https://models.example/v1/", true, true)]
    [InlineData("https://api.openai.com/v1/", false, false)]
    public static async Task ConfiguredProvider_PromptCacheKey_UsesSafeCompatibilityDefault(
        string baseUri,
        bool? configuredValue,
        bool expected)
    {
        string? requestJson = null;
        using var client = new HttpClient(new RecordingHandler(async (request, cancellationToken) =>
        {
            requestJson = await ReadRequestBodyAsync(request, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n", Encoding.UTF8, "text/event-stream"),
            };
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        var configured = CreateProvider(
            "cache-affinity",
            new Uri(baseUri),
            [CreateModel(FirstModelId, "model")]) with
        {
            PromptCacheKeyEnabled = configuredValue,
        };
        var catalog = new EffectiveModelProviderCatalog(
            new ModelProviderCatalogConfiguration { Providers = [configured] },
            new ModelProviderRegistry([new OpenAiCompatibleProviderRegistration()]));
        var provider = new ConfiguredModelProvider(client, catalog, (_, _) => Task.FromResult<string?>(null), timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var cacheAffinityId = Guid.NewGuid();

        _ = await provider.StreamAsync(
            new ModelStreamRequest
            {
                RunId = RunId.New(),
                CacheAffinityId = cacheAffinityId,
                ResolvedProfileId = FirstModelId,
                Input = "hello",
            },
            TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(Assert.IsType<string>(requestJson));
        Assert.Equal(expected, body.RootElement.TryGetProperty("prompt_cache_key", out var key));
        if (expected)
        {
            Assert.Equal(cacheAffinityId.ToString("D"), key.GetString());
        }
    }

    /// <summary>Adapted catalog profiles include provider display metadata without changing request settings.</summary>
    [Fact]
    public static async Task AdaptedCatalog_ProfileIncludesProviderDisplayName()
    {
        var legacy = new ConfiguredModelCatalog([CreateLegacyProfile()]).Profiles[0];
        var registration = new OpenAiCompatibleProviderRegistration();
        Uri? requestUri = null;
        var handler = new RecordingHandler(request =>
        {
            requestUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n", Encoding.UTF8, "text/event-stream"),
            };
        });

        var catalog = registration.CreateLegacyCatalog(
            new ConfiguredModelCatalog([legacy]));
        var provider = new ConfiguredModelProvider(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            catalog,
            (_, _) => Task.FromResult<string?>("legacy-secret"),
            legacy.Id,
            timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await foreach (var chunk in provider.StreamAsync(new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "legacy",
            WorkloadClass = WorkloadClass.Planning,
        }))
        {
            Assert.NotNull(chunk);
        }

        var projected = catalog.ModelCatalog.Get(legacy.Id);
        var definition = catalog.Get(legacy.Id);
        Assert.Equal(legacy with { ProviderName = definition.ProviderConfiguration.Name }, projected);
        Assert.Equal(legacy.Endpoint, requestUri);
        Assert.Equal("legacy-" + legacy.Id.Value.ToString("N"), definition.ProviderId);
        Assert.IsType<OpenAiCompatibleProviderConfiguration>(definition.ProviderConfiguration);
        Assert.IsType<OpenAiCompatibleModelConfiguration>(definition.ModelConfiguration);
    }

    /// <summary>Finite runtime limits retain exact configured values through profile and legacy projection.</summary>
    [Fact]
    public static void RuntimeLimits_ConfiguredValuesSurviveProfileAndLegacyProjection()
    {
        var model = CreateModel(FirstModelId, "runtime-limits") with
        {
            TimeoutSeconds = 600,
            MaximumStreamedBytes = 32L * 1024 * 1024,
            MaximumToolCalls = 1_000,
        };
        var catalog = CreateEffectiveCatalog(model);
        var profile = catalog.ModelCatalog.Get(FirstModelId);
        Assert.Equal(TimeSpan.FromSeconds(600), profile.Timeout);
        Assert.Equal(model.MaximumStreamedBytes, profile.MaximumStreamedBytes);
        Assert.Equal(model.MaximumToolCalls, profile.MaximumToolCalls);
        var adapted = new OpenAiCompatibleProviderRegistration().CreateLegacyCatalog(catalog.ModelCatalog);
        Assert.Equal(profile.MaximumStreamedBytes, adapted.ModelCatalog.Get(profile.Id).MaximumStreamedBytes);
        Assert.Equal(profile.MaximumToolCalls, adapted.ModelCatalog.Get(profile.Id).MaximumToolCalls);
    }

    /// <summary>Configured stream byte limits count UTF-8 text rather than UTF-16 characters or tokens.</summary>
    [Theory]
    [InlineData(4, true)]
    [InlineData(6, false)]
    public static async Task RuntimeLimits_StreamBytesAreEnforced(long maximumBytes, bool rejected)
    {
        const string content = "\u00e9\u00e9\u00e9";
        var payload = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content }, finish_reason = "stop" } } });
        using var http = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: " + payload + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        var model = CreateModel(FirstModelId, "byte-limit") with { MaximumStreamedBytes = maximumBytes };
        var profile = CreateEffectiveCatalog(model).ModelCatalog.Get(FirstModelId);
        var provider = new OpenAiCompatibleModelProvider(http, profile, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var request = new ModelStreamRequest { RunId = RunId.New(), Input = "Inspect the source." };
        if (rejected)
        {
            await Assert.ThrowsAsync<MalformedModelOutputException>(async () =>
                await provider.StreamAsync(request, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
        }
        else
        {
            var chunks = await provider.StreamAsync(request, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(content, string.Concat(chunks.Select(chunk => chunk.Text)));
        }
    }

    /// <summary>The configured finite tool-call limit may exceed the former compiled maximum.</summary>
    [Theory]
    [InlineData(1, 2, true)]
    [InlineData(300, 257, false)]
    public static async Task RuntimeLimits_ToolCallCountUsesConfiguredLimit(int maximum, int count, bool rejected)
    {
        var calls = Enumerable.Range(0, count).Select(index => new
        {
            index,
            id = "call_" + index,
            function = new { name = "inspect", arguments = "{}" },
        }).ToArray();
        var payload = JsonSerializer.Serialize(new { choices = new[] { new { delta = new { tool_calls = calls }, finish_reason = "tool_calls" } } });
        using var http = new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: " + payload + "\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        var model = CreateModel(FirstModelId, "tool-limit") with
        {
            MaximumToolCalls = maximum,
            Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true },
        };
        var provider = new OpenAiCompatibleModelProvider(http, CreateEffectiveCatalog(model).ModelCatalog.Get(FirstModelId), timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var request = new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "Inspect source.",
            Tools = [new ModelToolDefinition { Name = "inspect", Description = "Inspect source.", ArgumentsJsonSchema = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}" }],
        };
        if (rejected)
        {
            await Assert.ThrowsAsync<MalformedModelOutputException>(async () =>
                await provider.StreamAsync(request, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken));
        }
        else
        {
            var chunks = await provider.StreamAsync(request, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(count, chunks.Count(chunk => chunk.Output is ToolRequestModelOutput));
        }
    }

    /// <summary>A finite connection timeout may exceed the former five-minute product ceiling.</summary>
    [Theory]
    [InlineData(600)]
    [InlineData(int.MaxValue / 1000)]
    public static void HttpTransportOptions_FiniteConnectionTimeoutHasNoProductMaximum(int seconds)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["model:http:connectTimeoutSeconds"] = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();
        var options = ModelHttpTransportOptions.Load(configuration);
        using var handler = new SocketsHttpHandler { ConnectTimeout = options.ConnectTimeout };
        Assert.Equal(TimeSpan.FromSeconds(seconds), handler.ConnectTimeout);
    }

    /// <summary>Normal layered configuration controls bounded shared HTTP transport settings.</summary>
    [Fact]
    public static void HttpTransportOptions_LayeredValues_AreLoaded()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["model:http:pooledConnectionLifetimeSeconds"] = "600",
                ["model:http:pooledConnectionIdleTimeoutSeconds"] = "90",
                ["model:http:connectTimeoutSeconds"] = "15",
                ["model:http:maxConnectionsPerServer"] = "32",
            })
            .Build();

        var options = ModelHttpTransportOptions.Load(configuration);

        Assert.Equal(TimeSpan.FromMinutes(10), options.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromSeconds(90), options.PooledConnectionIdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), options.ConnectTimeout);
        Assert.Equal(32, options.MaxConnectionsPerServer);
    }

    /// <summary>Unsafe HTTP transport resource values fail startup validation.</summary>
    [Theory]
    [InlineData("model:http:pooledConnectionLifetimeSeconds", "0")]
    [InlineData("model:http:pooledConnectionIdleTimeoutSeconds", "0")]
    [InlineData("model:http:connectTimeoutSeconds", "-1")]
    [InlineData("model:http:connectTimeoutSeconds", "2147484")]
    [InlineData("model:http:maxConnectionsPerServer", "0")]
    public static void HttpTransportOptions_OutOfBoundsValue_IsRejected(string key, string value)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        Assert.Throws<InvalidOperationException>(() => ModelHttpTransportOptions.Load(configuration));
    }

    /// <summary>JSON mode is used only for structured responses without native tools.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task StructuredOutput_NativeTools_DoNotConstrainToolCallsToJson(
        bool structuredOutput,
        bool nativeTools)
    {
        string? requestJson = null;
        using var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            requestJson = await ReadRequestBodyAsync(request, cancellationToken);
            var stream = nativeTools
                ? "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"function\":{\"name\":\"inspect_file\",\"arguments\":\"{\\\"path\\\":\\\"README.md\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n"
                : "data: {\"choices\":[{\"delta\":{\"content\":\"{}\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
            };
        });
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var model = CreateModel(FirstModelId, "json-tool-compatibility") with
        {
            Capabilities = new ModelCapabilitySet
            {
                Streaming = true,
                ToolCalls = true,
                StructuredOutput = true,
            },
        };
        var provider = new ConfiguredModelProvider(
            http,
            CreateEffectiveCatalog(model),
            (_, _) => Task.FromResult<string?>(null),
            timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var chunks = new List<ModelChunk>();
        var tool = new ModelToolDefinition
        {
            Name = "inspect_file",
            Description = "Inspect one repository file.",
            ArgumentsJsonSchema = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}",
        };

        await foreach (var chunk in provider.StreamAsync(
            new ModelStreamRequest
            {
                RunId = RunId.New(),
                Input = "Inspect the file, then return a JSON object.",
                RequiredCapabilities = new ModelCapabilitySet
                {
                    Streaming = true,
                    ToolCalls = true,
                    StructuredOutput = structuredOutput,
                },
                Tools = nativeTools ? [tool] : [],
                AllowMultipleToolCalls = true,
            },
            TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        Assert.NotNull(requestJson);
        using var request = JsonDocument.Parse(requestJson);
        var body = request.RootElement;
        Assert.Equal(structuredOutput && !nativeTools, body.TryGetProperty("response_format", out var format));
        if (structuredOutput && !nativeTools)
        {
            Assert.Equal("json_object", format.GetProperty("type").GetString());
        }

        if (nativeTools)
        {
            var function = Assert.Single(body.GetProperty("tools").EnumerateArray()).GetProperty("function");
            Assert.Equal(tool.Name, function.GetProperty("name").GetString());
            Assert.Equal("string", function.GetProperty("parameters").GetProperty("properties").GetProperty("path").GetProperty("type").GetString());
            Assert.Equal("auto", body.GetProperty("tool_choice").GetString());
            Assert.True(body.GetProperty("parallel_tool_calls").GetBoolean());
            var output = Assert.IsType<ToolRequestModelOutput>(Assert.Single(chunks, chunk => chunk.Output is not null).Output);
            Assert.Equal(tool.Name, output.ToolName);
            Assert.Equal("{\"path\":\"README.md\"}", output.ArgumentsJson);
        }
        else
        {
            Assert.False(body.TryGetProperty("tools", out _));
            Assert.False(body.TryGetProperty("tool_choice", out _));
            Assert.False(body.TryGetProperty("parallel_tool_calls", out _));
            Assert.Equal("{}", string.Concat(chunks.Select(chunk => chunk.Text)));
        }
    }

    /// <summary>The repository-owned Plan 46 fixture set contains every normative profile exactly once.</summary>
    [Fact]
    public static void ReasoningParityFixture_Version1_IsCompleteAndUnique()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "model", "plan46-pi-reasoning-v1.json");
        using var fixture = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("plan46-pi-reasoning-v1", fixture.RootElement.GetProperty("fixtureSetId").GetString());
        string[] ids = [.. fixture.RootElement.GetProperty("profiles")
            .EnumerateArray()
            .Select(profile => profile.GetProperty("id").GetString() ?? string.Empty)];

        Assert.Equal(14, ids.Length);
        Assert.Equal(14, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("remote-deepseek-v4", ids);
        Assert.Contains("cloud-minimax-m3", ids);
    }

    /// <summary>Reasoning-only models can map every selectable level without advertising an off switch.</summary>
    [Theory]
    [InlineData("low", "low")]
    [InlineData("medium", "high")]
    [InlineData("high", "max")]
    public static async Task ReasoningCompatibility_MappedEffort_WithoutReasoningOffUsesConfiguredEffort(
        string levelName,
        string expectedEffort)
    {
        string? requestJson = null;
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            requestJson = await ReadRequestBodyAsync(request, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n", Encoding.UTF8, "text/event-stream"),
            };
        });
        var model = CreateModel(FirstModelId, "glm-5.3:cloud") with
        {
            DefaultReasoningLevel = ReasoningLevel.High,
            SupportedReasoningLevels = [ReasoningLevel.Low, ReasoningLevel.Medium, ReasoningLevel.High],
            ReasoningCompatibility = new OpenAiReasoningCompatibilityConfiguration
            {
                Mode = OpenAiReasoningControlMode.MappedEffort,
                ResponseMode = OpenAiReasoningResponseMode.ReasoningContent,
                LevelMap = new Dictionary<ReasoningLevel, string>
                {
                    [ReasoningLevel.Low] = "low",
                    [ReasoningLevel.Medium] = "high",
                    [ReasoningLevel.High] = "max",
                },
            },
        };
        var catalog = CreateEffectiveCatalog(model);
        var profile = catalog.ModelCatalog.Get(FirstModelId);
        Assert.Equal(ReasoningLevel.High, profile.DefaultReasoningLevel);
        Assert.False(profile.SupportsReasoningLevel(ReasoningLevel.None));
        Assert.False(profile.ReasoningCapability.SupportsReasoningOff);

        var provider = new ConfiguredModelProvider(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            catalog,
            (_, _) => Task.FromResult<string?>(null),
            timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        await foreach (var chunk in provider.StreamAsync(new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "hello",
            ReasoningLevel = new ReasoningLevel(levelName),
        }))
        {
            Assert.Null(chunk.Output);
        }

        Assert.NotNull(requestJson);
        Assert.Contains($"\"reasoning_effort\":\"{expectedEffort}\"", requestJson, StringComparison.Ordinal);
    }

    /// <summary>Both mapped template shapes reject missing entries before a provider can be activated.</summary>
    [Theory]
    [InlineData(OpenAiChatTemplateKind.ThinkingWithEffort, "none")]
    [InlineData(OpenAiChatTemplateKind.ThinkingWithEffort, "xhigh")]
    [InlineData(OpenAiChatTemplateKind.EnableThinkingWithPreservationAndEffort, "none")]
    [InlineData(OpenAiChatTemplateKind.EnableThinkingWithPreservationAndEffort, "xhigh")]
    public static void ReasoningCompatibility_MappedTemplate_MissingLevelFailsCatalogValidation(
        OpenAiChatTemplateKind kind,
        string missingLevel)
    {
        ReasoningLevel[] levels = [ReasoningLevel.None, new("xhigh")];
        var model = CreateModel(FirstModelId, "template-model") with
        {
            SupportedReasoningLevels = levels,
            ReasoningCompatibility = new OpenAiReasoningCompatibilityConfiguration
            {
                Mode = OpenAiReasoningControlMode.ChatTemplate,
                ChatTemplateKind = kind,
                LevelMap = levels.Where(level => level.Value != missingLevel).ToDictionary(level => level, level => level.Value),
            },
        };

        var exception = Assert.Throws<InvalidOperationException>(() => CreateEffectiveCatalog(model));

        Assert.Contains("completely map its required levels", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Structured requests coalesce adjacent user projections after the stable system message.</summary>
    [Fact]
    public static async Task StructuredMessages_AdjacentUserProjections_PreserveAlternatingRoles()
    {
        string? requestJson = null;
        var handler = new RecordingHandler(async (request, cancellationToken) =>
        {
            requestJson = await ReadRequestBodyAsync(request, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n", Encoding.UTF8, "text/event-stream"),
            };
        });
        var catalog = CreateEffectiveCatalog(CreateModel(FirstModelId, "qwen"));
        var provider = new ConfiguredModelProvider(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            catalog,
            (_, _) => Task.FromResult<string?>(null),
            timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));

        await foreach (var chunk in provider.StreamAsync(new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "current",
            Messages =
            [
                CreateMessage(ModelMessageRole.System, "host", "host policy"),
                CreateMessage(ModelMessageRole.Developer, "repository", "repository policy"),
                CreateMessage(ModelMessageRole.Developer, "instructions", "repository instructions"),
                CreateMessage(ModelMessageRole.User, "prior-user", "prior question"),
                CreateMessage(ModelMessageRole.Assistant, "prior-assistant", "prior answer"),
                CreateMessage(ModelMessageRole.Developer, "state", "governed state"),
                CreateMessage(ModelMessageRole.User, "current-user", "current"),
            ],
        }))
        {
            Assert.NotNull(chunk);
        }

        Assert.NotNull(requestJson);
        using var body = JsonDocument.Parse(requestJson);
        var messages = body.RootElement.GetProperty("messages").EnumerateArray();
        JsonElement[] projected = [.. messages];
        Assert.Equal(
            ["system", "user", "assistant", "user"],
            projected.Select(message => message.GetProperty("role").GetString()));
        Assert.Equal("host policy", projected[0].GetProperty("content").GetString());
        Assert.Equal(
            "<threadsmith_host_context>\nrepository policy\n</threadsmith_host_context>\n\n"
                + "<threadsmith_host_context>\nrepository instructions\n</threadsmith_host_context>\n\n"
                + "prior question",
            projected[1].GetProperty("content").GetString());
        Assert.Equal(
            "<threadsmith_host_context>\ngoverned state\n</threadsmith_host_context>\n\ncurrent",
            projected[3].GetProperty("content").GetString());
        Assert.DoesNotContain(projected.Skip(1), message => message.GetProperty("role").GetString() == "system");
        Assert.DoesNotContain(projected, message => message.GetProperty("role").GetString() == "developer");
    }

    /// <summary>Explicit modes reject unsupported host levels before any network request.</summary>
    [Fact]
    public static async Task ReasoningCompatibility_UnsupportedLevel_FailsBeforeNetworkIo()
    {
        var requests = 0;
        var handler = new RecordingHandler(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var model = CreateModel(FirstModelId, "strict") with
        {
            SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.High],
            ReasoningCompatibility = new OpenAiReasoningCompatibilityConfiguration
            {
                Mode = OpenAiReasoningControlMode.StandardEffort,
            },
        };
        var catalog = CreateEffectiveCatalog(model);
        var provider = new ConfiguredModelProvider(
            new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan },
            catalog,
            (_, _) => Task.FromResult<string?>(null),
            timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));

        await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var chunk in provider.StreamAsync(new ModelStreamRequest
            {
                RunId = RunId.New(),
                Input = "hello",
                ReasoningLevel = ReasoningLevel.Low,
            }))
            {
                Assert.NotNull(chunk);
            }
        });
        Assert.Equal(0, requests);
    }

    /// <summary>A fixed-off request shape cannot be projected as an always-on reasoning capability.</summary>
    [Fact]
    public static void ReasoningCompatibility_DisabledFixedShape_FailsCatalogValidation()
    {
        var model = CreateModel(FirstModelId, "fixed-off") with
        {
            ReasoningCompatibility = new OpenAiReasoningCompatibilityConfiguration
            {
                Mode = OpenAiReasoningControlMode.Fixed,
                FixedRequestKind = OpenAiFixedRequestKind.DisableThinkingWithPreservation,
            },
        };

        var exception = Assert.Throws<InvalidOperationException>(() => CreateEffectiveCatalog(model));

        Assert.Contains("settings that do not belong", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Unknown numeric values for nested compatibility selectors fail during catalog validation.</summary>
    [Fact]
    public static void ReasoningCompatibility_UnknownNestedSelectors_FailCatalogValidation()
    {
        // Arrange
        var chatTemplateModel = CreateModel(FirstModelId, "chat-template") with
        {
            SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.High],
            ReasoningCompatibility = new OpenAiReasoningCompatibilityConfiguration
            {
                Mode = OpenAiReasoningControlMode.ChatTemplate,
                ChatTemplateKind = (OpenAiChatTemplateKind)999,
            },
        };
        var fixedModel = CreateModel(FirstModelId, "fixed") with
        {
            ReasoningCompatibility = new OpenAiReasoningCompatibilityConfiguration
            {
                Mode = OpenAiReasoningControlMode.Fixed,
                FixedRequestKind = (OpenAiFixedRequestKind)999,
            },
        };

        // Act and assert
        Assert.Throws<InvalidOperationException>(() => CreateEffectiveCatalog(chatTemplateModel));
        Assert.Throws<InvalidOperationException>(() => CreateEffectiveCatalog(fixedModel));
    }

    /// <summary>Dedicated and legacy schemas cannot silently combine.</summary>
    [Fact]
    public static void ConfigurationPrecedence_NewAndLegacyTogether_IsRejected()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            OpenAiCompatibleProviderRegistration.EnsureConfigurationIsUnambiguous(true, true));

        Assert.Contains("cannot be used together", exception.Message, StringComparison.Ordinal);
        OpenAiCompatibleProviderRegistration.EnsureConfigurationIsUnambiguous(true, false);
        OpenAiCompatibleProviderRegistration.EnsureConfigurationIsUnambiguous(false, true);
    }

    private static EffectiveModelProviderCatalog CreateEffectiveCatalog(OpenAiCompatibleModelConfiguration model)
    {
        var configured = CreateProvider(
            "reasoning",
            new Uri("https://models.example/v1/"),
            [model]);
        return new EffectiveModelProviderCatalog(
            new ModelProviderCatalogConfiguration
            {
                DefaultProviderId = configured.Id,
                DefaultModelId = model.Id,
                Providers = [configured],
            },
            new ModelProviderRegistry([new OpenAiCompatibleProviderRegistration()]));
    }

    private static OpenAiCompatibleProviderConfiguration CreateProvider(
        string id,
        Uri baseUri,
        IReadOnlyList<ModelConfiguration> models)
    {
        return new OpenAiCompatibleProviderConfiguration
        {
            Id = id,
            Name = id,
            BaseUri = baseUri,
            Models = models,
        };
    }

    private static OpenAiCompatibleModelConfiguration CreateModel(ModelProfileId id, string modelId)
    {
        return new OpenAiCompatibleModelConfiguration
        {
            Id = id,
            Name = modelId,
            TimeoutSeconds = 0,
            ModelId = modelId,
            ContextWindow = 32000,
            MaximumOutputTokens = 4000,
            Capabilities = new ModelCapabilitySet { Streaming = true },
            SupportedReasoningLevels = [ReasoningLevel.None],
        };
    }

    private static ModelMessage CreateMessage(ModelMessageRole role, string sectionId, string content)
    {
        return new ModelMessage
        {
            Role = role,
            SectionId = sectionId,
            Content = [new ModelContentPart { Content = content }],
        };
    }

    private static ModelProfile CreateLegacyProfile()
    {
        return new ModelProfile
        {
            Id = FirstModelId,
            Name = "legacy",
            Provider = "openai-compatible",
            Endpoint = new Uri("https://legacy.example/custom/chat/completions?api-version=one"),
            ModelId = "legacy-model",
            SecretKeyReference = "secrets:models:legacy",
            ContextWindow = 8192,
            MaximumOutputTokens = 1024,
            Capabilities = new ModelCapabilitySet { Streaming = true, StructuredOutput = true },
            Cost = new ModelCostMetadata { InputPerMillionTokens = 1, OutputPerMillionTokens = 2 },
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            IntendedWorkloadClasses = [WorkloadClass.Planning],
            DefaultReasoningLevel = ReasoningLevel.Low,
            ReasoningEffort = "low",
            SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.Low],
            Temperature = 0.2m,
            Timeout = TimeSpan.FromSeconds(45),
            RetryPolicy = new ModelRetryPolicy
            {
                MaxAttempts = 2,
                Delay = TimeSpan.FromMilliseconds(75),
            },
        };
    }

    private static async Task<string?> ReadRequestBodyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> send)
            : this((request, _) => Task.FromResult(send(request)))
        {
        }

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _send(request, cancellationToken);
        }
    }
}
