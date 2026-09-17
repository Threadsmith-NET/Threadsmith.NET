namespace Threadsmith.ParallelAgents.Tests;

using System.Runtime.CompilerServices;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class ModelExplorerAssignmentRunnerTests
{
    /// <summary>Opted-in repeated child calls retain inherited ownership and ordinary evidence accounting.</summary>
    [Fact]
    public async Task RunAsync_AllowedDuplicatesReuseInheritedOperationScope()
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var tool = new InspectMetadataTool(allowDuplicates: true);
        var registry = new ToolRegistry([tool]);
        var assignment = CreateAssignment(profile.Id, [tool.Definition.Id]);
        var plan = CreatePlan(assignment);
        var call = new ToolRequestModelOutput(tool.Definition.Id, "{}");
        var provider = new ToolBatchSequenceProvider([[call], [call], []]);
        await using var scope = new ToolOperationScope(CancellationToken.None);
        var parent = CreateParentContext(plan, [tool.Definition.Id]);
        parent = parent with { Invocation = parent.Invocation with { OperationScope = scope } };
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            parent,
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId));

        var outcome = await runner.RunAsync(plan, assignment);
        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Equal(0, outcome.Usage.Corrections);
        Assert.Equal(2, evidence.Snapshot(plan.Provenance.SessionId).Count);
        Assert.Same(scope, tool.LastInvocationContext?.OperationScope);
    }

    /// <summary>Opt-in reuse does not permit identical sibling calls in one child response.</summary>
    [Fact]
    public async Task RunAsync_AllowedDuplicatesStillRejectSameBatchSiblings()
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var tool = new InspectMetadataTool(allowDuplicates: true);
        var registry = new ToolRegistry([tool]);
        var assignment = CreateAssignment(profile.Id, [tool.Definition.Id]);
        var plan = CreatePlan(assignment);
        var call = new ToolRequestModelOutput(tool.Definition.Id, "{}");
        var provider = new ToolBatchSequenceProvider([[call, call], [call], []]);
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
        Assert.Equal(1, outcome.Usage.Corrections);
        Assert.Single(evidence.Snapshot(plan.Provenance.SessionId));
        Assert.Contains(provider.Requests[1].Messages, message => message.IsError == true
            && message.GetModelVisibleContent().Contains("already called with these arguments", StringComparison.Ordinal));
    }

    /// <summary>Children share the parent's configured consecutive correction limit and reset it after accepted tools.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_DuplicateCorrections_UseSharedLimitAndResetAfterProgress(bool makeProgress)
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var tool = new InspectMetadataTool();
        var registry = new ToolRegistry([tool]);
        var assignment = CreateAssignment(profile.Id, [tool.Definition.Id]) with
        {
            Budget = AgentResourceBudget.CreateTelemetryOnly(TimeSpan.Zero),
        };
        var plan = CreatePlan(assignment);
        var call = new ToolRequestModelOutput(tool.Definition.Id, "{}");
        var fresh = new ToolRequestModelOutput(tool.Definition.Id, "{\"path\":\"other.cs\"}");
        var provider = new ToolBatchSequenceProvider(makeProgress
            ? [[call], [call], [fresh], [fresh], []]
            : [[call], [call], [call]]);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, [tool.Definition.Id]),
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId),
            executionLimits: new ExecutionLimits { MaxCorrectiveTurns = 1 });

        if (makeProgress)
        {
            var outcome = await runner.RunAsync(plan, assignment);
            Assert.Equal(AgentRunStatus.Completed, outcome.Status);
            Assert.Equal(2, outcome.Usage.Corrections);
            Assert.Equal(2, evidence.Snapshot(plan.Provenance.SessionId).Count);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<MalformedInvocationException>(() => runner.RunAsync(plan, assignment));
            Assert.Contains("already called with these arguments", exception.Message, StringComparison.Ordinal);
            Assert.Equal(3, provider.Requests.Count);
            Assert.Single(evidence.Snapshot(plan.Provenance.SessionId));
        }

        Assert.Contains(provider.Requests[2].Messages, message => message.IsError == true
            && message.GetModelVisibleContent().Contains("Corrective turn 1 of 1", StringComparison.Ordinal));
    }

    /// <summary>An ordinary tool execution error is delivered to the child, which can continue using another call.</summary>
    [Fact]
    public async Task RunAsync_ToolExecutionFailure_IsReturnedToModelWithoutFailingChild()
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var tool = new InspectMetadataTool { Failure = new IOException("Fixture file is unavailable.") };
        var sibling = new InspectMetadataTool(toolId: "alternative_inspection");
        var registry = new ToolRegistry([tool, sibling]);
        string[] toolIds = [tool.Definition.Id, sibling.Definition.Id];
        var assignment = CreateAssignment(profile.Id, toolIds);
        var plan = CreatePlan(assignment);
        var provider = new ToolBatchSequenceProvider([
            [new ToolRequestModelOutput(tool.Definition.Id, "{}")],
            [new ToolRequestModelOutput(sibling.Definition.Id, "{}")], []]);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, toolIds),
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId));

        var outcome = await runner.RunAsync(plan, assignment);

        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Contains(provider.Requests[1].Messages, message => message.ToolName == tool.Definition.Id
            && message.IsError == true);
        Assert.NotNull(sibling.LastInvocationContext);
        Assert.Equal(3, provider.Requests.Count);
    }

    /// <summary>Duplicate calls return feedback to the same child, which can correct and resubmit its batch.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_DuplicateToolBatch_ContinuesWithoutExecutingRejectedSiblings(bool duplicateWithinBatch)
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile() with { IntendedWorkloadClasses = [WorkloadClass.Review] };
        var tool = new InspectMetadataTool();
        var sibling = new InspectMetadataTool(toolId: "fresh_inspection");
        var registry = new ToolRegistry([tool, sibling]);
        string[] toolIds = [tool.Definition.Id, sibling.Definition.Id];
        var assignment = CreateAssignment(profile.Id, toolIds) with
        {
            Role = AgentRole.BugReviewer,
            Budget = AgentResourceBudget.CreateTelemetryOnly(TimeSpan.Zero),
        };
        var plan = CreatePlan(assignment);
        var call = new ToolRequestModelOutput(tool.Definition.Id, "{}");
        var fresh = new ToolRequestModelOutput(sibling.Definition.Id, "{}");
        ToolRequestModelOutput[][] batches = duplicateWithinBatch
            ? [[fresh, call, call]] : [[call], [fresh, call]];
        var provider = new ToolBatchSequenceProvider([.. batches, [fresh], []]);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, toolIds),
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId),
            options: CreateOptions() with { EnforceOperationalLimits = false });

        var outcome = await runner.RunAsync(plan, assignment);

        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Equal(1, outcome.Usage.Corrections);
        Assert.Equal(batches.Length + 2, provider.Requests.Count);
        var correctionRequest = provider.Requests[batches.Length];
        Assert.Contains(correctionRequest.Messages, message => message.IsError == true
            && message.GetModelVisibleContent().Contains("already called with these arguments", StringComparison.Ordinal));
        Assert.All(provider.Requests, request => Assert.Equal(provider.Requests[0].RunId, request.RunId));
        Assert.NotNull(sibling.LastInvocationContext);
        Assert.Equal(duplicateWithinBatch ? 1 : 2, evidence.Snapshot(plan.Provenance.SessionId).Count);
        Assert.Equal(duplicateWithinBatch, tool.LastInvocationContext is null);
    }

    /// <summary>Rejected calls stay retryable and accepted calls belong only to their child.</summary>
    [Fact]
    public async Task RunAsync_RejectedBatchCanBeCorrected_AndHistoryIsLocalToEachChild()
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var tool = new InspectMetadataTool();
        var registry = new ToolRegistry([tool]);
        var assignment = CreateAssignment(profile.Id, [tool.Definition.Id]);
        var next = assignment with { AssignmentId = AgentAssignmentId.New(), ChildRunId = RunId.New() };
        var plan = CreatePlan(assignment) with { Assignments = [assignment, next] };
        var call = new ToolRequestModelOutput(tool.Definition.Id, "{}");
        var invalid = new ToolRequestModelOutput(tool.Definition.Id, "{\"unexpected\":true}");
        var provider = new ToolBatchSequenceProvider([[call, invalid], [call], [], [call], []]);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, [tool.Definition.Id]),
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId));

        var outcome = await runner.RunAsync(plan, assignment);
        var nextOutcome = await runner.RunAsync(plan, next);

        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Equal(1, outcome.Usage.Corrections);
        Assert.Equal(AgentRunStatus.Completed, nextOutcome.Status);
        Assert.Equal(0, nextOutcome.Usage.Corrections);
        Assert.Equal(2, evidence.Snapshot(plan.Provenance.SessionId).Count);
        Assert.Equal(5, provider.Requests.Count);
    }

    /// <summary>Compacting prior tool results does not reset duplicate-call protection.</summary>
    [Fact]
    public async Task RunAsync_DuplicateAfterCompaction_IsStillRejected()
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile() with { ContextWindow = 64_000 };
        var tool = new InspectMetadataTool(new string('x', 12_000), maximumOutputBytes: 64_000);
        var registry = new ToolRegistry([tool]);
        var assignment = CreateAssignment(profile.Id, [tool.Definition.Id]);
        var plan = CreatePlan(assignment);
        var provider = new CompactingProvider(tool.Definition.Id, profile) { RepeatAfterCompaction = true };
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, [tool.Definition.Id]),
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId),
            options: CreateOptions() with
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
        Assert.Equal(1, outcome.Usage.Corrections);
        Assert.Contains(provider.Requests[^1].Messages, message => message.IsError == true
            && message.GetModelVisibleContent().Contains("already called with these arguments", StringComparison.Ordinal));
        Assert.Equal(1, provider.Summaries);
        Assert.Equal(1, provider.Requests[^1].HistoryRewriteGeneration);
        Assert.Equal(2, evidence.Snapshot(plan.Provenance.SessionId).Count);
    }

    private sealed class ToolBatchSequenceProvider : IModelProvider
    {
        private readonly IReadOnlyList<ToolRequestModelOutput[]> _batches;

        public ToolBatchSequenceProvider(IReadOnlyList<ToolRequestModelOutput[]> batches)
        {
            _batches = batches;
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = _batches[Requests.Count];
            Requests.Add(request);
            await Task.Yield();
            foreach (var call in batch)
            {
                yield return new ModelChunk { Output = call };
            }

            if (batch.Length == 0)
            {
                yield return new ModelChunk { Text = "Review complete." };
            }
        }
    }
}
