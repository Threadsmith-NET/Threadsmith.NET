# Plan 88 Implementation Blueprint: Conversation-Native Corrective Turns

**Status:** Active implementation record — conversation-loop/provider/tool-batch corrective turns are implemented through behavior signoff; plan-sanity, mutation-proposal, post-apply validation, and obsolete helper-loop migrations remain deferred.

**Delivery track:** Maintenance — detailed implementation guide for Plan 88
**Prerequisites:** Parent Plan 88 accepted; existing correction paths inventoried; behavior signoff before deferred tests and user/operator docs
**Parent work item:** Plan 88 — conversation-native corrective messages
**Purpose:** Concrete code-change guide for replacing bespoke model-output repair loops with one bounded corrective-turn pattern.
**Default corrective-turn budget:** 3 attempts, configurable by `execution:maxCorrectiveTurns`.

## 1. Goal

Threadsmith should gracefully handle malformed or invalid model requests by continuing the same conversation with a clear corrective message. The corrected request is still validated normally before anything executes.

The implementation should replace scattered local repair loops with one simpler pattern:

1. read one model response;
2. classify malformed/invalid request output before executing tools or staging mutations;
3. if recoverable and budget remains, append a corrective active-turn message;
4. ask the model again in the same logical turn;
5. on success, keep the successful evidence and drop corrective noise from future model history;
6. on exhaustion, fail closed with a sanitized reason.

For batched tool requests, the batch is atomic at the correction boundary: if any requested call is malformed or invalid before execution, **reject the entire batch**, execute none of its calls, and tell the model exactly which call/argument failed.

## 2. Current code inventory

