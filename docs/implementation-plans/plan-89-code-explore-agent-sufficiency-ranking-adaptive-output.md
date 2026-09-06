# Implementation Plan 89: Code Explore Agent-Sufficient Presentation, Ranking, Adaptive Output, and Availability

**Status:** Planned.
**Delivery track:** Maintenance — `code_explore` agent-usability, relevance, and availability hardening
**Strategy source:** Shared Context §A.1, §A.2, §A.3, §A.5, §C, §E, and §G; Scenario AO; Plans 81–85
**Prerequisite plans:** Plans 81–85 production contracts must remain intact. Capture a current Scenario AO / fixed-task baseline before implementation so ranking, presentation, and budget changes can be compared rather than tuned by anecdote.

## 1. Objective

Make `code_explore` feel sufficient to a coding agent on ordinary C# architecture, behavior, flow, and implementation questions: when the tool has usable source, the model should understand that it already has current line-numbered source and should not immediately fall back to `read_file` or broad search for the same files. When the tool cannot provide source for an expected, recoverable reason, it should return a bounded, success-shaped result with concrete next steps rather than an internal-looking failure that teaches the model to abandon the tool.

This plan improves four connected areas:

1. an agent-tuned presentation layer for source guarantees, back-references, omissions, continuations, and next actions;
2. stronger natural-language ranking and source allocation using CodeGraph-inspired retrieval lessons adapted to Roslyn and Threadsmith contracts;
3. repository-size-aware output defaults and presentation verbosity under the selected model/tool budget;
4. recoverable “not available” responses for expected workspace or semantic-readiness gaps, while preserving fail-closed security and argument validation.

The goal is fewer redundant `search`/`read_file` rounds, fewer repeated overlapping `code_explore` calls, equal or better answer correctness, and no weakening of host-owned authority, current-source identity, trust, path, or mutation boundaries.

## 2. Architectural Context

Plans 81–85 already provide the semantic substrate:

- exact symbol, stable-id, and C# path anchors with current source and digests;
- Roslyn-backed multi-anchor flow, dispatch branches, boundaries, and blast radius;
- deterministic natural-language discovery, candidate summaries, and source allocation;
- context-proven source-range deduplication with exact back-references;
- associated prompt/configuration/project artifacts as bounded supplements to the C# semantic spine.

Those contracts are strong for correctness and audit, but the current model-facing shape remains mostly a structured DTO. A model may not infer that returned `FileSections` are equivalent to a fresh read, may misread `Omissions` as failure rather than an exact follow-up list, or may retry with lower-signal tools after a recoverable environment condition.

The local repository at `C:\source\repos\codegraph` is available solely as a functional reference for observable agent behavior. Useful presentation/retrieval lessons include:

- state explicitly when source is verbatim, current, line-numbered, and equivalent to a read already performed;
- avoid pointer-only or omission-only responses when source can safely be restored;
- replace withheld repeated source with precise back-references, not silence;
- return success-shaped guidance for expected “not indexed/not available” conditions, but clean errors for security refusals;
- adapt output budgets by project size while staying under inline/result caps;
- seed named symbols and pinned files before fuzzy matching;
- down-weight weak, generated, test, or declaration-only matches instead of blindly hard-excluding them;
- use relative relevance floors and proportional byte allocation so file size does not dominate source budget.

Threadsmith must not copy, port, reverse engineer, depend on, or target compatibility with CodeGraph source, constants, schemas, prompts, internal names, tests, or implementation structure. This plan adapts the observed product lessons to Threadsmith’s Roslyn-backed, host-governed DTO contracts.

## 3. Scope

