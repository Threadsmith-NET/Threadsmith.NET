# T17 Add explicit incremental maintenance

**Status:** Proposed; implementation not started or accepted.

**Delivery track:** Proposed Repository Intelligence and Archeology capability; milestone registration follows T01 acceptance.

**Prerequisites:** [T16](t16-memory-linkage.md) complete, including its post-acceptance tests and documentation.

**Requirements and parent acceptance outcomes:** MT-03–09, ON-08, AR-06, NQ-03; AC-06–08, AC-13–14, AC-25.

**Sources:** [Parent requirements](../threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

A bounded refresh updates affected knowledge without pretending every record was reevaluated; changes arriving after B remain pending.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

Explicit bounded A-to-B incremental refresh and deliberate reprofiling using the same analysis, checkpoint and publication flow. No automatic trigger is introduced until T18.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [src/Threadsmith.Core/RepositoryInventoryContracts.cs](../../../src/Threadsmith.Core/RepositoryInventoryContracts.cs)
- [src/Threadsmith.Workspaces/GitQueryService.cs](../../../src/Threadsmith.Workspaces/GitQueryService.cs)
- [src/Threadsmith.Workspaces/GitQueryService.Inventory.cs](../../../src/Threadsmith.Workspaces/GitQueryService.Inventory.cs)
- [src/Threadsmith.Core/SemanticRefreshContracts.cs](../../../src/Threadsmith.Core/SemanticRefreshContracts.cs)
- [src/Threadsmith.Tools/ToolInvocationPipeline.cs](../../../src/Threadsmith.Tools/ToolInvocationPipeline.cs)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Authorize the explicit refresh through the existing feature operation. Load the known baseline and resolve B once; revalidate persistent activation and repository/worktree identity.

2. Check Git ancestry/history availability using existing query services. For unsafe A-to-B comparison, report the exact gap and use bounded comparison/reprofile only when the explicit request authorizes it.

3. Discover actual changed scope and new candidate episodes within the pinned range. Use T11 dependency/impact knowledge including known project/symbol/relationship transitive effects; exact paths alone are insufficient.

4. Build bounded work units for affected retained records and potentially new significant changes. Rank before fetching full patches; unrelated records receive honest carry-forward only when checked dependencies prove it.

5. Execute units through T05/T06, reconcile through T09 and checkpoint through T10. Reuse the same evaluator for explicit refresh and later automatic work; do not implement another updater.

6. Publish coherent revisions and relationships with expected-generation checks. Preserve user corrections or surface conflict; do not overwrite them because the inference output is newer.

7. Keep commits arriving after B pending. Record checked range, carry-forward method, incomplete coverage and per-item anchors separately; never stamp every item evaluated at B.

8. Resume interrupted refresh explicitly using pinned checkpoints. Deliberate reprofile reconciles existing history, while unavailable old sources remain labeled rather than erased.

## 7 Public Contracts and State Boundaries

A maintenance request identifies a known baseline A, captured target B, selected scope and resource limits. Run coverage and per-item lastEvaluatedCommit remain independent. Each impact result is affected, deterministically unaffected with range/method, or uncertain. Only validated publication advances run coverage; semantic evaluation anchors advance only for genuinely evaluated items.

## 8 Project and File Changes

**Permitted host touch points:** H4 only for proven ancestry/range gaps; H3/H10 action wiring. Reuse T10 executor, T11 impact/freshness and T09 publication, not a second updater. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Challenge timestamp-based range selection, blanket item freshness stamping and duplicate maintenance orchestration. Trace transitive impacts and explicit repair authority through the real refresh operation.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| Pinned range | Advance HEAD beyond B during refresh. | Only captured A-to-B work is covered; later changes remain pending. | Integration |
| Impact selection | Change one dependency with downstream item relationships. | Known affected records and new episodes are selected; proven unrelated items carry forward. | Unit/integration |
| Anchor honesty | Carry forward one item and semantically reevaluate another. | Only the reevaluated item advances its evaluation anchor. | Unit |
| Unsafe history | Rebase/reset/diverge or truncate history before A. | Gap is reported; unauthorized reprofile/inference does not start. | Integration |
| Retry/resume | Interrupt before/after unit publication and refresh the same range again. | No duplicate knowledge or false completed coverage occurs. | Integration |
| Correction preservation | New evidence conflicts with a user correction. | Conflict remains visible; attributed correction is not silently replaced. | Unit/integration |
| Rename/deleted evidence | Move/remove supporting sources while retained historical findings exist. | Current impact is assessed and old unavailable evidence remains qualified. | Integration |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [ ] Refresh is pinned to actual A-to-B ancestry/change evidence and never expands to later commits silently.
- [ ] Affected retained records and new episodes are considered proportionally, including known transitive impact.
- [ ] Unaffected carry-forward records its checked range/method without claiming semantic reevaluation.
- [ ] Unsafe history and source gaps are explicit; repair/reprofile requires authority and stays bounded.
- [ ] Replays/resume preserve stable records, historical lessons and user corrections using the existing pipeline.
- [ ] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [ ] The user has explicitly accepted that reviewed implementation before any task test or documentation implementation begins.
- [ ] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

## 15 Risks

A run baseline is not an item evaluation anchor. Divergent history cannot be repaired by pretending A is an ancestor or dropping historical lessons.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** Explicit refresh, range/coverage semantics, recovery and unsafe-history handling. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Choose bounded impact prioritization and gap/reprofile options using T10 units and T11 assessments; extend Git primitives only for demonstrated missing capability. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
