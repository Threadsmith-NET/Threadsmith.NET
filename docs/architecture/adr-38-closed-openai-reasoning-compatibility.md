# ADR-38: Closed OpenAI-Compatible Reasoning Compatibility

## Status

Accepted.

## Context

OpenAI-compatible chat-completions endpoints expose reasoning controls and streamed reasoning through incompatible request and response fields. A general JSON transformer, JSON Patch, or executable adapter configured by a repository would allow untrusted configuration to override host-owned model, message, tool, schema, streaming, token, sampling, transport, or authentication fields. The pre-M16 generic domain event for streamed reasoning also allowed hidden reasoning text to reach durable event persistence.

## Decision

The compiled OpenAI-compatible provider owns a versioned closed reasoning compatibility object. It supports standard effort, explicit mapped effort, compiled chat-template shapes, compiled fixed additions, always-on reasoning, and unsupported reasoning. Mappings are bounded scalars and fixed additions are selected from compiled enums; arbitrary JSON, paths, scripts, templates, headers, endpoints, credentials, and CLR types are not accepted.

Provider validation produces a provider-neutral effective capability classified as selectable, always-on, or unsupported. Explicit compatibility modes reject unsupported levels before network I/O. Absence retains the legacy request projection, including its unsupported-level clamp.

The adapter accepts only compiled response-delta shapes and normalizes accepted text to `ModelChunk.Reasoning`. Hidden reasoning is transient display data, not conversation, memory, evidence, telemetry, hook, diagnostic, or persistence data. Migration 7 deletes historical `modelReasoningObserved` rows, and the event store rejects any attempted reasoning-event append as defense in depth.

Repository overrides may narrow ordinary model metadata but cannot change an inherited reasoning compatibility mode or schema version under the same model identity.

## Consequences

- New endpoint variants require a reviewed compiled mode or fixed shape rather than configuration-time code.
- Protected request ownership remains deterministic and auditable.
- User surfaces can distinguish selectable reasoning from intrinsic always-on behavior.
- Unknown versions and modes fail closed.
- Existing catalogs preserve their request shape until they explicitly opt into M16 compatibility.
- Historical hidden-reasoning event payloads are irreversibly removed.
