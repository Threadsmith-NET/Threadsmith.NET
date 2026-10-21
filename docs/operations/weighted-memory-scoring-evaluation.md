# Weighted memory scoring evaluation — 2026-10-20

**Historical evaluation; Weighted has been removed.** The proposed weights did not deliver an acceptable overall recall/relevance tradeoff on this corpus. The runtime now supports baseline hybrid retrieval and optional reranker ordering only; no Weighted opt-in or scoring configuration remains. The measurements below describe the removed experiment, not available settings.

## Protocol and scope

The frozen base corpus contains 13 real repository-intelligence notes, three synthetic situational notes and one saved standing preference. Labels are agent-authored judgements of direct usefulness for each concrete task, reviewed independently before tuning. All 19 previously unjudged extra injected occurrences were labelled: five relevant and 14 irrelevant. The labels were recorded in the retired spike's `fixtures.json`, available in Git history. They are not a user-labelled production benchmark.

Thirty base recall queries run separately through both ownership routes. One additional held-out intelligence query uses a separate frozen pool with 30 irrelevant weather distractors to exercise lexical-only discovery. Thirteen reconciliation proposals run through the actual write service; both routes search both ownership partitions. One exact duplicate is a separate safety check. Fourteen calibration queries per recall route and 3/4 per ordinary/intelligence reconciliation route precede the held-out cases. Related original prompt/long-prompt queries stay in calibration. No held-out outcome influences weights or cutoffs.

Weights remain S=0.25, L=0.50, C=0.25. Weighted branch limits are semantic 20, lexical 20, concept 4, with no semantic floor. Prior retains its existing cosine 0.47 qualification and comparison windows (8 recall, 20 reconciliation). Final TopN is 3 recall and 20 reconciliation. Thus this comparison deliberately measures both discovery and admission changes, not fusion alone. Candidate differences are recorded per query. Both modes use identical frozen data, query inputs, model versions and final consumer limits. The native engine keeps its eight-pair batches and complete-memory coverage.

Threshold candidates were 0, 0.1, 0.25, 0.4, 0.5, 0.6, 0.75, 0.9, 1. Calibration minimizes `2 × false positives + false negatives`, breaking ties toward fewer false negatives and then the lower cutoff. Actual zero-cutoff consumer admission was checked against the score-sorted prefix before simulating threshold removal; every chosen cutoff then ran through the actual context assembler or write service. Standing preferences are excluded from optional-recall metrics and checked unconditionally. A new isolated SQLite backup prevents ordinary proposals from changing the frozen corpus; intelligence uses the existing provisional collection.

Versioned LF fixture SHA256: `37707b93f958ee69f8688b8cd8a2b8e16fe0e84709ae47bf0bb8a79b002ca517`. The original freeze used CRLF bytes (SHA256 `174d8813e34856d3a4e90156bfee6b545311ff81177396714efece19ea7b3402`); normalizing only line endings reproduces the versioned fixture. A final packaged replay reproduced every threshold and quality count without label or tuning changes. Exact model identities, all threshold sweeps, per-query admissions/pools/scores and timings were recorded in `evaluation-summary.json`. The offline runner and its versioned fixtures and results have been removed from the checkout; their historical versions remain in Git history under `spikes/WeightedMemoryScoring`.

## Calibration choices

| Route | Cutoff | Prior TP / FP / FN | Weighted TP / FP / FN |
|---|---:|---:|---:|
| Ordinary recall | 0.60 | 2 / 1 / 0 | 2 / 2 / 0 |
| Intelligence recall | 0.90 | 11 / 10 / 2 | 10 / 0 / 3 |
| Ordinary reconciliation | 1.00 | 2 / 0 / 0 | 2 / 2 / 0 |
| Intelligence reconciliation | 0.90 | 4 / 5 / 0 | 3 / 1 / 1 |

A cutoff of 1 still admits no-match candidates when the same weak candidate leads every signal. Scores are relative evidence, not relevance probabilities.

## Held-out final-consumer results

Recall metrics measure final injected optional memories, after prompt budgeting. Reconciliation metrics measure candidates actually presented by the write service; scores never authorize an update or deletion.

| Route (queries) | Mode | TP / FP / FN | Precision | Recall | No-match FP | Empty results |
|---|---|---:|---:|---:|---:|---:|
| Ordinary recall (16) | Prior | 4 / 1 / 0 | 80.0% | 100% | 1 | 11/16 |
| | Weighted | 4 / 1 / 0 | 80.0% | 100% | 1 | 11/16 |
| Intelligence recall (17) | Prior | 11 / 11 / 1 | 50.0% | 91.7% | 3 | 5/17 |
| | Weighted | 7 / 6 / 5 | 53.8% | 58.3% | 6 | 4/17 |
| Ordinary reconciliation (3) | Prior | 2 / 0 / 0 | 100% | 100% | 0 | 1/3 |
| | Weighted | 2 / 1 / 0 | 66.7% | 100% | 1 | 0/3 |
| Intelligence reconciliation (3) | Prior | 2 / 6 / 1 | 25.0% | 66.7% | 1 | 0/3 |
| | Weighted | 2 / 1 / 1 | 66.7% | 66.7% | 1 | 0/3 |

