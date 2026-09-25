# Implementation Plan 113.3: Measured Active Context Expansion

**Status:** Planned.

**Delivery track:** Maintenance — evidence-gated active-context efficiency.
**Prerequisites:** Accepted [113.1](plan-113.1-active-context-budget-protection-and-measurement.md) and [113.2](plan-113.2-deterministic-active-context-deduplication.md), including their reproducible reports and retained limitations.
**Sequence:** Third delivery in [plan 113](plan-113-supersession-aware-active-context-management.md). A documented no-expansion result is valid when measured gains do not justify additional machinery.

## 1. Objective

Use the preceding deliveries' evidence to select the smallest justified improvement: first-delivery output projection, earlier savings-based activation, partial overlaps, search/symbol projection, extension participation, or additional existing consumers. Demonstrate incremental benefit against 113.2 while preserving answer support and all admission/visibility contracts. Do not implement every candidate by default.

## 2. Architectural Context

113.1 owns conservative admission; 113.2 owns per-result capture, exact source proof, final-request validation, recovery, and atomic rewrite. Extend these same components and entry points. Do not introduce another planner, visibility index, summarizer, transcript, or extension context-interception API.

Implementing agents must read root `AGENTS.md`, planning governance, shared context §G, and C# guardrails, confirm the checkout, and inspect the implemented predecessors rather than treating their proposed names as authoritative code.

## 3. Scope

- Bounded opportunity measurement and a recorded selection/rejection decision for each candidate.
- Implementation only of candidates that pass §6's gate.
- Targeted invariant tests, control replays, and one bounded live comparison when configured access is available.
- Explicit assessment of answer support, recovery/read repetition, cache behavior, planner overhead, and cumulative use.

## 4. Non-Scope

No mandated general claim framework, automatic public extension API, fuzzy relevance, embeddings, model-controlled pruning, repository-wide preloads, benchmark project, broad evaluation matrix, or relaxed authority/first-delivery guarantees. No tuning to a single motivating question. Do not spread the planner across unsupported loops.

## 5. Current State

Use predecessor reports as the baseline. At implementation start replace assumptions with measurements: residual duplicate input, partial-overlap opportunities, opaque result families, recovery frequency, below-pressure replay, cache effects, and unsupported consumers. No percentage improvement or general answer-quality claim is assumed in advance.

## 6. Proposed Design

### 6.1 Candidate gate

For each candidate, record affected real call sites, residual overhead, a synthetic representative fixture, expected gain mechanism, added state/API/lifecycle cost, and the existing component to extend. Reject a candidate if the measured issue is absent or a smaller existing-path change addresses it.

Before implementation, record a fixed experiment and acceptance threshold: strictly positive net cumulative-input savings on its target replay after summary/recovery/schema overhead; preserved oracle facts; and no extra model/recovery calls on unique-evidence/no-op controls. Also define a maximum acceptable measured planner latency/allocation regression and, where reported, billed-cost/cache regression for that experiment. Choose these bounds from predecessor measurements and record the rationale before observing candidate results; do not move thresholds to accommodate a failure.

Implement one candidate at a time. Keep only passing candidates; remove experimental competing paths. If none qualifies, complete the measured decision report with no new production machinery. Passing deterministic replay establishes the stated workload result, not universal model quality or savings.

### 6.2 Earlier savings-based activation

Evaluate this candidate explicitly because failed-next-request admission can occur after most avoidable spending. Separate capacity safety from economic optimization. Use measurable removable replay input and rewrite costs, not an arbitrary context percentage or guessed count of remaining rounds.

A candidate can require positive net savings on the next admissible request, including schema/receipt costs, with batching/hysteresis to avoid repeated cache-prefix churn. Compare continued raw replay, 113.2 pressure-only activation, and the candidate under identical cache assumptions. Unknown future rounds or cache pricing stay unknown; report logical token savings separately from billed cost. Do not add a proactive model-summary policy merely because a deterministic rewrite saves input.

### 6.3 Partial source overlap

Extend the same fragment ownership contract with bounded exact interval subtraction only if measured residual overlap justifies it. Produce disjoint retained spans in deterministic order; verify intersection content in a proven snapshot, not equality of whole-range hashes. Bound fragment explosion and fail closed on limit/uncertainty. Revalidate final-request dependencies after splitting, summaries, and invalidation.

### 6.4 Search and symbol projectors

Use typed results and invocation scope/options, captured before serialization. Preserve unmatched paths, negative results, truncation, ambiguity, omissions, errors, and continuation. Only duplicated source fragments may disappear without a separate lossy-compaction decision. Stable symbol identity alone is not evidence that source or all relationship facts remain visible. Source edits and semantic generations invalidate proof. No tool-name switches in orchestration or parsing opaque prose.

