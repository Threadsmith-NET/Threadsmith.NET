using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Embeddings.Local;
using Threadsmith.Persistence;
using Threadsmith.Reranking.Local;
using Threadsmith.Telemetry;

// Offline manual evaluation, never part of the fast test suite. No user database/config changes.
// Usage: dotnet run --project spikes/WeightedMemoryScoring -- <output-directory>
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var ct = cancellation.Token;
var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, WriteIndented = true };
var fixtureBytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "fixtures.json"), ct);
var fixture = JsonSerializer.Deserialize<Fixture>(fixtureBytes, jsonOptions) ?? throw new InvalidDataException();
var output = Path.GetFullPath(args[0]);
var supplementOnly = args.Length > 1 && args[1] == "supplement-only";
Directory.CreateDirectory(output);
var root = Path.Combine(Path.GetTempPath(), "threadsmith-weighted-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "evaluation.db"), Pooling = false }.ToString();
try
{
    await new MigrationRunner(connectionString, DefaultMigrations.All).RunAsync(ct);
    using var store = new SqliteManagedRepositoryMemoryStore(connectionString);
    await using var embedding = new LocalTextEmbeddingGenerator();
    await using var encoder = new LocalTextCrossEncoder(cpuThreads: 1);
    var prompts = await DeployedPromptLoader.LoadAsync(AppContext.BaseDirectory, ct);
    var names = new Dictionary<RepositoryMemoryId, string>();
    var settings = new RepositoryMemoryOptions { MaxNumberOfRepoMemories = 50, Intelligence = new() { Enabled = true, MaximumStoredMemories = 50 } };
    foreach (var item in fixture.Memories)
    {
        var result = await store.AddAsync("evaluation", new RepositoryMemoryWrite
        {
            Text = item.Text,
            Origin = RepositoryMemoryOrigin.Manual,
            Ownership = (RepositoryMemoryOwnership)item.Ownership,
            MemoryType = (RepositoryMemoryType)item.MemoryType,
            Supports = item.Supports ?? [],
        }, embedding.Model, await embedding.GenerateAsync(item.Text, ct), settings, ct);
        names.Add((result.Entry ?? throw new InvalidDataException()).Id, item.Id);
    }

    var frozen = await store.GetSnapshotAsync("evaluation", [], ct);
    using var largeStore = await CloneAsync();
    foreach (var item in fixture.LargePoolMemories)
    {
        var added = await largeStore.AddAsync("evaluation", new RepositoryMemoryWrite { Text = item.Text, Origin = RepositoryMemoryOrigin.Manual, Ownership = RepositoryMemoryOwnership.Intelligence }, embedding.Model, await embedding.GenerateAsync(item.Text, ct), settings, ct);
        names.Add((added.Entry ?? throw new InvalidDataException()).Id, item.Id);
    }

    var warmup = Stopwatch.StartNew();
    await embedding.GenerateAsync("warm model paths", ct);
    await encoder.ScoreRecallAsync(new("warm model paths"), ["initialize scoring"], ct);
    var warmupMs = warmup.Elapsed.TotalMilliseconds;
    var outcomes = new List<Outcome>();
    var choices = new Dictionary<string, double>();
    if (supplementOnly)
    {
        using var priorChoices = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "frozen-choices.json"), ct));
        if (priorChoices.RootElement.GetProperty("FixtureSha256").GetString() != Convert.ToHexStringLower(SHA256.HashData(fixtureBytes))
            || priorChoices.RootElement.GetProperty("Embedding").GetProperty("SpaceId").GetString() != embedding.Model.SpaceId
            || priorChoices.RootElement.GetProperty("CrossEncoder").GetProperty("ModelId").GetString() != encoder.Model.ModelId)
        {
            throw new InvalidDataException("Supplement requires the original frozen fixture and model identities.");
        }

        foreach (var choice in priorChoices.RootElement.GetProperty("Choices").EnumerateObject())
        {
            choices.Add(choice.Name, choice.Value.GetDouble());
        }

        await RunSupplementAsync();
        return;
    }

    foreach (var route in (string[])["ordinary-recall", "intelligence-recall", "ordinary-reconciliation", "intelligence-reconciliation"])
    {
        var calibration = Cases(route, "calibration").ToArray();
        var scores = new List<Outcome>();
        foreach (var query in calibration)
        {
            outcomes.Add(await RunAsync(query, route, false, 0));
            scores.Add(await RunAsync(query, route, true, 0));
        }

        foreach (var row in scores)
        {
            var admitted = row.Scores.OrderByDescending(score => score.Final).ThenBy(score => score.StableId)
                .Take(route.EndsWith("recall", StringComparison.Ordinal) ? 3 : 20).Select(score => score.Id).Order().ToArray();
            if (!admitted.SequenceEqual(row.Actual.Order()))
            {
                throw new InvalidDataException("Calibration has budget or consumer omissions; offline threshold sweep is not valid.");
            }
        }

        // Zero-cutoff final admission equals the sorted prefix above. Raising the cutoff only removes
        // suffix entries, so every simulated subset also fits the unchanged prompt budget.
        // Only calibration scores are inspected here. Final held-out prompts run after choices are saved.
        var sweep = fixture.Thresholds.Select(threshold =>
        {
            var counts = scores.Select(row => Count(row.Relevant, [.. row.Scores.Where(score => score.Final >= threshold)
                .OrderByDescending(score => score.Final).ThenBy(score => score.StableId).Take(route.EndsWith("recall", StringComparison.Ordinal) ? 3 : 20).Select(score => score.Id)])).ToArray();
            return new { Threshold = threshold, TP = counts.Sum(row => row.TP), FP = counts.Sum(row => row.FP), FN = counts.Sum(row => row.FN) };
        }).ToArray();
        var chosen = sweep.OrderBy(row => (2 * row.FP) + row.FN).ThenBy(row => row.FN).ThenBy(row => row.Threshold).First();
        choices.Add(route, chosen.Threshold);
        await File.WriteAllTextAsync(Path.Combine(output, route + "-calibration.json"), JsonSerializer.Serialize(new { Sweep = sweep, Scores = scores, Chosen = chosen }, jsonOptions), ct);
        Console.WriteLine($"{route}: calibration cutoff={chosen.Threshold}, TP={chosen.TP}, FP={chosen.FP}, FN={chosen.FN}");
    }

    await File.WriteAllTextAsync(Path.Combine(output, "frozen-choices.json"), JsonSerializer.Serialize(new
    {
        FixtureSha256 = Convert.ToHexStringLower(SHA256.HashData(fixtureBytes)),
        Choices = choices,
        Embedding = embedding.Model,
        CrossEncoder = encoder.Model,
        SemanticWeight = .25,
        LexicalWeight = .50,
        CrossEncoderWeight = .25,
        SemanticCandidateLimit = 20,
        LexicalCandidateLimit = 20,
        ConceptCandidateLimit = 4,
    }, jsonOptions), ct);
    foreach (var (route, threshold) in choices)
    {
        foreach (var query in Cases(route, "calibration"))
        {
            outcomes.Add(await RunAsync(query, route, true, threshold));
        }

        foreach (var query in Cases(route, "heldout"))
        {
            outcomes.Add(await RunAsync(query, route, false, 0));
            outcomes.Add(await RunAsync(query, route, true, threshold));
        }
    }

    var summaries = outcomes.GroupBy(row => (row.Route, row.Split, row.Mode)).Select(group =>
    {
        var counts = group.Select(row => Count(row.Relevant, row.Actual)).ToArray();
        var tp = counts.Sum(row => row.TP);
        var fp = counts.Sum(row => row.FP);
        var fn = counts.Sum(row => row.FN);
        var times = group.Select(row => row.ElapsedMs).Order().ToArray();
        return new
        {
            group.Key.Route,
            group.Key.Split,
            group.Key.Mode,
            Queries = group.Count(),
            TP = tp,
            FP = fp,
            FN = fn,
            Precision = tp + fp == 0 ? (double?)null : (double)tp / (tp + fp),
            Recall = tp + fn == 0 ? (double?)null : (double)tp / (tp + fn),
            NoMatchFalsePositives = group.Where(row => row.Relevant.Length == 0).Sum(row => row.Actual.Length),
            EmptyResults = group.Count(row => row.Actual.Length == 0),
            MedianMs = times[times.Length / 2],
            P95Ms = times[(int)Math.Ceiling(times.Length * .95) - 1],
        };
    }).ToArray();
    await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
    {
        Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        FixtureSha256 = Convert.ToHexStringLower(SHA256.HashData(fixtureBytes)),
        Choices = choices,
        WarmupMs = warmupMs,
        Summaries = summaries,
        Outcomes = outcomes,
    }, jsonOptions), ct);
    var duplicateQuery = fixture.Queries.Single(query => query.Purpose == "duplicate-safety");
    var duplicate = await RunAsync(duplicateQuery, "ordinary-reconciliation", true, 1);
    if (duplicate.ServiceOutcome != "duplicate" || !duplicate.Actual.SequenceEqual(["preference"]))
    {
        throw new InvalidDataException("Exact duplicate safety failed.");
    }

    await File.WriteAllTextAsync(Path.Combine(output, "duplicate-safety.json"), JsonSerializer.Serialize(duplicate, jsonOptions), ct);
    await RunSupplementAsync();
    if ((await store.GetSnapshotAsync("evaluation", [], ct)).Revision != frozen.Revision)
    {
        throw new InvalidDataException("Evaluation changed frozen repository data.");
    }

    IEnumerable<Query> Cases(string route, string split) => fixture.Queries.Where(query => query.Split == split
        && (route.EndsWith("-recall", StringComparison.Ordinal) ? query.Purpose == "recall" && (query.Ownership is null || query.Ownership == (route.StartsWith("intelligence", StringComparison.Ordinal) ? 1 : 0))
            : query.Purpose == "reconciliation" && query.Ownership == (route.StartsWith("intelligence", StringComparison.Ordinal) ? 1 : 0)));

    async Task RunSupplementAsync()
    {
        // Added after held-out inspection to exercise a missing branch-only stratum. Never tunes a cutoff.
        var query = JsonSerializer.Deserialize<Query>(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "supplemental.json"), ct), jsonOptions) ?? throw new InvalidDataException();
        var prior = await RunAsync(query, "ordinary-recall", false, 0);
        var weighted = await RunAsync(query, "ordinary-recall", true, choices["ordinary-recall"]);
        var targetScore = weighted.Scores.Single(score => score.Id == "cache");
        var evidence = weighted.Details?.Candidates.Single(row => row.Id.Value == targetScore.StableId) ?? throw new InvalidDataException();
        if (evidence.SemanticRank is null || evidence.LexicalRank is not null)
        {
            throw new InvalidDataException("Supplement did not exercise semantic-only relevant evidence.");
        }

        await File.WriteAllTextAsync(Path.Combine(output, "supplemental-results.json"), JsonSerializer.Serialize(new[] { prior, weighted }, jsonOptions), ct);
    }

    async Task<SqliteManagedRepositoryMemoryStore> CloneAsync()
    {
        var copyString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, Guid.NewGuid().ToString("N") + ".db"), Pooling = false }.ToString();
        await using var source = new SqliteConnection(connectionString);
        await using var destination = new SqliteConnection(copyString);
        await source.OpenAsync(ct);
        await destination.OpenAsync(ct);
        source.BackupDatabase(destination);
        ct.ThrowIfCancellationRequested();
        return new SqliteManagedRepositoryMemoryStore(copyString);
    }

    async Task<Outcome> RunAsync(Query query, string route, bool weighted, double threshold)
    {
        var recall = route.EndsWith("-recall", StringComparison.Ordinal);
        var intelligence = route.StartsWith("intelligence", StringComparison.Ordinal);
        var scoring = new RepositoryMemoryScoringOptions
        {
            Mode = weighted ? RepositoryMemoryScoringMode.Weighted : RepositoryMemoryScoringMode.Prior,
            FinalThreshold = threshold,
            SemanticWeight = .25,
            LexicalWeight = .50,
            CrossEncoderWeight = .25,
            SemanticCandidateLimit = 20,
            LexicalCandidateLimit = 20,
            ConceptCandidateLimit = 4,
            SemanticDiscoveryMinimum = null,
        };
        var options = settings with
        {
            SemanticMinimum = .47,
            RerankerCandidateLimit = 8,
            ReconciliationSemanticMinimum = .47,
            ReconciliationCandidateLimit = 20,
            RerankerEnabled = true,
            MaxRepoMemoriesInContext = intelligence && recall ? 0 : 3,
            RecallScoring = scoring,
            ReconciliationEnabled = true,
            ReconciliationScoring = scoring,
            ReconciliationConceptsEnabled = false,
            Intelligence = settings.Intelligence with
            {
                Recall = settings.Intelligence.Recall with { Scoring = scoring, MaximumResults = 3, SemanticMinimum = .47, RerankerCandidateLimit = 8 },
                Reconciliation = settings.Intelligence.Reconciliation with { Scoring = scoring, MaximumResults = 20, SemanticMinimum = .47, RerankerCandidateLimit = 20 },
            },
        };
        using var ordinaryProposalStore = !recall && !intelligence ? await CloneAsync() : null;
        var activeStore = ordinaryProposalStore ?? (query.LargePool ? largeStore : store);
        using var search = new HybridRepositoryMemoryRetriever(activeStore, embedding, encoder);
        var recording = new RecordingSearch(search);
        var watch = Stopwatch.StartNew();
        string[] actual;
        string? serviceOutcome = null;
        var standingPreserved = true;
        if (recall)
        {
            await using var events = new Threadsmith.Execution.DomainEventStream();
            var sanitizer = new SecretOutputSanitizer();
            var assembler = new ContextAssembler(new EvidenceStore(events, sanitizer), new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, events, prompts, repositoryMemoryRetriever: recording);
            var request = new ContextAssemblyRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                Phase = RunPhase.EvidenceCollection,
                RepositoryPath = root,
                RepositoryIdentity = "evaluation",
                Task = new TaskSpecification(query.TaskIntent ?? query.Text, []),
                RepositoryMemoryCurrentInstruction = query.Text,
                IntelligenceEnabled = intelligence,
                RepositoryMemoryOptions = options,
            };
            if (query.Previous is { } previous)
            {
                await assembler.AssembleAsync(request with { RepositoryMemoryCurrentInstruction = previous, Task = new(previous, []) }, ct);
                watch.Restart();
            }

            var assembled = await assembler.AssembleAsync(request, ct);
            actual = [.. (assembled.RepositoryMemoryInclusions ?? []).Select(item => names[item.Id]).Where(name => name != "preference")];
            standingPreserved = assembled.Inspection.RepositoryMemoryItems.Any(item => item.Included && item.MemoryType == RepositoryMemoryType.StandingPreference);
            if (!standingPreserved || (weighted && assembled.Inspection.RepositoryMemoryItems.Any(item => item.Included && item.MemoryType == RepositoryMemoryType.Situational && item.ScoreEvidence?.Accepted != true)))
            {
                throw new InvalidDataException("Prompt admission evidence or unconditional preference missing.");
            }
        }
        else
        {
            // Intelligence uses a fresh provisional collection; ordinary proposals use a frozen SQLite backup.
            var service = new RepositoryMemoryService(activeStore, embedding, new SecretOutputSanitizer(), search: recording);
            var operation = await service.ExecuteAsync(new RepositoryMemoryOperationRequest
            {
                RepositoryIdentity = "evaluation",
                Collection = intelligence ? new(frozen, true, 0) : null,
                Action = "add",
                Text = query.Text,
                Origin = RepositoryMemoryOrigin.Manual,
                Ownership = intelligence ? RepositoryMemoryOwnership.Intelligence : RepositoryMemoryOwnership.Ordinary,
                MemoryType = query.Purpose == "duplicate-safety" ? RepositoryMemoryType.StandingPreference : RepositoryMemoryType.Situational,
                Supports = intelligence ? [new() { Path = "fixture.cs", ContentHash = new string('a', 64), ObservedAt = DateTimeOffset.UnixEpoch }] : [],
                Options = options,
            }, ct);
            serviceOutcome = operation.Outcome;
            actual = operation.Outcome == "duplicate" && operation.Entry is { } duplicateEntry ? [names[duplicateEntry.Id]] : [.. operation.Entries.Select(item => names[item.Id])];
        }

        var result = recording.Last;
        var details = intelligence && recall ? result?.IntelligenceSearchDetails : result?.SearchDetails;
        if (details?.CandidateEvidenceOmissions > 0)
        {
            throw new InvalidDataException("Evaluation requires complete candidate diagnostics.");
        }

        if (weighted && details?.Scoring is RepositoryMemorySearchBranchStatus.Unavailable or RepositoryMemorySearchBranchStatus.Incomplete)
        {
            throw new InvalidDataException("Failed scoring cannot count as improved empty recall.");
        }

        if (weighted && query.LargePool)
        {
            var target = details?.Candidates.Single(row => names[row.Id] == "prompts") ?? throw new InvalidDataException();
            if (target.LexicalRank is null || target.SemanticRank is not null || target.ScoreEvidence is null)
            {
                throw new InvalidDataException("Large-pool fixture did not exercise complete lexical-only evidence.");
            }
        }

        var relevant = query.Relevant.Where(name => !recall || fixture.Memories.Single(item => item.Id == name).Ownership == (intelligence ? 1 : 0)).ToArray();
        return new(route, query.Id, query.Split, weighted ? "Weighted" : "Prior", threshold, relevant, actual, watch.Elapsed.TotalMilliseconds,
            details?.Candidates.Where(item => item.ScoreEvidence is not null).Select(item => new Score(names[item.Id], item.Id.Value, item.ScoreEvidence!.FinalScore)).ToArray() ?? [],
            details?.Candidates.Where(item => item.LexicalRank is not null || item.SemanticRank is not null || item.ConceptMatched).Select(item => names[item.Id]).ToArray() ?? [],
            details, result?.IsComplete, standingPreserved, serviceOutcome);
    }
}
finally
{
    foreach (var path in Directory.EnumerateFiles(root))
    {
        File.Delete(path);
    }

    Directory.Delete(root);
}

