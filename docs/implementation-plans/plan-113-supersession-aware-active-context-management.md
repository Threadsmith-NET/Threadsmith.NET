# Implementation Plan 113: Supersession-Aware Active Context Management

**Status:** Planned.

**Delivery track:** Maintenance — active-turn context efficiency and budget admission hardening.
**Prerequisites:** The implemented contracts from plans 51–55, 80, 84, 86, and 109; the existing `ModelRequestBudgetUsage` pre-dispatch admission path; ADR-12 and ADR-31. Plan 84 continues to own the observable `code_explore` source-deduplication capability and its remaining acceptance work. This plan owns the generalized active-turn projection and compaction changes and must not create a second visibility index beside Plan 84's current-request frontier.
**Reference implementation reviewed:** Pi Context Prune 1.4.0, inspected from the installed package and its source map. It is a behavioral reference, not a dependency or compatibility target.

## 1. Objective

Replace repeated raw active-turn tool-result replay with one host-owned context-management pipeline that:

1. detects exact supersession between model-visible source and symbol observations;
2. mechanically reduces structured results before paying for model-written compaction;
3. invokes model-written compaction only for residual information that deterministic projection cannot safely preserve; and
4. considers the execution budget before starting either the summarizer or the next ordinary model request.

The implementation must be general across tool rounds and repository sizes. It must not encode the motivating semantic-hybrid-retriever trace, guess relevance from tool names or prose, or introduce a collection of unrelated per-tool cleanup branches in `SessionApplication`.

Full sanitized results remain authoritative in the evidence store, durable tool events, and explicitly enabled raw exchange logs. The model-visible active context becomes a derived, inspectable projection with exact recovery references. Every new result is delivered verbatim at least once before an older representation may replace it.

## 2. Architectural Context

`ContextAssembler` governs the initial request: instructions, conversation memory, repository memory, selected evidence, tool schemas, and required state are reduced to the selected model's input budget. `SessionApplication` then freezes that result for the active turn. Later tool calls and results are appended through `ConversationLoopState` and do not re-enter ordinary context selection.

Plans 80 and 86 added active-turn compaction around that continuation. The current implementation:

- stores complete assistant-call/result groups;
- requires a group to reach a completed later request verbatim before it becomes compactable;
- triggers model-written compaction primarily from per-request context-window pressure;
- retains a recent raw-token window;
- replaces only an oldest complete group prefix with one cumulative summary; and
- uses blind request-local truncation only as an emergency capacity fallback.

Plan 84 separately derives exact model-visible `code_explore` ranges from the canonical request. Its repository/workspace/path/range/digest checks are the established proof mechanism for whether source remains visible. That mechanism must be generalized or reused, not duplicated by a new session-history cache.

The evidence store already retains the sanitized full body of successful and failed tool results. The active-turn state, however, associates most provenance at group level rather than retaining a context projection and evidence handle for each correlated call/result pair. Consequently, the host cannot currently reduce one result, subtract a later exact source range from an earlier search, or expose a general recovery path to the main conversation.

Pi Context Prune demonstrates five useful strategies:

- keep original tool results in durable session state;
- maintain a monotonic attempted/summarized frontier;
- alter only future model context;
- insert a smaller summary only when it beats the raw result; and
- expose a recovery tool keyed by compact references.

Threadsmith must not copy Pi's weaker boundaries: character-count admission, fixed 2,000-character summarizer inputs, unrestricted request-message filtering, removal of tool results without host protocol validation, or extension authority to rewrite arbitrary model context. In the observed Pi run, batches were queued in agentic-auto mode but no `context_prune` call occurred. The displayed Pi and Threadsmith context sizes were already close, and no comparison of cumulative provider input was made. That run therefore establishes neither lower total input use nor a pruning benefit.

## 3. Scope

