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
    /// <summary>Memory changes preserve the completed-history prefix and cannot leave deleted text in the next request.</summary>
    [Fact]
    public static async Task Changed_memories_follow_reusable_history()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var retriever = new TestRetriever(CreateEntry("Old memory canary."));
        var assembler = CreateAssembler(events, retriever, conversationStore: fixture.Store);
        var request = CreateRequest(fixture);
        var priorRun = RunId.New();
        foreach (var (role, content) in new[] { (ConversationRole.User, "prior question"), (ConversationRole.Assistant, "prior answer") })
        {
            await fixture.Store.ArchiveMessageAsync(new ConversationMessage
            {
                Id = ConversationMessageId.New(), SessionId = request.SessionId, RunId = priorRun, Sequence = 0,
                Role = role, Content = content, ContentHash = "pending", EstimatedTokens = 4, OccurredAt = DateTimeOffset.UtcNow,
            });
        }

        var first = await assembler.AssembleAsync(request);
        retriever.Entry = retriever.Entry with { Text = "New memory canary.", Revision = 4, ContentHash = "changed-content" };
        var changed = await assembler.AssembleAsync(request);
        var removed = await assembler.AssembleAsync(request with { RepositoryMemoriesEnabled = false });
        var firstMessages = first.Messages!.ToList();
        var boundary = firstMessages.FindIndex(message => message.SectionId == "repository-memory");
        Assert.True(boundary > firstMessages.FindLastIndex(message => message.SectionId == "recent-assistant"));
        Assert.Contains(firstMessages.Take(boundary), message => message.SectionId == "recent-assistant");
        Assert.Equal(ModelMessageRole.HostContext, firstMessages[boundary].Role);
        var prefix = firstMessages.Take(boundary).Select(message => (message.Role, message.GetModelVisibleContent()));
        Assert.Equal(prefix, changed.Messages!.Take(boundary).Select(message => (message.Role, message.GetModelVisibleContent())));
        Assert.Equal(prefix, removed.Messages!.Take(boundary).Select(message => (message.Role, message.GetModelVisibleContent())));
        Assert.DoesNotContain(changed.Messages!, message => message.GetModelVisibleContent().Contains("Old memory canary.", StringComparison.Ordinal));
        Assert.DoesNotContain(removed.Messages!, message => message.SectionId == "repository-memory");
    }

    /// <summary>Stateless and denied-tool policy suppress retrieval itself as well as memory text.</summary>
    [Theory]
    [InlineData(ConversationContextMode.Stateless, true, 0)]
    [InlineData(ConversationContextMode.ConversationAware, false, 0)]
    [InlineData(ConversationContextMode.GovernedMemoryOnly, true, 1)]
    public static async Task Context_mode_and_tool_admission_gate_memory(ConversationContextMode mode, bool enabled, int expected)
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var retriever = new TestRetriever(CreateEntry("bounded memory"));
        var assembler = CreateAssembler(events, retriever);

        var result = await assembler.AssembleAsync(CreateRequest(fixture) with
        {
            ConversationModeOverride = mode,
            RepositoryMemoriesEnabled = enabled,
        });

        Assert.Equal(expected, retriever.Calls);
        Assert.Equal(expected, result.RepositoryMemoryInclusions?.Count);
        Assert.Equal(expected, result.Inspection.RepositoryMemoryItems.Count(item => item.Included));
        Assert.Equal(expected > 0, result.ModelInput.Contains("Repository memories that may be helpful", StringComparison.Ordinal));
    }

    /// <summary>Only stable identity and escaped text enter model memory blocks, independent of usage metadata.</summary>
    [Fact]
    public static async Task Usage_does_not_change_memory_prompt_and_preview_does_not_account()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var entry = CreateEntry("Use <typed> results & cancellation.");
        var retriever = new TestRetriever(entry);
        var assembler = CreateAssembler(events, retriever);
        var request = CreateRequest(fixture);
        var first = await assembler.AssembleAsync(request);
        retriever.Entry = entry with { InclusionCount = 500, LastIncludedAt = DateTimeOffset.UtcNow.AddDays(1) };

        var next = await assembler.AssembleAsync(request);

        var memory = Assert.Single(first.Messages ?? [], message => message.SectionId == "repository-memory").GetModelVisibleContent();
        Assert.Equal(
            TestPromptLoader.Instance.Get(PromptFileNames.SystemRepositoryMemoryGuidance)
            + $"\n<repository_memory untrusted=\"true\">\n<memory id=\"{entry.Id.Value:D}\">Use &lt;typed&gt; results &amp; cancellation.</memory>\n</repository_memory>",
            memory);
        Assert.Contains("Use them only when relevant to the current request.", memory, StringComparison.Ordinal);
        Assert.Equal(first.ModelInput, next.ModelInput);
        Assert.Equal(entry.Revision, Assert.Single(first.RepositoryMemoryInclusions ?? []).Revision);
        Assert.Equal(0, entry.InclusionCount);
    }

    /// <summary>Included sensitivity participates in ordinary configured profile selection.</summary>
    [Fact]
    public static async Task Included_memory_sets_sensitivity_and_reports_branch_contributions()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var retriever = new TestRetriever(CreateEntry("sensitive reference"));
        var resolver = new TestModelResolver();
        var assembler = CreateAssembler(events, retriever, resolver);

        var result = await assembler.AssembleAsync(CreateRequest(fixture));

        Assert.True(resolver.ContainsSensitiveData);
        var projection = Assert.Single(result.Inspection.RepositoryMemoryItems);
        Assert.Equal(1, projection.LexicalRank);
        Assert.Equal(2, projection.SemanticRank);
        Assert.Equal(0.9, projection.CosineSimilarity);
        Assert.Equal(7.25, projection.CrossEncoderScore);
        var memory = Assert.Single(result.Messages ?? [], message => message.SectionId == "repository-memory").GetModelVisibleContent();
        Assert.DoesNotContain("7.25", memory, StringComparison.Ordinal);
    }

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

    /// <summary>Assembly previews never report submission; the explicit dispatch hook records the receipt separately.</summary>
    [Fact]
    public static async Task Dispatch_diagnostics_require_an_explicit_submission_hook()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var assembler = CreateAssembler(events, new TestRetriever(CreateEntry("memory")));
        var request = CreateRequest(fixture);
        var assembled = await assembler.AssembleAsync(request);
        Assert.Null(assembled.Inspection.RepositoryMemoryDispatch);

        await assembler.UpdateRepositoryMemoryDispatchInspectionAsync(
            request.SessionId,
            request.RunId,
            new RepositoryMemoryDispatchInspection(assembled.RepositoryMemoryInclusions ?? [], "recorded", 1.25));

        var inspection = assembler.GetInspection(request.RunId);
        Assert.Equal("recorded", inspection?.RepositoryMemoryDispatch?.Outcome);
        Assert.Single(inspection?.RepositoryMemoryDispatch?.Inclusions ?? []);
        Assert.Equal(assembled.ModelInput, (await assembler.AssembleAsync(request)).ModelInput);
    }

    private static RepositoryMemoryEntry CreateEntry(string text) => new()
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

    private static ContextAssemblyRequest CreateRequest(ConversationFixture fixture) => new()
    {
        SessionId = SessionId.New(),
        RunId = RunId.New(),
        Phase = RunPhase.EvidenceCollection,
        RepositoryPath = fixture.DirectoryPath,
        RepositoryIdentity = MemoryTestData.Repository,
        Task = new TaskSpecification("current task intent", []),
    };

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
        public TestRetriever(RepositoryMemoryEntry entry) => Entry = entry;

        public RepositoryMemoryEntry Entry { get; set; }

        public int Calls { get; private set; }

        public RepositoryMemoryRetrievalRequest? Request { get; private set; }

        public Task<RepositoryMemoryRetrievalResult> RetrieveAsync(RepositoryMemoryRetrievalRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Request = request;
            return Task.FromResult(new RepositoryMemoryRetrievalResult([new RepositoryMemoryRetrievalCandidate(Entry, 0.03, 1, 2, 0.9, 7.25)], []));
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
