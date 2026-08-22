# Implementation Plan 12: Build, Diagnostics, and Baseline Classification

**Status:** Complete (2026-08-02)
**Milestone:** M6 — Build, Diagnostics, and Tests
**Strategy source:** §16 (Validation Pipeline), §10.7 (turn contract makes baseline/introduced well-defined), §13.x (confidence precondition), §16.2 (diagnostic DTO incl. `confidence`, `relatedSymbolId`), §10.4 (retry classification for correction loop), §29 (ADR 18), §30.1, §30.8
**Prerequisite plans:** plan-06 (semantic engine + confidence), plan-10 (baseline + staging), plan-11 (semantic mutations + `relatedSymbolId`)

## 1. Objective
Close the coding loop with structured validation: baseline build capture, affected-project calculation, structured build execution, diagnostic normalization, **baseline-vs-introduced classification** (authoritative only at `FullSemantic`), diagnostic→mutation correlation, and the acceptance gate — feeding the bounded correction loop (Scenario C).

## 2. Architectural Context
Parent: Mutation engine → Validation pipeline (§28). This is `Threadsmith.Validation`. It reads the plan-10 baseline + staging, compiles affected projects, classifies diagnostics against the §10.7 turn contract (a diagnostic is *introduced* iff present against staging but absent against the committed baseline), and correlates to mutations via `relatedMutationId`/`relatedSymbolId` (§16.2). Read `00-shared-context.md` §E (§10.7) + §H (gap #2) before starting.

## 3. Scope
- Baseline build capture (§16.3) — record diagnostics + confidence at capture time.
- Affected-project calculation (§13.6 affected graph).
- Structured build execution (§16.1 compilation stage) with cancellation (gap #7: non-cooperative — abandon-and-discard).
- Diagnostic normalization (§16.2 DTO: id, severity, file, range, code, message, `relatedMutationId`, `relatedSymbolId`, `confidence`, `isBaselineDiagnostic`).
- Baseline-vs-introduced classification (§16.3) — authoritative only at `FullSemantic`; `ConfidenceDegraded` otherwise (§13.x behavior 3, gap #2).
- Diagnostic→mutation correlation (§16.4).
- Diagnostics TUI views.
- Acceptance gate (§16.7) — may require human confirmation when `ConfidenceDegraded`.
- Bounded correction loop (Scenario C): model receives only relevant changed code + diagnostic + contract; retry budget enforced (§10.4).

## 4. Non-Scope
- No test execution (plan-13). No analyzers beyond the compiler's own (analyzer integration is incremental post-M6). No semantic mutations (plan-11).

## 5. Current State
Implemented. `Threadsmith.Validation` now provides baseline build capture, affected-project traversal, direct trusted build execution, normalized compiler diagnostics, confidence-aware baseline classification, mutation/symbol correlation, acceptance gating, bounded correction, metrics, and classified diagnostic event publication. The session projection and TUI render structured diagnostics. `Threadsmith.Validation.Tests` verifies the build half; plan-13 owns test discovery/execution.

## 6. Proposed Design
- `BaselineCapture`: run a build against the baseline snapshot; store diagnostics + the confidence level at capture time (§16.3, §13.x behavior 4).
- After a mutation commit, compile affected projects (§13.6); normalize diagnostics to the §16.2 DTO.
- Classification: introduced = present against staging, absent against baseline; authoritative only at `FullSemantic` for the affected projects (§13.x behavior 3); else `ConfidenceDegraded`.
- Correlation: match diagnostics to mutations via `relatedMutationId` + `relatedSymbolId` (§16.4).
- Correction loop: introduced diagnostics → governed context (plan-09) gives the model only the relevant changed code + diagnostic + contract → model proposes a corrective mutation → recompile; bounded by the §10.3/§10.4 retry budget; **stops at the configured budget** (Scenario C step 8).
- Cancellation: build/MSBuild is non-cooperative (gap #7) → abandon-and-discard + bounded-wait backstop; documented.

## 7. Public Contracts
- `Diagnostic` DTO (§16.2, with `relatedMutationId`, `relatedSymbolId`, `confidence`, `isBaselineDiagnostic`).
- `BaselineCapture` (diagnostics + capture-time confidence).
- `AcceptanceGateResult` (§16.7) — pass/fail/human-confirmation-required.
- `BuildStarted`, `DiagnosticObserved` events (§9.4).

## 8. Project and File Changes
- `Threadsmith.Validation/`: baseline capture, affected-project calc, build execution, diagnostic normalization, classification, correlation, acceptance gate.
- TUI/CLI: diagnostics views.

## 9. Ordered Implementation Tasks
1. Diagnostic DTO (§16.2) with `confidence` + `relatedSymbolId` + `relatedMutationId`.
2. Baseline build capture + capture-time confidence (§16.3, §13.x behavior 4).
3. Affected-project calculation (§13.6).
4. Structured build execution (§16.1) + non-cooperative cancellation (gap #7).
5. Diagnostic normalization → §16.2 DTO.
6. Baseline-vs-introduced classification (§16.3) — authoritative at `FullSemantic`; `ConfidenceDegraded` otherwise.
7. Diagnostic→mutation correlation (§16.4).
8. Acceptance gate (§16.7) — human confirmation when `ConfidenceDegraded`.
9. Bounded correction loop (Scenario C): governed context + retry budget + stop-at-budget.
10. Diagnostics TUI views.
11. ADR 18 (structured baseline vs. introduced diagnostics) finalized.

## 10. Testing
- **Baseline classification:** baseline has CS1001; mutation introduces CS1503 → CS1503 classified `introduced`, CS1001 `baseline` (Scenario C).
- **Confidence precondition:** classify at `FullSemantic` → authoritative; at `PartialCompilation` → `ConfidenceDegraded`; gate may require human confirmation.
- **Correlation:** CS1503 correlated to the mutation via `relatedMutationId` + symbol via `relatedSymbolId`.
- **Correction loop:** model corrects → recompiles → stops at retry budget (Scenario C step 8).
- **Cancellation:** cancel a build → abandon-and-discard; no orphaned MSBuild process.

## 11. Security and Permissions
- Build execution is side-effecting (writes to `obj/`/`bin/`); confine to the workspace; do not run custom MSBuild targets from untrusted repos without trust (§22.2).

## 12. Observability
- Diagnostics-per-mutation, introduced-vs-baseline counts, correction-loop length, build latency.

## 13. Migration and Compatibility
- Diagnostic DTO versioned (gap #3); `relatedSymbolId`/`confidence` additive.

## 14. Acceptance Criteria
- M6 exit criteria (build half): affected projects compile after a mutation; introduced diagnostics distinguishable from baseline; correction loop bounded and stops at budget; final result includes validation evidence.
- §13.x: classification authoritative only at `FullSemantic`; `ConfidenceDegraded` otherwise.

## 15. Risks and Mitigations
- **Roslyn load partial failure (§30.1):** confidence-aware classification avoids false "introduced" claims on degraded projects.
- **Non-cooperative build cancellation (gap #7):** abandon-and-discard + bounded wait; documented.
- **Long correction loops (§34-C, §30.5):** retry budget + classification-before-retry (§10.4) bound the loop.

## 16. Documentation
- ADR 18 (structured baseline vs. introduced diagnostics).
- `docs/architecture/validation-pipeline.md`.

## 17. Open Decisions
Resolved for plan 12:
- Compiler diagnostics only; Roslyn analyzer integration is incremental post-M6.
- Any possibly introduced error below `FullSemantic` requires human confirmation.