# Plan 38 — In-process Parallel Agents and Isolated Workers

**Milestone:** M11.1 — First-class Parallel Agents and Isolated Workers

**Prerequisites:** plans 02, 05–10, 12–18, 26–27, 30–35, and 37

**Depends on by:** plan 39 governed skills/workflows, future automated review gates, and governed Git/PR workflows

**Status:** Implemented. Maintained real-terminal, load, and interruption manual closeout remains.

## 1 Objective

Add a host-owned delegation model for bounded parallel research, isolated implementation, and independent review. Child agents are nested runs executed as asynchronous .NET tasks inside the Threadsmith host process—not separate agent processes. They receive explicit roles, assignments, models, budgets, tools, trust, context, cancellation, and immutable parent provenance.

The safe initial execution shape is:

1. parallel read-only exploration against one immutable baseline;
2. host synthesis and user-approved plan partitioning;
3. bounded implementation workers in managed Git worktrees, only for assignments proven non-overlapping;
4. parallel read-only security, test, performance, and architecture reviews of structured change/evidence artifacts;
5. parent-side conflict detection, exact-diff approval, transactional integration, and authoritative validation.

No autonomous delegation, recursive agent spawning, transcript merging, or unconstrained swarm behavior is permitted.

## Current State

M11.1 is implemented as a host-owned one-level delegation layer over Plan 37. `Threadsmith.Core` owns immutable delegation, assignment, role, scope, budget, policy, finding, review, change-set, conflict, worktree-lease, command, checkpoint, and provenance contracts. `Threadsmith.Execution` validates fixed-depth acyclic plans, dot-segment-free repository-relative scope, and dominating parent budgets; conservatively partitions mutation ownership; schedules observed in-process child tasks under global/actual-parent-run/implementer limits with linked cancellation, deadlines, returned-or-thrown failure policy, and generation fencing; and persists durable run-tree boundaries. `Threadsmith.Context`, `Threadsmith.Models`, and `Threadsmith.Tools` provide transcript-free child context, cited-finding admission, role-aware configured-model selection, authority-narrowing tool contexts with explicit deny-all representation, and atomic non-negative hierarchical usage ledgers.

`Threadsmith.Workspaces` creates/freeze/removes only host-owned detached worktrees under the managed root and rejects stale, incomplete, out-of-scope, or overlapping worker packages before parent restaging. SQLite migration 4 stores delegation run trees and recoverable worktree leases. Shared TUI/headless commands inspect and cancel delegations/children; `/agents <delegation-id>` renders bounded structured state rather than child prose. The dedicated `Threadsmith.Milestone11_1.Tests` suite covers bounded concurrency, cancellation, generation fencing, authority denial, partition fallback, budget accounting, persistence, conflict detection, and real local Git worktrees.

Worker changes remain artifacts until the existing Plan-37 parent transactional path converts/restages selected typed changes, obtains a fresh exact-diff policy decision, and performs aggregate validation. No Git merge, commit, rebase, cherry-pick, push, or automatic conflict resolution was added.

## 2 Architectural Context

`00-shared-context.md` §I already binds subagents to .NET-native in-process concurrency using `Task`/`async`/`await`, bounded `System.Threading.Channels`, `SemaphoreSlim`, linked `CancellationToken`, immutable baselines, per-category resource limits, governed context, and evidence provenance. Plans 08, 09, 16, 18, 31, and 33–35 provide tool policy, context, capabilities, persistence, model selection, and bounded continuity. Plan 10 provides detached Git-worktree infrastructure and transactional conflict checks. Plan 37 supplies the authoritative implementation/checkpoint/validation loop.

M11.1 promotes the pinned read-only design into a first-class feature and adds a separately authorized implementation-worker mode. Worktrees isolate file state; they are not security sandboxes. Model turns and orchestration stay in-process. Existing external processes remain limited to host-owned infrastructure and authorized tools such as Git, build, and test—the agent itself is never represented by or hosted in a child process.

## 3 Scope

