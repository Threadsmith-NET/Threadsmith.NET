# ADR-26: Event/schema-version tolerance at session restore

Status: Accepted
Date: Milestone 8 (plan-18, gap #3)
Strategy: §19.4

## Context

A persisted session from an older build may contain events whose payload schema version differs from
the version the current host understands. The previous `DomainEventJson.Deserialize` threw on any
unsupported version, which crashed session restore for any session with a single drifted event. The
exit criterion requires that an older session must migrate or mark itself Legacy — never crash.

## Decision

Introduce a `DomainEventMigrationRegistry` and `IDomainEventMigrator`:

- Each migrator is registered for a specific `(discriminator, fromVersion)` and produces a higher
  `toVersion`.
- `Classify(discriminator, schemaVersion, payloadJson)` walks the migration chain from the persisted
  version up to the current version and returns `Current`, `Migrated`, or `Legacy`:
  - `Current` — the persisted version equals the current version.
  - `Migrated` — a complete chain of migrators lifted the payload to the current version.
  - `Legacy` — the version is newer than the host understands, a migrator is missing for a hop, or a
    migrator/deserialize/projection step threw.
- `SessionRestorer` never throws for a single event. Legacy events are skipped and recorded as
  warnings; the result carries `IsLegacy` so the host can show a read-only partial-state banner
  (plan-18 open decision: read-only + banner).

## Consequences

- Restoring an older session no longer crashes; drifted events degrade gracefully to Legacy.
- The host can opt to ship migrators for known schema changes without touching persisted data.
- Legacy state is observable (`SessionRestorationResult.IsLegacy`, `LegacyEvents`, `Warnings`).
- Migrators are pure JSON transforms; they hold no SDK or host types and are cheap to test.