# Plan 57 — Parallelized Tool Support

**Milestone:** M21 — Parallelized Tool Support

**Prerequisites:** plans 08, 16, 19, 27, 37–38, 40–44, 49, and 51–56

**Depends on by:** future distributed tool workers, adaptive scheduling, and cross-run resource arbitration

**Status:** Implementation complete; maintained stress and real-adapter closeout pending.

## 1 Objective

Execute independent model-requested tool calls with real bounded overlap while preserving Threadsmith's host authority, policy, approval, budget, cancellation, provenance, activity, extension/MCP lifecycle, and deterministic model-continuation contracts.

A model response may contain multiple sibling tool calls. The host collects and validates the complete sibling set, derives each invocation's actual effect and resource claims from trusted host-owned metadata plus validated arguments, builds a deterministic conflict graph, partitions the calls into safe execution waves, and concurrently starts every eligible call in a wave. Results are joined and appended to the model continuation in original call order regardless of completion order.

This milestone adopts Option 2: closed host-owned effect metadata and conflict analysis for every tool. It does not rely on a hard-coded read-only allowlist, tool names, model assertions, or asynchronous APIs alone.

**Real parallelism is mandatory.** Automated tests must prove that at least two independent blocking tool bodies are simultaneously in flight and that elapsed batch duration reflects overlap. Sequential `await`, deferred enumeration, interleaving on one unfinished operation, or wrapping serial work in `Task.Run` without concurrent sibling execution does not satisfy this plan.

## 2 Architectural Context

`ToolDefinition` currently exposes category, side effect, idempotency, cancellation, timeout, approval, and output bounds. `ITool` derives concrete paths, secrets, executables, and network hosts after input validation. `ToolInvocationPipeline.InvokeAsync` independently owns registration identity validation, policy, hooks, approval, budget accrual, timeout, sanitization, events, activity, and result bounds. `SessionApplication` consumes streamed tool requests and awaits each invocation before accepting the next, so sibling requests are executed sequentially.

Plan 38 already establishes bounded in-process `Task` parallelism, hierarchical cancellation, category limiters, and structured joins for agents. Plan 49 owns truthful overlapping activity timing. Plans 51–55 require tool continuations to remain chronological and byte-stable. Plans 16 and 19 require leases and drain safety for extension and MCP capabilities. Plan 40 hooks can block invocations and must not introduce nondeterministic approval or policy behavior.

Static `ReadOnly` metadata is insufficient by itself. Two reads can conflict through exclusive compiler/workspace state, a non-thread-safe adapter, shared process/session handles, rate-limited remote services, or source-specific serialization. Conversely, reads of disjoint repository paths may safely overlap. The host therefore needs validated invocation-specific resource claims and conservative source capabilities.

## 3 Scope

- Add a closed versioned effect model covering access mode, resource domain, normalized resource identity, scope, isolation, concurrency safety, and source constraints.
- Require every built-in, MCP, extension, skill-adapted, hook-mediated, validation, scripting, and dynamically imported tool registration to project trusted scheduling metadata.
- Perform a complete implementation-time audit of every tool available from every current composition path; configure and justify each tool's access modes, resource kinds, concurrency mode, implicit resources, claim resolver, source/registration limits, approval interaction, and conservative fallback.
- Maintain a machine-verifiable registration coverage manifest generated or checked against the effective tool catalogs so adding or exposing a tool without an explicit reviewed scheduling classification fails tests/build rather than silently escaping the audit.
- Derive invocation-specific claims only after registration identity and arguments are validated, using host-confined canonical paths/hosts/solution identities rather than raw model strings.
- Build a deterministic conflict graph and stable execution waves for all sibling tool calls in one completed model response.
- Execute conflict-free wave members with actual overlapping tasks under global, category, source, session, and configured concurrency limits.
- Preserve per-invocation policy, approval, hooks, budgets, timeout, cancellation, sanitization, output bounds, events, provenance, and activity.
- Join results in original model tool-call order and append one canonical continuation after the entire accepted batch reaches a terminal state.
- Default unknown, incomplete, malformed, dynamically changing, non-thread-safe, approval-interactive, or exclusive claims to serialized execution.
- Add batch-level cancellation/failure policy, diagnostics, telemetry, configuration, `/tools` projection, focused deterministic tests, Scenario W, ADR-43, and maintained load/real-adapter verification.

