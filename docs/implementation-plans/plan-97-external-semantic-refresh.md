# Implementation Plan 97: External Semantic Refresh and Request Admission

**Status:** Implemented.
**Delivery track:** Maintenance — live semantic-workspace freshness, background refresh, and explicit recovery
**Prerequisites:** Completed Plans 05–06 repository/solution and Roslyn lifecycle, completed Plan 29 repository initialization, completed Plans 77 and 81 semantic activity/source-drift presentation, completed Plan 96 serialized PrettyPrompt console ownership, the current `SemanticEngineRegistry`, `SemanticLifecycleObserver`, generation-fenced semantic query services, and application submission command boundary
**Strategy source:** [Shared implementation context](00-shared-context.md), especially host-owned control flow, UI-as-projection, Roslyn/MSBuild as semantic sources of truth, cancellation propagation, immutable read snapshots, controlled-boundary invalidation, and structured observability
**Related contracts:** [planning governance](planning-governance.md), [Plan 06](plan-06-roslyn-msbuild-semantic-discovery.md), [Plan 29](plan-29-solution-memory-repo-initialization.md), [Plan 77](plan-77-shared-codex-style-tui-lifecycle-blocks.md), [Plan 81](plan-81-roslyn-code-explore-exact-anchors-and-source.md), [Plan 96](plan-96-active-run-steering-and-double-escape.md), [semantic confidence](../architecture/semantic-confidence.md), [event catalog](../architecture/event-catalog.md), [root AGENTS](../../AGENTS.md), [source-tree AGENTS](../../src/AGENTS.md), [Threadsmith.DotNet AGENTS](../../src/Threadsmith.DotNet/AGENTS.md), [Threadsmith.Tui AGENTS](../../src/Threadsmith.Tui/AGENTS.md), [Threadsmith.Workspaces AGENTS](../../src/Threadsmith.Workspaces/AGENTS.md), and [portable C# guardrails](../guardrails/portable-csharp-guardrails.md)

---

## 1 Objective

Keep Threadsmith's shared Roslyn semantic state current when repository files change outside Threadsmith, without reloading the complete solution for every ordinary source save and without allowing a newly submitted model request to start against semantic state already known to be stale.

Implement one workspace-scoped, single-flight semantic-refresh authority used by all three triggers:

1. settled external filesystem changes while Threadsmith is otherwise idle;
2. request admission when the user submits work while semantic changes are pending or a refresh is active; and
3. the explicit `/semantic_refresh` recovery command.

After a burst of external changes settles, Threadsmith refreshes in the background and writes `External changes detected; updating semantic model...` through the serialized console surface. If the user submits during settling or refresh, the request waits for that same refresh. No `RunId`, model request, tool invocation, or run budget is created until the applicable semantic generation is current.

## 2 Architectural Context

`SemanticEngineRegistry` owns one `SemanticEngine` for each `WorkspaceId`. Each engine owns a shared `MSBuildWorkspace` and immutable Roslyn `Solution` snapshot. Parent and delegated read-only runs share that state while individual queries obtain document/project semantic objects from the captured snapshot.

`SemanticLifecycleObserver` currently loads after `RepositoryOpened` and `SolutionLoaded`. `SemanticEngine.QueueInvalidation`, `ApplyInvalidationsAsync`, and `PromoteAsync` exist, but no production filesystem monitor drives them. `code_explore` verifies current file identity before emitting source and can omit drifted source, but that is a safety check rather than a refresh mechanism. Other semantic operations may continue from the previously loaded solution.

`SessionApplication.HandleAsync(SubmitRequestCommand)` currently creates and registers a run immediately. The freshness gate must execute before that point and must be application-owned so interactive and headless callers cannot bypass it. The TUI renders refresh lifecycle events; it does not decide whether semantic state is current.

Roslyn solutions are immutable and support incremental document replacement while retaining unaffected project state and caches. Project-system changes can alter evaluation, document membership, references, analyzers, generated source, target frameworks, or SDK selection and therefore require a full `MSBuildWorkspace` reload. Watcher notifications are hints rather than authoritative state: editors coalesce and rename temporary files, operating systems may duplicate or drop events, and watcher buffers may overflow.

## 3 Scope

- One repository/workspace-scoped filesystem change monitor with bounded coalescing and deterministic disposal/rebinding.
- One single-flight `SemanticRefreshCoordinator` shared by background, user-admission, host-mutation, and manual triggers.
- Monotonic dirty and applied versions so a refresh cannot publish falsely current state when another change arrives during preparation.
- Incremental Roslyn solution updates for stable edits to already loaded C# documents.
- Full reload for project-graph, document-membership, analyzer/configuration, uncertain, overflow, or explicitly forced changes.
- A request-admission gate before run identity, budget, registration, model dispatch, or tool activity.
- Background refresh after a change burst settles, even when the user has not submitted input.
- Serialized TUI lifecycle output that preserves an active PrettyPrompt draft.
- `/semantic_refresh` as an awaited, force-full-refresh command.
- Attribution of Threadsmith-owned writes so their watcher echoes do not produce false external-change messages or duplicate refreshes.
- Interactive/headless parity, domain events, cancellation, telemetry, tests, and implemented-behavior documentation.

## 4 Non-Scope

- Turning `MSBuildWorkspace` into an IDE-style continuously mutable workspace.
- Refreshing on every raw filesystem notification.
- Persisting Roslyn objects, watcher handles, or in-flight refresh tasks.
- Watching outside the active repository or following symlinks/reparse points.
- Treating build output, `.git`, temporary files, or unrelated repository files as semantic changes.
- Allowing repository configuration to disable freshness gating, expand watched paths, or weaken confinement.
- Cancelling an already active run merely because an external change is observed.
- Merging external edits with an active Threadsmith mutation transaction.
- Adding a second terminal reader, bypassing PrettyPrompt, or writing directly to `System.Console`.
- Incrementally interpreting arbitrary MSBuild graph changes when a full reload is required for correctness.

## 5 Current State

- `SemanticEngineRegistry.GetEngine` returns one shared engine per `WorkspaceId`.
- `SemanticLifecycleObserver` queues complete semantic loads only from repository/solution lifecycle events.
- No production `FileSystemWatcher` or equivalent repository change source updates semantic state.
- `SemanticEngine.ApplyInvalidationsAsync` demotes confidence and advances generation but does not apply changed document text.
- `SemanticEngine.PromoteAsync` performs a complete reload from the previous load request.
- Advanced semantic queries capture a generation-fenced immutable snapshot.
- `code_explore` compares semantic text with current disk text before emitting source and reports drift instead of refreshing.
- Ordinary TUI submission calls `controller.SubmitAsync` after slash-command handling and URL consent.
- `SessionApplication` allocates `RunId`, cancellation, state machine, budget, and run registration before starting execution.
- Initial semantic loading and semantic checks have lifecycle presentation, but there is no pre-run external-refresh lifecycle.

## 6 Proposed Design

### 6.1 One refresh authority

Add one `SemanticRefreshCoordinator` for all refresh decisions and execution. Trigger-specific callers may request work but may not load or mutate Roslyn state directly.

The coordinator maintains per `WorkspaceId`:

- repository and solution identity;
- latest observed dirty version;
- latest successfully applied version;
- coalesced changed paths and content identities;
- whether a full reload is required;
- whether changes are externally sourced or Threadsmith-owned;
- the current settling deadline;
- one in-flight refresh task;
- one pending force-full request; and
- failure and retry state.

The state machine is conceptually `Clean`, `Settling`, `Refreshing`, `DirtyDuringRefresh`, or `Failed`. State transitions are protected by a small lock, but filesystem reads, hashing, MSBuild loading, Roslyn compilation, event publication, and console projection never run under that lock.

Every trigger calls the same coordinator methods, for example:

```csharp
ValueTask ObserveChangeAsync(SemanticFileChange change, CancellationToken cancellationToken);

Task<SemanticRefreshResult> EnsureCurrentAsync(
    SessionId sessionId,
    SemanticRefreshReason reason,
    CancellationToken cancellationToken);

Task<SemanticRefreshResult> ForceRefreshAsync(
    SessionId sessionId,
    CancellationToken cancellationToken);
```

These are illustrative host-owned contracts; implementation may refine names and DTO shapes while preserving one authority and dependency direction. Core/application contracts must contain no Roslyn, MSBuild, watcher, terminal-library, or implementation types.

### 6.2 Repository change monitoring

Start one monitor after the active repository and selected solution are bound. Dispose and replace it atomically on repository/session rebind, resume, new-session transition, or application shutdown. Events from an obsolete monitor carry a binding generation and are discarded.

Handle `Changed`, `Created`, `Deleted`, and `Renamed`. Treat watcher `Error`, buffer overflow, lost identity, or an uncertain rename as requiring a full reload. Watch recursively only under the normalized active repository root, never traverse reparse points, and discard prohibited/out-of-root paths before queuing them.

Classify relevance from the loaded Roslyn inventory plus graph-control names:

- an edit to an existing loaded C# document is incrementally eligible;
- changes to loaded additional/analyzer-config documents receive their supported refresh treatment;
- create/delete/rename events that may change wildcard project membership require a full reload unless exact membership is proven;
- `.csproj`, `.sln`, `.slnx`, `.props`, `.targets`, `Directory.Build.*`, `Directory.Packages.props`, `global.json`, NuGet configuration, analyzer configuration, rulesets, generated-source inputs, and SDK/reference-affecting files require a full reload;
- `.git`, `bin`, `obj`, Threadsmith state, known generated/IDE/local-tool/test-result directories, temporary/editor files, and unrelated files do not dirty semantic state unless the loaded text-document inventory explicitly includes them.

The monitor does not perform semantic work. It normalizes a bounded change record, increments the dirty version, and resets a bounded settling timer. After no relevant change has arrived for the configured settle interval, it asks the coordinator to refresh. Use a conservative host-owned default in the 200–500 ms range and a maximum burst window so a noisy source cannot defer refresh forever.

Filesystem notifications alone do not prove content changed. Stable-read and hash the final confined file after settling and remove duplicate/no-op notifications. At admission and manual boundaries, perform a bounded authoritative identity check of queued paths. Watcher errors force an inventory rescan and full reload rather than claiming freshness.

### 6.3 Source attribution and host-owned writes

The transactional mutation/file-writing boundary registers expected target paths and, when available, expected resulting content identities before applying a Threadsmith-owned change. Matching watcher echoes are attributed to the host mutation and must not display `External changes detected` or enqueue an independent duplicate refresh.

Threadsmith-owned and external changes still flow through the same coordinator and semantic update implementation. Attribution changes the reason and presentation, not freshness rules. Ambiguous changes are treated as external/uncertain and use the safer full-refresh path. Do not suppress watcher events merely because a run is active.

### 6.4 Incremental document refresh

For an existing loaded C# document whose identity and project membership are stable:

1. read the current confined file after the save burst settles;
2. verify a stable content identity and locate the exact Roslyn `DocumentId` in the captured generation;
3. create a replacement solution with `Solution.WithDocumentText`;
4. preserve unaffected solution/project/document state and Roslyn caches;
5. optionally perform the bounded semantic readiness check required by current confidence policy; and
6. atomically publish the replacement solution, content identities, and next semantic generation.

If any eligibility or identity check fails, upgrade the cycle to a full reload. Multiple eligible document edits in one burst are applied to one replacement solution and published once.

### 6.5 Full refresh

Graph-affecting, membership-changing, uncertain, overflow-recovery, and manual refreshes use the existing semantic load authority. Build a new `MSBuildWorkspace`/solution away from the current engine state, apply the existing non-cooperative cancellation backstop, and publish through the existing atomic replacement boundary only after the candidate result is complete.

A completed load with reduced semantic confidence is a successful refresh and retains existing degraded/textual capability behavior. An exception, obsolete binding, unstable filesystem snapshot, or abandoned load is not success and must not advance the applied version.

### 6.6 Single-flight and dirty-version convergence

Only one refresh may execute per workspace. Background, request-admission, and manual callers join the same in-flight task.

At refresh start, capture target dirty version `V`. If a relevant change advances the dirty version while the candidate is being prepared, do not report the workspace current after publishing only `V`. Continue with one coalesced follow-up cycle until applied version equals dirty version and the filesystem has settled.

A manual request always requires one full refresh. If it joins an incremental refresh already in flight, mark `ForceFullPending`, await the incremental cycle, then run one full cycle before completing the manual caller. Repeated manual commands join the same forced cycle.

Waiter cancellation detaches that waiter but does not cancel shared background refresh. Repository rebind and application shutdown cancel/obsolete the owning refresh through the coordinator lifetime token. All obsolete results are drained and discarded.

### 6.7 Controlled publication and active runs

Background preparation may perform file reads and build a candidate snapshot without blocking the composer. Publishing a new semantic generation occurs only at the existing controlled semantic/turn boundary.

If no run is active for the workspace, publish immediately after preparation. If a run owns a frozen semantic generation, do not silently change that run's baseline in the middle of an atomic model/tool/semantic operation. Queue publication for its next legal boundary or terminal completion. Existing generation checks discard obsolete late results. Do not cancel the active run solely because a change was observed.

New run admission always waits for both candidate preparation and controlled publication. This plan's binding invariant is:

> A submitted request cannot receive a `RunId` while its workspace has unapplied semantic changes, is still settling relevant changes, or has an active semantic refresh/publication.

Move or wrap application submission so `EnsureCurrentAsync` completes before creating cancellation state, `RunStateMachine`, budget scope, steering registration, or `_runs` registration. The gate applies to every ordinary interactive/headless model request. Non-semantic slash commands such as `/help`, `/status`, and `/quit` remain immediately available.

If refresh cannot establish current state, fail request admission with a bounded sanitized error and do not dispatch the request. Preserve/re-present submitted composer text where the existing surface can do so safely; never silently discard it.

### 6.8 Background and TUI presentation

When a settled externally attributed cycle starts, publish one lifecycle start event and render exactly one visible message through the serialized PrettyPrompt console boundary:

```text
External changes detected; updating semantic model...
```

On success, render a concise completion such as:

```text
Semantic model updated (3 files, 240 ms).
```

The exact duration formatting must reuse the existing operation-duration formatter. A reduced-confidence completion states the resulting confidence without treating compiler diagnostics as refresh infrastructure failure. Failure output is sanitized, bounded, actionable, and leaves the workspace dirty.

Background output must not corrupt, clear, submit, or lose an active composer draft. Reuse Plan 96's single serialized console owner and Plan 77 lifecycle presentation; do not add a second console reader or direct console write. When the composer is empty, cooperatively end and immediately reopen that PrettyPrompt interaction so lifecycle output appears without physical input. When any draft text exists, including whitespace, queue the output until the draft is submitted, cancelled, or cleared back to empty. One coalesced cycle produces one start/completion pair, not one line per raw event.

If a request arrives during an already announced background refresh, it silently joins the same activity rather than printing another start message. If request admission becomes the first executor for settled unannounced changes, it publishes the same externally attributed lifecycle and message before waiting.

### 6.9 Manual command

Add `/semantic_refresh` to interactive command dispatch and `/help` in alphabetical order. The command:

- is handled locally and never enters model context;
- forces one full refresh even when the workspace is currently clean;
- joins/upgrades the shared single-flight coordinator as described above;
- waits for completion and reports duration and resulting confidence;
- returns a clear error when no repository/solution is bound; and
- supports cancellation without leaving coordinator state corrupt.

Provide equivalent headless application/command access where current command parity requires it. Do not create a model run, run budget, conversation message, or tool call for manual refresh.

## 7 Public Contracts

Add the minimum provider-neutral contracts needed to preserve application/UI separation. Expected concepts include:

- `SemanticRefreshId` if correlation cannot safely reuse workspace generation;
- `SemanticRefreshReason` (`ExternalChange`, `HostMutation`, `UserAdmission`, `Manual`, `Recovery`);
- `SemanticRefreshMode` (`Incremental`, `Full`);
- `EnsureSemanticCurrentCommand` and result, or an equivalent injected application gate;
- `ForceSemanticRefreshCommand` and result;
- `SemanticRefreshStarted`;
- `SemanticRefreshCompleted`; and
- `SemanticRefreshFailed`.

Events/results may include session/workspace identity, refresh identity, reason, mode, coalesced file count, applied/dirty generation, resulting confidence, duration, and bounded failure classification. They must not contain source text, raw changed paths, secrets, watcher implementation details, Roslyn/MSBuild types, exception dumps, or terminal types.

Prefer extending the existing semantic contracts and lifecycle authority to creating a parallel semantic subsystem. Preserve existing `SemanticLoadCompleted` compatibility; define explicitly whether refresh completion composes with it or whether initial load and refresh remain distinct lifecycle events without duplicate projection updates.

## 8 Project/File Changes

- `Threadsmith.Core`: provider-neutral refresh identifiers, reason/mode/result contracts, commands, and events.
- `Threadsmith.DotNet`: single-flight refresh coordinator, semantic change classification, incremental document replacement, full-refresh reuse, generation convergence, and safe disposal.
- `Threadsmith.Workspaces`: confined repository change source and Threadsmith-owned mutation attribution/lease integration if filesystem ownership fits this layer.
- `Threadsmith.Execution`: pre-`RunId` semantic freshness admission and active-turn publication boundary.
- `Threadsmith.App`: construct one coordinator, bind repository/session lifecycle, register monitor ownership, and dispose it in deterministic async order.
- `Threadsmith.Tui`: serialized background lifecycle rendering, request waiting, draft preservation, and `/semantic_refresh` dispatch/help.
- `Threadsmith.Cli`: shared command/gate behavior and structured headless reporting where applicable.
- Focused tests in semantic/model-tooling, workspace, CoreRuntime, TUI, CLI, and architecture/event-serialization suites.
- On implementation, update the event catalog, user guide, semantic/repository operations, acceptance scenario, and manual test procedure.

Do not add a new project unless dependency direction cannot be preserved with the existing semantic, workspace, execution, and surface projects.

## 9 Ordered Tasks

1. Read the applicable DOX chain, semantic/workspace architecture, current lifecycle observer/engine, submission flow, console serialization, and event projection tests.
2. Add failing coordinator tests for single-flight background/admission/manual convergence, dirty-during-refresh, and force-full upgrade.
3. Define minimal host-owned refresh contracts and lifecycle events.
4. Implement confined change classification, stable hashing, debounce, burst bound, watcher-error recovery, and repository-binding generation.
5. Implement coordinator state, background scheduling, single-flight task ownership, waiter cancellation, failure retention, and disposal.
6. Add incremental existing-document update and atomic semantic-generation publication.
7. Route graph/uncertain/manual changes through one full-refresh path using current load/backstop behavior.
8. Add Threadsmith-owned write attribution so mutation watcher echoes reuse the coordinator without false external messaging.
9. Insert the application admission gate before all run allocation/registration and add controlled active-run publication behavior.
10. Render refresh lifecycle through the serialized TUI surface and preserve composer drafts.
11. Add `/semantic_refresh`, alphabetical `/help`, and headless parity.
12. Add integration, race, cancellation, rebinding, event, and manual coverage.
13. Update implemented-behavior documentation and perform the DOX/minimal-update review.
14. Run focused suites, full build, prohibited-bookkeeping checks, `git diff --check`, and a final architecture/security review.

## 10 Testing

### 10.1 Change monitor

- A burst of duplicate change/create/rename notifications becomes one coalesced refresh.
- The final stable hash removes timestamp-only and duplicate no-op events.
- A loaded `.cs` edit is incrementally eligible.
- Project/solution/props/targets/SDK/analyzer/membership changes force full reload.
- Create/delete/rename ambiguity forces full reload.
- Watcher overflow/error triggers bounded rescan and recovery reload.
- `.git`, `bin`, `obj`, temporary, prohibited, out-of-root, and reparse paths do not become semantic inputs.
- Repository rebind/disposal rejects late events from the old watcher.

### 10.2 Coordinator concurrency

- Background and user-admission requests share exactly one in-flight refresh.
- Repeated user submissions do not duplicate a refresh or lifecycle start event.
- A change arriving during refresh advances dirty version and forces convergence before release.
- Multiple incremental paths publish one replacement generation.
- Manual refresh while clean performs one full reload.
- Manual refresh joining incremental work receives one full follow-up and repeated manual callers share it.
- A cancelled waiter does not cancel shared work; shutdown/rebind cancellation discards obsolete results.
- Failure leaves applied version unchanged and blocks request admission until successful recovery.
- Separate workspaces remain isolated.

### 10.3 Semantic correctness and performance

- A manual edit to an existing C# document becomes visible to symbol, reference, implementation, advanced semantic, and `code_explore` queries without full solution reload.
- Unchanged project/document identities and caches remain reusable after incremental edit.
- Project membership/reference/configuration changes become visible after full reload.
- Source identity and semantic results come from the same published generation.
- Reduced-confidence completed loads release admission with explicit confidence; infrastructure failure does not.
- Instrumented tests assert ordinary source saves do not call complete `OpenSolutionAsync`.
- A bounded large-solution fixture or fake loader verifies debounce and single-flight behavior without timing-fragile wall-clock assertions.

### 10.4 Submission and surfaces

- No `RunId`, budget, steering registration, model call, tool call, or conversation append occurs before freshness completes.
- A request submitted during settling waits for stable refresh.
- A request submitted during background refresh joins it and starts afterward.
- `/help`, `/status`, and `/quit` remain available without forcing refresh; semantic/model-run operations use the gate.
- `/semantic_refresh` is alphabetical in help, performs no model call, and reports success/degraded/failure states.
- Background start/completion output appears once and preserves partially typed PrettyPrompt content.
- Interactive and headless submission share the application gate and produce equivalent refresh outcomes.
- Event serialization, persistence tolerance, redaction, and projection tests cover all new public contracts.

### 10.5 Regression and manual verification

- Existing semantic load, drift detection, advanced query, mutation, repository lifecycle, session resume/new/clone, TUI activity, Plan 96 input, and parallel-agent tests pass.
- Architecture dependency and event-catalog checks pass.
- Full solution build and affected executable test suites pass.
- Add an executable manual case covering idle background refresh, typing during refresh, submission blocking, ordinary incremental edit, project-file full reload, forced manual refresh, and recovery after a simulated watcher miss.

## 11 Security/Permissions

The active repository, trust state, prohibited paths, symlink/reparse rules, and current workspace binding remain host authority. Filesystem notifications never grant read permission and are not trusted content. Normalize and confine every path again at stable-read time; do not follow a path that changed identity between notification and read.

Repository files and configuration cannot disable refresh, change settling/resource bounds, expand watcher roots, select arbitrary solution paths, force external process/network activity, or mark their own changes trusted. Do not log source content, secret values, raw untrusted file bodies, or unrestricted paths.

The monitor performs no write. Incremental refresh changes only in-memory Roslyn state. Full reload retains existing trust and MSBuild execution restrictions. Threadsmith-owned attribution is exact and short-lived; broad path-prefix suppression is forbidden because it could hide concurrent external edits.

## 12 Observability

Publish one correlated lifecycle for each coalesced refresh, including reason, mode, changed-file count, dirty/applied versions, resulting confidence, duration, and safe outcome classification. Record metrics for debounce cycles, incremental/full counts, joined waiters, forced upgrades, dirty-during-refresh follow-ups, watcher recovery, refresh duration, admission wait duration, failures, and discarded obsolete results.

TUI messages are concise and bounded. Logs may include normalized repository-relative paths only at an appropriate diagnostic level and after policy review; ordinary events and telemetry retain counts/classifications rather than path lists. Never emit source bodies or exception dumps to normal user output.

The event catalog must distinguish initial semantic load, external refresh, host-mutation refresh, manual refresh, degraded completion, and refresh failure without encoding implementation-specific watcher behavior.

## 13 Migration/Compatibility

No database migration is expected unless durable refresh events require the repository's normal event-schema registration. Dirty state, watcher handles, and in-flight tasks are runtime-only. Restored sessions perform the normal authoritative semantic load and begin clean only after that load completes.

Existing semantic tool schemas and result DTOs remain compatible. Existing initial `SemanticLoadCompleted` behavior remains available. Ordinary clean submissions incur only a bounded in-memory freshness check. Non-repository sessions and repositories without a selected solution retain current text-only behavior and return a clear result from `/semantic_refresh`.

Configuration is not required for initial delivery. If debounce/resource tuning is exposed, accept it only from repository-excluding trusted configuration with conservative validated bounds; defaults must be safe and useful without configuration.

## 14 Acceptance Criteria

1. A settled relevant external change automatically begins semantic refresh without user input and produces one `External changes detected; updating semantic model...` message.
2. Ordinary edits to existing loaded C# documents update the shared Roslyn solution incrementally rather than reopening the complete solution.
3. Project-graph, membership, analyzer/configuration, uncertain, watcher-recovery, and manual changes perform a complete refresh.
4. Background, user-admission, and manual triggers use one single-flight coordinator and never implement independent refresh paths.
5. A user request submitted while changes are settling or refreshing cannot receive a `RunId` or reach the model until applied version equals the latest settled dirty version.
6. A change arriving during refresh cannot cause a stale generation to be reported as current.
7. Background lifecycle output uses the serialized PrettyPrompt console boundary, appears without physical input when the composer is empty, waits while any draft text exists, and preserves drafted input exactly.
8. `/semantic_refresh` appears alphabetically in `/help`, forces/awaits a full refresh, and never creates a model run.
9. Threadsmith-owned writes refresh through the same coordinator without being falsely announced as external or refreshed twice.
10. Refresh failure remains visible and dirty, blocks new model-run admission, and can be recovered through later change, retry, or `/semantic_refresh`.
11. Completed degraded-confidence refreshes retain existing semantic fallback behavior and report the resulting confidence honestly.
12. Watcher overflow, repository rebind, cancellation, active runs, parallel semantic readers, and shutdown remain bounded and race-safe.
13. Interactive and headless requests enforce the same freshness invariant.
14. Focused regressions and the full solution build pass, and manual verification demonstrates background, blocked-admission, incremental, full, and forced-refresh paths.

## 15 Risks

- **Watcher events are duplicated or lost:** treat them as hints, hash stable content, bound debounce, and force authoritative rescan/full reload after watcher errors.
- **Reload storms:** coalesce by workspace and dirty generation, apply a maximum burst window, and publish once per converged cycle.
- **Stale publication race:** compare captured target version and binding generation immediately before atomic publication; follow up or discard when either changed.
- **Incremental eligibility is misclassified:** default ambiguous membership/configuration changes to full reload.
- **Threadsmith writes look external:** use exact mutation attribution rather than timing-only suppression.
- **External edits overlap a mutation transaction:** do not merge them implicitly; retain existing baseline/conflict authority and defer semantic publication to a legal boundary.
- **Background output damages the composer:** route exclusively through the serialized Plan 96 console owner and add draft-preservation tests.
- **Submission hangs behind noisy files:** cap settling, surface activity, allow cancellation, and use stable snapshot/recovery failure rather than waiting forever.
- **Shared refresh is cancelled by one caller:** separate coordinator lifetime from waiter cancellation.
- **Full reload is expensive:** use incremental replacement for proven document edits and instrument actual incremental/full rates and durations.
- **Semantic failure is confused with compiler errors:** a loaded lower-confidence snapshot may complete; only infrastructure/currentness failure keeps admission closed.
- **Excessive path disclosure:** events use counts and classifications; diagnostics remain bounded and sanitized.

## 16 Documentation

On implementation:

- update the user guide with automatic refresh, request waiting, completion/failure behavior, and `/semantic_refresh`;
- update repository/semantic operations with monitoring, debounce, incremental/full classification, recovery, and headless behavior;
- update the event catalog for new refresh lifecycle events;
- add or update a product-level acceptance scenario for stale-state admission and manual recovery;
- add one executable manual test covering idle/background and submission-time refresh;
- update `/help` and every maintained command reference alphabetically;
- update semantic/workspace/TUI DOX only if durable ownership or implementation guidance changes; and
- keep completed Plans 05, 06, 29, 77, 81, and 96 unchanged.

## 17 Open Decisions

- Select the measured default settle interval and maximum burst window within conservative bounds.
- Decide whether loaded additional documents can be replaced incrementally in the first delivery or conservatively force a full reload.
- Decide whether a refresh prepared during an active run publishes at the next model-round boundary or only after the run becomes terminal; preserve the frozen-baseline contract either way.
- Decide whether failed automatic refresh retries once after bounded backoff or waits for the next change/manual/admission trigger. Avoid an unbounded background retry loop.
- Decide whether the application gate is expressed as a dedicated command handler/middleware or an injected `ISemanticFreshnessGate` on `SessionApplication`; it must remain below all surfaces and before run allocation.
