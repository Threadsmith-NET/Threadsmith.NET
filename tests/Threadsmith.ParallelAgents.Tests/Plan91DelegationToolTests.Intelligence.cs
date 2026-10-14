namespace Threadsmith.ParallelAgents.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

public sealed partial class Plan91DelegationToolTests
{
    /// <summary>Only final onboarding coverage admits investigators through both boundaries.</summary>
    [Theory]
    [InlineData(RepositoryIntelligenceOperation.Onboard, true, true)]
    [InlineData(RepositoryIntelligenceOperation.Onboard, false, false)]
    [InlineData(RepositoryIntelligenceOperation.Refresh, true, false)]
    [InlineData(RepositoryIntelligenceOperation.Archeology, true, false)]
    [InlineData(RepositoryIntelligenceOperation.Onboard, true, false, true)]
    public async Task IntelligenceDelegation_UsesFinalCoverageGateAtInvocationAndPlanning(
        RepositoryIntelligenceOperation operation, bool coverage, bool allowed, bool cancelled = false)
    {
        var tool = new MetadataTool("delegate_agents", ToolCategory.Workflow, ToolSideEffect.ReadOnly, subagentAvailable: false);
        var registry = new ToolRegistry([tool]);
        var snapshots = new ConversationToolSnapshotStore();
        var workspace = WorkspaceId.New();
        var context = CreateExecutionContext(workspace, ConversationSensitivity.None, registry, snapshots);
        await using var intelligence = new InvestigatorIntelligenceRun { Operation = operation, IsCoverageReviewActive = coverage, CancellationToken = new CancellationToken(cancelled) };
        context = context with { Invocation = context.Invocation with { IntelligenceRun = intelligence } };
        var factory = CreateIntelligencePlanFactory(workspace, snapshots);
        var input = new DelegateAgentsInput { Agents = [InvestigationRequest()] };
        if (allowed)
        {
            Assert.Single(factory.Create(input, context).Assignments);
        }
        else
        {
            Assert.Throws<UnauthorizedAccessException>(() => factory.Create(input, context));
        }

        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(
            registry,
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            new SecretOutputSanitizer(),
            NullLogger<ToolInvocationPipeline>.Instance);
        var result = await pipeline.InvokeAsync(new ToolInvocationRequest
        {
            SessionId = context.SessionId,
            RunId = context.RunId,
            ToolId = tool.Definition.Id,
            ArgumentsJson = "{}",
            Context = context.Invocation,
            Phase = RunPhase.EvidenceCollection,
        });
        Assert.Equal(allowed, result.Succeeded);
        Assert.Equal(allowed ? 1 : 0, tool.Executions);
    }