## 4 Non-Scope

- No parallel mutation application, approval prompts, planning transitions, validation acceptance gates, or model turns.
- No model-controlled concurrency count, resource locks, dependency graph, scheduling priority, or claim override.
- No inference of safety from tool ID, description, category, `ReadOnly`, `Idempotent`, or model-supplied annotations alone.
- No speculative execution before the model response and complete sibling call set are known.
- No execution of a tool whose availability, phase legality, policy, registration generation, or arguments fail validation.
- No process-per-tool or distributed worker requirement; parallelism is bounded in-process task concurrency over existing adapters and child processes where those tools already use them.
- No change to Plan-37 mutation authority, Plan-38 worker isolation, Plan-40 blocking authority, or Plan-51–55 canonical continuation semantics.
- No promise that every multi-tool response runs concurrently. Conflicting or unclassified calls remain deterministic and sequential.

## 5 Current State

The model protocol can represent more than one tool request in a response, and the execution loop can collect multiple results for a continuation. Today each streamed request immediately awaits `_toolPipeline.InvokeAsync(...)`, so tool bodies never overlap within that model response.

Existing metadata supports legality and broad side-effect policy but cannot prove concurrency safety. `GetResourcePaths`, `GetSecretReferences`, `GetExecutable`, and `GetNetworkHosts` expose useful validated inputs, yet there is no normalized access mode, shared/exclusive resource claim, adapter thread-safety declaration, stable conflict planner, batch limiter, or deterministic concurrent join.

## 6 Proposed Design

### 6.1 Closed effect and concurrency contracts

Add host-owned versioned contracts equivalent to:

- `ToolAccessMode`: `Read`, `Write`, `Execute`, `ExternalEffect`, `Exclusive`;
- `ToolResourceKind`: `Repository`, `Path`, `GitStore`, `Solution`, `SemanticWorkspace`, `ProcessPool`, `NetworkHost`, `McpServer`, `ExtensionGeneration`, `SecretScope`, `SessionState`, `Global`;
- `ToolResourceClaim(ResourceKind, CanonicalIdentity, AccessMode, Scope)`;
- `ToolConcurrencyMode`: `ParallelSafe`, `SerializedPerResource`, `SerializedPerRegistration`, `SerializedPerSource`, `ExclusiveSession`, `ExclusiveGlobal`;
- `ToolSchedulingDescriptor` with schema version, default mode, maximum source concurrency, approval-interaction behavior, and claim resolver identity/version;
- `ToolInvocationSchedulingPlan` containing validated registration identity, original ordinal/correlation ID, normalized claims, effective limit keys, and conservative fallback reason;
- `ToolBatchPlan` containing deterministic waves and conflict rationale without arguments or secret values.

The closed enum set is host policy. Extensions and MCP imports may declare only values supported by their stable contracts; declarations are untrusted capability input until the host validates and narrows them. A host adapter may always make a declaration more restrictive, never less restrictive.

`ToolSideEffect` remains user/policy-facing behavior metadata. Scheduling claims are separate and cannot weaken approval, trust, phase legality, or side-effect classification.

#### 6.1.1 Exhaustive current-tool audit and configuration

Implementation must enumerate the effective registrations produced by every current App/test composition path, not merely the `Threadsmith.Tools` project. The audit includes canonical native tools, repository/Git/package/.NET/semantic/validation tools, `datetime`, `csharp_script`, web search, lifecycle/mutation proposal tools where projected, extension capabilities, MCP imports, governed skill mappings, hook-mediated adapters, and any provider/authentication or diagnostic capability represented through the shared tool pipeline.

For each current tool or closed dynamic-source class, record and implement:

