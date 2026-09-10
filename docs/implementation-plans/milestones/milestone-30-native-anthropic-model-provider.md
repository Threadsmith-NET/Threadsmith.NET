# Milestone 30 — Native Anthropic Model Provider

Current lifecycle status is owned by the [milestone index](../milestones.md).

## Objective

Provide direct Anthropic API access through an isolated official C# SDK adapter, with API-key model discovery and the same host-governed execution and selection boundaries used by existing model providers.

## Deliverables

- Bounded discovery and user-owned metadata caching, deterministic model identities, explicit eligibility and refresh/status behavior.
- Native streaming conversation and host-executed tool calls, including validated structured planning/mutation tools and multiple-call continuations.
- Supported adaptive/manual thinking with user-controlled summary inclusion/display through existing interactive controls and an equivalent headless setting, independent reasoning-level selection, and bounded private in-memory protocol replay with exact call correlation.
- Cache-aware request preparation, conservative capacity/cost admission, truthful usage, cancellation, retries and sanitized failures.
- Shared interactive/headless model integration, trusted role/summary routing, dependency isolation and maintained operator verification.

## Capability prerequisites

- M7.3: compiled model-provider catalogs and provider isolation.
- M18: native-provider composition, model selection and output-reserve semantics.
- M19: canonical context, wire accounting and cache/continuation boundaries.
- M21: bounded host-owned parallel tool execution.
- M22.2: extensible secret-resolution authority.

## Exit criteria

- A configured API key discovers eligible models and permits explicit selection without changing another provider's default or credentials.
- Requests preserve host instructions, canonical tools, chronological calls/results, sensitive-data admission and mutation/delegation authority.
- Users choose whether future responses include summary text: summarized when on, omitted when off. Preference changes apply at request boundaries without restarting an active stream or changing reasoning effort.
- Thinking-enabled tool turns continue correctly with summary inclusion enabled or omitted, without exposing or persisting signatures/private replay data, including after cancellation and at valid history-rewrite boundaries.
- Models with selectable reasoning effort retain that control even when reasoning cannot be disabled; visibility remains independent of effort.
- Cache controls preserve semantic requests; counters and cost/capacity admission include all required input and hidden output usage.
- All supported host model loops use the same preparation/replay contracts; trusted auxiliary routes remain repository-excluding.
- SDK configuration, HTTP lifetime, retries, failures and resource bounds cannot bypass host policy or damage shared transport.
- Deterministic integration/architecture/privacy evidence and separately identified opt-in live verification support the shipped documentation.

## Scope decisions

Direct Anthropic API-key access only. Cloud gateways, OAuth/subscription credentials, SDK-owned agent/tool loops, multimodal inputs, hosted tools, durable private thinking storage and a new thinking UI are excluded. Reuse existing thinking-summary rendering and interactive controls with equivalent headless inclusion control. Shared host additions carry the optional inclusion preference and opaque per-run state and govern their lifecycle; Anthropic protocol semantics remain inside the adapter. Existing providers do not acquire new thinking-persistence requirements. Existing host model selection, visible conversation persistence and approval semantics remain authoritative.

[Dependency DAG](dependency-dag.md)
