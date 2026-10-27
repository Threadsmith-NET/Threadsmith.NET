namespace Threadsmith.ExecutionOrchestration.Tests;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SessionConversationTests
{
    /// <summary>Completed and interrupted visible responses survive a fresh host and reach its bounded recent context once.</summary>
    [Theory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public static async Task VisibleResponsesSurviveInterruptedRunsAndResume(string completion)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-archive-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var connectionString = $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
            await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync(cancellationToken);
            await using var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var artifacts = new ArtifactStore(connectionString, Path.Combine(root, "artifacts"), sanitizer);
            await artifacts.InitializeAsync(cancellationToken);
            var store = new SqliteConversationStore(connectionString, artifacts, sanitizer);
            var provider = new InterruptedArchiveProvider(completion);
            var dispatcher = CreateArchiveDispatcher(provider);
            var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("archive interruption"), cancellationToken);
            var runId = await dispatcher.DispatchAsync(new SubmitRequestCommand(sessionId, "Assess the parser."), cancellationToken);
            await provider.PartialResponseReady.Task.WaitAsync(cancellationToken);
            var during = await store.GetSnapshotAsync(sessionId, cancellationToken: cancellationToken);
            if (completion == "cancel")
            {
                await dispatcher.DispatchAsync(new CancelRunCommand(sessionId, runId), cancellationToken);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.DispatchAsync(new WaitForRunCommand(runId), cancellationToken));
            }
            else
            {
                provider.ReleaseResponse.TrySetResult();
                if (completion == "failure")
                {
                    await Assert.ThrowsAsync<ModelProviderException>(() => dispatcher.DispatchAsync(new WaitForRunCommand(runId), cancellationToken));
                }
                else
                {
                    Assert.True(await dispatcher.DispatchAsync(new WaitForRunCommand(runId), cancellationToken));
                }
            }

            var restored = await store.GetSnapshotAsync(sessionId, cancellationToken: cancellationToken);
            Assert.Contains(during.Messages, message => message.Role == ConversationRole.Assistant && message.Content == "I inspected the source.");
            var replies = restored.Messages.Where(message => message.Role == ConversationRole.Assistant).ToArray();
            Assert.Equal(2, replies.Length);
            Assert.Equal("I inspected the source.", replies[0].Content);
            Assert.Equal(completion == "success" ? "The assessment is partly complete. Finished." : "The assessment is partly complete.", replies[1].Content);
            var resumed = CreateArchiveDispatcher(provider, sessionId);
            var resumedRun = await resumed.DispatchAsync(new SubmitRequestCommand(sessionId, "Continue the assessment."), cancellationToken);
            Assert.True(await resumed.DispatchAsync(new WaitForRunCommand(resumedRun), cancellationToken));
            var request = provider.Requests[^1];
            Assert.Single(request.Messages, message => message.SectionId == "recent-user" && message.GetModelVisibleContent() == "Assess the parser.");
            Assert.Single(request.Messages, message => message.SectionId == "recent-assistant" && message.GetModelVisibleContent() == replies[0].Content);
            Assert.Single(request.Messages, message => message.SectionId == "recent-assistant" && message.GetModelVisibleContent() == replies[1].Content);

            CommandDispatcher CreateArchiveDispatcher(IModelProvider model, SessionId? restoredSession = null)
            {
                var evidence = new EvidenceStore(events, sanitizer);
                var assembler = new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, events, TestPromptLoader.Instance, conversationStore: store);
                var registry = new ToolRegistry([new ListFilesTool(TestPromptLoader.Instance)]);
                var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
                var application = new SessionApplication(
                    events,
                    model,
                    UnboundedBudget.Instance,
                    sanitizer,
                    NullLogger<SessionApplication>.Instance,
                    pipeline,
                    (_, _) => Task.FromResult(new ToolInvocationContext { RepositoryPath = root, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "model" }),
                    assembler,
                    evidence,
                    registry,
                    conversationStore: store,
                    correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                    prompts: TestPromptLoader.Instance);
                if (restoredSession is { } restoredId)
                {
                    application.RegisterRestoredSession(restoredId);
                }

                return new CommandDispatcher([application]);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class InterruptedArchiveProvider : IModelProvider
    {
        private readonly string _completion;

        public InterruptedArchiveProvider(string completion)
        {
            _completion = completion;
        }

        public TaskCompletionSource PartialResponseReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                yield return new ModelChunk { Text = "I inspected the source." };
                yield return new ModelChunk { Output = new ToolRequestModelOutput("list_files", "{\"path\":\".\"}") };
            }
            else if (Requests.Count == 2)
            {
                yield return new ModelChunk { Text = "The assessment is partly complete." };
                PartialResponseReady.TrySetResult();
                await ReleaseResponse.Task.WaitAsync(cancellationToken);
                if (_completion == "failure")
                {
                    throw new ModelProviderException("Simulated provider interruption.");
                }

                yield return new ModelChunk { Text = " Finished.", FinishReason = ModelFinishReason.Stop };
            }
            else
            {
                yield return new ModelChunk { Text = "Continuing with the saved assessment.", FinishReason = ModelFinishReason.Stop };
            }
        }
    }
}
