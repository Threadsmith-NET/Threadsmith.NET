# ADR-60: Native Anthropic provider and transient protocol replay

Status: Accepted

## Context

Native Messages requests require ordered assistant content and original tool identities when continuing signed thinking responses. Adapters are created per request, while the host owns tool execution, history replacement, cancellation, and persistence. Provider metadata must also be discovered before an immutable catalog can validate selected defaults.

## Decision

Isolate the official pinned Anthropic SDK in `Threadsmith.Models.Anthropic`. Use explicit API-key configuration and a compiled direct-API authority. The SDK borrows the host transport through an owned forwarding client; the host owns retries, deadlines, bounds, and all tool execution.

Load typed trusted descriptors before bounded discovery and final materialization. Retain independent ordinary and repository-excluding catalogs. Detached discovery metadata may be cached atomically under the user directory; credentials and SDK objects may not be cached. Discovery never selects a new default implicitly.

Add optional provider-neutral preparation before admission and optional transient response envelopes. Each active model loop owns its replay state and binds local tool-call IDs by response ordinal. Only the provider adapter interprets the detached payload. Envelopes are committed after complete validated responses, bounded by bytes and retained reported output tokens, and bound to request generations and provider identity. Required active tool exchanges survive unchanged until a complete boundary; rewriting or switching cannot silently reconstruct signed content or repeat tool effects.

Private replay has a narrower disclosure policy than ordinary diagnostic requests: JSON serialization, formatting, events, hooks, checkpoints, memory, and durable history never include its payload. Disposal clears retained bytes. Restart uses completed visible history at a fresh turn boundary; it does not recover interrupted signed exchanges.

Thinking-summary inclusion is a host session preference captured per request, separate from reasoning effort and disable support. Existing transient reasoning presentation handles summaries; signed protocol data never supplies presentation text. Provider-specific cache controls change neither canonical authority nor input admission.

## Consequences

Existing providers retain neutral defaults and their stream facade. The host gains small preparation and lifetime seams without adopting an SDK tool runner. Fixture and integration tests must cover all model loops, exact ordinal replay, privacy, hostile ambient SDK configuration, discovery authority, capacity, and cache accounting. Live compatibility and cache-hit evidence remain separate from deterministic fixtures.
