namespace Threadsmith.Planning.Tests;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public static partial class Plan80ActiveTurnContinuationTests
{
    /// <summary>Tool continuations retain streamed assistant prose once, in order, including acknowledgements after steering.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task Commentary_survives_tool_continuations_and_steering(bool submitSteering, bool pauseBeforeTools)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var events = new DomainEventStream();
        var sanitizer = new SecretOutputSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var registry = new ToolRegistry([new TestDeterministicOutputTool("Evidence.")]);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
        var profile = CreateProfile() with { ContextWindow = 32_000 };
        var resolver = new ModelResolver(new ConfiguredModelCatalog([profile]), new InMemoryModelPreferenceSnapshotProvider());
        var assembler = CreateAssembler(events, evidence, resolver, sanitizer);
        var model = new CommentaryToolProvider();
        var steering = new RunSteeringCoordinator();
        var pauseRequested = new TaskCompletionSource<RunSteeringPauseRequestResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var observe = events.Subscribe((domainEvent, _) =>
        {
            if (submitSteering && !pauseBeforeTools && domainEvent is ToolInvocationCompleted completed && model.Requests.Count == 1)
            {
                pauseRequested.TrySetResult(steering.RequestPause(completed.SessionId, model.Requests[0].RunId));
            }

            return Task.CompletedTask;
        });
        var application = new SessionApplication(
            events,
            model,
            UnboundedBudget.Instance,
            sanitizer,
            NullLogger<SessionApplication>.Instance,
            pipeline,
            (_, _) => Task.FromResult(new ToolInvocationContext { RepositoryPath = AppContext.BaseDirectory, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model" }),
            assembler,
            evidence,
            registry,
            profile.Id,
            new ExecutionLimits { MaxModelRounds = 4 },
            steering: steering,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance);
        var dispatcher = new CommandDispatcher([application]);
        var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("commentary"), cancellationToken);
        if (pauseBeforeTools)
        {
            model.BeforeToolRequest = () => pauseRequested.TrySetResult(steering.RequestPause(sessionId, model.Requests[0].RunId));
        }

        var runId = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "Inspect the repository."), cancellationToken);
        if (submitSteering)
        {
            var pause = await pauseRequested.Task.WaitAsync(cancellationToken);
            var ready = await steering.WaitForPauseAsync(sessionId, runId, pause.PauseId, cancellationToken);
            Assert.Equal(RunSteeringPauseWaitStatus.Ready, ready.Status);
            Assert.Equal(RunSteeringSubmissionStatus.Accepted, steering.Submit(sessionId, runId, pause.PauseId, "Focus on the parser.").Status);
        }

        Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId), cancellationToken));

        Assert.Equal(3, model.Requests.Count);
        var second = model.Requests[1].Messages;
        Assert.Single(second, message => message.GetModelVisibleContent() == "Starting inspection.");
        var final = model.Requests[2].Messages.ToList();
        Assert.Equal(submitSteering ? 1 : 0, final.Count(message => message.SectionId == "run-user-steering"));
        var acknowledgement = Assert.Single(final, message => message.GetModelVisibleContent() == "Acknowledged. Inspecting the parser.");
        var between = Assert.Single(final, message => message.GetModelVisibleContent() == "Checking the next boundary.");
        var after = Assert.Single(final, message => message.GetModelVisibleContent() == "Evidence requested.");
        Assert.Equal(ModelMessageRole.Assistant, acknowledgement.Role);
        Assert.True(final.IndexOf(acknowledgement) < final.FindIndex(message => message.ToolCallId == "host-tool-2-1"));
        Assert.True(final.FindIndex(message => message.ToolCallId == "host-tool-2-1") < final.IndexOf(between));
        Assert.True(final.IndexOf(between) < final.FindIndex(message => message.ToolCallId == "host-tool-2-2"));
        Assert.True(final.FindIndex(message => message.ToolCallId == "host-tool-2-2") < final.IndexOf(after));
        Assert.True(final.IndexOf(after) < final.FindIndex(message => message.Role == ModelMessageRole.Tool && message.ToolCallId == "host-tool-2-1"));
    }

    private sealed class CommentaryToolProvider : IModelProvider
    {
        public List<ModelStreamRequest> Requests { get; } = [];

        public Action? BeforeToolRequest { get; set; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            if (Requests.Count == 1)
            {
                yield return new ModelChunk { Text = "Starting inspection." };
                BeforeToolRequest?.Invoke();
                yield return new ModelChunk { Output = new ToolRequestModelOutput("deterministic_output", "{\"sequence\":1}") };
            }
            else if (Requests.Count == 2)
            {
                yield return new ModelChunk { Text = "Acknowledged. " };
                yield return new ModelChunk { Text = "Inspecting the parser." };
                yield return new ModelChunk { Output = new ToolRequestModelOutput("deterministic_output", "{\"sequence\":2}") };
                yield return new ModelChunk { Text = "Checking the next boundary." };
                yield return new ModelChunk { Output = new ToolRequestModelOutput("deterministic_output", "{\"sequence\":3}") };
                yield return new ModelChunk { Text = "Evidence requested." };
            }
            else
            {
                yield return new ModelChunk { Text = "Done.", FinishReason = ModelFinishReason.Stop };
            }
        }
    }
}
