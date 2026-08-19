# Implementation Plan 13: Test Discovery, Selection, and Execution

**Milestone:** M6 — Build, Diagnostics, and Tests
**Strategy source:** §16.5 (Test Selection), §16.6 (Test Framework Abstraction), §16.1 (testing validation stage), §10.4 (retry), §34 (scenarios B, C, H), §30.8
**Prerequisite plans:** plan-06 (affected-project graph), plan-10 (baseline), plan-12 (build + diagnostics)

## 1. Objective
Deliver test discovery, defensible filtered selection, normalized execution, and test-result TUI views — so the harness runs a *explained* test scope after a mutation and feeds the acceptance gate.

## 2. Architectural Context
Parent: Mutation engine → Validation pipeline (§28), after plan-12. This is `Threadsmith.Validation` (testing half). Test selection is driven by the plan-06 affected-project graph + plan-11/plan-10 mutations. Execution uses the plan-08 process manager (tree-cancellation). Read `00-shared-context.md` §C before starting.

## 3. Scope
- Test-project discovery (§16.5) from the project graph.
- Filtered test selection (§16.5): tests covering affected projects/symbols; **explain the selection** (M6 exit criterion).
- Test framework abstraction (§16.6): xUnit + Microsoft.Testing.Platform.
- Normalized test results (pass/fail/skip + output + timing + correlation to mutations).
- Test-result TUI views.
- Cancellation of test runs (§5.8) via the process manager (§24.4).

## 4. Non-Scope
- No coverage-based selection (post-M6). No flaky-test handling (post-M6). No parallel-test-run scheduling policy beyond the process manager.

## 5. Current State

**Complete.** `Threadsmith.Validation` discovers supported xUnit/Microsoft.Testing.Platform projects from semantic inventory and confined project files, conservatively selects directly affected or referencing test projects, enumerates selected cases, runs `dotnet test --no-restore --no-build` through the tracked process manager, normalizes MTP/VSTest summaries, publishes structured `TestRunCompleted` evidence, projects results and rationale to CLI/TUI views, evaluates test failures in the combined acceptance gate, and exposes a hard-budget test-correction loop.

## 6. Proposed Design
- `TestDiscoverer`: walk the project graph, identify test projects (xUnit/MTP), enumerate tests.
- `TestSelector`: given affected projects (plan-06 §13.6) + mutations (plan-10/11), select tests likely to cover the change; produce a **selection rationale** (which projects/symbols drove inclusion).
- `TestRunner`: invoke `dotnet test` (or MTP) via the plan-08 process manager; tree-cancellable; normalize results.
- Results feed plan-12's acceptance gate (§16.7) and Scenario B step 10/11.

## 7. Public Contracts
- `TestProject`, `TestCase`, `TestSelection` (with rationale), `TestResult`.
- `TestRunCompleted` event (§9.4).

## 8. Project and File Changes
- `Threadsmith.Validation/`: discovery, selection, runner, result normalization.
- TUI/CLI: test views.

## 9. Ordered Implementation Tasks
1. Test-project discovery (§16.5) from plan-06 graph.
2. Test framework abstraction (§16.6) — xUnit + MTP.
3. Filtered selection (§16.5) based on affected projects + mutations.
4. Selection rationale (explainable) — M6 exit criterion.
5. `TestRunner` via plan-08 process manager + tree cancellation.
6. Result normalization → `TestResult`.
7. Test views (TUI/CLI).
8. Wire results to the plan-12 acceptance gate (§16.7).

## 10. Testing
- Selection: a mutation in project A selects tests in A's test project + downstream dependents; rationale lists the drivers.
- Execution: run a small fixture test set → pass/fail/skip normalized.
- Cancellation: cancel a run → process tree dies (Scenario H step 6–7).
- Flaky handling deferred (document).

## 11. Security and Permissions
- Test execution can run arbitrary user code in the repo (§22.1) — only in trusted repos (plan-05 trust); tree-cancellable.

## 12. Observability
- Tests selected, run, passed/failed/skipped; selection rationale per run; run latency.

## 13. Migration and Compatibility
N/A.

## 14. Acceptance Criteria
- M6 exit criteria (test half): harness runs a defensible test scope and **explains the selection**; test failures feed the correction loop; final result includes test evidence.
- Scenario B steps 10–11; Scenario H steps 2–7.

## 15. Risks and Mitigations
- **Over/under-selection (§16.5):** explainable rationale lets the user audit; start conservative (affected + direct dependents) and tune.
- **Long test runs (§30.8):** filtered selection + cancellation + parallelism via the process manager.

## 16. Documentation
- `docs/architecture/test-selection.md` (selection rules + rationale format).

## 17. Open Decisions

Resolved for M6:

- Selection granularity is conservative project-level selection. Known test cases are enumerated for evidence; coverage- or symbol-to-method selection remains post-M6 refinement.
- Selected projects run sequentially through the shared bounded process manager. Explicit parallel test scheduling remains post-M6 policy work.