namespace Threadsmith.Embeddings.Local.Tests;

using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Xunit;

/// <summary>Verifies local embedding behavior at its host-owned boundary.</summary>
public sealed class RealEmbeddingQualityTests
{
    /// <summary>Evaluates the production default on the frozen calibration and held-out queries.</summary>
    [Fact(Explicit = true)]
    [Trait("Category", "Integration")]
    public static async Task FrozenRetrievalFixtureRetainsCalibratedMinimum()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "retrieval-fixture.json"), cancellationToken));
        using var database = new FixtureDatabase();
        var connectionString = database.ConnectionString;
        await new SqliteEventStore(connectionString).InitializeAsync(cancellationToken);
        await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync(cancellationToken);
        var store = new SqliteManagedRepositoryMemoryStore(connectionString);
        await using var generator = new LocalTextEmbeddingGenerator();
        var identities = new Dictionary<RepositoryMemoryId, string>();
        foreach (var item in fixture.RootElement.GetProperty("memories").EnumerateArray())
        {
            var text = item.GetProperty("text").GetString() ?? string.Empty;
            var embedding = await generator.GenerateAsync(text, cancellationToken);
            var result = await store.AddAsync("fixture", new RepositoryMemoryWrite { Text = text, Origin = RepositoryMemoryOrigin.Manual }, generator.Model, embedding, new RepositoryMemoryOptions(), cancellationToken);
            Assert.NotNull(result.Entry);
            identities.Add(result.Entry.Id, item.GetProperty("id").GetString() ?? string.Empty);
        }

        using var allRetriever = new HybridRepositoryMemoryRetriever(store, generator);
        var examples = new List<Example>();
        foreach (var query in fixture.RootElement.GetProperty("queries").EnumerateArray())
        {
            var request = new RepositoryMemoryRetrievalRequest
            {
                RepositoryIdentity = "fixture",
                CurrentInstruction = query.GetProperty("text").GetString() ?? string.Empty,
                TaskIntent = query.TryGetProperty("taskIntent", out var intent) ? intent.GetString() : null,
                Options = new RepositoryMemoryOptions { MaxRepoMemoriesInContext = 20, SemanticMinimum = -1 },
            };
            var result = await allRetriever.RetrieveAsync(request, cancellationToken);
            var candidates = result.Selected.Select(candidate => new Candidate(
                identities[candidate.Entry.Id], candidate.Entry.Id.ToString(), candidate.LexicalRank, candidate.CosineSimilarity ?? -1)).ToArray();
            examples.Add(new Example(query.GetProperty("split").GetString() ?? string.Empty, request, [.. query.GetProperty("expected").EnumerateArray().Select(value => value.GetString() ?? string.Empty)], candidates));
        }

        var calibration = Enumerable.Range(20, 56).Select(value => value / 100d).Select(threshold =>
        {
            var metrics = Measure(examples.Where(example => example.Split == "calibration"), threshold, "semantic");
            return new { Threshold = threshold, metrics.FalseInclusions, metrics.MissedRelevant, Loss = (2 * metrics.FalseInclusions) + metrics.MissedRelevant };
        }).OrderBy(value => value.Loss).ThenBy(value => value.MissedRelevant).ThenByDescending(value => value.Threshold).ToArray();
        const double minimum = RepositoryMemoryOptions.DefaultSemanticMinimum;
        var retained = Assert.Single(calibration, candidate => candidate.Threshold == minimum);
        using var production = new HybridRepositoryMemoryRetriever(store, generator);
        var outcomes = new List<object>();
        foreach (var example in examples)
        {
            var actual = await production.RetrieveAsync(example.Request with { Options = new RepositoryMemoryOptions() }, cancellationToken);
            var selected = actual.Selected.Select(candidate => identities[candidate.Entry.Id]).ToArray();
            Assert.Equal(Rank(example.Candidates, minimum, "hybrid"), selected);
            outcomes.Add(new { example.Split, example.Request.CurrentInstruction, example.Expected, Selected = selected, example.Candidates });
        }

        var heldOut = examples.Where(example => example.Split == "heldout").ToArray();
        var hybrid = Measure(heldOut, minimum, "hybrid");
        var destination = Environment.GetEnvironmentVariable("THREADSMITH_EMBEDDING_REPORT");
        if (!string.IsNullOrWhiteSpace(destination))
        {
            var report = new
            {
                FixtureVersion = fixture.RootElement.GetProperty("version").GetInt32(),
                SemanticMinimum = minimum,
                HighestEquivalentCalibrationMinimum = calibration[0].Threshold,
                Calibration = calibration,
                HeldOut = new { Lexical = Measure(heldOut, minimum, "lexical"), Semantic = Measure(heldOut, minimum, "semantic"), Hybrid = hybrid },
                Outcomes = outcomes,
            };
            await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        }

        Assert.Equal(calibration[0].Loss, retained.Loss);
        Assert.Equal(calibration[0].MissedRelevant, retained.MissedRelevant);
        // Mandatory semantic admission intentionally excludes this lexical-only match.
        var lexicalOnlyQuery = Assert.Single(heldOut, example => example.Expected.SequenceEqual(["git-permission"]));
        var lexicalMatch = Assert.Single(lexicalOnlyQuery.Candidates, candidate => candidate.Id == "git-permission");
        Assert.NotNull(lexicalMatch.LexicalRank);
        Assert.InRange(lexicalMatch.Cosine, -1, minimum);
        Assert.Empty(Rank(lexicalOnlyQuery.Candidates, minimum, "hybrid"));
        Assert.Equal(0, Measure(heldOut.Where(example => example != lexicalOnlyQuery), minimum, "hybrid").MissedRelevant);
        Assert.Equal(1, hybrid.MissedRelevant);
        Assert.InRange(hybrid.FalseInclusions, 0, 1);
        Assert.All(heldOut.Where(example => example.Expected.Length == 0), example => Assert.Empty(Rank(example.Candidates, minimum, "hybrid")));
    }

    private static Quality Measure(IEnumerable<Example> examples, double threshold, string branch)
    {
        var falseInclusions = 0;
        var missedRelevant = 0;
        foreach (var example in examples)
        {
            var actual = Rank(example.Candidates, threshold, branch);
            falseInclusions += actual.Except(example.Expected).Count();
            missedRelevant += example.Expected.Except(actual).Count();
        }

        return new Quality(falseInclusions, missedRelevant);
    }

    private static string[] Rank(Candidate[] candidates, double threshold, string branch)
    {
        var semantic = candidates.Where(value => value.Cosine > threshold).OrderByDescending(value => value.Cosine).ThenBy(value => value.StableId, StringComparer.Ordinal).Select((value, index) => (value.Id, Rank: index + 1)).ToDictionary(value => value.Id, value => value.Rank);
        return [.. candidates.Where(value => branch == "lexical" || semantic.ContainsKey(value.Id)).Select(value => new
        {
            value.Id,
            value.StableId,
            Score = (branch != "semantic" && value.LexicalRank is { } lexical ? 1d / (60 + lexical) : 0)
                + (branch != "lexical" && semantic.TryGetValue(value.Id, out var rank) ? 1d / (60 + rank) : 0),
        }).Where(value => value.Score > 0).OrderByDescending(value => value.Score).ThenBy(value => value.StableId, StringComparer.Ordinal).Take(3).Select(value => value.Id)];
    }

    private sealed class FixtureDatabase : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "threadsmith-embedding-" + Guid.NewGuid().ToString("N"));

        public FixtureDatabase()
        {
            Directory.CreateDirectory(_directory);
            ConnectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "fixture.db"), Pooling = false }.ToString();
        }

        public string ConnectionString { get; }

        public void Dispose()
        {
            // Delete only the individually named files owned by this fixture, then its empty directory.
            foreach (var name in (string[])["fixture.db", "fixture.db-wal", "fixture.db-shm", "fixture.db-journal"])
            {
                File.Delete(Path.Combine(_directory, name));
            }

            Directory.Delete(_directory);
        }
    }

    private sealed record Candidate(string Id, string StableId, int? LexicalRank, double Cosine);

    private sealed record Example(string Split, RepositoryMemoryRetrievalRequest Request, string[] Expected, Candidate[] Candidates);

    private sealed record Quality(int FalseInclusions, int MissedRelevant);
}
