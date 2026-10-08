# T02 Add trusted activation and cancellation controls

**Status:** Complete; reviewed production implementation accepted and post-acceptance work validated.

**Delivery track:** M34 Repository Intelligence and Archeology.

**Prerequisites:** [T01](t01-dormant-assembly-boundary.md) complete, including its post-acceptance tests and documentation.

**Requirements and parent acceptance outcomes:** OPT-01–06, NQ-03–04; AC-15, AC-19.

**Sources:** [Parent requirements](threadsmith-repository-intelligence-and-archeology-requirements.md), [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md), [Scenario BA](../acceptance-scenarios.md#scenario-ba---repository-intelligence-activation-and-revocation), and [MTP-278](../manual-test-plan.md#mtp-278--repository-intelligence-activation-and-revocation).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

Controls are independent and visible; discovering stored data does not activate anything. Baseline configuration leaves recall/maintenance unchanged.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

Trusted repository-local activation and cancellation state, including an explicit scope/limits selection boundary. No scans, store initialization, model requests or unavailable analysis action may be started by this task.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [src/Threadsmith.App/ConfigurationBootstrap.cs](../../../src/Threadsmith.App/ConfigurationBootstrap.cs)
- [src/Threadsmith.App/EffectiveConfigurationPreflight.cs](../../../src/Threadsmith.App/EffectiveConfigurationPreflight.cs)
- [src/Threadsmith.Tools/RepositoryMemoryConfiguration.cs](../../../src/Threadsmith.Tools/RepositoryMemoryConfiguration.cs)
- [src/Threadsmith.Core/RepositoryMemoryOptionsProvider.cs](../../../src/Threadsmith.Core/RepositoryMemoryOptionsProvider.cs)
- [src/Threadsmith.Core/RepositorySettingsCoordinator.cs](../../../src/Threadsmith.Core/RepositorySettingsCoordinator.cs)
- [src/Threadsmith.Core/RepositoryIdentity.cs](../../../src/Threadsmith.Core/RepositoryIdentity.cs)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Trace how configuration distinguishes trusted user settings from repository-controlled data. Do not copy the memory configuration class or assume its activation semantics are suitable for this feature.

2. Select trusted user-owned storage keyed by stable local repository/worktree context. Never accept repository config, prompt content, a database's existence, or a model request as enablement authority.

3. Implement feature-owned control resolution with all defaults off. Invalid or unavailable trusted settings must not turn on a capability. Validate bounds without instantiating scanners, providers or stores.

4. Add a narrow host options snapshot and trusted control command. Show the selected repository/subsystem, optional history, provider identity and effective resource bounds before admitting baseline work.

5. Make each control independent. A baseline request must leave recall/maintenance unchanged; requesting one-off work must leave persistent settings unchanged. Unsupported actions return an explicit unavailable result.

6. Link admitted operations to existing host/session cancellation plus capability revocation. On disable, revoke admission first, cancel relevant work, invalidate cached eligibility and fence late publication. Cancel only work governed by the disabled control.

7. Fence settings capture and command execution to the active repository. Dispose linked cancellation registrations through the normal host lifecycle; a repository switch must not transfer old consent or operations.

8. Expose dormant/readiness and disabled reasons through existing command/result projections. Leave retained intelligence and curated memory untouched.

## 7 Public Contracts and State Boundaries

Represent the four independent controls explicitly: persistent intelligence, on-demand Archeology, automatic recall/enrichment, and automatic maintenance. Return an immutable repository-fenced snapshot with an activation generation or equivalent revocation fence. Controls express authorization, not readiness: recall/maintenance cannot secretly enable persistence or create missing data. An explicit one-off request gets invocation-scoped authority rather than permanently changing controls.

## 8 Project and File Changes

**Permitted host touch points:** H2, H10, H1 as needed; existing trusted settings, commands and host cancellation. Repository config may suggest limits, never grant activation authority. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Follow the trusted settings source rather than only checking a boolean. Inspect TOCTOU races between disablement and publication and the ownership/disposal of cancellation registrations.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| Control combinations | Exercise all 16 boolean combinations and invalid settings. | No control implicitly flips another; unsupported readiness is explicit without creating data. | Unit |
| Untrusted activation | Place enablement values in repository config/content and discover stored data. | Trusted activation remains off. | Integration |
| One-off authority | Request a single investigation with persistence disabled. | Only that invocation is authorized; settings remain unchanged. | Unit |
| Revocation race | Disable between admission, collection, model completion and publication. | Affected work is cancelled/fenced and cannot publish late. | Unit/integration |
| Repository change | Capture options, switch worktree/repository, then submit the old operation. | The stale context is rejected; authority does not transfer. | Unit |
| Data control | Disable/re-enable after existing retained knowledge is present. | Disable preserves records and user memory; re-enable does not auto-run a baseline. | Integration |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [x] All four controls default off and can be changed independently only through a trusted user action.
- [x] Repository content cannot activate the capability; existing data remains dormant until explicit authority is present.
- [x] Disablement prevents new affected operations and publication, cancels active affected work and preserves stored data.
- [x] Baseline/one-off requests expose scope/provider/limits and do not silently alter other controls.
- [x] Repository switches and stale settings snapshots cannot apply consent to a different context.
- [x] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [x] The user has explicitly accepted that reviewed implementation before any task test or documentation implementation begins.
- [x] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

The accepted implementation adds checkout-keyed user-owned controls, a thin App command adapter, a shared `/intelligence` command, bounded operation previews, and admission/revocation fences. A checkout-level settings lock and fresh read preserve independent changes from concurrent app instances; per-control revisions reject disable/re-enable races. Feature initialization remains lazy, and analysis remains explicitly unavailable pending later tasks. Disablement does not delete intelligence or curated memory. The clean-context review loop resolved concurrency and cancellation findings and ended without actionable production findings. The user then explicitly accepted the implementation before tests or product documentation were authored.

Post-acceptance validation: the solution build passed with zero warnings; feature tests passed (13); architecture/integration tests passed (330, with one optional live test skipped); CoreRuntime interaction tests passed (661, with two explicit measurement skips); repository lifecycle tests passed (38); `git diff --check` passed. Concurrent settings coordination and cancellation are exercised through two feature instances in one OS process; separate-process behavior remains source-reviewed. Physical terminal and live-provider behavior remain in MTP-278; no analysis operation exists yet to benchmark or observe under real load.

## 15 Risks

Configuration precedence can accidentally grant authority to the repository. A cancelled token alone does not prevent late non-cooperative results from being accepted.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** Activation authority, independent controls and disable-versus-delete behavior. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Choose the trusted storage location and command names by reusing the host's existing settings and interaction conventions; make worktree-sharing behavior explicit. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
