# Weighted memory scoring evaluation

Manual offline evaluation using the production SQLite store, embedding model, cross-encoder, context assembler and reconciliation service. It is deliberately outside the solution and fast unit tests. It does not access a user database, change repository configuration, call an online model, or publish anything.

Stage the existing embedding and reranker assets using the repository's normal setup, then run from the checkout root:

```powershell
dotnet run --project spikes/WeightedMemoryScoring/Spike.WeightedMemoryScoring.csproj -- .inbox/weighted-evaluation
```

The output directory is an argument; all model and fixture paths are relative to the project/output directory. Disposable SQLite files use a uniquely owned temporary directory. Ordinary write proposals use SQLite backups preserving the frozen IDs; intelligence proposals use the existing provisional collection. Both reconciliation routes search both ownership partitions. The base corpus never changes during the evaluation. The separate large-pool case adds 30 labelled distractors to a second frozen snapshot shared by both modes.

`fixtures.json` freezes all relevance labels, 19 prior extra-occurrence judgements, calibration/held-out splits, weights, candidate limits and threshold selection objective. The runner saves cutoffs before examining held-out outcomes. Calibration asserts that actual zero-cutoff final prompt/service admission equals its score-sorted TopN prefix; only then can a threshold sweep safely simulate removing suffix items. Chosen cutoffs are replayed through the real consumers. Incomplete diagnostic evidence or failed weighted scoring aborts the experiment. Standing preferences are verified separately and excluded from optional-recall precision. Exact duplicate handling is checked through the write service, outside approximate scoring metrics.

`evaluation-summary.json` records the 2026-10-20 comparison, including every query's labels, final admissions, candidate pool and final scores. Full raw/normalized diagnostics are emitted to the requested output directory. Transient IDs differ on a new run; the same frozen IDs are used by both modes within each run. Timing is one warm-model pass, in fixed Prior-then-Weighted order, not a controlled benchmark or timing assertion.

`supplemental.json` was authored after the first held-out comparison to cover a missing semantic-only stratum. It never changes cutoffs and is excluded from the main held-out metrics. To run only this supplemental probe with already frozen choices:

```powershell
dotnet run --project spikes/WeightedMemoryScoring/Spike.WeightedMemoryScoring.csproj -- .inbox/weighted-evaluation supplement-only
```

See [the evaluation report](../../docs/operations/weighted-memory-scoring-evaluation.md). **The release gate failed. Prior remains the default.**
