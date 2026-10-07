# T12 Deliver bounded explicit search and evidence inspection

**Status:** Proposed; implementation not started or accepted.

**Delivery track:** Proposed Repository Intelligence and Archeology capability; milestone registration follows T01 acceptance.

**Prerequisites:** [T11](t11-freshness-admission.md) complete, including its post-acceptance tests and documentation.

**Requirements and parent acceptance outcomes:** RT-05–06, RT-12, IN-03, IN-08–09, NQ-02; AC-08, AC-10, AC-23, AC-25.

**Sources:** [Parent requirements](threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

Explicit top-level Stateless inspection returns current-run tool evidence without recall/maintenance; retained evidence works after restart subject to source availability.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

Bounded explicit status/search/inspect/evidence operations over retained intelligence. Reads never start investigations, refresh, model calls or onboarding; delegated access remains denied until T15.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [src/Threadsmith.Tools/ToolContracts.cs](../../../src/Threadsmith.Tools/ToolContracts.cs)
- [src/Threadsmith.Tools/ToolInvocationPipeline.cs](../../../src/Threadsmith.Tools/ToolInvocationPipeline.cs)
- [src/Threadsmith.Core/MemoryConcepts.cs](../../../src/Threadsmith.Core/MemoryConcepts.cs)
- [src/Threadsmith.Core/RepositoryInventoryContracts.cs](../../../src/Threadsmith.Core/RepositoryInventoryContracts.cs)
- [src/Threadsmith.Tools/TextEvidenceDocument.cs](../../../src/Threadsmith.Tools/TextEvidenceDocument.cs)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Extend the existing feature tool's validated action schema and registry entry rather than adding independent tools for every projection. Keep refresh/investigate actions subject to their separate authority.

2. Implement a bounded indexed candidate query using the canonical records. Reuse concept/path normalization and T05 evidence resolution; do not clone the memory search engine or materialize the full store to page it.

3. Bind continuation state to repository identity, filters and a coherent result generation/revision. Validate offsets/cursors, reject mismatched continuation and use deterministic ordering.

4. Compose inspection from canonical item/revision and relationships, resolving old aliases without changing the historical meaning of a requested revision. Bound fanout and make further narrowing possible.

5. Resolve evidence through existing authorized pinned reads. Present supporting and conflicting evidence separately, with overlay labels and unavailable/redacted/truncated source reasons.

6. Apply T11 current freshness to explicit results. Stale records may be returned with evaluated snapshot and stale reason; do not silently filter away historical findings or treat a warning as fresh eligibility.

7. Distinguish no relevant item from insufficient/unexamined coverage, and surface reusable cached findings only when their context/coverage is suitable.

8. Run through the ordinary tool pipeline in top-level Stateless mode as current-request evidence only. Apply final serialized/output/model limits after composition and record omissions without dropping source provenance.

## 7 Public Contracts and State Boundaries

Search requests carry normalized question/concepts, scope, kind/applicability filters and effective limits. Results expose stable item/revision references, current freshness assessment, coverage and continuations bound to repository/query/revision. Inspect returns substantive item/assessments/relationships with bounded detail; evidence resolves its source revision or returns an explicit unavailable reason. No-match and unexamined coverage are different outcomes.

## 8 Project and File Changes

**Permitted host touch points:** H3/H10 registration; H2 for necessary DTOs only. Reuse T05 source resolution, T08 records and T11 freshness. Derived narratives remain projections. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Inspect database/query work before pagination and source access before sanitization. Ensure explicit reads qualify stale records and cannot silently escalate into investigation or global graph expansion.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| Filter combinations | Query by concepts, path/project/symbol, kinds and applicability. | Only matching bounded candidates appear with deterministic ordering. | Unit |
| Continuation integrity | Page results, mutate query/context or change store generation. | Continuation remains coherent or explicitly invalidates; it cannot broaden scope. | Unit/integration |
| Progressive inspection | Follow item to old revision, alias and supporting/conflicting evidence. | Meaning and provenance remain correct through restart. | Integration |
| Unavailable source | Delete history objects or deny a cited path. | Result reports unavailable/denied source without fabricating text. | Integration |
| Coverage distinction | Query a covered empty area and an unexamined area. | No-match and insufficient-coverage diagnostics differ. | Unit |
| Bounds | Use huge item relationship fanout and heavily escaped source. | Bounded queries and final byte/token ceilings hold with useful continuation. | Unit/integration |
| Stateless and side effects | Read stale intelligence with provider unavailable and maintenance off. | Explicit current-run evidence is returned with labels and no model/repair activity. | Integration |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [ ] Explicit search and item/evidence drill-down work without inference and survive restart for retained records subject to source availability.
- [ ] Pagination is bounded before retrieval and preserves query/repository/revision consistency.
- [ ] Stale and unavailable results identify the evaluated context and limitations; no-match is distinct from unexamined scope.
- [ ] Stateless explicit access does not enable recall, import conversation memory or enable maintenance.
- [ ] Reads use existing governance and never trigger analysis side effects.
- [ ] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [ ] The user has explicitly accepted that reviewed implementation before any task test or documentation implementation begins.
- [ ] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

## 15 Risks

An inspect operation can become unbounded through relationships. A cursor tied only to an offset can mix changing revisions and mislead evidence interpretation.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** Tool arguments, progressive inspection, continuation and limitation meanings. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Choose query/cursor and inspect detail shapes using the existing tool conventions; exact ranking is refined in T13 without creating another query implementation. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
