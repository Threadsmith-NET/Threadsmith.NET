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

## Permissions and paths

- Preview/staging requires `TrustedRead`; commit requires `TrustedMutation` or `FullyTrustedAutomation`.
- Mutation source, destination, and optional project metadata paths are slash-normalized and repository-relative; both move endpoints belong to accepted-plan and worker scope.
- Targets must be within a configured approved root and outside prohibited-path globs.
- Existing path components may not be symbolic links or junctions.
- User approval is identified by the approval id emitted for that staged set. Model proposals are forced to explicit review. Host-policy auto-approval is limited to sets independently classified as both low-risk and policy-auto-approved.

## Preview settings

`Mutation.PreviewEnabled` controls whether the UI shows that change as an individual view. It does not remove the mutation, approve it, or hide it from the aggregate exact diff. Lifecycle operation, source/destination, risk, case-only status, and add/delete content remain in the shared aggregate projection. Selection for commit is carried separately by `MutationApproval`.

## Isolation

Tracked in-place mode is the default and relies on private memory staging, file hashes, temporary sibling files, and rollback bytes. Optional Git-worktree mode creates a detached worktree through a direct process adapter; the baseline must describe that isolated path.

Plan-38 implementation workers receive coordinator-owned worktree leases only after an approved plan is conservatively partitioned. The lease binds delegation/assignment/child-run/baseline/revision identity and remains under the host-managed temporary root. Freeze verifies the owned terminal worktree still exists; cleanup invokes Git only for the exact coordinator-owned isolation. Worker packages are rejected before parent staging when incomplete, stale, from another generation/baseline, outside frozen ownership, or overlapping another selected worker. The parent never uses Git merge semantics: losslessly converted selected changes, including both lifecycle endpoints, re-enter this transactional lifecycle as one fresh aggregate preview and approval.

## File lifecycle boundaries

Create/delete/move are mutation primitives, never direct tools. A move may include one explicit content descriptor but never implies namespace, type, project, reference, or generated-file edits. Project-file inclusion is descriptive metadata; any project-file change is separately proposed and reviewed. Directory/glob/link/alternate-stream/permission operations, overwrite moves, binary generation outside supported text encoding, and Git staging/commit/move are outside the closed vocabulary. See ADR-36.
