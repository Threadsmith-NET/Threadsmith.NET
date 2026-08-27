# Implementation Plan 88.1: Complete Conversation-Native Correction Migration

**Status:** Completed — plan-sanity, mutation-proposal, post-apply validation, legacy-counter, and obsolete-helper migration implemented.
**Delivery track:** Maintenance — behavior-preserving migration from legacy correction loops to Plan 88 corrective turns
**Strategy source:** Shared Context §A.1, §A.2, §A.5, §C, and §G; Plan 88; Plan 88 blueprint
**Prerequisites:** Plan 88 conversation-loop/provider/tool-batch slice is implemented, including `execution:maxCorrectiveTurns`, `CorrectiveTurnState`, `CorrectiveMessageFactory`, provider-boundary `MalformedInvocationException` diagnostics, tool-call wire-name mapping, and whole-batch tool preflight.
**Parent work item:** [`plan-88-unified-model-correction-loop-substrate.md`](plan-88-unified-model-correction-loop-substrate.md)
**Implementation inventory:** [`plan88_plan.md`](plan88_plan.md)

## Completion Summary

- Plan-sanity repair now uses active-turn corrective messages and no longer appends hidden task constraints or emits new `PlanRevisionRequested` repair events.
- Mutation proposal repair now uses typed safe diagnostics, `ModelCorrectionAttempted`, and corrective `ModelMessage` values instead of `CorrectionEvidence`, substring repair classification, or `MutationProposalRepairAttempted` emissions.
- Post-apply validation correction now passes a bounded `MutationCorrectionContext` into mutation proposal generation while preserving checkpoint correction attempts, exact-diff approval, and rollback behavior.
- `execution:maxCorrectiveTurns` is the only production correction budget; legacy compatibility properties were removed.
- `CorrectionLoop` / `TestCorrectionLoop` and their DTO contracts were removed; `ValidationMetrics` owns the shared validation meter.
- `PlanRevisionRequested` and `MutationProposalRepairAttempted` remain historical durable replay event types.

## 1. Objective

Complete the deferred Plan 88 migration so every production model-output correction path uses bounded conversation-native corrective messages instead of hidden task-constraint injection, bespoke helper loops, or message-substring repair classification.

After this plan, recoverable plan-sanity, mutation-proposal, and post-apply validation failures should be corrected through the same model-visible turn substrate as malformed provider/tool/propose-plan output. No invalid request is executed, staged, approved, or silently repaired by the host.

## 2. Architectural Context

The signed-off Plan 88 slice already established the shared substrate for ordinary conversation turns:

- one configured correction budget, `execution:maxCorrectiveTurns`;
- safe malformed-invocation diagnostics in provider/model boundaries;
- `CorrectiveTurnState` attempt accounting;
- `CorrectiveMessageFactory` bounded model-visible correction text;
- provider all-or-none malformed tool-call validation;
- conversation tool-batch preflight and atomic rejection;
- purgeable active-turn correction groups after successful correction.

Three production areas still route corrections through legacy mechanisms:

- plan sanity repair appends revision instructions to `Task.UserConstraints` and re-enters plan generation;
- mutation proposal repair appends correction evidence to `Task.UserConstraints` and classifies repairability with exception-message substring checks;
- post-apply validation correction creates a correction-evidence string and passes it into mutation proposal generation.

Standalone validation helper loops remain in `Threadsmith.Validation`, but production orchestration uses `ExecutionOrchestrator`, not those helpers.

## 3. Scope

- Migrate repairable plan-sanity failures to active-turn corrective messages.
- Migrate mutation-proposal schema, bad `expectedText`, semantic rename, and pre-mutation diagnostic failures to corrective messages.
- Migrate post-apply validation correction requests to typed corrective messages while preserving checkpoint and approval semantics.
- Remove obsolete standalone validation correction helpers and tests after production paths cover the behavior.
- Remove legacy execution-limit compatibility properties and hidden correction-evidence command fields once no production code uses them.
- Replace new emissions of legacy repair-specific events with one sanitized correction-attempt event/projection path, while preserving historical event readability.
- Update focused tests and docs for the completed behavior.