- Host-owned delegation plans with explicit child roles, objectives, boundaries, inputs, expected structured outputs, and stopping conditions.
- In-process child runs using structured .NET concurrency and hierarchical cancellation.
- Read-only exploration agents operating concurrently against the same immutable parent baseline.
- Implementation workers operating in managed detached Git worktrees with non-overlapping approved assignments.
- Read-only security, test, performance, and architecture reviewer roles with role-specific schemas and tools.
- Per-child model selection, reasoning level, budgets, tool allowlists, trust ceilings, context policy, sensitivity constraints, and deadlines.
- Global, per-parent, per-role, per-provider/model, and per-tool-category parallelism limits.
- Structured findings/change sets/reviews rather than child transcript replay or concatenation.
- Parent/child run trees, immutable delegation provenance, checkpointing, cancellation, restoration, observability, and retention.
- Upfront assignment-overlap analysis plus integration-time path/content/baseline conflict detection.
- Parent-side selection and transactional integration of worker change sets through Plan 37.
- Shared interactive/headless surfaces for delegation proposal, approval, progress, cancellation, findings, conflicts, and integration.
- Deterministic automated/manual coverage, architecture documentation, configuration examples, and DOX.

## 4 Non-Scope

- Spawning a separate OS process, container, service, or executable to host any child agent.
- Recursive delegation or child-created agents; only the parent host orchestrator may create one fixed-depth child layer.
- Open-ended agent swarms, dynamic population growth, quorum games, or model-controlled concurrency.
- Multiple workers editing overlapping paths, symbols, projects, generated outputs, or shared dependency/configuration surfaces in parallel.
- Automatic Git merge, rebase, cherry-pick, commit, push, branch publication, or conflict resolution.
- Treating Git worktrees, .NET tasks, `AssemblyLoadContext`, or trust levels as OS security sandboxes.
- Sharing raw transcripts or hidden reasoning between parent and children.
- Holding mutable compiler/workspace objects concurrently across child runs.
- Parallel mutation of the primary worktree.
- Remote workers or cross-machine execution.

## 5 Delegation Contract

A host-owned `DelegationPlan` contains a bounded list of `AgentAssignment` records. Each assignment includes:

- stable parent run, delegation, child run, assignment, approved-plan, and plan-step IDs;
- role (`Explorer`, `Implementer`, `SecurityReviewer`, `TestReviewer`, `PerformanceReviewer`, or `ArchitectureReviewer`);
- concise objective, explicit questions/tasks, expected output schema, stopping condition, and deadline;
- immutable repository/baseline/solution identity and authorized evidence/artifact references;
- allowed repository roots and a normalized assignment scope of files, directories, projects, symbols, and shared surfaces;
- mode (`ReadOnlyBaseline`, `IsolatedWorktreeMutation`, or `ReadOnlyReview`);
- tool allowlist/denylist, trust ceiling, sensitivity, network/process permissions, and approval requirements;
- selected model profile/reasoning level plus selection rationale and provider constraints;
- child context and resource budgets;
- dependency IDs and join/failure policy.

The host creates delegation plans from an accepted parent plan or an explicit read-only research request. Model output may propose assignments, but the host validates and freezes them. Delegation requiring implementation workers is shown to the user with partitioning, models, budgets, tools, trust, worktree use, and integration policy before workers start unless an existing explicit host policy authorizes it.

Children cannot alter their role, objective, scope, model, tools, trust, budget, deadline, dependency graph, output schema, or parent plan. They cannot spawn descendants.

## 6 In-process Structured Concurrency

Add a host-owned `IAgentRunScheduler` implemented with standard .NET asynchronous primitives:

- one parent-owned linked `CancellationTokenSource` and one linked child token per assignment;
- `Task`/`async` execution tracked in a bounded parent scope;
- bounded `Channel<T>` streams for lifecycle, sanitized model chunks, tool activity, and terminal child results;
- `SemaphoreSlim`/existing limiters for parent, role, provider/model, model-call, file, semantic, process, MCP, extension, build, and test categories;
- a bounded join that awaits, cancels, or observes every child task before the parent scope disposes;
- no `Task.Run` around naturally asynchronous I/O and no unobserved fire-and-forget tasks;
- late child/model/tool results after cancellation or generation change are drained/discarded and never become authoritative.

