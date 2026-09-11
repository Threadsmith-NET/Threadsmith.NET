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

    private static readonly IReadOnlyList<ReasoningLevel> ConventionalReasoningLevels =
        [ReasoningLevel.None, ReasoningLevel.Minimal, ReasoningLevel.Low, ReasoningLevel.Medium, ReasoningLevel.High];

    /// <summary>Custom names survive catalog serialization and reach effort-based providers verbatim.</summary>
    [Theory]
    [InlineData("xhigh")]
    [InlineData("provider-Custom")]
    public static async Task ReasoningCompatibility_CustomEffort_PreservesProviderValue(string reasoning)
    {
        string? requestJson = null;
        using var client = new HttpClient(new RecordingHandler(request =>
        {
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n", Encoding.UTF8, "text/event-stream"),
            };
        }));
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
        var provider = new ConfiguredModelProvider(client, CreateEffectiveCatalog(reloaded), (_, _) => Task.FromResult<string?>(null));

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

    /// <summary>Reasoning maps may contain more than the former five host enum values.</summary>
    [Fact]
    public static void ReasoningCompatibility_CustomMap_AcceptsAdditionalModelLevels()
    {
        var extra = new ReasoningLevel("xhigh");
        ReasoningLevel[] levels = [.. ConventionalReasoningLevels, extra];
        var model = CreateModel(FirstModelId, "mapped-custom") with
        {
            DefaultReasoningLevel = extra,
            SupportedReasoningLevels = levels,
            ReasoningCompatibility = new OpenAiReasoningCompatibilityConfiguration
            {
                Mode = OpenAiReasoningControlMode.MappedEffort,
                LevelMap = levels.ToDictionary(level => level, level => level.Value),
            },
        };
        var options = new ModelProviderRegistry([new OpenAiCompatibleProviderRegistration()]).CreateSerializerOptions();
        var reloaded = Assert.IsType<OpenAiCompatibleModelConfiguration>(
            JsonSerializer.Deserialize<ModelConfiguration>(JsonSerializer.Serialize<ModelConfiguration>(model, options), options));

        var catalog = CreateEffectiveCatalog(reloaded);

        Assert.Equal(extra, catalog.ModelCatalog.Get(FirstModelId).DefaultReasoningLevel);
        Assert.Equal(6, catalog.ModelCatalog.Get(FirstModelId).SupportedReasoningLevels.Count);
    }

    /// <summary>Multiple compiled providers and nested models retain distinct host policy metadata.</summary>
    [Fact]
    public static void EffectiveCatalog_MultipleProvidersAndModels_ProjectsDistinctProfiles()
    {
        var registration = new OpenAiCompatibleProviderRegistration();
        var firstProvider = CreateProvider(
            "first",
            new Uri("https://first.example/v1/"),
            [
                CreateModel(FirstModelId, "first-model"),
                CreateModel(SecondModelId, "second-model") with
                {
                ContextWindow = 64000,
                MaximumOutputTokens = 8000,
                Temperature = 0.7m,
                Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true },
                Cost = new ModelCostMetadata { InputPerMillionTokens = 2, OutputPerMillionTokens = 4 },
                DefaultReasoningLevel = ReasoningLevel.High,
                    SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.High],
                },
            ]);
        var secondProvider = CreateProvider(
            "second",
            new Uri("https://second.example/api/"),
            [CreateModel(new ModelProfileId(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc")), "third-model")]);

        var catalog = new EffectiveModelProviderCatalog(
            new ModelProviderCatalogConfiguration { Providers = [firstProvider, secondProvider] },
            new ModelProviderRegistry([registration]));

        Assert.Equal(3, catalog.ModelCatalog.Profiles.Count);
        var second = catalog.ModelCatalog.Get(SecondModelId);
        Assert.Equal(firstProvider.Name, second.ProviderName);
        Assert.Equal(secondProvider.Name, catalog.ModelCatalog.Get(secondProvider.Models[0].Id).ProviderName);
        Assert.Equal(new Uri("https://first.example/v1/chat/completions"), second.Endpoint);
        Assert.Equal(64000, second.ContextWindow);
        Assert.Equal(0.7m, second.Temperature);
        Assert.Equal(ReasoningLevel.High, second.DefaultReasoningLevel);
        Assert.True(second.Capabilities.ToolCalls);
    }

    /// <summary>A hard output maximum equal to context remains usable with a smaller explicit request reserve.</summary>
    [Fact]
    public static void EffectiveCatalog_OutputMaximumEqualsContext_UsesExplicitRequestReserve()
    {
        var model = CreateModel(FirstModelId, "spark") with
        {
            ContextWindow = 128_000,
            MaximumOutputTokens = 128_000,
            RequestOutputTokenReserve = 32_768,
        };

        var catalog = CreateEffectiveCatalog(model);

        var profile = catalog.ModelCatalog.Get(FirstModelId);
        Assert.Equal(128_000, profile.MaximumOutputTokens);
        Assert.Equal(32_768, profile.EffectiveRequestOutputTokenReserve);
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
        using var client = new HttpClient(handler);
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
            });

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
            new HttpClient(handler),
            catalog,
            (_, _) => Task.FromResult<string?>("legacy-secret"),
            legacy.Id);
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
        }));
        var model = CreateModel(FirstModelId, "byte-limit") with { MaximumStreamedBytes = maximumBytes };
        var profile = CreateEffectiveCatalog(model).ModelCatalog.Get(FirstModelId);
        var provider = new OpenAiCompatibleModelProvider(http, profile);
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
        }));
        var model = CreateModel(FirstModelId, "tool-limit") with
        {
            MaximumToolCalls = maximum,
            Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true },
        };
        var provider = new OpenAiCompatibleModelProvider(http, CreateEffectiveCatalog(model).ModelCatalog.Get(FirstModelId));
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
    [Fact]
    public static void HttpTransportOptions_FiniteConnectionTimeoutHasNoProductMaximum()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["model:http:connectTimeoutSeconds"] = "600",
        }).Build();
        var options = ModelHttpTransportOptions.Load(configuration);
        using var handler = new SocketsHttpHandler { ConnectTimeout = options.ConnectTimeout };
        Assert.Equal(TimeSpan.FromMinutes(10), handler.ConnectTimeout);
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
    [InlineData("model:http:pooledConnectionLifetimeSeconds", "59")]
    [InlineData("model:http:pooledConnectionIdleTimeoutSeconds", "3601")]
    [InlineData("model:http:connectTimeoutSeconds", "-1")]
    [InlineData("model:http:maxConnectionsPerServer", "1025")]
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
        using var handler = new RecordingHandler(request =>
        {
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            var stream = nativeTools
                ? "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call-1\",\"function\":{\"name\":\"inspect_file\",\"arguments\":\"{\\\"path\\\":\\\"README.md\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\ndata: [DONE]\n"
                : "data: {\"choices\":[{\"delta\":{\"content\":\"{}\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
            };
        });
        using var http = new HttpClient(handler);
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
            (_, _) => Task.FromResult<string?>(null));
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

    /// <summary>Every normative Plan 46 profile projects its exact compatibility fragment and response field.</summary>
    [Fact]
    public static async Task ReasoningParityFixture_AllProfilesAndLevels_ProjectExactly()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "model",
            "plan46-pi-reasoning-v1.json");
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath));
        var fixtureProfiles = fixture.RootElement.GetProperty("profiles")
            .EnumerateArray()
            .ToDictionary(
                profile => profile.GetProperty("id").GetString() ?? string.Empty,
                profile => profile.Clone(),
                StringComparer.Ordinal);
        var parityCases = CreateReasoningParityCases();
        Assert.Equal(fixtureProfiles.Count, parityCases.Count);

        foreach (var parityCase in parityCases)
        {
            var fixtureProfile = fixtureProfiles[parityCase.FixtureId];
            Assert.Equal(parityCase.Model.ModelId, fixtureProfile.GetProperty("modelId").GetString());
            Assert.Equal(ToFixtureMode(parityCase.Mode), fixtureProfile.GetProperty("mode").GetString());
            Assert.Equal(
                parityCase.Levels.Select(level => level.ToString().ToLowerInvariant()),
                fixtureProfile.GetProperty("levels").EnumerateArray().Select(level => level.GetString()));
            Assert.Equal(ToFixtureResponse(parityCase.ResponseMode), fixtureProfile.GetProperty("response").GetString());
            foreach (var level in parityCase.Levels)
            {
                string? requestJson = null;
                var requests = 0;
                var handler = new RecordingHandler(request =>
                {
                    requests++;
                    requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
                    var reasoning = parityCase.ResponseMode switch
                    {
                        OpenAiReasoningResponseMode.ReasoningContent
                            => "\"reasoning_content\":\"hi\",\"reasoning\":\"wrong\",\"reasoning_text\":\"wrong\"",
                        OpenAiReasoningResponseMode.Reasoning
                            => "\"reasoning_content\":\"wrong\",\"reasoning\":\"hi\",\"reasoning_text\":\"wrong\"",
                        OpenAiReasoningResponseMode.ReasoningText
                            => "\"reasoning_content\":\"wrong\",\"reasoning\":\"wrong\",\"reasoning_text\":\"hi\"",
                        OpenAiReasoningResponseMode.KnownFields
                            => "\"reasoning_text\":\"hi\"",
                        _ => "\"reasoning_content\":\"wrong\",\"reasoning\":\"wrong\",\"reasoning_text\":\"wrong\"",
                    };
                    var reasoningEnd = parityCase.ResponseMode switch
                    {
                        OpenAiReasoningResponseMode.ReasoningContent => "\"reasoning_content\":\"dden\"",
                        OpenAiReasoningResponseMode.Reasoning => "\"reasoning\":\"dden\"",
                        OpenAiReasoningResponseMode.ReasoningText => "\"reasoning_text\":\"dden\"",
                        OpenAiReasoningResponseMode.KnownFields => "\"reasoning\":\"dden\"",
                        _ => "\"reasoning_content\":\"wrong\"",
                    };
                    var stream = $"data: {{\"choices\":[{{\"delta\":{{{reasoning},\"content\":\"vis\"}}}}]}}\n\n"
                        + $"data: {{\"choices\":[{{\"delta\":{{{reasoningEnd},\"content\":\"ible\",\"tool_calls\":[{{\"index\":0,\"id\":\"call-1\",\"function\":{{\"name\":\"inspect_\",\"arguments\":\"{{\\\"path\\\":\\\"README.\"}}}}]}}}}]}}\n\n"
                        + "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"file\",\"arguments\":\"md\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n"
                        + "data: {\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2}}\n\n"
                        + "data: [DONE]\n";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
                    };
                });
                var catalog = CreateEffectiveCatalog(parityCase.Model);
                var provider = new ConfiguredModelProvider(
                    new HttpClient(handler),
                    catalog,
                    (_, _) => Task.FromResult<string?>(null));
                var chunks = new List<ModelChunk>();

                await foreach (var chunk in provider.StreamAsync(new ModelStreamRequest
                {
                    RunId = RunId.New(),
                    Input = "fixture",
                    ReasoningLevel = level,
                    RequiredCapabilities = new ModelCapabilitySet
                    {
                        Streaming = true,
                        ToolCalls = true,
                        StructuredOutput = true,
                    },
                    Tools =
                    [
                        new ModelToolDefinition
                        {
                            Name = "inspect_file",
                            Description = "Inspect one repository file.",
                            ArgumentsJsonSchema = "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"]}",
                        },
                    ],
                }))
                {
                    chunks.Add(chunk);
                }

                Assert.Equal(1, requests);
                Assert.NotNull(requestJson);
                using var request = JsonDocument.Parse(requestJson);
                AssertReasoningFragment(parityCase, level, request.RootElement);
                AssertParityRequestBody(parityCase, request.RootElement);
                Assert.Equal(
                    "visible",
                    string.Concat(chunks.Where(chunk => chunk.Text is not null).Select(chunk => chunk.Text)));
                string[] reasoning = [.. chunks.Where(chunk => chunk.Reasoning is not null).Select(chunk => chunk.Reasoning!)];
                if (parityCase.ResponseMode == OpenAiReasoningResponseMode.None)
                {
                    Assert.Empty(reasoning);
                }
                else
                {
                    Assert.Equal("hidden", string.Concat(reasoning));
                }

                var usage = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage!;
                Assert.Equal(3, usage.InputTokens);
                Assert.Equal(2, usage.OutputTokens);
                var toolRequest = Assert.IsType<ToolRequestModelOutput>(
                    Assert.Single(chunks, chunk => chunk.Output is not null).Output);
                Assert.Equal("inspect_file", toolRequest.ToolName);
                Assert.Equal("{\"path\":\"README.md\"}", toolRequest.ArgumentsJson);
                Assert.Contains(chunks, chunk => chunk.FinishReason == ModelFinishReason.ToolCalls);
            }

            var unsupported = ConventionalReasoningLevels
                .FirstOrDefault(level => !parityCase.Levels.Contains(level));
            if (!parityCase.Levels.Contains(unsupported))
            {
                var requests = 0;
                var handler = new RecordingHandler(_ =>
                {
                    requests++;
                    return new HttpResponseMessage(HttpStatusCode.OK);
                });
                var catalog = CreateEffectiveCatalog(parityCase.Model);
                var provider = new ConfiguredModelProvider(
                    new HttpClient(handler),
                    catalog,
                    (_, _) => Task.FromResult<string?>(null));

                await Assert.ThrowsAsync<ModelProviderException>(async () =>
                {
                    await foreach (var chunk in provider.StreamAsync(new ModelStreamRequest
                    {
                        RunId = RunId.New(),
                        Input = "fixture",
                        ReasoningLevel = unsupported,
                    }))
                    {
                        Assert.NotNull(chunk);
                    }
                });
                Assert.Equal(0, requests);
            }
        }
    }

    /// <summary>Explicit mapped effort projects the exact provider value and configured response field.</summary>
    [Fact]
    public static async Task ReasoningCompatibility_MappedEffort_ProjectsAndNormalizesExactly()
    {
        string? requestJson = null;
        var handler = new RecordingHandler(request =>
        {
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"think\",\"content\":\"answer\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        });
        var model = CreateModel(FirstModelId, "mapped") with
        {
            DefaultReasoningLevel = ReasoningLevel.High,
            SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.High],
            ReasoningCompatibility = new OpenAiReasoningCompatibilityConfiguration
            {
                Mode = OpenAiReasoningControlMode.MappedEffort,
                ResponseMode = OpenAiReasoningResponseMode.ReasoningContent,
                LevelMap = new Dictionary<ReasoningLevel, string>
                {
                    [ReasoningLevel.None] = "off",
                    [ReasoningLevel.High] = "maximum",
                },
            },
        };
        var catalog = CreateEffectiveCatalog(model);
        var provider = new ConfiguredModelProvider(
            new HttpClient(handler),
            catalog,
            (_, _) => Task.FromResult<string?>(null));
        var chunks = new List<ModelChunk>();

        await foreach (var chunk in provider.StreamAsync(new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "hello",
            ReasoningLevel = ReasoningLevel.High,
        }))
        {
            chunks.Add(chunk);
        }

        Assert.NotNull(requestJson);
        Assert.Contains("\"reasoning_effort\":\"maximum\"", requestJson, StringComparison.Ordinal);
        Assert.Contains(chunks, chunk => chunk.Reasoning == "think");
        Assert.Contains(chunks, chunk => chunk.Text == "answer");
        Assert.Equal(ReasoningControllability.Selectable, catalog.ModelCatalog.Get(FirstModelId).ReasoningCapability.Controllability);
    }

    /// <summary>The existing boolean-only template shape stays unchanged for every model name and level.</summary>
    [Theory]
    [InlineData("qwen", "none", false)]
    [InlineData("qwen", "low", true)]
    [InlineData("qwen", "medium", true)]
    [InlineData("qwen", "xhigh", true)]
    [InlineData("other-vendor/model", "none", false)]
    [InlineData("other-vendor/model", "low", true)]
    [InlineData("other-vendor/model", "medium", true)]
    [InlineData("other-vendor/model", "xhigh", true)]
    public static async Task ReasoningCompatibility_QwenChatTemplate_EmitsBoundedNestedShape(
        string modelId,
        string reasoning,
        bool enabled)
    {
        string? requestJson = null;
        var handler = new RecordingHandler(request =>
        {
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            const string stream = "data: {\"choices\":[{\"delta\":{\"reasoning\":\"thinking\"}}]}\n\ndata: [DONE]\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream, Encoding.UTF8, "text/event-stream"),
            };
        });
        var model = CreateModel(FirstModelId, modelId) with
        {
            SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.Low, ReasoningLevel.Medium, new ReasoningLevel("xhigh")],
            ReasoningCompatibility = new OpenAiReasoningCompatibilityConfiguration
            {
                Mode = OpenAiReasoningControlMode.ChatTemplate,
                ChatTemplateKind = OpenAiChatTemplateKind.EnableThinkingWithPreservation,
                ResponseMode = OpenAiReasoningResponseMode.Reasoning,
            },
        };
        var catalog = CreateEffectiveCatalog(model);
        var provider = new ConfiguredModelProvider(
            new HttpClient(handler),
            catalog,
            (_, _) => Task.FromResult<string?>(null));

        var chunks = new List<ModelChunk>();
        await foreach (var chunk in provider.StreamAsync(new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "hello",
            ReasoningLevel = new ReasoningLevel(reasoning),
        }))
        {
            chunks.Add(chunk);
        }

        Assert.Contains(chunks, chunk => chunk.Reasoning == "thinking");
        Assert.NotNull(requestJson);
        using var request = JsonDocument.Parse(requestJson);
        Assert.Equal(enabled, request.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.True(request.RootElement.GetProperty("chat_template_kwargs").GetProperty("preserve_thinking").GetBoolean());
        Assert.DoesNotContain("reasoning_effort", requestJson, StringComparison.Ordinal);
        Assert.Equal(2, request.RootElement.GetProperty("chat_template_kwargs").EnumerateObject().Count());
        Assert.Equal(modelId, request.RootElement.GetProperty("model").GetString());
        Assert.Contains("\"max_completion_tokens\":4000", requestJson, StringComparison.Ordinal);
    }

    /// <summary>The opt-in shape survives configuration binding and carries mapped effort with both thinking flags.</summary>
    [Theory]
    [InlineData("none", "none", false)]
    [InlineData("low", "low", true)]
    [InlineData("medium", "medium", true)]
    [InlineData("xhigh", "xhigh", true)]
    [InlineData("custom", "Provider-Custom", true)]
    public static async Task ReasoningCompatibility_PreservedThinkingEffort_RoundTripsAndEmitsExactShape(
        string reasoning,
        string expectedEffort,
        bool enabled)
    {
        // Arrange: the explicit shape, rather than the model name, selects this protocol.
        string? requestJson = null;
        using var client = new HttpClient(new RecordingHandler(request =>
        {
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n", Encoding.UTF8, "text/event-stream"),
            };
        }));
        ReasoningLevel[] levels = [ReasoningLevel.None, ReasoningLevel.Low, ReasoningLevel.Medium, new("xhigh"), new("custom")];
        var map = levels.ToDictionary(level => level, level => level.Value);
        map[new ReasoningLevel("custom")] = "Provider-Custom";
        var model = CreateModel(FirstModelId, "template-model") with
        {
            SupportedReasoningLevels = levels,
            ReasoningCompatibility = new OpenAiReasoningCompatibilityConfiguration
            {
                Mode = OpenAiReasoningControlMode.ChatTemplate,
                ChatTemplateKind = OpenAiChatTemplateKind.EnableThinkingWithPreservationAndEffort,
                LevelMap = map,
            },
        };
        var options = new ModelProviderRegistry([new OpenAiCompatibleProviderRegistration()]).CreateSerializerOptions();
        var configurationJson = JsonSerializer.Serialize<ModelConfiguration>(model, options);
        Assert.Contains("enableThinkingWithPreservationAndEffort", configurationJson, StringComparison.Ordinal);
        var reloaded = Assert.IsType<OpenAiCompatibleModelConfiguration>(
            JsonSerializer.Deserialize<ModelConfiguration>(configurationJson, options));
        var provider = new ConfiguredModelProvider(client, CreateEffectiveCatalog(reloaded), (_, _) => Task.FromResult<string?>(null));

        // Act.
        await foreach (var chunk in provider.StreamAsync(new ModelStreamRequest
        {
            RunId = RunId.New(),
            Input = "hello",
            ReasoningLevel = new ReasoningLevel(reasoning),
        }))
        {
            Assert.Null(chunk.Output);
        }

        // Assert the entire nested shape; no competing top-level or generic thinking controls may leak.
        Assert.NotNull(requestJson);
        using var request = JsonDocument.Parse(requestJson);
        var body = request.RootElement;
        var template = body.GetProperty("chat_template_kwargs");
        Assert.Equal(3, template.EnumerateObject().Count());
        Assert.Equal(enabled, template.GetProperty("enable_thinking").GetBoolean());
        Assert.True(template.GetProperty("preserve_thinking").GetBoolean());
        Assert.Equal(expectedEffort, template.GetProperty("reasoning_effort").GetString());
        Assert.False(body.TryGetProperty("reasoning_effort", out _));
        Assert.False(body.TryGetProperty("thinking", out _));
        Assert.Equal("template-model", body.GetProperty("model").GetString());
        Assert.Equal(4000, body.GetProperty("max_completion_tokens").GetInt32());
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
        var handler = new RecordingHandler(request =>
        {
            requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n", Encoding.UTF8, "text/event-stream"),
            };
        });
        var catalog = CreateEffectiveCatalog(CreateModel(FirstModelId, "qwen"));
        var provider = new ConfiguredModelProvider(
            new HttpClient(handler),
            catalog,
            (_, _) => Task.FromResult<string?>(null));

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
            new HttpClient(handler),
            catalog,
            (_, _) => Task.FromResult<string?>(null));

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

    private static string ToFixtureMode(OpenAiReasoningControlMode mode)
    {
        var value = mode.ToString();
        return char.ToLowerInvariant(value[0]) + value[1..];
    }

    private static string ToFixtureResponse(OpenAiReasoningResponseMode mode)
    {
        return mode switch
        {
            OpenAiReasoningResponseMode.None => "none",
            OpenAiReasoningResponseMode.ReasoningContent => "reasoningContent",
            OpenAiReasoningResponseMode.Reasoning => "reasoning",
            OpenAiReasoningResponseMode.ReasoningText => "reasoningText",
            OpenAiReasoningResponseMode.KnownFields => "knownFields",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private static IReadOnlyList<ReasoningParityCase> CreateReasoningParityCases()
    {
        return
        [
            CreateParityCase(
                "remote-deepseek-v4",
                "deepseek-v4",
                [ReasoningLevel.None, ReasoningLevel.Medium, ReasoningLevel.High],
                OpenAiReasoningControlMode.ChatTemplate,
                OpenAiReasoningResponseMode.ReasoningContent,
                new Dictionary<ReasoningLevel, string> { [ReasoningLevel.None] = "false", [ReasoningLevel.Medium] = "high", [ReasoningLevel.High] = "high" },
                OpenAiChatTemplateKind.ThinkingWithEffort),
            CreateParityCase(
                "remote-gemma-4",
                "Gemma-4-31B-IT-FP8-Block-MTP",
                [ReasoningLevel.None],
                OpenAiReasoningControlMode.AlwaysOn,
                OpenAiReasoningResponseMode.ReasoningContent),
            CreateParityCase(
                "remote-nemotron-3-super",
                "nvidia/NVIDIA-Nemotron-3-Super-120B-A12B-NVFP4",
                [ReasoningLevel.None],
                OpenAiReasoningControlMode.Fixed,
                OpenAiReasoningResponseMode.ReasoningContent,
                fixedKind: OpenAiFixedRequestKind.ThinkingEnvironmentBudget4096),
            CreateParityCase(
                "remote-qwen-3-5",
                "Qwen/Qwen3.5-122B-A10B-FP8",
                [ReasoningLevel.None, ReasoningLevel.High],
                OpenAiReasoningControlMode.ChatTemplate,
                OpenAiReasoningResponseMode.ReasoningContent,
                chatTemplateKind: OpenAiChatTemplateKind.EnableThinkingWithPreservation),
            CreateParityCase(
                "remote-qwen-3-6-nothink",
                "Qwen/Qwen3.6-27B-FP8-NOTHINK",
                [ReasoningLevel.None],
                OpenAiReasoningControlMode.Unsupported,
                OpenAiReasoningResponseMode.None,
                fixedKind: OpenAiFixedRequestKind.DisableThinkingWithPreservation),
            CreateParityCase(
                "remote-qwen-3-6",
                "Qwen/Qwen3.6-27B-FP8",
                [ReasoningLevel.None, ReasoningLevel.Low, ReasoningLevel.Medium, ReasoningLevel.High],
                OpenAiReasoningControlMode.ChatTemplate,
                OpenAiReasoningResponseMode.Reasoning,
                chatTemplateKind: OpenAiChatTemplateKind.EnableThinkingWithPreservation),
            CreateParityCase(
                "remote-minimax-m2-7",
                "MiniMax-M2.7-NVFP4",
                [ReasoningLevel.None],
                OpenAiReasoningControlMode.AlwaysOn,
                OpenAiReasoningResponseMode.ReasoningContent),
            CreateParityCase(
                "local-qwen-coder-7b",
                "qwen2.5-coder:7b",
                [ReasoningLevel.None],
                OpenAiReasoningControlMode.Unsupported,
                OpenAiReasoningResponseMode.None),
            CreateMappedParityCase("cloud-glm-5-2", "glm-5.2:cloud"),
            CreateMappedParityCase("cloud-kimi-k2-5", "kimi-k2.5:cloud"),
            CreateMappedParityCase("cloud-deepseek-v4-pro", "deepseek-v4-pro:cloud"),
            CreateStandardParityCase(
                "cloud-kimi-k2-7-code",
                "kimi-k2.7-code:cloud",
                OpenAiReasoningResponseMode.KnownFields),
            CreateStandardParityCase("cloud-kimi-k2-6", "kimi-k2.6:cloud"),
            CreateStandardParityCase("cloud-minimax-m3", "minimax-m3:cloud"),
        ];
    }

    private static ReasoningParityCase CreateMappedParityCase(string fixtureId, string modelId)
    {
        return CreateParityCase(
            fixtureId,
            modelId,
            [ReasoningLevel.None, ReasoningLevel.High],
            OpenAiReasoningControlMode.MappedEffort,
            OpenAiReasoningResponseMode.ReasoningContent,
            new Dictionary<ReasoningLevel, string>
            {
                [ReasoningLevel.None] = "none",
                [ReasoningLevel.High] = "high",
            });
    }

    private static ReasoningParityCase CreateStandardParityCase(
        string fixtureId,
        string modelId,
        OpenAiReasoningResponseMode responseMode = OpenAiReasoningResponseMode.ReasoningContent)
    {
        return CreateParityCase(
            fixtureId,
            modelId,
            ConventionalReasoningLevels,
            OpenAiReasoningControlMode.StandardEffort,
            responseMode);
    }

    private static ReasoningParityCase CreateParityCase(
        string fixtureId,
        string modelId,
        IReadOnlyList<ReasoningLevel> levels,
        OpenAiReasoningControlMode mode,
        OpenAiReasoningResponseMode responseMode,
        IReadOnlyDictionary<ReasoningLevel, string>? levelMap = null,
        OpenAiChatTemplateKind? chatTemplateKind = null,
        OpenAiFixedRequestKind? fixedKind = null)
    {
        var model = CreateModel(FirstModelId, modelId) with
        {
            Temperature = 0.25m,
            Capabilities = new ModelCapabilitySet
            {
                Streaming = true,
                ToolCalls = true,
                StructuredOutput = true,
            },
            SupportedReasoningLevels = levels,
            DefaultReasoningLevel = levels[0],
            ReasoningCompatibility = new OpenAiReasoningCompatibilityConfiguration
            {
                Mode = mode,
                ResponseMode = responseMode,
                LevelMap = levelMap ?? new Dictionary<ReasoningLevel, string>(),
                ChatTemplateKind = chatTemplateKind,
                FixedRequestKind = fixedKind,
            },
        };
        return new ReasoningParityCase(fixtureId, model, levels, mode, responseMode);
    }

    private static void AssertParityRequestBody(ReasoningParityCase parityCase, JsonElement request)
    {
        Assert.Equal(parityCase.Model.ModelId, request.GetProperty("model").GetString());
        var message = Assert.Single(request.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("fixture", message.GetProperty("content").GetString());
        var tool = Assert.Single(request.GetProperty("tools").EnumerateArray());
        Assert.Equal("function", tool.GetProperty("type").GetString());
        Assert.Equal("inspect_file", tool.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(
            "Inspect one repository file.",
            tool.GetProperty("function").GetProperty("description").GetString());
        Assert.Equal(
            "object",
            tool.GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
        Assert.Equal("auto", request.GetProperty("tool_choice").GetString());
        Assert.True(request.GetProperty("stream").GetBoolean());
        Assert.True(request.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal(4000, request.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal(0.25m, request.GetProperty("temperature").GetDecimal());
        Assert.False(request.TryGetProperty("response_format", out _));
    }

    private static void AssertReasoningFragment(
        ReasoningParityCase parityCase,
        ReasoningLevel level,
        JsonElement request)
    {
        Assert.Equal(parityCase.Model.ModelId, request.GetProperty("model").GetString());
        var compatibility = parityCase.Model.ReasoningCompatibility!;
        switch (parityCase.Mode)
        {
            case OpenAiReasoningControlMode.StandardEffort:
                Assert.Equal(level != ReasoningLevel.None, request.TryGetProperty("reasoning_effort", out var standard));
                if (level != ReasoningLevel.None)
                {
                    Assert.Equal(level.ToString().ToLowerInvariant(), standard.GetString());
                }

                break;
            case OpenAiReasoningControlMode.MappedEffort:
                Assert.Equal(compatibility.LevelMap[level], request.GetProperty("reasoning_effort").GetString());
                break;
            case OpenAiReasoningControlMode.ChatTemplate:
                var template = request.GetProperty("chat_template_kwargs");
                var enabled = level != ReasoningLevel.None;
                if (compatibility.ChatTemplateKind == OpenAiChatTemplateKind.ThinkingWithEffort)
                {
                    Assert.Equal(enabled, template.GetProperty("thinking").GetBoolean());
                    Assert.Equal(compatibility.LevelMap[level], template.GetProperty("reasoning_effort").GetString());
                }
                else
                {
                    Assert.Equal(enabled, template.GetProperty("enable_thinking").GetBoolean());
                    Assert.True(template.GetProperty("preserve_thinking").GetBoolean());
                }

                break;
            case OpenAiReasoningControlMode.Fixed:
                Assert.True(request.GetProperty("LLM_ENABLE_THINKING").GetBoolean());
                Assert.Equal(4096, request.GetProperty("LLM_REASONING_BUDGET").GetInt32());
                break;
            case OpenAiReasoningControlMode.Unsupported when compatibility.FixedRequestKind is not null:
                var disabled = request.GetProperty("chat_template_kwargs");
                Assert.False(disabled.GetProperty("enable_thinking").GetBoolean());
                Assert.True(disabled.GetProperty("preserve_thinking").GetBoolean());
                break;
            case OpenAiReasoningControlMode.AlwaysOn:
            case OpenAiReasoningControlMode.Unsupported:
                Assert.False(request.TryGetProperty("reasoning_effort", out _));
                Assert.False(request.TryGetProperty("chat_template_kwargs", out _));
                break;
            default:
                throw new InvalidOperationException($"Unhandled parity mode for {parityCase.FixtureId}.");
        }
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

    private sealed record ReasoningParityCase(
        string FixtureId,
        OpenAiCompatibleModelConfiguration Model,
        IReadOnlyList<ReasoningLevel> Levels,
        OpenAiReasoningControlMode Mode,
        OpenAiReasoningResponseMode ResponseMode);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _send;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> send)
        {
            _send = send;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(_send(request));
        }
    }
}
