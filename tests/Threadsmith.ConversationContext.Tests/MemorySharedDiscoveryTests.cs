namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Shared text and concept discovery through real persistence and consumer paths.</summary>
public static class MemorySharedDiscoveryTests
{
    /// <summary>Discovery feeds reranking without coupling consumer settings.</summary>
    [Theory]
    [InlineData(RepositoryMemoryOrigin.Manual, RepositoryMemoryType.Situational, false)]
    [InlineData(RepositoryMemoryOrigin.Manual, RepositoryMemoryType.Situational, true)]
    [InlineData(RepositoryMemoryOrigin.Model, RepositoryMemoryType.StandingPreference, false)]
    [InlineData(RepositoryMemoryOrigin.Model, RepositoryMemoryType.StandingPreference, true)]
    public static async Task Reconciliation_uses_concepts_and_full_text_ranking(RepositoryMemoryOrigin origin, RepositoryMemoryType type, bool fuzzy)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var baseline = MemoryTestData.CreateService(fixture, generator);
        var saved = await baseline.ExecuteAsync(MemoryTestData.Operation("add", "Dispose enumerators") with { Concepts = ["cancellation"], MemoryType = type }, ct);
        using var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString, termResolver: new TestResolver());
        var encoder = new MemoryScoreEncoder { Score = (query, _) => new(query == "Propagate tokens" ? 2 : 1, 12, false) };
        using var search = new HybridRepositoryMemoryRetriever(store, generator, encoder);
        var service = new RepositoryMemoryService(store, generator, new SecretOutputSanitizer(), search: search);
        var options = new RepositoryMemoryOptions
        {
            ReconciliationEnabled = true, ReconciliationSemanticMinimum = 1,
            ConceptRecallEnabled = false, MaxRepoMemoriesInContext = 0,
            ReconciliationConceptFuzzyEnabled = fuzzy, ReconciliationConceptFuzzyMaximumDistance = 20,
        };
        var proposal = MemoryTestData.Operation("add", "Propagate tokens") with { Concepts = [fuzzy ? "cancelation" : "cancellation"], Origin = origin, Options = options };
        var collision = await service.ExecuteAsync(proposal, ct);
        Assert.Equal("reconciliationRequired", collision.Outcome);
        Assert.Equal(saved.Id, Assert.Single(collision.Entries).Id);
        Assert.Equal(0, collision.SearchDetails?.HybridCandidates);
        Assert.Equal(1, collision.SearchDetails?.ConceptOnlyCandidates);
        Assert.Equal("reconciliationRequired", (await service.ExecuteAsync(proposal with { Text = "Independent advice" }, ct)).Outcome);
    }

    /// <summary>Discovery feeds reranking without coupling consumer settings.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task Fuzzy_text_discovery_is_shared_by_recall_and_reconciliation(bool reconcile)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var baseline = MemoryTestData.CreateService(fixture, generator);
        var saved = await baseline.ExecuteAsync(MemoryTestData.Operation("add", "cancellation"), ct);
        var resolver = new TestResolver();
        using var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString, termResolver: resolver);
        using var search = new HybridRepositoryMemoryRetriever(store, generator, new MemoryScoreEncoder());
        var options = new RepositoryMemoryOptions
        {
            SemanticMinimum = 1, ReconciliationSemanticMinimum = 1, RerankerEnabled = true,
            ReconciliationEnabled = true,
            Lexical = new() { FuzzyEnabled = true, FuzzyMaximumDistance = 20 },
            ReconciliationLexical = new() { FuzzyEnabled = true, FuzzyMaximumDistance = 20 },
        };
        if (reconcile)
        {
            var service = new RepositoryMemoryService(store, generator, new SecretOutputSanitizer(), search: search);
            var result = await service.ExecuteAsync(MemoryTestData.Operation("add", "cancelation") with { Options = options }, ct);
            Assert.Equal("reconciliationRequired", result.Outcome);
            Assert.Equal(saved.Id, Assert.Single(result.Entries).Id);
            Assert.Equal(RepositoryMemorySearchBranchStatus.Completed, result.SearchDetails?.FuzzyLexical);
        }
        else
        {
            var result = await search.SearchAsync(new RepositoryMemorySearchRequest { RepositoryIdentity = MemoryTestData.Repository, Query = "cancelation", Options = RepositoryMemorySearchOptions.ForRecall(options) }, ct);
            Assert.Equal(saved.Id, Assert.Single(result.Selected).Entry.Id);
            Assert.Equal("cancellation", Assert.Single(result.SearchDetails!.LexicalExpansions).Term);
            var disabled = await search.SearchAsync(new RepositoryMemorySearchRequest { RepositoryIdentity = MemoryTestData.Repository, Query = "cancelation", Options = RepositoryMemorySearchOptions.ForRecall(options with { Lexical = new() }) }, ct);
            Assert.Empty(disabled.Selected);
        }

        Assert.All(resolver.Vocabularies, snapshot => Assert.Equal(MemoryVocabularyKind.Text, snapshot.Kind));
    }

    /// <summary>Exact matching and failure boundaries remain truthful with bounded fuzzy expansion.</summary>
    [Fact]
    public static async Task Expansion_preserves_exact_matches_and_counts_original_terms_not_alternatives()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var baseline = MemoryTestData.CreateService(fixture, generator);
        var exact = await baseline.ExecuteAsync(MemoryTestData.Operation("add", "alpha beta"), ct);
        var fuzzy = await baseline.ExecuteAsync(MemoryTestData.Operation("add", "alpha cancellation"), ct);
        var oneTerm = await baseline.ExecuteAsync(MemoryTestData.Operation("add", "cancellation cancellationpolicy"), ct);
        await baseline.ExecuteAsync(MemoryTestData.Operation("add", "foreignword") with { RepositoryIdentity = "another-repository" }, ct);
        var resolver = new TestResolver();
        using var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString, termResolver: resolver);
        var terms = new[] { "alpha", "beta", "cancelation" };
        var plain = await store.GetSnapshotAsync(MemoryTestData.Repository, terms, ct);
        var expanded = await store.GetSnapshotAsync(MemoryTestData.Repository, terms, new() { FuzzyEnabled = true, FuzzyMaximumDistance = 20, MaximumExpansions = 1 }, ct);
        Assert.Equal(plain.LexicalMatches, expanded.LexicalMatches.Where(match => !match.IsFuzzy));
        Assert.Equal(exact.Id, expanded.LexicalMatches[0].Id);
        Assert.Contains(expanded.LexicalMatches, match => match.Id == fuzzy.Id && match.IsFuzzy);
        Assert.DoesNotContain(expanded.LexicalMatches, match => match.Id == oneTerm.Id);
        Assert.Single(expanded.LexicalExpansions);
        var grouped = await store.GetSnapshotAsync(MemoryTestData.Repository, ["cancelation", "absentword"], new() { FuzzyEnabled = true, FuzzyMaximumDistance = 20 }, ct);
        Assert.Empty(grouped.LexicalMatches);
        Assert.All(resolver.Vocabularies, snapshot => Assert.DoesNotContain("foreignword", snapshot.Vocabulary));
        Assert.All(resolver.Queries, query => Assert.DoesNotContain(query, new[] { "alpha", "beta" }));
    }

    /// <summary>Exact matching and failure boundaries remain truthful with bounded fuzzy expansion.</summary>
    [Fact]
    public static async Task Missing_fuzzy_dependency_preserves_exact_recall_but_cannot_authorize_a_write()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await ConversationFixture.CreateAsync();
        var generator = new TestMemoryEmbeddingGenerator();
        var baseline = MemoryTestData.CreateService(fixture, generator);
        var saved = await baseline.ExecuteAsync(MemoryTestData.Operation("add", "alpha beta"), ct);
        using var store = new SqliteManagedRepositoryMemoryStore(fixture.ConnectionString, termResolver: new TestResolver { Fail = true });
        using var search = new HybridRepositoryMemoryRetriever(store, generator, new MemoryScoreEncoder());
        var options = new RepositoryMemoryOptions
        {
            SemanticMinimum = 1, ReconciliationEnabled = true,
            Lexical = new() { FuzzyEnabled = true, FuzzyMaximumDistance = 20 },
            ReconciliationLexical = new() { FuzzyEnabled = true, FuzzyMaximumDistance = 20 },
        };
        var result = await search.SearchAsync(new RepositoryMemorySearchRequest { RepositoryIdentity = MemoryTestData.Repository, Query = "alpha beta cancelation", Options = RepositoryMemorySearchOptions.ForRecall(options) }, ct);
        Assert.Equal(saved.Id, Assert.Single(result.Selected).Entry.Id);
        Assert.False(result.IsComplete);
        Assert.Equal(RepositoryMemorySearchBranchStatus.Unavailable, result.SearchDetails?.FuzzyLexical);
        var service = new RepositoryMemoryService(store, generator, new SecretOutputSanitizer(), search: search);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(MemoryTestData.Operation("add", "alpha beta cancelation") with { Options = options }, ct));
        Assert.Single((await store.GetSnapshotAsync(MemoryTestData.Repository, [], ct)).Entries);
    }

    private sealed class TestResolver : IRepositoryMemoryTermResolver
    {
        internal bool Fail { get; init; }

        internal List<RepositoryMemoryVocabularySnapshot> Vocabularies { get; } = [];

        internal List<string> Queries { get; } = [];

        public Task<MemoryTermResolution> ResolveTermsAsync(RepositoryMemoryVocabularySnapshot snapshot, IReadOnlyList<string> queries, int maximumDistance, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Vocabularies.Add(snapshot);
            Queries.AddRange(queries);
            return Task.FromResult(Fail ? new MemoryTermResolution([], "Native lookup unavailable") : new MemoryTermResolution(
                queries.Where(query => query == "cancelation").SelectMany(query => snapshot.Vocabulary.Where(term => term.StartsWith("cancellation", StringComparison.Ordinal)).Select(term => new MemoryTermMatch(query, term, 10))).ToArray()));
        }
    }
}