    /// <summary>Investigators cannot write memories or register supporting receipts for the parent.</summary>
    [Fact]
    public async Task IntelligenceInvestigators_AreNarrowAndCannotSupplyParentReceipts()
    {
        var memory = new MetadataTool("memories", ToolCategory.Workflow, ToolSideEffect.ReadOnly);
        var registry = new ToolRegistry([
            new ReadFileTool(TestPromptLoader.Instance, new SecretOutputSanitizer()),
            memory,
            new MetadataTool("run_process", ToolCategory.ProcessExecution, ToolSideEffect.ReadOnly),
            new MetadataTool("delegate_agents", ToolCategory.Workflow, ToolSideEffect.ReadOnly, subagentAvailable: false),
        ]);
        var snapshots = new ConversationToolSnapshotStore();
        var workspace = WorkspaceId.New();
        var context = CreateExecutionContext(workspace, ConversationSensitivity.None, registry, snapshots);
        await using var intelligence = new InvestigatorIntelligenceRun();
        context = context with { Invocation = context.Invocation with { IntelligenceRun = intelligence } };
        var factory = CreateIntelligencePlanFactory(workspace, snapshots);
        var input = new DelegateAgentsInput { Agents = [InvestigationRequest(), InvestigationRequest()] };
        var plan = factory.Create(input, context);
        Assert.Equal(2, plan.Assignments.Count);
        var assignment = plan.Assignments[0];
        Assert.False(assignment.Policy.IncludeParentEvidence);
        Assert.Equal(["read_file"], assignment.Policy.AllowedToolIds);
        var child = AgentToolPolicy.Scope(context.Invocation, plan, assignment, context.Invocation.RepositoryPath) with { RequestedBy = "model" };
        Assert.Same(intelligence, child.IntelligenceRun);
        Assert.Null(child.IntelligenceSourceObserver);
        Assert.Same(intelligence, context.Invocation.IntelligenceSourceObserver);

        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(
            registry,
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            new SecretOutputSanitizer(),
            NullLogger<ToolInvocationPipeline>.Instance);
        var request = new ToolInvocationRequest
        {
            SessionId = context.SessionId,
            RunId = assignment.ChildRunId,
            ToolId = "read_file",
            ArgumentsJson = "{\"path\":\"TestPromptLoader.cs\",\"startLine\":1,\"maximumLines\":5}",
            Context = child,
            Phase = RunPhase.EvidenceCollection,
        };
        // Read a real fixture source using the ordinary tool path, in the test project's directory.
        var sourceDirectory = Path.GetDirectoryName(FindTestSource())!;
        request = request with { Context = child with { RepositoryPath = sourceDirectory } };
        var childRead = await pipeline.InvokeAsync(request);
        Assert.True(childRead.Succeeded, childRead.Error);
        Assert.Equal(0, intelligence.SourceObservations);
        var parentRead = await pipeline.InvokeAsync(request with
        {
            RunId = context.RunId,
            Context = context.Invocation with { RepositoryPath = sourceDirectory },
        });
        Assert.True(parentRead.Succeeded, parentRead.Error);
        Assert.Equal(1, intelligence.SourceObservations);
        var deniedMemory = await pipeline.InvokeAsync(request with
        {
            ToolId = "memories",
            ArgumentsJson = "{}",
            Context = child with { AllowedToolIds = [] },
        });
        Assert.False(deniedMemory.Succeeded);
        Assert.Equal(0, memory.Executions);

        var evidence = new EvidenceStore(events, new SecretOutputSanitizer());
        var evidenceId = EvidenceId.New();
        await evidence.AddAsync(new Evidence
        {
            EvidenceId = evidenceId,
            SessionId = context.SessionId,
            RunId = assignment.ChildRunId,
            Kind = EvidenceKind.ToolResult,
            Content = "Previously read implementation details",
            Provenance = new EvidenceProvenance { Source = "read_file" },
            CollectedAt = DateTimeOffset.UtcNow,
        });
        var recoveryTool = new ChildAgentEvidenceTool(
            evidence,
            context.SessionId,
            assignment.ChildRunId,
            new HashSet<EvidenceId> { evidenceId },
            TestPromptLoader.Instance);
        var recoveryPipeline = new ToolInvocationPipeline(
            new ToolRegistry([recoveryTool]),
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            new SecretOutputSanitizer(),
            NullLogger<ToolInvocationPipeline>.Instance);
        var recovery = request with
        {
            ToolId = ChildAgentEvidenceTool.ToolId,
            ArgumentsJson = System.Text.Json.JsonSerializer.Serialize(new { evidenceId = evidenceId.Value }),
            Context = child with { AllowedToolIds = [ChildAgentEvidenceTool.ToolId] },
        };
        var recovered = await recoveryPipeline.InvokeAsync(recovery);
        Assert.True(recovered.Succeeded, recovered.Error);
        Assert.Contains("Previously read implementation details", recovered.ResultJson, StringComparison.Ordinal);
        Assert.False((await recoveryPipeline.InvokeAsync(recovery with { RunId = RunId.New() })).Succeeded);
        Assert.False((await recoveryPipeline.InvokeAsync(recovery with
        {
            ArgumentsJson = System.Text.Json.JsonSerializer.Serialize(new { evidenceId = Guid.NewGuid() }),
        })).Succeeded);

        Assert.Throws<ArgumentException>(() => factory.Create(input with { Agents = [InvestigationRequest(), InvestigationRequest(), InvestigationRequest()] }, context));
        Assert.Throws<ArgumentException>(() => factory.Create(input with { Agents = [InvestigationRequest() with { ToolAccess = DelegateAgentToolAccess.Inherit }] }, context));
        Assert.Throws<ArgumentException>(() => factory.Create(input with { Agents = [InvestigationRequest() with { Context = new string('x', 8193) }] }, context));
    }

