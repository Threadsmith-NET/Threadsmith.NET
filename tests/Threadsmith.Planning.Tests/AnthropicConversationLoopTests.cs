namespace Threadsmith.Planning.Tests;

using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Models.Anthropic;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Parent-loop continuations through the public native registration and actual SDK HTTP serialization.</summary>
public static class AnthropicConversationLoopTests
{
    /// <summary>Exercises canonical host requests against native protocol constraints.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task MixedPlanBatch_RejectsEveryOrdinalBeforeAnyToolEffects(bool validPlan, bool planLast)
    {
        var plan = ("wire_plan", "propose_plan", validPlan ? PlanJson("candidate") : "{}");
        var tool = ("wire_tool", "deterministic_output", "{\"sequence\":1}");
        await using var harness = new NativeLoopHarness(
            ToolStream(planLast ? [tool, plan] : [plan, tool]),
            TextStream("Batch corrected."));
        await harness.RunToCompletionAsync();

        Assert.Empty(harness.Observed.OfType<ToolInvocationStarted>());
        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.Equal(ModelCorrectionCategory.ToolBatch, Assert.Single(harness.Observed.OfType<ModelCorrectionAttempted>()).Category);
        AssertRejectedResults(harness.Handler.Requests[1], planLast ? ["wire_tool", "wire_plan"] : ["wire_plan", "wire_tool"]);
        AssertSignedReplay(harness.Handler.Requests[1]);
        Assert.All(harness.Model.Requests[1].Messages.Where(message => message.Role == ModelMessageRole.Tool), message => Assert.True(message.IsError));
    }

    /// <summary>Exercises canonical host requests against native protocol constraints.</summary>
    [Fact]
    public static async Task InvalidSinglePlan_UsesExistingBindingAndValidCorrectiveRequest()
    {
        await using var harness = new NativeLoopHarness(
            ToolStream([("wire_plan", "propose_plan", "{}")]),
            TextStream("Plan corrected."));
        await harness.RunToCompletionAsync();

        Assert.Empty(harness.Observed.OfType<ToolInvocationStarted>());
        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.Equal(ModelCorrectionCategory.PlanSchema, Assert.Single(harness.Observed.OfType<ModelCorrectionAttempted>()).Category);
        AssertRejectedResults(harness.Handler.Requests[1], ["wire_plan"]);
        AssertSignedReplay(harness.Handler.Requests[1]);
    }

    /// <summary>Exercises canonical host requests against native protocol constraints.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public static async Task StandaloneCorrection_IsChronologicalAndPassesNativeMapper(bool malformedInvocation, bool afterTool)
    {
        var rejected = malformedInvocation
            ? ToolStream([("wire_invalid", "deterministic_output", "{")])
            : TextStream(" ");
        var corrected = TextStream("Corrected response.");
        await using var harness = new NativeLoopHarness(afterTool
            ? [ToolStream([("wire_first", "deterministic_output", "{\"sequence\":1}")]), rejected, corrected]
            : [rejected, corrected]);
        await harness.RunToCompletionAsync();

        var correctionRound = afterTool ? 2 : 1;
        Assert.Equal(correctionRound + 1, harness.Handler.Requests.Count);
        var section = malformedInvocation ? "active-turn-correction:" : "active-turn-empty-response-correction:";
        var correction = Assert.Single(harness.Model.Requests[correctionRound].Messages, message => message.SectionId?.StartsWith(section, StringComparison.Ordinal) == true);
        Assert.Equal(ModelMessageRole.User, correction.Role);
        Assert.Equal(
            malformedInvocation ? ModelCorrectionCategory.ProviderInvocation : ModelCorrectionCategory.EmptyResponse,
            Assert.Single(harness.Observed.OfType<ModelCorrectionAttempted>()).Category);
        Assert.Equal(harness.Handler.Requests[0].GetProperty("system").GetRawText(), harness.Handler.Requests[correctionRound].GetProperty("system").GetRawText());
        if (afterTool)
        {
            AssertSignedReplay(harness.Handler.Requests[correctionRound]);
            Assert.Single(harness.Observed.OfType<ToolInvocationStarted>());
        }
    }