| Area | Current files/symbols | Current behavior | Change target |
|---|---|---|---|
| Main planning/tool loop | `src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs`: `GeneratePlanAsync`, `ExecuteConversationRoundAsync`, `ProcessModelChunkAsync`, `ProcessToolRequestAsync`, `EnqueueOrAnswerToolRequestAsync`, `InvokePendingToolBatchAsync`, `ConversationLoopState` | Plan generation and ordinary conversation tools share one loop. `propose_plan` has a bespoke schema repair branch. Non-plan malformed tool requests usually fail the run. Active-turn history stores completed tool-call/result groups. | Make this the first implementation target for provider-boundary malformed calls, invalid ordinary tool calls, batched rejection, and `propose_plan` repair migration. |
| Plan sanity repair | `src/Threadsmith.Execution/SessionApplication.cs`: `RunPlanSanityAndPolicyAsync`, `CreatePlanRepairInstructions`, `AccrueRepairWallClock`; `HandleAsync(RevisePlanCommand)` | Repairable sanity failures append text to `registration.Task.UserConstraints` and call `GeneratePlanAsync` again. Count is `MaxPlanRevisionRepairAttempts`. | Replace the loop with the corrective-turn path after ordinary tool/propose-plan correction is stable. Do not append correction text as task constraints. |
| Mutation proposal repair | `src/Threadsmith.Execution/MutationProposalApplication.cs`: `HandleAsync`, `HandleCoreAsync`, `IsRepairableMutationProposalFailure`, `FormatMutationProposalCorrectionEvidence`, `AnalyzePreMutationAsync`, `FormatPreMutationCorrection`, `ResolveModelReplaceTextRangesAsync` | Outer `for` loop retries `HandleCoreAsync`. Repairability is determined by exception-message substring matching. Correction evidence is appended to `Task.UserConstraints`. Count is `MaxMutationProposalRepairAttempts`. | Replace with corrective messages appended to the model request. Remove substring repair classification after diagnostics carry safe failure kinds. |
| Approved-plan validation correction | `src/Threadsmith.Execution/ExecutionOrchestrator.cs`: `ValidateAndCompleteAsync`, `CreateCorrectionEvidence`; `ExecutionContinuation.CorrectionAttempts/CorrectionBudget` | After validation failure, orchestrator asks `MutationProposalApplication` for a correction mutation and returns to mutation approval. This is production correction behavior, not the unused `Validation.CorrectionLoop`. | Keep checkpoint semantics, but route the correction request through the same corrective-message machinery used by mutation proposal generation. Use the unified configurable turn budget. |
| Provider-boundary tool assembly | `src/Threadsmith.Models.OpenAiCompatible/OpenAiCompatibleModelProvider.cs`: `DrainToolCalls`, `NormalizeToolArguments`, `ToolCallAccumulator`; `src/Threadsmith.Models.OpenAiCodex/OpenAiCodexModelProvider.cs`: `ReadEventsAsync` | Compatible provider validates in the provider stream and throws `MalformedModelOutputException`; it also silently normalizes malformed/empty no-arg calls to `{}`. Codex yields function calls with no local validation. The conversation loop cannot correct provider-boundary malformed arguments. | Surface safe malformed-invocation diagnostics instead of only throwing plain messages. Remove silent argument repair. Let execution turn the diagnostic into a developer corrective message. |
| Model output validation | `src/Threadsmith.Models/ModelOutputValidator.cs`; `src/Threadsmith.Models/ModelContracts.cs` | Throws `MalformedModelOutputException` with only text. This drives substring matching in mutation repair. | Add typed, safe malformed-invocation diagnostics/failure kinds while preserving existing exception compatibility. |
| Tool batch execution | `src/Threadsmith.Tools/ToolInvocationPipeline.cs`, `ToolBatchScheduler.cs`, `ToolContracts.cs` | `InvokeBatchAsync` may publish `ToolInvocationStarted` and return per-tool `InvalidArguments` results. A partially invalid batch can still execute valid sibling calls depending on wave ordering. | Add a no-side-effect batch preflight used by the conversation loop. If preflight finds one invalid request, no tool starts and the entire batch gets corrective results. |
| Unused validation correction loops | `src/Threadsmith.Validation/CorrectionLoop.cs`, `TestCorrectionLoop.cs`; `src/Threadsmith.Core/ValidationContracts.cs`: `CorrectionContext`, `CorrectionAttemptResult`, `CorrectionLoopResult`, `TestCorrectionContext`, `TestCorrectionAttemptResult`, `TestCorrectionLoopResult` | Standalone bounded correction helpers are covered by tests but are not used by production orchestration. | Remove after production correction is folded into conversation turns and tests are rewritten around the production path. |
| Config | `src/Threadsmith.Execution/ExecutionLimits.cs`; `src/Threadsmith.App/HostFoundation.cs`; `src/Threadsmith.App/ConfigurationBootstrap.cs`; `src/Threadsmith.App/ApplicationComposition.cs` | Separate knobs existed for plan proposal, plan revision, mutation proposal, and validation correction. | `execution:maxCorrectiveTurns` is now the one documented key; legacy execution-limit properties are sourced from it until deferred loops migrate. |
| Tests/projections | `tests/Threadsmith.Planning.Tests/Milestone4Tests.cs`, `tests/Threadsmith.Mutations.Tests/Milestone5Tests.cs`, `tests/Threadsmith.ExecutionOrchestration.Tests/ExecutionOrchestratorTests.cs`, `tests/Threadsmith.Validation.Tests/Milestone6Tests.cs`, `tests/Threadsmith.Architecture.Tests/RepoConfigTests.cs`, `src/Threadsmith.Tui/TuiPresentationFormatter.cs`, projections that render `MutationProposalRepairAttempted` | Existing tests assert bespoke repair messages/events and config keys. | Update after behavior signoff. Keep tests focused on visible behavior and batch atomicity rather than old helper classes. |

## 3. Target behavior rules

### 3.1 Recoverable malformed/invalid outputs

A model output is recoverable when the model can reasonably re-emit the request correctly without the host guessing data:

- tool arguments are not JSON;
- tool arguments are JSON but not an object;
- tool name is missing, empty, unknown, unavailable in the current phase, or outside the advertised inventory;
- tool arguments do not deserialize against the registered tool input type;
- one tool call in a sibling batch is malformed or invalid before execution;
- `propose_plan` or `propose_mutations` arguments do not match the required schema;
- plan sanity finds repairable issues before plan approval;
- pre-mutation analysis finds repairable diagnostics before staging;
- post-apply validation fails and another corrective mutation attempt remains.

Unknown provider corruption, transport failure, timeout, non-tool malformed stream frames, cancellation, and budget exhaustion are not corrective turns.

### 3.2 No host-side argument repair

