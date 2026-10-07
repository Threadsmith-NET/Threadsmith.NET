# Repository Intelligence and Archeology Incremental Implementation Plan

**Status:** Proposed task breakdown; no implementation or code sign-off implied.  
**Delivery track:** Proposed new capability milestone, to be registered after user sign-off; not Maintenance and not a reopening of completed milestones.  
**Prerequisites:** Parent requirements approval; task dependencies below; existing tool, model, context, persistence, memory, and delegation contracts. Recheck the active checkout before each implementation increment.  
**Parent specification:** [Repository Intelligence and Repository Archeology requirements v0.3](threadsmith-repository-intelligence-and-archeology-requirements.md).

## 1 Objective

Deliver the parent requirements as small, reviewable increments without changing ordinary application behavior when the capability is unused or disabled. All feature-specific production implementation belongs in **one new project, `src/Threadsmith.RepositoryIntelligence`**, included in `src/Threadsmith.sln`.

Every task requires clean-context adversarial review of the completed production implementation, iteration on applicable, valid and reasonable findings until reviews are clean, and only then a request for user acceptance followed by test and documentation implementation. **Do not write or modify tests, test fixtures, snapshots, benchmarks, or product documentation for a task until the user explicitly signs off on that task's implementation.** Approval of this plan is not approval of future code. The test conditions in the individual task files are specifications only. This requested plan, its 20 detailed task files and navigation entries are planning deliverables, not premature product documentation.

## 2 Architectural Context

