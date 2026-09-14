namespace Threadsmith.ParallelAgents.Tests;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

public sealed partial class ModelExplorerAssignmentRunnerTests
{
    /// <summary>Inherited children execute normal tools and nested skills/delegation without widening their model or tool surface.</summary>
    [Fact]
    public async Task InheritedTools_ProcessesWritesSkillsAndNestedDelegationUseSharedPipeline()
    {
        var repository = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"threadsmith-inherited-tools-{Guid.NewGuid():N}")).FullName;
        try
        {
            await using var events = new DomainEventStream();
            var observed = new ConcurrentQueue<IDomainEvent>();
            await using var subscription = events.Subscribe((item, _) =>
            {
                observed.Enqueue(item);
                return Task.CompletedTask;
            });
            var connectionString = $"Data Source={Path.Combine(repository, "state.db")};Pooling=False";
            await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var processes = new InheritedProcessManager();
            var configuration = new ConfigurationBuilder().Build();
            var registry = new ToolRegistry([
                new RunProcessTool(processes, TestPromptLoader.Instance, allowedExecutables: ["bash"], requireApproval: false, shellExecutable: "bash"),
                new WriteFileTool(new WriteFileConfiguration(configuration, configuration, repository), new UnusedInheritedConversationStore(), TestPromptLoader.Instance),
                new InspectMetadataTool(),
            ]);
            var pipeline = CreatePipeline(registry, events, sanitizer);
            var frozen = CreateProfile() with
            {
                ContextWindow = 32_768,
                Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true, StructuredOutput = true },
                IntendedWorkloadClasses = [WorkloadClass.General, WorkloadClass.Review],
                SupportedReasoningLevels = [ReasoningLevel.None, ReasoningLevel.Medium],
            };
            var current = frozen with { Id = ModelProfileId.New(), Name = "changed-session-selection" };
            var models = new ConfiguredModelCatalog([frozen, current]);
            var selector = new AgentModelSelector(models, new DefaultModelSelectionPolicy(models));
            var preferences = new SessionModelPreferences(current.Id, ReasoningLevel.None);
            var snapshots = new NativeSkillSnapshotProbe();
            var sessionId = SessionId.New();
            var rootRunId = RunId.New();
            var workspaceId = WorkspaceId.New();
            var authority = new ToolInvocationContext
            {
                WorkspaceId = workspaceId,
                RepositoryPath = repository,
                TrustLevel = RepositoryTrustLevel.FullyTrustedAutomation,
                ApprovedRoots = ["."],
                AllowedToolIds = ["run_process", "write_file", "invoke_skill", DelegateAgentsContract.ToolId],
                AllowedExecutables = ["bash"],
                AllowedNetworkHosts = ["example.test"],
                RequestedBy = "model",
                ModelProfileId = frozen.Id,
                ModelReasoningLevel = nameof(ReasoningLevel.Medium),
            };
            await using var workspaces = new TransactionalWorkspaceCoordinator(events);
            await workspaces.RegisterBaselineAsync(new WorkspaceBaseline(
                workspaceId,
                repository,
                DateTimeOffset.UtcNow,
                [],
                GitRevision: "test-baseline",
                ApprovedRoots: ["."],
                TrustLevel: authority.TrustLevel));
            await using var scheduler = CreateScheduler();
            var checkpoints = new DelegationCheckpointStore(connectionString);
            var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);
            var provider = new InheritedToolsProvider(sessionId, snapshots, authority);
            var usage = new SessionUsageProjection();
            var options = CreateOptions();
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
            registry.RegisterOrReplace(
                new DelegateAgentsTool(
                    new DelegateAgentsPlanFactory(workspaces, preferences, snapshots, TestPromptLoader.Instance, options, selector),
                    runners,
                    coordinator,
                    options,
                    TestPromptLoader.Instance),
                new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "delegate-agents"));
            var maintainedRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Threadsmith.Skills/MaintainedSkills"));
            var skills = new SkillCatalog([new SkillCatalogSource(SkillScope.Maintained, maintainedRoot, "maintained", IsMaintained: true)]);
            await skills.RefreshAsync();
            var native = new ModelSkillProcedureRunner(
                provider,
                registry,
                pipeline,
                sanitizer,
                (_, _) => throw new InvalidOperationException("A nested native skill must use its caller snapshot, not the session's broader tool context."),
                TestPromptLoader.Instance,
                models,
                snapshots: snapshots,
                sessionUsage: usage);
            await using var workflows = new SkillWorkflowOrchestrator(
                skills,
                new SkillPackageVerifier(new SkillTrustPolicySnapshot()),
                new SkillCompatibilityEvaluator(registry, models, "1.0.0"),
                new SkillContentLoader(sanitizer, TestPromptLoader.Instance),
                new BoundedJsonSchemaValidator(),
                native,
                TestPromptLoader.Instance,
                new SqliteSkillStateStore(connectionString),
                (_, _) => Task.FromResult(new SkillInvocationHostContext
                {
                    WorkspaceId = workspaceId,
                    Trust = authority.TrustLevel,
                    Phase = RunPhase.EvidenceCollection,
                    ModelProfileId = current.Id,
                    ReasoningLevel = nameof(ReasoningLevel.None),
                    DefaultBudget = new SkillBudget { ModelTurns = 8, ToolCalls = 8 },
                }),
                events,
                snapshots);
            registry.RegisterOrReplace(
                new InvokeSkillTool(workflows, TestPromptLoader.Instance),
                new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "invoke-skill"));
            var snapshotId = snapshots.Capture(
                sessionId,
                rootRunId,
                registry.GetRegistrations(sessionId, rootRunId)
                    .Where(item => authority.AllowedToolIds.Contains(item.Tool.Definition.Id, StringComparer.Ordinal)).ToArray(),
                authority);

            ToolInvocationResult result;
            try
            {
                result = await pipeline.InvokeAsync(new ToolInvocationRequest
                {
                    SessionId = sessionId,
                    RunId = rootRunId,
                    Phase = RunPhase.EvidenceCollection,
                    ToolId = DelegateAgentsContract.ToolId,
                    ArgumentsJson = InheritedToolsProvider.DelegateInput("Run the inherited-tools integration task."),
                    Context = authority with { ModelVisibleToolSnapshotId = snapshotId },
                }).WaitAsync(TimeSpan.FromSeconds(20));
            }
            finally
            {
                snapshots.Release(snapshotId);
            }

            Assert.True(result.Succeeded, result.Error ?? result.ModelResultContent);
            Assert.Equal("child artifact", await File.ReadAllTextAsync(Path.Combine(repository, ".inbox/child.md")));
            Assert.Equal("native artifact", await File.ReadAllTextAsync(Path.Combine(repository, ".inbox/native.md")));
            var process = Assert.Single(processes.Requests);
            Assert.NotEqual(rootRunId, process.RunId);
            Assert.Equal(repository, Path.TrimEndingDirectorySeparator(process.WorkingDirectory));
            Assert.Equal("bash", process.FileName);
            Assert.Contains("dotnet test", process.Arguments);
            var requests = provider.Requests.ToArray();
            Assert.Equal(8, requests.Length);
            Assert.Equal(0, usage.GetOwnerSnapshot(sessionId).InputTokens);
            Assert.Equal(70, usage.GetOwnerSnapshot(sessionId, process.RunId).InputTokens);
            Assert.Equal(80, usage.GetSnapshot(sessionId).InputTokens);
            Assert.Equal(16, usage.GetSnapshot(sessionId).OutputTokens);
            Assert.All(requests, request =>
            {
                Assert.Equal(frozen.Id, request.ResolvedProfileId);
                Assert.Equal(ReasoningLevel.Medium, request.ReasoningLevel);
                Assert.DoesNotContain(request.Tools, tool => tool.Name == "inspect_metadata");
            });
            var nativeRequests = requests.Where(request => request.Messages.Any(message => message.SectionId == "skill-procedure")).ToArray();
            Assert.Equal(3, nativeRequests.Length);
            Assert.All(nativeRequests, request => Assert.Contains(request.Tools, tool => tool.Name == ChildAgentEvidenceTool.ToolId));
            Assert.All(nativeRequests, request => Assert.Equal(process.RunId, request.RunId));
            var invocations = observed.OfType<ToolInvocationStarted>().ToArray();
            Assert.Contains(invocations, item => item.RunId == process.RunId && item.ToolName == "run_process");
            Assert.Contains(invocations, item => item.RunId == process.RunId && item.ToolName == "invoke_skill");
            Assert.Contains(invocations, item => item.RunId == process.RunId && item.ToolName == DelegateAgentsContract.ToolId
                && item.ActivityOrigin?.StartsWith("skill:Maintained:review@1.0.0:", StringComparison.Ordinal) == true);
            var delegations = observed.OfType<DelegationCheckpointWritten>().Select(item => item.DelegationId).Distinct().ToArray();
            Assert.Equal(2, delegations.Length);
            foreach (var delegationId in delegations)
            {
                var checkpoint = await checkpoints.GetAsync(delegationId);
                Assert.NotNull(checkpoint);
                Assert.Equal(AgentRunStatus.Completed, Assert.Single(checkpoint.ChildOutcomes).Status);
                Assert.Equal(AgentRunMode.SharedWorkspace, Assert.Single(checkpoint.Assignments).Mode);
            }

            Assert.Equal(snapshots.Captured.Order(), snapshots.Released.Order());
            Assert.Equal(current.Id, preferences.CurrentProfileId);
        }
        finally
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(repository), StringComparison.OrdinalIgnoreCase);
            Directory.Delete(repository, recursive: true);
        }
    }

    private sealed class InheritedToolsProvider : IModelProvider
    {
        private readonly SessionId _sessionId;
        private readonly NativeSkillSnapshotProbe _snapshots;
        private readonly ToolInvocationContext _parentAuthority;
        private RunId? _parentChildRunId;

        public InheritedToolsProvider(SessionId sessionId, NativeSkillSnapshotProbe snapshots, ToolInvocationContext parentAuthority)
        {
            _sessionId = sessionId;
            _snapshots = snapshots;
            _parentAuthority = parentAuthority;
        }

        public ConcurrentQueue<ModelStreamRequest> Requests { get; } = new();

        public static string DelegateInput(string task)
            => JsonSerializer.Serialize(new DelegateAgentsInput
            {
                Agents = [new DelegateAgentRequest { Role = AgentRole.TestReviewer, Task = task, Context = "Integration scope: use only the inherited tools.", ToolAccess = DelegateAgentToolAccess.Inherit }],
            });

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            var context = _snapshots.ResolveContext(_snapshots.Captured.Last(), _sessionId, request.RunId);
            Assert.NotNull(context);
            Assert.Equal(_parentAuthority.WorkspaceId, context.WorkspaceId);
            Assert.Equal(_parentAuthority.RepositoryPath, context.RepositoryPath);
            Assert.Equal(_parentAuthority.TrustLevel, context.TrustLevel);
            Assert.Equal(_parentAuthority.ApprovedRoots, context.ApprovedRoots);
            Assert.Equal(_parentAuthority.AllowedExecutables, context.AllowedExecutables);
            Assert.Equal(_parentAuthority.AllowedNetworkHosts, context.AllowedNetworkHosts);
            Assert.Equal("model", context.RequestedBy);
            Assert.Equal(request.ResolvedProfileId, context.ModelProfileId);
            Assert.Equal(request.ReasoningLevel.ToString(), context.ModelReasoningLevel);
            await Task.Yield();
            foreach (var toolResult in request.Messages.Where(message => message.Role == ModelMessageRole.Tool))
            {
                Assert.False(toolResult.IsError, toolResult.GetModelVisibleContent());
            }

            _parentChildRunId ??= request.RunId;
            var native = request.Messages.Any(message => message.SectionId == "skill-procedure");
            var call = native
                ? request.ToolContinuationRound switch
                {
                    0 => new ToolRequestModelOutput(DelegateAgentsContract.ToolId, DelegateInput("Return the nested leaf response.")),
                    1 => new ToolRequestModelOutput("write_file", "{\"path\":\".inbox/native.md\",\"content\":\"native artifact\"}"),
                    _ => null,
                }
                : request.RunId != _parentChildRunId ? null : request.ToolContinuationRound switch
                {
                    0 => new ToolRequestModelOutput("run_process", "{\"command\":\"dotnet test\"}"),
                    1 => new ToolRequestModelOutput("write_file", "{\"path\":\".inbox/child.md\",\"content\":\"child artifact\"}"),
                    2 => new ToolRequestModelOutput("invoke_skill", "{\"selector\":\"Maintained:review@1.0.0\",\"input\":{}}"),
                    _ => null,
                };
            yield return new ModelChunk
            {
                Output = call,
                Text = call is not null ? null : native
                    ? "{\"succeeded\":true,\"response\":\"Nested skill complete.\"}"
                    : "{\"status\":\"complete\",\"summary\":\"Inherited tool work complete.\",\"findings\":[]}",
                Usage = new ModelUsage(10, 2),
            };
        }
    }

    private sealed class InheritedProcessManager : IProcessManager
    {
        public ConcurrentQueue<ProcessExecutionRequest> Requests { get; } = new();

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(ProcessExecutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Enqueue(request);
            return Task.FromResult(new ProcessExecutionResult(123, 0, "tests passed", string.Empty, false, false, false, TimeSpan.Zero));
        }
    }

    private sealed class UnusedInheritedConversationStore : IConversationStore
    {
        public Task<ConversationMessage> ArchiveMessageAsync(ConversationMessage message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetModeAsync(SessionId sessionId, ConversationContextMode mode, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ConversationStateSnapshot> GetSnapshotAsync(SessionId sessionId, bool includeBodies = true, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> RemoveMessageBodiesOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
