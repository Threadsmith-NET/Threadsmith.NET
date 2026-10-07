# T04 Capture pinned identity and bounded structural facts

**Status:** Proposed; implementation not started or accepted.

**Delivery track:** Proposed Repository Intelligence and Archeology capability; milestone registration follows T01 acceptance.

**Prerequisites:** [T03](t03-governed-operation-routing.md) complete, including its post-acceptance tests and documentation.

**Requirements and parent acceptance outcomes:** ON-01–04, ON-06, NQ-04, NQ-06; AC-02, AC-08.

**Sources:** [Parent requirements](threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

Explicit bounded profiling returns facts and exclusions without persistence or inference. HEAD moving during collection does not change the target.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

Explicit bounded deterministic capture of identity, a pinned commit, an optional labeled overlay and structural facts. No inference, canonical storage, history-wide scan or repository execution.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [src/Threadsmith.Core/RepositoryIdentity.cs](../../../src/Threadsmith.Core/RepositoryIdentity.cs)
- [src/Threadsmith.Core/RepositoryInventoryContracts.cs](../../../src/Threadsmith.Core/RepositoryInventoryContracts.cs)
- [src/Threadsmith.Core/RepositoryGitStatus.cs](../../../src/Threadsmith.Core/RepositoryGitStatus.cs)
- [src/Threadsmith.Workspaces/GitQueryService.cs](../../../src/Threadsmith.Workspaces/GitQueryService.cs)
- [src/Threadsmith.Workspaces/GitQueryService.Inventory.cs](../../../src/Threadsmith.Workspaces/GitQueryService.Inventory.cs)
- [src/Threadsmith.Workspaces/GitQueryService.ShowFiles.cs](../../../src/Threadsmith.Workspaces/GitQueryService.ShowFiles.cs)
- [src/Threadsmith.Core/SemanticContracts.cs](../../../src/Threadsmith.Core/SemanticContracts.cs)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Reuse the repository identity and Git contracts through the governed T03 read path. Validate active checkout identity before collecting; do not infer compatibility solely from remote URL or directory name.

2. Resolve the selected ref to an immutable commit once, before committed reads. Capture HEAD/ref and available history boundaries; label unborn/no-Git/shallow cases without fabricating a baseline.

3. Pass the captured commit to every committed inventory/file/history read. A later HEAD change becomes pending work and never retargets the active capture.

4. Collect a working-tree overlay only when the requested scope needs it. Include additions, edits, deletions and both sides of moves. Capture content identity for mutable reads and mark unstable reads rather than mixing versions.

5. Use scoped inventory and batched revision-file reads before loading content. Prioritize project/module metadata and relevant documentation/tests; stop at explicit path/file/byte/time limits and record omissions.

6. Read project/dependency/build metadata as data. Reuse available host-evaluated facts only when their provenance matches; do not launch MSBuild, repository scripts or tests merely to obtain structural information.

7. Associate optional symbol/project facts with the existing semantic generation and snapshot. If immutable historical semantic facts are unavailable, report that limitation rather than creating a second Roslyn workspace.

8. Return a deterministic structural profile sorted with existing path conventions and separate discovery/analysis coverage. No database or model provider is required to obtain it.

## 7 Public Contracts and State Boundaries

A snapshot descriptor must distinguish stable local repository identity, checkout/worktree identity, selected ref and resolved immutable commit. Working-tree observations have their own captured source identities/digests and incompleteness state. Facts carry source revision/overlay, scope and omissions. A current semantic workspace cannot be presented as proof about another historical commit.

## 8 Project and File Changes

**Permitted host touch points:** H4 only for proven gaps; existing Git inventory/batched file reads and semantic/repository facts through host services. No new Git process execution or recursive repository crawler. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Trace revision arguments through actual Git calls and content provenance through overlays. Check upstream enumeration cost and ensure project inspection does not execute repository-controlled build logic.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| Moving HEAD | Advance HEAD between inventory and file reads. | All committed locators still reference the original target; the newer state is pending. | Integration |
| Dirty overlay | Add, edit, delete and rename files while observing a scoped overlay. | Mutable facts retain overlay identity; unstable reads are qualified, not commit-backed. | Integration |
| Identity separation | Open a copy, linked worktree, detached branch and different repository with similar paths. | Context identity is distinct or explicitly compatible under the chosen rule. | Unit/integration |
| Unavailable history | Use unborn HEAD, non-Git and shallow repositories. | Available facts are returned with precise gaps and no invented commit. | Integration |
| Bounds and hostile paths | Use oversized inventories, binary files, linked/outside paths and unusual names. | Existing policy confines reads and limits are honored with omissions. | Unit/integration |
| Semantic mismatch | Supply semantic facts for a different generation/checkout than the target. | Facts are withheld or labeled unavailable; no false historical attribution occurs. | Unit |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [ ] All committed facts belong to the captured target even if HEAD advances; dirty facts are explicitly separate.
- [ ] Repository copies, branches and worktrees cannot silently share incompatible current context.
- [ ] Capture is bounded before loading full source, uses existing readers and leaves repository files unchanged.
- [ ] Structural facts remain useful with unavailable history, inference or semantics and expose their limitations.
- [ ] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [ ] The user has explicitly accepted that reviewed implementation before any task test or documentation implementation begins.
- [ ] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

## 15 Risks

A profile can appear pinned while one helper silently defaults to HEAD. Working-tree hashes and status timestamps alone are not interchangeable evidence.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** Snapshot/overlay meaning, scope and structural coverage limitations. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Define stable identity/overlay fields using existing contracts; choose bounded initial profile scope without promising whole-repository coverage. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
