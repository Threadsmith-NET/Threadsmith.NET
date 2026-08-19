# Implementation Plan 75: Plan Approval Policy and Sanity Checks

**Milestone:** M23.4 - Roslyn-Based Pre-Mutation Analysis
**Strategy source:** User-requested low-friction plan approval policy; Plans 09, 30, 37, 40, 44, 51-57, and 74
**Prerequisite plans:** plans 09, 18, 27, 30, 37, 40, 44, 48, 51-57, and 74

## 1. Objective

Add configurable plan-approval policy so trusted users can avoid approving every valid low-risk plan while preserving plans as host-owned execution contracts. Every proposed plan, even one that still requires manual approval, must pass cheap repository sanity checks before being shown to the user; repairable plan problems are returned to the model for plan revision instead of wasting the user's attention.

The core outcome is:

> Plans remain mandatory structured contracts for mutation scope, correction, resume, audit, and later review, but plan approval can be policy-authorized and non-interrupting after host validation and repository sanity checks.

## 2. Architectural Context

- Plan 09 owns structured planning and context-governed `propose_plan` output.
- Plan 18 owns durable session restoration and tolerant event evolution.
- Plan 27 owns shared interactive/headless command patterns and repository-scoped availability controls.
- Plan 30 owns mutation approval policy and `/policy`; plan approval must remain a separate trust boundary from exact-diff mutation approval.
- Plan 37 owns approved-plan execution, plan-step correlation, mutation proposal, staging, validation, correction, and durable resume.
- Plan 40 owns lifecycle hooks including `PlanProposed`; managed policy may still block plans but repository hooks cannot grant approval.
- Plan 44 owns lifecycle mutation risk and path identity rules.
- Plan 48 owns repository-scoped persisted user choices with safe precedence and restart behavior.
- Plans 51-57 own canonical request construction, durable session transitions, parallel tools, and safe-boundary continuation ordering.
- Plan 74 owns pre-mutation proposal repair; this plan adds an earlier plan-revision repair loop for bad plan contracts before mutation proposal.

## 3. Scope

- Add a host-owned `PlanApprovalPolicy` distinct from `MutationApprovalPolicy`.
- Add repository/user/session-effective configuration and a shared interactive/headless command to inspect and change plan approval policy.
- Support low-friction modes equivalent to the approval modes discussed by the user: `ReviewAll`, `ReviewRisky`, `TrustSession`, `AlwaysTrustRepo`, and an explicit strongest auto-approval mode for valid plans.
- Run cheap host-owned plan sanity checks for **all** model-proposed plans before any manual prompt or policy auto-approval.
- Return repairable plan sanity failures to the model as bounded plan-revision evidence instead of presenting obviously invalid plans to the user.
- Keep plans mandatory as structured execution contracts: mutation proposals must still cite plan step ids and remain bounded by approved/auto-approved plan scope.
- Preserve exact-diff mutation approval policy, transactional mutation, pre-mutation Roslyn screening, and authoritative build/test validation unchanged.
- Record durable approval provenance: manual approval, policy auto-approval, policy id/mode, scope/risk summary, revision, and any sanity-check repair rounds.
- Update TUI/headless projection so auto-approved plans are visible as concise status, with full plan details available through existing transcript/durable records.

## 4. Non-Scope

- No removal of structured plans.
- No model self-approval and no repository-controlled approval authority.
- No mutation application, staging, build, test, restore, network access, or external process execution during plan sanity checks.
- No natural-language scraping as authoritative scope when structured schema-2 `fileIntents` are available.
- No bypass of managed hook denials, repository trust, path safety, mutation policy, exact-diff review, pre-mutation Roslyn analysis, or post-mutation validation.
- No automatic approval of broad, destructive, external-system, credential, generated/binary, dependency, lifecycle, or ambiguous plans unless an explicit strongest trusted policy allows it and hard guardrails still pass.

## 5. Current State

Implementation complete. Threadsmith validates plan structure and bounded path shape, runs cheap repository sanity checks before review or policy auto-approval, returns repairable plan-scope failures to the model for bounded plan revision, and supports a separate configurable plan approval policy through `/plan-policy` / `planning:approvalPolicy`.

