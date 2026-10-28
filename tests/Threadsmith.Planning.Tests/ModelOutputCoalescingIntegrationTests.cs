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
    [InlineData("complete", "sk-abcdefghijklmnop", -1)]
    [InlineData("fail", "sk-abcdefghijklmnop", 0)]
    [InlineData("cancel", "sk-abcdefghijklmnop", 4096)]
    [InlineData("tool", "password: 'first-line\nsecret-canary'")]
    [InlineData("complete", "-----BEGIN RSA PRIVATE KEY-----\nsecret-canary\n-----END RSA PRIVATE KEY-----\n")]
    [InlineData("complete", "oversized")]
    [InlineData("complete", "long-line", 0)]
    [InlineData("complete", null, 0)]
    public static async Task MainLoop_DrainsExactTextToSqliteBeforeTermination(
        string ending, string? sensitiveContent = null, int maximumOutputCharacters = 8 * 1024 * 1024)
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
            var content = sensitiveContent switch
            {
                "oversized" => "password: '" + new string('x', 16385) + "\nsecret-canary'",
                "long-line" => new string('x', 16385),
                _ => sensitiveContent,
            };
            var provider = new FragmentProvider(ending, content);
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
                    MaxStructuredOutputCharacters = maximumOutputCharacters,
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
            if (content is null)
            {
                Assert.Equal("first" + new string('x', 1000) + "\ntail", string.Concat(text.Select(item => item.Text)));
                Assert.Equal(17, text.Length);
                var reasoning = received.FindIndex(item => item is ModelReasoningObserved);
                Assert.Equal("first" + new string('x', 1000) + "\n", string.Concat(received.Take(reasoning).OfType<ModelOutputObserved>().Select(item => item.Text)));
            }
            else
            {
                var expected = sanitizer.Sanitize(content);
                Assert.Equal(expected, string.Concat(text.Select(item => item.Text)));
                Assert.Equal(expected, string.Concat(received.OfType<ModelReasoningObserved>().Select(item => item.Text)));
                Assert.DoesNotContain("secret-canary", string.Concat(text.Select(item => item.Text)), StringComparison.Ordinal);
                if (!content.Contains('\n'))
                {
                    Assert.True(received.FindIndex(item => item is ModelReasoningObserved) < received.FindIndex(item => item is ModelOutputObserved));
                }
            }

            Assert.All(text, item => Assert.InRange(item.Text.Length, 1, 64));
            Assert.True(received.FindLastIndex(item => item is ModelOutputObserved) < received.FindIndex(item => item is RunCompleted));
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
        private readonly string? _content;

        public FragmentProvider(string ending, string? content)
        {
            _ending = ending;
            _content = content;
        }

        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (_content is null)
            {
                yield return new ModelChunk { Text = "first" };
                for (var index = 0; index < 1000; index++)
                {
                    yield return new ModelChunk { Text = "x" };
                }

                yield return new ModelChunk { Text = "\n" };
                yield return new ModelChunk { Reasoning = "visible thoughts\n" };
                yield return new ModelChunk
                {
                    Text = "tail",
                    Output = _ending == "tool" ? new ToolRequestModelOutput("deterministic_output", "{\"sequence\":1}") : null,
                };
            }
            else
            {
                // Interleave both channels at every character boundary. Neither may flush
                // the other's incomplete credential, even when observations intervene.
                foreach (var character in _content)
                {
                    yield return new ModelChunk { Text = character.ToString() };
                    yield return new ModelChunk { Reasoning = character.ToString() };
                }

                if (_ending == "tool")
                {
                    yield return new ModelChunk { Output = new ToolRequestModelOutput("deterministic_output", "{\"sequence\":1}") };
                }
            }

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