- Retain per-result active-turn records inside existing complete chronological groups.
- Attach bounded, host-owned deterministic projection metadata to tool results before typed structure is discarded.
- Reuse and generalize the existing request-local visible-source frontier for exact source coverage and invalidation.
- Define exact supersession algebra over repository/workspace identity, source path, range, content digest, semantic identity, and generation.
- Implement compact deterministic projections for text search, symbol declaration lookup, exact file reads, and `code_explore`.
- Preserve unmatched, uncovered, truncated, failed, ambiguous, omitted, and continuation state rather than presenting a compact result as complete.
- Preserve native assistant tool-call/result correlation and sibling ordering when replacing an individual delivered result projection.
- Add a single active-context planner that orders deterministic reduction, model-summary fallback, request rebuilding, cache/history invalidation, and final admission.
- Trigger the planner for existing context pressure and when the next request cannot be admitted by the remaining execution budget.
- Preflight a model-summary attempt together with the bounded subsequent ordinary request; do not spend the remaining budget on a summary that cannot leave an admissible continuation.
- Retain full sanitized originals and provide a progressively advertised, read-only evidence-recovery tool for results referenced by active summaries or deterministic receipts.
- Allow built-in and extension tools to return an optional bounded compact projection through host-owned DTOs; the host validates and maps extension data before it can affect context.
- Preserve cancellation, sensitivity, trust, cache/stateful-continuation, inspection, event, and usage-accounting contracts.
- Validate the result with one deterministic replay of a long exploration trace and one bounded real-provider comparison, rather than a large prompt-evaluation matrix.

## 4. Non-Scope

- No fuzzy similarity, embeddings, LLM relevance score, filename-only inference, or special case for the motivating repository/question.
- No general extension hook that receives or mutates `ModelStreamRequest`, session history, host policy, or arbitrary model messages.
- No requirement that every tool provide a deterministic compact projection. Unknown, external, or opaque results remain raw until the existing validated model-summary path can replace an eligible complete prefix.
- No deletion of evidence, tool events, archived messages, audit artifacts, or configured raw model logs.
- No claim that a model-written summary is lossless or equivalent to exact source.
- No compaction of current user input, host/system instructions, approval state, mutation authority, pending calls, never-delivered results, or output contracts.
- No new relevance-scoring framework, transcript database, vector store, provider-specific context manager, or second active-turn state machine.
- No automatic model-controlled pruning tool in the first delivery. Host budget/context policy remains authoritative; a later agentic trigger may be added only from measured need without replacing host admission.
- No blanket reduction after every tool round. Rewrites occur only during an admitted compaction decision so unchanged cacheable prefixes remain stable below pressure.
- No proliferation of one test per permutation or a new test project/harness.

## 5. Current State

### 5.1 Active-turn representation

`ActiveTurnContinuationGroup` owns complete provider-neutral messages, group-level sources, file lists, an estimate, sensitivity, and `WasDeliveredVerbatim`. `ConversationLoopState` temporarily correlates calls and results, then discards the per-call association except for message IDs and bounded source references when it commits the group.

The group boundary is correct for native-tool protocol and model-summary cuts. It is too coarse for deterministic result projection. The implementation must add per-result detail within the group rather than replace the group abstraction.

### 5.2 Existing reduction

`ActiveTurnCompactionCutSelector` retains a newest raw window and selects an oldest delivered prefix. `ModelActiveTurnCompactionCandidateProvider` creates a cumulative Markdown summary, and the host activates it only after validation and positive canonical-request savings.

`BoundContinuationMessages` is a final compatibility mechanism. It orders older delivered results by size and binary-searches a prefix character count until the request fits. It does not understand source identity, preserve structured residue, persist the reduced representation, or prevent the same raw payload from being reconsidered later.

### 5.3 Existing source proof

`ModelVisibleSourceFrontierBuilder` already proves exact current-request `code_explore` source visibility using bounded ranges and digests. It admits the exact emitted range of a partial result when its range digest and delivered lines are present; omitted lines never count as visible. It fails closed for unverifiable, malformed, changed, cross-workspace, or compacted-away source. This proof should become the source-coverage component of the generalized active-context ledger.

### 5.4 Existing budget admission

`ModelRequestBudgetUsage.Start` performs a non-mutating budget check before provider dispatch and charges the call only after admission. Active-turn compaction currently decides from context pressure before the final prepared ordinary request exists, while summary calls accrue against the same budget after execution. There is no single planner proving that summary work plus the resulting ordinary request is a feasible use of the remaining budget.

### 5.5 Existing extension boundary

Extensions can register tools and model-preference contributors. They cannot observe all host tool results, read the session branch, call the configured model through a host summarization facade, or filter future request messages. That boundary should remain. Extension-produced results can participate by returning an optional host-shaped projection as part of their own result, not by gaining Pi-style arbitrary context interception.

### 5.6 Measurement terminology

Keep four measurements separate throughout implementation and review:

- **Request context size** is the input carried by one canonical model request. This is what Pi's context display was showing, and Threadsmith was already close in the observed comparison.
- **Cumulative provider input** is the sum of input charged or reported across every ordinary and summary model call. Replaying a moderate context over several rounds can make this much larger than any one request.
- **Execution-budget token use** is the host's cumulative input-plus-output accounting under its configured budget. It is not a synonym for context-window occupancy.
- **Provider cache use** is reported separately under the provider's declared included/additional semantics. A cache read does not reduce logical context size and may or may not reduce billed input cost.

The motivating failure is cumulative budget exhaustion and repeated evidence replay, not proof that Threadsmith's single-request context is generally oversized relative to Pi. Every baseline, inspection projection, test fixture, and A/B report must label these measurements explicitly and must not infer one from another.

## 6. Proposed Design

### 6.1 One canonical active-context ledger

Extend each committed continuation group with ordered `ActiveTurnToolExchange` records. Each record contains only host-owned data:

- the correlated assistant call and tool-result messages;
- tool call and invocation IDs;
- evidence ID for the full sanitized result;
- tool/schema identity and version;
- result success, truncation, and error state;
- bounded provenance sources;
- raw model-visible token estimate;
- optional validated deterministic projection; and
- delivery and active-projection generation metadata.

The existing group remains the atomic protocol and model-summary boundary. The exchange becomes the deterministic-reduction boundary. Rendering a group always emits one valid assistant call set followed by its correlated results in call order, regardless of whether each result is raw, compact, or a superseded receipt.

Do not create a parallel durable transcript. Full content remains in the evidence store and existing audit records. The ledger holds bounded identities and projections required to compile the next request.

### 6.2 Projection contract, not tool-name switches

Add a small optional result projection contract at the tool boundary. A proposed internal shape is:

- `ToolContextProjection` — schema version, compact model-visible content, completeness, claims, and recovery eligibility;
- `ToolContextClaim` — closed claim kind, stable identity, provenance, and optional exact content identity;
- `ToolContentIdentity` — repository/workspace, normalized path, range, digest, and source generation;
- `ToolContextProjectionCompleteness` — complete alternate representation, partial index, or opaque.

The names may be refined during implementation, but these meanings are binding.

Built-in tools create projections from typed results while their structure is still available. The tool pipeline sanitizes and bounds projection text alongside ordinary model-visible output, validates that every claim is rooted in the tool's returned provenance, and copies only host-owned DTOs into execution state. Core orchestration consumes the contract through a registry/interface; it must not switch on IDs such as `search`, `read_file`, or `code_explore`.

An extension tool may return the equivalent optional abstraction DTO with its `ExtensionToolResult`. Extension claims are untrusted input. The runtime maps them to host DTOs, enforces size/count/schema limits, and permits only claims rooted in that invocation's validated sources. An extension cannot claim that another tool's result is superseded and cannot retain a live extension type in durable or active state. Absence or rejection of a projection produces the safe opaque fallback.

### 6.3 Exact supersession algebra

Supersession is a deterministic coverage operation, not a relevance judgment.

A newer observation may cover an older observation only when all applicable identity fields agree:

- same repository and workspace;
- same normalized source identity;
- compatible source generation;
- exact content digest where source text is involved;
- a newer range fully contains the older range, or the newer structured identity exactly equals the older identity; and
- the replacement representation is present in the same canonical request being compiled.

Partial coverage subtracts only the covered portion. The older projection retains unmatched paths, uncovered ranges, negative results, truncation/omission notices, ambiguity, errors, and continuation information. A newer file read can therefore remove duplicated snippets from an older search without erasing the fact that other matches or unsearched files remain.

Digest absence, digest mismatch, edits, invalidation, repository/workspace mismatch, metadata eviction, malformed projection data, or uncertain range arithmetic fail closed to retaining the older material. Text normalization or approximate similarity never proves supersession.

The visibility proof comes from the canonical request projection being built, not from the fact that a tool once ran. Generalize Plan 84's current-request frontier in place, or layer a compatibility view over one generalized ledger; do not maintain two authorities that can disagree.

### 6.4 Deterministic projection rules

The common planner operates only on claims and projection completeness. Tool-specific knowledge stays in the projector that owns the typed result.