The scheduler is not a second command dispatcher or policy layer. Every child model and tool operation uses the same host registries, adapters, budgets, sanitization, event stream, and cancellation contracts as a primary run, with stricter assignment-scoped limits.

Default limits are conservative: a small configured maximum of active children per parent and globally, with lower independent caps for implementation workers and external process/build/test operations. A queue is bounded and fair; overload rejects or waits with an observable reason rather than creating tasks without limit.

A child failure, whether returned as a terminal failed outcome or raised as an exception, follows its declared host policy: `ContinueAndReport`, `CancelDependents`, or `FailDelegation`. It never implicitly cancels unrelated siblings. Parent cancellation cancels all descendants and awaits bounded cleanup. Per-parent admission is keyed by immutable parent-run identity, so concurrent delegation IDs owned by one parent share one ceiling.

## 7 Read-only Exploration Agents

Explorers receive:

- the same immutable baseline identity and turn snapshot;
- one narrow research question and stopping condition;
- a phase-specific subset of read-only repository, semantic, history, and explicitly authorized network/MCP/extension tools;
- bounded relevant evidence, repository instructions, and task context—not the parent transcript;
- a role-specific structured findings schema.

They cannot stage/apply mutations, run builds/tests unless the assignment explicitly authorizes a non-mutating process category at sufficient trust, propose mutation approval, revise the parent plan, or delegate.

Each result is an `AgentFindingSet` containing typed findings with category, summary, evidence/artifact citations, repository-relative locations/symbol IDs, confidence, uncertainty, risk, recommendation, unresolved questions, and coverage/omission notes. The host validates citations against admitted evidence and inserts accepted findings into the parent evidence store with child/model/tool/baseline provenance.

The parent receives a deterministic bounded synthesis of validated findings grouped by question and category. Raw child prompts, transcripts, model streams, and hidden reasoning are neither concatenated into parent context nor persisted as conversation turns.

## 8 Plan Partitioning and Non-overlap

Implementation parallelism begins only after the host has an approved Plan 37 plan and a valid partition. The partitioner expands each candidate assignment using:

- normalized declared files/directories;
- semantic symbol ownership and containing documents where confidence permits;
- project references and shared build/configuration/package files;
- generated-file relationships and expected outputs;
- test projects/fixtures that workers may need to edit;
- existing mutation scope, prohibited/secret/Git-metadata paths, and repository-specific constraints.

Assignments are rejected as overlapping when they may write the same path, one path contains another assignment's directory scope, semantic ownership is ambiguous, projects share a required mutable surface, generated outputs intersect, or confidence is insufficient. Broad shared files such as solution files, central package/configuration files, lock files, shared snapshots, and repository policy files default to exclusive serial ownership.

The host presents the resulting ownership map and any serialized steps. Users may reduce or serialize assignments but cannot force unsafe parallel overlap. If a clean partition cannot be proven, M11 executes the implementation serially.

## 9 Isolated Implementation Workers

For each approved non-overlapping assignment, the host first resolves the assignment from the frozen plan and requires its role, mode, and child-run identity to match the worktree request. It then:

1. creates a managed detached Git worktree from the exact parent baseline through `GitWorktreeManager`;
2. verifies clean identity/status, containment under the managed root, trust, approved roots, prohibited paths, and absence of unsafe reparse points;
3. creates an independent child workspace/context over that worktree, while retaining parent baseline correlation;
4. invokes Plan 37 implementation orchestration constrained to the assignment's plan steps and write ownership;
5. routes every worker mutation through proposal validation, exact diff, transactional staging, applicable approval policy, commit, affected build/test validation, and bounded correction inside that worktree;
6. freezes the terminal worktree state and returns a structured `WorkerChangeSet` plus validation evidence;
7. retains or cleans the worktree according to outcome, diagnostic, cancellation, and retention policy.

Implementation workers are in-process agent runs; only Git/build/test/tool adapters may use their existing tracked external processes. A worktree path is a scoped repository root, not direct filesystem authority. Workers cannot touch the primary worktree, other worktrees, `.git` metadata, or paths outside their assignment.