## 4. Non-Scope

- No new user capability or milestone.
- No change to plan approval, mutation approval, path policy, tool policy, validation gates, or exact-diff authorization.
- No host-side repair of malformed model-authored arguments, plans, or mutations.
- No execution of a partially invalid batch.
- No redesign of provider streaming, tool preflight, active-turn compaction, persistence schema, or execution checkpointing beyond the minimal compatibility edits required by this migration.
- No deletion of historical durable event types that may be needed to replay existing sessions.
- No broad cleanup unrelated to correction migration.

## 5. Pre-Implementation State

| Area | Pre-implementation files/symbols | Pre-implementation behavior | Migration target |
|---|---|---|---|
| Plan sanity repair | `src/Threadsmith.Execution/SessionApplication.cs`: `RunPlanSanityAndPolicyAsync`, `CreatePlanRepairInstructions`, `AccrueRepairWallClock`; `PlanRevisionRequested` | Repairable blocking sanity issues append sanitized text to `registration.Task.UserConstraints`, store `registration.PendingPlan`, call `GeneratePlanAsync` again, and count with `MaxPlanRevisionRepairAttempts`. | Keep sanity and policy gates, but feed a bounded corrective message back to the model. Do not mutate task constraints. Stop emitting new `PlanRevisionRequested` events unless kept only for historical replay. |
| Mutation proposal repair | `src/Threadsmith.Execution/MutationProposalApplication.cs`: `HandleAsync`, `HandleCoreAsync`, `IsRepairableMutationProposalFailure`, `FormatMutationProposalCorrectionEvidence`, `AnalyzePreMutationAsync`, `ResolveModelReplaceTextRangesAsync`; `MutationProposalRepairAttempted` | An outer retry loop catches `MalformedModelOutputException`, decides repairability with message substrings, emits `MutationProposalRepairAttempted`, and appends `CorrectionEvidence` to `Task.UserConstraints`. | Use typed/safe diagnostics and corrective `ModelMessage` values. Remove substring repair classification and hidden task-constraint correction. |
| Post-apply validation correction | `src/Threadsmith.Execution/ExecutionOrchestrator.cs`: `ValidateAndCompleteAsync`, `CreateCorrectionEvidence`; `ExecutionContinuation.CorrectionAttempts/CorrectionBudget` | Validation failure builds a text evidence block and calls `ProposeMutationSetCommand(..., RunPhase.CorrectionModelTurn, correctionEvidence)`, then returns to mutation approval with checkpoint attempts incremented. | Preserve correction-cycle checkpointing and separate mutation approval, but pass a typed post-apply validation corrective message into mutation proposal generation rather than `CorrectionEvidence`. |
| Standalone validation loops | `src/Threadsmith.Validation/CorrectionLoop.cs`, `TestCorrectionLoop.cs`; `src/Threadsmith.Core/ValidationContracts.cs`: `CorrectionLoopResult`, `TestCorrectionLoopResult`, related contexts/results | Helper loops are tested but not used by production orchestration. | Remove if no durable/public compatibility requires them; otherwise leave only historical DTOs with no production loop. Rewrite/remove helper-loop tests around production correction paths. |
| Config/contracts | `src/Threadsmith.Execution/ExecutionLimits.cs`; `src/Threadsmith.App/HostFoundation.cs`; `src/Threadsmith.Core/MutationContracts.cs` | Legacy properties `MaxPlanProposalRepairAttempts`, `MaxPlanRevisionRepairAttempts`, `MaxMutationProposalRepairAttempts` are sourced from `MaxCorrectiveTurns`; `ProposeMutationSetCommand.CorrectionEvidence` carries hidden repair text. | Retain only `MaxCorrectiveTurns` as the configured production authority. Replace `CorrectionEvidence` with a host-owned corrective-message or correction-context shape if mutation proposal still needs command input. |
| Events/projections | `src/Threadsmith.Core/Events.cs`, `src/Threadsmith.Execution/InMemoryProjectionStore.cs`, `src/Threadsmith.Tui/TuiEventSegments.cs`, `docs/architecture/event-catalog.md` | Legacy plan/mutation repair events remain public durable event types and TUI knows `MutationProposalRepairAttempted`. | Keep historical replay support, but emit one sanitized generic correction-attempt event for new correction attempts if visibility is needed. Update TUI/projections to render the generic event without routing on presentation text. |
| Tests/docs | `tests/Threadsmith.Planning.Tests/Milestone4Tests.cs`, `tests/Threadsmith.Mutations.Tests/Milestone5Tests.cs`, `tests/Threadsmith.ExecutionOrchestration.Tests/ExecutionOrchestratorTests.cs`, `tests/Threadsmith.Validation.Tests/Milestone6Tests.cs`, architecture/config/event tests, `docs/architecture/validation-pipeline.md`, `docs/user-guide.md`, `.threadsmith/AGENTS.md`, `src/AGENTS.md` | Tests and docs still describe a mixed legacy bridge. | Update after behavior changes land; keep docs about implemented behavior only. |

