## Milestone 19 — Cache-Optimized Context Generation  *(plans 51–55)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Maximize safe provider prompt/prefix-cache reuse and reduce repeated wire input without weakening Threadsmith's host-owned context, trust, phase, tool, planning, mutation, capacity, persistence, or audit authority.

**Deliverables:**
- Exact provider-wire token accounting and normalized cache-read/write usage, hit ratio, and estimated cost/latency savings.
- Canonical grouped native tool schemas with no textual duplication for native-capable providers.
- Provider-neutral structured system/developer/user/assistant/tool messages with chronological byte-stable conversation prefixes and current input last.
- Hierarchical confined `AGENTS.md` plus prompt-append instruction bundles with parent-to-child precedence, hashing, versioning, and precise invalidation.
- Closed volatility classes, deterministic evidence blocks, deliberate cache-aware compaction generations, and append-only tool continuations.
- Optional provider cache capabilities, explicit stable-boundary breakpoints, and tightly bound opaque stateful continuations with canonical stateless recovery.
- `/context`, headless, telemetry, and golden wire fixtures showing logical content, estimated wire tokens, duplicated schema cost, stable-prefix length, and provider-reported cache usage.

**Exit criteria:**
- Identical stable sections and eligible tool inventories serialize byte-for-byte; changing only current input does not alter the prior stable prefix.
- Native-tool providers receive no duplicate textual schemas, while legacy adapters retain one deterministic compatibility renderer.
- Prior conversation is chronological and append-only, current input/tool result is last, and hidden reasoning remains excluded.
- Applicable repository instructions resolve safely and invalidate only on relevant scope/instruction/prompt/trust changes, not ordinary source edits.
- Evidence and compaction remain deterministic, provenance-preserving, bounded, and correctness-first; compaction changes cache family only at an explicit complete-turn boundary.
- Tool continuations append to the frozen request when policy state is unchanged and safely reassemble on a named generation transition.
- Capacity includes structured messages, native tools, provider framing, and output/reasoning reserve; cache counters remain unavailable rather than fabricated when a provider omits them.
- Explicit caching and stateful continuation are optional optimizations; invalidation or remote failure falls back to an equivalent canonical stateless request when replay is safe.
- Focused Context/Models/Tools/Execution/provider/persistence/TUI tests, architecture gates, Scenario U, golden wire fixtures, maintained provider checks, documentation, ADRs, manual tests, status, and DOX pass.

**Prerequisites:** Plan 51 requires plans 07–09, 18, 26–27, 31–32, 35, 46, and 49–50. Plans 52–55 are sequential within M19; each builds on the canonical wire/layout generation established by its predecessor.

**Scope decisions:**
- Caching is never a correctness, availability, trust, permission, tool-legality, or durable-state dependency.
- Provider-neutral contracts describe messages, usage, capabilities, identities, and generations; provider wire/cache DTOs remain inside adapters.
- Cache preference never outranks current truth, relevance, provenance, bounds, phase safety, or required reassembly.
- Repository instructions are untrusted subordinate content and cannot override host policy.
- Provider continuation references are opaque credentials-like data and never become authoritative Threadsmith state.
- The canonical stateless request remains reconstructible for recovery and audit.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