A worker result contains immutable parent/child baseline IDs, assigned and actually touched paths/symbols, exact diff artifact, mutation IDs, validation results, correction history, approvals/policy provenance, remaining risks, and worktree identity/status. Uncommitted worktree changes are acceptable as an artifact; creating a Git commit is neither required nor authorized.

## 10 Review Agents

After worker change sets are frozen—or after a serial M11 change set—the host may run bounded independent reviewers concurrently against immutable diff/evidence snapshots:

- **Security:** trust boundaries, secret/data exposure, injection, path/network/process/authentication, dependency, and permission risks.
- **Test:** observable behavior, missing/obsolete tests, selection adequacy, failure evidence, edge cases, and test reliability.
- **Performance:** algorithmic/resource risks, allocations/I/O/concurrency, startup/runtime impact, and benchmark needs; no unsupported performance claims.
- **Architecture:** dependency direction, public-contract leakage, ownership/layering, state/cancellation, persistence/versioning, and DOX/ADR consistency.

Reviewer tool sets are read-only by default. Test/performance reviewers may invoke existing bounded build/test/benchmark tools only when explicitly enabled, trusted, budgeted, and non-mutating; process output remains structured evidence. Reviewers never alter worker changes, approve integration, or communicate directly with workers.

Each returns a `ReviewFindingSet` with stable finding ID, role/category, severity, confidence, affected path/range/symbol, evidence citations, consequence, recommendation, and disposition requirement. The parent deduplicates exact evidence identities and groups related findings without erasing disagreement. Blocking thresholds are host policy and user-visible; model reviewers cannot mark their own findings resolved.

## 11 Conflict Detection and Parent Integration

No worker branch/worktree is merged automatically. Before integration the parent host validates:

- worker package identity, completeness, terminal state, baseline, plan/assignment scope, diff hash, approvals, and validation evidence;
- actually touched paths/symbols are a subset of assigned ownership;
- worker-to-worker path, rename/delete, semantic, project/configuration, and generated-output conflicts;
- the primary worktree still matches the parent baseline for every affected path and repository/solution/trust fact;
- review findings and required dispositions are resolved according to host policy.

Selected worker diffs are converted to existing host-owned typed text/semantic mutation requests or rejected if lossless conversion is not possible. The parent stages the aggregate selected changes in its own transactional workspace, recomputes one exact combined diff, and presents the normal Plan 30 approval decision. Worker-local approval does not authorize parent integration.

After parent application, the host reruns dependency-aware aggregate build/test validation because isolated worker validations cannot prove the combined result. Introduced failures enter Plan 37 correction serially unless a new non-overlapping partition is explicitly approved. Conflict, stale baseline, conversion failure, review block, or aggregate validation failure never triggers an automatic Git merge/resolution; the host reports structured options to revise, serialize, exclude a worker, or restart from a fresh baseline.

## 12 Per-agent Models, Context, Tools, Trust, and Budgets

Each child uses a host-owned selection request based on role/workload, required capabilities, sensitivity, deadline, context window, cost/usage limits, and configured provider/profile constraints. Role preferences are advisory; the host records selected profile/reasoning and rationale. A child cannot select or switch its model.

Child context assembly starts from stable host/repository policy, assignment contract, immutable baseline, bounded role-relevant evidence, explicit parent decisions, and eligible tool schemas. It excludes raw parent/sibling transcripts, hidden reasoning, unrelated conversation memory, and mutable sibling outputs. Dependency findings enter only as schema-validated provenance-linked evidence at a durable join boundary.

Budgets are hierarchical reservations: parent aggregate limits dominate child allocations. Charge model tokens, tool calls, evidence, wall time, files/bytes, mutations, process/build/test work, and corrections to both child and parent ledgers without double-counting provider usage. Every charge component must be non-negative; callers cannot refund usage by submitting a negative delta. Unused reservations return to the parent; exhaustion cancels or completes the child with a structured partial outcome but never borrows silently from siblings.

Trust is a ceiling copied from authoritative parent/repository state and may be reduced per child. Tools are explicit per assignment and rechecked at every invocation. An empty child allowlist or empty parent/child intersection is represented as deny-all rather than the unrestricted empty-list sentinel. Configuration or model output cannot elevate either.

## 13 Parent/Child Provenance and Persistence

