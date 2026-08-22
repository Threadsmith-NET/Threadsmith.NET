# Implementation Plan 80: Token-Aware Active-Turn Tool Continuation Compaction

**Status:** Planned

**Delivery track:** Maintenance — active-turn context-governance hardening
**Strategy source:** Shared Context §A.2 and §A.5; ADR-12; ADR-31; Codex-style pre-sampling and mid-turn transcript compaction requested after measured long-running evidence collection
**Prerequisite plans:** plans 34, 35, 51-55, 57, and 78

## 1. Objective

Prevent a long evidence-collection turn from replaying every prior assistant tool-call group and full sanitized tool result until the selected model's emergency input limit. Before each model call, Threadsmith must estimate the complete provider-neutral request, compact an eligible older active-turn prefix when configured pressure is reached, retain the newest complete tool groups verbatim, preserve frozen authority and current-user context unchanged, and continue the same turn from a validated bounded summary.

This is maintenance hardening of existing governed context and continuation behavior. It does not reopen completed Milestone 25 or introduce a new memory capability.

## 2. Architectural Context

Plans 34-35 and ADR-31 govern cross-turn archive, structured conversation summaries, retrieval, pressure reduction, inspection, and compaction at safe completed-turn boundaries. Plans 51-55 establish canonical request layout, chronological provider-neutral messages, append-only tool continuations, cache-aware context generation, conservative cache/stateful continuation, and exact wire estimation. Plan 57 owns host-proven sibling-tool batching and conflict-free execution. Plan 78 adds bounded repository memory to the initially assembled context.

The active conversational loop currently freezes one `ContextAssemblyResult` for the turn. Each later model round appends an assistant tool-call message and every full sanitized tool result to `ConversationLoopState.ContinuationMessages`. `BoundContinuationMessages` reduces those results only when the complete request approaches the selected model's maximum input budget. Per-tool limits and aggregate call ceilings prevent unlimited individual operations, but they do not maintain a compact active-turn working set.

A measured read-only audit grew from 52,365 to 503,358 input characters over 24 completed tool rounds. Tool execution was already strongly batched; model-visible tool results, especially file reads, dominated growth. The missing control is therefore active-turn compaction, not another batching mechanism or an arbitrary tool-round cutoff.

Codex is an implementation reference, not a compatibility authority. Threadsmith retains host-owned provider-neutral contracts, evidence provenance, sensitivity policy, deterministic inspection, cancellation, and cache safety.

## 3. Scope

- Estimate the complete model request before every sampling call in the ordinary evidence-collection/planning conversation loop.
- Add an active-turn compaction pressure policy below the selected model's emergency maximum-input boundary, with explicit output reserve and recent-context targets.
- Keep stable host policy, repository instructions, current user input, output contracts, tool inventory, and other frozen authority-bearing context byte-for-byte unchanged.
- Partition continuation history into complete chronological groups: one assistant sibling-tool-call message and all matching tool-result messages.
- Select a compaction cut only after a complete group; never split siblings or orphan a tool call/result.
- Ensure every newly completed tool group is shown verbatim to the model in at least one subsequent round before it becomes eligible for compaction.
- Retain the newest eligible groups verbatim according to a configured token target and compact only an older completed prefix.
- Reuse the existing bounded structured conversation-compaction infrastructure where its contracts fit; add a separate active-turn entry point and candidate schema where lifecycle or validation differs.
- Generate a structured model summary of the eligible prefix, then validate schema, size, source references, authority, sensitivity, and cut-point correspondence before activation.
- Replace only the model-visible eligible prefix with one bounded low-authority active-turn summary while retaining newer messages unchanged.
- Preserve the complete original sanitized tool execution in existing run/evidence/audit boundaries according to current retention and redaction policy.
- Continue the same run and turn after successful compaction without requiring user input or emitting a model-visible compaction tool call.
- Reset or conservatively re-establish provider cache/stateful-continuation identity when rewritten history cannot legally extend the previous provider continuation.
- Expose bounded context inspection, events, telemetry, and diagnostics for trigger rationale, source range, before/after estimates, retained groups, and outcome.
- Propagate cancellation through estimation, candidate generation, validation, persistence, and continuation rebuild.
- Profile verbose result categories and permit lower model-visible per-result projection limits only as a separately evidenced optimization within this plan.

