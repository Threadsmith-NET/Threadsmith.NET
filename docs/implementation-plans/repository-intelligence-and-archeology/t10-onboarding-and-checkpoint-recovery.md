# T10 Deliver persistent onboarding and checkpoint recovery

**Status:** Proposed; implementation not started or accepted.

**Delivery track:** Proposed Repository Intelligence and Archeology capability; milestone registration follows T01 acceptance.

**Prerequisites:** [T09](t09-validation-and-reconciliation.md) complete, including its post-acceptance tests and documentation.

**Requirements and parent acceptance outcomes:** OPT-06, ON-01–08, AR-06, NQ-02–03, MT-07; AC-01–02, AC-13, AC-16, AC-25.

**Sources:** [Parent requirements](threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

Current-state baseline with profile, justified items, uncertainties, overview and maintenance anchor; automation remains off. Resume uses the same unit executor and publication path.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

Explicit persistent current-state/historically enriched onboarding and retained investigations using the already implemented flow, with progressive publication and explicit pinned resume.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [src/Threadsmith.Core/RepositoryInventoryContracts.cs](../../../src/Threadsmith.Core/RepositoryInventoryContracts.cs)
- [src/Threadsmith.Core/OperationActivityContracts.cs](../../../src/Threadsmith.Core/OperationActivityContracts.cs)
- [src/Threadsmith.Tools/ToolInvocationPipeline.cs](../../../src/Threadsmith.Tools/ToolInvocationPipeline.cs)
- [src/Threadsmith.Persistence/RetentionService.cs](../../../src/Threadsmith.Persistence/RetentionService.cs)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Validate persistent activation and show the selected scope/history/provider/resource plan. Capture one target through T04; do not enable automatic recall or maintenance.

2. Build an ordered bounded worklist of current-state profile units. Add T05 historical episodes only if history was explicitly selected; omitted history is a coverage limitation, not failure.

3. Execute current-state units through T05's current-snapshot-only packet mode and T06, with no required episodes or history discovery. Historical units use T05's history-enabled mode only when selected in step 2. Publish both through T09/T08. Carry each unit's collection mode into checkpoints and validated expansions; resume cannot silently enable history. Use the same components for retained targeted investigations and onboarding, with no second orchestration engine.

4. Record discovery coverage separately from completed inference and publication coverage. A successful unit can expose justified results before later units finish; unsupported findings need not create an item.

5. Commit a unit's publication and completion marker coherently. If crash ordering cannot be atomic across stores, use idempotent recovery that verifies publication rather than blindly repeating it.

6. Retain bounded resumable work descriptors for persistent mode only. Resume must be explicit, validate original snapshot/settings/source availability and honor remaining or newly explicitly authorized budgets.

7. Keep the run pinned when HEAD advances and report later changes as pending. A stale/changed source or incompatible checkpoint cannot be silently retargeted.

8. Project overview, readiness, uncertainty and maintenance baseline from maintained records. Deliberate reprofiling reuses reconciliation and preserves prior valid knowledge instead of clearing the store.

## 7 Public Contracts and State Boundaries

A persistent run records repository/worktree and pinned target, settings/model provenance, selected scope/history choice, consumed budgets, discovered coverage, completed semantic units, pending units and completion state. Checkpoints identify stable bounded units and their validated inputs/results, not raw transcripts. A committed checkpoint references a coherent published revision; run completion never implies every item was semantically evaluated.

## 8 Project and File Changes

**Permitted host touch points:** H3/H10 only for action registration. No second onboarding inference loop, checkpoint scheduler or investigator. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Inspect publication/checkpoint ordering and restart through the real explicit resume command. Distinguish completed discovery from completed semantic coverage and verify that current-state onboarding never invokes history accidentally.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| Current-state only | Run onboarding with history and automation off, including packet expansion and explicit checkpoint resume. | A usable pinned profile/baseline and justified findings appear through T05/T06 with empty episode collections; history is marked not requested, no history/frontier/patch discovery occurs, and automation remains off. | Integration |
| History selected | Enable bounded historical enrichment for one subsystem. | Episodes use the same T05/T06 path and retain coverage/budget limits. | Integration |
| No inference/empty findings | Provider unavailable or all candidates insignificant. | Deterministic profile remains usable; pending/no-item outcomes are honest. | Unit/integration |
| Crash boundaries | Interrupt before/after publication and before/after checkpoint update. | Resume preserves or recognizes completed units without duplicates/dangling state. | Integration |
| Moving target | Advance HEAD or edit the overlay during a persistent run. | Original target remains pinned; later state is pending or overlay uncertainty is explicit. | Integration |
| Resume source gap | Remove a needed revision or alter checkpoint settings/context. | Resume rejects/qualifies the gap; it does not silently retarget or claim completion. | Integration |
| Reprofile/disable | Reprofile existing knowledge, then disable before a later publish. | Prior knowledge survives and revoked late results cannot publish. | Integration |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [ ] Current-state baseline works with historical enrichment, recall and maintenance all off.
- [ ] Valid units become available progressively with honest discovered/analyzed/pending coverage.
- [ ] Persistent resume preserves the original target, validates sources and does not duplicate published knowledge.
- [ ] Interruption, provider failure and disablement retain last valid records without false completed baseline.
- [ ] Onboarding and persistent investigations reuse the same collection, interpretation and publication paths.
- [ ] History-off units, their expansions and resume use T05's current-snapshot-only mode without discovering history or requiring episodes.
- [ ] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [ ] The user has explicitly accepted that reviewed implementation before any task test or documentation implementation begins.
- [ ] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

## 15 Risks

A checkpoint written before publication can falsely skip work; one written afterward can duplicate work without idempotency. Reprofiling must not reset valuable history.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** Onboarding scope, readiness, coverage, partial success, explicit resume and reprofiling. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Choose bounded unit sizes and atomic/idempotent checkpoint ordering using existing storage infrastructure; do not introduce a new background job system. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
