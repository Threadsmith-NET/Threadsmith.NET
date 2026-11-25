namespace Threadsmith.ModelTooling.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible.OpenRouter;
using Xunit;

public static partial class OpenRouterProviderTests
{
    /// <summary>Optional arguments stay optional unless both opt-ins produce a supported strict schema.</summary>
    [Theory]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public static async Task ToolSchemas_ExplicitlyPreserveStrictOptOut(
        bool supportsStrict,
        bool prefersStrict,
        bool supportedSchema,
        bool expectedStrict)
    {
        var model = Model() with { StrictToolArguments = supportsStrict };
        var schema = JsonNode.Parse("""{"type":"object","additionalProperties":false,"required":[],"properties":{"query":{"type":"string"},"selector":{"type":"string"}}}""")!.AsObject();
        if (!supportedSchema)
        {
            schema["not"] = new JsonObject { ["required"] = new JsonArray("query", "selector") };
        }

        var request = Request() with
        {
            Tools =
            [
                new ModelToolDefinition
                {
                    Name = "inspect_skill",
                    Description = "Search by query or inspect by selector; omit the unused field.",
                    ArgumentsJsonSchema = schema.ToJsonString(),
                    PreferStrictArguments = prefersStrict,
                },
            ],
        };
        using var handler = new Handler(Completed());
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        await CollectAsync(Provider(client, model), request, []);

        using var body = JsonDocument.Parse(handler.Body!);
        var function = body.RootElement.GetProperty("tools")[0];
        Assert.Equal("inspect_skill", function.GetProperty("name").GetString());
        Assert.Equal(expectedStrict, function.GetProperty("strict").GetBoolean());
        var parameters = function.GetProperty("parameters");
        if (expectedStrict)
        {
            Assert.Equal(["query", "selector"], parameters.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
            foreach (var property in parameters.GetProperty("properties").EnumerateObject())
            {
                Assert.Equal(["string", "null"], property.Value.GetProperty("type").EnumerateArray().Select(value => value.GetString()));
            }
        }
        else
        {
            using var original = JsonDocument.Parse(request.Tools[0].ArgumentsJsonSchema);
            Assert.True(JsonElement.DeepEquals(original.RootElement, parameters));
            Assert.Empty(parameters.GetProperty("required").EnumerateArray());
        }
    }

    /// <summary>Seed capability gates only seed; optional temperature remains independent.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task Sampling_RespectsSeedCapabilityAndOptionalTemperature(bool seedSupported, bool temperatureConfigured)
    {
        var model = Model() with { SeedSupported = seedSupported, Temperature = temperatureConfigured ? 0.25m : null };
        using var handler = new Handler(Completed());
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        await CollectAsync(Provider(client, model), Request() with { Seed = 12345 }, []);

        using var json = JsonDocument.Parse(handler.Body!);
        var body = json.RootElement;
        Assert.Equal(seedSupported, body.TryGetProperty("seed", out var seed));
        if (seedSupported)
        {
            Assert.Equal(12345, seed.GetInt32());
        }

        Assert.Equal(temperatureConfigured, body.TryGetProperty("temperature", out var temperature));
        if (temperatureConfigured)
        {
            Assert.Equal(0.25m, temperature.GetDecimal());
        }
    }

    /// <summary>Budget controls cannot consume the visible completion reserve.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ReasoningBudget_RequiresVisibleHeadroom(bool insufficient)
    {
        var model = Model() with
        {
            ReasoningControl = ReasoningControllability.Selectable,
            SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.High],
            ReasoningBudgets = new Dictionary<ReasoningLevel, int> { [ReasoningLevel.High] = 1000 },
        };
        using var handler = new Handler(Completed());
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var request = Request() with { ReasoningLevel = ReasoningLevel.High, MaximumOutputTokens = insufficient ? 1000 : 1001 };
        if (insufficient)
        {
            await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(Provider(client, model), request, []));
            Assert.Equal(0, handler.Calls);
        }
        else
        {
            await CollectAsync(Provider(client, model), request, []);
            using var json = JsonDocument.Parse(handler.Body!);
            Assert.Equal(1000, json.RootElement.GetProperty("reasoning").GetProperty("max_tokens").GetInt32());
        }
    }

    /// <summary>Optional counters remain unknown while reported zero remains zero.</summary>
    [Theory]
    [InlineData("{}", true, null, null, null)]
    [InlineData("{\"input_tokens\":0,\"output_tokens\":0}", false, null, null, null)]
    [InlineData("{\"input_tokens\":10,\"output_tokens\":8,\"output_tokens_details\":{\"reasoning_tokens\":3}}", false, null, null, 3)]
    [InlineData("{\"input_tokens\":10,\"output_tokens\":8,\"input_tokens_details\":{\"cached_tokens\":0}}", false, 0, null, null)]
    [InlineData("{\"input_tokens\":10,\"output_tokens\":8,\"input_tokens_details\":{\"cache_write_tokens\":0}}", false, null, 0, null)]
    public static async Task Usage_DistinguishesUnknownZeroAndReasoningSubset(string wire, bool estimate, int? reads, int? writes, int? reasoning)
    {
        using var handler = new Handler(Terminal("response.completed", "completed", [], JsonNode.Parse(wire)!.AsObject()));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();
        await CollectAsync(Provider(client, Model()), Request(), chunks);
        var usage = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage!;
        Assert.Equal(estimate, usage.IsEstimate);
        Assert.Equal(reads, usage.Cache?.CacheReadTokens);
        Assert.Equal(writes, usage.Cache?.CacheWriteTokens);
        Assert.Equal(reasoning, usage.ReasoningTokens);
        Assert.Null(usage.ReportedCost);
        if (reasoning is not null)
        {
            Assert.Equal(8, usage.OutputTokens);
        }

        if (wire.Contains(":0,", StringComparison.Ordinal))
        {
            Assert.Equal(0, usage.InputTokens);
            Assert.Equal(0, usage.OutputTokens);
        }
    }

    /// <summary>EOF and malformed terminal frames are not successful completion.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("data: {\"type\":\"response.created\"}\n\n")]
    [InlineData("data: {\"type\":\"response.completed\"}")]
    public static async Task MissingOrTruncatedTerminal_NeverSucceeds(string body)
    {
        using var handler = new Handler(body);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();
        var error = await Record.ExceptionAsync(() => CollectAsync(Provider(client, Model()), Request(), chunks));
        if (body.EndsWith('}'))
        {
            Assert.IsType<MalformedModelOutputException>(error);
        }
        else
        {
            Assert.IsType<TransientModelException>(error);
        }

        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.FinishReason is not null);
    }

    /// <summary>Content and summary deltas reconcile independently of the display preference.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task SignedContentAndSummary_UseSeparateChannelsAndHiddenReplay(bool display)
    {
        var item = ReasoningItem("opaque", "brief");
        item["content"] = new JsonArray(new JsonObject { ["type"] = "reasoning_text", ["text"] = "thinking" });
        item["signature"] = "signature";
        item["format"] = "anthropic-claude-v1";
        using var handler = new Handler(
            Frame("""{"type":"response.reasoning_text.delta","output_index":0,"content_index":0,"delta":"think"}""")
            + ReasoningDelta("brief") + Completed(item, FunctionCall("call")));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();
        await CollectAsync(Provider(client, ReplayModel()), Request() with { IncludeReasoningText = display }, chunks);
        Assert.Equal(display ? "thinkbriefing" : string.Empty, string.Concat(chunks.Select(chunk => chunk.Reasoning)));
        Assert.Single(chunks, chunk => chunk.ResponseEnvelope is not null);
        Assert.Single(chunks, chunk => chunk.Output is not null);
        Assert.DoesNotContain("opaque", JsonSerializer.Serialize(chunks), StringComparison.Ordinal);
    }

    /// <summary>Final-only text and delta suffixes share the same multiline SSE path.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task FinalText_ReconcilesWithoutDuplicationAndSupportsMultilineFrames(bool delta)
    {
        var prefix = delta ? "event: response.output_text.delta\r\ndata: {\"type\":\"response.output_text.delta\",\r\ndata: \"output_index\":0,\"content_index\":0,\"delta\":\"hel\"}\r\n\r\n" : string.Empty;
        using var handler = new Handler(prefix + Completed(OutputMessage("hello")) + Frame("[DONE]"));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();
        await CollectAsync(Provider(client, Model()), Request(), chunks);
        Assert.Equal("hello", string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.Single(chunks, chunk => chunk.Usage is not null);
        Assert.Single(chunks, chunk => chunk.FinishReason is not null);
    }

    /// <summary>Ordinary output exhaustion preserves final-only text and streamed suffixes, even with tools advertised.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public static async Task LengthLimitedText_ReconcilesAndFinishesWithLength(bool delta, bool tools, bool reportedUsage)
    {
        var message = OutputMessage("partial answer");
        message["status"] = "incomplete";
        var report = reportedUsage ? new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 1000 } : null;

        var response = new JsonObject
        {
            ["status"] = "incomplete",
            ["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" },
            ["output"] = new JsonArray(message),
            ["usage"] = report,
        };
        var prefix = delta ? Frame("""{"type":"response.output_text.delta","output_index":0,"content_index":0,"delta":"partial"}""") : string.Empty;
        using var handler = new Handler(prefix + Frame(new JsonObject { ["type"] = "response.incomplete", ["response"] = response }.ToJsonString()) + Frame("[DONE]"));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();

        await CollectAsync(Provider(client, ReplayModel()), Request() with { Tools = tools ? Request().Tools : [], MaximumOutputTokens = 1000 }, chunks);

        Assert.Equal("partial answer", string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.Equal(ModelFinishReason.Length, Assert.Single(chunks, chunk => chunk.FinishReason is not null).FinishReason);
        var usage = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage!;
        Assert.Equal(!reportedUsage, usage.IsEstimate);
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.ResponseEnvelope is not null);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Orphaned XML uses the same sanitized corrective diagnostic as the inherited adapter.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task OrphanedArguments_UseCorrectableDiagnosticWithoutReleasingBatch(bool delta)
    {
        const string orphaned = "<parameter name=\"value\">private-argument</parameter></function></tool_call>";
        var prefix = delta ? Frame(new JsonObject { ["type"] = "response.output_text.delta", ["output_index"] = 0, ["content_index"] = 0, ["delta"] = orphaned }.ToJsonString()) : string.Empty;
        using var handler = new Handler(prefix + Completed(OutputMessage(orphaned), FunctionCall("call"), FunctionCall("sibling")));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();

        var error = await Assert.ThrowsAsync<MalformedInvocationException>(() => CollectAsync(Provider(client, ReplayModel()), Request(), chunks));

        Assert.Equal(MalformedInvocationFailureKind.ArgumentSchemaMismatch, error.Diagnostic.Kind);
        Assert.Equal("openrouter", error.Diagnostic.ProviderFamily);
        Assert.Equal(2, error.Diagnostic.ToolCallCount);
        Assert.DoesNotContain("private-argument", JsonSerializer.Serialize(error.Diagnostic), StringComparison.Ordinal);
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.ResponseEnvelope is not null || chunk.FinishReason is not null || chunk.Text is not null);
        Assert.Single(chunks, chunk => chunk.Usage is not null);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Public stream-limit overrides are independent of private replay bounds.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(10000000)]
    public static async Task StreamLimit_ZeroAndLargerOverridesAllowLargeText(long limit)
    {
        var text = new string('x', (8 * 1024 * 1024) + 1);
        using var handler = new Handler(Completed(OutputMessage(text)));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();
        await CollectAsync(Provider(client, Model() with { MaximumStreamedBytes = limit }), Request(), chunks);
        Assert.Equal(text.Length, chunks.Sum(chunk => chunk.Text?.Length ?? 0));
    }

    /// <summary>Final-only argument suffixes count against the configured byte bound.</summary>
    [Fact]
    public static async Task FinalArgumentSuffix_CannotBypassOutputBound()
    {
        var call = FunctionCall("call");
        call["arguments"] = JsonSerializer.Serialize(new { value = new string('x', 2000) });
        using var handler = new Handler(Frame("""{"type":"response.function_call_arguments.delta","output_index":0,"delta":"{"}""") + Completed(call));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();
        await Assert.ThrowsAsync<MalformedModelOutputException>(() => CollectAsync(Provider(client, Model() with { MaximumStreamedBytes = 1024 }), Request(), chunks));
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null);
    }

    /// <summary>Visible restored history uses a valid Responses assistant message.</summary>
    [Fact]
    public static async Task RestoredAssistantHistory_HasDeterministicRequiredFields()
    {
        using var handler = new Handler(Completed());
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var request = Request() with { Messages = [Message(ModelMessageRole.User, "one"), Message(ModelMessageRole.Assistant, "answer"), Message(ModelMessageRole.User, "two")] };
        await CollectAsync(Provider(client, Model()), request, []);
        using var json = JsonDocument.Parse(handler.Body!);
        var assistant = json.RootElement.GetProperty("input")[1];
        Assert.Equal("message", assistant.GetProperty("type").GetString());
        Assert.Equal("completed", assistant.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(assistant.GetProperty("id").GetString()));
        Assert.Equal("output_text", assistant.GetProperty("content")[0].GetProperty("type").GetString());
    }

    /// <summary>The actual transient owner binds host IDs back to wire call IDs across two rounds.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task TwoToolRounds_PreserveOriginalIdsAndOrderedPrivateItems(bool fallback)
    {
        var model = fallback ? ReplayModel() with
        {
            Upstreams = [new() { Provider = "azure", ExpectedProviderName = "Azure" }, new() { Provider = "openai", ExpectedProviderName = "OpenAI" }],
        } : ReplayModel();
        using var state = new ModelRequestTransientState();
        using var handler = new Handler(Completed(ReasoningItem("opaque-0"), FunctionCall("wire-0")).Replace("\"provider\":null", fallback ? "\"provider\":\"Azure\"" : "\"provider\":null", StringComparison.Ordinal), Completed(ReasoningItem("opaque-1"), FunctionCall("wire-1")).Replace("\"provider\":null", fallback ? "\"provider\":\"OpenAI\"" : "\"provider\":null", StringComparison.Ordinal), Completed(OutputMessage("done")));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var provider = Provider(client, model);
        var request = Request() with { ResolvedProfileId = model.Id, TransientState = state, IncludeReasoningText = false, Messages = [Message(ModelMessageRole.User, "test")] };
        for (var round = 0; round < 2; round++)
        {
            await foreach (var chunk in provider.StreamAsync(request, TestContext.Current.CancellationToken))
            {
                if (chunk.ResponseEnvelope is { } envelope)
                {
                    state.Accept(request, envelope);
                }
            }

            var hostId = "host-" + round;
            state.BindToolCall(round, 0, hostId);
            ModelMessage[] history = [.. request.Messages,
                Message(ModelMessageRole.Assistant, "{}") with { ToolCallId = hostId, ToolName = "inspect", ModelRound = round },
                Message(ModelMessageRole.Tool, "result") with { ToolCallId = hostId, ModelRound = round }];
            state.SealRound(round, history);
            request = request with { Messages = history, ToolContinuationRound = round + 1 };
        }

        // Admission and dispatch may project the same retained history repeatedly.
        var registration = new OpenRouterProviderRegistration();
        var profile = Assert.Single(registration.CreateProfiles(Configuration(model)));
        var envelopes = state.Responses.ToArray();
        var prepared = OpenRouterResponsesProjection.Create(request, profile, model);
        Assert.Equal(prepared, OpenRouterResponsesProjection.Create(request, profile, model));

        var chunks = new List<ModelChunk>();
        await CollectAsync(provider, request, chunks);
        for (var index = 0; index < envelopes.Length; index++)
        {
            Assert.Same(envelopes[index], state.Responses[index]);
        }

        Assert.Equal(prepared, OpenRouterResponsesProjection.Create(request, profile, model));

        using var projected = JsonDocument.Parse(prepared);
        using var dispatched = JsonDocument.Parse(handler.Bodies[2]);
        Assert.True(JsonElement.DeepEquals(projected.RootElement, dispatched.RootElement));
        Assert.Equal("done", string.Concat(chunks.Select(chunk => chunk.Text)));
        using var second = JsonDocument.Parse(handler.Bodies[1]);
        using var third = JsonDocument.Parse(handler.Bodies[2]);
        var input = third.RootElement.GetProperty("input");
        Assert.Equal(7, input.GetArrayLength());
        Assert.Equal("opaque-0", input[1].GetProperty("encrypted_content").GetString());
        Assert.Equal("wire-0", input[2].GetProperty("call_id").GetString());
        Assert.Equal("wire-0", input[3].GetProperty("call_id").GetString());
        Assert.Equal("opaque-1", input[4].GetProperty("encrypted_content").GetString());
        Assert.Equal("wire-1", input[5].GetProperty("call_id").GetString());
        Assert.Equal("wire-1", input[6].GetProperty("call_id").GetString());
        Assert.Equal(4, second.RootElement.GetProperty("input").GetArrayLength());
    }

    /// <summary>Refusals, conflicting completion and malformed invocations release no tools or replay.</summary>
    [Theory]
    [InlineData("refusal")]
    [InlineData("duplicate-call")]
    [InlineData("arguments")]
    [InlineData("conflicting-terminal")]
    [InlineData("argument-delta")]
    [InlineData("orphaned-xml")]
    public static async Task UnsafeCompletion_ReleasesNoBatch(string scenario)
    {
        var call = FunctionCall("call");
        if (scenario == "arguments")
        {
            call["arguments"] = "{";
        }

        var message = OutputMessage("<parameter name=\"value\">x</parameter></function></tool_call>");
        if (scenario == "refusal")
        {
            message["content"] = new JsonArray(new JsonObject { ["type"] = "refusal", ["refusal"] = "no" });
        }

        var body = scenario switch
        {
            "refusal" or "orphaned-xml" => Completed(message, call),
            "duplicate-call" => Completed(call, FunctionCall("call")),
            "conflicting-terminal" => Completed(call) + Completed(OutputMessage("other")),
            "argument-delta" => Frame("""{"type":"response.function_call_arguments.delta","output_index":0,"delta":"["}""") + Completed(call),
            _ => Completed(call),
        };
        using var handler = new Handler(body);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();
        if (scenario == "refusal")
        {
            await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(Provider(client, Model()), Request(), chunks));
        }
        else
        {
            await Assert.ThrowsAnyAsync<MalformedModelOutputException>(() => CollectAsync(Provider(client, Model()), Request(), chunks));
        }

        Assert.Contains(chunks, chunk => chunk.Usage is not null);
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.ResponseEnvelope is not null || chunk.FinishReason is not null);
    }

    /// <summary>Refusal signals remain private while terminal accounting is delivered without answer or tool release.</summary>
    [Theory]
    [InlineData("response.refusal.delta", true)]
    [InlineData("response.refusal.done", true)]
    [InlineData("response.output_item.done", true)]
    [InlineData("terminal", true)]
    [InlineData("response.refusal.delta", false)]
    public static async Task Refusal_PreservesTerminalUsageAndWithholdsOutput(string signal, bool reportedUsage)
    {
        // Arrange: include valid siblings and later text to catch premature answer or tool release.
        var refusal = OutputMessage(string.Empty);
        refusal["content"] = new JsonArray(new JsonObject { ["type"] = "refusal", ["refusal"] = "private-refusal" });
        var prefix = signal switch
        {
            "terminal" => string.Empty,
            "response.output_item.done" => Frame(new JsonObject { ["type"] = signal, ["output_index"] = 2, ["item"] = refusal.DeepClone() }.ToJsonString()),
            _ => Frame(new JsonObject { ["type"] = signal, ["output_index"] = 2, ["content_index"] = 0, ["delta"] = "private-refusal", ["refusal"] = "private-refusal" }.ToJsonString()),
        };
        var textDelta = signal == "terminal" ? string.Empty : Frame("""{"type":"response.output_text.delta","output_index":0,"content_index":0,"delta":"withheld"}""");
        using var handler = new Handler(prefix + textDelta + Terminal("response.completed", "completed", new JsonArray(OutputMessage("withheld answer"), FunctionCall("call"), refusal), reportedUsage ? Usage() : null) + Frame("[DONE]"));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();

        // Act
        var error = await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(Provider(client, ReplayModel()), Request(), chunks));

        // Assert
        var usage = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage!;
        Assert.Equal(!reportedUsage, usage.IsEstimate);
        Assert.Equal(reportedUsage ? 0.2m : null, usage.ReportedCost);
        if (reportedUsage)
        {
            Assert.Equal(10, usage.InputTokens);
            Assert.Equal(8, usage.OutputTokens);
            Assert.Equal(2, usage.Cache?.CacheReadTokens);
            Assert.Equal(3, usage.Cache?.CacheWriteTokens);
        }

        Assert.Equal("OpenRouter refused the response.", error.Message);
        Assert.DoesNotContain("private-refusal", JsonSerializer.Serialize(chunks), StringComparison.Ordinal);
        Assert.DoesNotContain(chunks, chunk => chunk.Text is not null || chunk.Reasoning is not null || chunk.Output is not null || chunk.ResponseEnvelope is not null || chunk.FinishReason is not null);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Both data and ignored lines remain bounded before any terminal output can be released.</summary>
    [Theory]
    [InlineData("data:")]
    [InlineData(":")]
    public static async Task SseFraming_RejectsOversizedLines(string field)
    {
        using var handler = new Handler(field + new string('x', (int)ModelProfile.DefaultMaximumStreamedBytes) + "\n\n");
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();

        var error = await Assert.ThrowsAsync<MalformedModelOutputException>(() => CollectAsync(Provider(client, Model() with { MaximumStreamedBytes = 1 }), Request(), chunks));

        Assert.Equal("OpenRouter exceeded the SSE frame limit.", error.Message);
        Assert.Empty(chunks);
    }

    /// <summary>Reported output limits apply even when unrelated input usage needs estimation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ReportedOutputOverrun_ReleasesUsageButNoTools(bool reportedInput)
    {
        var report = new JsonObject { ["output_tokens"] = 1001 };
        if (reportedInput)
        {
            report["input_tokens"] = 10;
        }

        using var handler = new Handler(Terminal("response.completed", "completed", new JsonArray(FunctionCall("call")), report));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();
        await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(Provider(client, ReplayModel()), Request() with { MaximumOutputTokens = 1000 }, chunks));
        var usage = Assert.Single(chunks, chunk => chunk.Usage is not null).Usage!;
        Assert.Equal(1001, usage.OutputTokens);
        Assert.Equal(!reportedInput, usage.IsEstimate);
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.ResponseEnvelope is not null || chunk.FinishReason is not null);
    }

    /// <summary>Output exhaustion retains targeted corrective guidance and rejects the entire tool batch.</summary>
    [Theory]
    [InlineData("response.incomplete", "incomplete", true)]
    [InlineData("response.updated", "incomplete", true)]
    [InlineData("response.completed", "completed", true)]
    [InlineData("response.completed", "completed", false)]
    public static async Task OutputExhaustion_UsesCorrectableDiagnosticsWithoutReleasingBatch(string type, string status, bool exhausted)
    {
        var invalid = FunctionCall("rejected");
        invalid["arguments"] = "{\"secret\":";
        var report = Usage();
        report["output_tokens"] = exhausted ? 1000 : 999;
        var response = new JsonObject
        {
            ["status"] = status,
            ["output"] = new JsonArray(FunctionCall("valid-sibling"), invalid),
            ["usage"] = report,
            ["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" },
        };
        using var handler = new Handler(Frame(new JsonObject { ["type"] = type, ["response"] = response }.ToJsonString()));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();

        var error = await Assert.ThrowsAsync<MalformedInvocationException>(() => CollectAsync(Provider(client, ReplayModel()), Request() with { MaximumOutputTokens = 1000 }, chunks));

        Assert.Equal(exhausted ? MalformedInvocationFailureKind.TruncatedArguments : MalformedInvocationFailureKind.InvalidJsonArguments, error.Diagnostic.Kind);
        Assert.Equal("openrouter", error.Diagnostic.ProviderFamily);
        Assert.Equal(2, error.Diagnostic.ToolCallCount);
        if (exhausted)
        {
            Assert.Equal(ModelOutputValidator.TruncatedInvocationMessage, error.Message);
        }

        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        Assert.Single(chunks, chunk => chunk.Usage is not null);
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.ResponseEnvelope is not null || chunk.FinishReason is not null);
        Assert.Equal(1, handler.Calls);
    }

    /// <summary>Response status is authoritative even when the upstream event label is not an error.</summary>
    [Theory]
    [InlineData("response.updated", "failed")]
    [InlineData("response.updated", "incomplete")]
    [InlineData("response.completed", "failed")]
    [InlineData("response.completed", "incomplete")]
    public static async Task UnsuccessfulStatus_CannotHideBehindNonErrorEventType(string type, string status)
    {
        using var handler = new Handler(Terminal(type, status, new JsonArray(FunctionCall("rejected")), Usage()));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();

        await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(Provider(client, Model()), Request(), chunks));

        Assert.Single(chunks, chunk => chunk.Usage is not null);
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.ResponseEnvelope is not null || chunk.FinishReason is not null);
    }

    /// <summary>Earlier tool metadata identifies truncated invocations even without terminal output items.</summary>
    [Theory]
    [InlineData("response.output_item.added")]
    [InlineData("response.output_item.done")]
    [InlineData("response.function_call_arguments.delta")]
    public static async Task Exhaustion_RecognizesPreviouslyStreamedCallsWhenTerminalOmitsOutput(string eventType)
    {
        var response = new JsonObject { ["status"] = "incomplete", ["incomplete_details"] = new JsonObject { ["reason"] = "max_output_tokens" }, ["usage"] = Usage() };
        var prefix = new JsonObject { ["type"] = eventType, ["output_index"] = 0, ["item"] = FunctionCall("rejected"), ["delta"] = "{" };
        using var handler = new Handler(Frame(prefix.ToJsonString()) + Frame(new JsonObject { ["type"] = "response.incomplete", ["response"] = response }.ToJsonString()));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();

        var error = await Assert.ThrowsAsync<MalformedInvocationException>(() => CollectAsync(Provider(client, Model()), Request(), chunks));

        Assert.Equal(MalformedInvocationFailureKind.TruncatedArguments, error.Diagnostic.Kind);
        Assert.Equal(1, error.Diagnostic.ToolCallCount);
        Assert.DoesNotContain(chunks, chunk => chunk.Output is not null || chunk.ResponseEnvelope is not null);
    }

    /// <summary>Only an interruption without protected tool fragments may release a replacement batch.</summary>
    [Theory]
    [InlineData("eof", false, true)]
    [InlineData("done", true, true)]
    [InlineData("eof", true, true)]
    [InlineData("unavailable", true, true)]
    [InlineData("eof", true, false)]
    [InlineData("completed arguments", true, true)]
    public static async Task ResponsesInterruptionStopsRetryAfterProtectedToolFragmentsAsync(string interruption, bool hasFragment, bool stopOnUnconfirmedTools)
    {
        const string replacementArguments = """{"optional":"replacement"}""";
        var replacement = FunctionCall("replacement");
        replacement["arguments"] = replacementArguments;
        var suffix = interruption switch
        {
            "eof" or "completed arguments" => string.Empty,
            "done" => Frame("[DONE]"),
            "unavailable" => Failed(503),
            _ => throw new ArgumentOutOfRangeException(nameof(interruption)),
        };
        var prefix = interruption == "completed arguments"
            ? Frame("""{"type":"response.function_call_arguments.done","output_index":0,"arguments":"{}"}""")
            : hasFragment ? Frame("""{"type":"response.function_call_arguments.delta","output_index":0,"delta":"{"}""") : string.Empty;
        using var handler = new Handler(prefix + suffix, Completed(replacement));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var provider = Provider(client, Model() with { RetryMaxAttempts = 2, RetryDelayMilliseconds = 0 });
        var request = Request() with { StopOnUnconfirmedToolStream = stopOnUnconfirmedTools };
        var chunks = new List<ModelChunk>();

        if (hasFragment && stopOnUnconfirmedTools)
        {
            var error = await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(provider, request, chunks));

            Assert.Contains("unconfirmed tool calls", error.Message, StringComparison.Ordinal);
            Assert.Equal(1, handler.Calls);
            Assert.Empty(chunks);
            return;
        }

        await CollectAsync(provider, request, chunks);
        Assert.Equal(2, handler.Calls);
        var tool = Assert.Single(chunks.Select(chunk => chunk.Output).OfType<ToolRequestModelOutput>());
        Assert.Equal(replacementArguments, tool.ArgumentsJson);
        Assert.Equal(ModelFinishReason.ToolCalls, chunks[^1].FinishReason);
    }

    private static OpenRouterModelConfiguration ReplayModel() => Model() with
    {
        ReasoningControl = ReasoningControllability.AlwaysOn,
        SupportsReasoningReplay = true,
        Upstreams = [new() { Provider = "fixture", ExpectedProviderName = "Fixture Provider" }],
    };

    private static ModelMessage Message(ModelMessageRole role, string text) => new() { Role = role, SectionId = "fixture", Content = [new() { Content = text }] };

    private static JsonObject OutputMessage(string text) => new()
    {
        ["type"] = "message",
        ["id"] = "message",
        ["status"] = "completed",
        ["role"] = "assistant",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = text, ["annotations"] = new JsonArray() }),
    };

    private static async Task CollectAsync(IModelProvider provider, ModelStreamRequest request, List<ModelChunk> chunks)
    {
        await foreach (var chunk in provider.StreamAsync(request, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }
    }
}
