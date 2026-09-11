namespace Threadsmith.ParallelAgents.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Models.Anthropic;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class ModelExplorerAssignmentRunnerTests
{
    /// <summary>Real native requests preserve signed multi-call rounds through child progress and host tool execution.</summary>
    [Fact]
    public async Task RunAsync_NativeThinkingTools_PreservesSignedRoundsAndPrivateState()
    {
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Add(item);
            return Task.CompletedTask;
        });
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var tool = new InspectMetadataTool();
        var registry = new ToolRegistry([tool]);
        var configuration = new AnthropicProviderConfiguration
        {
            Id = "native-child",
            Name = "Native child fixture",
            SecretKeyReference = "secrets:fixture",
            Models =
            [
                new AnthropicModelConfiguration
                {
                    Id = ModelProfileId.New(),
                    Name = "Native child model",
                    ModelId = "claude-fixture",
                    ContextWindow = 200_000,
                    MaximumOutputTokens = 8192,
                    RequestOutputTokenReserve = 4096,
                    Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true, StructuredOutput = true },
                    SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
                    DefaultReasoningLevel = ReasoningLevel.Low,
                    SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.Low],
                    Compatibility = new AnthropicModelCompatibility
                    {
                        ModelId = "claude-fixture",
                        ThinkingMode = AnthropicThinkingMode.Adaptive,
                        SupportsReasoningOff = true,
                        AdaptiveEffortLevels = [ReasoningLevel.Low],
                        SupportsStrictSchemas = true,
                        Prices = new AnthropicModelPrices
                        {
                            InputPerMillionTokens = 2,
                            CacheWritePerMillionTokens = 2.5m,
                            CacheReadPerMillionTokens = 0.2m,
                            OutputPerMillionTokens = 10,
                        },
                    },
                },
            ],
        };
        var catalog = new EffectiveModelProviderCatalog(
            new ModelProviderCatalogConfiguration { Providers = [configuration] },
            new ModelProviderRegistry([new AnthropicProviderRegistration()]));
        var profile = Assert.Single(catalog.ModelCatalog.Profiles);
        using var handler = new NativeChildHandler(tool.Definition.Id);
        using var client = new HttpClient(handler);
        var provider = new ConfiguredModelProvider(client, catalog, (_, _) => Task.FromResult<string?>("fixture-key"));
        var assignment = CreateAssignment(profile.Id, [tool.Definition.Id]);
        assignment = assignment with { Policy = assignment.Policy with { ReasoningLevel = "low" } };
        var plan = CreatePlan(assignment);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, [tool.Definition.Id]),
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId));

        var outcome = await runner.RunAsync(plan, assignment);

        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Equal("Inspection complete.", outcome.Response);
        Assert.Equal(3, outcome.Usage.ToolCalls);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("summarized", request.GetProperty("thinking").GetProperty("display").GetString());
            Assert.All(request.GetProperty("messages").EnumerateArray(), message =>
                Assert.Contains(message.GetProperty("role").GetString(), new[] { "user", "assistant" }));
        });
        var secondRound = handler.Requests[1].GetProperty("messages").EnumerateArray().ToArray();
        var replay = Assert.Single(secondRound, message => message.GetProperty("role").GetString() == "assistant");
        var blocks = replay.GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal("private-child-signature-0", blocks[0].GetProperty("signature").GetString());
        Assert.Equal(new[] { "native_one", "native_two" }, blocks.Skip(1).Select(block => block.GetProperty("id").GetString()));
        var results = secondRound.Last().GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(new[] { "native_one", "native_two" }, results.Take(2).Select(block => block.GetProperty("tool_use_id").GetString()));
        Assert.All(results.Take(2), block => Assert.False(block.GetProperty("is_error").GetBoolean()));
        var lastRound = handler.Requests[2].GetProperty("messages").EnumerateArray()
            .Where(message => message.GetProperty("role").GetString() == "assistant").ToArray();
        Assert.Equal(2, lastRound.Length);
        Assert.True(JsonElement.DeepEquals(replay, lastRound[0]));
        Assert.Equal("private-child-signature-1", lastRound[1].GetProperty("content")[0].GetProperty("signature").GetString());
        Assert.DoesNotContain("private-child", JsonSerializer.Serialize(observed), StringComparison.Ordinal);
        Assert.DoesNotContain("private-child", JsonSerializer.Serialize(outcome), StringComparison.Ordinal);
    }

    private sealed class NativeChildHandler : HttpMessageHandler
    {
        private readonly string _toolName;

        public NativeChildHandler(string toolName)
        {
            _toolName = toolName;
        }

        public List<JsonElement> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(document.RootElement.Clone());
            var round = Requests.Count - 1;
            var stream = new StringBuilder(NativeEvent("message_start", new
            {
                type = "message_start",
                message = new { id = "child_message_" + round, type = "message", role = "assistant", model = "claude-fixture", content = Array.Empty<object>(), usage = new { input_tokens = 100, output_tokens = 0 } },
            }));
            if (round < 2)
            {
                stream.Append(NativeEvent("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = "private-child-summary", signature = "private-child-signature-" + round } }));
                stream.Append(NativeEvent("content_block_stop", new { type = "content_block_stop", index = 0 }));
                for (var ordinal = 0; ordinal < (round == 0 ? 2 : 1); ordinal++)
                {
                    var wireId = round == 1 ? "native_third" : ordinal == 0 ? "native_one" : "native_two";
                    stream.Append(NativeEvent("content_block_start", new { type = "content_block_start", index = ordinal + 1, content_block = new { type = "tool_use", id = wireId, name = _toolName, input = new { } } }));
                    stream.Append(NativeEvent("content_block_stop", new { type = "content_block_stop", index = ordinal + 1 }));
                }
            }
            else
            {
                stream.Append(NativeEvent("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "Inspection complete." } }));
                stream.Append(NativeEvent("content_block_stop", new { type = "content_block_stop", index = 0 }));
            }

            stream.Append(NativeEvent("message_delta", new { type = "message_delta", delta = new { stop_reason = round < 2 ? "tool_use" : "end_turn", stop_sequence = (string?)null }, usage = new { output_tokens = 25 } }));
            stream.Append(NativeEvent("message_stop", new { type = "message_stop" }));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream.ToString(), Encoding.UTF8, "text/event-stream") };
        }

        private static string NativeEvent(string type, object body) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(body) + "\n\n";
    }
}