Auto-approved plans remain durable structured execution contracts. Exact-diff mutation approval, transactional mutation, pre-mutation Roslyn screening, post-mutation validation, correction, cancellation, and resume gates remain separate and authoritative.

## 6. Proposed Design

### 6.1 Plan approval policy

Introduce a closed `PlanApprovalPolicy` enum or value object with explicit semantics:

- `ReviewAll`: every valid plan requires manual user approval. This is the safest default for unknown repositories.
- `ReviewRisky`: auto-approve low-risk valid plans; require manual approval for broad, ambiguous, lifecycle, generated/protected, dependency, configuration, external-system, credential, or policy-sensitive plans.
- `TrustSession`: auto-approve valid plans for the current interactive session after sanity checks; do not persist across restarts.
- `AlwaysTrustRepo`: persistently auto-approve valid plans for the exact trusted repository identity, stored outside repository-controlled content or in the existing guarded user/repository preference mechanism as appropriate.
- `AutoApproveAllValid`: auto-approve every plan that passes hard validation and sanity checks, while still failing closed on hard guardrails. This mode must be explicit, clearly warned, and unavailable for untrusted repositories.

Names may be adjusted during implementation to match existing policy naming conventions, but behavior must stay distinct from mutation approval policy.

### 6.2 Configuration and command surface

Add a shared command, tentatively `/plan-policy`, with headless parity. It must support:

- current effective policy display;
- direct selection by mode;
- interactive numbered selection when omitted;
- repository-persisted changes for every policy except `TrustSession`;
- repository-persistent changes where eligible;
- reset/revoke to inherited/default policy;
- warnings for trust modes and strongest auto-approval.

Example shapes:

```text
/plan-policy
/plan-policy ReviewRisky
/plan-policy TrustSession
/plan-policy AlwaysTrustRepo
/plan-policy reset
```

Configuration should be layered consistently with existing user/repository settings. `/plan-policy` persists every plan policy except `TrustSession` in repository settings. Persistent trust for `AlwaysTrustRepo` must be bound to exact repository identity and not granted merely by repository content.

### 6.3 Plan sanity checks for all plans

Before manual prompt or auto-approval, run cheap host-owned checks against the structured plan:

- flatten all `ImplementationPlanStep.FileIntents` source and destination paths;
- normalize to repository-relative paths using existing path safety rules;
- reject rooted, traversal, `.git`, secret/protected, outside-repository, and forbidden lifecycle targets;
- classify bare file names or glob-like/ambiguous entries as ambiguous unless exactly one repository match is proven within bounds;
- verify declared existing-file edits point to files that exist in the current repository/baseline;
- verify declared create targets do not already exist when the step clearly declares creation;
- classify generated/binary/non-text files as risky or blocked according to existing policy;
- classify lifecycle operations, dependency/project/package changes, configuration changes, and test deletion as risky;
- cap number of affected files/directories and total path bytes;
- preserve filesystem-sensitive path comparison behavior consistent with workspace rules;
- consult managed Plan-40 policy hooks after host sanity evidence is formed, while preserving that hooks cannot grant approval.

Sanity checks should use structured plan fields and bounded repository metadata. They may optionally use current read-only evidence from the planning turn, but must not require full semantic discovery, build, test, restore, or Roslyn compilation.

### 6.4 Plan revision repair loop

If sanity checks find repairable problems, do not show the plan to the user. Return a bounded plan-revision packet to the model and ask it to call `propose_plan` again.

Repairable examples:

- affected file does not exist for an edit step;
- bare file name has multiple matches and needs exact relative path;
- affected file list is empty when policy requires concrete files;
- step declares a create target that already exists;
- step claims a path outside approved scope but an in-repo alternative is evident;
- plan text contradicts structured affected files;
- risk classification is missing or underreported.

Non-repairable examples:

- protected/secret/Git metadata path;
- path escape;
- untrusted repository tries to grant approval;
- managed policy denial;
- repeated revisions exceed budget;
- unsupported broad/external/dependency operation under current policy.

