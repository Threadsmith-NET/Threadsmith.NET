# T07 Deliver invocation-only Archeology

**Status:** Proposed; implementation not started or accepted.

**Delivery track:** Proposed Repository Intelligence and Archeology capability; milestone registration follows T01 acceptance.

**Prerequisites:** [T06](t06-bounded-model-interpretation.md) complete, including its post-acceptance tests and documentation.

**Requirements and parent acceptance outcomes:** OPT-03, AR-01, AR-07–11, NQ-01–05, RT-12; AC-17, AC-22–23.

**Sources:** [Parent requirements](threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

A useful one-off answer with no feature database, durable cache, checkpoint or investigation-run record; normal host retention continues. Stateless explicit invocation works without enabling recall.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

A complete explicit, invocation-only investigation through T03–T06. It must work with no baseline, feature database, canonical intelligence, durable packet cache or checkpoint.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [src/Threadsmith.Tools/ToolInvocationPipeline.cs](../../../src/Threadsmith.Tools/ToolInvocationPipeline.cs)
- [src/Threadsmith.Tools/ToolContracts.cs](../../../src/Threadsmith.Tools/ToolContracts.cs)
- [src/Threadsmith.Core/OperationActivityContracts.cs](../../../src/Threadsmith.Core/OperationActivityContracts.cs)
- [src/Threadsmith.Persistence/RetentionService.cs](../../../src/Threadsmith.Persistence/RetentionService.cs)
- [src/Threadsmith.Persistence/SqliteEventStore.MemoryRetention.cs](../../../src/Threadsmith.Persistence/SqliteEventStore.MemoryRetention.cs)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Register the investigate action only after its dependent collectors and interpreter are complete. Require a concrete question/scope and capture limits/provider/retention mode before beginning.

2. Resolve invocation-only authority without resolving any persistent feature store factory. Use the same collection and interpretation components that later persistent runs will call.

3. Allocate an invocation-bound evidence registry with bounded lifetime and size. Supply its evidence IDs/allowlist to T06's internal interpreter. Implement live inspection through T06's structured expansion request → host validation → T05 governed evidence resolution → interpreter continuation, all before the investigate tool's ExecuteAsync returns and under the same scope, collection mode, activity correlation and cumulative budget. Do not extend retention merely to support a later tool call.

4. For ordinary model-driven and manual callers, return bounded answer citations/excerpts and pinned source locators in the completed tool result. The caller receives this result after execution; live drill-down is the internal exchange in step 3, not a separate caller-issued inspection action during ExecuteAsync. Do not assume a caller-interactive tool channel or invent an unlogged long-lived investigation session to keep IDs alive. A later authorized source-locator read is a new read, not inspection of the expired packet.

5. Produce an answer or explicit inability to conclude, with then/current applicability distinguished and all material gaps and conflicts visible. Proposed intelligence is a result only; persistence-disabled mode cannot promote it.

6. Use ordinary sanitized requests, results, events and permitted artifacts. Do not create a durable feature run record, evidence index or restart checkpoint under a different filename.

7. Release in-memory packets and tracked scratch in finally/disposal through existing lifecycle facilities. Cancelled/failed work has the same cleanup guarantee; restart cleanup treats orphaned scratch as disposable through shared cleanup infrastructure rather than opening a feature cache.

8. On replay, show only what the normal session retained. Expired evidence IDs return an explicit lifetime result; new source reads resolve a pinned locator if available, and retry starts a new operation identity.

## 7 Public Contracts and State Boundaries

The live invocation owns bounded evidence IDs and any scratch resources. Its internal interpreter can inspect them only through T06's host-validated expansion exchange. Return useful answer/citations, pinned source locators, supporting/conflicting evidence, limits and proposed findings through normal host results. Feature packet inspection expires at completion/cancellation/restart; returned IDs do not promise a subsequent live inspection call. A retained citation can support a new explicit source read subject to ordinary authorization, never implicit restoration of the original packet. Top-level Stateless access is explicit current-run evidence; children remain denied until T15.

## 8 Project and File Changes

**Permitted host touch points:** H3/H10 registration only if not supplied by T03. Use T04–T06 unchanged with invocation-only retention and ordinary durable host events. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Search all disk-write paths, including artifacts and logs, for feature-owned packets disguised as host artifacts. Trace a normal investigate tool call through T06 expansion and T05 resolution before ExecuteAsync returns; calling a registry directly is insufficient evidence of reachable live inspection. Check whether live evidence IDs outlive the tool invocation or whether transient cleanup secretly requires feature startup scanning.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| No feature persistence | Run investigate from a repository without feature data. | Answer is useful and no feature database/cache/run/checkpoint is created or opened. | Integration |
| Live/expired IDs | Invoke investigate through the ordinary tool pipeline with a controlled internal interpretation response requesting detail for an allowed evidence ID; after return, attempt packet inspection. | T06 validates and resolves detail through T05 before ExecuteAsync returns, with ordinary activity and cumulative budget accounting; later inspection reports expired scope with source locators preserved. No separate caller-interactive channel is required. | Integration |
| Cleanup paths | Complete, fail and cancel at collection and inference boundaries. | Packets and scratch are released without suppressing ordinary terminal events. | Integration |
| Restart replay | Interrupt the host with scratch present, then replay retained session output. | Orphan scratch is discarded; no resume/full-packet promise is made. | Integration |
| Pinned reread | Request a cited source after restart with the revision present or missing. | New authorized read resolves the revision or explicitly reports unavailability. | Integration |
| Isolated modes | Invoke from top-level Stateless and attempt a delegated invocation. | Stateless result is current-run evidence only; the child is denied. | Integration |
| Retry identity | Retry a cancelled investigation. | A new run is created with new budget/admission, not checkpoint resume. | Unit |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [ ] One-off investigation produces a bounded evidence-backed answer without baseline or persistent activation.
- [ ] No feature-owned durable state is created; ordinary host session retention remains intact and sanitized.
- [ ] Live drill-down resolves invocation-scoped IDs through T06's validated internal expansion path before the normal tool call returns; expired IDs cannot masquerade as resumed packets, and subsequent source reads remain new authorized operations.
- [ ] Cancellation, failure, completion and restart release/discard transient feature resources.
- [ ] Stateless explicit use does not activate automatic recall, maintenance or durable intelligence.
- [ ] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [ ] The user has explicitly accepted that reviewed implementation before any task test or documentation implementation begins.
- [ ] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

## 15 Risks

Ordinary session retention and feature persistence are different contracts. Asynchronous-looking evidence IDs can accidentally promise durable inspection that cannot be fulfilled.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** One-off usage, host retention versus feature persistence, inspection lifetime and restart behavior. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Choose bounded expansion detail/range defaults within T06's specified internal exchange; the live inspection entry point is settled, not deferred to a new caller-interactive channel. Never create new session machinery merely to keep a packet available. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