| Result family | Compact representation | Supersession behavior |
|---|---|---|
| Text search | Query scope and options, completion/truncation state, and unmatched path/range identities; snippets only for ranges not covered elsewhere in the compiled request | Exact later source with matching digest removes only its covered snippet/range; remaining matches and coverage gaps survive |
| Symbol lookup | Stable symbol/declaration identity, kind/signature, declaration path/range, ambiguity and truncation state | An exact declaration read may replace duplicated declaration source, but the symbol identity and unresolved alternatives remain |
| Exact file read | Path, exact delivered range, content digest/generation, completeness/truncation state, and recovery evidence reference; retain any noncovered exact spans required by the projection | A later exact visible range with the same digest covers equal/subranges; changed or uncertain content is never suppressed |
| `code_explore` | Structured unique findings/relationships, exact non-superseded source ranges, coverage gaps, omitted targets, and continuation data | Deduplicate stable finding identities/content digests; subtract only exact source coverage proved by the generalized frontier |

“Relevant lines” must not be guessed after the fact. A file result's requested/delivered range and any tool-produced structured source ranges are valid citations. If a tool supplies only an opaque prose body, it has no deterministic line-selection contract and remains raw or enters model-written summary compaction.

Compact projections include an evidence reference when full recovery is permitted. They state that the content is historical and may be stale; they never imply current repository truth after invalidation.

### 6.5 Active-context planning pipeline

Introduce one cohesive `ActiveContextPlanner` in `Threadsmith.Context` (name refinable) and call it once before each ordinary model dispatch. It receives the frozen context identity, tool inventory estimate, continuation ledger, active summary, selected-model limits, current execution budget facade, and invalidation generations. It returns a closed host-owned decision:

- unchanged;
- deterministic rewrite;
- model-summary candidate required;
- capacity/budget cannot be satisfied; or
- invalid state.

The planner executes these phases in order:

1. Render the current canonical continuation and estimate the complete request.
2. Determine pressure from the existing context-window target or failed execution-budget admission for the bounded next request.
3. If there is no pressure, return unchanged; do not churn history or provider caches.
4. Select only complete previously delivered material. Compile deterministic compact/superseded projections and estimate the rebuilt request.
5. If deterministic projection satisfies both context and budget admission, atomically activate it without a summarizer call.
6. Otherwise, pass only the residual eligible prefix to the existing model-written cumulative summary path. The summary input may use bounded deterministic indexes, but the authoritative full sanitized evidence remains available to candidate construction and recovery.
7. Preflight summary generation plus the bounded post-summary ordinary request as one feasible budget decision before calling the summary provider.
8. Validate positive savings, exact covered boundaries, source references, authority, sensitivity, and final request admission.
9. Activate all changes as one history rewrite, increment `HistoryRewriteGeneration` once, invalidate incompatible opaque continuation state, and dispatch the final prepared request.

A failed, cancelled, oversized, invalid, or non-saving candidate leaves the prior active projection unchanged. The existing emergency truncator remains only as a last compatibility backstop and should consume the same per-result records so it cannot contradict the planner.

### 6.6 Budget-aware trigger and admission

Use budget arithmetic, not a percentage guessed from the motivating trace.

For an ordinary request, form a conservative admission delta from:

- exact prepared wire-input estimate;
- the request's configured maximum output or effective output reserve;
- one model call;
- estimated cost when profile pricing is known; and
- a provider timeout bound for wall-clock admission only when the existing budget/timeout contracts can express a reliable bound.

The check is non-mutating. Actual calls, reported tokens, cost, and elapsed time remain the accrued authority. Unknown cost or duration must remain unknown rather than becoming zero-confidence invented precision; the existing hard dimensions still apply.

When the ordinary request is not admissible:

1. try zero-provider-cost deterministic projection;
2. re-estimate and admit if it now fits;
3. otherwise prepare, but do not execute, the summary request;
4. check a combined conservative delta for the summary call and the maximum-size post-summary ordinary call;
5. execute the summary only when that combined path is admissible; and
6. recheck the actual rebuilt ordinary request after summary usage has accrued.

If no path is admissible, return controlled budget exhaustion before any provider call. Do not consume the last usable call/tokens on a summary that cannot lead to the next request. Call-count exhaustion must bypass model summarization but may still permit deterministic projection when an already-admissible ordinary call remains.

The existing 75% context-pressure trigger remains an independent cache-aware proactive trigger. This plan adds no new arbitrary token percentage, number of files, or number of rounds. The two objective triggers are therefore: model-context pressure, or inability to admit the bounded next request.

### 6.7 Monotonic frontier and retry behavior

Adopt Pi's useful monotonic-frontier property without its message-filter authority. Record the last deterministic/model-summary source boundary attempted, input identities, outcome, and projection generation. Do not retry an identical no-savings or oversized range every round. New eligible material, invalidation, budget/model change, configuration change, or a different candidate boundary permits another attempt.

