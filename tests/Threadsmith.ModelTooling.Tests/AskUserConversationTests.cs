namespace Threadsmith.ModelTooling.Tests;

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises clarification through the ordinary model and tool execution path.</summary>
public static class AskUserConversationTests
{
    private const string Arguments = """{"questions":[{"id":"q","prompt":"Which output?","options":[{"id":"a","label":"First"},{"id":"b","label":"Second"}]}]}""";

    /// <summary>Suspended answers obey the admitted byte ceiling before and after sanitization.</summary>
    [Theory]
    [InlineData(false, -1)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(false, 0, true)]
    [InlineData(true, 0, true)]
    public static async Task CompletionEnforcesCapturedEffectiveOutputLimit(bool securityCeiling, int extraBytes, bool expandSanitizedOutput = false)
    {
        var token = TestContext.Current.CancellationToken;
        var answer = new UserQuestionResult(UserQuestionStatus.Answered, [new("q", null, new string('é', 200))]);
        var limit = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(answer)) + extraBytes;
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((value, _) =>
        {
            observed.Enqueue(value);
            return Task.CompletedTask;
        });
        var steering = new RunSteeringCoordinator();
        using var questions = new UserQuestionCoordinator(events, steering);
        questions.Attach((_, _) => Task.FromResult(answer));
        var session = SessionId.New();
        var run = RunId.New();
        steering.RegisterRun(session, run);
        var context = CreateContext() with
        {
            UserQuestions = questions,
            SecurityPolicy = securityCeiling ? new EffectiveSessionPolicy
            {
                BindingId = Guid.NewGuid(),
                Generation = 1,
                RepositoryPath = Path.TrimEndingDirectorySeparator(Path.GetTempPath()),
                Fingerprint = "question-output-test",
                ResourceLimits = ImmutableDictionary<string, int>.Empty.Add("tools:runtime:defaults:maximumOutputBytes", limit),
            }
            : null,
        };
        var registry = new ToolRegistry([new AskUserTool(TestPromptLoader.Instance)], runtimeOptions: new ToolRuntimeOptions
        {
            Defaults = new ToolRuntimeOverride { MaximumOutputBytes = securityCeiling ? 8192 : limit },
        });
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, expandSanitizedOutput ? new ExpandingSanitizer() : new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance);
        var request = new ToolInvocationRequest { SessionId = session, RunId = run, ToolId = "ask_user", ArgumentsJson = Arguments, Context = context };
        var suspended = await pipeline.InvokeAsync(request, token);
        Assert.True(suspended.Suspension is not null, suspended.Error);
        Assert.NotNull(suspended.Suspension);
        UserQuestionValidation.ValidateAnswers(suspended.Suspension.Questions, answer.Answers);

        // Completion must retain the admitted ceiling even without the original policy context.
        var result = await pipeline.CompleteSuspensionAsync(request with { Context = CreateContext() }, suspended, answer, token);

        var completed = Assert.Single(observed.OfType<ToolInvocationCompleted>());
        Assert.Equal(suspended.ToolInvocationId, result.ToolInvocationId);
        Assert.Null(result.Suspension);
        Assert.Equal(extraBytes >= 0 && !expandSanitizedOutput, result.Succeeded);
        Assert.Equal(result.Succeeded, completed.Succeeded);
        if (extraBytes < 0 || expandSanitizedOutput)
        {
            Assert.Equal(ToolErrorClassification.OutputLimitExceeded, result.ErrorClassification);
            Assert.True(result.IsTruncated);
            Assert.True(completed.IsTruncated);
            Assert.Null(result.ResultJson);
            Assert.Null(completed.ResultJson);
            Assert.Equal(OperationActivityOutcome.Failed, completed.Outcome);
        }
        else
        {
            Assert.Equal(JsonSerializer.Serialize(answer), result.ResultJson);
        }

        await questions.EndRunAsync(session, run, token);
    }

    /// <summary>Human waits retain correlation without retaining execution resources.</summary>
    [Theory]
    [InlineData(false, false, false, "ask_user")]
    [InlineData(true, false, false, "ask_user")]
    [InlineData(false, true, false, "ask_user")]
    [InlineData(true, true, false, "ask_user")]
    [InlineData(false, false, true, "ask_user")]
    [InlineData(false, true, true, "ask_user")]
    [InlineData(false, false, false, "ASK_USER")]
    [InlineData(true, false, false, "ASK_USER")]
    [InlineData(false, true, false, "ASK_USER")]
    [InlineData(true, true, false, "ASK_USER")]
    [InlineData(false, false, true, "ASK_USER")]
    [InlineData(false, true, true, "ASK_USER")]
    [InlineData(false, false, false, "AsK_UsEr")]
    [InlineData(true, false, false, "AsK_UsEr")]
    [InlineData(false, true, false, "AsK_UsEr")]
    [InlineData(true, true, false, "AsK_UsEr")]
    [InlineData(false, false, true, "AsK_UsEr")]
    [InlineData(false, true, true, "AsK_UsEr")]
    public static async Task LiveWaitReleasesResourcesAndResumesOriginalCallExactlyOnce(bool cancelled, bool native, bool stopRun, string toolName)
    {
        var token = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = events.Subscribe((value, _) =>
        {
            observed.Enqueue(value);
            return Task.CompletedTask;
        });
        var sanitizer = new SecretOutputSanitizer();
        var prompts = TestPromptLoader.Instance;
        var evidence = new EvidenceStore(events, sanitizer);
        var profile = new ModelProfile
        {
            Id = new ModelProfileId(Guid.NewGuid()),
            Name = "fixture",
            Provider = "fixture",
            ModelId = "fixture",
            Endpoint = new Uri("https://example.test/v1/"),
            ContextWindow = 200000,
            MaximumOutputTokens = 4096,
            SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true, StructuredOutput = true },
        };
        var resolver = new ModelResolver(new ConfiguredModelCatalog([profile]), new InMemoryModelPreferenceSnapshotProvider());
        var assembler = new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, events, prompts, modelResolver: resolver);
        var budget = new ExecutionBudget(new BudgetDimensions(100000, 20, TimeSpan.MaxValue));
        var registry = new ToolRegistry([new AskUserTool(prompts), new DateTimeTool(prompts)]);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, budget);
        var model = new QuestionProvider { Native = native, ToolName = toolName };
        var steering = new RunSteeringCoordinator();
        using var questions = new UserQuestionCoordinator(events, steering);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<UserQuestionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        questions.Attach(async (request, cancellationToken) =>
        {
            Assert.True(model.StreamDisposed);
            var independent = await pipeline.InvokeAsync(
                new ToolInvocationRequest
                {
                    SessionId = request.SessionId,
                    RunId = request.RunId,
                    ToolId = "datetime",
                    ArgumentsJson = "{}",
                    Context = CreateContext(),
                },
                cancellationToken);
            Assert.True(independent.Succeeded, independent.Error);
            ready.TrySetResult();
            return await answer.Task.WaitAsync(cancellationToken);
        });
        var application = new SessionApplication(events, model, budget, sanitizer, NullLogger<SessionApplication>.Instance, pipeline, (_, _) => Task.FromResult(CreateContext()), assembler, evidence, registry, steering: steering, correctiveMessages: new CorrectiveMessageFactory(prompts), prompts: prompts, userQuestions: questions);
        var dispatcher = new CommandDispatcher([application]);
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("Question test"), token);
        var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, "Ask which output to use."), token);
        var completedRun = dispatcher.DispatchAsync(new WaitForRunCommand(run), token);
        var first = await Task.WhenAny(ready.Task, completedRun).WaitAsync(token);
        Assert.True(first == ready.Task, string.Join("\n", observed.OfType<DiagnosticObserved>().Select(value => value.Message)));
        Assert.Single(model.Requests);
        Assert.DoesNotContain(observed.OfType<ToolInvocationCompleted>(), value => value.ToolInvocationId == observed.OfType<UserQuestionStateChanged>().First().Request.ToolInvocationId);
        if (stopRun)
        {
            Assert.True(await dispatcher.DispatchAsync(new CancelRunCommand(session, run), token));
            try
            {
                await completedRun;
                Assert.Fail("The cancelled run completed normally.");
            }
            catch (OperationCanceledException)
            {
                Assert.True(completedRun.IsCanceled);
            }

            Assert.Single(model.Requests);
            Assert.Equal(UserQuestionStatus.Interrupted, observed.OfType<UserQuestionStateChanged>().Last().Result.Status);
            var questionId = observed.OfType<UserQuestionStateChanged>().First().Request.ToolInvocationId;
            Assert.False(Assert.Single(observed.OfType<ToolInvocationCompleted>(), value => value.ToolInvocationId == questionId).Succeeded);
            return;
        }

        answer.TrySetResult(cancelled ? new(UserQuestionStatus.Cancelled, []) : new(UserQuestionStatus.Answered, [new("q", "a", null)]));
        Assert.True(await completedRun);
        Assert.Equal(2, model.Requests.Count);
        var result = Assert.Single(model.Requests[1].Messages, message => message.Role == ModelMessageRole.Tool && message.ToolName == toolName);
        Assert.NotNull(result.ToolCallId);
        Assert.Contains(model.Requests[1].Messages, message => message.ToolCallId == result.ToolCallId && message.Role != ModelMessageRole.Tool);
        var parsed = JsonSerializer.Deserialize<UserQuestionResult>(result.GetModelVisibleContent());
        Assert.NotNull(parsed);
        Assert.Equal(cancelled ? UserQuestionStatus.Cancelled : UserQuestionStatus.Answered, parsed.Status);
        Assert.Equal(cancelled ? "The user declined to answer" : null, parsed.Message);
        Assert.Empty(observed.OfType<ModelCorrectionAttempted>());
        Assert.True(observed.OfType<UserQuestionStateChanged>().Last().Consumed);
        var invocationId = observed.OfType<UserQuestionStateChanged>().First().Request.ToolInvocationId;
        Assert.True(Assert.Single(observed.OfType<ToolInvocationCompleted>(), value => value.ToolInvocationId == invocationId).Succeeded);
    }

    /// <summary>Batch and caller admission reject unsupported waits before registration.</summary>
    [Fact]
    public static async Task MixedBatchAndUnsupportedCallersCannotAdmitQuestions()
    {
        var token = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var registry = new ToolRegistry([new AskUserTool(TestPromptLoader.Instance), new DateTimeTool(TestPromptLoader.Instance)]);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance);
        var request = new ToolInvocationRequest { SessionId = SessionId.New(), RunId = RunId.New(), ToolId = "ask_user", ArgumentsJson = Arguments, Context = CreateContext() };
        var preflight = pipeline.PreflightBatch([new(0, "q", request), new(1, "effect", request with { ToolId = "datetime", ArgumentsJson = "{}" })]);
        Assert.False(preflight.Succeeded);
        Assert.Contains("sole call", preflight.SafeReason, StringComparison.Ordinal);
        var forced = await pipeline.InvokeAsync(request, token);
        Assert.False(forced.Succeeded);
        Assert.Null(forced.Suspension);
        var child = await pipeline.InvokeAsync(request with { Context = request.Context with { IsChildAgent = true } }, token);
        Assert.False(child.Succeeded);
        Assert.False(new AskUserTool(TestPromptLoader.Instance).Definition.SubagentAvailable);
    }

    /// <summary>The common tool switch controls admission, persists, and cannot weaken ordinary policy.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task CommonToggleControlsAdvertisementAndForcedInvocation(bool enabled)
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "question-toggle-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, ".threadsmith", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var tool = new AskUserTool(TestPromptLoader.Instance);
            Assert.False(tool.Definition.EnabledByDefault);
            var state = new ToolStateManager([tool.Definition], new ConfigurationBuilder().Build(), path);
            Assert.False(Assert.Single(state.GetAllStates()).Enabled);
            Assert.Contains("Space", Assert.Single(state.GetAllStates()).AvailabilityReason, StringComparison.Ordinal);
            if (enabled)
            {
                await state.EnableAsync("ask_user", token);
            }

            await using var events = new DomainEventStream();
            var steering = new RunSteeringCoordinator();
            using var questions = new UserQuestionCoordinator(events, steering);
            questions.Attach((_, _) => Task.FromResult(new UserQuestionResult(UserQuestionStatus.Cancelled, [])));
            var context = CreateContext() with { UserQuestions = questions };
            var registry = new ToolRegistry([tool], state);
            var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance);
            var session = SessionId.New();
            var run = RunId.New();
            steering.RegisterRun(session, run);
            var snapshot = ConversationToolAvailability.CreateSnapshot(pipeline, registry, session, run, context, false);
            Assert.Equal(enabled, snapshot.Definitions.Any(definition => definition.Id == "ask_user"));
            var request = new ToolInvocationRequest { SessionId = session, RunId = run, ToolId = "ask_user", ArgumentsJson = Arguments, Context = context };
            var denied = await pipeline.InvokeAsync(request with { Context = context with { DenyAllTools = true } }, token);
            Assert.False(denied.Succeeded);
            Assert.Null(denied.Suspension);
            var result = await pipeline.InvokeAsync(request, token);
            Assert.Equal(enabled, result.Suspension is not null);
            if (!enabled)
            {
                Assert.False(result.Succeeded);
            }
            else
            {
                var cancellation = new UserQuestionResult(UserQuestionStatus.Cancelled, []);
                await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.CompleteSuspensionAsync(request with { ToolId = "datetime" }, result, cancellation, token));
                await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.CompleteSuspensionAsync(request with { RunId = RunId.New() }, result, cancellation, token));
                await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.CompleteSuspensionAsync(request with { SessionId = SessionId.New() }, result, cancellation, token));
            }

            await questions.EndRunAsync(session, run, token);
            Assert.Equal(enabled, Assert.Single(state.GetAllStates()).Enabled);
            if (enabled)
            {
                var reload = new ToolStateManager([tool.Definition], new ConfigurationBuilder().AddJsonFile(path).Build(), path);
                Assert.True(reload.IsEnabled("ask_user"));
            }

            await state.DisableAsync("ask_user", token);
            Assert.Empty(registry.Definitions);
            Assert.False(new ToolStateManager([tool.Definition], new ConfigurationBuilder().AddJsonFile(path).Build(), path).IsEnabled("ask_user"));
            await state.EnableAsync("ask_user", token);
            Assert.Null(Assert.Single(state.GetAllStates()).AvailabilityReason);
            await state.DisableAsync("ask_user", token);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ToolInvocationContext CreateContext() => new() { RepositoryPath = Path.GetTempPath(), TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model" };

    private sealed class ExpandingSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value) => value.Replace("é", "[redacted]", StringComparison.Ordinal);
    }

    private sealed class QuestionProvider : IModelProvider
    {
        internal bool Native { get; init; }

        internal string ToolName { get; init; } = "ask_user";

        internal List<ModelStreamRequest> Requests { get; } = [];

        internal bool StreamDisposed { get; private set; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            try
            {
                if (Requests.Count == 1)
                {
                    Assert.Contains(request.Tools, tool => tool.Name == "ask_user");
                    if (Native)
                    {
                        yield return new ModelChunk
                        {
                            ResponseEnvelope = new ModelResponseReplayEnvelope(
                                new ModelReplayBinding
                                {
                                    ProviderId = "fixture",
                                    ModelId = "fixture",
                                    ProfileId = request.ResolvedProfileId ?? new ModelProfileId(Guid.NewGuid()),
                                    RunId = request.RunId,
                                    ModelRound = request.ToolContinuationRound,
                                    HistoryRewriteGeneration = request.HistoryRewriteGeneration,
                                    CredentialGeneration = "fixture",
                                    ToolInventoryDigest = "fixture-tools",
                                    InstructionDigest = "fixture-instructions",
                                    NormalizedRoundDigest = "fixture-round",
                                },
                                [1],
                                ["wire-question-1"],
                                retainedOutputTokens: 1),
                        };
                    }

                    yield return new ModelChunk { Output = new ToolRequestModelOutput(ToolName, Arguments), FinishReason = ModelFinishReason.ToolCalls };
                }
                else
                {
                    yield return new ModelChunk { Text = "Question complete.", FinishReason = ModelFinishReason.Stop };
                }
            }
            finally
            {
                StreamDisposed = true;
            }
        }
    }
}