Persist a run tree rather than a merged transcript. Every child event/artifact/result records:

- parent and child run IDs, delegation/assignment ID, role, attempt/generation, and dependency IDs;
- repository, worktree, baseline, approved plan/step, and model profile identities;
- context/evidence/tool-policy versions, trust ceiling, budgets, cancellation lineage, and timestamps;
- structured output schema/version, artifact hashes, validation/review status, and terminal outcome.

The parent stores only validated bounded findings/change/review projections in governed evidence/context. Child model chunks may be projected live but are transient and sanitized; hidden reasoning is never replayed or persisted as a finding.

Checkpoint at delegation accepted, child queued/started, each child terminal, research join, worktree frozen, reviews joined, integration decision, parent staging/application, aggregate validation, and terminal delegation outcome. Restoration never resumes an in-flight model stream or .NET task. It creates a new attempt/generation from the last durable boundary after revalidating baseline, worktrees, models, tools, trust, budgets, artifacts, and policy; late prior-generation results are discarded.

## 14 Cancellation and Cleanup

Cancellation is hierarchical and cooperative:

- cancelling the parent links cancellation to all queued/running children and prevents new work;
- cancelling one child cancels its tools/model/build/test operations and declared dependents only;
- read-only children leave no repository effect;
- implementation cancellation invokes Plan 37 transaction safety, process-tree termination, frozen/discarded worktree policy, and bounded cleanup;
- the scheduler observes every terminal task and publishes exactly one child outcome;
- worktree removal uses the existing tracked Git adapter, retries only under bounded policy, and records cleanup blockers without deleting user paths or Git metadata directly.

The parent may complete with partial research/review results only when policy permits and omissions are explicit. It cannot integrate a cancelled/incomplete worker change set. Host shutdown first stops admission, cancels the run tree, awaits bounded joins/cleanup, checkpoints unresolved worktrees, and then disposes services.

## 15 Public Contracts

- `DelegationId`, `AgentAssignmentId`, `AgentRole`, `AgentRunMode`, `AgentFailurePolicy`, and immutable parent/child provenance records.
- `DelegationPlan`, normalized assignment ownership/scope, dependency graph, compatibility decision, and approval projection.
- `AgentResourceBudget`, reservation/usage/outcome, model/context/tool/trust policy snapshots.
- `AgentFinding`, `AgentFindingSet`, `WorkerChangeSet`, `ReviewFinding`, `ReviewFindingSet`, and aggregate synthesis/integration projections.
- `IAgentRunScheduler`, `IDelegationCoordinator`, `IAssignmentPartitioner`, `IWorkerWorktreeCoordinator`, and `IWorkerIntegrationCoordinator` facades.
- Commands for propose/approve/reject delegation, inspect run tree, wait/cancel child/delegation, resolve review findings, select worker results, and integrate.
- Durable lifecycle/checkpoint/restoration events and stable denial/conflict/cleanup reason codes.

No provider SDK, Roslyn/MSBuild workspace, terminal library, Git library/process, persistence row, extension/MCP implementation, `Task`, `Channel`, semaphore, cancellation-source, or worktree implementation type crosses host-owned subsystem contracts.

## 16 Project/File Changes

- `Threadsmith.Core` — delegation/assignment/role/budget/provenance/findings/change/review/conflict/checkpoint commands, events, records, and projections.
- `Threadsmith.Execution` — delegation coordinator, structured in-process scheduler, child-run lifecycle, join/synthesis, role policies, reviewer orchestration, and Plan 37 integration.
- `Threadsmith.Context` — role-specific child context policies and structured result admission without transcript merging.
- `Threadsmith.Models` — child selection requests/reservations and per-role rationale using existing provider-neutral contracts.
- `Threadsmith.Tools` — assignment-scoped tool-policy snapshots and hierarchical budget accounting; no child-specific bypass pipeline.
- `Threadsmith.Workspaces` — worktree lease/lifecycle coordination, partition/conflict inputs, frozen change-set extraction, and parent transactional restaging.
- `Threadsmith.Validation` — worker-local and mandatory aggregate validation facades plus reviewer evidence.
- `Threadsmith.Persistence` — run-tree/checkpoint/worktree/change/review records and tolerant restoration migrations.
- `Threadsmith.App` — scheduler/coordinator composition and bounded shutdown ordering.
- `Threadsmith.Tui` / `Threadsmith.Cli` — delegation review, live tree/progress, cancellation, findings, conflict/disposition, integration, and final evidence surfaces.
- Dedicated `Threadsmith.Milestone11_1.Tests`, expanded architecture/workspace/execution tests, fixtures, docs, configuration, and DOX.

