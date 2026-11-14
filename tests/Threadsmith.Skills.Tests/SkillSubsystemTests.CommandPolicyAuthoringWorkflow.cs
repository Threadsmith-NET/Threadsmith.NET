namespace Threadsmith.Skills.Tests;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Cli;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>The real package rejects unavailable interaction and continues through shared commands after an answer or safe retry.</summary>
    [Theory]
    [InlineData("answer")]
    [InlineData("after-mismatch")]
    [InlineData("decline")]
    [InlineData("headless-retry")]
    public static async Task CommandPolicyAuthoring_WorkflowPreservesExamplesAndEvidenceAcrossClarification(string scenario)
    {
        // Arrange the unchanged maintained package, real compatibility and command handlers.
        var token = TestContext.Current.CancellationToken;
        using var package = TemporaryPackage.CopyMaintained("command-policy-authoring");
        var catalog = package.CreateCatalog(SkillScope.Maintained);
        await catalog.RefreshAsync(token);
        var state = new InMemorySkillStateStore();
        var policy = new FileSkillTrustPolicyProvider(Path.Combine(package.Root, "trust.json"), new SkillTrustPolicySnapshot());
        var verifier = new SkillPackageVerifier(policy);
        var registry = new ToolRegistry([new ValidateCommandPolicyTool(TestPromptLoader.Instance), new AskUserTool(TestPromptLoader.Instance)]);
        var profile = RoutingProfile(ModelProfileId.New(), "authoring-integration", 64_000, 8192, true);
        var models = new ConfiguredModelCatalog([profile]);
        var compatibility = new SkillCompatibilityEvaluator(registry, models, "1.0.0");
        var clock = new FakeTimeProvider(DateTimeOffset.UnixEpoch);
        var sanitizer = new SecretOutputSanitizer();
        await using var events = new DomainEventStream();
        var steering = new RunSteeringCoordinator();
        using var questions = new UserQuestionCoordinator(events, steering);
        var context = PermissionContext() with { RepositoryPath = package.Root, UserQuestions = questions };
        var model = new AuthoringLoopModel(package.Root)
        {
            Ending = scenario == "decline" ? "decline" : "normal",
            ClarifyAfterMismatch = scenario == "after-mismatch",
        };
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, timeProvider: clock);
        var runner = new ModelSkillProcedureRunner(model, registry, pipeline, sanitizer, (_, _) => Task.FromResult(context), TestPromptLoader.Instance, catalog: models, questions: questions);
        await using var workflow = new SkillWorkflowOrchestrator(
            catalog,
            verifier,
            compatibility,
            new SkillContentLoader(sanitizer, TestPromptLoader.Instance),
            new BoundedJsonSchemaValidator(),
            runner,
            TestPromptLoader.Instance,
            state,
            (_, _) => Task.FromResult(new SkillInvocationHostContext
            {
                Trust = RepositoryTrustLevel.TrustedRead,
                Phase = RunPhase.EvidenceCollection,
                RepositoryPath = package.Root,
                ModelProfileId = profile.Id,
            }),
            events,
            timeProvider: clock);
        var application = new SkillApplication(
            catalog,
            verifier,
            policy,
            compatibility,
            workflow,
            state,
            new SkillPackageInstaller(Path.Combine(package.Root, "content"), Path.Combine(package.Root, "quarantine")));
        await using var writer = new StringWriter();
        var shell = new HeadlessShell(new CommandDispatcher([application]), new InMemoryProjectionStore(), writer, timeProvider: clock);
        var example = AuthoringRequest(package.Root, "Allow").Examples[0];
        var request = PermissionPlan().Request with
        {
            Selector = "command-policy-authoring",
            InputJson = JsonSerializer.Serialize(new
            {
                request = "Allow the supplied echo example, without a directory policy restriction.",
                examples = new[] { new { command = example.Command, expectedOutcome = example.ExpectedOutcome } },
            }),
            HostBudget = new SkillBudget(),
        };

        if (scenario == "headless-retry")
        {
            // No presenter: required interaction is unavailable even for otherwise complete input.
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => shell.InvokeSkillAsync(request, token));
            Assert.Contains("required skill tool", error.Message, StringComparison.Ordinal);
            Assert.Empty(model.Requests);
            var failed = Assert.IsType<SkillWorkflowCheckpoint>(await shell.GetSkillInvocationAsync(request.InvocationId, token));
            Assert.Equal(SkillInvocationStatus.Failed, failed.Status);
            Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(request.InputJson), JsonSerializer.Deserialize<JsonElement>(failed.InputJson)));
            Assert.Empty(failed.Steps.SelectMany(step => step.SideEffects));
        }

        var presenting = new TaskCompletionSource<UserQuestionRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<UserQuestionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        questions.Attach(async (question, questionToken) =>
        {
            presenting.SetResult(question);
            return await answer.Task.WaitAsync(questionToken);
        });

        // Act: continue the same procedure after an explicit barrier, or retry its safe failed checkpoint.
        var running = scenario == "headless-retry"
            ? shell.ResumeSkillAsync(request.InvocationId, token)
            : shell.InvokeSkillAsync(request, token);
        SkillInvocationResult result;
        try
        {
            var reached = await Task.WhenAny(presenting.Task, running).WaitAsync(token);
            if (reached == running)
            {
                await running;
                Assert.Fail("The authoring workflow completed before presenting its required clarification.");
            }

            var question = await presenting.Task.WaitAsync(token);
            Assert.False(running.IsCompleted);
            Assert.Equal(scenario == "after-mismatch" ? 2 : 1, model.Requests.Count);
            answer.SetResult(new UserQuestionResult(
                scenario == "decline" ? UserQuestionStatus.Cancelled : UserQuestionStatus.Answered,
                scenario == "decline" ? [] : [new(question.Questions[0].Id, null, "Keep the supplied Allow example unchanged.")]));
            result = await running;
        }
        finally
        {
            answer.TrySetResult(new(UserQuestionStatus.Cancelled, []));
            await running;
        }

        // Assert authoritative output and persisted state, not just the scripted model's prose.
        Assert.Equal(SkillInvocationStatus.Completed, result.Status);
        Assert.Equal(request.InvocationId, result.InvocationId);
        using var output = JsonDocument.Parse(Assert.IsType<string>(result.OutputJson));
        Assert.Equal(scenario == "decline" ? "incomplete" : "validated", output.RootElement.GetProperty("status").GetString());
        var checkpoint = Assert.IsType<SkillWorkflowCheckpoint>(await shell.GetSkillInvocationAsync(request.InvocationId, token));
        Assert.Equal(SkillInvocationStatus.Completed, checkpoint.Status);
        Assert.Equal(scenario == "headless-retry" ? 2 : 1, checkpoint.Attempt);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(request.InputJson), JsonSerializer.Deserialize<JsonElement>(checkpoint.InputJson)));
        var completed = Assert.Single(checkpoint.Steps);
        Assert.True(completed.Succeeded);
        Assert.Empty(completed.SideEffects);
        Assert.Equal(scenario == "decline" ? 2 : 4, model.Requests.Count);
        var delivered = Assert.Single(model.Requests[^1].Messages, message => message.Role == ModelMessageRole.Tool && message.ToolName == "ask_user");
        Assert.Contains(scenario == "decline" ? "The user declined to answer" : "Keep the supplied Allow example unchanged.", delivered.GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.All(model.Requests, modelRequest => Assert.Equal(profile.Id, modelRequest.ResolvedProfileId));
        if (scenario == "decline")
        {
            Assert.NotEmpty(output.RootElement.GetProperty("outstandingItems").EnumerateArray());
            Assert.DoesNotContain(model.Requests[^1].Messages, message => message.ToolName == "validate_command_policy");
        }
        else
        {
            var observations = model.Requests[^1].Messages
                .Where(message => message.Role == ModelMessageRole.Tool && message.ToolName == "validate_command_policy")
                .Select(message => JsonSerializer.Deserialize<CommandPolicyValidationResult>(message.GetModelVisibleContent(), AuthoringJsonOptions)!)
                .ToArray();
            Assert.Equal(2, observations.Length);
            Assert.False(observations[0].IsValidated);
            Assert.Equal("AlwaysDeny", Assert.Single(observations[0].Results).ActualOutcome);
            Assert.True(observations[1].IsValidated);
            var validation = output.RootElement.GetProperty("validation");
            Assert.Equal(observations[1].CandidateDigest, validation.GetProperty("candidateDigest").GetString());
            Assert.Equal(observations[1].ExampleSetId, validation.GetProperty("exampleSetId").GetString());
            var checkedExample = Assert.Single(validation.GetProperty("results").EnumerateArray());
            Assert.Equal(example.Command, checkedExample.GetProperty("command").GetString());
            Assert.Equal(example.ExpectedOutcome, checkedExample.GetProperty("expectedOutcome").GetString());
            Assert.Equal("Allow", checkedExample.GetProperty("actualOutcome").GetString());
            Assert.True(checkedExample.GetProperty("matched").GetBoolean());
            Assert.Equal(AuthoringRequest(package.Root, "Allow").PolicyJson, output.RootElement.GetProperty("policyJson").GetString());
            Assert.Equal(observations[1].Warnings, output.RootElement.GetProperty("warnings").EnumerateArray().Select(item => Assert.IsType<string>(item.GetString())).ToArray());
        }

        Assert.False(steering.TryBeginQuestion(request.SessionId, request.RunId));
    }
}