## 6. Proposed Design

### 6.1 Shared corrective-message inputs

Use the existing `CorrectiveTurnState` and `CorrectiveMessageFactory` rather than adding a large framework. Extend them only with narrow helpers for the remaining categories:

- plan-sanity repair;
- mutation schema or required-shape mismatch;
- mutation target/`expectedText`/semantic rename mismatch;
- pre-mutation diagnostics;
- post-apply validation failure.

Each helper must produce bounded, sanitized `ModelMessage` content that states:

- nothing from the rejected request was accepted, staged, or executed;
- the safe failure reason;
- the attempt number and maximum attempts;
- the exact next action, such as re-emitting `propose_plan` or `propose_mutations` once.

Do not include raw malformed JSON, provider bodies, full diffs, full source bodies, hidden reasoning, secrets, or unbounded diagnostic output.

### 6.2 Plan-sanity repair

Move repairable plan-sanity correction to the same logical model turn as the rejected `propose_plan` output. The implementation may split or reshape `GeneratePlanAsync` / `RunPlanSanityAndPolicyAsync`, but must preserve these rules:

1. A candidate `ImplementationPlan` is parsed and validated before sanity checks.
2. Sanity checks still publish `PlanSanityCheckCompleted` for each candidate.
3. Non-repairable blocking issues fail closed without a corrective turn.
4. Repairable blocking issues consume one `CorrectiveTurnState` attempt sourced from `_limits.MaxCorrectiveTurns`.
5. The model receives a corrective message, not a new `Task.UserConstraints` entry.
6. The previous rejected plan is not published for approval and does not become accepted evidence.
7. After a corrected plan passes sanity, purge rejected corrective noise from future active-turn history while preserving the accepted plan and any executed tool evidence.
8. Plan approval policy runs only after sanity passes.

Prefer preserving provider tool-call ordering when the rejected proposal was a correlated `propose_plan` call: pair the assistant tool call with a tool-role corrective result when safe; use a developer corrective message only when there is no safe provider-correlated call to answer.

Remove `CreatePlanRepairInstructions` and `AccrueRepairWallClock` when they no longer have production callers. Wall-clock and model usage should accrue through the ordinary model request and execution budget paths.

### 6.3 Mutation-proposal repair

Replace the outer legacy retry loop in `MutationProposalApplication.HandleAsync` with a correction loop that builds model-visible messages for retries.

Implementation rules:

