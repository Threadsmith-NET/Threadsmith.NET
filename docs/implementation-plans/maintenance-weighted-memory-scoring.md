# Weighted memory scoring

**Status:** Completed (implementation and evaluation; promotion rejected).
**Delivery track:** Maintenance.
**Prerequisites:** Shared memory search and the completed recall/comparison repairs.

## 1 Objective
Reduce irrelevant injection using measured three-signal fusion and explicit admission.
## 2 Architectural Context
Extend shared retrieval, configuration and inspection; preserve strict reconciliation.
## 3 Scope
Four independent routes: ordinary/intelligence recall/reconciliation. Opt-in weighted mode; prior mode remains default.
## 4 Non-Scope
No embedding migration, time decay, new model call, raw-logit cutoff or persisted schema.
## 5 Current State
Prior remains the default. Weighted is implemented behind explicit configuration and shares existing discovery, inference and consumers. The labelled comparison failed its promotion gate.
## 6 Proposed Design
Freeze bounded branch unions per ownership partition, complete inference, normalize S/L/C, threshold, then TopN and existing prompt admission.
## 7 Public Contracts
Immutable scoring configuration and score evidence, explicit failures and branch provenance.
## 8 Project/File Changes
Core contracts, Tools configuration, existing Persistence lexical discovery, Context scoring and focused tests.
## 9 Ordered Tasks
Configuration; bounded discovery; deterministic fusion; consumer safety; labelled calibration/held-out comparison. Commit each reviewable batch.
## 10 Testing
Hand calculations, SQLite integration, real packaged-model inference, unchanged recall/reconciliation regressions and labelled final prompt evaluation. Behavioral ownership: Scenario AM; manual checks: MTP-276/277.
## 11 Security/Permissions
No user database or configuration mutation. Exact duplicates, revisions, approval and incomplete-search write guards remain authoritative.
## 12 Observability
Raw/normalized components, pool membership, configuration, threshold outcomes and failure status without extra memory-text logging.
## 13 Migration/Compatibility
No schema/vector migration. Prior configuration remains valid; scoring settings participate in ranking cache identity.
## 14 Acceptance Criteria
Weighted results always have threshold admission evidence; incomplete scoring fails closed. Promotion requires held-out precision/recall evidence and an acceptable reviewed tradeoff.
## 15 Risks
Relative normalization can promote weak no-match candidates. Missing-branch weight caps can reject relevant paraphrases and concept-only notes.
## 16 Documentation
Operations owns settings; acceptance/manual guides own behavior. The single `.inbox/memory-recall-reconciliation-work-log.md` records iterations and live evidence.
## 17 Open Decisions
Calibration selected .60 ordinary recall, .90 intelligence recall, 1.00 ordinary reconciliation and .90 intelligence reconciliation with experimental .25/.50/.25 weights. These are evaluation settings, not recommendations. Held-out intelligence recall lost 33.3 percentage points and increased no-match false positives. Prior remains default; further experiments require fresh held-out data. See [evaluation](../operations/weighted-memory-scoring-evaluation.md).
