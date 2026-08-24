# Implementation Plan 87: Bounded Malformed Tool-Invocation Correction

**Status:** Planned

**Delivery track:** Maintenance — recoverable model-output correction without changing tool authority
**Strategy source:** Shared Context §A.1, §A.3, §A.5, §C, and §G; model contracts and tool pipeline contracts
**Prerequisite plans:** Plans 7, 8, 52, and 57

## 1. Objective

Add a bounded correction loop for malformed model-authored tool invocations so a recoverable JSON/schema/tool-shape error does not terminate the whole conversation. The host must continue to fail closed for execution: no malformed, partial, guessed, or repaired-by-host arguments are ever invoked. Instead, the model receives explicit corrective evidence and gets a bounded opportunity to re-emit a valid tool call or answer without the tool.

The initial deliverable focuses on malformed tool invocations wherever the model attempts to call host tools, including provider-boundary native tool-call assembly failures and ordinary conversation tool validation failures. Existing specialized plan and mutation correction loops remain authoritative until Plan 88 consolidates them.

## 2. Architectural Context

Threadsmith already treats model output as untrusted, structured proposals. `ModelOutputValidator` requires tool arguments to be JSON objects before they cross into execution. The OpenAI-compatible native-tool adapter accumulates streamed function-call deltas and currently throws `MalformedModelOutputException` when accumulated tool arguments are invalid JSON. In that provider-boundary case, the conversation loop receives only a terminal exception, not a validated `ToolRequestModelOutput` that can be represented as a normal assistant tool-call message.

Other areas already have local correction behavior: `propose_plan` has a bounded repair path in the conversation loop, mutation proposals have correction in the mutation proposal application, and semantic-first/duplicate tool guidance is returned as ordinary evidence. The gap is not safety; the gap is recoverability for malformed tool invocations that should be corrected by the model rather than ending the turn.

This plan adds the missing recoverable path while preserving host ownership, budgets, cancellation, tool policy, and provider-neutral contracts.

## 3. Scope

- Classify malformed model-authored tool invocations separately from general provider failures.
- Capture safe diagnostic metadata for malformed tool calls: known tool name when available, failure kind, argument character count, optional argument digest, JSON parser path/byte position, provider family, and continuation round.
- Do not log or persist raw malformed arguments by default.
- Convert recoverable malformed tool-call failures into bounded model correction evidence.
- Retry the active model request with a clear instruction to re-emit valid JSON object arguments matching the advertised tool schema, or to answer without a tool if the tool is no longer needed.
- Bound malformed-tool correction attempts independently from ordinary model rounds and existing plan/mutation repair counts.
- Apply the correction loop to ordinary conversation/evidence-collection tool calls, including failures thrown before a `ToolRequestModelOutput` can be yielded.
- Preserve existing specialized `propose_plan` and mutation correction behavior during this plan; they may use the same safe metadata type only when doing so is a small integration.
- Ensure malformed invocations never reach `IToolPipeline`, extension tools, MCP tools, process execution, mutation staging, validation, or durable authoritative state as executed calls.
- Implement functionality first, then stop for user review.
- Write or update automated tests only after the user signs off on the behavior.
- Write or update user/operator documentation only after the user signs off on the behavior.

## 4. Non-Scope

- No host-side JSON repair or best-effort argument completion.
- No execution of malformed or partially parsed tool arguments.
- No raw malformed argument logging in ordinary diagnostics, events, or user-visible messages.
- No unbounded retry loop or recursive self-correction.
- No broad refactor of all correction loops; that is Plan 88.
- No change to tool schemas, tool authorization, tool scheduling, side-effect classification, trust, approvals, or policy gates.
- No requirement that ordinary inspection tools opt into provider strict schemas.
- No user/operator documentation or manual-test-plan changes before behavior sign-off.

## 5. Current State

The current provider-neutral invariant is correct: a `ToolRequestModelOutput` must contain a non-empty tool name and JSON-object arguments. However, malformed native tool-call arguments can fail before the conversation loop can turn the problem into model-visible correction evidence.

A recent raw model exchange showed this failure mode: a model streamed ordinary answer text, then attempted another native tool call whose accumulated arguments were not valid JSON. The provider threw `MalformedModelOutputException("Tool arguments are not valid JSON.")`; logging recorded the failure but could not include the malformed call because it never became a normalized chunk. The conversation terminated even though the safe response would have been to reject execution and ask the model to re-emit valid tool arguments.

Existing repair loops prove the product already accepts bounded correction as a host-owned control-flow pattern. This plan makes malformed tool invocations follow that pattern.

## 6. Proposed Design

### 6.1 Malformed invocation classification

Introduce a provider-neutral malformed-tool-invocation failure shape. It may be an exception subtype or a result carried through an existing exception hierarchy, but it must expose only safe fields:

- known tool name, when available;
- failure kind, such as invalid JSON, non-object JSON, missing name, unknown tool, unsupported schema version, exceeded streamed argument bound, or malformed provider tool-call frame;
- argument character count and optional SHA-256 digest of the raw malformed argument string;
- JSON parser path, line/byte position, and message class when available;
- provider/transport family and tool-continuation round;
- whether the host has enough correlation to represent the malformed attempt as an assistant tool-call message.

The raw argument string remains inside the provider/validator failure boundary unless explicit raw diagnostics are enabled under the existing raw model log policy.

### 6.2 Provider-boundary behavior

When a provider can identify a malformed tool-call argument, throw the classified malformed-tool-invocation failure instead of a generic malformed-output exception. Keep generic malformed-output exceptions for unrecoverable stream corruption, protocol frames with no safe recovery path, and non-tool structured-output failures.

If a tool name is known and arguments are malformed, the failure is recoverable. If the tool name is absent or the provider frame is internally inconsistent, the failure may still be recoverable as a generic malformed tool attempt, but the correction message must not claim a specific tool.

### 6.3 Conversation correction path

Wrap ordinary conversation model streaming with a malformed-tool correction handler. On a recoverable malformed invocation:

1. stop consuming the failed stream;
2. do not enqueue or execute any tool call from the malformed attempt;
3. publish sanitized transient status that a model tool-call correction is being requested;
4. append bounded corrective host/developer evidence to the next continuation request;
5. increment a malformed-tool correction counter;
6. retry the same active turn while preserving budgets, cancellation, model selection, tool inventory, context layout, and prior valid tool results.

The correction instruction should be direct and minimal: the previous tool invocation was malformed; arguments must be one valid JSON object matching the advertised schema; do not include prose before the corrected tool call unless answering without the tool; never repeat the malformed payload.

### 6.4 Correlation and transcript safety

Only create an assistant tool-call message when the host has a valid tool name, valid tool-call id/correlation, and valid JSON arguments. Provider-boundary malformed JSON usually lacks that final requirement, so represent the correction as host/developer guidance rather than a fake tool result.

For validation failures that occur after a valid `ToolRequestModelOutput` exists, prefer a normal assistant tool-call plus tool-result correction message when correlation is safe. This preserves provider protocols that require a tool response after an assistant tool call.

### 6.5 Bounds and exhaustion

Add one bounded setting or internal default for malformed tool-call correction attempts, defaulting to a small value such as three. Attempts count per active turn/request chain, not globally. Exhaustion produces the existing terminal malformed-output failure with a clearer message stating that correction was attempted and exhausted.

Correction attempts count against wall-clock/model/cost/output budgets. Cancellation stops the loop immediately.

## 7. Public Contracts

Expected host-owned additions:

- a malformed-tool-invocation failure kind enum;
- a safe malformed-tool-invocation diagnostic DTO or exception subtype;
- a bounded correction-attempt counter in conversation loop state;
- sanitized activity/event text for correction start/exhaustion, if existing event contracts support it without schema churn;
- optional configuration surface for maximum malformed-tool correction attempts only if existing execution repair settings are insufficient.

No provider SDK, HTTP, SSE, terminal, extension, MCP SDK, or raw filesystem types cross subsystem boundaries.

## 8. Project/File Changes

Before functionality signoff, expected source changes are limited to:

- `src/Threadsmith.Models/ModelContracts.cs` and/or `ModelOutputValidator.cs` — classified malformed tool invocation metadata;
- `src/Threadsmith.Models.OpenAiCompatible/OpenAiCompatibleModelProvider.cs` — throw classified malformed tool failures from native tool-call accumulation/draining;
- `src/Threadsmith.Models.OpenAiCodex/OpenAiCodexModelProvider.cs` — apply equivalent classification where Responses function-call arguments can be malformed;
- `src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs` — bounded correction retry around ordinary conversation model streaming;
- `src/Threadsmith.Models/ModelExchangeLogging.cs` — sanitized failure metadata only if needed for diagnosis without raw payload leakage;
- configuration/composition files only if a public attempt-count setting is added.

Do not change tests or product documentation during the functionality trial.

After signoff, likely tests and documentation are listed in Sections 10 and 16.

## 9. Ordered Tasks

### Functionality trial

1. Get user approval of this plan and default correction-attempt count.
2. Reproduce or fixture the provider-boundary malformed JSON path from a scripted or recorded stream without persisting raw malformed content.
3. Add the safe malformed-tool-invocation classification type.
4. Convert OpenAI-compatible malformed native tool-call argument failures to the classified failure.
5. Add the conversation-loop correction handler and bounded retry state.
6. Ensure malformed attempts are never enqueued, scheduled, audited as completed, or represented as executed tools.
7. Add transient sanitized correction status where existing projection/event contracts allow it.
8. Build only the affected projects needed to exercise the behavior.
9. Run a focused manual trial with a malformed tool-call fixture or provider replay.
10. Stop and wait for explicit user signoff.

