# Plan 51 — Wire Cache Telemetry and Canonical Native Tools

**Milestone:** M19 — Cache-Optimized Context Generation

**Prerequisites:** plans 07–09, 18, 26–27, 31–32, 35, 46, 49–50

**Depends on by:** plans 52–55

**Status:** Complete. Deterministic provider projection and cache-reporting coverage pass; optional live-provider observations remain maintained under MTP-214 and MTP-218 without gating correctness or implying measured savings.

## 1 Objective

Measure the exact provider wire request and remove immediate native-tool schema waste without changing tool eligibility, phase legality, capacity safety, or provider behavior.

## 2 Architectural Context

Context estimates currently emphasize assembled logical content, while provider adapters own final message/tool framing and providers report cache usage differently. Native-tool providers must not also receive full textual tool schemas. Cache optimization requires provider-neutral accounting plus deterministic provider-owned serialization.

## 3 Scope

- Provider-neutral `InputTokens`, `CachedInputTokens`, `CacheWriteTokens`, and `CacheReadTokens` usage.
- Exact estimated wire-token accounting for messages, native tools, framing, and output/reasoning reserve.
- Stable-prefix, section, cache-hit, and estimated saving telemetry.
- Canonical native tool ordering/schema serialization.
- Textual schema fallback only for adapters lacking native tool calling.
- `/context`, headless, telemetry, and deterministic wire fixtures.

## 4 Non-Scope

Structured chronological messages, AGENTS.md resolution, evidence/compaction changes, explicit cache control, and stateful continuation.

## 5 Current State

The host canonicalizes eligible tools once with deterministic ordering/encoding, schema diagnostics, a stable digest, and native-versus-text transport. Wire estimates include structured content, tools, framing, stable prefix, and output reserve. OpenAI-compatible and Codex usage normalize reported cache reads while absent counters remain unavailable; `/context` exposes logical/wire/tool costs. Focused M19 coverage passes; maintained provider-wire and live counter checks remain.

## 6 Proposed Design

### 6.1 Wire accounting

Introduce a host-owned immutable wire estimate containing logical unique tokens, actual estimated wire tokens, per-section tokens, textual/native tool-schema tokens, stable-prefix tokens, and reserve/framing costs. Provider adapters contribute bounded framing estimates without leaking wire DTOs.

### 6.2 Cache usage normalization

Normalize provider counters without inventing unsupported values. Distinguish unavailable from zero, define whether reads are included in total input, and retain provider provenance. Cost/latency savings are estimates labeled with their assumptions.

### 6.3 Canonical tools

Build eligible inventories by stable group and stable tool ID. Canonicalization is representation-only: normalize JSON encoding, object-member order, set-like arrays such as `required`, and schema-version projection without changing model-visible schema semantics. Preserve every supported semantic schema keyword and value from MCP/extension definitions, including `default` and the distinction between an absent default and an explicit `null` default; do not omit nulls where their presence is semantically meaningful. Reject or diagnose unsupported schema constructs instead of silently rewriting or dropping them. A changed optional tool must not reorder unrelated core definitions.

### 6.4 No duplicate schemas

Native-capable adapters receive schemas only through their native mechanism plus concise tool-use policy. Legacy adapters use one deterministic textual renderer. Host phase gating remains authoritative.

## 7 Public Contracts

Add provider-neutral cache usage, cache-reporting availability, wire estimate, tool transport mode, and canonical inventory digest contracts. Provider-specific cache fields remain internal.

## 8 Project/File Changes

`Threadsmith.Models`, provider projects, `Threadsmith.Context`, `Threadsmith.Tools`, MCP/extension normalization boundaries, TUI/CLI context inspection, telemetry, fixtures, and focused tests.

## 9 Ordered Tasks

1. Capture current exact wire fixtures and baseline token/cost/latency measurements.
2. Define usage semantics and wire-estimate contracts.
3. Canonicalize grouped eligible tool inventories and JSON schemas.
4. Remove native/textual duplication with an explicit legacy fallback capability.
5. Map OpenAI-compatible and Codex cache usage without guessing absent counters.
6. Include native schemas and framing in capacity planning.
7. Update `/context`, telemetry, fixtures, tests, docs, manual tests, ADR, and DOX.

## 10 Testing

Golden requests prove byte-identical stable tools, deterministic group order, unchanged unrelated tools after one MCP change, no duplicate schemas for native providers, correct legacy fallback, cache-counter mapping, and capacity inclusion of native schemas. Semantic round-trip fixtures cover absent, non-null, and explicit-null defaults plus other supported model-visible schema keywords, proving canonicalization changes encoding/order only. Boundary tests cover unsupported schema constructs, unknown counters, overflow, cancellation, and redaction.

## 11 Security and Permissions

Telemetry contains counts/digests and bounded stable IDs, never prompt content, tool arguments/results, credentials, or raw provider bodies. Optimization cannot expose an ineligible tool.

## 12 Observability

Record provider/profile, cache capability, logical/wire/stable-prefix/tool tokens, cache reads/writes/hit ratio, and labeled estimated savings. Preserve existing request correlation outside model-visible content.

## 13 Migration and Compatibility

Absent cache fields restore as unavailable. Providers without native tools retain deterministic textual schemas. Existing usage consumers remain compatible through additive optional fields.

## 14 Acceptance Criteria

- Exact estimated wire capacity includes native tools and framing.
- Provider-reported cache usage maps correctly and honestly.
- Identical eligible inventories serialize byte-for-byte.
- Canonicalization preserves supported model-visible schema semantics, including explicit `null` defaults and absence-versus-presence distinctions.
- Native providers receive no textual duplicate schemas.
- Phase/tool policy and provider correctness are unchanged.

## 15 Risks

Tokenizer mismatch, ambiguous provider counters, schema-semantic changes during canonicalization, and misleading savings estimates. Mitigate with provider fixtures, explicit semantics, semantic schema tests, and labeled estimates.

## 16 Documentation

Document usage semantics, `/context` logical-versus-wire reporting, native/textual tool modes, and provider limitations. Add M19 manual cases before completion.

## 17 Open Decisions

Select the provider-neutral unavailable-counter representation, canonical JSON encoding, and cost/latency estimation inputs after inspecting current provider responses and tokenizers.