Do not transform malformed model arguments into executable requests. In particular, remove `NormalizeToolArguments` behavior that turns malformed or empty no-argument calls into `{}`. If a tool requires `{}` and the model omitted it, request correction.

### 3.3 Batch atomicity

For one model response containing multiple tool calls:

- validate the whole batch before invoking any tool;
- if any call is malformed or invalid, execute none of the calls;
- append corrective feedback for the whole batch;
- identify the failing ordinal/tool when safe;
- ask the model to re-emit the full intended batch or answer without tools;
- do not keep valid siblings as evidence because they did not execute.

When a correlated batch is rejected after valid tool-call messages exist, each assistant tool call must receive a tool-role response so provider protocol ordering remains valid. The failing call receives the specific correction; non-failing siblings receive a short “not executed because the batch was rejected” result.

Provider-boundary malformed batches that cannot safely produce valid assistant tool-call messages use one developer corrective message instead of fake tool results.

### 3.4 Corrective message text

Corrective messages should be short and actionable:

- state that nothing from the invalid request or batch was executed;
- state the specific issue;
- state the expected shape or next action;
- state the attempt count, e.g. `Corrective turn 1 of 3`;
- avoid raw malformed arguments, secrets, provider bodies, hidden reasoning, source bodies, or large schemas by default.

### 3.5 Future-history purge

After a corrective turn succeeds:

- remove purgeable corrective messages and rejected invalid batch messages from active-turn continuation history;
- keep valid tool calls/results that actually executed;
- keep the accepted `propose_plan`/`propose_mutations` result or staged mutation evidence;
- keep sanitized events/telemetry for attempt count and outcome.

If corrective turns exhaust, keep enough sanitized state to explain the failure. Do not archive corrective prompts as visible conversation messages.

## 4. Minimal implementation model

Avoid a large correction framework. Add a small set of internal helpers that the existing loops use.

### 4.1 Execution limits

`ExecutionLimits` exposes one documented correction budget:

```csharp
public int MaxCorrectiveTurns { get; init; } = 3;
```

Composition binds `execution:maxCorrectiveTurns` once in `HostFoundation.CreateAsync`. `ApplicationComposition` passes `host.ExecutionLimits.MaxCorrectiveTurns` into `ExecutionStartRequest.CorrectionBudget` until that public request shape can be simplified. Legacy plan-revision, mutation-proposal, and validation-correction counters remain as compatibility properties only and are sourced from the same configured value until their loops migrate.

### 4.2 Safe diagnostic shape

Add provider/model validation diagnostics in `src/Threadsmith.Models`:

```csharp
public enum MalformedInvocationFailureKind
{
    InvalidJsonArguments,
    NonObjectArguments,
    MissingToolName,
    UnknownTool,
    UnavailableTool,
    ArgumentSchemaMismatch,
    PhaseInvalidTool,
    MultipleToolProducingOutputs,
    PlanSchemaMismatch,
    MutationSchemaMismatch,
    PlanSanityRepair,
    PreMutationDiagnostics,
    PostApplyValidation,
}

public sealed record MalformedInvocationDiagnostic
{
    public required MalformedInvocationFailureKind Kind { get; init; }
    public required string SafeMessage { get; init; }
    public string? ToolName { get; init; }
    public int? ToolOrdinal { get; init; }
    public int? ToolCallCount { get; init; }
    public string? ProviderFamily { get; init; }
    public int? ArgumentCharacterCount { get; init; }
    public string? ArgumentSha256 { get; init; }
    public string? JsonPath { get; init; }
    public long? JsonLineNumber { get; init; }
    public long? JsonBytePositionInLine { get; init; }
}

public sealed class MalformedInvocationException : MalformedModelOutputException
{
    public required MalformedInvocationDiagnostic Diagnostic { get; init; }
}
```

Keep `MalformedModelOutputException` for compatibility. New code should throw/catch `MalformedInvocationException` when it has actionable diagnostic metadata; old call sites can still throw the base exception and be treated as permanent or mapped locally.

### 4.3 Corrective turn state

Add a small internal helper in `src/Threadsmith.Execution`, likely `CorrectiveTurnState.cs`:

```csharp
internal sealed class CorrectiveTurnState
{
    public CorrectiveTurnState(int maximumTurns);
    public int MaximumTurns { get; }
    public int AttemptsUsed { get; }
    public bool TryBeginAttempt(out int attemptNumber);
}
```

It only counts attempts. Do not infer counts from transcript text.

### 4.4 Corrective message factory

Add a small internal helper, likely `CorrectiveMessageFactory.cs`, that creates `ModelMessage` values:

- developer message for provider-boundary failures;
- tool-role result messages for correlated batch rejection;
- plan/mutation schema corrective messages;
- plan sanity/pre-mutation/post-apply corrective messages.

The helper should sanitize supplied messages and bound all text. It should not know workflow state machines.

Suggested section ids:

- `active-turn-correction:{attempt}` for developer messages;
- `active-turn-correction-tool:{toolCallId}` for tool results;
- `assistant-tool:{toolCallId}` remains for rejected correlated assistant calls.

### 4.5 Active-turn continuation changes

Extend `ConversationLoopState` in `SessionApplication.ConversationLoop.cs` with private purge tracking, not a public metadata framework:

- `CommitStandaloneMessage(int modelRound, ModelMessage message, bool purgeAfterCorrection)`
- `CommitCurrentGroup(int modelRound, bool purgeAfterCorrection = false)`
- `AbortCurrentGroup()` to clear pending calls/results/sources/files when a batch is rejected before group commit
- `PurgeCorrectionGroups()` to remove groups marked purgeable and increment `HistoryRewriteGeneration`
- `GetEligibleGroupCount()` must exclude purgeable correction groups from compaction eligibility until they are purged or the turn terminates

A standalone developer corrective message can be represented as an `ActiveTurnContinuationGroup` containing one `Developer` message. Existing compaction input already serializes arbitrary `ModelMessage` roles, but purgeable correction groups should not be compacted because they may need exact removal after success.

## 5. Next main processing loop

The future main loop is still the existing serial conversation loop, but it has an explicit “classify or correct before acting” step.

```text
SubmitRequest / GeneratePlan / ProposeMutations
        |
        v
+------------------------------+
| Start logical model turn     |
| correctiveAttempts = 0       |
+------------------------------+
        |
        v
+-----------------------------------------------------------+
| Assemble request                                            |
| - stable context/messages                                  |
| - active-turn groups                                       |
| - current corrective messages                              |
| - advertised tools for this phase                          |
+-----------------------------------------------------------+
        |
        v
+-----------------------------------------------------------+
| Stream one model response                                  |
| - collect text/reasoning                                   |
| - collect all requested tool/structured outputs as a batch |
| - do not invoke tools during streaming                     |
+-----------------------------------------------------------+
        |
        v
+----------------------+        provider/validator failure
| Classify response    |-----------------------------+
| and preflight batch  |                             |
+----------------------+                             v
        | accepted                         +--------------------------+
        v                                  | Recoverable and budget?  |
+-----------------------------+            +--------------------------+
| Act on accepted response    |              | yes                 | no
| - execute validated tool    |              v                     v
|   batch                     |   +---------------------+   +----------------+
| - accept plan/mutation      |   | Reject entire batch |   | Fail closed    |
| - answer with final text    |   | Append correction   |   | sanitized msg  |
+-----------------------------+   | attempts++          |   +----------------+
        |                         +---------------------+
        | success/continue                 |
        v                                  v
+-----------------------------+    +-----------------------------+
| Purge corrective noise      |    | Continue same logical turn  |
| from future model history   |<---| with correction in context  |
+-----------------------------+    +-----------------------------+
```

Pseudo-code for the execution-owned loop:

```csharp
while (modelRoundAllowed)
{
    var request = AssembleRequest(activeTurnMessages, correctiveMessages);
    ModelResponse response;
    try
    {
        response = await ReadCompleteModelResponseAsync(request, cancellationToken);
    }
    catch (MalformedInvocationException exception)
        when (correctiveTurns.TryBeginAttempt(out var attempt))
    {
        loopState.AbortCurrentGroup();
        loopState.CommitStandaloneMessage(
            modelRound,
            CorrectiveMessageFactory.CreateDeveloperMessage(exception.Diagnostic, attempt, max),
            purgeAfterCorrection: true);
        continue;
    }

    var preflight = ValidateModelResponseAndToolBatch(response);
    if (!preflight.Succeeded)
    {
        if (!correctiveTurns.TryBeginAttempt(out var attempt))
        {
            throw preflight.ToException();
        }

        loopState.AbortCurrentGroup();
        AppendBatchCorrectionMessages(response.ToolCalls, preflight, attempt, max);
        continue;
    }

    var result = await ExecuteOrAcceptAsync(response, cancellationToken);
    if (result.CompletedSuccessfully)
    {
        loopState.PurgeCorrectionGroups();
    }

    return result;
}
```

