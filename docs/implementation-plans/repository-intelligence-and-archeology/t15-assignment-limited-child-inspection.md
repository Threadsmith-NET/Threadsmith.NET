# T15 Add assignment-limited child inspection

**Status:** Proposed; implementation not started or accepted.

**Delivery track:** Proposed Repository Intelligence and Archeology capability; milestone registration follows T01 acceptance.

**Prerequisites:** [T14](t14-code-exploration-enrichment.md) complete, including its post-acceptance tests and documentation.

**Requirements and parent acceptance outcomes:** RT-10–12, ISO-04; AC-24.

**Sources:** [Parent requirements](../threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

Child can inspect assigned evidence without automatic store search or enrichment; parent reassesses joined findings at its live invalidation boundary.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

Role-eligible parent admission and assignment-limited child inspection of already admitted intelligence. No child search, new investigation, refresh, activation, persistence enablement or automatic enrichment.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [src/Threadsmith.Context/AgentContext.cs](../../../src/Threadsmith.Context/AgentContext.cs)
- [src/Threadsmith.Execution/ChildAgentModelLoop.cs](../../../src/Threadsmith.Execution/ChildAgentModelLoop.cs)
- [src/Threadsmith.Execution/ChildAgentEvidenceTool.cs](../../../src/Threadsmith.Execution/ChildAgentEvidenceTool.cs)
- [src/Threadsmith.Execution/ChildAgentEvidenceProgress.cs](../../../src/Threadsmith.Execution/ChildAgentEvidenceProgress.cs)
- [src/Threadsmith.Execution/ModelExplorerAssignmentRunner.cs](../../../src/Threadsmith.Execution/ModelExplorerAssignmentRunner.cs)
- [src/Threadsmith.Execution/ToolEvidenceAdmission.cs](../../../src/Threadsmith.Execution/ToolEvidenceAdmission.cs)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Trace the existing assignment snapshot construction, role evidence filters, immutable baseline, tool intersections, child evidence tools and durable join validation. Reuse their enforcement instead of creating a feature agent coordinator.

2. Replace T13's intelligence withholding at the existing AgentContextAssembler selection boundary only with role-eligible, parent-admitted item/revision evidence inside the assignment's baseline, allowlist and budgets. Preserve T13 origin metadata and reassess against that immutable baseline; being present as ordinary parent evidence, including an explicit tool result, is not sufficient admission. Do not silently substitute a different live checkout or add a parallel selector.

3. Carry minimal revision/source metadata inside existing bounded governed evidence, excluding parent/sibling transcripts and mutable sibling state. Avoid serializing whole items/graphs when a capsule and allowed locator suffice.

4. Replace T03's blanket child denial only for assigned inspect/evidence operations. Keep search/status-discovery/investigate/refresh/control mutations denied and enforce the operation allowlist at execution.

5. Resolve requested IDs only through the admitted item/evidence set and exact baseline/revision. Alias and relationship resolution must not open access to unadmitted endpoints or the latest revision.

6. Preserve assignment tool intersections, cancellation and result/output budgets. Parent repository opt-in cannot widen the child's role authority.

7. Validate returned citations through existing join rules. Reject unsupported references; retain child run/model/assignment/baseline provenance on accepted findings.

8. At subsequent parent use, run ordinary invalidation/freshness against the parent's current context. Child success at its frozen baseline never marks live guidance freshly evaluated.

## 7 Public Contracts and State Boundaries

AgentContextSnapshot carries only bounded host-admitted item/revision and evidence identities with source provenance, applicability, immutable baseline and role/tool eligibility. The inspection allowlist is host-created and cannot be expanded by tool arguments or related-item traversal. Child results remain ordinary citation-validated findings; they do not update the parent's live freshness state.

## 8 Project and File Changes

**Permitted host touch points:** H8, H2. Existing delegation, tool intersection and evidence admission own enforcement; feature resolves records only within the supplied allowlist. No feature agent/team executor. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Attempt allowlist escape through alias resolution, relationship fanout, latest-revision defaults and evidence expansion. Trace actual child tool execution and parent join rather than only snapshot serialization.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| Admitted inspection | Assign one item/revision and a subset of its evidence. | Only the assigned references resolve within ordinary budgets. | Unit/integration |
| Stored parent evidence | Populate parent evidence with admitted and unadmitted intelligence, including automatic and explicit origins and mixed code/intelligence results, then create a child snapshot through the real runner. | T13's denial is relaxed only for role-eligible allowlisted revisions proven admissible at the assignment baseline; generic evidence selection cannot inherit other intelligence. | Integration |
| Operation escape | Attempt search, investigation, refresh, activation or persistence from a child. | Execution denies every forbidden action independent of repository settings. | Integration |
| ID/relationship escape | Request an unrelated ID, related endpoint, alias target or newer revision. | No unadmitted knowledge is disclosed through traversal. | Unit |
| Baseline drift | Change parent HEAD/worktree after assignment then inspect from child. | Child remains tied to assigned baseline; parent later reassesses live use. | Integration |
| Role/tool intersection | Remove inspection from role/assignment tools. | Feature tool access remains unavailable despite parent opt-in. | Unit/integration |
| Join citations | Return findings with admitted and fabricated source references. | Normal durable join accepts valid citations and rejects fabricated ones. | Integration |
| Isolation | Enable parent recall and sibling findings while child explores. | No automatic lookup, sibling/transcript leakage or enrichment occurs. | Integration |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [ ] T13's child withholding is replaced at ordinary snapshot selection only by role-eligible, parent-admitted bounded intelligence tied to the immutable assignment baseline; stored parent evidence never bypasses admission.
- [ ] Assigned inspection resolves only allowed item revisions and cited evidence; every broader operation is rejected at runtime.
- [ ] No automatic store search, memory-link backfill or code_explore enrichment occurs for any child.
- [ ] Returned findings pass existing citation/join rules and cannot silently refresh parent-live guidance.
- [ ] Existing delegation remains the sole scheduling, lifecycle and evidence-join implementation.
- [ ] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [ ] The user has explicitly accepted that reviewed implementation before any task test or documentation implementation begins.
- [ ] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

## 15 Risks

A permitted inspect operation can become implicit discovery through linked endpoints. Parent and child baselines differ legitimately and must not share one mutable freshness cache.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** Delegated evidence and inspection limits, baseline versus parent-live freshness. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Use the existing snapshot/evidence extension points and exact revision allowlists; do not add a new child context format or independent feature delegation policy engine. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
