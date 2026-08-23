namespace Threadsmith.Planning.Tests;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Plan 80 ordinary-loop grouping, eligibility, replacement, and inspection tests.</summary>
public static class Plan80ActiveTurnContinuationTests
{
    /// <summary>An older delivered group is compacted on positive savings while the newest group and frozen prefix remain exact.</summary>
    [Fact]
    public static async Task Long_turn_compacts_delivered_prefix_even_when_request_remains_above_pressure()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-plan80-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            for (var index = 0; index < 280; index++)
            {
                var fileName = $"evidence-{index:D4}-{new string('x', 32)}.txt";
                await File.WriteAllTextAsync(Path.Combine(root, fileName), "evidence");
            }

            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var operationBudget = new RecordingBudget();
            var toolBudget = new ExecutionBudget(new BudgetDimensions(
                1_000_000,
                100,
                TimeSpan.FromMinutes(2)));
            var registry = new ToolRegistry([new ListFilesTool()]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                toolBudget);
            var profile = CreateProfile();
            var resolver = new ModelResolver(
                new ConfiguredModelCatalog([profile]),
                new InMemoryModelPreferenceSnapshotProvider());
            var assembler = CreateAssembler(events, evidence, resolver, sanitizer);
            var model = new TwoToolsThenTextProvider();
            var policy = new ActiveTurnCompactionPolicy
            {
                PressureTargetPercent = 1,
                OutputReserveTokens = 500,
                SummaryBudgetTokens = 500,
                MinimumSavingsTokens = 1,
                RetainedRecentTokens = 1_000,
            };
            var candidateProvider = new RequestCandidateProvider();
            var compactionProfileId = ModelProfileId.New();
            var compactionProfile = CreateCompactionCandidateProfile(compactionProfileId);
            var activityEvents = new List<IDomainEvent>();
            await using var activitySubscription = events.Subscribe(
                (domainEvent, _) =>
                {
                    if (domainEvent is ActiveTurnCompactionStarted or ActiveTurnCompactionCompleted)
                    {
                        activityEvents.Add(domainEvent);
                    }

                    return Task.CompletedTask;
                });
            var compactor = new ActiveTurnCompactor(
                candidateProvider,
                new ActiveTurnCompactionValidator(policy, sanitizer),
                policy);
            var hooks = new RecordingHookCoordinator();
            var usage = new SessionUsageProjection();
            var application = new SessionApplication(
                events,
                model,
                operationBudget,
                sanitizer,
                NullLogger<SessionApplication>.Instance,
                pipeline,
                (_, _) => Task.FromResult(new ToolInvocationContext
                {
                    RepositoryPath = root,
                    TrustLevel = RepositoryTrustLevel.TrustedRead,
                    RequestedBy = "model",
                }),
                assembler,
                evidence,
                registry,
                profile.Id,
                new ExecutionLimits { MaxModelRounds = 5 },
                sessionUsage: usage,
                hooks: hooks,
                activeTurnCompactor: compactor,
                activeTurnCompactionPolicy: policy,
                activeTurnCompactionProfile: compactionProfile);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("plan-80"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "Inspect the repository thoroughly."));

            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

            Assert.Equal(3, model.Requests.Count);
            Assert.Single(candidateProvider.Requests);
            Assert.Equal(
                ActiveTurnCompactionInspectionStatus.Completed,
                assembler.GetInspection(runId)?.ActiveTurnCompaction?.Status);
            var compactionRequest = candidateProvider.Requests[0];
            Assert.Equal("Inspect the repository thoroughly.", compactionRequest.TaskObjective);
            Assert.Empty(compactionRequest.AcceptanceIntent);
            Assert.Equal(16_000, compactionRequest.ProfileContextWindowTokens);
            Assert.Equal(500, compactionRequest.ProfileOutputReserveTokens);
            Assert.Equal(2, compactionRequest.ToolContinuationRound);
            var compactedGroup = Assert.Single(compactionRequest.EligiblePrefix);
            Assert.True(compactedGroup.WasDeliveredVerbatim);
            Assert.Equal(1, compactedGroup.Sequence);
            Assert.Contains(
                compactedGroup.Messages,
                message => message.ToolCallId == "host-tool-1-1"
                    && message.Role == ModelMessageRole.Assistant);
            Assert.Contains(
                model.Requests[1].Messages,
                message => message.ToolCallId == "host-tool-1-1"
                    && message.Role == ModelMessageRole.Tool);
            Assert.DoesNotContain(
                model.Requests[2].Messages,
                message => message.ToolCallId == "host-tool-1-1");
            var retainedCallIndex = model.Requests[2].Messages.ToList().FindIndex(message =>
                message.ToolCallId == "host-tool-2-1"
                && message.Role == ModelMessageRole.Assistant);
            var retainedResultIndex = model.Requests[2].Messages.ToList().FindIndex(message =>
                message.ToolCallId == "host-tool-2-1"
                && message.Role == ModelMessageRole.Tool);
            Assert.True(retainedCallIndex >= 0);
            Assert.True(retainedResultIndex > retainedCallIndex);
            Assert.Contains(
                model.Requests[2].Messages,
                message => message.SectionId == "active-turn-summary"
                    && message.Role == ModelMessageRole.Assistant);
            Assert.Equal(0, model.Requests[0].HistoryRewriteGeneration);
            Assert.Equal(0, model.Requests[1].HistoryRewriteGeneration);
            Assert.Equal(1, model.Requests[2].HistoryRewriteGeneration);
            Assert.Equal(model.Requests[0].Tools, model.Requests[2].Tools);
            Assert.Equal(
                model.Requests[0].Messages,
                model.Requests[2].Messages.Take(model.Requests[0].Messages.Count));

            var inspection = Assert.IsType<ContextInspectionProjection>(assembler.GetInspection(runId));
            var activeTurn = Assert.IsType<ActiveTurnCompactionInspectionProjection>(
                inspection.ActiveTurnCompaction);
            Assert.Equal(ActiveTurnCompactionInspectionStatus.Completed, activeTurn.Status);
            Assert.Equal(3, activeTurn.AssessmentSequence);
            Assert.Equal(1, activeTurn.CompactedGroupCount);
            Assert.Equal(1, activeTurn.RetainedGroupCount);
            Assert.True(activeTurn.AfterInputTokens < activeTurn.BeforeInputTokens);
            Assert.True(activeTurn.AfterInputTokens > activeTurn.PressureTargetTokens);
            Assert.Equal(1, activeTurn.HistoryRewriteGeneration);
            Assert.Equal(compactionProfileId.Value, activeTurn.CandidateProfileId);
            Assert.StartsWith("sha256:", activeTurn.SummaryContentHash, StringComparison.Ordinal);
            Assert.Equal(2, evidence.Snapshot(sessionId).Count);

            var compactionHooks = hooks.Invocations
                .Where(invocation => string.Equals(
                    invocation.Payload?["stage"],
                    "active-turn-compaction",
                    StringComparison.Ordinal))
                .ToArray();
            Assert.Collection(
                compactionHooks,
                invocation => Assert.Equal(HookPoint.BeforeModelRequest, invocation.Point),
                invocation => Assert.Equal(HookPoint.AfterModelRequest, invocation.Point));
            Assert.Equal(compactionHooks[0].OperationId, compactionHooks[1].OperationId);
            Assert.Equal(
                WorkloadClass.Summary.ToString(),
                compactionHooks[0].Payload?["workload"]);
            Assert.Equal(4, hooks.Invocations.Count(invocation =>
                invocation.Point == HookPoint.BeforeModelRequest));
            Assert.Equal(4, hooks.Invocations.Count(invocation =>
                invocation.Point == HookPoint.AfterModelRequest));
            Assert.Collection(
                activityEvents,
                domainEvent =>
                {
                    var started = Assert.IsType<ActiveTurnCompactionStarted>(domainEvent);
                    Assert.Equal(compactionProfileId, started.CandidateProfileId);
                    Assert.Equal(activeTurn.BeforeInputTokens, started.BeforeInputTokens);
                    Assert.Equal(activeTurn.PressureTargetTokens, started.PressureTargetTokens);
                },
                domainEvent =>
                {
                    var completed = Assert.IsType<ActiveTurnCompactionCompleted>(domainEvent);
                    Assert.Equal(compactionProfileId, completed.CandidateProfileId);
                    Assert.Equal(ActiveTurnCompactionInspectionStatus.Completed, completed.Status);
                    Assert.Equal(activeTurn.BeforeInputTokens, completed.BeforeInputTokens);
                    Assert.Equal(activeTurn.AfterInputTokens, completed.AfterInputTokens);
                    Assert.NotNull(completed.DurationMilliseconds);
                });
            var usageSnapshot = usage.GetSnapshot(sessionId);
            Assert.True(usageSnapshot.HasUnknownUsage);
            Assert.Equal(45, usageSnapshot.TotalTokens);
            Assert.Contains(operationBudget.Accruals, delta =>
                delta.Tokens == 0
                && delta.Calls == 1
                && delta.WallClock > TimeSpan.Zero);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A managed compaction pre-hook denial prevents candidate provider I/O.</summary>
    [Fact]
    public static async Task Managed_compaction_hook_denial_blocks_candidate_provider()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-plan80-hook-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            for (var index = 0; index < 280; index++)
            {
                var fileName = $"evidence-{index:D4}-{new string('x', 32)}.txt";
                await File.WriteAllTextAsync(Path.Combine(root, fileName), "evidence");
            }

            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var toolBudget = new ExecutionBudget(new BudgetDimensions(
                1_000_000,
                100,
                TimeSpan.FromMinutes(2)));
            var registry = new ToolRegistry([new ListFilesTool()]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                toolBudget);
            var profile = CreateProfile();
            var resolver = new ModelResolver(
                new ConfiguredModelCatalog([profile]),
                new InMemoryModelPreferenceSnapshotProvider());
            var assembler = CreateAssembler(events, evidence, resolver, sanitizer);
            var model = new TwoToolsThenTextProvider();
            var policy = new ActiveTurnCompactionPolicy
            {
                PressureTargetPercent = 75,
                OutputReserveTokens = 500,
                SummaryBudgetTokens = 500,
                MinimumSavingsTokens = 100,
                RetainedRecentTokens = 1_000,
            };
            var candidateProvider = new RequestCandidateProvider();
            var activityEvents = new List<IDomainEvent>();
            await using var activitySubscription = events.Subscribe(
                (domainEvent, _) =>
                {
                    if (domainEvent is ActiveTurnCompactionStarted or ActiveTurnCompactionCompleted)
                    {
                        activityEvents.Add(domainEvent);
                    }

                    return Task.CompletedTask;
                });
            var compactor = new ActiveTurnCompactor(
                candidateProvider,
                new ActiveTurnCompactionValidator(policy, sanitizer),
                policy);
            var hooks = new RecordingHookCoordinator { BlockActiveTurnCompaction = true };
            var application = new SessionApplication(
                events,
                model,
                UnboundedBudget.Instance,
                sanitizer,
                NullLogger<SessionApplication>.Instance,
                pipeline,
                (_, _) => Task.FromResult(new ToolInvocationContext
                {
                    RepositoryPath = root,
                    TrustLevel = RepositoryTrustLevel.TrustedRead,
                    RequestedBy = "model",
                }),
                assembler,
                evidence,
                registry,
                profile.Id,
                new ExecutionLimits { MaxModelRounds = 5 },
                hooks: hooks,
                activeTurnCompactor: compactor,
                activeTurnCompactionPolicy: policy);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("plan-80-hook"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "Inspect the repository thoroughly."));

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

            Assert.Equal(2, model.Requests.Count);
            Assert.Empty(candidateProvider.Requests);
            Assert.Collection(
                activityEvents,
                domainEvent => Assert.IsType<ActiveTurnCompactionStarted>(domainEvent),
                domainEvent => Assert.Equal(
                    ActiveTurnCompactionInspectionStatus.ProviderFailure,
                    Assert.IsType<ActiveTurnCompactionCompleted>(domainEvent).Status));
            var candidateHookInvocations = hooks.Invocations
                .Where(invocation => string.Equals(
                    invocation.Payload?["stage"],
                    "active-turn-compaction",
                    StringComparison.Ordinal))
                .ToArray();
            var denied = Assert.Single(candidateHookInvocations);
            Assert.Equal(HookPoint.BeforeModelRequest, denied.Point);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Inline sibling results are buffered behind every sibling call and ordered by call ordinal.</summary>
    [Fact]
    public static async Task Inline_sibling_result_does_not_interleave_assistant_calls()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-plan80-order-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "evidence.txt"), "evidence");
            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var budget = new ExecutionBudget(new BudgetDimensions(
                1_000_000,
                100,
                TimeSpan.FromMinutes(2)));
            var registry = new ToolRegistry([new ListFilesTool()]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                budget);
            var profile = CreateProfile();
            var assembler = CreateAssembler(
                events,
                evidence,
                new ModelResolver(
                    new ConfiguredModelCatalog([profile]),
                    new InMemoryModelPreferenceSnapshotProvider()),
                sanitizer);
            var model = new PendingThenInlineSiblingProvider();
            var application = new SessionApplication(
                events,
                model,
                budget,
                sanitizer,
                NullLogger<SessionApplication>.Instance,
                pipeline,
                (_, _) => Task.FromResult(new ToolInvocationContext
                {
                    RepositoryPath = root,
                    TrustLevel = RepositoryTrustLevel.TrustedRead,
                    RequestedBy = "model",
                }),
                assembler,
                null,
                registry,
                profile.Id,
                new ExecutionLimits { MaxModelRounds = 3 });
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("plan-80-order"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "Inspect the repository."));

            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

            Assert.Equal(2, model.Requests.Count);
            var continuation = model.Requests[1].Messages
                .Where(message => message.ToolCallId?.StartsWith(
                    "host-tool-1-",
                    StringComparison.Ordinal) == true)
                .ToArray();
            Assert.Collection(
                continuation,
                message =>
                {
                    Assert.Equal(ModelMessageRole.Assistant, message.Role);
                    Assert.Equal("host-tool-1-1", message.ToolCallId);
                },
                message =>
                {
                    Assert.Equal(ModelMessageRole.Assistant, message.Role);
                    Assert.Equal("host-tool-1-2", message.ToolCallId);
                },
                message =>
                {
                    Assert.Equal(ModelMessageRole.Tool, message.Role);
                    Assert.Equal("host-tool-1-1", message.ToolCallId);
                },
                message =>
                {
                    Assert.Equal(ModelMessageRole.Tool, message.Role);
                    Assert.Equal("host-tool-1-2", message.ToolCallId);
                });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ContextAssembler CreateAssembler(
        IDomainEventStream events,
        IEvidenceStore evidence,
        IModelResolver resolver,
        IOutputSanitizer sanitizer)
    {
        return new ContextAssembler(
            evidence,
            new TokenEstimator(),
            new ContextPolicy(),
            new PromptAppendLoader(sanitizer),
            sanitizer,
            events,
            new ContextAssemblerOptions { MaximumTokens = 32_000 },
            resolver);
    }

    private static ActiveTurnCompactionCandidateProfile CreateCompactionCandidateProfile(
        ModelProfileId profileId)
    {
        return new ActiveTurnCompactionCandidateProfile
        {
            ProfileId = profileId,
            ContextWindowTokens = 8_000,
            OutputReserveTokens = 500,
            ReasoningLevel = ReasoningLevel.None,
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            Cost = new ModelCostMetadata(),
        };
    }

    private static ModelProfile CreateProfile()
    {
        return new ModelProfile
        {
            Id = ModelProfileId.New(),
            Name = "plan-80-test",
            Provider = "openai-compatible",
            Endpoint = new Uri("https://plan80.example.test/v1/chat/completions"),
            ModelId = "plan-80-test",
            ContextWindow = 16_000,
            MaximumOutputTokens = 16_000,
            RequestOutputTokenReserve = 500,
            Capabilities = new ModelCapabilitySet
            {
                Streaming = true,
                StructuredOutput = true,
                ToolCalls = true,
            },
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            IntendedWorkloadClasses = [WorkloadClass.General, WorkloadClass.Planning],
        };
    }

    private sealed class RequestCandidateProvider : IActiveTurnCompactionCandidateProvider
    {
        public List<ActiveTurnCompactionRequest> Requests { get; } = [];

        public IActiveTurnCompactionCandidateAttempt PrepareCandidate(
            ActiveTurnCompactionRequest request)
        {
            return new RequestCandidateAttempt(this, request);
        }

        private sealed class RequestCandidateAttempt : IActiveTurnCompactionCandidateAttempt
        {
            private readonly RequestCandidateProvider _owner;
            private readonly ActiveTurnCompactionRequest _request;

            public RequestCandidateAttempt(
                RequestCandidateProvider owner,
                ActiveTurnCompactionRequest request)
            {
                _owner = owner;
                _request = request;
            }

            public ModelUsage? ObservedUsage => null;

            public Task<ActiveTurnCandidateGeneration> ExecuteAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _owner.Requests.Add(_request);
                var covered = (_request.PriorSummary?.CoveredGroupSequences ?? [])
                    .Concat(_request.EligiblePrefix.Select(group => group.Sequence))
                    .ToArray();
                var filesRead = (_request.PriorSummary?.FilesRead ?? [])
                    .Concat(_request.EligiblePrefix.SelectMany(group => group.FilesRead))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var filesChanged = (_request.PriorSummary?.FilesChanged ?? [])
                    .Concat(_request.EligiblePrefix.SelectMany(group => group.FilesChanged))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var candidate = new ActiveTurnCompactionCandidate
                {
                    PriorSummaryVersion = _request.PriorSummary?.Version ?? 0,
                    ThroughGroupSequence = _request.EligiblePrefix[^1].Sequence,
                    CoveredGroupSequences = covered,
                    SummaryText = "## Goal\nContinue the active repository inspection.\n\n## Progress\n### Done\n- Summarized earlier delivered tool activity.\n\n### In Progress\n- Continue with retained raw tool activity.\n\n### Blocked\n- None.",
                    FilesRead = filesRead,
                    FilesChanged = filesChanged,
                };
                return Task.FromResult(new ActiveTurnCandidateGeneration(candidate, null));
            }
        }
    }

    private sealed record HookInvocation(
        HookPoint Point,
        Guid OperationId,
        IReadOnlyDictionary<string, string>? Payload);

    private sealed class RecordingHookCoordinator : IHookCoordinator
    {
        public IReadOnlyList<HookHandlerDescriptor> Handlers => [];

        public bool BlockActiveTurnCompaction { get; init; }

        public List<HookInvocation> Invocations { get; } = [];

        public HookHandlerDescriptor? GetHandler(HookHandlerId handlerId)
        {
            return null;
        }

        public Task<HookBoundaryDecision> InvokeAsync(
            HookPoint point,
            SessionId sessionId,
            RunId? runId,
            string? repositoryIdentity,
            Guid operationId,
            int generation,
            IReadOnlyDictionary<string, string>? payload = null,
            IReadOnlyList<ExecutionArtifactReference>? artifacts = null,
            IReadOnlyList<string>? callChain = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations.Add(new HookInvocation(point, operationId, payload));
            var block = BlockActiveTurnCompaction
                && point == HookPoint.BeforeModelRequest
                && payload is not null
                && payload.TryGetValue("stage", out var stage)
                && string.Equals(stage, "active-turn-compaction", StringComparison.Ordinal);
            return Task.FromResult(new HookBoundaryDecision(
                block ? HookDecisionKind.Block : HookDecisionKind.Continue,
                [],
                []));
        }

        public Task<HookBoundaryDecision> InvokeHandlerAsync(
            HookHandlerId handlerId,
            HookPoint point,
            SessionId sessionId,
            RunId? runId,
            string? repositoryIdentity,
            Guid operationId,
            int generation,
            IReadOnlyDictionary<string, string>? payload = null,
            IReadOnlyList<ExecutionArtifactReference>? artifacts = null,
            IReadOnlyList<string>? callChain = null,
            CancellationToken cancellationToken = default)
        {
            return InvokeAsync(
                point,
                sessionId,
                runId,
                repositoryIdentity,
                operationId,
                generation,
                payload,
                artifacts,
                callChain,
                cancellationToken);
        }

        public bool SetEnabled(HookHandlerId handlerId, bool enabled)
        {
            return false;
        }
    }

    private sealed class RecordingBudget : IBudget
    {
        private readonly Lock _gate = new();
        private BudgetDimensions _used = new(0, 0, TimeSpan.Zero);

        public List<BudgetDimensions> Accruals { get; } = [];

        public BudgetStatus Accrue(BudgetDimensions delta)
        {
            lock (_gate)
            {
                Accruals.Add(delta);
                _used = Add(_used, delta);
                return new BudgetStatus(false, _used, null);
            }
        }

        public BudgetStatus Check(BudgetDimensions delta)
        {
            lock (_gate)
            {
                return new BudgetStatus(false, Add(_used, delta), null);
            }
        }

        private static BudgetDimensions Add(BudgetDimensions current, BudgetDimensions delta)
        {
            return new BudgetDimensions(
                current.Tokens + delta.Tokens,
                current.Calls + delta.Calls,
                current.WallClock + delta.WallClock,
                current.Cost + delta.Cost);
        }
    }

    private sealed class PendingThenInlineSiblingProvider : IModelProvider
    {
        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Requests.Count == 1)
            {
                const string arguments = "{\"path\":\".\",\"maximumEntries\":10}";
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput("list_files", arguments),
                    FinishReason = ModelFinishReason.ToolCalls,
                };
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput("list_files", arguments),
                    FinishReason = ModelFinishReason.ToolCalls,
                };
                yield break;
            }

            yield return new ModelChunk
            {
                Text = "Inspection complete.",
                FinishReason = ModelFinishReason.Stop,
            };
        }
    }

    private sealed class TwoToolsThenTextProvider : IModelProvider
    {
        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Requests.Count <= 2)
            {
                var maximumEntries = Requests.Count == 1 ? 280 : 279;
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput(
                        "list_files",
                        $"{{\"path\":\".\",\"maximumEntries\":{maximumEntries}}}"),
                    Usage = new ModelUsage(10, 5),
                    FinishReason = ModelFinishReason.ToolCalls,
                };
                yield break;
            }

            yield return new ModelChunk
            {
                Text = "Inspection complete.",
                Usage = new ModelUsage(10, 5),
                FinishReason = ModelFinishReason.Stop,
            };
        }
    }
}
