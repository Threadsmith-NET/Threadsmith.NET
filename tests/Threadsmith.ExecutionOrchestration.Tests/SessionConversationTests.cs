namespace Threadsmith.ExecutionOrchestration.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies ordinary conversation persistence and provider continuation.</summary>
public sealed partial class SessionConversationTests
{
    private sealed class ConversationScenario : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly DomainEventStream _events;

        private ConversationScenario(string directory, DomainEventStream events, CommandDispatcher dispatcher, SessionId sessionId, ConceptMemory memory)
        {
            _directory = directory;
            _events = events;
            Dispatcher = dispatcher;
            SessionId = sessionId;
            Memory = memory;
        }

        public CommandDispatcher Dispatcher { get; }

        public SessionId SessionId { get; }

        public ConceptMemory Memory { get; }

        public static async Task<ConversationScenario> CreateMemoryReplayAsync(IModelProvider provider, bool matches)
        {
            var ct = TestContext.Current.CancellationToken;
            var directory = Path.Combine(Path.GetTempPath(), $"threadsmith-conversation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            var connectionString = $"Data Source={Path.Combine(directory, "state.db")};Pooling=False";
            var events = new DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync(ct);
            var artifacts = new ArtifactStore(connectionString, Path.Combine(directory, "artifacts"), sanitizer);
            await artifacts.InitializeAsync(ct);
            var store = new SqliteConversationStore(connectionString, artifacts, sanitizer);
            var evidence = new EvidenceStore(events, sanitizer);
            var memory = new ConceptMemory { Matches = matches };
            var assembler = new ContextAssembler(
                evidence,
                new TokenEstimator(),
                new ContextPolicy(),
                new PromptAppendLoader(sanitizer),
                sanitizer,
                events,
                TestPromptLoader.Instance,
                conversationStore: store,
                repositoryMemoryRetriever: memory);
            var registry = new ToolRegistry([new MemoriesTool(memory, memory, TestPromptLoader.Instance), new ListFilesTool(TestPromptLoader.Instance)]);
            var pipeline = new ToolInvocationPipeline(
                registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
            var application = new SessionApplication(
                events,
                provider,
                new ExecutionBudget(new BudgetDimensions(100_000, 100, TimeSpan.FromMinutes(1))),
                sanitizer,
                NullLogger<SessionApplication>.Instance,
                pipeline,
                (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(new ToolInvocationContext
                    {
                        RepositoryPath = directory, TrustLevel = RepositoryTrustLevel.TrustedMutation, RequestedBy = "conversation-test",
                    });
                },
                contextAssembler: assembler,
                toolRegistry: registry,
                repositoryMemories: memory,
                repositoryMemoryOptions: memory,
                evidenceStore: evidence,
                conversationStore: store,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance);
            var dispatcher = new CommandDispatcher([application]);
            var session = await dispatcher.DispatchAsync(new CreateSessionCommand("memory replay"), ct);
            return new(directory, events, dispatcher, session, memory);
        }

        public async ValueTask DisposeAsync()
        {
            await _events.DisposeAsync();
            var resolved = Path.GetFullPath(_directory);
            if (!string.Equals(Path.GetDirectoryName(resolved), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("threadsmith-conversation-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Cleanup must remain in the owned temporary directory.");
            }

            Directory.Delete(resolved, recursive: true);
        }
    }
}
