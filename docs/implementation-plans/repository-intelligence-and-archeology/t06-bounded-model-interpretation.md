# T06 Add bounded interpretation using shared model execution

**Status:** Proposed; implementation not started or accepted.

**Delivery track:** Proposed Repository Intelligence and Archeology capability; milestone registration follows T01 acceptance.

**Prerequisites:** [T05](t05-evidence-and-episode-candidates.md) complete, including its post-acceptance tests and documentation.

**Requirements and parent acceptance outcomes:** PR-01–03, AR-05–08, IN-02–04, NQ-02–05; AC-03–05.

**Sources:** [Parent requirements](../threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

One packet can produce validated candidate interpretations or an explicit limitation using the configured provider; unavailable inference leaves deterministic functions usable.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

Bounded semantic interpretation over host-collected packets using the existing model execution owner. Deliver candidate results and uncertainty, not canonical writes or autonomous tool access.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs](../../../src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs)
- [src/Threadsmith.Execution/RepositoryMemoryDispatch.cs](../../../src/Threadsmith.Execution/RepositoryMemoryDispatch.cs)
- [src/Threadsmith.Execution/ModelRequestBudgetUsage.cs](../../../src/Threadsmith.Execution/ModelRequestBudgetUsage.cs)
- [src/Threadsmith.Execution/Budget.cs](../../../src/Threadsmith.Execution/Budget.cs)
- [src/Threadsmith.App/ModelComposition.cs](../../../src/Threadsmith.App/ModelComposition.cs)
- [src/Threadsmith.Core/JsonOutputSanitizer.cs](../../../src/Threadsmith.Core/JsonOutputSanitizer.cs)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Trace selected-model resolution, trust/transmission policy, request preparation, transport dispatch, usage receipts, retries and cancellation in the existing execution path. Identify a real reusable entry point; do not assume an IModelProvider call implements these responsibilities.

2. If the host lacks a bounded internal inference entry point, extend its existing owner with a narrow generic request/result seam. Keep feature operation schemas/interpretation in the feature assembly and shared model lifecycle in its current owner.

3. Implement focused operations for significance assessment, intent extraction, failure/reversal interpretation, reevaluation, relationship classification and selected cross-episode synthesis. Reuse one bounded request/execution path rather than one loop per operation.

4. Build prompts from registered deployed assets using the existing loader/catalog. Supply only the packet's evidence and question as untrusted data; wording assets must not control authority, schema or budgets.

5. Resolve the model once per admitted operation according to existing host rules. Fit the packet, schema and output reserve to its effective capacity; refuse or request narrower scope when required evidence cannot fit.

6. Validate structured output size, fields, enum values, evidence IDs, paths/revisions and relationship endpoints. Unsupported claims are rejected/deferred; a plausible story, temporal adjacency or model confidence cannot establish causality.

7. Allow uncertainty, insufficient evidence and no significant candidate as first-class outcomes. Accept T05 current-snapshot-only packets with no episodes. Implement live evidence drill-down through structured expansion requests from this internal interpreter: a request identifies invocation-scoped evidence IDs and bounded ranges/detail, or bounded additional source needs within the admitted scope. Before any resolution, the host validates the live operation identity, evidence allowlist, pinned target, collection mode, path/transmission authority and remaining cumulative budget. Resolve accepted requests through T05 and the existing governed read path, return bounded sanitized evidence to the interpreter, and continue through this same shared model execution path. Unknown/expired IDs, scope widening and attempts to enable history in current-snapshot-only mode are rejected or reported unavailable; no autonomous model tool access is granted.

8. Bound retries and correction attempts using existing classifications. Ignore late results after cancellation/revocation and retain ordinary activity/usage provenance; provider unavailability must not break deterministic facts or existing knowledge.

## 7 Public Contracts and State Boundaries

An interpretation request carries an operation kind, pinned snapshot, evidence allowlist, bounded packet with collection mode and remaining host budget. Output is a host-validated candidate envelope with references, classification, uncertainty and optional bounded expansion request. Expansion is an internal request/validate/read/continue exchange within the admitted operation, correlated through ordinary activity and usage; it is not a caller-interactive channel on ITool.ExecuteAsync. Model-provided identifiers do not become durable IDs. No provider SDK types cross the boundary. Production prompt assets are deployed application inputs; their reference documentation remains deferred until acceptance.

## 8 Project and File Changes

**Permitted host touch points:** H3, H2 and H11 if needed. First trace existing model selection, capacity admission, dispatch, retry, usage and activity. Extend a shared host entry point if necessary; adding an `IModelProvider` call in the feature is not sufficient reuse. No child-agent creation to simulate an inference service. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Prove the actual model dispatch uses all host governance boundaries. Inspect whether hidden direct provider calls, independent retries, or unmetered expansion were added behind a shared interface.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| Live evidence expansion | The internal interpreter requests more detail for an allowed live evidence ID, then submits unknown IDs, wider scope and a history request for a current-snapshot-only packet. | Allowed detail returns through governed T05 reads and shared model continuation with ordinary activity/usage; invalid requests cannot expand authority, mode or cumulative budgets. Empty episode collections remain valid. | Unit/integration |
| Forged evidence | Return unknown IDs, commits, paths or relationship endpoints. | Output is rejected/deferred before any candidate can publish. | Unit |
| Unsupported causality | Return strong causal intent backed only by adjacent commits. | The unsupported claim is not accepted as documented/reconstructed rationale. | Unit/evaluation |
| Malformed/large result | Return invalid schema, excessive arrays or output bytes. | Bounded validation fails safely with classified diagnostics. | Unit |
| Provider lifecycle | Exercise unavailable provider, transient retry, cancellation and late completion. | Existing retry/usage rules apply once; revoked output cannot be accepted. | Integration |
| Capacity | Supply packets near selected model context limits and request expansions. | Input/output reserve and cumulative work caps hold. | Unit/integration |
| Adversarial source | Embed executable instructions and secret-like text in repository evidence. | Evidence remains untrusted; normal sanitization/transmission policy applies. | Integration |
| Cross-episode synthesis | Infer a pattern from several selected episodes with conflicting evidence. | References remain traceable and conflict/uncertainty is retained. | Unit/evaluation |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [ ] All inference uses shared host selection, policy, request capacity, dispatch, telemetry, usage and cancellation rather than an SDK call or copied loop.
- [ ] Every accepted interpretation references supplied, validated evidence and preserves independent confidence, evidence class and applicability.
- [ ] No-significant-finding, uncertainty and provider-unavailable outcomes are usable and do not fabricate required fields.
- [ ] Packet expansion, retries and synthesis cannot exceed the original scope/budget or bypass validation.
- [ ] Live evidence inspection is reachable through the internal interpreter's validated expansion exchange, including current-snapshot-only packets without episodes; no caller-interactive tool channel is assumed.
- [ ] Prompt assets are registered through the deployed catalog; reference documentation is completed only after code acceptance.
- [ ] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [ ] The user has explicitly accepted that reviewed implementation before any task test or documentation implementation begins.
- [ ] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

## 15 Risks

A shared provider facade is not shared execution. Invalid model references can appear structurally valid, and output correction can become an unbounded second inference loop.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** Inference operations, provenance and limits; prompt catalog/reference updates in the same completed increment as any new prompt assets. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Choose the existing host entry point or narrowly extend its owner; settle schema versions and packet/operation defaults without adding a feature model scheduler. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
