namespace Threadsmith.ConversationContext.Tests;

using Microsoft.Data.Sqlite;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Standing preferences are required context independent of situational retrieval.</summary>
public static class StandingPreferenceContextTests
{
    /// <summary>Unavailable semantic inference and reranking cannot suppress standing preferences.</summary>
    [Fact]
    public static async Task Retrieval_failure_keeps_standing_preferences()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "situational reference"));
        var preference = await service.ExecuteAsync(MemoryTestData.Operation("add", "Address the user as sir.") with { MemoryType = RepositoryMemoryType.StandingPreference });
        generator.Generate = _ => throw new InvalidOperationException("Encoder unavailable.");
        using var retriever = new HybridRepositoryMemoryRetriever(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator);

        var result = await retriever.RetrieveAsync(Query("unrelated request") with { Options = new RepositoryMemoryOptions { RerankerEnabled = true } });

        Assert.Equal(preference.Id, Assert.Single(result.StandingPreferences).Id);
        Assert.Empty(result.Selected);
        Assert.Contains(result.Diagnostics, text => text.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Changing only the type invalidates cached retrieval and moves the same identity between admission paths.</summary>
    [Fact]
    public static async Task Type_changes_invalidate_cached_admission()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        var saved = await service.ExecuteAsync(MemoryTestData.Operation("add", "Address the user as sir."));
        using var retriever = new HybridRepositoryMemoryRetriever(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator);
        var query = Query("unrelated request");
        Assert.Single((await retriever.RetrieveAsync(query)).Selected);
        Assert.True((await retriever.RetrieveAsync(query)).RankingCacheHit);

        var generationCalls = generator.Calls;
        generator.Generate = _ => throw new InvalidOperationException("Metadata changes must not use embeddings.");
        var updated = await service.ExecuteAsync(MemoryTestData.Operation("update", "Address the user as sir.", saved.Id) with { MemoryType = RepositoryMemoryType.StandingPreference });
        var standing = await retriever.RetrieveAsync(query);
        await service.ExecuteAsync(MemoryTestData.Operation("update", "Address the user as sir.", saved.Id) with { MemoryType = RepositoryMemoryType.Situational });
        var situational = await retriever.RetrieveAsync(query);

        Assert.Equal(generationCalls, generator.Calls);
        Assert.Equal("updated", updated.Outcome);
        Assert.Equal(saved.Id, Assert.Single(standing.StandingPreferences).Id);
        Assert.Empty(standing.Selected);
        Assert.False(standing.RankingCacheHit);
        Assert.Empty(situational.StandingPreferences);
        Assert.Equal(saved.Id, Assert.Single(situational.Selected).Entry.Id);
        Assert.False(situational.RankingCacheHit);
    }

    /// <summary>Final request pressure removes optional matches but retains every standing preference.</summary>
    [Fact]
    public static async Task Final_capacity_reduction_retains_standing_preferences()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var preference = Entry("Address the user as <sir> & preserve cancellation.");
        var optional = Entry(new string('s', 1_800)) with { MemoryType = RepositoryMemoryType.Situational };
        var baseline = await CreateAssembler(events, new FixedRetriever([preference], []), new FixedModelResolver(32_000)).AssembleAsync(Request(fixture));
        var requiredBudget = Math.Max(baseline.Inspection.WireInputTokens, baseline.Inspection.EstimatedTokens) + 30;
        var result = await CreateAssembler(events, new FixedRetriever([preference], [optional]), new FixedModelResolver(requiredBudget)).AssembleAsync(Request(fixture));

        Assert.Equal(preference.Id, Assert.Single(result.RepositoryMemoryInclusions ?? []).Id);
        Assert.Contains("Address the user as &lt;sir&gt; &amp; preserve cancellation.", result.ModelInput, StringComparison.Ordinal);
        Assert.Contains(result.Inspection.RepositoryMemoryItems, item => item.Id == optional.Id && !item.Included);
    }

    /// <summary>Capacity exhaustion is explicit instead of silently dropping standing preferences.</summary>
    [Fact]
    public static async Task Insufficient_model_capacity_does_not_omit_a_standing_preference()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var assembler = CreateAssembler(events, new FixedRetriever([Entry(new string('p', 2_000))], []), new FixedModelResolver(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() => assembler.AssembleAsync(Request(fixture)));
    }

    /// <summary>Standing preferences respect the same explicit memory and stateless gates as other persisted context.</summary>
    [Theory]
    [InlineData(ConversationContextMode.Stateless, true)]
    [InlineData(ConversationContextMode.ConversationAware, false)]
    public static async Task Disabled_memory_does_not_inject_standing_preferences(ConversationContextMode mode, bool enabled)
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var retriever = new FixedRetriever([Entry("Address the user as sir.")], []);

        var result = await CreateAssembler(events, retriever, new FixedModelResolver(32_000)).AssembleAsync(Request(fixture) with
        {
            ConversationModeOverride = mode,
            RepositoryMemoriesEnabled = enabled,
        });

        Assert.Empty(result.RepositoryMemoryInclusions ?? []);
        Assert.Equal(0, retriever.Calls);
    }

    /// <summary>A broken search index preserves standing context; an unreadable memory store stops assembly explicitly.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task Storage_failure_cannot_silently_remove_standing_preferences(bool entriesReadable)
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        var saved = await service.ExecuteAsync(MemoryTestData.Operation("add", "Address the user as sir.") with
        {
            MemoryType = RepositoryMemoryType.StandingPreference,
        });
        await using var connection = new SqliteConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = entriesReadable ? "DROP TABLE managed_memories_fts;" : "DROP TABLE managed_memory_repositories;";
        await command.ExecuteNonQueryAsync();
        var calls = generator.Calls;
        using var retriever = new HybridRepositoryMemoryRetriever(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator);
        var assembler = CreateAssembler(events, retriever, new FixedModelResolver(32_000));

        if (entriesReadable)
        {
            var result = await assembler.AssembleAsync(Request(fixture));
            Assert.Equal(saved.Id, Assert.Single(result.RepositoryMemoryInclusions ?? []).Id);
            Assert.Contains("Address the user as sir.", result.ModelInput, StringComparison.Ordinal);
        }
        else
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => assembler.AssembleAsync(Request(fixture)));
            Assert.Contains("standing preferences cannot be assembled", failure.Message, StringComparison.Ordinal);
        }

        Assert.Equal(calls, generator.Calls);
    }

    /// <summary>Verifies independent memory routing and intent eligibility.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public static async Task Enforced_mismatches_never_remove_required_preference_text(bool intelligence, bool automatic)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        var preferences = new List<RepositoryMemoryEntry>();
        foreach (var text in (string[])["Address the user as sir.", "Use concise responses."])
        {
            var saved = await service.ExecuteAsync(
                MemoryTestData.Operation("add", text) with
                {
                    MemoryType = RepositoryMemoryType.StandingPreference,
                    Concepts = ["unrelated"],
                },
                ct);
            preferences.Add(saved.Entry!);
        }

        var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString);
        const string intelligenceText = "Compiler errors reveal subsystem design history.";
        await store.AddAsync(
            MemoryTestData.Repository,
            new RepositoryMemoryWrite { Text = intelligenceText, Origin = RepositoryMemoryOrigin.Model, Ownership = RepositoryMemoryOwnership.Intelligence },
            generator.Model,
            await generator.GenerateAsync(intelligenceText, ct),
            new RepositoryMemoryOptions(),
            ct);
        using var retriever = new HybridRepositoryMemoryRetriever(store, generator);
        var request = Request(fixture);
        request = request with
        {
            IntelligenceEnabled = intelligence,
            RepositoryMemoryOptions = new()
            {
                SituationalConceptFilterMode = SituationalConceptFilterMode.Enforce,
                Intelligence = new() { Enabled = intelligence, AutomaticRecallEnabled = automatic, Recall = new() },
            },
            MemoryIntentEpoch = 1,
            MemoryIntentConcepts = new(MemoryTestData.Repository, request.RunId, 1, [new(1, ToolInvocationId.New(), true, ["compiler"])]),
        };
        var result = await CreateAssembler(events, retriever, new FixedModelResolver(32_000)).AssembleAsync(request, ct);
        var memory = Assert.Single(result.Messages!, message => message.SectionId == "repository-memory").GetModelVisibleContent();
        var positions = preferences.OrderBy(entry => entry.Id.Value).Select(entry => memory.IndexOf(entry.Text, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.Order(), positions);
        Assert.Equal(intelligence && automatic, memory.Contains(intelligenceText, StringComparison.Ordinal));
    }

    /// <summary>Shadow instrumentation leaves actual submitted context identical to Off.</summary>
    [Fact]
    public static async Task Shadow_keeps_submitted_context_identical()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        await service.ExecuteAsync(MemoryTestData.Operation("add", "Compiler error guidance") with { Concepts = ["unrelated"] }, ct);
        using var retriever = new HybridRepositoryMemoryRetriever(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator);
        var request = Request(fixture);
        request = request with
        {
            MemoryIntentConcepts = new(MemoryTestData.Repository, request.RunId, 0, [new(1, ToolInvocationId.New(), true, ["compiler"])]),
            RepositoryMemoryOptions = new(),
        };
        var baseline = await CreateAssembler(events, retriever, new FixedModelResolver(32_000)).AssembleAsync(request, ct);
        var shadow = await CreateAssembler(events, retriever, new FixedModelResolver(32_000)).AssembleAsync(
            request with { RepositoryMemoryOptions = new() { SituationalConceptFilterMode = SituationalConceptFilterMode.Shadow } },
            ct);
        Assert.Equal(baseline.ModelInput, shadow.ModelInput);
        Assert.Equal(baseline.RepositoryMemoryInclusions, shadow.RepositoryMemoryInclusions);
    }

    private static RepositoryMemoryRetrievalRequest Query(string query)
    {
        return new() { RepositoryIdentity = MemoryTestData.Repository, CurrentInstruction = query };
    }

    private static RepositoryMemoryEntry Entry(string text)
    {
        return new()
        {
            Id = RepositoryMemoryId.New(),
            RepositoryIdentity = MemoryTestData.Repository,
            Text = text,
            ContentHash = "fixture",
            MemoryType = RepositoryMemoryType.StandingPreference,
            Origin = RepositoryMemoryOrigin.Manual,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
    }

    private static ContextAssemblyRequest Request(ConversationFixture fixture)
    {
        return new()
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            Phase = RunPhase.EvidenceCollection,
            RepositoryPath = fixture.DirectoryPath,
            RepositoryIdentity = MemoryTestData.Repository,
            Task = new TaskSpecification("Explain a compiler error", []),
        };
    }

    private static ContextAssembler CreateAssembler(IDomainEventStream events, IHybridRepositoryMemoryRetriever retriever, IModelResolver resolver)
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
            repositoryMemoryRetriever: retriever);
    }

    private sealed class FixedRetriever : IHybridRepositoryMemoryRetriever
    {
        private readonly RepositoryMemoryRetrievalResult _result;

        public FixedRetriever(IReadOnlyList<RepositoryMemoryEntry> standing, IReadOnlyList<RepositoryMemoryEntry> situational)
        {
            _result = new RepositoryMemoryRetrievalResult([.. situational.Select(entry => new RepositoryMemoryRetrievalCandidate(entry, 1, 1, 1, 1))], [])
            {
                StandingPreferences = standing,
            };
        }

        internal int Calls { get; private set; }

        public Task<RepositoryMemoryRetrievalResult> RetrieveAsync(RepositoryMemoryRetrievalRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(_result);
        }
    }

    private sealed class FixedModelResolver : IModelResolver
    {
        private readonly int _budget;

        public FixedModelResolver(int budget)
        {
            _budget = budget;
        }

        public int MaximumInputTokenBudget => _budget;

        internal bool ContainsSensitiveData { get; private set; }

        public ModelResolution Resolve(WorkloadClass workloadClass, ModelCapabilitySet requiredCapabilities, ModelSelectionConstraints constraints, ModelProfileId? defaultModelProfileId = null)
        {
            ContainsSensitiveData = constraints.ContainsSensitiveData;
            return new ModelResolution(new ModelProfileId(Guid.NewGuid()), _budget, 0, [], [], []);
        }
    }
}
