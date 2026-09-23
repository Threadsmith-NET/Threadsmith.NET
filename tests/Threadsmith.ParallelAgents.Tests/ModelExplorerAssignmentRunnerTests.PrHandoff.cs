namespace Threadsmith.ParallelAgents.Tests;

using System.Runtime.CompilerServices;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Tools.PullRequests;
using Xunit;

public sealed partial class ModelExplorerAssignmentRunnerTests
{
    /// <summary>A child can read its parent's complete captured PR without a provider tool.</summary>
    [Fact]
    public async Task RunAsync_CapturedPrEvidence_ReachesChildWithoutRefetch()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scope = new ToolOperationScope(CancellationToken.None);
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var inspect = new InspectMetadataTool();
        var registry = new ToolRegistry([inspect]);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, [inspect.Definition.Id]);
        var plan = CreatePlan(assignment);
        var parent = CreateParentContext(plan, [inspect.Definition.Id]) with
        {
            Invocation = CreateParentContext(plan, [inspect.Definition.Id]).Invocation with
            {
                OperationScope = scope,
            },
        };
        var snapshotId = Guid.NewGuid();
        var diff = "@@ -1 +1 @@\n-old\n+new\n" + new string('x', 14_000);
        var snapshot = new PrFetchOutput(
            "account",
            PrFetchKind.Diff,
            snapshotId,
            DateTimeOffset.UtcNow,
            new PullRequestMetadata(
                "https://example.test/pr/7",
                "repo",
                "7",
                "Test",
                "{\"password\":\"fixture-pr-secret\"}",
                "open",
                "repo",
                "source",
                "repo",
                "destination",
                "revision",
                1),
            new PullRequestPage("complete", [new PullRequestFile("src/Example.cs", null, "modified")], diff, []),
            false,
            true);
        scope.GetOrCreate(PrEvidenceRegistry.ScopeKey, static () => new PrEvidenceRegistry())
            .Register(plan.Provenance.SessionId, plan.Provenance.ParentRunId, snapshot);
        var provider = new CapturedPrReadProvider(snapshotId);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            parent,
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId));

        // Act
        var outcome = await runner.RunAsync(plan, assignment);

        // Assert
        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Contains(new EvidenceId(snapshotId), outcome.DeliveredEvidenceIds);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains(provider.Requests[0].Messages, message =>
            message.SectionId == "child-assignment"
            && message.GetModelVisibleContent().Contains(snapshotId.ToString("D"), StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Requests[0].Tools, tool => tool.Name == "pr_fetch");
        var toolResult = Assert.Single(provider.Requests[1].Messages, message =>
            message.Role == ModelMessageRole.Tool && message.ToolName == ChildAgentEvidenceTool.ToolId);
        Assert.Contains(diff, toolResult.GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-pr-secret", toolResult.GetModelVisibleContent(), StringComparison.Ordinal);
        var reader = new ChildAgentEvidenceTool(
            evidence,
            plan.Provenance.SessionId,
            assignment.ChildRunId,
            new HashSet<EvidenceId> { new(snapshotId) },
            TestPromptLoader.Instance,
            new Dictionary<EvidenceId, PrFetchOutput> { [new(snapshotId)] = snapshot });
        var childContext = parent with { RunId = assignment.ChildRunId };
        var full = await reader.ExecuteAsync(new ChildAgentEvidenceInput(snapshotId), childContext);
        var diffStart = Array.FindIndex(
            full.Value.Split('\n'),
            line => line.TrimEnd('\r').Equals("Diff:", StringComparison.Ordinal)) + 2;
        var range = await reader.ExecuteAsync(new ChildAgentEvidenceInput(snapshotId, diffStart, diffStart + 1), childContext);
        Assert.Equal("@@ -1 +1 @@\n-old", range.Value.Replace("\r\n", "\n", StringComparison.Ordinal));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.ExecuteAsync(
            new ChildAgentEvidenceInput(snapshotId),
            childContext with { RunId = RunId.New() }));
        Assert.Empty(scope.GetOrCreate(PrEvidenceRegistry.ScopeKey, static () => new PrEvidenceRegistry())
            .Snapshot(plan.Provenance.SessionId, RunId.New()));
    }

    private sealed class CapturedPrReadProvider : IModelProvider
    {
        private readonly Guid _snapshotId;

        public CapturedPrReadProvider(Guid snapshotId)
        {
            _snapshotId = snapshotId;
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (Requests.Count == 1)
            {
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput(
                        ChildAgentEvidenceTool.ToolId,
                        "{\"evidenceId\":\"" + _snapshotId.ToString("D") + "\"}"),
                };
            }
            else
            {
                yield return new ModelChunk { Text = "Completed PR review." };
            }
        }
    }
}
