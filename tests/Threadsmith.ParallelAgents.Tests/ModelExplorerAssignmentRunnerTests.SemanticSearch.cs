namespace Threadsmith.ParallelAgents.Tests;

using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class ModelExplorerAssignmentRunnerTests
{
    /// <summary>Children correct broad C# discovery, allow known-file reads, and retain semantic attempts across rounds.</summary>
    [Theory]
    [InlineData(null, true)]
    [InlineData(".", true)]
    [InlineData("src/Widget.cs", false)]
    public async Task RunAsync_SearchForCSharpSymbol_UsesSharedAdmission(string? path, bool requiresSemantic)
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var search = new InspectMetadataTool(toolId: "search");
        var semantic = new InspectMetadataTool(toolId: "code_explore");
        var registry = new ToolRegistry([search, semantic]);
        string[] toolIds = [search.Definition.Id, semantic.Definition.Id];
        var assignment = CreateAssignment(profile.Id, toolIds);
        var plan = CreatePlan(assignment);
        var call = new ToolRequestModelOutput("search", path is null
            ? "{\"query\":\"Widget.cs\"}"
            : JsonSerializer.Serialize(new { query = "Widget.cs", path }));
        var semanticCall = new ToolRequestModelOutput("code_explore", "{}");
        var provider = new ToolBatchSequenceProvider(requiresSemantic
            ? [[call], [semanticCall], [call], []] : [[call], []]);
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
        Assert.Equal(requiresSemantic ? 1 : 0, outcome.Usage.Corrections);
        Assert.NotNull(search.LastInvocationContext);
        Assert.Equal(requiresSemantic ? 2 : 1, evidence.Snapshot(plan.Provenance.SessionId).Count);
        if (requiresSemantic)
        {
            Assert.Contains(provider.Requests[1].Messages, message => message.IsError == true
                && message.GetModelVisibleContent().Contains("code_explore", StringComparison.Ordinal));
        }
    }

    /// <summary>Declaration keywords are matched as tokens; prose substrings do not force semantic lookup.</summary>
    [Theory]
    [InlineData("structured output")]
    [InlineData("recording failures")]
    public async Task RunAsync_SearchForDeclarationKeywordSubstring_DoesNotForceSemanticLookup(string query)
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var search = new InspectMetadataTool(toolId: "search");
        var semantic = new InspectMetadataTool(toolId: "code_explore");
        var registry = new ToolRegistry([search, semantic]);
        string[] toolIds = [search.Definition.Id, semantic.Definition.Id];
        var assignment = CreateAssignment(profile.Id, toolIds);
        var plan = CreatePlan(assignment);
        var call = new ToolRequestModelOutput("search", JsonSerializer.Serialize(new { query }));
        var provider = new ToolBatchSequenceProvider([[call], []]);
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
        Assert.Equal(0, outcome.Usage.Corrections);
        Assert.NotNull(search.LastInvocationContext);
        Assert.Null(semantic.LastInvocationContext);
        Assert.Single(evidence.Snapshot(plan.Provenance.SessionId));
    }

    /// <summary>Ref-based reads remain valid when the review target differs from the active semantic workspace.</summary>
    [Fact]
    public async Task RunAsync_RemoteRevisionRead_DoesNotForceActiveWorkspaceSemanticTools()
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var process = new InspectMetadataTool(toolId: "run_process");
        var semantic = new InspectMetadataTool(toolId: "code_explore");
        var registry = new ToolRegistry([process, semantic]);
        string[] toolIds = [process.Definition.Id, semantic.Definition.Id];
        var assignment = CreateAssignment(profile.Id, toolIds) with
        {
            InitialContext = "Review origin/feature against origin/main. The active checkout is main; use git show for target evidence.",
        };
        var plan = CreatePlan(assignment);
        var provider = new ToolBatchSequenceProvider([
            [new ToolRequestModelOutput("run_process", "{\"query\":\"git show origin/feature:src/Widget.cs\"}")], []]);
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
        Assert.Equal(0, outcome.Usage.Corrections);
        Assert.Null(semantic.LastInvocationContext);
        Assert.Contains("confirmed to match that revision", provider.Requests[0].Messages[0].GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.Single(evidence.Snapshot(plan.Provenance.SessionId));
    }
}
