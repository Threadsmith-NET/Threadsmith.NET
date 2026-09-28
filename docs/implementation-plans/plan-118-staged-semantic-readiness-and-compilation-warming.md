# Plan 118 — Staged semantic readiness and bounded compilation warming

**Status:** Planned  
**Delivery track:** Maintenance — semantic startup performance, readiness coordination, and bounded resource use  
**Prerequisites:** Completed Plan 117 with Roslyn 5.9/.NET 10.0.4xx compatibility closed and same-host load-phase measurements recorded; the existing semantic confidence contract; external semantic refresh; request-admission freshness; advanced semantic query and code-explore generation fencing; pre-mutation analysis; and frontend-neutral startup coordination.  
**Related contracts:** [planning governance](planning-governance.md), [shared context §G](00-shared-context.md#g-implementation-document-template-and-agent-instructions), [Plan 06](plan-06-roslyn-msbuild-semantic-discovery.md), [Plan 74](plan-74-roslyn-based-pre-mutation-analysis.md), [Plan 81](plan-81-roslyn-code-explore-exact-anchors-and-source.md), [Plan 97](plan-97-external-semantic-refresh.md), [Plan 117](plan-117-roslyn-dotnet-semantic-toolchain-upgrade.md), [semantic confidence](../architecture/semantic-confidence.md), [event catalog](../architecture/event-catalog.md), and [portable C# guardrails](../guardrails/portable-csharp-guardrails.md).

## 1. Objective

Reduce the time from repository selection to a usable compiler-backed Threadsmith session without weakening semantic confidence, freshness, completeness disclosures, cancellation, or validation authority.

Replace the current all-project sequential startup barrier with one workspace-owned preparation path that:

1. evaluates and confines the selected solution/project once;
2. prepares a small deterministic readiness frontier until at least one usable compilation is available;
3. publishes the existing terminal initial-load result at honest `PartialCompilation` or the appropriate degraded level;
4. lets interactive and headless work proceed under the existing minimum-confidence rules;
5. compiles exact projects on demand before project-scoped semantic work;
6. warms remaining projects in the background with bounded concurrency and lower priority than demand work; and
7. progressively promotes project and aggregate confidence to `FullSemantic` only when every required project is proven usable.

Plan 117's measurements determine whether this work proceeds and provide the baseline. The implementation must improve measured time to usable semantic readiness on representative multi-project solutions while keeping full-confidence time, memory, generator correctness, and cancellation within reviewed limits.

## 2. Architectural Context

`SemanticEngineRegistry` owns one `SemanticEngine` per `WorkspaceId`. Each engine owns one shared `MSBuildWorkspace`, one immutable Roslyn `Solution` snapshot, a set of projects whose compilations have been proven usable, project inventory, confidence, and generation. Parent and delegated read-only runs share that state.

Today `SemanticEngine.LoadCoreAsync` opens the selected solution/project and then awaits `GetCompilationAsync` sequentially for every loaded project. Only after that loop does it replace shared state and return to `SemanticLifecycleObserver`, which publishes `SemanticConfidenceChanged` and `SemanticLoadCompleted`. The interactive coordinator blocks its startup splash on `SemanticLoadCompleted`. Headless request startup waits for at least `PartialCompilation` or terminal load completion. For the current 51-project Threadsmith solution, every initial project compilation therefore sits on the startup-critical path.

The confidence model already permits progressive truth:

- `ProjectGraphOnly` means evaluation succeeded but no usable compilation is proven;
- `PartialCompilation` means one or more specific projects are usable; and
- `FullSemantic` means every expected/loaded project is usable with no confidence-reducing workspace failure.

This plan uses those existing meanings. It does not redefine partial state as complete. Queries must operate only on their proven compiled coverage or await the missing coverage they require. Validation and mutation paths must never accept a partial subset when their affected-project contract requires more.

Roslyn solutions and projects are immutable views with internal caches. Calling `GetCompilationAsync` concurrently can reduce wall-clock latency for independent work but can also amplify CPU, memory, generator, analyzer, and dependency-graph work. Concurrency must therefore be host-bounded, measured, generation-fenced, and shared across startup, background, query, validation, and refresh callers.

## 3. Scope

- Separate solution/project evaluation from compilation preparation inside the existing `SemanticEngine` lifecycle.
- Publish evaluated, confined project state internally before compilation preparation without exposing it to semantic consumers as compiled coverage.
- Add one per-workspace semantic preparation coordinator used by initial readiness, demand requests, background warming, refresh, validation, and disposal.
- Produce initial usable readiness after the first successful deterministic frontier compilation rather than after every project compilation.
- For direct-project selection, prioritize the explicitly selected project.
- For solution selection, prioritize a deterministic dependency-graph frontier without guessing from project names or silently excluding tests.
- Add demand-driven project compilation for path-, project-, symbol-, validation-, mutation-, and code-explore-scoped work.
- Add bounded, lower-priority background warming for remaining loaded projects.
- Deduplicate concurrent compilation requests so one project/generation has at most one preparation operation.
- Use a small production concurrency bound selected from Plan 117 measurements and exposed through immutable internal semantic resource limits for deterministic tests.
- Preserve non-cooperative Roslyn cancellation through abandon-and-discard with a bounded-wait backstop.
- Atomically promote compiled coverage, per-project confidence, aggregate confidence, and semantic generation.
- Preserve request freshness and full-reload/incremental-refresh ownership under Plan 97.
- Preserve interactive/headless minimum readiness of `PartialCompilation` for model requests that require semantic tools.
- Make query waits and background promotion observable through existing tool/semantic activity and bounded structured telemetry.
- Document direct project selection as an immediate user-controlled way to reduce the evaluated graph when a complete solution is unnecessary.
- Compare time to `PartialCompilation`, time to `FullSemantic`, CPU, memory, generator results, and first-query latency against Plan 117 baselines.

## 4. Non-Scope

- No second `MSBuildWorkspace`, alternate project loader, language server, compiler server, out-of-process semantic engine, or duplicate semantic state store.
- No change to Roslyn/MSBuild versions; Plan 117 owns the toolchain upgrade and compatibility closure.
- No persistent cross-process Roslyn cache, serialized compilation, syntax-tree cache, or reuse of compiler objects from durable storage.
- No unloading or omitting projects from the selected solution merely to claim faster full confidence.
- No automatic replacement of a selected solution with an inferred product project.
- No test-project detection based only on names, paths, xUnit references, or other heuristics for startup exclusion.
- No user-configurable arbitrary compilation thread count in the first implementation. The host owns a conservative bound; a public setting requires separate operational justification.
- No unbounded `Task.WhenAll`, per-project OS process, per-file compiler invocation, whole-repository preload, or eager generated-source materialization outside Roslyn's established project path.
- No weakening of `FullSemantic`, affected-project validation, pre-mutation analysis, introduced-diagnostic classification, source freshness, or request admission.
- No silent partial result where an existing tool contract requires complete solution coverage. Such a call waits for the required projects or returns an explicit bounded unavailable/incomplete result.
- No cancellation of shared background/demand compilation merely because one waiter cancels; no continued publication from an obsolete workspace generation.
- No new terminal-owned execution path or direct `System.Console` status output.

## 5. Current State

### 5.1 Initial load

After `OpenSolutionAsync` or `OpenProjectAsync`, the engine confines projects, source documents, additional documents, analyzer configuration, and references to repository policy. It then executes one sequential loop over `load.Solution.Projects`, calling the established non-cooperative wrapper around `Project.GetCompilationAsync`.

The loop builds:

- `_compiledProjects`, currently a complete set only after the loop ends;
- per-project `SemanticProjectInfo.Confidence`;
- aggregate confidence; and
- diagnostics for workspace and compilation failures.

`ReplaceStateAsync` swaps the complete workspace/solution/inventory atomically, increments generation, publishes confidence when requested, and publishes `SemanticLoadCompleted` when the caller owns completion. The lifecycle observer suppresses engine publication during binding and publishes the two events after `LoadForBindingAsync` returns.

### 5.2 Startup and admission

The interactive coordinator subscribes to `SemanticLoadCompleted` and keeps the startup surface in `Semantic Loading ...` until that event. It does not admit ordinary composer input during startup.

Headless startup polls the session projection for `PartialCompilation` with a 30-second backstop. It currently returns early when terminal loading completes below partial and then declines to submit the request. Under staged loading, terminal initial completion should normally be published only after a usable compilation exists or after the readiness frontier has conclusively failed, preserving this behavior without polling through a known terminal failure.

### 5.3 Semantic consumers

Many engine/query paths capture `_solution`, a copy of `_compiledProjects`, confidence, and generation. They filter work to compiled projects, but some operations call `GetCompilationAsync` again or scan projects to resolve symbol IDs. Advanced code exploration relies on immutable snapshots and explicit omissions at partial confidence. Validation and mutation analysis derive affected projects and require compiler diagnostics for the relevant coverage.

There is no central operation that says “ensure these projects are compiled for this generation.” Without that authority, adding background compilation separately to each query would duplicate work and race confidence publication.

### 5.4 Refresh and invalidation

Stable document edits create a replacement immutable solution and reprepare affected projects that were already compiled. Graph, membership, analyzer/configuration, SDK, uncertain, or manual changes use complete reload. Refresh publication is generation/version fenced and request admission waits for current state.

A compilation warming operation prepared from an older solution must not publish into the replacement generation. Conversely, an incremental replacement should retain proven unaffected coverage and reschedule any still-uncompiled work against the new solution rather than forcing another complete workspace evaluation.

### 5.5 Existing user-controlled scope

`--solution` already accepts a supported project path as well as a solution. Direct-project selection is the only authoritative current signal that a narrower graph is intended. The optimization should preserve and measure this path rather than inventing a repository-wide project preference.

## 6. Proposed Design

### 6.1 Three readiness milestones

Treat one initial binding as three ordered milestones:

1. **Evaluated:** MSBuild has opened and Threadsmith has confined one immutable solution/project graph. Confidence is internally `ProjectGraphOnly`; no compiler-dependent consumer is admitted solely from this state.
2. **Usable:** At least one readiness-frontier project has a non-null compilation and all state for that project is published atomically. Aggregate confidence is honestly `PartialCompilation` unless the selection contains only that successfully compiled project and has no workspace failure. The lifecycle load returns and `SemanticLoadCompleted` is published once.
3. **Warm:** Every expected/loaded project has reached a terminal preparation result for the current generation. Aggregate confidence becomes `FullSemantic` only under the existing full-confidence rules; otherwise it remains an honest degraded level with recorded omissions/failures.

The evaluated milestone is internal and measured. Do not persist a new public event merely to announce that semantic tools are not yet usable. The existing startup remains visible until usable or terminal degraded readiness. Background warm promotion uses `SemanticConfidenceChanged` when the aggregate level changes; it does not emit a second `SemanticLoadCompleted`.

If the complete selected graph contains no compilable project, attempt the bounded readiness candidates in deterministic order and publish terminal `ProjectGraphOnly`/`None` only after no candidate can establish partial readiness. Do not spin automatically on the same failed generation.

### 6.2 One preparation coordinator

Add one engine-owned `SemanticCompilationCoordinator` or equivalently focused internal component. It owns preparation for exactly one current workspace generation and is the only path that calls `GetCompilationAsync` for readiness/warming purposes.

Its state contains:

- immutable solution and generation identity;
- engine-lifetime cancellation linked to reload/disposal;
- per-project state: `Pending`, `Queued`, `Running`, `Succeeded`, `Failed`, or `Obsolete`;
- one shared completion task/result per project and generation;
- a high-priority demand queue and low-priority warm queue;
- a bounded worker count;
- aggregate terminal counts and bounded sanitized failure facts; and
- one full-warm completion signal.

The coordinator does not own the workspace, public confidence, event stream, refresh versions, or query DTOs. `SemanticEngine` remains the state/publication authority. The coordinator reports detached host-owned preparation outcomes back through one generation-checked engine method.

Do not introduce one coordinator per caller. Initial readiness, model tools, validation, mutation, refresh, and background warm all join the same per-project task.

### 6.3 Deterministic readiness frontier

For a direct project selection, enqueue the explicitly selected Roslyn project first. If Roslyn loads referenced projects, queue them for later warming according to dependency order; do not delay usable readiness merely to mark the complete closure compiled unless `GetCompilationAsync` itself requires that work.

For a solution selection, rank readiness candidates using only the evaluated project graph:

1. projects with the greatest number of transitive dependents first, because preparing a shared dependency is most likely to be reused by later project compilations;
2. then projects with more direct dependents;
3. then stable declared solution order; and
4. finally normalized project path as a deterministic tie breaker when required.

Do not use project/test naming, directory names, package references, output kind, or document content to exclude a candidate. The ranking chooses preparation order, not capability or confidence.

Queue only a small bounded frontier initially. Return usable readiness when the first candidate succeeds. Other already-running frontier work may continue as shared warming; it is not discarded merely because partial confidence was reached. If a candidate fails, record the failure and continue through candidates until one succeeds or every loaded candidate has terminally failed.

The exact frontier size and worker bound must be selected from Plan 117 measurements. Production defaults must remain conservative—normally one or two concurrent preparations, never an unbounded processor-count fan-out—and be locked by direct tests.

### 6.4 Atomic progressive publication

When a preparation succeeds, the engine reacquires its small state lock and verifies:

- the coordinator is still the active coordinator;
- workspace id, solution reference/generation, and project id match;
- the project still exists in the current solution; and
- the result has not already been published.

It then copies and replaces the compiled-project set, updates that project's inventory confidence, recalculates aggregate confidence with the existing expected/loaded/workspace-failure rules, and increments semantic generation exactly once for the published coverage change. No Roslyn await, event publication, logging, or continuation runs under the lock.

Publish `SemanticConfidenceChanged` only when the aggregate enum changes, not for every project. Project inventory readers still observe progressively updated per-project confidence. The lifecycle observer publishes the single `SemanticLoadCompleted` returned by initial usable/terminal readiness. Promotion from partial to full later publishes confidence only.

Initial publication is an explicit ordering barrier. On the ordinary lifecycle path, the engine may retain already-running frontier work, but it cannot publish later coverage or start additional warm work until `SemanticLifecycleObserver` has successfully published the initial confidence and `SemanticLoadCompleted` pair and acknowledged the binding generation. If initial publication fails or the binding is superseded, abort that generation instead of allowing a later `FullSemantic` event to precede, contradict, or resurrect a missing initial completion. On the direct `SemanticEngine.LoadAsync` path, the engine releases the same barrier only after its own initial event publication succeeds. Use one internal lifecycle handoff; do not add timing delays or rely on scheduler ordering.

Compilation failure records bounded diagnostics and marks the project terminally failed for that generation. A failure cannot remove previously proven project coverage or repeatedly requeue itself. Manual/full refresh creates a new generation and may retry it.

### 6.5 Demand-driven coverage

Add one internal asynchronous engine operation along the lines of:

```csharp
Task<SemanticPreparationResult> EnsureProjectsPreparedAsync(
    IReadOnlySet<ProjectId> projectIds,
    SemanticPreparationReason reason,
    CancellationToken cancellationToken);
```

The exact type names may change, but the contract must:

- validate requested project IDs against one captured generation;
- expand to the project-reference dependency closure only where Roslyn/consumer correctness requires it;
- promote pending warm items to the demand queue instead of creating duplicates;
- await shared project tasks with caller cancellation that cancels only that wait;
- return host-owned succeeded/failed/obsolete/omitted project IDs and resulting confidence;
- force the caller to recapture its semantic snapshot after successful promotion; and
- never expose `Project`, `ProjectId`, `Compilation`, or other Roslyn types outside `Threadsmith.DotNet`.

Consumer routing must be explicit:

| Consumer shape | Required preparation |
|---|---|
| Inventory/project graph only | None beyond evaluated graph; disclose current confidence. |
| Exact document/path/project anchor | Owning project(s), plus required dependency closure. |
| Affected-project diagnostics, pre-mutation analysis, or semantic mutation | Every affected project required by the existing validation contract before analysis. |
| Known symbol identity with project provenance | Proven owning project and required relationship scope. |
| Symbol identity without sufficient provenance | Search compiled coverage first only when the result can be proven complete there; otherwise demand the remaining candidate projects before returning “not found.” |
| Global symbol/reference/implementation query | All projects needed for the existing completeness contract, or an explicitly partial result with current omissions where that contract already permits partial confidence. |
| Code explore with path/symbol anchors | Resolve cheap textual/structural candidates first, demand only candidate project coverage, recapture, then run semantic expansion. |
| Natural-language code explore | Use existing bounded lexical/structural candidate retrieval, demand candidate projects, and disclose unprepared/failed projects under existing omission rules. |
| Generated-code query | Prepare projects whose generators must run before claiming generated inventory completeness. |

Do not make every semantic tool await full warm by default; that would move the original startup barrier to the first tool call. Do not return a transient partial “not found” as authoritative. Each tool must distinguish “complete for requested scope,” “partial with disclosed omissions,” and “preparation failed/cancelled.”

### 6.6 Background warming

After usable readiness publication, enqueue every remaining loaded project once at low priority. Workers continue while the engine generation remains current and the host is alive.

“After publication” is enforced through the initial publication barrier in §6.4. Work that was already required to establish usable readiness may finish behind the barrier, but its state/event promotion is buffered or discarded according to the binding generation. This keeps the durable event order deterministic without throwing away useful compiler work.

Demand work always moves ahead of queued background work. Already-running background compilation is not preempted unless the generation becomes obsolete or the engine is disposed; forcibly cancelling shared Roslyn work for priority would waste work and can be non-cooperative. The conservative worker bound ensures at least one demand item cannot be starved behind an unbounded running set.

Background warming must yield capacity to active user work beyond compilation where the existing semantic concurrency category requires it. Integrate with the established tool/concurrency ownership rather than creating an unrelated semaphore that permits combined unbounded work.

When every project is terminal:

- promote to `FullSemantic` only if every expected/loaded project succeeded and workspace failures permit it;
- otherwise retain `PartialCompilation` or `ProjectGraphOnly` and bounded failure/omission facts;
- publish no repeated work automatically for the failed generation; and
- expose completion through internal diagnostics/metrics so tests and disposal can join deterministically.

### 6.7 Cancellation, reload, and disposal

There are three cancellation scopes:

1. **Caller wait:** cancelling a tool/run/headless waiter stops that waiter only. Shared preparation continues for background or other callers.
2. **Generation lifetime:** full reload, repository/solution rebind, or engine replacement cancels the coordinator, applies the existing bounded non-cooperative backstop, and discards every late result from the obsolete generation.
3. **Application lifetime:** engine disposal cancels preparation, completes queues, joins workers under the established bounded teardown policy, disposes the workspace once, and publishes no late events.

Never pass one arbitrary tool caller's cancellation token as the sole token controlling shared compilation. Conversely, do not detach work from engine lifetime. Every late continuation checks generation before state or event publication.

Readiness failure and cancellation remain distinguishable. User cancellation cannot be reported as a compiler failure; obsolete generation completion cannot lower confidence or overwrite a newer workspace.

### 6.8 Refresh and mutation integration

Incremental document refresh starts from the current immutable solution and proven compiled-project set. It must:

- cancel/obsolete queued or running preparation built from the old solution generation;
- preserve proven coverage for unaffected projects only where Roslyn solution identity and current contracts make reuse valid;
- reprepare changed affected projects that were previously compiled before publishing the refreshed solution;
- create a new coordinator for remaining uncompiled projects against the replacement solution; and
- retain Plan 97 dirty/applied version and request-admission fencing.

A full refresh follows the staged load path but `/semantic_refresh` remains an explicit awaited recovery operation. Manual refresh completion must continue to mean the requested refresh reached its documented terminal confidence, not merely that background warming was queued. If preserving that contract requires manual refresh to await full warm, it does so; interactive initial startup optimization must not silently weaken recovery semantics.

Pre-mutation analysis, semantic mutation, baseline capture, and post-mutation validation call demand preparation for their exact affected project closure before reading diagnostics. If required coverage cannot be prepared, existing fail-closed/degraded gates apply. Background partial coverage never authorizes a mutation or validation result that previously required broader evidence.

### 6.9 Interactive and headless behavior

Interactive startup continues displaying `Semantic Loading ...` until the lifecycle publishes one initial terminal result. Under ordinary trusted multi-project loading this occurs at honest `PartialCompilation`, so the composer becomes available while background warm continues. The retained status/footer follows later `SemanticConfidenceChanged` promotion to `FullSemantic` without reopening the startup splash or printing one line per project.

Headless request startup retains its current `PartialCompilation` requirement and timeout. It can proceed as soon as usable readiness exists. A subsequent semantic tool demand joins the shared coordinator and is covered by normal tool activity/cancellation visibility. If the initial frontier cannot establish partial confidence, headless startup receives the same explicit refusal as today.

Do not add an unsolicited background transcript stream. A concise status transition or retained footer update is sufficient. Explicit `/semantic_refresh`, user-visible recovery, and tool waits retain their established activity blocks and duration ownership.

The user guide should recommend selecting a direct project with `--solution <path.csproj>` when the desired scope is known. This is an opt-in authoritative graph reduction, not an automatic optimization.

### 6.10 Measurement and adaptive decision gates

Use Plan 117's phase measurements and extend them with:

- time from selection to evaluated graph;
- time to first usable compilation/`SemanticLoadCompleted`;
- time to full warm terminal state;
- frontier candidate attempts/failures;
- demand queue wait and preparation duration;
- background queue depth and bounded concurrency utilization;
- duplicate requests joined rather than executed;
- obsolete results discarded;
- peak/sampled working set and CPU during cold/warm runs; and
- first semantic query latency by project-scoped, code-explore candidate-scoped, and global-completeness shapes.

Compare three controlled modes in an opt-in measurement harness that calls production paths: current sequential baseline from Plan 117, staged single-worker, and staged bounded-worker target. Do not ship multiple production modes or retain a benchmark-only alternate engine.

Select the production worker/frontier defaults from evidence across the small fixture, direct app project, full Threadsmith solution, and at least one generator-heavy multi-project fixture. Faster partial readiness is not sufficient if full warm, peak memory, generator correctness, or first meaningful query materially regresses without justification.

## 7. Public Contracts

No Roslyn or MSBuild type may enter Core, events, persistence, projections, extension contracts, model context, or terminal interfaces.

The preferred implementation adds only internal `Threadsmith.DotNet` preparation types. Existing public confidence enum values remain unchanged. `SemanticLoadCompleted` remains a single durable initial-load terminal fact, but its documented terminal point changes from “all eager compilation attempts finished” to “the evaluated selection reached usable partial/full readiness or conclusively failed to do so.” Later background promotion is represented by `SemanticConfidenceChanged` only.

Update the event catalog and projection tests for that clarified lifecycle meaning. Event schema need not change because the existing confidence field truthfully carries the terminal initial state. If implementation discovers that consumers require durable distinction between “initial readiness complete” and “background warm terminal,” stop and amend this plan rather than overloading an unrelated event or adding an unbounded project-progress event stream.

Host-owned semantic query results retain current confidence and omission fields. Where demand preparation expands coverage, callers recapture and report the new current confidence. No result may claim full-solution completeness merely because its requested project subset compiled.

## 8. Project/File Changes

| Path | Expected work |
|---|---|
| `src/Threadsmith.DotNet/SemanticEngine.cs` | Split evaluation/readiness/warm phases, integrate one coordinator, atomically publish progressive coverage, and remove the sequential all-project startup loop. |
| `src/Threadsmith.DotNet/SemanticEngineRegistry.cs` | Route internal demand preparation through the existing workspace-owned engine where needed. |
| `src/Threadsmith.DotNet/AdvancedSemanticQueryService.cs` | Classify query coverage, demand exact candidate projects, recapture generation-fenced snapshots, and preserve omissions/completeness. |
| `src/Threadsmith.DotNet/SemanticMutationEngine.cs` | Ensure affected project coverage through the shared coordinator before semantic mutation work. |
| `src/Threadsmith.DotNet/SemanticRefreshCoordinator.cs` | Fence warming across incremental/full refresh and preserve single refresh authority. |
| `src/Threadsmith.Core/OperationalLimits.cs` or existing semantic internal limits owner | Add a validated internal/test seam for frontier and worker bounds only if the current `SemanticResourceLimits` location is the established owner. No public configuration key. |
| `src/Threadsmith.Interaction/Coordination/InteractionCoordinator.cs`, transcript/status projection | Preserve startup wait and show progressive confidence without duplicate completion output. |
| `src/Threadsmith.Cli/HeadlessShell.cs` | Preserve partial readiness/timeout behavior and avoid polling through conclusive failure. |
| `tests/Threadsmith.ModelTooling.Tests/` | Coordinator, prioritization, deduplication, progressive confidence, refresh, cancellation, failure, and measurement coverage. |
| `tests/Threadsmith.NativeTools.Tests/` | Demand-scoped advanced query/code-explore/generated-code coverage and honest omissions. |
| `tests/Threadsmith.Validation.Tests/`, `tests/Threadsmith.Mutations.Tests/` | Exact affected-project preparation and fail-closed diagnostic/mutation behavior. |
| `tests/Threadsmith.CoreRuntime.Tests/`, `tests/Threadsmith.SessionStatus.Tests/` | Startup, event/projection, headless/interactive, status promotion, and no-duplicate-output coverage. |
| `tests/Threadsmith.Architecture.Tests/` | Preserve dependency direction and prevent Roslyn/coordinator types crossing host boundaries. |
| `docs/architecture/semantic-confidence.md`, `event-catalog.md` | Clarify initial readiness versus background full warm while retaining enum/event schemas. |
| `docs/user-guide.md` | Document progressive readiness/status and direct-project selection guidance after implementation. |
| `docs/implementation-plans/acceptance-scenarios.md` | Update the existing semantic investigation/startup scenario only when implementation changes observable behavior. |
| `docs/implementation-plans/manual-test-plan.md` | Add or update an executable large-solution progressive-readiness case at implementation time. |
| `docs/implementation-plans/plan-118-staged-semantic-readiness-and-compilation-warming.md` | Record measured defaults, implementation, deviations, review findings, and completion evidence. |

## 9. Ordered Tasks

### P118-01 — Confirm the measured case

1. Require Plan 117 completion and extract its same-host evaluation/compilation/full-load measurements.
2. Confirm eager compilation is a material contributor on at least one representative multi-project solution.
3. Record the current time to `PartialCompilation`/`FullSemantic`, peak or sampled working set, workspace failures, generated documents, and first-query latency.
4. If MSBuild evaluation overwhelmingly dominates and the proposed staging cannot materially improve usable readiness, pause this plan and record that evidence rather than adding concurrency machinery without benefit.

### P118-02 — Inventory readiness consumers

1. Trace every `GetCompilationAsync` call and every capture of `_compiledProjects`/advanced snapshots.
2. Classify each caller as graph-only, project-scoped, affected-project-scoped, candidate-scoped, or global-completeness.
3. Record its present partial-confidence and omission behavior.
4. Identify calls that duplicate compilation preparation and calls that must remain independent semantic model retrieval after preparation.
5. Build a coverage matrix mapping each public/internal semantic operation to required project preparation and failure behavior.

### P118-03 — Introduce the single preparation coordinator

1. Add one generation-owned internal coordinator with bounded high/low priority queues and per-project single-flight tasks.
2. Add immutable validated internal resource limits for frontier size and worker count.
3. Route preparation through the existing non-cooperative cancellation wrapper.
4. Implement caller-wait, generation, and application cancellation scopes.
5. Add deterministic fake-project-operation tests for queueing, promotion, deduplication, priority, failure, obsolete results, and disposal before connecting real Roslyn work.

### P118-04 — Stage initial readiness

1. Split workspace evaluation/confinement from project compilation preparation.
2. Install evaluated state and its coordinator atomically without admitting compiler-dependent consumers prematurely.
3. Compute the direct-project or dependency-centrality frontier deterministically.
4. Return the initial load on first successful compilation or conclusive frontier/all-project failure.
5. Publish exactly one load completion and honest confidence, then acknowledge the initial publication barrier.
6. Start lower-priority warming of remaining projects through the same coordinator; abort it when publication failed or the binding was superseded.

### P118-05 — Add demand preparation to semantic consumers

1. Implement the internal ensure-projects operation and mandatory snapshot recapture.
2. Migrate exact path/project and affected-project consumers first.
3. Migrate semantic mutation and validation paths, retaining fail-closed coverage.
4. Migrate code explore to prepare bounded candidate projects after cheap candidate discovery.
5. Migrate global and symbol-identity operations with explicit completeness rules.
6. Remove direct duplicate readiness calls only after the shared path covers the same correctness behavior.

### P118-06 — Integrate refresh and lifecycle

1. Fence coordinators by solution generation across incremental updates and full reloads.
2. Preserve unaffected proven coverage only where supported; reprepare affected compiled projects before refresh publication.
3. Keep manual refresh terminal semantics explicit.
4. Join/cancel warming during repository rebind, `/new`, shutdown, and disposal.
5. Verify no obsolete completion changes confidence, inventory, events, or projections.

### P118-07 — Update interactive/headless projection

1. Keep the startup splash until usable or conclusively degraded initial readiness.
2. Close startup at partial readiness and update retained status on later full promotion.
3. Preserve headless partial threshold, timeout, exit behavior, and single-output contracts.
4. Ensure demand compilation is visible as part of the invoking tool/semantic activity rather than silent unbounded work.
5. Prevent per-project transcript noise and duplicate load completion.

### P118-08 — Measure and tune bounded defaults

1. Run staged single-worker and candidate bounded-worker modes through the production measurement harness.
2. Select frontier/worker defaults from measured readiness, full warm, memory, CPU, generator, and first-query evidence.
3. Test the selected default on Windows, Linux, and macOS with the same fixtures.
4. Lock production defaults and exact-boundary tests; keep large synthetic allocations out of ordinary unit tests.
5. Remove benchmark-only switches/alternate paths before completion.

### P118-09 — Documentation and final review

1. Update semantic confidence/event architecture wording, user guidance, acceptance behavior, and manual verification.
2. Run focused, full, repeated concurrency, and real-boundary suites.
3. Adversarially trace startup, queries, refresh, validation, mutation, child-agent sharing, cancellation, status, and shutdown.
4. Verify the original sequential competing preparation path is removed rather than wrapped.
5. Record baseline/target measurements, chosen bounds, unassessed environments, findings, and completion in this plan.

## 10. Testing

### 10.1 Deterministic coordinator tests

Use controllable compilation-operation fakes to prove:

- one task per project/generation despite initial, background, and multiple demand callers;
- demand promotion moves ahead of queued warm work;
- running work never exceeds the configured bound;
- one cancelled waiter does not cancel shared work;
- reload/disposal cancels generation work and discards late completion;
- failures are terminal for the generation and do not spin;
- first success completes initial usable readiness exactly once;
- all failure completes degraded initial readiness exactly once;
- background success cannot publish before the initial confidence/completion pair is acknowledged;
- initial publication failure or supersession aborts later warming/publication for that generation;
- partial-to-full confidence emits one change per enum transition; and
- worker/queue disposal is signal-driven without sleeps or polling as the assertion mechanism.

### 10.2 Real Roslyn/MSBuild integration

Exercise production `MSBuildWorkspace` with:

- the checked-in small semantic solution;
- direct `Threadsmith.App.csproj` selection;
- full `src/Threadsmith.sln` selection;
- independent projects that can overlap;
- a dependency diamond that proves shared dependency reuse;
- multi-target, linked, analyzer, and generator projects;
- one broken project alongside usable siblings;
- complete initial failure;
- incremental source refresh during background warm;
- full graph refresh during background warm; and
- cancellation/disposal while Roslyn work is non-cooperative.

Assert project coverage, per-project/aggregate confidence, generated documents, symbols, diagnostics, omissions, event ordering, workspace disposal, and absence of late obsolete publication.

### 10.3 Consumer regressions

Run focused coverage for:

- inventory at graph/partial/full confidence;
- symbol, reference, implementation, call hierarchy, impact, pattern, generated-code, and code-explore queries;
- exact path and natural-language candidate preparation;
- global “not found” completeness;
- source drift and generation retry/omission behavior;
- pre-mutation analysis and semantic mutation;
- semantic-only baseline/post-mutation diagnostics;
- external incremental/full/manual refresh;
- parent and delegated read-only runs sharing one workspace; and
- interactive/headless startup and status projection.

Tests must verify the underlying operation and prepared project set, not only displayed labels.

### 10.4 Performance matrix

Use Plan 117's environment-recording method and production path. Measure at least:

| Fixture | Required comparisons |
|---|---|
| Small semantic solution | Ensure staging overhead does not dominate small repositories. |
| Direct app project | Compare authoritative narrow selection, partial readiness, full closure warm, and first query. |
| Full Threadsmith solution | Compare time to usable partial, time to full, memory/CPU, and representative queries. |
| Generator-heavy multi-project fixture | Compare generator correctness, overlap, memory, and warm completion. |
| Broken mixed solution | Compare time to honest partial/degraded state and no-spin behavior. |

Record repetitions, medians, ranges, SDK/Roslyn/MSBuild versions, project counts, worker/frontier values, confidence transitions, queue metrics, and machine details. Separate cold process/SDK-host startup from warm runs.

Acceptance requires a material, repeatable reduction in time to usable readiness for the representative large solution, with no unexplained material regression in full warm time, first meaningful semantic query, generator correctness, or peak memory. Record the actual numbers rather than encoding an arbitrary universal percentage in tests.

### 10.5 Build and suite gates

Run affected projects independently, then the repository gate:

```powershell
dotnet restore src/Threadsmith.sln
dotnet build src/Threadsmith.sln --configuration Debug --no-restore
dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4
```

Run concurrency/cancellation-sensitive affected suites three consecutive times. Do not use retries to mask a race. Preserve architecture and release/publish tests because workspace/build-host payload changes can affect packaging even when package versions do not change.

### 10.6 Manual/observable verification

On a representative large trusted solution:

1. launch interactive Threadsmith and record startup time to composer availability plus displayed partial confidence;
2. observe later promotion to full confidence without duplicate startup/completion output;
3. immediately submit a project-scoped code question and verify demand preparation, tool visibility, cancellation, and correct evidence;
4. immediately submit a global query and verify it waits or discloses omissions according to its completeness contract;
5. edit a source file during warming, then force `/semantic_refresh` and verify freshness/generation correctness;
6. repeat headless with a request, cancellation, a broken project, and direct-project selection; and
7. exit during warming and verify prompt teardown with no hang or late output.

Update the manual test catalog with stable steps/expected results when implementation begins; do not put benchmark-specific machine numbers into the durable procedure.

### 10.7 Adversarial review

- Trace every former startup `GetCompilationAsync` call and prove there is one shared preparation owner.
- Challenge whether “partial” results are truly complete for their declared project scope.
- Verify “not found” never arises solely from a still-warming omitted project without disclosure or demand.
- Check parent/child/query/refresh overlap for duplicated compilation and invisible work.
- Check worst-case solution sizes, generator cost, queue bounds, memory retention, and time to first visible activity.
- Verify full refresh, manual recovery, mutation, and validation do not inherit a weakened startup shortcut.
- Verify cancellation hierarchy and workspace disposal under abandoned non-cooperative work.
- Re-review fixes through interactive, headless, tool, internal validation, and delegated entry points.

## 11. Security and Permissions

Staging does not alter trust. MSBuild evaluation and compiler-backed preparation remain available only under the established `TrustedBuild` boundary. `TrustedRead` continues to load text/project metadata without executing MSBuild.

All evaluated projects, documents, analyzer configuration, references, generated sources, and project paths remain confined by existing repository/prohibited-path policy before they enter shared semantic state. Background work receives no broader filesystem, process, network, package-source, or mutation authority than the existing semantic load.

Demand preparation is not approval authority. It only prepares compiler state needed by an already-admitted read/validation/mutation path. Mutation, build, test, process, MCP, extension, and model policy remain unchanged.

Logs/events/metrics contain bounded counts, durations, confidence, reasons, and sanitized project identity where already permitted. They contain no source text, compiler command line, environment, secrets, analyzer payload, arbitrary exception dump, or Roslyn object.

## 12. Observability

Extend Plan 117's semantic-load measurements rather than creating a competing telemetry system. Required structured observations include:

- evaluated, usable, and warm milestone durations;
- readiness frontier size/attempts/failures;
- current worker limit and maximum observed concurrency;
- high/low queue counts at bounded sampling points;
- demand promotions and joined duplicate count;
- per-reason aggregate demand wait/preparation duration;
- succeeded/failed/obsolete project counts;
- confidence transitions and final warm outcome;
- cancellation owner (`waiter`, `generation`, or `application`); and
- discarded late result count.

Do not emit a durable event per project or per queue transition. Existing semantic lifecycle events remain authoritative. Use metrics and bounded structured logs for internal detail, with correlation to workspace/session/generation through host-owned IDs.

Activity presentation must remain truthful: startup covers initial usable readiness; a tool covers demand preparation it awaits; manual refresh covers its complete operation; passive background warming normally uses retained status/telemetry rather than transcript spam.

## 13. Migration and Compatibility

There is no persisted-state, repository-configuration, or extension migration. Existing sessions restore semantic confidence from durable history as before and establish fresh in-memory preparation state when a repository is rebound.

The lifecycle interpretation of `SemanticLoadCompleted` is clarified but its schema remains compatible. Old persisted events still mean that the then-current implementation reached its terminal initial load; new events may carry `PartialCompilation` more commonly because remaining coverage is warmed afterward. Projection code already accepts all defined confidence values.

Rollback removes the coordinator/staged path and restores the sequential all-project preparation loop as one coherent change. Do not leave background warming active beside the old loop. Preserve Plan 117/118 measurements so rollback cause remains reviewable.

Direct-project selection remains compatible and becomes the documented fastest authoritative narrow scope. No existing selected solution is rewritten automatically.

## 14. Acceptance Criteria

1. Initial MSBuild evaluation occurs once per binding through the existing workspace path.
2. Initial usable readiness no longer waits for every project when at least one deterministic frontier project compiles successfully.
3. `SemanticLoadCompleted` is emitted exactly once at honest usable partial/full readiness or conclusive degraded failure.
4. Initial confidence and load completion are published before any background promotion; publication failure/supersession prevents that generation from warming or publishing later state.
5. Remaining projects warm through one bounded lower-priority coordinator and promote confidence through existing events.
6. One project/generation compilation preparation executes at most once across startup, background, tools, validation, mutation, and refresh callers.
7. Direct-project selection prioritizes the selected project; solution selection uses deterministic graph-based ranking without test/name/path exclusion heuristics.
8. Production frontier and worker bounds are conservative, measurement-backed, immutable at runtime, and directly tested.
9. Demand work outranks queued background work and cannot be starved by an unbounded running set.
10. A cancelled caller stops waiting without cancelling shared work needed by others; reload/disposal cancels and fences the generation.
11. Late obsolete completion cannot alter solution, inventory, confidence, events, projections, or diagnostics.
12. Per-project and aggregate confidence remain truthful; `FullSemantic` still requires every expected/loaded project and no confidence-reducing workspace failure.
13. Project/path/affected-project consumers prepare exact required coverage and recapture their snapshot before semantic work.
14. Global “not found” and completeness claims cannot ignore still-warming projects without explicit permitted omissions.
15. Code explore uses bounded candidate-driven preparation and preserves source, flow, ranking, generation, and omission contracts.
16. Generated-source inventory never claims completeness before relevant generators have run successfully.
17. Pre-mutation, semantic mutation, baseline, post-mutation, and introduced-diagnostic paths retain required affected-project coverage and fail closed when it is unavailable.
18. Incremental/full/manual refresh preserves Plan 97 dirty/applied fencing and explicitly documented terminal semantics.
19. Interactive composer availability advances at partial readiness; later full promotion updates status without duplicate startup/completion output.
20. Headless request admission retains its partial threshold, timeout, cancellation, exit, and single-output behavior.
21. Background warming shuts down deterministically without hanging, leaking a workspace, or publishing late output.
22. Same-host measurements show a repeatable material reduction in large-solution time to usable readiness.
23. Full warm time, first meaningful query latency, generator correctness, CPU, and memory have no unexplained material regression.
24. Small/direct/generator-heavy/broken fixtures and Windows/Linux/macOS coverage support the chosen bounds; unassessed environments are recorded.
25. Focused suites, full solution build/tests, repeated concurrency tests, architecture gates, and relevant publish tests pass.
26. Semantic confidence/event architecture docs, user guidance, acceptance behavior, manual procedure, and this plan reflect implemented behavior without rewriting frozen historical plans.
27. Final adversarial review finds no competing loader/preparation path, duplicate work, silent omission, confidence inflation, cancellation race, or disproportionate preload.

## 15. Risks

| Risk | Mitigation |
|---|---|
| Startup becomes fast but first useful query pays the entire old cost | Demand exact candidate projects; measure first-query shapes; never default all tools to full warm. |
| Partial “not found” is mistaken for complete | Encode per-consumer coverage rules; demand remaining scope or disclose omissions before authoritative absence. |
| Parallel compilation multiplies memory/generator work | Conservative measured bound, one task per project/generation, no processor-count fan-out, memory/CPU gates. |
| Background work competes with active tools | Separate demand/warm priorities, bounded running set, integrate existing semantic concurrency ownership. |
| A waiter cancels work needed by another caller | Separate waiter cancellation from generation/application lifetime. |
| Reload publishes old compilation results | Coordinator generation identity plus atomic publish fence and obsolete-result tests. |
| Progressive coverage races snapshot consumers | Publish immutable copied sets/inventory; require post-demand snapshot recapture. |
| Failed project spins forever | Terminal failure per generation; retry only on new/full/manual generation. |
| Direct and solution selection diverge into separate engines | One coordinator and load path; only readiness ranking differs by authoritative selection type. |
| Test/project heuristics hide coverage | Use graph ranking only; never exclude selected projects from warm/full confidence. |
| Manual refresh semantics weaken accidentally | Specify and test manual/full refresh terminal behavior independently from startup. |
| Shutdown hangs on non-cooperative Roslyn work | Preserve abandon-and-discard backstop, generation discard, bounded worker join, single workspace disposal. |
| Per-project telemetry becomes noisy or sensitive | Aggregate metrics, bounded samples, sanitized identity, no durable per-project event stream. |
| Complexity exceeds measured benefit | P118-01 stop gate; require material measured readiness improvement before completion. |

## 16. Documentation

When implementation changes observable behavior:

- update `docs/architecture/semantic-confidence.md` with evaluated/usable/warm lifecycle and unchanged confidence meanings;
- update `docs/architecture/event-catalog.md` with clarified single initial completion and later confidence promotion;
- update `docs/user-guide.md` for progressive startup status, possible partial confidence, demand waits, and direct-project selection;
- update the existing semantic investigation/responsive-startup acceptance scenario rather than adding plan bookkeeping to it;
- add or update one stable manual large-solution progressive-readiness procedure;
- document any new internal resource default in the owning resource-limit reference only if it is operator-visible; and
- record the measured baseline, target, chosen bounds, deviations, review, and completion evidence in this plan.

Do not update completed milestone details or historical plans. Plan 117 retains the upgrade baseline it actually measured.

## 17. Open Decisions

These are measurement-resolved implementation decisions, not permission to broaden scope:

1. What initial frontier size and maximum worker count provide the best reviewed readiness/full-warm/memory balance? Default candidates are one or two, subject to Plan 117/118 evidence.
2. Which existing concurrency owner should admit semantic preparation so combined background and foreground semantic work remains bounded without a second competing semaphore?
3. Which global semantic operations can lawfully return partial results with existing omission contracts, and which must await full required coverage?
4. Can incremental solution replacement safely retain unaffected proven coverage under the exact Roslyn 5.9 behavior, or must the coordinator conservatively reprove more projects?
5. Must explicit manual `/semantic_refresh` await full warm to preserve its current operator meaning? The default decision is yes unless current contract tracing proves terminal partial refresh is already accepted.
6. Does progressive project inventory need a frontend-neutral retained status detail beyond aggregate confidence? The default decision is no per-project UI stream.
