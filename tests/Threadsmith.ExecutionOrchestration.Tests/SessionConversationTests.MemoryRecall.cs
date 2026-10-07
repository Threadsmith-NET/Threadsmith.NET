namespace Threadsmith.ExecutionOrchestration.Tests;

using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.Anthropic;
using Xunit;

public sealed partial class SessionConversationTests
{
    /// <summary>A real Anthropic response envelope survives concept refresh and replays its tool correlation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnthropicConceptContinuation_PreservesReplayWithAndWithoutMemoryMatches(bool matches)
    {
        var ct = TestContext.Current.CancellationToken;
        using var handler = new MemoryReplayHandler();
        using var client = new HttpClient(handler);
        var provider = new MemoryReplayProvider(client);
        await using var scenario = await ConversationScenario.CreateMemoryReplayAsync(provider, matches);
        var run = await scenario.Dispatcher.DispatchAsync(new SubmitRequestCommand(scenario.SessionId, "Inspect the repository"), ct);
        Assert.True(await scenario.Dispatcher.DispatchAsync(new WaitForRunCommand(run), ct));

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(provider.Requests[0].HistoryRewriteGeneration, provider.Requests[1].HistoryRewriteGeneration);
        Assert.Equal(provider.Requests[0].Messages, provider.Requests[1].Messages.Take(provider.Requests[0].Messages.Count));
        var response = Assert.Single(provider.RetainedResponses);
        Assert.Equal("wire-memory", Assert.Single(response.WireToolCallIds));
        var blocks = handler.Requests[1].GetProperty("messages").EnumerateArray()
            .SelectMany(message => message.GetProperty("content").EnumerateArray()).ToArray();
        Assert.Contains(blocks, block => block.GetProperty("type").GetString() == "tool_use"
            && block.GetProperty("id").GetString() == "wire-memory");
        Assert.Contains(blocks, block => block.GetProperty("type").GetString() == "tool_result"
            && block.GetProperty("tool_use_id").GetString() == "wire-memory");
        var memory = Assert.IsType<ConceptMemory>(scenario.Memory);
        Assert.Contains(memory.Requests, request => request.Concepts.Contains("cancellation"));
        Assert.Equal(matches, provider.Requests[1].Messages.Any(message => message.SectionId == "repository-memory"
            && message.GetModelVisibleContent().Contains(memory.Entry.Text, StringComparison.Ordinal)));
    }