Important: `ExecuteOrAcceptAsync` is reached only after the whole response or batch is accepted.

## 6. File-by-file implementation plan

### 6.1 `src/Threadsmith.Execution/ExecutionLimits.cs`

- Add `MaxCorrectiveTurns` default 3.
- Remove the three workflow-specific repair properties from production use.
- Keep `MaxStructuredOutputCharacters` unchanged.

### 6.2 `src/Threadsmith.App/HostFoundation.cs`

- Bind `execution:maxCorrectiveTurns` into `ExecutionLimits.MaxCorrectiveTurns`.
- Do not bind the obsolete per-loop keys. Assign the `execution:maxCorrectiveTurns` value to legacy execution-limit properties only as an internal compatibility bridge until those loops migrate.

### 6.3 `src/Threadsmith.App/ConfigurationBootstrap.cs`

- Add default `execution:maxCorrectiveTurns = "3"`.
- Remove bespoke repair-key defaults from documented configuration.
- Keep `execution:maxCorrectiveTurns` as the one documented key.

### 6.4 `src/Threadsmith.App/ApplicationComposition.cs`

- Construct `ExecutionStartRequest` from `host.ExecutionLimits.MaxCorrectiveTurns` instead of reading a separate correction-budget configuration value.

### 6.5 `src/Threadsmith.Models/ModelContracts.cs`

- Add `MalformedInvocationFailureKind`, `MalformedInvocationDiagnostic`, and `MalformedInvocationException`.
- Keep `MalformedModelOutputException` and `ModelFailureClassifier` behavior compatible.
- Optionally classify `MalformedInvocationException` as `RetryClassification.MalformedOutput` through the existing base type.

### 6.6 `src/Threadsmith.Models/ModelOutputValidator.cs`

- Add helper methods that produce `MalformedInvocationException` for tool-name, JSON, non-object, plan-schema, and mutation-schema failures when the caller is validating a model invocation.
- Keep existing `Validate`/`ParsePlan`/`ParseMutationSet` signatures where possible, but route new call sites through diagnostic-producing overloads.
- Stop relying on exception message text for repairability.

### 6.7 `src/Threadsmith.Models.OpenAiCompatible/OpenAiCompatibleModelProvider.cs`

- Change `DrainToolCalls` to validate the full accumulated batch and throw `MalformedInvocationException` with safe metadata when any call is invalid.
- Remove `NormalizeToolArguments` or reduce it to exact pass-through validation. Do not coerce missing/malformed arguments to `{}`.
- Include safe metadata only: provider family `openai-compatible`, tool ordinal, batch count, known tool name if present, argument character count, optional SHA-256 digest, JSON path/line/byte position.
- Ensure no valid sibling tool call is yielded when any sibling in the batch is malformed.

### 6.8 `src/Threadsmith.Models.OpenAiCodex/OpenAiCodexModelProvider.cs`

- Validate `response.output_item.done` function-call name and arguments before yielding `ToolRequestModelOutput`.
- Throw `MalformedInvocationException` with provider family `openai-codex` for missing name, invalid JSON arguments, and non-object arguments.
- If Codex response batches multiple function calls and any is malformed, yield none for that response. If current event shape makes whole-response batching awkward, accumulate function calls until `response.completed`, then validate/yield in model order.

### 6.9 `src/Threadsmith.Tools/ToolContracts.cs`

Add a no-side-effect batch preflight result shape:

```csharp
public sealed record ToolBatchPreflightResult(
    bool Succeeded,
    int? FailedOrdinal = null,
    string? FailedToolId = null,
    ToolErrorClassification ErrorClassification = ToolErrorClassification.None,
    string? SafeReason = null);
```

