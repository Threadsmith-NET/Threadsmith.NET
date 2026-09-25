# Implementation Plan 113.1: Active Context Budget Protection and Measurement

**Status:** Complete (2026-09-25).

**Delivery track:** Maintenance — model admission and active-turn measurement.
**Prerequisites:** Implemented contracts from plans 51–55, 80, 84, 86, and 109; ADR-12 and ADR-31; existing `ModelRequestBudgetUsage`, active-turn compactor, estimator, and budget contracts.
**Sequence:** First delivery in [plan 113](plan-113-supersession-aware-active-context-management.md). This document owns its implementation status and acceptance evidence.

## 1. Objective

Protect the remaining execution budget before ordinary requests and each summary attempt, including retries. Establish a reproducible baseline that separates context occupancy from cumulative spending. Deliver this without projection metadata, a new summarizer, or a new context framework.

Success means fewer inadmissible provider calls, not a claim that admission protection alone optimizes cumulative input.

## 2. Architectural Context

`ContextAssembler` creates the governed frozen context. `SessionApplication.ConversationLoop` appends complete continuation groups, assesses active-turn compaction, and prepares the ordinary request. `ModelRequestBudgetUsage.Start` checks estimated input and charges a call before ordinary dispatch. The existing summary observer charges usage after execution; the compactor can retry. Reuse these execution and observation boundaries.

Read root `AGENTS.md`, `planning-governance.md`, `00-shared-context.md` §G, and the portable C# guardrails before code changes. Confirm the active checkout. Preserve normal hooks, policy, usage, progress, cancellation, and provider preparation.

## 3. Scope

- One shared conservative admission calculation for prepared ordinary and summary requests.
- Output allowance, call count, known cost, and reliable duration bounds where supported.
- Summary-plus-continuation preflight at every provider attempt.
- Budget-admission failure as an additional reason to assess existing summary compaction.
- Content-free replay attribution and a bounded synthetic baseline/control corpus.
- Inventory of other consumers and shared-budget concurrency; integration begins with ordinary conversation.

## 4. Non-Scope

No new or changed tool-output projection, deterministic history projection, source subtraction, new evidence tool, extension DTO, new project, new summarizer, or proactive production trigger based on guessed future rounds. Existing projections remain unchanged. First verbatim delivery means delivery of the tool's declared sanitized, bounded model-facing result, including an existing `ModelResultContent` projection; it does not require raw process output. Do not relax that delivery rule or the existing recent raw retention contract. Do not copy admission orchestration into every loop.

## 5. Current State

Ordinary admission currently estimates input without reserving maximum output. Summary compaction is primarily triggered by context pressure and charges budget after execution. A moderate request repeatedly replayed can exhaust cumulative budget without reaching context pressure. Existing retries mean preflighting only the first summary call is insufficient.

The observed Pi comparison did not measure cumulative provider input and is not evidence of savings. Use it only as historical motivation.

The tool pipeline already supports `ModelResultContent`. Native validation tools project typed build/test results and omit successful raw output; Git status already requests compact output. Baselines must include these existing reductions rather than treating all tool results as unfiltered terminal output. Inspect `NativeValidationTools.cs`, `ToolInvocationPipeline.cs`, and the `git_status`/`run_process` producers in `BuiltInTools.cs` when attributing remaining overhead.

## 6. Proposed Design

### 6.1 Admission arithmetic

Use the existing prepared wire estimate and selected profile. Define ordinary delta as estimated input plus enforced maximum output, one call, known estimated cost, and any reliable existing duration bound. An effective reserve is acceptable only when its relationship to the actual output cap is explicit; do not claim a hard bound from an unenforced reserve. Unknown cost/duration remains explicitly unknown. Estimates are not exact provider accounting.

Share arithmetic without moving execution authority into `Threadsmith.Context` or introducing a dependency cycle. Actual reported usage remains the accrued authority; admission checks do not double-charge estimates. Preserve missing-usage behavior and disclose its limits.

### 6.2 Summary continuation headroom

Before each summary attempt, prepare its actual request without dispatch and calculate:

`summary input estimate + summary output cap + bounded continuation input + ordinary output cap`, with two calls and applicable other dimensions.

Derive continuation input from frozen context, retained groups, tool schemas, protocol overhead, and the validator-enforced maximum rendered summary size. Record the derivation in a test; do not assume that the candidate achieves a desired compression ratio. Use the actual summary profile separately from the ordinary profile. Recheck after summary usage accrues and after final ordinary preparation.