### 6.5 Extensions

Add an optional bounded abstraction DTO only if representative extension-produced results demonstrate benefit not already available through opaque summary fallback. Keep self-supplied compact alternatives distinct from host-verified exact coverage. Extension provenance strings alone cannot authorize suppression of another result. Exact claims require host-verifiable observation proof; otherwise remain opaque for cross-result deduplication.

Map/copy to host DTOs, enforce schema/count/character bounds, preserve leases/unload, and reject malformed projections without changing ordinary tool success semantics. An extension receives no history, evidence-store, provider, or arbitrary request-rewrite capability. Existing binaries and absent projections retain their existing behavior.

### 6.6 Other consumers

For each child/proposal/correction/approved-plan loop, trace actual tool continuation, authority, events, logs, progress, completion, and cancellation. Integrate only if it can call the same implementation without duplicating lifecycle machinery. Shared formatting does not establish shared execution. Record unsupported consumers and concrete reasons; child result schemas and authority do not change.

### 6.7 First-delivery output projection

Use 113.1's attribution to identify remaining oversized model-facing results after existing native validation projections and compact Git output. Extend the tool-owned `ModelResultContent` path only for measured opportunities, such as repeated diagnostics or verbose known-format failure logs. Compare against the existing projected baseline, not an artificial raw-terminal baseline. This candidate reduces a result before first delivery; subsequent history deduplication still requires verbatim delivery of that declared model-facing result.

Prefer typed outcomes and diagnostics. Preserve exit/outcome status, timeout, failed/skipped counts, diagnostic identity and location, project/framework distinctions, failure details, omissions, and continuation. Unknown or unrecognized output retains the existing bounded representation. Do not introduce a shell-command rewrite proxy, generic errors-only filter, or another process execution path. Keep exact source reads exact; signature-only views require an explicit structural exploration contract rather than silent body removal.

The conversation evidence path currently stores the selected model-facing content; a smaller `ModelResultContent` does not automatically preserve its larger underlying output. If a selected projector promises recovery of omitted detail, explicitly retain and link the sanitized bounded underlying result through existing evidence/artifact mechanisms, with bounded authorized reads and retention limits. Do not claim recovery of uncaptured or expired output. Preserve raw-data diagnostic opt-in and normal telemetry restrictions. This retention work is conditional scope of the selected 113.3 candidate, not a new requirement for 113.1 or 113.2.

Measure first-delivery and replay savings separately, then assess net cumulative input including recoveries. Apply the same predeclared candidate gate as every other expansion; smaller output alone does not establish preserved answer support.

## 7. Public Contracts

Default to no new public API. Any selected extension/subsystem contract must document a measured need, bounded versioned DTO shape, fallback behavior, and compatibility/unload tests before addition. Tool-specific partial residue rendering stays with the owning projector.

## 8. Project/File Changes

Use the predecessor-owned tool/context/execution/frontier/recovery files for selected candidates. Search/symbol typed producers, extension abstractions/runtime, or child history may change only when their candidate passes the gate. Extend existing tests and operations documentation; no new project or generic benchmark harness.

## 9. Ordered Tasks

1. Verify predecessor acceptance and rerun their corpus; record baseline commands, limits, and unsupported consumers.
2. Inventory and quantify all six candidate categories in §6. Record pursue/reject/defer decisions with fixture evidence and specific reuse points, including existing first-delivery projections before proposing output filtering.
3. Freeze selected candidate scope, experiment, thresholds, bounds, and test oracles here before production changes. No candidate means proceed to the evidence/report tasks.
4. Implement one selected candidate through existing paths; add its distinct invariant tests and rerun predecessor regressions.
5. Compare with 113.2 and no-op controls; remove candidates that fail the recorded gate. Do not keep unused abstractions for speculative future use.
6. Run a bounded real-provider comparison if configured access is available, following §10. Record missing access as unassessed live behavior, not a passed comparison.
7. Review real entry points for reuse, observable integration, proportional work, and evidence limits. Run focused/architecture/build/format/governance validation for implemented changes.
8. Record decisions, accepted gains, rejected options, live evidence/limits, and acceptance outcomes here; update only owned docs.

## 10. Testing

Reuse the predecessor replay corpus and existing suites. Add only fixtures/invariants specific to selected candidates: interval boundaries/fragment limits; search residue and symbol ambiguity; extension spoofing and unload; earlier trigger/cache stability; or shared-loop cancellation/authority/activity. No hard test-method quota.

For a selected first-delivery projector, test success, failure, timeout, incomplete/truncated output, and unknown-format fallback through the ordinary tool entry point. Assert preserved outcome/diagnostic facts, unchanged command execution and policy, accurate omission markers, and retrieval of retained details when recovery is promised. Include recovery overhead in the savings test and verify exact source reads are unchanged.

