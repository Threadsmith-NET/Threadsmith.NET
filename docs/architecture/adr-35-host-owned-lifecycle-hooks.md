# ADR-35 — Host-owned lifecycle hooks and managed blocking policy

**Status:** Accepted

## Context

Domain events are durable observations, not callback or authorization APIs. External automation needs stable lifecycle notifications without gaining control over prompts, tools, plans, mutations, validation, extensions, MCP, or run state. Repository declarations and handler output are untrusted.

## Decision

Threadsmith uses a closed, versioned `HookPoint` catalog and host-owned envelopes/results. The coordinator resolves an immutable declaration snapshot, checks exact external repository approval and repository-excluding managed grants, invokes bounded adapters, validates the closed result union, persists audit, and returns only `Continue`, `Block`, or `Cancelled` to the owning host boundary.

Handlers are advisory and fail-open by default. Repository handlers are always advisory/fail-open. Effective blocking or fail-closed behavior requires an immutable handler identity, eligible pre-action point, allowed denial code, and explicit organization/machine/user managed grant outside repository control. After and terminal points never block or roll back completed work.

Executable handlers use tracked processes and JSON standard streams. HTTP handlers use validated HTTPS (literal loopback HTTP only), bounded non-redirecting JSON transport, and final-boundary logical-secret resolution. MCP and extension handlers adapt already-authorized capabilities through host-provided invokers; connection, lease, and tool policy remain owned by their existing subsystems.

Exact repository approvals and bounded audit rows are stored outside repository control by persistence migration 6. Configuration digest changes invalidate approval. Cancellation, timeouts, concurrency, retries, payload/result bytes, recursion depth, data scope, secret names, and aggregate ordering remain host enforced.

## Consequences

- Hooks cannot approve actions, rewrite host-owned data, or request executable effects.
- Managed policy outages can block only explicitly granted pre-actions.
- Process and extension bounds are resource controls, not security sandboxes.
- External handlers receive only explicitly granted data and can still disclose it according to their own operating environment.
- Unknown schemas and malformed or excessive output fail deterministically and remain auditable.
