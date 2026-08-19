# ADR-3: SQLite + artifact files for persistence

- **Status:** Accepted
- **Date:** 2026-07-31
- **Strategy source:** §6 (Technology Choices), §29 (ADR 6)
- **Validated by:** `spikes/Spike.Sqlite` (plan-01 task 14)

## Context
The harness persists durable session state (domain events, projections) and must restore sessions across restarts (plan-18). The persistence layer must be local-deployment-friendly and portable, with no external server dependency.

## Decision
Use **SQLite** (via `Microsoft.Data.Sqlite`) for structured/event persistence, plus **artifact files** for large/binary outputs (build logs, diffs, diagnostic bundles). Event persistence and telemetry must not retain extension object graphs (§7.1).

## Consequences
- `Microsoft.Data.Sqlite` 10.0.10 provides a clean async surface on .NET 10.
- **Open issue:** `Microsoft.Data.Sqlite` 10.0.10 pulls a transitive `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 with a known CVE (NU1903). M0 spikes suppress the warning; **plan-18 must pin a fixed version**.
- Event/schema versioning (gap #3) will be added in plan-02 (`SchemaVersion` per event) and restored with tolerance in plan-18.

## Validation
`Spike.Sqlite` writes an event row, closes the connection, reopens a new connection, reads the row back, and asserts all columns match → `PASS` (exit 0). See `spikes/Spike.Sqlite/README.md` and `docs/architecture/spike-notes.md`.