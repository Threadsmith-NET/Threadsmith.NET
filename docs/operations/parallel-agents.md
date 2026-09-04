# Parallel-agent operations

Milestone 11.1 adds one host-owned delegation layer for bounded research, isolated implementation, and independent review. It composes over the serial Plan-37 execution path; it does not replace mutation approval, transactions, validation, or recovery.

For a component-by-component explanation of the conversation tool, see [`delegate_agents` under the hood](../architecture/delegate-agents-tool.md).

## Safety boundary

- Child agents are in-process asynchronous .NET runs. No process hosts an agent.
- Delegation depth is exactly one; children cannot create descendants or change their assignment.
- Ordinary conversation delegation creates Explorer children only. It cannot create implementation workers or bypass approved-plan execution.
- Explorers and reviewers are read-only. Implementers require explicit authorization, proven non-overlap, and a managed detached Git worktree.
- Worktrees isolate file state but are not sandboxes. Trust, prohibited paths, reparse checks, tool policy, secrets, network, process, and approval gates still apply.
- Structured findings, reviews, and change sets cross join boundaries. Raw transcripts and hidden reasoning do not.
- No automatic merge, rebase, cherry-pick, commit, push, or conflict resolution occurs.

## Start from ordinary conversation

Ask Threadsmith for parallel inspection in a trusted repository with a selected semantic workspace. When useful, the parent model can invoke the built-in `delegate_agents` tool with one to three children by default:

```json
{
  "agents": [
    {
      "task": "Trace scheduler admission and join behavior.",
      "context": "Inspect the current execution implementation and cite exact files.",
      "toolAccess": "readOnly"
    },
    {
      "task": "Check cancellation and durable checkpoint coverage.",
      "context": "Report gaps and uncertainty; do not edit files.",
      "toolAccess": "inherit"
    }
  ]
}
```

The input schema is exact. Unknown fields, empty or oversized text, unsupported access values, and excess children fail before scheduling. The model cannot choose child models, reasoning, budgets, deadlines, trust, roots, tool IDs, approvals, or concurrency.

Give each child one narrow, non-overlapping objective. Name the exact behavioral claims to establish and include every known relevant file, symbol, prior evidence item, constraint, and stopping condition in `context`. Children are instructed to batch independent inspections, use semantic or structural tools before broad search, switch to exact symbols and paths after discovery, and avoid `dotnet_inventory` unless project topology is material to the assignment. After each tool batch, host-authored feedback distinguishes newly attributed file/source coverage from merely different result payloads so the child can return immediately when the requested claims are supported without treating one unsuccessful retrieval as a terminal gap.

`readOnly` uses only approval-free, non-network read tools from the exact parent request. `inherit` may additionally retain eligible network-backed read tools, but both modes remove mutation, process/code-execution, approval-required, workflow, and delegation tools before rechecking every invocation through central child policy. Children retain the caller's executable allowlist so retained read-only tools can use their declared host-managed dependencies. Inheritance never grants a tool or executable authority that the parent did not have.

The tool is session-exclusive and returns only after the children join or terminate. Its compact result contains the delegation ID, each assignment ID/status, cited findings, uncertainty, omissions, conservative disagreements, bounded usage, and aggregate status (`Completed`, `Partial`, `Failed`, or `Cancelled`). Empty ordinary Explorer findings fail; usable siblings still produce `Partial`. The joined checkpoint becomes durable before validated findings enter parent evidence, so a failed join write cannot expose a result as authoritative. Keep the delegation ID for inspection.

## Inspect and cancel

Interactive commands:

```text
/agents
/agents <delegation-id>
/agents <delegation-id> cancel
/agents <delegation-id> cancel-child <assignment-id>
```

After an accepted checkpoint is durably recorded, the TUI immediately prints the stable delegation ID and the matching inspection command. Bare `/agents` shows a bounded, active-first index of delegations observed in the current interactive session; assignment IDs appear as their lifecycle events arrive. This is a convenience index, not checkpoint history. `/agents <delegation-id>` remains the authoritative inspection path and reports the latest durable phase, generation, child status and bounded usage, current lifecycle reason, and next legal host action. It does not render checkpoint history or the joined result's omission details. Cancellation is cooperative and hierarchical. Cancelling a parent stops admission, cancels queued/running children, observes every task, and records the cancellation boundary. Cancelling one child affects only that child and dependencies selected by its frozen failure/dependency policy.

While a conversation or delegation is active, the TUI shows `Running — Enter to steer; Esc Esc to stop.` Pressing Enter creates one idempotent request and immediately writes `Steering request received; waiting for the current model/tool boundary.` Repeated Enter presses while it is pending do not create more events, prompts, or steering messages.