### After functionality signoff

11. Add or update the focused automated tests listed in Section 10.
12. Run the focused suites and fix confirmed defects.
13. Update only the documentation listed in Section 16.
14. Run broader build/test/format/planning-governance checks.
15. Update this plan status only after signed-off behavior, deferred verification, and deferred documentation pass.

Do not commit the functionality trial as complete before the deferred tests and documentation are finished.

## 10. Testing

After signoff, add or update tests for these cases:

1. **Provider-boundary invalid JSON** — streamed native tool-call arguments are malformed, no tool executes, corrective model continuation is issued, and a subsequent valid call succeeds.
2. **Malformed after visible text** — partial assistant text does not prevent correction and is accounted without being treated as a final answer when a tool call was attempted.
3. **Non-object JSON arguments** — syntactically valid but non-object arguments are corrected rather than executed.
4. **Missing or empty tool name** — correction evidence avoids naming a tool and no fake tool call is created.
5. **Unknown tool name** — correction occurs where recoverable and existing tool policy remains authoritative.
6. **Retry exhaustion** — bounded attempts end with a sanitized terminal failure and no tool execution.
7. **Cancellation** — cancellation stops correction without another model call.
8. **No raw payload leakage** — logs, events, and user-visible messages omit malformed raw arguments while retaining safe metadata.
9. **Existing plan/mutation repair unaffected** — specialized loops still behave as before until Plan 88 changes them.
10. **OpenAI-compatible and Codex provider parity** — both providers classify malformed function-call arguments consistently where their protocols expose them.

## 11. Security/Permissions

The correction loop never executes malformed arguments, never infers missing fields, never uses model-authored authority to bypass policy, and never treats correction text as approval. Tool trust, side-effect classification, scheduling, MCP/extension/process boundaries, path policy, secret redaction, and mutation/build approvals remain unchanged.

Malformed raw argument strings may contain secrets or repository content. Do not include them in ordinary logs, durable events, tool results, correction instructions, or UI text. Only existing explicit raw model diagnostics may persist provider payloads under their opt-in safety gates.

## 12. Observability

Record sanitized counts and classes: malformed-tool failure kind, provider family, known tool name if safe, continuation round, attempt number, corrected success, exhaustion, cancellation, and duration. Do not log raw arguments, request bodies, response bodies, hidden reasoning, credentials, authorization headers, or tool output bodies beyond existing explicit diagnostic policy.

## 13. Migration/Compatibility

The behavior is additive. Existing sessions without malformed tool-call failures behave unchanged. Older durable events remain valid. If a public setting is added, default behavior should preserve bounded correction without requiring repository configuration. Unknown failure kinds fail closed and may fall back to the existing terminal malformed-output behavior when no safe correction path exists.

## 14. Acceptance Criteria

- Recoverable malformed model-authored tool invocations trigger bounded correction rather than terminating the conversation immediately.
- Malformed, partial, non-object, unknown, or uncorrelated tool attempts are never executed.
- The model receives clear correction evidence and can recover by re-emitting valid JSON object arguments or by answering without a tool.
- Retry attempts are bounded, cancellable, budget-accounted, and sanitized.
- Provider-boundary malformed native tool-call arguments are handled without fake tool-call correlation.
- Existing specialized plan and mutation correction loops remain stable until Plan 88.
- Focused tests, affected provider/execution tests, architecture checks, solution build, and planning-governance checks pass after signoff.

## 15. Risks

- **Correction loop becomes unbounded:** enforce a small attempt count and ordinary budgets.
- **Malformed arguments leak secrets:** keep raw payloads out of ordinary diagnostics and correction messages.
- **Fake tool correlation breaks provider protocol:** create assistant/tool messages only when correlation and JSON are valid.
- **Host accidentally repairs model data:** correction instructions ask the model to re-emit; host never edits arguments into executable form.
- **Specialized loops diverge further:** keep this plan focused, then consolidate through Plan 88.

## 16. Documentation

After signoff, update model/tool troubleshooting documentation and any operator reference that describes malformed model-output behavior. Document that malformed tool calls fail closed for execution but receive bounded correction attempts before terminal failure. Update user-facing docs only if the behavior is visible in ordinary interaction. Update testing docs only if new scripted-provider or raw-log fixtures become normative.

Do not update user/operator documentation, acceptance scenarios, or manual test procedures before behavior signoff.

## 17. Open Decisions

- Default malformed-tool correction attempt count.
- Whether the attempt count is internal-only or exposed through execution configuration.
- Exact failure-kind taxonomy shared by OpenAI-compatible and Codex providers.
- Whether raw diagnostic mode should include malformed argument hashes only, or optionally parser snippets under an additional explicit safety gate.
- Whether unknown tool names should always correct or sometimes fail terminally depending on phase and provider correlation.