Follow [AGENTS.md](../../AGENTS.md), [planning governance](planning-governance.md), [shared context section G](00-shared-context.md#g-implementation-document-template-and-agent-instructions), [context policy](../architecture/context-policy.md), [ADR-31](../architecture/adr-31-bounded-conversational-continuity.md), and [ADR-6](../architecture/adr-06-event-oriented-durable-session-model.md). Before changing C#, read [portable C# guardrails](../guardrails/portable-csharp-guardrails.md).

Assembly isolation assigns feature ownership; it does not authorize parallel host infrastructure. Reuse actual execution paths, not copied implementations behind new interfaces. No feature-owned model loop, agent scheduler, process launcher, Git reader, source reader, event bus, context assembler, memory store, or renderer. A missing shared capability must be supplied by a focused extension of its existing owner, with its call sites and reason documented in the implementation review.

Core must never reference the feature assembly. Existing consumers call minimal host-owned contracts; composition supplies their implementation. Public results contain serializable host-owned DTOs, never Roslyn, provider SDK, terminal, database connection, or extension implementation objects. Feature-specific algorithms, storage schemas, reconciliation rules, ranking, and lifecycle orchestration stay in the new assembly.

## 3 Scope

Optional activation; pinned profiling; bounded one-off and persistent investigations; normalized evidence, episodes, items, relationships, conflicts and capsules; reconciliation and recovery; explicit inspection; freshness admission; selective recall; exploration enrichment; constrained child inspection; memory links; maintenance; correction, export and deletion. Requirement identifiers and AC identifiers below refer to the parent specification.

## 4 Non-Scope

No coding-application rewrite, new scheduling platform, replacement memory implementation, automatic knowledge-to-policy conversion, repository mutations during analysis, mandatory embeddings, hosted service, or exhaustive history preload. No feature activation merely because files, configuration, or stored data exist. No production implementation, tests, or product documentation is delivered by this planning change.

## 5 Current State

These are reuse anchors in the current checkout, not claims that the feature already exists:

| Responsibility | Established owner and integration constraint |
|---|---|
| Composition and tool inventory | `Threadsmith.App/HostFoundation.cs` constructs `GitQueryService`, `ToolInvocationPipeline`, and `CodeExploreTool`; `ApplicationComposition.cs` wires application services. Register lazily here rather than adding another host. |
| Tool authority and visibility | `Threadsmith.Tools/ToolInvocationPipeline.cs` owns invocation, batch handling, outcomes and activity. Manual, model-driven and internal feature work must reach its governed execution path where tools are used. |
| Repository facts and revisions | `Threadsmith.Core/RepositoryInventoryContracts.cs` and `Threadsmith.Workspaces/GitQueryService*.cs` provide bounded Git log, diff, show, inventory and batched revision-file reads. Extend these only for proven gaps such as bounded history continuation/ancestry. |
| Current semantic exploration | `Threadsmith.Tools/CodeExploreTool.cs` calls `ICodeExploreService`, supplies a policy source reader, confines output and applies model/result budgets. Add a callback to this result path; preserve its reader and bounding logic. |
| Context admission | `Threadsmith.Context/ContextAssembler.cs` applies evidence invalidations before assembly and handles memory eligibility/budgets. `SessionApplication.ConversationLoop.cs` retains/fits context, including frozen context. Both fresh assembly and reuse need integration. |
| Governed evidence | `Threadsmith.Context/EvidenceStore.cs` and `SourceEvidence.cs` own ordinary evidence and dependency invalidation. Feature provenance must map into them when admitted, without a second active-context evidence store. |
| Model requests | `SessionApplication.ConversationLoop.cs` dispatches through `RepositoryMemoryDispatch.StreamAsync`; delegated requests have their own established host execution. A general bounded inference entry point with all required host governance has not been established by this inspection. T06 must trace and extend the existing owner, not call an SDK or copy a loop. |
| Memory | `RepositoryMemoryService`, `HybridRepositoryMemoryRetriever`, `MemoryConcepts`, and `IManagedRepositoryMemoryService` provide existing memory semantics and vocabulary. Reuse these; intelligence is a distinct record type. |
| Persistence | `Threadsmith.Persistence/Migrations.cs` exposes `MigrationRunner` and `IDatabaseMigration`; stores and retention already exist. The runner contains a memory-specific version-10 branch, so suitability for a separate feature schema must be checked, not assumed. |
| Dependency enforcement | `tests/Threadsmith.Architecture.Tests/DependencyDirectionTests.cs` enumerates projects and allowed references. Its necessary update is test implementation and must wait for task code sign-off. |

Source inspection establishes integration candidates. Runtime feasibility, performance, and exact API suitability remain to be verified during implementation; no new framework is justified merely by a missing convenience API.

## 6 Proposed Design

### Increment lifecycle

1. Implement only the task's production scope and enumerated host touch points. Keep unfinished operations unregistered or unavailable. Preserve the cheap disabled path.
2. Build affected projects and run appropriate **existing** checks; inspect real call paths and demonstrate supported behavior where useful. Do not create or modify tests, fixtures, evaluation harnesses or product documentation before acceptance. Report gaps honestly.
3. Run a **clean-context adversarial review using a fresh reviewer agent**, without the implementer's conversation or reasoning. Give the reviewer the task, parent requirements, repository instructions, active checkout, exact change scope and existing-check outputs. Require inspection of real entry points and relevant code outside the diff. Address applicable, valid and reasonable findings, substantiate any disputed findings, and repeat fresh reviews of the corrected final diff until there are no unresolved actionable findings. Missing material review evidence is not a clean review.
4. **Only after reviews are clean**, present the concrete diff, reused owners, host changes, observable result, checks, limitations and review dispositions, then ask the user to accept that task's implementation. Wait for explicit user acceptance; plan approval, reviewer approval or silence does not count.
5. **Only after user acceptance**, implement the task's unit tests and other required tests, then necessary documentation. Run the relevant gates. Any subsequent production-code changes beyond the accepted diff must pass fresh adversarial review and user acceptance before further test/documentation expansion for those changes.
6. Only then mark the increment complete and make its supported operations available under explicit opt-in. A dependent task requires completed prerequisites, not merely accepted code.

Every task file repeats this workflow and adds task-specific adversarial checks. If clean-context review cannot be performed, report the blocker rather than claiming a clean result or substituting self-review. Reviews must not manufacture defects, demand unrelated refactors, or treat missing post-acceptance tests/docs as a defect in the agreed sequencing.

Keep architecture-gate changes and prompt-reference documentation in the same completed increment as their production changes, but author them after acceptance. A pre-acceptance scaffold can be built independently; do not hide an expected architecture-test failure by weakening the gate or claim the increment is ready to land. Existing rules requiring tests before completion remain satisfied by step 5.

### Shared execution and narrow abstractions

Start with a small optional feature access contract, operation request/result DTOs, and a freshness/admission callback only when their first consumers require them. Names are provisional; avoid a framework of providers or one interface per class. Internal implementation details remain internal to the new assembly.

Feature tools should be implemented in the new project using existing `Tool<TInput,TResult>` contracts. App registers them in the existing registry. A host invocation bridge may be needed for nested, bounded reads and inference; it must enter shared policy, budget, event, progress, cancellation and sanitization machinery. It must not merely put an ordinary tool label around direct ungoverned work. Parent and nested invocations need distinct IDs, correlated activity, bounded concurrency and no double accounting or semaphore deadlocks.

Use one evidence collection and interpretation flow. Its retention policy is either invocation-only or persistent; do not build a separate investigator for each. Invocation-only retention must not resolve/open the feature store. Durable storage owns canonical intelligence and feature run checkpoints, not copies of host session/event state. Reuse existing persistence infrastructure through the narrowest suitable seam; storage algorithms and schema implementations remain feature-owned.

Freshness is a prerequisite of automatic use, independent of maintenance. Request eligibility precedes lookup. Revalidate candidate and previously admitted item dependencies before the next model request, including frozen-context reuse. Unknown compatibility withholds automatic guidance. Explicit reads qualify stale data. None of these checks may launch inference or maintenance.

## 7 Public Contracts

Add only the following boundary families as their tasks require them:

- Trusted activation snapshot and bounded operation context: repository/worktree, session/run/invocation IDs, request mode, assignment authority, budget and cancellation.
- Pinned source descriptors: repository identity, immutable commit, separately identified working-tree overlay, scope, source locators, coverage and omissions.
- Serializable operation results: item/revision, capsule, evidence locator, independent assessment dimensions, freshness, diagnostics and continuation information.
- Optional context/exploration admission callback with eligible request context and already available facts; no callback owns host policy or context rendering.
- Parent-admitted item/evidence allowlist tied to an immutable assignment baseline; child inspection cannot broaden it.
- Memory source link containing intelligence item/revision and repository context; writes continue through existing memory operations.

Internal stores, candidate schemas and inference packets need no public interface unless a real consumer requires one. Preserve schema versions for durable boundaries. Reuse existing identifiers and concept normalization before defining equivalents.

## 8 Project and File Changes

The following codes enumerate the **maximum proposed host touch points**. Each task below lists its subset; an implementing agent must name the actual changed files and explain every change outside the new project at review. A subset is not permission for broad refactoring.

| Code | Allowed location and purpose |
|---|---|
| H1 | `src/Threadsmith.sln`, new project file, `Threadsmith.App.csproj`, App composition/foundation: project inclusion, lazy registration and host-owned dependency injection. |
| H2 | Core boundary/configuration contracts and trusted settings integration: minimal DTOs/callbacks and activation authority. No feature algorithms. |
| H3 | Tools invocation/registry and Execution host operation integration: shared governed invocation/inference entry points, correlation and cancellation plumbing only. |
| H4 | Core repository contracts and existing Workspaces Git/query implementation: only proven missing bounded repository primitives, reused by existing callers. |
| H5 | Existing Persistence infrastructure: narrow migration/connection/artifact/retention seam if needed; no feature tables, intelligence repository implementation or feature lifecycle here. |
| H6 | Context assembler/evidence dependencies and Execution conversation invalidation/reuse: optional delegated admission and ordinary accounting. |
| H7 | `CodeExploreTool.cs`, output DTO/formatting path: optional compact enrichment, preserving current policy and final byte budget. |
| H8 | Existing assignment snapshot/child tool eligibility/join paths: admitted-item allowlist and baseline metadata only; no new delegation machinery. |
| H9 | Existing memory contracts/service boundary: optional source revision links and shared concept/deduplication integration; no replacement memory lifecycle. |
| H10 | Existing Interaction commands/projections and CLI wiring where required: thin submission and projection through existing presentation, no feature renderer or terminal types. |
| H11 | Existing prompt catalog/loading/publish registration, feature-owned prompt payloads, central package pins only if necessary. Prompt contract documentation is post-sign-off in the same completed increment. |

Proposed dependency direction is `App -> RepositoryIntelligence -> Core`, with references to Tools and Persistence only where their existing reusable contracts/infrastructure are required. Prefer Core-supplied host services over referencing concrete Execution, Workspaces or DotNet implementations. Do not introduce reverse references from Context, Tools, Execution, Interaction or Persistence to the feature. Any additional reference requires a concrete call-site justification and post-sign-off architecture-gate coverage.

Create `tests/Threadsmith.RepositoryIntelligence.Tests` **after T01 code sign-off**, and keep feature tests there. Add focused coverage to existing architecture/integration suites where the owning boundary is outside the feature. Product documentation changes are listed per task and deferred. This plan, its 20 individual task files and their README navigation entries are the planning edits authorized now; do not edit the proposed parent requirements or completed milestone contracts.

## 9 Ordered Tasks

The following 20 task files are the executable implementation instructions. Each file owns its task status, prerequisites, detailed steps, acceptance criteria, deferred test cases, and mandatory clean-context review and user acceptance gates. Implement one task through its post-acceptance completion gate before starting its dependent task. Read the parent requirements and architectural constraints with each assignment.

| Task | Detailed instructions |
|---|---|
| T01 | [Establish the dormant assembly boundary](repository-intelligence-and-archeology/t01-dormant-assembly-boundary.md) |
| T02 | [Add trusted activation and cancellation controls](repository-intelligence-and-archeology/t02-activation-and-cancellation.md) |
| T03 | [Route feature operations through host governance](repository-intelligence-and-archeology/t03-governed-operation-routing.md) |
| T04 | [Capture pinned identity and bounded structural facts](repository-intelligence-and-archeology/t04-pinned-identity-and-structural-facts.md) |
| T05 | [Build normalized evidence and bounded episode candidates](repository-intelligence-and-archeology/t05-evidence-and-episode-candidates.md) |
| T06 | [Add bounded interpretation using shared model execution](repository-intelligence-and-archeology/t06-bounded-model-interpretation.md) |
| T07 | [Deliver invocation-only Archeology](repository-intelligence-and-archeology/t07-invocation-only-archeology.md) |
| T08 | [Add local canonical records and atomic storage](repository-intelligence-and-archeology/t08-canonical-records-and-storage.md) |
| T09 | [Implement validation and reconciliation](repository-intelligence-and-archeology/t09-validation-and-reconciliation.md) |
| T10 | [Deliver persistent onboarding and checkpoint recovery](repository-intelligence-and-archeology/t10-onboarding-and-checkpoint-recovery.md) |
| T11 | [Implement current freshness admission](repository-intelligence-and-archeology/t11-freshness-admission.md) |
| T12 | [Deliver bounded explicit search and evidence inspection](repository-intelligence-and-archeology/t12-explicit-search-and-inspection.md) |
| T13 | [Add bounded parent recall and admission diagnostics](repository-intelligence-and-archeology/t13-parent-recall-and-diagnostics.md) |
| T14 | [Enrich existing code exploration](repository-intelligence-and-archeology/t14-code-exploration-enrichment.md) |
| T15 | [Add assignment-limited child inspection](repository-intelligence-and-archeology/t15-assignment-limited-child-inspection.md) |
| T16 | [Link intelligence with existing memory](repository-intelligence-and-archeology/t16-memory-linkage.md) |
| T17 | [Add explicit incremental maintenance](repository-intelligence-and-archeology/t17-explicit-incremental-maintenance.md) |
| T18 | [Add opt-in bounded maintenance triggering](repository-intelligence-and-archeology/t18-automatic-maintenance-triggers.md) |
| T19 | [Deliver correction, suppression, export and reset](repository-intelligence-and-archeology/t19-correction-export-and-reset.md) |
| T20 | [Complete integrated acceptance and measured evaluation](repository-intelligence-and-archeology/t20-integrated-acceptance-and-evaluation.md) |

## 10 Testing

The cases in the individual task files linked from section 9 identify conditions to cover, not permission to implement tests. Use the dedicated feature test project after sign-off, focused integration coverage at real host entry points, and the existing architecture suite for dependency direction. Prefer deterministic controlled inference responses for validation/reconciliation assertions; assess semantic extraction separately with predefined evidence and expected supported conclusions. Avoid tests that merely mirror private implementation.

Before code sign-off, existing build/test commands and read-only demonstrations may provide review evidence. Record failures and unassessed behavior; do not weaken existing tests or add provisional fixtures. After sign-off, implement the cases applicable to the final approved design, run meaningful subsystem checks and relevant existing regressions, then complete the task. No tests or benchmarks are implemented by this planning change.

## 11 Security and Permissions

Historical content, commit messages, documents and capsules are untrusted data. Use existing path/access/secret/transmission controls, safe Git options and host-managed resources. Analysis does not run repository code, hooks, builds or tests to gather evidence, nor authorize source changes. User sign-off here is a development workflow gate; application operation authority remains the existing host policy. Local-first storage does not imply that configured model inference runs locally.

## 12 Observability

Every expensive operation uses ordinary correlated host activity, progress, cancellation and completion. Results identify snapshot, scope, coverage, inference provenance, consumption and rejection/defer reasons. Persistent provenance belongs in feature run records; invocation-only results use ordinary host event retention. Detailed diagnostics stay inspectable without routinely filling model context. No duplicated events or silent nested Git/model work.

## 13 Migration and Compatibility

All activation defaults remain off. Completed increments may be integrated while dormant; unfinished actions are not advertised. No feature migration, scanning, inference, recall or maintenance occurs on ordinary startup. Optional callbacks must return cheaply before resolving feature services. Existing memory, tools and semantic exploration remain usable if feature storage/provider is unavailable.

Disabling revokes work and admissions without deleting knowledge. Reset is a separate explicit action. Persistent recovery resumes pinned units only when sources remain valid; invocation-only retry always starts a new run. Keep database/provider/framework types behind adapters and update dependency/package/prompt integration only after the relevant code sign-off where tests/documentation are involved.

## 14 Acceptance Criteria

Each task is complete only when its production implementation has passed clean-context adversarial review, applicable findings have been resolved, the user has then explicitly accepted that reviewed code, its subsequently implemented tests pass, its necessary documentation is updated and its real host entry points have been reviewed. The full capability additionally meets the parent acceptance outcomes:

| Parent outcomes | Owning tasks |
|---|---|
| AC-01–02 | T04, T10 |
| AC-03–04 | T05–07 |
| AC-05–06 | T06, T08–09, T17 |
| AC-07–08 | T11, T17–18 |
| AC-09–10 | T12–13 |
| AC-11–12 | T14, T16, T19 |
| AC-13–14 | T09–10, T17, T19 |
| AC-15–16 | T01–02, T10, T14 |
| AC-17–18 | T01, T03, T07–08; boundary review in every task |
| AC-19 | T02–03, T18–19 |
| AC-20–21 | T11, T13–14, T18 |
| AC-22 | T07 |
| AC-23–24 | T07, T12–16 |
| AC-25 | T10, T12, T17 |
| Integrated acceptance and quality | T20 |

At every review, trace actual reused call sites, enumerate host changes, inspect proportional work before useful output, and distinguish measured results from estimates. Reject duplicate infrastructure even if tests pass. A formatter or shared interface alone does not prove shared execution.

## 15 Risks

- Nested tool/model execution may expose missing host primitives or reentrancy limits. Resolve these in the existing owner during T03/T06; do not add a hidden execution path.
- Persistence reuse may require a narrow infrastructure change because existing migration behavior includes memory-specific assumptions. Keep it feature-neutral and validate that dormant startup never migrates feature data.
- Cached and frozen context can retain obsolete guidance. T11 precedes recall and covers reuse paths, notification gaps and conservative withholding.
- Large histories can look bounded at the output while doing unbounded upstream work. Measure source reads/processes/memory and continuation behavior; prioritize before fetching full contents.
- Delaying tests until sign-off means pre-sign-off code is intentionally not complete. Keep it dormant and report missing verification instead of treating approval as proof of correctness.
- Semantic inference cannot establish original intent from plausibility alone. Unknown outcomes and conflicts remain valid; evaluation must penalize unsupported claims.

## 16 Documentation

Only this requested plan, the 20 detailed task files and their navigation entries are authored now. Each task lists necessary future documentation and explicitly defers its implementation until user code sign-off. Update only the document that owns a changed contract. Prompt assets and their required reference documentation finish together after code sign-off; acceptance/manual procedure updates follow actual behavior changes. Register the new capability milestone after T01 sign-off and keep status solely in `milestones.md`; completion remains gated on measured acceptance.

## 17 Open Decisions

Resolve decisions in the named task before submitting code for sign-off, without weakening parent requirements:

| Decision | Owner and constraint |
|---|---|
| Exact minimal contracts and project references | T01/T03; real consumers and dependency-safe injection only. |
| Trusted activation storage and control surface | T02; repository content cannot self-enable. |
| Shared bounded inference entry point | T06; host execution, budgets, visibility and cancellation must remain shared. |
| Feature schema/location and infrastructure reuse | T08; lazy local persistence, no ordinary startup migration or copied migration runner. |
| Episode heuristics and packet/default resource bounds | T05/T06; proportional work with inspectable omissions. |
| Capsule/query ranking and budgets | T12/T13; deterministic baseline retrieval, embeddings optional. |
| Maintenance trigger boundary | T18; existing lifecycle, no new always-running scheduler. |
| Quantitative quality/performance thresholds | T20, before evaluation fixtures; distinguish extraction quality from retrieval usefulness. |

The unavailable external conversation linked by the parent is not needed to implement this breakdown: the repository-owned parent requirements define the contract. Any discovered contradiction is resolved explicitly before implementing the affected task.