The repair loop is bounded by plan-revision count, model-call budget, elapsed time, and cancellation. It must be durable at safe boundaries and record sanitized summaries without source contents or secrets.

### 6.5 Auto-approval decision

After schema validation, sanity checks, and managed policy checks pass, compute a plan risk classification:

- `Low`: exact existing source/test/doc paths, bounded count, no lifecycle/dependency/config/secret/generated/external changes.
- `Moderate`: multiple files, tests plus source, limited lifecycle creates, documentation/config without secrets, or partial path confidence.
- `High`: deletes/moves, project/package/dependency changes, generated/binary files, broad directories, cross-repository effects, external systems, credentials, hooks/policies, or ambiguous scope.
- `Blocked`: hard guardrail failure.

Policy maps risk to action:

- `ReviewAll`: prompt for every non-blocked plan.
- `ReviewRisky`: auto-approve `Low`; prompt `Moderate` and `High`.
- `TrustSession`: auto-approve `Low` and `Moderate`; prompt `High` unless explicitly configured to allow it for the session.
- `AlwaysTrustRepo`: auto-approve valid `Low` and `Moderate` for the exact repository identity; prompt/deny `High` according to managed/user policy.
- `AutoApproveAllValid`: auto-approve every non-blocked plan after a prominent warning and repository trust check.

Implementation may tune thresholds, but every decision must be deterministic, explainable, and recorded.

### 6.6 Presentation and audit

Manual review remains available and should show approval-relevant facts, including affected files and risk classification. Auto-approved plans should produce concise projection such as:

```text
PLAN: auto-approved - rev 1 - 1 step - 1 file - low risk - policy ReviewRisky
```

The full structured plan remains in durable session records and transcript/context authority. The model must still propose mutations tied to approved step ids; out-of-plan mutations are rejected or require plan revision.

## 7. Public Contracts

Expected host-owned contracts, names illustrative:

- `PlanApprovalPolicy`
- `PlanApprovalDecision`
- `PlanRiskClassification`
- `PlanSanityCheckRequest`
- `PlanSanityCheckResult`
- `PlanSanityIssue`
- `PlanSanityIssueKind`
- `PlanApprovalPolicyChanged`
- `PlanSanityCheckCompleted`
- `PlanAutoApproved`

Durable events must avoid source contents, secret values, raw hook payloads, provider SDK types, terminal types, Roslyn objects, or unbounded path lists.

## 8. Project/File Changes

Expected implementation areas:

- `src/Threadsmith.Core`: plan approval policy DTOs/events and plan sanity result contracts.
- `src/Threadsmith.Execution`: plan validation/sanity checker, plan-revision repair loop, auto-approval decision, durable event emission, and existing plan-step mutation correlation preservation.
- `src/Threadsmith.Configuration` or owning configuration layer: plan approval policy binding, precedence, defaults, and repository identity persistence.
- `src/Threadsmith.Tui`: `/plan-policy` command, plan review/auto-approval projection, warnings, and headless parity plumbing where commands are shared.
- `src/Threadsmith.App`: production composition of the policy/sanity services and command registration.
- `tests/Threadsmith.Milestone4.Tests` and/or execution/TUI suites: schema/sanity/repair/auto-approval coverage.
- `.threadsmith/config.example`, `docs/user-guide.md`, `docs/operations/execution-resumption.md`, `docs/architecture/validation-pipeline.md`, `docs/implementation-plans/manual-test-plan.md`, and DOX files.

## 9. Ordered Tasks