## 4. Non-Scope

- No model-visible evidence handle, generic result-rehydration tool, or per-result provenance-envelope protocol.
- No claim that compaction is lossless; the full sanitized execution remains auditable, while the active model view is a validated lossy summary plus recent raw groups.
- No arbitrary host cutoff that ends exploration after a fixed number of rounds, calls, files, searches, or elapsed seconds.
- No generic batch tool, change to sibling-tool scheduling, or weakening of Plan 57 conflict policy.
- No deletion or mutation of archived user/assistant conversation history, durable repository memory, raw run evidence, tool events, or optional raw model-exchange logs.
- No preservation or replay of hidden reasoning, provider SDK objects, secrets, unsanitized payloads, or terminal-library types.
- No compaction of current authority instructions, current user input, approval state, mutation authorization, or pending/incomplete tool groups.
- No use of summaries to grant tool, policy, approval, mutation, hook, skill, MCP, extension, or repository-memory authority.
- No provider-managed conversation identity as the durable source of truth.
- No initial expansion into mutation application, correction, validation, delegated-worker, or approved-plan execution loops unless implementation inspection proves they already share the same safe read-only continuation contract.

## 5. Current State

`ContextAssembler` creates a bounded governed context and one `ContextInspectionRecord` at turn start. `SessionApplication` freezes that result, appends chronological assistant tool calls and sanitized tool results outside the assembler, and reconstructs each subsequent request from the frozen messages plus the full continuation list.

`BoundContinuationMessages` estimates the combined request against the model token budget and greedily replaces large tool results with bounded previews only when needed to fit the provider request. It preserves protocol shape but is an emergency wire-fit mechanism: it has no operational pressure target, active-turn summary, tool-group cut selection, stable summary checkpoint, or active-turn inspection record.

Cross-turn `ConversationCompactor` already provides bounded candidate generation, validation, provenance, cancellation, idempotence, and failure preservation for archived visible conversation. Raw tool results are intentionally excluded from the durable conversation archive, so the cross-turn entry point cannot simply be invoked unchanged over active continuation messages.

## 6. Proposed Design

### 6.1 Pre-sampling pressure assessment

Before every model call, build the same provider-neutral request envelope that would otherwise be sent and estimate:

- frozen authority/context tokens;
- active-turn summary tokens, if one exists;
- retained assistant tool-call and tool-result groups;
- current output-schema and tool-definition cost;
- selected-model output reserve and provider safety margin;
- projected total wire input.

Use the canonical Plan 51/54 estimator rather than character-count heuristics or provider SDK types. Trigger active-turn compaction when the configured pressure target is reached while enough eligible history exists to produce a useful reduction. The trigger is token-pressure-based, not round-count-based. Context inspection must explain both trigger and no-trigger decisions.

### 6.2 Complete continuation groups and cut selection

Represent the continuation as ordered host-owned groups. A tool group contains:

1. the assistant message holding one or more sibling tool calls;
2. every corresponding tool-result message in call order;
3. host-owned invocation/evidence identifiers and token estimates used for validation, not extra model authority.

A group becomes eligible only after the model has received it verbatim in a completed subsequent request. Select the oldest complete prefix whose replacement can meet the target while preserving a configured newest-group/recent-token window. Never split an assistant sibling-call set, orphan a result, reorder calls, or compact a currently pending group.

Malformed historical pairing fails validation and leaves the original continuation active. The emergency wire-fit mechanism remains the final compatibility backstop; it is not the normal compaction path.

### 6.3 Active-turn compaction candidate

Build a bounded candidate request from the eligible prefix. Reuse the existing compaction provider abstraction when possible, but use an active-turn schema that captures only information required to continue the current task:

- current task objective and acceptance intent;
- explicit user constraints and corrections already present in the eligible prefix;
- established repository findings;
- relevant paths, symbols, line ranges, diagnostics, and tool outcomes;
- unresolved questions and hypotheses, clearly distinguished from facts;
- failed or inconclusive investigations that should not be repeated;
- recommended next evidence step without granting authority;
- source references to host-known message, tool invocation, or evidence identifiers.

The compaction request may use bounded tool-result projections to fit its own budget, but must retain identifiers and source metadata. It must not include hidden reasoning or unsanitized/raw provider payloads.

### 6.4 Validation and authority

Validate a candidate before activation:

- supported schema/version and closed category values;
- configured item, character, and token limits;
- every source identifier belongs to the selected eligible prefix;
- cited repository paths/ranges or symbols correspond to recorded tool provenance;
- factual findings are attributable and do not promote unsupported model prose;
- unresolved hypotheses remain labeled as unresolved;
- no current-user, host-policy, approval, permission, mutation, or output-schema authority is introduced or rewritten;
- sensitivity does not exceed the selected model/profile policy;
- the summary covers exactly the selected source range and declares its cut boundary/version;
- the resulting request fits the active-turn target and provider maximum.

Use the existing compaction repair/retry classification only if it is already bounded and cancellation-safe. Invalid candidates never replace the active continuation.

### 6.5 Request replacement and continuation

On success:

1. keep the frozen initial context unchanged;
2. insert one host-owned, low-authority active-turn summary using the same untrusted context boundary as existing validated conversation summaries, never a system-policy role;
3. retain the newest raw continuation groups byte-for-byte;
4. rebuild provider-neutral chronological messages;
5. invalidate request/cache/stateful-continuation identities whose history no longer matches;
6. begin a compatible new provider request segment and continue the same Threadsmith turn.

Subsequent compaction is cumulative: a later candidate receives the active summary plus the next eligible raw prefix and produces a new validated summary version. It must not recursively expand prior summaries or duplicate retained groups.

### 6.6 Audit, persistence, and inspection

Compaction changes only the model-visible working set. Existing `ToolInvocationStarted`/`Completed`, evidence, run events, and configured raw-exchange logging retain the original sanitized execution according to current persistence, sensitivity, retention, and redaction contracts.

Record a bounded active-turn compaction checkpoint containing:

- run/turn and summary version;
- source group range and invocation/evidence identifiers;
- frozen-context identity;
- before/after token estimates;
- retained-group count/tokens;
- candidate provider/profile identity without credentials;
- outcome/failure classification and duration;
- summary content hash and storage reference when persistence is required for safe resume.

Do not copy raw tool bodies into diagnostics, telemetry, or context inspection. Determine during implementation whether ordinary non-resumable conversational runs need durable summary checkpoints or only in-memory state plus the existing durable original execution.

### 6.7 Failure, cancellation, and fallback

Cancellation abandons candidate generation and leaves the original continuation unchanged. A transient candidate failure may use the existing bounded retry policy; validation or authority failure does not retry blindly. If the unchanged request still fits the provider maximum, the host may continue once under an inspectable failure/backoff state rather than attempting compaction every round.

If the request cannot fit the provider maximum, use the existing deterministic wire-fit reduction only as a compatibility fallback or fail with a controlled context-capacity outcome. Never silently omit frozen policy/current-user content, create unmatched tool messages, or treat a failed compaction as success.

### 6.8 Optional verbose-result projection reduction

Profile active-turn contributors after compaction is implemented. Lower a tool's model-visible result projection only when reproducible evidence shows material residual pressure and focused tests prove usefulness is preserved. Keep host-owned full sanitized results and existing tool-specific continuation metadata. Do not adopt a blanket byte limit from Pi, Codex, or any provider.

This optimization is optional and must not delay acceptance of the core active-turn compaction path.

## 7. Public Contracts

Expected host-owned contracts include:

- `ActiveTurnCompactionPolicy` — enabled state, pressure target, output reserve, summary budget, and retained-recent target;
- `ActiveTurnContinuationGroup` — complete assistant-call/result pairing plus estimates and source identifiers;
- `ActiveTurnCompactionRequest` — frozen-context identity, prior summary, eligible source range, and bounded candidate input;
- `ActiveTurnCompactionCandidate` — versioned structured summary and source references;
- `ActiveTurnCompactionValidationResult` — accepted candidate or closed rejection reasons;
- `ActiveTurnCompactionCheckpoint` — summary version, source boundary, estimates, hashes, and outcome;
- inspection DTO additions for active-turn pressure, retained/compacted groups, summary version, and failure/backoff state;
- lifecycle events such as active-turn compaction started/completed/failed if existing generic context events cannot express the boundary safely.

Contracts remain provider-neutral and serializable. No model-provider SDK, terminal, extension, Roslyn, or persistence implementation types cross subsystem boundaries. Any new public domain event requires event-catalog and schema-version updates.

## 8. Project/File Changes

Expected areas:

- `Threadsmith.Core` — provider-neutral policy, group, candidate, checkpoint, inspection, and event contracts.
- `Threadsmith.Context` — active-turn candidate construction, validation reuse/adaptation, source-reference validation, and cumulative-summary behavior.
- `Threadsmith.Execution` — pre-sampling assessment, complete-group tracking, compaction orchestration, replacement, failure/backoff, cancellation, and continued same-turn execution.
- `Threadsmith.Models` — canonical request estimates and low-authority summary projection contracts.
- Compiled model providers — preserve chronological pairing and reset/re-establish cache or stateful continuation after rewritten history.
- `Threadsmith.Persistence` — only if active-run resume requires durable checkpoint storage beyond existing run/evidence events.
- `Threadsmith.Telemetry` and TUI/headless projections — bounded lifecycle, pressure, duration, and inspection output without content leakage.
- Configuration/bootstrap/example files — active-turn policy defaults and validation.
- Focused Context, Execution, ModelTooling, provider, persistence, architecture, and projection tests.
- User/operator/architecture documentation only after implemented observable behavior or durable contracts require it.

## 9. Ordered Tasks

1. Re-read the applicable DOX chain and portable C# guardrails; inspect current conversation loop, context assembler/compactor, evidence store, request estimator, cache/stateful providers, persistence, and projections.
2. Add a deterministic profiling fixture reproducing a long same-turn tool continuation and record pre-change round, wire-token, message-count, result-category, and completion baselines.
3. Freeze active-turn eligibility, pressure, reserve, summary, recent-group, failure/backoff, and provider-reset semantics in host-owned contracts and tests.
4. Implement complete continuation grouping and cut selection; prove every new group is seen verbatim once and sibling call/results are never split.
5. Add pre-sampling pressure assessment using the canonical request estimator.
6. Add or adapt the structured active-turn candidate provider and validator with bounded source references and no authority promotion.
7. Replace eligible prefixes with cumulative low-authority summaries plus exact recent groups; continue the same turn.
8. Integrate cache-family and stateful-continuation invalidation/restart behavior for OpenAI-compatible and native Codex providers.
9. Add lifecycle events, inspection/projection fields, diagnostics, and optional checkpoint persistence required for cancellation and resume safety.
10. Implement classified failure, bounded retry/backoff, emergency wire-fit compatibility, and end-to-end cancellation.
11. Profile residual result pressure and adopt per-tool projection reductions only if the predeclared materiality/usefulness gate passes.
12. Run focused tests, architecture tests, provider suites, solution build, formatting checks, planning-governance checks, and `git diff --check`.
13. Perform the DOX/documentation closeout and update this plan's status/current state when acceptance passes; do not edit completed milestone details.

## 10. Testing

Automated coverage must verify:

- no compaction occurs below pressure or without an eligible complete prefix;
- assessment occurs before every model call and includes frozen context, tools/schema, continuation, and output reserve;
- pressure triggers below the selected model's maximum input boundary;
- frozen host/repository/current-user/output-contract context remains byte-for-byte unchanged;
- a newly completed tool group is delivered verbatim at least once before eligibility;
- sibling tool calls and every matching result remain paired, ordered, and unsplit across cut selection and provider projection;
- newest retained groups remain byte-for-byte unchanged;
- candidate summaries satisfy schema/item/token bounds and cumulative version/source-range rules;
- fabricated, missing, stale, mismatched, sensitive, oversized, or authority-bearing candidate content is rejected without replacing the original continuation;
- successful compaction reduces the estimated request below target and the same turn continues to an answer or next legal tool round;
- repeated compaction is cumulative, bounded, deterministic for fixed candidates, and does not duplicate summaries or groups;
- complete original sanitized tool events/evidence remain available to audit and retention paths;
- OpenAI-compatible and native Codex projections preserve valid chronology after rewritten history and do not reuse incompatible continuation/cache identities;
- cancellation during generation, validation, checkpointing, or rebuild leaves no partial active summary;
- candidate failure/backoff does not loop every round and emergency wire-fit/failure behavior remains controlled;
- TUI and headless behavior remain equivalent, with bounded status/inspection output and no terminal types in engine contracts;
- repository-memory sensitivity and initial context selection from Plan 78 remain unchanged;
- fixed long-turn profiling shows bounded working-set behavior and lower repeated wire input without reducing final-answer correctness;
- optional per-tool projection changes, if any, pass separate usefulness, continuation, and provenance tests.

Focused verification should precede broad solution gates. Do not use a full-solution filtered test command.

## 11. Security/Permissions

Active-turn summaries are untrusted historical context. They cannot alter current host policy, user authority, tool eligibility, path safety, approval, mutation scope, hook/skill/MCP/extension authority, output schema, or sensitive-data routing.

Candidate generation must use only a model/profile allowed to receive the selected prefix's sensitivity. Never route sensitive results to an incompatible cheaper compaction model. Validate source references against host-owned messages, tool invocations, evidence, repository identity/revision, and invalidation state where applicable.

Compaction grants no new filesystem, process, network, Git, mutation, or persistence permission. Active-turn pressure, reserve, candidate-model, and failure policy are host/user-owned reliability settings; repository configuration and repository content cannot disable, delay, force, or weaken compaction or select a less-capable sensitivity boundary. Raw provider payloads, hidden reasoning, credentials, secret values, and unsanitized tool content remain excluded. Audit retention follows existing repository/session privacy and deletion contracts.

## 12. Observability

Emit bounded structured diagnostics and spans for:

- pre-sampling estimated input, pressure target, and trigger/no-trigger reason;
- eligible, compacted, and retained group/token counts;
- candidate provider/profile ID, duration, bounded retry count, and closed outcome;
- before/after request estimates and cache/continuation reset reason;
- summary schema/version/hash and source-range identifiers;
- cancellation, validation rejection, backoff, emergency reduction, and capacity failure classifications.

Do not emit summary bodies, raw tool results, prompts, hidden reasoning, paths beyond existing safe identifiers, provider payloads, credentials, or exception messages containing user/tool content. Existing raw model logging remains explicit opt-in and independently governed.

## 13. Migration/Compatibility

The feature is additive to provider-neutral request assembly. Existing sessions, archives, repository memory, and completed-turn summaries remain readable. When no active-turn policy/checkpoint exists, composition supplies validated defaults. Configuration layering remains host/user/repository governed and repository configuration cannot increase provider capacity or weaken sensitivity/authority validation.

Legacy active runs without an active-turn summary continue through the existing raw continuation path until the new pressure policy triggers. Providers that cannot continue from rewritten history must restart from the full compacted provider-neutral request; they must not silently reuse an incompatible response/conversation identifier. Existing emergency continuation reduction remains available during migration.

Any persistent checkpoint addition requires an additive schema migration, restoration tests, retention/deletion integration, and tolerant handling of unknown future summary versions. No completed milestone contract or durable archive record is rewritten.

## 14. Acceptance Criteria