- Add bounded model-facing presentation guidance to `code_explore` results while preserving host-owned structured DTOs as the authority.
- Make result summaries and per-section guidance clearly state what source is current, complete, partial, already visible, omitted, or available by exact continuation.
- Add “next action” hints that prefer exact `code_explore` continuations and warn against redundant reads only when the host has actually returned complete current source for that file/range.
- Improve natural-language candidate and file ranking with deterministic Roslyn evidence: path pinning, named-symbol seeding, kind weighting, weak-symbol usage corroboration, type/container context, co-location, graph connectivity, generated/test focus exceptions, relative floors, and stable backfill.
- Allocate source bytes by relevance and usefulness instead of file size or simple rank order; ensure every returned source section is useful or replaced by an exact continuation/pointer.
- Compute repository-size scale from the current semantic workspace/catalog and apply adaptive default output envelopes, metadata verbosity, and suggested follow-up call count under model/tool caps.
- Return recoverable, success-shaped `code_explore` results for expected availability misses such as no opened workspace, semantic workspace not ready enough, no compiled projects, or no matching declarations.
- Preserve errors for malformed tool arguments, security/path/policy refusals, cancellation, unauthorized trust state, unexpected internal failures, and any condition where a success result would mask a host problem.
- Add focused tests, evaluation fixtures, telemetry counters, and documentation updates for the changed model-visible behavior.

## 4. Non-Scope

- No multi-language graph, external index service, embeddings, provider reranking, or provider-owned retrieval memory.
- No copy of CodeGraph algorithms, constants, prompts, schemas, internal structure, or tests.
- No removal of granular semantic tools, `search`, or `read_file`.
- No host-side rewriting of arbitrary model tool calls or autonomous stopping rule outside the tool result/prompt guidance.
- No weakening of repository trust, path confinement, prohibited-path, sensitivity, artifact, semantic confidence, source digest, approval, mutation, process, or network policy.
- No treatment of presentation text as authority for planning, mutation, validation, or persistence decisions.
- No change to Plan 84’s rule that source dedup can rely only on exact source ranges currently visible in the canonical model request.
- No broad refactor of the semantic workspace, tool runtime, provider adapters, or conversation loop beyond required integration points.

## 5. Current State

`code_explore` already has substantial structured evidence:

- `CodeExploreRequest` supports `Auto`, `Survey`, `Flow`, and `Impact` modes, exact symbol anchors, stable symbol IDs, C# path anchors, associated artifact anchors, and explicit limits.
- `CodeExploreResult` returns source sections, resolved anchors, query interpretation, discovery, candidate summaries, allocation, flow, blast radius, back-references, deduplication, emissions, associated artifacts, and coverage.
- `CodeExploreTool` describes the tool as primary C# exploration and clamps source by selected model effective input budget.
- `AdvancedSemanticQueryService` builds a generation-scoped C# declaration catalog, interprets natural-language terms, ranks candidates, applies bounded graph connectivity, projects source, and returns continuations/omissions.
- Plan 84 visible-source-frontier logic can suppress unchanged exact ranges and return back-references when source remains visible in the current request.
- Expected “no match” natural-language results already return a structured `CodeExploreResult`, but lack a broader availability contract and agent-tuned recovery language.
- `CodeExploreTool.ExecuteAsync` currently throws when no workspace is open: `Code exploration requires an opened workspace.` This is safe, but for an agent it can look like a broken tool rather than a recoverable state.

Gaps this plan targets:

- The model-facing DTO does not have a concise “source below is already read-equivalent” guarantee.
- Completion/omission/continuation details are accurate but not optimized for agent decision-making.
- Ranking has useful deterministic tiers but limited kind weighting, weak-match isolation checks, relative floor/backfill, and proportional file-budget allocation.
- Output limits are primarily request/model-budget driven, not repository-size aware.
- Expected unavailability is not consistently distinguished from invalid arguments, security refusals, cancellation, or internal failures.

## 6. Proposed Design

### 6.1 Agent-tuned presentation layer

Keep `CodeExploreResult` as the durable authority, but add a bounded presentation contract that is explicitly for model consumption. Preferred shape:

- `CodeExplorePresentation` on `CodeExploreResult`;
- `CodeExploreSourceGuarantee` entries that state whether returned source is current, verbatim, line-numbered, digest-identified, complete for the advertised range, partial, omitted, drifted, or already visible by back-reference;
- `CodeExploreNextActionHint` entries with a closed kind such as `UseReturnedSource`, `UseBackReference`, `FollowContinuation`, `RefineAnchor`, `WaitForWorkspace`, `OpenWorkspace`, `UseGranularFallback`, or `AskUser`;
- `CodeExploreNotShownTarget` entries summarizing the most important omitted files/symbols/artifacts with exact continuation anchors where available;
- a concise `ModelSummary` string generated from the structured presentation only after sanitization and bounding.

