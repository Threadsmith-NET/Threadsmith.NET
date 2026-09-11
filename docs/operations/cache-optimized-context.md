# Cache-Optimized Context Operations

Threadsmith builds one canonical stateless request for every model invocation. Provider caching may reduce repeated input work, but it never changes tool eligibility, phase legality, trust, approval, capacity, evidence, or recovery behavior.

## Request order

Structured requests use this order:

1. stable host policy;
2. phase policy;
3. the applicable repository instruction bundle and any initial instruction-only corrections;
4. chronological complete recent user/assistant turns;
5. relevant explicit repository-memory IDs/text;
6. current governed state and attributable evidence;
7. the current user input;
8. append-only correlated tool calls/results during an unchanged continuation.

Hidden reasoning is never archived or reconstructed as ordinary conversation history. Anthropic's active tool continuation retains and validates its private native response blocks for protocol-required replay. A new phase, trust/policy generation, tool inventory, instruction bundle, compaction generation, repository-memory content, model, or layout requires reassembly rather than continuation reuse.

Layout version 2 places changing memories and request state after reusable history. These sections use the provider-neutral `HostContext` role: Codex emits chronological developer messages; OpenAI-compatible and Anthropic adapters emit clearly delimited user-message context blocks. Anthropic's actual system/developer instructions remain in its required top-level instruction prefix. Host policy, approval, tool authority, and memory relevance rules are unchanged. Removed memories disappear from the next assembled request.

This preserves the prefix through completed history when request-local data changes. It does not promise that the entire previous request survives a new turn: current-state blocks are rebuilt, history may be compacted, and phase/model/tool changes can deliberately invalidate earlier prefixes. Unchanged tool continuations append to the prepared request.

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

The TUI header retains cumulative per-agent token totals. Agent details (F2 from output, then F2 for full text) also show the latest observed request's stage/round, input/output tokens, cache-read and cache-write tokens, and cache-hit percentage. Each agent owns its own latest request; an unavailable report never borrows an earlier request's counters. Percentages use the provider's normalized input semantics and remain unavailable for estimated input, missing reads, or unknown semantics. Anthropic input totals already include cache reads and writes, so they are not added twice. The raw model log's `responseSummary` entries retain each request/continuation round's normalized `Usage.Cache` counters.

When available, reasoning-token counts appear as a subset of output in the header and latest-request details. Missing counts leave the old display intact, and partial cumulative reasoning is hidden. Cache read/write accounting and per-request hit percentages are independent of reasoning availability. See [provider usage fields](model-providers.md#reasoning-token-usage).

For vLLM, start the server with [`--enable-prompt-tokens-details`](https://docs.vllm.ai/en/stable/cli/serve/#--enable-prompt-tokens-details) to expose per-request counters in streamed usage. Threadsmith already sends `stream_options.include_usage=true` and reads `prompt_tokens_details.cached_tokens` and `created_cache_tokens` when supplied. The server flag controls reporting, not whether prefix caching runs. vLLM's rolling server hit-rate log is not a particular request's cache-hit percentage.

## Provider acceleration and recovery

Compiled providers currently use canonical stateless requests and automatic exact-prefix behavior, if offered by the remote service. Explicit breakpoints are emitted only when an adapter declares bounded support. Stateful continuation remains disabled unless an adapter can safely protect and bind its opaque reference.

Anthropic's configured prompt caching uses at most four explicit breakpoints: native tools, repository instructions, completed conversation history (or phase policy when history is absent), and the final request content block. A dedicated history breakpoint preserves reuse when memories/state change; the final breakpoint advances with tool continuations. Both follow Anthropic's [prefix and lookback rules](https://platform.claude.com/docs/en/build-with-claude/prompt-caching). Disabling Anthropic prompt caching emits no cache controls.

Any future opaque continuation must bind to provider/profile, request generation, instruction bundle, trust/policy, tool inventory, layout, compaction generation, and stateless request digest. A mismatch discards it. Remote rejection may retry once through canonical stateless reconstruction only when replay is safe.

Repository-memory rankings and query embeddings are reused across unchanged rounds. Usage receipts never change injected text/order. A content update/delete rebuilds the request and invalidates incompatible provider continuation so removed blocks are absent from the next submission; historical conversation records are not rewritten.