1. **Define policy and sanity contracts.** Add closed plan approval policy, risk classification, sanity result, issue kind, approval decision, and durable event contracts in Core using host-owned DTOs only.
2. **Bind configuration safely.** Add layered configuration binding for safe defaults and session/user/repository-effective values, with explicit repository-identity fencing for persistent trust modes and reset/revoke semantics.
3. **Add the command surface.** Implement shared interactive/headless `/plan-policy` inspection, direct selection, numbered selection, warnings, persistence where eligible, and reset behavior without overwriting unrelated configuration.
4. **Implement repository plan sanity checks.** Build a bounded host-owned checker that evaluates structured schema-2 `fileIntents`, path safety, existence/ambiguity, protected/generated/binary classification, lifecycle/dependency/config/test-deletion risk, and managed policy denial inputs before any plan review surface.
5. **Wire plan revision repair.** Integrate sanity-check repairable failures into a bounded plan-revision loop that returns concise evidence to the model and asks for `propose_plan` again before user review or auto-approval.
6. **Compute auto-approval decisions.** Map sanity/risk results to policy decisions and record approval provenance, policy, risk, scope summary, revision, repository identity where relevant, and repair history.
7. **Update plan presentation.** Show approval-relevant facts during manual review and concise `PLAN: auto-approved` projection for policy-approved plans; keep full structured plans durable and context-authoritative.
8. **Preserve mutation gates.** Ensure approved/auto-approved plans remain required for mutation proposals, mutation proposals cite valid step ids and stay within approved scope, and Plan-30/37/74 exact-diff/pre-mutation/post-validation gates remain unchanged.
9. **Add persistence/resume handling.** Persist policy-change and plan-sanity/auto-approval events, restore sessions at safe boundaries, and ensure cancellation/budget exhaustion resumes or fails deterministically.
10. **Update docs and DOX.** Update user guide, config example, validation/resumption docs, manual tests, scenarios, milestone detail, shared context, and applicable AGENTS inventories.

## 10. Testing

Automated tests must include:

- Unit tests for `PlanApprovalPolicy` binding, precedence, reset, warning text, and repository identity fencing.
- Unit tests for plan sanity checks over existing, missing, ambiguous, protected, generated, binary, lifecycle, dependency, config, test-deletion, empty-scope, and bare-name paths.
- Execution tests proving sanity failures revise the plan before any approval prompt or mutation proposal.
- Execution tests proving `ReviewAll`, `ReviewRisky`, `TrustSession`, `AlwaysTrustRepo`, and `AutoApproveAllValid` prompt or auto-approve only eligible plans and record provenance.
- TUI/headless command tests for `/plan-policy` display, selection, warning, persistence, reset, restart, and repository-switch behavior.
- Resume/cancellation tests across plan sanity check, plan repair, auto-approval, and manual prompt boundaries.
- Architecture tests confirming no UI/config/provider/Roslyn/terminal types leak into Core contracts and repository content cannot grant persistent trust.
- Regression tests proving exact-diff mutation approval, pre-mutation Roslyn screening, transactional apply, post-mutation validation, and correction still run after plan auto-approval.

Manual verification is captured in MTP-242.

## 11. Security/Permissions

- Repository-controlled content must not grant persistent trust to itself.
- `AlwaysTrustRepo` and `AutoApproveAllValid` require explicit user/managed trust and exact repository identity binding.
- Plan sanity checks must never read or emit secret values, source contents, raw hook payloads, provider payloads, or unbounded path lists.
- Protected, secret, `.git`, outside-repository, path traversal, and managed-policy-denied plans fail closed before user presentation or auto-approval.
- Repository hooks remain advisory unless separately managed/trusted; hooks may block but cannot grant approval.
- Plan auto-approval authorizes only implementation proposal attempts, never exact diff approval or repository writes.
- Strong trust modes must show clear warning text and be revocable.

## 12. Observability

- Emit sanitized durable events for policy changes, sanity-check completion, plan repair attempts, manual approvals, and policy auto-approvals.
- Record plan revision, policy, risk classification, bounded scope summary, approval source, repository identity fingerprint where relevant, and omitted/blocked sanity issue counts.
- Do not persist source text, secret values, raw model/provider payloads, raw hook payloads, or unbounded path enumerations.
- Interactive/headless output must be concise, terminal-safe, and explain why a plan was auto-approved, prompted, revised, or blocked.
- Context inspection and diagnostic bundles may include sanitized plan-sanity provenance and policy state but not sensitive path contents or secrets.