Presentation rules:

1. If a file section has `CodeExploreSourceCompleteness.Complete` for a current range with digest identity, state that the shown range is verbatim current source and should be used as an already-performed read for that range.
2. Do not say “do not read this file” for drifted, omitted, partial, prohibited, malformed, or policy-suppressed content. For partial source, say exactly which range is shown and provide continuation anchors.
3. Back-references must state that unchanged source is already visible in the current model request, name the holder/tool call, file, line range, digest, and symbols when bounded, and avoid the word “omitted” for successfully deduplicated source.
4. Avoid pointer-only success when high-ranked source can safely be re-emitted and the response would otherwise feel empty. If all candidate source is suppressed or unavailable, include either one restored useful source section or explicit next-action guidance explaining why no new source was emitted.
5. `Omissions` remain factual but should be grouped by closed reason and translated into action-oriented hints: exact continuation, retry after semantic readiness, refine ambiguous anchor, or use granular fallback.
6. Presentation text must never invent completeness, semantic flow, prior visibility, source identity, or recommended authority beyond the structured fields.
7. Bound all presentation text independently from source. Under pressure, drop lower-value prose before dropping source, exact continuations, or safety-critical omissions.

The intended model-facing effect is similar to CodeGraph’s strongest presentation lesson: when source is returned, the answer should make it unmistakable that the model already has the code it would otherwise read. Threadsmith should express that as host-owned structured hints plus a sanitized summary, not as unstructured prompt magic.

### 6.2 Natural-language ranking improvements

Refactor natural-language ranking into two explicit stages: symbol/declaration relevance and file/source relevance. Keep all ranking deterministic for a fixed workspace generation and request.

#### 6.2.1 Query interpretation and pinning

Enhance `InterpretCodeExploreQuery` and discovery to recognize:

- exact repository-relative C# path spans, including backtick/quote-wrapped paths and `path:...` labels;
- stable symbol IDs and documentation-comment IDs;
- qualified/container-qualified C# names;
- PascalCase/camelCase/snake_case/kebab-case identifier tokens;
- type/file context tokens that disambiguate overloaded member names;
- exact field/property-like identifiers that may not be high-value alone but can seed related containing members.

Explicit path anchors, stable IDs, and exact qualified symbols remain pinned ahead of inferred candidates. A named path should admit its source even if stripped query terms are otherwise weak, subject to existing policy and source bounds.

#### 6.2.2 Symbol kind and weak-match weighting

Introduce host-owned relevance bands for Roslyn symbol kinds:

- high signal: methods, constructors, local functions that are safely cataloged, named types, delegates, records, enums when the query targets type shape;
- medium signal: properties, events, fields, containing types/namespaces, project/file path matches;
- low signal: constants, enum members, generated declarations, or declaration-only shape when not corroborated by behavior terms.

For weak symbols, run bounded usage corroboration only after preliminary ranking, using Roslyn references/call relationships already available or cheaply derived. Weak candidates with no incoming/outgoing usage evidence should be down-weighted unless explicitly pinned or matched by exact qualified name. The failure mode to avoid is a local constant, helper field, or incidental property consuming source budget because it shares an English term with the query.

#### 6.2.3 Generated, test, and low-value handling

Generated and test-classified code should usually be down-weighted, not hard-excluded. Apply user-focus exceptions when query terms explicitly target tests, fixtures, generated code, generated APIs, designer files, or source generators. In all-generated or all-test areas, normalize penalties so relative ranking still works.

Low-value matches should remain discoverable as follow-up targets when they are the only evidence, but they should not displace source-bearing implementation files for ordinary behavior questions.

#### 6.2.4 Relative floors, backfill, and ambiguity