    /// <summary>Exercises canonical host requests against native protocol constraints.</summary>
    [Fact]
    public static async Task PlanSanityCorrection_IsUserMessageAfterTerminalReplayRelease()
    {
        await using var harness = new NativeLoopHarness(
            ToolStream([("wire_plan", "propose_plan", PlanJson("missing", "src/missing.cs"))]),
            TextStream("I will revise the missing path."));
        harness.CheckPlanSanity = true;
        await harness.RunToCompletionAsync();

        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.Equal(ModelCorrectionCategory.PlanSanity, Assert.Single(harness.Observed.OfType<ModelCorrectionAttempted>()).Category);
        var correction = Assert.Single(harness.Model.Requests[1].Messages, message => message.SectionId?.StartsWith("active-turn-plan-sanity-correction:", StringComparison.Ordinal) == true);
        Assert.Equal(ModelMessageRole.User, correction.Role);
        Assert.False(harness.Model.HadReplay[1]);
        Assert.DoesNotContain(harness.Model.Requests[1].Messages, message => message.ToolCallId is not null);
    }

    /// <summary>Exercises canonical host requests against native protocol constraints.</summary>
    [Fact]
    public static async Task ToolEvidenceThenSteering_PreservesFrozenPrefixAndSignedReplay()
    {
        await using var harness = new NativeLoopHarness(
            ToolStream([("wire_tool", "deterministic_output", "{\"sequence\":1}")]),
            TextStream("Steering accepted."));
        var paused = new TaskCompletionSource<RunSteeringPauseRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.OnEvent = domainEvent =>
        {
            if (domainEvent is ToolInvocationCompleted completed)
            {
                paused.TrySetResult(harness.Steering.RequestPause(completed.SessionId, harness.Model.Requests[0].RunId));
            }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = harness.RunToCompletionAsync();
        var pause = await paused.Task.WaitAsync(timeout.Token);
        var ready = await harness.Steering.WaitForPauseAsync(harness.SessionId, harness.RunId, pause.PauseId, timeout.Token);
        Assert.Equal(RunSteeringPauseWaitStatus.Ready, ready.Status);
        Assert.Equal(RunSteeringSubmissionStatus.Accepted, harness.Steering.Submit(harness.SessionId, harness.RunId, pause.PauseId, "Focus on the parser boundary.").Status);
        await running;

        Assert.Single(harness.Observed.OfType<ToolInvocationCompleted>());
        Assert.Equal(2, harness.Handler.Requests.Count);
        var first = harness.Model.Requests[0];
        var second = harness.Model.Requests[1];
        Assert.Equal(first.Messages, second.Messages.Take(first.Messages.Count));
        Assert.Equal(first.HistoryRewriteGeneration, second.HistoryRewriteGeneration);
        Assert.True(harness.Model.HadReplay[1]);
        AssertSignedReplay(harness.Handler.Requests[1]);
        var steeringIndex = second.Messages.ToList().FindIndex(message => message.SectionId == "run-user-steering");
        var resultIndex = second.Messages.ToList().FindIndex(message => message.Role == ModelMessageRole.Tool);
        Assert.True(steeringIndex > resultIndex && resultIndex >= 0);
        Assert.Equal(ModelMessageRole.User, second.Messages[steeringIndex].Role);
        Assert.False(second.Messages[resultIndex].IsError);
        Assert.Contains(harness.Observed.OfType<EvidenceAdded>(), item => item.Kind == EvidenceKind.ToolResult.ToString());
    }

    /// <summary>A corrected exclusive native plan releases its replay and enters governed review.</summary>
    [Fact]
    public static async Task CorrectedSinglePlan_CompletesReplayAndEntersReview()
    {
        await using var harness = new NativeLoopHarness(
            ToolStream([("wire_bad_plan", "propose_plan", "{}")]),
            ToolStream([("wire_good_plan", "propose_plan", PlanJson("repaired native plan"))]));
        harness.ExpectedPlanSummary = "repaired native plan";
        await harness.RunToCompletionAsync();

        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.Empty(harness.Observed.OfType<ToolInvocationStarted>());
        AssertRejectedResults(harness.Handler.Requests[1], ["wire_bad_plan"]);
        Assert.All(harness.Model.Requests, request => Assert.False(request.TransientState!.HasResponses));
    }

    private static void AssertRejectedResults(JsonElement request, string[] expectedIds)
    {
        var results = request.GetProperty("messages").EnumerateArray()
            .SelectMany(message => message.GetProperty("content").EnumerateArray())
            .Where(block => block.GetProperty("type").GetString() == "tool_result")
            .ToArray();
        Assert.Equal(expectedIds, results.Select(block => block.GetProperty("tool_use_id").GetString()));
        Assert.All(results, block => Assert.True(block.GetProperty("is_error").GetBoolean()));
    }

    private static void AssertSignedReplay(JsonElement request)
    {
        var assistant = Assert.Single(request.GetProperty("messages").EnumerateArray(), message => message.GetProperty("role").GetString() == "assistant");
        Assert.Equal("signed-native-loop", assistant.GetProperty("content")[0].GetProperty("signature").GetString());
        Assert.Equal("private native reasoning", assistant.GetProperty("content")[0].GetProperty("thinking").GetString());
    }

    private static string PlanJson(string summary, string path = "src/example.cs") => JsonSerializer.Serialize(new
    {
        schemaVersion = 2,
        revision = 1,
        summary,
        steps = new[]
        {
            new
            {
                stepId = Guid.NewGuid().ToString("D"),
                title = "Update file",
                description = "Apply the reviewed change.",
                fileIntents = new[] { new { kind = "Modify", path } },
                expectedOutcome = "Updated file.",
                validation = new[] { "Build succeeds." },
            },
        },
        risks = Array.Empty<string>(),
        outstandingQuestions = Array.Empty<string>(),
    });

    private static string Event(string type, object body) => "event: " + type + "\ndata: " + JsonSerializer.Serialize(body) + "\n\n";

    private static string Start() => Event("message_start", new
    {
        type = "message_start",
        message = new { id = "message_native_loop", type = "message", role = "assistant", model = "claude-opus-5", content = Array.Empty<object>(), usage = new { input_tokens = 100, output_tokens = 0 } },
    });

    private static string End(string reason) => Event("message_delta", new { type = "message_delta", delta = new { stop_reason = reason, stop_sequence = (string?)null }, usage = new { output_tokens = 50 } })
        + Event("message_stop", new { type = "message_stop" });

    private static string TextStream(string text) => Start()
        + Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = string.Empty } })
        + Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } })
        + Event("content_block_stop", new { type = "content_block_stop", index = 0 })
        + End("end_turn");

    private static string ToolStream((string Id, string Name, string Arguments)[] calls)
    {
        var stream = new StringBuilder(Start());
        stream.Append(Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = string.Empty, signature = string.Empty } }));
        stream.Append(Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "thinking_delta", thinking = "private native reasoning" } }));
        stream.Append(Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "signature_delta", signature = "signed-native-loop" } }));
        stream.Append(Event("content_block_stop", new { type = "content_block_stop", index = 0 }));
        var index = 1;
        foreach (var call in calls)
        {
            stream.Append(Event("content_block_start", new { type = "content_block_start", index, content_block = new { type = "tool_use", id = call.Id, name = call.Name, input = new { } } }));
            stream.Append(Event("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "input_json_delta", partial_json = call.Arguments } }));
            stream.Append(Event("content_block_stop", new { type = "content_block_stop", index }));
            index++;
        }

        return stream.Append(End("tool_use")).ToString();
    }

    private sealed class NativeLoopHarness : IAsyncDisposable
    {
        private readonly HttpClient _client;
        private readonly string _root;
        private readonly EffectiveModelProviderCatalog _catalog;

        internal NativeLoopHarness(params string[] responses)
        {
            _root = Path.Combine(Path.GetTempPath(), "threadsmith-native-loop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Handler = new NativeHandler(responses);
            _client = new HttpClient(Handler);
            var provider = AnthropicCatalogHydrator.Hydrate(
                new AnthropicProviderConfiguration { Id = "native", Name = "Native", SecretKeyReference = "secrets:models:native", Models = [] },
                [new AnthropicDiscoveredModel { ModelId = "claude-opus-5", DisplayName = "Native", MaximumInputTokens = 200000, MaximumOutputTokens = 32000 }],
                AnthropicReviewedModels.All);
            _catalog = new EffectiveModelProviderCatalog(
                new ModelProviderCatalogConfiguration { Providers = [provider], DefaultModelId = provider.Models[0].Id },
                new ModelProviderRegistry([new AnthropicProviderRegistration()]));
            Model = new CapturingProvider(new ConfiguredModelProvider(_client, _catalog, static (_, _) => Task.FromResult<string?>("fixture-native-key")));
            Evidence = new EvidenceStore(Events, new SecretOutputSanitizer());
        }

        internal DomainEventStream Events { get; } = new();

        internal EvidenceStore Evidence { get; }

        internal NativeHandler Handler { get; }

        internal CapturingProvider Model { get; }

        internal List<IDomainEvent> Observed { get; } = [];

        internal RunSteeringCoordinator Steering { get; } = new();

        internal Action<IDomainEvent>? OnEvent { get; set; }

        internal bool CheckPlanSanity { get; set; }

        internal string? ExpectedPlanSummary { get; set; }

        internal SessionId SessionId { get; private set; }

        internal RunId RunId { get; private set; }

        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            _client.Dispose();
            await Events.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }

        internal async Task RunToCompletionAsync()
        {
            await using var capture = Events.Subscribe((domainEvent, _) =>
            {
                Observed.Add(domainEvent);
                OnEvent?.Invoke(domainEvent);
                return Task.CompletedTask;
            });
            var sanitizer = new SecretOutputSanitizer();
            var registry = new ToolRegistry([new TestDeterministicOutputTool("Tool evidence collected.")]);
            var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), Events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, UnboundedBudget.Instance);
            var resolver = new ModelResolver(_catalog.ModelCatalog, new InMemoryModelPreferenceSnapshotProvider());
            var assembler = new ContextAssembler(Evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, Events, TestPromptLoader.Instance, modelResolver: resolver);
            var projections = new InMemoryProjectionStore();
            await using var project = Events.Subscribe(projections.ApplyAsync);
            var application = new SessionApplication(
                Events,
                Model,
                UnboundedBudget.Instance,
                sanitizer,
                NullLogger<SessionApplication>.Instance,
                pipeline,
                (_, _) => Task.FromResult(new ToolInvocationContext { RepositoryPath = _root, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model" }),
                assembler,
                Evidence,
                registry,
                _catalog.DefaultModelId,
                new ExecutionLimits { MaxModelRounds = 4, MaxCorrectiveTurns = 2 },
                planSanityChecker: CheckPlanSanity ? new PlanSanityChecker(TestPromptLoader.Instance) : null,
                planSanityRequestFactory: CheckPlanSanity ? (_, plan, _) => Task.FromResult<PlanSanityCheckRequest?>(new PlanSanityCheckRequest
                {
                    Plan = plan,
                    RepositoryRoot = _root,
                    Baseline = new WorkspaceBaseline(WorkspaceId.New(), _root, DateTimeOffset.UtcNow, [], TrustLevel: RepositoryTrustLevel.TrustedMutation),
                    TrustLevel = RepositoryTrustLevel.TrustedMutation,
                }) : null,
                steering: Steering,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            SessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("native loop integration"));
            RunId = await dispatcher.DispatchAsync(new SubmitRequestCommand(SessionId, "Inspect the repository and report."));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (ExpectedPlanSummary is not null)
            {
                SessionProjection? review;
                do
                {
                    review = await projections.GetAsync<SessionProjection>(new ProjectionKey("session", SessionId.Value.ToString("D")), timeout.Token);
                    if (review?.Phase is not (RunPhase.AwaitingPlanApproval or RunPhase.Failed))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
                    }
                }
                while (review?.Phase is not (RunPhase.AwaitingPlanApproval or RunPhase.Failed));

                Assert.Null(review.Error);
                Assert.Equal(ExpectedPlanSummary, review.Plan?.Plan.Summary);
                Assert.True(await dispatcher.DispatchAsync(new RejectPlanCommand(SessionId, RunId, "test complete"), timeout.Token));
                Assert.False(await dispatcher.DispatchAsync(new WaitForRunCommand(RunId), timeout.Token));
            }
            else
            {
                var succeeded = await dispatcher.DispatchAsync(new WaitForRunCommand(RunId), timeout.Token);
                var projection = await projections.GetAsync<SessionProjection>(new ProjectionKey("session", SessionId.Value.ToString("D")), timeout.Token);
                Assert.True(succeeded, projection?.Error);
            }
        }
    }

    private sealed class CapturingProvider : IModelProvider, IModelRequestPreparationResolver
    {
        private readonly ConfiguredModelProvider _inner;

        internal CapturingProvider(ConfiguredModelProvider inner) => _inner = inner;

        internal List<ModelStreamRequest> Requests { get; } = [];

        internal List<bool> HadReplay { get; } = [];

        public ModelStreamRequest Prepare(ModelStreamRequest request) => _inner.Prepare(request);

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            HadReplay.Add(request.TransientState?.HasResponses == true);
            await foreach (var chunk in _inner.StreamAsync(request, cancellationToken))
            {
                yield return chunk;
            }
        }
    }

    private sealed class NativeHandler : HttpMessageHandler
    {
        private readonly string[] _responses;

        internal NativeHandler(string[] responses) => _responses = responses;

        internal List<JsonElement> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(content);
            Requests.Add(json.RootElement.Clone());
            Assert.True(Requests.Count <= _responses.Length, "Unexpected native retry or extra model round.");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_responses[Requests.Count - 1], Encoding.UTF8, "text/event-stream") };
        }
    }
}
