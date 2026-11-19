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
    /// <summary>Imported over-budget text stays inspectable but never contributes a dispatch receipt or sensitivity.</summary>
    [Fact]
    public static async Task Memory_budget_omission_has_no_receipt_or_sensitivity()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var retriever = new TestRetriever(CreateEntry(new string('m', 10_000)));
        var resolver = new TestModelResolver();
        var assembler = CreateAssembler(events, retriever, resolver);

        var result = await assembler.AssembleAsync(CreateRequest(fixture));

        Assert.Empty(result.RepositoryMemoryInclusions ?? []);
        Assert.False(resolver.ContainsSensitiveData);
        Assert.False(Assert.Single(result.Inspection.RepositoryMemoryItems).Included);
        Assert.DoesNotContain(new string('m', 100), result.ModelInput, StringComparison.Ordinal);
    }

    /// <summary>Current steering is the first retrieval query source, with active intent supplied separately.</summary>
    [Fact]
    public static async Task Current_steering_precedes_task_intent()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var retriever = new TestRetriever(CreateEntry("memory"));
        var assembler = CreateAssembler(events, retriever);

        await assembler.AssembleAsync(CreateRequest(fixture) with { RepositoryMemoryCurrentInstruction = "new steering" });

        Assert.Equal("new steering", retriever.Request?.CurrentInstruction);
        Assert.Equal("current task intent", retriever.Request?.TaskIntent);
    }

    /// <summary>Assembly results, input diagnostics, snapshots and published events never expose cached collections.</summary>
    [Fact]
    public static async Task Inspection_collections_are_detached_at_assembly_and_dispatch_boundaries()
    {
        // Arrange through the existing assembler and retriever path.
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, token) =>
        {
            token.ThrowIfCancellationRequested();
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        var support = new RepositoryMemorySupport { Path = "src/original.cs", ContentHash = "hash", ObservedAt = DateTimeOffset.UtcNow };
        RepositoryMemorySupport[] supports = [support];
        string[] terms = ["original"];
        var retriever = new TestRetriever(CreateEntry("memory") with { Supports = supports })
        {
            SearchDetails = new()
            {
                Candidates = new RepositoryMemoryCandidateSearchEvidence[]
                {
                    new(RepositoryMemoryId.New(), "selected", 1, null, false, null, null, null) { MatchedTerms = terms },
                },
            },
        };
        var assembler = CreateAssembler(events, retriever);
        var request = CreateRequest(fixture);
        var result = await assembler.AssembleAsync(request, cancellationToken);
        var published = Assert.Single(observed.OfType<ContextAssembled>()).Inspection;

        // Act: mutate upstream inputs, the assembly result, and an event payload.
        supports[0] = support with { Path = "changed-input" };
        terms[0] = "changed-input";
        Assert.IsType<RepositoryMemorySupport[]>(published.RepositoryMemoryItems[0].Supports)[0] = support with { Path = "changed-event" };
        Assert.IsType<string[]>(published.RepositoryMemorySearch!.Candidates[0].MatchedTerms)[0] = "changed-event";
        Assert.IsType<RepositoryMemorySupport[]>(result.Inspection.RepositoryMemoryItems[0].Supports)[0] =
            support with { Path = "changed-result" };
        var first = Assert.IsType<ContextInspectionProjection>(assembler.GetInspection(request.RunId));

        // Assert: cached data and repeated retrieval remain independent.
        Assert.Equal("memory", first.RepositoryMemoryItems[0].Text);
        Assert.Equal(support, first.RepositoryMemoryItems[0].Supports[0]);
        Assert.Equal("original", first.RepositoryMemorySearch!.Candidates[0].MatchedTerms[0]);
        Assert.IsType<RepositoryMemorySupport[]>(first.RepositoryMemoryItems[0].Supports)[0] = support with { Path = "changed-snapshot" };
        Assert.IsType<string[]>(first.RepositoryMemorySearch.Candidates[0].MatchedTerms)[0] = "changed-snapshot";
        Assert.Equal(support, assembler.GetInspection(request.RunId)!.RepositoryMemoryItems[0].Supports[0]);
        Assert.Equal("original", assembler.GetInspection(request.RunId)!.RepositoryMemorySearch!.Candidates[0].MatchedTerms[0]);

        RepositoryMemoryInclusion[] inclusions = [new(retriever.Entry.Id, retriever.Entry.Revision)];
        var expectedInclusion = inclusions[0];
        await assembler.UpdateRepositoryMemoryDispatchInspectionAsync(
            request.SessionId, request.RunId, new(inclusions, "recorded", 1.25), cancellationToken);
        inclusions[0] = expectedInclusion with { Revision = 999 };
        var updatedEvent = observed.OfType<ContextAssembled>().Last().Inspection;
        Assert.IsType<RepositoryMemoryInclusion[]>(updatedEvent.RepositoryMemoryDispatch!.Inclusions)[0] = expectedInclusion with { Revision = 888 };
        Assert.IsType<RepositoryMemorySupport[]>(updatedEvent.RepositoryMemoryItems[0].Supports)[0] = support with { Path = "changed-update-event" };
        var memoryEvent = Assert.Single(observed.OfType<MemoriesIncluded>());
        Assert.Equal(support, memoryEvent.Items[0].Supports[0]);
        Assert.IsType<RepositoryMemorySupport[]>(memoryEvent.Items[0].Supports)[0] = support with { Path = "changed-memory-event" };
        var dispatched = Assert.IsType<ContextInspectionProjection>(assembler.GetInspection(request.RunId));
        Assert.Equal(expectedInclusion, Assert.Single(dispatched.RepositoryMemoryDispatch!.Inclusions));
        Assert.Equal(support, dispatched.RepositoryMemoryItems[0].Supports[0]);
        Assert.IsType<RepositoryMemoryInclusion[]>(dispatched.RepositoryMemoryDispatch.Inclusions)[0] = expectedInclusion with { Revision = 777 };
        Assert.Equal(expectedInclusion, Assert.Single(assembler.GetInspection(request.RunId)!.RepositoryMemoryDispatch!.Inclusions));
    }

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