Replace pure absolute admission thresholds with a relative floor based on the strongest file/symbol evidence, capped so one dominant match cannot starve clearly named peers. Always backfill to a small minimum candidate set when the floor would otherwise leave the model with too little to act on.

Candidate summaries should expose stable ranks, closed reason flags, selected/omitted state, ambiguity group, and a short reason. They should not expose magic internal constants or imply that a numeric score is portable across workspace generations.

#### 6.2.5 Graph-aware glue and file relevance

Use compiler-known structure to add bounded glue around strong seeds:

- callers and callees in files already surfaced by high-ranked seeds;
- containing types and sibling members when a member name is ambiguous but a type/file token is present;
- implementations/overrides around interface or virtual dispatch boundaries;
- entry-to-terminal bridge symbols required by flow mode.

Move file relevance above “sum of selected symbols” by computing a file-level relevance record. A file’s allocation should consider strongest selected symbols, distinct query-term coverage, graph/flow membership, co-located corroboration, weak-symbol isolation, generated/test focus, source size, and whether it contains complete useful declaration bodies.

### 6.3 Source allocation by relevance and usefulness

Add a relevance-proportional source allocation pass after candidate/file ranking and before source projection. This pass should decide which files receive source, which receive only exact continuations, and how much each source-bearing file may spend.

Rules:

1. Reserve source budget for pinned anchors and flow-spine/call-site evidence before peripheral support.
2. Split remaining source budget by file relevance, not by file size and not solely by rank order.
3. Apply a relative “source cliff” for weak tail files: a cliffed file receives no source section and no source-bearing file slot, but it remains named with exact continuation anchors if safe.
4. Guarantee a minimum useful section for every source-bearing file. If the available allowance cannot hold at least one meaningful declaration/call-site window, emit a continuation instead of a misleading fragment.
5. Prefer complete methods/declarations, complete call-site windows, and top-ranked clusters over arbitrary source-order truncation.
6. Permit bounded whole-file buys when a file nearly fits its reservation and doing so avoids forcing a redundant read for a tiny remainder.
7. Carry unused allocation from skipped, already-visible, unreadable, or small files to the next ranked files so reclaimed budget improves recall.
8. Flow-spine files and exact pinned files can receive a bounded boost, but one giant file cannot consume the entire response.
9. Associated artifacts retain an independent budget and cannot displace the C# semantic spine.
10. Result-bound trimming should drop presentation prose and lower-value summaries before dropping emitted source, exact continuation anchors, or safety-critical omissions.

Update `CodeExploreAllocationSummary` or add a companion `CodeExploreFileRelevanceSummary` to make allocation decisions inspectable without logging source.

### 6.4 Repository-size-aware output envelopes

Compute a `CodeExploreRepositoryScale` for the current semantic workspace/catalog. Inputs may include:

- compiled C# document count;
- total C# document count;
- generated/source-generated document count;
- project count and target-framework spread;
- declaration catalog entry count and completeness;
- associated artifact candidate count when artifacts are enabled.

Use the scale to choose default output envelopes only within host-validated and model-effective maxima. Explicit request limits still cap the result; repository scale cannot widen beyond tool, policy, or selected-model bounds.

Proposed behavior by scale:

- tiny/small repositories: fewer default source-bearing files, lower total source cap, tighter per-file windows, fewer candidate summaries, omit optional relationship/meta prose unless useful;
- medium repositories: current defaults or slightly concentrated defaults, with full presentation/continuation metadata;
- large/very large repositories: do not grow a single response without bound; instead keep the single-call result under the model/tool inline budget, concentrate source on the semantic spine, include stronger not-shown targets, and expose a recommended bounded follow-up count or continuation plan.

The scale policy should also control presentation verbosity. Small repositories often need one clear result, not long meta commentary. Large repositories need enough guidance to prevent scattershot search/read follow-ups.

### 6.5 Recoverable “not available” responses

Introduce a typed availability outcome for `code_explore` so expected non-source states are successful tool results with actionable guidance, not unclassified exceptions.

Preferred public shape:

- `CodeExploreAvailabilityStatus` enum with values such as `Available`, `NoWorkspaceOpen`, `SemanticWorkspaceUnavailable`, `SemanticReadinessBelowMinimum`, `NoCompiledProjects`, `NoMatchingDeclarations`, `NoSourceAfterPolicy`, and `TimedOutPartial`;
- `CodeExploreAvailability` record containing status, safe reason, retryability, recommended next actions, minimum readiness required, current readiness when known, and whether granular fallback may be useful;
- `CodeExploreResult.Availability` optional/additive property.

Recoverable success-shaped results should be returned for:

- no opened workspace when the invocation context has no `WorkspaceId`;
- semantic workspace not loaded or below the minimum confidence required for C# source exploration;
- no compiled C# projects in the opened workspace;
- no compiler-known declaration/path match for the request;
- expected timeout after partial safe evidence has been assembled;
- policy removed all otherwise relevant source, when the reason can be safely summarized.

Still throw or return tool errors for:

- invalid/malformed input schema or out-of-range limits;
- repository trust or approval policy refusal before the tool is authorized;
- path traversal, absolute out-of-repo paths, prohibited/sensitive paths, reparse/device refusal, or artifact security refusal;
- cancellation requested by the caller;
- unexpected internal exceptions, data corruption, or invariant violations.

A success-shaped unavailable result must not encourage blind retries. It should say whether to open/load the workspace, wait for semantic readiness, refine anchors, use an exact continuation, or use granular fallback for that one task.

### 6.6 Model/tool-description guidance

Update the canonical tool description and any context guidance so models learn a stable policy:

- Use `code_explore` first for C# source-bearing survey, flow, and impact questions.
- Treat complete current `FileSections` as read-equivalent for their advertised line ranges.
- Use `BackReferences` from the current request rather than reading those ranges again.
- Use `ContinuationTargets` for focused follow-up.
- Use `find_symbol`, `search`, or `read_file` only for exact gaps, non-C# arbitrary text, unavailable semantic workspace, or when `code_explore` explicitly instructs a fallback.

Keep this guidance concise. The presentation layer should carry the per-result details; global prompt/tool descriptions should not become a brittle manual.

## 7. Public Contracts

Expected additive host-owned contracts:

- `CodeExplorePresentation` — bounded model-facing summary, source guarantees, not-shown targets, and next-action hints derived from authoritative result fields.
- `CodeExploreSourceGuarantee` — per file/range guarantee of current/verbatim/read-equivalent source, partial source, drift, omission, or visible back-reference.
- `CodeExploreNextActionHint` / `CodeExploreNextActionKind` — closed follow-up guidance for returned source, back-reference, continuation, refine, open/wait workspace, granular fallback, or ask user.
- `CodeExploreNotShownTarget` — bounded file/symbol/artifact target with reason and exact continuation when safe.
- `CodeExploreRepositoryScale` / `CodeExploreAdaptiveBudget` — scale tier, input counts, effective source cap, file cap, per-file cap, metadata verbosity, and budget source.
- `CodeExploreAvailability` / `CodeExploreAvailabilityStatus` — safe availability outcome and recovery guidance.
- Optional `CodeExploreFileRelevanceSummary` — inspectable file-level relevance/allocation reason bands.

Expected changes to existing contracts:

- `CodeExploreDiscoverySummary.BudgetSource` or `CodeExploreAllocationSummary.BudgetSource` should mention adaptive repository-scale and model-budget clamps when applicable.
- `CodeExploreCoverage.Omissions` should remain factual; presentation hints translate them into model actions.
- `CodeExploreResult` remains backward-compatible through additive optional properties.

No contract may expose Roslyn objects, provider state, terminal-library types, raw filesystem handles, raw model payloads, hidden reasoning, unbounded source, secret text, or copied CodeGraph names/constants/schemas.

## 8. Project/File Changes

Likely implementation files:

- `src/Threadsmith.Core/CodeExploreContracts.cs` — additive presentation, availability, adaptive-budget, and file-relevance DTOs/enums.
- `src/Threadsmith.DotNet/AdvancedSemanticQueryService.cs` — ranking/refinement, file relevance, source allocation, repository-scale calculation, and result construction.
- `src/Threadsmith.Tools/CodeExploreTool.cs` — availability shaping for no-workspace/no-source cases, model-budget plus repository-scale clamping, schema/tool-description updates, and final result bounding order.
- `src/Threadsmith.Tools/ToolContracts.cs` — only if invocation/context budget or availability transport needs an additive host-owned field.
- `src/Threadsmith.Context` — only if canonical guidance, evidence admission, or visible-source presentation needs generic context changes beyond the tool result.
- `src/Threadsmith.Execution` / provider projection code — only if tool-result rendering to model messages needs a semantic presentation pass outside the generic serializer.
- `docs/user-guide.md` and `docs/operations/tools.md` — user/operator descriptions after implementation.
- `docs/implementation-plans/acceptance-scenarios.md` and `docs/implementation-plans/manual-test-plan.md` — only when executable Scenario AO/MTP behavior changes are implemented.

Likely tests:

- `tests/Threadsmith.NativeTools.Tests/Plan81CodeExploreToolTests.cs`
- `tests/Threadsmith.NativeTools.Tests/Plan82CodeExploreFlowTests.cs`
- `tests/Threadsmith.NativeTools.Tests/Plan85CodeExploreAssociatedArtifactTests.cs`
- new or existing native-tool tests for presentation, ranking, adaptive budgets, and availability;
- provider/schema projection tests if the tool result’s model-facing shape changes;
- context/frontier tests if back-reference wording/availability depends on canonical request visibility;
- architecture tests for dependency direction and public DTO boundaries.

## 9. Ordered Tasks

1. Re-read root/docs/src/tests DOX and the portable C# guardrails before code edits.
2. Capture a fixed baseline on representative small, medium, and large C# repositories:
   - current `code_explore` calls;
   - subsequent redundant `read_file`/`search` calls;
   - missed relevant files;
   - total tool calls, serialized result size, latency, and answer correctness.
3. Define the additive Core presentation, availability, repository-scale, adaptive-budget, and optional file-relevance DTOs with XML docs and closed enums.
4. Implement presentation synthesis from existing authoritative fields only:
   - current-source guarantees for complete source ranges;
   - partial/drift/omitted distinctions;
   - back-reference guidance;
   - continuation-first next actions;
   - bounded not-shown targets.
5. Adjust result bounding so source, continuations, safety omissions, and availability status survive before optional presentation prose/candidate detail.
6. Implement repository-scale calculation from the semantic snapshot/catalog and propagate the selected adaptive envelope into allocation summaries.
7. Apply adaptive defaults when the request uses default limits; clamp explicit limits by host/model maximums without silently widening a caller’s narrower request.
8. Split ranking into preliminary symbol relevance and file/source relevance, keeping stable deterministic ordering.
9. Add query/path/name seeding improvements, including path labels, quote/backtick stripping, qualified names, type-context disambiguation, and safe camel/substring fallback for exact identifier-looking terms.
10. Add kind weighting and bounded weak-symbol usage corroboration; measure and cap Roslyn reference/call probes so ranking remains cancellable.
11. Add generated/test/low-value down-weighting with explicit user-focus exceptions.
12. Add relative floor/backfill and expose ambiguity/unresolved-term behavior in candidate summaries.
13. Implement relevance-proportional allocation, source cliffing, minimum useful section enforcement, carry-forward slack, flow/pinned boosts, and artifact-independent budget preservation.
14. Add recoverable availability results for no workspace, semantic unavailability/readiness, no compiled projects, no matches, timeout partials, and policy-empty results.
15. Verify security and invalid-input paths still fail closed and do not return success-shaped results.
16. Update tool descriptions and context guidance only after the result presentation contract exists.
17. Add focused fixtures and regression tests for all new behavior.
18. Run focused native-tool, provider/schema, context/frontier, architecture, and solution build gates.
19. Repeat the fixed-task comparison and record whether redundant reads/searches, repeated explores, size, latency, and correctness improved.
20. Update user/operator docs, Scenario AO, and manual tests only for implemented observable behavior.
21. Run planning-governance searches and `git diff --check`; close out the implementation document only after verification.

