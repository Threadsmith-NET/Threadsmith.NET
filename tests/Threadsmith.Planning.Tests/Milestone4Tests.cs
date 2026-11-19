namespace Threadsmith.Planning.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies Milestone 4 ordinary conversation and governed context behavior.</summary>
public static class Milestone4Tests
{
    /// <summary>An ordinary message remains a conversational turn even when governed context is configured.</summary>
    [Fact]
    public static async Task SessionApplication_OrdinaryMessage_CompletesWithoutPlanning()
    {
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var capture = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var model = new ConversationalModelProvider("Hello! How can I help?");
        var application = new SessionApplication(
            events,
            model,
            new ExecutionBudget(new BudgetDimensions(100000, 100, TimeSpan.MaxValue)),
            sanitizer,
            NullLogger<SessionApplication>.Instance,
            contextAssembler: CreateAssembler(events, evidence),
            evidenceStore: evidence,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var dispatcher = new CommandDispatcher([application]);
        var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("conversation"));
        var runId = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "hello"));

        Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
        Assert.Equal(
            "Hello! How can I help?",
            string.Concat(observed.OfType<ModelOutputObserved>().Select(item => item.Text)));
        Assert.DoesNotContain(
            observed.OfType<RunTransitioned>(),
            transition => transition.Destination == RunPhase.ChangePlanning);
        var request = Assert.Single(model.Requests);
        Assert.False(request.RequiredCapabilities.StructuredOutput);
        Assert.False(request.RequiredCapabilities.ToolCalls);
        Assert.Empty(request.Tools);
        Assert.DoesNotContain("propose_plan", request.Input, StringComparison.Ordinal);
        Assert.Contains(
            SecurityElement.Escape(TestPromptLoader.Instance.Get(PromptFileNames.SystemImplementationValidationWriteable)),
            request.Input,
            StringComparison.Ordinal);
    }

    /// <summary>Conversational streaming stops before retaining or publishing output beyond the host bound.</summary>
    [Fact]
    public static async Task SessionApplication_OversizedConversationalOutput_FailsClosed()
    {
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var capture = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        var application = new SessionApplication(
            events,
            new ConversationalModelProvider("123456789"),
            UnboundedBudget.Instance,
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            limits: ExecutionLimits.Default with { MaxStructuredOutputCharacters = 8 },
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var dispatcher = new CommandDispatcher([application]);
        var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("bounded output"));
        var runId = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "hello"));

        var exception = await Assert.ThrowsAnyAsync<MalformedModelOutputException>(() =>
            dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

        Assert.Contains("maximum retained output size", exception.Message, StringComparison.Ordinal);
        Assert.Empty(observed.OfType<ModelOutputObserved>());
    }

    /// <summary>Reasoning is bounded before publication by the shared retained-output ceiling.</summary>
    [Fact]
    public static async Task SessionApplication_OversizedReasoning_FailsBeforePublication()
    {
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var capture = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        var application = new SessionApplication(
            events,
            new ChunkModelProvider(new ModelChunk { Reasoning = "123456789" }),
            UnboundedBudget.Instance,
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            limits: ExecutionLimits.Default with { MaxStructuredOutputCharacters = 8 },
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var dispatcher = new CommandDispatcher([application]);
        var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("bounded reasoning"));
        var runId = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "hello"));

        _ = await Assert.ThrowsAnyAsync<MalformedModelOutputException>(() =>
            dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

        Assert.Empty(observed.OfType<ModelReasoningObserved>());
    }

    /// <summary>Tool arguments are bounded before JSON parsing or retention in continuation state.</summary>
    [Fact]
    public static async Task SessionApplication_OversizedToolArguments_FailBeforeParsing()
    {
        await using var events = new DomainEventStream();
        var application = new SessionApplication(
            events,
            new ChunkModelProvider(new ModelChunk
            {
                Output = new ToolRequestModelOutput("datetime", new string('{', 9)),
            }),
            UnboundedBudget.Instance,
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            limits: ExecutionLimits.Default with { MaxStructuredOutputCharacters = 8 },
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var dispatcher = new CommandDispatcher([application]);
        var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("bounded tool output"));
        var runId = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "hello"));

        var exception = await Assert.ThrowsAnyAsync<MalformedModelOutputException>(() =>
            dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

        Assert.Contains("maximum retained output size", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Tiny tool requests cannot bypass retained-output safety through allocation count.</summary>
    [Fact]
    public static async Task SessionApplication_ExcessiveToolCallCount_FailsBeforeRetention()
    {
        await using var events = new DomainEventStream();
        var application = new SessionApplication(
            events,
            new RepeatedToolCallModelProvider(257),
            UnboundedBudget.Instance,
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            limits: ExecutionLimits.Default,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var dispatcher = new CommandDispatcher([application]);
        var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("bounded tool count"));
        var runId = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "hello"));

        var exception = await Assert.ThrowsAnyAsync<MalformedModelOutputException>(() =>
            dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

        Assert.Contains("maximum retained tool-call count", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Ordinary conversation remains available after cumulative usage crosses execution limits.</summary>
    [Fact]
    public static async Task SessionApplication_ConversationBudget_IsUnbounded()
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var model = new ConversationalModelProvider("ok", new ModelUsage(6, 4));
        var prototype = new ExecutionBudget(new BudgetDimensions(10, 1, TimeSpan.MaxValue));
        var application = new SessionApplication(
            events,
            model,
            prototype,
            sanitizer,
            NullLogger<SessionApplication>.Instance,
            budgetFactory: static () => UnboundedBudget.Instance,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var dispatcher = new CommandDispatcher([application]);
        var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("scoped budget"));

        var first = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "first"));
        var second = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "second"));

        Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(first)));
        Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(second)));
        Assert.Equal(2, model.Requests.Count);
    }

    /// <summary>An estimated request that cannot fit the remaining execution budget is rejected before dispatch.</summary>
    [Fact]
    public static async Task SessionApplication_RequestEstimateExceedsBudget_DoesNotDispatchProvider()
    {
        await using var events = new DomainEventStream();
        var model = new ConversationalModelProvider("must not be dispatched");
        var application = new SessionApplication(
            events,
            model,
            new ExecutionBudget(new BudgetDimensions(1, 10, TimeSpan.MaxValue)),
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var dispatcher = new CommandDispatcher([application]);
        var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("request admission"));
        var runId = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "inspect the repository"));

        var exception = await Assert.ThrowsAsync<BudgetExceededException>(() =>
            dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

        Assert.Contains("cannot admit a model request estimated to require", exception.Message, StringComparison.Ordinal);
        Assert.Empty(model.Requests);
    }

    /// <summary>An ordinary request whose input fits is still rejected when its enforced output allowance does not.</summary>
    [Fact]
    public static async Task SessionApplication_OutputAllowanceExceedsBudget_DoesNotDispatchProvider()
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var model = new ConversationalModelProvider("must not be dispatched");
        var profile = CreateProfile("output-admission", structuredOutput: true, permitsSensitiveData: true) with
        {
            ContextWindow = 64_000,
            MaximumOutputTokens = 30_000,
            RequestOutputTokenReserve = 30_000,
            Capabilities = new ModelCapabilitySet
            {
                Streaming = true,
                StructuredOutput = true,
                ToolCalls = true,
            },
            IntendedWorkloadClasses = [WorkloadClass.General, WorkloadClass.Planning],
        };
        var resolver = new ModelResolver(
            new ConfiguredModelCatalog([profile]),
            new InMemoryModelPreferenceSnapshotProvider());
        var application = new SessionApplication(
            events,
            model,
            new ExecutionBudget(new BudgetDimensions(20_000, 10, TimeSpan.MaxValue)),
            sanitizer,
            NullLogger<SessionApplication>.Instance,
            contextAssembler: CreateAssembler(events, evidence, modelResolver: resolver),
            evidenceStore: evidence,
            defaultModelProfileId: profile.Id,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var dispatcher = new CommandDispatcher([application]);
        var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("output allowance admission"));
        var runId = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "inspect the repository"));

        var exception = await Assert.ThrowsAsync<BudgetExceededException>(() =>
            dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

        Assert.Contains("30000 output tokens", exception.Message, StringComparison.Ordinal);
        Assert.Empty(model.Requests);
    }

    /// <summary>Malformed provider-boundary output receives a generic correction-attempt event before retrying.</summary>
    [Fact]
    public static async Task SessionApplication_MalformedProviderOutput_PublishesGenericCorrectionEvent()
    {
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var capture = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        var model = new MalformedProviderThenTextModelProvider();
        var application = new SessionApplication(
            events,
            model,
            UnboundedBudget.Instance,
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            limits: ExecutionLimits.Default with { MaxCorrectiveTurns = 2 },
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var dispatcher = new CommandDispatcher([application]);
        var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("provider correction"));
        var runId = await dispatcher.DispatchAsync(
            new SubmitRequestCommand(sessionId, "answer after a provider correction"));

        Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

        Assert.Equal(2, model.Requests.Count);
        var correction = Assert.Single(observed.OfType<ModelCorrectionAttempted>());
        Assert.Equal(ModelCorrectionCategory.ProviderInvocation, correction.Category);
        Assert.Equal(1, correction.AttemptNumber);
        Assert.Equal(2, correction.MaximumAttempts);
        Assert.Contains("malformed invocation", correction.SafeReason, StringComparison.OrdinalIgnoreCase);
        Assert.Single(
            model.Requests[1].Messages,
            message => message.Role == ModelMessageRole.Developer
                && string.Equals(message.SectionId, "active-turn-correction:1", StringComparison.Ordinal));
    }

    /// <summary>Duplicate, stale, and over-budget evidence is omitted with explicit rationale.</summary>
    [Fact]
    public static async Task ContextReduction_PreservesDecisionsAndExplainsOmissions()
    {
        await using var events = new DomainEventStream();
        var evidence = new EvidenceStore(events, new SecretOutputSanitizer());
        var sessionId = SessionId.New();
        var runId = RunId.New();
        await evidence.AddAsync(CreateEvidence(
            sessionId,
            runId,
            EvidenceKind.Decision,
            "must retain this decision",
            relevance: 0));
        await evidence.AddAsync(CreateEvidence(
            sessionId,
            runId,
            EvidenceKind.SourceExcerpt,
            "duplicate",
            relevance: 0.9));
        await evidence.AddAsync(CreateEvidence(
            sessionId,
            runId,
            EvidenceKind.SourceExcerpt,
            "duplicate",
            relevance: 0.8));
        await evidence.AddAsync(CreateEvidence(
            sessionId,
            runId,
            EvidenceKind.SourceExcerpt,
            new string('x', 2_000),
            relevance: 0.7));
        var stale = CreateEvidence(
            sessionId,
            runId,
            EvidenceKind.SemanticFact,
            "stale semantic",
            relevance: 1) with
        {
            InvalidationKeys = ["semantic"],
        };
        await evidence.AddAsync(stale);
        evidence.QueueInvalidation(sessionId, "semantic", "confidence demoted");
        Assert.False(evidence.Snapshot(sessionId).Single(item => item.EvidenceId == stale.EvidenceId).IsStale);
        var baseline = await CreateAssembler(events, new EvidenceStore(events, new SecretOutputSanitizer())).AssembleAsync(
            CreateAssemblyRequest(sessionId, runId));
        var assembler = CreateAssembler(events, evidence, maximumTokens: baseline.Inspection.EstimatedTokens + 400);
        var result = await assembler.AssembleAsync(CreateAssemblyRequest(sessionId, runId));

        Assert.True(evidence.Snapshot(sessionId).Single(item => item.EvidenceId == stale.EvidenceId).IsStale);
        Assert.Contains("must retain this decision", result.ModelInput);
        Assert.Contains(result.Inspection.Reductions, reason => reason.Contains("Duplicate", StringComparison.Ordinal));
        Assert.Contains(result.Inspection.Reductions, reason => reason.Contains("confidence demoted", StringComparison.Ordinal));
        Assert.Contains(result.Inspection.Reductions, reason => reason.Contains("token budget", StringComparison.Ordinal));
    }

    /// <summary>Queued invalidations are applied only at the owning session's turn boundary.</summary>
    [Fact]
    public static async Task EvidenceInvalidation_IsScopedToOwningSession()
    {
        await using var events = new DomainEventStream();
        var evidence = new EvidenceStore(events, new SecretOutputSanitizer());
        var firstSession = SessionId.New();
        var secondSession = SessionId.New();
        var first = CreateEvidence(
            firstSession,
            RunId.New(),
            EvidenceKind.SemanticFact,
            "first semantic fact",
            relevance: 1) with
        {
            InvalidationKeys = ["semantic"],
        };
        var second = CreateEvidence(
            secondSession,
            RunId.New(),
            EvidenceKind.SemanticFact,
            "second semantic fact",
            relevance: 1) with
        {
            InvalidationKeys = ["semantic"],
        };
        await evidence.AddAsync(first);
        await evidence.AddAsync(second);
        evidence.QueueInvalidation(firstSession, "semantic", "first session changed");

        Assert.Equal(1, await evidence.ApplyInvalidationsAsync(firstSession));
        Assert.True(evidence.Snapshot(firstSession).Single().IsStale);
        Assert.False(evidence.Snapshot(secondSession).Single().IsStale);
    }

    /// <summary>Lifecycle events stale only dependent evidence at the next assembly boundary.</summary>
    [Fact]
    public static async Task LifecycleInvalidation_IsDependencySpecificAndBoundaryApplied()
    {
        await using var events = new DomainEventStream();
        var evidence = new EvidenceStore(events, new SecretOutputSanitizer());
        var loader = new PromptAppendLoader(new SecretOutputSanitizer());
        var observer = new ContextLifecycleObserver(evidence, loader);
        var observedSemantic = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedRepository = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe(async (domainEvent, cancellationToken) =>
        {
            await observer.ObserveAsync(domainEvent, cancellationToken);
            if (domainEvent is SemanticConfidenceChanged)
            {
                observedSemantic.TrySetResult();
            }

            if (domainEvent is RepositoryOpened)
            {
                observedRepository.TrySetResult();
            }
        });
        var sessionId = SessionId.New();
        var runId = RunId.New();
        var semantic = CreateEvidence(
            sessionId,
            runId,
            EvidenceKind.SemanticFact,
            "compiler result",
            relevance: 1) with
        {
            InvalidationKeys = ["repository", "semantic"],
        };
        var listing = CreateEvidence(
            sessionId,
            runId,
            EvidenceKind.ToolResult,
            "file listing",
            relevance: 1) with
        {
            InvalidationKeys = ["repository"],
        };
        var otherSession = CreateEvidence(
            SessionId.New(),
            RunId.New(),
            EvidenceKind.ToolResult,
            "other repository",
            relevance: 1) with
        {
            InvalidationKeys = ["repository"],
        };
        await evidence.AddAsync(semantic);
        await evidence.AddAsync(listing);
        await evidence.AddAsync(otherSession);

        await events.PublishAsync(new SemanticConfidenceChanged(
            sessionId,
            DateTimeOffset.UtcNow,
            nameof(SemanticConfidenceLevel.TextOnly)));
        await observedSemantic.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.All(evidence.Snapshot(sessionId), item => Assert.False(item.IsStale));

        var assembler = CreateAssembler(events, evidence);
        _ = await assembler.AssembleAsync(CreateAssemblyRequest(sessionId, runId));
        Assert.True(evidence.Snapshot(sessionId).Single(item => item.EvidenceId == semantic.EvidenceId).IsStale);
        Assert.False(evidence.Snapshot(sessionId).Single(item => item.EvidenceId == listing.EvidenceId).IsStale);

        await events.PublishAsync(new RepositoryOpened(
            sessionId,
            DateTimeOffset.UtcNow,
            Environment.CurrentDirectory));
        await observedRepository.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(evidence.Snapshot(sessionId).Single(item => item.EvidenceId == listing.EvidenceId).IsStale);
        _ = await assembler.AssembleAsync(CreateAssemblyRequest(sessionId, runId));
        Assert.True(evidence.Snapshot(sessionId).Single(item => item.EvidenceId == listing.EvidenceId).IsStale);
        Assert.False(evidence.Snapshot(otherSession.SessionId).Single().IsStale);
    }

    /// <summary>A governed read-only tool result enters the evidence store with attribution.</summary>
    [Fact]
    public static async Task ReadOnlyToolResult_BecomesAttributedEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m4-tool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "sample.txt"), "sample");
            await using var events = new DomainEventStream();
            var projections = new InMemoryProjectionStore();
            await using var projectionSubscription = events.Subscribe(projections.ApplyAsync);
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var budget = new ExecutionBudget(new BudgetDimensions(
                100000,
                100,
                TimeSpan.FromMinutes(1)));
            var registry = new ToolRegistry([new ListFilesTool(TestPromptLoader.Instance)]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                budget,
                timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            var model = new ListFilesThenTextModelProvider("tool response");
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
                CreateAssembler(events, evidence),
                evidence,
                registry,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("tool evidence"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "Inspect then answer"));
            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId), TestContext.Current.CancellationToken));

            var item = Assert.Single(evidence.Snapshot(sessionId));
            Assert.Equal(EvidenceKind.ToolResult, item.Kind);
            Assert.NotNull(item.Provenance.ToolInvocationId);
            Assert.Equal("tool:list_files", item.Provenance.Source);
            Assert.Equal(SemanticConfidenceLevel.None, item.Provenance.SemanticConfidence);
            Assert.Equal(["repository"], item.InvalidationKeys);
            Assert.Contains("sample.txt", item.Content, StringComparison.Ordinal);
            Assert.Equal(2, model.Requests.Count);
            Assert.Equal(0, model.Requests[0].ToolContinuationRound);
            Assert.Equal(1, model.Requests[1].ToolContinuationRound);
            Assert.Contains("sample.txt", model.Requests[1].Input, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>An invalid sibling rejects the whole conversation tool batch before execution.</summary>
    [Fact]
    public static async Task ToolBatchPreflight_InvalidSiblingRejectsWholeBatchBeforeExecution()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m4-batch-correction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var events = new DomainEventStream();
            var observed = new List<IDomainEvent>();
            await using var capture = events.Subscribe((domainEvent, _) =>
            {
                observed.Add(domainEvent);
                return Task.CompletedTask;
            });
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var budget = new ExecutionBudget(new BudgetDimensions(100000, 100, TimeSpan.MaxValue));
            var tool = new CountingReadTool();
            var registry = new ToolRegistry([tool]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                budget,
                timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            var model = new InvalidBatchThenTextModelProvider();
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
                    TrustLevel = RepositoryTrustLevel.UntrustedInspection,
                    RequestedBy = "model",
                }),
                CreateAssembler(events, evidence),
                evidence,
                registry,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("batch correction"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "Inspect with a malformed sibling"));

            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

            Assert.Equal(0, tool.ExecutionCount);
            Assert.Empty(evidence.Snapshot(sessionId));
            Assert.Equal(2, model.Requests.Count);
            var correction = Assert.Single(observed.OfType<ModelCorrectionAttempted>());
            Assert.Equal(ModelCorrectionCategory.ToolBatch, correction.Category);
            Assert.Equal(1, correction.AttemptNumber);
            Assert.Contains("counting_read", correction.SafeReason, StringComparison.Ordinal);
            var correctionRequest = model.Requests[1];
            var correctionMessages = correctionRequest.Messages.ToList();
            ModelMessage[] assistantCalls = [.. correctionRequest.Messages
                .Where(message => message.Role == ModelMessageRole.Assistant && message.ToolName == "counting_read")];
            ModelMessage[] correctionResults = [.. correctionRequest.Messages
                .Where(message => message.Role == ModelMessageRole.Tool && message.ToolName == "counting_read")];
            Assert.Equal(2, assistantCalls.Length);
            Assert.Equal(2, correctionResults.Length);
            Assert.Contains(
                correctionResults,
                message => message.Content.Any(part => part.Content.Contains("Call 1", StringComparison.Ordinal)));
            Assert.All(
                correctionResults,
                message =>
                {
                    Assert.True(message.IsError);
                    Assert.Contains("executed", message.Content[0].Content, StringComparison.Ordinal);
                    var resultIndex = correctionMessages.IndexOf(message);
                    Assert.Equal(ModelMessageRole.Assistant, correctionMessages[resultIndex - 1].Role);
                    Assert.Equal(message.ToolCallId, correctionMessages[resultIndex - 1].ToolCallId);
                });
            Assert.Contains(
                correctionResults,
                message => message.Content[0].Content.Contains("Nothing in the batch was executed", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A never-delivered oversized tool group fails capacity rather than silently truncating its first delivery.</summary>
    [Fact]
    public static async Task NeverDeliveredLargeToolResult_FailsBeforeContinuationDispatch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m4-budget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var budget = new ExecutionBudget(new BudgetDimensions(100000, 100, TimeSpan.MaxValue));
            // Admit current fixed framing while keeping the first tool result larger than capacity.
            const int inputBudget = 3500;
            var registry = new ToolRegistry(
                [new TestDeterministicOutputTool("oversized-result:" + new string('x', inputBudget * 4), maximumOutputBytes: 16384)]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                budget,
                timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            var model = new ToolThenTextModelProvider("{\"sequence\":1}");
            var assembler = CreateAssembler(events, evidence, maximumTokens: inputBudget);
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
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("bounded continuation"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "Inspect a large listing then answer"));

            var exception = await Assert.ThrowsAsync<BudgetExceededException>(() =>
                dispatcher.DispatchAsync(new WaitForRunCommand(runId)));
            Assert.Contains("Tool continuation requires", exception.Message, StringComparison.Ordinal);
            Assert.Single(model.Requests);
            var fullEvidence = Assert.Single(evidence.Snapshot(sessionId));
            Assert.Contains("oversized-result", fullEvidence.Content, StringComparison.Ordinal);
            Assert.Equal(
                ActiveTurnCompactionInspectionStatus.CapacityExceeded,
                assembler.GetInspection(runId)?.ActiveTurnCompaction?.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Duplicate metadata permits later responses but never identical siblings in one response.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(true, false, true)]
    public static async Task DuplicateToolCall_RespectsToolMetadata(bool allowDuplicates, bool duplicateWithinResponse, bool invalidArguments = false)
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m4-dup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "sample.txt"), "sample");
            await using var events = new DomainEventStream();
            var projections = new InMemoryProjectionStore();
            await using var projectionSubscription = events.Subscribe(projections.ApplyAsync);
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var budget = new ExecutionBudget(new BudgetDimensions(100000, 100, TimeSpan.MaxValue));
            var registry = new ToolRegistry([new DuplicatePolicyListTool(allowDuplicates) { InvalidArguments = invalidArguments }]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                budget,
                timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            var model = new DuplicateToolThenTextModelProvider("dedup response", duplicateWithinResponse);
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
                CreateAssembler(events, evidence),
                evidence,
                registry,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("dedup"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "list twice then answer"));
            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId), TestContext.Current.CancellationToken));

            var snapshot = evidence.Snapshot(sessionId);
            Assert.Equal(duplicateWithinResponse || invalidArguments ? 0 : allowDuplicates ? 2 : 1, snapshot.Count(item => item.Kind == EvidenceKind.ToolResult));
            Assert.Equal(invalidArguments ? 1 : 0, snapshot.Count(item => item.Kind == EvidenceKind.Failure));
            Assert.Equal(duplicateWithinResponse ? 2 : 3, model.Requests.Count);
            var finalRequest = model.Requests[^1];
            Assert.Equal(duplicateWithinResponse || !allowDuplicates || invalidArguments, finalRequest.Messages.Any(message => message.Role == ModelMessageRole.Tool
                    && string.Equals(message.ToolName, "list_files", StringComparison.Ordinal)
                    && message.Content.Any(part => part.Content.Contains(
                        duplicateWithinResponse ? "occurs more than once" : "already has 1 accepted invocations",
                        StringComparison.Ordinal))));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>code_explore remains advertised without a workspace so its availability guidance can reach the model.</summary>
    [Fact]
    public static async Task SessionApplication_CodeExploreWithoutWorkspace_ReturnsAvailabilityToModel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m4-code-explore-no-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var budget = new ExecutionBudget(new BudgetDimensions(100000, 100, TimeSpan.MaxValue));
            var service = new UnexpectedCodeExploreService();
            var registry = new ToolRegistry([new CodeExploreTool(service, TestPromptLoader.Instance)]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                budget,
                timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            var model = new CodeExploreNoWorkspaceThenTextModelProvider();
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
                    RequestedBy = "model",
                }),
                CreateAssembler(events, evidence),
                evidence,
                registry,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("code-explore availability"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "inspect repository without workspace"));

            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId)));

            Assert.False(service.WasCalled);
            Assert.Equal(2, model.Requests.Count);
            var advertised = Assert.Single(model.Requests[0].Tools, tool => tool.Name == "code_explore");
            Assert.True(advertised.PreferStrictArguments);
            Assert.Contains(
                model.Requests[1].Messages,
                message => message.Role == ModelMessageRole.Tool
                    && string.Equals(message.ToolName, "code_explore", StringComparison.Ordinal)
                    && message.Content.Any(part => part.Content.Contains(
                        nameof(CodeExploreAvailabilityStatus.NoWorkspaceOpen),
                        StringComparison.Ordinal)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Repository <c>tools:deny</c> configuration withholds the denied tool from the model's advertised tool set so the model never selects a tool the host would reject.</summary>
    [Theory]
    [InlineData(RepositoryTrustLevel.TrustedRead, false)]
    [InlineData(RepositoryTrustLevel.TrustedBuild, true)]
    public static async Task SessionApplication_ToolVisibilityUsesTrustAndDenialsNotSideEffectsOrApproval(RepositoryTrustLevel trust, bool processAvailable)
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m4-deny-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "sample.txt"), "sample");
            await using var events = new DomainEventStream();
            var projections = new InMemoryProjectionStore();
            await using var projectionSubscription = events.Subscribe(projections.ApplyAsync);
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var budget = new ExecutionBudget(new BudgetDimensions(100000, 100, TimeSpan.MaxValue));
            var registry = new ToolRegistry(
            [
                new ListFilesTool(TestPromptLoader.Instance),
                new ReadFileTool(TestPromptLoader.Instance, new Threadsmith.Telemetry.SecretOutputSanitizer()),
                new RunProcessTool(
                    new NonExecutingProcessManager(),
                    TestPromptLoader.Instance,
                    requireApproval: true,
                    shellExecutable: "bash"),
            ]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                budget,
                timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            var model = new CaptureToolsModelProvider("response without denied tool");
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
                    TrustLevel = trust,
                    RequireApprovalToolIds = ["run_process"],
                    DeniedToolIds = ["list_files"],
                    RequestedBy = "model",
                }),
                CreateAssembler(events, evidence),
                evidence,
                registry,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("deny check"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "Inspect then answer"));

            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId), TestContext.Current.CancellationToken));

            var firstRequest = Assert.Single(model.Requests);
            IReadOnlyList<string> advertisedToolNames = [.. firstRequest.Tools.Select(tool => tool.Name)];
            Assert.Contains("read_file", advertisedToolNames);
            Assert.DoesNotContain("list_files", advertisedToolNames);
            Assert.Equal(processAvailable, advertisedToolNames.Contains("run_process"));
            Assert.DoesNotContain(firstRequest.Tools, tool => tool.Name == "memories");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The ordinary conversation keeps tools available beyond the former 16-round window.</summary>
    [Fact]
    public static async Task ConversationRounds_KeepInspectionToolsBeyondFormerSixteenRoundLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-m4-unbounded-planning-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "sample.txt"), "sample");
            await using var events = new DomainEventStream();
            var projections = new InMemoryProjectionStore();
            await using var subscription = events.Subscribe(projections.ApplyAsync);
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var budget = new ExecutionBudget(new BudgetDimensions(100000, 100, TimeSpan.MaxValue));
            var registry = new ToolRegistry([new ListFilesTool(TestPromptLoader.Instance)]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance,
                budget,
                timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            var model = new ToolForManyRoundsThenTextModelProvider(
                "after extended exploration",
                toolRounds: 17);
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
                CreateAssembler(events, evidence),
                evidence,
                registry,
                limits: new ExecutionLimits
                {
                    MaxModelRounds = 20,
                },
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("extended conversation tools"));
            var runId = await dispatcher.DispatchAsync(
                new SubmitRequestCommand(sessionId, "Inspect more than sixteen things, then answer"));

            Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId), TestContext.Current.CancellationToken));

            Assert.Equal(18, model.Requests.Count);
            Assert.All(
                model.Requests.Take(17),
                request => Assert.Contains(request.Tools, tool => tool.Name == "list_files"));
            var postFormerLimitRequest = model.Requests[16];
            Assert.Contains(postFormerLimitRequest.Tools, tool => tool.Name == "list_files");
            var finalRequest = model.Requests[17];
            Assert.Contains(finalRequest.Tools, tool => tool.Name == "list_files");
            Assert.DoesNotContain(finalRequest.Tools, tool => tool.Name == "propose_plan");
            Assert.Equal(17, evidence.Snapshot(sessionId).Count(item => item.Kind == EvidenceKind.ToolResult));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A process manager that rejects unexpected execution in tool-policy tests.</summary>
    private sealed class NonExecutingProcessManager : IProcessManager
    {
        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("The advertisement test must not execute a process.");
        }
    }

    /// <summary>The host-resolved model profile reaches provider dispatch through the ordinary conversation.</summary>
    [Fact]
    public static async Task ModelResolution_HonoredHintReachesProviderDispatch()
    {
        await using var events = new DomainEventStream();
        var projections = new InMemoryProjectionStore();
        await using var projectionSubscription = events.Subscribe(projections.ApplyAsync);
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var generalBase = CreateProfile("general", structuredOutput: true, permitsSensitiveData: true);
        var general = generalBase with
        {
            Capabilities = generalBase.Capabilities with { ToolCalls = true },
            IntendedWorkloadClasses = [WorkloadClass.General, WorkloadClass.Planning],
        };
        var cheapBase = CreateProfile("cheap", structuredOutput: true, permitsSensitiveData: true);
        var cheap = cheapBase with
        {
            Capabilities = cheapBase.Capabilities with { ToolCalls = true },
            IntendedWorkloadClasses = [WorkloadClass.General, WorkloadClass.Planning],
        };
        var hints = new InMemoryModelPreferenceSnapshotProvider();
        hints.Replace(
        [
            new ModelPreferenceHint
            {
                WorkloadClass = WorkloadClass.General,
                PreferredProfileId = cheap.Id,
                Source = "extension:conversation",
                Priority = 10,
            },
        ]);
        var resolver = new ModelResolver(new ConfiguredModelCatalog([general, cheap]), hints);
        var assembler = CreateAssembler(events, evidence, modelResolver: resolver);
        var model = new QueueModelProvider(["resolved"]);
        var application = new SessionApplication(
            events,
            model,
            new ExecutionBudget(new BudgetDimensions(100000, 100, TimeSpan.MaxValue)),
            sanitizer,
            NullLogger<SessionApplication>.Instance,
            contextAssembler: assembler,
            evidenceStore: evidence,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var dispatcher = new CommandDispatcher([application]);
        var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("resolution"));
        var runId = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "Plan"));
        Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId), TestContext.Current.CancellationToken));

        var dispatched = Assert.Single(model.Requests);
        Assert.Equal(cheap.Id, dispatched.ResolvedProfileId);
        Assert.Contains(
            assembler.GetInspection(runId)?.ModelRationale ?? [],
            item => item.Contains("Applied hint extension:conversation", StringComparison.Ordinal));
    }

    /// <summary>Assembly sanitizes task free text and freezes inspection token categories.</summary>
    [Fact]
    public static async Task ContextAssembly_SanitizesTaskFieldsAndFreezesInspectionData()
    {
        await using var events = new DomainEventStream();
        var sanitizer = new ReplacingSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var assembler = CreateAssembler(events, evidence, sanitizer: sanitizer);
        var result = await assembler.AssembleAsync(new ContextAssemblyRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            Phase = RunPhase.EvidenceCollection,
            Task = new TaskSpecification(
                "Intent SECRET",
                [new AcceptanceCriterion("Criterion SECRET")],
                ["Constraint SECRET"]),
            RepositoryPath = Environment.CurrentDirectory,
        });

        Assert.DoesNotContain("SECRET", result.ModelInput, StringComparison.Ordinal);
        var mutableView = Assert.IsAssignableFrom<IDictionary<string, int>>(
            result.Inspection.TokensByCategory);
        Assert.Throws<NotSupportedException>(() => mutableView.Add("tamper", 1));
        Assert.False(assembler.GetInspection(result.Inspection.RunId)?.TokensByCategory
            .ContainsKey("tamper"));
    }

    private static ContextAssemblyRequest CreateAssemblyRequest(SessionId sessionId, RunId runId)
    {
        return new()
        {
            SessionId = sessionId,
            RunId = runId,
            Phase = RunPhase.EvidenceCollection,
            Task = new TaskSpecification("Inspect source", []),
            RepositoryPath = Environment.CurrentDirectory,
        };
    }

    private static ContextAssembler CreateAssembler(
        IDomainEventStream events,
        IEvidenceStore evidence,
        int maximumTokens = 32000,
        IModelResolver? modelResolver = null,
        IOutputSanitizer? sanitizer = null)
    {
        return new(
            evidence,
            new TokenEstimator(),
            new ContextPolicy(),
            new PromptAppendLoader(sanitizer ?? new SecretOutputSanitizer()),
            sanitizer ?? new SecretOutputSanitizer(),
            events,
            TestPromptLoader.Instance,
            new ContextAssemblerOptions { MaximumTokens = maximumTokens },
            modelResolver);
    }

    private static Evidence CreateEvidence(
        SessionId sessionId,
        RunId runId,
        EvidenceKind kind,
        string content,
        double relevance)
    {
        return new()
        {
            EvidenceId = EvidenceId.New(),
            SessionId = sessionId,
            RunId = runId,
            Kind = kind,
            Content = content,
            Provenance = new EvidenceProvenance
            {
                Source = "test",
                SemanticConfidence = SemanticConfidenceLevel.FullSemantic,
            },
            CollectedAt = DateTimeOffset.UtcNow,
            Relevance = relevance,
            EstimatedTokens = Math.Max(1, (content.Length + 3) / 4),
        };
    }

    private static async Task CreateDirectoryLinkAsync(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        process.StartInfo.ArgumentList.Add("/c");
        process.StartInfo.ArgumentList.Add("mklink");
        process.StartInfo.ArgumentList.Add("/J");
        process.StartInfo.ArgumentList.Add(linkPath);
        process.StartInfo.ArgumentList.Add(targetPath);
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start the Windows junction command.");
        }

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to create the Windows test junction. {error}{output}".Trim());
        }
    }

    private static ModelProfile CreateProfile(
        string name,
        bool structuredOutput,
        bool permitsSensitiveData)
    {
        return new()
        {
            Id = ModelProfileId.New(),
            Name = name,
            Provider = "openai-compatible",
            Endpoint = new Uri($"https://{name}.example.test/v1/chat/completions"),
            ModelId = name,
            ContextWindow = 32000,
            MaximumOutputTokens = 4000,
            Capabilities = new ModelCapabilitySet
            {
                Streaming = true,
                StructuredOutput = structuredOutput,
            },
            SensitiveDataPolicy = permitsSensitiveData
                ? ModelSensitiveDataPolicy.Allowed
                : ModelSensitiveDataPolicy.Prohibited,
            IntendedWorkloadClasses = [WorkloadClass.Planning],
        };
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

    private sealed class ReplacingSanitizer : IOutputSanitizer
    {
        private readonly SecretOutputSanitizer _inner = new();

        public string Sanitize(string value)
        {
            return _inner.Sanitize(
                value.Replace("SECRET", "[REDACTED]", StringComparison.Ordinal));
        }
    }

    private sealed class RepeatedToolCallModelProvider : IModelProvider
    {
        private readonly int _count;

        public RepeatedToolCallModelProvider(int count)
        {
            _count = count;
        }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            for (var index = 0; index < _count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput("datetime", "{}"),
                };
            }
        }
    }

    private sealed class ChunkSequenceModelProvider : IModelProvider
    {
        private readonly ModelChunk[] _chunks;

        public ChunkSequenceModelProvider(IEnumerable<ModelChunk> chunks)
        {
            ArgumentNullException.ThrowIfNull(chunks);
            _chunks = [.. chunks];
        }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            foreach (var chunk in _chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return chunk;
            }
        }
    }

    private sealed class ChunkModelProvider : IModelProvider
    {
        private readonly ModelChunk _chunk;

        public ChunkModelProvider(ModelChunk chunk)
        {
            _chunk = chunk;
        }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return _chunk;
        }
    }

    private sealed class ConversationalModelProvider : IModelProvider
    {
        private readonly string _response;
        private readonly ModelUsage? _usage;

        public ConversationalModelProvider(string response, ModelUsage? usage = null)
        {
            _response = response;
            _usage = usage;
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ModelChunk { Text = _response };
            if (_usage is not null)
            {
                yield return new ModelChunk { Usage = _usage };
            }
        }
    }

    private sealed class MalformedProviderThenTextModelProvider : IModelProvider
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
                throw new MalformedInvocationException(new MalformedInvocationDiagnostic
                {
                    Kind = MalformedInvocationFailureKind.InvalidJsonArguments,
                    SafeMessage = "The provider returned a malformed invocation.",
                    ProviderFamily = "test",
                    ToolCallCount = 1,
                });
            }

            yield return new ModelChunk { Text = "Corrected after provider retry." };
        }
    }

    private static ModelResolver CreateReplayResolver()
    {
        var profile = CreateProfile("replay", structuredOutput: true, permitsSensitiveData: true) with
        {
            Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true, StructuredOutput = true },
            IntendedWorkloadClasses = [],
        };
        return new ModelResolver(new ConfiguredModelCatalog([profile]), new InMemoryModelPreferenceSnapshotProvider());
    }

    private sealed class QueueModelProvider : IModelProvider
    {
        private readonly Queue<string> _responses;

        public QueueModelProvider(
            IEnumerable<string> responses)
        {
            _responses = new Queue<string>(responses);
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (!_responses.TryDequeue(out var response))
            {
                throw new InvalidOperationException("No scripted response remains.");
            }

            yield return new ModelChunk { Text = response, FinishReason = ModelFinishReason.Stop };
        }
    }

    private sealed class CodeExploreNoWorkspaceThenTextModelProvider : IModelProvider
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
                var advertised = Assert.Single(request.Tools, tool => tool.Name == "code_explore");
                Assert.True(advertised.PreferStrictArguments);
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput(
                        "code_explore",
                        "{\"query\":\"ContextCompaction\"}"),
                    FinishReason = ModelFinishReason.ToolCalls,
                };
                yield break;
            }

            Assert.Contains(
                request.Messages,
                message => message.Role == ModelMessageRole.Tool
                    && string.Equals(message.ToolName, "code_explore", StringComparison.Ordinal)
                    && message.Content.Any(part => part.Content.Contains(
                        nameof(CodeExploreAvailabilityStatus.NoWorkspaceOpen),
                        StringComparison.Ordinal)));
            yield return new ModelChunk { Text = "No-workspace availability was visible." };
        }
    }

    private sealed class UnexpectedCodeExploreService : ICodeExploreService
    {
        public bool WasCalled { get; private set; }

        public Task<CodeExploreResult> QueryCodeExploreAsync(
            WorkspaceId workspaceId,
            CodeExploreRequest request,
            ICodeExploreSourceReader sourceReader,
            ModelVisibleSourceFrontier? visibleSourceFrontier = null,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("The no-workspace branch should not call the semantic service.");
        }
    }

    private sealed class SearchThenSemanticThenTextModelProvider : IModelProvider
    {
        private readonly string _response;
        private readonly string? _filePath;
        private readonly bool _fileScoped;

        public SearchThenSemanticThenTextModelProvider(string response, string? filePath, bool fileScoped)
        {
            _response = response;
            _filePath = filePath;
            _fileScoped = fileScoped;
        }

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
                Assert.Contains(request.Tools, tool => tool.Name == "search");
                Assert.Contains(request.Tools, tool => tool.Name == "find_symbol");
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput(
                        "search",
                        _filePath is not null
                            ? JsonSerializer.Serialize(new { query = "SectorEntityStandardizer", path = _filePath, glob = _fileScoped ? "*" : "*.cs" })
                            : "{\"query\":\"SectorEntityStandardizer\",\"glob\":\"*.cs\"}"),
                    FinishReason = ModelFinishReason.ToolCalls,
                };
                yield break;
            }

            if (Requests.Count == 2 && !_fileScoped)
            {
                Assert.Contains(
                    request.Messages,
                    message => message.Role == ModelMessageRole.Tool
                        && string.Equals(message.ToolName, "search", StringComparison.Ordinal)
                        && message.Content.Any(part => part.Content.Contains(
                            "Call find_symbol",
                            StringComparison.Ordinal)));
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput(
                        "find_symbol",
                        "{\"query\":\"SectorEntityStandardizer\"}"),
                    FinishReason = ModelFinishReason.ToolCalls,
                };
                yield break;
            }

            yield return new ModelChunk { Text = _response, FinishReason = ModelFinishReason.Stop };
        }
    }

    private sealed class FixedSemanticResolver : ISemanticEngineResolver
    {
        private readonly WorkspaceId _workspaceId;

        public FixedSemanticResolver(WorkspaceId workspaceId)
        {
            _workspaceId = workspaceId;
        }

        public bool FindSymbolsCalled { get; private set; }

        public SemanticConfidenceLevel GetConfidence(WorkspaceId workspaceId)
        {
            return workspaceId == _workspaceId
                ? SemanticConfidenceLevel.FullSemantic
                : SemanticConfidenceLevel.None;
        }

        public Task<IReadOnlyList<SymbolResult>> FindSymbolsAsync(
            WorkspaceId workspaceId,
            string query,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(_workspaceId, workspaceId);
            Assert.Equal("SectorEntityStandardizer", query);
            FindSymbolsCalled = true;
            IReadOnlyList<SymbolResult> results =
            [
                new SymbolResult(
                    new SemanticSymbolIdentity("symbol:sector", "SectorEntityStandardizer", "class"),
                    new SemanticSourceLocation(
                        "TestProject",
                        "net10.0",
                        "SectorEntityStandardizer.cs",
                        new SourceRange(1, 14, 1, 38),
                        IsGenerated: false,
                        IsLinked: false),
                    SemanticConfidenceLevel.FullSemantic),
            ];
            return Task.FromResult(results);
        }

        public Task<IReadOnlyList<ReferenceResult>> FindReferencesAsync(
            WorkspaceId workspaceId,
            string symbolId,
            bool allowTextFallback = false,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("This test only uses find_symbol.");
        }

        public Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(
            WorkspaceId workspaceId,
            IReadOnlyList<string> projectPaths,
            IReadOnlyList<string> changedFiles,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Diagnostic>>([]);
        }

        public Task<IReadOnlyList<ImplementationResult>> FindImplementationsAsync(
            WorkspaceId workspaceId,
            string symbolId,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("This test only uses find_symbol.");
        }
    }

    private sealed class ToolForManyRoundsThenTextModelProvider : IModelProvider
    {
        private readonly string _response;
        private readonly int _toolRounds;

        public ToolForManyRoundsThenTextModelProvider(string response, int toolRounds)
        {
            _response = response;
            _toolRounds = toolRounds;
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Requests.Count <= _toolRounds)
            {
                Assert.Contains(request.Tools, tool => tool.Name == "list_files");
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput(
                        "list_files",
                        $"{{\"path\":\".\",\"maximumEntries\":{Requests.Count}}}"),
                    FinishReason = ModelFinishReason.ToolCalls,
                };
                yield break;
            }

            yield return new ModelChunk { Text = _response, FinishReason = ModelFinishReason.Stop };
        }
    }

    private sealed class DuplicatePolicyListTool : Tool<ListFilesInput, ListFilesOutput>
    {
        private readonly ListFilesTool _inner = new(TestPromptLoader.Instance);

        public DuplicatePolicyListTool(bool allowDuplicates)
        {
            Definition = _inner.Definition with { NumberOfExactToolInvocationsPerTurn = allowDuplicates ? int.MaxValue : 0 };
        }

        public override ToolDefinition Definition { get; }

        public bool InvalidArguments { get; init; }

        public override Task<ToolExecution<ListFilesOutput>> ExecuteAsync(ListFilesInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            if (InvalidArguments)
            {
                throw new ToolArgumentValidationException("Invalid fixture arguments.");
            }

            return _inner.ExecuteAsync(input, context, cancellationToken);
        }

        protected override void ValidateInput(ListFilesInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
        }
    }

    private sealed class DuplicateToolThenTextModelProvider : IModelProvider
    {
        private readonly string _response;
        private readonly bool _duplicateWithinResponse;

        public DuplicateToolThenTextModelProvider(string response, bool duplicateWithinResponse = false)
        {
            _response = response;
            _duplicateWithinResponse = duplicateWithinResponse;
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if ((_duplicateWithinResponse && Requests.Count == 1) || (!_duplicateWithinResponse && Requests.Count <= 2))
            {
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput(
                        "list_files",
                        "{\"path\":\".\",\"maximumEntries\":10}"),
                    FinishReason = ModelFinishReason.ToolCalls,
                };
                if (_duplicateWithinResponse)
                {
                    yield return new ModelChunk
                    {
                        Output = new ToolRequestModelOutput(
                            "list_files",
                            "{\"path\":\".\",\"maximumEntries\":10}"),
                        FinishReason = ModelFinishReason.ToolCalls,
                    };
                }

                yield break;
            }

            yield return new ModelChunk { Text = _response, FinishReason = ModelFinishReason.Stop };
        }
    }

    private sealed class ToolThenTextModelProvider : IModelProvider
    {
        private readonly string _argumentsJson;

        public ToolThenTextModelProvider(string argumentsJson)
        {
            _argumentsJson = argumentsJson;
        }

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
                    Output = new ToolRequestModelOutput("deterministic_output", _argumentsJson),
                    FinishReason = ModelFinishReason.ToolCalls,
                };
                yield break;
            }

            yield return new ModelChunk { Text = "Inspection complete." };
        }
    }

    private sealed class CaptureToolsModelProvider : IModelProvider
    {
        private readonly string _response;

        public CaptureToolsModelProvider(string response)
        {
            _response = response;
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ModelChunk { Text = _response, FinishReason = ModelFinishReason.Stop };
        }
    }

    private sealed class ListFilesThenTextModelProvider : IModelProvider
    {
        private readonly string _argumentsJson;
        private readonly string _response;

        public ListFilesThenTextModelProvider(
            string response,
            string argumentsJson = "{\"path\":\".\",\"maximumEntries\":10}")
        {
            _response = response;
            _argumentsJson = argumentsJson;
        }

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
                        "list_files",
                        _argumentsJson),
                    FinishReason = ModelFinishReason.ToolCalls,
                };
                yield break;
            }

            yield return new ModelChunk { Text = _response, FinishReason = ModelFinishReason.Stop };
        }
    }

    private sealed class InvalidBatchThenTextModelProvider : IModelProvider
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
                        "counting_read",
                        "{\"path\":123}"),
                    FinishReason = ModelFinishReason.ToolCalls,
                };
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput(
                        "counting_read",
                        "{\"path\":\".\"}"),
                    FinishReason = ModelFinishReason.ToolCalls,
                };
                yield break;
            }

            yield return new ModelChunk { Text = "Corrected without tools." };
        }
    }

    private sealed class CountingReadTool : Tool<CountingReadInput, CountingReadOutput>
    {
        private static readonly ToolDefinition _definition = new()
        {
            Id = "counting_read",
            Version = "1.0",
            Description = "Test-only read tool.",
            Category = ToolCategory.FileRead,
            InputSchema = new ToolSchema(
                nameof(CountingReadInput),
                1,
                "{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}},\"required\":[\"path\"],\"additionalProperties\":false}"),
            OutputSchema = new ToolSchema(nameof(CountingReadOutput), 1, "{\"type\":\"object\"}"),
            RequiredTrust = RepositoryTrustLevel.UntrustedInspection,
            SideEffect = ToolSideEffect.ReadOnly,
            Idempotency = ToolIdempotency.Idempotent,
            SupportsCancellation = true,
            Timeout = TimeSpan.FromSeconds(5),
            MaximumOutputBytes = 1024,
        };

        public int ExecutionCount { get; private set; }

        public override ToolDefinition Definition => _definition;

        public override Task<ToolExecution<CountingReadOutput>> ExecuteAsync(
            CountingReadInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecutionCount++;
            return Task.FromResult(new ToolExecution<CountingReadOutput>(
                new CountingReadOutput(input.Path),
                []));
        }

        protected override void ValidateInput(CountingReadInput input)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(input.Path);
        }
    }

    private sealed record CountingReadInput(string Path);

    private sealed record CountingReadOutput(string Path);

    private sealed class RecordingHookCoordinator : IHookCoordinator
    {
        public IReadOnlyList<HookHandlerDescriptor> Handlers => [];

        public List<(HookPoint Point, string? RepositoryIdentity)> Invocations { get; } = [];

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
            RepositoryStorageBinding? storageBinding = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Invocations.Add((point, repositoryIdentity));
            return Task.FromResult(new HookBoundaryDecision(HookDecisionKind.Continue, [], []));
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
                cancellationToken: cancellationToken);
        }

        public bool SetEnabled(HookHandlerId handlerId, bool enabled)
        {
            return false;
        }
    }

    private sealed class ConversationHarness : IAsyncDisposable
    {
        private readonly DomainEventStream _eventStream;
        private readonly IDomainEventSubscription _captureSubscription;
        private readonly IDomainEventSubscription _projectionSubscription;

        private ConversationHarness(
            DomainEventStream eventStream,
            IDomainEventSubscription projectionSubscription,
            IDomainEventSubscription captureSubscription,
            InMemoryProjectionStore projections,
            QueueModelProvider model,
            CommandDispatcher dispatcher,
            SessionId sessionId,
            List<IDomainEvent> events)
        {
            _eventStream = eventStream;
            _projectionSubscription = projectionSubscription;
            _captureSubscription = captureSubscription;
            Projections = projections;
            Model = model;
            Dispatcher = dispatcher;
            SessionId = sessionId;
            Events = events;
        }

        public CommandDispatcher Dispatcher { get; }

        public List<IDomainEvent> Events { get; }

        public QueueModelProvider Model { get; }

        public InMemoryProjectionStore Projections { get; }

        public SessionId SessionId { get; }

        public static async Task<ConversationHarness> CreateAsync(
            IReadOnlyList<string> responses)
        {
            var stream = new DomainEventStream();
            var projections = new InMemoryProjectionStore();
            var observed = new List<IDomainEvent>();
            var projectionSubscription = stream.Subscribe(projections.ApplyAsync);
            var captureSubscription = stream.Subscribe((domainEvent, _) =>
            {
                observed.Add(domainEvent);
                return Task.CompletedTask;
            });
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(stream, sanitizer);
            var assembler = CreateAssembler(stream, evidence);
            var model = new QueueModelProvider(responses);
            var application = new SessionApplication(
                stream,
                model,
                new ExecutionBudget(new BudgetDimensions(
                    100000,
                    100,
                    TimeSpan.FromMinutes(1))),
                sanitizer,
                NullLogger<SessionApplication>.Instance,
                contextAssembler: assembler,
                evidenceStore: evidence,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var sessionId = await dispatcher.DispatchAsync(
                new CreateSessionCommand("M4 tests"));
            return new ConversationHarness(
                stream,
                projectionSubscription,
                captureSubscription,
                projections,
                model,
                dispatcher,
                sessionId,
                observed);
        }

        public async Task<SessionProjection> GetProjectionAsync()
        {
            return await Projections.GetAsync<SessionProjection>(
                        new ProjectionKey("session", SessionId.Value.ToString("D")))
                        ?? throw new InvalidOperationException("Projection is unavailable.");
        }

        public Task<bool> WaitTask(RunId runId)
        {
            return Dispatcher.DispatchAsync(new WaitForRunCommand(runId));
        }

        public async ValueTask DisposeAsync()
        {
            await _captureSubscription.DisposeAsync();
            await _projectionSubscription.DisposeAsync();
            await _eventStream.DisposeAsync();
        }
    }
}
