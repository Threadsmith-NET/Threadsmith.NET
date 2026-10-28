namespace Threadsmith.Planning.Tests;

using System.Text.Json;
using System.Text.Json.Serialization;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Xunit;

/// <summary>Real provider serialization with explicit host replay ownership and changed authorities.</summary>
public static class ReasoningReplayBoundaryTests
{
    /// <summary>Late cumulative usage contributes to continuation admission before the correlated batch is released.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(5000, false)]
    [InlineData(5000, true)]
    public static async Task CompatibleUsageTrailer_FinalizesReplayAllowance(int? outputTokens, bool earlierUsage)
    {
        var response = ReasoningReplayFixture.Stream(false, 0, "lookup", "{}");
        using var baseline = new ReplaySession(false, response);
        await baseline.CompleteToolRoundAsync();
        var fallback = baseline.State.RetainedOutputTokens;
        if (outputTokens is { } reported)
        {
            var usage = "data: " + JsonSerializer.Serialize(new
            {
                choices = Array.Empty<object>(),
                usage = new { prompt_tokens = 100, completion_tokens = reported, completion_tokens_details = new { reasoning_tokens = reported / 2 } },
            }) + "\n\n";
            response = response.Replace("data: [DONE]", usage + "data: [DONE]", StringComparison.Ordinal);
            if (earlierUsage)
            {
                response = usage + response;
            }
        }

        using var fixture = new ReplaySession(false, response);
        var next = await fixture.CompleteToolRoundAsync();
        var expected = Math.Max(outputTokens ?? 0, fallback);
        Assert.Equal(expected, fixture.State.RetainedOutputTokens);
        var envelopeIndex = fixture.Chunks.FindIndex(chunk => chunk.ResponseEnvelope is not null);
        Assert.True(fixture.Chunks.FindLastIndex(chunk => chunk.Usage is not null) < envelopeIndex);
        Assert.True(envelopeIndex < fixture.Chunks.FindIndex(chunk => chunk.Output is ToolRequestModelOutput));
        Assert.Equal(ModelFinishReason.ToolCalls, fixture.Chunks[^1].FinishReason);

        var prepared = ModelRequestPreparation.Apply(fixture.Definition, next);
        Assert.Equal(next.WireEstimate!.TotalCapacityTokens + expected, prepared.WireEstimate!.TotalCapacityTokens);
        Assert.Equal(prepared.WireEstimate.WireInputTokens, ModelRequestAdmissionEstimator.Estimate(prepared).InputTokens);
        var tooSmall = fixture.Definition.Registration.CreateProvider(fixture.Context with
        {
            Profile = fixture.Context.Profile with { ContextWindow = (int)prepared.WireEstimate.TotalCapacityTokens - 1 },
        });
        await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(tooSmall, prepared));
        Assert.Single(fixture.Handler.Requests);
        await CollectAsync(fixture.Provider, prepared);
        ReasoningReplayFixture.AssertReplay(fixture.Handler.Requests[1], false, 1);
    }

    /// <summary>A rejected trailer cannot expose a partially finalized replay envelope or execute its tool batch.</summary>
    [Fact]
    public static async Task CompatibleInvalidUsageTrailer_DoesNotReleaseReplayOrTools()
    {
        var response = ReasoningReplayFixture.Stream(false, 0, "lookup", "{}")
            .Replace("data: [DONE]", "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":100,\"completion_tokens\":-1}}\n\ndata: [DONE]", StringComparison.Ordinal);
        using var fixture = new ReplaySession(false, response);
        await Assert.ThrowsAsync<MalformedModelOutputException>(() => fixture.CompleteToolRoundAsync());
        Assert.DoesNotContain(fixture.Chunks, chunk => chunk.ResponseEnvelope is not null || chunk.Output is not null || chunk.FinishReason is not null);
        Assert.False(fixture.State.HasResponses);
    }

    /// <summary>Configuration uses the exact supported wire field names and rejects other names.</summary>
    [Theory]
    [InlineData("reasoning", OpenAiReasoningReplayField.Reasoning)]
    [InlineData("reasoning_content", OpenAiReasoningReplayField.ReasoningContent)]
    public static void ReplayFieldConfiguration_UsesWireNames(string field, OpenAiReasoningReplayField expected)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        var config = JsonSerializer.Deserialize<OpenAiReasoningCompatibilityConfiguration>($"{{\"replayField\":\"{field}\"}}", options)!;
        Assert.Equal(expected, config.ReplayField);
        Assert.Contains($"\"replayField\":\"{field}\"", JsonSerializer.Serialize(config, options), StringComparison.Ordinal);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<OpenAiReasoningCompatibilityConfiguration>("{\"replayField\":\"arbitrary_field\"}", options));
    }

    /// <summary>Retained responses cannot silently change their replay protocol field.</summary>
    [Fact]
    public static async Task ReplayFieldChange_IsRejectedBeforeTransport()
    {
        using var fixture = new ReplaySession(false);
        var next = await fixture.CompleteToolRoundAsync();
        var model = (OpenAiCompatibleModelConfiguration)fixture.Context.ModelConfiguration;
        var provider = fixture.Definition.Registration.CreateProvider(fixture.Context with
        {
            ModelConfiguration = model with { ReasoningCompatibility = model.ReasoningCompatibility! with { ReplayField = OpenAiReasoningReplayField.Reasoning } },
        });
        await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(provider, next));
        Assert.Single(fixture.Handler.Requests);
    }

    /// <summary>Prepared admission includes replay exactly once and releases its allowance with the state.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task Preparation_AccountsForReplayBeforeAdmissionAndDoesNotAccumulate(bool codex)
    {
        using var fixture = new ReplaySession(codex);
        var next = await fixture.CompleteToolRoundAsync();
        var neutral = next.WireEstimate!;
        var prepared = ModelRequestPreparation.Apply(fixture.Definition, next);
        var repeated = ModelRequestPreparation.Apply(fixture.Definition, prepared);
        var retained = fixture.State.RetainedOutputTokens;

        Assert.True(retained > 0);
        Assert.Equal(neutral.TotalCapacityTokens + retained, prepared.WireEstimate!.TotalCapacityTokens);
        Assert.Equal(prepared.WireEstimate.TotalCapacityTokens, repeated.WireEstimate!.TotalCapacityTokens);
        Assert.Equal(prepared.Preparation!.WireDigest, repeated.Preparation!.WireDigest);
        Assert.Equal(prepared.WireEstimate.WireInputTokens, prepared.WireEstimate.Components.Sum(item => item.Tokens));
        Assert.Equal(prepared.WireEstimate.WireInputTokens, ModelRequestAdmissionEstimator.Estimate(prepared).InputTokens);
        var boundaryProfile = fixture.Context.Profile with { ContextWindow = (int)prepared.WireEstimate.TotalCapacityTokens };
        Assert.True(neutral.TotalCapacityTokens < boundaryProfile.ContextWindow);
        var tooSmall = fixture.Definition.Registration.CreateProvider(fixture.Context with { Profile = boundaryProfile with { ContextWindow = boundaryProfile.ContextWindow - 1 } });
        await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(tooSmall, prepared));
        Assert.Single(fixture.Handler.Requests);
        var provider = fixture.Definition.Registration.CreateProvider(fixture.Context with { Profile = boundaryProfile });
        await CollectAsync(provider, prepared);
        Assert.Equal(2, fixture.Handler.Requests.Count);

        fixture.State.Clear();
        var cleared = ModelRequestPreparation.Apply(fixture.Definition, prepared);
        Assert.Equal(neutral.WireInputTokens, cleared.WireEstimate!.WireInputTokens);
        Assert.DoesNotContain(cleared.WireEstimate.Components, item => item.Id == "retained-allowance");
    }

    /// <summary>Allowing appended corrections does not permit rewriting instructions preceding retained reasoning.</summary>
    [Theory]
    [InlineData(false, ModelMessageRole.System)]
    [InlineData(true, ModelMessageRole.System)]
    [InlineData(false, ModelMessageRole.Developer)]
    [InlineData(true, ModelMessageRole.Developer)]
    public static async Task ExistingInstructionChange_IsRejectedBeforeTransport(bool codex, ModelMessageRole role)
    {
        using var fixture = new ReplaySession(codex);
        var request = WithCapacity(fixture.Request with
        {
            Messages = [new() { Role = role, SectionId = "original-policy", Content = [new() { Content = "Original instruction." }] }, .. fixture.Request.Messages],
        });
        var next = await fixture.CompleteToolRoundAsync(request);
        next = WithCapacity(next with
        {
            Messages = [next.Messages[0] with { Content = [new() { Content = "Changed instruction." }] }, .. next.Messages.Skip(1)],
        });

        var error = await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(fixture.Provider, next));

        Assert.Contains("preceding history changed", error.Message, StringComparison.Ordinal);
        Assert.Single(fixture.Handler.Requests);
    }

    /// <summary>Buffering native items does not charge JSON framing against the existing output byte limit.</summary>
    [Fact]
    public static async Task CodexWithoutReasoning_PreservesExistingOutputByteAccounting()
    {
        using var fixture = new ReplaySession(true, ReasoningReplayFixture.Stream(true, 0, "lookup", "{}", reasoning: false));
        var provider = fixture.Definition.Registration.CreateProvider(fixture.Context with
        {
            Profile = fixture.Context.Profile with { MaximumStreamedBytes = 10 },
        });
        var chunks = await CollectAsync(provider, fixture.Request);
        Assert.Single(chunks, chunk => chunk.Output is ToolRequestModelOutput);
        Assert.DoesNotContain(chunks, chunk => chunk.ResponseEnvelope is not null);
    }

    /// <summary>Completed reasoning items remain exact even when encryption is absent.</summary>
    [Fact]
    public static async Task CodexReasoningWithoutEncryption_PreservesOriginalItem()
    {
        var response = ReasoningReplayFixture.Stream(true, 0, "lookup", "{}")
            .Replace(",\"encrypted_content\":\"opaque-0-state\"", string.Empty, StringComparison.Ordinal);
        using var fixture = new ReplaySession(true, response);
        var next = await fixture.CompleteToolRoundAsync();
        await CollectAsync(fixture.Provider, next);
        var reason = fixture.Handler.Requests[1].GetProperty("input").EnumerateArray()
            .Single(item => item.TryGetProperty("type", out var type) && type.GetString() == "reasoning");
        Assert.Equal("reason-0", reason.GetProperty("id").GetString());
        Assert.Equal("retained", reason.GetProperty("provider_extension").GetString());
        Assert.False(reason.TryGetProperty("encrypted_content", out _));
        Assert.Equal("visible summary", reason.GetProperty("summary")[0].GetProperty("text").GetString());
    }

    /// <summary>No retained state may cross a changed request, credential, model, or endpoint.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ChangedAuthority_IsRejectedBeforeTransport(bool codex)
    {
        foreach (var change in new[] { "model", "endpoint", "credential", "profile", "run", "generation", "history", "tools", "instructions", "protocol" })
        {
            using var fixture = new ReplaySession(codex);
            var next = await fixture.CompleteToolRoundAsync();
            var context = fixture.Context;
            switch (change)
            {
                case "model":
                    context = context with { Profile = context.Profile with { ModelId = "other" } };
                    break;
                case "endpoint":
                    context = context with { Profile = context.Profile with { Endpoint = new Uri("https://other.invalid/v1/") } };
                    break;
                case "credential":
                    context = context with { ResolvedSecret = "other-key" };
                    break;
                case "profile":
                    next = next with { ResolvedProfileId = ModelProfileId.New() };
                    break;
                case "run":
                    next = next with { RunId = RunId.New() };
                    break;
                case "generation":
                    next = next with { HistoryRewriteGeneration = 1 };
                    break;
                case "history":
                    next = next with { Messages = [next.Messages[0] with { Content = [new ModelContentPart { Content = "rewritten" }] }, .. next.Messages.Skip(1)] };
                    break;
                case "tools":
                    next = next with { Tools = [] };
                    break;
                case "instructions":
                    next = next with { ProviderInstructions = new() { SectionId = "policy", Content = "changed" } };
                    break;
                case "protocol":
                    var other = ReasoningReplayFixture.Catalog(!codex);
                    var definition = other.Get(other.DefaultModelId!.Value);
                    context = context with { Profile = definition.Profile with { Id = context.Profile.Id }, ProviderConfiguration = definition.ProviderConfiguration, ModelConfiguration = definition.ModelConfiguration };
                    var switched = definition.Registration.CreateProvider(context);
                    await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(switched, WithCapacity(next)));
                    Assert.Single(fixture.Handler.Requests);
                    continue;
            }

            var provider = fixture.Definition.Registration.CreateProvider(context);
            await Assert.ThrowsAsync<ModelProviderException>(() => CollectAsync(provider, WithCapacity(next)));
            Assert.Single(fixture.Handler.Requests);
        }
    }

    /// <summary>Compaction releases opaque bytes and subsequent normalized requests carry no stale fields.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ClearedHistory_DropsReplayAndCanContinue(bool codex)
    {
        using var fixture = new ReplaySession(codex);
        var next = await fixture.CompleteToolRoundAsync();
        var envelope = Assert.Single(fixture.State.Responses);
        Assert.Equal("{}", JsonSerializer.Serialize(envelope));
        Assert.DoesNotContain("opaque-", envelope.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(next), StringComparison.Ordinal);
        fixture.State.Clear();
        Assert.Equal(0, envelope.ByteCount);
        next = WithCapacity(next with { HistoryRewriteGeneration = 1, Messages = next.Messages.Select(message => message with { ModelRound = null }).ToArray() });
        await CollectAsync(fixture.Provider, next);
        var body = fixture.Handler.Requests[1].GetRawText();
        Assert.DoesNotContain("opaque-", body, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning_content", body, StringComparison.Ordinal);
    }

    /// <summary>Cancellation and abandoned enumeration cannot leave an unclaimed envelope usable.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task AbandonedStream_ReleasesUnclaimedEnvelope(bool codex)
    {
        using var fixture = new ReplaySession(codex);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        ModelResponseReplayEnvelope? retained = null;
        await using (var stream = fixture.Provider.StreamAsync(fixture.Request, cancellation.Token).GetAsyncEnumerator(cancellation.Token))
        {
            while (await stream.MoveNextAsync())
            {
                if (stream.Current.ResponseEnvelope is { } envelope)
                {
                    retained = envelope;
                    await cancellation.CancelAsync();
                    break;
                }
            }
        }

        Assert.NotNull(retained);
        Assert.Equal(0, retained.ByteCount);
        Assert.False(fixture.State.HasResponses);
    }

    /// <summary>Opt-out and native OpenAI profiles never send the compatible vendor extension.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task UnsupportedProfile_DoesNotRetainOrSendReasoning(bool nativeOpenAi)
    {
        using var fixture = new ReplaySession(false);
        var context = nativeOpenAi
            ? fixture.Context with { Profile = fixture.Context.Profile with { Provider = "openai" } }
            : fixture.Context with { ModelConfiguration = ((OpenAiCompatibleModelConfiguration)fixture.Context.ModelConfiguration) with { ReasoningCompatibility = new() { Mode = OpenAiReasoningControlMode.StandardEffort, SupportsReasoningReplay = false } } };
        var provider = fixture.Definition.Registration.CreateProvider(context);
        var chunks = await CollectAsync(provider, fixture.Request);
        Assert.DoesNotContain(chunks, chunk => chunk.ResponseEnvelope is not null);
        await CollectAsync(provider, fixture.Request with { ToolContinuationRound = 1 });
        Assert.All(fixture.Handler.Requests, body => Assert.DoesNotContain("reasoning_content", body.GetRawText(), StringComparison.Ordinal));
    }

    private static async Task<List<ModelChunk>> CollectAsync(IModelProvider provider, ModelStreamRequest request)
    {
        var chunks = new List<ModelChunk>();
        await foreach (var chunk in provider.StreamAsync(request, TestContext.Current.CancellationToken))
        {
            chunks.Add(chunk);
        }

        return chunks;
    }

    private static ModelStreamRequest WithCapacity(ModelStreamRequest request) => request with
    {
        WireEstimate = ModelWireEstimator.Estimate(request.Messages, request.Tools, request.ToolTransportMode, 0, 4096, request.ProviderInstructions),
    };

    private sealed class ReplaySession : IDisposable
    {
        private readonly HttpClient _client;

        internal ReplaySession(bool codex, string? response = null)
        {
            var catalog = ReasoningReplayFixture.Catalog(codex);
            Definition = catalog.Get(catalog.DefaultModelId!.Value);
            Handler = new([response ?? ReasoningReplayFixture.Stream(codex, 0, "lookup", "{}"), ReasoningReplayFixture.Final(codex)]) { FragmentResponses = true };
            _client = new HttpClient(Handler);
            Context = new() { HttpClient = _client, Profile = Definition.Profile, ProviderConfiguration = Definition.ProviderConfiguration, ModelConfiguration = Definition.ModelConfiguration, ResolvedSecret = "fixture-key" };
            Provider = Definition.Registration.CreateProvider(Context);
            Request = WithCapacity(new ModelStreamRequest
            {
                RunId = RunId.New(),
                Input = "hello",
                ResolvedProfileId = Definition.Profile.Id,
                MaximumOutputTokens = 4096,
                TransientState = State,
                ProviderInstructions = new() { SectionId = "policy", Content = "fixture instructions" },
                Messages = [new() { Role = ModelMessageRole.User, SectionId = "user", Content = [new() { Content = "hello" }] }],
                Tools = [new() { Name = "lookup", Description = "Lookup", ArgumentsJsonSchema = "{\"type\":\"object\",\"properties\":{}}" }],
            });
        }

        internal ConfiguredModelDefinition Definition { get; }

        internal ModelProviderActivationContext Context { get; }

        internal IModelProvider Provider { get; }

        internal ModelRequestTransientState State { get; } = new();

        internal ModelStreamRequest Request { get; }

        internal AnthropicConversationLoopTests.NativeHandler Handler { get; }

        internal List<ModelChunk> Chunks { get; } = [];

        public void Dispose()
        {
            State.Dispose();
            _client.Dispose();
        }

        internal async Task<ModelStreamRequest> CompleteToolRoundAsync(ModelStreamRequest? request = null)
        {
            request ??= Request;
            await foreach (var chunk in Provider.StreamAsync(request, TestContext.Current.CancellationToken))
            {
                Chunks.Add(chunk);
                if (chunk.ResponseEnvelope is { } envelope)
                {
                    State.Accept(request, envelope);
                }
            }

            Assert.Single(State.Responses);
            State.BindToolCall(0, 0, "host-call");
            var next = WithCapacity(request with
            {
                ToolContinuationRound = 1,
                Messages = [.. request.Messages,
                    new() { Role = ModelMessageRole.Assistant, SectionId = "call", ModelRound = 0, ToolCallId = "host-call", ToolName = "lookup", Content = [new() { Content = "{}" }] },
                    new() { Role = ModelMessageRole.Tool, SectionId = "result", ModelRound = 0, ToolCallId = "host-call", ToolName = "lookup", Content = [new() { Content = "found" }] }],
            });
            State.SealRound(0, next.Messages);
            return next;
        }
    }
}
