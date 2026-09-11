namespace Threadsmith.AnthropicProvider.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.Anthropic;

/// <summary>Exercises native request preparation, authority, schemas, and exact private replay.</summary>
public sealed class AnthropicRequestMapperTests
{
    /// <summary>Changing request context cannot rewrite native system blocks or the preceding conversation prefix.</summary>
    [Fact]
    public async Task StreamAsync_RequestContext_LeavesReusableNativePrefixUnchanged()
    {
        var handler = new TestAnthropicHandler(TestAnthropic.TextStream(), TestAnthropic.TextStream());
        using var client = new HttpClient(handler);
        var compatibility = TestAnthropic.Compatibility() with { PromptCachingEnabled = true, MinimumCacheableTokens = 1 };
        var provider = new AnthropicModelProvider(client, TestAnthropic.Profile(), "key", compatibility);
        var request = TestAnthropic.Request() with
        {
            Messages =
            [
                TestAnthropic.Message(ModelMessageRole.System, "host-policy", "stable host"),
                TestAnthropic.Message(ModelMessageRole.Developer, "repository-instructions", "stable repository"),
                TestAnthropic.Message(ModelMessageRole.User, "recent-user", "prior question"),
                TestAnthropic.Message(ModelMessageRole.Assistant, "recent-assistant", "prior answer"),
                TestAnthropic.Message(ModelMessageRole.HostContext, "repository-memory", "old memory"),
                TestAnthropic.Message(ModelMessageRole.HostContext, "governed-request-state", "old evidence"),
                TestAnthropic.Message(ModelMessageRole.User, "current-user", "current question"),
            ],
        };
        await TestAnthropic.CollectAsync(provider, request);
        await TestAnthropic.CollectAsync(provider, request with
        {
            Messages = [.. request.Messages.Take(4), TestAnthropic.Message(ModelMessageRole.HostContext, "governed-request-state", "fresh evidence"), request.Messages[^1]],
        });

        var first = handler.Requests[0];
        var next = handler.Requests[1];
        Assert.Equal(first.GetProperty("system").GetRawText(), next.GetProperty("system").GetRawText());
        for (var index = 0; index < 2; index++)
        {
            Assert.Equal(first.GetProperty("messages")[index].GetRawText(), next.GetProperty("messages")[index].GetRawText());
        }

        Assert.DoesNotContain("old memory", next.GetRawText(), StringComparison.Ordinal);
        Assert.Equal("user", next.GetProperty("messages")[2].GetProperty("role").GetString());
        Assert.Equal("<threadsmith_host_context>\nfresh evidence\n</threadsmith_host_context>", next.GetProperty("messages")[2].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("current question", next.GetProperty("messages")[2].GetProperty("content")[1].GetProperty("text").GetString());
        Assert.Equal("ephemeral", next.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("cache_control").GetProperty("type").GetString());
        Assert.Equal("ephemeral", next.GetProperty("messages")[2].GetProperty("content")[1].GetProperty("cache_control").GetProperty("type").GetString());
    }

    /// <summary>Initial instructions retain trust order while hidden and legacy duplicate content stay absent.</summary>
    [Fact]
    public async Task StreamAsync_StructuredMessages_PreservesNativeRolesAndExactContent()
    {
        var handler = new TestAnthropicHandler(TestAnthropic.TextStream());
        using var client = new HttpClient(handler);
        var request = TestAnthropic.Request() with
        {
            ProviderInstructions = new ModelProviderInstructions { SectionId = "host-policy", Content = "host\npolicy" },
            Messages =
            [
                TestAnthropic.Message(ModelMessageRole.System, "host-policy", "host\npolicy"),
                TestAnthropic.Message(ModelMessageRole.Developer, "repository-instructions", "<untrusted>repo</untrusted>"),
                TestAnthropic.Message(ModelMessageRole.User, "current-user", "{\"data\":true}") with
                {
                    Content = [new ModelContentPart { Kind = ModelContentPartKind.Json, Content = "{\"data\":true}" }, new ModelContentPart { Content = "HIDDEN_CANARY", IsModelVisible = false }],
                },
                TestAnthropic.Message(ModelMessageRole.User, "steering", "more"),
            ],
        };
        await TestAnthropic.CollectAsync(new AnthropicModelProvider(client, TestAnthropic.Profile(), "key", TestAnthropic.Compatibility()), request);
        var body = Assert.Single(handler.Requests);
        Assert.Equal(2, body.GetProperty("system").GetArrayLength());
        Assert.Equal("host\npolicy", body.GetProperty("system")[0].GetProperty("text").GetString());
        Assert.Equal("<untrusted>repo</untrusted>", body.GetProperty("system")[1].GetProperty("text").GetString());
        Assert.Single(body.GetProperty("messages").EnumerateArray());
        Assert.Equal(2, body.GetProperty("messages")[0].GetProperty("content").GetArrayLength());
        Assert.DoesNotContain("HIDDEN_CANARY", body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(request.Input, body.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>Unsupported authority or output shapes fail local admission before observer or transport.</summary>
    [Theory]
    [InlineData("late-system")]
    [InlineData("profile")]
    [InlineData("sensitive")]
    [InlineData("output")]
    [InlineData("duplicate-instructions")]
    [InlineData("capacity")]
    public async Task StreamAsync_InvalidLocalRequest_NoSubmission(string kind)
    {
        var handler = new TestAnthropicHandler(TestAnthropic.TextStream());
        using var client = new HttpClient(handler);
        var observed = 0;
        var request = TestAnthropic.Request() with { SubmissionObserver = () => observed++ };
        var profile = TestAnthropic.Profile();
        request = kind switch
        {
            "late-system" => request with { Messages = [.. request.Messages, TestAnthropic.Message(ModelMessageRole.System, "late-policy", "not allowed")] },
            "profile" => request with { ResolvedProfileId = new ModelProfileId(Guid.NewGuid()) },
            "sensitive" => request with { ContainsSensitiveData = true },
            "output" => request with { MaximumOutputTokens = profile.MaximumOutputTokens + 1 },
            "duplicate-instructions" => request with { ProviderInstructions = new ModelProviderInstructions { SectionId = "current-user", Content = "hello" } },
            _ => request,
        };
        if (kind == "capacity")
        {
            profile = profile with { ContextWindow = 1 };
        }

        await Assert.ThrowsAsync<ModelProviderException>(() => TestAnthropic.CollectAsync(new AnthropicModelProvider(client, profile, "key", TestAnthropic.Compatibility()), request));
        Assert.Empty(handler.Requests);
        Assert.Equal(0, observed);
    }

    /// <summary>Thinking display controls new summaries independently of model effort.</summary>
    [Theory]
    [InlineData(true, "summarized")]
    [InlineData(false, "omitted")]
    [InlineData(null, "omitted")]
    public async Task StreamAsync_ThinkingPreference_ProjectsExactDisplay(bool? include, string expected)
    {
        var handler = new TestAnthropicHandler(TestAnthropic.ToolStream(thinking: true));
        using var client = new HttpClient(handler);
        var request = TestAnthropic.Request(true) with { IncludeReasoningText = include, Tools = [TestAnthropic.Tool()] };
        var chunks = await TestAnthropic.CollectAsync(new AnthropicModelProvider(client, TestAnthropic.Profile(true), "key", TestAnthropic.Compatibility(true)), request);
        Assert.Equal(expected, handler.Requests[0].GetProperty("thinking").GetProperty("display").GetString());
        Assert.Equal("low", handler.Requests[0].GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal(include == true, chunks.Any(chunk => chunk.Reasoning is not null));
        var diagnostic = JsonSerializer.Serialize(chunks);
        Assert.DoesNotContain("signed-canary", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("redacted-canary", diagnostic, StringComparison.Ordinal);
        var envelope = Assert.Single(chunks, chunk => chunk.ResponseEnvelope is not null).ResponseEnvelope;
        Assert.NotNull(envelope);
        Assert.Equal(25, envelope.RetainedOutputTokens);
        Assert.Equal("{}", JsonSerializer.Serialize(envelope));
        Assert.DoesNotContain("signed-canary", envelope.ToString(), StringComparison.Ordinal);
        envelope.Dispose();
    }

    /// <summary>Manual budgets remain explicit and strictly below the requested output ceiling.</summary>
    [Theory]
    [InlineData(1023, false)]
    [InlineData(1024, true)]
    [InlineData(2048, false)]
    public void Create_ManualThinking_ValidatesBudget(int budget, bool valid)
    {
        var compatibility = TestAnthropic.Compatibility(true) with { ThinkingMode = AnthropicThinkingMode.Manual, ManualThinkingBudgets = new Dictionary<ReasoningLevel, int> { [ReasoningLevel.Low] = budget } };
        var request = TestAnthropic.Request(true) with { MaximumOutputTokens = 2048, IncludeReasoningText = false };
        if (valid)
        {
            var body = AnthropicRequestMapper.CreateBody(request, TestAnthropic.Profile(true), compatibility);
            Assert.Equal(budget, body["thinking"]?["budget_tokens"]?.GetValue<int>());
            Assert.False(body.ContainsKey("output_config"));
        }
        else
        {
            Assert.Throws<ModelProviderException>(() => AnthropicRequestMapper.CreateBody(request, TestAnthropic.Profile(true), compatibility));
        }
    }

    /// <summary>Optional/null strict projection and safe fallback leave canonical schema authority intact.</summary>
    [Fact]
    public void Create_StrictSchemas_PreservesCanonicalAndFallsBackForRecursion()
    {
        const string optional = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}},\"additionalProperties\":false}";
        const string recursive = "{\"type\":\"object\",\"properties\":{\"node\":{\"$ref\":\"#/$defs/node\"}},\"$defs\":{\"node\":{\"type\":\"object\",\"properties\":{\"next\":{\"$ref\":\"#/$defs/node\"}},\"additionalProperties\":false}},\"additionalProperties\":false}";
        var request = TestAnthropic.Request() with { Tools = [TestAnthropic.Tool("strict", optional) with { PreferStrictArguments = true }, TestAnthropic.Tool("fallback", recursive) with { PreferStrictArguments = true }] };
        var body = AnthropicRequestMapper.CreateBody(request, TestAnthropic.Profile(), TestAnthropic.Compatibility());
        var tools = Assert.IsType<JsonArray>(body["tools"]);
        var strict = tools.Single(tool => tool?["name"]?.GetValue<string>() == "strict");
        Assert.True(strict?["strict"]?.GetValue<bool>());
        Assert.Contains("null", strict?["input_schema"]?.ToJsonString() ?? string.Empty, StringComparison.Ordinal);
        var fallback = Assert.IsType<JsonObject>(tools.Single(tool => tool?["name"]?.GetValue<string>() == "fallback"));
        Assert.False(fallback.ContainsKey("strict"));
        Assert.Equal(optional, request.Tools[0].ArgumentsJsonSchema);
    }

    /// <summary>Final-response schemas merge with effort while tool concurrency remains an independent auto preference.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void Create_ResponseFormatAndConcurrency_MergesIndependentPolicies(bool? multiple)
    {
        var request = TestAnthropic.Request(true) with
        {
            Tools = [TestAnthropic.Tool()],
            AllowMultipleToolCalls = multiple,
            ResponseFormat = new ModelResponseFormat { SchemaId = "fixture", JsonSchema = TestAnthropic.Tool().ArgumentsJsonSchema },
        };
        var body = AnthropicRequestMapper.CreateBody(request, TestAnthropic.Profile(true), TestAnthropic.Compatibility(true));
        Assert.Equal("low", body["output_config"]?["effort"]?.GetValue<string>());
        Assert.Equal("json_schema", body["output_config"]?["format"]?["type"]?.GetValue<string>());
        Assert.Equal(multiple.HasValue, body.ContainsKey("tool_choice"));
        if (multiple is { } allowed)
        {
            Assert.Equal(!allowed, body["tool_choice"]?["disable_parallel_tool_use"]?.GetValue<bool>());
            Assert.Equal("auto", body["tool_choice"]?["type"]?.GetValue<string>());
        }
    }

    /// <summary>Prepared explicit cache boundaries are distinct, stable, and absent when caching is disabled.</summary>
    [Fact]
    public void Prepare_CacheSectionsAndDigest_ReflectsNativeShape()
    {
        var request = TestAnthropic.Request() with
        {
            Tools = [TestAnthropic.Tool() with { PreferStrictArguments = true }],
            Messages = [TestAnthropic.Message(ModelMessageRole.System, "host-policy", "host stable"), TestAnthropic.Message(ModelMessageRole.Developer, "repository-instructions", "repository stable"), TestAnthropic.Message(ModelMessageRole.Developer, "phase-policy", "phase stable"), .. TestAnthropic.Request().Messages],
        };
        var compatibility = TestAnthropic.Compatibility() with { PromptCachingEnabled = true, MinimumCacheableTokens = 1 };
        var prepared = AnthropicRequestPreparer.Prepare(request, TestAnthropic.Profile(), compatibility, "instance");
        var projected = request with { CacheCapabilities = prepared.CacheCapabilities, CachePlan = prepared.CachePlan };
        var body = AnthropicRequestMapper.CreateBody(projected, TestAnthropic.Profile(), compatibility);
        Assert.Equal(4, prepared.CachePlan?.Breakpoints.Count);
        Assert.Equal(4, CountCacheControls(body));
        Assert.NotNull(body["messages"]?.AsArray().Last()?["content"]?.AsArray().Last()?["cache_control"]);
        Assert.Equal(AnthropicRequestMapper.Digest(body), prepared.WireDigest);
        Assert.True(prepared.WireEstimate.WireInputTokens >= JsonSerializer.SerializeToUtf8Bytes(body).Length);
        var uncached = AnthropicRequestMapper.CreateBody(projected, TestAnthropic.Profile(), compatibility with { PromptCachingEnabled = false });
        RemoveCacheControls(body);
        Assert.True(JsonNode.DeepEquals(body, uncached));
    }

    /// <summary>Exact thinking/text/tool replay remains in chronological position despite sanitized host text and a display toggle.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamAsync_TwoToolContinuation_ReplaysSignedContentExactlyOnce(bool withRequestContext)
    {
        var handler = new TestAnthropicHandler(TestAnthropic.ToolStream(thinking: true, secondTool: true), TestAnthropic.TextStream("done"));
        using var client = new HttpClient(handler);
        using var state = new ModelRequestTransientState();
        var request = TestAnthropic.Request(true) with { Tools = [TestAnthropic.Tool()], IncludeReasoningText = true, TransientState = state };
        if (withRequestContext)
        {
            request = request with
            {
                Messages =
                [
                    TestAnthropic.Message(ModelMessageRole.System, "host-policy", "stable host"),
                    TestAnthropic.Message(ModelMessageRole.User, "recent-user", "prior question"),
                    TestAnthropic.Message(ModelMessageRole.Assistant, "recent-assistant", "prior answer"),
                    TestAnthropic.Message(ModelMessageRole.HostContext, "governed-request-state", "current evidence"),
                    .. request.Messages,
                ],
            };
        }

        var chunks = await TestAnthropic.CollectAsync(new AnthropicModelProvider(client, TestAnthropic.Profile(true), "key", TestAnthropic.Compatibility(true), "instance"), request);
        var continuation = BindContinuation(request, chunks);
        await TestAnthropic.CollectAsync(new AnthropicModelProvider(client, TestAnthropic.Profile(true), "key", TestAnthropic.Compatibility(true), "instance"), continuation with { IncludeReasoningText = false });
        var second = handler.Requests[1];
        var messages = second.GetProperty("messages");
        var offset = withRequestContext ? 2 : 0;
        Assert.Equal("hello", messages[offset].GetProperty("content").EnumerateArray().Last().GetProperty("text").GetString());
        var content = messages[offset + 1].GetProperty("content");
        Assert.Equal(new[] { "thinking", "redacted_thinking", "text", "tool_use", "tool_use" }, content.EnumerateArray().Select(block => block.GetProperty("type").GetString()));
        Assert.Equal("signed-canary-AB", content[0].GetProperty("signature").GetString());
        Assert.Equal("redacted-canary", content[1].GetProperty("data").GetString());
        Assert.Equal("original visible text", content[2].GetProperty("text").GetString());
        Assert.Equal("wire_one", content[3].GetProperty("id").GetString());
        Assert.Equal("wire_two", content[4].GetProperty("id").GetString());
        var results = messages[offset + 2].GetProperty("content");
        Assert.Equal("wire_one", results[0].GetProperty("tool_use_id").GetString());
        Assert.Equal("wire_two", results[1].GetProperty("tool_use_id").GetString());
        Assert.True(results[1].GetProperty("is_error").GetBoolean());
        Assert.Equal("omitted", second.GetProperty("thinking").GetProperty("display").GetString());
        Assert.DoesNotContain("host-sanitized", second.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>Every changed continuation authority fails before another HTTP request.</summary>
    [Theory]
    [InlineData("key")]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("tools")]
    [InlineData("instructions")]
    [InlineData("thinking")]
    [InlineData("history")]
    [InlineData("preceding-user")]
    [InlineData("host-context")]
    public async Task StreamAsync_ReplayIdentityChange_FailsLocally(string change)
    {
        var handler = new TestAnthropicHandler(TestAnthropic.ToolStream(thinking: true, secondTool: true));
        using var client = new HttpClient(handler);
        using var state = new ModelRequestTransientState();
        var request = TestAnthropic.Request(true) with { Tools = [TestAnthropic.Tool()], TransientState = state };
        if (change == "host-context")
        {
            request = request with { Messages = [TestAnthropic.Message(ModelMessageRole.HostContext, "governed-request-state", "original host context"), .. request.Messages] };
        }

        var profile = TestAnthropic.Profile(true);
        var compatibility = TestAnthropic.Compatibility(true);
        var chunks = await TestAnthropic.CollectAsync(new AnthropicModelProvider(client, profile, "key", compatibility, "instance"), request);
        var next = BindContinuation(request, chunks);
        next = change switch
        {
            "tools" => next with { Tools = [TestAnthropic.Tool() with { Description = "changed authority" }] },
            "instructions" => next with { ProviderInstructions = new ModelProviderInstructions { SectionId = "new", Content = "changed" } },
            "thinking" => next with { ReasoningLevel = ReasoningLevel.High },
            "history" => next with { HistoryRewriteGeneration = 1 },
            "preceding-user" => next with { Messages = [next.Messages[0] with { Content = [new ModelContentPart { Content = "rewritten user" }] }, .. next.Messages.Skip(1)] },
            "host-context" => next with { Messages = [next.Messages[0] with { Content = [new ModelContentPart { Content = "changed host context" }] }, .. next.Messages.Skip(1)] },
            _ => next,
        };
        if (change == "model")
        {
            profile = profile with { ModelId = "claude-changed" };
            compatibility = compatibility with { ModelId = "claude-changed" };
        }

        await Assert.ThrowsAsync<ModelProviderException>(() => TestAnthropic.CollectAsync(new AnthropicModelProvider(client, profile, change == "key" ? "rotated" : "key", compatibility, change == "provider" ? "other" : "instance"), next));
        Assert.Single(handler.Requests);
    }

    /// <summary>A frozen prepared body cannot be changed between admission and native submission.</summary>
    [Fact]
    public async Task StreamAsync_ChangedPreparedRequest_RejectsBeforeHttp()
    {
        var handler = new TestAnthropicHandler(TestAnthropic.TextStream());
        using var client = new HttpClient(handler);
        var request = TestAnthropic.Request();
        var prepared = AnthropicRequestPreparer.Prepare(request, TestAnthropic.Profile(), TestAnthropic.Compatibility(), "anthropic");
        var changed = request with { Preparation = prepared, Messages = [TestAnthropic.Message(ModelMessageRole.User, "current-user", "changed after admission")] };
        await Assert.ThrowsAsync<ModelProviderException>(() => TestAnthropic.CollectAsync(new AnthropicModelProvider(client, TestAnthropic.Profile(), "key", TestAnthropic.Compatibility()), changed));
        Assert.Empty(handler.Requests);
    }

    /// <summary>Unsigned historical calls use native blocks and all matching results precede steering.</summary>
    [Fact]
    public void Create_UnsignedHistory_ReconstructsNativeToolsAndOrdersResults()
    {
        var request = TestAnthropic.Request() with
        {
            Messages =
            [
                .. TestAnthropic.Request().Messages,
                TestAnthropic.Message(ModelMessageRole.Assistant, "call", "{}", "historical_one", "lookup"),
                TestAnthropic.Message(ModelMessageRole.Assistant, "call", "{}", "historical_two", "lookup"),
                TestAnthropic.Message(ModelMessageRole.Tool, "result", "second", "historical_two", "lookup", error: true),
                TestAnthropic.Message(ModelMessageRole.Tool, "result", "first", "historical_one", "lookup"),
                TestAnthropic.Message(ModelMessageRole.User, "steering", "continue"),
            ],
        };
        var body = AnthropicRequestMapper.CreateBody(request, TestAnthropic.Profile(), TestAnthropic.Compatibility());
        Assert.Equal("tool_use", body["messages"]?[1]?["content"]?[0]?["type"]?.GetValue<string>());
        Assert.Equal("historical_one", body["messages"]?[2]?["content"]?[0]?["tool_use_id"]?.GetValue<string>());
        Assert.Equal("historical_two", body["messages"]?[2]?["content"]?[1]?["tool_use_id"]?.GetValue<string>());
        Assert.Equal("continue", body["messages"]?[2]?["content"]?[2]?["text"]?.GetValue<string>());
    }

    /// <summary>Strict grammar limits retain non-strict fallback without discarding any authorized tool.</summary>
    [Fact]
    public void Create_StrictToolLimit_UsesSafeFallbackForTwentyFirstTool()
    {
        var tools = Enumerable.Range(0, 21).Select(index => TestAnthropic.Tool("lookup_" + index) with { PreferStrictArguments = true }).ToArray();
        var body = AnthropicRequestMapper.CreateBody(TestAnthropic.Request() with { Tools = tools }, TestAnthropic.Profile(), TestAnthropic.Compatibility());
        var projected = Assert.IsType<JsonArray>(body["tools"]);
        Assert.Equal(21, projected.Count);
        Assert.Equal(20, projected.Count(tool => tool?["strict"]?.GetValue<bool>() == true));
    }

    /// <summary>Provider-incompatible canonical tool names return their exact host identities after native aliasing.</summary>
    [Fact]
    public async Task StreamAsync_ToolWireAlias_ReturnsCanonicalIdentity()
    {
        var tool = TestAnthropic.Tool("mcp.fixture.lookup");
        var alias = ModelToolWireNameMap.Create([tool]).ToWireName(tool.Name);
        using var client = new HttpClient(new TestAnthropicHandler(TestAnthropic.ToolStream().Replace("lookup", alias, StringComparison.Ordinal)));
        var chunks = await TestAnthropic.CollectAsync(new AnthropicModelProvider(client, TestAnthropic.Profile(), "key", TestAnthropic.Compatibility()), TestAnthropic.Request() with { Tools = [tool] });
        Assert.Equal(tool.Name, Assert.IsType<ToolRequestModelOutput>(Assert.Single(chunks, chunk => chunk.Output is not null).Output).ToolName);
        Assert.Single(chunks, chunk => chunk.ResponseEnvelope is not null).ResponseEnvelope?.Dispose();
    }

    /// <summary>Identical local ordinals across consecutive rounds retain independent wire identities and exact prefixes.</summary>
    [Fact]
    public async Task StreamAsync_ThreeRounds_KeepsRoundSpecificReplayAndCorrelation()
    {
        var first = TestAnthropic.ToolStream(thinking: true, secondTool: true);
        var second = first.Replace("wire_one", "wire_three", StringComparison.Ordinal).Replace("wire_two", "wire_four", StringComparison.Ordinal);
        var handler = new TestAnthropicHandler(first, second, TestAnthropic.TextStream("done"));
        using var client = new HttpClient(handler);
        using var state = new ModelRequestTransientState();
        var request = TestAnthropic.Request(true) with { Tools = [TestAnthropic.Tool()], TransientState = state };
        for (var round = 0; round < 2; round++)
        {
            var chunks = await TestAnthropic.CollectAsync(new AnthropicModelProvider(client, TestAnthropic.Profile(true), "key", TestAnthropic.Compatibility(true), "instance"), request);
            request = BindContinuation(request, chunks);
        }

        await TestAnthropic.CollectAsync(new AnthropicModelProvider(client, TestAnthropic.Profile(true), "key", TestAnthropic.Compatibility(true), "instance"), request);
        var messages = handler.Requests[2].GetProperty("messages");
        Assert.Equal(5, messages.GetArrayLength());
        Assert.Equal("wire_one", messages[2].GetProperty("content")[0].GetProperty("tool_use_id").GetString());
        Assert.Equal("wire_three", messages[4].GetProperty("content")[0].GetProperty("tool_use_id").GetString());
        Assert.Equal(2, state.Responses.Count);
        Assert.Equal(50, state.RetainedOutputTokens);
    }

    /// <summary>Active round tags cannot silently fall back to reconstructed unsigned historical calls.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamAsync_MissingActiveReplay_RejectsBeforeAnotherSubmission(bool clearState)
    {
        var handler = new TestAnthropicHandler(TestAnthropic.ToolStream(thinking: true, secondTool: true));
        using var client = new HttpClient(handler);
        using var state = new ModelRequestTransientState();
        var submissions = 0;
        var request = TestAnthropic.Request(true) with { Tools = [TestAnthropic.Tool()], TransientState = state, SubmissionObserver = () => submissions++ };
        var provider = new AnthropicModelProvider(client, TestAnthropic.Profile(true), "key", TestAnthropic.Compatibility(true), "instance");
        var chunks = await TestAnthropic.CollectAsync(provider, request);
        var continuation = BindContinuation(request, chunks);
        if (clearState)
        {
            state.Clear();
        }
        else
        {
            continuation = continuation with { TransientState = null };
        }

        await Assert.ThrowsAsync<ModelProviderException>(() => TestAnthropic.CollectAsync(provider, continuation));
        Assert.Equal(1, submissions);
        Assert.Single(handler.Requests);
    }

    /// <summary>Failed or abandoned later enumeration cannot release replay retained by the host for correction.</summary>
    [Theory]
    [InlineData("eof")]
    [InlineData("malformed")]
    [InlineData("dispose")]
    public async Task StreamAsync_FailedContinuation_PreservesHostOwnedReplayForCorrection(string failure)
    {
        var interrupted = TestAnthropic.Start() + TestAnthropic.Block(0, new { type = "text", text = "partial" });
        if (failure == "malformed")
        {
            interrupted += TestAnthropic.Event("content_block_delta", new { type = "content_block_delta", index = 0 });
        }

        var handler = new TestAnthropicHandler(TestAnthropic.ToolStream(thinking: true, secondTool: true), interrupted, TestAnthropic.TextStream("corrected"));
        using var client = new HttpClient(handler);
        using var state = new ModelRequestTransientState();
        var provider = new AnthropicModelProvider(client, TestAnthropic.Profile(true), "key", TestAnthropic.Compatibility(true), "instance");
        var request = TestAnthropic.Request(true) with { Tools = [TestAnthropic.Tool()], TransientState = state };
        var first = await TestAnthropic.CollectAsync(provider, request);
        var continuation = BindContinuation(request, first);
        var retained = Assert.Single(state.Responses);
        var byteCount = retained.ByteCount;
        if (failure == "dispose")
        {
            await using var iterator = provider.StreamAsync(continuation).GetAsyncEnumerator();
            Assert.True(await iterator.MoveNextAsync());
            Assert.Equal("partial", iterator.Current.Text);
        }
        else
        {
            await Assert.ThrowsAsync<MalformedModelOutputException>(() => TestAnthropic.CollectAsync(provider, continuation));
        }

        Assert.Same(retained, Assert.Single(state.Responses));
        Assert.Equal(byteCount, retained.ByteCount);
        Assert.True(byteCount > 0);
        Assert.Equal(25, state.RetainedOutputTokens);
        var corrected = continuation with { Messages = [.. continuation.Messages, TestAnthropic.Message(ModelMessageRole.User, "correction", "Complete the response.")] };
        var final = await TestAnthropic.CollectAsync(provider, corrected);
        Assert.Equal("corrected", string.Concat(final.Select(chunk => chunk.Text)));
        Assert.Equal(3, handler.Requests.Count);
        var replay = handler.Requests[2].GetProperty("messages")[1];
        Assert.Equal(handler.Requests[1].GetProperty("messages")[1].GetRawText(), replay.GetRawText());
        Assert.Equal("signed-canary-AB", replay.GetProperty("content")[0].GetProperty("signature").GetString());
        Assert.Equal("wire_one", replay.GetProperty("content")[3].GetProperty("id").GetString());
        Assert.Equal("wire_one", handler.Requests[2].GetProperty("messages")[2].GetProperty("content")[0].GetProperty("tool_use_id").GetString());
    }

    /// <summary>Nullable scalar enums use API-compatible alternatives while preserving exact allowed values.</summary>
    [Theory]
    [InlineData("string", "[\"red\",\"blue\"]")]
    [InlineData("integer", "[1,2]")]
    [InlineData("number", "[1.5,2.5]")]
    [InlineData("boolean", "[true,false]")]
    public void Prepare_OptionalTypedEnum_UsesScalarAlternativesAndStableDigest(string type, string values)
    {
        var schema = "{\"type\":\"object\",\"properties\":{\"choice\":{\"type\":\"" + type + "\",\"enum\":" + values + "}},\"additionalProperties\":false}";
        var request = TestAnthropic.Request() with { Tools = [TestAnthropic.Tool("classify_fixture", schema) with { PreferStrictArguments = true }] };
        var prepared = AnthropicRequestPreparer.Prepare(request, TestAnthropic.Profile(), TestAnthropic.Compatibility(), "anthropic");
        var projected = request with { CacheCapabilities = prepared.CacheCapabilities, CachePlan = prepared.CachePlan };
        var body = AnthropicRequestMapper.CreateBody(projected, TestAnthropic.Profile(), TestAnthropic.Compatibility());
        var tool = Assert.IsType<JsonObject>(body["tools"]?[0]);
        Assert.True(tool["strict"]?.GetValue<bool>());
        var choice = Assert.IsType<JsonObject>(tool["input_schema"]?["properties"]?["choice"]);
        Assert.False(choice.ContainsKey("type"));
        Assert.False(choice.ContainsKey("enum"));
        var alternatives = Assert.IsType<JsonArray>(choice["anyOf"]);
        Assert.Equal(2, alternatives.Count);
        Assert.Equal(type, alternatives[0]?["type"]?.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(values), alternatives[0]?["enum"]));
        Assert.Equal("null", alternatives[1]?["type"]?.GetValue<string>());
        Assert.Equal("choice", tool["input_schema"]?["required"]?[0]?.GetValue<string>());
        Assert.Equal(schema, request.Tools[0].ArgumentsJsonSchema);
        Assert.Equal(AnthropicRequestMapper.Digest(body), prepared.WireDigest);
    }

    /// <summary>Required nullable enum projection retains the enum's exclusion of null.</summary>
    [Fact]
    public void Create_RequiredNullableTypedEnum_DoesNotBroadenAllowedValues()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"choice\":{\"type\":[\"string\",\"null\"],\"enum\":[\"red\",\"blue\"]}},\"required\":[\"choice\"],\"additionalProperties\":false}";
        var request = TestAnthropic.Request() with { ResponseFormat = new ModelResponseFormat { SchemaId = "fixture", JsonSchema = schema } };
        var body = AnthropicRequestMapper.CreateBody(request, TestAnthropic.Profile(), TestAnthropic.Compatibility());
        var alternatives = Assert.IsType<JsonArray>(body["output_config"]?["format"]?["schema"]?["properties"]?["choice"]?["anyOf"]);
        Assert.Single(alternatives);
        Assert.Equal("string", alternatives[0]?["type"]?.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("[\"red\",\"blue\"]"), alternatives[0]?["enum"]));
    }

    /// <summary>Unreviewed mixed scalar enum unions keep the original schema and omit the strict preference.</summary>
    [Fact]
    public void Create_MixedEnumTypeUnion_PreservesNonStrictFallback()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"choice\":{\"type\":[\"string\",\"integer\"],\"enum\":[\"red\",1]}},\"required\":[\"choice\"],\"additionalProperties\":false}";
        var request = TestAnthropic.Request() with { Tools = [TestAnthropic.Tool("classify_fixture", schema) with { PreferStrictArguments = true }] };
        var body = AnthropicRequestMapper.CreateBody(request, TestAnthropic.Profile(), TestAnthropic.Compatibility());
        var tool = Assert.IsType<JsonObject>(body["tools"]?[0]);
        Assert.False(tool.ContainsKey("strict"));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(schema), tool["input_schema"]));
    }

    /// <summary>Nested enum normalization preserves schema annotations and never mutates the host's source schema.</summary>
    [Fact]
    public void Create_NestedNullableEnum_PreservesAnnotationsAndOriginalDefault()
    {
        const string schema = "{\"type\":\"object\",\"properties\":{\"rows\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"choice\":{\"type\":\"string\",\"enum\":[\"red\",\"blue\"],\"description\":\"Choose a color.\",\"title\":\"Color\",\"default\":\"red\"}},\"additionalProperties\":false}}},\"required\":[\"rows\"],\"additionalProperties\":false}";
        var request = TestAnthropic.Request() with { Tools = [TestAnthropic.Tool("classify_fixture", schema) with { PreferStrictArguments = true }] };
        var body = AnthropicRequestMapper.CreateBody(request, TestAnthropic.Profile(), TestAnthropic.Compatibility());
        var choice = Assert.IsType<JsonObject>(body["tools"]?[0]?["input_schema"]?["properties"]?["rows"]?["items"]?["properties"]?["choice"]);
        Assert.Equal("Choose a color.", choice["description"]?.GetValue<string>());
        Assert.Equal("Color", choice["title"]?.GetValue<string>());
        Assert.Equal(2, Assert.IsType<JsonArray>(choice["anyOf"]).Count);
        Assert.Equal(schema, request.Tools[0].ArgumentsJsonSchema);
        Assert.Equal("red", JsonNode.Parse(request.Tools[0].ArgumentsJsonSchema)?["properties"]?["rows"]?["items"]?["properties"]?["choice"]?["default"]?.GetValue<string>());
    }

    private static ModelStreamRequest BindContinuation(ModelStreamRequest request, IReadOnlyList<ModelChunk> chunks)
    {
        var state = request.TransientState ?? throw new InvalidOperationException("Fixture requires owned state.");
        var envelope = chunks.Single(chunk => chunk.ResponseEnvelope is not null).ResponseEnvelope ?? throw new InvalidOperationException("Fixture requires completed response.");
        Assert.Contains(envelope, state.Responses);
        var round = request.ToolContinuationRound;
        state.BindToolCall(round, 0, "host_one");
        state.BindToolCall(round, 1, "host_two");
        ModelMessage[] messages =
        [
            .. request.Messages,
            TestAnthropic.Message(ModelMessageRole.Assistant, "assistant", "host-sanitized", round: round),
            TestAnthropic.Message(ModelMessageRole.Assistant, "call", "{}", "host_one", "lookup", round),
            TestAnthropic.Message(ModelMessageRole.Assistant, "call", "{}", "host_two", "lookup", round),
            TestAnthropic.Message(ModelMessageRole.Tool, "result", "second denied", "host_two", "lookup", round, true),
            TestAnthropic.Message(ModelMessageRole.Tool, "result", "first result", "host_one", "lookup", round),
        ];
        state.SealRound(round, messages);
        return request with { ToolContinuationRound = round + 1, Messages = messages };
    }

    private static int CountCacheControls(JsonNode? node) => node switch
    {
        JsonObject item => (item.ContainsKey("cache_control") ? 1 : 0) + item.Sum(property => CountCacheControls(property.Value)),
        JsonArray items => items.Sum(CountCacheControls),
        _ => 0,
    };

    private static void RemoveCacheControls(JsonNode? node)
    {
        if (node is JsonObject item)
        {
            item.Remove("cache_control");
            foreach (var property in item)
            {
                RemoveCacheControls(property.Value);
            }
        }
        else if (node is JsonArray items)
        {
            foreach (var child in items)
            {
                RemoveCacheControls(child);
            }
        }
    }
}
