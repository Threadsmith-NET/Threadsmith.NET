namespace Threadsmith.Skills.Tests;

using System.Runtime.CompilerServices;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>Review is one ordinary procedure; retry preserves its model and prepared context usage.</summary>
    [Theory]
    [InlineData("review", "{}")]
    [InlineData("review-pr", "{\"changeSummary\":\"Inspect change\",\"paths\":[\"tracked.txt\"]}")]
    public async Task NativeReview_FailedResponseResumesThroughSelectedModel(string selector, string input)
    {
        var catalog = new SkillCatalog([new SkillCatalogSource(SkillScope.Maintained, MaintainedRoot(), "maintained", IsMaintained: true)]);
        await catalog.RefreshAsync();
        var verifier = new SkillPackageVerifier(new SkillTrustPolicySnapshot());
        var candidate = await verifier.VerifyAsync(catalog.Resolve(selector));
        Assert.True(candidate.Enabled, candidate.VerificationReason);
        var step = Assert.Single(candidate.Metadata.Workflow.Steps);
        Assert.Equal(SkillWorkflowStepKind.InvokeProcedure, step.Kind);
        Assert.Equal(PromptFileNames.SkillReview, step.PromptFile);
        Assert.Null(step.InstructionAsset);
        var selected = ModelProfileId.New();
        var current = selected;
        var other = ModelProfileId.New();
        var model = new NativeResponseProvider();
        var sanitizer = new SecretOutputSanitizer();
        var registry = new ToolRegistry([]);
        var state = new InMemorySkillStateStore();
        var usage = new SessionUsageProjection();
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolInvocationPipeline>.Instance);
        await using var workflow = new SkillWorkflowOrchestrator(
            catalog,
            verifier,
            new CompatibleEvaluator { Profiles = [other, selected] },
            new SkillContentLoader(sanitizer, TestPromptLoader.Instance),
            new BoundedJsonSchemaValidator(),
            new ModelSkillProcedureRunner(model, registry, pipeline, sanitizer, (_, _) => Task.FromResult(PermissionContext()), TestPromptLoader.Instance, sessionUsage: usage),
            TestPromptLoader.Instance,
            state,
            (_, _) => Task.FromResult(new SkillInvocationHostContext { Trust = RepositoryTrustLevel.TrustedRead, Phase = RunPhase.EvidenceCollection, ModelProfileId = current, ReasoningLevel = "medium" }),
            events);
        var request = PermissionPlan().Request with { Selector = selector, InputJson = input, HostBudget = new SkillBudget() };

        var failed = await workflow.InvokeAsync(request);
        Assert.Equal(SkillInvocationStatus.Failed, failed.Status);
        Assert.Contains("Review unavailable", failed.Response, StringComparison.Ordinal);
        Assert.NotNull(failed.OutputJson);
        current = other;
        var completed = await workflow.ResumeAsync(failed.InvocationId);

        Assert.Equal(SkillInvocationStatus.Completed, completed.Status);
        Assert.Contains("Review complete", completed.Response, StringComparison.Ordinal);
        Assert.Equal(2, model.Requests.Count);
        var counters = usage.GetOwnerSnapshot(request.SessionId);
        Assert.Equal(20, counters.InputTokens);
        Assert.Equal(4, counters.OutputTokens);
        Assert.False(counters.HasUnknownUsage);
        var requestStatus = usage.GetRequestStatus(request.SessionId);
        var contextUsage = Assert.IsType<ContextUsageSnapshot>(requestStatus?.ContextUsage);
        var requestEstimate = Assert.IsType<ModelWireEstimate>(model.Requests[^1].WireEstimate);
        Assert.Equal("skill-procedure", contextUsage.Stage);
        Assert.Equal((long)requestEstimate.WireInputTokens, contextUsage.InputTokens);
        Assert.Equal(contextUsage.InputTokens, contextUsage.Components.Sum(item => item.Tokens));
        Assert.All(model.Requests, item =>
        {
            Assert.Equal(selected, item.ResolvedProfileId);
            Assert.Equal(ReasoningLevel.Medium, item.ReasoningLevel);
            Assert.Contains("Call delegate_agents", item.Input, StringComparison.Ordinal);
        });
    }

    /// <summary>An unavailable selected provider cannot silently dispatch work through a different root model.</summary>
    [Fact]
    public async Task NativeReview_OfflineRootFailsBeforeToolExecution()
    {
        var plan = PermissionPlan();
        var model = new NativeResponseProvider { Unavailable = true };
        var usage = new SessionUsageProjection();
        var registry = new ToolRegistry([]);
        var sanitizer = new SecretOutputSanitizer();
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolInvocationPipeline>.Instance);
        var runner = new ModelSkillProcedureRunner(model, registry, pipeline, sanitizer, (_, _) => Task.FromResult(PermissionContext()), TestPromptLoader.Instance, sessionUsage: usage);

        await Assert.ThrowsAsync<ModelProviderException>(() => runner.RunAsync(plan, PermissionStep(), 1, [], "{}"));

        Assert.Equal(plan.ModelProfileId, Assert.Single(model.Requests).ResolvedProfileId);
        var counters = usage.GetOwnerSnapshot(plan.Request.SessionId);
        Assert.True(counters.HasUnknownUsage);
        Assert.True(counters.HasObservation);
        Assert.Equal(0, counters.InputTokens);
    }

    /// <summary>Session-activated tools are inherited only by their owning invocation.</summary>
    [Fact]
    public async Task NativeCompatibility_UsesSessionScopedActivation()
    {
        var request = PermissionPlan().Request;
        var registry = new ToolRegistry([new PermissionProbeTool()], activationPolicy: new SessionActivation(request.SessionId));
        var catalog = new SkillCatalog([new SkillCatalogSource(SkillScope.Maintained, MaintainedRoot(), "maintained", IsMaintained: true)]);
        await catalog.RefreshAsync();
        var candidate = catalog.Resolve("review");
        var evaluator = new SkillCompatibilityEvaluator(registry, new ConfiguredModelCatalog([]), "1.0.0");

        Assert.Contains("permission_probe", evaluator.Evaluate(candidate, request).InheritedTools);
        Assert.Empty(evaluator.Evaluate(candidate, request with { SessionId = SessionId.New() }).InheritedTools);
    }

    /// <summary>A semantic tool failure retains its diagnostic body in the native continuation.</summary>
    [Fact]
    public async Task NativeProcedure_FailedToolRetainsDiagnosticContent()
    {
        var model = new PermissionModelProvider();
        var tool = new PermissionProbeTool
        {
            ModelResultContent = "The specialist failed; configuration was not assessed.",
            Failure = new ToolExecutionFailure(ToolErrorClassification.ExecutionFailure, "procedure failed"),
        };
        await using var events = new DomainEventStream();
        await CreatePermissionRunner(PermissionContext, tool, model, events).RunAsync(PermissionPlan(), PermissionStep(), 1, [], "{}");

        var message = Assert.Single(model.Requests[1].Messages, item => item.Role == ModelMessageRole.Tool);
        Assert.True(message.IsError);
        Assert.Contains(tool.ModelResultContent, message.GetModelVisibleContent(), StringComparison.Ordinal);
    }

    /// <summary>A host-only workflow can continue even when the selected model cannot run native procedures.</summary>
    [Fact]
    public async Task NativeWorkflow_HostOnlyContinueDoesNotRequireModelCompatibility()
    {
        var catalog = new SkillCatalog([new SkillCatalogSource(SkillScope.Maintained, MaintainedRoot(), "maintained", IsMaintained: true)]);
        await catalog.RefreshAsync();
        var verified = await new SkillPackageVerifier(new SkillTrustPolicySnapshot()).VerifyAsync(catalog.Resolve("review"));
        var candidate = verified with
        {
            Metadata = verified.Metadata with
            {
                Requirements = verified.Metadata.Requirements with { RequiredTools = [], InheritAvailableTools = false },
                Workflow = new SkillWorkflowDefinition { WorkflowId = "host-only", Steps = [new SkillWorkflowStep { StepId = "ask", Kind = SkillWorkflowStepKind.AskUserInput }] },
            },
        };
        var state = new InMemorySkillStateStore();
        await using var events = new DomainEventStream();
        await using var workflow = new SkillWorkflowOrchestrator(
            new FixedCatalog([candidate]),
            new PassThroughVerifier(),
            new SkillCompatibilityEvaluator(new ToolRegistry([]), new ConfiguredModelCatalog([]), "1.0.0"),
            new SkillContentLoader(new SecretOutputSanitizer(), TestPromptLoader.Instance),
            new BoundedJsonSchemaValidator(),
            new FixedProcedureRunner("{}"),
            TestPromptLoader.Instance,
            state,
            (_, _) => Task.FromResult(new SkillInvocationHostContext { Trust = RepositoryTrustLevel.TrustedRead, Phase = RunPhase.EvidenceCollection, ModelProfileId = ModelProfileId.New() }),
            events);

        var waiting = await workflow.InvokeAsync(PermissionPlan().Request with { Selector = "review" });
        Assert.Equal(SkillInvocationStatus.AwaitingHost, waiting.Status);
        var result = await workflow.ContinueAsync(waiting.InvocationId, "{}");
        Assert.Equal(SkillInvocationStatus.Completed, result.Status);
        Assert.Equal(0, Assert.Single(result.Checkpoint.Steps).ModelTurns);
    }

    private sealed class SessionActivation(SessionId owner) : IProgressiveToolActivationPolicy
    {
        public bool IsActive(string toolId, SessionId sessionId, RunId runId) => sessionId == owner;
    }

    private sealed class NativeResponseProvider : IModelProvider
    {
        public bool Unavailable { get; init; }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Unavailable)
            {
                throw new ModelProviderException("Selected provider is offline.");
            }

            yield return new ModelChunk
            {
                Usage = new ModelUsage(10, 2),
                Text = Requests.Count == 1
                ? "{\"succeeded\":false,\"response\":\"# Review unavailable\\nAll specialists failed.\"}"
                : "{\"succeeded\":true,\"response\":\"# Review complete\\nNo supported issue.\"}",
            };
        }
    }
}
