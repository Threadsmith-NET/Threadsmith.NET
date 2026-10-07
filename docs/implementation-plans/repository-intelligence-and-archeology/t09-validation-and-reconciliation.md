# T09 Implement validation and reconciliation

**Status:** Proposed; implementation not started or accepted.

**Delivery track:** Proposed Repository Intelligence and Archeology capability; milestone registration follows T01 acceptance.

**Prerequisites:** [T08](t08-canonical-records-and-storage.md) complete, including its post-acceptance tests and documentation.

**Requirements and parent acceptance outcomes:** MT-01–02, MT-07, AR-08–09, IN-05–09, PR-05; AC-05–06.

**Sources:** [Parent requirements](../threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

Replaying a packet corroborates or explicitly changes stable records without duplicates or unexplained loss.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

A single validation/reconciliation/publication path for every retained candidate source. No new evidence reader, memory reconciliation clone, inference runtime or independent update path.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [src/Threadsmith.Core/MemoryConcepts.cs](../../../src/Threadsmith.Core/MemoryConcepts.cs)
- [src/Threadsmith.Core/MemoryConceptResolution.cs](../../../src/Threadsmith.Core/MemoryConceptResolution.cs)
- [src/Threadsmith.Context/RepositoryMemoryService.cs](../../../src/Threadsmith.Context/RepositoryMemoryService.cs)
- [src/Threadsmith.Core/ManagedMemoryContracts.cs](../../../src/Threadsmith.Core/ManagedMemoryContracts.cs)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Define deterministic structural/reference/context checks before semantic decisions. Reject unknown evidence, unsupported locators, invalid relationship directions/endpoints and incompatible target context.

2. Separate candidate validation from semantic equivalence suggestions. Reuse concept normalization, but do not treat equal titles/tags or a model suggestion as authoritative identity.

3. Create stable idempotency keys for the candidate/evidence/operation unit so replay can recognize completed publication. Distinguish corroboration from a new substantive revision.

4. Implement each required reconciliation outcome with explicit transition rules. Preserve old statements/provenance on merge/supersession and retain partial supersession and historical-only applicability.

5. Resolve aliases transitively with cycle protection and preserve older citation meaning. Do not rewrite an old revision's evidence to make it appear to describe the latest item.

6. Treat conflicting evidence and user-attributed corrections as inspectable records. Later inference cannot silently overwrite user correction or erase a historical failure because it omitted that item.

7. Generate or validate the bounded capsule against its substantive item revision; invalidate/regenerate it on semantic changes. Historical guidance must read as history and include material uncertainty.

8. Commit through T08 using expected revisions and coherent references. Retry a conflict only after reloading/revalidating the candidate; a retry must not duplicate items, relationships or evidence.

## 7 Public Contracts and State Boundaries

A candidate references validated T05/T06 evidence and an expected canonical revision/context. Host code assigns durable IDs and produces one of added, corroborating, revised, merged, superseding, conflicting, rejected or deferred with reasons/provenance. Old IDs/revisions remain resolvable through explicit alias/supersession semantics. Item changes and capsule revisions publish atomically through T08.

## 8 Project and File Changes

**Permitted host touch points:** None expected. Reuse shared normalization primitives when suitable; do not copy memory reconciliation or change memory semantics to accommodate intelligence. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Challenge every identity/deduplication shortcut. Trace onboarding, investigation and later maintenance to this single publication path; ensure aliases preserve the meaning of historical citations.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| Outcome matrix | Submit candidates that trigger each of the eight outcomes. | The correct explicit transition and reason occur without unrelated changes. | Unit |
| Replay | Submit the same operation/evidence repeatedly after success or interrupted response. | No duplicate records appear; stable IDs and outcomes are recoverable. | Unit/integration |
| Alias chain/cycle | Merge several items and attempt a cyclic/invalid replacement. | Old IDs resolve consistently; cycles and invalid endpoints are rejected. | Unit |
| Partial supersession | Replace a mechanism while preserving its invariant/failure lesson. | Surviving applicability and historical evidence remain available. | Unit |
| Correction conflict | Infer a claim contradicting a user-attributed correction. | Conflict is retained or deferred; user attribution is not silently removed. | Unit |
| Capsule consistency | Revise statement, applicability or material uncertainty. | Capsule changes/invalidation is atomic with the item revision. | Unit/integration |
| Concurrent publication | Race equivalent and conflicting candidates against one revision. | Expected-revision handling prevents lost updates and duplicate identity. | Integration |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [ ] Every retained candidate uses this path and yields an explicit outcome with validated references and provenance.
- [ ] Equivalent replay corroborates or deliberately revises stable identities; later model omission never deletes knowledge.
- [ ] Merge/supersession preserves older references, historical lessons and independent applicability/confidence.
- [ ] User corrections cannot be silently overwritten and conflicting explanations remain inspectable.
- [ ] Published capsules always match the substantive item revision and retain uncertainty.
- [ ] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [ ] The user has explicitly accepted that reviewed implementation before any task test or documentation implementation begins.
- [ ] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

## 15 Risks

Overaggressive merging loses distinct scope or conflicting evidence; conservative merging can accumulate duplicates. Model omission is never a deletion instruction.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** Reconciliation outcomes, identity/alias semantics and publication guarantees. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Settle explicit transition and alias rules and an idempotency identity based on actual provenance, not transient model wording. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
