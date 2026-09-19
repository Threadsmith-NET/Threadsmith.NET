# Transactional Mutation Model

M5 uses one immutable repository baseline and one private staging view per mutation set.

## Lifecycle

1. Capture `WorkspaceBaseline`: file hashes and immutable text, Git revision/status when available, selected solution/configuration, trust, approved roots, and prohibited paths.
2. Validate a bounded `MutationSet` against the exact workspace and capture timestamp.
3. Apply ordered typed text, create, delete, and move changes to private staging only. Lifecycle sources bind SHA-256 plus byte count; create/move destinations must be absent; move-plus-edit carries explicit content/encoding/newline metadata.
4. Produce an exact aggregate unified diff plus configurable individual change previews and explicit lifecycle source/destination/risk/case-only projections.
5. Request approval for the whole set, selected files, or selected mutations.
6. Recheck source/destination identities and path policy, detect repository-filesystem casing behavior, write private sibling files, remove baseline identities, publish final identities, and verify exact final hashes. On failure, compensation ignores caller cancellation, attempts every cleanup/restore effect, and aggregates incomplete compensation.
7. Reconcile lifecycle operations as `NotStarted`, `Applied`, `Compensated`, `Conflicted`, or `Indeterminate`; validate affected endpoints through M11, accept, or roll back while committed hashes still match.

Baseline reads never observe staging. A conflict at stage or commit blocks disk writes. Cancellation before or during commit prevents completion and compensating restore returns already-written files to their original bytes.

The planning boundary remains ahead of implementation: every accepted `propose_plan` payload contains one complete structured plan tranche, and the user or plan policy approves that whole tranche before its implementation begins. Corrective retries re-emit a complete candidate; incremental mutation execution does not lazily generate or separately approve later steps within that tranche. The host keeps the approved plan identity fixed, selects its earliest incomplete step, and asks the implementation model only for the next small coherent mutation batch within that step. A large step can therefore span several batches; small steps normally finish in one. After the tranche validates, the same run returns to ordinary planning so the model can propose one next tranche or confirm the finished objective with `complete_objective` and exactly `{}`. Confirmation runs the shared validation path over cumulative affected scope before terminal success. Ordinary text leaves unfinished or blocked work resumable.

Approved execution can span several separately authorized mutation sets. Configured mutation, distinct-file, and mutation-content targets guide model batch size but are soft: tightly coupled or indivisible semantic edits may exceed them while remaining subject to existing hard workspace limits and approved scope. A passing fully applied batch with `stepComplete: true` completes only the active step. `false` or an omitted hint keeps that step active. A no-change completion confirmation requires current passing evidence from a fully applied batch of the same selected step; another step's evidence is insufficient. Corrections preserve the original batch's completion intent.

Each generated set uses the existing private staging, exact-diff policy, transaction, baseline promotion, and validation path. Full authorization followed by passing validation either requests another batch for the same step or advances to the next approved step without a new plan approval. Partial authorization validates applied work and stops at `ContinuationPending`; it neither completes the run nor regenerates omitted work automatically. Explicit resume generates a fresh candidate requiring fresh exact-diff authorization. Interactive execution retains the run guard and uses the existing `/validation retry` route for this decision.

Later implementation model turns and later approved plan tranches restore durable progress without restarting the objective execution or replaying applied mutations. A restored schema-1 checkpoint is rewritten with schema-2 progress before further work. `PlanContinuationPending` preserves cumulative original-file artifacts, changed files, lifecycle evidence, affected validation scope, compact validation status, budget use, behavior summaries, and risks while current-plan scope is replaced. Detailed historical validation payloads and batch diffs stay in their own artifacts and are not copied through every active-state snapshot. Final diff artifacts compare original per-path content, retained once before that path's first objective mutation, with the latest promoted workspace snapshot using the same bounded diff renderer as mutation previews. Original dirty content is part of the comparison basis. Historical runs without original content do not present concatenated batch patches as a net final diff.

## Permissions and paths

- Preview/staging requires `TrustedRead`; commit requires `TrustedMutation` or `FullyTrustedAutomation`.
- Mutation source, destination, and optional project metadata paths are slash-normalized and repository-relative; both move endpoints belong to accepted-plan and worker scope.
- Targets must be within a configured approved root and outside prohibited-path globs.
- Existing path components may not be symbolic links or junctions.
- User approval is identified by the approval id emitted for that staged set. Model proposals are forced to explicit review. Host-policy auto-approval is limited to sets independently classified as both low-risk and policy-auto-approved.

At the model proposal boundary, Threadsmith accepts only lossless, deterministic structural normalization before normal validation: an unambiguous omitted `mutationSet` wrapper, one mutation object in place of the array, and exact supported `kind`/`path` aliases when they do not conflict with canonical fields. Duplicate authority-bearing properties, conflicting payloads or aliases, unknown operations, and malformed source content are rejected through the existing corrective-turn path. This normalization never edits proposed source text, invents missing mutation data, widens approved scope, or bypasses resource, path, semantic, staging, and approval checks.

## Preview settings

`Mutation.PreviewEnabled` controls whether the UI shows that change as an individual view. It does not remove the mutation, approve it, or hide it from the aggregate exact diff. Lifecycle operation, source/destination, risk, case-only status, and add/delete content remain in the shared aggregate projection. Selection for commit is carried separately by `MutationApproval`.

## Isolation

Tracked in-place mode is the default and relies on private memory staging, file hashes, temporary sibling files, and rollback bytes. Optional Git-worktree mode creates a detached worktree through a direct process adapter; the baseline must describe that isolated path.

Plan-38 implementation workers receive coordinator-owned worktree leases only after an approved plan is conservatively partitioned. The lease binds delegation/assignment/child-run/baseline/revision identity and remains under the host-managed temporary root. Freeze verifies the owned terminal worktree still exists; cleanup invokes Git only for the exact coordinator-owned isolation. Worker packages are rejected before parent staging when incomplete, stale, from another generation/baseline, outside frozen ownership, or overlapping another selected worker. The parent never uses Git merge semantics: losslessly converted selected changes, including both lifecycle endpoints, re-enter this transactional lifecycle as one fresh aggregate preview and approval.

## File lifecycle boundaries

Create/delete/move are mutation primitives, never direct tools. A move may include one explicit content descriptor but never implies namespace, type, project, reference, or generated-file edits. Project-file inclusion is descriptive metadata; any project-file change is separately proposed and reviewed. Directory/glob/link/alternate-stream/permission operations, overwrite moves, binary generation outside supported text encoding, and Git staging/commit/move are outside the closed vocabulary. See ADR-36.
