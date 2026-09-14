namespace Threadsmith.ParallelAgents.Tests;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

public sealed partial class ModelExplorerAssignmentRunnerTests
{
    private const string NativeReviewContext = "Frozen native review context. Jira BUG-123 acceptance criterion: metadata must retain compiler-backed descriptions.";

    private const string NativeChildResponse = "Ordinary child inspected Compiler-backed metadata.";

    /// <summary>A native procedure delegates through ordinary tools while retaining its frozen model and request snapshots.</summary>
    [Theory]
    [InlineData(AgentRole.SecurityReviewer)]
    [InlineData(AgentRole.BugReviewer)]
    public async Task NativeSkill_DelegatesThroughSharedPipelineAndFreezesParentModel(AgentRole role)
    {
        var repository = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"threadsmith-native-delegation-{Guid.NewGuid():N}")).FullName;
        try
        {
            await using var events = new DomainEventStream();
            var observed = new ConcurrentQueue<IDomainEvent>();
            await using var subscription = events.Subscribe((item, _) =>
            {
                observed.Enqueue(item);
                return Task.CompletedTask;
            });
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var metadata = new InspectMetadataTool();
            var registry = new ToolRegistry([metadata]);
            var pipeline = CreatePipeline(registry, events, sanitizer);
            var frozen = CreateProfile() with
            {
                Name = "frozen-native-parent",
                ContextWindow = 32_768,
                Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true, StructuredOutput = true },
                IntendedWorkloadClasses = [WorkloadClass.General, WorkloadClass.Planning, WorkloadClass.Review],
                SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.Medium],
            };
            var current = frozen with { Id = ModelProfileId.New(), Name = "later-session-selection" };
            var catalog = new ConfiguredModelCatalog([frozen, current]);
            var selector = new AgentModelSelector(catalog, new DefaultModelSelectionPolicy(catalog));
            var preferences = new SessionModelPreferences(current.Id, ReasoningLevel.None);
            var snapshots = new NativeSkillSnapshotProbe();
            var usage = new SessionUsageProjection();
            var options = CreateOptions();
            var request = new SkillInvocationRequest
            {
                InvocationId = SkillInvocationId.New(),
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                WorkspaceId = WorkspaceId.New(),
                Selector = "native-delegation-test",
                InputJson = "{}",
                Trust = RepositoryTrustLevel.TrustedRead,
                Phase = RunPhase.EvidenceCollection,
                HostBudget = new SkillBudget { ModelTurns = 2, ToolCalls = 1, ContentTokens = 8_000 },
            };
            var baseline = new WorkspaceBaseline(
                request.WorkspaceId.Value,
                repository,
                DateTimeOffset.UtcNow,
                [],
                GitRevision: "native-delegation-baseline",
                ApprovedRoots: ["."],
                TrustLevel: request.Trust);
            await using var workspaces = new TransactionalWorkspaceCoordinator(events);
            await workspaces.RegisterBaselineAsync(baseline);
            await using var scheduler = CreateScheduler();
            var checkpoints = new RoleCheckpointStore();
            var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
            var provider = new NativeDelegatingProvider(request.RunId, metadata.Definition.Id, request.SessionId, usage, role);
            var runners = new ModelExplorerAssignmentRunnerFactory(
                new AgentContextAssembler(evidence),
                new AgentFindingAdmission(evidence),
                selector,
                provider,
                pipeline,
                evidence,
                new StubInstructionProvider(),
                snapshots,
                sanitizer,
                options,
                TestPromptLoader.Instance,
                sessionUsage: usage);
            var delegateTool = new DelegateAgentsTool(
                new DelegateAgentsPlanFactory(workspaces, preferences, snapshots, TestPromptLoader.Instance, options, selector),
                runners,
                coordinator,
                options,
                TestPromptLoader.Instance);
            registry.RegisterOrReplace(delegateTool, new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "delegate-agents"));
            var authority = new ToolInvocationContext
            {
                WorkspaceId = request.WorkspaceId,
                RepositoryPath = repository,
                TrustLevel = request.Trust,
                ApprovedRoots = ["."],
                AllowedToolIds = [metadata.Definition.Id, DelegateAgentsContract.ToolId],
                RequestedBy = "explicit-skill-user",
            };
            var plan = new SkillInvocationPlan
            {
                Request = request,
                Package = new SkillPackageIdentity(
                    new SkillId("native-delegation-test"),
                    "test.native-delegation",
                    "1.0.0",
                    new SkillDigest("sha256", new string('a', 64)),
                    "test"),
                Scope = SkillScope.User,
                Verification = SkillVerificationState.DigestAllowlisted,
                Compatibility = new SkillCompatibilityResult { IsCompatible = true },
                ModelProfileId = frozen.Id,
                ReasoningLevel = ReasoningLevel.Medium.ToString(),
                AvailableToolIds = authority.AllowedToolIds,
                EffectiveBudget = request.HostBudget,
            };
            var native = new ModelSkillProcedureRunner(
                provider,
                registry,
                pipeline,
                sanitizer,
                (_, _) => Task.FromResult(authority),
                TestPromptLoader.Instance,
                catalog,
                snapshots: snapshots,
                sessionUsage: usage);

            var result = await native.RunAsync(
                plan,
                new SkillWorkflowStep { StepId = "coordinate", Kind = SkillWorkflowStepKind.InvokeProcedure },
                iteration: 1,
                content: [],
                inputJson: "{}").WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(20, usage.GetOwnerSnapshot(request.SessionId).InputTokens);
            Assert.Equal(6, usage.GetOwnerSnapshot(request.SessionId).OutputTokens);
            Assert.Equal(8, usage.GetOwnerSnapshot(request.SessionId).CachedInputTokens);
            Assert.Equal(40, usage.GetSnapshot(request.SessionId).InputTokens);
            Assert.Equal(12, usage.GetSnapshot(request.SessionId).OutputTokens);
            Assert.Equal(2, result.ModelTurns);
            Assert.Equal(1, result.ToolCalls);
            Assert.Contains(NativeChildResponse, result.OutputJson, StringComparison.Ordinal);
            Assert.Equal(current.Id, preferences.CurrentProfileId);
            var requests = provider.Requests.ToArray();
            var parentRequests = requests.Where(item => item.RunId == request.RunId).ToArray();
            Assert.Equal(2, parentRequests.Length);
            var joined = Assert.Single(parentRequests[1].Messages, message => message.Role == ModelMessageRole.Tool);
            Assert.False(joined.IsError, joined.GetModelVisibleContent());
            Assert.Contains(NativeChildResponse, joined.GetModelVisibleContent(), StringComparison.Ordinal);
            Assert.Equal(4, requests.Length);
            Assert.All(requests, item =>
            {
                Assert.Equal(frozen.Id, item.ResolvedProfileId);
                Assert.Equal(ReasoningLevel.Medium, item.ReasoningLevel);
            });
            Assert.Single(parentRequests[1].Messages, message => message.GetModelVisibleContent().Contains(NativeChildResponse, StringComparison.Ordinal));
            Assert.Equal(DelegateAgentsContract.ToolId, joined.ToolName);
            var childRequests = requests.Where(item => item.RunId != request.RunId).ToArray();
            Assert.Equal(2, childRequests.Length);
            Assert.Contains(childRequests[0].Messages, message => message.GetModelVisibleContent().Contains(NativeReviewContext, StringComparison.Ordinal));
            Assert.Contains(childRequests[1].Messages, message => message.Role == ModelMessageRole.Tool
                && message.GetModelVisibleContent().Contains("Compiler-backed metadata.", StringComparison.Ordinal));
            var delegationId = Assert.Single(observed.OfType<DelegationCheckpointWritten>().Select(item => item.DelegationId).Distinct());
            var checkpoint = await checkpoints.GetAsync(delegationId);
            Assert.NotNull(checkpoint);
            var assignment = Assert.Single(checkpoint.Assignments);
            Assert.Equal(role, assignment.Role);
            Assert.Equal(NativeReviewContext, assignment.InitialContext);
            Assert.Equal(AgentAssignment.ResponseSchema, assignment.OutputSchema);
            Assert.Equal([metadata.Definition.Id], assignment.Policy.AllowedToolIds);
            Assert.Equal(frozen.Id, assignment.Policy.ModelSelection?.EffectiveProfileId);
            Assert.Equal(ReasoningLevel.Medium.ToString(), assignment.Policy.ReasoningLevel);
            var outcome = Assert.Single(checkpoint.ChildOutcomes);
            Assert.Equal(AgentRunStatus.Completed, outcome.Status);
            Assert.Equal(1, outcome.Usage.ToolCalls);
            Assert.Contains(NativeChildResponse, outcome.Response, StringComparison.Ordinal);
            Assert.Contains(observed, item => item is ToolInvocationStarted started
                && started.RunId == request.RunId && started.ToolName == DelegateAgentsContract.ToolId
                && started.RequestedBy == "model");
            Assert.Contains(observed, item => item is ToolInvocationStarted started
                && started.RunId == assignment.ChildRunId && started.ToolName == metadata.Definition.Id
                && started.RequestedBy.StartsWith("agent:", StringComparison.Ordinal));
            var expectedOrigin = $"skill:User:native-delegation-test@1.0.0:{request.InvocationId.Value:D}";
            Assert.All(observed.OfType<ToolInvocationStarted>(), started => Assert.Equal(expectedOrigin, started.ActivityOrigin));
            Assert.NotNull(metadata.LastInvocationContext);
            Assert.Equal(expectedOrigin, metadata.LastInvocationContext.ActivityOrigin);
            Assert.Null(metadata.LastInvocationContext.ModelVisibleToolSnapshotId);
            var captured = snapshots.Captured.ToArray();
            Assert.Equal(2, captured.Length);
            Assert.Contains(captured[0], snapshots.Resolved);
            Assert.Equal(captured, snapshots.Released.ToArray());
            Assert.All(captured, identity => Assert.Throws<InvalidOperationException>(() =>
                snapshots.Resolve(identity, request.SessionId, request.RunId)));
        }
        finally
        {
            Directory.Delete(repository, recursive: true);
        }
    }

    private sealed class NativeDelegatingProvider(RunId parentRunId, string metadataToolId, SessionId sessionId, SessionUsageProjection usage, AgentRole role) : IModelProvider
    {
        public ConcurrentQueue<ModelStreamRequest> Requests { get; } = new();

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            await Task.Yield();
            var status = usage.GetRequestStatus(sessionId, request.RunId == parentRunId ? null : request.RunId);
            Assert.NotNull(status);
            Assert.Equal(request.ResolvedProfileId, status.ProfileId);
            Assert.Equal(request.ReasoningLevel, status.Reasoning);
            Assert.Equal(request.WireEstimate?.WireInputTokens, status.ContextTokens);
            Assert.Equal(32_768, status.ContextLimit);
            var reported = new ModelUsage(10, request.ToolContinuationRound == 0 ? 2 : 4, Cache: new ModelCacheUsage
            {
                Availability = CacheUsageAvailability.Reported,
                CacheReadTokens = 4,
                ReadInputSemantics = CacheReadInputSemantics.IncludedInInput,
            });
            yield return new ModelChunk { Usage = reported };
            yield return new ModelChunk { Usage = reported };
            var live = usage.GetOwnerSnapshot(sessionId, request.RunId == parentRunId ? null : request.RunId);
            Assert.Equal((request.ToolContinuationRound + 1) * 10, live.InputTokens);
            Assert.False(live.HasUnknownUsage);
            if (request.ToolContinuationRound == 0)
            {
                yield return new ModelChunk
                {
                    Output = request.RunId == parentRunId
                        ? new ToolRequestModelOutput(DelegateAgentsContract.ToolId, JsonSerializer.Serialize(new DelegateAgentsInput
                        {
                            Agents =
                            [
                                new DelegateAgentRequest
                                {
                                    Role = role,
                                    Task = "Inspect the assigned metadata using inspect_metadata and return a useful response.",
                                    Context = NativeReviewContext,
                                    ToolAccess = DelegateAgentToolAccess.ReadOnly,
                                },
                            ],
                        }))
                        : new ToolRequestModelOutput(metadataToolId, "{}"),
                    Usage = reported,
                };
                yield break;
            }

            yield return new ModelChunk
            {
                Text = request.RunId == parentRunId
                    ? JsonSerializer.Serialize(new { succeeded = true, response = "Root synthesis: " + NativeChildResponse })
                    : JsonSerializer.Serialize(new { status = "complete", summary = NativeChildResponse, findings = Array.Empty<object>() }),
                Usage = reported,
            };
        }
    }

    private sealed class NativeSkillSnapshotProbe : IConversationToolSnapshotStore
    {
        private readonly ConversationToolSnapshotStore _inner = new();

        public ConcurrentQueue<Guid> Captured { get; } = new();

        public ConcurrentQueue<Guid> Resolved { get; } = new();

        public ConcurrentQueue<Guid> Released { get; } = new();

        public Guid Capture(SessionId sessionId, RunId runId, IReadOnlyList<ToolRegistration> registrations)
        {
            var identity = _inner.Capture(sessionId, runId, registrations);
            Captured.Enqueue(identity);
            return identity;
        }

        public IReadOnlyList<ToolRegistration> Resolve(Guid snapshotId, SessionId sessionId, RunId runId)
        {
            var registrations = _inner.Resolve(snapshotId, sessionId, runId);
            Resolved.Enqueue(snapshotId);
            return registrations;
        }

        public void Release(Guid snapshotId)
        {
            _inner.Release(snapshotId);
            Released.Enqueue(snapshotId);
        }
    }
}
