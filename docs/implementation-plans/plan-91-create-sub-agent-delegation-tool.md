# Implementation Plan 91: Create Sub-Agent Delegation Tool

**Status:** Implementation complete. The model-callable fork/join tool, model-backed Explorer runner, tool-policy narrowing, durable inspection/cancellation, and focused coverage are implemented. The active-run input feasibility gate failed under the current PrettyPrompt/native-scrollback ownership contract, so steering and double-`Esc` remain explicitly deferred.
**Delivery track:** Maintenance — Plan 38 usable delegation surface and interactive control follow-up  
**Prerequisites:** Plan 38, Plan 39, Plan 57, Plan 88, the current tool-policy pipeline, and the current interactive conversation loop. The Plan 38 scheduler/coordinator/checkpoint contracts remain the infrastructure authority; this plan adds a direct user/model-facing entry point and does not reopen completed milestone capability contracts.  
**Strategy source:** [Shared implementation context](00-shared-context.md), especially host-owned control flow, bounded context, typed tool contracts, one-level child delegation, cancellation, auditability, and model/tool policy boundaries  
**Related contracts:** [ADR-33](../architecture/adr-33-in-process-delegation-isolated-workers.md), [ADR-34](../architecture/adr-34-governed-declarative-skills.md), [ADR-43](../architecture/adr-43-host-owned-tool-effect-conflict-scheduling.md), [parallel-agent operations](../operations/parallel-agents.md), [user guide parallel-agent section](../user-guide.md#parallel-agents-and-isolated-workers), [Threadsmith.Tools AGENTS](../../src/Threadsmith.Tools/AGENTS.md), [Threadsmith.App AGENTS](../../src/Threadsmith.App/AGENTS.md), and [portable C# guardrails](../guardrails/portable-csharp-guardrails.md)

---

## 1. Objective

Make Threadsmith sub-agents directly usable from ordinary model tool use.

The current product has real delegation infrastructure: `AgentRunScheduler`, `DelegationCoordinator`, durable delegation checkpoints, `/agents <delegation-id>` inspection/cancellation, approved-plan preflight delegation, and skill host-action proposal plumbing. Ordinary chat cannot currently say “delegate two agents” and cause real child model runs because no model-callable delegation tool is advertised. This plan adds that missing entry point.

Add a bounded `delegate_agents` tool with a simple model-facing schema. The calling model supplies each child’s task, the context it believes the child needs, and a tool-access mode. The host validates the request, creates one Plan-38 delegation, runs child agents concurrently through the existing scheduler, waits for them to join, and returns one compact result to the parent model. The main model is paused at the tool-call boundary while children run, and the TUI continues rendering activity. Because the current TUI has no active-run input owner, slash-command inspection/cancellation resumes when the composer is available; headless host commands remain callable independently, and `Ctrl+C` owns active interactive cancellation.

This plan also evaluates two interactive controls that depend on the TUI's active-run input capabilities:

1. **Steering:** while a child delegation is active, user-authored text should be injectable into still-running child context at safe continuation boundaries. Under the current PrettyPrompt/native-scrollback architecture, a permanently pinned composer is not assumed possible. The preferred design is an interrupt-to-steer flow: a lightweight active-run key watcher detects `Enter`, requests a pause at the next safe response/tool boundary, opens the ordinary PrettyPrompt composer to collect steering text, injects that text as steering, and resumes the active delegation.
2. **Double Escape cancellation:** pressing `Esc` twice in succession should terminate the current conversation loop by cooperatively cancelling the active model/tool/delegation run, without exiting Threadsmith. If raw key capture during active output cannot be implemented without breaking PrettyPrompt/native scrollback, this shortcut is gated by the same active-run input decision.

The goal is a reliable, observable fork/join delegation feature that a user can actually exercise, not only backend scheduler coverage. The delegation tool is the minimum shippable scope; interactive steering and double-`Esc` ship only if their TUI feasibility gates pass under the existing terminal ownership rules or an explicit later TUI architecture change is accepted.

---

## 2. Architectural Context

- The model proposes; the host validates, authorizes, schedules, executes, and records outcomes.
- Plan 38 already defines one in-process child layer. Children are asynchronous runs inside the Threadsmith process, not separate operating-system agent processes.
- `Threadsmith.Execution` owns delegation validation, scheduling coordination, approved-plan delegation, and run/checkpoint orchestration. It is the natural owner for a model-callable delegation workflow tool because the tool needs model dispatch, tool-policy narrowing, child-run scheduling, checkpointing, and cancellation.
- `Threadsmith.Tools` owns the generic `ITool` contract, policy pipeline, scheduling descriptors, bounded model-visible result content, and tool result sanitization. A delegation tool must use this same pipeline and must not bypass policy for child tool calls.
- `Threadsmith.App` is the composition root. It owns registration order, model/tool/context composition, active-session services, and lifetime of the scheduler/coordinator.
- `Threadsmith.Tui` owns keyboard input, the composer/input row, and interactive command routing. Plan 26 selected the composer-adjacent fallback and explicitly avoided a permanently pinned footer/composer because PrettyPrompt and native scrollback own the active prompt and transcript. Plan 91 must not assume a frozen bottom composer exists; it must first prove a safe active-run input mode or defer steering/double-`Esc` instead of weakening ADR-15/Plan-26 terminal ownership.
- Existing `/agents <delegation-id>` can inspect or cancel known delegation IDs, but it does not create delegations or list recent IDs. The new tool result must therefore return the delegation ID, and this plan may add a listing convenience if needed for observability.

No provider SDK, terminal-library, tool implementation type, raw transcript, hidden reasoning, or worktree implementation type crosses child/parent boundaries. Child outputs are schema-valid, cited, bounded findings or summaries with provenance.

---

## 3. Scope

### 3.1 Model-callable delegation tool

Add a built-in model-callable tool named `delegate_agents`.

The model-facing input is intentionally small:

```json
{
  "agents": [
    {
      "task": "Inspect the scheduler and report how children are admitted and joined.",
      "context": "Relevant paths: src/Threadsmith.Execution/DelegationOrchestration.cs. Do not edit files.",
      "toolAccess": "readOnly"
    }
  ]
}
```

Rules:

- `agents` is required and bounded by host configuration, with a conservative default such as 1-3 children.
- `task` is required bounded text.
- `context` is required bounded text. It is model-supplied context, not authority; it cannot override host, system, AGENTS, trust, approval, tool, or path policy.
- `toolAccess` is required and uses a string enum:
  - `readOnly` — the child receives only currently available read-only tools after parent/session/repository policy narrowing.
  - `inherit` — the child starts from the calling model’s currently available ordinary tool surface, then the host removes mutation, process/code-execution, approval-required, workflow, and delegation tools and reapplies child trust, network, phase, budget, and path-policy checks.
- `delegate_agents` is never inherited by children. Delegation depth remains exactly one.
- The v1 tool does not expose model, reasoning, timeout, budget, priority, output-schema, approval, trust, path-root, or concurrency knobs. Those are host-owned.

### 3.2 Fork/join execution

The tool should use a fork/join model:

1. Parent model emits one `delegate_agents` tool call.
2. Host validates and freezes the delegation plan.
3. Host starts children concurrently through `AgentRunScheduler` and `DelegationCoordinator`.
4. Parent model waits at the tool-call boundary until all children complete, fail, time out, or are cancelled.
5. The tool returns one compact result containing child outcomes and enough IDs to inspect with `/agents`.
6. Parent model resumes with the joined evidence.

The wait is asynchronous. The TUI and host process must remain responsive. Caller cancellation, double-`Esc`, `/agents cancel`, child deadline expiry, and process shutdown must all flow through existing linked cancellation paths and produce terminal child/delegation outcomes.

### 3.3 Model-backed child runner

Add a real model-backed child runner rather than reusing the current host-only `ApprovedPlanAssignmentRunner`.

Each child receives:

- immutable assignment identity, role, task, context, baseline, scope, tool-access mode, budget, deadline, trust ceiling, and sensitivity;
- host/system/developer instructions appropriate for read-only child evidence gathering;
- applicable repository `AGENTS.md` and prompt-append context selected by the same host-owned rules as the parent, narrowed to assignment scope where possible;
- no parent or sibling raw transcript;
- steering messages delivered by host sequence number after original task/context;
- only the tools admitted by its frozen tool policy.

Child result requirements:

- Explorer children return one structured `AgentFindingSet` with cited findings, omissions, uncertainty, and coverage notes.
- Unsupported or malformed child model output is corrected only within a bounded child correction budget.
- Child findings enter the parent only through the delegation join result; raw child prose, hidden reasoning, and provider payloads are not spliced into the parent transcript.

### 3.4 Tool access policy

`readOnly` mode:

- Include only tools whose effective invocation is read-only and available to the parent in the current repository/session.
- Exclude mutation, process/code execution, approval-required, delegation, and workflow-transition tools unless a later plan adds explicit child approval semantics.
- Preserve semantic workspace, path, network, secret, and trust gates.

`inherit` mode:

- Start from the exact tool IDs the calling model could use in the current request.
- Remove mutation, process/code-execution, approval-required, workflow-transition, and `delegate_agents` tools; v1 Explorer inheritance never includes command execution or host mutation authority.
- Re-evaluate every child tool call through the central `IToolInvocationPipeline` with the child’s frozen context, trust ceiling, approved roots, prohibited paths, allow/deny sets, budget, and phase.
- Do not treat inheritance as trust elevation. A child cannot obtain a tool, root, secret, network host, process, approval, model, or mutation authority the parent did not have.
- Host-workflow transition tools require special care. In v1, child-created plan/mutation/skill/delegation host actions are not allowed to transition the parent workflow automatically. They are either withheld from child tools or captured as child evidence for the parent to consider, depending on the existing synthetic-tool architecture at implementation time. This decision must be explicit in tests.

Because `delegate_agents` may internally run children that invoke different tools, its own scheduling descriptor should serialize it conservatively with sibling parent tool calls, preferably as session-exclusive or otherwise non-parallel-safe. Child tool calls then get their own ordinary scheduling and policy checks inside the delegation.

### 3.5 Compact parent-visible result

The tool returns a bounded structured result and a compact model-visible projection.

Minimum result fields:

```json
{
  "delegationId": "...",
  "status": "Completed",
  "children": [
    {
      "assignmentId": "...",
      "role": "Explorer",
      "toolAccess": "readOnly",
      "status": "Completed",
      "summary": "...",
      "findings": [
        {
          "title": "...",
          "filePath": "...",
          "symbol": "...",
          "evidence": "...",
          "confidence": "High"
        }
      ],
      "omissions": [],
      "usage": { "modelTokens": 0, "toolCalls": 0 }
    }
  ],
  "steering": {
    "submitted": 0,
    "delivered": 0,
    "undelivered": 0
  },
  "disagreements": [],
  "omissions": []
}
```

Projection rules:

- Lead with `delegationId` and child statuses.
- Group findings by child assignment.
- Include file paths, symbols, cited evidence, omissions, and disagreements.
- Bound child summaries independently from structured findings.
- Keep detailed usage, exact provider metadata, hidden reasoning, raw tool JSON, and transcripts out of the model projection.
- Preserve enough structured data for `/agents <delegation-id>` and durable checkpoint inspection.

### 3.6 Steering input

Steering lets the user add context while the parent model is waiting for child agents, but it is gated by current TUI constraints. Plan 26 documented that Threadsmith ships a composer-adjacent status row, not a permanently pinned footer or always-mounted bottom composer. Therefore Plan 91 cannot simply require a frozen input row during active output.

User experience goal:

- If a safe active-run key watcher passes feasibility, pressing `Enter` while a delegation is running requests a steering pause instead of trying to keep a composer permanently visible.
- At the next safe response/tool boundary, Threadsmith displays the ordinary PrettyPrompt composer, pauses the active conversation/delegation until steering text is submitted or dismissed, injects submitted text as steering, and then resumes.
- The UI may show a concise hint such as `Running — Enter steers at next boundary; Esc Esc cancels` only if the active-run key watcher is actually implemented.
- Slash commands such as `/agents <id>` retain command semantics rather than becoming steering text.
- If active-run input cannot be implemented under ADR-15/Plan-26 constraints, interactive steering is deferred. It must not be replaced by a hidden prompt, background blind typing, alternate-screen UI, separate side channel, or a fake normal-composer claim.

Host semantics, when steering is available:

- Steering is user-authored context with a stable sequence number, timestamp, session, run, optional delegation ID, and raw/sanitized body governed like ordinary user text.
- Steering is lower authority than system/developer/host policy, AGENTS.md, approved plans, tool schemas, trust, and path policy. It cannot approve tools, mutate files, change assignment role/mode/tools/deadline/budget/model, or create child descendants.
- Steering is delivered to still-running children at safe boundaries, such as before the next child model request or correction turn. It does not interrupt a provider stream mid-token unless the provider runner already supports safe cancellation/retry.
- A child that has already completed does not receive later steering. The parent result reports submitted, delivered, and undelivered steering counts.
- Steering must be visible in audit/provenance and recoverable enough to explain child outputs. Durable storage should follow existing conversation-body/artifact retention and sanitization rules rather than introducing a transcript leak.

Implementation requires an active-run key-capture feasibility spike before building `DelegationSteeringCoordinator`. The spike must prove that `Enter` and `Esc` can be detected while PrettyPrompt is not reading, without racing future PrettyPrompt reads, corrupting native scrollback, losing paste input, or requiring private terminal APIs. Full text entry still belongs to the ordinary PrettyPrompt composer opened only after the active run reaches a safe pause boundary.

### 3.7 Double-`Esc` conversation-loop termination

Add a keyboard shortcut: pressing `Esc` twice in succession cancels the current conversation loop.

Rules:

- The shortcut cancels the active model/tool/delegation run for the current session. It does not quit Threadsmith, close the repository, delete checkpoints, reset the conversation, or kill arbitrary processes outside the tracked tool/process cancellation path.
- Use a short host-owned time window, such as 750-1000 ms. The first `Esc` arms cancellation and displays a concise hint; the second confirms.
- Any ordinary character input, Enter submission, or timeout clears the armed state.
- When no run is active, preserve existing `Esc` behavior where possible, such as clearing/dismissing input state.
- Cancellation links into the same source used by the conversation loop, active tool invocations, model streams, `delegate_agents`, child runs, and tracked process tools. Started children and tools must receive terminal cancelled outcomes; queued children must not start after cancellation.
- The final conversation state should return to a usable prompt with aggregate `Cancelled` status, retained per-child terminal status for diagnosis, and no stale child result becoming authoritative later.

### 3.8 Inspection and observability

- The `delegate_agents` result must always include `delegationId` when validation progressed far enough to create one.
- `/agents <delegation-id>` continues to inspect details.
- Consider adding `/agents` with no ID to list recent delegations for the active session, because a missed tool result currently makes IDs hard to discover.
- TUI activity should show each child assignment start/complete/cancel/fail with bounded role/task labels and no raw child transcript.
- Headless output should include the same joined result and cancellation status without interactive steering.

---

## 4. Non-Scope

- No second delegation layer, swarms, child-created descendants, or autonomous dynamic child counts.
- No separate sub-agent console, split pane, blind background typing, alternate-screen UI, or custom steering side channel. Steering uses normal submitted console text only if active-run input passes the TUI feasibility gate; otherwise steering is deferred.
- No literal copying of parent or sibling transcripts into child context.
- No automatic approval of child process/network/secret/mutation requests.
- No direct file mutation by read-only children.
- No automatic merge, rebase, commit, push, cherry-pick, or conflict resolution.
- No child result can approve a plan, approve mutations, mark validation passed, change trust, change model selection, or alter tool availability for the parent.
- No durable storage of hidden reasoning, provider request/response bodies, raw tool payloads beyond existing governed evidence, or unsanitized secret-bearing text.
- No replacement of existing approved-plan delegation or skill proposal contracts; this tool composes with them.
- No background long-running delegation API in v1. The parent model waits for the join result, with bounded timeout/partial return.

---

## 5. Current State

- `AgentRunScheduler` runs in-process child assignments concurrently under queue/global/parent/implementer limits, observes cancellation, applies failure policies, discards stale generations, and returns terminal outcomes.
- `DelegationCoordinator` validates plans, writes durable checkpoints, starts the scheduler, records joined outcomes, and supports cancellation/inspection by known delegation ID.
- `/agents <delegation-id>` can inspect or cancel a known delegation; `/agents` without an ID currently shows usage guidance, not a list.
- `ApprovedPlanDelegatingOrchestrator` can create host-owned read-only preflight assignments for approved plans with at least two disjoint affected path scopes. Its current runner is not a model-backed Explorer; it returns a host-only preflight finding.
- Declarative skills can propose `ProposeDelegation` or `RequestReviews` host actions, but ordinary chat has no direct delegation tool and no bundled sample that reliably creates visible child agents.
- Ordinary conversation model requests do not advertise a delegation tool. Prompting the model to “use actual sub-agents” therefore correctly results in ordinary tool batching or an honest inability response.
- The TUI does not currently treat normal busy-loop input as delegation steering, and double-`Esc` does not terminate the active conversation loop as a universal cancellation shortcut.

---

## 6. Proposed Design

### 6.1 Contracts

Add small host-owned contracts for the tool request/result, ideally in the owning implementation layer unless another subsystem boundary requires Core DTOs.

Candidate input types:

```csharp
public sealed record DelegateAgentsInput
{
    public required IReadOnlyList<DelegateAgentRequest> Agents { get; init; }
}

public sealed record DelegateAgentRequest
{
    public required string Task { get; init; }

    public required string Context { get; init; }

    public DelegateAgentToolAccess ToolAccess { get; init; } = DelegateAgentToolAccess.ReadOnly;
}

public enum DelegateAgentToolAccess
{
    ReadOnly,
    Inherit,
}
```

Do not add model-facing fields for model/profile, reasoning, temperature, budget, deadline, output schema, role, trust, approval, paths, or concurrency in v1. Derive those from host policy and current context.

Candidate result types:

- `DelegateAgentsResult`
- `DelegateAgentOutcomeSummary`
- `DelegateAgentFindingSummary`
- `DelegationSteeringSummary`

Use strings and existing identifier DTOs only. Do not expose provider SDK types or implementation-specific runner state.

### 6.2 Tool definition and registration

Implement a built-in `ITool` in `Threadsmith.Execution` or another layer that can legally depend on the model runner, context assembly, tool registry/pipeline, and delegation coordinator. Register it from `Threadsmith.App` after the delegation coordinator, model dispatcher, context assembly, and tool registry are available.

Definition requirements:

- `Id = "delegate_agents"`
- read as a workflow/orchestration tool, not a repository read primitive;
- non-idempotent;
- supports cancellation;
- conversation-available only when a repository/session/model context can support child runs;
- `PreferStrictArguments = true`;
- conservative scheduling, preferably session-exclusive or otherwise serialized with sibling parent calls;
- maximum output bounded independently from child structured findings.

The tool must be withheld from child tool sets.

### 6.3 Delegation plan creation

For one invocation:

1. Validate input counts and text bounds.
2. Capture parent session, run, repository, workspace, trust, selected model, sensitivity, available tool IDs, approved roots, prohibited paths, phase, and canonical baseline identity.
3. Create one `DelegationPlan` with one Explorer assignment per requested child.
4. Freeze each assignment’s objective from `task`, initial child context from `context`, tool policy from `toolAccess`, deadline and budget from host defaults, and scope from current repository context.
5. Persist an accepted/queued checkpoint through `DelegationCoordinator`.
6. Run children with the model-backed runner and wait for join.

The model cannot choose assignment IDs, child run IDs, roles, deadlines, budgets, models, trust ceilings, or tool allowlists directly.

### 6.4 Child model execution

The child runner should factor out the minimum reusable model/tool loop needed for an Explorer run. Avoid recursively invoking the full parent `SessionApplication` in a way that would create a second parent conversation, duplicate durable messages, or leak raw transcript.

Each child loop:

1. Assembles a bounded child request from host policy, assignment task/context, applicable instructions, available child tools, and delivered steering.
2. Calls the selected provider with child run identity and workload metadata.
3. Executes child tool calls through the ordinary `IToolInvocationPipeline` using child invocation context and child budget accounting.
4. Performs bounded correction when tool arguments or output schema are invalid.
5. Stops when the child returns schema-valid findings, reaches budget/deadline, is cancelled, or exceeds correction limits.

Child context should make the expected output clear: cited findings, file paths, symbols, evidence, omissions, uncertainties, and a concise answer to the assigned task. It should also clearly state that children cannot mutate, approve, delegate, or change host policy.

### 6.5 Parent join behavior

`delegate_agents` returns after every child has a terminal outcome or after the delegation deadline produces partial/cancelled outcomes.

Return status should distinguish:

- `Completed` — all children returned valid findings/reviews.
- `Partial` — at least one child completed and at least one child failed, timed out, or was cancelled.
- `Failed` — no child produced usable evidence.
- `Cancelled` — parent/user cancellation stopped the delegation.

The parent model receives the compact projection and should answer using child results plus any already gathered evidence. It should not need to call `/agents` to use the result; `/agents` is for inspection and cancellation.

### 6.6 Active-run input feasibility gate

Before implementing steering or double-`Esc`, prove whether the current TUI can safely collect input while model/tool/delegation output is active. This gate is separate from the `delegate_agents` tool itself.

The gate must compare at least:

1. **Current PrettyPrompt/native-scrollback mode** — no pinned row; input is only collected when a prompt is active. Expected result: safest current architecture, but no during-run steering trigger.
2. **Active-run key watcher plus safe-boundary PrettyPrompt steering** — a host-owned key watcher reads only simple `Enter`/`Esc` keys while PrettyPrompt is not active. `Enter` sets a steering-pause request; the active loop opens the normal PrettyPrompt composer only at the next safe boundary, collects steering text, injects it, and resumes. This is the preferred Plan 91 steering design if it preserves paste, resize, echo, transcript selection, streaming, and command handling.
3. **Public-API active-run text input arbiter** — one host-owned input reader active while PrettyPrompt is not reading, with serialized output and no private terminal APIs. This is lower priority than the key-watcher design because full text input outside PrettyPrompt risks duplicating composer behavior.
4. **Terminal-ownership change** — full-screen/alternate-screen or pinned layout. This is out of Plan 91 v1 unless an explicit ADR/Plan-26 successor accepts the terminal ownership tradeoff.

The gate fails if the design requires blind background typing, private PrettyPrompt internals, cursor-save redrawing that corrupts transcript selection, alternate-screen behavior not accepted by ADR-15, concurrent unsynchronized console writers/readers, or a background key watcher that can consume bytes intended for the next PrettyPrompt read.

### 6.7 Steering delivery, if active-run input passes

If the feasibility gate passes, add an active-run steering service with operations equivalent to:

- register active run/delegation;
- accept user steering text for the active run;
- drain pending steering for a child before each child model request;
- mark sequence numbers delivered per assignment;
- report submitted/delivered/undelivered counts in the final result;
- close the channel on completion/cancellation.

Delivery order is original task/context first, then steering messages in submission order. Each steering block is labeled as user steering and includes a bounded timestamp/sequence. Children should receive all undelivered steering that arrived before their final request assembly. No child should receive steering after its terminal outcome.

If multiple delegations are active in one session, v1 should either prevent that with session-exclusive scheduling or require an explicit target before accepting steering. Prefer session-exclusive scheduling for v1.

### 6.8 TUI active-run normal-input steering, if feasible

If active-run key capture passes, the TUI should implement interrupt-to-steer rather than a frozen footer composer.

Requirements:

- Preserve native transcript scrollback, selection, paste, resize, and serialized output.
- Show a concise mode hint while steering is available, such as `Running — Enter steers at next boundary; Esc Esc cancels`, only if the underlying key watcher exists.
- Pressing `Enter` while a delegation is active records a steering-pause request and does not immediately start a new parent turn.
- The active parent/child loop pauses only at safe boundaries: after the current model response, before the next child model request, after the current tool batch, or before returning the delegation join result. It does not splice text into a provider stream mid-token.
- At the pause, the ordinary PrettyPrompt composer opens with a clear steering prompt. Submitted text is sent to the steering service; empty submission or an explicit cancel dismisses steering and resumes.
- Echo a compact status such as `Steering added to delegation <id>; delivered to running children at their next safe boundary.`
- If the text cannot be delivered because children already joined, show `Delegation already completed; submit as a new message if still relevant.`
- Slash commands keep command behavior. `/agents` inspection/cancellation should remain available while the parent model is waiting, including while a steering prompt is open if command parsing already supports it safely.
- If no safe active-run key capture passes, steering remains unimplemented in v1 and the docs must say so plainly.

Headless mode has no interactive steering, but future automation may expose an explicit command if needed.

### 6.9 Double-`Esc` cancellation, if active-run key capture passes

Implement double-`Esc` only if active-run key capture passes the same terminal safety gate:

1. First `Esc` while a run is active arms cancellation and displays a bounded hint.
2. Second `Esc` within the configured short window invokes the same cancellation path as an explicit run cancellation.
3. The active parent model stream, pending/active tools, `delegate_agents`, scheduler children, and tracked process tools receive linked cancellation.
4. Durable terminal events/checkpoints must honestly report cancellation or partial cancellation.
5. Late child/model/tool results from cancelled generations are discarded.

If active-run `Esc` capture cannot be made safe, retain the existing cancellation mechanisms and do not document double-`Esc` as shipped. The shortcut should be covered by deterministic input tests using a fake time provider or equivalent key-event harness when implemented.

### 6.10 Implementation result

- `delegate_agents` is a strict, workspace-required, `TrustedRead`, session-exclusive workflow tool. It accepts only bounded `agents[].task`, `agents[].context`, and `agents[].toolAccess` values and runs one to three Explorer assignments by default.
- Each invocation freezes the exact model-visible parent tool-registration snapshot. `readOnly` retains only non-network, approval-free read tools; `inherit` may additionally retain eligible network-backed read tools. Both modes remove mutation, process/code-execution, approval-required, workflow, and delegation tools while retaining the caller's executable allowlist for declared dependencies of the remaining tools, then re-run every child call through the central invocation pipeline with child identity, trust, roots, prohibited paths, phase, cancellation, and budget.
- Model-backed children receive host and repository instructions plus the complete policy-eligible assignment/evidence context, never parent or sibling transcripts. Every resolved `AGENTS.md` and configured prompt append source is included. Cumulative model and tool usage is observed but not capped; each complete provider-wire request must fit the selected model's actual context and output limits, streamed tool payloads remain independently bounded, and the child deadline remains authoritative. Output and citations are corrected within one bounded correction allowance. Validated findings cross into parent evidence only after the joined checkpoint is durable, in one complete-set-validated batch with the effective selected-model provenance. Pre-commit publication preparation failure or cancellation leaves the batch uncommitted; after the commit gate, the complete batch exists before subscriber observation and later subscriber failure neither rolls it back nor revokes the joined result.
- The joined result contains delegation and assignment IDs, honest terminal statuses, summaries/findings/omissions/uncertainty, conservative disagreements, usage totals, and zero steering counts. Finding count has no separate cap; complete findings are retained when they fit the structured tool envelope, with fair cross-child retention only when that real envelope is full. Empty ordinary Explorer findings fail, usable siblings join as `Partial`, and caller cancellation remains aggregate `Cancelled` even when one sibling already completed. The compact projection always retains every child status. Durable Accepted, Queued, Running, and role-specific terminal (`ResearchJoined`, `WorkersFrozen`, `ReviewsJoined`, `Failed`, or `Cancelled`) checkpoints make the same delegation inspectable and cancellable through `/agents <delegation-id>`. Checkpoint revisions increase monotonically; persistence rejects stale late progress writes and the coordinator emits no lifecycle events for those rejected revisions.
- The active-input gate failed. During an ordinary active run, `ConversationalShell` waits on execution, decision, and output-drain tasks and does not own a terminal reader. `PrettyPromptConsoleSurface.ReadAsync` is the only normal input owner and holds the shared console gate for the complete composer read. A second `Console.ReadKey`-style watcher would race the next PrettyPrompt read and could consume paste or command bytes; a full-time input arbiter would duplicate PrettyPrompt editing; pinned or alternate-screen control would contradict ADR-15 and the Plan-26 result. Plan 91 therefore ships no steering prompt and no double-`Esc` shortcut. Existing `Ctrl+C` and caller cancellation remain authoritative during an active interactive turn; `/agents ... cancel` is available once the composer owns input, and the equivalent headless host command remains available to an independent caller.

---

## 7. Implementation Tasks

1. **Inventory current control flow** — map ordinary conversation model dispatch, tool-call execution, active-run cancellation, TUI input submission, `/agents`, `DelegationCoordinator`, `AgentRunScheduler`, and approved-plan/skill delegation paths.
2. **Define tool contracts** — add `DelegateAgentsInput`, child request, access enum, result, child summary, finding summary, and steering summary with strict JSON schema and bounded validation.
3. **Register tool safely** — compose `delegate_agents` from App with conservative availability, strict arguments, non-idempotent workflow semantics, session-exclusive scheduling, and no child inheritance of itself.
4. **Build child tool policy narrowing** — implement `readOnly` and `inherit` modes from the parent’s effective tool set, with explicit handling for synthetic workflow-transition tools.
5. **Implement model-backed child runner** — factor a transcript-free Explorer model loop with bounded tool use, correction, citations, omissions, and terminal structured findings.
6. **Connect fork/join execution** — create a `DelegationPlan`, run through `DelegationCoordinator`/`AgentRunScheduler`, wait asynchronously for terminal outcomes, and return compact parent-visible results.
7. **Run the active-input feasibility gate** — determine whether `Enter`/`Esc` key capture can be collected during active output without violating ADR-15 or Plan 26. Record the result before implementing steering or double-`Esc`.
8. **Add steering service if feasible** — create active-run registration, steering submission, child-boundary delivery, per-assignment accounting, and closeout summaries only when a safe active-run key/pause mode exists.
9. **Route interrupt-to-steer input if feasible** — while a delegation is active, `Enter` requests a safe-boundary steering prompt; slash commands remain commands where command input is active; status output is bounded and non-confusing.
10. **Implement double-`Esc` cancellation if feasible** — detect two successive `Esc` keypresses for the active loop, cancel cooperatively, preserve existing no-run behavior, and surface honest terminal status.
11. **Improve observability** — ensure tool results include delegation IDs; consider `/agents` listing for active/recent session delegations; add activity blocks for child start/complete/cancel/fail.
12. **Persist/audit boundaries** — record child outcomes, and steering if implemented, with existing retention/sanitization patterns; do not store raw transcripts or hidden reasoning.
13. **Update docs and DOX** — update user guide, parallel-agent operations, TUI command/keybinding docs, configuration examples if new limits are exposed, and applicable `AGENTS.md` files only for durable contract changes.
14. **Add tests** — cover schema validation, read-only narrowing, inherit narrowing, recursion denial, fork/join concurrency, active-run input feasibility outcomes, steering delivery/undelivered accounting if implemented, double-`Esc` if implemented, cancellation, `/agents` inspection, policy denial, and model-output correction.

---

## 8. Testing Strategy

Automated coverage should include:

- `delegate_agents` schema accepts only `agents[].task`, `agents[].context`, and `agents[].toolAccess`.
- Oversized context/task, zero children, too many children, unknown fields, unknown access modes, and attempts to provide model/budget/trust/tool IDs fail as argument validation.
- Two `readOnly` Explorer children run concurrently in-process and return joined findings.
- `readOnly` children cannot receive mutation, process/code execution, network, secret, approval-required, or delegation tools.
- `inherit` children receive the parent’s currently available ordinary tools after stripping `delegate_agents` and rechecking child policy.
- Child tool calls run through `IToolInvocationPipeline` and emit ordinary tool events with child run/requester identity.
- Parent result is compact, includes delegation/assignment IDs, findings, omissions, usage summaries, and no raw child transcript or hidden reasoning.
- Active-run key-capture feasibility is recorded. If it fails under current TUI constraints, steering and double-`Esc` are explicitly deferred and not documented as shipped behavior.
- If steering is implemented, pressing `Enter` during an active delegation opens a normal PrettyPrompt steering prompt at the next safe boundary rather than requiring a pinned composer.
- If steering is implemented, submitted text before a child’s next request appears in that child context with sequence/order preserved.
- If steering is implemented, text submitted after a child completes is reported undelivered.
- If active-run input is implemented, slash commands during active delegation are not captured as steering.
- If double-`Esc` is implemented, it cancels the active model/tool/delegation run and returns to a usable prompt.
- Late child results after cancellation or generation change are discarded.
- `/agents <delegation-id>` can inspect the delegation created by the tool.
- Headless operation returns the joined result without interactive steering and cancels through the existing cancellation token.

Regression suites likely affected:

- `Threadsmith.ModelTooling.Tests` for tool schema/pipeline/cancellation behavior.
- `Threadsmith.ParallelAgents.Tests` for scheduler/coordinator/delegation behavior.
- `Threadsmith.CoreRuntime.Tests` for event catalog and durable event compatibility if new events are added.
- `Threadsmith.Context.Tests` if child context assembly or steering retention uses context services.
- `Threadsmith.Tui.Tests` or existing TUI input tests for steering and double-`Esc` behavior.
- `Threadsmith.Architecture.Tests` for dependency direction after introducing the tool/runner composition.

---

## 9. Acceptance Criteria

1. In ordinary interactive chat, a model can call `delegate_agents` and cause at least two real child model-backed Explorer assignments to run concurrently under `AgentRunScheduler`.
2. The parent model waits asynchronously for the delegation join and then receives a compact result containing delegation ID, assignment IDs, statuses, findings, omissions, and usage summaries.
3. `/agents <delegation-id>` can inspect the delegation created by the tool.
4. `readOnly` children receive only read-only tools and cannot mutate, execute processes/code, access secrets/network, approve actions, or delegate.
5. `inherit` children receive no tool authority beyond the calling model’s currently available tool set, and never receive `delegate_agents`.
6. All child tool calls are governed by the central tool pipeline, budgets, trust, path policy, approval policy, and cancellation.
7. Active-run key-capture feasibility is explicitly evaluated against ADR-15 and Plan 26 before steering or double-`Esc` is implemented.
8. If active-run key capture passes, pressing `Enter` during a running delegation requests a safe-boundary pause, opens the ordinary PrettyPrompt composer for steering text, injects submitted steering into still-running children, resumes the delegation, and reports final steering accounting.
9. If active-run `Esc` capture passes, pressing `Esc` twice in succession cancels the current conversation loop, including active child agents, and returns the TUI to a usable prompt with honest cancellation status.
10. If active-run input does not pass, steering and double-`Esc` are deferred and the shipped Plan 91 behavior remains the usable `delegate_agents` fork/join tool plus existing cancellation/inspection paths.
11. No raw parent transcript, sibling transcript, hidden reasoning, provider payload, or unbounded tool output crosses child/parent boundaries.
12. Interactive and headless runs remain equivalent except for any explicitly shipped interactive-only steering and key handling.

---

## 10. Security and Safety Notes

- `context` and steering text are untrusted user/model-authored content. They cannot override host policy or higher-authority instructions.
- `inherit` is not elevation. It is the parent’s current tool availability intersected with child policy and explicit recursion denial.
- Children are not sandboxes. Tool/process/network/secret safety still comes from the existing central policy and tracked infrastructure.
- A child result is evidence, not authority. It cannot approve a plan, apply a mutation, mark validation complete, or modify parent state outside the explicit delegation join.
- Child cancellation must be cooperative and observed. Fire-and-forget child tasks are prohibited.
- Durable checkpoints should retain enough provenance to explain outcomes without storing raw model transcripts or hidden reasoning.

---

## 11. Documentation Updates When Implemented

- `docs/user-guide.md` — document `delegate_agents`, `readOnly`/`inherit`, fork/join behavior, steering, double-`Esc`, and `/agents` inspection.
- `docs/operations/parallel-agents.md` — add operational examples for starting, steering, inspecting, cancelling, and diagnosing delegation-tool runs.
- TUI/keybinding docs — document double-`Esc` as current-loop cancellation.
- `README.md` — concise tool/capability entry if the public tool list changes.
- `src/Threadsmith.Tools/AGENTS.md`, `src/Threadsmith.App/AGENTS.md`, and any new nearer DOX file — update only for durable ownership or contract changes.
- Acceptance scenarios and manual test plan — add or update product-level behavior and executable verification once the feature ships.
