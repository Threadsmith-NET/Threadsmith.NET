# Cache-Optimized Context Operations

Threadsmith builds one canonical stateless request for every model invocation. Provider caching may reduce repeated input work, but it never changes tool eligibility, phase legality, trust, approval, capacity, evidence, or recovery behavior.

## Request order

Structured requests use this order:

1. stable host policy;
2. the applicable repository instruction bundle;
3. phase policy;
4. bounded structured memory;
5. chronological complete recent user/assistant turns;
6. current governed state and attributable evidence;
7. the current user input;
8. append-only correlated tool calls/results during an unchanged continuation.

Hidden reasoning is never replayed. A new phase, trust/policy generation, tool inventory, instruction bundle, compaction generation, model, or layout requires reassembly rather than continuation reuse.

## Repository instructions

At every turn boundary, Threadsmith resolves `AGENTS.md` files from the canonical repository root toward the active working scope. Parent files precede child files. Configured prompt-append files follow under distinct provenance. Sources must:

- remain inside the repository;
- avoid prohibited paths, symbolic links, junctions, and reparse traversal;
- decode as strict UTF-8;
- satisfy file, total-size, count, and depth bounds;
- remain unchanged during their confined snapshot read.

The complete ordered bundle is content-addressed. Watcher notifications may invalidate eagerly, but the mandatory turn-boundary fingerprint comparison is the correctness authority. Repository instructions are untrusted and subordinate to host policy.

## Tool schemas

Eligible tools are grouped and ordered deterministically. JSON schemas preserve supported model-visible semantics, including explicit `null` defaults. Native-tool providers receive the inventory only through their native protocol. Legacy adapters may receive one deterministic textual fallback. Invalid, duplicate, or unsupported schema shapes fail before network dispatch rather than being silently rewritten.

## `/context inspect`

The interactive command reports:

- logical unique tokens;
- estimated provider-wire input tokens and the effective input budget;
- stable-prefix tokens;
- native versus textual tool transport;
- conversation mode and pressure;
- included/omitted memory and evidence plus reductions.

The inspection projection also carries the layout version, cache family, stable-prefix digest, canonical tool-inventory digest, instruction-bundle digest, native/textual tool token costs, and framing estimate. Headless output uses the same host-owned projection.

Provider cache counters are reported only when supplied by the provider. Missing counters remain unavailable; Threadsmith does not infer a cache hit from latency or invent zero-valued reads/writes.

## Provider acceleration and recovery

Compiled providers currently use canonical stateless requests and automatic exact-prefix behavior, if offered by the remote service. Explicit breakpoints are emitted only when an adapter declares bounded support. Stateful continuation remains disabled unless an adapter can safely protect and bind its opaque reference.

Any future opaque continuation must bind to provider/profile, request generation, instruction bundle, trust/policy, tool inventory, layout, compaction generation, and stateless request digest. A mismatch discards it. Remote rejection may retry once through canonical stateless reconstruction only when replay is safe.