- stable tool/source identity and owning subsystem;
- all direct and implicit resources touched, including mutable caches, registries, projections, workspaces, processes, connections, leases, rate limits, and external services;
- access mode per resource and hierarchy/alias rules;
- adapter and dependency thread-safety evidence from local source/tests or official upstream contracts;
- effective `ToolConcurrencyMode`, hard/source maximum, approval behavior, cancellation/drain behavior, and claim-resolver identity/version;
- whether parallel execution is enabled, restricted by resource/source, or intentionally serialized, with a concise safety justification;
- representative argument shapes that produce different claims, such as same file versus disjoint files, same MCP server versus distinct servers, or same semantic workspace generation;
- focused tests proving both allowed overlap and prohibited overlap where applicable.

“Unknown therefore serialized” is a valid compatibility fallback for newly discovered third-party capabilities, but it is not a substitute for evaluating a current first-party tool. Every current first-party registration must have explicit reviewed metadata and a documented/tested decision, even when that decision is serialization. Dynamic MCP and extension registrations must be covered by explicit source-class defaults plus validation/narrowing rules.

The coverage check compares the effective composed tool catalogs with the reviewed scheduling manifest/descriptors. It fails on missing registrations, duplicate/stale manifest entries, unsupported descriptor versions, missing claim resolvers, or a first-party tool still using the generic unknown fallback. Tool aliases/canonicalization must resolve to one audited definition rather than creating coverage gaps.

### 6.2 Invocation-specific claim resolution

Split pipeline preparation from execution without duplicating policy authority:

1. Resolve and generation-fence the registration.
2. Deserialize and validate typed arguments.
3. Evaluate phase availability and host policy.
4. Resolve canonical resource claims from the validated typed input and current invocation context.
5. Validate scheduling descriptor and source capability.
6. Return an opaque prepared invocation owned by the pipeline; it contains no live approval grant and cannot execute twice.

Canonical identities use existing confinement and platform comparison rules. Repository paths resolve symlinks/reparse points as required by current policy before comparison. Resource identities are stable host keys, not logged raw paths, URLs, arguments, secrets, or model text.

Claims include implicit resources. Examples:

- a file read claims shared `Repository` plus `Read Path:<canonical-file>`;
- Git inspection claims `Read GitStore:<repository-identity>` and any narrower path;
- semantic inspection claims the loaded solution generation and either shared or serialized semantic-workspace access according to the service contract;
- a process tool claims the process pool and executable policy identity, and defaults to serialized when external effects cannot be bounded;
- MCP claims the profile/server generation plus declared remote concurrency mode;
- an extension claims its invocation lease/generation plus validated extension concurrency declaration;
- approvals claim `ExclusiveSession` because user prompts are not issued concurrently in M21.

A missing resolver, empty claims where resources are required, invalid canonicalization, changing registration, unsupported descriptor version, or resolver exception yields a conservative serialized plan or ordinary validation failure—never optimistic parallelism.

### 6.3 Deterministic conflict analysis

Collect the complete sibling call set from one model response before executing any tool. Reject duplicate call identities and apply existing duplicate name+arguments policy before scheduling.

Two invocations conflict when any of these holds:

- either is `ExclusiveGlobal` or both share an applicable global-exclusive key;
- either is `ExclusiveSession` in the same session;
- source/registration/resource serialization keys match;
- claims overlap and at least one access mode is `Write`, `Execute`, `ExternalEffect`, or `Exclusive`;
- resource hierarchy overlaps (repository/directory contains path, solution owns project, server owns remote resource) and either side is non-read;
- approval interaction would overlap;
- source capability, hook policy, or concurrency metadata is unknown or changes;
- configured global/category/source/session capacity is exhausted for the same wave.

Read/read claims overlap safely only when both descriptors are `ParallelSafe`, all involved adapters explicitly support concurrent invocation, and no stricter limiter applies.

Build the graph deterministically by original ordinal and stable resource keys. Produce stable waves using a documented greedy coloring/partition algorithm: consider calls in original order and place each in the earliest wave where it has no conflict and every limiter admits it. Configuration changes apply only at a batch boundary. The planner output for identical inputs and snapshots is byte-for-byte stable.

