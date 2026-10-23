namespace Threadsmith.Skills.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>Nested procedures cannot advertise or occupy the parent's clarification channel.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkillProcedure_CannotInheritQuestionAdmission(bool callerSnapshot)
    {
        var token = TestContext.Current.CancellationToken;
        const string arguments = """{"questions":[{"id":"q","prompt":"Which output?","options":[]}]}""";
        var plan = PermissionPlan() with { AvailableToolIds = ["ask_user"] };
        await using var events = new DomainEventStream();
        var steering = new RunSteeringCoordinator();
        steering.RegisterRun(plan.Request.SessionId, plan.Request.RunId);
        using var questions = new UserQuestionCoordinator(events, steering);
        questions.Attach((_, _) => throw new InvalidOperationException("Nested questions must not be presented."));
        var context = PermissionContext() with { UserQuestions = questions };
        var registry = new ToolRegistry([new AskUserTool(TestPromptLoader.Instance)]);
        var snapshots = new ConversationToolSnapshotStore();
        if (callerSnapshot)
        {
            var snapshot = snapshots.Capture(plan.Request.SessionId, plan.Request.RunId, [registry.GetRegistration("ask_user")], context);
            plan = plan with { Request = plan.Request with { CallerToolSnapshotId = snapshot } };
        }

        var model = new SkillBatchModelProvider { Calls = [new("ask_user", arguments)] };
        var sanitizer = new SecretOutputSanitizer();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
        var runner = new ModelSkillProcedureRunner(model, registry, pipeline, sanitizer, (_, _) => Task.FromResult(context), TestPromptLoader.Instance, snapshots: snapshots);

        await runner.RunAsync(plan, PermissionStep(), 1, [], "{}", token);

        Assert.All(model.Requests, request => Assert.DoesNotContain(request.Tools, tool => tool.Name == "ask_user"));
        Assert.Equal(2, model.Requests.Count);
        Assert.Single(model.Requests[1].Messages, message => message.Role == ModelMessageRole.Tool && message.IsError == true);

        // A forced nested call must not occupy the parent's sole pending question slot.
        var parent = await pipeline.InvokeAsync(
            new ToolInvocationRequest
            {
                SessionId = plan.Request.SessionId,
                RunId = plan.Request.RunId,
                ToolId = "ask_user",
                ArgumentsJson = arguments,
                Context = context,
            },
            token);
        Assert.NotNull(parent.Suspension);
        await questions.EndRunAsync(plan.Request.SessionId, plan.Request.RunId, token);
        if (plan.Request.CallerToolSnapshotId is { } callerId)
        {
            snapshots.Release(callerId);
        }
    }
}
