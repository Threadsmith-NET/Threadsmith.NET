# Plan 44 — Structured File Lifecycle Mutations

**Milestone:** M14 — Rich Native Tool Inventory

**Prerequisites:** plans 10–13, 27, 30, 37–38, and 41–43

**Depends on by:** future typed repository refactorings and scaffolding workflows

**Status:** Complete. Production implementation, deterministic filesystem-effect fault coverage, reconciliation, validation/worker integration, shared surfaces, documentation, and cross-platform CI are complete; maintained abrupt-process and real-terminal cases remain compatibility checks.

## 1 Objective

Add structured repository file create, delete, and move operations as first-class mutation primitives while preserving exact-diff review, plan scope, conflict detection, transactional application, rollback, validation, and resume safety.

## 2 Architectural Context

Plan 10 already owns transactional text mutation and path confinement; Plan 30 owns approval policy; Plan 37 owns proposal, staging, validation, correction, authoritative outcomes, and interruption reconciliation; Plan 38 owns isolated worker integration. File lifecycle operations must enter through those boundaries. They are not direct tools and must never write during evidence collection.

## 3 Scope

- Typed `CreateFile`, `DeleteFile`, and `MoveFile` mutation operations.
- Exact source/destination baseline identities, content hashes, overwrite rules, encoding/newline metadata, case-only moves, and project-file impact classification.
- Aggregate exact diff preview including adds/deletes/renames and content changes.
- Transactional apply/compensation/rollback, write-ahead intent, resume reconciliation, mutation-policy risk classification, affected-project validation, worker integration, and host-authored outcomes.
- Optional project-file inclusion metadata; no hidden project edits.

## 4 Non-Scope

- Direct model/user file-system side effects outside a governed mutation set.
- Directory tree deletion/move, glob mutations, symlink/reparse-point creation, hard links, alternate streams, permission/owner changes, or writes outside approved roots.
- Git staging/commit/move commands.
- Implicit namespace/type/project-reference rewrites after moves.
- Binary generation beyond explicitly bounded supported content contracts.

## 5 Current State

Plan 44 is implemented through the existing governed mutation path. The versioned host contract now carries explicit create/delete/move semantics, exact source identity, absent-destination expectation, encoding/newline/content metadata, lifecycle risk, optional project-file association, move-plus-edit, case-only move projection, lifecycle preview DTOs, and identity-based reconciliation outcomes. `TransactionalWorkspace` stages source/destination pairs privately, renders exact aggregate add/delete/move/edit diffs, commits with a bounded compensating two-phase file transaction, and rolls back only when final identities remain unchanged. Model validation and accepted-plan scope include move destinations; policy classification treats moves as risky and evaluates both paths; execution continues to use Plan-37 write-ahead intent/checkpoints and authoritative validation, while Plan-38 touched-path scope/conflict checks cover both move endpoints. Shared TUI/headless projections render lifecycle operation, endpoints, risk, and case-only metadata without a direct filesystem tool. The M14 suite covers mixed create/delete/move, destination and stale-identity conflict, encoding/newlines, move-plus-edit, exact previews, all five reconciliation states, deterministic faults at every primary filesystem-effect boundary, incomplete compensation, affected-project propagation, worker ownership/overlap, and rollback. Windows, Linux, and macOS run the suite in CI; MTP-199–202 remain maintained abrupt-process and real-surface compatibility checks.

## 6 Implemented Design

Extend the closed mutation union with explicit lifecycle operations. Every operation binds to the current mutation baseline and approved plan step. Create requires destination absence unless an explicit separately reviewed replacement operation is selected. Delete requires exact existing identity/hash. Move requires exact source identity/hash and destination absence; any accompanying content edit is represented explicitly and previewed as part of the same atomic set.

Normalize paths with existing root, Git metadata, secret-path, reparse-point, casing, and collision policy. Stage all lifecycle changes privately, render exact add/delete/rename diffs, classify risk, authorize through Plan 30, and commit through Plan 10/37 write-ahead reconciliation. On filesystems where rename atomicity differs, use a bounded compensating transaction and verify final identities before recording success.

## 7 Public Contracts

- `CreateFileMutation`, `DeleteFileMutation`, and `MoveFileMutation` in the versioned mutation union.
- `ExpectedFileIdentity`, `DestinationExpectation`, `FileContentDescriptor`, and `FileLifecycleRisk`.
- Preview DTOs for added/deleted/moved/case-renamed files and explicit content edits.
- Reconciliation outcomes distinguishing not-started, applied, compensated, conflicted, and indeterminate states.

Contracts contain no filesystem handle, Git, Roslyn, terminal, process, or persistence-row implementation types.

## 8 Project/File Changes