## 17 Ordered Tasks

1. Amend the sub-agent/concurrency ADR and shared context to make M11.1 the binding in-process implementation plan and distinguish agent tasks from tracked Git/build/test infrastructure processes.
2. Define bounded delegation, assignment, role, ownership, budget, provenance, structured result, review, conflict, checkpoint, and command/event contracts.
3. Implement static delegation validation: fixed one-level graph, assignment/dependency limits, allowed roles/modes, explicit stopping conditions, output schemas, and no cycles/recursive delegation.
4. Implement hierarchical budget reservation and bounded fair limiters across global/parent/role/provider/model/tool/process/build/test categories.
5. Implement `IAgentRunScheduler` with linked tokens, bounded channels, observed tasks, generation fencing, bounded joins, admission shutdown, and no agent subprocesses.
6. Add role-specific model selection, context assembly, tool/trust ceilings, deadlines, and sensitivity policy over existing host services.
7. Implement parallel read-only exploration and structured finding validation/citation/admission/synthesis.
8. Implement conservative plan partitioning with path/symbol/project/shared-surface ownership, confidence gates, explicit user projection, and serial fallback.
9. Extend managed worktree lifecycle with leases, exact baseline creation, confinement, independent child workspace setup, frozen results, retention, and bounded cleanup.
10. Run assignment-constrained Plan 37 orchestration in each implementation worktree and produce immutable `WorkerChangeSet` artifacts.
11. Implement four read-only reviewer policies/schemas and optional explicitly authorized non-mutating validation tools.
12. Implement worker-to-worker and worker-to-parent conflict detection, selected-change conversion, parent restaging, exact aggregate diff approval, and mandatory aggregate validation.
13. Add run-tree persistence, checkpoint/resume, attempt/generation fencing, worktree recovery, late-result discard, and cancellation/shutdown cleanup.
14. Add shared `/agents` or equivalent interactive/headless surfaces for proposal, approval, tree/progress, findings, budgets, cancellation, review disposition, conflicts, integration, and provenance.
15. Add deterministic concurrency, scheduling, fault-injection, cancellation, overlap/conflict, worktree, review, integration, persistence, security, load, and architecture tests.
16. Update strategy amendment references, ADRs, shared context, execution/workspace/context/model/persistence docs, README/user/configuration/operations guides, milestones/index/scenarios/manual tests, and the complete affected DOX chain.

## 18 Testing

Automated coverage must verify:

- child agents are in-process asynchronous runs and no process is spawned to host an agent;
- bounded admission/fairness enforce global, actual-parent-run, role, provider/model, and category limits under load, including concurrent delegations owned by one parent;
- every task is observed; parent cancellation reaches all children; returned and thrown failures apply the declared child failure policy; child cancellation follows dependency policy; late-generation results are discarded;
- bounded channels never drop approvals, failures, terminal results, or lifecycle events and apply documented coalescing to transient activity only;
- explorers observe one immutable baseline, receive no parent transcript, use only assigned read tools/trust, and return schema-valid cited findings;
- findings enter parent evidence with child/model/tool/baseline provenance and disagreements/omissions remain visible;
- partitioning rejects path/directory/symbol/project/generated/shared-file ambiguity and falls back to serial execution when non-overlap is not provable;
- workers use distinct managed worktrees at the exact parent baseline and cannot access the primary/peer worktree or exceed assignment scope;
- every worker mutation repeats Plan 37 proposal, approval-policy, transaction, validation, correction, cancellation, and final-evidence gates;
- worker-local approval never authorizes parent integration;
- reviewer roles receive immutable bounded artifacts, use only eligible tools, return typed evidence-linked findings, and cannot mutate/approve/resolve themselves;
- integration detects overlapping paths, rename/delete interactions, semantic/shared configuration conflict, stale parent bytes, changed repository/solution/trust, incomplete results, and invalid artifacts;
- selected worker changes are losslessly converted/restaged, one exact aggregate parent diff is approved, and aggregate build/tests rerun;
- no automatic merge/rebase/cherry-pick/commit/conflict resolution occurs;
- hierarchical budget charging/reservations reject negative deltas and do not oversubscribe or double-count usage;
- deterministic interruption after every checkpoint restores one legal next action without duplicate children, model calls, mutations, approvals, validation, findings, or terminal events;
- worktree cleanup is confined, bounded, cancellation-safe, and never deletes user-owned paths;
- interactive/headless behavior, final outcomes, provenance, redaction, retention, and diagnostic bundles are equivalent and secret-safe;
- Scenario L passes with deterministic fake models/tools and real local Git worktrees;
- architecture tests prevent concurrency/Git/provider/terminal/persistence implementation types leaking through host contracts.

