# ADR-36 — Structured File Lifecycle Mutations

## Status

Accepted.

## Context

The transactional workspace already owned exact text staging, review, commit, conflict detection, and rollback. Basic creation and deletion could be represented, but relocation, source/destination identity, encoding/newline intent, case-only filesystem behavior, and lifecycle-specific recovery were not explicit enough for governed orchestration, worker integration, or durable outcomes. A direct filesystem or shell tool would bypass Plan-30 approval and Plan-37 transaction/resume authority.

## Decision

Create, delete, and move are versioned host-owned mutation operations in the existing `MutationSet` boundary.

- Create requires an absent repository-relative destination and bounded supported text content.
- Delete requires exact baseline SHA-256 and byte count.
- Move requires an exact source identity, a distinct absent destination, and both endpoints in accepted-plan and worker scope.
- Move-plus-edit is explicit content metadata in the same atomic lifecycle operation; no semantic or project edit is inferred.
- Supported generated content is UTF-8 with optional BOM and explicit LF or CRLF normalization.
- Exact previews expose lifecycle kind, source/destination, case-only status, host risk, and add/delete content through shared interactive/headless projections.
- `ReviewRisky` treats moves and deletes as review-requiring. Every policy retains root, traversal, Git metadata, secret, prohibited, reparse, baseline, and destination guards.
- Commit detects repository-filesystem casing behavior, privately encodes every final file, removes baseline identities, publishes final identities, and verifies hashes. A partial failure performs every bounded compensation effect without caller cancellation and aggregates incomplete compensation.
- Rollback first removes created/destination identities and then restores exact baseline bytes, refusing to overwrite later user changes.
- Plan-37 write-ahead operation records remain the durable effect authority. Lifecycle reconciliation uses only `NotStarted`, `Applied`, `Compensated`, `Conflicted`, or `Indeterminate`; uncertain state fails closed.
- Lifecycle actions are proposal data, never ordinary tools. Directory/glob/link/alternate-stream/permission operations, overwrite moves, implicit namespace/project rewrites, and Git lifecycle commands remain unsupported.

## Consequences

The existing transaction, policy, validation, worker, persistence, TUI, and CLI boundaries remain authoritative. Both move endpoints contribute to affected-file promotion, semantic invalidation, worker ownership/conflict checks, aggregate diffs, and final outcomes. Case-only moves use repository-filesystem behavior, the same temporary-file transaction, and exact-name enumeration rather than an operating-system heuristic or platform rename behavior. Existing text-only mutation sets remain readable and executable; unknown operation schema versions fail validation before workspace access.