The model cannot force a wave. Optional future dependency hints are excluded.

### 6.4 Actual concurrent execution

For each wave with more than one member, create all invocation tasks before awaiting completion, using the existing in-process async/task model and bounded `SemaphoreSlim` limiters. Each tool body must be capable of being simultaneously active with siblings. `Task.WhenAll` or an equivalent structured join waits for terminal results while retaining each task's original ordinal.

The implementation must not:

- await one invocation before starting the next wave member;
- hold a shared pipeline lock around tool execution;
- serialize through the event stream, activity projection, output sanitizer, budget, hook coordinator, registry lease, or console gate;
- use `Task.Run` as proof of parallelism;
- release a limiter or extension/MCP lease before terminal result/event publication.

Per-invocation timeout starts when execution is admitted, not while queued behind a limiter. Batch wall-clock bounds include queue time and are separately enforced. Category/source limiters are acquired in one canonical order to avoid deadlock and released in reverse order.

### 6.5 Policy, hooks, approval, and budget

Preparation does not consume an approval or budget twice. The final execution boundary revalidates generation, policy snapshot identity, phase, and registration lease before starting.

Approval-bearing calls are placed in sequential waves in M21. Approval requests remain ordered by original ordinal. A denial produces that call's ordinary failure result and does not authorize or implicitly cancel unrelated calls unless configured fail-fast policy requires cancellation.

Before any wave starts, atomically reserve aggregate call-count and applicable resource budgets for admitted members. If the complete wave cannot be reserved, produce deterministic budget failures without partially starting it. Accrue actual elapsed/cost/output independently as existing contracts require. Concurrency does not multiply configured budgets or bypass per-session/per-run limits.

Before/after hooks remain correlated per invocation. Trusted blocking hooks run before the corresponding tool body and may make that invocation fail. Hook adapters with unknown concurrency safety serialize their invocation source; hook event ordering is by actual lifecycle time, while continuation ordering remains model ordinal.

### 6.6 Failure and cancellation semantics

Add a closed configured batch failure mode with conservative default `CompleteStarted`:

- `CompleteStarted`: a tool failure does not cancel independent already-started siblings; all terminal results are returned;
- optional `CancelBatchOnFailure`: first non-cancellation failure cancels linked sibling tokens, then drains every started invocation to a bounded terminal state before continuation.

User/run/session cancellation always links to every queued and active invocation. Queued calls never start after cancellation. Non-cooperative adapters use their existing drain/kill/abandon contracts; the batch cannot publish completion while a lease or process remains falsely active. If bounded drain fails, return an explicit indeterminate/timeout classification and block unsafe continuation where existing policy requires it.

An exception from one task must never make `Task.WhenAll` discard sibling results. Normalize each invocation into a terminal host-owned result, preserve cancellations distinctly, and generate the continuation only after the wave/batch join is complete.

### 6.7 Deterministic continuation and provenance

The batch scheduler may complete tools in any order, but the model-visible continuation is sorted by original sibling ordinal/correlation identity. Every tool result appears exactly once with its matching call ID. Result JSON, errors, truncation, provenance, and source identity retain existing sanitization and bounds.

Plan-51–55 frozen-prefix behavior remains unchanged: append the assistant's complete ordered tool-call message once, followed by correlated tool results in original request order. Completion order is operational telemetry only and must not alter canonical request bytes, evidence insertion ordering, duplicate detection, or replay.

No later model round starts until the entire accepted batch has reached a terminal joined state.

### 6.8 Dynamic sources: MCP, extensions, skills, and built-ins

- Built-ins receive explicit reviewed scheduling descriptors and invocation claim resolvers.
- MCP imports default to `SerializedPerSource` unless the trusted host adapter can derive a supported server concurrency capability and bounded resource claims. Remote read-only labeling alone is insufficient.
- Extensions default to `SerializedPerRegistration`. A stable SDK addition may permit a closed concurrency descriptor; runtime validates it, holds one invocation lease per concurrent call, respects generation drain, and caps concurrency independently of extension claims.
- Skill tools retain their mapped underlying tool claims. `invoke_skill` itself is workflow/session-stateful and remains serialized; sibling ordinary tools are not allowed to race its procedure turns.
- Executable/code/script/mutation-capable tools remain serialized unless a later contract proves isolation beyond M21 requirements.