Extend `IToolInvocationPipeline`:

```csharp
ToolBatchPreflightResult PreflightBatch(IReadOnlyList<ToolBatchRequest> requests);
```

### 6.10 `src/Threadsmith.Tools/ToolInvocationPipeline.cs` and `ToolBatchScheduler.cs`

- Implement `PreflightBatch` by reusing the scheduler preparation path.
- It should deserialize input and compute scheduling claims but must not publish `ToolInvocationStarted`, request approvals, run tools, or call lifecycle hooks.
- If any request has `PreparationError`, return the first failed ordinal/tool/reason.
- `InvokeBatchAsync` remains unchanged for already accepted batches.

### 6.11 `src/Threadsmith.Execution/CorrectiveTurnState.cs` new file

- Add the small counter helper described in §4.3.
- Validate `maximumTurns >= 0`.
- `TryBeginAttempt` increments only when it returns true.

### 6.12 `src/Threadsmith.Execution/CorrectiveMessageFactory.cs` new file

- Centralize bounded correction text.
- Inputs should be safe diagnostics or already-sanitized host validation messages.
- Output `ModelMessage` values with proper role, section id, tool id, and tool-call id.
- Include helpers for:
  - provider-boundary malformed invocation developer message;
  - correlated rejected tool-batch tool results;
  - `propose_plan` schema correction;
  - `propose_mutations` schema/expected-text/pre-mutation correction;
  - plan sanity correction;
  - post-apply validation correction.

### 6.13 `src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs`

Primary implementation target.

Changes:

- Replace `maximumPlanProposalRepairAttempts` with one `CorrectiveTurnState` initialized from `_limits.MaxCorrectiveTurns`.
- Stop invoking/enqueuing tools while the response is still being read if doing so prevents whole-batch rejection. Either collect candidate tool calls first or ensure `AbortCurrentGroup` clears all pending state before correction.
- Catch `MalformedInvocationException` around `_model.StreamAsync` in `ExecuteConversationRoundAsync` and append a developer corrective message when attempts remain.
- Convert invalid non-plan tool output into a corrective turn instead of terminal failure when attempts remain.
- Before `InvokePendingToolBatchAsync`, call `_toolPipeline.PreflightBatch(streamState.PendingToolCalls)`.
- If preflight fails, append correlated tool-result correction messages for the entire batch, clear pending calls, and continue without invoking tools.
- Replace the `propose_plan` bespoke catch branch with the same corrective-turn path and message factory.
- Add handling for `propose_plan` plus other tool-producing output in the same response: reject the entire response and ask for exactly one valid `propose_plan` call or an ordinary answer.
- Add `ConversationLoopState` purge support as described in §4.5.
- After a successful corrected plan/tool batch, call `PurgeCorrectionGroups` before the next model request or before returning final success.

### 6.14 `src/Threadsmith.Execution/SessionApplication.cs`

Plan sanity migration target after §6.13 is stable.

Changes:

- Replace the local `for` loop in `RunPlanSanityAndPolicyAsync` with a call path that appends a corrective active-turn message and reuses `GeneratePlanAsync`.
- Stop mutating `registration.Task.UserConstraints` for plan sanity correction.
- Keep `PlanSanityCheckCompleted` and `PlanRevisionRequested` events if they remain useful, but the model-visible correction should be a corrective message, not a hidden task constraint.
- Remove `AccrueRepairWallClock`; corrective requests accrue normal model usage and wall-clock through the ordinary model call path.
- Keep hard/non-repairable sanity failures fail-closed.

### 6.15 `src/Threadsmith.Execution/MutationProposalApplication.cs`

Mutation proposal migration target.

Changes:

- Replace the outer bespoke retry `for` loop in `HandleAsync` with `CorrectiveTurnState`.
- Change `HandleCoreAsync` so it accepts a list of corrective `ModelMessage` values, not a `CorrectionEvidence` string hidden in `Task.UserConstraints`.
- Append corrective messages to `ModelStreamRequest.Messages`; also append a bounded legacy correction block to `Input` only when `context.Messages` is empty.
- Replace `IsRepairableMutationProposalFailure` substring matching with diagnostic/failure-kind classification.
- Move text from `FormatMutationProposalCorrectionEvidence` into `CorrectiveMessageFactory` and keep it bounded.
- Preserve pre-mutation Roslyn correction content from `FormatPreMutationCorrection`, but route it as a corrective message.
- Preserve checkpoint-facing behavior: successful correction still returns a staged mutation set requiring exact diff approval.