The current provider response or tool batch is allowed to finish. For `delegate_agents`, every still-running child then pauses before its next provider request (or becomes terminal), and the joined result cannot return to the parent. After all earlier output is flushed, the ordinary PrettyPrompt composer opens as `steer >`; the run remains paused, so the prompt cannot scroll away. Submit text to add lower-authority user context to the parent and eligible children, submit empty/cancel to resume unchanged, or use bare `/agents` to recover the IDs before issuing an inspection or cancellation command while paused. Children that completed before submission are not reopened and appear in the joined delivered/undelivered steering accounting.

Press unmodified Escape twice within 850 ms to cooperatively cancel the active conversation. `Ctrl+C` remains supported. Neither shortcut can suspend a provider stream or tool halfway through; cancellation latency still depends on the operation observing its token.

Headless automation uses the same command dispatcher:

- `StartDelegationCommand`
- `GetDelegationCommand`
- `CancelDelegationCommand`
- `CancelAgentAssignmentCommand`

## Deployed delegation prompts

The parent `delegate_agents` description and joined-result wording, together with delegated-child host/output policy, task/evidence framing, correction, progress, and steering guidance, are loaded from the application-wide deployed [prompt catalog](prompts.md). The same immutable startup snapshot serves parent and child request paths. Edits require a process restart and may change wording or token cost, but they cannot change child count, depth, role, model selection, tool snapshots, trust, paths, network, secrets, budgets, deadlines, cancellation, finding admission, parent evidence admission, or join behavior. Those decisions and the `delegate_agents` and `agent-findings/1` schemas remain compiled host contracts.

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

These values limit resources; they do not grant delegation, mutation, process, network, secret, model, or trust authority. Child reservations must fit within the parent resource limits. `delegate_agents` records cumulative resource usage as telemetry but does not impose cumulative quotas; each request is instead constrained by the selected model's actual context and output limits, per-response tool-payload bounds, and the child deadline. Resource-limited approved-plan children complete with a structured partial/failure outcome and cannot borrow silently from siblings.

Trusted machine/user configuration may adjust ordinary conversation delegation under `agents:delegation`, but validation keeps every setting inside compiled ceilings. Defaults allow three children, 4,096 task characters, 8,192 context characters, 1,024 summary characters, and a five-minute deadline. Compiled ceilings allow at most eight children, 4,096 task characters, 8,192 context characters, 4,096 summary characters, and a 30-minute deadline. Model-callable Explorer corrections and inspection continue until valid output, cancellation, or the deadline; mutation, process, build, and test authority remains unavailable through tool policy rather than zero-valued quotas. Finding count is not capped independently: the complete result is retained when it fits the tool's structured-output envelope, otherwise findings are fairly retained across children until that real envelope is full. Every eligible parent evidence item and every resolved `AGENTS.md` and configured prompt append source is included in the child request. Repository configuration cannot replace these trusted settings, and none are model-facing fields.

Coverage feedback is advisory and does not reintroduce a cumulative exploration budget. It hashes exact sanitized child tool payloads only to recognize distinct results and retains all existing messages. It does not trim host evidence, repository instructions, prompt appends, tool output, or caller-supplied context, and it cannot narrow or expand inherited authority.

## Diagnose a delegation-tool run

- No `delegate_agents` tool: confirm the repository is at least `TrustedRead`, a solution/workspace is selected, the tool remains enabled by effective tool policy, and at least one configured profile supports streaming, tool calls, and structured output. Sensitive assignments additionally require a profile that permits sensitive data.
- Request rejected before a delegation ID: inspect the exact input shape and configured child/text bounds.
- `Partial` or `Failed`: inspect the original joined tool result for omissions and `/agents <delegation-id>` for the latest child status, reason, and usage exhaustion. Retry only with a narrower, non-duplicative assignment.
- Child tool unavailable: compare `readOnly` versus `inherit`, then check parent request availability, approval, trust, path, network, phase, and sensitivity policy. Process/code and mutation tools are intentionally unavailable to Explorer children in both modes. Do not widen policy through child context.
- Cancelled run: use the durable delegation ID to confirm queued and running children reached terminal cancellation; late results from the cancelled generation are not authoritative.

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

SQLite migration 4 stores delegation run-tree checkpoints and worktree-lease recovery records. Durable boundaries include acceptance, queue/start, role-specific terminal joins (`ResearchJoined`, `WorkersFrozen`, or `ReviewsJoined`), integration decision, parent staging, aggregate validation, failure, and cancellation. Checkpoints carry monotonically increasing revisions; persistence ignores a stale lower revision, including an abandoned progress write that completes after terminal state, and rejected writes emit no lifecycle event. Restoration creates a new attempt/generation after revalidating baseline, worktrees, model/tool/trust policy, budgets, and artifacts. It never resumes an in-flight task or model stream; results from an earlier generation are discarded.

On shutdown, Threadsmith stops admission, links cancellation through active children, performs a bounded join, and records unresolved managed worktrees for recovery. Cleanup removes only worktrees owned by the current coordinator through the tracked Git adapter.
