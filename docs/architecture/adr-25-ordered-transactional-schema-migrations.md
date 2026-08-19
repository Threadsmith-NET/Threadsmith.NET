# ADR-25: Ordered transactional schema migrations for the durable store

Status: Accepted
Date: Milestone 8 (plan-18)
Strategy: §19.5

## Context

The durable SQLite event store has grown its schema across milestones (M1 event tables, M8 artifact
metadata). Until now each subsystem created its own tables lazily, which leaves no authoritative
record of the schema version and no safe way to evolve the schema without risking the prior session
data. Gap #4 (plan-18) requires that an older persisted session must migrate or mark itself Legacy —
never crash — and that a migration failure must not destroy prior data.

## Decision

Introduce a `MigrationRunner` that owns an ordered, contiguous, idempotent `IDatabaseMigration`
sequence starting at version 0. Each migration runs in its own transaction:

- A `schema_version` table records the highest applied version.
- A migration that throws rolls back its transaction; the prior version remains readable.
- Migrations are idempotent (`CREATE TABLE IF NOT EXISTS`) so re-running is a no-op.
- The runner is invoked at startup before any session restore.

The initial `DefaultMigrations` set declares the M1 event tables (version 0) and the M8 artifact
metadata tables (version 1). Future schema changes append version N+1 migrations without touching
prior ones.

## Consequences

- A failed migration never corrupts prior session data; the database stays openable at the last
  successful version.
- The schema version is authoritative and queryable (`MigrationRunner.ReadCurrentVersionAsync`).
- Migration ordering is enforced at construction (contiguous from version 0).
- Domain event *payload* schema drift is handled separately by the event migrator registry
  (ADR-26), which classifies events as Current / Migrated / Legacy at restore time.