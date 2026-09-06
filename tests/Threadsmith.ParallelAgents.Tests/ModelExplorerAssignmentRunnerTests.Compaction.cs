namespace Threadsmith.ParallelAgents.Tests;

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
    /// <summary>Compaction uses the real summary service; mixed evidence reads preserve originals and usage.</summary>
    [Theory]
    [InlineData(AgentRole.Explorer, WorkloadClass.General, 12_000)]
    [InlineData(AgentRole.SecurityReviewer, WorkloadClass.Review, 12_000)]
    [InlineData(AgentRole.Implementer, WorkloadClass.CodeEdit, 12_000)]
    [InlineData(AgentRole.Explorer, WorkloadClass.General, 300_000)]
    public async Task RunAsync_CompactionAndEvidenceRead_PreservesTaskAndAccountsForSummary(AgentRole role, WorkloadClass workload, int contentCharacters)
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var paths = Enumerable.Range(0, 600).Select(index => new ToolProvenanceSource("file", $"src/File{index}.cs"))
            .Append(new ToolProvenanceSource("file", "src/password=private-provenance-value/File.cs")).ToArray();
        var content = new string('x', contentCharacters);
        var tool = new InspectMetadataTool(content, Math.Max(64_000, contentCharacters * 4), paths);
        var registry = new ToolRegistry([tool]);
        var profile = CreateProfile() with { ContextWindow = Math.Max(64_000, contentCharacters * 2), IntendedWorkloadClasses = [workload] };
        var provider = new CompactingProvider(tool.Definition.Id, profile);
        var assignment = CreateAssignment(profile.Id, [tool.Definition.Id]) with { Role = role };
        var plan = CreatePlan(assignment);
        var usage = new SessionUsageProjection();
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, [tool.Definition.Id]),
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId),
            usage,
            CreateOptions() with
            {
                Compaction = new ChildAgentCompactionOptions
                {
                    TriggerTokens = 5_000,
                    TriggerPercent = 0,
                    TargetTokens = 3_000,
                    RecentTokens = 1,
                    MinimumSavingsTokens = 500,
                },
            });

        var outcome = await runner.RunAsync(plan, assignment);

        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Equal("An unrestricted final response.", outcome.Response);
        Assert.Equal(1, provider.Summaries);
        Assert.Equal(4, outcome.Usage.ToolCalls);
        Assert.Equal(3, evidence.Snapshot(plan.Provenance.SessionId).Count);
        Assert.Equal(50, usage.GetSnapshot(plan.Provenance.SessionId).InputTokens);
        var requests = provider.Requests.Where(request => request.Messages.Any(message => message.SectionId == "child-assignment")).ToArray();
        Assert.Equal(4, requests.Length);
        Assert.Equal(1, requests[2].HistoryRewriteGeneration);
        Assert.Equal(requests[0].Messages.Take(5), requests[2].Messages.Take(5));
        Assert.Contains(requests[2].Messages, message => message.SectionId == "child-evidence-index");
        var summary = Assert.Single(requests[2].Messages, message => message.SectionId == "active-turn-summary");
        Assert.DoesNotContain("Files read", summary.GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.All(provider.Requests, request => Assert.DoesNotContain("private-provenance-value", string.Join('\n', request.Messages.Select(message => message.GetModelVisibleContent())), StringComparison.Ordinal));
        Assert.DoesNotContain(requests[2].Messages, message => message.ToolCallId == requests[1].Messages.First(message => message.Role == ModelMessageRole.Tool).ToolCallId);
        var retrieved = Assert.Single(requests[3].Messages, message => message.Role == ModelMessageRole.Tool && message.ToolName == ChildAgentEvidenceTool.ToolId);
        Assert.Equal(content, retrieved.GetModelVisibleContent());
        Assert.Contains(evidence.Snapshot(plan.Provenance.SessionId), item => item.EvidenceId == provider.FirstEvidenceId);
        if (contentCharacters > 262_144)
        {
            var candidate = Assert.Single(provider.Requests, request => request.Messages.Any(message => message.SectionId == "active-turn-compaction-policy"));
            Assert.True(candidate.WireEstimate?.WireInputTokens > 65_536);
        }
    }

    /// <summary>Off switches avoid auxiliary calls; rejected replacements keep exact history and back off.</summary>
    [Theory]
    [InlineData(false, false, 0, 0)]
    [InlineData(true, false, 0, 0)]
    [InlineData(true, true, 0, 1)]
    [InlineData(true, false, 1, 1)]
    public async Task History_DisabledOrFailed_KeepsOriginalMessages(bool enabled, bool trigger, int percent, int expectedAttempts)
    {
        var options = new ChildAgentCompactionOptions
        {
            Enabled = enabled,
            TriggerTokens = trigger ? 1 : 0,
            TriggerPercent = percent,
            RecentTokens = 1,
            MinimumSavingsTokens = 1,
        };
        var messages = new List<ModelMessage> { HistoryMessage("unchanged task", ModelMessageRole.User) };
        var history = new ChildAgentHistory(messages, options, TestPromptLoader.Instance);
        for (var round = 0; round < 2; round++)
        {
            var start = messages.Count;
            messages.Add(ChildAgentPrompt.CreateToolCallMessage($"call-{round}", new ToolRequestModelOutput("inspect", "{}")));
            messages.Add(ChildAgentPrompt.CreateToolResultMessage($"call-{round}", "inspect", new string('x', 8_000)));
            history.RecordExchange(start, round, [EvidenceId.New()]);
            history.MarkDelivered();
        }

        var original = messages.ToArray();
        var compactor = new FailingCompactor();
        var assignment = CreateAssignment(ModelProfileId.New(), []);
        var model = new AgentModelSelection(assignment.Policy.ModelProfileId, ReasoningLevel.None, [])
        {
            ContextWindowTokens = 64_000,
            MaximumOutputTokens = 4_096,
            OutputReserveTokens = 1_024,
        };
        var tools = ModelWireEstimator.EstimateTools([], ToolTransportMode.Native);
        await history.CompactAsync(assignment, model, tools, 2, compactor, new NoopCompactionObserver(), TestContext.Current.CancellationToken);
        await history.CompactAsync(assignment, model, tools, 3, compactor, new NoopCompactionObserver(), TestContext.Current.CancellationToken);

        Assert.Equal(expectedAttempts, compactor.Attempts);
        Assert.Equal(original, messages);
        Assert.Equal(0, history.RewriteGeneration);
    }

    /// <summary>Evidence lookup rejects sibling, unknown, stale, and cross-session identities.</summary>
    [Fact]
    public async Task EvidenceRead_OnlyPreviouslyDeliveredAndCurrentEvidenceIsAccessible()
    {
        await using var events = new DomainEventStream();
        var store = new EvidenceStore(events, new SecretOutputSanitizer());
        var assignment = CreateAssignment(ModelProfileId.New(), []);
        var plan = CreatePlan(assignment);
        var id = EvidenceId.New();
        await store.AddAsync(CreateParentEvidence(plan, id, "exact code", EvidenceSensitivity.None) with { InvalidationKeys = ["repository"] });
        var tool = new ChildAgentEvidenceTool(store, plan.Provenance.SessionId, assignment.ChildRunId, new HashSet<EvidenceId> { id }, TestPromptLoader.Instance);
        var context = CreateParentContext(plan, []) with { RunId = assignment.ChildRunId };
        var result = await tool.ExecuteAsync(new ChildAgentEvidenceInput(id.Value), context);
        Assert.Equal("exact code", result.Value);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tool.ExecuteAsync(new ChildAgentEvidenceInput(Guid.NewGuid()), context));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tool.ExecuteAsync(new ChildAgentEvidenceInput(id.Value), context with { RunId = RunId.New() }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tool.ExecuteAsync(new ChildAgentEvidenceInput(id.Value), context with { SessionId = SessionId.New() }));
        store.QueueInvalidation(plan.Provenance.SessionId, "repository", "changed");
        await store.ApplyInvalidationsAsync(plan.Provenance.SessionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => tool.ExecuteAsync(new ChildAgentEvidenceInput(id.Value), context));
    }

    /// <summary>Repeated summaries preserve steering and replace the prior summary instead of accumulating it.</summary>
    [Fact]
    public async Task History_RepeatedCompaction_PreservesSteeringAndRecentPairs()
    {
        var task = HistoryMessage("original task", ModelMessageRole.User);
        var messages = new List<ModelMessage> { task };
        var options = new ChildAgentCompactionOptions
        {
            TriggerTokens = 1,
            TriggerPercent = 0,
            RecentTokens = 1,
            MinimumSavingsTokens = 1,
        };
        var history = new ChildAgentHistory(messages, options, TestPromptLoader.Instance);
        void AddExchange(int round)
        {
            var start = messages.Count;
            messages.Add(ChildAgentPrompt.CreateToolCallMessage($"call-{round}", new ToolRequestModelOutput("inspect", "{}")));
            messages.Add(ChildAgentPrompt.CreateToolResultMessage($"call-{round}", "inspect", new string('x', 8_000)));
            history.RecordExchange(start, round, [EvidenceId.New()]);
            history.MarkDelivered();
        }

        AddExchange(0);
        AddExchange(1);
        var steering = HistoryMessage("Keep the public API unchanged", ModelMessageRole.User);
        messages.Add(steering);
        var compactor = new SuccessfulCompactor();
        var assignment = CreateAssignment(ModelProfileId.New(), []);
        var model = new AgentModelSelection(assignment.Policy.ModelProfileId, ReasoningLevel.None, [])
        {
            ContextWindowTokens = 64_000,
            MaximumOutputTokens = 4_096,
            OutputReserveTokens = 1_024,
        };
        var tools = ModelWireEstimator.EstimateTools([], ToolTransportMode.Native);
        await history.CompactAsync(assignment, model, tools, 2, compactor, new NoopCompactionObserver(), TestContext.Current.CancellationToken);
        AddExchange(3);
        await history.CompactAsync(assignment, model, tools, 3, compactor, new NoopCompactionObserver(), TestContext.Current.CancellationToken);
        Assert.Equal(1, history.RewriteGeneration);
        await history.CompactAsync(assignment, model, tools, 5, compactor, new NoopCompactionObserver(), TestContext.Current.CancellationToken);

        Assert.Equal(2, history.RewriteGeneration);
        Assert.Same(task, messages[0]);
        Assert.Single(messages, message => ReferenceEquals(message, steering));
        Assert.Single(messages, message => message.SectionId == "active-turn-summary");
        Assert.Single(messages, message => message.SectionId == "child-evidence-index");
        Assert.Equal(2, messages.Count(message => message.ToolCallId == "call-3"));
        Assert.DoesNotContain(messages, message => message.ToolCallId is "call-0" or "call-1");
    }

    private static ModelMessage HistoryMessage(string text, ModelMessageRole role) => new()
    {
        Role = role,
        SectionId = "test-pinned",
        Content = [new ModelContentPart { Content = text }],
    };

    private sealed class CompactingProvider : IModelProvider
    {
        private readonly string _toolId;
        private readonly IModelSelectionPolicy _selection;
        private int _round;

        public CompactingProvider(string toolId, ModelProfile profile)
        {
            _toolId = toolId;
            _selection = new DefaultModelSelectionPolicy(new ConfiguredModelCatalog([profile]));
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public int Summaries { get; private set; }

        public EvidenceId FirstEvidenceId { get; private set; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Assert.Equal(request.ResolvedProfileId, _selection.Resolve(new ModelSelectionRequest
            {
                WorkloadClass = request.WorkloadClass,
                RequiredCapabilities = request.RequiredCapabilities,
                Constraints = request.SelectionConstraints,
                PreferredProfileId = request.ResolvedProfileId,
            }).ProfileId);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Messages.Any(message => message.SectionId == "active-turn-compaction-policy"))
            {
                Summaries++;
                yield return new ModelChunk { Text = "## Goal\nInspect behavior.\n## Critical Context\nThe metadata inspection succeeded; continue from recent evidence." };
            }
            else
            {
                if (_round == 1)
                {
                    using var content = JsonDocument.Parse(request.Messages.Last(message => message.Role == ModelMessageRole.Tool).GetModelVisibleContent());
                    FirstEvidenceId = new EvidenceId(content.RootElement.GetProperty("evidenceId").GetGuid());
                }

                if (_round < 2)
                {
                    yield return new ModelChunk { Output = new ToolRequestModelOutput(_toolId, "{}") };
                }
                else if (_round == 2)
                {
                    yield return new ModelChunk { Output = new ToolRequestModelOutput(ChildAgentEvidenceTool.ToolId, JsonSerializer.Serialize(new { evidenceId = FirstEvidenceId.Value })) };
                    yield return new ModelChunk { Output = new ToolRequestModelOutput(_toolId, "{}") };
                }
                else
                {
                    yield return new ModelChunk { Text = "An unrestricted final response." };
                }

                _round++;
            }

            yield return new ModelChunk { Usage = new ModelUsage(10, 2) };
        }
    }

    private sealed class FailingCompactor : IActiveTurnCompactor
    {
        public int Attempts { get; private set; }

        public Task<ActiveTurnCompactionResult> CompactAsync(ActiveTurnCompactionRequest request, IActiveTurnCompactionAttemptObserver attemptObserver, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new ModelProviderException("Test provider failure");
        }
    }

    private sealed class SuccessfulCompactor : IActiveTurnCompactor
    {
        public Task<ActiveTurnCompactionResult> CompactAsync(ActiveTurnCompactionRequest request, IActiveTurnCompactionAttemptObserver attemptObserver, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ActiveTurnCompactionResult
            {
                Outcome = ActiveTurnCompactionOutcome.Completed,
                Rationale = "test summary",
                Summary = new ActiveTurnCompactionSummary
                {
                    Version = (request.PriorSummary?.Version ?? 0) + 1,
                    ThroughGroupSequence = request.EligiblePrefix[^1].Sequence,
                    CoveredGroupSequences = (request.PriorSummary?.CoveredGroupSequences ?? []).Concat(request.EligiblePrefix.Select(group => group.Sequence)).ToArray(),
                    Content = "Working notes about completed inspections.",
                    ContentHash = "test-summary",
                    FilesRead = request.EligiblePrefix.SelectMany(group => group.FilesRead).ToArray(),
                    FilesChanged = [],
                },
            });
        }
    }

    private sealed class NoopCompactionObserver : IActiveTurnCompactionAttemptObserver
    {
        public Task BeforeProviderCallAsync(ActiveTurnCompactionRequest request, int attempt, Guid invocationId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task AfterProviderCallAsync(ActiveTurnCompactionRequest request, int attempt, Guid invocationId, ActiveTurnCompactionAttemptOutcome outcome, ModelUsage? usage, TimeSpan duration, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
