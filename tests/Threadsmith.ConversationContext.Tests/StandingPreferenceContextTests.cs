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
    /// <summary>Standing-only repositories never invoke retrieval models, even with an empty query or disabled situational slots.</summary>
    [Theory]
    [InlineData("", 3)]
    [InlineData("explain a compiler error", 3)]
    [InlineData("explain a compiler error", 0)]
    public static async Task Standing_preferences_do_not_depend_on_the_query(string query, int maximum)
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        var saved = await service.ExecuteAsync(MemoryTestData.Operation("add", "User prefers to be addressed as 'sir'.") with
        {
            MemoryType = RepositoryMemoryType.StandingPreference,
        });
        var calls = generator.Calls;
        generator.Generate = _ => throw new InvalidOperationException("No retrieval inference is needed.");
        var encoder = new RecordingCrossEncoder();
        using var retriever = new HybridRepositoryMemoryRetriever(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator, encoder);

        var result = await retriever.RetrieveAsync(Query(query) with
        {
            Options = new RepositoryMemoryOptions { RerankerEnabled = true, SemanticMinimum = 1, MaxRepoMemoriesInContext = maximum },
        });

        Assert.Equal(saved.Id, Assert.Single(result.StandingPreferences).Id);
        Assert.Empty(result.Selected);
        Assert.Equal(calls, generator.Calls);
        Assert.Empty(encoder.Documents);
    }

    /// <summary>Mixed stores preserve situational lexical/semantic ranking and give all standing entries separate admission.</summary>
    [Fact]
    public static async Task Standing_preferences_do_not_consume_situational_slots_or_reranker_candidates()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var service = MemoryTestData.CreateService(fixture, generator);
        foreach (var text in (string[])["build errors in compiler", "fix build errors from project", "build errors after restore", "diagnose build errors"])
        {
            await service.ExecuteAsync(MemoryTestData.Operation("add", text));
        }

        var encoder = new RecordingCrossEncoder();
        using var retriever = new HybridRepositoryMemoryRetriever(new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString), generator, encoder);
        var before = await retriever.RetrieveAsync(Query("build errors"));
        for (var index = 0; index < 4; index++)
        {
            await service.ExecuteAsync(MemoryTestData.Operation("add", $"When discussing build errors, observe preference {index}.") with
            {
                MemoryType = RepositoryMemoryType.StandingPreference,
            });
        }

        var after = await retriever.RetrieveAsync(Query("build errors"));
        var ranked = await retriever.RetrieveAsync(Query("build errors") with { Options = new RepositoryMemoryOptions { RerankerEnabled = true } });

        Assert.Equal(
            before.Selected.Select(item => (item.Entry.Id, item.Score, item.LexicalRank, item.SemanticRank)),
            after.Selected.Select(item => (item.Entry.Id, item.Score, item.LexicalRank, item.SemanticRank)));
        Assert.Equal(3, ranked.Selected.Count);
        Assert.Equal(4, ranked.StandingPreferences.Count);
        Assert.Equal(4, encoder.Documents.Count);
        Assert.DoesNotContain(encoder.Documents, text => text.Contains("preference", StringComparison.Ordinal));
    }

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

    /// <summary>Every standing preference survives the old memory framing limit and contributes a real inclusion receipt.</summary>
    [Fact]
    public static async Task All_twenty_standing_preferences_are_required_context()
    {
        await using var fixture = await ConversationFixture.CreateAsync();
        await using var events = new DomainEventStream();
        var preferences = Enumerable.Range(0, 20).Select(index => Entry($"Preference {index}: " + new string('x', 600))).ToArray();
        var retriever = new FixedRetriever(preferences, []);
        var resolver = new FixedModelResolver(32_000);
        var assembler = CreateAssembler(events, retriever, resolver);

        var result = await assembler.AssembleAsync(Request(fixture));

        Assert.Equal(20, result.RepositoryMemoryInclusions?.Count);
        Assert.Equal(20, result.Inspection.RepositoryMemoryItems.Count(item => item.Included));
        Assert.True(resolver.ContainsSensitiveData);
        foreach (var entry in preferences)
        {
            Assert.Contains(entry.Text, result.ModelInput, StringComparison.Ordinal);
        }

        var memory = Assert.Single(result.Messages ?? [], message => message.SectionId == "repository-memory").GetModelVisibleContent();
        Assert.Contains("Standing preferences", memory, StringComparison.Ordinal);
        Assert.DoesNotContain("Repository memories that may be helpful", memory, StringComparison.Ordinal);
        Assert.All(result.Inspection.RepositoryMemoryItems, item =>
        {
            Assert.Equal(RepositoryMemoryType.StandingPreference, item.MemoryType);
            Assert.Null(item.Score);
            Assert.Null(item.CrossEncoderScore);
        });
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

    private static RepositoryMemoryRetrievalRequest Query(string query) => new() { RepositoryIdentity = MemoryTestData.Repository, CurrentInstruction = query };

    private static RepositoryMemoryEntry Entry(string text) => new()
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

    private static ContextAssemblyRequest Request(ConversationFixture fixture) => new()
    {
        SessionId = SessionId.New(),
        RunId = RunId.New(),
        Phase = RunPhase.EvidenceCollection,
        RepositoryPath = fixture.DirectoryPath,
        RepositoryIdentity = MemoryTestData.Repository,
        Task = new TaskSpecification("Explain a compiler error", []),
    };

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

        public FixedModelResolver(int budget) => _budget = budget;

        public int MaximumInputTokenBudget => _budget;

        internal bool ContainsSensitiveData { get; private set; }

        public ModelResolution Resolve(WorkloadClass workloadClass, ModelCapabilitySet requiredCapabilities, ModelSelectionConstraints constraints, ModelProfileId? defaultModelProfileId = null)
        {
            ContainsSensitiveData = constraints.ContainsSensitiveData;
            return new ModelResolution(new ModelProfileId(Guid.NewGuid()), _budget, 0, [], [], []);
        }
    }

    private sealed class RecordingCrossEncoder : ITextCrossEncoder
    {
        public TextCrossEncoderModelDescriptor Model { get; } = new("fixture", 512, 64);

        internal IReadOnlyList<string> Documents { get; private set; } = [];

        public Task<IReadOnlyList<TextCrossEncoderScore>> ScoreAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Documents = documents.ToArray();
            return Task.FromResult<IReadOnlyList<TextCrossEncoderScore>>([.. documents.Select(_ => new TextCrossEncoderScore(1, 12, false))]);
        }
    }
}
