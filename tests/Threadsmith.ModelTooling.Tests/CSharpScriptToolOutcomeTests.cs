namespace Threadsmith.ModelTooling.Tests;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Checks the result classification consumed by the conversation loop.</summary>
public static class CSharpScriptToolOutcomeTests
{
    /// <summary>A worker timeout is an unknown action outcome, not an ordinary script error.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task GoalPausesOnlyForUnconfirmedScriptOutcome(bool timedOut)
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider();
        await using var events = new DomainEventStream(timeProvider: clock);
        var prompts = TestPromptLoader.Instance;
        var sanitizer = new SecretOutputSanitizer();
        var registry = new ToolRegistry([new CSharpScriptTool(new FailedWorker(timedOut), prompts)]);
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, timeProvider: clock);
        var repository = Path.GetTempPath();
        var context = new ToolInvocationContext
        {
            RepositoryPath = repository,
            RequestedBy = "model",
            TrustLevel = RepositoryTrustLevel.FullyTrustedAutomation,
            AllowedExecutables = ["dotnet"],
            Scratchpad = new ScratchpadSessionCapability { IsActive = true, RootPath = Path.Combine(repository, ".threadsmith", ".scratchpad", Guid.NewGuid().ToString("N")) },
        };
        var evidence = new EvidenceStore(events, sanitizer);
        var assembler = new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, events, prompts);
        var model = new ScriptCallerModel();
        var app = new SessionApplication(
            events,
            model,
            UnboundedBudget.Instance,
            sanitizer,
            NullLogger<SessionApplication>.Instance,
            pipeline,
            (_, _) => Task.FromResult(context),
            assembler,
            evidence,
            registry,
            correctiveMessages: new CorrectiveMessageFactory(prompts),
            prompts: prompts,
            outputClock: clock);
        var dispatcher = new CommandDispatcher([app]);
        var session = await dispatcher.DispatchAsync(new CreateSessionCommand("script outcome"), ct);
        const string goal = "Run the script and report its result.";
        await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Set, goal), ct);
        var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, goal), ct);
        var done = dispatcher.DispatchAsync(new WaitForRunCommand(run), ct);
        if (timedOut)
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => done.WaitAsync(ct));
            Assert.Contains("the outcome of csharp_script", error.Message, StringComparison.Ordinal);
            Assert.Contains("is unknown", error.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(await done.WaitAsync(ct));
        }

        Assert.Equal(timedOut ? 1 : 2, model.Requests);
        var status = await dispatcher.DispatchAsync(new SessionGoalCommand(session, SessionGoalAction.Show), ct);
        Assert.Equal(timedOut ? goal : null, status.DisplayText);
    }

    private sealed class ScriptCallerModel : IModelProvider
    {
        internal int Requests { get; private set; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.CompletedTask;
            Requests++;
            if (Requests == 1)
            {
                yield return new ModelChunk { Output = new ToolRequestModelOutput("csharp_script", """{"code":"1 + 1"}"""), FinishReason = ModelFinishReason.ToolCalls };
                yield break;
            }

            Assert.Contains(request.Messages, message => message.Role == ModelMessageRole.Tool && message.GetModelVisibleContent().Contains("Script failed.", StringComparison.Ordinal));
            yield return new ModelChunk { Text = "All requested work is complete.", FinishReason = ModelFinishReason.Stop };
        }
    }

    private sealed class FailedWorker : ICSharpScriptEngine
    {
        private readonly bool _timedOut;

        internal FailedWorker(bool timedOut)
        {
            _timedOut = timedOut;
        }

        public Task<CSharpScriptOutput> ExecuteAsync(string code, ScriptKind kind, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CSharpScriptOutput(false, null, "Script failed.", 0, false) { TimedOut = _timedOut });
        }
    }
}
