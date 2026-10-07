# T20 Complete integrated acceptance and measured evaluation

**Status:** Proposed; implementation not started or accepted.

**Delivery track:** Proposed Repository Intelligence and Archeology capability; milestone registration follows T01 acceptance.

**Prerequisites:** [T19](t19-correction-export-and-reset.md) complete, including its post-acceptance tests and documentation. All earlier task gates must also be complete.

**Requirements and parent acceptance outcomes:** NQ-03, NQ-06 and all parent AC-01–25.

**Sources:** [Parent requirements](threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

A requirement-to-evidence matrix identifies verified results and remaining limitations; no claim of unchanged performance rests on source inspection alone.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

Integrated implementation audit, scoped gap fixes and post-acceptance quality/performance evaluation across AC-01–25. Do not introduce new product scope, infrastructure, unconditional activation or a second execution path.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [docs/implementation-plans/repository-intelligence-and-archeology/threadsmith-repository-intelligence-and-archeology-requirements.md](threadsmith-repository-intelligence-and-archeology-requirements.md)
- [tests/Threadsmith.Architecture.Tests/DependencyDirectionTests.cs](../../../tests/Threadsmith.Architecture.Tests/DependencyDirectionTests.cs)
- [docs/implementation-plans/acceptance-scenarios.md](../../../docs/implementation-plans/acceptance-scenarios.md)
- [docs/implementation-plans/manual-test-plan.md](../../../docs/implementation-plans/manual-test-plan.md)
- [docs/implementation-plans/milestones.md](../../../docs/implementation-plans/milestones.md)
- [src/Threadsmith.App/HostFoundation.cs](../../../src/Threadsmith.App/HostFoundation.cs)
- [src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs](../../../src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Read all completed task handoffs and the full parent requirements. Trace the integrated manual/model/internal paths through activation, execution, collection, inference, publication, freshness, context and presentation.

2. Build a review handoff matrix for AC-01–25 and identify missing evidence or behavioral gaps. Use existing tests and demonstrations only before this task's acceptance; do not author new fixtures, benchmark harnesses or product documentation.

3. Review the disabled path from startup/open/restore through memory and exploration, checking for feature storage initialization, subscriptions, provider calls or scans that earlier increments may have introduced.

4. Exercise existing checks for nonpersistent cleanup/replay, persistent recovery, all request modes, child allowlists, dirty invalidation and frozen-context reuse. Distinguish measured behavior from code inspection and disclose unavailable runtime environments.

5. Fix only concrete gaps in the owning feature code or original host integration seam. Reuse each earlier task's constraints and return every changed production diff through fresh adversarial review before requesting acceptance.

6. Before later measurements, propose representative repository/history scales, controlled inference cases, predefined coding tasks with expected relevant items, irrelevant tasks, and concrete numerical resource/quality thresholds. Settle evaluation choices with the user; do not invent measured targets or declare unmeasured performance acceptable.

7. Run the required clean-context review loop over the integrated final diff and surrounding code. Request user acceptance only once applicable findings are resolved; even if no new code fix is necessary, obtain acceptance of the integrated implementation before new evaluation/tests/docs.

8. After acceptance, implement missing unit/integration/architecture tests and the agreed evaluation fixtures. Measure extraction faithfulness and retrieval utility separately plus resource growth/time to first activity; implement only necessary owning documentation updates.

9. If post-acceptance tests/evaluation require production changes, repeat the code review/acceptance gate for those changes before expanding tests/docs for them. Complete the capability only when required outcomes and agreed thresholds pass or scope changes receive explicit user approval.

## 7 Public Contracts and State Boundaries

The acceptance evidence matrix associates requirement/AC IDs with exact entry point, approved code revision, existing checks, later post-acceptance cases, measured results and limitations. Pre-acceptance evidence is a review handoff, not new product documentation or an evaluation harness. Evaluation inputs specify expected supported conclusions and relevant guidance before output is scored; extraction correctness and retrieval usefulness are assessed separately.

## 8 Project and File Changes

**Permitted host touch points:** No new host path. Any fix must use its original H1–H11 owner and corresponding task gate. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Review the whole integrated feature through real entry points and relevant code outside the final task diff. Search for competing paths accumulated across increments and identify missing runtime evidence explicitly.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| End-to-end parent acceptance | Implement any remaining AC-01–25 checks through actual user/tool entry points. | Each specified outcome has direct evidence; plan conformity alone does not count. | Integration/end-to-end |
| Disabled comparison | Compare ordinary startup/open/restore/tools/memory/explore with feature installed but off. | No feature initialization/work occurs; measured overhead meets the agreed bound. | Performance/integration |
| Mode/retention matrix | Cross eligible modes, Stateless, child contexts and persistent/invocation-only modes. | No unauthorized lookup, packet retention, resume or context leakage occurs. | Integration |
| Extraction evaluation | Use fixed evidence with known support/conflicts and no rationale in selected cases. | Unsupported claims, evidence fidelity and uncertainty are scored separately from retrieval. | Evaluation |
| Retrieval evaluation | Use predefined relevant/irrelevant tasks before examining selected capsules. | Relevance, missed consequential guidance, redundancy and budget fit meet agreed thresholds. | Evaluation |
| Scale and responsiveness | Increase repository/history/scope size with controlled budgets. | Measure time to first activity, memory, processes/model calls and bytes/tokens; work remains bounded. | Performance |
| Recovery and races | Cancel/restart/disable/reset across publication, checkpoints and context reuse. | Valid state survives and no stale/unauthorized result is admitted. | Integration |
| Package boundaries | Run dependency/prompt deployment and affected existing regression gates. | One-assembly ownership, deployed assets and current application contracts remain valid. | Architecture/integration |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [ ] AC-01–25 map to real integrated paths with reviewable evidence and no unsupported clean/runtime claims.
- [ ] All feature implementation remains in one production assembly except enumerated narrow host touch points; no competing execution or lifecycle machinery exists.
- [ ] Disabled application behavior, isolated request/child modes, retention boundaries and freshness admission hold end to end.
- [ ] Before this task's acceptance, no new test/evaluation harness or product documentation is written; integrated code and gap fixes receive clean-context review first.
- [ ] After acceptance, agreed extraction/retrieval and resource evaluations are measured, required checks pass and remaining limitations are explicitly documented.
- [ ] Milestone completion is recorded only after all task gates and capability exit criteria are satisfied.
- [ ] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [ ] The user has explicitly accepted that reviewed implementation before any task test or documentation implementation begins.
- [ ] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

## 15 Risks

Passing isolated unit tests can conceal broken host integration. A plausible report does not measure extraction fidelity or recall usefulness, and a short sample does not establish large-history performance.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** Only changed acceptance scenarios and executable manual procedures, linked by their actual assigned IDs; user/operator guidance; measured limitations and active milestone status when exit criteria pass. Do not rewrite historical plans or completed milestone details. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Agree representative data, expected conclusions/relevant items and numerical pass criteria before post-acceptance harness authoring. Do not weaken parent outcomes to obtain a clean review. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
