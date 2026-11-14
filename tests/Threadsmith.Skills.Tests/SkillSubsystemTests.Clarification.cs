namespace Threadsmith.Skills.Tests;

using Microsoft.Extensions.Configuration;
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
    private const string ClarificationArguments = """{"questions":[{"id":"q","prompt":"Which approval mode?","options":[]}]}""";

    /// <summary>Nested human input releases admission, preserves cancellation and joins presentation.</summary>
    [Theory]
    [InlineData("answer")]
    [InlineData("cancel")]
    [InlineData("repository")]
    public async Task SkillClarification_ReleasesAncestorAdmissionAndReenters(string outcome)
    {
        var token = TestContext.Current.CancellationToken;
        using var package = TemporaryPackage.CopyMaintained("review");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var plan = PermissionPlan() with
        {
            AvailableToolIds = ["ask_user"],
            Compatibility = new SkillCompatibilityResult { IsCompatible = true, AvailableRequiredTools = ["ask_user"] },
        };
        await using var events = new DomainEventStream();
        var steering = new RunSteeringCoordinator();
        steering.RegisterRun(plan.Request.SessionId, plan.Request.RunId);
        using var questions = new UserQuestionCoordinator(events, steering);
        var presenting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<UserQuestionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var presenterJoined = false;
        questions.Attach(async (_, questionToken) =>
        {
            presenting.TrySetResult();
            try
            {
                return await answer.Task.WaitAsync(questionToken);
            }
            finally
            {
                presenterJoined = true;
            }
        });
        var snapshots = new ConversationToolSnapshotStore();
        var model = new SkillBatchModelProvider { Calls = [new("ask_user", ClarificationArguments)] };
        var sanitizer = new SecretOutputSanitizer();
        var hostTool = new ClarificationHostTool();
        var askUser = new AskUserTool(TestPromptLoader.Instance);
        var configurationPath = Path.Combine(package.Root, ".threadsmith", "config.json");
        var state = new ToolStateManager(
            [askUser.Definition, hostTool.Definition],
            new ConfigurationBuilder().Build(),
            configurationPath,
            Path.Combine(package.Root, "consent.json"),
            Path.Combine(package.Root, "mcp-approvals.json"));
        var registry = new ToolRegistry([askUser, hostTool], state);
        Assert.DoesNotContain(registry.Definitions, definition => definition.Id == "ask_user");
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var context = PermissionContext() with { UserQuestions = questions };
        var runner = new ModelSkillProcedureRunner(model, registry, pipeline, sanitizer, (_, _) => Task.FromResult(context), TestPromptLoader.Instance, snapshots: snapshots, questions: questions);
        hostTool.Run = async execution =>
        {
            var snapshot = snapshots.Capture(execution.SessionId, execution.RunId, [], context);
            try
            {
                await runner.RunAsync(plan with { Request = plan.Request with { CallerToolSnapshotId = snapshot } }, PermissionStep(), 1, [], "{}", cancellation.Token);
            }
            finally
            {
                snapshots.Release(snapshot);
            }
        };
        var request = new ToolInvocationRequest
        {
            SessionId = plan.Request.SessionId,
            RunId = plan.Request.RunId,
            ToolId = "invoke_skill",
            ArgumentsJson = "{}",
            Context = context,
        };

        var running = pipeline.InvokeAsync(request, token);
        try
        {
            await presenting.Task.WaitAsync(token);
            Assert.False(running.IsCompleted);
            Assert.Single(model.Requests);

            // This same-source call can finish only if the enclosing invocation released its cap.
            var probe = await pipeline.InvokeAsync(request with { ArgumentsJson = "{\"probe\":true}" }, token);
            Assert.True(probe.Succeeded, probe.Error);
            if (outcome == "cancel")
            {
                await cancellation.CancelAsync();
            }
            else
            {
                if (outcome == "repository")
                {
                    context = context with { RepositoryPath = package.Root };
                }

                answer.TrySetResult(new(UserQuestionStatus.Cancelled, []));
            }
        }
        finally
        {
            answer.TrySetResult(new(UserQuestionStatus.Cancelled, []));
        }

        if (outcome == "cancel")
        {
            var result = await running;
            Assert.False(result.Succeeded);
            Assert.False(token.IsCancellationRequested);
            Assert.Single(model.Requests);
        }
        else if (outcome == "repository")
        {
            var result = await running;
            Assert.False(result.Succeeded);
            Assert.Single(model.Requests);
        }
        else
        {
            var result = await running;
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(2, model.Requests.Count);
            var delivered = Assert.Single(model.Requests[1].Messages, message => message.Role == ModelMessageRole.Tool);
            Assert.Contains("The user declined to answer", delivered.GetModelVisibleContent(), StringComparison.Ordinal);
        }

        Assert.True(presenterJoined);
        Assert.False(state.IsEnabled("ask_user"));
        Assert.DoesNotContain(registry.Definitions, definition => definition.Id == "ask_user");
        Assert.False(File.Exists(configurationPath));
        Assert.True(steering.TryBeginQuestion(plan.Request.SessionId, plan.Request.RunId));
        steering.EndQuestion(plan.Request.SessionId, plan.Request.RunId);
        var nextQuestion = await pipeline.InvokeAsync(
            request with
            {
                ToolId = "ask_user",
                ArgumentsJson = ClarificationArguments,
                Context = PermissionContext() with { UserQuestions = questions, RequiredSkillToolIds = ["ask_user"] },
            },
            token);
        Assert.NotNull(nextQuestion.Suspension);
        await questions.EndRunAsync(plan.Request.SessionId, plan.Request.RunId, token);
    }

    /// <summary>Required dependencies do not grant clarification authority.</summary>
    [Theory]
    [InlineData("deny")]
    [InlineData("allowlist")]
    [InlineData("deny-all")]
    [InlineData("child")]
    [InlineData("unattached")]
    public async Task SkillClarification_RequiredToolCannotBypassCallerRestrictions(string restriction)
    {
        var plan = PermissionPlan() with
        {
            AvailableToolIds = ["ask_user"],
            Compatibility = new SkillCompatibilityResult { IsCompatible = true, AvailableRequiredTools = ["ask_user"] },
        };
        await using var events = new DomainEventStream();
        using var questions = new UserQuestionCoordinator(events, new RunSteeringCoordinator());
        if (restriction != "unattached")
        {
            questions.Attach((_, _) => throw new InvalidOperationException("A denied question must not be presented."));
        }

        var context = PermissionContext() with
        {
            UserQuestions = questions,
            DeniedToolIds = restriction == "deny" ? ["ask_user"] : [],
            AllowedToolIds = restriction == "allowlist" ? ["read_file"] : [],
            DenyAllTools = restriction == "deny-all",
            IsChildAgent = restriction == "child",
        };
        var model = new SkillBatchModelProvider { Calls = [new("ask_user", ClarificationArguments)] };
        var sanitizer = new SecretOutputSanitizer();
        var registry = new ToolRegistry([new AskUserTool(TestPromptLoader.Instance)]);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
        var runner = new ModelSkillProcedureRunner(model, registry, pipeline, sanitizer, (_, _) => Task.FromResult(context), TestPromptLoader.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(plan, PermissionStep(), 1, [], "{}", TestContext.Current.CancellationToken));

        Assert.Empty(model.Requests);
    }

    /// <summary>Outer sibling tools cannot begin alongside a potentially interactive skill.</summary>
    [Fact]
    public async Task SkillClarification_OuterSkillBatchRejectsSiblingsBeforeExecution()
    {
        var registry = new ToolRegistry([new ClarificationHostTool(), new PermissionProbeTool()]);
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance);
        var request = new ToolInvocationRequest { SessionId = SessionId.New(), RunId = RunId.New(), ToolId = "invoke_skill", ArgumentsJson = "{}", Context = PermissionContext() };

        var result = pipeline.PreflightBatch([new(0, "skill", request), new(1, "other", request with { ToolId = "permission_probe" })]);

        Assert.False(result.Succeeded);
        Assert.Contains("sole call", result.SafeReason, StringComparison.Ordinal);
    }

    private sealed record ClarificationHostInput(bool Probe = false);

    private sealed record ClarificationHostOutput;

    private sealed class ClarificationHostTool : Tool<ClarificationHostInput, ClarificationHostOutput>
    {
        public Func<ToolExecutionContext, Task>? Run { get; set; }

        public override ToolDefinition Definition { get; } = new()
        {
            Id = "invoke_skill",
            DisplayName = "Skill host probe",
            Version = "1.0.0",
            Description = "Exercise the normal enclosing invocation lifecycle.",
            InputSchema = new ToolSchema("input", 1, "{\"type\":\"object\",\"properties\":{\"probe\":{\"type\":\"boolean\"}}}"),
            OutputSchema = new ToolSchema("output", 1, "{\"type\":\"object\"}"),
            Timeout = Timeout.InfiniteTimeSpan,
            MaximumOutputBytes = 1024,
            Scheduling = new ToolSchedulingDescriptor { MaximumSourceConcurrency = 1 },
        };

        public override async Task<ToolExecution<ClarificationHostOutput>> ExecuteAsync(ClarificationHostInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            if (!input.Probe)
            {
                await (Run ?? throw new InvalidOperationException("The runner was not attached."))(context);
            }

            return new(new(), []);
        }

        protected override void ValidateInput(ClarificationHostInput input)
        {
        }
    }
}
