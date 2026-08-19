# Implementation Plan 07: Model Provider Abstraction and OpenAI-Compatible Adapter

**Milestone:** M3 — Model and Tool Runtime
**Strategy source:** §11 (Model Integration Strategy), §10.2 (output contracts), §10.3 (budgets incl. cost — gap #4), §10.4 (retry), §11.7 (streaming + usage), §29 (ADR 10), §36 (host-owned abstraction)
**Prerequisite plans:** plan-04 (`IModelProvider` frozen interface + fake), plan-02 (output contracts, budget framework, retry classification)

## 1. Objective
Deliver the host-owned model abstraction backed by a real OpenAI-compatible provider, with profiles, capability negotiation, streaming normalization, usage reporting (including the missing-usage case), and **cost as a budget dimension** (gap #4) — replacing the fake for real use.

## 2. Architectural Context
Parent: Foundation → Model abstraction → Tool runtime (§28). This is `Threadsmith.Models` + a provider adapter. The `IModelProvider` interface was frozen in plan-04; this plan implements it for real and wires the budget layer to accept `EstimatedCost`. Model SDK types must not leak (§7.1, §8.1). Read `00-shared-context.md` §B + §E (§10.2/§10.3) before starting.

## 3. Scope
- Host-owned model abstraction finalized (built on plan-04 `IModelProvider`).
- OpenAI-compatible adapter (streaming completions, tool-call DTOs).
- Model profiles (§11.4): id, context window, capabilities, limits, cost metadata, **`IntendedWorkloadClasses`**.
- Capability negotiation (§11.5): does the provider support tool calls / structured output / streaming.
- **Configured-models list** (§21): the set of `ModelProfile`s available to the host, sourced from repo/session config (provider, endpoint, model id, key ref, workload classes, cost). Multiple profiles may target the same provider.
- **Model selection contract** (`IModelSelectionPolicy` + `ModelSelectionRequest`): resolve a `ModelProfile` from a requested **workload class** + capability requirements + constraints (context window, tool support, cost ceiling, sensitive-data policy), gated by §11.5 negotiation. This is the foundation for per-skill/per-extension model preference (plan-09 / plan-14 / plan-16).
- Streaming normalization (§11.7): provider chunks → host `ModelChunk`s.
- Usage reporting (§11.7): prompt/completion tokens; **handle missing-usage** (gap #4).
- **Cost as a budget dimension** (gap #4): `EstimatedCost` accrues; cost-pause policy alongside token/wall-clock.
- Structured output validation against §10.2 contracts (schema-versioned).

## 4. Non-Scope
- No tool runtime (plan-08). No context governance (plan-09). No prompt-asset rendering (plan-09). No MCP-provided models.

## 5. Current State
Implemented. The host-owned model catalog, selection policy, OpenAI-compatible streaming adapter, structured-output validation, usage/cost estimation, retries, and cancellation are wired in the composition root. Credentials remain logical references until the Plan-62 `ISecretResolver` resolves them at the final boundary; static secret values stay outside ordinary configuration.

## 6. Proposed Design
- Adapter wraps a minimal OpenAI-compatible `HttpClient`/SSE implementation behind the host facade; no HTTP or provider-specific type leaks per §36.
- Profiles drive routing + budget cost projection: `costPerMTokenPrompt`, `costPerMTokenCompletion`.
- Missing-usage handling: if the provider omits usage in the stream, the layer estimates from tokenized input/output and flags the estimate (gap #4).
- Cost accrues in the budget layer (plan-02 extension); cost-pause triggers at the configured cost ceiling.
- **Configured-models list:** loaded from repo/session config (§21) as an ordered `ModelProfile[]`; each profile is referenced by stable `ModelProfileId` (§9.1). The list is the universe from which all selection happens — skills/extensions never supply arbitrary endpoints or keys, they only express preferences *over this list* (see plan-14/plan-16/plan-09).
- **Model selection contract:** `IModelSelectionPolicy.Resolve(ModelSelectionRequest) → ModelSelectionResult`. A `ModelSelectionRequest` carries a requested `workloadClass` (e.g. `planning`, `code-edit`, `review`, `summary`), required capabilities (tool/structured-output/streaming), and hard constraints (min context window, max cost-per-Mtoken, sensitive-data policy). `Resolve` filters the configured list to compatible profiles (§11.5 negotiation: reject profiles that lack required capabilities *before* selection), then picks per host policy (default: lowest cost meeting constraints; overridable by session/user default-model choice and by advisory hints from plan-16 contributors). The result records the chosen `ModelProfileId` + the rationale (which constraints filtered which profiles) so plan-09 can surface it and execution records can cite it. **Skills/extensions are advisory only** — they contribute hints via plan-14/plan-16; the host policy + user defaults + budget make the final pick (§5.1 host owns control flow; the model cannot self-select to bypass policy).

## 7. Public Contracts
- `IModelProvider` (frozen in plan-04 — do not break).
- `ModelProfile` (incl. `IntendedWorkloadClasses`), `ModelCapabilitySet`.
- `ModelUsage` (extended with `EstimatedCost` + `IsEstimate`).
- Cost-budget extension to plan-02 `BudgetDimensions`.
- **Model selection:** `IModelSelectionPolicy`, `ModelSelectionRequest` (workload class + required capabilities + constraints), `ModelSelectionResult` (chosen `ModelProfileId` + rationale), `WorkloadClass` enum (extensible). Stable from day one so plan-09/plan-14/plan-16 build on it.

## 8. Project and File Changes
- `Threadsmith.Models/`: profiles, capability negotiation, cost metadata.
- `Threadsmith.Models.OpenAI/` (or under `Threadsmith.Models`): OpenAI-compatible adapter.
- `Threadsmith.Execution/`: budget cost-dimension wiring (plan-02 extension).
- `tests/Threadsmith.Models.Tests/` (or new project): adapter tests with a mock HTTP server.

## 9. Ordered Implementation Tasks
1. Finalize `ModelProfile` + cost metadata + `IntendedWorkloadClasses` (§11.4).
2. **Configured-models list** loaded from repo/session config (§21) as `ModelProfile[]` keyed by `ModelProfileId`.
3. Capability negotiation (§11.5).
4. **Model selection contract:** `IModelSelectionPolicy` + `ModelSelectionRequest`/`Result` + `WorkloadClass` enum; `Resolve` filters configured list by required capabilities + constraints, picks per host policy, returns rationale. **Note:** the `Resolve` signature accepts an optional `IReadOnlyList<ModelPreferenceHint>` parameter (the hints are supplied by the caller — plan-09 — not resolved here); this plan does **not** implement hint aggregation (that is plan-16/plan-09). The contract is stable from day one so plan-09/plan-14/plan-16 build on it.
5. OpenAI-compatible adapter: streaming completions.
6. Tool-call DTO normalization to §10.2 contracts.
7. Usage reporting + missing-usage estimation (gap #4).
8. Cost dimension in budget layer + cost-pause policy (gap #4).
9. Structured output validation (schema-versioned, §10.2).
10. Cancellation through the adapter (§5.8) — HTTP cancel.
11. ADR 10 (host-owned model abstraction) finalized.

## 10. Testing
- Mock OpenAI-compatible server (use a recorded-response fixture, not a real key) → stream + tool calls + usage.
- Missing-usage case → estimated usage flagged `IsEstimate=true`; cost accrues on estimate.
- Cost-pause: exceed cost ceiling → controlled pause per §10.3.
- Retry classification (§10.4): transient HTTP 529/503 retried; 4xx not; malformed output not retried as transient.
- Cancellation: cancel mid-stream → HTTP cancelled, no orphaned task.

## 11. Security and Permissions
- Provider key from the secret store (plan-08 wires the real store; plan-07 uses the stub + config). Never logged (§22.3 redaction).
- Outbound network only to the configured endpoint (policy gate via plan-08).

## 12. Observability
- Per-call: model id, tokens, cost, latency, retries, success/failure (redacted of content).
- Cost rollup per run + per session.

## 13. Migration and Compatibility
- `IModelProvider` frozen — adapter implements, does not modify.

## 14. Acceptance Criteria
- M3 subset: a real model streams structured output + tool requests through the host abstraction.
- Missing-usage handled (gap #4).
- Cost accrues and can pause a run at the cost ceiling.
- **Model selection:** given a configured list of ≥2 profiles, a `ModelSelectionRequest` for a workload + required capabilities resolves to a compatible profile (§11.5 negotiation rejects incompatible ones) with recorded rationale; profiles outside the configured list are never selectable.
- No model SDK type leaks into `Threadsmith.Core`/events/persistence (architecture test).

## 15. Risks and Mitigations
- **Provider variability (§30.5):** capability negotiation + structured-output validation reject unsupported shapes rather than guessing.
- **Missing usage under-reporting cost (gap #4):** estimation + `IsEstimate` flag; cost-pause uses max(reported, estimate).
- **SDK leakage (§36):** host facade; architecture test asserts no `IChatClient`/SDK type in domain events.

## 16. Documentation
- ADR 10 (host-owned model abstraction).
- `docs/operations/model-providers.md` (profile config + cost).

## 17. Current Decisions
- The real provider is a host-owned `HttpClient`/SSE OpenAI-compatible adapter; no provider SDK type crosses the boundary.
- Missing usage is estimated from request/response text and the selected profile's cost rates, with `IsEstimate` preserving provenance.