1. Use `_limits.MaxCorrectiveTurns` through `CorrectiveTurnState`; do not use `MaxMutationProposalRepairAttempts`.
2. Publish `MutationProposalStarted` per model proposal attempt if existing projections depend on it, but stop emitting `MutationProposalRepairAttempted` for new correction attempts after the generic event/projection exists.
3. Remove `IsRepairableMutationProposalFailure`; recoverability must come from typed/safe failure kinds or local typed exceptions, not exception-message substrings.
4. Remove `FormatMutationProposalCorrectionEvidence` and the `Task.UserConstraints` correction append.
5. Replace `ProposeMutationSetCommand.CorrectionEvidence` with a narrow host-owned corrective-message/correction-context input only if orchestration must seed the first correction message.
6. If the provider returns a safe correlated `propose_mutations` call, answer the rejected call with a tool-role corrective result; if the provider fails before producing a safe call, append a developer corrective message.
7. Schema/shape failures, bad `expectedText`, ambiguous replacement text, semantic rename failure, mutation outside approved plan, and repairable pre-mutation diagnostics can be corrective if the model can re-emit a valid proposal without host guessing.
8. Non-repairable host failures, budget exhaustion, cancellation, path-policy violations, identity tampering, baseline mismatch, and malformed provider corruption that lacks safe diagnostic metadata fail closed.
9. A successful corrected proposal is staged through the existing workspace path and still requires exact mutation approval.

Keep existing structured-output size bounds and model usage accounting. Do not let correction messages widen approved plan scope or mutation authority.

### 6.4 Post-apply validation correction

Preserve the existing approved-plan execution lifecycle:

1. Apply only approved mutations.
2. Run validation.
3. If validation passes, complete.
4. If validation fails and the post-apply correction-cycle budget remains, ask the model for a correction mutation.
5. Stage the correction and return to mutation approval.
6. If validation still fails after budget exhaustion, fail closed with rollback availability as today.

Change only the model-visible correction channel. Replace `CreateCorrectionEvidence` string injection with a bounded post-apply validation corrective message or typed correction context passed to `MutationProposalApplication`.

`ExecutionContinuation.CorrectionAttempts` and `CorrectionBudget` may remain as checkpoint-facing state for resume compatibility, but `CorrectionBudget` must continue to be populated from `ExecutionLimits.MaxCorrectiveTurns`. Do not introduce a new validation-correction configuration key.

The post-apply corrective message should summarize only bounded safe validation evidence: gate status, up to a small number of sanitized gate reasons, introduced diagnostic identities/messages, failing test identifiers, and validation phase names. It must not include full build logs, full test logs, full diffs, raw process output, secrets, or unbounded source excerpts.

### 6.5 Obsolete helper cleanup

After plan, mutation, and post-apply production correction paths are covered:

- remove `CorrectionLoop.cs` and `TestCorrectionLoop.cs` if no production or durable compatibility caller remains;
- remove the corresponding helper DTOs from `ValidationContracts.cs` when safe;
- update `src/Threadsmith.Validation/AGENTS.md` ownership text if helper files are deleted;
- remove or rewrite `Milestone6Tests` helper-loop cases to assert production orchestration behavior instead;
- keep historical durable event types and JSON derived-type registrations where existing persisted sessions need replay.

### 6.6 Generic correction-attempt visibility

Introduce one generic, sanitized visibility path if implementation still needs user/TUI/headless observability for correction attempts after legacy event emissions stop.

Preferred durable event shape:

```csharp
public sealed record ModelCorrectionAttempted(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    ModelCorrectionCategory Category,
    int AttemptNumber,
    int MaximumAttempts,
    string SafeReason) : IDomainEvent;
```

`ModelCorrectionCategory` should be a closed enum with values such as `ProviderInvocation`, `ToolBatch`, `PlanSchema`, `PlanSanity`, `MutationProposal`, `PreMutationAnalysis`, and `PostApplyValidation`.

If a narrower existing projection can satisfy visibility without a new durable event, document that decision in this plan during implementation. Do not route behavior based on TUI presentation text.

## 7. Public Contracts

Expected removals or replacements after migration:

