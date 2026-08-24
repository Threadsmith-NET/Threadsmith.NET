# Implementation Plan 88: Unified Model Correction Loop Substrate

**Status:** Planned

**Delivery track:** Maintenance — consolidate scattered model-output correction without changing host authority
**Strategy source:** Shared Context §A.1, §A.2, §A.5, §C, and §G; execution, model, planning, mutation, and validation contracts
**Prerequisite plans:** Plan 87 functionality signoff; existing plan/mutation repair behavior must be covered before consolidation begins

## 1. Objective

Replace scattered one-off model-output repair loops with one small host-owned correction-loop substrate. The substrate should let each workflow define its own correction policy and message while sharing retry bounds, budget accounting, cancellation, observability, sanitized diagnostics, and continuation mechanics.

The goal is not to make the model authoritative. The goal is to make nondeterministic malformed model output recoverable everywhere it is safe to retry, while keeping host validation and workflow state transitions authoritative.

## 2. Architectural Context

Threadsmith currently has correction behavior in multiple places:

- ordinary conversation tool guidance can be returned as tool evidence for semantic-first and duplicate-tool cases;
- `propose_plan` has a bounded schema repair path inside the conversation loop;
- plan sanity and revision flows have their own repair behavior;
- mutation proposals have a separate correction loop in the mutation proposal application;
- Plan 87 adds malformed tool-invocation correction for provider-boundary and ordinary conversation failures.

These loops share concepts but not implementation: attempt counts, correction text, evidence insertion, retry eligibility, budget accounting, cancellation behavior, and failure reporting are distributed. That makes it easy for one malformed-output class to terminate while another receives useful correction.

Plan 88 creates a common substrate after Plan 87 proves the malformed-tool behavior. It should consolidate mechanics without flattening domain policy. Plan proposal, mutation proposal, sanity repair, and ordinary tool invocation still have different validation rules and authority boundaries.

## 3. Scope

- Define a shared correction-loop runner for model-output correction attempts.
- Keep correction policies typed and workflow-specific: ordinary tool invocation, `propose_plan`, plan revision/sanity, mutation proposal, and future structured outputs can each supply their own eligibility rules and correction prompt.
- Share attempt counting, exhaustion handling, cancellation, model/cost/wall-clock budget accounting, transient status, durable sanitized observability, and raw-payload redaction rules.
- Normalize correction result classifications: corrected, exhausted, cancelled, unsafe/non-recoverable, budget-exhausted, provider-failed, and superseded by workflow transition.
- Preserve existing transcript/protocol requirements for tool-call correlation, provider message order, and structured chronological messages.
- Keep all provider SDK, Roslyn, terminal, extension, MCP, and persistence implementation types behind existing boundaries.
- Migrate existing specialized loops only when their current behavior is covered by focused tests or explicit acceptance notes.
- Implement functionality first, then stop for user review.
- Write or update automated tests only after the user signs off on the behavior.
- Write or update user/operator documentation only after the user signs off on the behavior.

## 4. Non-Scope

- No change to the validated schemas for plans, mutations, or tools unless required to preserve existing behavior.
- No host-side repair of model-authored JSON into executable operations.
- No relaxation of approval, trust, mutation, path, process, MCP, extension, validation, or tool policy gates.
- No replacement of domain-specific validation with a generic parser.
- No new autonomous planner, self-healing agent layer, or multi-agent debate.
- No unbounded retries or hidden background correction after a user-visible terminal failure.
- No acceptance-scenario, manual-test-plan, user-guide, or operations-document changes before behavior signoff.

## 5. Current State

Correction loops are useful but local. The `propose_plan` path can repair malformed schema output in evidence collection. Mutation proposals can be corrected against malformed schema, missing expected text, and pre-mutation diagnostics. Plan sanity repair has separate flow-specific handling. Ordinary conversation tool calls have policy/tool-result guidance for some semantic misuse cases, and Plan 87 adds malformed tool-call recovery.

The fragmentation causes inconsistent behavior and duplicated mechanics. It also makes new structured model outputs risky because each feature must remember to add its own bounded retry, sanitization, logging, budget, and cancellation behavior.

## 6. Proposed Design

### 6.1 Correction substrate

Introduce a small internal correction-loop abstraction, not a public framework. It should coordinate:

1. current attempt count and configured/internal maximum;
2. whether a failure is recoverable in the current phase/workflow;
3. construction of sanitized correction evidence/instructions;
4. model continuation request creation or workflow-specific retry invocation;
5. budget, cancellation, and timeout handling;
6. result classification and terminal failure formatting.

