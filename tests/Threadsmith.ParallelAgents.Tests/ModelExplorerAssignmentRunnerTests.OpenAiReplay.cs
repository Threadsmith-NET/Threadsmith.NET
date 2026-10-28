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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add(document.RootElement.Clone());
            Assert.True(Requests.Count <= 3, "Unexpected retry or extra model round.");
            var round = Requests.Count - 1;
            var stream = round < 2
                ? ReasoningReplayFixture.Stream(_codex, round, _tool, JsonSerializer.Serialize(new { path = $"src/round-{round}.cs" }))
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
