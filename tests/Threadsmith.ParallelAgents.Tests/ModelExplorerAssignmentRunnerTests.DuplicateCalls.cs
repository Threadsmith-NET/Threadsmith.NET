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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_AllowedDuplicatesReuseInheritedOperationScope(bool invalidArguments)
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var tool = new InspectMetadataTool(allowDuplicates: true) { Failure = invalidArguments ? new ToolArgumentValidationException("Invalid fixture arguments.") : null };
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

        var outcome = await runner.RunAsync(plan, assignment, TestContext.Current.CancellationToken);
        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Equal(invalidArguments ? 1 : 0, outcome.Usage.Corrections);
        Assert.Equal(invalidArguments ? 1 : 2, evidence.Snapshot(plan.Provenance.SessionId).Count);
        Assert.Same(scope, tool.LastInvocationContext?.OperationScope);
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
