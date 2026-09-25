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
    /// <summary>Positive savings can compact an older group above pressure while retaining two recent groups and the frozen prefix.</summary>
    [Fact]
    public static async Task Long_turn_compacts_delivered_prefix_even_when_request_remains_above_pressure()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-plan80-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var operationBudget = new RecordingBudget();
            var toolBudget = new ExecutionBudget(new BudgetDimensions(
                1_000_000,
                100,
                TimeSpan.FromMinutes(2)));
            var registry = new ToolRegistry([new TestDeterministicOutputTool(new string('x', 256))]);
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
            var model = new ToolsThenTextProvider { ToolRounds = 3 };
            var policy = new ActiveTurnCompactionPolicy
            {
                PressureTargetPercent = 1,
                OutputReserveTokens = 128,
                SummaryBudgetTokens = 256,
                MaximumRenderedSummaryCharacters = 1_024,
                MinimumSavingsTokens = 1,
                RetainedRecentTokens = 150,
            };
            var timeProvider = new ManualTimeProvider();
            var candidateDuration = TimeSpan.FromMilliseconds(500);
            var candidateProvider = new RequestCandidateProvider(timeProvider, candidateDuration);
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
                new ActiveTurnCompactionValidator(policy, sanitizer, TestPromptLoader.Instance),
                policy,
                TestPromptLoader.Instance,
                timeProvider);
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
                activeTurnCompactionProfile: compactionProfile,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("plan-80"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "Inspect the repository thoroughly."));

            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

            Assert.Equal(4, model.Requests.Count);
            var finalRequest = model.Requests[^1];
            Assert.Single(candidateProvider.Requests);
            Assert.Equal(
                ActiveTurnCompactionInspectionStatus.Completed,
                assembler.GetInspection(runId)?.ActiveTurnCompaction?.Status);
            var compactionRequest = candidateProvider.Requests[0];
            Assert.Equal("Inspect the repository thoroughly.", compactionRequest.TaskObjective);
            Assert.Empty(compactionRequest.AcceptanceIntent);
            Assert.Equal(4_096, compactionRequest.ProfileContextWindowTokens);
            Assert.Equal(128, compactionRequest.ProfileOutputReserveTokens);
            Assert.Equal(3, compactionRequest.ToolContinuationRound);
            var compactedGroup = Assert.Single(compactionRequest.EligiblePrefix);
            Assert.True(compactedGroup.EstimatedTokens < policy.RetainedRecentTokens);
            Assert.True(compactedGroup.EstimatedTokens * 2 >= policy.RetainedRecentTokens);
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
                finalRequest.Messages,
                message => message.ToolCallId == "host-tool-1-1");
            var retainedCallIndex = finalRequest.Messages.ToList().FindIndex(message =>
                message.ToolCallId == "host-tool-2-1"
                && message.Role == ModelMessageRole.Assistant);
            var retainedResultIndex = finalRequest.Messages.ToList().FindIndex(message =>
                message.ToolCallId == "host-tool-2-1"
                && message.Role == ModelMessageRole.Tool);
            Assert.True(retainedCallIndex >= 0);
            Assert.True(retainedResultIndex > retainedCallIndex);
            Assert.Equal(
                model.Requests[2].Messages.Where(message => message.ToolCallId == "host-tool-2-1"),
                finalRequest.Messages.Where(message => message.ToolCallId == "host-tool-2-1"));
            Assert.Collection(
                finalRequest.Messages.Where(message => message.ToolCallId == "host-tool-3-1"),
                message => Assert.Equal(ModelMessageRole.Assistant, message.Role),
                message => Assert.Equal(ModelMessageRole.Tool, message.Role));
            Assert.Contains(
                finalRequest.Messages,
                message => message.SectionId == "active-turn-summary"
                    && message.Role == ModelMessageRole.Assistant);
            Assert.Equal(0, model.Requests[0].HistoryRewriteGeneration);
            Assert.Equal(0, model.Requests[1].HistoryRewriteGeneration);
            Assert.Equal(0, model.Requests[2].HistoryRewriteGeneration);
            Assert.Equal(1, finalRequest.HistoryRewriteGeneration);
            Assert.Equal(model.Requests[0].Tools, finalRequest.Tools);
            Assert.Equal(
                model.Requests[0].Messages,
                finalRequest.Messages.Take(model.Requests[0].Messages.Count));

            var inspection = Assert.IsType<ContextInspectionProjection>(assembler.GetInspection(runId));
            var activeTurn = Assert.IsType<ActiveTurnCompactionInspectionProjection>(
                inspection.ActiveTurnCompaction);
            Assert.Equal(ActiveTurnCompactionInspectionStatus.Completed, activeTurn.Status);
            Assert.Equal(4, activeTurn.AssessmentSequence);
            Assert.Equal(1, activeTurn.CompactedGroupCount);
            Assert.Equal(2, activeTurn.RetainedGroupCount);
            Assert.Equal(policy.RetainedRecentTokens, activeTurn.ConfiguredRetentionTargetTokens);
            Assert.Equal(policy.RetainedRecentTokens, activeTurn.EffectiveRetentionTargetTokens);
            Assert.True(activeTurn.AfterInputTokens < activeTurn.BeforeInputTokens);
            Assert.True(activeTurn.AfterInputTokens > activeTurn.PressureTargetTokens);
            Assert.Equal(1, activeTurn.HistoryRewriteGeneration);
            Assert.Equal(1, activeTurn.CandidateAttemptCount);
            Assert.NotNull(activeTurn.SummaryPreparedInputTokens);
            Assert.Equal(100, activeTurn.SummaryAdmissionOutputTokens);
            Assert.Equal(2, activeTurn.CombinedAdmissionCalls);
            Assert.True(
                activeTurn.CombinedAdmissionTokens
                    > activeTurn.SummaryPreparedInputTokens
                        + activeTurn.SummaryAdmissionOutputTokens);
            Assert.NotNull(finalRequest.WireEstimate);
            Assert.True(finalRequest.WireEstimate.NativeToolTokens > 0);
            Assert.True(finalRequest.WireEstimate.FramingTokens > 0);
            Assert.Equal(128, finalRequest.WireEstimate.OutputReserveTokens);
            Assert.Equal(compactionProfileId.Value, activeTurn.CandidateProfileId);
            Assert.StartsWith("sha256:", activeTurn.SummaryContentHash, StringComparison.Ordinal);
            Assert.Equal(3, evidence.Snapshot(sessionId).Count);

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
            Assert.Equal(5, hooks.Invocations.Count(invocation =>
                invocation.Point == HookPoint.BeforeModelRequest));
            Assert.Equal(5, hooks.Invocations.Count(invocation =>
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
            Assert.Equal(60, usageSnapshot.TotalTokens);
            Assert.Contains(operationBudget.Accruals, delta =>
                delta.Tokens == 0
                && delta.Calls == 1
                && delta.WallClock > TimeSpan.Zero);
            Assert.Single(
                operationBudget.Accruals,
                delta => delta.WallClock == candidateDuration);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Replay envelopes preserve duplicate tool names by ordinal across two parent continuation rounds and clear at completion.</summary>
    [Fact]
    public static async Task Replay_envelopes_bind_duplicate_parent_tools_by_ordinal_and_clear_on_success()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-plan104-main-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var budget = new ExecutionBudget(new BudgetDimensions(100_000, 100, TimeSpan.FromMinutes(1)));
            var registry = new ToolRegistry([new TestDeterministicOutputTool("tool output")]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                budget);
            var profile = CreateProfile();
            var model = new ReplayTwoRoundProvider();
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
                CreateAssembler(events, evidence, new ModelResolver(new ConfiguredModelCatalog([profile]), new InMemoryModelPreferenceSnapshotProvider()), sanitizer),
                evidence,
                registry,
                profile.Id,
                new ExecutionLimits { MaxModelRounds = 5 },
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("plan-104-main"));

            // Act
            var runId = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "inspect twice"));

            // Assert
            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
            Assert.Equal(3, model.Requests.Count);
            var firstContinuation = model.Requests[1];
            var firstCalls = firstContinuation.Messages.Where(message => message.ModelRound == 0 && message.Role == ModelMessageRole.Assistant).ToArray();
            var firstResults = firstContinuation.Messages.Where(message => message.ModelRound == 0 && message.Role == ModelMessageRole.Tool).ToArray();
            Assert.Equal(2, firstCalls.Length);
            Assert.Equal(2, firstResults.Length);
            Assert.All(firstResults, result => Assert.False(result.IsError));
            Assert.Equal(firstCalls.Select(call => call.ToolCallId), firstResults.Select(result => result.ToolCallId));
            Assert.All(firstCalls, call => Assert.Equal("deterministic_output", call.ToolName));
            var secondCall = Assert.Single(model.Requests[2].Messages, message => message.ModelRound == 1 && message.Role == ModelMessageRole.Assistant);
            var secondResult = Assert.Single(model.Requests[2].Messages, message => message.ModelRound == 1 && message.Role == ModelMessageRole.Tool);
            Assert.Equal(secondCall.ToolCallId, secondResult.ToolCallId);
            var transientState = model.FirstTransientState;
            Assert.NotNull(transientState);
            Assert.Empty(transientState.Responses);
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
            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var toolBudget = new ExecutionBudget(new BudgetDimensions(
                1_000_000,
                100,
                TimeSpan.FromMinutes(2)));
            var registry = new ToolRegistry([new TestDeterministicOutputTool(new string('x', 256))]);
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
            var model = new ToolsThenTextProvider();
            var policy = new ActiveTurnCompactionPolicy
            {
                PressureTargetPercent = 1,
                OutputReserveTokens = 128,
                SummaryBudgetTokens = 256,
                MaximumRenderedSummaryCharacters = 1_024,
                MinimumSavingsTokens = 1,
                RetainedRecentTokens = 64,
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
                new ActiveTurnCompactionValidator(policy, sanitizer, TestPromptLoader.Instance),
                policy,
                TestPromptLoader.Instance);
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
                activeTurnCompactionPolicy: policy,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
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

    /// <summary>The conversation path does not spend its final call on a summary without continuation headroom.</summary>
    [Fact]
    public static async Task Summary_without_two_call_headroom_is_not_dispatched()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-plan113-admission-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var operationBudget = new RejectCombinedBudget();
            var toolBudget = new ExecutionBudget(new BudgetDimensions(
                1_000_000,
                100,
                TimeSpan.FromMinutes(2)));
            var registry = new ToolRegistry([new TestDeterministicOutputTool(new string('x', 256))]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                toolBudget);
            var profile = CreateProfile();
            var assembler = CreateAssembler(
                events,
                evidence,
                new ModelResolver(
                    new ConfiguredModelCatalog([profile]),
                    new InMemoryModelPreferenceSnapshotProvider()),
                sanitizer);
            var model = new ToolsThenTextProvider { ToolRounds = 2 };
            var policy = new ActiveTurnCompactionPolicy
            {
                PressureTargetPercent = 1,
                OutputReserveTokens = 128,
                SummaryBudgetTokens = 256,
                MaximumRenderedSummaryCharacters = 1_024,
                MinimumSavingsTokens = 1,
                RetainedRecentTokens = 64,
            };
            var candidateProvider = new RequestCandidateProvider();
            var compactor = new ActiveTurnCompactor(
                candidateProvider,
                new ActiveTurnCompactionValidator(policy, sanitizer, TestPromptLoader.Instance),
                policy,
                TestPromptLoader.Instance);
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
                activeTurnCompactor: compactor,
                activeTurnCompactionPolicy: policy,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("plan-113-admission"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "Inspect the repository thoroughly."));

            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
            Assert.Equal(3, model.Requests.Count);
            Assert.Empty(candidateProvider.Requests);
            Assert.Contains(
                model.Requests[^1].Messages,
                message => message.ToolCallId == "host-tool-1-1"
                    && message.Role == ModelMessageRole.Tool);
            Assert.Equal(0, model.Requests[^1].HistoryRewriteGeneration);
            Assert.Contains(operationBudget.Checks, delta => delta.Calls == 2);
            Assert.Equal(
                ActiveTurnCompactionInspectionStatus.BudgetAdmissionRejected,
                assembler.GetInspection(runId)?.ActiveTurnCompaction?.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The summary headroom check applies the existing delivered-result reducer before rejecting capacity.</summary>
    [Fact]
    public static async Task Summary_continuation_headroom_uses_delivered_result_capacity_fallback()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-plan113-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var budget = new ExecutionBudget(new BudgetDimensions(
                1_000_000,
                100,
                TimeSpan.FromMinutes(2)));
            var registry = new ToolRegistry(
                [new VariableDeterministicOutputTool()]);
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
            var model = new ToolsThenTextProvider { ToolRounds = 3 };
            var policy = new ActiveTurnCompactionPolicy
            {
                PressureTargetPercent = 70,
                OutputReserveTokens = 128,
                SummaryBudgetTokens = 256,
                MaximumRenderedSummaryCharacters = 256,
                MinimumSavingsTokens = 1,
                RetainedRecentTokens = 800,
            };
            var candidateProvider = new RequestCandidateProvider();
            var compactor = new ActiveTurnCompactor(
                candidateProvider,
                new ActiveTurnCompactionValidator(policy, sanitizer, TestPromptLoader.Instance),
                policy,
                TestPromptLoader.Instance);
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
                evidence,
                registry,
                profile.Id,
                new ExecutionLimits { MaxModelRounds = 5 },
                activeTurnCompactor: compactor,
                activeTurnCompactionPolicy: policy,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("plan-113-fallback"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "Inspect the repository thoroughly."));

            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
            Assert.NotEmpty(candidateProvider.Requests);
            Assert.Equal(4, model.Requests.Count);
            Assert.Equal(
                ActiveTurnCompactionInspectionStatus.EmergencyReduction,
                assembler.GetInspection(runId)?.ActiveTurnCompaction?.Status);
            Assert.Equal(1, model.Requests[^1].HistoryRewriteGeneration);
            Assert.Contains(
                model.Requests[^1].Messages,
                message => message.SectionId == "active-turn-summary");
            Assert.Contains(
                model.Requests[^1].Messages,
                message => message.Role == ModelMessageRole.Tool
                    && message.GetModelVisibleContent().Contains(
                        "\"isTruncated\":true",
                        StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>An irreducible maximum-summary bound declines optional compaction without aborting an ordinary request that fits.</summary>
    [Fact]
    public static async Task Oversized_summary_bound_declines_context_only_compaction()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-plan113-capacity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var budget = new ExecutionBudget(new BudgetDimensions(
                1_000_000,
                100,
                TimeSpan.FromMinutes(2)));
            var registry = new ToolRegistry([new TestDeterministicOutputTool(new string('x', 256))]);
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
            var model = new ToolsThenTextProvider { ToolRounds = 2 };
            var policy = new ActiveTurnCompactionPolicy
            {
                PressureTargetPercent = 1,
                OutputReserveTokens = 128,
                SummaryBudgetTokens = 256,
                MaximumRenderedSummaryCharacters = 65_536,
                MinimumSavingsTokens = 1,
                RetainedRecentTokens = 64,
            };
            var candidateProvider = new RequestCandidateProvider();
            var compactor = new ActiveTurnCompactor(
                candidateProvider,
                new ActiveTurnCompactionValidator(policy, sanitizer, TestPromptLoader.Instance),
                policy,
                TestPromptLoader.Instance);
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
                evidence,
                registry,
                profile.Id,
                new ExecutionLimits { MaxModelRounds = 4 },
                activeTurnCompactor: compactor,
                activeTurnCompactionPolicy: policy,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("plan-113-capacity"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "Inspect the repository thoroughly."));

            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
            Assert.Equal(3, model.Requests.Count);
            Assert.Empty(candidateProvider.Requests);
            Assert.Equal(0, model.Requests[^1].HistoryRewriteGeneration);
            Assert.Equal(
                ActiveTurnCompactionInspectionStatus.CapacityExceeded,
                assembler.GetInspection(runId)?.ActiveTurnCompaction?.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The rebuilt ordinary request is rechecked after summary usage accrues and before activation.</summary>
    [Fact]
    public static async Task Summary_activation_rechecks_final_prepared_request()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-plan113-final-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var operationBudget = new RejectFinalAfterSummaryBudget();
            var toolBudget = new ExecutionBudget(new BudgetDimensions(
                1_000_000,
                100,
                TimeSpan.FromMinutes(2)));
            var registry = new ToolRegistry([new TestDeterministicOutputTool(new string('x', 256))]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                toolBudget);
            var profile = CreateProfile();
            var assembler = CreateAssembler(
                events,
                evidence,
                new ModelResolver(
                    new ConfiguredModelCatalog([profile]),
                    new InMemoryModelPreferenceSnapshotProvider()),
                sanitizer);
            var model = new ToolsThenTextProvider { ToolRounds = 3 };
            var policy = new ActiveTurnCompactionPolicy
            {
                PressureTargetPercent = 1,
                OutputReserveTokens = 128,
                SummaryBudgetTokens = 256,
                MaximumRenderedSummaryCharacters = 1_024,
                MinimumSavingsTokens = 1,
                RetainedRecentTokens = 150,
            };
            var candidateProvider = new RequestCandidateProvider();
            var compactor = new ActiveTurnCompactor(
                candidateProvider,
                new ActiveTurnCompactionValidator(policy, sanitizer, TestPromptLoader.Instance),
                policy,
                TestPromptLoader.Instance);
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
                activeTurnCompactor: compactor,
                activeTurnCompactionPolicy: policy,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("plan-113-final"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "Inspect the repository thoroughly."));

            await Assert.ThrowsAsync<BudgetExceededException>(() =>
                dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
            Assert.Single(candidateProvider.Requests);
            Assert.Equal(3, model.Requests.Count);
            Assert.True(operationBudget.FinalRequestRejected);
            var inspection = Assert.IsType<ActiveTurnCompactionInspectionProjection>(
                assembler.GetInspection(runId)?.ActiveTurnCompaction);
            Assert.Equal(ActiveTurnCompactionInspectionStatus.BudgetAdmissionRejected, inspection.Status);
            Assert.Equal(BudgetExhaustionDimension.Calls, inspection.AdmissionRejectedDimensions);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The baseline uses actual native-validation projection and opaque pipeline delivery messages.</summary>
    [Fact]
    public static async Task Admission_baseline_replays_actual_pipeline_model_results()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-plan113-baseline-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "src"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "src", "Threadsmith.sln"),
                string.Empty);
            const string hiddenValidationOutputPrefix = "hidden-native-validation-output:";
            var hiddenValidationOutput = hiddenValidationOutputPrefix + new string('v', 20_000);
            var opaqueOutput = new string('p', 2_000);
            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var budget = new ExecutionBudget(new BudgetDimensions(
                1_000_000,
                100,
                TimeSpan.FromMinutes(2)));
            var registry = new ToolRegistry(
            [
                new ConversationAvailableTool(new DotNetBuildTool(
                    new BaselineNativeValidationService(hiddenValidationOutput),
                    TestPromptLoader.Instance)),
                new RunProcessTool(
                    new BaselineProcessManager(opaqueOutput),
                    TestPromptLoader.Instance,
                    allowedExecutables: ["powershell"],
                    requireApproval: false,
                    shellExecutable: "powershell"),
            ]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new AllowApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                budget);
            var profile = CreateProfile();
            var usage = new SessionUsageProjection();
            var model = new BaselineProjectionProvider();
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
                    TrustLevel = RepositoryTrustLevel.TrustedBuild,
                    ApprovedRoots = ["."],
                    AllowedExecutables = ["dotnet", "powershell"],
                    RequestedBy = "plan-113-baseline",
                }),
                CreateAssembler(
                    events,
                    evidence,
                    new ModelResolver(
                        new ConfiguredModelCatalog([profile]),
                        new InMemoryModelPreferenceSnapshotProvider()),
                    sanitizer),
                evidence,
                registry,
                profile.Id,
                new ExecutionLimits { MaxModelRounds = 4 },
                sessionUsage: usage,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("plan-113-baseline"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "Build, then collect opaque evidence."));

            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
            Assert.Equal(3, model.Requests.Count);
            var projected = Assert.Single(
                model.Requests[1].Messages,
                message => message.Role == ModelMessageRole.Tool
                    && message.ToolName == "dotnet_build");
            var opaque = Assert.Single(
                model.Requests[2].Messages,
                message => message.Role == ModelMessageRole.Tool
                    && message.ToolName == "run_process");
            var projectedContent = projected.GetModelVisibleContent();
            var opaqueContent = opaque.GetModelVisibleContent();
            Assert.False(projected.IsError, projectedContent);
            Assert.DoesNotContain(hiddenValidationOutputPrefix, projectedContent, StringComparison.Ordinal);
            Assert.Contains("\"success\":true", projectedContent, StringComparison.Ordinal);
            Assert.Contains(opaqueOutput, opaqueContent, StringComparison.Ordinal);

            var toolDefinitions = model.Requests[2].Tools;
            ModelWireEstimate Estimate(params ModelMessage[] messages) => ModelWireEstimator.Estimate(
                messages,
                toolDefinitions,
                ToolTransportMode.Native,
                stablePrefixMessageCount: 0,
                outputReserveTokens: 128);
            var overlapReplay = projected with { ToolCallId = "projected-replay" };
            var uniqueEvidence = opaque with
            {
                ToolCallId = "unique-evidence",
                Content = [new ModelContentPart { Content = new string('u', 6_000) }],
            };
            var overlapping = Estimate(projected, opaque, overlapReplay);
            var unique = Estimate(projected, opaque, uniqueEvidence);
            var replayRequests = new[]
            {
                Estimate(projected),
                Estimate(projected, opaque),
                Estimate(projected, opaque, overlapReplay),
            };
            var retryAttempt = Estimate(projected, opaque);
            var cumulativeReplay = replayRequests.Sum(item => (long)item.WireInputTokens);
            var failedRetryCumulative = retryAttempt.WireInputTokens * 2L;

            Assert.Equal(1_688, overlapping.WireInputTokens);
            Assert.Equal(3_161, unique.WireInputTokens);
            Assert.Equal(1_688, replayRequests[^1].WireInputTokens);
            Assert.Equal(4_435, cumulativeReplay);
            Assert.Equal(1_056, overlapping.NativeToolTokens);
            Assert.Equal(12, overlapping.FramingTokens);
            Assert.Equal(3_197, model.Requests[1].WireEstimate?.WireInputTokens);
            Assert.Equal(3_771, model.Requests[2].WireEstimate?.WireInputTokens);
            Assert.True(unique.WireInputTokens > overlapping.WireInputTokens);
            Assert.True(cumulativeReplay > replayRequests[^1].WireInputTokens);
            Assert.Equal(retryAttempt.WireInputTokens * 2L, failedRetryCumulative);
            Assert.Equal(projectedContent, projected.GetModelVisibleContent());
            Assert.Equal(opaqueContent, opaque.GetModelVisibleContent());
            var usageSnapshot = usage.GetSnapshot(sessionId);
            Assert.Equal(45, usageSnapshot.TotalTokens);
            Assert.Equal(3, model.Requests.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Inline sibling results are buffered behind every sibling call and ordered by call ordinal.</summary>
    [Fact]
    public static async Task Inline_rejected_sibling_results_follow_their_correlated_assistant_calls()
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
            var registry = new ToolRegistry([new ListFilesTool(TestPromptLoader.Instance)]);
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
                new ExecutionLimits { MaxModelRounds = 3 },
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
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
                    Assert.Equal(ModelMessageRole.Tool, message.Role);
                    Assert.Equal("host-tool-1-1", message.ToolCallId);
                    Assert.True(message.IsError);
                },
                message =>
                {
                    Assert.Equal(ModelMessageRole.Assistant, message.Role);
                    Assert.Equal("host-tool-1-2", message.ToolCallId);
                },
                message =>
                {
                    Assert.Equal(ModelMessageRole.Tool, message.Role);
                    Assert.Equal("host-tool-1-2", message.ToolCallId);
                    Assert.True(message.IsError);
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
            TestPromptLoader.Instance,
            new ContextAssemblerOptions { MaximumTokens = 32_000 },
            resolver);
    }

    private static ActiveTurnCompactionCandidateProfile CreateCompactionCandidateProfile(
        ModelProfileId profileId)
    {
        return new ActiveTurnCompactionCandidateProfile
        {
            ProfileId = profileId,
            ContextWindowTokens = 2_048,
            OutputReserveTokens = 128,
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
            ContextWindow = 4_096,
            MaximumOutputTokens = 4_096,
            RequestOutputTokenReserve = 128,
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
        private readonly TimeSpan _elapsed;
        private readonly ManualTimeProvider? _timeProvider;

        public RequestCandidateProvider()
        {
        }

        public RequestCandidateProvider(ManualTimeProvider timeProvider, TimeSpan elapsed)
        {
            ArgumentNullException.ThrowIfNull(timeProvider);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(elapsed, TimeSpan.Zero);
            _timeProvider = timeProvider;
            _elapsed = elapsed;
        }

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
            private ModelStreamRequest _preparedRequest;

            public RequestCandidateAttempt(
                RequestCandidateProvider owner,
                ActiveTurnCompactionRequest request)
            {
                _owner = owner;
                _request = request;
                var messages = new[]
                {
                    new ModelMessage
                    {
                        Role = ModelMessageRole.User,
                        SectionId = "candidate:instruction",
                        Content = [new ModelContentPart { Content = "Create a bounded summary." }],
                    },
                };
                _preparedRequest = new ModelStreamRequest
                {
                    RunId = request.RunId,
                    Input = "Create a bounded summary.",
                    ResolvedProfileId = request.CandidateProfile?.ProfileId ?? request.ProfileId,
                    Messages = messages,
                    MaximumOutputTokens = 100,
                    WireEstimate = ModelWireEstimator.Estimate(
                        messages,
                        [],
                        ToolTransportMode.Native,
                        stablePrefixMessageCount: 0,
                        outputReserveTokens: 100),
                };
            }

            public ModelUsage? ObservedUsage => null;

            public ModelRequestAdmissionEstimate AdmissionEstimate { get; } = new(100, 100, 1);

            public ModelStreamRequest PreparedRequest => _preparedRequest;

            public int SelectedGroupCount => _request.EligiblePrefix.Count;

            public void SetSubmissionObserver(Action? submissionObserver)
            {
                _preparedRequest = _preparedRequest with
                {
                    SubmissionObserver = submissionObserver,
                };
            }

            public Task<ActiveTurnCandidateGeneration> ExecuteAsync(
                CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _preparedRequest.SubmissionObserver?.Invoke();
                _owner._timeProvider?.Advance(_owner._elapsed);

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

    private sealed class VariableDeterministicOutputTool :
        Tool<TestDeterministicOutputInput, TestDeterministicOutput>
    {
        private readonly ToolDefinition _definition = new()
        {
            Id = "deterministic_output",
            Version = "1.0",
            Description = "Returns a sequence-dependent deterministic test output.",
            Category = ToolCategory.FileRead,
            InputSchema = new ToolSchema(
                nameof(TestDeterministicOutputInput),
                1,
                "{\"type\":\"object\",\"properties\":{\"sequence\":{\"type\":\"integer\"}},\"required\":[\"sequence\"],\"additionalProperties\":false}"),
            OutputSchema = new ToolSchema(nameof(TestDeterministicOutput), 1, "{\"type\":\"object\"}"),
            RequiredTrust = RepositoryTrustLevel.UntrustedInspection,
            SideEffect = ToolSideEffect.ReadOnly,
            Idempotency = ToolIdempotency.Idempotent,
            SupportsCancellation = true,
            Timeout = TimeSpan.FromSeconds(5),
            MaximumOutputBytes = 8_192,
        };

        public override ToolDefinition Definition => _definition;

        public override Task<ToolExecution<TestDeterministicOutput>> ExecuteAsync(
            TestDeterministicOutputInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = input.Sequence <= 2
                ? new string('x', 3_500)
                : new string('y', 50);
            return Task.FromResult(new ToolExecution<TestDeterministicOutput>(
                new TestDeterministicOutput(content),
                []));
        }

        protected override void ValidateInput(TestDeterministicOutputInput input)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(input.Sequence);
        }
    }

    private sealed class BaselineNativeValidationService(string capturedOutput) :
        INativeValidationToolService
    {
        public IReadOnlyList<string> ConfiguredNetworkHosts => [];

        public IReadOnlyList<string> ConfiguredSecretReferences => [];

        public Task<ValidationToolResult> BuildAsync(
            string repositoryPath,
            RunId runId,
            BuildToolRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ValidationToolResult(
                "plan-113-build",
                ValidationInvocationKind.Build,
                ValidationAuthority.Exploratory,
                true,
                request.TargetPath,
                ["build", "--no-restore"],
                [],
                capturedOutput,
                0,
                TimeSpan.FromMilliseconds(25),
                false,
                false));
        }

        public Task<NuGetDependencyHealthResult> InspectPackagesAsync(
            string repositoryPath,
            RunId runId,
            NuGetDependencyHealthRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<NuGetDependencyHealthResult>(new NotSupportedException());

        public Task<ValidationToolResult> AnalyzeAsync(
            string repositoryPath,
            RunId runId,
            AnalyzerToolRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ValidationToolResult>(new NotSupportedException());

        public Task<ValidationToolResult> CheckFormatAsync(
            string repositoryPath,
            RunId runId,
            FormatCheckRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ValidationToolResult>(new NotSupportedException());

        public Task<DiagnosticQueryResult> QueryDiagnosticsAsync(
            string repositoryPath,
            DiagnosticQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromException<DiagnosticQueryResult>(new NotSupportedException());

        public Task<TestDiscoveryResult> DiscoverTestsAsync(
            string repositoryPath,
            RunId runId,
            TestDiscoveryRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<TestDiscoveryResult>(new NotSupportedException());

        public string ResolveTestProjectPath(string repositoryPath, DiscoveredTestId testId)
        {
            throw new NotSupportedException();
        }

        public Task<TargetedTestResult> RunTargetedTestAsync(
            string repositoryPath,
            RunId runId,
            TargetedTestRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<TargetedTestResult>(new NotSupportedException());
    }

    private sealed class ConversationAvailableTool(ITool inner) : ITool
    {
        public ToolDefinition Definition { get; } = inner.Definition with
        {
            ConversationAvailable = true,
        };

        public object DeserializeInput(string argumentsJson) => inner.DeserializeInput(argumentsJson);

        public string? GetActivityDetail(object input) => inner.GetActivityDetail(input);

        public IReadOnlyList<string> GetResourcePaths(
            object input,
            ToolInvocationContext context) => inner.GetResourcePaths(input, context);

        public IReadOnlyList<string> GetSecretReferences(object input) =>
            inner.GetSecretReferences(input);

        public string? GetExecutable(object input) => inner.GetExecutable(input);

        public string? GetExecutable(object input, ToolInvocationContext context) =>
            inner.GetExecutable(input, context);

        public IReadOnlyList<string> GetNetworkHosts(object input) =>
            inner.GetNetworkHosts(input);

        public IReadOnlyList<ToolResourceClaim> GetSchedulingClaims(
            object input,
            ToolInvocationContext context) => inner.GetSchedulingClaims(input, context);

        public Task<ToolExecutionEnvelope> ExecuteAsync(
            object input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default) =>
            inner.ExecuteAsync(input, context, cancellationToken);
    }

    private sealed class BaselineProcessManager(string output) : IProcessManager
    {
        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ProcessExecutionResult(
                113,
                0,
                output,
                string.Empty,
                false,
                false,
                false,
                TimeSpan.FromMilliseconds(25)));
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            return Interlocked.Read(ref _timestamp);
        }

        public void Advance(TimeSpan elapsed)
        {
            if (elapsed < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsed));
            }

            _ = Interlocked.Add(ref _timestamp, elapsed.Ticks);
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

    private sealed class RejectCombinedBudget : IBudget
    {
        private readonly Lock _gate = new();
        private BudgetDimensions _used = new(0, 0, TimeSpan.Zero);

        public List<BudgetDimensions> Checks { get; } = [];

        public BudgetStatus Accrue(BudgetDimensions delta)
        {
            lock (_gate)
            {
                _used = Add(_used, delta);
                return new BudgetStatus(false, _used, null);
            }
        }

        public BudgetStatus Check(BudgetDimensions delta)
        {
            lock (_gate)
            {
                Checks.Add(delta);
                var rejected = delta.Calls >= 2;
                return new BudgetStatus(
                    rejected,
                    Add(_used, delta),
                    rejected ? "Two-call admission rejected for test." : null)
                {
                    ExhaustedDimensions = rejected
                        ? BudgetExhaustionDimension.Calls
                        : BudgetExhaustionDimension.None,
                };
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

    private sealed class RejectFinalAfterSummaryBudget : IBudget
    {
        private readonly Lock _gate = new();
        private BudgetDimensions _used = new(0, 0, TimeSpan.Zero);
        private bool _combinedAdmissionObserved;
        private bool _summaryUsageAccrued;

        public bool FinalRequestRejected { get; private set; }

        public BudgetStatus Accrue(BudgetDimensions delta)
        {
            lock (_gate)
            {
                _used = Add(_used, delta);
                if (_combinedAdmissionObserved && delta.Calls == 1)
                {
                    _summaryUsageAccrued = true;
                }

                return new BudgetStatus(false, _used, null);
            }
        }

        public BudgetStatus Check(BudgetDimensions delta)
        {
            lock (_gate)
            {
                if (delta.Calls >= 2)
                {
                    _combinedAdmissionObserved = true;
                }

                var rejected = _summaryUsageAccrued && delta.Calls == 1;
                FinalRequestRejected |= rejected;
                return new BudgetStatus(
                    rejected,
                    Add(_used, delta),
                    rejected ? "Final prepared request rejected for test." : null)
                {
                    ExhaustedDimensions = rejected
                        ? BudgetExhaustionDimension.Calls
                        : BudgetExhaustionDimension.None,
                };
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

    private sealed class ReplayTwoRoundProvider : IModelProvider
    {
        public ModelRequestTransientState? FirstTransientState { get; private set; }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            FirstTransientState ??= request.TransientState;
            await Task.Yield();
            if (request.ToolContinuationRound == 0)
            {
                yield return new ModelChunk { ResponseEnvelope = CreateEnvelope(request, ["wire-0", "wire-1"]) };
                yield return new ModelChunk { Output = new ToolRequestModelOutput("deterministic_output", "{\"sequence\":1}") };
                yield return new ModelChunk { Output = new ToolRequestModelOutput("deterministic_output", "{\"sequence\":2}") };
                yield break;
            }

            if (request.ToolContinuationRound == 1)
            {
                yield return new ModelChunk { ResponseEnvelope = CreateEnvelope(request, ["wire-2"]) };
                yield return new ModelChunk { Output = new ToolRequestModelOutput("deterministic_output", "{\"sequence\":3}") };
                yield break;
            }

            yield return new ModelChunk { Text = "Replay sequence complete.", FinishReason = ModelFinishReason.Stop };
        }

        private static ModelResponseReplayEnvelope CreateEnvelope(
            ModelStreamRequest request,
            IReadOnlyList<string> wireToolIds)
        {
            return new ModelResponseReplayEnvelope(
                new ModelReplayBinding
                {
                    ProviderId = "test",
                    ModelId = "test",
                    ProfileId = request.ResolvedProfileId ?? throw new InvalidOperationException("A profile is required."),
                    RunId = request.RunId,
                    ModelRound = request.ToolContinuationRound,
                    CredentialGeneration = "test",
                    ToolInventoryDigest = "test-tools",
                    InstructionDigest = "test-instructions",
                    NormalizedRoundDigest = "test-round",
                },
                [1],
                wireToolIds,
                retainedOutputTokens: 1);
        }
    }

    private sealed class ToolsThenTextProvider : IModelProvider
    {
        public List<ModelStreamRequest> Requests { get; } = [];

        public int ToolRounds { get; init; } = 2;

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Requests.Count <= ToolRounds)
            {
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput(
                        "deterministic_output",
                        $"{{\"sequence\":{Requests.Count}}}"),
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

    private sealed class BaselineProjectionProvider : IModelProvider
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
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput(
                        "dotnet_build",
                        "{\"targetPath\":\"src/Threadsmith.sln\"}"),
                    Usage = new ModelUsage(10, 5),
                    FinishReason = ModelFinishReason.ToolCalls,
                };
                yield break;
            }

            if (Requests.Count == 2)
            {
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput(
                        "run_process",
                        "{\"command\":\"Write-Output baseline\"}"),
                    Usage = new ModelUsage(10, 5),
                    FinishReason = ModelFinishReason.ToolCalls,
                };
                yield break;
            }

            yield return new ModelChunk
            {
                Text = "Baseline complete.",
                Usage = new ModelUsage(10, 5),
                FinishReason = ModelFinishReason.Stop,
            };
        }
    }
}