- remove production use of `ExecutionLimits.MaxPlanProposalRepairAttempts`, `MaxPlanRevisionRepairAttempts`, and `MaxMutationProposalRepairAttempts`;
- remove `HostFoundation` compatibility assignments for those properties;
- remove or replace `ProposeMutationSetCommand.CorrectionEvidence`;
- stop emitting new `PlanRevisionRequested` and `MutationProposalRepairAttempted` events from production repair paths, while retaining types for historical replay if needed;
- remove standalone validation correction helper contracts if no public/durable compatibility requires them.

Expected retained contracts:

- `execution:maxCorrectiveTurns` remains the only documented correction-budget configuration key;
- `MalformedInvocationException` and `MalformedInvocationDiagnostic` remain safe provider/model diagnostics;
- `ExecutionContinuation.CorrectionAttempts` / `CorrectionBudget` remain checkpoint-compatible unless a separate checkpoint simplification is explicitly planned;
- accepted plans, staged mutations, approvals, validation results, and execution outcomes retain their existing authority and schemas.

## 8. Project/File Changes

Likely implementation files:

- `src/Threadsmith.Execution/SessionApplication.cs`
- `src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs`
- `src/Threadsmith.Execution/CorrectiveMessageFactory.cs`
- `src/Threadsmith.Execution/CorrectiveTurnState.cs`
- `src/Threadsmith.Execution/MutationProposalApplication.cs`
- `src/Threadsmith.Execution/ExecutionOrchestrator.cs`
- `src/Threadsmith.Execution/ExecutionLimits.cs`
- `src/Threadsmith.Core/MutationContracts.cs`
- `src/Threadsmith.Core/ExecutionOrchestrationContracts.cs`
- `src/Threadsmith.Core/Events.cs`
- `src/Threadsmith.Core/ValidationContracts.cs`
- `src/Threadsmith.Validation/CorrectionLoop.cs`
- `src/Threadsmith.Validation/TestCorrectionLoop.cs`
- `src/Threadsmith.Validation/AGENTS.md` if helper ownership text changes
- `src/Threadsmith.App/HostFoundation.cs`
- `src/Threadsmith.Execution/InMemoryProjectionStore.cs`
- `src/Threadsmith.Tui/TuiEventSegments.cs`
- `docs/architecture/event-catalog.md`
- `docs/architecture/validation-pipeline.md`
- `docs/user-guide.md`
- `.threadsmith/AGENTS.md`
- `src/AGENTS.md`

Likely test files:

- `tests/Threadsmith.Planning.Tests/Milestone4Tests.cs`
- `tests/Threadsmith.Mutations.Tests/Milestone5Tests.cs`
- `tests/Threadsmith.ExecutionOrchestration.Tests/ExecutionOrchestratorTests.cs`
- `tests/Threadsmith.Validation.Tests/Milestone6Tests.cs`
- `tests/Threadsmith.CoreRuntime.Tests/Milestone1Tests.cs`
- `tests/Threadsmith.Architecture.Tests/RepoConfigTests.cs`
- event/projection/TUI tests affected by generic correction visibility

## 9. Ordered Tasks

1. Re-read root, `src`, `.threadsmith`, validation, docs, and affected test DOX files plus the portable C# guardrails before code edits.
2. Build a short implementation inventory of every production and test reference to legacy repair counters, `CorrectionEvidence`, `PlanRevisionRequested`, `MutationProposalRepairAttempted`, `CorrectionLoop`, and `TestCorrectionLoop`.
3. Add or extend focused tests for the generic correction-attempt visibility contract, if a new event/projection is selected.
4. Migrate plan-sanity repair:
   - keep sanity checks before approval;
   - feed repairable failures as corrective messages;
   - prove no `Task.UserConstraints` mutation;
   - prove non-repairable and exhausted cases fail closed.