Retry only if the next attempt plus continuation still fits. Cancellation before dispatch invokes no provider. Invalid, oversized, non-saving, or failed summaries leave history unchanged; budget usage already incurred remains charged. Conservative preflight cannot guarantee summary validity, provider success, or eventual task completion.

Inventory concurrent consumers of the same budget. If reachable consumers can spend concurrently between check and dispatch, reuse existing reservations or serialize the relevant admission/dispatch ownership; do not claim reserved headroom from an unprotected snapshot. Do not hold a global execution lock across provider I/O. Record the selected concurrency contract and a regression test where applicable.

### 6.3 Trigger and fallback

Keep the existing context-pressure trigger. Also assess the existing eligible summary prefix when ordinary admission fails. If no eligible prefix or feasible combined path exists, return controlled exhaustion before dispatch. Do not spend a last remaining call on a summary. Keep the existing capacity fallback and protocol rules; estimate the actual request after it acts.

### 6.4 Measurement contract

For each ordinary/summary request distinguish: canonical estimated input; reported provider input/output; cumulative host input-plus-output use; tool-schema contribution; cache counters with included/additional semantics; calls; known cost; elapsed time; and outcome. Missing reports remain missing, never fabricated zero usage.

Attribute tool-result contribution using existing message identities, separating the sanitized bounded captured result (where already available), the actual first-delivery model-facing representation, and its subsequent replay contribution. Record existing projection identity when available; unavailable captured size remains unknown. Captured content that never enters a request is not provider input, and existing projection savings must not be credited to this delivery. Label per-result estimates as attribution estimates: tokenizer and framing effects can prevent their sum from equaling the complete wire estimate. Use existing in-memory results and fixture metadata; do not add raw-output retention, rerun commands, change projections, or create a separate measurement subsystem. Do not add source bodies or queries to normal telemetry.

Replay four synthetic workloads through existing test infrastructure: overlapping exploration, mostly unique evidence, moderate context repeated to budget exhaustion, and failed/retried summary attempts. Fix model limits, output caps, tool inventory, and scripted usage. Record the current behavior baseline and this delivery's result using the same fixtures. No proprietary log is required.

### 6.5 Resolved implementation decisions

- Admission uses the provider-prepared `ModelStreamRequest` wire estimate. The shared estimate adds one call and the output that can physically fit beside prepared input, capped by the enforced request ceiling or by the profile hard maximum when the provider cannot enforce a request-specific limit. OpenAI Codex is explicitly classified as the latter; Anthropic retains enforced request limits.
- Known cost and duration lower bounds are retained when another request in a combined path has an unknown dimension. Completeness is reported separately. No existing provider timeout is treated as a reliable whole-operation duration bound.
- The continuation bound uses the validator-enforced 65,536-character rendered-summary ceiling and runs the existing delivered-result capacity fallback before admission. It is calculated lazily for the exact prefix selected by each prepared attempt. The rebuilt ordinary request is prepared and checked again after actual summary usage accrues.
- Production composition and the default concrete `ExecutionBudget` constructor path create one budget scope per run. Explicit `budgetFactory` callers may intentionally supply shared/custom ownership. Summary and ordinary requests within one run execute serially, so the combined two-call check cannot race another request in that scope.
- The supported delivery path is the primary `SessionApplication` conversation loop and its existing active-turn compactor. Mutation proposal requests use the same ordinary admission helper and now carry profile cost/output enforcement facts, but do not gain active-turn compaction. Child-agent, skill-procedure, MCP, embedding, reranking, and direct provider loops retain their existing budget contracts and are outside 113.1 compaction coverage.

## 7. Public Contracts

Prefer internal admission estimates and closed reason enums. Extend existing budget/compaction attempt contracts only where necessary to expose prepared estimates and preserve continuation allowance. Persisted additions are host-owned and versioned. No public extension API changes.

## 8. Project/File Changes

- `src/Threadsmith.Execution/ModelRequestBudgetUsage.cs`, `Budget.cs`, and existing budget interfaces as justified by inspection.
- `SessionApplication.ConversationLoop.cs` admission and compaction observer integration.
- `src/Threadsmith.Context/ActiveTurnCompaction.cs` prepared-attempt and retry integration; retain its candidate provider and validator.
- Existing context inspection, usage accounting, and conversation/context tests.
- `docs/operations/conversation-context.md` and budget documentation identified during inventory.