static (int TP, int FP, int FN) Count(string[] relevant, string[] actual) => (actual.Intersect(relevant).Count(), actual.Except(relevant).Count(), relevant.Except(actual).Count());

internal sealed record Fixture(Memory[] Memories, Memory[] LargePoolMemories, Query[] Queries, double[] Thresholds);
internal sealed record Memory(string Id, string Text, int Ownership, int MemoryType, RepositoryMemorySupport[]? Supports);
internal sealed record Query(string Id, string Split, string Text, string[] Relevant, string Purpose, int? Ownership, string? TaskIntent, string? Previous, bool LargePool);
internal sealed record Score(string Id, Guid StableId, double Final);
internal sealed record Outcome(string Route, string Query, string Split, string Mode, double Threshold, string[] Relevant, string[] Actual, double ElapsedMs, Score[] Scores, string[] Pool, RepositoryMemorySearchDetails? Details, bool? SearchComplete, bool StandingPreserved, string? ServiceOutcome);

internal sealed class RecordingSearch : IHybridRepositoryMemoryRetriever, IRepositoryMemorySearch
{
    private readonly HybridRepositoryMemoryRetriever _inner;

    internal RecordingSearch(HybridRepositoryMemoryRetriever inner) => _inner = inner;

    public RepositoryMemoryRetrievalResult? Last { get; private set; }

    public async Task<RepositoryMemoryRetrievalResult> RetrieveAsync(RepositoryMemoryRetrievalRequest request, CancellationToken cancellationToken = default)
    {
        Last = await _inner.RetrieveAsync(request, cancellationToken);
        return Last;
    }

    public async Task<RepositoryMemoryRetrievalResult> SearchAsync(RepositoryMemorySearchRequest request, CancellationToken cancellationToken = default)
    {
        Last = await _inner.SearchAsync(request, cancellationToken);
        return Last;
    }
}