Load tests must demonstrate configured bounds rather than a fixed throughput target: active tasks, queued assignments, channels, model calls, file handles, worktrees, external processes, memory, and retained artifacts stay within policy during slow, failing, cancelling, and high-volume child runs.

## 19 Security and Permissions

- Delegation creates narrower child authority; it never amplifies parent trust, tools, roots, network, process, model, secret, mutation, or approval permissions.
- Agent prompts, findings, change sets, reviews, repository instructions, extensions, MCP results, and restored records are untrusted data.
- Read-only agents cannot receive mutation tools. Implementation workers receive mutation proposal capability only inside assignment-scoped Plan 37 orchestration.
- Git worktrees isolate repository state but are not security boundaries. All path/reparse/prohibited/secret/Git-metadata rules remain mandatory.
- Child model routing obeys sensitivity/provider policy. Siblings do not see private context or outputs unless the host admits a sanitized structured result at a join boundary.
- Parent integration requires a fresh exact aggregate diff and current policy decision; no child can approve, merge, or validate itself authoritatively.
- Review output is advisory evidence subject to host schema/citation checks and user/host disposition policy.
- No arbitrary threading, reflection, dynamic code, direct filesystem access, or raw Git commands are exposed to models.

## 20 Observability

Emit spans/events for delegation proposal/decision, admission/queue, child creation/start/terminal, model/tool activity, budget reservation/usage, research join/synthesis, partition decision, worktree lease/create/freeze/cleanup, worker mutation/validation, reviewer finding, conflict analysis, integration selection/stage/approval/apply, aggregate validation, checkpoint/resume, cancellation, and final outcome.

Metrics include queue/active counts by role/model/category, limiter wait, channel depth/coalescing, child duration/outcome, token/tool/process/build/test use, cancellation latency, findings by category/severity/confidence, partition rejection reasons, worktree lifetime/cleanup blockers, worker/parent conflicts, review blocks, integration results, and aggregate validation failures. Logs use stable IDs and sanitized classifications, never raw transcripts, hidden reasoning, secrets, unbounded diffs/output, or private sibling context.

The TUI/headless run tree displays parent/child roles, states, model IDs, budgets, tool activity, worktree identity, findings/change/review counts, wait reasons, cancellation, and next legal action without interleaving raw child prose into the main conversation.

## 21 Migration and Compatibility

Add ordered migrations for run-tree, delegation, assignment, budget, worktree lease, structured result, review, conflict, and checkpoint records. Existing sessions/runs remain single-root trees and restore unchanged. Existing Plan 37 runs without delegation metadata remain serial.

Append compatible phases/events without renumbering persisted values. Unknown delegation/result/checkpoint versions remain inspectable but cannot execute/integrate. Existing worktree-isolation configuration remains valid; M11.1 adds separate conservative agent/worktree limits and does not reinterpret existing options as delegation authorization.

On upgrade, no previously approved plan is automatically partitioned or delegated. Interrupted older runs require a new explicit delegation decision unless every immutable identity and policy fact needed by M11.1 is present.

## 22 Acceptance Criteria

