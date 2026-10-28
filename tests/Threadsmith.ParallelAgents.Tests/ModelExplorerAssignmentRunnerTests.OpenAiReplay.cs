namespace Threadsmith.ParallelAgents.Tests;

using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class ModelExplorerAssignmentRunnerTests
{
    /// <summary>Child requests retain provider reasoning through two real tool executions.</summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false, true)]
    public async Task RunAsync_OpenAiReasoning_PreservesAndReleasesReplay(bool codex, bool cancelAfterTool, bool exceedCapacity, bool reasoningAlias = false)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var ct = cancellation.Token;
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe(async (item, _) =>
        {
            observed.Add(item);
            if (cancelAfterTool && item is ToolInvocationCompleted)
            {
                await cancellation.CancelAsync();
            }
        });
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var tool = new InspectMetadataTool();
        var registry = new ToolRegistry([tool]);
        var catalog = ReasoningReplayFixture.Catalog(codex, contextWindow: exceedCapacity ? 9000 : 200000, reasoningAlias: reasoningAlias);
        var profile = Assert.Single(catalog.ModelCatalog.Profiles);
        using var handler = new OpenAiReplayHandler(codex, tool.Definition.Id) { ExceedCapacity = exceedCapacity, ReasoningAlias = reasoningAlias };
        using var client = new HttpClient(handler);
        var provider = new ReplayTrackingProvider(new ConfiguredModelProvider(client, catalog, (_, _) => Task.FromResult<string?>("fixture-key")));
        var assignment = CreateAssignment(profile.Id, [tool.Definition.Id]);
        assignment = assignment with { Policy = assignment.Policy with { ReasoningLevel = "low" } };
        var plan = CreatePlan(assignment);
        var runner = new ModelExplorerAssignmentRunner(
            new AgentContextAssembler(evidence),
            new AgentFindingAdmission(evidence),
            new AgentModelSelector(catalog.ModelCatalog, new DefaultModelSelectionPolicy(catalog.ModelCatalog), new ReasoningReplayFixture.InstructionResolver(catalog)),
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            new StubInstructionProvider(),
            sanitizer,
            CreateOptions(),
            CreateParentContext(plan, [tool.Definition.Id]),
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId),
            TestPromptLoader.Instance);

        if (cancelAfterTool)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(plan, assignment, ct));
            Assert.Single(handler.Requests);
            Assert.All(provider.Requests, request => Assert.False(request.TransientState?.HasResponses));
            return;
        }

        if (exceedCapacity)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(plan, assignment, ct));
            Assert.Single(handler.Requests);
            Assert.Single(provider.Requests);
            var estimate = provider.PreparedRequests[^1].WireEstimate!;
            var allowance = Assert.Single(estimate.Components, item => item.Id == "retained-allowance");
            Assert.True(estimate.TotalCapacityTokens > profile.ContextWindow);
            Assert.True(estimate.TotalCapacityTokens - allowance.Tokens <= profile.ContextWindow);
            Assert.All(provider.Requests, request => Assert.False(request.TransientState?.HasResponses));
            return;
        }

        var outcome = await runner.RunAsync(plan, assignment, ct);

        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Equal("Inspection complete.", outcome.Response);
        Assert.Equal(2, outcome.Usage.ToolCalls);
        Assert.Equal(3, handler.Requests.Count);
        ReasoningReplayFixture.AssertReplay(handler.Requests[1], codex, 1, reasoningAlias);
        ReasoningReplayFixture.AssertReplay(handler.Requests[2], codex, 2, reasoningAlias);
        Assert.All(provider.Requests.Skip(1), request =>
        {
            var allowance = Assert.Single(request.WireEstimate!.Components, item => item.Id == "retained-allowance");
            Assert.True(allowance.Tokens > 0);
            Assert.Equal(request.WireEstimate.WireInputTokens, request.WireEstimate.Components.Sum(item => item.Tokens));
        });
        Assert.DoesNotContain("opaque-", JsonSerializer.Serialize(observed), StringComparison.Ordinal);
        Assert.DoesNotContain("private-", JsonSerializer.Serialize(outcome), StringComparison.Ordinal);
        Assert.All(provider.Requests, request => Assert.False(request.TransientState?.HasResponses));
    }

    /// <summary>Replay state from other rounds cannot require an envelope on an ordinary or rejected response.</summary>
    [Theory]
    [InlineData(false, 0, "success")]
    [InlineData(true, 0, "success")]
    [InlineData(false, 1, "success")]
    [InlineData(true, 1, "success")]
    [InlineData(false, 1, "duplicate")]
    [InlineData(true, 1, "duplicate")]
    [InlineData(false, 1, "malformed")]
    [InlineData(true, 1, "malformed")]
    public async Task RunAsync_MixedReplayRounds_PreserveHistoryAndCorrections(bool codex, int ordinaryRound, string outcome)
    {
        // Arrange: three tool rounds, including one without private reasoning, then a final answer.
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var tool = new InspectMetadataTool();
        var registry = new ToolRegistry([tool]);
        var catalog = ReasoningReplayFixture.Catalog(codex);
        var profile = Assert.Single(catalog.ModelCatalog.Profiles);
        using var handler = new OpenAiReplayHandler(codex, tool.Definition.Id)
        {
            ToolRounds = 3,
            OrdinaryRound = ordinaryRound,
            OrdinaryOutcome = outcome,
        };
        using var client = new HttpClient(handler);
        var provider = new ReplayTrackingProvider(new ConfiguredModelProvider(client, catalog, (_, _) => Task.FromResult<string?>("fixture-key")));
        var assignment = CreateAssignment(profile.Id, [tool.Definition.Id]);
        assignment = assignment with { Policy = assignment.Policy with { ReasoningLevel = "low" } };
        var plan = CreatePlan(assignment);
        var runner = new ModelExplorerAssignmentRunner(
            new AgentContextAssembler(evidence),
            new AgentFindingAdmission(evidence),
            new AgentModelSelector(catalog.ModelCatalog, new DefaultModelSelectionPolicy(catalog.ModelCatalog), new ReasoningReplayFixture.InstructionResolver(catalog)),
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            new StubInstructionProvider(),
            sanitizer,
            CreateOptions(),
            CreateParentContext(plan, [tool.Definition.Id]),
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId),
            TestPromptLoader.Instance);

        // Act.
        var result = await runner.RunAsync(plan, assignment, TestContext.Current.CancellationToken);

        // Assert: retained rounds still use original wire IDs, ordinary rounds use host IDs.
        Assert.Equal("Inspection complete.", result.Response);
        Assert.Equal(outcome == "success" ? 0 : 1, result.Usage.Corrections);
        Assert.Equal(4, handler.Requests.Count);
        var messages = provider.Requests[^1].Messages;
        var calls = messages.Where(message => message.Role == ModelMessageRole.Assistant && message.ToolCallId is not null).ToArray();
        Assert.Equal(outcome == "malformed" ? 2 : 3, calls.Length);
        var expectedIds = calls.Select(message => message.ModelRound == ordinaryRound ? message.ToolCallId : $"wire-{message.ModelRound}").ToArray();
        var wire = handler.Requests[^1];
        if (codex)
        {
            var items = wire.GetProperty("input").EnumerateArray().Where(item => item.TryGetProperty("type", out _)).ToArray();
            Assert.Equal(expectedIds, items.Where(item => item.GetProperty("type").GetString() == "function_call").Select(item => item.GetProperty("call_id").GetString()));
            Assert.Equal(expectedIds, items.Where(item => item.GetProperty("type").GetString() == "function_call_output").Select(item => item.GetProperty("call_id").GetString()));
            Assert.Equal(
                Enumerable.Range(0, 3).Where(round => round != ordinaryRound).Select(round => $"opaque-{round}-state"),
                items.Where(item => item.GetProperty("type").GetString() == "reasoning").Select(item => item.GetProperty("encrypted_content").GetString()));
        }
        else
        {
            var items = wire.GetProperty("messages").EnumerateArray().ToArray();
            Assert.Equal(expectedIds, items.Where(item => item.TryGetProperty("tool_calls", out _)).SelectMany(item => item.GetProperty("tool_calls").EnumerateArray()).Select(item => item.GetProperty("id").GetString()));
            Assert.Equal(expectedIds, items.Where(item => item.GetProperty("role").GetString() == "tool").Select(item => item.GetProperty("tool_call_id").GetString()));
            Assert.Equal(
                Enumerable.Range(0, 3).Where(round => round != ordinaryRound).Select(round => $"private-{round}-state"),
                items.Where(item => item.TryGetProperty("reasoning_content", out _)).Select(item => item.GetProperty("reasoning_content").GetString()));
        }

        Assert.Equal(outcome == "duplicate" ? 1 : 0, messages.Count(message => message.Role == ModelMessageRole.Tool && message.IsError == true));
        Assert.All(provider.Requests, request => Assert.False(request.TransientState?.HasResponses));
    }

    private sealed class ReplayTrackingProvider : IModelProvider, IModelRequestPreparationResolver
    {
        private readonly ConfiguredModelProvider _inner;

        internal ReplayTrackingProvider(ConfiguredModelProvider inner)
        {
            _inner = inner;
        }

        internal List<ModelStreamRequest> Requests { get; } = [];

        internal List<ModelStreamRequest> PreparedRequests { get; } = [];

        public ModelStreamRequest Prepare(ModelStreamRequest request)
        {
            var prepared = _inner.Prepare(request);
            PreparedRequests.Add(prepared);
            return prepared;
        }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await foreach (var chunk in _inner.StreamAsync(request, cancellationToken))
            {
                yield return chunk;
            }
        }
    }

    private sealed class OpenAiReplayHandler : HttpMessageHandler
    {
        private readonly bool _codex;
        private readonly string _tool;

        internal OpenAiReplayHandler(bool codex, string tool)
        {
            _codex = codex;
            _tool = tool;
        }

        internal List<JsonElement> Requests { get; } = [];

        internal bool ExceedCapacity { get; init; }

        internal bool ReasoningAlias { get; init; }

        internal int ToolRounds { get; init; } = 2;

        internal int OrdinaryRound { get; init; } = -1;

        internal string OrdinaryOutcome { get; init; } = "success";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(document.RootElement.Clone());
            Assert.True(Requests.Count <= ToolRounds + 1, "Unexpected retry or extra model round.");
            var round = Requests.Count - 1;
            var arguments = round == OrdinaryRound && OrdinaryOutcome == "malformed"
                ? "{invalid"
                : JsonSerializer.Serialize(new { path = $"src/round-{(round == OrdinaryRound && OrdinaryOutcome == "duplicate" ? 0 : round)}.cs" });
            var stream = round < ToolRounds
                ? ReasoningReplayFixture.Stream(_codex, round, _tool, arguments, reasoning: round != OrdinaryRound)
                : ReasoningReplayFixture.Final(_codex);
            stream = stream.Replace("reasoning_content", ReasoningAlias ? "reasoning" : "reasoning_content", StringComparison.Ordinal);
            if (ExceedCapacity)
            {
                stream = _codex
                    ? stream.Replace("\"output_tokens\":40", "\"output_tokens\":4000", StringComparison.Ordinal)
                    : stream.Replace("private-0-", new string('r', 16000), StringComparison.Ordinal);
            }

            return new(HttpStatusCode.OK) { Content = ReasoningReplayFixture.FragmentedContent(stream) };
        }
    }
}
