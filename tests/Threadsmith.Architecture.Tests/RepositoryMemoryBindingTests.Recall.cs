namespace Threadsmith.Architecture.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.RepositoryIntelligence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public static partial class RepositoryMemoryBindingTests
{
    /// <summary>Disabling intelligence removes its next-request context and inclusion events while ordinary recall continues.</summary>
    [Fact]
    public static async Task DisableCommandExcludesIntelligenceFromNextRequestAndInclusionEvents()
    {
        const string ordinary = "ordinary-memory-fixture";
        const string intelligence = "intelligence-memory-fixture";
        var token = TestContext.Current.CancellationToken;
        var root = Directory.CreateTempSubdirectory("threadsmith-disable-recall-");
        try
        {
            var identity = RepositoryIdentity.Create(root.FullName);
            var connectionString = $"Data Source=disable-recall-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Pooling=False";
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(token);
            await new SqliteEventStore(connectionString).InitializeAsync(token);
            await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync(token);
            using var store = new SqliteManagedRepositoryMemoryStore(connectionString);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["tools:config:memories:Intelligence:Enabled"] = "true",
            }).Build();
            var options = new RepositoryMemoryConfiguration(configuration, configuration, root.FullName);
            var settings = options.CaptureCurrent();
            var embeddings = new RecallEmbeddingGenerator();
            var vector = await embeddings.GenerateAsync(string.Empty, token);
            await store.CommitCollectionAsync(new RepositoryMemoryCollection(await store.GetSnapshotAsync(identity, [], token), true, 0), settings, token);
            var ordinarySeed = new RepositoryMemoryWrite { Text = ordinary, Origin = RepositoryMemoryOrigin.Manual };
            var intelligenceSeed = new RepositoryMemoryWrite { Text = intelligence, Origin = RepositoryMemoryOrigin.Model, Ownership = RepositoryMemoryOwnership.Intelligence };
            var ordinaryWrite = await store.AddAsync(identity, ordinarySeed, embeddings.Model, vector, settings, token);
            var intelligenceWrite = await store.AddAsync(identity, intelligenceSeed, embeddings.Model, vector, settings, token);
            Assert.NotNull(ordinaryWrite.Entry);
            Assert.NotNull(intelligenceWrite.Entry);

            var sanitizer = new SecretOutputSanitizer();
            await using var events = new DomainEventStream();
            using var observer = new RepositoryFileObserver();
            await using var monitor = new RepositoryIntelligenceMonitor(store, observer, events, NullLogger<RepositoryIntelligenceMonitor>.Instance, sanitizer);
            var automation = new RepositoryIntelligenceAutomation(new QueuedCommits(), monitor, options, events, NullLogger<RepositoryIntelligenceAutomation>.Instance);
            await using var operations = new RepositoryIntelligenceOperations(
                store, options, monitor, TestPromptLoader.Instance, events, options.SetIntelligenceEnabledAsync, automation);
            using var retriever = new HybridRepositoryMemoryRetriever(store, embeddings);
            var memory = new RepositoryMemoryService(store, embeddings, sanitizer);
            var registry = new ToolRegistry([new MemoriesTool(memory, options, TestPromptLoader.Instance)]);
            var assembler = new ContextAssembler(
                new EvidenceStore(events, sanitizer),
                new TokenEstimator(),
                new ContextPolicy(),
                new PromptAppendLoader(sanitizer),
                sanitizer,
                events,
                TestPromptLoader.Instance,
                repositoryMemoryRetriever: retriever);
            var model = new CallbackModel(() => { }, "done");
            var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new DenyApprovalPolicy(), events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance, timeProvider: new FakeTimeProvider());
            var invocationContext = new ToolInvocationContext { RepositoryPath = root.FullName, TrustLevel = RepositoryTrustLevel.TrustedRead, RequestedBy = "test" };
            var application = new SessionApplication(
                events,
                model,
                new ExecutionBudget(new BudgetDimensions(100000, 100, TimeSpan.MaxValue)),
                sanitizer,
                NullLogger<SessionApplication>.Instance,
                toolPipeline: pipeline,
                toolContextFactory: (_, _) => Task.FromResult(invocationContext),
                contextAssembler: assembler,
                toolRegistry: registry,
                correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
                prompts: TestPromptLoader.Instance,
                repositoryMemories: memory,
                repositoryMemoryOptions: options,
                repositoryIntelligence: monitor);
            var session = await application.HandleAsync(new CreateSessionCommand("recall"), token);
            await operations.BindAsync(session, root.FullName, token);
            await monitor.WaitUntilReadyAsync(token);

            var inclusions = new List<MemoriesIncluded>();
            await using (events.Subscribe((item, _) =>
            {
                if (item is MemoriesIncluded included)
                {
                    inclusions.Add(included);
                }

                return Task.CompletedTask;
            }))
            {
                var before = await application.HandleAsync(new SubmitRequestCommand(session, "Recall repository notes"), token);
                Assert.True(await application.HandleAsync(new WaitForRunCommand(before), token));
                var status = await operations.HandleAsync(new ManageRepositoryIntelligenceCommand(session, root.FullName, "disable"), token);
                Assert.False(status.IsEffective);
                var after = await application.HandleAsync(new SubmitRequestCommand(session, "Recall repository notes"), token);
                Assert.True(await application.HandleAsync(new WaitForRunCommand(after), token));

                Assert.Collection(
                    model.CapturedRequests,
                    request => Assert.Contains(request.Messages, message => message.GetModelVisibleContent().Contains(intelligence, StringComparison.Ordinal)),
                    request =>
                    {
                        Assert.Contains(request.Messages, message => message.GetModelVisibleContent().Contains(ordinary, StringComparison.Ordinal));
                        Assert.DoesNotContain(request.Messages, message => message.GetModelVisibleContent().Contains(intelligence, StringComparison.Ordinal));
                    });
                Assert.Contains(inclusions.Where(item => item.RunId == before).SelectMany(item => item.Items), item => item.Id == intelligenceWrite.Entry.Id);
                var next = Assert.Single(inclusions.Where(item => item.RunId == after).SelectMany(item => item.Items));
                Assert.Equal(ordinaryWrite.Entry.Id, next.Id);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private sealed class RecallEmbeddingGenerator : ITextEmbeddingGenerator
    {
        public TextEmbeddingModelDescriptor Model { get; } = new("fixture", 1, 256);

        public Task<TextEmbeddingResult> GenerateAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TextEmbeddingResult(new float[] { 1 }, 1, false));
        }
    }
}
