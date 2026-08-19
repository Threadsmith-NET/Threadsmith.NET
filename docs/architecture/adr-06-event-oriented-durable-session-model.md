# ADR-6: Event-oriented durable session model

- **Status:** Accepted (design ADR — proven by later plans)
- **Date:** 2026-07-31
- **Strategy source:** §9.4 (Event model), §29 (ADR 5)
- **Validated by:** `spikes/Spike.Sqlite` (mechanism); full proof in plan-02/plan-18

## Context
The harness needs a durable, observable session model: immutable domain events that the TUI, persistence, telemetry, and automation all consume from the same stream. Sessions must survive restart (plan-18).

## Decision
Adopt an **event-oriented durable session model**: immutable domain events (`SessionCreated`, `RepositoryOpened`, `SolutionLoaded`, … per §9.4) are the single stream consumed by all surfaces. Persistence (SQLite, ADR-3) stores events; projections are rebuilt from the event stream. Model-provider SDK types, Roslyn objects, and extension object graphs never appear in events or persistent state (§7.1).

## Consequences
- **Open issue (gap #3):** add a `SchemaVersion` field per event for forward-compatible session restore. plan-02 defines it; plan-18 restores with tolerance.
- The full event catalog (§9.4) lands in plan-02; the in-memory projections + cancellation contract land in plan-02; persistence + session restoration land in plan-18.
- The M0 SQLite spike validated the write/close/reopen/read durability mechanism this ADR depends on.

## Validation
Mechanism validated by `Spike.Sqlite` (durable event write + restore). Full event stream + projections proven in plan-02; session restoration proven in plan-18.