This frontier replaces repeated best-effort reconsideration; it does not make failed material disappear. Raw results stay visible until a replacement is successfully activated.

### 6.8 Exact evidence recovery

Promote the existing child-agent evidence-read pattern into a shared host-owned service and add a main-conversation `read_evidence` tool. Advertise it only when the current canonical request contains compact projections or summaries with recoverable evidence IDs; this avoids paying its schema cost on unrelated requests.

The tool:

- accepts one evidence ID already admitted to this session/run and exposed in the current projection;
- returns the stored sanitized original with its provenance and stale/invalidation state;
- cannot fetch arbitrary session evidence or cross repository/session boundaries;
- performs no repository, process, network, mutation, or secret operation;
- labels recovered content as historical evidence, not a fresh read; and
- participates in ordinary result bounding and active-context management.

Recovery is an escape hatch, not an excuse for poor compact projections. Repeated recovery of the same result is an observable signal that its projector or summary omitted necessary working state.

### 6.9 Extension participation

Additive extension support is limited to an optional compact projection returned with an extension tool's own result. The host owns:

- activation and invocation leases;
- projection sanitation, limits, and source validation;
- supersession decisions;
- context triggers and request rebuilding;
- evidence storage and recovery authorization;
- model-summary calls and budgets; and
- durable DTO mapping and unload safety.

Do not expose Pi's general `context` event or session-branch mutation API. An extension that wants a specialized compact representation can supply one; it cannot see or rewrite unrelated model messages. Existing extensions remain compatible and simply use opaque fallback behavior.

### 6.10 Child loops and other consumers

Implement the projection algebra independently of `SessionApplication`, then reuse it in the ordinary conversation loop first. `ChildAgentHistory` already uses the active-turn compaction contracts and has an evidence-recovery tool; adapt it to the shared projection types only when doing so removes duplication without changing child authority or result schemas.

Mutation proposal, correction, and approved-plan execution loops enter scope only if inspection proves they use the same complete read-only tool continuation contract. Do not copy the planner into each loop. Unsupported consumers retain their current behavior and are recorded explicitly.

## 7. Public Contracts

Prefer internal contracts in `Threadsmith.Tools`, `Threadsmith.Context`, and `Threadsmith.Execution`. Public additions are justified only at subsystem or extension boundaries.

Expected host-owned contracts:

- `ActiveTurnToolExchange` — per-call/result identity and projection state inside a complete group;
- `ToolContextProjection`, `ToolContextClaim`, and `ToolContentIdentity` — bounded deterministic projection and exact coverage facts;
- generalized current-request source/claim frontier compatible with Plan 84;
- `ActiveContextPlan` and closed outcome/reason enums;
- compaction attempt-frontier/checkpoint additions;
- an admission estimate that distinguishes wire input, reserved output, call, cost, and wall-clock confidence; and
- context inspection fields for raw, deterministically reduced, superseded, summarized, recoverable, and retained tokens/results.

Expected additive extension contracts:

- optional `ExtensionToolContextProjection` and claim DTOs on `ExtensionToolResult`, using primitive/serializable fields only;
- explicit schema/count/character bounds; and
- no interface that exposes model requests, session stores, evidence stores, or host implementation services.

All persisted or projected state is host-owned, versioned where durable, provider-neutral, and free of Roslyn, provider SDK, terminal, persistence implementation, or extension implementation types.

## 8. Project/File Changes

Expected areas, refined after implementation inspection:

- `src/Threadsmith.Tools/ToolContracts.cs` and `ToolInvocationPipeline.cs` — optional typed projection production, sanitation, validation, and propagation.
- Built-in search/read/symbol tools and `CodeExploreTool`/output formatting — focused projector implementations over typed results.
- `src/Threadsmith.Context/ActiveTurnCompaction.cs` — projection/claim contracts, exact coverage algebra, planner, attempt frontier, candidate input integration, and validation reuse.
- Existing Plan 84 frontier implementation — generalize in place while preserving its code-explore compatibility and inspection behavior.
- `src/Threadsmith.Execution/SessionApplication.ConversationLoop.cs` — retain per-result records, invoke the planner, activate one atomic rewrite, and progressively advertise recovery.
- `src/Threadsmith.Execution/ModelRequestBudgetUsage.cs` and budget contracts only as necessary — shared conservative admission estimates and no-dispatch outcomes.
- Existing child evidence retrieval service/tool — extract reusable host-owned lookup/authorization without merging child and parent authority.
- `src/Threadsmith.Extensions.Abstractions` and `Threadsmith.Extensions.Runtime` — optional projection DTO and validated mapping.
- Existing context, execution-orchestration, model-tooling, extension, architecture, and caching tests — update/consolidate rather than creating a new suite.
- Context/tool/extension operations documentation, prompt catalog/reference files if model-facing assets change, and context inspection documentation.

