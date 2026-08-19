# Plan 52 — Structured Chronological Model Requests

**Milestone:** M19 — Cache-Optimized Context Generation

**Prerequisites:** plan 51 and plans 09, 33–35, 37, 46, 50

**Depends on by:** plans 53–55

**Status:** Complete. Structured and legacy projection compatibility, chronology, and append-only continuation coverage pass; MTP-215 remains a maintained provider-wire regression procedure.

## 1 Objective

Replace the monolithic changing user document with provider-neutral structured messages whose stable policy and chronological conversation form the longest safe exact prefix, with the current turn or newest tool result last.

## 2 Architectural Context

Conversation-aware continuity is bounded and governed, but synthesizing one reordered document defeats provider prefix caches. Structured messages must improve serialization without making transcript state authoritative or weakening host-owned execution state.

## 3 Scope

System/developer/user/assistant/tool roles; stable host/repository/phase segments; chronological normalized replay; deterministic tool-call/result representation; append-only tool continuation; compatibility rendering for legacy adapters; cache-family/version identity; golden wire fixtures.

## 4 Non-Scope

Unbounded replay, hidden reasoning replay, AGENTS.md discovery, explicit provider cache controls, or authoritative provider-side threads.

## 5 Current State

Context assembly emits closed structured messages with stable host/repository/phase policy first, complete recent turns in role chronology, governed state/evidence later, and current input last. OpenAI-compatible and Codex adapters project native roles and correlated tool items. Multi-round execution freezes the initial structured prefix and appends tool calls/results; deterministic legacy input remains for compatibility. Focused M4/M19 coverage passes; maintained exact wire checks remain.

## 6 Proposed Design

### 6.1 Provider-neutral messages

Use closed roles and typed content parts. Stable host policy precedes stable repository instructions and phase contract. Compacted summary/durable decisions precede complete recent turns. Approved run state and evidence follow the conversation prefix. Current user input or newest attributable tool result is last.

### 6.2 Chronological invariants

Append normalized complete messages; never reorder old messages or insert newly retrieved memory between them. Sanitization and serialization are deterministic. Hidden reasoning is excluded. Compaction alone may replace an explicitly bounded old-message range.

### 6.3 Tool continuations

Capture an immutable request-prefix snapshot. Append assistant tool call and correlated tool result without rebuilding prior evidence or inventory unless a real phase/policy/context-generation transition requires reassembly.

### 6.4 Legacy renderer

Adapters without structured-message support receive one deterministic compatibility rendering of the same semantic request. Correctness and audit remain reconstructible statelessly.

## 7 Public Contracts

Add provider-neutral message roles/content parts, normalized tool-call/result correlation, request layout/cache-family version, and adapter structured-message capability. Provider wire types remain internal.

## 8 Project/File Changes

`Threadsmith.Models`, `Threadsmith.Context`, `Threadsmith.Execution`, provider adapters, archive projection, fake provider/fixtures, inspection surfaces, and tests.

## 9 Ordered Tasks

1. Inventory current assembly/provider projection and freeze compatibility fixtures.
2. Define typed messages and deterministic canonical serialization.
3. Project stable policy and bounded archive chronologically with current input last.
4. Implement append-only tool continuations and transition-triggered reassembly.
5. Add legacy rendering and migrate OpenAI-compatible/Codex adapters.
6. Add prefix-diff inspection, golden tests, docs, manual tests, ADR, and DOX.

## 10 Testing

Prove changing only the current question preserves all prior bytes; appending a turn leaves prior messages unchanged; role chronology is exact; tool continuations append only; hidden reasoning never appears; phase transitions create deliberate cache families; legacy and structured renderers are semantically equivalent.

## 11 Security and Permissions

Message roles confer no authority. Tool eligibility, approvals, trust, evidence provenance, phase gating, sanitization, and bounds remain host-owned. Untrusted content cannot become system/developer policy by supplying role-like text.

## 12 Observability

Record layout version, cache family, stable-prefix boundary, reassembly reason, per-role/section tokens, and longest shared prefix without recording content.

## 13 Migration and Compatibility

Archive records remain host-owned and are projected at request time. Existing providers use compatibility rendering until explicitly declaring structured support. Old execution records remain auditable.

## 14 Acceptance Criteria

- Current input is last after chronological bounded history.
- Prior normalized message bytes do not change when a turn is appended.
- Tool continuations preserve the original prefix byte-for-byte when state is unchanged.
- Structured and legacy adapters preserve semantic correctness.
- Transcript replay does not replace governed state.

## 15 Risks

Role confusion, provider role differences, duplicated current turns, continuation mismatch, and token regression. Mitigate with closed roles, adapter validation, generation fencing, and exact fixtures.

## 16 Documentation

Document request ordering, cache families, chronological replay, tool continuation, legacy behavior, and `/context` prefix diagnostics.

## 17 Open Decisions

Finalize developer-role fallback and content-part normalization per adapter from official provider contracts before implementation.
