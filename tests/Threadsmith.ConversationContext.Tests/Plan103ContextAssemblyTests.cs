namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Final memory inclusion, context-mode and sensitivity contracts independent of retrieval scoring.</summary>
public static class Plan103ContextAssemblyTests
{
    private static RepositoryMemoryEntry CreateEntry(string text)
    {
        return new()
        {
            Id = RepositoryMemoryId.New(),
            RepositoryIdentity = MemoryTestData.Repository,
            Text = text,
            ContentHash = "test-content-hash",
            Origin = RepositoryMemoryOrigin.Manual,
            Revision = 3,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Sensitivity = ConversationSensitivity.Sensitive,
        };
    }

    private static ContextAssemblyRequest CreateRequest(ConversationFixture fixture)
    {
        return new()
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            Phase = RunPhase.EvidenceCollection,
            RepositoryPath = fixture.DirectoryPath,
            RepositoryIdentity = MemoryTestData.Repository,
            Task = new TaskSpecification("current task intent", []),
        };
    }

    private static ContextAssembler CreateAssembler(IDomainEventStream events, IHybridRepositoryMemoryRetriever retriever, IModelResolver? resolver = null, IConversationStore? conversationStore = null)
    {
        var sanitizer = new SecretOutputSanitizer();
        return new ContextAssembler(
            new EvidenceStore(events, sanitizer),
            new TokenEstimator(),
            new ContextPolicy(),
            new PromptAppendLoader(sanitizer),
            sanitizer,
            events,
            TestPromptLoader.Instance,
            modelResolver: resolver,
            conversationStore: conversationStore,
            repositoryMemoryRetriever: retriever);
    }

    private sealed class TestRetriever : IHybridRepositoryMemoryRetriever
    {
        public TestRetriever(RepositoryMemoryEntry entry)
        {
            Entry = entry;
        }

        public RepositoryMemoryEntry Entry { get; set; }

        public RepositoryMemorySearchDetails? SearchDetails { get; init; }

        public int Calls { get; private set; }

        public RepositoryMemoryRetrievalRequest? Request { get; private set; }

        public Task<RepositoryMemoryRetrievalResult> RetrieveAsync(RepositoryMemoryRetrievalRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Request = request;
            return Task.FromResult(new RepositoryMemoryRetrievalResult([new RepositoryMemoryRetrievalCandidate(Entry, 0.03, 1, 2, 0.9, 7.25)], [])
            {
                SearchDetails = SearchDetails,
            });
        }
    }

    private sealed class TestModelResolver : IModelResolver
    {
        public bool ContainsSensitiveData { get; private set; }

        public int MaximumInputTokenBudget => 32_000;

        public ModelResolution Resolve(WorkloadClass workloadClass, ModelCapabilitySet requiredCapabilities, ModelSelectionConstraints constraints, ModelProfileId? defaultModelProfileId = null)
        {
            ContainsSensitiveData = constraints.ContainsSensitiveData;
            return new ModelResolution(new ModelProfileId(Guid.NewGuid()), 32_000, 0, [], [], []);
        }
    }
}