The substrate does not parse plans, mutations, or tool arguments itself. It calls workflow-owned validators and receives typed correction failures.

### 6.2 Typed correction policies

Each participating workflow supplies a correction policy with:

- a stable correction category;
- maximum attempts or a reference to an existing configured bound;
- recoverability rules;
- safe diagnostic fields permitted in correction text;
- transcript/message insertion strategy;
- validation callback for the next model output;
- exhaustion behavior.

Initial policies:

- malformed ordinary tool invocation, building on Plan 87;
- `propose_plan` schema repair;
- plan sanity/revision repair, where the current workflow already retries;
- mutation proposal repair;
- optional structured-output provider failures where an existing safe retry path already exists.

### 6.3 Message and evidence strategies

The substrate supports multiple safe correction strategies:

- host/developer correction message when no valid tool-call correlation exists;
- assistant tool-call plus tool-result correction when a valid tool call exists but domain validation rejected it;
- workflow-specific repair evidence for plan or mutation proposals;
- direct retry with additional validation evidence for non-tool structured outputs.

The policy chooses the strategy. The substrate enforces bounds and redaction.

### 6.4 State and accounting

Track correction attempts in the active turn or workflow state, not in raw transcript text. Preserve existing durable checkpoint semantics for plan and mutation workflows. Correction attempts must count toward model request count, token/cost estimates, wall-clock budget, and cancellation. Exhaustion should produce one clear terminal reason with the last safe failure category.

### 6.5 Incremental migration

Do not replace all loops in one risky edit. Migrate in this order:

1. wrap Plan 87 malformed-tool correction with the shared substrate;
2. move `propose_plan` repair mechanics while preserving its exact correction schema guidance and bounded count;
3. adapt plan sanity/revision repair only where it shares the same model-call lifecycle;
4. move mutation proposal repair mechanics while preserving its diagnostic/pre-mutation evidence behavior;
5. remove duplicated counters/helpers only after behavior parity is verified.

## 7. Public Contracts

Expected contracts are internal host-owned types, likely in `Threadsmith.Execution` with shared model-facing DTOs remaining in `Threadsmith.Models` only where provider-neutral failures require them:

- `CorrectionCategory` or equivalent closed internal identifier;
- `CorrectionFailure` safe diagnostic record;
- `CorrectionPolicy`/`CorrectionRequest`/`CorrectionOutcome` internal records;
- shared exhaustion formatter and sanitized activity/event projection helpers;
- optional configuration binding for correction counts only if existing knobs cannot be reused.

No provider SDK, terminal, Roslyn, extension, MCP SDK, persistence connection, or live workflow object crosses subsystem boundaries.

## 8. Project/File Changes

Before functionality signoff, expected source changes are limited to:

- `src/Threadsmith.Execution/` — shared correction substrate and integration into conversation/planning/mutation flows;
- `src/Threadsmith.Models/` — only if Plan 87 failure metadata needs minor adjustment for shared use;
- `src/Threadsmith.Models.OpenAiCompatible/` and `src/Threadsmith.Models.OpenAiCodex/` — only if Plan 87 integration needs provider metadata naming updates;
- configuration/composition files only if correction counts become an exposed setting;
- telemetry/event projection files only if existing sanitized activity hooks are insufficient.

Do not change tests or product documentation during the functionality trial.

After signoff, likely tests and documentation are listed in Sections 10 and 16.

## 9. Ordered Tasks

### Functionality trial

1. Get user approval of this plan and the incremental migration order.
2. Inventory the existing correction loops and record their current attempt counts, correction messages, terminal failure classes, and durable state effects.
3. Define the smallest shared correction substrate that can express Plan 87 and `propose_plan` without weakening either.
4. Integrate Plan 87 malformed-tool correction into the substrate.
5. Migrate `propose_plan` repair mechanics and verify behavior parity manually.
6. Migrate plan sanity/revision repair only if the same substrate fits without broad redesign.
7. Migrate mutation proposal repair mechanics only after preserving its existing diagnostics and pre-mutation correction behavior.
8. Remove duplicated retry helpers/counters only where the migrated paths prove parity.
9. Build affected projects and run focused manual/scripted trials.
10. Stop and wait for explicit user signoff.

### After functionality signoff

11. Add or update the focused automated tests listed in Section 10.
12. Run focused suites and fix confirmed parity defects.
13. Update only the documentation listed in Section 16.
14. Run broader build/test/format/planning-governance checks.
15. Update this plan status only after signed-off behavior, deferred verification, and deferred documentation pass.

