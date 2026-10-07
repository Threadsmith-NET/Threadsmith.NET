# T08 Add local canonical records and atomic storage

**Status:** Proposed; implementation not started or accepted.

**Delivery track:** Proposed Repository Intelligence and Archeology capability; milestone registration follows T01 acceptance.

**Prerequisites:** [T07](t07-invocation-only-archeology.md) complete, including its post-acceptance tests and documentation.

**Requirements and parent acceptance outcomes:** IN-01–09, NQ-01, MT-07, ISO-02; AC-05, AC-13, AC-18.

**Sources:** [Parent requirements](threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

Durable records round-trip locally with provenance and revision consistency; disabled and invocation-only paths never open this store.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

Feature-owned durable schemas and atomic repository-local storage, activated lazily. Do not duplicate host events, existing memory records, migration infrastructure or an inference lifecycle.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [src/Threadsmith.Persistence/Migrations.cs](../../../src/Threadsmith.Persistence/Migrations.cs)
- [src/Threadsmith.Persistence/DefaultMigrations.cs](../../../src/Threadsmith.Persistence/DefaultMigrations.cs)
- [src/Threadsmith.Persistence/SqliteManagedRepositoryMemoryStore.cs](../../../src/Threadsmith.Persistence/SqliteManagedRepositoryMemoryStore.cs)
- [src/Threadsmith.Persistence/RepositoryMemoryDatabasePath.cs](../../../src/Threadsmith.Persistence/RepositoryMemoryDatabasePath.cs)
- [src/Threadsmith.Persistence/RetentionService.cs](../../../src/Threadsmith.Persistence/RetentionService.cs)
- [Directory.Packages.props](../../../Directory.Packages.props)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Inspect the existing migration/connection/artifact/retention facilities and their actual callers. In particular, account for MigrationRunner's memory-specific version-10 behavior before choosing how to reuse it.

2. Choose a lazily opened local feature store isolated from normal session/memory migration. Reuse a suitable shared mechanism or narrowly parameterize its existing owner; never copy the runner or move feature schema logic into Persistence.

3. Define schema versions and normalized record ownership. Keep reusable evidence separate from excerpts/capsules and distinguish source identity, item identity and revision identity.

4. Represent the eight item kinds and independent assessment dimensions without a universal nullable field bag. Preserve unknown values explicitly rather than inventing rationale/scope.

5. Implement transactions that publish internally coherent groups: items, references, relationships and revision-linked capsules together. Candidates and incomplete work cannot become recallable merely because rows exist.

6. Add optimistic revision checks and repository fencing to writes. Store provenance/continuations sufficient for later T10 recovery without creating another host event stream.

7. Open/create/migrate only after trusted persistent activation and only for the intended repository context. Preserve one-off operation by ensuring its dependency graph never resolves this store.

8. Bound queries and serialization, enforce referential integrity, and classify unavailable/corrupt/migration-failed storage. Roll back failed publication/migration and preserve previous readable data; defer final reconciliation policy to T09.

## 7 Public Contracts and State Boundaries

Persist stable host-owned record IDs and versioned item revisions, normalized evidence, episode constituents, typed relationships, uncertainties, capsule source revisions and run/checkpoint provenance. Applicability, confidence, evidence classification, freshness and coverage are separate fields/concepts. Support all eight required item kinds with bounded kind-specific payloads. Feature persistence may use an internal database adapter; database types must not escape into public host DTOs.

## 8 Project and File Changes

**Permitted host touch points:** H5, H1, H11 only for required package pins. Prefer a lazily opened repository-scoped feature database using suitable shared SQLite infrastructure, avoiding unconditional migration of ordinary session/memory storage. Check the existing migration runner's memory-specific behavior and extend its owner narrowly if reuse requires it; do not copy a migration engine. Keep all feature schema logic in the new assembly. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Inspect migration version semantics and all factory resolution sites. Verify the design does not create a second store for session events or memory under the guise of feature isolation.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| Record round trip | Persist all item kinds, unknown optional values and independent dimensions. | Values, revisions and source relationships round-trip without conflation. | Unit/integration |
| Atomic failure | Fail between item/evidence/relationship/capsule writes. | No partial published group is visible and prior revision remains readable. | Integration |
| Concurrency | Publish using an old expected revision from another operation. | Conflict is explicit; no lost update occurs. | Integration |
| Migration lifecycle | Reopen an upgraded database and inject a migration failure. | Successful migrations are idempotent; failure preserves old state. | Integration |
| Inactive storage | Open normal app, disabled repo and one-off investigation. | Feature store factory remains unopened and no feature migration runs. | Integration |
| Repository isolation | Use wrong-context paths and linked storage targets. | Writes are rejected or confined by the established path/identity policy. | Integration |
| Dependency boundaries | Inspect public contracts and existing-project changes. | Database/framework types and feature schemas remain in their permitted owners. | Architecture |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [ ] Canonical feature data resides locally with all required kinds, independent dimensions and reusable provenance.
- [ ] Publication is atomic and revision-checked; readers cannot observe dangling references or mismatched capsules.
- [ ] Feature migrations are lazy and cannot run through default session/memory startup or one-off investigation.
- [ ] Shared persistence infrastructure is reused without a copied runner, duplicate host event store or feature algorithms in Persistence.
- [ ] Failed migrations/writes leave the last valid data intact and produce bounded diagnostics.
- [ ] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [ ] The user has explicitly accepted that reviewed implementation before any task test or documentation implementation begins.
- [ ] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

## 15 Risks

The existing runner has memory-specific migration policy. A generic-looking interface does not make that behavior suitable for a separate schema; eager migrations would violate disabled compatibility.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** Storage ownership, format/version boundaries, data locations and recovery limitations. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Settle physical storage and migration reuse with a concrete call-site analysis. Exact table and type names remain implementation choices constrained by atomicity, isolation and bounded access. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