Hot replacement, MCP disconnect, session transition, repository switch, model switch, and policy/config generation changes wait for or cancel/drain admitted batches through existing safe-boundary rules.

### 6.9 Configuration and projection

Add bounded layered host settings equivalent to:

- `tools:parallel:enabled` (default `true` after compatibility gates pass; implementation may ship default-off until those gates pass);
- `tools:parallel:maximumConcurrency` (small bounded default, capped by host hard maximum);
- per-category/source maxima that can only narrow tool-declared concurrency;
- `tools:parallel:failureMode` from the closed enum;
- optional batch wall-clock limit.

Repository configuration cannot broaden compiled/source hard limits or make unknown tools parallel-safe.

Extend `/tools` and headless inspection with effective concurrency mode and bounded reason (`parallel-safe`, `serialized-by-resource`, `serialized-by-source`, `approval`, `unknown-metadata`, or configured limit). Do not expose canonical resource identities, raw paths, arguments, hosts, secret scopes, or lock keys.

### 6.10 Activity and safe-boundary integration

Plan 49 activity supports multiple simultaneously active tool rows, each with independent monotonic elapsed time and source. The overall request duration remains one monotonic turn clock. UI updates are coalesced without holding the console gate across scheduler awaits.

Session transitions, repository/model switches, approved mutation boundaries, and shutdown observe the batch as active work until all admitted tasks drain and terminal events publish. One tool completing early does not make the turn or batch safe.

## 7 Public Contracts

Public contracts are closed host-owned immutable scheduling metadata, normalized claim DTOs where cross-subsystem projection is necessary, batch policy/configuration, and bounded inspection results. Prepared invocations and lock/limiter handles remain internal runtime objects and are non-serializable, single-use, and generation-fenced.

`ToolDefinition` gains a versioned scheduling descriptor or stable reference to one. `ITool` gains a host-invoked typed claim-resolution boundary after argument validation; compatibility helpers provide conservative serialized defaults so older built-ins and extensions do not become parallel by accident.

Extension abstractions receive only the minimum closed concurrency declaration required. MCP SDK types, extension implementation types, delegates, typed tool inputs, semaphores, tasks, cancellation sources, leases, canonical local paths, resolved secrets, and provider DTOs never enter public projections or durable state.

No new durable event is required for each planner edge. Existing invocation events retain actual temporal order and correlation. If a batch event is added, it contains only batch ID, call count, wave count, effective concurrency, timing, outcome, and schema version.

## 8 Project/File Changes

- `Threadsmith.Core` — closed batch failure/configuration/projection contracts and optional batch activity identity/event DTOs.
- `Threadsmith.Tools` — scheduling descriptors, validated claim resolution, preparation/execution split, conflict planner, limiter hierarchy, concurrent batch executor, deterministic join, and `/tools` state.
- `Threadsmith.Execution` — collect complete sibling tool calls, invoke one batch, preserve canonical continuation ordering, cancellation, budgets, duplicate detection, and safe-boundary state.
- `Threadsmith.Extensions.Abstractions` / `Threadsmith.Extensions.Runtime` — conservative concurrency declaration, validation, per-call leases, source/generation caps, draining/hot-replacement integration.
- `Threadsmith.Mcp` — source/server concurrency descriptors, conservative defaults, connection-generation leases, disconnect/drain behavior, and real-transport coverage.
- `Threadsmith.Skills` / `Threadsmith.Hooks` — explicit serialized workflow/approval behavior and adapter concurrency metadata.
- `Threadsmith.Tui` / `Threadsmith.Cli` — concurrent activity and bounded concurrency inspection; no scheduling authority.
- App composition/configuration — bounded global/category/source limiters and compatibility default.
- Deterministic fake tools/providers and focused Tools/Execution/MCP/Extensions/TUI/integration tests.
- ADR-43, Scenario W, plan/milestone/index/shared-context/status, operations/manual tests when implemented, and DOX.