    /// <summary>Read-only investigation does not capture or retain unrelated repository contents.</summary>
    [Fact]
    public async Task IntelligenceDelegation_WithoutBaseline_ReadsLiveFilesWithoutRepositoryCapture()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"threadsmith-readonly-{Guid.NewGuid():N}")).FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(repository, "Small.cs"), "// focused evidence", ct);
            await using var unrelated = new FileStream(Path.Combine(repository, "Large.cs"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            unrelated.SetLength((256L * 1024 * 1024) + 1);
            await using var events = new DomainEventStream();
            await using var workspaces = new TransactionalWorkspaceCoordinator(events);
            var workspace = WorkspaceId.New();
            var snapshots = new ConversationToolSnapshotStore();
            var sanitizer = new SecretOutputSanitizer();
            var registry = new ToolRegistry([new ReadFileTool(TestPromptLoader.Instance, sanitizer)]);
            var context = CreateExecutionContext(workspace, ConversationSensitivity.None, registry, snapshots);
            context = context with
            {
                Invocation = context.Invocation with { RepositoryPath = repository, IntelligenceRun = new InvestigatorIntelligenceRun() },
            };
            context = context with
            {
                Invocation = context.Invocation with
                {
                    ModelVisibleToolSnapshotId = snapshots.Capture(context.SessionId, context.RunId, registry.GetRegistrations(context.SessionId, context.RunId), context.Invocation),
                },
            };
            var factory = new DelegateAgentsPlanFactory(workspaces, new SessionModelPreferences(), snapshots, TestPromptLoader.Instance);
            var input = new DelegateAgentsInput { Agents = [InvestigationRequest()] };

            var plan = factory.Create(input, context);
            Assert.Null(plan.Provenance.BaselineIdentity);
            Assert.Equal(workspace, plan.Provenance.WorkspaceId);
            Assert.Equal(repository, plan.Provenance.RepositoryIdentity);
            var roundTrip = System.Text.Json.JsonSerializer.Deserialize<DelegationPlan>(System.Text.Json.JsonSerializer.Serialize(plan))!;
            DelegationPlanValidator.Validate(roundTrip);
            Assert.Null(roundTrip.Provenance.BaselineIdentity);
            var assignment = Assert.Single(plan.Assignments);
            var child = AgentToolPolicy.Scope(context.Invocation, plan, assignment, repository);
            var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
            var read = await pipeline.InvokeAsync(
                new ToolInvocationRequest
                {
                    SessionId = context.SessionId,
                    RunId = assignment.ChildRunId,
                    ToolId = "read_file",
                    ArgumentsJson = "{\"path\":\"Small.cs\"}",
                    Context = child,
                    Phase = RunPhase.EvidenceCollection,
                },
                ct);
            Assert.True(read.Succeeded, read.Error);
            Assert.Contains("focused evidence", read.ResultJson, StringComparison.Ordinal);
            Assert.Throws<KeyNotFoundException>(() => workspaces.GetWorkspace(workspace));

            // Neither inherited authority nor a forged checkpoint may bypass the mutation baseline.
            var ordinary = context with { Invocation = context.Invocation with { IntelligenceRun = null } };
            Assert.Throws<KeyNotFoundException>(() => factory.Create(
                input with
                {
                    Agents = [InvestigationRequest(), InvestigationRequest() with { ToolAccess = DelegateAgentToolAccess.Inherit }],
                },
                ordinary));
            Assert.Throws<InvalidDataException>(() => DelegationPlanValidator.Validate(plan with
            {
                Assignments = [assignment with { Mode = AgentRunMode.SharedWorkspace }],
            }));
            Assert.Throws<InvalidOperationException>(() => factory.Create(input, context with { SessionId = SessionId.New() }));
            Assert.Throws<InvalidOperationException>(() => factory.Create(input, context with { RunId = RunId.New() }));
            Assert.Throws<InvalidOperationException>(() => factory.Create(input, context with { Invocation = context.Invocation with { WorkspaceId = WorkspaceId.New() } }));
            Assert.Throws<InvalidOperationException>(() => factory.Create(input, context with { Invocation = context.Invocation with { RepositoryPath = Path.GetTempPath() } }));
            snapshots.Release(context.Invocation.ModelVisibleToolSnapshotId!.Value);
            Assert.Throws<InvalidOperationException>(() => factory.Create(input, context));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    internal sealed class InvestigatorIntelligenceRun : IRepositoryIntelligenceRun
    {
        public CancellationToken CancellationToken { get; init; }

        public RepositoryIntelligenceOperation Operation { get; init; } = RepositoryIntelligenceOperation.Onboard;

        public bool IsCoverageReviewActive { get; init; } = true;

        public RepositoryMemoryCollection? Collection => null;

        public string Instructions => string.Empty;

        public int SourceObservations { get; private set; }

        public Task<long> CaptureSourceCheckpointAsync(string relativePath, CancellationToken cancellationToken = default) => Task.FromResult(0L);

        public void ObserveSource(string relativePath, string contentHash, long checkpoint) => SourceObservations++;

        public IReadOnlyList<RepositoryMemorySupport> ResolveSupports(IReadOnlyList<string> paths) => [];

        public Task<RepositoryIntelligenceChangePage> ReadChangesAsync(string? afterPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RepositoryIntelligenceChangePage([], null));

        public void CoverChanges(IReadOnlyList<string> paths)
        {
        }

        public Task CompleteAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string FindTestSource([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "TestPromptLoader.cs");

    private static DelegateAgentRequest InvestigationRequest() => new()
    {
        Task = "Investigate one checklist question.",
        Context = "Locate the implementation and caller.",
        ToolAccess = DelegateAgentToolAccess.ReadOnly,
        Role = AgentRole.Explorer,
    };

    private static DelegateAgentsPlanFactory CreateIntelligencePlanFactory(WorkspaceId workspace, ConversationToolSnapshotStore snapshots) =>
        new(
            new StubWorkspaceResolver(new WorkspaceBaseline(workspace, Environment.CurrentDirectory, DateTimeOffset.UtcNow, [], GitRevision: "baseline", ApprovedRoots: ["."], TrustLevel: RepositoryTrustLevel.TrustedBuild)),
            new SessionModelPreferences(ModelProfileId.New(), ReasoningLevel.Medium),
            snapshots,
            TestPromptLoader.Instance);
}