Do not place tool-specific JSON parsing or reduction switches in `SessionApplication`. Do not introduce a new project.

## 9. Ordered Tasks

1. Capture a sanitized baseline from the motivating long exploration log: each request's canonical context size, tool-schema cost, each result's raw replay contribution, cumulative provider input, cumulative execution-budget input-plus-output, cache counters under their declared semantics, and whether compaction was assessed. Retain only metadata and synthetic content needed for regression replay. Do not use the Pi context display as cumulative-token evidence.
2. Inventory every current producer/consumer of `ActiveTurnContinuationGroup`, the Plan 84 visible frontier, `ToolInvocationResult`, evidence IDs, emergency reduction, child history, and provider history generations. Record unsupported continuation loops before editing.
3. Freeze the invariants and DTO shapes in Sections 6–7. Obtain an adversarial review specifically for duplicate state, provider protocol, extension authority, cache invalidation, and whether any proposed field can be derived instead of stored.
4. Add per-result exchange capture and evidence linkage while preserving byte-for-byte current request rendering. This is the first independently testable checkpoint.
5. Generalize the existing visible frontier and implement exact coverage/subtraction as a pure deterministic component. Do not add tool-specific behavior yet.
6. Add the four typed projectors through one registration/interface path. Verify that unknown and rejected projections remain opaque.
7. Implement the active-context planner and replace scattered assessment/reduction ordering with its closed decision. Retain the current model-summary provider and validator as the residual fallback rather than adding another summarizer.
8. Integrate conservative execution-budget admission. Prove no summary or ordinary provider call starts when its required continuation path is infeasible.
9. Add monotonic attempt-frontier behavior and one atomic history/cache generation change per activated plan. Reconcile the emergency reducer with the same ledger.
10. Add progressively advertised evidence recovery by reusing the existing evidence-read service semantics.
11. Add the optional extension result projection DTO and runtime mapping. Exercise unload/replacement with only copied host DTOs retained.
12. Update inspection, events, metrics, and sanitized diagnostic export. Do not add source bodies, queries, result text, or credentials to normal telemetry.
13. Consolidate existing Plan 80/84 tests and add only the focused scenario matrix in Section 10.
14. Run the sanitized trace replay, then one bounded real-provider before/after comparison with identical model, tools, repository state, question, and cache treatment. Inspect answer support and recovery behavior, not only total tokens.
15. Run focused suites, architecture tests, solution build/formatting, planning-governance checks, and an adversarial review through the real ordinary conversation entry point.
16. Update owned documentation and record implementation evidence in this plan. Do not edit completed plans 80 or 86 except to repair a factual contradiction.

## 10. Testing

### 10.1 Test-budget rule

Use the existing `Plan80ActiveTurnCompactionTests`, `Plan84VisibleSourceFrontierTests`, execution-orchestration, model-tooling, extension, caching, and architecture suites. Consolidate or replace obsolete narrow tests when a table-driven scenario covers the same invariant. Do not create a new test project, snapshot framework, combinatorial matrix, or prose-exactness suite.

The implementation target is no more than ten net-new test methods across the repository. Prefer `[Theory]` data rows and scripted multi-assertion scenarios. Exceed that target only when implementation discovers a materially distinct safety boundary that cannot be covered clearly in the existing scenarios; record the reason in this plan before adding it. Test quality and entry-point coverage matter more than assertion count.

### 10.2 Focused deterministic scenarios

