## Milestone 11.1 — First-class Parallel Agents and Isolated Workers  *(plan 38)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Add bounded host-owned delegation using in-process .NET child runs, parallel read-only research, non-overlapping implementation workers in managed Git worktrees, independent specialist reviews, and conflict-safe parent integration. No child agent is hosted in a separate process.

**Deliverables:**
- One-level delegation plans with explicit roles, objectives, scopes, stopping conditions, output schemas, models, budgets, tools, trust ceilings, context, dependencies, and parent provenance.
- Structured-concurrency scheduler using `Task`/`async`, bounded `Channel<T>`, `SemaphoreSlim`/existing category limiters, linked cancellation, observed joins, and attempt/generation fencing.
- Parallel read-only explorers over one immutable baseline returning validated cited findings rather than transcripts.
- Conservative path/symbol/project/shared-surface partitioning with serial fallback when non-overlap cannot be proven.
- Plan-37 implementation workers in confined managed detached worktrees, returning frozen structured change sets.
- Read-only security, test, performance, and architecture reviewers returning typed evidence-linked findings.
- Worker/parent conflict detection, selected-change conversion, parent transactional restaging, fresh exact aggregate diff approval, and mandatory aggregate build/test validation.
- Durable run trees, checkpoints/restoration, cancellation/cleanup, bounded configuration, observability/redaction, interactive/headless surfaces, and automated/manual coverage.

**Exit criteria:**
- Child agents run as in-process asynchronous .NET tasks; no agent subprocess, recursive delegation, fire-and-forget work, or unbounded queue/channel/concurrency exists.
- Parallel explorers share an immutable baseline, receive narrow governed context/tools, and contribute only schema-valid provenance-linked findings.
- Each child has independently enforced model, reasoning, budget, tool, trust, context, sensitivity, scope, and deadline constraints no broader than the parent.
- Implementation parallelism starts only for host-proven non-overlapping assignments; ambiguous/shared work falls back to serial M11 execution.
- Workers cannot touch primary/peer worktrees or exceed assignment ownership and repeat all M11 mutation, approval, validation, correction, and cancellation guardrails.
- Specialist reviewers are independent and read-only; findings are advisory evidence, not approval or self-resolution authority.
- Worker results are never automatically merged. Parent integration detects conflicts/staleness, restages selected changes transactionally, obtains a fresh exact-diff policy decision, and reruns aggregate validation.
- Parent/child provenance and structured findings/change/review/conflict/outcome records remain inspectable without transcript merging.
- Cancellation/interruption at every durable boundary leaves no orphan child work and resumes without duplicate children, effects, findings, reviews, or terminal events.
- Scenario L, deterministic scheduling/load/fault/worktree/conflict/security/persistence tests, architecture gates, docs/config/manual cases, and DOX pass.

**Prerequisites:** plans 02, 05–10, 12–18, 26–27, 30–35, and 37.

**Scope decisions:**
- Agent control flow stays in the host process and uses .NET structured concurrency. Existing tracked Git/build/test/tool processes remain infrastructure, not agent hosts.
- Delegation is one level deep and host-created; no swarms or child-created agents.
- Worktrees isolate mutable file state but are not security sandboxes.
- Parallel mutation requires non-overlapping ownership and worktrees; the primary worktree is mutated only through parent transactional integration.
- No automatic merge/rebase/cherry-pick/commit/push/conflict resolution or remote workers.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