### 6.16 `src/Threadsmith.Execution/ExecutionOrchestrator.cs`

Approved-plan validation correction migration target.

Changes:

- Continue to use durable checkpoints, approval gates, and `CorrectionAttempts`/`CorrectionBudget` on `ExecutionContinuation` for approved-plan validation correction.
- Source `CorrectionBudget` from `ExecutionLimits.MaxCorrectiveTurns` through `ExecutionStartRequest`.
- Replace `CreateCorrectionEvidence` string injection with a corrective message passed into `MutationProposalApplication`.
- Keep the behavior that a correction mutation returns to mutation approval with an exact diff.

### 6.17 `src/Threadsmith.Validation/*`

Removal target after production paths and tests are migrated:

- delete `CorrectionLoop.cs` and `TestCorrectionLoop.cs` if no production code uses them;
- remove `CorrectionContext`, `CorrectionAttemptResult`, `CorrectionLoopResult`, `TestCorrectionContext`, `TestCorrectionAttemptResult`, and `TestCorrectionLoopResult` from `ValidationContracts.cs` if no longer referenced;
- remove or rewrite tests in `tests/Threadsmith.Validation.Tests/Milestone6Tests.cs` that cover only those unused helpers.

### 6.18 Events/projections/TUI

Candidate minimal event:

```csharp
public sealed record ModelCorrectionRequested(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    RunId RunId,
    string Category,
    int AttemptNumber,
    int MaximumAttempts,
    string Reason) : DomainEvent(SessionId, OccurredAt);
```

Use it for all corrective turns. Existing events such as `MutationProposalRepairAttempted` may stay as durable compatibility records but should not be emitted from the new code once migration is complete. Update `TuiPresentationFormatter` and projection handling to render the generic event.

## 7. Migration order

1. Add `MaxCorrectiveTurns` config plumbing.
2. Add diagnostic exception/metadata types.
3. Update OpenAI-compatible provider to surface provider-boundary malformed tool-call diagnostics without yielding partial batches.
4. Update Codex provider to validate function calls consistently.
5. Add tool-batch preflight.
6. Update `SessionApplication.ConversationLoop` for:
   - provider-boundary corrective developer messages;
   - invalid ordinary tool corrective turns;
   - batch atomic rejection;
   - `propose_plan` migration;
   - purge of successful correction noise.
7. Stop and manually verify behavior with scripted/fake providers.
8. After behavior signoff, add/update focused tests for §6.13.
9. Migrate plan sanity repair from task-constraint injection to corrective messages.
10. Migrate mutation proposal repair from task-constraint injection and substring matching to corrective messages.
11. Migrate approved-plan validation correction evidence to the same message path.
12. Remove unused validation correction helper classes and obsolete tests.
13. Update user/operator docs only after behavior is signed off.

## 8. Tests to add/update after behavior signoff

### Planning/conversation tests

File: `tests/Threadsmith.Planning.Tests/Milestone4Tests.cs`

- provider-boundary invalid JSON tool args gets a developer corrective message and second model request;
- provider-boundary invalid args with prior assistant text does not archive the text as final answer;
- non-object tool args corrective turn;
- valid batch with one invalid sibling rejects entire batch and invokes no tools;
- correlated rejected batch includes tool-role result for every assistant tool call;
- `propose_plan` malformed schema uses generic corrective-turn counter and succeeds;
- `propose_plan` plus another tool in same response rejects the whole response and corrects;
- exhaustion after `MaxCorrectiveTurns` fails closed.

### Provider tests

Existing provider-specific test project if present, otherwise the closest model tooling tests:

- OpenAI-compatible invalid JSON function arguments throw `MalformedInvocationException` with safe metadata;
- OpenAI-compatible no longer coerces malformed no-arg calls to `{}`;
- OpenAI-compatible partial malformed batch yields no valid sibling outputs;
- Codex invalid/missing function arguments throw the same diagnostic shape;
- raw malformed arguments are not present in exception message.

### Tool pipeline tests

