# Current source evidence during incremental implementation

**Status:** Proposed

**Delivery track:** Maintenance

**Prerequisites:** Existing incremental mutation execution in [Plan 112](plan-112-incremental-approved-plan-execution.md), [mid-tranche replanning](maintenance-mid-tranche-replanning.md), governed context assembly, transactional baseline promotion, and the central tool invocation pipeline.

## 1 Objective

Let implementation obtain current source evidence without abandoning an approved plan when its scope remains valid. Remove evidence-only replacement-plan cycles caused by stale file contents or missing supporting APIs.

## 2 Architectural Context

Preserve [ADR-32](../architecture/adr-32-host-owned-resumable-execution-orchestration.md), [ADR-11](../architecture/adr-11-central-tool-policy-pipeline.md), [ADR-58](../architecture/adr-58-current-mutation-baselines-and-text-anchors.md), the [context policy](../architecture/context-policy.md), and [shared implementation guidance](00-shared-context.md).

`ExecutionOrchestrator` owns approved-step selection, mutation application, baseline promotion, and durable progress. `MutationProposalApplication` owns implementation decisions and correction. `ContextAssembler`, `EvidenceStore`, and `ContextLifecycleObserver` own evidence selection and invalidation. `SessionApplication.ConversationLoop.cs` owns the established model tool-continuation, invocation-context, and evidence-admission path. Extend these owners and extract focused shared components only where necessary.

This maintenance item intentionally extends the earlier implementation tool restriction to permit bounded built-in file reads. Preserve the completed replanning document as a historical contract; update current owned architecture and operational contracts during implementation.

## 3 Scope

- Mutation- and rollback-driven source-evidence invalidation.
- Reconciliation of supplied source evidence with the current mutation baseline.
- Bounded refresh of affected evidence needed by the next active step.
- Existing built-in `read_file` continuations during implementation and correction.
- Shared policy, execution, events, evidence admission, cancellation, budgets, and provider continuation handling.
- Deterministic regression coverage and current contract/prompt documentation.

## 4 Non-Scope

General history compaction, broader implementation tool access, replanning-policy redesign, new evidence stores, alternate tool pipelines, changed mutation authorization, and changed final compilation/test timing remain separate work.

## 5 Current State

The October 5, 2026 raw-model-log investigation observed repeated implementation requests carrying current baseline hashes alongside earlier source reads. Evidence-only gaps repeatedly triggered `request_replan`, exploration, replacement-plan generation, and implementation restart.

The inspected implementation promotes mutation baselines after applying changes. However, `ContextLifecycleObserver` handles repository-open and semantic-confidence events, not `MutationApplied`. `MutationProposalApplication` advertises only `propose_mutations` and, when enabled, `request_replan`; implementation cannot read a missing or changed file directly.

These findings establish the target behavior. They do not establish a measured latency improvement or successful validation of the separately running workload.

## 6 Proposed Design

### Invalidate at mutation boundaries

Extend the existing evidence invalidation mechanism to handle applied mutations and rollback. Invalidate affected source paths, including both source and destination for moves. Normalize paths consistently with repository filesystem semantics.

Retain historical evidence for inspection, but exclude outdated versions from subsequent model requests. Invalidation must cover relevant multi-file evidence through its declared dependencies; comparing only the first provenance path is insufficient.

Before implementation context is assembled, reconcile source evidence against the current mutation baseline. This is a correctness backstop when event-observer delivery is delayed or execution resumes after interruption. Baseline hashes and supplied source contents must never silently disagree.

### Refresh relevant evidence

After baseline promotion, refresh source evidence needed by the next active step. Use the existing registered `read_file` through `IToolInvocationPipeline`, preserving policy checks, sanitization, output bounds, cancellation, and normal activity events.

Refresh only changed files and previously needed ranges relevant to that step. Newly created files must be eligible for subsequent reads; deleted files must not retain usable source evidence. Keep paging explicit for large files. Do not preload the repository or repeatedly buffer all file contents to construct pages.

Admit refreshed evidence with path, range, invocation identity, and file digest. Supersede older file versions while preserving complementary ranges from the same version. Expose freshness and omission reasons in context inspection. Refreshes must observe the settled current baseline; external changes or identity mismatches must not be presented as matching evidence.

### Continue read-only work within implementation

Advertise the existing built-in `read_file` alongside `propose_mutations` and `request_replan` during implementation and correction.

A read-only round can obtain missing source contracts and continue the same approved plan, active step, and mutation batch. Reading supporting files does not grant permission to mutate them.

Reuse the existing model tool-continuation handling, invocation-context construction, tool pipeline, and evidence admission used by `SessionApplication.ConversationLoop.cs`. Extract a focused shared component where necessary; do not create a competing tool execution or evidence-storage path.

Preserve provider response-envelope handling and tool-call correlation. Charge each continuation to the existing execution budget, enforce bounded rounds/output, and reject responses mixing reads with a terminal mutation or replan decision.

Retain `request_replan` for required scope or approach changes. A denied read, exhausted budget, or unresolved dependency must produce a clear resumable outcome.

## 7 Public Contracts

Keep approved-plan identity, active-step mutation scope, exact-diff authorization, and existing command entry points authoritative. Add host-owned evidence dependency/version metadata or bounded continuation configuration only where existing contracts cannot express the required behavior. Do not expose provider SDK, extension, or terminal types.

## 8 Project/File Changes

