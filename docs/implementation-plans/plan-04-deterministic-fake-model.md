# Implementation Plan 04: Deterministic Fake Model and Scripted Session

**Milestone:** M1 — Core Host, Events, and TUI Shell
**Strategy source:** §26.2 (deterministic model double), §11 (model integration — abstraction only, fake impl), §10.2 (model output contracts), §34 (scenarios A–H all need a drivable model)
**Prerequisite plans:** plan-02 (output contracts, dispatcher), plan-03 (TUI/CLI surfaces to observe activity)

## 1. Objective
Deliver a deterministic, scriptable model test double that streams structured outputs, requests tools, and fails on cue — the foundation for every later plan's E2E and integration tests, and the only "model" available until plan-07.

## 2. Architectural Context
Parent: Foundation → Model abstraction (§28). The fake implements the plan-02 model-output contract and the plan-07 `IModelProvider` precursor (the interface is forward-defined here so the fake is not a throwaway). It is the engine for scenarios A–H (§34) and the M1 scripted-session exit criterion. Read `00-shared-context.md` §E (§10.2) before starting.

## 3. Scope
- A `FakeModelProvider` implementing the host-owned model abstraction interface (forward-defined from plan-07).
- A script format (YAML or JSON) describing a session: ordered turns, each turn emitting a structured model output (tool request / plan / mutation / text), usage deltas, and optional failure.
- Streaming: emits output token-by-token with cancellable delays.
- Tool-request emission: produces a well-formed tool-call DTO that the (later) tool runtime can consume.
- Failure injection: transient provider error, malformed output, mid-stream cancellation.
- Usage reporting: emits token usage per §11.7 (and the missing-usage case, gap #4).
- Determinism: same script + seed → identical output, no wall-clock dependence.

## 4. Non-Scope
- No real model provider (plan-07). No real tool execution (plan-08) — tool requests are emitted but the fake doesn't require tools to actually run (M1 only needs scripted activity observable).
- No prompt-asset rendering (plan-09) — the fake emits pre-baked structured outputs.

## 5. Current State
Implemented. `FakeModelProvider` replays versioned JSON `ScriptedSession` fixtures with seed-dependent deterministic output, a configurable `TimeProvider`, cancellable delay, usage or missing-usage behavior, JSON-object tool requests, and explicit transient, malformed, and cancellation failures. Each provider call stops after its next tool request and resumes from the request-owned zero-based `ToolContinuationRound`, so governed tool-result continuation cannot replay the same tool indefinitely and concurrent runs share no mutable cursor. Milestone tests verify deterministic replay, continuation, fixture coverage, failure classification, terminal state, tool-request validation, and TUI/headless parity.

## 6. Proposed Design
- Define the minimal `IModelProvider` interface (streaming, cancellation, usage) in `Threadsmith.Models` now — it is the contract plan-07 will implement for real.
- `FakeModelProvider` reads a `ScriptedSession` and replays it as an `IAsyncEnumerable<ModelChunk>` with cancellation.
- A script driver in tests loads a script and asserts the resulting event stream matches expectations.
- Scripts live under `tests/fixtures/scripts/` and are referenced by scenario tests (plan-09+ and scenarios A–H).

## 7. Public Contracts
- `IModelProvider`, `ModelChunk`, `ModelUsage`, `ModelStreamRequest` (in `Threadsmith.Models` — stable, plan-07 will implement).
- `ScriptedSession` script schema (versioned).

## 8. Project and File Changes
- `Threadsmith.Models/`: `IModelProvider` + DTOs.
- `Threadsmith.Models.Fake/` (or under `Threadsmith.Models`): `FakeModelProvider`, script loader.
- `tests/fixtures/scripts/`: scenario scripts.
- `tests/Threadsmith.Execution.Tests/` or a dedicated `Threadsmith.FakeModel.Tests/`: determinism + failure-injection tests.

## 9. Ordered Implementation Tasks
1. Define `IModelProvider` + `ModelChunk` + `ModelUsage` + `ModelStreamRequest` (forward of plan-07).
2. Define `ScriptedSession` schema (versioned).
3. `FakeModelProvider`: scripted replay as `IAsyncEnumerable<ModelChunk>`.
4. Streaming with cancellable delays.
5. Tool-request emission (well-formed DTO per §10.2).
6. Failure injection: transient error, malformed output, mid-stream cancel.
7. Usage reporting incl. missing-usage case (gap #4).
8. Determinism test: same script + seed → identical event stream.
9. Scripts for scenarios A–H stubs (enough to drive M1 scripted session).

## 10. Testing
- **Determinism (§26.2):** same script + seed → byte-identical output across runs.
- Cancellation: cancel mid-stream → stream terminates, no orphaned tasks.
- Failure injection: each failure type produces the expected retry classification (§10.4) — validates the plan-02 retry framework.
- Missing-usage handling: provider omits usage → budget layer degrades gracefully (gap #4).
- Tool continuation: text → tool → final-text script executes the tool once, resumes at round 1, and completes; a separate run begins at round 0.

## 11. Security and Permissions
- Scripts must not contain real secrets; any "key" in a script is a placeholder.

## 12. Observability
- Fake emits the same `ToolInvocation*` / model events a real provider would, so telemetry paths are exercised identically.

## 13. Migration and Compatibility
- `IModelProvider` is a stable contract from day one; plan-07 implements it for real — do not break this interface later.

## 14. Acceptance Criteria
- M1 exit criterion: scripted activity observable in TUI and CLI (with plan-03).
- Determinism test passes (same seed → identical stream).
- All three failure types produce correct retry classification.
- Missing-usage case handled without crashing the budget layer.

## 15. Risks and Mitigations
- **Fake drift from real provider contract:** define `IModelProvider` now and freeze it; plan-07 adapts to it, not vice-versa.
- **Non-determinism from timers:** drive delays from a seedable clock, not wall-clock.

## 16. Documentation
- `docs/testing/fake-model-scripts.md` (script format + examples).

## 17. Current Decisions
- Script fixtures use JSON and are documented in `docs/testing/fake-model-scripts.md`.
- `ModelChunk` carries one optional tool request per chunk; a turn may emit multiple chunks without changing `IModelProvider`.
- `ModelStreamRequest.ToolContinuationRound` is zero-based and request-owned; the fake remains stateless across runs.
