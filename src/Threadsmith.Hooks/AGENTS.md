# AGENTS.md — Threadsmith.Hooks

## Purpose

Own host-controlled lifecycle hook policy, deterministic coordination, bounded transport adapters, recursion suppression, and shared management commands.

## Ownership

- `HookPolicy.cs` — normalization, immutable SHA-256 configuration digests, exact repository approval, and repository-excluding managed grants.
- `HookCoordinator.cs` — ordering, eligibility, concurrency, abandon-and-discard timeout/retry, targeted management tests, result validation, effective decision, audit, events, telemetry, and recursion fencing.
- `HookAdapters.cs` — tracked executable, bounded HTTP, and host-delegated MCP/extension adapters.
- `HookManagementApplication.cs` — list/inspect/enable/disable/test/approve/revoke/audit command handlers and in-memory test store.
- `HookEventObserver.cs` — authoritative durable source-event projection to an independently drained advisory queue, including transaction-level mutation completion correlation.

## Local Contracts

- Repository handlers are always advisory and fail-open, even after exact external approval.
- Managed blocking/fail-closed authority requires an immutable repository-excluding grant and an eligible pre-action point; denial codes are allowlisted.
- After and terminal hooks never block or roll back completed work.
- Event-backed hooks leave the single-reader domain-event subscriber before coordinator invocation, so coordinator audit publication cannot re-enter and deadlock that subscriber.
- Handler results remain closed bounded DTOs and never approve, mutate, execute, grant authority, or select host transitions.
- Executable paths are bare PATH-resolved names with JSON standard streams and no command-line secrets. HTTP defaults to HTTPS, permits only literal-loopback HTTP, and never follows redirects.
- MCP/extension delegates must preserve existing central policy, connected identity, lease, budget, timeout, and recursion-suppression ownership.
- `MutationStaged` is invoked only by the staging coordinator after the exact diff exists; preview events never invoke it. `MutationApplied` is emitted once after all per-mutation events and the transaction approval-completion event.
- Management tests invoke only the selected handler. Adapter work that ignores cancellation is abandoned at the configured timeout, and late results are observed only for fault cleanup and otherwise discarded.
- Persist only normalized bounded audit and logical secret-reference names; never store values, raw prompts, arguments, diffs, files, provider output, or logs.

## Work Guidance

- Preserve deterministic managed-priority/scope/id/version ordering.
- Recompute configuration digests after any authority-relevant declaration change.
- Propagate owning cancellation and discard late generation results.
- Do not add provider, MCP SDK, extension implementation, persistence row, process, HTTP implementation, or terminal types to Core contracts.

## Verification

- `tests\Threadsmith.Milestone13.Tests\bin\Debug\net10.0\Threadsmith.Milestone13.Tests.exe`
- `dotnet test --project tests\Threadsmith.Architecture.Tests\Threadsmith.Architecture.Tests.csproj`
- `tests\Threadsmith.Milestone3.Tests\bin\Debug\net10.0\Threadsmith.Milestone3.Tests.exe`

## Child DOX Index

No child AGENTS.md files yet.
