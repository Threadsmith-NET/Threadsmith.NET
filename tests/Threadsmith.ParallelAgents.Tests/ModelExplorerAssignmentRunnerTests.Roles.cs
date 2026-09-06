namespace Threadsmith.ParallelAgents.Tests;

using System.Collections.Concurrent;
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
    /// <summary>Assignments written before explicit role metadata retain Explorer interpretation.</summary>
    [Fact]
    public void Deserialize_LegacyAssignmentWithoutRole_PreservesExplorerSchema()
    {
        var original = CreateAssignment(CreateProfile().Id, []) with
        {
            OutputSchema = DelegateAgentsContract.FindingSchema,
        };
        var document = JsonSerializer.SerializeToNode(original)?.AsObject()
            ?? throw new InvalidOperationException("Assignment JSON is required.");
        document.Remove("Role");
        document.Remove("RoleRunnerVersion");

        var restored = JsonSerializer.Deserialize<AgentAssignment>(document.ToJsonString());

        Assert.NotNull(restored);
        Assert.Equal(AgentRole.Explorer, restored.Role);
        Assert.Equal(1, restored.RoleRunnerVersion);
        Assert.Equal(DelegateAgentsContract.FindingSchema, restored.OutputSchema);
        Assert.Null(restored.Policy.ModelSelection);
    }

    /// <summary>All six roles receive their instructions and return ordinary text without synthetic results.</summary>
    [Theory]
    [InlineData(AgentRole.Explorer, PromptFileNames.SystemChildAgentExplorer)]
    [InlineData(AgentRole.Implementer, PromptFileNames.SystemChildAgentImplementer)]
    [InlineData(AgentRole.SecurityReviewer, PromptFileNames.SystemChildAgentSecurityReviewer)]
    [InlineData(AgentRole.TestReviewer, PromptFileNames.SystemChildAgentTestReviewer)]
    [InlineData(AgentRole.PerformanceReviewer, PromptFileNames.SystemChildAgentPerformanceReviewer)]
    [InlineData(AgentRole.ArchitectureReviewer, PromptFileNames.SystemChildAgentArchitectureReviewer)]
    public async Task RunAsync_AllRoles_OrdinaryResponsePreservesRoleAndModel(AgentRole role, string rolePromptFileName)
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateRoleProfile();
        var assignment = CreateRoleAssignment(profile.Id, role);
        var plan = CreatePlan(assignment);
        var response = $"Notes for {role}: no further changes are needed.\nInspection is complete.";
        var provider = new FindingSequenceProvider(response);
        var runner = CreateRunner(provider, CreatePipeline(new ToolRegistry([]), events, sanitizer), evidence, sanitizer, profile, CreateParentContext(plan, []), []);

        var result = await runner.RunAsync(plan, assignment);

        Assert.Equal(role, result.Role);
        Assert.Equal(AgentRunStatus.Completed, result.Status);
        Assert.Equal(response, result.Response);
        Assert.Null(result.Findings);
        Assert.Null(result.Review);
        Assert.Null(result.Implementation);
        Assert.Null(result.ChangeSet);
        Assert.Empty(result.DeliveredEvidenceIds);
        Assert.Equal(0, result.Usage.Corrections);
        Assert.True(DelegationOutcomeClassifier.HasUsableResult(plan, result));
        Assert.Equal(profile.Id, result.ModelSelection?.EffectiveProfileId);
        var request = Assert.Single(provider.Requests);
        var expectedWorkload = role switch
        {
            AgentRole.Explorer => WorkloadClass.General,
            AgentRole.Implementer => WorkloadClass.CodeEdit,
            _ => WorkloadClass.Review,
        };
        Assert.Equal(expectedWorkload, request.WorkloadClass);
        Assert.False(request.RequiredCapabilities.StructuredOutput);
        var hostPolicy = Assert.Single(request.Messages, message => message.SectionId == "child-host-policy");
        Assert.Equal(TestPromptLoader.Instance.Get(PromptFileNames.SystemChildAgentHostPolicy), hostPolicy.GetModelVisibleContent());
        Assert.Equal(ModelMessageRole.System, hostPolicy.Role);
        Assert.Same(hostPolicy, request.Messages[0]);
        var roleAmendment = Assert.Single(request.Messages, message => message.SectionId == "child-role-amendment");
        Assert.Equal(TestPromptLoader.Instance.Get(rolePromptFileName), roleAmendment.GetModelVisibleContent());
        Assert.Equal(ModelMessageRole.System, roleAmendment.Role);
        Assert.Same(roleAmendment, request.Messages[1]);
    }

    /// <summary>Implementer JSON remains advisory response text without a host-authorized proposal or mutation.</summary>
    [Fact]
    public async Task RunAsync_ReadOnlyImplementer_DoesNotPromoteResponseToProposal()
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateRoleProfile();
        var assignment = CreateRoleAssignment(profile.Id, AgentRole.Implementer);
        var plan = CreatePlan(assignment);
        var evidenceId = EvidenceId.New();
        await evidence.AddAsync(CreateParentEvidence(plan, evidenceId, "Source for src/Test.cs", EvidenceSensitivity.None));
        var response = JsonSerializer.Serialize(new
        {
            summary = "Add a cancellation check.",
            inspectedFiles = new[] { new { path = "src/Test.cs", summary = "The loop lacks cancellation.", evidenceIds = new[] { evidenceId.Value.ToString("D") } } },
            proposedChanges = new[]
            {
                new
                {
                    path = "src/Test.cs",
                    evidenceIds = new[] { evidenceId.Value.ToString("D") },
                    intendedChange = "Check the token before each iteration.",
                    validationPlan = new[] { "Assert a cancelled token stops the loop." },
                    risks = Array.Empty<string>(),
                    handoff = "Submit through the approved parent workflow.",
                },
            },
            validationPlan = new[] { "Add the focused cancellation test." },
            risks = Array.Empty<string>(),
            handoff = "Review and prepare the proposed change.",
            unresolvedQuestions = Array.Empty<string>(),
            coverageNotes = new[] { "Inspected source only; no tests executed." },
        });
        var provider = new FindingSequenceProvider(response);
        var runner = CreateRunner(provider, CreatePipeline(new ToolRegistry([]), events, sanitizer), evidence, sanitizer, profile, CreateParentContext(plan, []), []);

        var result = await runner.RunAsync(plan, assignment);

        Assert.Equal(response, result.Response);
        Assert.Null(result.Implementation);
        Assert.Null(result.Findings);
        Assert.Null(result.Review);
        Assert.Equal([evidenceId], result.DeliveredEvidenceIds);
        Assert.Equal(0, result.Usage.Corrections);
        Assert.Null(result.ChangeSet);
        Assert.Equal(0, result.Usage.Mutations);
        Assert.Equal(0, result.Usage.ToolCalls);
        Assert.Empty(Assert.Single(provider.Requests).Tools);
        Assert.Equal(WorkloadClass.CodeEdit, provider.Requests[0].WorkloadClass);
        Assert.True(DelegationOutcomeClassifier.HasUsableResult(plan, result));
    }

    /// <summary>Mixed roles accept arbitrary responses; only actual provider failure yields a partial join.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_MixedRoles_OnlyProviderFailureMakesJoinPartial(bool providerFails)
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateRoleProfile();
        AgentAssignment[] assignments =
        [
            CreateRoleAssignment(profile.Id, AgentRole.Explorer),
            CreateRoleAssignment(profile.Id, AgentRole.SecurityReviewer),
            CreateRoleAssignment(profile.Id, AgentRole.TestReviewer),
        ];
        var plan = CreatePlan(assignments[0]) with
        {
            Assignments = assignments,
            ParentBudget = AgentResourceBudget.Aggregate(assignments.Select(item => item.Budget).ToArray()),
        };
        var evidenceId = EvidenceId.New();
        await evidence.AddAsync(CreateParentEvidence(plan, evidenceId, "Known source evidence.", EvidenceSensitivity.None));
        var longResponse = new string('a', 1_500) + "\n\n## Review notes\n- Keep the existing authorization check.";
        var responses = new Dictionary<RunId, string>
        {
            [assignments[0].ChildRunId] = CreateFindingJson(evidenceId.Value.ToString("D"), "Supported Explorer result."),
            [assignments[1].ChildRunId] = longResponse,
            [assignments[2].ChildRunId] = "{}",
        };
        var provider = new MixedRoleProvider(responses, providerFails ? assignments[2].ChildRunId : null);
        var runner = CreateRunner(provider, CreatePipeline(new ToolRegistry([]), events, sanitizer), evidence, sanitizer, profile, CreateParentContext(plan, []), []);
        var roles = new AgentRoleRunnerRegistry(
            Enum.GetValues<AgentRole>().Select(role => new ModelAgentRoleRunner(role, runner)),
            runner);
        await using var scheduler = new AgentRunScheduler(new AgentSchedulerOptions
        {
            MaximumActiveChildren = 3,
            MaximumActiveChildrenPerParent = 3,
        });
        var checkpoints = new RoleCheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);

        var running = coordinator.StartAsync(plan, roles);
        await provider.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        provider.Release.TrySetResult();
        var checkpoint = await running.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(providerFails ? 2 : 3, checkpoint.ChildOutcomes.Count(item => item.Status == AgentRunStatus.Completed));
        Assert.Equal(providerFails ? 1 : 0, checkpoint.ChildOutcomes.Count(item => item.Status == AgentRunStatus.Failed));
        Assert.All(provider.RequestCounts.Values, count => Assert.Equal(1, count));
        Assert.All(checkpoint.ChildOutcomes, outcome =>
        {
            Assert.Equal(outcome.Status == AgentRunStatus.Completed ? responses[outcome.ChildRunId] : null, outcome.Response);
            Assert.Null(outcome.Findings);
            Assert.Null(outcome.Review);
            Assert.Null(outcome.Implementation);
            Assert.Equal(0, outcome.Usage.Corrections);
        });
        var result = new DelegateAgentsResultProjector(CreateOptions(), TestPromptLoader.Instance).Project(plan, checkpoint).Result;
        Assert.Equal(providerFails ? DelegateAgentsStatus.Partial : DelegateAgentsStatus.Completed, result.Status);
        Assert.Equal(new[] { "Explorer", "SecurityReviewer", "TestReviewer" }, result.Children.Select(item => item.Role));
        var projectedReview = Assert.Single(result.Children, child => child.Role == nameof(AgentRole.SecurityReviewer));
        Assert.Equal(longResponse, projectedReview.Summary);
        var rendered = new DelegateAgentsResultRenderer(TestPromptLoader.Instance).Render(result, out var truncated);
        Assert.False(truncated);
        Assert.Contains(longResponse, rendered, StringComparison.Ordinal);
        Assert.All(checkpoint.ChildOutcomes, outcome => Assert.NotNull(outcome.ModelSelection));
        var restored = JsonSerializer.Deserialize<DelegationCheckpoint>(JsonSerializer.Serialize(checkpoint));
        Assert.NotNull(restored);
        Assert.Equal(3, restored.Assignments.Count);
        Assert.Equal(assignments[1].Role, restored.Assignments[1].Role);
        Assert.Equal(profile.Id, restored.Assignments[1].Policy.ModelSelection?.EffectiveProfileId);
        Assert.All(restored.Assignments, assignment => Assert.Equal(AgentAssignment.ResponseSchema, assignment.OutputSchema));
        Assert.Equal(checkpoint.ChildOutcomes.Select(outcome => outcome.Response), restored.ChildOutcomes.Select(outcome => outcome.Response));
        Assert.DoesNotContain(evidence.Snapshot(plan.Provenance.SessionId), item =>
            item.RunId == plan.Provenance.ParentRunId && item.Provenance.ChildRunId is not null);
    }

    /// <summary>The complete child evidence is rechecked against a larger fallback without trimming it.</summary>
    [Fact]
    public async Task RunAsync_CompleteRoleContextRequiresLargerModel_RebuildsBeforeDispatch()
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var preferred = CreateRoleProfile() with { ContextWindow = 2_048, MaximumOutputTokens = 1_024 };
        var fallback = CreateRoleProfile() with { Name = "large-fallback", ContextWindow = 32_768 };
        var assignment = CreateRoleAssignment(preferred.Id, AgentRole.SecurityReviewer);
        var plan = CreatePlan(assignment);
        var content = "complete-source-start " + new string('x', 12_000) + " complete-source-end";
        await evidence.AddAsync(CreateParentEvidence(plan, EvidenceId.New(), content, EvidenceSensitivity.None));
        var provider = new FindingSequenceProvider(CreateEmptyFindingJson());
        var runner = CreateRunner(provider, CreatePipeline(new ToolRegistry([]), events, sanitizer), evidence, sanitizer, new[] { preferred, fallback }, CreateParentContext(plan, []), []);

        var outcome = await runner.RunAsync(plan, assignment);

        var request = Assert.Single(provider.Requests);
        Assert.Equal(fallback.Id, request.ResolvedProfileId);
        Assert.Equal(preferred.Id, outcome.ModelSelection?.ConfiguredProfileId);
        Assert.Equal(fallback.Id, outcome.ModelSelection?.EffectiveProfileId);
        Assert.NotNull(outcome.ModelSelection?.FallbackReason);
        Assert.Contains(request.Messages, item => item.GetModelVisibleContent().Contains(content, StringComparison.Ordinal));
    }

    /// <summary>Tool evidence records the effective dispatch profile after complete-request fallback.</summary>
    [Fact]
    public async Task RunAsync_FallbackThenToolCall_RecordsEffectiveEvidenceModel()
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var preferred = CreateRoleProfile() with { ContextWindow = 2_048, MaximumOutputTokens = 1_024 };
        var fallback = CreateRoleProfile() with { Name = "large-fallback", ContextWindow = 32_768 };
        var tool = new InspectMetadataTool();
        var registry = new ToolRegistry([tool]);
        var assignment = CreateAssignment(preferred.Id, [tool.Definition.Id]);
        var plan = CreatePlan(assignment);
        await evidence.AddAsync(CreateParentEvidence(plan, EvidenceId.New(), new string('x', 12_000), EvidenceSensitivity.None));
        var provider = new ToolThenFindingProvider(tool.Definition.Id);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            new[] { preferred, fallback },
            CreateParentContext(plan, [tool.Definition.Id]),
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId));

        var outcome = await runner.RunAsync(plan, assignment);

        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.All(provider.Requests, request => Assert.Equal(fallback.Id, request.ResolvedProfileId));
        var childEvidence = Assert.Single(evidence.Snapshot(plan.Provenance.SessionId), item => item.RunId == assignment.ChildRunId);
        Assert.Equal(fallback.Id, childEvidence.Provenance.ModelProfileId);
        Assert.Equal(preferred.Id, outcome.ModelSelection?.ConfiguredProfileId);
    }

    private static ModelProfile CreateRoleProfile()
    {
        return CreateProfile() with
        {
            IntendedWorkloadClasses = [WorkloadClass.General, WorkloadClass.CodeEdit, WorkloadClass.Review],
        };
    }

    private static AgentAssignment CreateRoleAssignment(ModelProfileId profile, AgentRole role)
    {
        return CreateAssignment(profile, []) with
        {
            Role = role,
            Mode = role is AgentRole.Explorer or AgentRole.Implementer
                ? AgentRunMode.ReadOnlyBaseline : AgentRunMode.ReadOnlyReview,
            OutputSchema = AgentAssignment.ResponseSchema,
        };
    }

    private sealed class RoleCheckpointStore : IDelegationCheckpointStore
    {
        private DelegationCheckpoint? _checkpoint;

        public Task<bool> SaveAsync(DelegationCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            _checkpoint = checkpoint;
            return Task.FromResult(true);
        }

        public Task<DelegationCheckpoint?> GetAsync(DelegationId delegationId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_checkpoint);
        }
    }

    private sealed class MixedRoleProvider : IModelProvider
    {
        private readonly IReadOnlyDictionary<RunId, string> _responses;
        private readonly RunId? _failureRunId;

        public MixedRoleProvider(IReadOnlyDictionary<RunId, string> responses, RunId? failureRunId = null)
        {
            _responses = responses;
            _failureRunId = failureRunId;
        }

        public ConcurrentDictionary<RunId, int> RequestCounts { get; } = new();

        public TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RequestCounts.AddOrUpdate(request.RunId, 1, static (_, count) => count + 1);
            if (RequestCounts.Count == _responses.Count)
            {
                AllStarted.TrySetResult();
            }

            await Release.Task.WaitAsync(cancellationToken);
            if (request.RunId == _failureRunId)
            {
                throw new ModelProviderException("Synthetic provider failure.");
            }

            yield return new ModelChunk { Output = new TextModelOutput(_responses[request.RunId]) };
        }
    }
}
