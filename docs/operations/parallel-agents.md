# Parallel-agent operations

Threadsmith has one host-owned delegation layer for bounded research, isolated implementation, and independent review. It composes over the serial approved-plan execution path; it does not replace mutation approval, transactions, validation, or recovery.

For a component-by-component explanation of the conversation tool, see [`delegate_agents` under the hood](../architecture/delegate-agents-tool.md).

## Safety boundary

- Child agents are in-process asynchronous .NET runs. No process hosts an agent.
- Delegation depth is exactly one; children cannot create descendants or change their assignment.
- Ordinary conversation delegation supports all six roles. Every child is read-only, including an implementer that proposes changes.
- Approved implementer preparation uses the existing mutation proposal flow. The child prepares only a proposal; the parent stages it after the authoritative join and retains exact-diff approval, transactions, validation, and corrections.
- Existing isolated-worker APIs require approved ownership and managed detached Git worktrees. Selecting `implementer` in conversation does not start a worktree worker or automatic parallel application.
- Worktrees isolate file state but are not sandboxes. Trust, prohibited paths, reparse checks, tool policy, secrets, network, process, and approval gates still apply.
- Ordinary final responses cross durable join boundaries with host-owned role, model, and status metadata. Their claims are not promoted to verified findings. Legacy structured outcomes and approved mutation packages remain separate supported contracts; hidden reasoning is not joined.
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
      "role": "testReviewer",
      "task": "Check cancellation and durable checkpoint coverage.",
      "context": "Report gaps and uncertainty; do not edit files.",
      "toolAccess": "inherit"
    }
  ]
}
```

Each request has `task`, `context`, `toolAccess`, and an optional `role`. Omitting `role` preserves `explorer` behavior. Role names are exact and case-sensitive. Unknown roles or fields, empty or oversized text, unsupported access values, and excess children fail before scheduling. The model cannot choose child models, reasoning, budgets, deadlines, trust, roots, tool IDs, approvals, or concurrency.

| Role | Instruction focus |
|---|---|
| `explorer` | Investigate relevant code, behavior, and contracts. This is the default. |
| `implementer` | Inspect implementation work and propose changes without applying them. |
| `securityReviewer` | Review security risks and possible mitigations. |
| `testReviewer` | Review test coverage and useful assertions. |
| `performanceReviewer` | Review performance behavior and possible measurements. |
| `architectureReviewer` | Review ownership, dependencies, and contracts. |

Each role is a system-prompt amendment combined with its selected model and eligible tools, not an output template. A child may return any final body, including plain text, JSON, whitespace, or an empty response. No role fields, citation GUIDs, or response-format repair are required. Reviewers cannot publish reviews, approve changes, or run tests. A proposed check or a claim that checks passed is not host verification that those checks ran.

Give each child a distinct task and include the relevant files, symbols, evidence, and constraints in `context`. Role prompts encourage focused inspection and batching independent reads when useful. Tool progress reports distinguish new source coverage from repeated or different payloads; they do not grade the answer. Rejected tool calls remain in the conversation with their error results, so the child can see what failed and decide how to proceed.

`readOnly` uses only approval-free, non-network read tools from the exact parent request. `inherit` may additionally retain eligible network-backed read tools, but both modes remove mutation, process/code-execution, approval-required, workflow, and delegation tools before rechecking every invocation through central child policy. Children retain the caller's executable allowlist so retained read-only tools can use their declared host-managed dependencies. Inheritance never grants a tool or executable authority that the parent did not have.

The tool is session-exclusive and returns only after the children join or terminate. Its result carries child responses separately from host-owned delegation and assignment IDs, role, status, model-selection details, usage, and aggregate status (`Completed`, `Partial`, `Failed`, or `Cancelled`). A present response, even empty, can complete; completion describes transport and join mechanics, not answer quality. Failed or cancelled transport is not successful completion. Successful siblings remain available when another child fails, producing `Partial`. The joined checkpoint becomes durable before responses are exposed as joined results. Keep the delegation ID for inspection.

The ordinary final body is stored in `AgentRunOutcome.Response` and is not parsed into findings, graded, or repaired. Host result-envelope limits may omit detail; they do not define a role-specific body format. Legacy structured outcomes retain their compatibility path rather than being inferred from a new response's contents.

## Inspect and cancel

Interactive commands:

```text
/agents
/agents <delegation-id>
/agents <delegation-id> cancel
/agents <delegation-id> cancel-child <assignment-id>
```

After an accepted checkpoint is durably recorded, the TUI immediately prints the stable delegation ID and the matching inspection command. Bare `/agents` shows a bounded, active-first index of delegations observed in the current interactive session; assignment IDs appear as their lifecycle events arrive. This is a convenience index, not checkpoint history. `/agents <delegation-id>` remains the authoritative inspection path and reports the latest durable phase, generation, role, child status, effective provider/profile/reasoning, selection source and fallback, bounded usage, current lifecycle reason, and next legal host action. It does not render checkpoint history or the joined result's omission details. Cancellation is cooperative and hierarchical. Cancelling a parent stops admission, cancels queued/running children, observes every task, and records the cancellation boundary. Cancelling one child affects only that child and dependencies selected by its frozen failure/dependency policy.

While a conversation or delegation is active, the TUI shows `Running — Enter to steer; Esc Esc to stop.` Pressing Enter creates one idempotent request and immediately writes `Steering request received; waiting for the current model/tool boundary.` Repeated Enter presses while it is pending do not create more events, prompts, or steering messages.

The current provider response or tool batch is allowed to finish. For `delegate_agents`, every still-running child then pauses before its next provider request (or becomes terminal), and the joined result cannot return to the parent. After all earlier output is flushed, the ordinary PrettyPrompt composer opens as `steer >`; the run remains paused, so the prompt cannot scroll away. Submit text to add lower-authority user context to the parent and eligible children, submit empty/cancel to resume unchanged, or use bare `/agents` to recover the IDs before issuing an inspection or cancellation command while paused. Children that completed before submission are not reopened and appear in the joined delivered/undelivered steering accounting.

Press unmodified Escape twice within 850 ms to cooperatively cancel the active conversation. `Ctrl+C` remains supported. Neither shortcut can suspend a provider stream or tool halfway through; cancellation latency still depends on the operation observing its token.

Headless automation uses the same command dispatcher:

- `StartDelegationCommand`
- `GetDelegationCommand`
- `CancelDelegationCommand`
- `CancelAgentAssignmentCommand`

## Deployed delegation prompts

The parent `delegate_agents` description and joined-result wording, together with role amendments, task/evidence framing, progress, and steering guidance, are loaded from the application-wide deployed [prompt catalog](prompts.md). The same immutable startup snapshot serves parent and child request paths. Edits require a process restart and may change wording or token cost, but cannot change the supported roles or a frozen assignment's authority. Ordinary final replies have no required schema or response-format correction loop. Tool argument validation, model routing, trust, paths, network, secrets, resource controls, cancellation, and join behavior remain code-owned.

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

These values limit resources; they do not grant delegation, mutation, process, network, secret, model, or trust authority. Child reservations must fit within the parent resource limits. Selected-model context and output capacity, tool-policy checks, transport and result-envelope bounds, and cancellation still apply. Approved-plan preparation failures cannot authorize staging or borrow authority from siblings.

Trusted machine/user configuration controls ordinary conversation delegation under `agents:delegation`. Existing defaults allow three children, 4,096 task characters, 8,192 context characters, 1,024 summary characters, and a five-minute deadline. An unrestricted final body does not disable active scheduler, request, tool, or result-envelope resource controls. Mutation, process, build, and test authority remains unavailable through tool policy. Every eligible parent evidence item and every resolved `AGENTS.md` and configured prompt append source is included in the child request. Repository configuration cannot replace these trusted settings, and none are model-facing fields.

Coverage feedback is advisory and does not reintroduce a cumulative exploration budget. It hashes exact sanitized child tool payloads only to recognize distinct results and retains all existing messages. It does not trim host evidence, repository instructions, prompt appends, tool output, or caller-supplied context, and it cannot narrow or expand inherited authority.

Use trusted `agents:roleModels` to select existing provider/profile/reasoning preferences by role. Application assignment pins take precedence, followed by role mappings, inherited preferences, and compatible defaults. Request compatibility can require a visible fallback or fail before model I/O. Assignments, checkpoints, and outcomes retain configured and effective selections, their source, and fallback reasons. Repository configuration cannot route these models, even by replacing a provider endpoint under the same ID. Changes require restart and have no TUI editor. See [models for delegated roles](model-providers.md#models-for-delegated-roles) for executable user JSON, startup validation, and migration from rejected `agents:roleProfiles` settings.

Role keys and field names are case-sensitive; provider IDs and reasoning names are case-insensitive. Invalid role configuration stops startup instead of falling back. Only `RoleConfiguration` selections and their fallbacks use the trusted catalog. Application pins, inherited preferences, and defaults use ordinary model routing and its normal repository and secret rules. Tool access mode does not change the model's routing authority.

## Approved implementer preparation

With configured models, normal approved implementation and correction turns select `ApprovedImplementerProposalApplication`, which uses `MutationProposalApplication` and the delegation coordinator to prepare a candidate for the accepted plan. Unlike an ordinary final response, this actual mutation protocol validates the proposal and can request corrections, but does not stage or apply it. The parent stages the prepared proposal only after the child has joined authoritatively, then uses the existing exact-diff approval, transaction, validation, and correction flow. Cancellation or a failed join prevents staging. The no-model offline flow keeps the direct mutation proposal path.

This path does not automatically partition, apply, or merge parallel worktree changes. The existing isolated-worker APIs and their integration checks remain separate. A role name, a proposed file change, or a clean review cannot authorize a repository write.

## Diagnose a delegation-tool run

- No `delegate_agents` tool: confirm the repository is at least `TrustedRead`, a solution/workspace is selected, the tool remains enabled by effective tool policy, and a configured profile meets the actual request's capability and capacity requirements. Sensitive assignments additionally require a profile that permits sensitive data.
- Request rejected before a delegation ID: inspect the exact input shape and configured child/text bounds.
- `Partial` or `Failed`: inspect the original joined tool result for omissions and `/agents <delegation-id>` for the latest child status, reason, and usage exhaustion. Retry only with a narrower, non-duplicative assignment.
- Child tool unavailable: compare `readOnly` versus `inherit`, then check parent request availability, approval, trust, path, network, phase, and sensitivity policy. Every ordinary role excludes process/code and mutation tools in both modes. A role or its context cannot widen policy.
- Cancelled run: use the durable delegation ID to confirm queued and running children reached terminal cancellation; late results from the cancelled generation are not authoritative.

## Live role evaluation

From a source checkout, the opt-in `SubagentRoleLiveTests.AllRoles_RealProvider_RecordResponsesAndEfficiency` test exercises the six roles against synthetic files through normal `ModelComposition.CreateAsync` and provider-instruction resolution. It supports configured OpenAI-compatible providers and native `openai-codex` using existing Threadsmith authentication through the normal OAuth resolver and user cache. It never changes provider configuration or reads the checkout as test evidence.

The harness uses the trusted user catalog's default profile unless `THREADSMITH_LIVE_AGENT_PROFILE` names another existing profile ID. Provider selection is derived from that catalog unless `THREADSMITH_LIVE_AGENT_PROVIDER` is set. For a native Codex profile supplied outside the user provider catalog, set the profile ID and `THREADSMITH_LIVE_AGENT_PROVIDER=openai-codex`; the explicit provider is required because the user catalog cannot derive that binding. No separate credential import is needed.

```powershell
dotnet build tests/Threadsmith.Architecture.Tests/Threadsmith.Architecture.Tests.csproj
$env:THREADSMITH_LIVE_AGENT_TESTS = '1'
$env:THREADSMITH_LIVE_AGENT_REPORT_DIRECTORY = Join-Path $env:TEMP 'threadsmith-role-reports'
$env:THREADSMITH_LIVE_AGENT_TIMEOUT_SECONDS = '180'
# For an existing native Codex profile, also set THREADSMITH_LIVE_AGENT_PROFILE
# to its profile GUID and THREADSMITH_LIVE_AGENT_PROVIDER to 'openai-codex'.
tests/Threadsmith.Architecture.Tests/bin/Debug/net10.0/Threadsmith.Architecture.Tests.exe --filter-class Threadsmith.Architecture.Tests.SubagentRoleLiveTests --show-live-output on --progress off
```

The test-only timeout is optional; omitted or zero means no extra test deadline. Provider and application settings still apply. Automated assertions check mechanics: successful completion and join, expected trusted model routing, and unchanged read-only fixture files. Network or transport failures fail the run; response format, missed concepts, and uninspected sources are not automatic content failures. Reports save final responses, synthetic tool evidence, effective routing, request/tool counts, repeated calls, token usage, elapsed time, and observations for manual review. They do not save hidden reasoning.

Manually evaluate saved responses for correctness, relevance, omissions, unsupported claims, and useful proposed fixes or assertions. Where a response supplies citations, inspect their support; citations are not a required format. Compare efficiency only across comparable tasks and useful answers, not merely shorter or cheaper runs. These small cases are not a guarantee that a model will review arbitrary repositories correctly. Keep live tests separate from the normal offline suite; selecting a remote provider can consume provider usage.

## Partition and integration failures

For callers of the existing worker APIs, parallel mutation falls back to serial execution when ownership is unproven or intersects another assignment by file, containing directory, symbol, project, generated output, or shared configuration surface. Worker integration fails closed for:

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

SQLite migration 4 stores delegation run-tree checkpoints and worktree-lease recovery records. Durable boundaries include acceptance, queue/start, role-specific terminal joins (`ResearchJoined`, `WorkersFrozen`, or `ReviewsJoined`), integration decision, parent staging, aggregate validation, failure, and cancellation. Checkpoints carry monotonically increasing revisions; persistence ignores a stale lower revision, including an abandoned progress write that completes after terminal state, and rejected writes emit no lifecycle event.

`Checkpoint.Assignments` preserves each role, contract marker, runner version, and configured/effective provider/profile/reasoning with selection source and fallback. The ordinary `agent-response/1` marker imposes no body shape. Outcomes retain model provenance and `Response`; an empty response is distinct from `null` in legacy structured checkpoints, which remain supported. These records support persisted inspection. The delegation coordinator has no automatic resume API for interrupted model loops. New delegated work requires a new attempt/generation and validation of the current baseline, worktrees where applicable, model/tool/trust policy, budgets, and artifacts. It does not reopen an interrupted task or provider stream, and earlier-generation results cannot become authoritative. Approved-plan execution keeps its existing [checkpoint and resume lifecycle](execution-resumption.md).

On shutdown, Threadsmith stops admission, links cancellation through active children, performs a bounded join, and records unresolved managed worktrees for recovery. Cleanup removes only worktrees owned by the current coordinator through the tracked Git adapter.
