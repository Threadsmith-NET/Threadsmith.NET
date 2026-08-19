# ADR-43 — Host-Owned Tool Effect Metadata and Conflict Scheduling

**Status:** Accepted for planned Milestone 21

## Context

A model response can contain multiple sibling tool calls, but Threadsmith currently awaits each invocation before processing the next. Parallelizing every `ReadOnly` tool is unsafe: tools can share non-thread-safe compiler workspaces, Git stores, process pools, MCP connections, extension generations, rate-limited services, approval surfaces, and session state. Tool names and model assertions do not prove independence.

The product requires actual bounded execution overlap rather than merely asynchronous method signatures. It must also retain deterministic model continuation ordering and every existing policy, budget, cancellation, lifecycle, and safe-boundary guarantee.

## Decision

- Threadsmith owns a closed, versioned scheduling model comprising access modes, resource kinds, canonical resource claims, concurrency modes, source limits, and batch failure policy.
- Each invocation is prepared after typed argument validation and policy evaluation. The host derives invocation-specific claims from validated input and confined current state. Raw model strings are never scheduling authority.
- Built-ins declare reviewed scheduling behavior. MCP and extension declarations are untrusted capability metadata that host adapters validate and may only narrow. Missing, incompatible, or uncertain metadata defaults to serialized execution.
- The host collects a complete sibling tool-call set, generation-fences registrations, detects duplicates, builds a deterministic conflict graph, and partitions calls into stable waves.
- Calls in a conflict-free wave are started concurrently on bounded in-process tasks. Acceptance requires barrier-controlled proof that multiple tool bodies are simultaneously active; sequential `await` or `Task.Run` wrapping does not qualify.
- Shared/exclusive limiters are bounded at global, category, source, session, registration, and resource scopes and are acquired in canonical order.
- Approval-interactive, mutation-capable, workflow, executable/code, unknown, and unvalidated dynamic tools serialize in Milestone 21.
- Every invocation retains independent policy, approval, hooks, budgets, timeout, cancellation, sanitization, provenance, events, activity, and extension/MCP leases.
- The structured join normalizes every terminal sibling outcome and orders model-visible results by original tool-call ordinal regardless of completion order. No later model round begins before the full batch joins.
- Scheduler tasks, claims, locks, permits, and leases are transient and never durable session authority.

## Consequences

Independent inspections can reduce wall-clock latency without letting the model authorize concurrency or making scheduler timing part of model semantics. Some apparently read-only calls remain sequential until their complete adapter/resource behavior is explicitly proven safe. Adding a tool now requires concurrency metadata review, but conservative defaults preserve compatibility. Operational events can reflect true overlap while canonical model continuations remain deterministic.
