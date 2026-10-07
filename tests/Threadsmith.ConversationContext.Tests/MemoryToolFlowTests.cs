namespace Threadsmith.ConversationContext.Tests;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises the production conversation/tool/service path with deterministic model decisions.</summary>
public static class MemoryToolFlowTests
{
    /// <summary>A collision is delivered back to the model, whose next tool invocation resolves it.</summary>
    [Theory]
    [InlineData(RepositoryMemoryType.Situational, false)]
    [InlineData(RepositoryMemoryType.StandingPreference, false)]
    [InlineData(RepositoryMemoryType.Situational, true)]
    [InlineData(RepositoryMemoryType.StandingPreference, true)]
    public static async Task Model_receives_collision_and_can_supersede_or_confirm_distinct(RepositoryMemoryType type, bool distinct)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var identity = RepositoryIdentity.Create(fixture.DirectoryPath);
        var generator = new TestMemoryEmbeddingGenerator();
        var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        var sanitizer = new SecretOutputSanitizer();
        using var search = new HybridRepositoryMemoryRetriever(store, generator, new MemoryScoreEncoder());
        var memory = new RepositoryMemoryService(store, generator, sanitizer, search: search);
        var original = await memory.ExecuteAsync(MemoryTestData.Operation("add", "Original cancellation convention") with { RepositoryIdentity = identity, MemoryType = type }, ct);
        var options = new FlowOptions();
        var tool = new MemoriesTool(memory, options, TestPromptLoader.Instance);
        var registry = new ToolRegistry([tool]);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
        var model = new CollisionModel(distinct, type, original.Id!.Value);
        var application = new SessionApplication(
            events,
            model,
            new ExecutionBudget(new BudgetDimensions(100000, 20, TimeSpan.FromMinutes(2))),
            sanitizer,
            NullLogger<SessionApplication>.Instance,
            pipeline,
            (_, _) => Task.FromResult(new ToolInvocationContext { RepositoryPath = fixture.DirectoryPath, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model" }),
            contextAssembler: new ContextAssembler(new EvidenceStore(events, sanitizer), new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, events, TestPromptLoader.Instance),
            toolRegistry: registry,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            repositoryMemories: memory,
            repositoryMemoryOptions: options);
        var dispatcher = new CommandDispatcher([application]);
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("memory flow"), ct);

        var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, "Remember the revised convention"), ct);
        Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(run), ct));

        Assert.Equal(3, model.Requests);
        var entries = (await memory.GetSnapshotAsync(identity, ct)).Entries;
        Assert.Equal(distinct ? 2 : 1, entries.Count);
        var existing = Assert.Single(entries, entry => entry.Id == original.Id);
        Assert.Equal(distinct ? 1 : 2, existing.Revision);
        Assert.Equal(distinct ? "Original cancellation convention" : CollisionModel.Replacement, existing.Text);
        Assert.Equal(type, existing.MemoryType);
        Assert.Contains(entries, entry => entry.Text == CollisionModel.Replacement && entry.Origin == RepositoryMemoryOrigin.Model);
    }

    private sealed class FlowOptions : IRepositoryMemoryOptionsProvider
    {
        public RepositoryMemoryOptions CaptureCurrent()
        {
            return new() { ReconciliationEnabled = true, };
        }

        public RepositoryMemoryOptions Capture(string repositoryIdentity)
        {
            return CaptureCurrent();
        }
    }

    private sealed class CollisionModel : IModelProvider
    {
        public const string Replacement = "Revised cancellation convention with iterator support";
        private readonly bool _distinct;
        private readonly RepositoryMemoryType _type;
        private readonly RepositoryMemoryId _original;

        public CollisionModel(bool distinct, RepositoryMemoryType type, RepositoryMemoryId original)
        {
            _distinct = distinct;
            _type = type;
            _original = original;
        }

        public int Requests { get; private set; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            Requests++;
            if (Requests == 1)
            {
                yield return new ModelChunk { Output = new ToolRequestModelOutput("memories", JsonSerializer.Serialize(new { action = "add", text = Replacement, memoryType = _type == RepositoryMemoryType.StandingPreference ? "standingPreference" : "situational" })) };
                yield break;
            }

            var result = request.Messages.Last(message => message.Role == ModelMessageRole.Tool).GetModelVisibleContent();
            using var document = JsonDocument.Parse(result);
            var body = document.RootElement;
            if (Requests == 2)
            {
                Assert.Equal("reconciliationRequired", body.GetProperty("Outcome").GetString());
                Assert.False(body.GetProperty("Added").GetBoolean());
                var candidate = Assert.Single(body.GetProperty("Entries").EnumerateArray());
                Assert.Equal(_original.Value.ToString("D"), candidate.GetProperty("Id").GetString());
                Assert.Contains("Original cancellation", candidate.GetProperty("Text").GetString(), StringComparison.Ordinal);
                Assert.Contains("expectedRevision", body.GetProperty("Resolution").GetString(), StringComparison.Ordinal);
                var arguments = _distinct
                    ? JsonSerializer.Serialize(new { action = "add", text = Replacement, memoryType = _type == RepositoryMemoryType.StandingPreference ? "standingPreference" : "situational", confirmDistinctFrom = new[] { new { id = _original.Value.ToString("D"), revision = candidate.GetProperty("Revision").GetInt64() } } })
                    : JsonSerializer.Serialize(new { action = "update", id = _original.Value.ToString("D"), text = Replacement, expectedRevision = candidate.GetProperty("Revision").GetInt64() });
                yield return new ModelChunk { Output = new ToolRequestModelOutput("memories", arguments) };
                yield break;
            }

            Assert.Equal(_distinct ? "added" : "updated", body.GetProperty("Outcome").GetString());
            yield return new ModelChunk { Text = "Memory resolved." };
        }
    }
}