## 10. Testing

Automated tests must cover:

- presentation says complete returned ranges are current/read-equivalent only when source is complete and digest-identified;
- presentation does not discourage reading or claim completeness for partial, drifted, omitted, policy-suppressed, or unavailable source;
- back-reference presentation names exact current-request holder, file, line range, digest, and bounded symbols, and avoids pointer-only failure-feeling results when source can safely be restored;
- continuations and not-shown targets survive result bounding ahead of optional prose;
- path-like query spans and label/quote/backtick variants pin C# files deterministically;
- exact/qualified/type-context symbols outrank isolated common-word or weak-kind collisions;
- weak fields/properties/constants without usage corroboration are down-weighted unless explicitly pinned/focused;
- generated/test/low-value penalties stand down or invert when the query explicitly targets generated or test code;
- relative floor and backfill avoid both noisy long tails and empty under-serving;
- proportional allocation gives high-relevance files more useful source than lower-relevance small files;
- source-cliffed files do not consume source-bearing file slots and return exact continuations;
- every emitted file has a minimum useful source section or is replaced by a continuation/back-reference;
- repository-scale tiers adjust defaults and metadata verbosity while respecting explicit limits, tool caps, and selected-model budget clamps;
- no-workspace, semantic-unavailable, no-compiled-project, no-match, partial-timeout, and policy-empty outcomes return bounded availability results;
- invalid schema/limits, security/path refusals, cancellation, and internal failures remain failures;
- deterministic ordering holds across repeated runs for the same workspace generation;
- additive contract serialization/deserialization and provider schema projection remain stable.

Evaluation coverage should compare pre/post behavior on fixed tasks:

- redundant reads of files already returned by `code_explore`;
- broad searches immediately after sufficient source was available;
- repeated overlapping `code_explore` calls;
- missed relevant source/artifacts;
- final cited evidence correctness;
- result size and cumulative model input;
- latency and cancellation behavior.

## 11. Security/Permissions

Natural-language query text remains inert bounded data. It cannot become executable code, regex supplied to an unsafe engine, SQL, shell/process arguments, analyzer/plugin loading, network requests, configuration authority, mutation scope, or provider-owned retrieval state.

Presentation hints are advisory model guidance only. Host decisions must continue to use structured state, trust policy, source digests, semantic confidence, approvals, and validation results. A hint cannot authorize reading prohibited paths, executing tools, staging mutations, accepting plans, or trusting back-references outside the current canonical model request.

Availability shaping must not turn security refusals into benign guidance. Path traversal, absolute out-of-repo anchors, prohibited/sensitive paths, reparse/device concerns, malformed arguments, cancellation, and unexpected host failures remain fail-closed and visible as failures through existing tool-runtime paths.

Telemetry and logs must not include source bodies, artifact content, query text when policy forbids it, hidden reasoning, raw provider payloads, secret values, sensitive path segments, or copied external benchmark content.

## 12. Observability

Record bounded counters and classifications:

- presentation guarantees emitted by kind;
- next-action hints by closed kind;
- availability outcomes and retryability;
- repository scale inputs/tier and selected adaptive budget;
- candidate counts by tier/reason/kind;
- weak-symbol corroboration probes, capped/skipped counts, and duration;
- generated/test/low-value/user-focus classifications;
- relative floor/backfill decisions;
- file relevance bands, source-cliffed files, useful-section failures, carry-forward bytes, and allocation spend;
- redundant-read/search displacement metrics in opt-in local evaluation;
- cancellation, timeout, policy-empty, and result-bound trimming outcomes.

Do not log source text, artifact text, raw queries, full symbol names when telemetry privacy policy disallows them, model messages, provider payloads, or hidden reasoning.

## 13. Migration/Compatibility

All public contracts should be additive where possible. Older persisted `CodeExploreResult` records simply omit presentation, availability, scale, or file-relevance details. Existing exact anchors, source sections, flow, blast radius, associated artifacts, continuations, back-references, and emissions remain valid.