1. **Coverage algebra theory:** exact duplicate, contained range, partial overlap, digest mismatch, invalid generation, and cross-workspace cases prove precise subtraction and fail-closed retention.
2. **Projector contract theory:** representative search, symbol, file-read, and `code_explore` results produce bounded compact projections; truncation, ambiguity, gaps, and continuations survive; an opaque tool remains unchanged.
3. **First-delivery and protocol scenario:** a sibling batch is delivered raw once; later pressure rewrites selected individual results while preserving call/result correlation, order, and never-delivered results.
4. **Planner fallback scenario:** deterministic projection alone resolves one pressure case; a second case requires the existing model summary; a third no-savings candidate leaves history unchanged and advances only the attempt frontier.
5. **Budget-admission theory:** ordinary request fits; deterministic reduction makes it fit; summary plus continuation fits; summary would fit but continuation would not; call budget is exhausted; and cancellation occurs before dispatch. Assert provider invocation counts, actual accrual, and controlled outcomes.
6. **Recovery and extension-boundary scenario:** a compact reference can recover only its admitted current-session evidence; stale/cross-session IDs fail; a valid extension projection is copied and bounded; invalid claims fall back to opaque; unloading retains no extension object.
7. **History/cache scenario:** one activated multi-result plan increments the rewrite generation once, invalidates incompatible continuation state, recomputes the visible frontier, and leaves unchanged requests generation-stable.

Update existing validation, retry, sensitivity, summary-profile, and activity tests only where contracts change; do not duplicate them under Plan 113 names.

### 10.3 Trace replay and live check

Create one synthetic deterministic trace shaped like the observed exploration: an initial broad result, overlapping exact read, redundant symbol/search routing results, and a later multi-file read batch. It must contain no proprietary source. Assert:

- superseded bytes disappear only after replacement evidence is present;
- unmatched routing/source facts remain;
- deterministic reduction runs before model summarization;
- the final request is admitted or fails before dispatch;
- full evidence remains recoverable; and
- total replay tokens are lower than the unchanged baseline.

Run one real-provider A/B check after deterministic tests pass. Keep the task, repository revision, model/profile, tool inventory, settings, and cache order recorded. Report per-request context size separately from cumulative provider input and execution-budget use. Also compare output tokens, model and summary calls, cache reads under their reported semantics, elapsed time, recoveries/repeated reads, and answer support. This is diagnostic evidence, not a statistical benchmark or a license to add trace-specific rules.

## 11. Security/Permissions

- Tool content, compact projections, summaries, and recovered evidence remain untrusted model data and cannot grant authority.
- Projection claims are accepted only from host validation against the invocation's actual provenance and current repository/workspace generations.
- Extension projections are sanitized, bounded, copied to host DTOs, and incapable of referencing unrelated tool results authoritatively.
- Recovery is restricted to evidence IDs explicitly exposed in the current canonical projection for the same session/run and allowed repository identity.
- Compaction never changes approval, trust, mutation, process, network, secret, skill, hook, MCP, or tool-availability policy.
- Sensitive content may use a summarizer only when the selected candidate profile permits it; deterministic projection cannot declassify content.
- Cancellation propagates through projection, summary preflight/execution, evidence recovery, persistence, and request rebuild.

## 12. Observability

Extend existing context inspection and content-free events with:

- pressure reason: context, execution-budget admission, or both;
- raw and final complete-request estimates;
- raw, deterministic, superseded, summary, and retained result/token counts;
- exact/partial/rejected supersession counts by closed reason;
- opaque-result count;
- recovery-reference count and recovery invocations;
- summary avoided because deterministic projection sufficed;
- summary rejected because the combined continuation path was infeasible;
- attempt-frontier boundary/outcome;
- history rewrite and visible-frontier generations; and
- actual model/summary usage, cost, duration, and cache counters when reported.

Normal logs and telemetry contain IDs, counts, hashes, closed reasons, and durations only. Source text, tool arguments, query text, result bodies, summaries, user content, and credentials remain excluded unless the user explicitly enables the existing raw diagnostic facility.

## 13. Migration/Compatibility

- Existing sessions and tool results have no deterministic projection metadata and therefore use the opaque fallback; they remain readable and safe.
- New optional fields are versioned and additive. Unknown versions fail closed to raw retention or model-summary eligibility.
- Existing extensions compile and run unchanged. The optional extension projection is not required.
- Existing evidence retention and deletion policy remains authoritative. A missing/expired body makes recovery unavailable without invalidating the historical compact receipt.
- Existing summary checkpoints remain readable. The first new rewrite rebuilds the generalized frontier and records the new projection generation.
- Providers without caching or stateful continuation continue with complete stateless requests. Compatible cache prefixes remain unchanged below pressure.
- The current emergency reducer remains during migration and is removed or narrowed only after the new planner covers its capacity cases through the same entry point.

## 14. Acceptance Criteria

