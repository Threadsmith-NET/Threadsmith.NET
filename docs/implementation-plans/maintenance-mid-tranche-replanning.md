# Mid-Tranche Replanning

**Status:** Complete (2026-09-19). Implementation, deterministic regression coverage, prompt synchronization, current documentation, and targeted GPT-5.6-Terra replanning verification are complete; physical-terminal procedures remain maintained compatibility checks.
**Delivery track:** Maintenance
**Prerequisites:** Implemented incremental approved-plan execution and objective continuation in [Plan 112](plan-112-incremental-approved-plan-execution.md); existing conversation-native corrective feedback and deployed prompt catalog.

## 1 Objective

Let implementation request further evidence and a replacement for unfinished work without inventing another planning or correction loop. Do not limit how many plans an objective may need.

## 2 Architectural Context

`MutationProposalApplication` already owns implementation decisions and malformed-output correction. `ExecutionOrchestrator` owns durable step/batch progress and validated plan boundaries. `SessionApplication.CompleteIncrementalExecutionAsync` already feeds boundaries into `GeneratePlanAsync`, ordinary tools, plan sanity, formatter, and approval. Extend those owners.

## 3 Scope

- Exclusive `request_replan` with one nonempty `reason` in incremental implementation/correction turns.
- A durable unfinished-plan boundary that reuses normal evidence/planning and replacement approval.
- Preserved applied work, diagnostic evidence, progress, budgets, validation scope, and net diff.
- Remove the plan-count cap, its configuration binding, and capability gating. Existing budgets and cancellation remain authoritative.

## 4 Non-Scope

No inspection tools inside mutation generation, model-authored execution bookkeeping, nested plans, automatic rollback, alternate tool pipeline, new validation runner, changed plan approval policy, or implicit mutation authorization.

## 5 Current State

Before this change, implementation could propose only mutations. The existing objective boundary assumed all current steps had passed, and plan continuation was capped at twelve by default. Re-entering planning from an unfinished or failed tranche therefore needed an explicit nonterminal state instead of pretending completion.

## 6 Proposed Design

Advertise `request_replan` only when the existing incremental plan continuation is enabled and host-selected step scope exists. Accept safe property casing; reject missing/empty/duplicate reason fields or mixed decisions through existing bounded corrective feedback. Sanitize and truncate the reason using the configured plan summary bound.

Persist `PlanReplanningPending` only between settled transactions. Reuse the existing plan-boundary notification with the interrupted plan and authoritative receipt. The session re-enters its main evidence/planning loop, supplies the reason and current evidence tools, and withholds objective completion. A replacement is an ordinary complete plan with a new revision and the same objective/workspace identity.

Retain completed-step evidence and unresolved validation, promote transactional bytes normally, and preserve any existing diagnostic baseline across replacement and subsequent tranches. This prevents introduced failures from becoming ignored baseline failures. Preserve cumulative scope, original file evidence, net diff, and budget accounting. A replacement cannot replay old authorization.

## 7 Public Contracts

Append `PlanReplanningPending` without renumbering existing phases. Add optional host-owned replanning reason, interrupted-plan boundary data, and proposal decision/capability fields. Keep the existing plan schema, command entry points, and completion guard. Allow a null staged candidate only for a valid unfinished-plan/preparation state.

## 8 Project/File Changes

Update Core contracts/catalog, Execution proposal/session/orchestrator owners, existing App configuration binding, shared Interaction status/resume handling, and deployed Context/Execution prompt assets. Extend existing mutation and conversation test fixtures; add focused replanning coverage. Update the prompt inventories and current workflow/validation/resource-limit documentation.

## 9 Ordered Tasks

1. Extend the exclusive implementation decision and existing corrective feedback.
2. Persist and restore unfinished-plan progress through the existing orchestrator boundary.
3. Reuse the session evidence/planning/approval cycle; reject premature completion.
4. Remove all plan-count admission and configuration paths without altering ordinary budgets.
5. Verify runtime entry points, failure-baseline preservation, restart, authorization, and presentation.
6. Synchronize prompt assets, current contracts, Scenario B, and MTP-273.

## 10 Testing

Exercise initial, partial-step, completed-prior-step, and failed-validation replan requests; malformed/mixed requests; disabled capability; no-staging behavior; process restoration; replacement plan and exact-diff approvals; budget carry-forward; cumulative diff/scope; completion rejection; and normal/replacement continuation beyond the former cap. Run solution build and focused mutation, orchestration, core, conversation, architecture, and interaction coverage. Record live-model and physical-terminal checks separately from deterministic coverage.

## 11 Security/Permissions

Replanning grants no write, rollback, inspection, or approval authority. Existing policy owns each ordinary evidence tool invocation and replacement plan/mutation. Reasons are sanitized and bounded. Unsettled operations and incompatible restoration fail closed.

## 12 Observability

Use existing checkpoint events, execution receipts, model-call/correction events, and shared status/plan/diff presentation. Unfinished work must never render as terminal success. No new presentation stream or renderer.

## 13 Migration/Compatibility

Existing state defaults to no replan reason and normal baseline behavior. Persisted unfinished state supports initial replanning without a staged candidate. Older binaries cannot resume the new phase and must fail closed. The removed `planning:incrementalPlans:maximumPlansPerObjective` setting is ignored if present; new scaffolds omit it. Prompt token `CompletedPlans` becomes `PlansUsed` and `MaximumPlans` is removed; deploy the synchronized catalog.

## 14 Acceptance Criteria

Scenario B and MTP-273 demonstrate normal-loop re-entry, preserved failures/applied work, fresh approval, honest resumability/completion, and unlimited plan count under existing execution budgets. No alternate correction loop or mutation execution path is introduced.

## 15 Risks

Failure evidence could be lost through a newly captured diagnostic baseline; test its identity and values after restart. Initial replan has no staged mutation; validate that shape explicitly. Boundary events must not trigger replay or duplicate planning. Model requests can still exhaust ordinary budgets; unlimited plan count is not unlimited resource authority.

## 16 Documentation

Update README workflow, user guide, execution resumption, mutation/validation architecture, resource limits, prompt operations/reference inventories, Scenario B, and MTP-273. Preserve completed historical planning documents.

## 17 Open Decisions

None. The requested scope is `request_replan` only, with existing main-loop reuse and no plan-count cap.

## 18 Completion Evidence

The opt-in `Objective_Terra_ReplansWhenApprovedScopeLacksRequiredEvidence` scenario used the configured `gpt-5.6-terra` profile at medium reasoning against a disposable workspace. Its deliberately incomplete approved step omitted the authoritative mapping file from active scope. Terra selected `request_replan` on the first implementation request; assertions confirmed that no mutation was staged and all repository files remained unchanged at the replanning boundary. The same session then received the ordinary execution receipt, proposed a replacement plan, produced a fresh exact diff, and explicitly completed the objective after validation.

The run completed in 19.0 seconds with four model requests, two observed plans, one authorized four-file mutation batch, and no corrective turns. The first exact diff appeared after 17.1 seconds. Although mutation guidance targeted one file, Terra kept the four mapping-driven edits together as one coherent batch; this remained within active-step scope and hard limits. Final contents and the cumulative diff were verified. This is one provider/profile measurement and does not establish cross-provider or physical-terminal behavior.