5. Migrate mutation-proposal repair:
   - replace substring repairability with typed/safe diagnostics;
   - append corrective messages instead of correction evidence;
   - cover schema, `expectedText`, semantic rename, mutation outside plan, and pre-mutation diagnostic repairs;
   - prove exact mutation approval remains required.
6. Migrate post-apply validation correction:
   - replace `CreateCorrectionEvidence` injection with bounded validation corrective messages;
   - preserve checkpoint correction attempts, resume, staged correction diff publication, and return to mutation approval.
7. Remove obsolete helper loops and contracts that are no longer referenced; retain historical event DTOs only where replay compatibility requires them.
8. Remove legacy execution-limit compatibility properties and host assignments after all callers/tests are migrated.
9. Update TUI/projections/event catalog for generic correction visibility or document why no generic event was needed.
10. Update user guide, validation-pipeline architecture doc, `.threadsmith/AGENTS.md`, `src/AGENTS.md`, and affected child DOX only for durable implemented behavior changes.
11. Run focused planning, mutation, execution-orchestration, validation, core-runtime, architecture/config/event, and TUI/projection tests.
12. Run the solution build, planning-governance searches, and `git diff --check`.
13. Close out this implementation document with completion status, final current state, tests run, and any retained compatibility debt.

## 10. Testing

Add or update automated tests for:

- repairable plan-sanity failure produces a corrective model message and a corrected plan can pass sanity and reach normal plan approval;
- plan-sanity correction exhaustion uses `execution:maxCorrectiveTurns` and fails closed without approval;
- non-repairable plan-sanity failures do not request correction;
- plan-sanity correction does not append to `Task.UserConstraints` and does not emit new legacy repair events after migration;
- mutation proposal schema mismatch is corrected through model messages and does not rely on exception-message substrings;
- bad `expectedText` / ambiguous replacement text correction reasks with bounded safe evidence and stages only the corrected mutation set;
- semantic rename and pre-mutation diagnostic repair use typed corrective messages;
- mutation proposal correction exhaustion fails closed without staging invalid mutations;
- post-apply validation failure seeds a bounded validation corrective message, stages a correction mutation, writes compatible checkpoints, and returns to mutation approval;
- post-apply validation exhaustion preserves final failure behavior and rollback availability;
- obsolete `CorrectionLoop` / `TestCorrectionLoop` tests are removed or replaced by production-path tests;
- legacy events remain deserializable if retained for historical replay;
- generic correction event/projection, if added, contains only category, attempt counts, and sanitized bounded reason;
- no raw malformed arguments, full diffs, full source, build logs, test logs, secrets, provider bodies, or hidden reasoning appear in correction messages/events/logs/snapshots;
- `execution:maxCorrectiveTurns` is the only documented/configured correction budget.

Relevant focused commands should include, adjusted to the final touched projects:

```powershell
dotnet test src\Threadsmith.sln --no-restore --filter "FullyQualifiedName~Threadsmith.Planning.Tests|FullyQualifiedName~Threadsmith.Mutations.Tests|FullyQualifiedName~Threadsmith.ExecutionOrchestration.Tests|FullyQualifiedName~Threadsmith.Validation.Tests|FullyQualifiedName~Threadsmith.CoreRuntime.Tests|FullyQualifiedName~Threadsmith.Architecture.Tests"
dotnet build src\Threadsmith.sln --no-restore
git diff --check
```

## 11. Security/Permissions

Corrective messages are retry guidance only. They do not grant tool availability, plan approval, mutation approval, path access, validation waiver, or execution authority.

All correction text must be sanitized, single-line normalized where appropriate, and bounded. It must omit raw malformed arguments, full model payloads, raw exception text, full diffs, full source bodies, build/test logs, secrets, credentials, provider bodies, and hidden reasoning.

Repository configuration cannot increase correction authority or select a separate legacy budget. The only documented budget key remains `execution:maxCorrectiveTurns`, and accepted requests still pass ordinary host validation.

## 12. Observability