Any new fixture copied to output uses `CopyToOutputDirectory=PreserveNewest`.

## 9 Ordered Tasks

1. Enumerate every effective tool registration from all current application composition paths and produce the reviewed scheduling manifest. For each first-party tool, inspect its implementation and dependencies, identify direct/implicit mutable resources and thread-safety evidence, configure explicit metadata and claim resolution, justify parallel/restricted/serialized behavior, and add overlap/conflict tests. Add a coverage gate that fails for missing, stale, duplicate, aliased-but-unresolved, or generic-fallback first-party entries.
2. Define closed versioned access/resource/concurrency/failure contracts, conservative compatibility defaults, and ADR-43.
3. Split invocation preparation from execution while keeping registration fencing, policy, approval, hooks, budget, timeout, sanitization, events, and result bounds single-owned.
4. Add typed invocation-specific claim resolution and canonicalization with repository confinement and no model-controlled resource identities.
5. Implement deterministic conflict detection, hierarchy overlap, stable wave construction, and explainable bounded reasons.
6. Add global/category/source/session/registration/resource limiters with canonical acquisition order, aggregate admission, and bounded queue/batch timing.
7. Implement true concurrent wave start and structured join, with per-invocation terminal normalization and original-ordinal result ordering.
8. Change `SessionApplication` to collect the complete sibling tool-call message, batch duplicate detection, execute once, and append one canonical ordered continuation.
9. Integrate batch cancellation/failure modes, atomic budget reservation, approval serialization, hook correlation, safe-boundary/drain, and non-cooperative adapter behavior.
10. Add explicit built-in descriptors/claims; default uncertain executable, mutation, script, workflow, and stateful tools to serialized.
11. Add MCP and extension closed concurrency declarations, host narrowing, leases/generation fences, disconnect/unload drain, and source caps.
12. Add configuration, `/tools`, headless inspection, overlapping activity projection, telemetry, and diagnostic-bundle redaction.
13. Add deterministic overlap, conflict, ordering, cancellation, budget, race, dynamic-source, load, and real-adapter tests plus Scenario W.
14. Update shared context, milestone/index/status, operations and manual tests when implementation completes, event catalog if needed, and every affected DOX file.

## 10 Testing

Automated tests must cover:

- **complete current-tool coverage:** enumerate effective catalogs from every production composition path and assert every current first-party tool/alias maps to exactly one explicit reviewed scheduling descriptor and claim resolver; reject missing, stale, duplicate, unsupported-version, or generic-unknown first-party classifications;
- table-driven metadata/claim tests for every current first-party tool, including representative argument-dependent resource identities, implicit shared resources, source caps, approval behavior, and documented parallel/restricted/serialized decision;
- paired allowed-overlap and prohibited-overlap tests for each tool family that can vary by resource, plus explicit serialization tests for every tool intentionally classified as non-parallel;
- **real overlap proof:** two barrier-controlled independent tool bodies both enter execution before either is released; peak active count is at least two; elapsed batch time is materially below serial sum using deterministic gates rather than timing alone;
- regression test proving the old sequential start/await loop would deadlock or fail the barrier test;
- 3+ independent calls respecting global/category/source caps and never exceeding measured peak concurrency;
- read/read same and disjoint resource behavior; read/write, write/write, execute, external-effect, session/global exclusive conflicts; ancestor/descendant path and solution/project overlap;
- deterministic graph/waves and continuation bytes across randomized completion order, scheduler interleavings, registration order, and repeated runs;
- complete sibling collection before any body starts and no next model call before joined terminal results;
- duplicate IDs and duplicate name+arguments rejection before scheduling;
- preparation validation, policy denial, registration-generation change, malformed claims, resolver failure, and unknown metadata falling back safely without executing unauthorized work;
- aggregate budget reservation with no partial wave start, per-call timeouts beginning at admission, batch timeout, queue cancellation, and no limiter leaks;
- `CompleteStarted` and `CancelBatchOnFailure`, including simultaneous faults, user cancellation, timeout, non-cooperative drain, and preservation of every terminal sibling result;
- approval-bearing calls never prompt concurrently and remain original-order deterministic;
- event/activity timing allowing overlapping intervals while model-visible results remain original-order; no console-gate deadlock or completed `THINKING` marker;
- MCP disconnect/reconnect, extension drain/hot replacement, generation fencing, lease counts, source caps, and conservative defaults for undeclared third-party capabilities;
- session/repository/model transition and shutdown cannot publish a safe boundary while a batch member remains active;
- no raw arguments, paths, lock keys, hosts, secrets, or outputs in planner diagnostics/telemetry;
- existing sequential single-tool, hook, policy, tool continuation, caching, and fake-model fixtures remain byte-compatible where semantics are unchanged;
- maintained stress run with randomized delays/failures and real stdio/HTTP MCP plus extension adapters proves bounded overlap and clean drain.

