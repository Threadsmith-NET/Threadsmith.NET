# Implementation Plan 88: Conversation-Native Corrective Turns

**Status:** Complete — conversation, plan-sanity, mutation-proposal, and post-apply validation corrections use bounded model-visible corrective messages; obsolete retry helpers are removed from production use, their public shapes remain as compatibility-only APIs, and legacy limits are removed.

**Delivery track:** Maintenance — graceful recovery from malformed or invalid model requests
**Strategy source:** Shared Context §A.1, §A.2, §A.5, §C, and §G; execution, model, planning, mutation, and validation contracts
**Prerequisites:** Existing plan, mutation, tool, and execution-correction behavior must be inventoried before removal or migration
**Implementation blueprint:** [`plan88_plan.md`](plan88_plan.md)

## 1. Objective

Make recoverable malformed or invalid model requests part of the normal active conversation. When the model emits a malformed tool request, invalid tool batch, malformed structured plan, malformed mutation proposal, or repairable validation failure, the host rejects the request, appends a bounded corrective message, and asks the model to try again in the same logical turn.

No malformed or invalid request is executed, staged, approved, or silently repaired by the host.

## 2. Architectural Context

Threadsmith already has several local repair mechanisms:

- `propose_plan` schema repair inside the conversation loop;
- plan sanity repair before approval;
- mutation proposal repair for schema, expected-text, and pre-mutation diagnostic failures;
- approved-plan validation correction after post-apply build/test failure;
- ordinary tool-result guidance for duplicate or phase-limited tools;
- provider-boundary failures for malformed native tool-call arguments.

Plan 88 folds these into one simpler conversation-based corrective-turn pattern. The detailed file/symbol inventory and implementation outline live in `plan88_plan.md`.

## 3. Scope

- Add one configurable corrective-turn limit: `execution:maxCorrectiveTurns`.
- Convert recoverable malformed/invalid model requests into active-turn corrective messages.
- Reject an entire batched tool request when any sibling is malformed or invalid before execution.
- Preserve useful successful tool evidence and accepted plans/mutations.
- Purge corrective noise from future model history after successful correction.
- Remove existing bespoke correction loops after their behavior is folded into the shared corrective-turn path.
- Preserve existing approval and validation gates around accepted plans, mutations, and tool execution.

## 4. Non-Scope

- No separate hidden correction-loop substrate.
- No host-side repair of malformed model-authored arguments into executable requests.
- No execution of partially malformed batches.
- No weakening of plan approval, mutation approval, tool policy, path checks, or validation.
- No user/operator documentation updates before behavior signoff.

## 5. Current State

The current repair behavior is fragmented and uses separate counters, hidden task-constraint injection, and message-substring classification in places. Provider-boundary malformed native tool-call arguments can fail before the conversation loop can ask for correction. Tool batches are not preflighted as one atomic unit before execution.

## 6. Proposed Design

Use the active conversation as the correction channel:

1. assemble a model request with active-turn history;
2. stream one model response;
3. classify malformed/invalid request output before execution;
4. if recoverable and the configured corrective-turn budget remains, append a corrective message and continue;
5. if accepted, execute or stage through the ordinary path;
6. after success, purge corrective/rejected-request noise from future model history;
7. if exhausted, fail closed with a sanitized reason.

The detailed processing-loop diagram, file-level changes, provider diagnostics, tool-batch preflight, and bespoke-loop removal steps are specified in `plan88_plan.md`.

## 7. Public Contracts

Expected additive or changed contracts:

- `ExecutionLimits.MaxCorrectiveTurns` and config key `execution:maxCorrectiveTurns`;
- safe malformed-invocation diagnostics in `Threadsmith.Models`;
- a no-side-effect tool-batch preflight result in `Threadsmith.Tools`;
- a generic correction-attempt event/projection if needed for TUI/headless visibility.

Existing durable correction/checkpoint fields remain compatible until a separate checkpoint simplification is warranted.

## 8. Project/File Changes

See `plan88_plan.md` for the authoritative implementation file list. Primary areas are:

- `src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs`
- `src/Threadsmith.Execution/SessionApplication.cs`
- `src/Threadsmith.Execution/MutationProposalApplication.cs`
- `src/Threadsmith.Execution/ExecutionOrchestrator.cs`
- `src/Threadsmith.Models/`
- `src/Threadsmith.Models.OpenAiCompatible/`
- `src/Threadsmith.Models.OpenAiCodex/`
- `src/Threadsmith.Tools/`
- `src/Threadsmith.App/`
- affected tests and projections after behavior signoff

## 9. Ordered Tasks

1. Implement the configurable corrective-turn limit.
2. Add safe malformed-invocation diagnostics.
3. Add provider-boundary malformed tool-call correction.
4. Add whole-batch tool preflight and atomic rejection.
5. Migrate ordinary tool and `propose_plan` correction in the conversation loop.
6. Stop for behavior signoff.
7. Add focused tests. **Done for the signed-off conversation-loop/provider/tool-batch slice.**
8. Migrate plan sanity, mutation proposal, and post-apply validation correction. **Done.**
9. Remove obsolete bespoke helper loops and counters. **Done; historical durable event types remain readable but are not emitted by the migrated paths.**
10. Update user/operator docs only after behavior signoff. **Done for the signed-off slice.**

## 10. Testing

Focused tests cover provider-boundary malformed arguments, provider-safe tool-name aliasing, batch preflight/prepared invocation, conversation-level invalid-batch rejection, `propose_plan` correction, null plan schema diagnostics, plan-sanity continuation, typed mutation correction, post-apply validation evidence, and config binding. Obsolete standalone validation-loop tests were removed; the unused public helpers and DTO shapes remain compatibility-only while production correction is execution-owned.

## 11. Security/Permissions

Corrective messages are instructions to retry; they are not approval. Accepted requests still pass the ordinary tool, plan, mutation, path, and validation gates. Raw malformed arguments and provider bodies must not be included in correction text, ordinary events, logs, or snapshots.

## 12. Observability

Record sanitized corrective-turn attempts with category, attempt number, maximum attempts, safe reason, and outcome. Do not log raw malformed arguments.

## 13. Migration/Compatibility

The migration is internal. Existing durable sessions and events remain readable. Obsolete local repair events or helper DTOs may remain as historical types if persistence compatibility requires them, but new production code should use the conversation-native path.

## 14. Acceptance Criteria

- Recoverable malformed or invalid model requests receive bounded active-turn corrective feedback.
- One invalid sibling rejects an entire tool batch before any sibling executes.
- Corrective-turn count is configurable through `execution:maxCorrectiveTurns`.
- Existing bespoke correction loops are removed or folded into the shared process.
- Corrective noise is not retained in future model history after success.
- Approval, mutation, tool, path, and validation gates remain unchanged for accepted requests.
- Focused tests and relevant build/architecture checks pass after implementation.

## 15. Risks

- Provider-boundary diagnostics may accidentally leak raw malformed arguments.
- Batch preflight could diverge from invocation validation if it does not reuse the same deserialization path.
- Purging corrective messages too aggressively could remove useful executed evidence.
- Migrating plan/mutation loops too early could change approval or checkpoint behavior.

## 16. Documentation

No user/operator documentation updates before behavior signoff. After signoff, document only observable behavior: Threadsmith can ask the model to correct malformed or invalid requests a bounded number of times before failing closed.

## 17. Open Decisions

- Whether obsolete config keys should be read as one-release compatibility aliases for `execution:maxCorrectiveTurns`.
- Exact generic event name for corrective-turn projection.