If a provider or serialized-result consumer does not understand new fields, the old structured result remains usable. If adaptive budgets are disabled or scale cannot be computed, fall back to current model/request-limit behavior and state that fallback in allocation budget source.

No existing configuration should be required. A future repository setting may narrow adaptive output defaults or disable presentation prose, but repository configuration cannot widen host/tool/model maximums or change ranking with executable logic.

## 14. Acceptance Criteria

- Complete current source returned by `code_explore` is presented as read-equivalent for exact advertised ranges, and repeated redundant reads decrease in fixed-task evaluation.
- Partial, omitted, drifted, policy-suppressed, and unavailable results are honestly labeled with exact continuations or fallback actions.
- Natural-language ranking better prioritizes named, qualified, path-pinned, structurally connected, and behavior-bearing C# files over weak incidental matches.
- Generated/test/low-value material remains reachable when explicitly requested but no longer crowds out ordinary implementation source.
- Source allocation is relevance-proportional, emits useful sections, preserves the semantic spine, and names safe continuations for source that does not fit.
- Repository-size-aware defaults reduce small-repo verbosity and concentrate large-repo output without exceeding model/tool caps.
- Expected unavailability returns bounded success-shaped guidance, while invalid input, security refusal, cancellation, and internal failures remain fail-closed.
- Focused automated tests, provider/schema tests as needed, context/frontier tests as needed, architecture tests, solution build, fixed-task evaluation, Scenario AO review, and updated manual/user/operator documentation pass.
- No host authority, source identity, path policy, trust boundary, approval, mutation, validation, or telemetry privacy regression is introduced.

## 15. Risks

- **Presentation overclaims source authority:** derive every statement from completeness/digest/frontier fields and test negative cases.
- **Agent ignores DTO and follows prose too literally:** keep guidance concise, closed-kind-backed, and accurate; avoid unconditional “do not read” language.
- **Ranking overfits CodeGraph lessons or one fixture:** adapt principles only, use Threadsmith repositories/fixtures, and require varied fixed-task evaluation.
- **Weak-symbol usage probes become expensive:** cap probes, run only after preliminary filtering, propagate cancellation, and report skipped corroboration.
- **Relative floors hide useful tail files:** cap the floor and backfill a minimum candidate set with exact continuations.
- **Small-repo caps force read fallback:** evaluate small repos specifically and keep minimum useful sections.
- **Large-repo output becomes too large:** cap under selected model/tool result budgets and prefer follow-up continuations over larger single responses.
- **Availability success hides real failures:** maintain a closed recoverable-status allowlist and keep policy/security/internal errors as failures.

## 16. Documentation

When implemented, update user and operations documentation for:

- how to interpret read-equivalent `code_explore` source;
- back-reference meaning and current-context requirement;
- continuation-first follow-up behavior;
- ranking limitations and generated/test focus behavior;
- repository-size adaptive budgets and model-budget clamps;
- recoverable availability statuses and what the user/model should do next;
- when to fall back to `find_symbol`, `search`, or `read_file`.

Update Scenario AO and the maintained manual test plan only when the executable behavior changes. Planning progress alone does not require DOX changes. Preserve the statement that `C:\source\repos\codegraph` was consulted only as a functional reference; Threadsmith must not copy or depend on that implementation.

## 17. Open Decisions

- Exact DTO names and whether `CodeExplorePresentation` should be serialized as structured JSON only, rendered Markdown-like summary text, or both.
- Whether no-workspace availability can be shaped inside `CodeExploreTool.ExecuteAsync` without weakening generic tool-runtime error handling.
- Minimum useful source-section thresholds by mode and repository scale.
- Repository-scale tier boundaries and whether they should consider only compiled C# files or all repository files from inventory.
- Final weak-symbol corroboration budget and whether to cache usage summaries per workspace generation.
- Whether file-level relevance summaries should include numeric weights, coarse bands, or only closed reason flags.
- Materiality threshold for declaring evaluation success: e.g., minimum redundant-read reduction with no correctness regression.
