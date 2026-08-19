# Plan 54 — Cache-Aware Context, Evidence, Compaction, and Continuations

**Milestone:** M19 — Cache-Optimized Context Generation

**Prerequisites:** plan 53 and plans 09, 33–35, 37–40, 49

**Depends on by:** plan 55

**Status:** Complete. Deterministic evidence, compaction, continuation, and restoration coverage pass. MTP-217 remains a maintained long-session regression and measurement procedure.

## 1 Objective

Classify context by volatility, stabilize evidence serialization, compact only at deliberate turn boundaries, and preserve append-only tool continuations so volatile context does not invalidate safe stable prefixes.

## 2 Architectural Context

Evidence and memory remain governed authoritative inputs, but their placement and repeated regeneration can destroy prefix reuse. Optimization must preserve provenance, relevance, pressure reduction, execution correctness, and deterministic restoration.

## 3 Scope

Process/repository/session/phase/turn/request volatility classes; deterministic evidence identities/ranking/formatting; reusable unchanged blocks; append-friendly selection; deterministic compaction generations; cache-family transition records; append-only tool results; `/context` cache diagnostics.

## 4 Non-Scope

Weakening relevance to preserve cache, unbounded evidence retention, hiding pressure reductions, provider cache-control markers, or provider-authoritative context.

## 5 Current State

Request layouts classify process/repository/session/phase/turn/request volatility and fingerprint every segment. Evidence uses deterministic append-friendly tie ordering and serializes content identity plus complete provenance without incidental timestamps. Existing deterministic boundary compaction remains authoritative, and unchanged tool rounds append to a frozen prefix. Focused M7.4/M19 coverage passes; maintained long-session cache measurement remains.

## 6 Proposed Design

Assign every model-visible segment one closed volatility class and canonical identity. Order stable classes before phase/session state, chronological conversation, run state, evidence, and current request. Runtime IDs/timestamps stay in host metadata unless semantically required.

Evidence uses content-addressed IDs, deterministic relevance with stable tie-breakers, canonical field order, and reusable encoded blocks. Correctness/relevance wins over append preference; when ranks permit, unchanged selected blocks retain order and new evidence appends.

Compaction occurs only at complete turn boundaries after configured pressure, creates one deterministic summary generation, and remains unchanged until another compaction is required. Record replaced range, context generation, and cache-family transition. Tool continuations append correlated results to the frozen prefix unless phase, trust, inventory, instruction, compaction, or policy generation changed.

## 7 Public Contracts

Add volatility class, canonical segment/evidence digest, compaction generation/reason, cache-family transition reason, and continuation reassembly decision contracts.

## 8 Project/File Changes

`Threadsmith.Context`, evidence store, conversation compaction/retrieval, execution continuation path, persistence/events, context inspection, telemetry, fixtures, and tests.

## 9 Ordered Tasks

1. Baseline segment churn and identify nondeterministic serialization.
2. Define volatility/identity/layout contracts.
3. Canonicalize evidence ordering and reusable block encoding.
4. Make compaction thresholded, deterministic, generation-stable, and auditable.
5. Freeze continuation prefixes and enumerate mandatory reassembly transitions.
6. Integrate capacity reduction without weakening relevance/provenance.
7. Add regression/load/restart tests, docs, manual tests, ADR, and DOX.

## 10 Testing

Prove stable classes do not contain volatile timestamps/IDs; evidence tie-breaking and bytes are deterministic; one new MCP/evidence item does not reorder unrelated blocks when relevance permits; summaries remain unchanged below pressure; compaction changes only its intended boundary; continuation appends only; mandatory state transitions force safe reassembly; restart reproduces identities.

## 11 Security and Permissions

Cache preference never outranks policy, trust, current truth, relevance, secret filtering, or phase/tool legality. Stale evidence and stale continuation generations fail closed.

## 12 Observability

Record per-class tokens, segment reuse, shared-prefix length, evidence reuse/churn, compaction/cache-family changes, continuation append/rebuild reason, and estimated cache-read tokens without content.

## 13 Migration and Compatibility

Old summaries/evidence restore under legacy normalization and trigger one explicit new generation when next assembled. Existing archives remain authoritative; no silent rewriting occurs.

## 14 Acceptance Criteria

- Stable content precedes volatile evidence/current input.
- Equivalent evidence serializes deterministically with provenance intact.
- Compaction occurs only at complete boundaries and remains stable until required.
- Tool continuations append unless a named safety/correctness generation changes.
- Cache optimization never selects stale or less relevant required context.

## 15 Risks

Ranking instability, cache-biased stale context, compaction drift, oversized frozen prefixes, and continuation races. Mitigate with deterministic tie-breakers, authority-first rules, generations, budgets, and reassembly gates.

## 16 Documentation

Document volatility classes, evidence ordering, compaction/cache-family behavior, continuation invalidation, and inspection fields.

## 17 Open Decisions

Tune compaction pressure and evidence append preference only from Plan-51 measurements; do not hard-code performance claims in the contract.