Tests that only assert tasks were created, methods returned incomplete tasks, or total duration changed are insufficient without simultaneous-in-body evidence.

## 11 Security/Permissions

Parallel scheduling never grants authority. Every invocation independently passes phase availability, trust, policy, consent, approval, path, secret, executable, network, registration-generation, and output sanitization gates. Batch admission cannot reuse one call's approval or policy decision for another.

Tool/extension/MCP concurrency declarations are untrusted capability metadata. The host validates, narrows, and defaults unknown values to serialization. Repository configuration can reduce concurrency only. Canonical claims are confined before comparison and never shown raw in UI/logs.

Concurrent reads must observe the same immutable turn baseline. No sibling sees another sibling's staged mutation or transient result unless a later model round requests it. Mutation-capable and external-effect calls remain serialized unless a future isolation contract explicitly changes this decision.

Avoid denial-of-service through hard concurrency caps, bounded sibling count, bounded queue and batch duration, aggregate budget reservation, source rate limits, bounded result memory, and drain backstops. Do not create one unbounded task, semaphore, timer, or event buffer per model-controlled value.

## 12 Observability

Record secret-free batch and per-invocation telemetry:

- batch/correlation ID, sibling count, wave count, admitted/serialized/rejected counts;
- configured/effective maximum and observed peak concurrency;
- queue, execution, join, and total batch duration from monotonic clocks;
- tool/source/category identifiers already safe for activity;
- bounded serialization reason category and failure/cancellation outcome;
- limiter saturation and drain timeout counts.

Do not log arguments, result bodies, canonical resource identities, raw paths/hosts, secret scopes, approval content, or extension/MCP payloads. Event temporal order may reflect real completion order; model continuation order remains separately deterministic and observable by ordinal only.

## 13 Migration/Compatibility

No SQLite migration is required unless batch audit persistence is introduced during implementation. Existing tool definitions and external capabilities load with conservative serialized scheduling defaults. Adding effect metadata changes canonical tool schema bytes only if the metadata is model-visible; the preferred design keeps scheduling metadata host-only so provider tool definitions and cache families remain stable.

Configuration absent or invalid uses a bounded conservative default. A compatibility switch can disable parallel batches and reproduce existing sequential behavior for diagnosis. Persisted sessions restore without scheduler state; queued/running tasks, limiters, claims, and leases are never durable.

Extensions built against older abstractions and MCP servers without validated concurrency capabilities remain usable sequentially. No source becomes parallel-safe solely through upgrade.

## 14 Acceptance Criteria

