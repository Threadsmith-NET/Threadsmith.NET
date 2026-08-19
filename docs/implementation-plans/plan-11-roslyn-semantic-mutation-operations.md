# Implementation Plan 11: Roslyn Semantic Mutation Operations

**Milestone:** M5 — Transactional Mutation
**Strategy source:** §13.7 (Semantic Edits), §15 (mutation model), §10.7 (turn/visibility), §35 (open decision: first semantic mutation scope), §16.2 (`relatedSymbolId` at ≥ PartialCompilation)
**Prerequisite plans:** plan-06 (semantic engine + confidence), plan-10 (transactional workspace + staging + mutation contracts)
**Status:** Complete (2026-08-02)

## 1. Objective
Add Roslyn-backed semantic mutations — starting with **rename and bounded syntax replacement** (the §33 plan-11 scope and the §35 deferred decision narrowed here) — that produce the same `MutationSet` as text patches but carry symbol-level correlation, routed through the plan-10 transactional workspace.

## 2. Architectural Context
Parent: Workspace baseline → Mutation engine (§28), layered on plan-10. This adds semantic mutators in `Threadsmith.DotNet` that emit `Mutation`s into the plan-10 staging view. Symbol-level correlation (`relatedSymbolId`) is populated only at `SemanticConfidence ≥ PartialCompilation` (§16.2, gap #2 follow-on). Read `00-shared-context.md` §H before starting.

## 3. Scope
- **First semantic mutation set (resolves §35 open decision #3 for M5):** rename symbol + bounded syntax-tree replacement.
- Symbol-level correlation: each semantic `Mutation` carries `relatedSymbolId` when confidence ≥ `PartialCompilation`; `null` otherwise (§16.2).
- Integration with plan-10 staging + the §10.7 turn contract.
- Preview as diff (text patch) — the *output* of a semantic mutation is a text patch the plan-10 previewer renders (§5.4 "typed mutations with text-patch fallback", ADR 8).
- Confidence precondition: semantic mutations rejected when the owning project is below `PartialCompilation` (§13.x behavior 1).

## 4. Non-Scope
- No broader semantic mutation set (add-member, refactor) — deferred per §35; expand incrementally after M5.
- No build/diagnostics (plan-12).

## 5. Current State
Implemented. `SemanticMutationEngine` captures one workspace-isolated Roslyn mutation snapshot, rejects confidence below `PartialCompilation`, uses Roslyn rename conflict handling across the compiled subset, and supports exact expression/statement/member syntax-node replacement with changed-region formatting. Both operations emit plan-10 `MutationSet` text replacements with baseline hashes and `RelatedSymbolId`; generated or non-baseline changes are omitted with warnings. Transactional preview, conflict detection, approval, commit, and rollback remain owned by `Threadsmith.Workspaces`. `Threadsmith.Milestone5.Tests` verifies full-confidence rename, bounded syntax replacement, correlation, preview reuse, and the text-only rejection path.

## 6. Proposed Design
- `RenameSymbolMutation` and `SyntaxReplacementMutation` operate on the Roslyn `Compilation`/`SyntaxTree` for the affected project (from plan-06), produce a `SyntaxNode` change, and emit a text-patch `Mutation` into the plan-10 staging view.
- Confidence gate: if the owning project is `< PartialCompilation`, reject with an actionable message (degrade to text patch only if the user opts in — §13.x behavior 1).
- `relatedSymbolId` populated from plan-06 symbol identity (§13.5) when confidence allows; enables plan-12 diagnostic→mutation correlation (§16.2, §16.4).

## 7. Public Contracts
- `RenameSymbolMutation`, `SyntaxReplacementMutation` inputs (symbol id + target).
- `Mutation.RelatedSymbolId` field (plan-10 contract extension; null below `PartialCompilation`).
- Confidence precondition enforced in the plan-08 tool pipeline.

## 8. Project and File Changes
- `Threadsmith.DotNet/`: semantic mutation operations (§13.7).
- `Threadsmith.Workspaces/`: `Mutation.RelatedSymbolId` field (plan-10 extension).
- TUI/CLI: semantic-mutation preview (reuses plan-10 diff view).

## 9. Ordered Implementation Tasks
1. **Resolve §35 first-mutation-scope decision** (gap #8 from assessment): confirm rename + bounded syntax replacement for M5; record rationale.
2. `Mutation.RelatedSymbolId` field in plan-10 contract (§16.2).
3. `RenameSymbolMutation` via Roslyn `SymbolFinder` + `Renamer` or `SolutionEditor` (§13.7).
4. `SyntaxReplacementMutation` via `SyntaxNode` replacement.
5. Emit text-patch `Mutation`s into plan-10 staging.
6. Confidence gate: reject below `PartialCompilation` (§13.x behavior 1).
7. `relatedSymbolId` populated when confidence allows (§16.2).
8. Preview reuses plan-10 diff view.
9. Conflict detection + rollback via plan-10 (unchanged).

## 10. Testing
- Rename a symbol in `SmallDotNetSolution` at `FullSemantic` → all references updated in the patch; `relatedSymbolId` set.
- Rename in a `PartialCompilation` repo → only the compiled subset's references updated; others flagged.
- Rename when the owning project is `TextOnly` → rejected with actionable message.
- Bounded syntax replacement: replace a node → patch emitted; `relatedSymbolId` set if the node maps to a symbol.
- Rollback: apply then rollback → baseline unchanged (plan-10 guarantees).

## 11. Security and Permissions
- Approval at the plan-10/§15.8 level (semantic mutations are side-effecting). No write outside approved roots.

## 12. Observability
- Metric: semantic-mutation success by confidence level; rejection reasons.

## 13. Migration and Compatibility
- `Mutation.RelatedSymbolId` is additive; old persisted mutations have `null` (schema-versioned per plan-02 gap #3).

## 14. Acceptance Criteria
- Rename + bounded syntax replacement work at `≥ PartialCompilation`; rejected below.
- `relatedSymbolId` populated when confidence allows (enables plan-12 correlation).
- Preview/conflict/rollback reuse plan-10 guarantees (§10.7 invariants hold).

## 15. Risks and Mitigations
- **Roslyn rename scope ambiguity (§13.7):** use Roslyn's `Renamer` which handles conflicts; if it can't rename cleanly, produce a partial result + a warning, not a silent partial rename.
- **Confidence downgrade mid-mutation:** gate at turn start; if confidence drops mid-turn (rare, queued invalidation), discard staging per §10.7 invariant 5.

## 16. Documentation
- `docs/architecture/semantic-mutations.md` (rename + syntax replacement; confidence preconditions).

## 17. Open Decisions
- **Resolved here (§35 #3):** first semantic mutation set = rename + bounded syntax replacement. Broader set deferred to post-M5.
- Roslyn `Renamer` vs. `SolutionEditor` for rename — recommend `Renamer` for conflict handling.
