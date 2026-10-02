namespace Threadsmith.Planning.Tests;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies the real chat loop and durable event subscriber share coalesced output.</summary>
public static class ModelOutputCoalescingIntegrationTests
{
    /// <summary>Restoration retains exact text before successful, failed, and cancelled terminal events.</summary>
    [Theory]
    [InlineData("complete")]
    [InlineData("fail")]
    [InlineData("cancel")]
    [InlineData("tool")]
    public static async Task MainLoop_DrainsExactTextToSqliteBeforeTermination(string ending)
    {
        var directory = Path.Combine(Path.GetTempPath(), "threadsmith-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SqliteEventStore($"Data Source={Path.Combine(directory, "events.db")};Pooling=False");
            await store.InitializeAsync(TestContext.Current.CancellationToken);
            await using var events = new DomainEventStream();
            var received = new List<IDomainEvent>();
            await using var subscriber = events.Subscribe(async (item, token) =>
            {
                received.Add(item);
                if (item is not ModelReasoningObserved)
                {
                    await store.AppendAsync(item, token);
                }
            });
            var provider = new FragmentProvider(ending);
            var sanitizer = new SecretOutputSanitizer();
            var registry = new ToolRegistry([new TestDeterministicOutputTool("tool result")]);
            var pipeline = new ToolInvocationPipeline(
                registry,
                new DefaultPolicyEngine(),
                new DenyApprovalPolicy(),
                events,
                sanitizer,
                NullLogger<ToolInvocationPipeline>.Instance);
            var application = new SessionApplication(
                events,
                provider,
                new ExecutionBudget(new BudgetDimensions(long.MaxValue, int.MaxValue, TimeSpan.FromHours(1))),
                sanitizer,
                NullLogger<SessionApplication>.Instance,
                toolPipeline: pipeline,
                toolContextFactory: (_, _) => Task.FromResult(new ToolInvocationContext
                {
                    RepositoryPath = directory,
                    TrustLevel = RepositoryTrustLevel.TrustedRead,
                    RequestedBy = "model",
                }),
                toolRegistry: registry,
                limits: new ExecutionLimits
                {
                    MaxModelOutputBatchCharacters = 64,
                    ModelOutputFlushIntervalMilliseconds = int.MaxValue,
                },
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var session = await dispatcher.DispatchAsync(new CreateSessionCommand("coalescing"), TestContext.Current.CancellationToken);
            var run = await dispatcher.DispatchAsync(new SubmitRequestCommand(session, "Answer briefly."), TestContext.Current.CancellationToken);
            if (ending == "cancel")
            {
                await provider.Waiting.Task.WaitAsync(TestContext.Current.CancellationToken);
                await dispatcher.DispatchAsync(new CancelRunCommand(session, run), TestContext.Current.CancellationToken);
            }

            var completion = dispatcher.DispatchAsync(new WaitForRunCommand(run), TestContext.Current.CancellationToken);
            if (ending == "fail")
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => completion.WaitAsync(TestContext.Current.CancellationToken));
            }
            else if (ending == "cancel")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.WaitAsync(TestContext.Current.CancellationToken));
            }
            else
            {
                Assert.True(await completion);
            }

            var restored = await store.ReadAsync(session, TestContext.Current.CancellationToken);
            var text = restored.OfType<ModelOutputObserved>().ToArray();
            Assert.Equal("first" + new string('x', 1000) + "tail", string.Concat(text.Select(item => item.Text)));
            Assert.Equal(18, text.Length);
            Assert.All(text, item => Assert.InRange(item.Text.Length, 1, 64));
            Assert.True(received.FindLastIndex(item => item is ModelOutputObserved) < received.FindIndex(item => item is RunCompleted));
            var reasoning = received.FindIndex(item => item is ModelReasoningObserved);
            Assert.Equal("first" + new string('x', 1000), string.Concat(received.Take(reasoning).OfType<ModelOutputObserved>().Select(item => item.Text)));
            Assert.DoesNotContain(restored, item => item is ModelReasoningObserved);
            if (ending == "tool")
            {
                Assert.True(Assert.Single(received.OfType<ToolInvocationCompleted>()).Succeeded);
                Assert.True(received.FindLastIndex(item => item is ModelOutputObserved) < received.FindIndex(item => item is ToolInvocationStarted));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FragmentProvider : IModelProvider
    {
        private readonly string _ending;

        public FragmentProvider(string ending)
        {
            _ending = ending;
        }

        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new ModelChunk { Text = "first" };
            for (var index = 0; index < 1000; index++)
            {
                yield return new ModelChunk { Text = "x" };
            }

            yield return new ModelChunk { Reasoning = "private reasoning" };
            yield return new ModelChunk
            {
                Text = "tail",
                Output = _ending == "tool" ? new ToolRequestModelOutput("deterministic_output", "{\"sequence\":1}") : null,
            };
            if (_ending == "fail")
            {
                throw new InvalidOperationException("provider failed");
            }

            if (_ending == "cancel")
            {
                Waiting.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            yield return new ModelChunk { FinishReason = ModelFinishReason.Stop };
        }
    }
}
