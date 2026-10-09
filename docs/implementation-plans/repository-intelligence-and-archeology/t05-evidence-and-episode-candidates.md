# T05 Build normalized evidence and bounded episode candidates

**Status:** Complete; reviewed production implementation accepted and post-acceptance tests and documentation validated.

**Delivery track:** M34 Repository Intelligence and Archeology.

**Prerequisites:** [T04](t04-pinned-identity-and-structural-facts.md) complete, including its post-acceptance tests and documentation.

**Requirements and parent acceptance outcomes:** AR-01–04, AR-07–08, IN-06–08, ON-06; AC-03–05.

**Sources:** [Parent requirements](threadsmith-repository-intelligence-and-archeology-requirements.md) and [implementation plan](../threadsmith-repository-intelligence-and-archeology-implementation-plan.md).

**Mandatory order:** Implement production code → run a clean-context adversarial review → address applicable, valid and reasonable findings and repeat fresh reviews until clean → ask the user to accept this task's implementation → wait for explicit acceptance → implement unit tests, other required tests and documentation → validate and complete. Plan approval, a clean review and silence are not user acceptance.

## 1 Objective

A narrow question yields a bounded packet with reproducible provenance, using current-snapshot evidence alone or optional historical episode candidates; no full-history preload is required.

## 2 Architectural Context

