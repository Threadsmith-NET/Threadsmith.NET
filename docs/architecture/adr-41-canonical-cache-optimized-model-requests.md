# ADR-41 — Canonical Cache-Optimized Model Requests Preserve Stateless Authority

**Status:** Accepted

## Context

Threadsmith previously assembled most governed context as one changing user document. Provider adapters then added native tool definitions and protocol framing that context budgeting did not fully represent. Rebuilding that document after a tool call changed earlier bytes, duplicated schemas in model-visible text, and reduced safe provider prefix-cache reuse. Repository prompt appends were bounded and versioned, but applicable hierarchical `AGENTS.md` files had no host-owned resolution boundary.

Provider caching differs by protocol and account. Some providers report cache-read counters, some cache exact prefixes automatically, and some expose explicit cache controls or remote continuation identifiers. None of those mechanisms may become a correctness, trust, permission, audit, or recovery dependency.

## Decision

- The host owns closed structured system, developer, user, assistant, and tool messages. Stable host policy, confined repository instructions, and phase policy precede bounded governed memory, chronological complete turns, governed state/evidence, and the current user input.
- Tool definitions are canonicalized once by stable group and id. JSON object members and set-like `required` arrays use deterministic encoding while all supported schema values remain semantically intact, including the difference between an absent default and an explicit `null` default. Duplicate or invalid schema structures fail before provider invocation.
- Native-capable providers receive the canonical tool inventory only through their native protocol. A single deterministic textual renderer remains available for legacy adapters.
- Capacity planning includes structured message content, canonical native or textual tool schemas, framing, and the selected model's request output reserve. Cache-read/write counters remain optional; absent counters are unavailable rather than zero.
- Context segments have closed volatility classes and content digests. Evidence ordering is deterministic and append-friendly for equal-ranked items, preserves provenance, and contains no incidental request identifiers or timestamps unless semantically required.
- Each turn boundary resolves the applicable root-to-scope `AGENTS.md` chain plus configured prompt appends using confined strict-UTF-8 snapshots. The bundle is untrusted, subordinate to host policy, content-addressed, and revalidated independently of watcher delivery.
- Tool continuations freeze the original structured prefix and append correlated assistant calls and tool results. A phase, trust/policy, tool inventory, instruction bundle, compaction, model, or layout generation change requires named reassembly.
- Provider cache controls and opaque stateful continuations are optional adapter optimizations. Compiled providers remain stateless when a safe official contract is unavailable. Canonical stateless reconstruction is always the audit and recovery authority.
- Opaque continuation references, if introduced by a provider adapter, are credentials-like: they never enter prompts, repository state, hooks, diagnostics, logs, or public projections and must be bound to every authoritative generation before reuse.

## Consequences

Identical stable sections and eligible tools serialize deterministically, native schemas are not duplicated in context text, and provider framing is visible in `/context` inspection. Current input remains last and complete prior turns preserve role chronology. Tool-result continuations can reuse a byte-stable prefix while legacy scripted providers retain deterministic textual compatibility input.

Repository instruction changes are observed at the next turn even if a watcher notification is lost; ordinary source edits do not change the instruction bundle. Cache reporting is honest but may remain unavailable for providers that omit counters. Provider-specific cache acceleration can be added independently without changing the canonical request or granting remote state authority.