## 9. Ordered Tasks

1. Read governing documents and trace ordinary preparation/dispatch, compactor preparation/retries, usage accrual, hooks, and all shared-budget consumers. Record supported and unsupported entry points here.
2. Capture the four synthetic baselines and exact reproducible commands using existing suites. Inventory existing model-facing projections and distinguish captured size, first delivery, and replay within these baselines. Record estimator/profile assumptions and missing report behavior.
3. Specify admission dimensions, continuation bound, output-cap enforcement, and concurrency ownership here before implementing them.
4. Implement shared admission arithmetic and ordinary preflight; preserve current charging semantics.
5. Integrate per-attempt summary continuation preflight and the additional budget-pressure assessment through the existing conversation path.
6. Add content-free reasons and measurements. Test retries, invalid candidates, cancellation, and actual usage exceeding estimates.
7. Run the acceptance matrix, relevant suites, architecture tests, build, formatting/analyzers, and planning checks. Review real entry points for duplicate execution, missing activity, and unnecessary scans.
8. Record evidence and limitations in §14, update owned documentation, and hand the baseline to 113.2. Do not implement 113.2 during this delivery.

## 10. Testing

Extend `Plan80ActiveTurnCompactionTests`, execution-orchestration/conversation tests, and existing budget/usage tests. Use parameterized invariant scenarios, not a test-count quota or a new harness.

Required scenarios: ordinary fits; input fits but output allowance does not; summary plus continuation fits; summary alone fits; one call remains; retry loses headroom; invalid/no-savings candidate; cancellation before dispatch; usage exceeds estimate; unknown cost/usage; distinct summary profile; and shared-budget competition if reachable. Assert provider invocation counts, request identity, actual accrual, final outcome, and unchanged history on rejection.

Within the existing baseline scenarios, include an already-projected native validation result and an opaque process result. Assert that attribution follows the actual first-delivery message and later replays, does not charge hidden captured content as model input, and leaves both model-facing representations unchanged.

Use repository-supported Microsoft.Testing.Platform commands from `CONTRIBUTING.md`; discover and record the actual project/filter commands rather than assuming VSTest syntax. At completion run solution build, architecture tests, relevant focused suites, `dotnet format src/Threadsmith.sln --verify-no-changes --no-restore`, analyzer verification at severity info, and the governance checks. Record unrelated baseline failures separately.

## 11. Security/Permissions

Admission cannot grant model, tool, mutation, approval, network, or secret authority. Preserve sensitive-profile selection, cancellation, and ordinary hooks. Measurements use IDs, counts, hashes, enums, and durations only. No staging, committing, pushing, or destructive Git actions are implied.

## 12. Observability

Expose context/budget/both pressure, estimated combined path, accepted/rejected dimension, summary attempt count, actual usage, and unknown dimensions through existing inspection/events. Normal summary activity must remain visible. Report source-derived bounds separately from measurements.

## 13. Migration/Compatibility

Existing sessions need no projection metadata. The output allowance makes admission more conservative; document the intentional earlier controlled exhaustion. Providers without cache or usage reports retain their existing accounting behavior with explicit limitations. Unsupported loops remain listed, not silently declared covered.

## 14. Acceptance Criteria

| ID | Agent-verifiable pass condition | Required evidence |
|---|---|---|
| B1 | Ordinary provider count is zero when its conservative delta fails; admissible requests charge one call and actual usage once. | Parameterized admission tests and recorded command. |
| B2 | Summary provider count is zero when summary fits but continuation does not, or fewer than two calls remain. | Real conversation-entry scenario with scripted provider. |
| B3 | Every retry rechecks headroom; an inadmissible retry is not dispatched. | Retry scenario with exact counts and accrued usage. |
| B4 | Continuation bound includes validated maximum summary rendering, schemas, frozen/retained content, framing, and ordinary output; final request is checked again. | Bound calculation test and final-request assertion. |
| B5 | Rejected/invalid/cancelled candidates preserve history and generations; spent usage is retained. | Failure/cancellation scenarios. |
| B6 | Same-budget concurrency is either proved absent on covered paths or protected and tested. | Call-site inventory and applicable race test. |
| B7 | Four reproducible baselines separate request size, cumulative usage, estimates, cache semantics, and missing data; tool attribution distinguishes available captured size, unchanged first-delivery projections, and replay without crediting existing savings to this delivery. | Sanitized fixture/report paths, projected/opaque result assertions, and commands. |
| B8 | Existing execution, hooks, activity, sensitivity, and protocol contracts pass; required validation has no unexplained failures. | Test/build/format/governance results and adversarial review notes. |

