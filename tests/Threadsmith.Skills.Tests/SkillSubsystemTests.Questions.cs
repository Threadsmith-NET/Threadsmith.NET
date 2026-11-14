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
    /// <summary>Manual and nested procedures deliver clarification through the shared coordinator.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkillProcedure_ClarificationUsesSharedCoordinator(bool callerSnapshot)
    {
        var token = TestContext.Current.CancellationToken;
        const string arguments = """{"questions":[{"id":"q","prompt":"Which output?","options":[]}]}""";
        var plan = PermissionPlan() with
        {
            AvailableToolIds = ["ask_user"],
            EffectiveBudget = new SkillBudget { ModelTurns = 3, ToolCalls = 2 },
        };
        await using var events = new DomainEventStream();
        var steering = new RunSteeringCoordinator();
        if (callerSnapshot)
        {
            steering.RegisterRun(plan.Request.SessionId, plan.Request.RunId);
        }

        using var questions = new UserQuestionCoordinator(events, steering);
        var presentations = 0;
        questions.Attach((request, _) =>
        {
            presentations++;
            return Task.FromResult(new UserQuestionResult(UserQuestionStatus.Answered, [new(request.Questions[0].Id, null, "Use JSON")]));
        });
        var context = PermissionContext() with { UserQuestions = questions };
        var registry = new ToolRegistry([new AskUserTool(TestPromptLoader.Instance)]);
        var snapshots = new ConversationToolSnapshotStore();
        if (callerSnapshot)
        {
            var snapshot = snapshots.Capture(plan.Request.SessionId, plan.Request.RunId, [registry.GetRegistration("ask_user")], context);
            plan = plan with { Request = plan.Request with { CallerToolSnapshotId = snapshot } };
        }

        var model = new SkillBatchModelProvider
        {
            Calls = [new("ask_user", arguments)],
            AdditionalBatches = [[new("ask_user", arguments.Replace("Which output?", "Which format?", StringComparison.Ordinal))]],
        };
        var sanitizer = new SecretOutputSanitizer();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var runner = new ModelSkillProcedureRunner(model, registry, pipeline, sanitizer, (_, _) => Task.FromResult(context), TestPromptLoader.Instance, snapshots: snapshots, questions: questions);

        await runner.RunAsync(plan, PermissionStep(), 1, [], "{}", token);

        Assert.Equal(2, presentations);
        Assert.Equal(3, model.Requests.Count);
        var delivered = model.Requests[2].Messages.Where(message => message.Role == ModelMessageRole.Tool).ToArray();
        Assert.Equal(2, delivered.Length);
        Assert.All(delivered, message =>
        {
            Assert.False(message.IsError);
            Assert.Contains("Use JSON", message.GetModelVisibleContent(), StringComparison.Ordinal);
        });
        Assert.Contains(model.Requests[0].Tools, tool => tool.Name == "ask_user");

        // A borrowed run remains active; an owned manual run has been closed.
        if (!callerSnapshot)
        {
            Assert.False(steering.TryBeginQuestion(plan.Request.SessionId, plan.Request.RunId));
            steering.RegisterRun(plan.Request.SessionId, plan.Request.RunId);
        }

        // Delivery releases the same run's pending question slot.
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
