# Implementation Plan 06: Roslyn/MSBuild Semantic Discovery

**Milestone:** M2 — Repository and .NET Semantic Discovery
**Strategy source:** §13 (.NET Semantic Engine incl. §13.x Semantic Confidence Levels), §10.7 (turn/visibility contract), §5.8 (cancellation), §30.1 (Roslyn load risk), §30.8 (large solutions), §29 (ADR 7)
**Prerequisite plans:** plan-05 (workspace + baseline), plan-02 (turn contract, events, `SemanticConfidenceChanged`)

## 1. Objective
Make the harness compiler-aware: load the solution via Roslyn/MSBuild, build the project graph + symbol index, answer find-symbol / find-references / find-implementations, classify generated + linked files, invalidate on file change — and do all of it across a **spectrum of semantic confidence** (§13.x) that degrades gracefully when a repo won't fully load.

## 2. Architectural Context
Parent: Repository lifecycle → Roslyn/MSBuild semantic engine (§28). This is `Threadsmith.DotNet`. It reads against the plan-05 baseline snapshot and the plan-02 turn contract (§10.7): all semantic reads observe the immutable baseline; invalidation is applied at turn boundaries. This plan implements the **Semantic Confidence Levels** contract (§13.x, gap #2) and the **non-cooperative cancellation** caveat (gap #7). Read `00-shared-context.md` §E (§10.7) before starting.

## 3. Scope
- Roslyn workspace lifecycle over the plan-05 solution.
- Project graph + target-framework inventory (§13.3 multi-targeting).
- Symbol, reference, and implementation search (§13.1, §13.5 symbol identity, §13.6 affected graph).
- Generated-code + linked-file classification (§13.4 source generators).
- Cache invalidation on file change (§13.8).
- **`SemanticConfidenceLevel` enum + per-level tool availability (§13.x).**
- Repository + semantic projections for the TUI (consumed by plan-03 stubs → real content here).
- `SemanticConfidenceChanged` event emission on promotion/demotion plus `SemanticLoadCompleted` terminal facts for every lifecycle load.

## 4. Non-Scope
- No semantic *edits* (plan-11). No build/diagnostics (plan-12). No mutation (plan-10). Read-only discovery only.

## 5. Current State
Implemented. `SemanticEngine` loads selected solutions or direct projects according to trust, exposes confidence-aware project and symbol DTOs, includes omitted projects in degraded confidence, classifies generated/linked sources, queues turn-boundary invalidation, promotes by reload, emits confidence changes, and publishes a terminal completion fact even when confidence remains `None`. `SemanticEngineRegistry` isolates state by workspace, while `SemanticLifecycleObserver` queues repository selection outside event callbacks and reports unavailable completion after a load failure. The checked-in semantic fixture verifies symbols, references, implementations, TFMs, ranges, generated/linked files, degraded and failed-project confidence, direct-project loading, invalidation, promotion, workspace isolation, and non-reentrant lifecycle loading.

## 6. Proposed Design
- A `SemanticEngine` that attempts full load and, on any per-project failure, records that project at a lower `SemanticConfidenceLevel` rather than failing the whole solution (§13.x, §30.1).
- Every semantic tool result carries `SemanticConfidence` (§13.x carriage rule). `FindReferences` against a `TextOnly` repo is **rejected** unless the caller opts into text fallback (§13.x behavior 1).
- Background promotion: retry failed restores/loads when the environment changes; emit `SemanticConfidenceChanged` (§13.x behavior 5).
- File-change invalidation (§13.8) is **queued and applied at turn boundaries** (§10.7 invariant 4); a `.csproj` edit can demote confidence.
- Cancellation: Roslyn/MSBuild APIs are largely non-cooperative (gap #7); use the abandon-and-discard pattern — run on a background task, discard the result on cancel, bounded-wait backstop. **Document this as a known limitation, not a bug.**

## 7. Public Contracts
- `SemanticConfidenceLevel` enum: `None | TextOnly | ProjectGraphOnly | PartialCompilation | FullSemantic` (§13.x).
- `ISemanticEngine`, `SymbolResult`, `ReferenceResult`, `ImplementationResult` — all carrying `SemanticConfidence`.
- `SemanticConfidenceChanged` and `SemanticLoadCompleted` events (already in §9.4 catalog).
- Repository/semantic projection DTOs (host-owned; no Roslyn types leak — §7.1).

## 8. Project and File Changes
- `Threadsmith.DotNet/`: workspace lifecycle, project graph, symbol index, search, confidence, invalidation.
- `Threadsmith.DotNet/`: confidence-level enum (or `Threadsmith.Core` if referenced by non-compiler-aware code — recommend `Threadsmith.Core` since §16 consumes it).
- TUI/CLI: real content for the symbol-results + solution-browser projections.
- `tests/Threadsmith.DotNet.Tests/` + fixtures from `samples/repositories/`.

## 9. Ordered Implementation Tasks
1. Define `SemanticConfidenceLevel` (in `Threadsmith.Core`, consumed by §16) — §13.x.
2. Roslyn workspace lifecycle over plan-05 solution.
3. Confidence-aware load: per-project success/failure → `PartialCompilation`; total failure → `None`/`TextOnly`/`ProjectGraphOnly` (§13.x).
4. Project graph + TFM inventory (§13.3).
5. Symbol identity (§13.5) + symbol search.
6. Find references + find implementations.
7. Generated-code + linked-file classification (§13.4).
8. Carriage: `SemanticConfidence` on every semantic tool result (§13.x).
9. Tool-availability enforcement: reject `FindReferences` at `TextOnly` unless opt-in (§13.x behavior 1).
10. Cache invalidation queue + turn-boundary application (§13.8 + §10.7).
11. Background promotion + `SemanticConfidenceChanged` emission (§13.x behavior 5).
12. Non-cooperative cancellation: abandon-and-discard + bounded-wait backstop (gap #7); document.
13. Projections for TUI.

## 10. Testing
- Full load on `SmallDotNetSolution` → `FullSemantic`; find symbol, references, implementations.
- `MultiTargetedSolution` → per-TFM results (§13.3).
- `GeneratedCodeSolution` → generated code classified (§13.4).
- **Degraded load:** break one project (missing SDK) → `PartialCompilation`; tools in that project rejected/degraded; event emitted.
- **Total failure:** no SDK → `None`; `FindReferences` rejected with actionable message.
- Invalidation: edit a `.csproj` mid-run → queued; applied at turn boundary; confidence demoted if applicable.
- Cancellation: cancel a symbol search → result discarded; no orphaned task (abandon-and-discard).

## 11. Security and Permissions
- Read-only against approved roots (§22.1). No file writes. Generated code is classified but not executed.

## 12. Observability
- Metrics: confidence level per project, search latency, invalidation queue depth, promotion attempts.
- Span per semantic operation with confidence tag.

## 13. Migration and Compatibility
- `SemanticConfidenceLevel` is a stable public enum from day one (plan-12/§16 depend on it).

## 14. Acceptance Criteria
- M2 exit criteria: TUI inspects solution structure + semantic symbol info; search results include project, TFM, file, range, symbol identity; file changes invalidate affected semantic state; no model required.
- §13.x: every semantic tool result carries `SemanticConfidence`; `FindReferences` rejected at `TextOnly`; `SemanticConfidenceChanged` emitted on promotion/demotion.
- Non-cooperative cancellation documented and tested (gap #7).

## 15. Risks and Mitigations
- **Roslyn load complexity (§30.1):** confidence spectrum makes partial load a first-class success path, not a failure.
- **Large solutions (§30.8):** lazy project load; affected-graph (§13.6) limits work.
- **Non-cooperative cancellation (gap #7):** abandon-and-discard + bounded wait; documented.

## 16. Documentation
- ADR 7 (Roslyn + MSBuild as semantic sources of truth).
- `docs/architecture/semantic-confidence.md` (the §13.x levels + degraded mode).

## 17. Current Decisions
- `SemanticConfidenceLevel` is a Roslyn-free host contract in `Threadsmith.Core`.
- The selected solution/project graph loads as a bounded operation; failed projects degrade confidence instead of failing the repository, and large-solution tuning remains operational work.