Completion evidence (2026-09-25):

- B1/B2: `Milestone4Tests.SessionApplication_RequestEstimateExceedsBudget_DoesNotDispatchProvider`, `Milestone4Tests.SessionApplication_OutputAllowanceExceedsBudget_DoesNotDispatchProvider`, and `Plan80ActiveTurnContinuationTests.Summary_without_two_call_headroom_is_not_dispatched` verify zero provider calls on rejection and unchanged history.
- B3/B5: `Plan80ActiveTurnCompactionTests.Retry_is_not_dispatched_after_actual_usage_consumes_headroom` uses actual usage greater than the prepared attempt estimate, preserves the spent first attempt, and rejects the retry. Existing invalid/cancellation cases remain green.
- B4: `Plan80ActiveTurnContinuationTests.Summary_continuation_headroom_uses_delivered_result_capacity_fallback` proves the exact continuation applies the established delivered-result reducer; `Summary_activation_rechecks_final_prepared_request` rejects after summary usage and before activation. `Rendered_summary_character_bound_is_enforced` fixes the summary-side bound. Prepared wire estimates include frozen messages, retained groups, schemas, framing, and ordinary output reserve through `CreateRequestEnvelope` and `ModelWireEstimator`.
- B6: `Milestone1Tests.SessionApplication_DefaultExecutionBudgetScopesConcurrentRuns` submits concurrent one-call runs through the default concrete budget and verifies both complete in independent scopes. `ApplicationComposition` supplies `CreateScope` explicitly for production conversation and mutation applications. Custom `IBudget`/factory implementations own any intentional cross-run sharing.
- B7: [the reproducible baseline report](evidence/plan-113.1-baseline.md) fixes profile, tool, output, usage, cache, and missing-data assumptions and records the four workload rows. `Plan80ActiveTurnContinuationTests.Admission_baseline_replays_actual_pipeline_model_results` executes the real `DotNetBuildTool` and `RunProcessTool` through `ToolInvocationPipeline` and the ordinary conversation result-to-message path. It verifies hidden captured native output never enters provider input, both first-delivery strings remain byte-for-byte unchanged, unique evidence is larger than overlapping replay, and cumulative replay exceeds latest-request occupancy. `Plan113AdmissionMeasurementTests` preserves reported cache and missing-usage semantics. The failed-summary row uses the observed provider count and 300-token actual usage from the B3 retry fixture.
- B8: solution build and `AnalysisLevel=latest-all` build completed with zero warnings/errors. Full relevant suites passed: ConversationContext 150, Planning 147, CoreRuntime 651 with two explicit skips, Architecture 281 with four live-environment skips, Codex provider 43, and Anthropic provider 168. Focused post-review additions passed: continuation 9 and compaction/measurement 26. `git diff --check` and planning-governance searches were clean. The first parallel Architecture run encountered a transient Windows file-lock cleanup failure; its isolated rerun passed 281 with four live-environment skips. `dotnet format src/Threadsmith.sln --verify-no-changes --no-restore` continues to report pre-existing formatting findings in unrelated files and untouched lines; no whole-repository formatting rewrite was applied.
- Two independent adversarial reviewers traced runtime/budget and test/acceptance paths. Valid findings were fixed before completion: mixed known/unknown lower bounds, overflow, per-run budget ownership, exact summary telemetry, cancellation/error observability, eager prefix preparation, capacity fallback ordering, mutation/Codex output admission, final recheck, and real projection attribution.

## 15. Risks

Conservative bounds may reject useful attempts; tests must demonstrate the tradeoff without weakening hard admission. Late budget triggering protects dispatch but does not recover earlier replay cost. Provider estimates, unknown pricing, missing usage, and failed summaries limit guarantees; preserve those distinctions in reports.

## 16. Documentation

Update only owned admission/context/inspection documentation during implementation. Follow planning governance and keep completed plans frozen. Prompt filename, purpose, or token-contract changes require prompt operations/reference updates in the same change. No acceptance/manual workflow edits are required merely to split the plan.

## 17. Open Decisions

Resolved in §6.5. The prepared request remains provider-owned until exposed through the existing compaction-attempt boundary; run-scoped budgets avoid cross-run reservations; provider capability selects enforced versus hard output ceilings; and the rendered-summary bound is 65,536 characters.
