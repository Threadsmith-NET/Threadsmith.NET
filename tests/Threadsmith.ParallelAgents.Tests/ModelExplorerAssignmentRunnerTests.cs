namespace Threadsmith.ParallelAgents.Tests;

using System.Collections.Concurrent;
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

/// <summary>Verifies transcript-free responses, tool authority, evidence metadata, and usage.</summary>
public sealed partial class ModelExplorerAssignmentRunnerTests
{
    /// <summary>Child tool evidence survives observer failure without promoting answer claims on join.</summary>
    [Fact]
    public async Task RunAsync_ToolCallAndResponse_KeepsEvidenceMetadataWithoutPromotingClaims()
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
        Assert.Null(outcome.Findings);
        Assert.Null(outcome.Review);
        Assert.Null(outcome.Implementation);
        var childEvidence = Assert.Single(
            evidence.Snapshot(plan.Provenance.SessionId),
            item => item.RunId == assignment.ChildRunId);
        Assert.Equal([childEvidence.EvidenceId], outcome.DeliveredEvidenceIds);
        Assert.Equal(CreateFindingJson(childEvidence.EvidenceId.Value.ToString("D"), "Tool-backed finding."), outcome.Response);
        Assert.Equal(assignment.ChildRunId, childEvidence.Provenance.ChildRunId);
        Assert.Equal(assignment.AssignmentId, childEvidence.Provenance.AgentAssignmentId);
        Assert.Equal(profile.Id, childEvidence.Provenance.ModelProfileId);
        Assert.Equal(plan.Provenance.BaselineIdentity, childEvidence.Provenance.BaselineIdentity);
        Assert.DoesNotContain(
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
            Assert.False(request.RequiredCapabilities.StructuredOutput);
            Assert.True(request.RequiredCapabilities.ToolCalls);
            Assert.DoesNotContain(request.Tools, definition =>
                definition.Name == DelegateAgentsContract.ToolId);
        });
        Assert.Contains(provider.Requests[0].Messages, message =>
            message.SectionId == "child-assignment"
            && message.GetModelVisibleContent().Contains("bounded child context", StringComparison.Ordinal));
        Assert.DoesNotContain(provider.Requests[0].Messages, message =>
            message.SectionId.Contains("transcript", StringComparison.OrdinalIgnoreCase));
        var toolResult = Assert.Single(
            provider.Requests[1].Messages,
            message => message.SectionId == "child-tool-result");
        using var toolResultDocument = JsonDocument.Parse(toolResult.GetModelVisibleContent());
        var toolResultContent = toolResultDocument.RootElement.GetProperty("content");
        Assert.Equal(JsonValueKind.String, toolResultContent.ValueKind);
        Assert.Equal("Compiler-backed metadata.", toolResultContent.GetString());
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

    /// <summary>Private replay envelopes bind an otherwise identical child tool request by ordinal and retain its error metadata boundary.</summary>
    [Fact]
    public async Task RunAsync_ReplayEnvelope_UsesOrdinalBoundTaggedToolHistory()
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
        var provider = new ToolThenFindingProvider(tool.Definition.Id, emitReplayEnvelope: true);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, [tool.Definition.Id]),
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId));

        // Act
        var outcome = await runner.RunAsync(plan, assignment);

        // Assert
        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Equal(2, provider.Requests.Count);
        var continuation = provider.Requests[1];
        var call = Assert.Single(continuation.Messages, message => message.SectionId == "child-tool-call");
        var result = Assert.Single(continuation.Messages, message => message.SectionId == "child-tool-result");
        Assert.Equal(0, call.ModelRound);
        Assert.Equal(call.ModelRound, result.ModelRound);
        Assert.False(result.IsError);
        Assert.Equal(call.ToolCallId, result.ToolCallId);
        Assert.True(continuation.IncludeReasoningText);
        Assert.DoesNotContain(
            continuation.Messages.SkipWhile(message => message.Role is ModelMessageRole.System or ModelMessageRole.Developer),
            message => message.Role is ModelMessageRole.System or ModelMessageRole.Developer);
    }

    /// <summary>Structured child tool output is embedded as JSON instead of an escaped JSON string.</summary>
    [Fact]
    public async Task RunAsync_JsonToolResult_EmbedsStructuredContentWithoutNestedSerialization()
    {
        // Arrange
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var tool = new StructuredMetadataTool();
        var registry = new ToolRegistry([tool]);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, [tool.Definition.Id]);
        var plan = CreatePlan(assignment);
        var provider = new ToolThenFindingProvider(tool.Definition.Id, retrieveEvidence: true);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, [tool.Definition.Id]),
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId));

        // Act
        var outcome = await runner.RunAsync(plan, assignment);

        // Assert
        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Equal(3, provider.Requests.Count);
        var continuation = provider.Requests[1];
        var toolResult = Assert.Single(
            continuation.Messages,
            message => message.SectionId == "child-tool-result");
        using var document = JsonDocument.Parse(toolResult.GetModelVisibleContent());
        var projectedContent = document.RootElement.GetProperty("content");
        Assert.Equal(JsonValueKind.Object, projectedContent.ValueKind);
        Assert.Equal("src/Test.cs", projectedContent.GetProperty("Path").GetString());
        Assert.Equal(["first", "second"], projectedContent.GetProperty("Lines").EnumerateArray().Select(item => item.GetString()));
        Assert.DoesNotContain("fixture-secret", projectedContent.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", projectedContent.GetProperty("Source").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\\\"Path\\\"", toolResult.GetModelVisibleContent(), StringComparison.Ordinal);
        var storedEvidence = Assert.Single(evidence.Snapshot(plan.Provenance.SessionId));
        using var stored = JsonDocument.Parse(storedEvidence.Content);
        Assert.True(JsonElement.DeepEquals(projectedContent, stored.RootElement));
        var retrieved = Assert.Single(provider.Requests[2].Messages, message =>
            message.Role == ModelMessageRole.Tool && message.ToolName == ChildAgentEvidenceTool.ToolId);
        Assert.Equal(storedEvidence.Content, retrieved.GetModelVisibleContent());
    }

    /// <summary>JSON-aware evidence and model-content sanitization must retain credential-field redaction.</summary>
    [Theory]
    [InlineData("123456", "password")]
    [InlineData("{\"value\":\"fixture-secret\"}", "password")]
    [InlineData("[\"fixture-secret\"]", "password")]
    [InlineData("123456", "database_password")]
    [InlineData("\"fixture-secret\"", "database_password")]
    public async Task JsonSanitization_CredentialProperties_AreRedactedAtBothBoundaries(string credentialJson, string propertyName)
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var content = "{" + JsonSerializer.Serialize(propertyName) + ":" + credentialJson + ",\"safe\":42}";
        var tool = new InspectMetadataTool(content);
        var registry = new ToolRegistry([tool]);
        var plan = CreatePlan(CreateAssignment(ModelProfileId.New(), [tool.Definition.Id]));
        var pipeline = CreatePipeline(registry, events, sanitizer);

        await evidence.AddAsync(CreateParentEvidence(plan, EvidenceId.New(), content, EvidenceSensitivity.None));
        var result = await pipeline.InvokeAsync(new ToolInvocationRequest
        {
            SessionId = plan.Provenance.SessionId,
            RunId = plan.Provenance.ParentRunId,
            ToolId = tool.Definition.Id,
            ArgumentsJson = "{}",
            Context = CreateParentContext(plan, [tool.Definition.Id]).Invocation,
        });

        Assert.True(result.Succeeded, result.Error);
        foreach (var sanitized in new[] { Assert.Single(evidence.Snapshot(plan.Provenance.SessionId)).Content, result.ModelResultContent })
        {
            using var document = JsonDocument.Parse(Assert.IsType<string>(sanitized));
            Assert.Equal("[REDACTED]", document.RootElement.GetProperty(propertyName).GetString());
            Assert.Equal(42, document.RootElement.GetProperty("safe").GetInt32());
        }
    }

    /// <summary>Ordinary text, empty text, and malformed JSON return verbatim without format correction.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("The loop checks cancellation before each item.")]
    [InlineData("{not valid JSON")]
    [InlineData("  Notes:\n- First observation.\n- Second observation.\n")]
    public async Task RunAsync_ArbitraryFinalResponse_ReturnsVerbatimWithoutCorrection(string response)
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
        var provider = new FindingSequenceProvider(response);
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
        Assert.Equal(response, outcome.Response);
        Assert.Equal(0, outcome.Usage.Corrections);
        var request = Assert.Single(provider.Requests);
        Assert.DoesNotContain(request.Messages, message => message.SectionId == "child-tool-error");
        Assert.False(request.RequiredCapabilities.StructuredOutput);
        Assert.Equal([evidenceId], outcome.DeliveredEvidenceIds);
        Assert.Null(outcome.Findings);
        Assert.Null(outcome.Review);
        Assert.Null(outcome.Implementation);
        Assert.True(DelegationOutcomeClassifier.HasUsableResult(plan, outcome));
        Assert.DoesNotContain(evidence.Snapshot(plan.Provenance.SessionId), item =>
            item.Provenance.Source.StartsWith("agent:", StringComparison.Ordinal));
    }

    /// <summary>Unverified citation text neither triggers format repair nor becomes admitted evidence.</summary>
    [Fact]
    public async Task RunAsync_UnadmittedCitation_RemainsUnverifiedResponseText()
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
        var unadmittedId = EvidenceId.New();
        var response = CreateFindingJson(unadmittedId.Value.ToString("D"), "Unadmitted citation.");
        var provider = new FindingSequenceProvider(response);
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
        Assert.Equal(response, outcome.Response);
        Assert.Null(outcome.Findings);
        Assert.Null(outcome.Review);
        Assert.Null(outcome.Implementation);
        Assert.Equal([admittedId], outcome.DeliveredEvidenceIds);
        Assert.DoesNotContain(unadmittedId, outcome.DeliveredEvidenceIds);
        Assert.Equal(0, outcome.Usage.Corrections);
        Assert.Single(provider.Requests);
        Assert.DoesNotContain(evidence.Snapshot(plan.Provenance.SessionId), item =>
            item.RunId == plan.Provenance.ParentRunId
            && item.Provenance.Source.StartsWith("agent:", StringComparison.Ordinal));
    }

    /// <summary>A JSON-looking response needs no confidence field and receives no invented confidence.</summary>
    [Fact]
    public async Task RunAsync_OmittedConfidence_ReturnsTextWithoutInventedFinding()
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
        var response = CreateFindingJsonWithoutConfidence(evidenceId);
        var provider = new FindingSequenceProvider(response);
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
        Assert.Equal(response, outcome.Response);
        Assert.Null(outcome.Findings);
        Assert.Equal(0, outcome.Usage.Corrections);
        Assert.Single(provider.Requests);
    }

    /// <summary>A legacy empty-finding object remains response text rather than a parsed finding set.</summary>
    [Fact]
    public async Task RunAsync_EmptyFindingObject_CompletesAsResponseWithoutCorrection()
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
        Assert.Equal(CreateEmptyFindingJson(), outcome.Response);
        Assert.Null(outcome.Findings);
        Assert.Null(outcome.Review);
        Assert.Null(outcome.Implementation);
        Assert.Equal(0, outcome.Usage.Corrections);
        Assert.Single(provider.Requests);
    }

    /// <summary>Answer category text is not constrained by the deprecated finding schema.</summary>
    [Fact]
    public async Task RunAsync_UnknownCategory_ReturnsUnparsedResponse()
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
        var response = CreateFindingJson(evidenceId.Value.ToString("D"), "Custom category.", "custom-category");
        var provider = new FindingSequenceProvider(response);
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
        Assert.Equal(response, outcome.Response);
        Assert.Null(outcome.Findings);
        Assert.Equal(0, outcome.Usage.Corrections);
        Assert.Single(provider.Requests);
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

    /// <summary>A failed durable join remains a failure without promoting claims from response text.</summary>
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

    /// <summary>Optional public summaries do not change baseline host limits or fallback estimates.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisplayOnlyReasoningDoesNotConsumeLegacyChildBudget(bool includeSummary)
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, []);
        var plan = CreatePlan(assignment);
        var provider = new ExcessiveReasoningProvider(includeSummary ? new string('r', (128 * 1024) + 1) : string.Empty, displayOnly: true);
        var registry = new ToolRegistry([]);
        var runner = CreateRunner(provider, CreatePipeline(registry, events, sanitizer), evidence, sanitizer, profile, CreateParentContext(plan, []), []);
        var outcome = await runner.RunAsync(plan, assignment);
        Assert.Equal(string.Empty, outcome.Response);
        Assert.True(provider.Requests[0].IncludeReasoningText);
        Assert.True(outcome.Usage.ModelTokens < 128 * 1024 / 4);
    }

    /// <summary>Missing-usage reasoning is telemetry; only the independent character ceiling rejects its size.</summary>
    [Theory]
    [InlineData(5_000, true)]
    [InlineData((128 * 1024) + 1, false)]
    public async Task RunAsync_StreamedReasoning_UsesCharacterLimitNotEstimatedTokenLimit(int characters, bool succeeds)
    {
        // Arrange
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, []);
        var plan = CreatePlan(assignment);
        var provider = new ExcessiveReasoningProvider(new string('r', characters));
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
        if (succeeds)
        {
            var outcome = await runner.RunAsync(plan, assignment);
            Assert.Equal(string.Empty, outcome.Response);
            Assert.Null(outcome.Findings);
            Assert.True(outcome.Usage.ModelTokens >= TokenEstimator.Estimate(new string('r', characters)));
            Assert.Equal(0, outcome.Usage.Corrections);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => runner.RunAsync(plan, assignment));
            Assert.Contains("output bound", exception.Message, StringComparison.Ordinal);
        }

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

    /// <summary>Fallback selection stays in response metadata without inventing parent evidence.</summary>
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
        Assert.DoesNotContain(evidence.Snapshot(plan.Provenance.SessionId), item =>
            item.RunId == plan.Provenance.ParentRunId
            && item.Provenance.Source.StartsWith("agent:", StringComparison.Ordinal));
        Assert.Equal(fallback.Id, outcome.ModelSelection?.EffectiveProfileId);
        Assert.Null(outcome.Findings);
        Assert.Equal(CreateFindingJson(evidenceId.Value.ToString("D"), "Fallback-backed finding."), outcome.Response);
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

    /// <summary>Invalid or unavailable tool requests remain rejected even though answer text has no schema.</summary>
    [Theory]
    [InlineData("inspect_metadata", "{")]
    [InlineData("inspect_metadata", "{\"unexpected\":true}")]
    [InlineData(DelegateAgentsContract.ToolId, "{}")]
    [InlineData("inspect_metadata", "{}", true)]
    public async Task RunAsync_InvalidToolRequest_RepairsBeforeAcceptingOrdinaryResponse(string toolId, string argumentsJson, bool providerFailure = false)
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var tool = new InspectMetadataTool();
        var registry = new ToolRegistry([tool]);
        var assignment = CreateAssignment(profile.Id, [tool.Definition.Id]);
        var plan = CreatePlan(assignment);
        var attempt = new ToolRequestModelOutput(toolId, argumentsJson);
        var provider = new ToolAttemptThenResponseProvider(attempt) { RejectAtProvider = providerFailure };
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, [tool.Definition.Id]),
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId));

        var outcome = await runner.RunAsync(plan, assignment);

        Assert.Equal("The requested tool could not be used.", outcome.Response);
        Assert.Equal(1, outcome.Usage.Corrections);
        Assert.Equal(2, provider.Requests.Count);
        AssertRejectedToolHistory(provider.Requests[1], !providerFailure && toolId == tool.Definition.Id ? [attempt] : []);
        if (providerFailure || toolId != tool.Definition.Id)
        {
            Assert.DoesNotContain(provider.Requests[1].Messages, message => message.ToolCallId is not null);
        }

        Assert.Null(tool.LastInvocationContext);
        Assert.Empty(evidence.Snapshot(plan.Provenance.SessionId));
        Assert.Empty(outcome.DeliveredEvidenceIds);
        Assert.Null(outcome.Findings);
    }

    /// <summary>One invalid sibling rejects the full batch while retaining every known attempted call and error.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_InvalidToolBatch_PreservesBothRejectedSiblingsWithoutExecution(bool invalidFirst)
    {
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var tool = new InspectMetadataTool();
        var registry = new ToolRegistry([tool]);
        var assignment = CreateAssignment(profile.Id, [tool.Definition.Id]);
        var plan = CreatePlan(assignment);
        var valid = new ToolRequestModelOutput(tool.Definition.Id, "{}");
        var invalid = new ToolRequestModelOutput(tool.Definition.Id, "{\"unexpected\":true}");
        ToolRequestModelOutput[] attempts = invalidFirst ? [invalid, valid] : [valid, invalid];
        var provider = new ToolAttemptThenResponseProvider(attempts);
        var runner = CreateRunner(
            provider,
            CreatePipeline(registry, events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, [tool.Definition.Id]),
            registry.GetRegistrations(plan.Provenance.SessionId, plan.Provenance.ParentRunId));

        var outcome = await runner.RunAsync(plan, assignment);

        Assert.Equal("The requested tool could not be used.", outcome.Response);
        Assert.Equal(1, outcome.Usage.Corrections);
        Assert.Equal(2, provider.Requests.Count);
        AssertRejectedToolHistory(provider.Requests[1], attempts);
        var failedCallNumber = invalidFirst ? 1 : 2;
        var validCallNumber = invalidFirst ? 2 : 1;
        foreach (var message in provider.Requests[1].Messages.Where(message => message.Role == ModelMessageRole.Tool))
        {
            using var payload = JsonDocument.Parse(message.GetModelVisibleContent());
            var error = payload.RootElement.GetProperty("error").GetString();
            Assert.NotNull(error);
            Assert.Contains($"Call {failedCallNumber} ({invalid.ToolName}) failed validation:", error, StringComparison.Ordinal);
            Assert.DoesNotContain($"Call {validCallNumber} ({valid.ToolName}) failed validation:", error, StringComparison.Ordinal);
            Assert.Contains(
                "Other calls in this batch were not executed; this does not mean their paths or arguments were invalid.",
                error,
                StringComparison.Ordinal);
        }

        Assert.Null(tool.LastInvocationContext);
        Assert.Empty(evidence.Snapshot(plan.Provenance.SessionId));
        Assert.Empty(outcome.DeliveredEvidenceIds);
        Assert.Null(outcome.Findings);
    }

    /// <summary>Caller cancellation interrupts a waiting provider and retains its effective model metadata.</summary>
    [Fact]
    public async Task RunAsync_CancelledProvider_PreservesCancellationAndModelDetails()
    {
        await using var events = new DomainEventStream();
        using var cancellation = new CancellationTokenSource();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, []);
        var plan = CreatePlan(assignment);
        var provider = new MixedRoleProvider(new Dictionary<RunId, string>
        {
            [assignment.ChildRunId] = "This response must not be returned after cancellation.",
        });
        var runner = CreateRunner(
            provider,
            CreatePipeline(new ToolRegistry([]), events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, []),
            []);
        var running = runner.RunAsync(plan, assignment, cancellation.Token);
        try
        {
            await provider.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await cancellation.CancelAsync();

#pragma warning disable VSTHRD003 // The test starts this gated task before requesting cancellation.
            var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
#pragma warning restore VSTHRD003
            Assert.True(ChildAgentFailureDetails.TryGet(exception, out var failure));
            Assert.NotNull(failure);
            Assert.Equal(profile.Id, failure.ModelProfileId);
            Assert.Equal(profile.Id, failure.ModelSelection?.EffectiveProfileId);
            Assert.Empty(evidence.Snapshot(plan.Provenance.SessionId));
            Assert.Equal(1, Assert.Single(provider.RequestCounts).Value);
        }
        finally
        {
            await cancellation.CancelAsync();
            provider.Release.TrySetResult();
        }
    }

    /// <summary>Cancellation after the final chunk but before normal EOF wins without losing recorded usage.</summary>
    [Fact]
    public async Task RunAsync_ProviderCancelsBeforeNormalEof_PreservesUsageAndRejectsFinalResponse()
    {
        await using var events = new DomainEventStream();
        using var cancellation = new CancellationTokenSource();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = CreateProfile();
        var assignment = CreateAssignment(profile.Id, []);
        var plan = CreatePlan(assignment);
        var provider = new FinalChunkCancellationProvider(cancellation);
        var usage = new SessionUsageProjection();
        var runner = CreateRunner(
            provider,
            CreatePipeline(new ToolRegistry([]), events, sanitizer),
            evidence,
            sanitizer,
            profile,
            CreateParentContext(plan, []),
            [],
            usage);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync(plan, assignment, cancellation.Token));

        Assert.True(provider.ReachedNormalEof);
        Assert.True(cancellation.IsCancellationRequested);
        var request = Assert.Single(provider.Requests);
        var estimate = Assert.IsType<ModelWireEstimate>(request.WireEstimate);
        Assert.True(ChildAgentFailureDetails.TryGet(exception, out var failure));
        Assert.NotNull(failure);
        Assert.Equal(Math.Max(48L, (long)estimate.WireInputTokens + TokenEstimator.Estimate(provider.Response)), failure.Usage.ModelTokens);
        Assert.Equal(0, failure.Usage.ToolCalls);
        Assert.Equal(0, failure.Usage.Corrections);
        Assert.Equal(profile.Id, failure.ModelProfileId);
        Assert.Equal(profile.Id, failure.ModelSelection?.EffectiveProfileId);
        Assert.Equal(37, usage.GetSnapshot(plan.Provenance.SessionId).InputTokens);
        Assert.Equal(11, usage.GetSnapshot(plan.Provenance.SessionId).OutputTokens);
        Assert.Empty(evidence.Snapshot(plan.Provenance.SessionId));
    }

    /// <summary>Child-only cancellation overrides a non-cooperative completion while preserving metadata and its sibling.</summary>
    [Fact]
    public async Task Scheduler_CancelledChildReturnsCompleted_ClearsResponseAndPreservesMetadata()
    {
        using var parentCancellation = new CancellationTokenSource();
        await using var scheduler = new AgentRunScheduler(new AgentSchedulerOptions
        {
            MaximumActiveChildren = 2,
            MaximumActiveChildrenPerParent = 2,
            ShutdownTimeout = TimeSpan.FromSeconds(2),
        });
        var profile = CreateProfile();
        var cancelledAssignment = CreateAssignment(profile.Id, []);
        var siblingAssignment = CreateAssignment(profile.Id, []);
        var plan = CreatePlan(cancelledAssignment) with
        {
            Assignments = [cancelledAssignment, siblingAssignment],
            ParentBudget = AgentResourceBudget.Aggregate([cancelledAssignment.Budget, siblingAssignment.Budget]),
        };
        var completed = new AgentRunOutcome
        {
            AssignmentId = cancelledAssignment.AssignmentId,
            ChildRunId = cancelledAssignment.ChildRunId,
            Role = cancelledAssignment.Role,
            Generation = plan.Provenance.Generation,
            Status = AgentRunStatus.Completed,
            Response = "This late response must not survive cancellation.",
            Reason = "The fake ignored cancellation.",
            Usage = new AgentResourceUsage { ModelTokens = 73, ToolCalls = 2, EvidenceItems = 1, WallTime = TimeSpan.FromMilliseconds(12) },
            ModelProfileId = profile.Id,
            ModelSelection = CreateSelector(profile).Select(cancelledAssignment).Provenance,
            DeliveredEvidenceIds = [EvidenceId.New()],
            Findings = new AgentFindingSet
            {
                AssignmentId = cancelledAssignment.AssignmentId,
                ChildRunId = cancelledAssignment.ChildRunId,
                Generation = plan.Provenance.Generation,
                Summary = "A late legacy payload must also be cleared.",
            },
        };
        var siblingCompleted = completed with
        {
            AssignmentId = siblingAssignment.AssignmentId,
            ChildRunId = siblingAssignment.ChildRunId,
            Response = "The uncancelled sibling completed normally.",
            Findings = null,
        };
        var runner = new CancellationIgnoringRunner(new Dictionary<AgentAssignmentId, AgentRunOutcome>
        {
            [cancelledAssignment.AssignmentId] = completed,
            [siblingAssignment.AssignmentId] = siblingCompleted,
        });
        var running = scheduler.RunAsync(plan, runner, parentCancellation.Token);
        try
        {
            await runner.AllStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(await scheduler.CancelAssignmentAsync(plan.DelegationId, cancelledAssignment.AssignmentId));
            Assert.True(runner.ObservedTokens[cancelledAssignment.AssignmentId].IsCancellationRequested);
            Assert.False(runner.ObservedTokens[siblingAssignment.AssignmentId].IsCancellationRequested);
            Assert.False(parentCancellation.IsCancellationRequested);
            runner.Release.TrySetResult();

            var outcomes = await running.WaitAsync(TimeSpan.FromSeconds(2));

            var cancelled = Assert.Single(outcomes, outcome => outcome.AssignmentId == cancelledAssignment.AssignmentId);
            Assert.Equal(AgentRunStatus.Cancelled, cancelled.Status);
            Assert.Null(cancelled.Response);
            Assert.Null(cancelled.Findings);
            Assert.Null(cancelled.Review);
            Assert.Null(cancelled.Implementation);
            Assert.Null(cancelled.ChangeSet);
            Assert.Equal(completed.Usage, cancelled.Usage);
            Assert.NotNull(cancelled.ModelSelection);
            Assert.Equal(completed.ModelSelection, cancelled.ModelSelection);
            Assert.Equal(completed.ModelProfileId, cancelled.ModelProfileId);
            Assert.Equal(completed.DeliveredEvidenceIds, cancelled.DeliveredEvidenceIds);
            Assert.Equal(completed.ChildRunId, cancelled.ChildRunId);
            Assert.Equal(completed.Generation, cancelled.Generation);
            Assert.Equal(siblingCompleted, Assert.Single(outcomes, outcome => outcome.AssignmentId == siblingAssignment.AssignmentId));
        }
        finally
        {
            runner.Release.TrySetResult();
            await parentCancellation.CancelAsync();
        }
    }

    private static void AssertRejectedToolHistory(
        ModelStreamRequest request,
        IReadOnlyList<ToolRequestModelOutput> attempts)
    {
        var correction = Assert.Single(request.Messages, message => message.SectionId == "child-tool-error");
        Assert.Equal(ModelMessageRole.Developer, correction.Role);
        Assert.Same(correction, request.Messages[^1]);
        var history = request.Messages
            .Where(message => message.SectionId is "child-tool-call" or "child-tool-result")
            .ToArray();
        Assert.Equal(attempts.Count * 2, history.Length);
        var correlations = new HashSet<string>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < attempts.Count; ordinal++)
        {
            var call = history[ordinal * 2];
            var result = history[(ordinal * 2) + 1];
            Assert.Equal("child-tool-call", call.SectionId);
            Assert.Equal(ModelMessageRole.Assistant, call.Role);
            Assert.Equal(attempts[ordinal].ToolName, call.ToolName);
            Assert.Equal(attempts[ordinal].ArgumentsJson, call.GetModelVisibleContent());
            Assert.NotNull(call.ToolCallId);
            Assert.NotEmpty(call.ToolCallId);
            Assert.True(correlations.Add(call.ToolCallId));
            Assert.Equal("child-tool-result", result.SectionId);
            Assert.Equal(ModelMessageRole.Tool, result.Role);
            Assert.Equal(call.ToolCallId, result.ToolCallId);
            Assert.Equal(call.ToolName, result.ToolName);
            using var payload = JsonDocument.Parse(result.GetModelVisibleContent());
            Assert.False(payload.RootElement.GetProperty("succeeded").GetBoolean());
            Assert.False(payload.RootElement.GetProperty("executed").GetBoolean());
            Assert.False(payload.RootElement.TryGetProperty("evidenceId", out _));
            var error = payload.RootElement.GetProperty("error").GetString();
            Assert.NotNull(error);
            Assert.NotEmpty(error);
            Assert.Contains(error, correction.GetModelVisibleContent(), StringComparison.Ordinal);
        }
    }

    private static ModelExplorerAssignmentRunner CreateRunner(
        IModelProvider provider,
        IToolInvocationPipeline pipeline,
        IEvidenceStore evidence,
        IOutputSanitizer sanitizer,
        ModelProfile profile,
        ToolExecutionContext parentContext,
        IReadOnlyList<ToolRegistration> registrations,
        SessionUsageProjection? usage = null,
        DelegateAgentsOptions? options = null,
        IModelProvider? trustedModels = null,
        ActiveTurnCompactionCandidateProfile? compactionProfile = null)
    {
        return CreateRunner(
            provider,
            pipeline,
            evidence,
            sanitizer,
            [profile],
            parentContext,
            registrations,
            usage,
            options,
            trustedModels,
            compactionProfile);
    }

    private static ModelExplorerAssignmentRunner CreateRunner(
        IModelProvider provider,
        IToolInvocationPipeline pipeline,
        IEvidenceStore evidence,
        IOutputSanitizer sanitizer,
        IReadOnlyList<ModelProfile> profiles,
        ToolExecutionContext parentContext,
        IReadOnlyList<ToolRegistration> registrations,
        SessionUsageProjection? usage = null,
        DelegateAgentsOptions? options = null,
        IModelProvider? trustedModels = null,
        ActiveTurnCompactionCandidateProfile? compactionProfile = null)
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
            options ?? CreateOptions(),
            parentContext,
            registrations,
            TestPromptLoader.Instance,
            usage,
            trustedModels: trustedModels,
            compactionProfile: compactionProfile);
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
                StructuredOutput = false,
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
            Tasks = ["Explain the behavior you inspect."],
            InitialContext = "bounded child context",
            OutputSchema = AgentAssignment.ResponseSchema,
            StoppingCondition = "Return when the assigned question is answered.",
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

    private sealed class StructuredMetadataTool : Tool<InspectMetadataInput, JsonElement>
    {
        public override ToolDefinition Definition { get; } = new InspectMetadataTool().Definition with
        {
            OutputSchema = new ToolSchema("Object", 1, "{\"type\":\"object\"}"),
        };

        public override Task<ToolExecution<JsonElement>> ExecuteAsync(
            InspectMetadataInput input,
            ToolExecutionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = JsonSerializer.SerializeToElement(new
            {
                Path = "src/Test.cs",
                Lines = new[] { "first", "second" },
                Source = "Server=example;Database=test;Password=fixture-secret",
            });
            return Task.FromResult(new ToolExecution<JsonElement>(content, []));
        }

        protected override void ValidateInput(InspectMetadataInput input)
        {
        }
    }

    private sealed class InspectMetadataTool : Tool<InspectMetadataInput, string>
    {
        private readonly string _modelResultContent;
        private readonly IReadOnlyList<ToolProvenanceSource> _sources;

        /// <summary>Initializes a new instance of the <see cref="InspectMetadataTool"/> class.</summary>
        public InspectMetadataTool(string modelResultContent = "Compiler-backed metadata.", int maximumOutputBytes = 4_096, IReadOnlyList<ToolProvenanceSource>? sources = null)
        {
            _modelResultContent = modelResultContent;
            _sources = sources ?? [new ToolProvenanceSource("file", "src/Test.cs")];
            Definition = Definition with { MaximumOutputBytes = maximumOutputBytes };
        }

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
                _sources,
                ModelResultContent: _modelResultContent));
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

    private sealed class ToolThenFindingProvider(
        string toolId,
        bool retrieveEvidence = false,
        bool emitReplayEnvelope = false) : IModelProvider
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
                if (emitReplayEnvelope)
                {
                    yield return new ModelChunk
                    {
                        ResponseEnvelope = CreateEnvelope(request),
                    };
                }

                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput(toolId, "{}"),
                    Usage = new ModelUsage(20, 5),
                };
                yield break;
            }

            var toolResult = request.Messages.Single(message => message.Role == ModelMessageRole.Tool && message.ToolName == toolId);
            using var document = JsonDocument.Parse(toolResult.GetModelVisibleContent());
            var evidenceId = document.RootElement.GetProperty("evidenceId").GetString()
                ?? throw new InvalidDataException("The tool result omitted its evidence identity.");
            if (retrieveEvidence && Requests.Count == 2)
            {
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput(ChildAgentEvidenceTool.ToolId, JsonSerializer.Serialize(new { evidenceId })),
                    Usage = new ModelUsage(20, 5),
                };
                yield break;
            }

            yield return new ModelChunk
            {
                Output = new TextModelOutput(CreateFindingJson(evidenceId, "Tool-backed finding.")),
                Usage = new ModelUsage(30, 15),
            };
        }

        private static ModelResponseReplayEnvelope CreateEnvelope(ModelStreamRequest request)
        {
            return new ModelResponseReplayEnvelope(
                new ModelReplayBinding
                {
                    ProviderId = "test",
                    ModelId = "test",
                    ProfileId = request.ResolvedProfileId ?? throw new InvalidOperationException("Profile is required."),
                    RunId = request.RunId,
                    ModelRound = request.ToolContinuationRound,
                    CredentialGeneration = "test",
                    ToolInventoryDigest = "test-tools",
                    InstructionDigest = "test-instructions",
                    NormalizedRoundDigest = "test-round",
                },
                [1],
                ["wire-0"],
                retainedOutputTokens: 1);
        }
    }

    private sealed class FinalChunkCancellationProvider : IModelProvider
    {
        private readonly CancellationTokenSource _cancellation;

        /// <summary>Initializes a new instance of the <see cref="FinalChunkCancellationProvider"/> class.</summary>
        public FinalChunkCancellationProvider(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        /// <summary>Gets the final text that must not become a completed outcome.</summary>
        public string Response { get; } = "The provider emitted this final answer before cancellation.";

        /// <summary>Gets the requests received before cancellation.</summary>
        public List<ModelStreamRequest> Requests { get; } = [];

        /// <summary>Gets whether the provider ended normally after cancelling its caller.</summary>
        public bool ReachedNormalEof { get; private set; }

        /// <inheritdoc />
        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new ModelChunk
            {
                Output = new TextModelOutput(Response),
                Usage = new ModelUsage(37, 11),
            };
            await _cancellation.CancelAsync();
            ReachedNormalEof = true;
        }
    }

    private sealed class CancellationIgnoringRunner : IAgentAssignmentRunner
    {
        private readonly IReadOnlyDictionary<AgentAssignmentId, AgentRunOutcome> _outcomes;

        /// <summary>Initializes a new instance of the <see cref="CancellationIgnoringRunner"/> class.</summary>
        public CancellationIgnoringRunner(IReadOnlyDictionary<AgentAssignmentId, AgentRunOutcome> outcomes)
        {
            _outcomes = outcomes;
        }

        /// <summary>Gets each admitted child's cancellation token.</summary>
        public ConcurrentDictionary<AgentAssignmentId, CancellationToken> ObservedTokens { get; } = new();

        /// <summary>Gets the gate indicating that every expected child entered.</summary>
        public TaskCompletionSource AllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the test-owned gate that releases non-cooperative completions.</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public async Task<AgentRunOutcome> RunAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            CancellationToken cancellationToken = default)
        {
            ObservedTokens[assignment.AssignmentId] = cancellationToken;
            if (ObservedTokens.Count == _outcomes.Count)
            {
                AllStarted.TrySetResult();
            }

#pragma warning disable VSTHRD003 // The test releases this non-cooperative runner after cancelling only one child.
            await Release.Task;
#pragma warning restore VSTHRD003
            return _outcomes[assignment.AssignmentId];
        }
    }

    private sealed class ToolAttemptThenResponseProvider : IModelProvider
    {
        private readonly IReadOnlyList<ToolRequestModelOutput> _attempts;

        public ToolAttemptThenResponseProvider(params ToolRequestModelOutput[] attempts)
        {
            _attempts = attempts;
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public bool RejectAtProvider { get; init; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Requests.Count > 2)
            {
                throw new InvalidOperationException("An ordinary final response must not trigger another request.");
            }

            await Task.Yield();
            if (Requests.Count == 1)
            {
                foreach (var attempt in _attempts)
                {
                    yield return new ModelChunk { Output = attempt, Usage = new ModelUsage(20, 10) };
                }

                if (RejectAtProvider)
                {
                    throw new MalformedInvocationException("The provider emitted tool parameters as assistant text.");
                }

                yield break;
            }

            yield return new ModelChunk
            {
                Output = new TextModelOutput("The requested tool could not be used."),
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

    private sealed class ExcessiveReasoningProvider(string reasoning, bool displayOnly = false) : IModelProvider
    {
        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            yield return new ModelChunk { Reasoning = reasoning, IsDisplayOnlyReasoning = displayOnly };
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