    /// <summary>Later memory replacement/removal appends a current snapshot without changing prior replay prefixes.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnthropicMemoryRevisionContinuation_PreservesEarlierSnapshotsAsHistory(bool remove)
    {
        var ct = TestContext.Current.CancellationToken;
        using var handler = new MemoryReplayHandler { EmitSecondTool = true };
        using var client = new HttpClient(handler);
        var provider = new MemoryReplayProvider(client);
        await using var scenario = await ConversationScenario.CreateMemoryReplayAsync(provider, matches: true);
        var memory = Assert.IsType<ConceptMemory>(scenario.Memory);
        var originalText = memory.Entry.Text;
        handler.BeforeSecondResponse = () =>
        {
            memory.Entry = memory.Entry with { Text = "Updated cancellation guidance.", Revision = 2 };
            memory.Matches = !remove;
        };
        var run = await scenario.Dispatcher.DispatchAsync(new SubmitRequestCommand(scenario.SessionId, "Inspect the repository"), ct);
        Assert.True(await scenario.Dispatcher.DispatchAsync(new WaitForRunCommand(run), ct));

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(provider.Requests[1].Messages, provider.Requests[2].Messages.Take(provider.Requests[1].Messages.Count));
        Assert.All(provider.Requests, request => Assert.Equal(provider.Requests[0].HistoryRewriteGeneration, request.HistoryRewriteGeneration));
        var snapshots = provider.Requests[2].Messages.Where(message => message.SectionId == "repository-memory").ToArray();
        Assert.Equal(2, snapshots.Length);
        Assert.Contains(originalText, snapshots[0].GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.DoesNotContain(originalText, snapshots[1].GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.Contains("supersedes earlier", snapshots[1].GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.Equal(!remove, snapshots[1].GetModelVisibleContent().Contains(memory.Entry.Text, StringComparison.Ordinal));
        Assert.Contains(memory.Requests, request => request.RetainedMemories.Any(item => item.Revision == 1));
    }

    /// <summary>Concepts and admitted memory belong to one ordinary run and reset for the next request.</summary>
    [Fact]
    public async Task OrdinaryRuns_ResetConceptRecall()
    {
        var ct = TestContext.Current.CancellationToken;
        using var handler = new MemoryReplayHandler();
        using var client = new HttpClient(handler);
        var provider = new MemoryReplayProvider(client);
        await using var scenario = await ConversationScenario.CreateMemoryReplayAsync(provider, matches: true);
        var first = await scenario.Dispatcher.DispatchAsync(new SubmitRequestCommand(scenario.SessionId, "Inspect cancellation"), ct);
        Assert.True(await scenario.Dispatcher.DispatchAsync(new WaitForRunCommand(first), ct));
        var memory = Assert.IsType<ConceptMemory>(scenario.Memory);
        Assert.Contains(memory.Requests, request => request.Concepts.Contains("cancellation"));
        var count = memory.Requests.Count;
        var next = await scenario.Dispatcher.DispatchAsync(new SubmitRequestCommand(scenario.SessionId, "Explain another topic"), ct);
        Assert.True(await scenario.Dispatcher.DispatchAsync(new WaitForRunCommand(next), ct));
        Assert.All(memory.Requests.Skip(count), request =>
        {
            Assert.Empty(request.Concepts);
            Assert.Empty(request.RetainedMemories);
        });
    }

    private sealed class ConceptMemory : IManagedRepositoryMemoryService, IHybridRepositoryMemoryRetriever, IRepositoryMemoryOptionsProvider
    {
        public bool Matches { get; set; } = true;

        public RepositoryMemoryEntry Entry { get; set; } = new()
        {
            Id = RepositoryMemoryId.New(),
            RepositoryIdentity = "test",
            Text = "Preserve iterator cancellation during subsequent edits.",
            ContentHash = "test",
            Revision = 1,
            Origin = RepositoryMemoryOrigin.Manual,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };

        public List<RepositoryMemoryRetrievalRequest> Requests { get; } = [];

        public RepositoryMemoryOptions CaptureCurrent()
        {
            return new() { ConceptRecallEnabled = true, };
        }

        public RepositoryMemoryOptions Capture(string repositoryIdentity)
        {
            return CaptureCurrent();
        }

        public Task<RepositoryMemoryRetrievalResult> RetrieveAsync(RepositoryMemoryRetrievalRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new RepositoryMemoryRetrievalResult(Matches && request.Concepts.Contains("cancellation") ? [new(Entry, 1, null, null, null)] : [], [], Entry.Revision));
        }

        public Task<RepositoryMemoryReadSnapshot> GetSnapshotAsync(string repositoryIdentity, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new RepositoryMemoryReadSnapshot(repositoryIdentity, Entry.Revision, Matches ? [Entry] : [], [], []));
        }

        public Task<RepositoryMemoryOperationResult> ExecuteAsync(RepositoryMemoryOperationRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<RepositoryMemoryId>> EnforceCapacityAsync(string repositoryIdentity, RepositoryMemoryOptions options, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<RepositoryMemoryId>>([]);
        }

        public Task RecordInclusionsAsync(string repositoryIdentity, RunId runId, IReadOnlyList<RepositoryMemoryInclusion> inclusions, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryReplayProvider : IModelProvider, IModelRequestPreparationResolver
    {
        private readonly IModelProvider _inner;
        private readonly ModelProfile _profile;
        private readonly ConfiguredModelDefinition _definition;

        public MemoryReplayProvider(HttpClient client)
        {
            _profile = new ModelProfile
            {
                Id = ModelProfileId.New(),
                Name = "memory replay fixture",
                Provider = "anthropic",
                ModelId = "claude-test",
                Endpoint = AnthropicProviderRegistration.MessagesEndpoint,
                ContextWindow = 200000,
                MaximumOutputTokens = 8192,
                RequestOutputTokenReserve = 4096,
                Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true },
                SupportedReasoningLevels = [ReasoningLevel.None],
                ReasoningCapability = new EffectiveReasoningCapability { SupportsReasoningOff = true },
                SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
                RetryPolicy = new ModelRetryPolicy { MaxAttempts = 1, Delay = TimeSpan.Zero },
            };
            var compatibility = new AnthropicModelCompatibility
            {
                ModelId = _profile.ModelId,
                SupportsStrictSchemas = true,
                SupportsReasoningOff = true,
                ThinkingMode = AnthropicThinkingMode.Disabled,
                Prices = new AnthropicModelPrices { InputPerMillionTokens = 2, CacheWritePerMillionTokens = 2.5m, CacheReadPerMillionTokens = 0.2m, OutputPerMillionTokens = 10 },
            };
            _definition = new ConfiguredModelDefinition
            {
                Profile = _profile,
                ProviderId = "memory-replay",
                Registration = new AnthropicProviderRegistration(),
                ProviderConfiguration = new AnthropicProviderConfiguration { Id = "memory-replay", Name = "fixture", Models = [] },
                ModelConfiguration = new AnthropicModelConfiguration { Id = _profile.Id, Name = _profile.Name, ModelId = _profile.ModelId, Compatibility = compatibility },
            };
            _inner = _definition.Registration.CreateProvider(new ModelProviderActivationContext
            {
                HttpClient = client,
                Profile = _profile,
                ResolvedSecret = "test-key",
                ProviderConfiguration = _definition.ProviderConfiguration,
                ModelConfiguration = _definition.ModelConfiguration,
            });
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public List<ModelResponseReplayEnvelope> RetainedResponses { get; } = [];

        public ModelStreamRequest Prepare(ModelStreamRequest request)
        {
            return ModelRequestPreparation.Apply(
            _definition,
            request with { ResolvedProfileId = _profile.Id, ReasoningLevel = ReasoningLevel.None, ToolTransportMode = ToolTransportMode.Native, MaximumOutputTokens = 4096 });
        }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.TransientState is { HasResponses: true } state)
            {
                RetainedResponses.AddRange(state.Responses);
            }

            await foreach (var chunk in _inner.StreamAsync(request, cancellationToken))
            {
                yield return chunk;
            }
        }
    }

    private sealed class MemoryReplayHandler : HttpMessageHandler
    {
        public List<JsonElement> Requests { get; } = [];

        public bool EmitSecondTool { get; init; }

        public Action? BeforeSecondResponse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content ?? throw new InvalidOperationException("Missing request body.");
            using var document = JsonDocument.Parse(await content.ReadAsStringAsync(cancellationToken));
            Requests.Add(document.RootElement.Clone());
            if (Requests.Count == 2)
            {
                BeforeSecondResponse?.Invoke();
            }

            var toolResponse = Requests.Count == 1 || (EmitSecondTool && Requests.Count == 2);
            var stream = Event("message_start", new { type = "message_start", message = new { id = "memory-response", type = "message", role = "assistant", model = "claude-test", content = Array.Empty<object>(), usage = new { input_tokens = 100, output_tokens = 0 } } });
            if (toolResponse)
            {
                var arguments = Requests.Count == 1 ? "{\"concepts\":[\"cancellation\"]}" : "{\"path\":\".\",\"concepts\":[\"logging\"]}";
                stream += Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "tool_use", id = Requests.Count == 1 ? "wire-memory" : "wire-memory-2", name = "list_files", input = new { } } });
                stream += Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "input_json_delta", partial_json = arguments } });
            }
            else
            {
                stream += Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "Inspection complete." } });
            }

            stream += Event("content_block_stop", new { type = "content_block_stop", index = 0 });
            stream += Event("message_delta", new { type = "message_delta", delta = new { stop_reason = toolResponse ? "tool_use" : "end_turn", stop_sequence = (string?)null }, usage = new { output_tokens = 25 } });
            stream += Event("message_stop", new { type = "message_stop" });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        }

        private static string Event(string name, object value)
        {
            return "event: " + name + "\ndata: " + JsonSerializer.Serialize(value) + "\n\n";
        }
    }
}