| Area | Expected changes |
|---|---|
| Context | `ContextLifecycleObserver.cs`, `EvidenceStore.cs`, `ContextContracts.cs`, and `ContextAssembler.cs`: invalidation, version reconciliation, selection, and inspector reasons. |
| Execution | `ExecutionOrchestrator.cs`, `MutationProposalApplication.cs`, and `SessionApplication.ConversationLoop.cs`: targeted refresh and shared read continuation. |
| Core/App/Tools | Applicable execution contracts/limits and host composition; reuse the registered `read_file` and central pipeline. Extend shared components only as required. |
| Tests | Existing planning, mutation, and execution-orchestration suites; relevant tool, provider-continuation, and architecture regressions. |
| Documentation/prompts | Current context/execution contracts, affected implementation/correction prompt assets, prompt inventories, and incremental execution acceptance/manual coverage. |

Exact additional file changes should follow the focused implementation trace rather than introducing new abstractions in advance.

## 9 Ordered Tasks

1. **Invalidate source evidence at mutation boundaries.** Handle apply, move, delete, and rollback; normalize dependencies; exclude stale versions; reconcile evidence against the promoted baseline before assembly.
2. **Refresh affected evidence before the next implementation batch.** Use the central file-read pipeline, refresh relevant ranges only, preserve provenance/digests, and expose refresh activity and selection reasons.
3. **Allow bounded `read_file` continuations within implementation.** Share established continuation and evidence-admission machinery, preserve approved-step identity and budgets, enforce exclusive read versus terminal-decision responses, and retain genuine replanning.
4. **Add regression coverage and update owned contracts.** Exercise the observed failure sequence through real entry points, run required checks, and synchronize current documentation and deployed prompts.

## 10 Testing

Use deterministic scripted providers and explicit synchronization rather than runner-speed assertions.

- Apply batch A; batch B receives source matching the promoted baseline, with the previous version excluded.
- Add tests in multiple batches; subsequent requests receive updated test contents.
- Read a missing constructor or interface during implementation, then propose mutations without another `propose_plan`.
- Create, move, delete, rollback, and resume preserve correct evidence identity.
- Preserve complementary ranges from the same version and exclude obsolete ranges from earlier versions, including multi-file evidence dependencies.
- Cover delayed invalidation delivery, external changes, file denial, cancellation, budget exhaustion, paging, and provider tool-call correlation.
- Exact-diff authorization, approved mutation scope, and final compilation/test timing remain intact.
- Both headless and interactive runs use the same execution and tool activity path.

Run the solution build and relevant planning, mutation, execution-orchestration, tool/continuation, dependency-direction, and prompt-payload checks. Verify request contents and round counts. Live elapsed-time measurements are supplementary and must distinguish measured results from source-based expectations.

## 11 Security/Permissions

All reads pass through the existing tool policy, repository trust, path confinement, reparse-point, output-bound, sanitization, and cancellation controls. Bind authorization to the registered built-in tool identity. File-reading authority does not expand mutation scope or authorize file changes. Existing plan and exact-diff mutation approval remain unchanged.

## 12 Observability

Refresh reads and model-requested supporting reads produce ordinary tool activity, events, logs, and completion/cancellation outcomes. Context inspection explains stale/superseded omissions and admitted source versions. Avoid silent internal filesystem reads or a separate renderer/activity store.

## 13 Migration/Compatibility

Preserve historical evidence and existing execution identities. At resume, reconstruct current eligible evidence from authoritative state rather than trusting pre-interruption source bodies. Additive metadata requires explicit compatibility handling; do not renumber existing phases or reinterpret historical evidence as current. Preserve genuine replanning and existing authorization semantics.

## 14 Acceptance Criteria

- A multi-batch edit followed by a missing supporting-file read completes under one approved plan, with no evidence-only replan.
- Implementation requests contain current, attributable source evidence that agrees with the applicable baseline; missing or excluded evidence is explicit.
- Superseded versions are excluded without losing needed ranges from the current version.
- Refresh work and supporting reads appear through ordinary tool activity.
- Scope or approach changes still require replanning.
- Cancellation, budgets, provider continuation, approved mutation scope, exact-diff authorization, and final validation remain governed.
- Review traces real manual, model-driven, and internal entry points and finds no competing execution or evidence-admission path.

## 15 Risks

- Observer delivery may lag baseline promotion; assembly-time reconciliation must close that race.
- Multi-file results and mixed absolute/relative provenance can evade single-path invalidation.
- Supersession must distinguish obsolete versions from complementary current-version ranges.
- Refreshing every affected file in full would increase prompt size and delay useful work; keep scope and paging bounded.
- Extracting continuation support can disturb provider envelopes, budget accounting, or correlation; verify through production entry points.
- External edits during a read must not be mislabeled as matching the mutation baseline.

## 16 Documentation

Update `docs/architecture/context-policy.md`, the current execution contract where required, affected implementation/correction prompt assets, `docs/operations/prompts.md`, and `docs/prompt-file-reference.md` together. Update resource-limit documentation if continuation configuration changes.

Extend the existing incremental-execution acceptance coverage and MTP-273 for refresh and supporting-read behavior. This document owns work-item status; its README entry is navigation only. Preserve completed milestone and maintenance records.

## 17 Open Decisions

- Resolve the smallest reusable boundary for continuation and evidence admission by tracing existing conversation and implementation call sites.
- Confirm whether existing evidence metadata can represent all file dependencies and versions; add metadata only where necessary.
- Determine how existing execution budgets express bounded read-continuation rounds and retained output, adding configuration only if required.

These are implementation design decisions within the stated scope. They do not authorize an alternate tool pipeline or require broadening mutation authority.