- Every ordinary evidence-collection/planning model call is preceded by canonical complete-request estimation.
- Active-turn compaction triggers at configured pressure below the provider emergency maximum and only when a complete eligible prefix exists.
- Frozen authority, current user input, repository instructions, output contracts, and tool definitions are preserved exactly.
- Every new tool group reaches the model verbatim once; cuts preserve complete sibling call/result groups.
- A validated bounded structured summary replaces only the eligible older prefix, newest groups remain verbatim, and the same turn continues.
- Invalid, unsupported, sensitive-incompatible, oversized, or authority-bearing candidates leave the original continuation active and produce a controlled classified outcome.
- Rewritten history resets/re-establishes provider cache and stateful continuation safely for every compiled provider.
- Complete original sanitized tool execution remains auditable under existing retention/redaction policy; active summaries do not become durable memory automatically.
- Context inspection and telemetry explain pressure, cut range, retained groups, before/after estimates, outcome, and fallback without exposing content.
- A deterministic long-turn fixture demonstrates that model-visible continuation no longer grows monotonically to the model maximum while preserving final-answer correctness and host authority.
- No generic evidence rehydration, batching change, arbitrary exploration cutoff, or completed-milestone edit is introduced.
- Focused tests, provider tests, architecture tests, solution build, planning-governance checks, documentation/DOX closeout, and `git diff --check` pass.

## 15. Risks

- **Loss of task-critical detail:** show every result verbatim once, retain a recent raw window, require attributable structured summaries, and keep original evidence auditable.
- **Summary authority escalation:** project summaries as low-authority context and reject policy, permission, approval, mutation, or output-contract claims.
- **Broken provider chronology:** cut only complete groups and test every provider's call/result projection.
- **Cache/stateful continuation corruption:** reset continuation identity whenever history is rewritten; prefer a full compatible request over unsafe cache reuse.
- **Compaction recursion or oscillation:** use cumulative versions, stable source boundaries, minimum savings, and bounded failure/backoff policy.
- **Compactor request itself becomes oversized:** bound candidate input using group-aware selection and source-preserving projections.
- **Sensitive-data exposure:** require model compatibility with the prefix's sensitivity and preserve sanitization/redaction boundaries.
- **Cancellation leaves mixed state:** activate a summary only after generation, validation, optional persistence, and rebuild complete atomically.
- **Audit storage grows:** reuse existing retention/artifact policy; do not duplicate full tool bodies in summary checkpoints or telemetry.
- **Premature output shrinking harms investigation:** gate optional per-tool changes on measured residual pressure and usefulness tests.
- **Scope leaks into completed capabilities:** keep Plan 80 on Maintenance and leave Milestone 25, M7.4, and M19 detail contracts frozen.

## 16. Documentation

Planning adds this implementation document and one navigation row in `docs/implementation-plans/README.md`. It does not change milestone status, the completed Milestone 25 detail, dependency DAG, acceptance scenarios, manual procedures, or DOX ownership.

When implemented, update:

- `docs/operations/conversation-context.md` for active-turn pressure, inspection, cancellation, and failure behavior;
- `docs/user-guide.md` if context status or commands expose the behavior;
- `docs/architecture/event-catalog.md` for new public events;
- the applicable context/cache ADR only if implementation requires a new durable decision rather than the maintenance hardening described here;
- acceptance scenarios or manual tests only if their owned observable behavior/procedure changes;
- source/test DOX only when durable ownership or contracts change.

## 17. Open Decisions

Resolve before implementation code begins:

- Exact default pressure, output-reserve, minimum-savings, summary-budget, and retained-recent token values, including whether existing Plan 35 settings can be reused without changing their meaning.
- Whether the existing conversation compaction candidate provider can accept active-turn tool groups safely or needs a dedicated provider-neutral candidate contract.
- The precise low-authority message/content kind used to project an active-turn summary without masquerading as current user or system policy.
- Whether active-turn checkpoints must be durable for ordinary run recovery or can remain in memory while original events/evidence stay durable.
- The provider-specific boundary for abandoning OpenAI-compatible cache families and native Codex response continuation after history replacement.
- The minimum reproducible materiality and usefulness gate for any optional per-tool result projection reduction.
