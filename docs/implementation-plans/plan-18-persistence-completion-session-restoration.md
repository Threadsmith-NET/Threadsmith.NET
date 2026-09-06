# Implementation Plan 18: Persistence Completion and Session Restoration

**Milestone:** M8 — MCP, Persistence Completion, and Operational Hardening
**Strategy source:** §19 (Persistence + Session Restoration), §9.4 (event model + `SchemaVersion` — gap #3), §10.2 (model output schema versions — gap #3), §19.5 (DB migrations), §19.6 (retention), §29 (ADR 6), §34 (scenarios B, H require inspectability across restart)
**Prerequisite plans:** plan-02 (event store baseline + `SchemaVersion`), plan-10 (mutations), plan-12 (diagnostics), plan-13 (tests), plan-16/17 (extension events)

## 1. Objective
Complete the SQLite persistence layer — full schema, migrations, artifact storage, session restoration, retention, and redaction — so sessions survive restart, including **tolerance for event/schema-version drift** (gap #3) and the rule that a migration failure must not destroy prior session data (§19.5).

## 2. Architectural Context
Parent: M8 (§28). This is `Threadsmith.Persistence`. It finalizes the plan-02 minimal event store into the full §19.2 persisted-data model, stores artifacts (§19.3), restores sessions (§19.4), and applies migrations safely (§19.5). Crucially, it implements the **event/schema-version tolerance** that gap #3 requires: a persisted session with older event/model-output schema versions must either migrate or load as `Legacy` — never crash. Read `00-shared-context.md` §E (§9.4) + §H (gap #3) before starting.

## 3. Scope
- Full SQLite schema for §19.2 persisted data: sessions, runs, steps, tool invocations, evidence, mutations, diagnostics, test results, approvals, extension events, prompts.
- Event store with `SchemaVersion` per event (gap #3, plan-02 seeded this).
- Model-output contract versioning persisted with sessions (§10.2, gap #3).
- **Migrations (§19.5):** ordered, idempotent, testable; a failed migration rolls back and leaves prior data intact.
- **Session restoration (§19.4):** rebuild projections from the event stream; tolerate older schema versions (migrate or mark `Legacy`).
- Artifact storage (§19.3): tool outputs, patches, diffs, test logs, diagnostic bundles — files on disk, referenced from SQLite.
- Retention + redaction (§19.6): age-based cleanup; secret redaction before persist.
- Diagnostic export (§23.4): bundle for support.

## 4. Non-Scope
- No MCP (plan-19). No operational hardening beyond retention/redaction (plan-20). No remote persistence.

## 5. Current State
Complete. Ordered transactional migrations, content-addressed sanitized artifacts, tolerant event restoration, retention, startup redaction auditing, and conversation restoration are implemented and covered by `Threadsmith.PersistenceMcpHardening.Tests`. Startup now performs one configured retention pass and deletes both eligible artifact metadata and bodies.

## 6. Proposed Design
- Schema per §19.2; one table per aggregate + a single append-only events table with `SchemaVersion`.
- Migrations (§19.5): versioned migration files applied in order; each runs in a transaction; a migration that fails rolls back and the prior version remains readable.
- Restoration: read events in order, replay into projections (plan-02); if an event's `SchemaVersion` is older than the reader supports, either apply a registered migrator or mark the affected state `Legacy` and continue — never throw mid-restore.
- Artifacts on disk keyed by a hash; SQLite stores the path + metadata.
- Retention (§19.6): periodic cleanup of sessions older than the configured age; redaction pass before any artifact is persisted.

## 7. Public Contracts
- SQLite schema (§19.2) + migration framework.
- `ISessionRestorer`, `LegacyState` marker.
- Artifact store API (§19.3).
- Retention + redaction policy (§19.6).

## 8. Project and File Changes
- `Threadsmith.Persistence/`: full schema, migrations, event store upgrade, session restorer, artifact store, retention, redaction.
- `Threadsmith.Telemetry/`: diagnostic export (§23.4) integration.

## 9. Ordered Implementation Tasks
1. Full §19.2 schema design.
2. Migration framework (ordered, idempotent, transactional) (§19.5).
3. Event store upgrade: `SchemaVersion` per event (gap #3).
4. Model-output contract version persisted with sessions (§10.2, gap #3).
5. **Session restorer with version tolerance:** migrate-or-mark-`Legacy` (gap #3); never throw mid-restore.
6. Artifact storage on disk + SQLite references (§19.3).
7. Retention policy (§19.6).
8. Redaction before persist (§19.6, §22.3).
9. Diagnostic export (§23.4).
10. ADR 6 (SQLite + artifact files) finalized.

## 10. Testing
- **Migration safety (§19.5):** a migration that fails rolls back; prior session data still readable.
- **Version tolerance (gap #3):** restore a session persisted with an older event schema → migrates or loads `Legacy`; no crash.
- **Version tolerance (gap #3):** restore a session with an older model-output contract → migrates or `Legacy`.
- Round-trip: persist a full session (scenarios B/H) → restart → restore → projections match.
- Redaction: a secret in a tool result → not persisted (redacted).
- Retention: aged sessions cleaned.

## 11. Security and Permissions
- Redaction (§22.3, §19.6) before any persist; secrets never in SQLite or artifacts.
- Artifact access confined to the workspace.

## 12. Observability
- Restore time, migration outcomes, `Legacy` marker counts, retention cleanup volume.

## 13. Migration and Compatibility
- **This plan *is* the migration framework.** Migrations must be safe (§19.5): a failure must not destroy prior data — tested explicitly.
- Event `SchemaVersion` reader policy (N−1) finalized here (plan-02 deferred the exact policy).

## 14. Acceptance Criteria
- M8 subset: sessions survive restart; MCP tools governed like built-ins (plan-19); diagnostic bundles exclude secrets.
- Gap #3: session restore tolerates event + model-output schema drift (migrate or `Legacy`, no crash).
- §19.5: a failed migration does not destroy prior data (tested).

## 15. Risks and Mitigations
- **Schema drift breaks restore (gap #3):** version tolerance + `Legacy` marking + migration safety.
- **Migration destroys data (§19.5):** transactional migrations + rollback test.
- **Artifact leakage of secrets (§19.3, §19.6):** redaction before persist + retention cleanup.

## 16. Documentation
- ADR 6.
- `docs/operations/persistence-and-restore.md`, `docs/operations/retention.md`.

## 17. Open Decisions
Resolved assumptions and follow-ups:

- Event readers use registered stepwise migrations when available; any unsupported version is preserved as visible read-only partial `Legacy` state rather than hidden or fatal.
- Retention is a startup pass, not a timer. Ages must be positive; the compiled defaults are 30 days, conversation bodies are independently detachable, and metadata-only is the strict artifact policy.
- Persisted domain events remain append-only during redaction audit. Unsafe artifact bodies may be repaired; event findings are reported rather than rewriting history.
- A dedicated user-facing session-restore browser remains a later surface; M8 establishes the durable/restoration contracts and startup lifecycle.
