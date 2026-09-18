namespace Threadsmith.Skills.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>A nested skill retains the caller's registration identity even before its first model turn.</summary>
    [Fact]
    public async Task SkillProcedure_CallerSnapshotDoesNotRebindReplacedRegistration()
    {
        var plan = PermissionPlan();
        var original = new PermissionProbeTool();
        var replacement = new PermissionProbeTool();
        var registry = new ToolRegistry([]);
        var source = new ToolActivitySource(ToolActivitySourceKind.Extension, "caller-original");
        registry.RegisterOrReplace(original, source);
        var snapshots = new ConversationToolSnapshotStore();
        var callerId = snapshots.Capture(
            plan.Request.SessionId,
            plan.Request.RunId,
            [registry.GetRegistration("permission_probe")],
            PermissionContext());
        plan = plan with { Request = plan.Request with { CallerToolSnapshotId = callerId } };
        registry.RegisterOrReplace(replacement, source, original);
        var model = new PermissionModelProvider();
        var sanitizer = new SecretOutputSanitizer();
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(
            registry,
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            sanitizer,
            NullLogger<ToolInvocationPipeline>.Instance);
        var runner = new ModelSkillProcedureRunner(
            model,
            registry,
            pipeline,
            sanitizer,
            (_, _) => throw new InvalidOperationException("A bound caller must not fall back to session tool scope."),
            TestPromptLoader.Instance,
            snapshots: snapshots);

        await runner.RunAsync(plan, PermissionStep(), 1, [], "{}");

        Assert.Equal(0, original.Executions);
        Assert.Equal(0, replacement.Executions);
        Assert.Single(model.Requests[0].Tools);
        Assert.Equal(2, model.Requests.Count);
        var error = Assert.Single(model.Requests[1].Messages, message => message.IsError == true);
        Assert.Contains("no longer matches", error.GetModelVisibleContent(), StringComparison.Ordinal);
        snapshots.Release(callerId);
    }

    /// <summary>A caller scope must still exist and belong to the exact invoking session and run.</summary>
    [Theory]
    [InlineData("expired")]
    [InlineData("session")]
    [InlineData("run")]
    public async Task SkillProcedure_CallerSnapshotCannotBeReusedByAnotherOwner(string change)
    {
        var plan = PermissionPlan();
        var tool = new PermissionProbeTool();
        var registry = new ToolRegistry([tool]);
        var snapshots = new ConversationToolSnapshotStore();
        var callerId = snapshots.Capture(
            plan.Request.SessionId,
            plan.Request.RunId,
            [registry.GetRegistration("permission_probe")],
            PermissionContext());
        plan = plan with
        {
            Request = plan.Request with
            {
                CallerToolSnapshotId = callerId,
                SessionId = change == "session" ? SessionId.New() : plan.Request.SessionId,
                RunId = change == "run" ? RunId.New() : plan.Request.RunId,
            },
        };
        if (change == "expired")
        {
            snapshots.Release(callerId);
        }

        var model = new PermissionModelProvider();
        var sanitizer = new SecretOutputSanitizer();
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(
            registry,
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            sanitizer,
            NullLogger<ToolInvocationPipeline>.Instance);
        var runner = new ModelSkillProcedureRunner(
            model,
            registry,
            pipeline,
            sanitizer,
            (_, _) => Task.FromResult(PermissionContext()),
            TestPromptLoader.Instance,
            snapshots: snapshots);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(plan, PermissionStep(), 1, [], "{}"));

        Assert.Contains("snapshot", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(model.Requests);
        Assert.Equal(0, tool.Executions);
        snapshots.Release(callerId);
    }

    /// <summary>Explicit host continuation revalidates current authority after the caller's model request ends.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkillWorkflow_HostContinueAfterCallerReleaseUsesCurrentAuthority(bool allowTool)
    {
        var request = PermissionPlan().Request with { Selector = "review" };
        var tool = new PermissionProbeTool();
        var registry = new ToolRegistry([tool]);
        var selected = ModelProfileId.New();
        var snapshots = new ConversationToolSnapshotStore();
        var callerId = snapshots.Capture(
            request.SessionId,
            request.RunId,
            [registry.GetRegistration("permission_probe")],
            PermissionContext() with { ModelProfileId = selected, ModelReasoningLevel = "medium" });
        request = request with { CallerToolSnapshotId = callerId };
        var catalog = new SkillCatalog([new SkillCatalogSource(SkillScope.Maintained, MaintainedRoot(), "maintained", IsMaintained: true)]);
        await catalog.RefreshAsync();
        var verifier = new SkillPackageVerifier(new SkillTrustPolicySnapshot());
        var verified = await verifier.VerifyAsync(catalog.Resolve("review"));
        var candidate = verified with
        {
            Metadata = verified.Metadata with
            {
                Workflow = new SkillWorkflowDefinition
                {
                    WorkflowId = "caller-continuation",
                    Steps =
                    [
                        new SkillWorkflowStep { StepId = "ask", Kind = SkillWorkflowStepKind.AskUserInput },
                        Assert.Single(verified.Metadata.Workflow.Steps) with { DependsOn = ["ask"] },
                    ],
                },
            },
        };
        var model = new PermissionModelProvider { FinalText = "{\"succeeded\":true,\"delivery\":\"inline\",\"response\":\"Host continuation completed.\"}" };
        var sanitizer = new SecretOutputSanitizer();
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(
            registry,
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            sanitizer,
            NullLogger<ToolInvocationPipeline>.Instance);
        var sessionContextCalls = 0;
        var runner = new ModelSkillProcedureRunner(
            model,
            registry,
            pipeline,
            sanitizer,
            (_, _) =>
            {
                sessionContextCalls++;
                return Task.FromResult(PermissionContext() with
                {
                    AllowedToolIds = allowTool ? ["permission_probe"] : ["invoke_skill"],
                });
            },
            TestPromptLoader.Instance,
            snapshots: snapshots);
        await using var workflow = new SkillWorkflowOrchestrator(
            new FixedCatalog([candidate]),
            new PassThroughVerifier(),
            new CompatibleEvaluator { Profiles = [selected] },
            new SkillContentLoader(sanitizer, TestPromptLoader.Instance),
            new BoundedJsonSchemaValidator(),
            runner,
            TestPromptLoader.Instance,
            new InMemorySkillStateStore(),
            (_, _) => Task.FromResult(new SkillInvocationHostContext
            {
                Trust = RepositoryTrustLevel.TrustedRead,
                Phase = RunPhase.EvidenceCollection,
                ModelProfileId = selected,
            }),
            events,
            snapshots);
        var waiting = await workflow.InvokeAsync(request);
        Assert.Equal(SkillInvocationStatus.AwaitingHost, waiting.Status);
        Assert.Empty(model.Requests);
        snapshots.Release(callerId);

        var completed = await workflow.ContinueAsync(waiting.InvocationId, "{}");

        Assert.Equal(SkillInvocationStatus.Completed, completed.Status);
        Assert.True(sessionContextCalls > 0);
        Assert.Equal(allowTool ? 1 : 0, tool.Executions);
        Assert.Equal(allowTool, model.Requests[0].Tools.Any(item => item.Name == "permission_probe"));
        Assert.All(model.Requests, item => Assert.Equal(selected, item.ResolvedProfileId));
    }
}
