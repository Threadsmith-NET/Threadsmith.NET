# Implementation Plan 02: Core Commands, Events, Projections, and Cancellation

**Milestone:** M1 — Core Host, Events, and TUI Shell
**Strategy source:** §9 (core domain model), §10 (execution engine incl. §10.7), §24.2–24.4 (concurrency/process), §29 (ADRs 4, 5, 20)
**Prerequisite plans:** plan-01

## 1. Objective
Deliver the durable core contracts — stable identifiers, the `RunPhase` state machine + transition contracts, the immutable domain-event stream, host-owned projections, the application command dispatcher, and end-to-end `CancellationToken` plumbing — that every later subsystem builds on.

## 2. Architectural Context
Parent: Foundation (§28). This is the `Threadsmith.Core` + `Threadsmith.Execution` + `Threadsmith.Persistence`-baseline + configuration bootstrap. The TUI and fake model (plans 03, 04) consume these contracts. Read `00-shared-context.md` §E before starting.

## 3. Scope
- Stable identifiers (§9.1): `SessionId`, `RunId`, `StepId`, `ToolInvocationId`, `MutationSetId`, `MutationId`, `EvidenceId`, `ApprovalId`, `ExtensionId`, `ExtensionGenerationId`, `CapabilityId`, `ModelProfileId`, `WorkspaceId`.
- `RunPhase` enum + transition contract (§9.2, §9.3) including preconditions, required evidence, allowed tools, approval level, I/O types, retry policy, budget impact, cancellation behavior, events emitted, rollback, failure classification.
- Domain event catalog (§9.4) as immutable records.
- Host-owned projections (§9.5) and an in-memory projection store.
- Application command dispatcher (commands + queries); all side effects go through it with `CancellationToken`.
- Model output contract base (§10.2) with schema-version field.
- Budget framework (§10.3) — token/call/wall-clock accrual + controlled-pause hook (cost dimension added in plan-07/08 per gap #4).
- Retry classification framework (§10.4).
- Approval policy hook (§10.5) — model cannot self-authorize destructive actions.
- **Execution Turn & Concurrency Contract (§10.7)** encoded as core invariants: single mutable baseline + copy-on-write staging, read-only parallelism, turn-boundary invalidation, turn-granular cancellation.
- Configuration bootstrap (§21) layered providers; secrets stub (real secret store in plan-08).
- Structured logging + tracing baseline (§23.1, §23.2) with redaction.

## 4. Non-Scope
- No TUI (plan-03). No real model provider (plan-07). No tools (plan-08). No Roslyn (plan-06). No full SQLite schema (plan-18) — only the minimal event-store spike promoted to a real (still small) store.

## 5. Current State
Implemented. `Threadsmith.Core` owns stable identifiers, commands, the complete versioned event catalog, projection DTOs, run phases, semantic/repository contracts, and cancellation-aware interfaces. `Threadsmith.Execution` supplies the command dispatcher, absorbing state machine, bounded fan-out event stream, detached projections, budget/retry/sanitization, and session application; `Threadsmith.Persistence` durably stores schema-versioned events. The composition root subscribes projection, persistence, telemetry, semantic, and TUI consumers independently.

## 6. Proposed Design
- Identifiers as `readonly struct` or `record` (serializable, comparable, log-safe).
- `RunPhase` enum exactly as §9.2; transitions expressed as a state-machine table validated on every transition; invalid transition throws + emits a `RunTransitionFailed` event.
- Events as immutable records implementing a marker `IDomainEvent`; each carries `SchemaVersion` (gap #3).
- Projection store: append-only event consumption → mutable host-owned DTO projections; queries return snapshots.
- Command dispatcher: `ICommandHandler<TCommand, TResponse>` with `CancellationToken`; a single application-level dispatcher with middleware (logging, validation, policy hook).
- Turn contract enforced in `Threadsmith.Execution`: a `Turn` owns a baseline snapshot reference; staging is private to the turn; commit at boundary replaces baseline; invalidation queue drained at boundary.

## 7. Public Contracts
- All identifiers (§9.1).
- `RunPhase`, `TransitionContract`, `IStateMachine`.
- `IDomainEvent`, full §9.4 catalog (with `SchemaVersion`).
- `IProjection`, `IProjectionStore`, `ProjectionKey`.
- `ICommandDispatcher`, `ICommandHandler<,>`, `IQueryHandler<,>`.
- `IBudget` + `BudgetDimensions` (extensible for cost).
- `RetryClassification` enum.
- `IApprovalPolicy`.
- `ITurn`, `IBaselineSnapshot`, `IStagingView` (§10.7).
- Configuration root + `IConfiguration` bootstrap.

## 8. Project and File Changes
- `Threadsmith.Core/`: identifiers, `RunPhase`, events, projections, commands, budget, retry, approval interfaces, turn/baseline/staging contracts.
- `Threadsmith.Execution/`: state machine, turn implementation, dispatcher middleware.
- `Threadsmith.Persistence/`: minimal event store (SQLite, promoted from spike) + append/read.
- `Threadsmith.Telemetry/`: logging + tracing + redaction baseline.
- `tests/Threadsmith.Core.Tests/`, `tests/Threadsmith.Execution.Tests/`.

## 9. Ordered Implementation Tasks
1. Identifiers (§9.1).
2. `RunPhase` + transition contract + state machine (§9.2, §9.3).
3. Event catalog as immutable records with `SchemaVersion` (§9.4; gap #3).
4. Projection store + host-owned DTO projections (§9.5).
5. Command dispatcher + handlers + `CancellationToken` middleware.
6. Budget framework (§10.3) with cost extension point.
7. Retry classification (§10.4).
8. Approval policy hook (§10.5).
9. Turn contract implementation: baseline snapshot, staging, turn-boundary commit/invalidation (§10.7).
10. Configuration bootstrap (§21) + secrets stub.
11. Logging/tracing/redaction baseline (§23).
12. Minimal SQLite event store (promote plan-01 spike).
13. ADRs 4, 5, 20 drafted/updated.

## 10. Testing
- State machine: every legal transition accepted; every illegal transition rejected with event.
- Events: round-trip serialize/deserialize; `SchemaVersion` preserved.
- Projections: event append → projection reflects new state; snapshot returned, not live object.
- Dispatcher: command → handler → result; cancellation propagates; middleware runs in order.
- Turn contract: staging invisible to a concurrent read tool; invalidation queued and applied at boundary; cancel discards staging.
- **Open decision:** add a `RolledBack` terminal phase or document that rollback maps to `Cancelled` (assessor note §3.9) — resolve here, before any plan depends on terminal-phase semantics.

## 11. Security and Permissions
- Redaction in logging (no secrets) — establish the redaction pipeline now.
- Approval policy hook present but no real policy until plan-08.

## 12. Observability
- Every command + transition emits structured events + OTel spans.
- Metrics: events/sec, transition latency, turn commit latency.

## 13. Migration and Compatibility
- Event `SchemaVersion` set to `1` from day one (gap #3) — future readers must tolerate `N−1`.

## 14. Acceptance Criteria
- M1 exit criteria subset (core half): a session can be created, scripted activity recorded as events, projections updated, cancellation propagated through the dispatcher.
- Turn contract test: a parallel read tool cannot observe a half-applied mutation; invalidation applied only at boundary (§10.7 invariants 1–6).
- All §9.4 events serializable with `SchemaVersion`.

## 15. Risks and Mitigations
- **Event schema drift** (gap #3): `SchemaVersion` from day one; document reader-tolerance policy.
- **Turn contract ambiguity** (gap #1, resolved in §10.7): encode all 6 invariants as executable tests.
- **Over-engineering the dispatcher**: keep middleware minimal; add middleware only when a second consumer appears (§8.2, no single-use abstractions).

## 16. Documentation
- ADRs 4 (explicit state machine), 5 (event-oriented durable session model), 20 (policy-gated side effects).
- `docs/architecture/event-catalog.md` listing every event + version.

## 17. Current Decisions
- `RolledBack` is a distinct terminal phase in the absorbing run state machine.
- Events carry `SchemaVersion`; plan 18 owns restoration migrations and broader reader-tolerance policy.
- Budget dimensions include estimated cost, which plans 07–08 wire into model and tool execution.