## 13. Migration/Compatibility

- Default behavior remains `ReviewAll` unless an explicit user/session/repository policy changes it, preserving existing manual-review behavior.
- Existing sessions without plan-approval events restore using the default/inherited policy.
- Existing plan records remain valid; new events must be schema-version tolerant under Plan 18.
- Existing mutation approval settings and `/policy` behavior are unchanged.
- Repository config examples may document safe defaults, but persistent trust modes require guarded user/repository identity approval and must not be silently enabled by checked-in config.
- Headless callers get deterministic exit/status behavior rather than interactive prompts when policy requires approval but no approval channel exists.

## 14. Acceptance Criteria

- A configured `PlanApprovalPolicy` controls whether valid plans are manually reviewed or policy auto-approved, independent of mutation approval policy.
- `/plan-policy` and headless equivalents can inspect, set, persist where eligible, and reset policy without overwriting unrelated configuration.
- Every plan, including manually reviewed plans, receives cheap repository sanity checks before user presentation.
- Repairable sanity failures trigger bounded plan revision instead of presenting invalid plans to the user.
- Hard path/trust/secret/protected/generated/external-policy failures fail closed and cannot be auto-approved.
- Auto-approved plans remain durable structured contracts, and later mutation proposals must cite approved step ids and stay within approved scope.
- Auto-approval records policy, repository identity where relevant, risk, scope summary, revision, and sanity outcome.
- Existing mutation approval, exact diff, pre-mutation Roslyn screening, transactional apply, post-mutation build/test validation, correction, cancellation, and resume behavior remain unchanged.
- Interactive and headless output are concise, explainable, redacted, and terminal-safe.
- Focused tests cover all policy modes, repository persistence/reset, invalid file repair, ambiguous file repair, non-existent file repair, protected-path denial, lifecycle risk escalation, managed policy denial, cancellation/budget exhaustion, and unchanged mutation gates.

## 15. Risks

- **Risk:** Auto-approval weakens user trust by hiding important scope.  
  **Mitigation:** Keep concise auto-approval projection, durable full plan records, risk classification, and manual review modes.

- **Risk:** Sanity checks reject valid exploratory plans that cannot know files yet.  
  **Mitigation:** Make requirements policy-sensitive; allow bounded `UnknownScope` only in explicit trust modes, and require exact mutation validation later.

- **Risk:** Bare filename matching is expensive or ambiguous.  
  **Mitigation:** Bound search, prefer structured exact paths, and return repairable ambiguity evidence.

- **Risk:** Plan approval and mutation approval become confused.  
  **Mitigation:** Use separate command names, separate config keys, separate events, and explicit UI text.

- **Risk:** Repository configuration grants its own trust.  
  **Mitigation:** Bind persistent trust outside repository control or to exact repository identity under existing guarded preference rules.

## 16. Documentation

- Update `docs/user-guide.md` with `/plan-policy`, modes, examples, and distinction from `/policy` mutation approval.
- Update `.threadsmith/config.example` with safe non-trust defaults and comments.
- Update `docs/operations/execution-resumption.md` with plan-sanity and auto-approval safe boundaries.
- Update `docs/architecture/validation-pipeline.md` to place plan sanity before plan review/auto-approval and mutation pre-screening.
- Update `docs/implementation-plans/manual-test-plan.md`, `acceptance-scenarios.md`, milestone detail, and DOX files.

## 17. Open Decisions

- Decide final command/config names: `/plan-policy` and `planning:approvalPolicy` are proposed but may be adjusted to match existing command/config naming conventions.
- Decide whether `AutoApproveAllValid` should be product-supported or retained only as an internal/test-managed policy mode.
- Decide exact risk thresholds for `Moderate` and `High`, especially documentation/config changes, limited lifecycle creates, and test-only changes.
- Decide whether bare filename resolution is allowed under `ReviewRisky` when exactly one bounded match exists, or whether exact repository-relative paths are mandatory for auto-approval.
- Decide headless behavior when policy requires manual plan review but no approval channel is available: fail closed immediately or emit resumable pending-approval state.
