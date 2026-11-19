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
