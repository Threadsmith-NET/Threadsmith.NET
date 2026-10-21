# Memory retrieval cleanup

**Status:** Complete.
**Delivery track:** Maintenance.
**Prerequisites:** Completed weighted evaluation, shared retrieval repairs, and memory intelligence routing.

## 1 Objective
Remove the rejected Weighted experiment while preserving baseline retrieval and correctness fixes.
## 2 Architectural Context
ADR-59 owns hybrid discovery, ranking-only cross-encoder use, and reconciliation safety.
## 3 Scope
Remove scoring modes/options, configuration bindings, normalization, thresholds, weighted discovery branches, score-evidence contracts, and weighted-only tests.
## 4 Non-Scope
No model, vector, database, concept-policy, intelligence-routing, or memory-modal redesign.
## 5 Current State
The experiment failed its promotion gate; its historical measurements remain in the evaluation document.
## 6 Proposed Design
Retain semantic qualification, identifier-aware lexical qualification, exact-score-preserving fuzzy expansion, reciprocal-rank fusion, and bounded optional reranking as the sole execution path.
## 7 Public Contracts
Remove weighted scoring contracts. Reject retired per-route Scoring blocks with removal guidance.
## 8 Project/File Changes
Core, Context, Persistence, Tools, their tests, and current operator documentation.
## 9 Ordered Tasks
Trace consumers; remove alternate branches and contracts; retain correctness regressions; verify source/configuration absence and relevant suites.
## 10 Testing
App build and conversation-context, model-tooling, architecture, execution, and local inference unit suites passed. Real-model fixtures remain opt-in and were not run. Source and configuration scans found no remaining scoring implementation or activation binding; retired blocks are rejected. Scenario AM and MTP-277 own behavioral checks.
## 11 Security/Permissions
No user configuration or memory database mutation. Keep ownership, revisions, duplicate checks, and incomplete-search write guards.
## 12 Observability
Keep branch provenance, reranker accounting, failures, and concept-policy decisions; remove weighted-only evidence.
## 13 Migration/Compatibility
Remove Scoring blocks from all four routes. Existing memories and embeddings require no migration.
## 14 Acceptance Criteria
No executable weighted mode or binding remains. Baseline, concept filtering, intelligence routing, reranker correctness, and reconciliation safety regressions pass.
## 15 Risks
Shared tests and discovery code must be retained when removing experimental branches.
## 16 Documentation
Current operations, limits, acceptance, and manual procedures describe the supported baseline. Historical evaluation remains explicitly historical.
## 17 Open Decisions
None; the user requested removal of the rejected experiment.
