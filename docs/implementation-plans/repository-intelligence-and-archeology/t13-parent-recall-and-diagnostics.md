# T13 Add bounded parent recall and admission diagnostics

**Status:** Proposed; implementation not started or accepted.

**Delivery track:** Proposed Repository Intelligence and Archeology capability; milestone registration follows T01 acceptance.

**Prerequisites:** [T12](t12-explicit-search-and-inspection.md) complete, including its post-acceptance tests and documentation.

**Requirements and parent acceptance outcomes:** RT-01–04, RT-10–12, MT-10–12, IN-04–05; AC-09, AC-20–21, AC-23–24 (child withholding until T15; assignment-limited admission remains T15-owned).

**Sources:** [Parent requirements](../threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

Relevant fresh capsules appear only in eligible opted-in requests; unrelated requests can select none. Store lookup never runs automatically for Stateless or children.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

Small automatic capsule selection for explicitly enabled, freshness-eligible top-level conversation-aware or governed-memory-only requests, with ordinary context accounting and inspectable reasons. Enforce origin-aware admission when existing evidence is selected or reused, and withhold feature intelligence from child snapshots until T15 supplies assignment-baseline admission.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [src/Threadsmith.Context/ContextAssembler.cs](../../../src/Threadsmith.Context/ContextAssembler.cs)
- [src/Threadsmith.Context/HybridRepositoryMemoryRetriever.cs](../../../src/Threadsmith.Context/HybridRepositoryMemoryRetriever.cs)
- [src/Threadsmith.Context/AgentContext.cs](../../../src/Threadsmith.Context/AgentContext.cs)
- [src/Threadsmith.Context/ContextContracts.cs](../../../src/Threadsmith.Context/ContextContracts.cs)
- [src/Threadsmith.Execution/ToolEvidenceAdmission.cs](../../../src/Threadsmith.Execution/ToolEvidenceAdmission.cs)
- [src/Threadsmith.Execution/ModelExplorerAssignmentRunner.cs](../../../src/Threadsmith.Execution/ModelExplorerAssignmentRunner.cs)
- [src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs](../../../src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs)
- [src/Threadsmith.Core/MemoryConcepts.cs](../../../src/Threadsmith.Core/MemoryConcepts.cs)
- [src/Threadsmith.Execution/RepositoryMemoryDispatch.cs](../../../src/Threadsmith.Execution/RepositoryMemoryDispatch.cs)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Add the cheap eligibility gate to the existing parent assembly boundary. Explicitly exclude Stateless and every delegated child before resolving retrieval services or traversing memory links.

2. Build retrieval signals from the active task and already available significant tool/scope facts. Normalize concepts through existing vocabulary; avoid an LLM call merely to generate each query.

3. Reuse T12 bounded candidate retrieval, then rank inside the feature by relevance, concrete scope, current applicability, confidence, freshness, redundancy and likely engineering value. Deterministic ties use stable identities; embeddings are optional.

4. Apply T11 admission to new candidates and retained selections. Historical failures can be relevant if qualified as history and freshness-admissible; confidence labels never override a stale-current assessment.

5. Select zero or a small set, normally one to three, within configured and selected-model context budgets. Do not truncate capsules until their meaning or uncertainty is lost; omit instead.

6. Integrate through ordinary governed evidence/context assembly and final fitting. Preserve host-owned origin metadata for intelligence content: automatic recall/enrichment versus authorized explicit current-request tool evidence, repository/item/revision, source dependencies and originating request/invocation. ContextAssembler must apply current eligibility and T11 admission when selecting stored automatic intelligence, not only when retrieving new candidates. Disabling recall or switching to Stateless withholds previously recalled content even when its evidence record remains non-stale; no feature lookup is needed to reject it. Authorized explicit current-request intelligence results remain governed inputs under RT-12, including in Stateless mode, with evaluated-context/freshness labels; retained automatic content cannot be relabeled as explicit merely because it is already in context. Account for equivalent content in memory, tool results and active context without making inferred guidance host policy. For mixed code_explore evidence, retain separable origin metadata so optional automatic intelligence can be withheld without discarding the authorized code facts.

7. Invalidate cached selections when task scope, request mode, activation, item revision, context identity or freshness changes. Apply the same origin-aware admission before frozen context or continuation reuse; retained projections/compaction must preserve the origin needed for that decision. Archived records need not be erased and must not restore automatic guidance around the gate. At AgentContextAssembler's actual evidence selection boundary, withhold all feature-intelligence payloads, including parent explicit results and mixed-result intelligence sections, until T15 implements role, allowlist and assignment-baseline admission. T03's child tool denial alone does not prevent inheritance from the ordinary parent evidence store. Use existing evidence/snapshot boundaries, not a second child selector or feature state store.

8. Expose disabled, ineligible, stale/unverified, relevance, redundancy and budget omission reasons in the existing context inspector. Only claim final inclusion when the ordinary request accounting establishes it.

## 7 Public Contracts and State Boundaries

The admission callback consumes request mode/parenthood, trusted activation, task/scope/concepts, current dependency state and already admitted content. Eligibility is decided before store lookup and enforced again at ordinary evidence selection/reuse. Selection returns revision-linked capsules, source dependencies, bounded token cost and selected/omitted reasons. Minimal host-owned evidence provenance preserves automatic versus explicit origin and the relevant request/invocation and item/revision identities; mixed results expose separable intelligence content. The host owns selection and delegates feature admission through an optional boundary without referencing feature implementation types. Ineligible automatic evidence is withheld before resolving feature retrieval services. Cached state includes repository/context/mode/task, activation and freshness generations; it never substitutes for T11 revalidation. Before T15, child snapshots admit no feature intelligence; T15 alone replaces that denial with assignment-limited admission.

## 8 Project and File Changes

**Permitted host touch points:** H6, H2; H3 only for preserving origin at existing tool-evidence admission, and H8 only for withholding feature intelligence in existing child snapshot selection until T15. Existing concept normalization, evidence/token accounting and context inspector remain owners. Reuse T12 query primitives rather than a separate automatic retrieval engine. No required embedding/index service or per-tool model call. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Place breakpoints/source traces before lookup to prove child/Stateless exclusion. Populate the ordinary evidence store first, then disable recall, switch to Stateless and delegate through the real assignment runner; trace ContextAssembler and AgentContextAssembler selection as well as frozen/compacted context reuse. Verify explicit current-request results remain permitted for top-level Stateless and that mixed results preserve code facts while withholding automatic guidance. Check final request fitting, not only the ranking method. Challenge any per-tool full-store scan or inference.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| Mode matrix | Use equivalent Conversation-aware, Governed-memory-only, Stateless and child contexts. | Only eligible opted-in parent modes query/select automatically. | Unit/integration |
| Stored automatic evidence | Admit fresh capsules, then disable recall or switch to Stateless while retaining their non-stale evidence records and cached/frozen context. | The next actual model request omits automatic capsules through origin-aware selection/reassembly without new feature lookup; retained records do not bypass the gate. | Integration |
| Explicit isolated access | Mix previously recalled capsules with authorized current-request intelligence tool results in top-level Stateless. | Explicit results remain bounded, freshness-labeled current-request evidence; recalled capsules are withheld and cannot acquire explicit origin through reuse/compaction. | Integration |
| Child inheritance before T15 | Populate parent evidence with recalled capsules, explicit intelligence results and mixed code/intelligence results, then delegate through the existing assignment runner. | Child snapshots receive no feature intelligence before assignment admission exists; ordinary authorized code evidence remains usable and child feature tools remain denied. | Integration |
| Expected relevance | Use predefined tasks with relevant, unrelated and globally important items. | Relevant bounded selections win; irrelevant tasks may select none. | Unit/evaluation |
| Budget pressure | Fit long capsules near final selected-model capacity. | Omit whole unsuitable capsules; normal capacity accounting remains correct. | Unit/integration |
| Continuity | Repeat unchanged scope, then change task/item/mode/dependency. | No duplicate injection; changed cached state is reconsidered safely. | Integration |
| Memory/evidence duplicate | Provide equivalent capsule content in memory or explicit current evidence. | Combined context avoids redundant settled guidance. | Unit/integration |
| Historical/stale distinction | Offer a relevant historical failure and a stale current constraint. | History is qualified when eligible; stale current guidance is withheld. | Unit |
| Inspector receipts | Compare provisional selection, final fitting and actual request inclusion. | Reasons and counts reflect real admission rather than optimistic selection. | Integration |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [ ] Automatic lookup runs only after top-level mode, opt-in and authority eligibility; Stateless/child requests perform none.
- [ ] Stored automatic intelligence is origin-filtered at ordinary evidence selection and context reuse after recall disablement or mode changes; authorized explicit current-request results retain RT-12 semantics.
- [ ] Existing child snapshot selection withholds feature intelligence until T15 supplies assignment-baseline admission; parent tool results and mixed evidence cannot bypass this boundary.
- [ ] Fresh relevant capsules fit the ordinary model budget; zero selection is valid and repeated unchanged content is avoided.
- [ ] Cached selection and final context reuse respond to scope/mode/dependency/item changes before dispatch.
- [ ] Historical guidance is distinguishable from current instructions and cannot override user/host authority.
- [ ] Selection and omission reasons are inspectable without routinely increasing prompt size.
- [ ] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [ ] The user has explicitly accepted that reviewed implementation before any task test or documentation implementation begins.
- [ ] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

## 15 Risks

A correct ranker can still leak guidance through a cached prefix or memory link. Provisional selection must not be reported as actual provider inclusion.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** Recall controls, request-mode boundary, selection/omission explanations and budget behavior. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Define bounded deterministic ranking and cache keys with existing accounting; tune weights later using T20 evidence rather than arbitrary global importance. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