Do not commit the functionality trial as complete before the deferred tests and documentation are finished.

## 10. Testing

After signoff, add or update tests for these cases:

1. **Plan 87 parity** — malformed tool-call correction still succeeds, exhausts, cancels, and redacts as before.
2. **Propose-plan parity** — malformed plan JSON/schema receives the same bounded guidance and successful repair as before.
3. **Propose-plan exhaustion** — exhaustion count and terminal reason match the previous contract except for intentional wording improvements.
4. **Plan sanity/revision parity** — existing repair evidence, approval boundaries, and durable proposal state remain unchanged.
5. **Mutation proposal parity** — malformed schema, missing expected text, and pre-mutation diagnostic correction remain bounded and preserve durable checkpoint behavior.
6. **Budget accounting** — all correction attempts count against model/cost/wall-clock budgets.
7. **Cancellation** — cancellation stops any correction category without starting another model request.
8. **Transcript strategy safety** — provider tool-call correlation is used only when valid; otherwise host/developer correction is used.
9. **Redaction** — raw malformed payloads, secrets, hidden reasoning, provider bodies, and source bodies are not leaked.
10. **Unknown category safety** — unsupported correction categories fail closed rather than retrying indefinitely.
11. **No behavior expansion** — correction substrate does not change tool authorization, mutation approval, validation gates, or phase transitions.

## 11. Security/Permissions

The substrate centralizes retry mechanics, not authority. Every workflow validator remains authoritative. Correction messages are untrusted model input, not approval, not evidence of user intent, and not permission to execute. Tool, mutation, build, MCP, extension, web, process, path, secret, and validation policies remain enforced before any side effect.

Centralized redaction must be stricter than any prior local loop. If a workflow cannot provide safe diagnostic fields, its correction policy must emit generic correction text or mark the failure non-recoverable.

## 12. Observability

Emit sanitized correction telemetry consistently: category, attempt, maximum, failure kind, workflow phase, provider family when applicable, corrected/exhausted/cancelled outcome, budget exhaustion, and duration. Do not log raw malformed payloads, request/response bodies, hidden reasoning, credentials, source bodies, mutation contents beyond existing approved previews, or tool result bodies outside existing governed evidence.

## 13. Migration/Compatibility

The migration is internal. Existing durable sessions, events, checkpoints, plan proposals, mutation proposals, and tool results remain readable. Historical events from local correction loops remain valid. New events or status text, if any, must be additive and schema-version tolerant. If a migrated category fails to preserve behavior, revert that category to its local loop and keep the substrate for the categories that passed signoff.

## 14. Acceptance Criteria

- One shared correction substrate handles retry bounds, cancellation, budget accounting, sanitized observability, exhaustion, and message/evidence insertion for migrated categories.
- Plan 87 malformed-tool correction runs through the substrate without behavior regression.
- Existing `propose_plan`, plan repair, and mutation correction behavior is preserved or intentionally left on local loops until parity is proven.
- No malformed or invalid model output is executed, staged, approved, or treated as host authority.
- Correction attempts are consistent, bounded, recoverable where safe, and terminal only after exhaustion or non-recoverability.
- Focused parity tests, affected workflow tests, architecture checks, solution build, and planning-governance checks pass after signoff.

## 15. Risks

- **Over-generalization hides domain policy:** keep validation and correction text workflow-owned.
- **Large refactor destabilizes planning/mutation:** migrate incrementally and stop when parity becomes uncertain.
- **Central loop logs too much:** default to generic safe diagnostics unless a workflow explicitly supplies redacted fields.
- **Provider protocol mismatch:** policy chooses transcript strategy; substrate does not fake correlations.
- **Behavior changes masked as refactor:** require parity tests after signoff before marking complete.

## 16. Documentation

After signoff, update internal testing docs and operator troubleshooting only where behavior is user/operator visible. User-facing docs should describe the observable principle, not internal substrate details: malformed model output receives bounded correction when safe, and terminal failure occurs only after correction exhaustion or non-recoverability. Architecture or DOX updates are needed only if durable ownership or workflow guidance changes.

Do not update user/operator documentation, acceptance scenarios, or manual test procedures before behavior signoff.

## 17. Open Decisions

- Final substrate type names and owning project.
- Whether correction attempt counts remain category-specific or share one default with category overrides.
- Which plan sanity/revision paths should migrate in the first pass versus remain local.
- Whether mutation proposal correction should migrate fully or only share budget/exhaustion/redaction helpers.
- Exact event/status wording for correction attempts and exhaustion.
