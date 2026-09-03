namespace Threadsmith.ParallelAgents.Tests;

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

/// <summary>Verifies the Plan 91 transcript-free model, tool, evidence, and correction loop.</summary>
public sealed class ModelExplorerAssignmentRunnerTests
{
    /// <summary>Verifies child tool evidence survives a later observer failure and is admitted on join.</summary>
    [Fact]
    public async Task RunAsync_ToolCallAndCitedFinding_AdmitsEvidenceOnlyOnJoin()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Add(item);
            return Task.CompletedTask;
        });
        await using var failingSubscription = events.Subscribe((item, _) =>
            item is EvidenceAdded
                ? Task.FromException(new InvalidOperationException("evidence observer failed"))
                : Task.CompletedTask);
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var tool = new InspectMetadataTool();
        var registry = new ToolRegistry([tool]);
        var pipeline = new ToolInvocationPipeline(
            registry,
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            sanitizer,
            NullLogger<ToolInvocationPipeline>.Instance,
            UnboundedBudget.Instance);
        var provider = new ToolThenFindingProvider(tool.Definition.Id);
        var usage = new SessionUsageProjection();
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, [tool.Definition.Id]);
        var plan = CreatePlan(assignment);
        var parentContext = CreateParentContext(plan, [tool.Definition.Id]);
        var runner = CreateRunner(
            provider,
            pipeline,
            evidence,
            sanitizer,
            profile,
            parentContext,
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId),
            usage);

        // Act
        var outcome = await runner.RunAsync(plan, assignment);
        Assert.DoesNotContain(evidence.Snapshot(plan.Provenance.SessionId), item =>
            item.RunId == plan.Provenance.ParentRunId);
        Assert.True(await runner.JoinAsync(plan, [outcome], static () => true));

        // Assert
        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Equal(1, outcome.Usage.ToolCalls);
        Assert.True(outcome.Usage.ModelTokens > 70);
        Assert.Equal(profile.Id, outcome.ModelProfileId);
        var finding = Assert.Single(Assert.IsType<AgentFindingSet>(outcome.Findings).Findings);
        var childEvidence = Assert.Single(
            evidence.Snapshot(plan.Provenance.SessionId),
            item => item.RunId == assignment.ChildRunId);
        Assert.Equal([childEvidence.EvidenceId], finding.EvidenceIds);
        Assert.Equal(assignment.ChildRunId, childEvidence.Provenance.ChildRunId);
        Assert.Equal(assignment.AssignmentId, childEvidence.Provenance.AgentAssignmentId);
        Assert.Equal(profile.Id, childEvidence.Provenance.ModelProfileId);
        Assert.Equal(plan.Provenance.BaselineIdentity, childEvidence.Provenance.BaselineIdentity);
        Assert.Single(
            evidence.Snapshot(plan.Provenance.SessionId),
            item => item.RunId == plan.Provenance.ParentRunId);
        Assert.Contains(observed, item => item is ToolInvocationStarted started
            && started.RunId == assignment.ChildRunId
            && started.ToolName == tool.Definition.Id
            && started.RequestedBy.StartsWith("agent:", StringComparison.Ordinal));
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(50, usage.GetSnapshot(plan.Provenance.SessionId).InputTokens);
        Assert.Equal(20, usage.GetSnapshot(plan.Provenance.SessionId).OutputTokens);
        Assert.All(provider.Requests, request =>
        {
            Assert.NotNull(request.WireEstimate);
            Assert.DoesNotContain(request.Tools, definition =>
                definition.Name == DelegateAgentsContract.ToolId);
        });
        Assert.Contains(provider.Requests[0].Messages, message =>
            message.SectionId == "child-assignment"
            && message.GetModelVisibleContent().Contains("bounded child context", StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Requests[0].Messages, message =>
            message.SectionId.Contains("transcript", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(tool.LastInvocationContext);
        Assert.Equal(profile.ContextWindow, tool.LastInvocationContext.ModelContextWindowTokens);
        Assert.Equal(
            profile.EffectiveRequestOutputTokenReserve,
            tool.LastInvocationContext.ModelRequestOutputReserveTokens);
        Assert.Equal<int?>(
            profile.ContextWindow - profile.EffectiveRequestOutputTokenReserve,
            tool.LastInvocationContext.ModelEffectiveInputBudgetTokens);
        Assert.Null(tool.LastInvocationContext.VisibleSourceFrontier);
    }

    /// <summary>Verifies malformed final JSON receives one bounded correction before valid findings.</summary>
    [Fact]
    public async Task RunAsync_MalformedFindingResponse_UsesBoundedCorrection()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, []);
        var plan = CreatePlan(assignment);
        var evidenceId = EvidenceId.New();
        await evidence.AddAsync(new Evidence
        {
            EvidenceId = evidenceId,
            SessionId = plan.Provenance.SessionId,
            RunId = plan.Provenance.ParentRunId,
            Kind = EvidenceKind.SourceExcerpt,
            Content = "Known parent evidence.",
            Provenance = new EvidenceProvenance
            {
                Source = "file",
                SourcePath = "src/Known.cs",
                BaselineIdentity = plan.Provenance.BaselineIdentity,
            },
            CollectedAt = DateTimeOffset.UtcNow,
            Relevance = 1,
            EstimatedTokens = 4,
        });
        var provider = new CorrectionThenFindingProvider(evidenceId);
        var registry = new ToolRegistry([]);
        var pipeline = new ToolInvocationPipeline(
            registry,
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            sanitizer,
            NullLogger<ToolInvocationPipeline>.Instance,
            UnboundedBudget.Instance);
        var runner = CreateRunner(
            provider,
            pipeline,
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, []),
            []);

        // Act
        var outcome = await runner.RunAsync(plan, assignment);
        Assert.DoesNotContain(evidence.Snapshot(plan.Provenance.SessionId), item =>
            item.RunId == plan.Provenance.ParentRunId
            && item.Provenance.Source.StartsWith("agent:", StringComparison.Ordinal));
        Assert.True(await runner.JoinAsync(plan, [outcome], static () => true));

        // Assert
        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Equal(1, outcome.Usage.Corrections);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Contains(provider.Requests[1].Messages, message =>
            message.SectionId == "child-correction"
            && message.GetModelVisibleContent().Contains(
                "prior response was rejected",
                StringComparison.Ordinal));
        Assert.Equal(
            [evidenceId],
            Assert.Single(Assert.IsType<AgentFindingSet>(outcome.Findings).Findings).EvidenceIds);
    }

    /// <summary>Verifies an unadmitted citation is corrected before any parent finding is promoted.</summary>
    [Fact]
    public async Task RunAsync_UnadmittedCitation_UsesCorrectionBeforeAtomicAdmission()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, []);
        var plan = CreatePlan(assignment);
        var admittedId = EvidenceId.New();
        await evidence.AddAsync(CreateParentEvidence(
            plan,
            admittedId,
            "admitted parent evidence",
            EvidenceSensitivity.None));
        var provider = new FindingSequenceProvider(
            CreateFindingJson(EvidenceId.New().Value.ToString("D"), "Unadmitted citation."),
            CreateFindingJson(admittedId.Value.ToString("D"), "Corrected citation."));
        var registry = new ToolRegistry([]);
        var pipeline = CreatePipeline(registry, events, sanitizer);
        var runner = CreateRunner(
            provider,
            pipeline,
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, []),
            []);

        // Act
        var outcome = await runner.RunAsync(plan, assignment);
        Assert.DoesNotContain(evidence.Snapshot(plan.Provenance.SessionId), item =>
            item.RunId == plan.Provenance.ParentRunId
            && item.Provenance.Source.StartsWith("agent:", StringComparison.Ordinal));
        Assert.True(await runner.JoinAsync(plan, [outcome], static () => true));

        // Assert
        Assert.Equal(1, outcome.Usage.Corrections);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Single(evidence.Snapshot(plan.Provenance.SessionId), item =>
            item.RunId == plan.Provenance.ParentRunId
            && item.Provenance.Source.StartsWith("agent:", StringComparison.Ordinal));
    }

    /// <summary>Verifies omitted confidence is schema-invalid instead of silently becoming zero.</summary>
    [Fact]
    public async Task RunAsync_OmittedConfidence_UsesCorrection()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, []);
        var plan = CreatePlan(assignment);
        var evidenceId = EvidenceId.New();
        await evidence.AddAsync(CreateParentEvidence(
            plan,
            evidenceId,
            "parent evidence",
            EvidenceSensitivity.None));
        var provider = new FindingSequenceProvider(
            CreateFindingJsonWithoutConfidence(evidenceId),
            CreateFindingJson(evidenceId.Value.ToString("D"), "Confidence supplied."));
        var registry = new ToolRegistry([]);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, []),
            []);

        // Act
        var outcome = await runner.RunAsync(plan, assignment);

        // Assert
        Assert.Equal(1, outcome.Usage.Corrections);
        Assert.Equal(
            0.9,
            Assert.Single(Assert.IsType<AgentFindingSet>(outcome.Findings).Findings).Confidence);
    }

    /// <summary>Verifies a schema-valid empty finding set completes without a correction retry.</summary>
    [Fact]
    public async Task RunAsync_EmptyFindingSet_CompletesWithoutCorrection()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, []);
        var plan = CreatePlan(assignment);
        var provider = new FindingSequenceProvider(CreateEmptyFindingJson());
        var registry = new ToolRegistry([]);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, []),
            []);

        // Act
        var outcome = await runner.RunAsync(plan, assignment);

        // Assert
        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Empty(Assert.IsType<AgentFindingSet>(outcome.Findings).Findings);
        Assert.Equal(0, outcome.Usage.Corrections);
        Assert.Single(provider.Requests);
    }

    /// <summary>Verifies an unsupported finding category receives one bounded correction.</summary>
    [Fact]
    public async Task RunAsync_UnsupportedCategory_UsesCorrection()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, []);
        var plan = CreatePlan(assignment);
        var evidenceId = EvidenceId.New();
        await evidence.AddAsync(CreateParentEvidence(
            plan,
            evidenceId,
            "parent evidence",
            EvidenceSensitivity.None));
        var provider = new FindingSequenceProvider(
            CreateFindingJson(evidenceId.Value.ToString("D"), "Unsupported category.", "security"),
            CreateFindingJson(evidenceId.Value.ToString("D"), "Supported category."));
        var registry = new ToolRegistry([]);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, []),
            []);

        // Act
        var outcome = await runner.RunAsync(plan, assignment);

        // Assert
        Assert.Equal(1, outcome.Usage.Corrections);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(
            "behavior",
            Assert.Single(Assert.IsType<AgentFindingSet>(outcome.Findings).Findings).Category);
    }

    /// <summary>Verifies missing provider usage falls back to the complete host wire estimate.</summary>
    [Fact]
    public async Task RunAsync_ProviderOmitsUsage_ChargesHostWireEstimate()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, []);
        var plan = CreatePlan(assignment);
        var evidenceId = EvidenceId.New();
        await evidence.AddAsync(CreateParentEvidence(
            plan,
            evidenceId,
            "parent evidence",
            EvidenceSensitivity.None));
        var provider = new NoUsageFindingProvider(
            CreateFindingJson(evidenceId.Value.ToString("D"), "Estimated usage finding."));
        var registry = new ToolRegistry([]);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, []),
            []);

        // Act
        var outcome = await runner.RunAsync(plan, assignment);

        // Assert
        var request = Assert.Single(provider.Requests);
        var estimate = Assert.IsType<ModelWireEstimate>(request.WireEstimate);
        Assert.True(outcome.Usage.ModelTokens >= estimate.WireInputTokens);
    }

    /// <summary>Verifies parent evidence admission occurs only after the durable join checkpoint.</summary>
    [Fact]
    public async Task StartAsync_JoinedCheckpointFails_DoesNotAdmitParentEvidence()
    {
        // Arrange
        await using var events = new DomainEventStream();
        await using var scheduler = CreateScheduler();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, []);
        var plan = CreatePlan(assignment);
        var evidenceId = EvidenceId.New();
        await evidence.AddAsync(CreateParentEvidence(
            plan,
            evidenceId,
            "parent evidence",
            EvidenceSensitivity.None));
        var provider = new FindingSequenceProvider(
            CreateFindingJson(evidenceId.Value.ToString("D"), "Joined finding."));
        var registry = new ToolRegistry([]);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, []),
            []);
        var coordinator = new DelegationCoordinator(
            scheduler,
            new ThrowOnJoinedCheckpointStore(),
            events);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(plan, runner));
        Assert.DoesNotContain(evidence.Snapshot(plan.Provenance.SessionId), item =>
            item.RunId == plan.Provenance.ParentRunId
            && item.Provenance.Source.StartsWith("agent:", StringComparison.Ordinal));
    }

    /// <summary>Verifies streamed tool arguments fail before an unbounded request list can accumulate.</summary>
    [Fact]
    public async Task RunAsync_OversizedToolArguments_FailsBeforeInvocation()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var tool = new InspectMetadataTool();
        var registry = new ToolRegistry([tool]);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, [tool.Definition.Id]);
        var plan = CreatePlan(assignment);
        var runner = CreateRunner(
            new OversizedToolArgumentProvider(tool.Definition.Id),
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, [tool.Definition.Id]),
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId));

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(plan, assignment));
        Assert.Null(tool.LastInvocationContext);
    }

    /// <summary>Verifies cumulative model usage does not impose a synthetic request ceiling.</summary>
    [Fact]
    public async Task RunAsync_UnboundedModelUsage_UsesSelectedModelContext()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, []) with
        {
            Budget = CreateBudget(),
        };
        var plan = CreatePlan(assignment);
        var provider = new FindingSequenceProvider(CreateEmptyFindingJson());
        var registry = new ToolRegistry([]);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, []),
            []);

        // Act
        var outcome = await runner.RunAsync(plan, assignment);

        // Assert
        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Single(provider.Requests);
    }

    /// <summary>Verifies the complete eligible parent evidence set reaches the first request.</summary>
    [Fact]
    public async Task RunAsync_InitialEvidenceIncludesEveryEligibleItem()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, []);
        var plan = CreatePlan(assignment);
        var highPriorityId = EvidenceId.New();
        var lowPriorityId = EvidenceId.New();
        await evidence.AddAsync(CreateParentEvidence(
            plan,
            highPriorityId,
            "HIGH_PRIORITY_EVIDENCE",
            EvidenceSensitivity.None) with
        {
            Relevance = 1,
        });
        var lowPriorityContent = "LOW_PRIORITY_EVIDENCE" + new string('x', 13_000);
        await evidence.AddAsync(CreateParentEvidence(
            plan,
            lowPriorityId,
            lowPriorityContent,
            EvidenceSensitivity.None) with
        {
            Relevance = 0.1,
            EstimatedTokens = TokenEstimator.Estimate(lowPriorityContent),
        });
        var provider = new FindingSequenceProvider(
            CreateFindingJson(highPriorityId.Value.ToString("D"), "Highest-ranked evidence retained."));
        var registry = new ToolRegistry([]);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, []),
            []);

        // Act
        var outcome = await runner.RunAsync(plan, assignment);

        // Assert
        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        var request = Assert.Single(provider.Requests);
        var initialEvidence = Assert.Single(request.Messages, message =>
            message.SectionId == "child-initial-evidence").GetModelVisibleContent();
        Assert.Contains("HIGH_PRIORITY_EVIDENCE", initialEvidence, StringComparison.Ordinal);
        Assert.Contains("LOW_PRIORITY_EVIDENCE", initialEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain("<evidence-omission", initialEvidence, StringComparison.Ordinal);
    }

    /// <summary>Verifies hidden streamed reasoning consumes child output capacity even without provider usage.</summary>
    [Fact]
    public async Task RunAsync_StreamedReasoningExceedsOutputBudget_FailsBoundedly()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, []);
        var plan = CreatePlan(assignment);
        var provider = new ExcessiveReasoningProvider(new string('r', 5_000));
        var registry = new ToolRegistry([]);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, []),
            []);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(plan, assignment));
        Assert.Single(provider.Requests);
    }

    /// <summary>Verifies tool registration uses Explorer capability and sensitivity negotiation.</summary>
    [Fact]
    public void ModelSelector_ExplorerAvailability_UsesCompleteNegotiationContract()
    {
        // Arrange
        var budget = CreateBudget();
        var noToolCalls = CreateProfile() with
        {
            Name = "no-tool-calls",
            Capabilities = new ModelCapabilitySet
            {
                Streaming = true,
                ToolCalls = false,
                StructuredOutput = true,
            },
        };
        var insufficientContext = CreateProfile() with
        {
            Name = "insufficient-context",
            ContextWindow = 8_192,
        };
        var nonSensitiveOnly = CreateProfile() with
        {
            Name = "non-sensitive-only",
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Prohibited,
        };

        // Act / Assert
        Assert.False(CreateSelector(noToolCalls).CanSelectExplorer(
            budget,
            ConversationSensitivity.None));
        Assert.True(CreateSelector(insufficientContext).CanSelectExplorer(
            budget,
            ConversationSensitivity.None));
        Assert.True(CreateSelector(nonSensitiveOnly).CanSelectExplorer(
            budget,
            ConversationSensitivity.None));
        Assert.False(CreateSelector(nonSensitiveOnly).CanSelectExplorer(
            budget,
            ConversationSensitivity.Sensitive));
    }

    /// <summary>Verifies fallback selection is retained in child, tool, and admitted-finding provenance.</summary>
    [Fact]
    public async Task RunAsync_PreferredProfileIsIncompatible_RecordsEffectiveFallbackProfile()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var preferred = CreateProfile() with
        {
            Id = ModelProfileId.New(),
            Name = "incompatible-preference",
            Capabilities = new ModelCapabilitySet
            {
                Streaming = false,
                ToolCalls = true,
                StructuredOutput = true,
            },
        };
        var fallback = CreateProfile() with
        {
            Id = ModelProfileId.New(),
            Name = "compatible-fallback",
        };
        var assignment = CreateAssignment(preferred.Id, []);
        var plan = CreatePlan(assignment);
        var evidenceId = EvidenceId.New();
        await evidence.AddAsync(CreateParentEvidence(
            plan,
            evidenceId,
            "fallback evidence",
            EvidenceSensitivity.None));
        var provider = new FindingSequenceProvider(
            CreateFindingJson(evidenceId.Value.ToString("D"), "Fallback-backed finding."));
        var registry = new ToolRegistry([]);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            [preferred, fallback],
            CreateParentContext(plan, []),
            []);

        // Act
        var outcome = await runner.RunAsync(plan, assignment);

        // Assert
        Assert.Equal(fallback.Id, outcome.ModelProfileId);
        Assert.DoesNotContain(evidence.Snapshot(plan.Provenance.SessionId), item =>
            item.RunId == plan.Provenance.ParentRunId
            && item.Provenance.Source.StartsWith("agent:", StringComparison.Ordinal));
        Assert.True(await runner.JoinAsync(plan, [outcome], static () => true));
        Assert.Single(evidence.Snapshot(plan.Provenance.SessionId), item =>
            item.RunId == plan.Provenance.ParentRunId
            && item.Provenance.Source.StartsWith("agent:", StringComparison.Ordinal)
            && item.Provenance.ModelProfileId == fallback.Id);
        Assert.Equal(fallback.Id, Assert.Single(provider.Requests).ResolvedProfileId);
    }

    /// <summary>Verifies a non-sensitive child cannot inherit sensitive evidence from earlier parent state.</summary>
    [Fact]
    public async Task ContextAssembler_NonSensitiveAssignment_ExcludesSensitiveEvidence()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var evidence = new EvidenceStore(events, new SecretOutputSanitizer());
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, []);
        var plan = CreatePlan(assignment);
        var ordinaryId = EvidenceId.New();
        var sensitiveId = EvidenceId.New();
        await evidence.AddAsync(CreateParentEvidence(
            plan,
            ordinaryId,
            "ordinary evidence",
            EvidenceSensitivity.None));
        await evidence.AddAsync(CreateParentEvidence(
            plan,
            sensitiveId,
            "sensitive evidence",
            EvidenceSensitivity.Sensitive));

        // Act
        var context = new AgentContextAssembler(evidence).Assemble(plan, assignment);

        // Assert
        Assert.Contains(context.Evidence, item => item.EvidenceId == ordinaryId);
        Assert.DoesNotContain(context.Evidence, item => item.EvidenceId == sensitiveId);
    }

    private static ModelExplorerAssignmentRunner CreateRunner(
        IModelProvider provider,
        IToolInvocationPipeline pipeline,
        IEvidenceStore evidence,
        IOutputSanitizer sanitizer,
        ModelProfile profile,
        ToolExecutionContext parentContext,
        IReadOnlyList<ToolRegistration> registrations,
        SessionUsageProjection? usage = null)
    {
        return CreateRunner(
            provider,
            pipeline,
            evidence,
            sanitizer,
            [profile],
            parentContext,
            registrations,
            usage);
    }

    private static ModelExplorerAssignmentRunner CreateRunner(
        IModelProvider provider,
        IToolInvocationPipeline pipeline,
        IEvidenceStore evidence,
        IOutputSanitizer sanitizer,
        IReadOnlyList<ModelProfile> profiles,
        ToolExecutionContext parentContext,
        IReadOnlyList<ToolRegistration> registrations,
        SessionUsageProjection? usage = null)
    {
        var catalog = new ConfiguredModelCatalog(profiles);
        return new ModelExplorerAssignmentRunner(
            new AgentContextAssembler(evidence),
            new AgentFindingAdmission(evidence),
            new AgentModelSelector(catalog, new DefaultModelSelectionPolicy(catalog)),
            provider,
            pipeline,
            evidence,
            new StubInstructionProvider(),
            sanitizer,
            CreateOptions(),
            parentContext,
            registrations,
            TestPromptLoader.Instance,
            usage);
    }

    private static ToolInvocationPipeline CreatePipeline(
        IToolRegistry registry,
        IDomainEventStream events,
        IOutputSanitizer sanitizer)
    {
        return new ToolInvocationPipeline(
            registry,
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            sanitizer,
            NullLogger<ToolInvocationPipeline>.Instance,
            UnboundedBudget.Instance);
    }

    private static AgentModelSelector CreateSelector(ModelProfile profile)
    {
        var catalog = new ConfiguredModelCatalog([profile]);
        return new AgentModelSelector(catalog, new DefaultModelSelectionPolicy(catalog));
    }

    private static DelegateAgentsOptions CreateOptions()
    {
        return new DelegateAgentsOptions
        {
            ChildBudget = CreateBudget(),
        };
    }

    private static AgentRunScheduler CreateScheduler()
    {
        return new AgentRunScheduler(new AgentSchedulerOptions
        {
            QueueCapacity = 2,
            MaximumActiveChildren = 1,
            MaximumActiveChildrenPerParent = 1,
            MaximumActiveImplementers = 1,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });
    }

    private static Evidence CreateParentEvidence(
        DelegationPlan plan,
        EvidenceId evidenceId,
        string content,
        EvidenceSensitivity sensitivity)
    {
        return new Evidence
        {
            EvidenceId = evidenceId,
            SessionId = plan.Provenance.SessionId,
            RunId = plan.Provenance.ParentRunId,
            Kind = EvidenceKind.SourceExcerpt,
            Content = content,
            Provenance = new EvidenceProvenance { Source = "test" },
            CollectedAt = DateTimeOffset.UtcNow,
            Relevance = 1,
            EstimatedTokens = 2,
            Sensitivity = sensitivity,
        };
    }

    private static AgentResourceBudget CreateBudget()
    {
        return AgentResourceBudget.CreateTelemetryOnly(TimeSpan.FromMinutes(1));
    }

    private static ModelProfile CreateProfile()
    {
        return new ModelProfile
        {
            Id = ModelProfileId.New(),
            Name = "plan91-test",
            Provider = "test",
            Endpoint = new Uri("https://example.test/v1/chat"),
            ModelId = "test-model",
            ContextWindow = 16_384,
            MaximumOutputTokens = 4_096,
            RequestOutputTokenReserve = 1_024,
            Capabilities = new ModelCapabilitySet
            {
                Streaming = true,
                ToolCalls = true,
                StructuredOutput = true,
            },
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            IntendedWorkloadClasses = [WorkloadClass.General],
            SupportedReasoningLevels = [ReasoningLevel.None],
        };
    }

    private static AgentAssignment CreateAssignment(
        ModelProfileId profileId,
        IReadOnlyList<string> toolIds)
    {
        return new AgentAssignment
        {
            AssignmentId = AgentAssignmentId.New(),
            ChildRunId = RunId.New(),
            Role = AgentRole.Explorer,
            Mode = AgentRunMode.ReadOnlyBaseline,
            Objective = "Inspect the assigned behavior.",
            Tasks = ["Return one cited finding."],
            InitialContext = "bounded child context",
            OutputSchema = DelegateAgentsContract.FindingSchema,
            StoppingCondition = "Stop after one supported finding.",
            Deadline = DateTimeOffset.UtcNow.AddMinutes(1),
            Scope = new AgentAssignmentScope { IsOwnershipProven = true },
            Policy = new AgentPolicySnapshot
            {
                AllowedToolIds = toolIds,
                DeniedToolIds = [DelegateAgentsContract.ToolId],
                TrustCeiling = RepositoryTrustLevel.TrustedRead,
                ModelProfileId = profileId,
                ReasoningLevel = nameof(ReasoningLevel.None),
                ModelSelectionRationale = "test profile",
                ContextPolicyVersion = "agent-context/2",
                ToolPolicyVersion = "delegate-agents-read-only/1",
            },
            Budget = CreateBudget(),
        };
    }

    private static DelegationPlan CreatePlan(AgentAssignment assignment)
    {
        var acceptedAt = DateTimeOffset.UtcNow;
        assignment = assignment with { Deadline = acceptedAt.AddMinutes(1) };
        return new DelegationPlan
        {
            DelegationId = DelegationId.New(),
            Provenance = new DelegationProvenance
            {
                SessionId = SessionId.New(),
                ParentRunId = RunId.New(),
                RepositoryIdentity = Environment.CurrentDirectory,
                BaselineIdentity = "plan91-baseline",
                WorkspaceId = WorkspaceId.New(),
            },
            Assignments = [assignment],
            ParentBudget = assignment.Budget,
            AcceptedAt = acceptedAt,
        };
    }

    private static ToolExecutionContext CreateParentContext(
        DelegationPlan plan,
        IReadOnlyList<string> allowedToolIds)
    {
        return new ToolExecutionContext(
            ToolInvocationId.New(),
            plan.Provenance.SessionId,
            plan.Provenance.ParentRunId,
            new ToolInvocationContext
            {
                WorkspaceId = plan.Provenance.WorkspaceId,
                RepositoryPath = Environment.CurrentDirectory,
                TrustLevel = RepositoryTrustLevel.TrustedBuild,
                ApprovedRoots = ["."],
                AllowedToolIds = allowedToolIds,
                ModelContextWindowTokens = 999,
                ModelRequestOutputReserveTokens = 998,
                ModelEffectiveInputBudgetTokens = 1,
                VisibleSourceFrontier = new ModelVisibleSourceFrontier(
                    Environment.CurrentDirectory,
                    plan.Provenance.WorkspaceId,
                    1,
                    [],
                    0,
                    0,
                    0),
                RequestedBy = "model:parent",
            })
        {
            Phase = RunPhase.EvidenceCollection,
        };
    }

    private sealed record InspectMetadataInput;

    private sealed class InspectMetadataTool : Tool<InspectMetadataInput, string>
    {
        public ToolInvocationContext? LastInvocationContext { get; private set; }

        public override ToolDefinition Definition { get; } = new()
        {
            Id = "inspect_metadata",
            DisplayName = "Inspect metadata",
            Version = "1.0.0",
            Description = "Returns bounded test metadata.",
            Category = ToolCategory.RepositoryInspection,
            InputSchema = new ToolSchema(
                nameof(InspectMetadataInput),
                1,
                "{\"type\":\"object\",\"additionalProperties\":false}"),
            OutputSchema = new ToolSchema("String", 1, "{\"type\":\"string\"}"),
            RequiredTrust = RepositoryTrustLevel.UntrustedInspection,
            RequiredApproval = ApprovalLevel.None,
            SideEffect = ToolSideEffect.ReadOnly,
            Idempotency = ToolIdempotency.Idempotent,
            SupportsCancellation = true,
            Timeout = TimeSpan.FromSeconds(2),
            MaximumOutputBytes = 4_096,
            ConversationAvailable = true,
        };

        public override Task<ToolExecution<string>> ExecuteAsync(
            InspectMetadataInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastInvocationContext = context.Invocation;
            return Task.FromResult(new ToolExecution<string>(
                "metadata",
                [new ToolProvenanceSource("file", "src/Test.cs")],
                ModelResultContent: "Compiler-backed metadata."));
        }

        protected override void ValidateInput(InspectMetadataInput input)
        {
        }
    }

    private sealed class StubInstructionProvider : IChildAgentInstructionProvider
    {
        public Task<RepositoryInstructionBundle> GetAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            ToolInvocationContext parentContext,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new RepositoryInstructionBundle
            {
                RepositoryRoot = parentContext.RepositoryPath,
                WorkingScope = ".",
                Digest = "test-instructions",
            });
        }
    }

    private sealed class ToolThenFindingProvider(string toolId) : IModelProvider
    {
        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            if (Requests.Count == 1)
            {
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput(toolId, "{}"),
                    Usage = new ModelUsage(20, 5),
                };
                yield break;
            }

            var toolResult = request.Messages.Single(message => message.Role == ModelMessageRole.Tool);
            using var document = JsonDocument.Parse(toolResult.GetModelVisibleContent());
            var evidenceId = document.RootElement.GetProperty("evidenceId").GetString()
                ?? throw new InvalidDataException("The tool result omitted its evidence identity.");
            yield return new ModelChunk
            {
                Output = new TextModelOutput(CreateFindingJson(evidenceId, "Tool-backed finding.")),
                Usage = new ModelUsage(30, 15),
            };
        }
    }

    private sealed class CorrectionThenFindingProvider(EvidenceId evidenceId) : IModelProvider
    {
        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            yield return new ModelChunk
            {
                Output = new TextModelOutput(Requests.Count == 1
                    ? string.Empty
                    : CreateFindingJson(
                        evidenceId.Value.ToString("D"),
                        "Corrected cited finding.")),
                Usage = new ModelUsage(20, 10),
            };
        }
    }

    private sealed class FindingSequenceProvider(params string[] responses) : IModelProvider
    {
        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Requests.Count > responses.Length)
            {
                throw new InvalidOperationException("The provider received an unexpected request.");
            }

            await Task.Yield();
            yield return new ModelChunk
            {
                Output = new TextModelOutput(responses[Requests.Count - 1]),
                Usage = new ModelUsage(20, 10),
            };
        }
    }

    private sealed class NoUsageFindingProvider(string response) : IModelProvider
    {
        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            yield return new ModelChunk
            {
                Output = new TextModelOutput(response),
            };
        }
    }

    private sealed class ExcessiveReasoningProvider(string reasoning) : IModelProvider
    {
        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            yield return new ModelChunk { Reasoning = reasoning };
        }
    }

    private sealed class ThrowOnJoinedCheckpointStore : IDelegationCheckpointStore
    {
        private DelegationCheckpoint? _latest;

        public Task<bool> SaveAsync(
            DelegationCheckpoint checkpoint,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (checkpoint.Phase == DelegationCheckpointPhase.ResearchJoined)
            {
                throw new InvalidOperationException("joined checkpoint failed");
            }

            if (_latest is null || _latest.Revision < checkpoint.Revision)
            {
                _latest = checkpoint;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        public Task<DelegationCheckpoint?> GetAsync(
            DelegationId delegationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_latest?.DelegationId == delegationId ? _latest : null);
        }
    }

    private sealed class OversizedToolArgumentProvider(string toolId) : IModelProvider
    {
        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new ModelChunk
            {
                Output = new ToolRequestModelOutput(toolId, new string('x', (32 * 1024) + 1)),
                Usage = new ModelUsage(20, 10),
            };
        }
    }

    private static string CreateFindingJson(
        string evidenceId,
        string summary,
        string category = "behavior")
    {
        return JsonSerializer.Serialize(new
        {
            summary,
            findings = new[]
            {
                new
                {
                    category,
                    summary,
                    evidenceIds = new[] { evidenceId },
                    locations = new[] { "src/Test.cs" },
                    symbols = new[] { "Test.Symbol" },
                    confidence = 0.9,
                    uncertainty = (string?)null,
                    risk = (string?)null,
                    recommendation = (string?)null,
                },
            },
            unresolvedQuestions = Array.Empty<string>(),
            coverageNotes = new[] { "Focused test evidence only." },
        });
    }

    private static string CreateEmptyFindingJson()
    {
        return JsonSerializer.Serialize(new
        {
            summary = "No supported findings were identified.",
            findings = Array.Empty<object>(),
            unresolvedQuestions = Array.Empty<string>(),
            coverageNotes = new[] { "The assigned area was inspected." },
        });
    }

    private static string CreateFindingJsonWithoutConfidence(EvidenceId evidenceId)
    {
        return JsonSerializer.Serialize(new
        {
            summary = "Confidence omitted.",
            findings = new[]
            {
                new
                {
                    category = "behavior",
                    summary = "Confidence omitted.",
                    evidenceIds = new[] { evidenceId.Value.ToString("D") },
                    locations = new[] { "src/Test.cs" },
                    symbols = new[] { "Test.Symbol" },
                    uncertainty = (string?)null,
                    risk = (string?)null,
                    recommendation = (string?)null,
                },
            },
            unresolvedQuestions = Array.Empty<string>(),
            coverageNotes = Array.Empty<string>(),
        });
    }
}
