# T03 Route feature operations through host governance

**Status:** Proposed; implementation not started or accepted.

**Delivery track:** Proposed Repository Intelligence and Archeology capability; milestone registration follows T01 acceptance.

**Prerequisites:** [T02](t02-activation-and-cancellation.md) complete, including its post-acceptance tests and documentation.

**Requirements and parent acceptance outcomes:** ISO-04–06, NQ-03–05, AR-10; AC-18–19.

**Sources:** [Parent requirements](../threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

An explicit status operation has the same policy, receipts and visibility in CLI and interactive use. Nested work is observable and bounded.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

A feature-owned operation/tool shell and the narrow shared-host bridge it needs. Deliver a status operation; analysis operations remain unavailable until their owning tasks finish.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [src/Threadsmith.Tools/ToolInvocationPipeline.cs](../../../src/Threadsmith.Tools/ToolInvocationPipeline.cs)
- [src/Threadsmith.Tools/ToolContracts.cs](../../../src/Threadsmith.Tools/ToolContracts.cs)
- [src/Threadsmith.Tools/ToolRegistry.cs](../../../src/Threadsmith.Tools/ToolRegistry.cs)
- [src/Threadsmith.App/HostFoundation.cs](../../../src/Threadsmith.App/HostFoundation.cs)
- [src/Threadsmith.Core/OperationActivityContracts.cs](../../../src/Threadsmith.Core/OperationActivityContracts.cs)
- [src/Threadsmith.Execution/ToolEvidenceAdmission.cs](../../../src/Threadsmith.Execution/ToolEvidenceAdmission.cs)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Trace manual command submission, native model tool dispatch and internal host tool invocation to their common policy/execution owner. Write the call chain in the review handoff, not in premature product documentation.

2. Implement the tool in the feature project. Advertise only completed actions, using current schema generation and strict argument/resource-path validation rather than a second dispatcher.

3. Route manual and internal entry points into the same governed operation used by model calls. Never invoke a feature service directly if that skips permissions, sanitization, receipts or accounting.

4. Add only a missing generic invocation seam in the existing owner. Preserve the existing prepared/batched invocation semantics and obtain operation authority from the host, not caller-supplied labels.

5. For nested reads, retain the parent relationship and distinct invocation IDs. Check the concurrency model before awaiting a nested operation so the outer operation cannot hold the only permit needed by its child.

6. Charge each underlying tool/model operation once to the existing budget owner. Bound nested concurrency and expansion; status must not start repository analysis.

7. Reject all delegated feature invocations at execution and exclude them from child catalogs until T15. A supplied tool name or forged argument cannot bypass this restriction.

8. Use ordinary operation activity/progress and terminal outcome handling for success, unavailable, denied, failure and cancellation. Preserve headless/interactive equivalence and avoid a new renderer.

## 7 Public Contracts and State Boundaries

Carry repository/worktree, session/run/invocation identifiers, trusted activation snapshot, request mode/assignment authority, operation bounds and cancellation through existing host contracts. A result is a serializable DTO with classified completion, bounded payload and sanitized diagnostics. Use existing Tool<TInput,TResult> and registry conventions; do not expose feature implementation types to Core consumers.

## 8 Project and File Changes

**Permitted host touch points:** H3, H1, H2, H10. Use `ToolInvocationPipeline`, ordinary operation activity and existing rendering. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Inspect the actual underlying operation, not its displayed tool label. Prove that all entry points use the same execution owner; look for nested semaphore ownership and duplicate receipts.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| Entry-point parity | Invoke status manually, through a model tool and through authorized internal submission. | Equivalent authority, result and event semantics are observed. | Integration |
| Policy denial | Deny the operation or its source access before execution. | No feature service work occurs; the standard denial receipt is returned. | Integration |
| Delegated bypass | Submit a feature tool name directly from a child context. | Execution rejects it even if advertisement filtering is bypassed. | Unit/integration |
| Nested capacity | Use a single-category permit and a nested governed read. | Operation completes or fails boundedly; no circular permit wait occurs. | Integration |
| Accounting | Complete/retry/fail a nested read with correlated parent activity. | Each underlying operation is charged once, with no duplicate completion event. | Integration |
| Error surface | Cancel or throw during status/nested work with sensitive error text. | Existing sanitized classification and terminal lifecycle are preserved. | Unit/integration |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [ ] Manual, model-driven and internal status calls follow the same authority and receipt path with equivalent results.
- [ ] Disabled or unauthorized operations perform no feature work; delegated callers are rejected at runtime.
- [ ] Nested work has correlated visibility, bounded concurrency and exactly-once accounting without deadlock.
- [ ] Only completed actions are exposed and failure/cancellation produces one ordinary terminal outcome.
- [ ] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [ ] The user has explicitly accepted that reviewed implementation before any task test or documentation implementation begins.
- [ ] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

## 15 Risks

A wrapper can give the appearance of governed execution while directly calling a reader or provider. Nested invocations can deadlock if they share a permit held by the parent.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** Operation boundary, authority and diagnostic interpretation. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Determine whether an existing internal invocation API is sufficient before adding any host bridge. Its contract must remain generic host infrastructure. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
