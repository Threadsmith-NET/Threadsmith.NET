# T14 Enrich existing code exploration

**Status:** Proposed; implementation not started or accepted.

**Delivery track:** Proposed Repository Intelligence and Archeology capability; milestone registration follows T01 acceptance.

**Prerequisites:** [T13](t13-parent-recall-and-diagnostics.md) complete, including its post-acceptance tests and documentation.

**Requirements and parent acceptance outcomes:** RT-07, RT-10–12, MT-10–12; AC-11, AC-15, AC-20, AC-23–24.

**Sources:** [Parent requirements](threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

Eligible fresh context enriches ordinary exploration; disabled, stale, Stateless and delegated calls retain ordinary behavior without automatic lookup.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

An optional compact intelligence addition to the existing code_explore result. No new explorer, source read path, semantic workspace, renderer or automatic investigation.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [src/Threadsmith.Tools/CodeExploreTool.cs](../../../src/Threadsmith.Tools/CodeExploreTool.cs)
- [src/Threadsmith.Tools/CodeExploreOutputFormattingTool.cs](../../../src/Threadsmith.Tools/CodeExploreOutputFormattingTool.cs)
- [src/Threadsmith.Core/CodeExploreContracts.cs](../../../src/Threadsmith.Core/CodeExploreContracts.cs)
- [src/Threadsmith.Core/CodeExploreOptions.cs](../../../src/Threadsmith.Core/CodeExploreOptions.cs)
- [src/Threadsmith.App/HostFoundation.cs](../../../src/Threadsmith.App/HostFoundation.cs)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Trace CodeExploreTool.ExecuteAsync from input validation through its policy source reader, semantic query, confinement, provenance and final byte bounding. Find the smallest callback point after authorized facts exist.

2. Add only host-owned optional DTO/callback contracts; do not introduce a dependency from Tools/Core to the feature assembly. App supplies the feature implementation.

3. Check T13 request eligibility before any lookup. An explicit code_explore call is not explicit authority to inspect intelligence in Stateless or delegated contexts.

4. Use the existing explored anchors as input to T12/T13 selection and T11 freshness. Do not perform a second semantic/source scan or use enrichment to broaden the user's scope.

5. Attach a small distinguishable intelligence section with item/revision, qualified capsule, freshness and deeper-inspection reference. Preserve T13's host-owned automatic-origin metadata and separable section boundaries through tool-evidence admission and context reuse; an explicit code_explore invocation does not convert enrichment into explicitly requested intelligence. Downstream parent/child evidence selection must be able to withhold the intelligence section while preserving authorized code facts. Preserve ordinary code findings when no eligible intelligence exists.

6. Pass the combined result through existing confinement/sanitization/formatting and final serialized byte budgeting. Prefer dropping optional enrichment to destroying required code evidence; avoid double counting or reinjecting equivalent active context.

7. Handle unavailable/corrupt feature storage with bounded diagnostics and preserved code findings. Propagate ordinary operation cancellation rather than disguising cancellation as empty enrichment.

8. Advertise deeper investigation only as an explicit capability suggestion when a gap exists; never launch it from the exploration callback.

## 7 Public Contracts and State Boundaries

The callback receives authorized existing explored project/path/symbol facts, request-mode/assignment context, trusted activation and remaining output constraints. It returns bounded intelligence references/capsules with freshness and provenance. Code facts and historical interpretation stay structurally distinguishable; existing code result provenance and truncation semantics remain authoritative.

## 8 Project and File Changes

**Permitted host touch points:** H7, H2, H1. Extend existing output formatting and final byte bounding; no competing exploration tool, result renderer or reader. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Review final combined serialization, not an intermediate DTO size. Trace the underlying semantic reader and ensure enrichment does not add a parallel reader or bypass request-mode checks.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| Positive enrichment | Explore a fresh relevant scoped area in an eligible opted-in parent request. | Compact references/capsules appear with correct provenance. | Integration |
| Eligibility matrix | Repeat with disabled, Stateless, child and stale contexts. | No automatic enrichment/forbidden store lookup occurs. | Unit/integration |
| Retained mixed evidence | Retain an enriched code_explore result in parent evidence, then disable recall, switch to Stateless or delegate before T15. | Origin-aware selection/reuse withholds the automatic intelligence section while preserving authorized code facts; the explicit exploration call does not bypass T13 or child admission. | Integration |
| Result limits | Use a near-limit code result plus escaped capsule metadata. | Final byte/model limits hold; optional intelligence is omitted first where appropriate. | Unit |
| Feature failure | Make the store unavailable during otherwise successful exploration. | Ordinary code facts remain useful and diagnostics are bounded. | Integration |
| Cancellation | Cancel during enrichment after semantic results arrive. | Cancellation follows normal lifecycle; no late result is silently published. | Integration |
| Deduplication | Select the same item through parent recall and exploration. | Active context avoids redundant capsules while references remain inspectable. | Unit/integration |
| Evidence gap | Explore an unprofiled area. | Gap is explicit; no history/inference operation starts. | Integration |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [ ] Existing semantic exploration remains the only execution/read/formatting path.
- [ ] Eligible opted-in fresh intelligence appears compactly and is distinct from code facts.
- [ ] Disabled, absent, stale, Stateless and delegated cases perform no prohibited lookup and retain usable ordinary exploration.
- [ ] Combined serialized output stays within current tool/model bounds and preserves essential code evidence.
- [ ] Enrichment cannot trigger automatic investigation or bypass cancellation.
- [ ] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [ ] The user has explicitly accepted that reviewed implementation before any task test or documentation implementation begins.
- [ ] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

## 15 Risks

A late enrichment append can evade the existing byte ceiling. Returning empty enrichment on cancellation can incorrectly report a completed operation.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** Exploration enrichment and explicit deeper-inspection workflow. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Choose the narrow result contract and optional-section bounding priority using current CodeExplore formatting; preserve existing caller expectations. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
