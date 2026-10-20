# Weighted memory scoring

**Status:** Active.
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
Prior mode uses hybrid discovery and optional cross-encoder ordering without final-score admission.
## 6 Proposed Design
Freeze bounded branch unions per ownership partition, complete inference, normalize S/L/C, threshold, then TopN and existing prompt admission.
## 7 Public Contracts
Immutable scoring configuration and score evidence, explicit failures and branch provenance.
## 8 Project/File Changes
Core contracts, Tools configuration, existing Persistence lexical discovery, Context scoring and focused tests.
## 9 Ordered Tasks
Configuration; bounded discovery; deterministic fusion; consumer safety; labelled calibration/held-out comparison. Commit each reviewable batch.
## 10 Testing
Hand calculations, SQLite integration, real packaged-model inference, unchanged recall/reconciliation regressions and labelled final prompt evaluation.
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
No calibrated thresholds yet. Research weights .25/.50/.25 are experimental. Keep opt-in unless the release gate is met.