The predeclared gate required improved precision in every route, no more than five percentage points of recall loss and no increase in no-match false positives. It fails. Intelligence reconciliation is the one promising result, but three held-out proposals are insufficient evidence for promoting even that route independently.

## Concrete failures and preserved behavior

- “Expose a new command in both the terminal and headless frontend” previously injected both relevant notes. Weighted scores of 0.8153 and 0.8943 reject both at the calibration-selected 0.90 cutoff.
- File-edit and progress tasks lose useful secondary tool/frontend guidance at that cutoff. This is a real recall loss under the frozen labels, not a diagnostic omission.
- Intelligence recall now injects hooks for a staging smoke-test question and onboarding guidance for a subscription-tier cache question. No-match false positives increase despite fewer total false positives.
- An unrelated “do not log customer credit card numbers” proposal receives a cache-memory reconciliation candidate with final score 1. Relative normalization cannot reject it.
- The large-pool exact-symbol target is verified lexical-only: lexical rank 1, no semantic rank, L=1 and C=0, final score 0.50. Weighted rejects it at 0.90; Prior also misses it. Even with C=1, a lexical-only candidate cannot exceed 0.75 under these weights.
- Native long-question tests independently show the current concept-only target surviving at score 0.25 while an old-task lexical match ranks above it. At the character cap, old task text no longer fits and that lexical influence disappears. Strict reconciliation still rejects overlong comparisons. This ranking tradeoff is explicitly tested, not presented as an improvement.
- The standing preference survives all 122 measured recall assemblies. Its paraphrase is found in calibration reconciliation, and the exact duplicate resolves through the service without approximate scoring even at cutoff 1. This preserves existing non-situational behavior; it does not establish a new standing-preference recall improvement.
- No weighted scoring stage failed or returned incomplete inference during evaluation. Long-query semantic truncation remains separately reported, so successful bounded-question recall is not confused with complete reconciliation search.

## Latency and limits

| Held-out route | Prior median / p95 | Weighted median / p95 |
|---|---:|---:|
| Ordinary recall | 35 / 74 ms | 155 / 205 ms |
| Intelligence recall | 124 / 528 ms | 1090 / 2105 ms |
| Ordinary reconciliation | 65 / 89 ms | 1508 / 1595 ms |
| Intelligence reconciliation | 220 / 469 ms | 1570 / 1587 ms |

These are the final packaged replay on local Windows/.NET 10 with warmed models and one cross-encoder CPU thread. The earlier run had identical quality counts but materially lower timings (for example, ordinary recall medians 12/50 ms versus final 35/155 ms), illustrating host-load variability. Models are identical between modes; caches start fresh per case except the explicit topic-change case. Fixed ordering, remaining JIT effects, tiny reconciliation samples and machine load limit latency conclusions. Broader candidate coverage clearly costs more inference here; no performance improvement is claimed. Database snapshot and FTS work still scale with stored memories rather than TopK.

A supplemental semantic-only probe was added after held-out inspection because all relevant base-corpus candidates happened to have lexical evidence. It uses the already frozen ordinary cutoff and is reported separately; it cannot improve the primary held-out score or retune the experiment. The probe asks about an account upgrade still displaying the old plan. The relevant cache note has cosine 0.4280, no lexical hit, normalized S=1 and C=1, and final score 0.50. Prior misses it at its semantic floor; Weighted discovers it but rejects it at the frozen 0.60 cutoff. Thus the new discovery path alone does not repair the final recall miss. Both modes return empty; the supplemental result is explicitly excluded from the primary held-out metrics.

## Historical validation and disposition

Numerical/SQLite checks cover zero and negative evidence, singleton/equal logits, finite-input rejection, ownership isolation, native BM25 direction, OR/fuzzy provenance, threshold-before-TopN and 1/8/9/17 candidate batch invariance. Consumer tests exercise failure-closed writes, exact duplicates, cache invalidation and per-item evidence despite diagnostic caps. Native tests cover current-intent changes, long questions, complete documents and concept-only admission. Each implementation batch passed a fresh-context adversarial review after corrections. Final full solution run with both native flags and four concurrent test modules: 4,121 total, 4,078 passed, 43 skipped, zero failed, 2m30.582s. An initial unrestricted parallel run hit one existing Git-process cancellation in secret-store tests; that project passed in isolation and in the final run without changing timeouts. Windows native behavior was exercised; other operating systems and skipped environmental checks remain unverified.

The initial disposition kept the opt-in implementation. The subsequent [retrieval cleanup](../implementation-plans/maintenance-memory-retrieval-cleanup.md) removes that implementation and retains this evidence. Any future weighting/discovery experiment needs fresh labels and a new held-out set; lowering the cutoff based on these failures would leak evaluation information into tuning.