Correction attempts should be observable through sanitized metadata:

- correction category;
- attempt number;
- maximum attempts;
- safe bounded reason;
- terminal outcome where already represented by existing operation events.

Do not log raw exception objects or unbounded exception messages for correction attempts. If `ModelCorrectionAttempted` is added, update event serialization, projections, TUI formatting, and `docs/architecture/event-catalog.md` together.

## 13. Migration/Compatibility

This is an internal behavior migration with user-visible safety behavior unchanged except that remaining recovery prompts are conversation-native.

Persisted sessions containing historical `PlanRevisionRequested` or `MutationProposalRepairAttempted` events must remain readable. Retain historical event registrations unless a reviewed persistence migration proves they are unnecessary.

Execution checkpoints may retain `CorrectionAttempts` and `CorrectionBudget` until a separate checkpoint simplification removes them. The values must continue to round-trip across resume.

If public helper DTOs from `ValidationContracts.cs` cannot be removed safely, mark them as historical compatibility contracts and remove production helper execution paths.

## 14. Acceptance Criteria

- Repairable plan-sanity failures receive bounded corrective messages and no longer mutate task constraints.
- Mutation proposal repair uses typed/safe diagnostics and corrective messages, not substring repair classification or hidden `CorrectionEvidence`.
- Post-apply validation correction uses bounded validation corrective messages while preserving checkpoint, approval, and rollback behavior.
- `execution:maxCorrectiveTurns` is the single configured correction budget for migrated paths.
- Obsolete standalone validation correction helpers are removed or explicitly retained only as historical compatibility DTOs with no production loop.
- New production correction visibility uses one sanitized generic path or is documented as intentionally unnecessary.
- Legacy durable event types remain replay-compatible, but new production correction does not depend on legacy repair-specific events.
- Focused tests, architecture/config/event tests, solution build, planning-governance checks, and `git diff --check` pass.
- Documentation reflects implemented behavior and no longer describes the legacy bridge after implementation completes.

## 15. Risks

- Moving plan sanity into the active conversation loop could accidentally change when plan approval policy runs.
- Tool-call/provider ordering for rejected `propose_plan` or `propose_mutations` calls can break provider continuation protocols if corrective messages are not correlated correctly.
- Removing substring repair classification requires enough typed diagnostics to avoid losing repairable mutation cases.
- Post-apply validation correction has checkpoint/resume semantics; changing the input channel must not change durable execution recovery.
- Generic correction observability can leak sensitive evidence if safe-reason bounds and sanitization are not enforced.
- Deleting helper contracts may break tests or public API checks if they are treated as durable surface.

## 16. Documentation

After implementation, update only documents that own durable implemented behavior:

- `docs/architecture/validation-pipeline.md` — remove the legacy plan-revision bridge and describe conversation-native plan/mutation/validation correction.
- `docs/architecture/event-catalog.md` — add the generic correction event if created and mark legacy repair events as historical replay types if they stop emitting.
- `docs/user-guide.md` — document the single correction budget and fail-closed behavior without implementation history.
- `.threadsmith/AGENTS.md` — remove the note that legacy plan/mutation/validation budgets are still sourced from `execution:maxCorrectiveTurns` after migration.
- `src/AGENTS.md` and affected child `AGENTS.md` files — update only durable workflow/ownership rules that changed.
- This file — record completion and any retained compatibility debt.

Do not edit completed milestone details merely to record this remediation.

## 17. Resolved Decisions

- Added `ModelCorrectionAttempted` with a closed `ModelCorrectionCategory` and wired durable serialization, projection activity, TUI rendering, event docs, and focused tests.
- Removed obsolete validation correction-loop DTOs from `ValidationContracts.cs` because no production or durable replay caller required them.
- Replaced `ProposeMutationSetCommand.CorrectionEvidence` with a narrow host-owned `MutationCorrectionContext`; `MutationProposalApplication` renders the corresponding corrective `ModelMessage` internally.
