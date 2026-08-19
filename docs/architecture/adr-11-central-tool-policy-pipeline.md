# ADR-11: Central Tool Policy and Invocation Pipeline

- **Status:** Accepted
- **Date:** 2026-08-01
- **Strategy source:** §12, §22.4, §24.4, §29 (decision 20)
- **Validated by:** `Threadsmith.Milestone3.Tests`

## Context

Model tool calls are untrusted proposals. File paths, commands, network destinations, and secret references must not bypass repository trust, user approval, output bounds, cancellation, or durable auditing. Built-in and future extension tools need one invocation contract.

## Decision

Every tool is registered under a stable identifier with typed input/output schemas, trust and approval requirements, side-effect classification, timeout, output bound, and cancellation contract. Every invocation passes through one host-owned pipeline in this order: record start, deserialize and validate, evaluate policy, request approval, accrue budget, execute with linked timeout/cancellation, normalize and sanitize output, enforce the serialized bound, and record completion.

Policy confines paths to the repository and approved roots using host filesystem case semantics, rejects prohibited patterns and reparse-point traversal, applies repository tool allow/deny lists, and allowlists executable basenames, network hosts, and logical `secrets:` references. Executable inputs must be bare names and are resolved from absolute host `PATH` entries. Models cannot approve their own requests. Approval grants and denials are durable events.

Child processes use tokenized arguments without shell interpolation, a filtered environment, independent bounded stdout/stderr capture, active-process tracking, timeouts, and process-tree termination. Direct process launch outside the process manager is not a supported tool path.

## Consequences

- Invalid arguments and policy denials cannot enter tool implementation code.
- Results are host-owned, attributable, bounded, sanitized, and persisted through domain events.
- Read-only and future mutating or extension tools reuse the same policy boundary.
- `AssemblyLoadContext`, Roslyn, provider, process, and UI implementation types remain outside durable tool results.
- A denied or cancelled request remains observable without retaining raw model arguments or secrets.
