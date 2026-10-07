# T19 Deliver correction, suppression, export and reset

**Status:** Proposed; implementation not started or accepted.

**Delivery track:** Proposed Repository Intelligence and Archeology capability; milestone registration follows T01 acceptance.

**Prerequisites:** [T18](t18-automatic-maintenance-triggers.md) complete, including its post-acceptance tests and documentation.

**Requirements and parent acceptance outcomes:** OPT-04, MT-08, IN-08–09, NQ-01, NQ-04–05; AC-12, AC-14, AC-19.

**Sources:** [Parent requirements](../threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

User controls retained intelligence; export is a projection, corrections survive maintenance, reset is distinct from disablement.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

User-attributed correction/dispute, recall suppression, bounded provenance-preserving export and explicit feature-only reset. No alternate report store/renderer or deletion of independently curated memory/session history.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [src/Threadsmith.Tools/ToolInvocationPipeline.cs](../../../src/Threadsmith.Tools/ToolInvocationPipeline.cs)
- [src/Threadsmith.Core/RepositoryPathPolicy.cs](../../../src/Threadsmith.Core/RepositoryPathPolicy.cs)
- [src/Threadsmith.Core/JsonOutputSanitizer.cs](../../../src/Threadsmith.Core/JsonOutputSanitizer.cs)
- [src/Threadsmith.Persistence/RetentionService.cs](../../../src/Threadsmith.Persistence/RetentionService.cs)
- [src/Threadsmith.Core/ManagedMemoryServiceContracts.cs](../../../src/Threadsmith.Core/ManagedMemoryServiceContracts.cs)
- [src/Threadsmith.Core/OperationActivityContracts.cs](../../../src/Threadsmith.Core/OperationActivityContracts.cs)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Add explicit control operations through the existing feature tool/command path. Use host authority for user correction and destructive reset; the model cannot manufacture user attribution or broaden a deletion target.

2. Apply corrections and disputes through T09 revision checks and provenance, preserving prior claims and contrary evidence. Suppression must update automatic eligibility and cached context invalidation immediately.

3. Ensure T17/T18 maintenance respects correction/dispute/suppression state. Surface new conflicts rather than clearing a user preference simply because a model generated different output.

4. Project export from a coherent snapshot in bounded pages/streaming output using existing artifact and presentation facilities. Include schema/target, item/revision, supporting/conflicting locators, assessments, coverage and unavailable-source labels.

5. Keep export path validation, sanitization and overwrite behavior in existing host authority. Never serialize secrets/raw provider payloads or resolve all historical source bodies by default.

6. For reset, revoke activity/admission and advance an operation/store generation fence before drain/delete. Validate resolved ownership and linked-path protections; target only feature-owned data, indexes and scratch approved for reset.

7. Delete through established filesystem/retention facilities after cancellation/drain or safe writer fencing. Prevent a late model result/checkpoint from recreating the reset store.

8. Leave ordinary retained session receipts and independent memory under their existing owners. Mark source links unavailable honestly; disabling alone performs no deletion. Re-enable after reset requires fresh explicit work.

## 7 Public Contracts and State Boundaries

Correction/dispute records identify target item/revision, actor attribution, scope and expected revision. Suppression affects automatic recall without erasing evidence. Export is a versioned projection over coherent canonical records with snapshot/provenance/source-availability labels. Reset authority names the exact repository-owned feature data and is distinct from disablement; reset invalidates the store generation used by all in-flight writers.

## 8 Project and File Changes

**Permitted host touch points:** H10, H3; H5 only for shared retention/artifact hooks. Existing authority, command and projection paths; no report store or renderer. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Trace reset fencing through every write path and inspect resolved deletion targets. Challenge export paths that preload the whole graph or read all source bodies; verify no memory/session deletion is smuggled into cleanup.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| Correction conflict | Correct a claim and refresh with contradictory new model output. | User attribution persists; conflict is explicit instead of silent overwrite. | Unit/integration |
| Suppression invalidation | Suppress an already-selected capsule, restart and refresh. | Automatic eligibility and cached selection remain suppressed until changed explicitly. | Integration |
| Coherent export | Export while another operation revises items/relationships. | Output is a coherent bounded snapshot with matching revisions/provenance. | Integration |
| Export safety | Use hostile paths, escaped text, missing sources and large graphs. | Existing path/sanitization/bounds apply; unavailable evidence is labeled. | Unit/integration |
| Reset race | Reset during inference/publication/checkpoint write. | No late writer recreates deleted canonical state. | Integration |
| Ownership isolation | Attempt cross-repository/linked-path reset and inspect memory/session stores. | Wrong targets are rejected; unrelated user data remains intact. | Integration |
| Disable versus reset | Disable and re-enable without reset, then explicitly reset. | Disable retains data; only authorized reset removes feature-owned records. | Integration |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [ ] Correction/dispute and suppression retain attribution, scope and prior evidence and survive refresh.
- [ ] Export is bounded, human-readable and reproducible from coherent canonical records with useful provenance.
- [ ] Reset is explicit, repository-confined and resistant to in-flight resurrection; it is separate from disablement.
- [ ] User memory and ordinary session retention remain intact with honest broken-source links.
- [ ] Existing control, artifact, policy and rendering owners are reused.
- [ ] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [ ] The user has explicitly accepted that reviewed implementation before any task test or documentation implementation begins.
- [ ] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

## 15 Risks

Deletion races can resurrect data after the user believes it is gone. Export can violate bounded work or disclosure policy if it serializes internal model packets.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** Correction/dispute/suppression, export interpretation, retention and reset consequences. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Choose existing artifact/export conventions and exact feature-owned reset targets. Define conservative behavior if writers cannot be safely drained rather than deleting active foreign state. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
