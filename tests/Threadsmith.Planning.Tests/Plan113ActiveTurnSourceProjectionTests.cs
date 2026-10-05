namespace Threadsmith.Planning.Tests;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Plan 113.2 exact active-turn source projection tests.</summary>
public static class Plan113ActiveTurnSourceProjectionTests
{
    /// <summary>The ordinary conversation path projects duplicates before sampling and avoids the summary provider.</summary>
    [Fact]
    public static async Task Conversation_projection_reduces_complete_request_and_advertises_recovery_only_with_receipts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-plan1132-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var lines = Enumerable.Range(1, 400)
                .Select(index => $"line {index:D4}: {new string((char)('a' + (index % 20)), 80)}")
                .ToArray();
            await File.WriteAllLinesAsync(Path.Combine(root, "fixture.txt"), lines);
            await using var events = new DomainEventStream();
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
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var budget = new ExecutionBudget(new BudgetDimensions(
                1_000_000,
                100,
                TimeSpan.FromMinutes(2)));
            var registry = new ToolRegistry(
            [
                new ReadFileTool(TestPromptLoader.Instance, sanitizer),
                new ActiveTurnEvidenceTool(evidence, TestPromptLoader.Instance),
            ]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                budget);
            var profile = CreateProfile();
            var summaryProfile = CreateCompactionCandidateProfile(ModelProfileId.New());
            var assembler = new ContextAssembler(
                evidence,
                new TokenEstimator(),
                new ContextPolicy(),
                new PromptAppendLoader(sanitizer),
                sanitizer,
                events,
                TestPromptLoader.Instance,
                new ContextAssemblerOptions { MaximumTokens = 65_536 },
                new ModelResolver(
                    new ConfiguredModelCatalog([profile]),
                    new InMemoryModelPreferenceSnapshotProvider()));
            var model = new DuplicateReadProvider();
            var compactionPolicy = new ActiveTurnCompactionPolicy
            {
                PressureTargetPercent = 21,
                OutputReserveTokens = 128,
                MinimumSavingsTokens = 1,
            };
            var summaryProvider = new UnexpectedSummaryProvider();
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
                activeTurnCompactor: new ActiveTurnCompactor(
                    summaryProvider,
                    new ActiveTurnCompactionValidator(compactionPolicy, sanitizer, TestPromptLoader.Instance),
                    compactionPolicy,
                    TestPromptLoader.Instance),
                activeTurnCompactionPolicy: compactionPolicy,
                activeTurnCompactionProfile: summaryProfile,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("plan-113.2"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "Read the same fixture twice, then finish."));

            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
            Assert.Equal(3, model.Requests.Count);
            Assert.DoesNotContain(
                model.Requests[0].Tools,
                tool => tool.Name == ActiveTurnEvidenceTool.ToolId);
            Assert.DoesNotContain(
                model.Requests[1].Tools,
                tool => tool.Name == ActiveTurnEvidenceTool.ToolId);
            Assert.Equal(0, model.Requests[0].HistoryRewriteGeneration);
            Assert.Equal(0, model.Requests[1].HistoryRewriteGeneration);
            var projectedRequest = model.Requests[2];
            Assert.True(
                projectedRequest.Tools.Any(tool => tool.Name == ActiveTurnEvidenceTool.ToolId),
                $"wire estimates: {string.Join(", ", model.Requests.Select(item => item.WireEstimate?.WireInputTokens))}; "
                + $"status: {assembler.GetInspection(runId)?.ActiveTurnCompaction?.Status}; "
                + $"target: {assembler.GetInspection(runId)?.ActiveTurnCompaction?.PressureTargetTokens}; "
                + $"tool results: {string.Join(", ", projectedRequest.Messages.Where(item => item.Role == ModelMessageRole.Tool).Select(item => $"{item.ToolCallId}:{item.GetModelVisibleContentLength()}"))}");
            Assert.Equal(1, projectedRequest.HistoryRewriteGeneration);
            var readResults = projectedRequest.Messages
                .Where(message => message.Role == ModelMessageRole.Tool && message.ToolName == "read_file")
                .ToArray();
            Assert.Equal(2, readResults.Length);
            var older = JsonSerializer.Deserialize<ReadFileOutput>(readResults[0].GetModelVisibleContent());
            var newer = JsonSerializer.Deserialize<ReadFileOutput>(readResults[1].GetModelVisibleContent());
            Assert.NotNull(older?.SourceReceipt);
            Assert.Empty(older.Lines);
            Assert.Equal(400, newer?.Lines.Count);
            var inspection = assembler.GetInspection(runId)?.ActiveTurnCompaction;
            Assert.NotNull(inspection);
            Assert.Equal(ActiveTurnCompactionInspectionStatus.DeterministicReduction, inspection.Status);
            Assert.Equal(profile.Id.Value, inspection.CandidateProfileId);
            Assert.NotEqual(summaryProfile.ProfileId.Value, inspection.CandidateProfileId);
            Assert.True(inspection.SummaryAvoidedBySourceProjection);
            Assert.Equal(1, inspection.SourceRemovedRangeCount);
            Assert.Equal(36_615, inspection.SourceReclaimedCharacters);
            // Exact ordinary-conversation envelope including model-selected validation guidance.
            Assert.Equal(21_602, inspection.BeforeInputTokens);
            Assert.Equal(12_539, inspection.AfterInputTokens);
            Assert.Equal(2_582, model.Requests[0].WireEstimate?.WireInputTokens);
            Assert.Equal(12_069, model.Requests[1].WireEstimate?.WireInputTokens);
            Assert.Equal(12_539, projectedRequest.WireEstimate?.WireInputTokens);
            Assert.Equal(
                36_253,
                model.Requests[0].WireEstimate?.WireInputTokens
                    + model.Requests[1].WireEstimate?.WireInputTokens
                    + inspection.BeforeInputTokens);
            Assert.Equal(
                27_190,
                model.Requests.Sum(request => request.WireEstimate?.WireInputTokens));
            Assert.Equal(0, summaryProvider.PrepareCalls);
            Assert.Collection(
                activityEvents,
                domainEvent =>
                {
                    var started = Assert.IsType<ActiveTurnCompactionStarted>(domainEvent);
                    Assert.Equal(profile.Id, started.CandidateProfileId);
                    Assert.Equal(inspection.BeforeInputTokens, started.BeforeInputTokens);
                    Assert.Equal(inspection.PressureTargetTokens, started.PressureTargetTokens);
                },
                domainEvent =>
                {
                    var completed = Assert.IsType<ActiveTurnCompactionCompleted>(domainEvent);
                    Assert.Equal(profile.Id, completed.CandidateProfileId);
                    Assert.Equal(
                        ActiveTurnCompactionInspectionStatus.DeterministicReduction,
                        completed.Status);
                    Assert.Equal(inspection.BeforeInputTokens, completed.BeforeInputTokens);
                    Assert.Equal(inspection.AfterInputTokens, completed.AfterInputTokens);
                    Assert.Null(completed.DurationMilliseconds);
                });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A delivered start event is closed when its publisher reports a delivery failure.</summary>
    [Fact]
    public static async Task Conversation_projection_closes_activity_after_start_publication_failure()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-plan1132-event-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var lines = Enumerable.Range(1, 400)
                .Select(index => $"line {index:D4}: {new string((char)('a' + (index % 20)), 80)}")
                .ToArray();
            await File.WriteAllLinesAsync(Path.Combine(root, "fixture.txt"), lines);
            await using var events = new StartPublicationFailingEventStream();
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
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var budget = new ExecutionBudget(new BudgetDimensions(
                1_000_000,
                100,
                TimeSpan.FromMinutes(2)));
            var registry = new ToolRegistry(
            [
                new ReadFileTool(TestPromptLoader.Instance, sanitizer),
                new ActiveTurnEvidenceTool(evidence, TestPromptLoader.Instance),
            ]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                budget);
            var profile = CreateProfile();
            var assembler = new ContextAssembler(
                evidence,
                new TokenEstimator(),
                new ContextPolicy(),
                new PromptAppendLoader(sanitizer),
                sanitizer,
                events,
                TestPromptLoader.Instance,
                new ContextAssemblerOptions { MaximumTokens = 65_536 },
                new ModelResolver(
                    new ConfiguredModelCatalog([profile]),
                    new InMemoryModelPreferenceSnapshotProvider()));
            var model = new DuplicateReadProvider();
            var policy = new ActiveTurnCompactionPolicy
            {
                PressureTargetPercent = 21,
                OutputReserveTokens = 128,
                MinimumSavingsTokens = 1,
            };
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
                activeTurnCompactionPolicy: policy,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("plan-113.2-event-failure"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "Read the same fixture twice, then finish."));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

            Assert.Contains("simulated start publication failure", exception.Message, StringComparison.Ordinal);
            Assert.Collection(
                activityEvents,
                domainEvent => Assert.IsType<ActiveTurnCompactionStarted>(domainEvent),
                domainEvent =>
                {
                    var completed = Assert.IsType<ActiveTurnCompactionCompleted>(domainEvent);
                    Assert.Equal(ActiveTurnCompactionInspectionStatus.ProviderFailure, completed.Status);
                    Assert.Equal(completed.BeforeInputTokens, completed.AfterInputTokens);
                });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Receipt and schema overhead reject a tiny duplicate without changing request history.</summary>
    [Fact]
    public static async Task Conversation_no_savings_projection_preserves_raw_history_and_provider_calls()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-plan1132-noop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "fixture.txt"), "tiny");
            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var budget = new ExecutionBudget(new BudgetDimensions(
                1_000_000,
                100,
                TimeSpan.FromMinutes(2)));
            var registry = new ToolRegistry(
            [
                new ReadFileTool(TestPromptLoader.Instance, sanitizer),
                new ActiveTurnEvidenceTool(evidence, TestPromptLoader.Instance),
            ]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                budget);
            var profile = CreateProfile();
            var assembler = new ContextAssembler(
                evidence,
                new TokenEstimator(),
                new ContextPolicy(),
                new PromptAppendLoader(sanitizer),
                sanitizer,
                events,
                TestPromptLoader.Instance,
                new ContextAssemblerOptions { MaximumTokens = 65_536 },
                new ModelResolver(
                    new ConfiguredModelCatalog([profile]),
                    new InMemoryModelPreferenceSnapshotProvider()));
            var model = new SmallDuplicateReadProvider();
            var policy = new ActiveTurnCompactionPolicy
            {
                PressureTargetPercent = 1,
                OutputReserveTokens = 128,
                MinimumSavingsTokens = 1,
            };
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
                activeTurnCompactionPolicy: policy,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("plan-113.2-noop"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "Read the tiny fixture twice, then finish."));

            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
            Assert.Equal(3, model.Requests.Count);
            Assert.All(model.Requests, request => Assert.Equal(0, request.HistoryRewriteGeneration));
            Assert.All(
                model.Requests,
                request => Assert.DoesNotContain(
                    request.Tools,
                    tool => tool.Name == ActiveTurnEvidenceTool.ToolId));
            var finalReads = model.Requests[^1].Messages
                .Where(message => message.Role == ModelMessageRole.Tool && message.ToolName == "read_file")
                .ToArray();
            Assert.Equal(2, finalReads.Length);
            Assert.All(finalReads, message =>
            {
                var visibleContent = message.GetModelVisibleContent();
                Assert.Contains("tiny", visibleContent, StringComparison.Ordinal);
                Assert.DoesNotContain("sourceReceipt", visibleContent, StringComparison.Ordinal);
            });
            var inspection = assembler.GetInspection(runId)?.ActiveTurnCompaction;
            Assert.NotNull(inspection);
            Assert.Equal(ActiveTurnCompactionInspectionStatus.Disabled, inspection.Status);
            Assert.Equal(1, inspection.SourceRemovedRangeCount);
            Assert.Equal(0, inspection.HistoryRewriteGeneration);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>An active projection remains applied to the exact prefix of a later summary fallback.</summary>
    [Fact]
    public static async Task Conversation_active_projection_flows_into_later_summary_candidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-plan1132-summary-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var lines = Enumerable.Range(1, 400)
                .Select(index => $"line {index:D4}: {new string((char)('a' + (index % 20)), 80)}")
                .ToArray();
            var uniqueLines = Enumerable.Range(1, 400)
                .Select(index => $"unique {index:D4}: {new string((char)('A' + (index % 20)), 80)}")
                .ToArray();
            await File.WriteAllLinesAsync(Path.Combine(root, "fixture.txt"), lines);
            await File.WriteAllLinesAsync(Path.Combine(root, "unique.txt"), uniqueLines);
            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var budget = new ExecutionBudget(new BudgetDimensions(
                1_000_000,
                100,
                TimeSpan.FromMinutes(2)));
            var registry = new ToolRegistry(
            [
                new ReadFileTool(TestPromptLoader.Instance, sanitizer),
                new ActiveTurnEvidenceTool(evidence, TestPromptLoader.Instance),
            ]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                budget);
            var profile = CreateProfile();
            var assembler = new ContextAssembler(
                evidence,
                new TokenEstimator(),
                new ContextPolicy(),
                new PromptAppendLoader(sanitizer),
                sanitizer,
                events,
                TestPromptLoader.Instance,
                new ContextAssemblerOptions { MaximumTokens = 65_536 },
                new ModelResolver(
                    new ConfiguredModelCatalog([profile]),
                    new InMemoryModelPreferenceSnapshotProvider()));
            var model = new ProjectionThenUniqueReadProvider();
            var policy = new ActiveTurnCompactionPolicy
            {
                PressureTargetPercent = 21,
                OutputReserveTokens = 128,
                MinimumSavingsTokens = 1,
                RetainedRecentTokens = 1,
            };
            var summaryProvider = new CapturingFailingSummaryProvider();
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
                activeTurnCompactor: new ActiveTurnCompactor(
                    summaryProvider,
                    new ActiveTurnCompactionValidator(policy, sanitizer, TestPromptLoader.Instance),
                    policy,
                    TestPromptLoader.Instance),
                activeTurnCompactionPolicy: policy,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("plan-113.2-summary"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "Read the overlapping fixture, then unique evidence."));

            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
            Assert.Equal(4, model.Requests.Count);
            Assert.Equal(1, model.Requests[2].HistoryRewriteGeneration);
            Assert.Equal(1, model.Requests[3].HistoryRewriteGeneration);
            var summaryRequest = Assert.Single(summaryProvider.Requests);
            Assert.True(summaryRequest.ProjectedEligiblePrefixes.TryGetValue(2, out var projectedPrefix));
            Assert.NotNull(projectedPrefix);
            var projectedOlder = projectedPrefix[0].Messages.Single(
                message => message.Role == ModelMessageRole.Tool && message.ToolName == "read_file");
            var projectedOutput = JsonSerializer.Deserialize<ReadFileOutput>(
                projectedOlder.Content.Single(part =>
                    part.IsModelVisible && part.Kind == ModelContentPartKind.Json).Content);
            Assert.NotNull(projectedOutput?.SourceReceipt);
            Assert.Empty(projectedOutput.Lines);
            Assert.Equal("host-tool-2-1", projectedOutput.SourceReceipt.ToolCallId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Equal and contained delivered reads project to receipts backed by the newest raw range.</summary>
    [Fact]
    public static void Read_file_equal_and_contained_ranges_use_direct_newest_support()
    {
        var firstEvidence = EvidenceId.New();
        var secondEvidence = EvidenceId.New();
        var thirdEvidence = EvidenceId.New();
        ActiveTurnContinuationGroup[] groups =
        [
            CreateReadGroup(1, "call-a", firstEvidence, 2, ["two", "three"], "file-v1", delivered: true),
            CreateReadGroup(2, "call-b", secondEvidence, 1, ["one", "two", "three", "four"], "file-v1", delivered: true),
            CreateReadGroup(3, "call-c", thirdEvidence, 1, ["one", "two", "three", "four"], "file-v1", delivered: false),
        ];

        var projection = new ActiveTurnSourceProjector(TestPromptLoader.Instance).Propose(
            groups,
            Environment.CurrentDirectory,
            workspaceId: null,
            historyRewriteGeneration: 0);

        Assert.NotNull(projection);
        Assert.Equal(2, projection.RemovedRangeCount);
        Assert.Equal(3, projection.CandidateRangeCount);
        Assert.Equal(2, projection.EvidenceReferences.Count);
        foreach (var sequence in new long[] { 1, 2 })
        {
            var result = projection.GroupMessages[sequence].Single(message => message.Role == ModelMessageRole.Tool);
            var output = JsonSerializer.Deserialize<ReadFileOutput>(result.GetModelVisibleContent());
            Assert.NotNull(output?.SourceReceipt);
            Assert.Empty(output.Lines);
            Assert.Equal("call-c", output.SourceReceipt.ToolCallId);
        }

        var finalMessages = groups.SelectMany(group =>
            projection.GroupMessages.GetValueOrDefault(group.Sequence) ?? group.Messages).ToArray();
        Assert.True(ActiveTurnSourceProjector.ValidateFinalRequest(
            finalMessages,
            Environment.CurrentDirectory,
            workspaceId: null,
            historyRewriteGeneration: 1));

        var afterOldestSummary = projection.RetainGroups(new HashSet<long> { 2, 3 });
        Assert.NotNull(afterOldestSummary);
        var afterSummaryMessages = groups
            .Where(group => group.Sequence >= 2)
            .SelectMany(group => afterOldestSummary.GroupMessages.GetValueOrDefault(group.Sequence) ?? group.Messages)
            .ToArray();
        Assert.True(ActiveTurnSourceProjector.ValidateFinalRequest(
            afterSummaryMessages,
            Environment.CurrentDirectory,
            workspaceId: null,
            historyRewriteGeneration: 2));

        var strandedMessages = projection.GroupMessages[1];
        Assert.False(ActiveTurnSourceProjector.ValidateFinalRequest(
            strandedMessages,
            Environment.CurrentDirectory,
            workspaceId: null,
            historyRewriteGeneration: 2));
    }

    /// <summary>The existing summary provider serializes the projection for its exact selected prefix.</summary>
    [Fact]
    public static void Summary_candidate_uses_dependency_safe_projected_prefix()
    {
        ActiveTurnContinuationGroup[] groups =
        [
            CreateReadGroup(1, "call-a", EvidenceId.New(), 2, ["two", "three"], "file-v1", delivered: true),
            CreateReadGroup(2, "call-b", EvidenceId.New(), 1, ["one", "two", "three", "four"], "file-v1", delivered: true),
        ];
        var projection = new ActiveTurnSourceProjector(TestPromptLoader.Instance).Propose(
            groups,
            Environment.CurrentDirectory,
            workspaceId: null,
            historyRewriteGeneration: 0);
        Assert.NotNull(projection);
        var projectedGroups = groups.Select(group => projection.GroupMessages.TryGetValue(
            group.Sequence,
            out var projectedMessages)
                ? group with { Messages = projectedMessages }
                : group).ToArray();
        var policy = new ActiveTurnCompactionPolicy
        {
            SummaryBudgetTokens = 256,
            OutputReserveTokens = 128,
            MaximumInputTokens = 65_536,
        };
        var provider = new ModelActiveTurnCompactionCandidateProvider(
            new DuplicateReadProvider(),
            policy,
            TestPromptLoader.Instance);
        var attempt = provider.PrepareCandidate(new ActiveTurnCompactionRequest
        {
            RunId = RunId.New(),
            ProfileId = ModelProfileId.New(),
            ProfileMaximumOutputTokens = 4_096,
            ProfileContextWindowTokens = 65_536,
            ProfileOutputReserveTokens = 128,
            FrozenContextIdentity = "fixture",
            TaskObjective = "Summarize the reads.",
            AcceptanceIntent = [],
            EligiblePrefix = groups,
            ProjectedEligiblePrefixes = new Dictionary<int, IReadOnlyList<ActiveTurnContinuationGroup>>
            {
                [2] = projectedGroups,
            },
            SelectionConstraints = new ModelSelectionConstraints(),
            BeforeInputTokens = 1_000,
            PressureTargetTokens = 500,
        });

        Assert.Equal(2, attempt.SelectedGroupCount);
        var preparedRequest = attempt.PreparedRequest;
        Assert.NotNull(preparedRequest);
        var input = preparedRequest.Input;
        Assert.NotNull(input);
        using var document = JsonDocument.Parse(input);
        var firstActivity = document.RootElement.GetProperty("newToolActivity")[0];
        var firstToolResultJson = firstActivity.GetProperty("messages")[1]
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString();
        var firstToolResult = JsonSerializer.Deserialize<ReadFileOutput>(firstToolResultJson!);
        Assert.NotNull(firstToolResult?.SourceReceipt);
        Assert.Empty(firstToolResult.Lines);
        Assert.Equal("call-b", firstToolResult.SourceReceipt.ToolCallId);
    }

    /// <summary>Candidate selection evaluates projected prefixes whose sizes can decrease.</summary>
    [Fact]
    public static void Summary_candidate_selects_later_projected_prefix_after_larger_intermediate_prefix()
    {
        var largeLine = new string('x', 8_000);
        ActiveTurnContinuationGroup[] groups =
        [
            CreateReadGroup(1, "call-a", EvidenceId.New(), 1, ["small"], "file-v1", delivered: true),
            CreateReadGroup(2, "call-b", EvidenceId.New(), 1, [largeLine], "file-v2", delivered: true),
            CreateReadGroup(3, "call-c", EvidenceId.New(), 1, [largeLine], "file-v3", delivered: true),
        ];
        var projectedGroups = groups
            .Select(group => group with { Messages = [] })
            .ToArray();
        var policy = new ActiveTurnCompactionPolicy
        {
            SummaryBudgetTokens = 256,
            OutputReserveTokens = 128,
            MaximumInputTokens = 1_500,
        };
        var provider = new ModelActiveTurnCompactionCandidateProvider(
            new DuplicateReadProvider(),
            policy,
            TestPromptLoader.Instance);

        var attempt = provider.PrepareCandidate(new ActiveTurnCompactionRequest
        {
            RunId = RunId.New(),
            ProfileId = ModelProfileId.New(),
            ProfileMaximumOutputTokens = 4_096,
            ProfileContextWindowTokens = 65_536,
            ProfileOutputReserveTokens = 128,
            FrozenContextIdentity = "fixture",
            TaskObjective = "Summarize the reads.",
            AcceptanceIntent = [],
            EligiblePrefix = groups,
            ProjectedEligiblePrefixes = new Dictionary<int, IReadOnlyList<ActiveTurnContinuationGroup>>
            {
                [3] = projectedGroups,
            },
            SelectionConstraints = new ModelSelectionConstraints(),
            BeforeInputTokens = 10_000,
            PressureTargetTokens = 5_000,
        });

        Assert.Equal(3, attempt.SelectedGroupCount);
    }

    /// <summary>Projection observes cancellation before processing retained source candidates.</summary>
    [Fact]
    public static void Source_projection_observes_cancellation()
    {
        var cancellation = new CancellationToken(canceled: true);
        var group = CreateReadGroup(
            1,
            "call-a",
            EvidenceId.New(),
            1,
            ["one"],
            "file-v1",
            delivered: true);

        Assert.Throws<OperationCanceledException>(() =>
            new ActiveTurnSourceProjector(TestPromptLoader.Instance).Propose(
                [group],
                Environment.CurrentDirectory,
                workspaceId: null,
                historyRewriteGeneration: 0,
                cancellationToken: cancellation));
    }

    /// <summary>Partial overlap, changed snapshots, and first delivery remain byte-for-byte raw.</summary>
    [Theory]
    [InlineData(1, 2, "file-v1", true)]
    [InlineData(1, 4, "file-v2", true)]
    [InlineData(1, 4, "file-v1", false)]
    public static void Uncertain_or_never_delivered_reads_remain_raw(
        int laterStart,
        int laterCount,
        string laterDigest,
        bool olderDelivered)
    {
        var older = CreateReadGroup(
            1,
            "call-a",
            EvidenceId.New(),
            2,
            ["two", "three", "four"],
            "file-v1",
            olderDelivered);
        var laterLines = Enumerable.Range(laterStart, laterCount)
            .Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        var later = CreateReadGroup(
            2,
            "call-b",
            EvidenceId.New(),
            laterStart,
            laterLines,
            laterDigest,
            delivered: false);

        var projection = new ActiveTurnSourceProjector(TestPromptLoader.Instance).Propose(
            [older, later],
            Environment.CurrentDirectory,
            workspaceId: null,
            historyRewriteGeneration: 0);

        Assert.Null(projection);
    }

    /// <summary>Matching snapshot metadata cannot cover a different sanitized visible range.</summary>
    [Fact]
    public static void Sanitized_visible_content_mismatch_remains_raw()
    {
        var older = CreateReadGroup(1, "call-a", EvidenceId.New(), 1, ["safe", "[REDACTED]"], "file-v1", delivered: true);
        var later = CreateReadGroup(2, "call-b", EvidenceId.New(), 1, ["safe", "different"], "file-v1", delivered: false);

        var projection = new ActiveTurnSourceProjector(TestPromptLoader.Instance).Propose(
            [older, later],
            Environment.CurrentDirectory,
            workspaceId: null,
            historyRewriteGeneration: 0);

        Assert.Null(projection);
    }

    /// <summary>Matching claims from different producers remain raw until a shared presentation contract exists.</summary>
    [Fact]
    public static void Cross_producer_source_coverage_remains_raw()
    {
        var older = CreateReadGroup(
            1,
            "read-a",
            EvidenceId.New(),
            1,
            ["1: one"],
            "file-v1",
            delivered: true);
        var later = CreateCodeExploreGroup(
            2,
            "code-b",
            EvidenceId.New(),
            ["1: one"],
            new SourceRange(1, 1, 1, 4),
            delivered: false,
            exposeSource: true);

        Assert.Null(new ActiveTurnSourceProjector(TestPromptLoader.Instance).Propose(
            [older, later],
            Environment.CurrentDirectory,
            WorkspaceId.New(),
            historyRewriteGeneration: 0));
    }

    /// <summary>The candidate cap fails closed when the possible survivor lies beyond the bound.</summary>
    [Fact]
    public static void Candidate_bound_retains_source_without_bounded_proof()
    {
        var groups = new List<ActiveTurnContinuationGroup>
        {
            CreateReadGroup(1, "call-1", EvidenceId.New(), 1, ["same"], "target", delivered: true),
        };
        for (var index = 2; index <= 256; index++)
        {
            groups.Add(CreateReadGroup(
                index,
                $"call-{index}",
                EvidenceId.New(),
                1,
                ["same"],
                $"unique-{index}",
                delivered: true));
        }

        groups.Add(CreateReadGroup(
            257,
            "call-257",
            EvidenceId.New(),
            1,
            ["same"],
            "target",
            delivered: false));

        Assert.Null(new ActiveTurnSourceProjector(TestPromptLoader.Instance).Propose(
            groups,
            Environment.CurrentDirectory,
            workspaceId: null,
            historyRewriteGeneration: 0));
    }

    /// <summary>Failed results, missing identities, and unknown producers remain opaque and raw.</summary>
    [Fact]
    public static void Failed_evicted_and_unknown_results_remain_raw()
    {
        var failed = CreateReadGroup(
            1,
            "read-a",
            EvidenceId.New(),
            1,
            ["same"],
            "file-v1",
            delivered: true);
        failed = failed with
        {
            Messages = failed.Messages.Select(message => message.Role == ModelMessageRole.Tool
                ? message with { IsError = true }
                : message).ToArray(),
        };
        var evicted = CreateReadGroup(
            2,
            "read-b",
            EvidenceId.New(),
            1,
            ["same"],
            "file-v1",
            delivered: true) with
        {
            Results = [],
        };
        var unknown = CreateGroup(
            3,
            "unknown-c",
            "extension_source",
            EvidenceId.New(),
            "same",
            hiddenJson: null,
            delivered: false);

        Assert.Null(new ActiveTurnSourceProjector(TestPromptLoader.Instance).Propose(
            [failed, evicted, unknown],
            Environment.CurrentDirectory,
            workspaceId: null,
            historyRewriteGeneration: 0));
    }

    /// <summary>A visible Markdown code range can cover an older delivered range while its hidden sidecar alone cannot.</summary>
    [Fact]
    public static void Code_explore_requires_visible_source_and_preserves_non_source_facts()
    {
        var older = CreateCodeExploreGroup(
            1,
            "code-a",
            EvidenceId.New(),
            ["2: two"],
            new SourceRange(2, 1, 2, 4),
            delivered: true,
            exposeSource: true);
        var later = CreateCodeExploreGroup(
            2,
            "code-b",
            EvidenceId.New(),
            ["1: one", "2: two", "3: three"],
            new SourceRange(1, 1, 3, 6),
            delivered: false,
            exposeSource: true);

        var projector = new ActiveTurnSourceProjector(TestPromptLoader.Instance);
        var projection = projector.Propose(
            [older, later],
            Environment.CurrentDirectory,
            WorkspaceId.New(),
            historyRewriteGeneration: 0);

        Assert.NotNull(projection);
        var projected = projection.GroupMessages[1].Single(message => message.Role == ModelMessageRole.Tool);
        Assert.Contains("code-b", projected.GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.DoesNotContain("2: two", projected.GetModelVisibleContent(), StringComparison.Ordinal);
        var structured = JsonSerializer.Deserialize<CodeExploreResult>(
            projected.Content.Single(part => !part.IsModelVisible && part.Kind == ModelContentPartKind.Json).Content);
        Assert.NotNull(structured);
        Assert.Empty(structured.FileSections);
        Assert.Equal(SemanticConfidenceLevel.FullSemantic, structured.Confidence);

        var hiddenOnly = CreateCodeExploreGroup(
            2,
            "code-hidden",
            EvidenceId.New(),
            ["1: one", "2: two", "3: three"],
            new SourceRange(1, 1, 3, 6),
            delivered: false,
            exposeSource: false);
        Assert.Null(projector.Propose(
            [older, hiddenOnly],
            Environment.CurrentDirectory,
            WorkspaceId.New(),
            historyRewriteGeneration: 0));
    }

    /// <summary>Projection never makes a source section visible when it existed only in the hidden sidecar.</summary>
    [Fact]
    public static void Code_explore_projection_does_not_reveal_hidden_sibling_source()
    {
        var older = CreateMultiSectionCodeExploreGroup(
            1,
            "code-a",
            EvidenceId.New(),
            delivered: true);
        var later = CreateCodeExploreGroup(
            2,
            "code-b",
            EvidenceId.New(),
            ["1: visible"],
            new SourceRange(1, 1, 1, 8),
            delivered: false,
            exposeSource: true);

        var projection = new ActiveTurnSourceProjector(TestPromptLoader.Instance).Propose(
            [older, later],
            Environment.CurrentDirectory,
            WorkspaceId.New(),
            historyRewriteGeneration: 0);

        Assert.NotNull(projection);
        var projected = projection.GroupMessages[1].Single(message => message.Role == ModelMessageRole.Tool);
        Assert.DoesNotContain("99: hidden", projected.GetModelVisibleContent(), StringComparison.Ordinal);
        var structured = JsonSerializer.Deserialize<CodeExploreResult>(
            projected.Content.Single(part => !part.IsModelVisible && part.Kind == ModelContentPartKind.Json).Content);
        Assert.NotNull(structured);
        Assert.Single(structured.FileSections);
        Assert.Equal("src/hidden.cs", structured.FileSections[0].FilePath);
        Assert.Contains("99: hidden", structured.FileSections[0].Source.NumberedLines);
    }

    /// <summary>Projection retains visible unique source even when that sibling lacks proof metadata.</summary>
    [Fact]
    public static void Code_explore_projection_retains_visible_opaque_sibling_source()
    {
        var older = CreateOpaqueSiblingCodeExploreGroup(
            1,
            "code-a",
            EvidenceId.New(),
            delivered: true);
        var later = CreateCodeExploreGroup(
            2,
            "code-b",
            EvidenceId.New(),
            ["1: visible"],
            new SourceRange(1, 1, 1, 8),
            delivered: false,
            exposeSource: true);

        var projection = new ActiveTurnSourceProjector(TestPromptLoader.Instance).Propose(
            [older, later],
            Environment.CurrentDirectory,
            WorkspaceId.New(),
            historyRewriteGeneration: 0);

        Assert.NotNull(projection);
        var projected = projection.GroupMessages[1].Single(message => message.Role == ModelMessageRole.Tool);
        Assert.Contains("99: unique opaque", projected.GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.DoesNotContain("1: visible", projected.GetModelVisibleContent(), StringComparison.Ordinal);
    }

    /// <summary>Code ranges from different semantic workspace generations never cover each other.</summary>
    [Fact]
    public static void Code_explore_workspace_generation_mismatch_remains_raw()
    {
        var older = CreateCodeExploreGroup(
            1,
            "code-a",
            EvidenceId.New(),
            ["1: one"],
            new SourceRange(1, 1, 1, 4),
            delivered: true,
            exposeSource: true,
            generation: 7);
        var later = CreateCodeExploreGroup(
            2,
            "code-b",
            EvidenceId.New(),
            ["1: one"],
            new SourceRange(1, 1, 1, 4),
            delivered: false,
            exposeSource: true,
            generation: 8);

        Assert.Null(new ActiveTurnSourceProjector(TestPromptLoader.Instance).Propose(
            [older, later],
            Environment.CurrentDirectory,
            WorkspaceId.New(),
            historyRewriteGeneration: 0));
    }

    /// <summary>Recovery is request-authorized, bounded, pageable, and labels stale or missing bodies.</summary>
    [Fact]
    public static async Task Recovery_reads_only_current_request_evidence_and_reports_lifecycle_state()
    {
        await using var events = new DomainEventStream();
        var store = new EvidenceStore(events, new SecretOutputSanitizer());
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var availableId = EvidenceId.New();
        var staleId = EvidenceId.New();
        var missingId = EvidenceId.New();
        var availableInvocationId = ToolInvocationId.New();
        var staleInvocationId = ToolInvocationId.New();
        var missingInvocationId = ToolInvocationId.New();
        var repositoryIdentity = RepositoryIdentity.Create(Environment.CurrentDirectory);
        await store.AddBatchAsync(
        [
            CreateEvidence(
                sessionId,
                runId,
                availableId,
                availableInvocationId,
                string.Join('\n', Enumerable.Repeat(new string('x', 1024), 160)),
                "src/first-file.cs"),
            CreateEvidence(sessionId, runId, staleId, staleInvocationId, "stale body", "src/file.cs"),
        ]);
        store.QueueInvalidation(sessionId, "stale-key", "fixture invalidation");
        Assert.Equal(1, await store.ApplyInvalidationsAsync(sessionId));
        var references = new[]
        {
            new ActiveTurnEvidenceReference(
                availableId,
                "call-a",
                availableInvocationId,
                repositoryIdentity,
                "src/second-file.cs",
                1,
                160),
            new ActiveTurnEvidenceReference(
                staleId,
                "call-b",
                staleInvocationId,
                repositoryIdentity,
                "src/file.cs",
                1,
                1),
            new ActiveTurnEvidenceReference(
                missingId,
                "call-c",
                missingInvocationId,
                repositoryIdentity,
                "src/file.cs",
                1,
                1),
        };
        var invocation = new ToolInvocationContext
        {
            RepositoryPath = Environment.CurrentDirectory,
            TrustLevel = RepositoryTrustLevel.TrustedRead,
            RequestedBy = "model",
            ActiveTurnEvidenceReferences = references,
        };
        var tool = new ActiveTurnEvidenceTool(store, TestPromptLoader.Instance);
        var pipeline = new ToolInvocationPipeline(
            new ToolRegistry([tool]),
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            new SecretOutputSanitizer(),
            NullLogger<ToolInvocationPipeline>.Instance,
            new ExecutionBudget(new BudgetDimensions(1_000_000, 20, TimeSpan.FromMinutes(1))));
        Task<ToolInvocationResult> InvokeAsync(
            EvidenceId evidenceId,
            int startLine = 1,
            int startColumn = 1,
            ToolInvocationContext? requestContext = null) =>
            pipeline.InvokeAsync(new ToolInvocationRequest
            {
                SessionId = sessionId,
                RunId = runId,
                Phase = RunPhase.EvidenceCollection,
                ToolId = ActiveTurnEvidenceTool.ToolId,
                ArgumentsJson = JsonSerializer.Serialize(new
                {
                    evidenceId = evidenceId.Value,
                    startLine,
                    startColumn,
                }),
                Context = requestContext ?? invocation,
            });

        var pipelineResult = await InvokeAsync(availableId);
        Assert.True(pipelineResult.Succeeded, pipelineResult.Error);
        var firstPage = JsonSerializer.Deserialize<ActiveTurnEvidenceOutput>(pipelineResult.ResultJson!);
        Assert.NotNull(firstPage);
        Assert.Equal(ActiveTurnEvidenceStatus.Available, firstPage.Status);
        Assert.True(pipelineResult.IsTruncated);
        Assert.NotNull(firstPage.NextLine);
        Assert.True(firstPage.Content?.Length < 160 * 1025);
        var page = firstPage;
        var pageCount = 1;
        while (page.NextLine is { } nextLine)
        {
            var pageResult = await InvokeAsync(availableId, nextLine, page.NextColumn ?? 1);
            Assert.True(pageResult.Succeeded, pageResult.Error);
            page = JsonSerializer.Deserialize<ActiveTurnEvidenceOutput>(pageResult.ResultJson!);
            Assert.NotNull(page);
            Assert.Equal(ActiveTurnEvidenceStatus.Available, page.Status);
            Assert.NotEmpty(page.Content ?? string.Empty);
            Assert.True(++pageCount < 20);
        }

        Assert.True(pageCount > 1);
        Assert.Null(page.NextLine);

        var staleResult = await InvokeAsync(staleId);
        Assert.True(staleResult.Succeeded, staleResult.Error);
        var stale = JsonSerializer.Deserialize<ActiveTurnEvidenceOutput>(staleResult.ResultJson!);
        Assert.Equal(ActiveTurnEvidenceStatus.Stale, stale?.Status);
        Assert.Contains("fixture invalidation", stale?.Detail, StringComparison.Ordinal);
        var missingResult = await InvokeAsync(missingId);
        Assert.True(missingResult.Succeeded, missingResult.Error);
        var missing = JsonSerializer.Deserialize<ActiveTurnEvidenceOutput>(missingResult.ResultJson!);
        Assert.Equal(ActiveTurnEvidenceStatus.Missing, missing?.Status);

        var unauthorized = await InvokeAsync(EvidenceId.New());
        Assert.False(unauthorized.Succeeded);

        var otherRepository = invocation with
        {
            RepositoryPath = Path.Combine(Environment.CurrentDirectory, "other-repository"),
        };
        var repositoryMismatchResult = await InvokeAsync(
            availableId,
            requestContext: otherRepository);
        Assert.True(repositoryMismatchResult.Succeeded, repositoryMismatchResult.Error);
        var repositoryMismatch = JsonSerializer.Deserialize<ActiveTurnEvidenceOutput>(
            repositoryMismatchResult.ResultJson!);
        Assert.Equal(ActiveTurnEvidenceStatus.Missing, repositoryMismatch?.Status);
    }

    private static ActiveTurnContinuationGroup CreateReadGroup(
        long sequence,
        string toolCallId,
        EvidenceId evidenceId,
        int startLine,
        IReadOnlyList<string> lines,
        string fileDigest,
        bool delivered)
    {
        var output = new ReadFileOutput(
            "src/file.cs",
            startLine,
            startLine + lines.Count - 1,
            20,
            lines,
            true,
            startLine + lines.Count,
            ReadFileTruncationReason.LineLimit)
        {
            FileSha256 = fileDigest,
            VisibleRangeSha256 = $"range-{startLine}-{lines.Count}",
        };
        return CreateGroup(sequence, toolCallId, "read_file", evidenceId, JsonSerializer.Serialize(output), null, delivered);
    }

    private static ActiveTurnContinuationGroup CreateCodeExploreGroup(
        long sequence,
        string toolCallId,
        EvidenceId evidenceId,
        IReadOnlyList<string> lines,
        SourceRange range,
        bool delivered,
        bool exposeSource,
        long generation = 7)
    {
        var source = new CodeExploreSourceRange(
            range,
            lines,
            "file-v1",
            $"range-{range.StartLine}-{range.EndLine}",
            CodeExploreSourceCompleteness.Complete,
            [],
            null);
        var result = new CodeExploreResult(
            generation,
            SemanticConfidenceLevel.FullSemantic,
            [],
            [new CodeExploreFileSection("src/file.cs", "Project", "net10.0", [], source, false, false, "selected")],
            new CodeExploreCoverage(true, true, true, true, []),
            [],
            []);
        var visible = exposeSource
            ? $"**`src/file.cs`**{Environment.NewLine}{string.Join(Environment.NewLine, lines)}"
            : "Source is intentionally absent from this model projection.";
        return CreateGroup(sequence, toolCallId, "code_explore", evidenceId, visible, JsonSerializer.Serialize(result), delivered);
    }

    private static ActiveTurnContinuationGroup CreateMultiSectionCodeExploreGroup(
        long sequence,
        string toolCallId,
        EvidenceId evidenceId,
        bool delivered)
    {
        static CodeExploreSourceRange Source(int line, string text) => new(
            new SourceRange(line, 1, line, text.Length),
            [$"{line}: {text}"],
            "file-v1",
            $"range-{line}",
            CodeExploreSourceCompleteness.Complete,
            [],
            null);

        var result = new CodeExploreResult(
            7,
            SemanticConfidenceLevel.FullSemantic,
            [],
            [
                new CodeExploreFileSection(
                    "src/file.cs",
                    "Project",
                    "net10.0",
                    [],
                    Source(1, "visible"),
                    false,
                    false,
                    "selected"),
                new CodeExploreFileSection(
                    "src/hidden.cs",
                    "Project",
                    "net10.0",
                    [],
                    Source(99, "hidden"),
                    false,
                    false,
                    "selected"),
            ],
            new CodeExploreCoverage(true, true, true, true, []),
            [],
            []);
        return CreateGroup(
            sequence,
            toolCallId,
            "code_explore",
            evidenceId,
            $"**`src/file.cs`**{Environment.NewLine}1: visible",
            JsonSerializer.Serialize(result),
            delivered);
    }

    private static ActiveTurnContinuationGroup CreateOpaqueSiblingCodeExploreGroup(
        long sequence,
        string toolCallId,
        EvidenceId evidenceId,
        bool delivered)
    {
        var removable = new CodeExploreSourceRange(
            new SourceRange(1, 1, 1, 8),
            ["1: visible"],
            "file-v1",
            "range-1",
            CodeExploreSourceCompleteness.Complete,
            [],
            null);
        var opaque = new CodeExploreSourceRange(
            new SourceRange(99, 1, 99, 13),
            ["99: unique opaque"],
            "file-v1",
            null,
            CodeExploreSourceCompleteness.Partial,
            [],
            null);
        var result = new CodeExploreResult(
            7,
            SemanticConfidenceLevel.FullSemantic,
            [],
            [
                new CodeExploreFileSection(
                    "src/file.cs",
                    "Project",
                    "net10.0",
                    [],
                    removable,
                    false,
                    false,
                    "selected"),
                new CodeExploreFileSection(
                    "src/opaque.cs",
                    "Project",
                    "net10.0",
                    [],
                    opaque,
                    false,
                    false,
                    "selected"),
            ],
            new CodeExploreCoverage(true, true, true, true, []),
            [],
            []);
        var visible = string.Join(
            Environment.NewLine,
            "**`src/file.cs`**",
            "1: visible",
            "**`src/opaque.cs`**",
            "99: unique opaque");
        return CreateGroup(
            sequence,
            toolCallId,
            "code_explore",
            evidenceId,
            visible,
            JsonSerializer.Serialize(result),
            delivered);
    }

    private static ActiveTurnContinuationGroup CreateGroup(
        long sequence,
        string toolCallId,
        string toolName,
        EvidenceId evidenceId,
        string visibleContent,
        string? hiddenJson,
        bool delivered)
    {
        var call = new ModelMessage
        {
            Role = ModelMessageRole.Assistant,
            SectionId = "tool-call",
            ToolCallId = toolCallId,
            ToolName = toolName,
            Content = [new ModelContentPart { Kind = ModelContentPartKind.Json, Content = "{}" }],
        };
        var parts = new List<ModelContentPart>
        {
            new()
            {
                Kind = hiddenJson is null ? ModelContentPartKind.Json : ModelContentPartKind.Text,
                Content = visibleContent,
            },
        };
        if (hiddenJson is not null)
        {
            parts.Add(new ModelContentPart
            {
                Kind = ModelContentPartKind.Json,
                Content = hiddenJson,
                IsModelVisible = false,
            });
        }

        var result = new ModelMessage
        {
            Role = ModelMessageRole.Tool,
            SectionId = "tool-result",
            ToolCallId = toolCallId,
            ToolName = toolName,
            Content = parts,
        };
        return new ActiveTurnContinuationGroup
        {
            Sequence = sequence,
            CompletedModelRound = (int)sequence,
            Messages = [call, result],
            Results = [new ActiveTurnResultReference(toolCallId, toolName, ToolInvocationId.New(), evidenceId)],
            Sources = [],
            FilesRead = ["src/file.cs"],
            FilesChanged = [],
            EstimatedTokens = 100,
            Sensitivity = ConversationSensitivity.None,
            WasDeliveredVerbatim = delivered,
        };
    }

    private static Evidence CreateEvidence(
        SessionId sessionId,
        RunId runId,
        EvidenceId evidenceId,
        ToolInvocationId toolInvocationId,
        string content,
        string sourcePath)
    {
        return new Evidence
        {
            EvidenceId = evidenceId,
            SessionId = sessionId,
            RunId = runId,
            Kind = EvidenceKind.ToolResult,
            Content = content,
            Provenance = new EvidenceProvenance
            {
                Source = "tool:read_file",
                SourcePath = sourcePath,
                ToolInvocationId = toolInvocationId,
            },
            CollectedAt = DateTimeOffset.UtcNow,
            Relevance = 1,
            EstimatedTokens = Math.Max(1, content.Length / 4),
            InvalidationKeys = content == "stale body" ? ["stale-key"] : ["repository"],
        };
    }

    private static ModelProfile CreateProfile()
    {
        return new ModelProfile
        {
            Id = ModelProfileId.New(),
            Name = "plan-113.2-test",
            Provider = "openai-compatible",
            Endpoint = new Uri("https://plan1132.example.test/v1/chat/completions"),
            ModelId = "plan-113.2-test",
            ContextWindow = 65_536,
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

    private static ActiveTurnCompactionCandidateProfile CreateCompactionCandidateProfile(
        ModelProfileId profileId)
    {
        return new ActiveTurnCompactionCandidateProfile
        {
            ProfileId = profileId,
            ContextWindowTokens = 32_768,
            OutputReserveTokens = 128,
            ReasoningLevel = ReasoningLevel.None,
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            Cost = new ModelCostMetadata(),
        };
    }

    private sealed class StartPublicationFailingEventStream : IDomainEventStream
    {
        private readonly DomainEventStream _inner = new();
        private bool _failed;

        public IDomainEventSubscription Subscribe(
            Func<IDomainEvent, CancellationToken, Task> handler,
            int capacity = 256)
        {
            return _inner.Subscribe(handler, capacity);
        }

        public async Task PublishAsync(
            IDomainEvent domainEvent,
            CancellationToken cancellationToken = default)
        {
            await _inner.PublishAsync(domainEvent, cancellationToken);
            if (!_failed && domainEvent is ActiveTurnCompactionStarted)
            {
                _failed = true;
                throw new InvalidOperationException("simulated start publication failure");
            }
        }

        public Task PublishCommittedBatchAsync(
            IReadOnlyList<IDomainEvent> domainEvents,
            Func<bool> tryCommit,
            CancellationToken cancellationToken = default)
        {
            return _inner.PublishCommittedBatchAsync(domainEvents, tryCommit, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            return _inner.DisposeAsync();
        }
    }

    private sealed class DuplicateReadProvider : IModelProvider
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
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput(
                        "read_file",
                        Requests.Count == 1
                            ? "{\"path\":\"fixture.txt\",\"startLine\":2,\"maximumLines\":398}"
                            : "{\"path\":\"fixture.txt\",\"startLine\":1,\"maximumLines\":400}"),
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

    private sealed class SmallDuplicateReadProvider : IModelProvider
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
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput(
                        "read_file",
                        Requests.Count == 1
                            ? "{\"path\":\"fixture.txt\",\"startLine\":1}"
                            : "{\"path\":\"fixture.txt\",\"startLine\":1,\"maximumLines\":1}"),
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

    private sealed class ProjectionThenUniqueReadProvider : IModelProvider
    {
        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Requests.Count <= 3)
            {
                var arguments = Requests.Count switch
                {
                    1 => "{\"path\":\"fixture.txt\",\"startLine\":2,\"maximumLines\":398}",
                    2 => "{\"path\":\"fixture.txt\",\"startLine\":1,\"maximumLines\":400}",
                    _ => "{\"path\":\"unique.txt\",\"startLine\":1,\"maximumLines\":400}",
                };
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput("read_file", arguments),
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

    private sealed class CapturingFailingSummaryProvider : IActiveTurnCompactionCandidateProvider
    {
        public List<ActiveTurnCompactionRequest> Requests { get; } = [];

        public IActiveTurnCompactionCandidateAttempt PrepareCandidate(
            ActiveTurnCompactionRequest request)
        {
            Requests.Add(request);
            throw new ModelProviderException("Synthetic summary preflight failure.");
        }
    }

    private sealed class UnexpectedSummaryProvider : IActiveTurnCompactionCandidateProvider
    {
        public int PrepareCalls { get; private set; }

        public IActiveTurnCompactionCandidateAttempt PrepareCandidate(
            ActiveTurnCompactionRequest request)
        {
            PrepareCalls++;
            throw new InvalidOperationException("Deterministic projection should avoid summary preparation.");
        }
    }
}
