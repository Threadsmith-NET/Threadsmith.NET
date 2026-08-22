# Implementation Plan 10: Transactional Workspace and Text Mutations

**Milestone:** M5 — Transactional Mutation
**Strategy source:** §15 (Transactional Workspace + Mutation Model), §10.7 (turn/visibility contract — staging), §5.4 (mutation cycle), §29 (ADRs 8, 9)
**Prerequisite plans:** plan-02 (turn contract, `MutationSetId`/`MutationId`, approval), plan-05 (baseline manifest), plan-08 (tool pipeline + policy)
**Status:** Complete (2026-08-02)

## 1. Objective
Deliver the transactional workspace — baseline + copy-on-write staging, mutation contracts, text-patch preview/apply, conflict detection, rollback, mutation approval policy, and the Git worktree isolation option — so the model can propose a bounded, reviewable, rollback-able mutation set with no writes outside approved roots.

## 2. Architectural Context
Parent: Workspace baseline → Mutation engine (§28). This is `Threadsmith.Workspaces` (mutation staging) building on the plan-05 baseline and the plan-02 §10.7 turn contract (staging invisible until commit). Mutation tools reuse the plan-08 tool pipeline at a higher approval level (§15.8). Read `00-shared-context.md` §E (§10.7, §5.4) before starting.

## 3. Scope
- Workspace baseline (§15.1) — promote plan-05 manifest to the §10.7 immutable baseline.
- Copy-on-write staging view (§10.7 invariant 2) — mutations write here, invisible to read tools until commit.
- Mutation types (§15.3): text patch (primary); semantic mutations are plan-11.
- Mutation set (§15.4) + conflict detection (§15.5) via file hashing.
- Preview (diff) + apply + rollback (§15.6).
- Mutation approval levels (§15.8).
- Formatting (§15.7).
- Git worktree isolation option (§15.2, ADR 9).
- `MutationSetProposed`, `MutationApplied` events (§9.4).

## 4. Non-Scope
- No Roslyn semantic mutations (plan-11). No build/diagnostics (plan-12). No tests (plan-13). No extension-provided mutators.

## 5. Current State
Implemented. `WorkspaceBaseline` now retains Git/status, selected-solution, trust, approved-root, and prohibited-path facts. Mutation-preparation context carries the accepted plan and planned-file baseline hashes; `MutationProposalApplication` requests bounded structured model output, discarding fallback text once structured output arrives, rejects changed host identity and every normalized mutation path outside the accepted plan, forces explicit review, and stages the validated proposal through `TransactionalWorkspaceCoordinator`. The coordinator registers model-proposed set ownership so its public preview, commit, and rollback commands remain usable. `TransactionalWorkspace` captures immutable baseline content asynchronously and cancellably; stages bounded typed changes privately; emits aggregate and per-change unified previews; permits each individual preview to be enabled or disabled without regenerating the aggregate; deduplicates and bounds concurrent conflict hashes; enforces commit trust, approval selection, path/root/reparse-point policy, and baseline hashes; commits through temporary sibling files with compensating restore; and refuses rollback over newer user changes. `TransactionalWorkspaceCoordinator` exposes the same command boundary to TUI and CLI. `GitWorktreeManager` provides explicit detached-worktree create/remove operations. `Threadsmith.Mutations.Tests` verifies these contracts.

## 6. Proposed Design
- A `Turn` (plan-02) holds a `BaselineSnapshot`; mutations produce a `StagingView`; commit at the turn boundary replaces the baseline; discard on failure/rollback (§10.7).
- `MutationSet` = ordered `Mutation`s (text patches here); each carries `relatedMutationId`.
- Conflict detection: hash files before apply; if the on-disk hash ≠ baseline hash (external change), block (§15.5) — this is the §10.7 "mid-turn mutation by external process is a contract violation" enforcement.
- Worktree isolation (§15.2): optional; use Git worktrees so mutations don't touch the primary worktree until committed. (Open decision §35: Git library vs. process — plan-08 chose process; continue here.)

## 7. Public Contracts
- `IBaselineSnapshot`, `IStagingView`, `Turn` integration (§10.7).
- `Mutation`, `MutationSet`, `MutationType` (§15.3, §15.4).
- `ConflictReport` (§15.5).
- `MutationApprovalLevel` (§15.8).
- `MutationSetProposed`, `MutationApplied` events.

## 8. Project and File Changes
- `Threadsmith.Workspaces/`: staging view, mutation contracts, patch apply/preview, conflict detection, rollback, worktree isolation.
- TUI/CLI: diff view + approval.

## 9. Ordered Implementation Tasks
1. Promote plan-05 baseline to the §10.7 `IBaselineSnapshot`.
2. `IStagingView` (copy-on-write) integrated with the plan-02 `Turn` (§10.7 invariant 2).
3. Text-patch `Mutation` type + `MutationSet` (§15.3, §15.4).
4. Diff preview generation (§15.7 formatting).
5. Conflict detection via file hash (§15.5) — block on external change.
6. Apply to staging + commit at turn boundary (§15.6).
7. Rollback (discard staging) (§15.6).
8. Mutation approval levels (§15.8) wired to plan-08 pipeline + plan-02 approval policy.
9. Git worktree isolation option (§15.2).
10. TUI diff + approval views.
11. ADRs 13 (strategy decision 8: typed mutations + text-patch fallback) and 14 (strategy decision 9: worktree isolation) finalized; existing ADR numbers remain append-only.

## 10. Testing
- Propose a text mutation set → preview shows exact diff → approve → apply → baseline updated.
- Conflict: external file change mid-turn → apply blocked (§10.7, §15.5).
- Rollback: apply then rollback → baseline unchanged.
- No write outside approved roots (M5 invariant) — architecture test + runtime guard.
- Staging invisibility: a concurrent read tool (plan-08) during a turn sees baseline, not staging (§10.7 invariant 2).

## 11. Security and Permissions
- Approval policy (§10.5): the model cannot self-authorize mutations (M5). All mutations require user approval at the level defined by §15.8.
- Path confinement: no write outside approved roots (§22.1).

## 12. Observability
- Mutation size, conflict rate, rollback rate metrics.
- Span per mutation set with approval decision.

## 13. Migration and Compatibility
N/A — new subsystem.

## 14. Acceptance Criteria
- M5 exit criteria (text half): model proposes a bounded mutation set; user previews exact diff; conflicting file changes block; applied changes roll back; no write outside approved roots.
- §10.7: staging invisible to concurrent reads; invalidation/commit at turn boundaries.

## 15. Risks and Mitigations
- **Worktree isolation complexity (§15.2):** optional + behind a flag; default to in-place staging with hash-based conflict detection.
- **External mutation races:** hash check + block is the enforcement; document that mid-turn external mutation is unsupported (§10.7 invariant 4).

## 16. Documentation
- ADRs 13 and 14 implement strategy decisions 8 and 9 without renumbering existing ADRs.
- `docs/architecture/mutation-model.md`.

## 17. Open Decisions
- Git library vs. process adapter (§35) — plan-08 chose process; confirm here; revisit only if worktree perf demands it.
- Default approval level for text patches (§15.8) — recommend user-approve-each-set (not per-file) for M5.
