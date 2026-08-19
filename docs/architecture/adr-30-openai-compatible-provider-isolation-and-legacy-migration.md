# ADR-30: Isolate the OpenAI-Compatible Provider and Bound Legacy Migration

**Status:** Accepted

## Context

ADR-29 introduced provider-neutral polymorphic catalogs and compiled registrations, but the concrete OpenAI-compatible HTTP/SSE adapter and transitional configuration still lived in `Threadsmith.Models`. The legacy `model:profiles[]` path also bypassed compiled registration and directly constructed that adapter. This preserved a reverse dependency pressure that future provider implementations and SDKs would amplify.

## Decision

- `Threadsmith.Models` owns only provider-neutral configuration, registry, selection, activation, and streaming contracts.
- `Threadsmith.Models.OpenAiCompatible` references `Threadsmith.Models` and owns the concrete typed configuration, registration, HTTP/SSE adapter, and private wire DTOs. The App composition root explicitly registers it.
- The provider continues using direct `HttpClient`; no SDK is added without a demonstrated correctness benefit.
- OpenAI-compatible endpoints are formed from an absolute base URI and bounded relative chat-completions path. Composition preserves the base path and rejects root, authority, traversal, query, fragment, and control-character escapes.
- Optional configured headers are bounded and request-local. Authentication, proxy authorization, cookies, API-key-like/credential-like fields, hop-by-hop fields, invalid names, controls, and excessive values are rejected.
- Resolved bearer credentials are attached to each request and never mutate `HttpClient.DefaultRequestHeaders` or durable configuration.
- App owns one client/connection pool for its lifetime. Bounded `model:http` scalar settings use normal configuration layering for pooled connection lifetime/idle timeout, connect timeout, and per-server concurrency. Shared cookies remain disabled across providers, and the global client timeout remains disabled so profile-linked cancellation stays authoritative.
- Legacy profiles are converted only in memory into deterministic compiled provider/model bindings when no dedicated catalog exists. Stable profile IDs and exact full endpoints/settings are retained; files are never written and one startup deprecation warning is emitted.
- A dedicated catalog and legacy profiles are ambiguous and fail before activation. Legacy removal has no selected milestone and requires a later announced decision.

## Consequences

Concrete protocol and future SDK dependencies can evolve without entering the neutral model assembly or public host projections. Adding a compiled provider follows one project/registration pattern rather than modifying dispatch. Existing installations remain functional through a visible, bounded migration path, while mixed schemas cannot create surprising precedence.

The provider project directly references `Threadsmith.Core` as well as `Threadsmith.Models` because repository build configuration disables transitive project-reference reliance and the existing provider-neutral exception/identifier contracts are Core-owned. Dependency direction remains one-way; Core and Models do not reference the provider project.
