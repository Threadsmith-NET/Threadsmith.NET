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
    /// <summary>Verifies exact-call limits through the owning admission and configuration boundaries.</summary>
    [Theory]
    [InlineData(0, 1, false)]
    [InlineData(1, 1, false)]
    [InlineData(2, 2, false)]
    [InlineData(3, 3, false)]
    [InlineData(3, 3, true)]
    public async Task RunAsync_ExactInvocationQuotaUsesSharedCorrection(int limit, int accepted, bool duplicateBatch)
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var tool = new InspectMetadataTool();
        var registry = new ToolRegistry([tool], runtimeOptions: new ToolRuntimeOptions
        {
            ByTool = [new ToolRuntimeToolOverride { ToolId = tool.Definition.Id, NumberOfExactToolInvocationsPerTurn = limit }],
        });
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, [tool.Definition.Id]);
        var plan = CreatePlan(assignment);
        var provider = new ExactInvocationProvider(tool.Definition.Id, accepted + 1, duplicateBatch);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, [tool.Definition.Id]),
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId));

        var outcome = await runner.RunAsync(plan, assignment, TestContext.Current.CancellationToken);

        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Equal(accepted, evidence.Snapshot(plan.Provenance.SessionId).Count(item => item.RunId == assignment.ChildRunId));
        Assert.Contains(provider.Requests[^1].Messages, message => message.IsError == true
            && message.GetModelVisibleContent().Contains($"already has {accepted} accepted invocations", StringComparison.Ordinal));
        if (duplicateBatch)
        {
            Assert.Contains(provider.Requests[1].Messages, message => message.IsError == true
                && message.GetModelVisibleContent().Contains("occurs more than once", StringComparison.Ordinal));
        }
    }

    private sealed class ExactInvocationProvider : IModelProvider
    {
        private readonly string _tool;
        private readonly int _attempts;
        private readonly bool _duplicate;

        public ExactInvocationProvider(string tool, int attempts, bool duplicate)
        {
            _tool = tool;
            _attempts = attempts;
            _duplicate = duplicate;
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            if (Requests.Count <= _attempts + (_duplicate ? 1 : 0))
            {
                yield return new ModelChunk { Output = new ToolRequestModelOutput(_tool, "{}") };
                if (_duplicate && Requests.Count == 1)
                {
                    yield return new ModelChunk { Output = new ToolRequestModelOutput(_tool, "{}") };
                }
            }
            else
            {
                yield return new ModelChunk { Text = "done" };
            }
        }
    }
}