- Context Assembly and active-turn management form one explainable pipeline: frozen governed context plus a separately optimized continuation, both included in the final canonical estimate.
- Inspection and evaluation distinguish per-request context size, cumulative provider input, cumulative execution-budget use, and provider-cache counters; no result claims that the Pi trace demonstrated lower cumulative input.
- Exact later source removes only provably duplicated older material; mismatches and uncertainty retain it.
- Search, symbol, file-read, and `code_explore` reduction is produced by registered typed projectors, with no core tool-ID switch and no parsing of arbitrary prose for relevance.
- Every new result is delivered verbatim at least once before it can be compacted, and native tool-call/result protocol remains valid after per-result rewrites.
- Deterministic projection runs before model-written compaction and can avoid a summary call entirely.
- Model-written compaction remains the bounded residual fallback, not a competing context subsystem.
- A summary call does not start unless its conservative combined path can leave an admissible ordinary request.
- An ordinary model request does not start when its bounded admission delta exceeds the remaining execution budget.
- Full sanitized originals remain in existing evidence/audit storage and are recoverable only through authorized current-projection references.
- Below pressure, canonical messages and history generations remain unchanged, preserving cache stability.
- Invalid/cancelled/no-savings projection or summary attempts leave the previous active context intact and do not loop on the identical frontier.
- Extension tools can optionally contribute bounded self-projections without receiving arbitrary context or mutation authority, and unload tests retain no extension object graphs.
- The focused suites, architecture tests, solution build, formatting, planning checks, sanitized trace replay, real-entry-point adversarial review, and bounded live comparison pass.
- The implementation stays within the Section 10 test strategy or records a concrete safety-boundary reason for exceeding it.

## 15. Risks

- **A compact representation drops needed detail.** Require first verbatim delivery, preserve incomplete/negative state, provide exact recovery, and treat opaque output as non-reducible.
- **Supersession hides changed source.** Require exact identity/digest/generation and fail closed on uncertainty.
- **Projection metadata becomes another unbounded context.** Bound claims and compact text at the tool pipeline; eviction causes harmless raw retention rather than optimistic suppression.
- **Frequent rewrites destroy provider cache value.** Plan only under existing context pressure or hard budget admission pressure, batch all changes into one generation, and keep below-pressure requests unchanged.
- **A summarizer consumes the budget needed to continue.** Preflight the combined bounded path and recheck after actual usage.
- **Tool-specific behavior leaks into orchestration.** Keep typed projector registration at the tool boundary and make the planner consume only common claims/completeness.
- **Two visibility indexes disagree.** Generalize Plan 84's frontier in place and enforce one canonical-request authority.
- **Recovery becomes a way to read arbitrary old data.** Authorize only currently exposed evidence IDs in the same session/run and preserve invalidation/staleness labels.
- **Extension claims spoof host evidence.** Root claims in validated result provenance and deny cross-result supersession authority.
- **The change grows into a framework.** Reuse the existing group, evidence, compactor, estimator, budget, and frontier components; add one planner and one small projection contract, not parallel lifecycles.
- **Tests grow mechanically with permutations.** Use the bounded table-driven scenarios in Section 10 and delete superseded narrow tests.

## 16. Documentation

This planning change adds this document and one navigation row only. Do not modify acceptance scenarios, manual procedures, completed milestone details, or completed plans now.

During implementation, update:

- `docs/operations/conversation-context.md` for deterministic projection, summary fallback, recovery, triggers, and failure behavior;
- `docs/operations/tools.md` for the progressively advertised recovery tool and tool projection ownership;
- extension-authoring documentation for the optional projection DTO and its authority limits;
- context inspection/user documentation for the new counts and reasons;
- prompt operations/reference files in the same change if any prompt filename, purpose, or token contract changes; and
- acceptance/manual documentation only if a stable observable user workflow changes.

Do not restate implementation status outside this active plan.

## 17. Open Decisions

Resolve these during Task 3 before implementation changes:

- Whether the generalized frontier keeps the current public `ModelVisibleSourceFrontier` name with additive claim types or introduces a broader internal name plus a compatibility projection for Plan 84. There must be one underlying authority either way.
- Whether the recovery tool's final identifier is `read_evidence` or a name aligned with the existing child-agent evidence tool. It must be progressively advertised and use the shared host service.
- The smallest extension DTO that can express a compact alternate representation and self-owned provenance claims without exposing the internal supersession algebra.

These decisions may change names and packaging, but not the first-delivery, exact-proof, deterministic-first, combined-budget-admission, recovery, host-authority, or bounded-testing requirements above.
