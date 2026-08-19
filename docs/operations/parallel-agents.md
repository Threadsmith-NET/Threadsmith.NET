# Parallel-agent operations

Milestone 11.1 adds one host-owned delegation layer for bounded research, isolated implementation, and independent review. It composes over the serial Plan-37 execution path; it does not replace mutation approval, transactions, validation, or recovery.

## Safety boundary

- Child agents are in-process asynchronous .NET runs. No process hosts an agent.
- Delegation depth is exactly one; children cannot create descendants or change their assignment.
- Explorers and reviewers are read-only. Implementers require explicit authorization, proven non-overlap, and a managed detached Git worktree.
- Worktrees isolate file state but are not sandboxes. Trust, prohibited paths, reparse checks, tool policy, secrets, network, process, and approval gates still apply.
- Structured findings, reviews, and change sets cross join boundaries. Raw transcripts and hidden reasoning do not.
- No automatic merge, rebase, cherry-pick, commit, push, or conflict resolution occurs.

## Inspect and cancel

Interactive commands:

```text
/agents <delegation-id>
/agents <delegation-id> cancel
/agents <delegation-id> cancel-child <assignment-id>
```

Inspection reports the durable phase, generation, child status and bounded usage, terminal reason, and next legal host action. Cancellation is cooperative and hierarchical. Cancelling a parent stops admission, cancels queued/running children, observes every task, and records the cancellation boundary. Cancelling one child affects only that child and dependencies selected by its frozen failure/dependency policy.

Headless automation uses the same command dispatcher:

- `StartDelegationCommand`
- `GetDelegationCommand`
- `CancelDelegationCommand`
- `CancelAgentAssignmentCommand`

## Configuration

The repository example documents these conservative defaults:

```json
{
  "agents": {
    "queueCapacity": 32,
    "maxActiveGlobal": 4,
    "maxActivePerParent": 3,
    "maxActiveImplementers": 2,
    "shutdownTimeoutSeconds": 30
  }
}
```

These values limit resources; they do not grant delegation, mutation, process, network, secret, model, or trust authority. Child reservations must fit within the parent budget. Exhausted children complete with a structured partial/failure outcome and cannot borrow silently from siblings.

## Partition and integration failures

Parallel mutation falls back to serial execution when ownership is unproven or intersects another assignment by file, containing directory, symbol, project, generated output, or shared configuration surface. Integration fails closed for:

- incomplete or mismatched worker packages;
- obsolete attempt/generation results;
- stale parent baseline identity;
- touched paths outside assignment ownership;
- worker-to-worker path overlap;
- missing or invalid diff/validation evidence;
- unresolved required review findings;
- changed repository, solution, trust, or policy facts.

Resolve by revising or serializing assignments, excluding a worker, or restarting from a fresh baseline. Never resolve by manually merging a worker worktree into the primary repository behind Threadsmith's transactional boundary.

## Persistence and recovery

SQLite migration 4 stores delegation run-tree checkpoints and worktree-lease recovery records. Durable boundaries include acceptance, queue/start, joined findings, frozen workers, joined reviews, integration decision, parent staging, aggregate validation, and terminal outcome. Restoration creates a new attempt/generation after revalidating baseline, worktrees, model/tool/trust policy, budgets, and artifacts. It never resumes an in-flight task or model stream; results from an earlier generation are discarded.

On shutdown, Threadsmith stops admission, links cancellation through active children, performs a bounded join, and records unresolved managed worktrees for recovery. Cleanup removes only worktrees owned by the current coordinator through the tracked Git adapter.
