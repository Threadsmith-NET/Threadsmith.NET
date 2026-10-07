# T16 Link intelligence with existing memory

**Status:** Proposed; implementation not started or accepted.

**Delivery track:** Proposed Repository Intelligence and Archeology capability; milestone registration follows T01 acceptance.

**Prerequisites:** [T15](t15-assignment-limited-child-inspection.md) complete, including its post-acceptance tests and documentation.

**Requirements and parent acceptance outcomes:** RT-08–10, IN-04, MT-08; AC-12, AC-14, AC-23–24.

**Sources:** [Parent requirements](../threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

User-curated memory has inspectable origin; source changes do not silently rewrite it or inject contradictory settled guidance.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

Explicit curated memory promotion and source revision links, change awareness and combined recall conflict/deduplication handling. Existing memory remains a distinct record type with its own lifecycle.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [src/Threadsmith.Core/ManagedMemoryServiceContracts.cs](../../../src/Threadsmith.Core/ManagedMemoryServiceContracts.cs)
- [src/Threadsmith.Core/RepositoryMemoryContracts.cs](../../../src/Threadsmith.Core/RepositoryMemoryContracts.cs)
- [src/Threadsmith.Core/ManagedMemoryContracts.cs](../../../src/Threadsmith.Core/ManagedMemoryContracts.cs)
- [src/Threadsmith.Core/MemoryConcepts.cs](../../../src/Threadsmith.Core/MemoryConcepts.cs)
- [src/Threadsmith.Context/RepositoryMemoryService.cs](../../../src/Threadsmith.Context/RepositoryMemoryService.cs)
- [src/Threadsmith.Context/HybridRepositoryMemoryRetriever.cs](../../../src/Threadsmith.Context/HybridRepositoryMemoryRetriever.cs)
- [src/Threadsmith.Execution/RepositoryMemoryApplication.cs](../../../src/Threadsmith.Execution/RepositoryMemoryApplication.cs)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Trace memory creation/update and retrieval from manual and model entry points through IManagedRepositoryMemoryService. Inspect existing metadata/concept support before adding optional link fields.

2. Implement a feature-owned promotion request that validates current source context/revision and eligible guidance, then delegates the actual memory write to the existing service under existing authority.

3. Store minimal source-link provenance without copying the intelligence item/evidence graph into memory. If link and memory writes cannot be atomic across owners, use stable operation IDs and repairable linking rather than a distributed transaction framework.

4. Reuse existing concept normalization and scope conventions. Keep intelligence-specific ranking/lifecycle in the feature and memory-specific behavior in its current owner.

5. On item revision, supersession, dispute or correction, make linked memories discoverable for reconsideration. Preserve independently curated user wording/intent and source revision history; require existing memory authority for any eventual edit.

6. At combined recall, detect identical/redundant content and material contradictions using available metadata and existing evidence. Avoid representing contradictory claims as settled guidance; expose unresolved conflicts rather than guessing a winner.

7. Gate link traversal before lookup in Stateless and child contexts and when intelligence is disabled. Independently curated memory retains its own rules; no link may act as an automatic intelligence backfill.

8. Handle missing/deleted intelligence sources with honest broken-source status while retaining valid user memory and its original provenance.

## 7 Public Contracts and State Boundaries

A link identifies source repository/worktree context, intelligence item and exact revision, plus the existing memory identity and promotion attribution. Source updates make links reconsiderable; they do not silently rewrite memory text or imply current applicability. Existing memory operations remain the authority for memory creation/update/removal. Intelligence lookup through links is subject to the same request-mode and freshness gates as direct recall.

## 8 Project and File Changes

**Permitted host touch points:** H9, H6 as needed. `IManagedRepositoryMemoryService` remains the only memory write path. Do not clone the store, copy the entire intelligence corpus or add an automatic backfill path. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Trace the actual memory write call and every link traversal before lookup. Look for copied memory storage/reconciliation, automatic promotion and silent rewriting of user intent.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| Explicit promotion | Promote one approved guidance item, then discover additional items. | Only explicit promotion creates memory through the existing service. | Integration |
| Idempotent/link recovery | Retry promotion after memory write succeeds but link recording is interrupted. | No duplicate memory appears; source linkage repairs or reports its gap. | Integration |
| Changed source | Revise, dispute and supersede the linked item. | Linked memory is reconsiderable with original source revision retained. | Unit/integration |
| User edits | Edit memory manually before an intelligence refresh. | User wording/intent survives and conflict is surfaced if necessary. | Integration |
| Combined recall | Offer duplicate and contradictory intelligence/memory content. | Duplicates are avoided and contradictions are not silently settled. | Unit |
| Eligibility traversal | Use disabled, Stateless and delegated requests with links present. | No automatic intelligence lookup/backfill occurs. | Integration |
| Deleted/wrong source | Reset the source store or supply a cross-repository link. | Missing source is explicit; incompatible links are rejected and user memory survives. | Unit/integration |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [ ] Promotion is explicit and uses the existing memory write service; discovery never copies the corpus automatically.
- [ ] Memory links retain exact source item/revision/context and attribution without conflating lifecycles.
- [ ] Source changes identify affected links for reconsideration but cannot silently rewrite user-curated memory.
- [ ] Combined recall avoids duplicate or contradictory settled guidance and obeys intelligence eligibility before link traversal.
- [ ] Missing/reset intelligence does not destroy independently curated memory.
- [ ] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [ ] The user has explicitly accepted that reviewed implementation before any task test or documentation implementation begins.
- [ ] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

## 15 Risks

Cross-store partial writes can orphan links; copying source text without revision metadata loses change awareness. Link traversal can undermine otherwise correct request-mode gates.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** Distinct lifecycles, promotion, links and reconsideration behavior. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Choose minimal link metadata placement and repairable cross-owner write ordering using current memory contracts; avoid a second memory store or universal knowledge record. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
