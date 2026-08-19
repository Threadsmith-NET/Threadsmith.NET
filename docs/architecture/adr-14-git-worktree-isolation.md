# ADR-14: Optional Git Worktree Isolation

- **Status:** Accepted
- **Date:** 2026-08-02
- **Strategy source:** §15.2, §29 (strategy decision 9), §35
- **Validated by:** Threadsmith.Milestone5.Tests

## Context

Tracked in-place operation is simple and supports strong hash checks and rollback, but longer-running or autonomous work benefits from keeping approved changes away from the primary worktree. The existing tool runtime already established direct process invocation instead of a Git library.

## Decision

Tracked in-place staging remains the M5 default. `GitWorktreeManager` provides an optional detached-worktree mode using direct `git` process invocation with argument lists, bounded captured output, caller cancellation, and process-tree termination. It never invokes a shell.

The active `WorkspaceIsolation` is a host-owned descriptor visible to callers. Worktree removal is explicit, accepts only a path created by the same manager instance, and delegates removal to `git worktree remove --force`. It is not performed implicitly by finalization or object disposal.

Temporary-copy isolation remains a declared progression mode but is not implemented in M5. Container or VM isolation remains future work.

## Consequences

- Longer-running mutation work can avoid touching the primary checkout.
- The host must capture a baseline in the active isolated path before staging mutations.
- Worktree cleanup is a deliberate side effect, not an automatic destructor action.
- Git availability and repository validity remain explicit preconditions for this optional mode.
