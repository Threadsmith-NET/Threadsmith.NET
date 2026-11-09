# Milestone 36 — Repository tool statistics

Current lifecycle status is owned by the [milestone index](../milestones.md).

## Objective

Inspect and reset durable repository-local tool completion counts and measured durations without delaying tool execution.

## Capability prerequisites

M3 central tool runtime, M8 SQLite persistence, M20 repository/session binding, and M21 sibling scheduling. Use the existing frontend-neutral interaction coordinator and sole TUIKit retained runtime.

## Deliverables

- Single-owner completion accounting across ordinary, failed, cancelled and suspended pipeline paths.
- Detached original database ownership and ordered background-thread SQLite aggregation, snapshots and reset.
- Typed application commands and local `/toolstats [reset]`, with the standard retained modal and safe text fallback.
- Deterministic storage, pipeline and interaction regression evidence, plus documented durability and measurement limits.

## Exit criteria

Applicable automated tests, solution build and documentation preflight pass. Scenario TOOL-STATS defines observable behavior; MTP-TOOL-STATS defines maintained terminal/restart checks. Platform and live-adapter verification limits must be reported separately from automated fixture evidence.

## Boundaries

No invocation history, payload retention, retrospective event replay, model-facing statistics tool, separate executor, dispatcher, modal runtime or statistics cache. Storage faults cannot change tool results or produce apparently exact incomplete snapshots.

[Implementation plan](../Threadsmith-tool-statistics-implementation-plan.md) · [Dependency DAG](dependency-dag.md)
