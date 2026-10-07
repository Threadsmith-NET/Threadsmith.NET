# T11 Implement current freshness admission

**Status:** Proposed; implementation not started or accepted.

**Delivery track:** Proposed Repository Intelligence and Archeology capability; milestone registration follows T01 acceptance.

**Prerequisites:** [T10](t10-onboarding-and-checkpoint-recovery.md) complete, including its post-acceptance tests and documentation.

**Requirements and parent acceptance outcomes:** MT-03–06, MT-09–12, RT-03–04; AC-08, AC-20–21.

**Sources:** [Parent requirements](../threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

With maintenance off, changed/unverifiable guidance cannot enter the next request; unaffected guidance can qualify, and explicit inspection explains stale reasons.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

Deterministic context-local freshness assessment and invalidation integration, including frozen request reuse. Automatic recall stays unavailable until T13; maintenance is not required or launched by these checks.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [src/Threadsmith.Context/ContextAssembler.cs](../../../src/Threadsmith.Context/ContextAssembler.cs)
- [src/Threadsmith.Context/EvidenceStore.cs](../../../src/Threadsmith.Context/EvidenceStore.cs)
- [src/Threadsmith.Context/SourceEvidence.cs](../../../src/Threadsmith.Context/SourceEvidence.cs)
- [src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs](../../../src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs)
- [src/Threadsmith.Core/SemanticRefreshContracts.cs](../../../src/Threadsmith.Core/SemanticRefreshContracts.cs)
- [src/Threadsmith.Core/RepositoryGitStatus.cs](../../../src/Threadsmith.Core/RepositoryGitStatus.cs)
- [src/Threadsmith.DotNet/SemanticRefreshCoordinator.cs](../../../src/Threadsmith.DotNet/SemanticRefreshCoordinator.cs)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Trace repository/semantic notification sources, applied-mutation and rollback invalidations, ContextAssembler's before-assembly boundary and the conversation loop's frozen-context reuse conditions. Identify every route to a next model request.

2. Derive normalized evidence/scope dependency keys from retained records and existing source identities. Include both sides of moves, configuration/project dependencies and known transitive relationships; missing dependency coverage is an uncertainty state.

3. Add a minimal optional host callback that runs only for feature-eligible work and delegates freshness policy to the feature assembly. Apply pending invalidations before selection or reuse, not after model dispatch.

4. Compare current identity, HEAD/ref and dirty changes to evaluation/cached admission anchors with scoped existing Git/status/fact services. Treat branch divergence, unavailable ancestry and unverifiable identity as gaps requiring bounded checks or withholding.

5. Invalidate cached assumptions on restart, notification loss, watcher gaps and changed modes. Do not rely on a watcher delivering every event or on Git timestamps as proof of unchanged relevant content.

6. Assess candidate and already admitted items proportionally to declared dependencies. Reuse shared batched identities; do not profile the repository or fetch every item on each request.

7. Map invalidation into ordinary evidence/context versioning so frozen contexts, cached capsule selections and continuation reuse cannot reintroduce affected guidance. Preserve archived messages as historical evidence rather than treating them as fresh guidance.

8. Expose stale/evaluated-context reasons for explicit reads and exploration enrichment. Deterministic checks never invoke a model, repair a profile, schedule maintenance or alter semantic evaluation anchors.

## 7 Public Contracts and State Boundaries

Assessment distinguishes evaluated, unaffected by checked changes, pending reevaluation, incompatible context and unavailable/uncertain evidence. Store evaluation anchors remain separate from transient admission validity. Assessment input includes candidate/already admitted dependencies, repository/worktree/ref/HEAD identity, dirty changes and known semantic/project dependencies. A checked range and method may justify unaffected carry-forward without advancing lastEvaluatedCommit.

## 8 Project and File Changes

**Permitted host touch points:** H6, H2; H4 only for missing comparison primitives. Include `SessionApplication.ConversationLoop.cs` frozen-context reuse, not just `ContextAssembler.AssembleAsync`. No new watcher service; no inference from admission. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Exercise the next-request path after cached context reuse, not only the callback in isolation. Check delayed/missing events, broad scope and missing dependencies for optimistic freshness assumptions.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| Dirty change matrix | Edit/add/delete/move through tools and externally; roll back a mutation. | Affected current guidance is withheld, including both move paths; unrelated proven-unaffected items remain eligible. | Integration |
| Frozen reuse | Select a capsule, change its dependency, then reuse a frozen/continuation context. | The next request cannot retain it as eligible current guidance. | Integration |
| History/context change | Switch branch/worktree, reset/rebase, or lose ancestry. | Assessment reports incompatible/unverified state rather than false freshness. | Integration |
| Notification gap | Drop a change event, restart, or mark watcher continuity lost. | Cached assumptions expire and bounded verification or withholding follows. | Unit/integration |
| Transitive/missing dependencies | Change a referenced project/configuration or omit dependency coverage. | Known transitive impacts invalidate; unknown coverage cannot be assumed unaffected. | Unit |
| Carry-forward | Check an unrelated A-to-B change range. | Item may be unaffected with method/range recorded; lastEvaluatedCommit stays unchanged. | Unit |
| Maintenance off | Run admission and explicit stale read with repair disabled. | No inference, onboarding or maintenance occurs; explicit result labels stale context. | Integration |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [ ] Relevant external/internal dirty edits, additions, deletions, moves and rollback invalidate affected admission before the next request or enriched result.
- [ ] Frozen request/context reuse obeys the same checks as newly assembled requests.
- [ ] Unknown identity/dependencies/notification continuity conservatively withholds affected guidance; unaffected items can retain eligibility with recorded justification.
- [ ] Maintenance-off operation still checks freshness without launching inference/repair or advancing semantic evaluation anchors.
- [ ] Unused/disabled paths perform no feature-specific rechecking beyond their cheap eligibility gate.
- [ ] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [ ] The user has explicitly accepted that reviewed implementation before any task test or documentation implementation begins.
- [ ] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

## 15 Risks

The most dangerous defect is a fresh-looking reused context after a relevant edit. Path matching alone misses transitive impact; whole-repository rechecking defeats bounded recall.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** Freshness states, withholding, carry-forward versus reevaluation and cache invalidation contract. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Choose how to carry dependency completeness and context invalidation generations through existing contracts. A conservative unknown state is preferable to invented compatibility. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
