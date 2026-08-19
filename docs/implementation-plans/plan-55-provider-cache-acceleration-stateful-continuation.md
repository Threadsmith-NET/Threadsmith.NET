# Plan 55 — Provider Cache Acceleration and Recoverable Stateful Continuation

**Milestone:** M19 — Cache-Optimized Context Generation

**Prerequisites:** plans 51–54 and plans 31–32, 46, 48, 50

**Depends on by:** future provider optimization and model-routing work

**Status:** Complete. Capability planning, strict continuation binding, and canonical stateless recovery coverage pass. Compiled providers intentionally remain stateless until a safe official acceleration contract is enabled; MTP-218 remains the maintained procedure for any future live-provider acceleration.

## 1 Objective

Let provider adapters exploit explicit cache controls and optional stateful continuations while canonical stateless requests remain the correctness, recovery, audit, and capacity authority.

## 2 Architectural Context

Providers differ: some cache exact prefixes automatically, some accept cache breakpoints, some expose response/thread continuation IDs, and some provide no caching. Core contracts must describe capability and outcome without importing provider semantics.

## 3 Scope

Provider cache capabilities; explicit breakpoint planning; automatic-prefix behavior; opaque continuation references; strict binding/invalidation; stateless fallback/recovery; provider-native framing/capacity; cache dashboards and tuning fixtures.

## 4 Non-Scope

Correctness dependence on remote state, durable provider wire payloads, arbitrary cache directives from repository content, bypassing context/tool/phase policy, or claiming savings a provider does not report.

## 5 Current State

Provider-neutral cache capabilities, bounded host/repository/native-tool/phase breakpoint plans, strict provider/profile/request-session/phase/trust-policy/layout/instruction/tool/compaction/stateless-request continuation bindings, and precise named reassembly reasons are implemented over canonical stateless authority. Compiled providers conservatively remain stateless because no safe configured explicit-cache or protected opaque-continuation contract is enabled; providers without support are unchanged and remote automatic prefix behavior may still apply. Maintained real-provider acceleration checks and any future provider-owned protected reference implementation remain gated by official support.

## 6 Proposed Design

Expose immutable capabilities including automatic prefix caching, explicit cache control, stateful continuation, cached-token reporting, maximum breakpoints, and minimum cacheable prefix tokens. Capabilities affect optimization only.

A provider adapter may place bounded cache breakpoints only after eligible stable policy/repository/tool/phase sections, respecting provider limits. Breakpoint projection is deterministic and not model-visible content.

Store only opaque continuation references in a protected provider-owned persistence envelope bound to provider, profile/model, session, request generation, instruction bundle, trust/policy/tool inventory, context-layout version, compaction generation, and stateless request digest. Invalidate on mismatch, model switch, instruction/trust/tool legality change, incompatible compaction, logout, or provider rejection. Always retain canonical stateless reconstruction and retry once through it only when replay is safe.

## 7 Public Contracts

Add provider-neutral cache capability, breakpoint class/plan, continuation binding/version/status, invalidation reason, and optimization outcome. Opaque references and provider DTOs remain adapter-owned and excluded from model-visible/durable public projections.

## 8 Project/File Changes

`Threadsmith.Models`, provider projects, Context/Execution generation binding, protected persistence, TUI/CLI context/cache inspection, telemetry, golden fixtures, tests, operations docs, ADR, and DOX.

## 9 Ordered Tasks

1. Verify official caching/continuation contracts for each compiled provider.
2. Define conservative provider-neutral capabilities and generation bindings.
3. Implement deterministic explicit breakpoint planning.
4. Add protected opaque continuation storage, invalidation, and stateless recovery.
5. Implement adapters one at a time; providers without support remain stateless.
6. Add dashboards/inspection and measured tuning against Plan-51 baseline.
7. Add failure/restart/security tests, docs, manual tests, ADR, and DOX.

## 10 Testing

Cover capability negotiation, min-prefix/max-breakpoint limits, exact markers, providers without caching, continuation success, restart, model/instruction/trust/tool/compaction invalidation, remote expiry/rejection, logout, cancellation, unsafe replay denial, stateless recovery equivalence, and cached-usage reporting. Golden fixtures must prove optimization metadata does not alter semantic request content.

## 11 Security and Permissions

Continuation references are credentials-like opaque data: never prompt, log, hook, diagnostic, repository, or public projection content. Provider state grants no authority and cannot preserve access after trust/policy/tool invalidation.

## 12 Observability

Record capabilities, selected cache family, breakpoint count/classes, continuation used/invalidated/fallback reason, provider-reported cache usage, hit ratio, and labeled savings. Never record opaque references or cacheable content.

## 13 Migration and Compatibility

Default all providers to stateless/no explicit caching. Missing or corrupt continuation state is discarded safely. Disabling optimization restores canonical stateless behavior without changing results.

## 14 Acceptance Criteria

- Provider caching is an optional adapter optimization.
- Explicit breakpoints are deterministic, bounded, and placed only at eligible stable boundaries.
- Continuations are opaque, tightly bound, safely invalidated, and recoverable statelessly.
- Providers without support behave unchanged.
- Cache/cost/latency reporting is honest and measured against the baseline.

## 15 Risks

Remote-state drift, secret-like reference leakage, unsafe replay, provider protocol change, and misleading performance dashboards. Mitigate with strict bindings, redaction, replay classification, exact fixtures, and stateless authority.

## 16 Documentation

Document provider capabilities, breakpoint/invalidation behavior, privacy, recovery, `/context` reporting, and operational disablement. Add maintained provider-specific manual cases before completion.

## 17 Open Decisions

For each adapter, verify whether continuation references are safe to persist, their lifetime/account binding, replay semantics, and official cache-control syntax. If uncertain, retain stateless behavior.
