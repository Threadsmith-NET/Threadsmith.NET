# T01 Establish the dormant assembly boundary

**Status:** Proposed; implementation not started or accepted.

**Delivery track:** Proposed Repository Intelligence and Archeology capability; milestone registration follows T01 acceptance.

**Prerequisites:** Parent requirements approval.

**Requirements and parent acceptance outcomes:** ISO-01–07, OPT-05; AC-15, AC-18.

**Sources:** [Parent requirements](../threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

The app builds and opens normally; an unused feature causes no feature-owned work. The standalone feature project can compile before architecture-test inventory changes.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

A compiled, lazily reachable production assembly with no active feature behavior. Do not add tools, schema, analysis, inference or speculative public service interfaces.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [src/Threadsmith.App/HostFoundation.cs](../../../src/Threadsmith.App/HostFoundation.cs)
- [src/Threadsmith.App/ApplicationComposition.cs](../../../src/Threadsmith.App/ApplicationComposition.cs)
- [src/Threadsmith.App/Threadsmith.App.csproj](../../../src/Threadsmith.App/Threadsmith.App.csproj)
- [src/Threadsmith.Tools/Threadsmith.Tools.csproj](../../../src/Threadsmith.Tools/Threadsmith.Tools.csproj)
- [Directory.Build.props](../../../Directory.Build.props)
- [Directory.Packages.props](../../../Directory.Packages.props)
- [tests/Threadsmith.Architecture.Tests/DependencyDirectionTests.cs](../../../tests/Threadsmith.Architecture.Tests/DependencyDirectionTests.cs)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Inspect the adjacent project files and the dependency gate before editing. Record the actual App startup and shutdown owners; identify where an optional registration can remain unresolved.

2. Create the SDK-style production project under src and add it to the existing classic solution. Inherit nullable, language, analyzers and package settings; do not introduce package versions or custom compiler exceptions.

3. Add only the smallest internal composition/factory entry point needed for later explicit activation. Prefer an internal concrete implementation over a public interface with no consumer.

4. Wire App to the feature assembly without constructing services on repository open, restore, help/tool inventory generation, or ordinary conversation startup. Avoid static constructors, hosted services and eager option validation that reads feature storage.

5. Keep Core and all other existing consumers free of references to the feature implementation. If a DTO is actually consumed, put only that boundary contract in the appropriate existing host contract project.

6. Build the feature and affected App projects with existing repository commands. Inspect the actual default startup path and report the expected architecture-inventory gap without changing the test yet.

7. After clean review and user acceptance, add the dedicated feature test project and focused architecture-gate entries. Register the new milestone through the existing governance files without changing completed milestone contracts.

## 7 Public Contracts and State Boundaries

Use the existing App composition lifecycle. The production project is Threadsmith.RepositoryIntelligence, with its matching root namespace. Start with Core as the only project dependency unless executable code demonstrates another need. Do not add a separate abstractions, persistence, Archeology or worker project. A factory may be held by composition, but neither factory creation nor resolution on the disabled path may open storage or inspect a repository.

## 8 Project and File Changes

**Permitted host touch points:** H1; H2 only if a consumed boundary is necessary. No alternate bootstrap or service container. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Trace lazy construction through actual startup and tool-inventory enumeration. Challenge registrations whose constructors indirectly open a database. Verify the new project cannot become a shortcut around dependency direction.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| New project references | Parse solution/project references and exported boundary types. | Only the approved edges exist; Core has no feature dependency and SDK/Roslyn/terminal types do not escape. | Architecture |
| Dormant registration | Construct the normal host with feature factories observable after acceptance; open and restore a repository. | Factories remain unresolved and no feature artifact or task appears. | Integration |
| Ordinary operations | Run the existing memory, ordinary tool and exploration paths while disabled. | Existing outcomes remain available without feature calls. | Integration |
| Application shutdown | Start and stop with the assembly installed but unused. | No feature resources or shutdown waits are added. | Integration |
| Build/package inheritance | Load the project using shared settings. | Target is .NET 10; central versions and existing compiler rules remain effective. | Architecture |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [ ] Exactly one new production project is present and the existing solution/App can reference it without cycles or external type leakage.
- [ ] Default startup, repository open/restore, existing tools and code exploration contain no feature initialization, storage access, scanning, inference or recurring work.
- [ ] No speculative tool/API, alternate service provider, renderer, repository reader or background host has been added.
- [ ] Before acceptance, architecture-test edits and the test project are absent; their pending work is disclosed. After acceptance they are implemented and the dependency gate passes.
- [ ] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [ ] The user has explicitly accepted that reviewed implementation before any task test or documentation implementation begins.
- [ ] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

## 15 Risks

A lazy-looking registration can still evaluate arguments eagerly. Architecture project enumeration is intentionally incomplete until post-acceptance work; this is not permission to hide the gap.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** Project ownership/dependency contract and new milestone registration without changing completed milestones. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Choose only the minimal registration shape needed now. Additional references require a demonstrated call site, not anticipated future usefulness. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