Read [AGENTS.md](../../../AGENTS.md), [planning governance](../planning-governance.md), [shared implementation contract](../00-shared-context.md#g-implementation-document-template-and-agent-instructions), [C# guardrails](../../guardrails/portable-csharp-guardrails.md), [context policy](../../architecture/context-policy.md), [ADR-31](../../architecture/adr-31-bounded-conversational-continuity.md) and [ADR-6](../../architecture/adr-06-event-oriented-durable-session-model.md). Confirm `git rev-parse --show-toplevel` resolves to `C:/source/repos/Threadsmith` before edits, builds or tests. Inspect current implementations and prerequisite changes before designing abstractions. Do not stage, commit, push or use another checkout.

All feature-specific production code belongs in `src/Threadsmith.RepositoryIntelligence`. Core must not reference that assembly. Use host-owned DTOs and existing authority, execution, cancellation, persistence infrastructure and presentation. **No duplicated execution paths, readers, stores for the same state, schedulers or renderers.** Extend an existing owner narrowly when a required primitive is missing.

## 3 Scope

Question-scoped evidence normalization, current-snapshot-only packet construction, optional deterministic episode nomination and bounded packet composition. No semantic truth claims, new inference loop or persistent evidence cache.

## 4 Non-Scope

Do not implement later tasks or refactor unrelated infrastructure. Before explicit user acceptance, do not create or modify unit tests, integration tests, fixtures, snapshots, benchmark/evaluation harnesses, or product/user/operator/architecture documentation. This task file is an authorized planning specification, not permission to implement its listed tests. Running existing tests is permitted. Missing required verification must be reported, not concealed.

## 5 Current State and Required Reads

- [src/Threadsmith.Core/RepositoryInventoryContracts.cs](../../../src/Threadsmith.Core/RepositoryInventoryContracts.cs)
- [src/Threadsmith.Workspaces/GitQueryService.cs](../../../src/Threadsmith.Workspaces/GitQueryService.cs)
- [src/Threadsmith.Workspaces/GitQueryService.Inventory.cs](../../../src/Threadsmith.Workspaces/GitQueryService.Inventory.cs)
- [src/Threadsmith.Workspaces/GitQueryService.ShowFiles.cs](../../../src/Threadsmith.Workspaces/GitQueryService.ShowFiles.cs)
- [src/Threadsmith.Core/RepositoryPathPolicy.cs](../../../src/Threadsmith.Core/RepositoryPathPolicy.cs)
- [src/Threadsmith.Context/SourceEvidence.cs](../../../src/Threadsmith.Context/SourceEvidence.cs)

Also inspect production code introduced by the prerequisites. These are starting points, not proof that an API already satisfies the task; follow actual callers and ownership before editing.

## 6 Proposed Design and Implementation Steps

1. Normalize the requested question, paths, concepts, symbols or commit range using shared identity/path/concept rules. Capture an explicit current-snapshot-only or history-enabled collection mode with the T04 pinned target. Resolve symbol anchors through available host services and record unresolved targets.

2. In current-snapshot-only mode, select scoped files, documents, project metadata and available snapshot-matching structural facts from T04's bounded inventory. Read committed content at the pinned revision and any authorized overlay at its captured identity. Do not discover commit frontiers, walk ancestry/history, read historical patches or nominate episodes. Reading a file at the selected current snapshot is permitted and is not historical enrichment.

3. Only in history-enabled mode, select a bounded commit/change frontier before reading patches. Prioritize relevant scope/dependency changes, renames, reversals and associated documentation/tests. Use deterministic tie breaking and record why changes were selected or excluded. Extend the existing Git query owner only if an actual ancestry or continuation primitive is missing; never create a feature Git subprocess implementation.

4. Group selected historical changes into candidate episodes with explicit constituent commits and bounds. Allow multiple commits per episode and multiple derived candidates later; temporal adjacency is a signal, not causality. Current-snapshot-only packets have an empty episode collection and explicitly record history as not requested, with no claim of historical coverage. No synthetic episode or historical predecessor is required to interpret a current-state unit through T06.

5. Create reusable evidence IDs from validated source identity within the selected repository context. Deduplicate evidence without dropping distinct ranges, conflicting revisions or opposing sources.

6. Fetch bounded excerpts through existing policy-aware batched readers. Preserve binary/missing/redacted/truncated classifications and actual historical revisions; never treat test source as execution evidence.

7. Compose both modes through the same evidence normalization, policy-aware readers, provenance, sanitization and packet budgeting. Account for both input and serialized-output limits, including JSON escaping and metadata. Leave room for provenance/omissions rather than truncating them away; do not add a separate onboarding packet builder.

8. Represent bounded expansion as a continuation tied to target, collection mode, filters and remaining cumulative budget. Reject a changed target/mode or replay that exceeds limits; current-snapshot expansion cannot enable history, and pagination must not first buffer all history.

## 7 Public Contracts and State Boundaries

An evidence descriptor identifies type, repository/worktree, actual source revision or overlay, normalized locator/range and source identity where available. Separate the canonical source identity from a particular sanitized/truncated excerpt. Packets contain a bounded question, pinned target, collection mode/history choice, evidence IDs, optional candidate episodes, selected excerpts, budget consumption and material omissions. Current-snapshot-only packets carry no episodes and distinguish unrequested history from unavailable or examined history. Episode candidates carry change bounds and signals, not asserted causal intent.

## 8 Project and File Changes

**Permitted host touch points:** H4 only if bounded history cursor, ancestry or batch primitives are missing. Reuse existing file/path policy and sanitized read results; feature assembly owns episode selection, not raw readers. H-codes resolve to the parent plan's project/file table. List every actual changed file outside the feature project and its integration purpose in the review handoff. Keep feature algorithms in the new assembly; a boundary DTO is not permission to relocate behavior.

Feature tests belong in `tests/Threadsmith.RepositoryIntelligence.Tests` after user acceptance. Amend existing architecture/integration suites only for their owned boundaries and only after acceptance. Required prompt references, dependency test inventories and product documentation finish in the same completed increment, authored after acceptance; report any interim gate failure instead of weakening it.

## 9 Implementation Review and User Acceptance Workflow

1. Finish the scoped production implementation. Build affected projects and run relevant existing checks. Inspect real entry points and collect concise results; do not author new tests or documentation to prepare for review.
2. Start a **fresh reviewer agent with a clean context** (for example, `fork_turns="none"`). Supply this task, the parent requirements/plan, repository instructions, active checkout, exact diff scope including new/untracked production files, the task baseline and existing-check outputs. Distinguish prerequisite code and unrelated user changes without excluding relevant callers. Do not pass the implementer's conversation, reasoning, self-review conclusions or an assertion that the work is correct. The reviewer must read the code and relevant callers outside the diff and must not edit files or write deferred tests/docs.
3. Require adversarial review of intended behavior, reuse/ownership, observable manual/model/internal integration, budgets/proportional work, cancellation, disabled compatibility, evidence and task-specific acceptance. Each finding must include an affected path, concrete trigger, consequence and reusable existing owner where applicable. Passing checks or matching the plan alone is not a clean review. Deferred test/documentation implementation is intentional at this gate and is not itself a defect; missing production behavior or material verification must still be reported. Do not invent defects or demand unrelated refactoring.
4. Evaluate every finding for applicability, validity and reasonableness. Fix substantiated issues; explain rejected or out-of-scope suggestions with source evidence. Send disputed findings and evidence for independent reassessment; do not dismiss a finding merely to get a clean result. Re-run affected existing checks and obtain a fresh clean-context review of the final diff, including the actual fix entry points. Repeat until no applicable, valid, reasonable actionable findings remain.
5. If required review context/tooling or material validation is unavailable, report the blocker and do not claim a clean review or request acceptance as if the gate passed. A nonblocking unmeasured limitation must remain explicit in the review and handoff.
6. **Only after reviews are clean**, ask the user to accept this task's implementation. Present changed behavior, actual host touch points, checks and limitations, review outcomes and disposition of findings, and the tests/docs still deferred. Wait for explicit acceptance of the concrete implementation; the reviewer cannot grant it.
7. After acceptance, implement the unit tests and other applicable cases in section 10, then documentation in section 16; run required checks. Any subsequent production-code change beyond the accepted diff returns through clean-context review and user acceptance before further test/documentation expansion for that changed code. Do not mark the task complete with deferred required work.

**Task-specific adversarial focus:** Look for per-file process loops, whole-history buffering behind a page API and late truncation after expensive reads. Trace current-snapshot-only packet construction and expansion to prove no history discovery or episode construction occurs. Audit evidence deduplication for accidental loss of contradiction or revision identity.

## 10 Test Cases to Implement Only After User Acceptance

**Do not implement these tests before the user accepts the production implementation following clean reviews.** The cases specify behavior and expected evidence, not implementation-shaped assertions. Use existing test infrastructure and deterministic controlled dependencies where appropriate.

| Case | Trigger or setup | Expected result | Level |
|---|---|---|---|
| Current snapshot without history | Build and expand a scoped packet from pinned files/documents and structural facts with history off. | Valid evidence and coverage reach T06 with zero episodes and history marked not requested; no history/frontier/patch discovery occurs, and expansion cannot enable it. | Unit/integration |
| Multi-commit episode | Present introduction, revert and corrective follow-up commits. | One bounded episode can retain the sequence and all source links without asserting intent. | Unit |
| Merge/rename paths | Collect across merges and moved/deleted files. | Locators refer to the correct revision/path and history limits are explicit. | Integration |
| Shared evidence | Select one source for several episodes and overlapping excerpts. | Source identity is reused without losing meaningful ranges or conflicting versions. | Unit |
| Untrusted locator | Request outside-policy paths, nonexistent revisions or unsupported symbols. | The host rejects or labels unavailable; no fabricated evidence ID is accepted. | Unit/integration |
| Packet limits | Use long paths, heavily escaped text, huge patches and binary sources. | Serialized limits and cumulative work limits hold with omission metadata intact. | Unit/integration |
| Continuation | Page through a large bounded history then change the target or exhaust the budget. | Valid continuation advances without full buffering; changed/exhausted requests cannot expand work. | Unit/integration |
| Test source fidelity | Read assertions and test names with no recorded test run. | Output describes source evidence only, never a passing execution. | Unit |

## 11 Security and Permissions

Preserve existing trust, path, secret and transmission policy. Repository text and inferred guidance are data, never execution authority. Propagate `CancellationToken` through async boundaries; use established bounded abandon-and-discard handling for non-cooperative APIs. Do not run repository-controlled builds, hooks or tests as Archeology evidence collection. User acceptance of implementation is separate from runtime operation authorization.

## 12 Observability

Use existing correlated host activity, sanitized outcomes and diagnostics. Expose relevant snapshot, scope, omissions, resource use and failure/cancellation state without filling routine model context. No parallel event or logging store. Record source-based estimates separately from runtime measurements in the acceptance handoff.

## 13 Migration and Compatibility

Keep unused/disabled behavior cheap and unchanged. Do not initialize feature storage, inference, analysis or recurring work from ordinary startup. Unfinished actions remain unavailable. Preserve last valid knowledge on failure and use the existing owner for migration/lifecycle infrastructure. Prerequisite completion does not itself enable this increment for a repository.

## 14 Acceptance Criteria

- [x] Question scope limits discovery and content acquisition before model interpretation can begin.
- [x] Current-snapshot-only units produce provenance-complete bounded packets without history discovery or required episodes, using the shared packet path and preserving history-off expansion.
- [x] Every supplied evidence ID resolves to a validated source descriptor and retains its revision or overlay identity.
- [x] Episodes preserve constituent changes without presenting unsupported intent or causality as fact.
- [x] Packets and continuations are bounded, deterministic where inputs are fixed, and explicit about gaps/conflicts.
- [x] The final production diff has passed the clean-context adversarial review loop; all applicable, valid and reasonable findings are resolved and remaining limitations are disclosed.
- [x] The user explicitly accepted the reviewed implementation before the remaining unit/integration tests and documentation began. The earlier live harness was separately authorized by the user's explicit live-testing request.
- [x] After acceptance, required unit/integration/architecture tests and documentation are implemented, relevant checks pass, and no required gate is silently deferred.

Completion evidence (2026-10-08): the clean-context production review loop resolved metadata acquisition/accounting, retained profile-source reuse, historical batching, configured prerequisite ceilings, mutable-listing admission, host capture-limit bypass and small-budget ancestry findings. A separate clean-context review of the post-acceptance tests and documentation found an escaping/large-history coverage gap; both cases were added and the re-review was clean. No production code changed after the user's acceptance.

Validation: `dotnet build src/Threadsmith.sln --no-restore` passed with zero warnings/errors. Repository-intelligence tests passed (67), native-tool tests passed (179, with one explicitly excluded test skipped), and architecture tests passed (334, with three opt-in live tests skipped). The separately requested real-provider check passed all three snapshot/history/overlay scenarios using GPT-6.1-Sol at low reasoning without fallback. Its normal host events and packets were independently reviewed: cited IDs and values matched, nested reads were correlated, and the current-snapshot/overlay cases performed no history discovery. Both reported Git regressions also have offline coverage: host capture limits remain effective and a 128-byte ordinary parented commit diff returns changed paths. The live responses' phrase "no other tools were invoked" means no additional direct model calls; nested host Git/file reads did execute.

The dedicated collector suite covers scoped snapshot expansion, introduction/revert/correction constituents, merge parents, rename/deletion locators, shared commit evidence across episodes, overlapping source inspections, prohibited paths and unavailable refs/symbols, admitted escaped text and rejected oversized acquisitions, binary/test source classification, cumulative file/input/output/commit limits, configured batches and per-file limits, history cursors/replay, captured profile-body/fact reuse, governed read denial, cancellation and registry release. Composed-host tests verify trusted configuration reaches the real profile reader, repository configuration cannot enlarge it, and unknown trusted keys fail explicitly.

Actual production touch points outside the feature assembly:

- `src/Threadsmith.App/ApplicationComposition.cs`: lazy trusted evidence-limit binding and forwarding of the existing Git limits.
- `src/Threadsmith.Core/RepositoryEvidenceResourceLimits.cs`: configurable invocation evidence ceilings.
- `src/Threadsmith.Core/RepositoryIntelligenceControls.cs`: trusted resource-limit composition.
- `src/Threadsmith.Core/OperationalLimits.cs`: configurable Git metadata and history-offset ceilings.
- `src/Threadsmith.Core/RepositoryInventoryContracts.cs`: optional bounded metadata/cursor requests and acquisition receipts on the existing Git contracts.
- `src/Threadsmith.Tools/BuiltInTools.cs`: metadata admission on the existing mutable file-list reader.
- `src/Threadsmith.Tools/RepositoryInventoryTools.cs`: forwarding bounded acquisition requests through existing Git tools.
- `src/Threadsmith.Workspaces/GitQueryService.cs`: bounded history cursors and metadata-only ancestry/diff acquisition in the existing process owner.
- `src/Threadsmith.Workspaces/GitQueryService.Metadata.cs`: byte capture bounded by request and configured host capture limits.
- `src/Threadsmith.Workspaces/GitQueryService.Inventory.cs`: bounded inventory acquisition receipts.
- `src/Threadsmith.Workspaces/GitQueryService.ShowFiles.cs`: bounded batched-source metadata acquisition receipts.

Remaining capability limits: the collector is internal infrastructure, with no new production action, inference loop or canonical intelligence. History is a bounded recent repository frontier and can miss older scoped changes; episodes group selected changes within each page and imply no causality. Immutable symbol resolution and semantic coverage remain unavailable/unassessed, overlays are non-atomic, and test source proves no execution. Large-repository performance beyond the controlled fixtures was not measured. Limits, provenance and coverage semantics are documented in [context policy](../../architecture/context-policy.md#bounded-repository-evidence) and [tool operations](../../operations/tools.md#evidence-collection-limits), including the opt-in live procedure.

## 15 Risks

A short packet may hide an unbounded discovery phase. Reusing an excerpt hash as source identity can incorrectly merge differently sanitized or versioned evidence.

## 16 Documentation to Implement Only After User Acceptance

**Do not implement documentation before the user accepts the reviewed production implementation.** Evidence/episode distinctions, source locators and coverage accounting. Update only owning documents whose contracts or executable procedures changed. Keep README navigation-only and completed milestone details frozen. Record this task's completion here only after post-acceptance work passes; do not backfill status prose across historical plans.

## 17 Decisions to Resolve During Implementation

Choose a small deterministic episode heuristic and explicit packet/continuation bounds; do not introduce a mandatory ontology or global history graph. Resolve from the active checkout and parent requirements; do not invent missing API behavior or silently relax the requirements.
