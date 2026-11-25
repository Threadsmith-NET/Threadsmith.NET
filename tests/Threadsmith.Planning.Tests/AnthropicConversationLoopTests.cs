namespace Threadsmith.Planning.Tests;

using System.Collections.Concurrent;
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
    /// <summary>Native signed blocks survive an accepted prefix compaction without replaying completed tool effects.</summary>
    [Fact]
    public static async Task CompactionPreservesLatestSignedExchange()
    {
        await using var harness = new NativeLoopHarness(
            ToolStream([("first_tool", "deterministic_output", "{\"sequence\":1}")]),
            ToolStream([("second_tool", "deterministic_output", "{\"sequence\":2}")]),
            TextStream("Done."));
        harness.ToolEvidence = new string('e', 4096);
        harness.Compactor = new ReasoningReplayConversationTests.ReplayCompactor();
        harness.CompactionPolicy = new ActiveTurnCompactionPolicy { PressureTargetPercent = 1, RetainedRecentTokens = 1 };
        await harness.RunToCompletionAsync();
        Assert.Equal(1, harness.Model.Requests[2].HistoryRewriteGeneration);
        Assert.Equal(2, harness.Observed.OfType<ToolInvocationCompleted>().Count());
        var body = harness.Handler.Requests[2].GetRawText();
        Assert.DoesNotContain("first_tool", body, StringComparison.Ordinal);
        Assert.Contains("second_tool", body, StringComparison.Ordinal);
        var blocks = harness.Handler.Requests[2].GetProperty("messages").EnumerateArray()
            .SelectMany(message => message.GetProperty("content").EnumerateArray());
        var thinking = Assert.Single(blocks, block => block.GetProperty("type").GetString() == "thinking");
        Assert.Equal("signed-native-loop", thinking.GetProperty("signature").GetString());
        Assert.Equal("private native reasoning", thinking.GetProperty("thinking").GetString());
    }

    /// <summary>Compaction preserves the active goal and its host instructions, or the later cancellation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CompactionPreservesGoalStateAsHostContext(bool cancelGoal)
    {
        const string goal = "Inspect the old parser fixture.";
        await using var harness = new NativeLoopHarness(
            ToolStream([("first_tool", "deterministic_output", "{\"sequence\":1}")]),
            ToolStream([("second_tool", "deterministic_output", "{\"sequence\":2}")]),
            ToolStream([("third_tool", "deterministic_output", "{\"sequence\":3}")]),
            TextStream("All requested work is complete."));
        harness.Goal = goal;
        harness.ToolEvidence = new string('e', 4096);
        harness.Compactor = new ReasoningReplayConversationTests.ReplayCompactor();
        harness.CompactionPolicy = new ActiveTurnCompactionPolicy
        {
            PressureTargetPercent = 1,
            RetainedRecentTokens = 1,
            FailureBackoffRounds = 0,
        };

        await harness.RunToCompletionAsync(cancelGoalAfterFirstTool: cancelGoal);

        var initial = Assert.Single(harness.Model.Requests[0].Messages, message => message.SectionId == "session-goal");
        Assert.Equal(ModelMessageRole.User, initial.Role);
        Assert.Contains(goal, initial.GetModelVisibleContent(), StringComparison.Ordinal);
        var compacted = harness.Model.Requests[^1];
        Assert.True(compacted.HistoryRewriteGeneration > 0);
        var wireText = harness.Handler.Requests[^1].GetProperty("messages").EnumerateArray()
            .SelectMany(message => message.GetProperty("content").EnumerateArray())
            .Where(block => block.GetProperty("type").GetString() == "text")
            .Select(block => block.GetProperty("text").GetString())
            .ToArray();
        if (cancelGoal)
        {
            Assert.DoesNotContain(compacted.Messages, message => message.SectionId is "session-goal" or "session-goal-context");
            Assert.DoesNotContain(wireText, text => text?.Contains(goal, StringComparison.Ordinal) == true);
        }
        else
        {
            Assert.Equal(initial, Assert.Single(compacted.Messages, message => message.SectionId == "session-goal"));
            Assert.Single(wireText, text => text?.Contains(goal, StringComparison.Ordinal) == true);
        }

        var expectedSection = cancelGoal ? "session-goal-cleared" : "session-goal-context";
        var host = Assert.Single(compacted.Messages, message => message.SectionId == expectedSection);
        Assert.Equal(ModelMessageRole.HostContext, host.Role);
        var notice = Assert.Single(wireText, text => text?.Contains(host.GetModelVisibleContent(), StringComparison.Ordinal) == true);
        Assert.Contains(cancelGoal ? "\"goalActive\":false" : "\"goalActive\":true", notice, StringComparison.Ordinal);
        Assert.StartsWith("<threadsmith_host_context>\n", notice, StringComparison.Ordinal);
        Assert.EndsWith("\n</threadsmith_host_context>", notice, StringComparison.Ordinal);
    }

    /// <summary>Goal wording on the native wire comes from the resolved fallback's prompt view.</summary>
    [Fact]
    public static async Task GoalInstructionsUseResolvedFallbackBeforeDispatch()
    {
        var preferred = ModelProfileId.New();
        await using var harness = new NativeLoopHarness(TextStream("All requested work is complete."))
        {
            Goal = "Write a greeting.",
            InitialPreference = new SessionModelPreferences(preferred, ReasoningLevel.None),
            Prompts = new ProfileTaggedGoalPrompts(),
        };

        await harness.RunToCompletionAsync();

        var fallback = Assert.Single(harness.Observed.OfType<ModelFallbackSelected>());
        var request = Assert.Single(harness.Model.Requests);
        Assert.NotEqual(preferred, request.ResolvedProfileId);
        var host = Assert.Single(request.Messages, message => message.SectionId == "session-goal-context");
        Assert.Contains($"Goal wording for {request.ResolvedProfileId?.Value:D}", host.GetModelVisibleContent(), StringComparison.Ordinal);
        var body = Assert.Single(harness.Handler.Requests).GetRawText();
        Assert.Contains($"Goal wording for {request.ResolvedProfileId?.Value:D}", body, StringComparison.Ordinal);
        Assert.DoesNotContain($"Goal wording for {preferred.Value:D}", body, StringComparison.Ordinal);
        Assert.Equal(request.ResolvedProfileId, fallback.SelectedProfileId);
    }

    /// <summary>Steering that cancels a native pending batch retains its prose through replay without adding a second copy.</summary>
    [Fact]
    public static async Task SteeringBeforeTools_RetainsCommentaryOnce()
    {
        const string commentary = "Starting the inspection.";
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var harness = new NativeLoopHarness(
            ToolStream([("wire_cancelled", "deterministic_output", "{\"sequence\":1}")], commentary),
            TextStream("Steering accepted."));
        var paused = new TaskCompletionSource<RunSteeringPauseRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.OnEvent = domainEvent =>
        {
            if (domainEvent is ModelOutputObserved output && harness.Model.Requests.Count == 1)
            {
                paused.TrySetResult(harness.Steering.RequestPause(output.SessionId, harness.Model.Requests[0].RunId));
            }
        };
        var running = harness.RunToCompletionAsync();
        var pause = await paused.Task.WaitAsync(cancellationToken);
        var ready = await harness.Steering.WaitForPauseAsync(harness.SessionId, harness.RunId, pause.PauseId, cancellationToken);
        Assert.Equal(RunSteeringPauseWaitStatus.Ready, ready.Status);
        Assert.Equal(RunSteeringSubmissionStatus.Accepted, harness.Steering.Submit(harness.SessionId, harness.RunId, pause.PauseId, "Inspect the parser instead.").Status);
        await running;

        Assert.Empty(harness.Observed.OfType<ToolInvocationStarted>());
        var messages = harness.Model.Requests[1].Messages;
        Assert.Single(messages, message => message.GetModelVisibleContent() == commentary);
        Assert.Single(messages, message => message.SectionId == "run-user-steering");
        Assert.Contains(messages, message => message.IsError == true && message.GetModelVisibleContent().Contains("cancelledByUserSteering", StringComparison.Ordinal));
        AssertSignedReplay(harness.Handler.Requests[1]);
    }

    /// <summary>Goal-only steering retires queued work and preserves native replay when a replacement continues.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("Inspect the parser instead.")]
    public static async Task GoalChangeBeforeToolsDiscardsNativeBatchWithoutSteering(string? replacement)
    {
        const string commentary = "Starting the inspection.";
        const string callId = "wire_retired";
        var ct = TestContext.Current.CancellationToken;
        await using var harness = new NativeLoopHarness(
            ToolStream([(callId, "deterministic_output", "{\"sequence\":1}")], commentary),
            TextStream("All requested work is complete.")) { Goal = "Inspect the repository." };
        var paused = new TaskCompletionSource<RunSteeringPauseRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.OnEvent = item =>
        {
            if (item is ModelOutputObserved output && harness.Model.Requests.Count == 1)
            {
                paused.TrySetResult(harness.Steering.RequestPause(output.SessionId, harness.Model.Requests[0].RunId));
            }
        };

        await harness.RunToCompletionAsync(beforeWait: async (dispatcher, token) =>
        {
            var pause = await paused.Task.WaitAsync(token);
            var ready = await harness.Steering.WaitForPauseAsync(harness.SessionId, harness.RunId, pause.PauseId, token);
            Assert.Equal(RunSteeringPauseWaitStatus.Ready, ready.Status);
            try
            {
                var action = replacement is null ? SessionGoalAction.Cancel : SessionGoalAction.Set;
                await dispatcher.DispatchAsync(new SessionGoalCommand(harness.SessionId, action, replacement), token);
            }
            finally
            {
                harness.Steering.Submit(harness.SessionId, harness.RunId, pause.PauseId, null);
            }
        });
        ct.ThrowIfCancellationRequested();
        Assert.Empty(harness.Observed.OfType<ToolInvocationStarted>());
        Assert.Empty(harness.ExecutedSequences);
        if (replacement is null)
        {
            Assert.Single(harness.Handler.Requests);
            return;
        }

        var replay = Assert.Single(harness.Model.Requests.Skip(1));
        Assert.Single(replay.Messages, message => message.GetModelVisibleContent() == commentary);
        Assert.DoesNotContain(replay.Messages, message => message.SectionId == "run-user-steering");
        Assert.Contains(replay.Messages, message => message.SectionId == "session-goal" && message.GetModelVisibleContent().Contains(replacement, StringComparison.Ordinal));
        var result = Assert.Single(replay.Messages, message => message.Role == ModelMessageRole.Tool);
        Assert.True(result.IsError);
        Assert.Contains("cancelledByGoalChange", result.GetModelVisibleContent(), StringComparison.Ordinal);
        AssertRejectedResults(harness.Handler.Requests[1], [callId]);
        AssertSignedReplay(harness.Handler.Requests[1]);
    }

    /// <summary>Canonical commentary participates in native history binding but is emitted only once on the wire.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task ToolCommentary_IsRetainedAndReplayedExactlyOnce(bool rejectedCall)
    {
        const string commentary = "I have acknowledged the guidance and will inspect the parser.";
        await using var harness = new NativeLoopHarness(
            ToolStream([("wire_commentary", "deterministic_output", rejectedCall ? "{}" : "{\"sequence\":1}")], commentary),
            TextStream("Done."));

        await harness.RunToCompletionAsync();

        var retained = Assert.Single(harness.Model.Requests[1].Messages, message => message.GetModelVisibleContent() == commentary);
        Assert.Equal(ModelMessageRole.Assistant, retained.Role);
        Assert.Equal(0, retained.ModelRound);
        Assert.True(harness.Model.HadReplay[1]);
        var assistant = Assert.Single(harness.Handler.Requests[1].GetProperty("messages").EnumerateArray(), message => message.GetProperty("role").GetString() == "assistant");
        var text = Assert.Single(assistant.GetProperty("content").EnumerateArray(), block => block.GetProperty("type").GetString() == "text");
        Assert.Equal(commentary, text.GetProperty("text").GetString());
        AssertSignedReplay(harness.Handler.Requests[1]);
    }

    /// <summary>Final preparation, submission, header accounting and inspection agree after multiple real tool rounds.</summary>
    [Fact]
    public static async Task ContextMapTracksFinalNativeContinuationAndKeepsPrivateReplaySizeOnly()
    {
        await using var harness = new NativeLoopHarness(
            ToolStream([("first_tool", "deterministic_output", "{\"sequence\":1}")]),
            ToolStream([("second_tool", "deterministic_output", "{\"sequence\":2}")]),
            TextStream("Done."));
        await harness.RunToCompletionAsync();

        Assert.Equal(3, harness.Model.Requests.Count);
        var status = harness.Usage.GetRequestStatus(harness.SessionId);
        var snapshot = Assert.IsType<ContextUsageSnapshot>(status?.ContextUsage);
        Assert.True(snapshot.DispatchStarted);
        Assert.Equal(2, snapshot.Round);
        Assert.Equal(harness.RunId, snapshot.RunId);
        Assert.Equal<long?>(harness.Model.Requests[^1].WireEstimate?.WireInputTokens, snapshot.InputTokens);
        Assert.Equal(snapshot.InputTokens, status.ContextTokens);
        Assert.Equal(snapshot.InputTokens, snapshot.Components.Sum(item => item.Tokens));
        Assert.True(snapshot.InputTokens > harness.Model.Requests[0].WireEstimate?.WireInputTokens);
        Assert.Contains(snapshot.Components, item => item.Category == "System prompt" && item.Tokens > 0);
        Assert.Contains(snapshot.Components, item => item.Category == "Tools" && item.Label == "deterministic_output");
        Assert.Equal(100, Assert.Single(snapshot.Components, item => item.Id == "retained-allowance").Tokens);
        var serialized = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("signed-native-loop", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private native reasoning", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Tool evidence collected.", serialized, StringComparison.Ordinal);
        var inspection = await new ConversationContextApplication(harness.Assembler!, harness.Usage).HandleAsync(new GetContextInspectionCommand(harness.RunId, harness.SessionId), TestContext.Current.CancellationToken);
        Assert.Same(snapshot, inspection?.RequestUsage);
    }

    /// <summary>Exercises canonical host requests against native protocol constraints.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task MixedInvalidToolBatch_RejectsEveryOrdinalBeforeAnyToolEffects(bool unknownTool, bool invalidLast)
    {
        var invalid = ("wire_invalid", unknownTool ? "unknown_tool" : "deterministic_output", "{}");
        var tool = ("wire_tool", "deterministic_output", "{\"sequence\":1}");
        await using var harness = new NativeLoopHarness(
            ToolStream(invalidLast ? [tool, invalid] : [invalid, tool]),
            TextStream("Batch corrected."));
        await harness.RunToCompletionAsync();

        Assert.Empty(harness.Observed.OfType<ToolInvocationStarted>());
        Assert.Equal(2, harness.Handler.Requests.Count);
        Assert.Equal(
            unknownTool ? ModelCorrectionCategory.ProviderInvocation : ModelCorrectionCategory.ToolBatch,
            Assert.Single(harness.Observed.OfType<ModelCorrectionAttempted>()).Category);
        if (unknownTool)
        {
            // The native adapter rejects unadvertised tools before accepting a replayable turn.
            Assert.DoesNotContain(harness.Model.Requests[1].Messages, message => message.Role == ModelMessageRole.Tool);
            Assert.False(harness.Model.HadReplay[1]);
            return;
        }

        AssertRejectedResults(harness.Handler.Requests[1], invalidLast ? ["wire_tool", "wire_invalid"] : ["wire_invalid", "wire_tool"]);
        AssertSignedReplay(harness.Handler.Requests[1]);
        Assert.All(harness.Model.Requests[1].Messages.Where(message => message.Role == ModelMessageRole.Tool), message => Assert.True(message.IsError));
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
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
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
        Assert.Contains(harness.Observed.OfType<EvidenceAdded>(), item => item.Kind == nameof(EvidenceKind.ToolResult));
    }

    /// <summary>A corrected ordinary tool call preserves native replay through the final response.</summary>
    [Fact]
    public static async Task CorrectedSingleTool_CompletesReplayAndConversation()
    {
        await using var harness = new NativeLoopHarness(
            ToolStream([("wire_bad_tool", "deterministic_output", "{}")]),
            ToolStream([("wire_good_tool", "deterministic_output", "{\"sequence\":1}")]),
            TextStream("Done."));
        await harness.RunToCompletionAsync();

        Assert.Equal(3, harness.Handler.Requests.Count);
        Assert.Single(harness.Observed.OfType<ToolInvocationStarted>());
        AssertRejectedResults(harness.Handler.Requests[1], ["wire_bad_tool"]);
        Assert.True(harness.Model.HadReplay[2]);
        Assert.DoesNotContain(harness.Observed, item => item is PlanProposed);
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

    private static string Event(string type, object body)
    {
        return "event: " + type + "\ndata: " + JsonSerializer.Serialize(body) + "\n\n";
    }

    private static string Start()
    {
        return Event("message_start", new
        {
            type = "message_start",
            message = new { id = "message_native_loop", type = "message", role = "assistant", model = "claude-opus-5", content = Array.Empty<object>(), usage = new { input_tokens = 100, output_tokens = 0 } },
        });
    }

    private static string End(string reason)
    {
        return Event("message_delta", new { type = "message_delta", delta = new { stop_reason = reason, stop_sequence = (string?)null }, usage = new { output_tokens = 50 } })
        + Event("message_stop", new { type = "message_stop" });
    }

    private static string TextStream(string text)
    {
        return Start()
        + Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = string.Empty } })
        + Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } })
        + Event("content_block_stop", new { type = "content_block_stop", index = 0 })
        + End("end_turn");
    }

    private static string ToolStream((string Id, string Name, string Arguments)[] calls, string? commentary = null)
    {
        var stream = new StringBuilder(Start());
        stream.Append(Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "thinking", thinking = string.Empty, signature = string.Empty } }));
        stream.Append(Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "thinking_delta", thinking = "private native reasoning" } }));
        stream.Append(Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "signature_delta", signature = "signed-native-loop" } }));
        stream.Append(Event("content_block_stop", new { type = "content_block_stop", index = 0 }));
        var index = 1;
        if (commentary is not null)
        {
            stream.Append(Event("content_block_start", new { type = "content_block_start", index, content_block = new { type = "text", text = string.Empty } }));
            stream.Append(Event("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "text_delta", text = commentary } }));
            stream.Append(Event("content_block_stop", new { type = "content_block_stop", index }));
            index++;
        }

        foreach (var call in calls)
        {
            stream.Append(Event("content_block_start", new { type = "content_block_start", index, content_block = new { type = "tool_use", id = call.Id, name = call.Name, input = new { } } }));
            stream.Append(Event("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "input_json_delta", partial_json = call.Arguments } }));
            stream.Append(Event("content_block_stop", new { type = "content_block_stop", index }));
            index++;
        }

        return stream.Append(End("tool_use")).ToString();
    }

    internal sealed class NativeLoopHarness : IAsyncDisposable
    {
        private readonly HttpClient _client;
        private readonly string _root;
        private readonly EffectiveModelProviderCatalog _catalog;

        internal NativeLoopHarness(params string[] responses)
            : this(null, responses)
        {
        }

        internal NativeLoopHarness(EffectiveModelProviderCatalog? catalog, params string[] responses)
        {
            _root = Path.Combine(Path.GetTempPath(), "threadsmith-native-loop-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Handler = new NativeHandler(responses);
            _client = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            var provider = AnthropicCatalogHydrator.Hydrate(
                new AnthropicProviderConfiguration { Id = "native", Name = "Native", SecretKeyReference = "secrets:models:native", Models = [] },
                [new AnthropicDiscoveredModel { ModelId = "claude-opus-5", DisplayName = "Native", MaximumInputTokens = 200000, MaximumOutputTokens = 32000 }],
                AnthropicReviewedModels.All);
            _catalog = catalog ?? new EffectiveModelProviderCatalog(
                new ModelProviderCatalogConfiguration { Providers = [provider], DefaultModelId = provider.Models[0].Id },
                new ModelProviderRegistry([new AnthropicProviderRegistration()]));
            Model = new CapturingProvider(new ConfiguredModelProvider(_client, _catalog, static (_, _) => Task.FromResult<string?>("fixture-native-key"), timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow)));
            Evidence = new EvidenceStore(Events, new SecretOutputSanitizer());
        }

        internal DomainEventStream Events { get; } = new(timeProvider: new FakeTimeProvider());

        internal EvidenceStore Evidence { get; }

        internal NativeHandler Handler { get; }

        internal CapturingProvider Model { get; }

        internal List<IDomainEvent> Observed { get; } = [];

        internal RunSteeringCoordinator Steering { get; } = new();

        internal SessionUsageProjection Usage { get; } = new();

        internal IContextAssembler? Assembler { get; private set; }

        internal Action<IDomainEvent>? OnEvent { get; set; }

        internal IActiveTurnCompactor? Compactor { get; set; }

        internal ActiveTurnCompactionPolicy? CompactionPolicy { get; set; }

        internal string? Goal { get; set; }

        internal SessionModelPreferences? InitialPreference { get; init; }

        internal IPromptLoader Prompts { get; init; } = TestPromptLoader.Instance;

        internal string ToolEvidence { get; set; } = "Tool evidence collected.";

        internal ConcurrentQueue<int> ExecutedSequences { get; } = new();

        internal ExecutionLimits Limits { get; set; } = new() { MaxModelRounds = 4, MaxCorrectiveTurns = 2 };

        internal SessionId SessionId { get; private set; }

        internal RunId RunId { get; private set; }

        async ValueTask IAsyncDisposable.DisposeAsync()
        {
            _client.Dispose();
            await Events.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }

        internal async Task RunToCompletionAsync(bool cancelAfterTool = false, bool cancelGoalAfterFirstTool = false, Func<CommandDispatcher, CancellationToken, Task>? beforeWait = null)
        {
            await using var capture = Events.Subscribe((domainEvent, _) =>
            {
                Observed.Add(domainEvent);
                OnEvent?.Invoke(domainEvent);
                return Task.CompletedTask;
            });
            var sanitizer = new SecretOutputSanitizer();
            var registry = new ToolRegistry([new TestDeterministicOutputTool(ToolEvidence, onExecute: ExecutedSequences.Enqueue)]);
            var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), Events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, UnboundedBudget.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            var resolver = new ModelResolver(_catalog.ModelCatalog, new InMemoryModelPreferenceSnapshotProvider());
            var assembler = new ContextAssembler(Evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, Events, Prompts, modelResolver: resolver, providerInstructionResolver: new ReasoningReplayFixture.InstructionResolver(_catalog));
            Assembler = assembler;
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
                Limits,
                sessionPreferences: InitialPreference,
                sessionUsage: Usage,
                activeTurnCompactor: Compactor,
                activeTurnCompactionPolicy: CompactionPolicy,
                selectActiveModel: InitialPreference is null ? null : SelectFallbackAsync,
                steering: Steering,
                correctiveMessages: new CorrectiveMessageFactory(Prompts),
                prompts: Prompts,
                outputClock: new FakeTimeProvider());
            var dispatcher = new CommandDispatcher([application]);
            var cancellationToken = TestContext.Current.CancellationToken;
            var cancelGoalPending = cancelGoalAfterFirstTool;
            await using var cancel = Events.Subscribe(async (item, ct) =>
            {
                if (item is ToolInvocationCompleted tool)
                {
                    if (cancelAfterTool)
                    {
                        await dispatcher.DispatchAsync(new CancelRunCommand(tool.SessionId, tool.RunId), ct);
                    }

                    if (cancelGoalPending)
                    {
                        cancelGoalPending = false;
                        await dispatcher.DispatchAsync(new SessionGoalCommand(tool.SessionId, SessionGoalAction.Cancel), ct);
                    }
                }
            });
            SessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("native loop integration"), cancellationToken);
            if (Goal is { } goal)
            {
                await dispatcher.DispatchAsync(new SessionGoalCommand(SessionId, SessionGoalAction.Set, goal), cancellationToken);
            }

            RunId = await dispatcher.DispatchAsync(new SubmitRequestCommand(SessionId, "Inspect the repository and report."), cancellationToken);
            if (beforeWait is not null)
            {
                await beforeWait(dispatcher, cancellationToken);
            }

            if (cancelAfterTool)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.DispatchAsync(new WaitForRunCommand(RunId), cancellationToken));
            }
            else
            {
                var succeeded = await dispatcher.DispatchAsync(new WaitForRunCommand(RunId), cancellationToken);
                var projection = await projections.GetAsync<SessionProjection>(new ProjectionKey("session", SessionId.Value.ToString("D")), cancellationToken);
                Assert.True(succeeded, projection?.Error);
            }
        }

        private Task<ActiveModelSelectionResult> SelectFallbackAsync(ModelProfileId id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = _catalog.ModelCatalog.Get(id);
            InitialPreference!.SetReasoning(id, profile.DefaultReasoningLevel);
            return Task.FromResult(new ActiveModelSelectionResult
            {
                Persisted = true,
                Selection = new ActiveModelSelectionSnapshot { Profile = profile, ProviderId = profile.Provider },
            });
        }
    }

    internal sealed class CapturingProvider : IModelProvider, IModelRequestPreparationResolver
    {
        private readonly ConfiguredModelProvider _inner;

        internal CapturingProvider(ConfiguredModelProvider inner)
        {
            _inner = inner;
        }

        internal List<ModelStreamRequest> Requests { get; } = [];

        internal List<bool> HadReplay { get; } = [];

        public ModelStreamRequest Prepare(ModelStreamRequest request)
        {
            return _inner.Prepare(request);
        }

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

    internal sealed class NativeHandler : HttpMessageHandler
    {
        private readonly string[] _responses;

        internal NativeHandler(string[] responses)
        {
            _responses = responses;
        }

        internal List<JsonElement> Requests { get; } = [];

        internal bool FragmentResponses { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(content);
            Requests.Add(json.RootElement.Clone());
            Assert.True(Requests.Count <= _responses.Length, "Unexpected native retry or extra model round.");
            var response = _responses[Requests.Count - 1];
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = FragmentResponses ? ReasoningReplayFixture.FragmentedContent(response) : new StringContent(response, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class ProfileTaggedGoalPrompts : IPromptLoader
    {
        private readonly AsyncLocal<ModelProfileId?> _profile = new();

        public IDisposable BeginModelScope(ModelProfileId? modelProfileId)
        {
            var previous = _profile.Value;
            _profile.Value = modelProfileId;
            return new RestoreScope(() => _profile.Value = previous);
        }

        public string Get(string promptFileName) => TestPromptLoader.Instance.Get(promptFileName);

        public string Render(string promptFileName, IReadOnlyDictionary<string, string> tokens)
        {
            var content = TestPromptLoader.Instance.Render(promptFileName, tokens);
            return promptFileName == PromptFileNames.ContextGoalWorkContext ? $"Goal wording for {_profile.Value?.Value:D}\n{content}" : content;
        }

        private sealed class RestoreScope : IDisposable
        {
            private readonly Action _restore;

            public RestoreScope(Action restore)
            {
                _restore = restore;
            }

            public void Dispose() => _restore();
        }
    }
}
