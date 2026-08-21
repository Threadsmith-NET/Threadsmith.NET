namespace Threadsmith.Milestone3.Tests;

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

    /// <summary>Legacy adaptation preserves stable identity and the exact full request endpoint without file mutation.</summary>
    [Fact]
    public static async Task LegacyCatalog_Profile_IsPreservedInMemory()
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
        Assert.Equal(legacy, projected);
        Assert.Equal(legacy.Endpoint, requestUri);
        var definition = catalog.Get(legacy.Id);
        Assert.Equal("legacy-" + legacy.Id.Value.ToString("N"), definition.ProviderId);
        Assert.IsType<OpenAiCompatibleProviderConfiguration>(definition.ProviderConfiguration);
        Assert.IsType<OpenAiCompatibleModelConfiguration>(definition.ModelConfiguration);
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
    [InlineData("model:http:connectTimeoutSeconds", "0")]
    [InlineData("model:http:maxConnectionsPerServer", "1025")]
    public static void HttpTransportOptions_OutOfBoundsValue_IsRejected(string key, string value)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        Assert.Throws<InvalidOperationException>(() => ModelHttpTransportOptions.Load(configuration));
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
                            => "\"reasoning_content\":\"hi\",\"reasoning\":\"wrong\"",
                        OpenAiReasoningResponseMode.Reasoning
                            => "\"reasoning_content\":\"wrong\",\"reasoning\":\"hi\"",
                        _ => "\"reasoning_content\":\"wrong\",\"reasoning\":\"wrong\"",
                    };
                    var reasoningEnd = parityCase.ResponseMode switch
                    {
                        OpenAiReasoningResponseMode.ReasoningContent => "\"reasoning_content\":\"dden\"",
                        OpenAiReasoningResponseMode.Reasoning => "\"reasoning\":\"dden\"",
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

            var unsupported = Enum.GetValues<ReasoningLevel>()
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

    /// <summary>Compiled Qwen chat-template compatibility controls thinking without overriding host fields.</summary>
    [Fact]
    public static async Task ReasoningCompatibility_QwenChatTemplate_EmitsBoundedNestedShape()
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
        var model = CreateModel(FirstModelId, "qwen") with
        {
            SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.High],
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
            ReasoningLevel = ReasoningLevel.None,
        }))
        {
            chunks.Add(chunk);
        }

        Assert.Contains(chunks, chunk => chunk.Reasoning == "thinking");
        Assert.NotNull(requestJson);
        Assert.Contains("\"chat_template_kwargs\":{\"enable_thinking\":false,\"preserve_thinking\":true}", requestJson, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning_effort", requestJson, StringComparison.Ordinal);
        Assert.Contains("\"model\":\"qwen\"", requestJson, StringComparison.Ordinal);
        Assert.Contains("\"max_completion_tokens\":4000", requestJson, StringComparison.Ordinal);
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
            CreateStandardParityCase("cloud-kimi-k2-7-code", "kimi-k2.7-code:cloud"),
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

    private static ReasoningParityCase CreateStandardParityCase(string fixtureId, string modelId)
    {
        return CreateParityCase(
            fixtureId,
            modelId,
            Enum.GetValues<ReasoningLevel>(),
            OpenAiReasoningControlMode.StandardEffort,
            OpenAiReasoningResponseMode.ReasoningContent);
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
        Assert.Equal(
            "json_object",
            request.GetProperty("response_format").GetProperty("type").GetString());
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
