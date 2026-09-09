namespace Threadsmith.Embeddings.Local.Tests;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Xunit;

/// <summary>Verifies local embedding behavior at its host-owned boundary.</summary>
public sealed class RealEmbeddingQualityTests
{
    /// <summary>Verifies the declared embedding behavior without online model calls.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public static async Task CalibrateHeldOutQualityAndMeasureWarmLatency()
    {
        if (Environment.GetEnvironmentVariable("THREADSMITH_EMBEDDING_INTEGRATION") != "1")
        {
            Assert.Skip("Set THREADSMITH_EMBEDDING_INTEGRATION=1 to run the versioned offline real retrieval/latency fixture.");
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "retrieval-fixture.json"), cancellationToken));
        using var database = new FixtureDatabase();
        var connectionString = database.ConnectionString;
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await new ManagedRepositoryMemorySchemaMigration().ApplyAsync(connection, cancellationToken);
        var store = new SqliteManagedRepositoryMemoryStore(connectionString);
        await using var generator = new LocalTextEmbeddingGenerator();
        var watch = Stopwatch.StartNew();
        await generator.GenerateAsync("cold initialization timing", cancellationToken);
        var coldFirstCall = watch.Elapsed.TotalMilliseconds;
        var identities = new Dictionary<RepositoryMemoryId, string>();
        var memoryTimings = new List<double>();
        var queryTimings = new List<double>();
        var tokenLengths = new List<int>();
        foreach (var item in fixture.RootElement.GetProperty("memories").EnumerateArray())
        {
            var text = item.GetProperty("text").GetString() ?? string.Empty;
            watch.Restart();
            var embedding = await generator.GenerateAsync(text, cancellationToken);
            memoryTimings.Add(watch.Elapsed.TotalMilliseconds);
            tokenLengths.Add(embedding.InputTokenCount);
            var result = await store.AddAsync("fixture", new RepositoryMemoryWrite { Text = text, Origin = RepositoryMemoryOrigin.Manual }, generator.Model, embedding, new RepositoryMemoryOptions(), cancellationToken);
            Assert.NotNull(result.Entry);
            identities.Add(result.Entry.Id, item.GetProperty("id").GetString() ?? string.Empty);
        }

        using var allRetriever = new HybridRepositoryMemoryRetriever(store, generator, -1);
        var examples = new List<Example>();
        foreach (var query in fixture.RootElement.GetProperty("queries").EnumerateArray())
        {
            var request = new RepositoryMemoryRetrievalRequest
            {
                RepositoryIdentity = "fixture",
                CurrentInstruction = query.GetProperty("text").GetString() ?? string.Empty,
                TaskIntent = query.TryGetProperty("taskIntent", out var intent) ? intent.GetString() : null,
                Options = new RepositoryMemoryOptions { MaxRepoMemoriesInContext = 20 },
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
        var selectedMinimum = calibration[0].Threshold;
        Assert.Equal(LocalTextEmbeddingGenerator.SemanticMinimum, selectedMinimum);
        using var production = new HybridRepositoryMemoryRetriever(store, generator, selectedMinimum);
        var outcomes = new List<object>();
        var retrievalTimings = new List<double>();
        var completeTurnTimings = new List<double>();
        var cacheHits = 0;
        foreach (var example in examples)
        {
            watch.Restart();
            var actual = await production.RetrieveAsync(example.Request with { Options = new RepositoryMemoryOptions() }, cancellationToken);
            retrievalTimings.Add(watch.Elapsed.TotalMilliseconds);
            await store.RecordInclusionsAsync("fixture", RunId.New(), [.. actual.Selected.Select(candidate => new RepositoryMemoryInclusion(candidate.Entry.Id, candidate.Entry.Revision))], cancellationToken);
            completeTurnTimings.Add(watch.Elapsed.TotalMilliseconds);
            var predicted = Rank(example.Candidates, selectedMinimum, "hybrid");
            Assert.Equal(predicted, actual.Selected.Select(candidate => identities[candidate.Entry.Id]));
            var cached = await production.RetrieveAsync(example.Request with { Options = new RepositoryMemoryOptions() }, cancellationToken);
            if (cached.QueryEmbeddingCacheHit && cached.RankingCacheHit)
            {
                cacheHits++;
            }

            outcomes.Add(new { example.Split, Query = example.Request.CurrentInstruction, example.Request.TaskIntent, example.Expected, Bm25 = Rank(example.Candidates, selectedMinimum, "lexical"), Semantic = Rank(example.Candidates, selectedMinimum, "semantic"), Hybrid = predicted, Scores = example.Candidates });
        }

        // Repeated uncached inference measures CPU work separately from query-cache/database overhead.
        for (var iteration = 0; iteration < 60; iteration++)
        {
            var text = examples[iteration % examples.Count].Request.CurrentInstruction;
            watch.Restart();
            var result = await generator.GenerateAsync(text, cancellationToken);
            queryTimings.Add(watch.Elapsed.TotalMilliseconds);
            tokenLengths.Add(result.InputTokenCount);
            var memory = fixture.RootElement.GetProperty("memories")[iteration % identities.Count].GetProperty("text").GetString() ?? string.Empty;
            watch.Restart();
            await generator.GenerateAsync(memory, cancellationToken);
            memoryTimings.Add(watch.Elapsed.TotalMilliseconds);
        }

        var lookupTimings = new List<double>();
        var cacheTimings = new List<double>();
        var receiptTimings = new List<double>();
        var duplicateReceiptTimings = new List<double>();
        var receipt = new RepositoryMemoryInclusion(identities.Keys.First(), 1);
        var lastRun = RunId.New();
        for (var iteration = 0; iteration < 200; iteration++)
        {
            watch.Restart();
            await store.GetSnapshotAsync("fixture", ["repository", "notes"], cancellationToken);
            lookupTimings.Add(watch.Elapsed.TotalMilliseconds);
            watch.Restart();
            await production.RetrieveAsync(examples[0].Request with { Options = new RepositoryMemoryOptions() }, cancellationToken);
            cacheTimings.Add(watch.Elapsed.TotalMilliseconds);
            lastRun = RunId.New();
            watch.Restart();
            await store.RecordInclusionsAsync("fixture", lastRun, [receipt], cancellationToken);
            receiptTimings.Add(watch.Elapsed.TotalMilliseconds);
            watch.Restart();
            await store.RecordInclusionsAsync("fixture", lastRun, [receipt], cancellationToken);
            duplicateReceiptTimings.Add(watch.Elapsed.TotalMilliseconds);
        }

        var warmQuery = Percentiles(queryTimings);
        var report = new
        {
            FixtureVersion = 1,
            generator.Model,
            Runtime = RuntimeInformation.FrameworkDescription,
            OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            LogicalProcessors = Environment.ProcessorCount,
            IntraOpThreads = 2,
            InterOpThreads = 1,
            MaximumConcurrentInference = 1,
            Spinning = false,
            ColdFirstCallMs = coldFirstCall,
            ColdNativeSessionLoadMs = generator.SessionLoadDuration.TotalMilliseconds,
            WarmTurnBudgetMs = 25,
            CompleteUncachedRetrievalAndReceiptMs = Percentiles(completeTurnTimings),
            WarmMemoryEmbeddingMs = Percentiles(memoryTimings),
            WarmQueryEmbeddingMs = warmQuery,
            UncachedHybridRetrievalMs = Percentiles(retrievalTimings),
            SqliteLexicalSnapshotMs = Percentiles(lookupTimings),
            CachedHybridRetrievalMs = Percentiles(cacheTimings),
            NewInclusionReceiptMs = Percentiles(receiptTimings),
            DuplicateInclusionReceiptMs = Percentiles(duplicateReceiptTimings),
            Database = "Local file-backed SQLite, pooling disabled; 8 memories; default durability",
            InputTokenMinimum = tokenLengths.Min(),
            InputTokenMaximum = tokenLengths.Max(),
            QueryCacheHits = cacheHits,
            QueryCacheRequests = examples.Count,
            SemanticMinimum = selectedMinimum,
            CalibrationCriterion = "Minimum 2*falseInclusions + missedRelevant on semantic-only calibration; ties minimize misses then prefer higher threshold. Held-out examples never select threshold.",
            Calibration = calibration,
            HeldOut = new
            {
                Bm25 = Measure(examples.Where(example => example.Split == "heldout"), selectedMinimum, "lexical"),
                Semantic = Measure(examples.Where(example => example.Split == "heldout"), selectedMinimum, "semantic"),
                Hybrid = Measure(examples.Where(example => example.Split == "heldout"), selectedMinimum, "hybrid"),
            },
            Outcomes = outcomes,
        };
        var destination = Environment.GetEnvironmentVariable("THREADSMITH_EMBEDDING_REPORT");
        if (destination is not null)
        {
            await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        }

        Assert.Equal(examples.Count, cacheHits);
        var heldOut = Measure(examples.Where(example => example.Split == "heldout"), selectedMinimum, "hybrid");
        Assert.Equal(0, heldOut.MissedRelevant);
        Assert.InRange(heldOut.FalseInclusions, 0, 1);
        Assert.All(examples.Where(example => example.Expected.Length == 0), example => Assert.Empty(Rank(example.Candidates, selectedMinimum, "hybrid")));
    }

    private static string[] Rank(Candidate[] candidates, double threshold, string branch)
    {
        var semantic = candidates.Where(value => value.Cosine > threshold).OrderByDescending(value => value.Cosine).ThenBy(value => value.StableId, StringComparer.Ordinal).Select((value, index) => (value.Id, Rank: index + 1)).ToDictionary(value => value.Id, value => value.Rank);
        return [.. candidates.Select(value => new
        {
            value.Id,
            value.StableId,
            Score = (branch != "semantic" && value.LexicalRank is { } lexical ? 1d / (60 + lexical) : 0)
                + (branch != "lexical" && semantic.TryGetValue(value.Id, out var rank) ? 1d / (60 + rank) : 0),
        }).Where(value => value.Score > 0).OrderByDescending(value => value.Score).ThenBy(value => value.StableId, StringComparer.Ordinal).Take(3).Select(value => value.Id)];
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

    private static object Percentiles(List<double> values)
    {
        values.Sort();
        return new { Samples = values.Count, P50 = values[(int)Math.Ceiling(values.Count * 0.5) - 1], P95 = values[(int)Math.Ceiling(values.Count * 0.95) - 1], Maximum = values[^1] };
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