- `Threadsmith.Core` / `Threadsmith.Execution` — versioned proposal schemas, plan-step validation, events, checkpoints, and authoritative outcomes.
- `Threadsmith.Workspaces` — lifecycle staging, previews, transactions, compensation, rollback, and reconciliation.
- `Threadsmith.Validation` — affected-project calculation for created/deleted/moved source/project/config files.
- `Threadsmith.Tools` — proposal schema exposure only in eligible mutation phases; no direct write tool.
- `Threadsmith.Persistence`, TUI, CLI, App, Plan-38 integration, dedicated M14 tests/fixtures, docs, scenarios, and DOX.

Any new project-level schema/fixture asset must be copied to output when newer.

## 9 Ordered Tasks

1. Inventory current mutation union, created-file handling, path/security policy, diff renderer, transaction journal, rollback, reconciliation, validation selection, and Plan-38 frozen change sets.
2. Define lifecycle contracts, invariants, schema versioning, operation IDs, risk classes, and legal combinations.
3. Extend proposal validation to enforce approved plan paths/steps, operation count/byte limits, baseline identities, and collision rules.
4. Implement private staging and exact previews for create/delete/move, including case-only rename and explicit move-plus-edit.
5. Implement transactional application, compensation, rollback, write-ahead intent, crash reconciliation, and idempotent resume.
6. Extend affected-project/test selection and semantic invalidation for lifecycle operations.
7. Extend Plan-30 risk policy and Plan-38 worker freeze/conflict/restaging/integration.
8. Add TUI/headless preview, approval, conflict, outcome, and recovery rendering through shared commands/projections.
9. Add fault-injection and cross-platform filesystem tests.
10. Update docs, Scenario N, manual cases, event catalog, roadmap status, and DOX when implementation lands.

## 10 Testing

Cover create/delete/move success, absent/existing destination conflicts, stale hashes, same-path and case-only moves, move-plus-edit, source/project/config/generated/secret/Git paths, reparse points, path traversal, operation/byte limits, encoding/newlines, exact previews, policy risk, cancellation at every boundary, crash before/after each filesystem effect and checkpoint, compensation failure, rollback over user changes, validation selection, worker conflicts, interactive/headless parity, and architecture gates.

## 11 Security/Permissions

Lifecycle changes are mutations and always require current repository trust, approved roots, accepted-plan scope, baseline validation, exact preview, and Plan-30 authorization. Hard denials for outside-root, Git metadata, secrets, traversal, links/reparse points, and scope expansion remain invariant under every trust policy. No tool or model can directly invoke filesystem writes.

## 12 Observability

Record mutation/operation IDs, operation kind, normalized path hashes or approved relative paths according to sensitivity policy, baseline/destination state, bytes, preview artifact, risk, approval source, transaction/reconciliation outcome, validation scope, duration, cancellation, and failure class. Never log deleted/moved content or secret-path contents.

## 13 Migration/Compatibility

Version the mutation proposal and durable checkpoint schemas. Existing text-only mutation sets restore unchanged. Unknown lifecycle operation versions fail resume closed. Old clients can inspect unsupported operations but cannot approve or apply them. No repository migration is required.

## 14 Acceptance Criteria

- Create/delete/move are explicit versioned mutation operations, not shell/process commands or inferred text patches.
- Every operation is accepted-plan-scoped, baseline-bound, root-confined, exactly previewed, separately risk-classified, and policy-authorized before application.
- A mixed lifecycle/content mutation set applies atomically or compensates to an inspectable prior state.
- Cancellation/interruption reconciles to exactly one legal result without duplicate moves, lost files, fabricated success, or blind replay.
- Semantic invalidation and affected build/test selection include lifecycle effects.
- Plan-38 workers cannot bypass lifecycle scope/conflict checks; parent restaging and aggregate approval remain mandatory.
- Hard path/secret/Git/reparse guardrails and architecture tests pass on supported platforms.

## 15 Risks

- Cross-platform rename semantics differ: define identity-based outcomes and fault-test compensation.
- Rename detection hides content edits: model move and edit explicitly and display both.
- Delete rollback loses data: capture bounded baseline artifacts before authorization and refuse unsafe overwrite.
- New primitives bypass orchestration: expose them only through existing proposal/staging contracts.

## 16 Documentation

Document lifecycle proposal schemas, preview semantics, risk classification, platform/casing behavior, conflict/recovery rules, validation impact, and examples. Planned behavior remains distinct from current implementation.

## 17 Decisions

- File lifecycle actions are mutation primitives, not ordinary tools.
- Directory/glob/link operations are excluded from the first version.
- Moves never imply hidden semantic/project edits.
- Existing Plan 10/30/37/38 authority, transaction, validation, and integration contracts remain the only write path.