- One model response containing at least two independent eligible tool calls causes both tool bodies to be simultaneously active under a deterministic automated barrier test; this is actual parallel execution, not serial asynchronous awaiting.
- The host derives trusted invocation-specific claims, constructs a stable conflict graph/waves, and never relies on model declarations or a hard-coded tool-name allowlist for safety.
- Conflicting, approval-interactive, unknown, malformed, mutation-capable, and source-serialized calls do not overlap.
- Global, category, source, session, registration, and resource limits are bounded, deadlock-free, cancellation-safe, and measured never to exceed their caps.
- Every call retains independent policy, approval, hook, budget, timeout, sanitization, provenance, event, activity, and registration/lease enforcement.
- Random completion order cannot change model-visible result order, correlation, canonical continuation bytes, evidence insertion, or the next model request.
- Failure and cancellation drain all started work according to the closed policy, preserve terminal sibling outcomes, leak no permits/leases/processes, and never expose a false safe boundary.
- Every current first-party tool from every effective composition path has been individually evaluated and configured with explicit reviewed scheduling metadata, implicit-resource claims, resolver/version, limits, approval/drain behavior, safety justification, and representative tests; no first-party tool remains on the generic unknown fallback.
- A machine-verifiable catalog-to-metadata coverage gate fails for missing, stale, duplicate, unsupported-version, unresolved-alias, or unclassified current tools, so future tool additions cannot bypass concurrency review.
- MCP, extension, skill, hook, executable, semantic, Git, and built-in tool families have explicit reviewed source behavior; unknown future third-party registrations remain sequential until evaluated.
- `/tools`, headless inspection, telemetry, and diagnostic bundles show bounded effective concurrency truth without exposing sensitive claims.
- Focused automated coverage, architecture gates, Scenario W, maintained stress/real-adapter verification, documentation, ADR-43, status, and DOX pass.

## 15 Risks

- **Incorrect metadata causes races:** closed access/resource model, invocation-specific host resolution, source validation/narrowing, conservative defaults, and conflict matrix tests.
- **Parallelism is only nominal:** barrier-controlled simultaneous-body tests and measured peak concurrency are release gates.
- **Deadlocks across limiters:** canonical acquisition order, atomic wave admission where practical, no tool execution while acquiring additional scheduler locks, and drain tests.
- **Nondeterministic model context:** retain original ordinals and join all results before canonical continuation assembly.
- **Budget/approval races:** aggregate reservation and sequential approval waves with single-owner accounting.
- **Extension/MCP unload races:** one lease per invocation, generation fencing, bounded drain, and source caps.
- **Cancellation loses sibling outcomes:** terminal-result normalization and bounded structured join rather than exception-short-circuiting `WhenAll`.
- **Read-only tools mutate caches or shared services:** concurrency safety is explicit and separate from side-effect classification; unknown adapters serialize.
- **Resource exhaustion:** hard sibling/concurrency/output/time caps and bounded activity/event buffering.
- **Performance regression for small calls:** configurable threshold is not introduced initially; benchmark scheduler overhead and retain sequential compatibility switch.

## 16 Documentation

- Add ADR-43 for host-owned effect metadata, conflict planning, true bounded execution, and deterministic joins.
- Update tool/extension/MCP authoring guidance with closed scheduling descriptors, conservative compatibility, thread-safety obligations, resource claim rules, and examples.
- Update `docs/user-guide.md`, `/tools`, configuration, safety, and operations documentation only when implementation ships.
- Add Scenario W and, during implementation, maintained manual cases for visible overlap, caps, cancellation, MCP/extension drain, deterministic ordering, and compatibility disablement.
- Update shared context, milestones, plan index, root/docs/source/test DOX, and status references.

## 17 Open Decisions

Resolved for planning:

- M21 uses Option 2: host-owned effect metadata plus invocation-specific conflict analysis, not a tool-name allowlist.
- Parallelism is real simultaneous sibling execution on bounded in-process tasks; async method signatures alone do not qualify.
- The complete sibling tool-call set is collected before execution and joined before the next model round.
- Model-visible results remain original-order deterministic; operational events may retain actual timing order.
- Read/read is parallel only when claims and every involved adapter explicitly permit it.
- Approval-bearing calls, unknown sources, workflows, executable/code/mutation-capable tools, and unvalidated MCP/extensions serialize by default.
- Repository configuration may narrow but never broaden host/source concurrency limits.
- `CompleteStarted` is the conservative default failure policy; user cancellation always propagates to the whole batch.
- Scheduler state is transient and never persisted or replayed.
- Distributed execution, adaptive priority, speculative tools, model-authored dependency graphs, and mutation parallelism remain future work.