File: `tests/Threadsmith.ModelTooling.Tests` or closest tool-runtime suite:

- `PreflightBatch` detects invalid arguments without publishing `ToolInvocationStarted`;
- `PreflightBatch` detects unknown tool;
- a valid batch still executes through existing `InvokeBatchAsync` behavior.

### Mutation tests

File: `tests/Threadsmith.Mutations.Tests/Milestone5Tests.cs`

- bad `expectedText` correction appears as a corrective model message, not `Task.UserConstraints`;
- legacy mutation schema correction uses failure kind, not substring matching;
- pre-mutation diagnostics correction preserves existing guidance;
- correction exhaustion uses `MaxCorrectiveTurns`.

### Execution orchestration tests

File: `tests/Threadsmith.ExecutionOrchestration.Tests/ExecutionOrchestratorTests.cs`

- post-apply validation correction budget comes from `ExecutionStartRequest.CorrectionBudget` populated by `MaxCorrectiveTurns`;
- correction still returns to exact mutation approval;
- existing resume/checkpoint behavior remains unchanged.

### Cleanup tests

- Remove or rewrite `CorrectionLoop_*` and `TestCorrectionLoop_*` tests after deleting unused helper classes.
- Update `RepoConfigTests` to assert `execution:maxCorrectiveTurns` binding/defaults instead of the three bespoke repair keys.

## 9. Validation commands

Run sequentially on Windows to avoid shared-output file locks:

```powershell
dotnet build src\Threadsmith.sln --no-restore
dotnet test tests\Threadsmith.Planning.Tests\Threadsmith.Planning.Tests.csproj --no-build
dotnet test tests\Threadsmith.Mutations.Tests\Threadsmith.Mutations.Tests.csproj --no-build
dotnet test tests\Threadsmith.ExecutionOrchestration.Tests\Threadsmith.ExecutionOrchestration.Tests.csproj --no-build
dotnet test tests\Threadsmith.ModelTooling.Tests\Threadsmith.ModelTooling.Tests.csproj --no-build
dotnet test tests\Threadsmith.Architecture.Tests\Threadsmith.Architecture.Tests.csproj --no-build
rg -n "^## Scenario .*\*\(.*plan|^\*\*(Coverage status|Planned coverage):" docs\implementation-plans\acceptance-scenarios.md
rg -n "^\*\*(Status|Baseline|Coverage status|Planned coverage):|^## MTP-.*\(M[0-9]" docs\implementation-plans\manual-test-plan.md
rg -n "Implementation status:|implementation-complete|completion history" docs\implementation-plans\README.md
rg -n "Plan [0-9]|plan-[0-9]" -g "AGENTS.md" .
git diff --check
```

The first four `rg` governance searches should return no matches.

## 10. Implementation notes and pitfalls

- Do not add raw malformed arguments to messages, events, logs, or test snapshots.
- Do not let `ToolInvocationPipeline.InvokeBatchAsync` see a batch that failed preflight.
- Do not mark valid negative tool results as corrective noise; only rejected malformed/invalid batches are purgeable.
- Do not route on human-readable exception message substrings after failure kinds exist.
- Do not fake `Tool` role messages for provider-boundary failures without valid assistant tool-call correlation.
- Do not compact purgeable corrective groups before success/exhaustion.
- Do not hide correction by appending it to `TaskSpecification.UserConstraints`; it should be visible as an active-turn model message.
- Keep plan/mutation approval behavior unchanged: correction can ask the model to re-propose, but approval still applies to the accepted exact plan/diff.
- Preserve cancellation behavior: cancellation stops corrective turns immediately and does not append another correction.

## 11. Assumptions for implementation

- Default `execution:maxCorrectiveTurns` is 3 because all existing bespoke repair budgets default to 3.
- One invalid sibling invalidates the entire requested tool batch.
- Provider-boundary malformed tool-call arguments use developer correction, even when a provider supplied a native call id, because the host cannot safely create a normal tool result without a valid executable argument object.
- Existing `ExecutionContinuation.CorrectionAttempts`/`CorrectionBudget` remain for durable approved-plan validation checkpoints until a separate checkpoint-schema simplification is warranted.
- Planning/user/operator documentation updates stay deferred until the behavior is signed off.