- Threadsmith can run bounded parallel read-only explorers as in-process .NET child tasks against one immutable baseline and synthesize only validated structured findings.
- No child agent is hosted in a separate process; linked cancellation, bounded channels/limiters, observed joins, and generation fencing prevent orphaned work and late authoritative results.
- Every child has explicit role, objective, model/rationale, budget, tools, trust ceiling, context, scope, stopping condition, and parent provenance.
- An approved implementation plan is partitioned only where non-overlap is provable; ambiguous/shared work is serialized.
- Implementation workers use confined managed worktrees, stay inside assignment ownership, and execute the complete Plan 37 transactional/validation path.
- Security, test, performance, and architecture reviewers return typed evidence-linked findings without mutation or self-resolution authority.
- Worker results are never automatically merged. Parent integration detects worker/parent conflicts, restages selected changes transactionally, presents one fresh exact diff, and reruns aggregate validation.
- Cancellation and restart at every durable boundary are safe and idempotent; incomplete workers cannot integrate.
- Parent/child provenance, model/tool/budget/context/policy/worktree identity, findings, changes, reviews, conflicts, and outcomes remain inspectable without transcript merging.
- Interactive/headless parity, Scenario L, load/fault/cancellation/worktree/conflict/security/persistence tests, architecture gates, documentation, configuration examples, and DOX pass.

## 23 Risks

- **Agent swarm/resource exhaustion:** fixed one-level delegation, conservative caps, bounded queues/channels, hierarchical budgets, and host-only admission.
- **Racey shared state:** immutable read baselines, independent child contexts/workspaces, join-boundary evidence admission, and no concurrent primary-worktree mutation.
- **False non-overlap:** conservative path/symbol/project/shared-surface expansion, confidence gate, serial fallback, and integration-time conflict checks.
- **Worktrees mistaken for sandboxing:** retain all trust/tool/path policies and document that worktrees isolate state only.
- **Cancellation leaks tasks/worktrees/processes:** structured parent scope, linked tokens, observed bounded join, generation fencing, tracked infrastructure processes, leases, and cleanup-blocker records.
- **Transcript/context explosion:** structured schemas, citation validation, deterministic synthesis, bounded artifacts, and no transcript merging.
- **Reviewer authority confusion:** findings are advisory host evidence; policy/user decides disposition and aggregate validation remains authoritative.
- **Worker-local success fails when combined:** mandatory parent aggregate staging/build/tests and serial correction after integration.
- **Duplicated effects after restoration:** immutable IDs, atomic checkpoints, idempotency keys, attempts/generations, and fault injection at every boundary.

## 24 Documentation

Implementation must update/add:

- `00-shared-context.md` §I and ADRs for in-process structured concurrency, delegation authority, worktree integration, and reviewer findings;
- execution state machine, context policy, mutation/worktree, validation, model selection, persistence/restoration, security, and telemetry architecture docs;
- `/agents` interactive/headless user and operations guidance;
- `.threadsmith/config.example` plus trusted machine/user settings for bounded agent limits/model/role policy, without credentials;
- `README.md`, acceptance scenarios, manual test plan, milestones/index/status, and strategy amendment references;
- every affected owning `AGENTS.md` and Child DOX Index.

Planned behavior must not be described as currently available before M11.1 lands.

## 25 Decisions

- M11.1 is Plan 38 and precedes Plan 39 skills/workflows.
- Child agents are in-process nested runs using .NET tasks, bounded channels/limiters, and linked cancellation—not agent subprocesses.
- Git/build/test may continue using existing tracked process adapters; this does not make an agent process-based.
- Delegation depth is one; only the host parent creates children.
- Parallel read-only exploration precedes implementation partitioning.
- Parallel implementation requires host-proven non-overlap and managed detached worktrees; otherwise execution is serial.
- Worker results are structured change sets, never Git merges or transcript patches.
- Parent integration repeats conflict detection, transactional staging, exact-diff approval, and aggregate validation.
- Security/test/performance/architecture reviewers are independent read-only child roles returning typed cited findings.
- Per-child authority is narrower than the parent and all resource use charges hierarchical bounded budgets.
- Structured findings/evidence cross join boundaries; raw transcripts and hidden reasoning do not.