Every deterministic fixture names required facts and their source/recovery references. Assert required facts remain represented or explicitly recoverable under the applicable contract, admission holds, and no invalid exact-visibility claims appear. Exercise negative/incomplete facts, not just positive source snippets. Scripted outcomes test the host contract, not model comprehension.

For the bounded live comparison, use the same task, repository revision, model/profile, inventory, settings, and matched cache treatment for 113.2 versus the selected result. Fix token/call/time limits before running; do not loop until favorable. Use existing configuration and tooling without exposing credentials. Record context size, cumulative provider input/output, host budget use, summary/model calls, recoveries/repeated reads, cache semantics, known cost, duration, and answer-support rubric results. A single pair is diagnostic, not statistical proof. Unsupported final claims or lost required findings require investigation before calling the candidate beneficial.

No configured provider access: deterministic implementation acceptance can complete, but the report must explicitly say live answer behavior and provider economics are unassessed. Do not fabricate live pass status or generalize replay estimates into actual billed savings.

Run repository-supported focused suites and predecessor invariants, architecture tests, solution build, formatting/analyzers, and governance checks for code changes. A report-only no-expansion outcome needs link/governance/diff validation and reproducible measurement evidence, not an unnecessary full build.

## 11. Security/Permissions

All predecessor trust, sensitive-data, cancellation, approval, recovery, and host-authority constraints remain binding. Extension-authored claims are untrusted. Ordinary model/tool/MCP handling and activity cannot be bypassed. Benchmark telemetry must not expose proprietary source or credentials; raw diagnostics remain explicitly opt-in.

## 12. Observability

Use existing reason/count fields. Add only closed reasons needed to explain a selected trigger/proof rejection. Report aggregate and per-workload gains/losses; retain estimates, actual provider reports, and unknowns as distinct values. No new normal-log source bodies.

## 13. Migration/Compatibility

Absence/unknown version of new proof metadata falls back safely. Preserve existing serialized checkpoints and opaque results. Selected public additions are optional/additive and tested for old extension behavior. Below applicable triggers, messages and cache/history generations remain stable.

## 14. Acceptance Criteria

| ID | Agent-verifiable pass condition | Required evidence |
|---|---|---|
| E1 | Every candidate category has a measured pursue/reject/defer decision and concrete reuse analysis. | Decision table with fixtures, commands, baseline metrics, and call sites. |
| E2 | Each selected candidate has a pre-recorded scope, threshold, bounds, and oracle; no unselected machinery ships. | Experiment records and final diff review. |
| E3 | Each shipped candidate beats 113.2 on its target net-input metric, preserves required facts, and passes predeclared control/overhead bounds. First-delivery candidates use the existing projected baseline and verify outcome preservation, unknown-format fallback, and any promised recovery. | Reproducible before/after report and applicable ordinary tool-entry assertions. |
| E4 | Predecessor admission, first delivery, final visibility, recovery authority, and atomic generation invariants still pass. | Regression commands/results through actual entry points. |
| E5 | Selected extensions/consumers preserve authority, lifecycle, activity, and compatibility; non-selected cases are explicitly out of scope. | Applicable spoof/unload/consumer tests and inventory. |
| E6 | Live comparison is recorded with support assessment and fixed limits, or explicitly unassessed for missing configured access. | Sanitized report with no unsupported quality/cost claim. |
| E7 | Required validation passes with no unexplained failures; rejected experiments leave no competing implementation. | Build/test/format/governance and adversarial review evidence, or report-only checks. |

Completion evidence: **Pending.** If no candidate passes, E2–E5 must be marked not applicable with measured rejection evidence and unchanged production diff, not marked passed without work. E1, E6's honest limitation reporting, and E7 remain required. This outcome completes the assessment without promising unjustified expansion.

## 15. Risks

Small token gains may lose on cache cost, latency, or recovery. A single provider pair can vary in tool choices and cannot prove general quality. Partial spans and public extension contracts can add disproportionate maintenance cost. Prefer a documented rejection over retaining machinery without demonstrated benefit.

## 16. Documentation

Update context/tools/inspection operations and extension authoring only for selected shipped behavior. Update prompt catalog/reference together when affected. Record status and evidence only here; preserve completed predecessor plans and navigation-only README. Acceptance/manual changes require a stable changed user workflow.

## 17. Open Decisions

The selected candidate set and numerical overhead thresholds intentionally depend on predecessor measurements. Task 3 must resolve and record them before implementation. No further user approval is inherently required for routine choices within this plan; do not expand scope or weaken invariants to force a positive result.
