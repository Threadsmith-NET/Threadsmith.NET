# ADR-33 — In-process delegation and isolated implementation workers

**Status:** Accepted

## Context

Plan 37 provides one authoritative serial approved-plan execution path. Parallel research can reduce discovery latency, and independent implementation/review can improve throughput and review quality, but uncontrolled agents would amplify trust, race shared state, merge transcripts, oversubscribe providers/tools/processes, and bypass exact-diff integration.

## Decision

Threadsmith implements one host-created child layer. A child agent is an in-process asynchronous run governed by immutable `DelegationPlan` and `AgentAssignment` contracts. The scheduler uses linked cancellation, observed tasks, bounded global/parent/implementer admission, deadlines, hierarchical reservations, declared dependency/failure policy, and attempt/generation fencing. No operating-system process hosts an agent.

Explorers and security/test/performance/architecture reviewers are read-only and return cited structured findings. They receive bounded governed evidence rather than parent/sibling transcripts. Implementation workers are separately authorized only for approved Plan-37 steps whose path/symbol/project/shared-surface ownership is proven non-overlapping. Each worker uses a host-owned detached Git worktree and the ordinary Plan-37 mutation/approval/validation path.

Worker packages never merge themselves. The parent rejects stale, incomplete, overlapping, or out-of-scope packages, converts selected changes to host-owned typed mutations, transactionally restages one aggregate diff, obtains a fresh current-policy decision, and reruns aggregate validation. Git worktrees isolate file state; they are not security boundaries. Threadsmith does not automatically merge, rebase, cherry-pick, commit, push, or resolve conflicts.

SQLite migration 4 persists versioned delegation run trees and worktree leases. Restoration starts a new attempt/generation from a validated durable boundary and discards late prior-generation results.

## Consequences

- Child authority can only be narrower than parent authority.
- Parallel mutation is an optimization; uncertain ownership deterministically falls back to serial execution.
- Structured findings/change sets/reviews preserve provenance without transcript growth or hidden-reasoning replay.
- Existing Git/build/test/tool processes remain tracked infrastructure and do not violate the in-process agent decision.
- TUI and headless adapters inspect and cancel through the same command dispatcher.
- Plan 39 skills may request delegation but cannot create or schedule children directly.
