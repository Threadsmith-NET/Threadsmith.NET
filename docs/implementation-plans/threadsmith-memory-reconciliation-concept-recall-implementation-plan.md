# Threadsmith.NET Repository Memory Reconciliation and Concept-Aware Recall

## Implementation Plan

**Status:** Implemented and regression-tested; production ranking evaluation and six-RID native runtime gates remain open.

**Delivery track:** [M33](milestones/milestone-33-memory-reconciliation-and-concept-recall.md). Current milestone status is owned by [milestones.md](milestones.md); completed memory milestones remain frozen.

**Prerequisites:** Current managed-memory/standing-preference implementation, local embedding and cross-encoder adapters, central tool pipeline, and ordinary active-turn continuation/compaction.

**Readiness and implementation review:** 2026-10-04 against the active checkout. Deterministic regression tests and isolated live provider/SQLite exercises now cover write resolution and recall. Shipped-model production ranking evaluation and the complete native runtime matrix are not yet established.

Read root `AGENTS.md`, `planning-governance.md`, `00-shared-context.md` §G and, before writing C#, `docs/guardrails/portable-csharp-guardrails.md`.

## 1. Objective

Implement two independently configurable consumers of one repository-memory search engine:

1. Conversational recall finds memories relevant to the current coding task.
2. Before either a model or manual add commits, reconciliation finds sufficiently related memories and requires an explicit choice to update an existing stable ID or confirm distinctness.

Then add model-supplied memory kind and concepts, and use concepts from native tool invocations to discover additional memories before ordinary model continuations within the same user turn. Reconciliation works without concept metadata and also uses supplied concepts when its concept branch is enabled. Optional shared fuzzy lexical discovery applies to both consumers.

No additional generative-model call is introduced. Local embedding and cross-encoder inference remain permitted and measured.

## 2. Architectural Context

[ADR-59](../architecture/adr-59-model-managed-repository-memories.md) owns current managed memory and supersedes ADR-50's old authority, validity and promotion design. Preserve the repository-local database, canonical identity, explicit writes, complete-text embedding admission, host provenance and final-dispatch usage receipts.

Use the central tool pipeline (ADR-11), effect scheduling (ADR-43), context policy and normal continuation reconstruction. Shared search belongs in Context with Core DTOs and Persistence queries. Both manual commands and the `memories` tool continue through `RepositoryMemoryService`; reconciliation must not become a tools-only checker or second write path. It runs inside the existing operation/activity and cancellation boundary.

## 3. Scope

- Direct-query shared search and independent recall/reconciliation configuration.
- Reconciliation of both manual and model adds against both standing preferences and situational memories.
- Bounded collision responses, revision-aware distinctness confirmation and atomic searched-revision fencing.
- Managed-memory `kind` and relational concepts, independent of `memoryType`.
- Exact concept lookup and packaged spellfix1 lexical fallback.
- Optional concepts on explicitly eligible native tool inputs through ordinary schemas and execution.
- Bounded ephemeral concept/admission state scoped to one user turn.
- Progressive recall, diagnostics, migrations, calibration and release validation.

## 4. Non-Scope

No extra generative request, repository exploration, inference of concepts from filenames/results, automatic memory creation/invalidation, supersession graph, MCP/extension concept support, Kind/frequency relevance boosts or unbounded cross-turn concepts.

Concepts do not enter memory-text FTS or JSON persistence. Do not add a replacement tool pipeline, context assembler or UI-specific engine. The approved follow-up includes concept-assisted reconciliation and bounded fuzzy memory-text discovery through shared search. Concepts remain a discovery signal; complete proposed-text scores order candidates for caller review.

## 5. Baseline Readiness Findings

This table records the pre-implementation gaps, not outstanding defects. Paths are relative to `src/`.

| Existing owner | Required integration |
|---|---|
| `Threadsmith.Context/HybridRepositoryMemoryRetriever.cs` and `.Reranking.cs` | Query construction, snapshot filtering, vector rebuild, RRF, reranking and caches are intertwined. Extract mechanics while preserving conversational behavior first. |
| `Threadsmith.Core/ManagedMemoryContracts.cs`, `ManagedMemoryServiceContracts.cs` | Live records are `RepositoryMemoryEntry` and operation/write DTOs. Existing `MemoryType` controls standing versus situational behavior. |
| `Threadsmith.Core/RepositoryMemoryContracts.cs` | Historical `RepositoryMemoryKind` already exists with incompatible members. Do not replace or reinterpret it. |
| `Threadsmith.Context/RepositoryMemoryService.cs` | Sanitizes, handles exact duplicates, embeds complete new text and shares writes. Same-text metadata changes already skip inference. |
| `Threadsmith.Persistence/SqliteManagedRepositoryMemoryStore.cs` | Updates fence entry revisions; adds do not yet fence the repository revision observed by a search. Ordinary search-then-add is racy. |
| `RepositoryMemoryTypeSchemaMigration.cs`, store lexical queries | FTS indexing and SQL filtering currently exclude standing preferences. All-type reconciliation needs a migration, not just removal of a retriever filter. |
| `Threadsmith.Tools/MemoriesTool.cs` | Hand-written sealed schema, action validation, bounded output and serialized resource scheduling require coordinated changes. |
| `Threadsmith.Tools/ToolContracts.cs`, `BuiltInTools.cs`, `ToolInvocationPipeline.cs` | Concrete properties drive schemas; unknown JSON members are rejected. Default interface members do not implement transport. |
| `Threadsmith.Execution/SessionApplication.ConversationLoop.cs` | Existing loop state, memory refresh, frozen-context invalidation and request rebuild own turn lifecycle. New concepts must invalidate context even without a database change. |
| `Threadsmith.Context/ContextAssembler.cs`, `Threadsmith.Execution/RepositoryMemoryDispatch.cs` | Reuse capacity/sensitivity admission and final-submission accounting. Search must not increment usage. |
| `RepositoryMemoryApplication.cs`, Interaction/CLI memory command adapters | Manual add currently returns an entry and assumes success. It needs a structured non-writing collision result and confirmation syntax. |

The original proposal was not ready for unattended end-to-end implementation: enum naming, manual flow, FTS eligibility, revision races, failure/completeness handling, transport, lifecycle, calibration and packaging were underspecified. The following contracts resolve implementation choices and identify evidence gates rather than leaving agents to invent production behavior.

## 6. Implementation Contract

### 6.1 Phase 1 — shared search without behavior change

Extract host-owned direct-query request/result/options from the existing retriever. Keep `IHybridRepositoryMemoryRetriever` as the conversational adapter. Preserve its instruction-first/task-intent query builder, character/term bounding, standing-preference handling and recall fallback semantics.

The shared request contains canonical repository identity, direct query, immutable search options, explicit eligible memory types and optional user-turn identity for degraded-cache scope. Add normalized concepts in the later concept phase. Options distinguish semantic qualification, reranker candidate bound, result bound and cache/diagnostic bounds. Reconciliation does not use `EffectiveContextMaximum`: setting recall count to zero must not disable the write check.

Shared search owns snapshot-consistent FTS/vector discovery, compatibility/rebuild, query cache, RRF, deterministic ordering, reranking and structured diagnostics. Return repository revision and explicit branch availability, completeness and truncation state; an empty result or diagnostic string cannot stand for successful reconciliation. Preserve candidate/omission information needed by the caller.

Cache keys include repository/revision, query, embedding space, eligible types, selection options, reranker identity and later concepts/resolver version. Do not share degraded recall results as successful reconciliation. Query vectors can be reused across consumers when complete text and embedding identity match. Keep caches bounded, propagate cancellation and perform inference outside write transactions.

Acceptance: existing lexical qualification, cosine cutoff, RRF, ordering, standing preferences, bounding and fallback behavior remain compatible. Phase 1 changes neither FTS corpus nor production thresholds.

### 6.2 Phase 2 — reconcile all explicit adds

Interception stays in `RepositoryMemoryService.ExecuteAsync`, after sanitation/normalization and exact duplicate handling, before insertion or eviction. Query only the complete sanitized proposed text that would be stored. Exclude current instruction, history, task intent, transcripts and generated answers.

Generate and validate the complete proposed-text embedding once, then reuse it for search and eventual persistence through a host-internal search input. Do not accept caller/model-supplied vectors or compute the same proposed vector twice. Existing text/embedding limits remain binding. Query-memory reranker truncation is an incomplete check, never a valid strong-collision score.

1. Exact normalized duplicates retain the existing `duplicate` no-op: no insertion, inference, eviction or distinctness override.
2. Search both memory types using independent reconciliation settings.
3. Discovered candidates with complete cross-encoder scores are ordered for caller review, including negative scores. No absolute cross-encoder cutoff is applied; the caller decides whether to supersede or retain distinct memories.
4. If any strong candidate is unacknowledged, return `reconciliationRequired`, `added: false`, bounded candidates with ID, revision, text, memory type and later kind, plus omission count and next-action guidance. No entry write or eviction occurs. Compatible-vector repair is allowed and diagnosed separately.
5. Update through the ordinary existing-ID path, or retry add with `confirmDistinctFrom: [{id,revision}]`. Search again; new strong candidates or changed candidate revisions require another explicit acknowledgment.
6. Carry the searched repository revision into the existing add transaction. Check it before insertion/eviction; drift is a non-mutating conflict. Recompute once against a fresh snapshot, then return an actionable conflict if state keeps changing. No inference under the write transaction or unbounded retries.

Confirmations assert distinctness, not write permission. Validate UUIDs/revisions, deduplicate and bound the list by storage capacity; reject malformed entries and use on non-add actions. Current search results determine collisions, not the caller's list. Already acknowledged candidates need not be repeated in bounded responses; show unacknowledged candidates and omissions so retries can progress. Require acknowledgment of all strong candidates in the checked window, and disclose candidate-window limitations. Do not claim exhaustive semantic duplicate detection.

Validate output limits so at least one ordinary candidate plus response metadata fits after JSON escaping. Imported oversized text must have an explicit truncation marker and existing inspection guidance; never silently omit every candidate while requesting confirmation. Acknowledgments remain tied to the complete stored revision, not a displayed excerpt.

Expose candidate revision and optional update `expectedRevision`. Guidance for correcting a displayed collision supplies that revision; check it at service read and commit. Omission preserves existing callers' service-observed revision behavior. ID alone cannot protect against edits since a collision response.

**Required inference:** Local inference is a deployed prerequisite, not an optional reconciliation backend. No fail-open setting is introduced. Unexpected inference/storage errors, invalid scores or incomplete reranking return a visible non-writing failure. An unavailable branch must not silently become “no collision.” Cancellation propagates. This does not alter ordinary conversational recall's existing degraded behavior.

**All-type FTS:** In phase 2, migrate the existing text-only FTS index/triggers to include both memory types and parameterize eligibility in the shared lexical queries. Keep concepts out of it. Do not create a second reconciliation index/reader. Recall still filters situational entries and assembles standing preferences separately. Broader corpus statistics can change BM25 order even after filtering: measure/report recall regressions rather than claiming this migration is behavior-neutral.

### 6.3 Manual and model resolution surfaces

Both entry points use the same structured operation result. Extend `RememberRepositoryMemoryCommand` and its handlers/adapters to return committed, duplicate, reconciliation-required or failed/conflicting outcomes, rather than throwing because `Entry` is absent.

Manual syntax extends the existing parser:

- `/memory remember [--type ...] [--confirm-distinct-from <id>@<revision>]... <text>`.
- `/memory update [--expected-revision <revision>] <id> <replacement>`, preserving current type options.
- Kind/concept options are added with the metadata phase, using the same validation and omission semantics.

Preserve existing text parsing and provide an explicit end-of-options escape for literal text beginning with option-like tokens. A collision prints bounded candidates and concrete retry/update guidance; it never prints “remembered.” Headless structured output carries the same outcome and no-write indicator. Interactive resolution does not make an extra generative call or create a separate confirmation store. Tests cover both retained frontend and headless adapters.

### 6.4 Calibration and production enablement

Build calibration and held-out fixtures for exact duplicates, paraphrases, corrections including negation, related-but-distinct and unrelated pairs, both memory types, technical abbreviations, long complete pairs and discovery misses.

Use shipped model assets/hashes, not only fake scores. Report candidate-discovery recall before reranking, duplicate/correction interruption recall, false-positive interruption rate, score distributions, truncation, candidate-window omissions and cold/warm latency.

Evaluate candidate discovery, ordering and window sizes on held-out pairs, recording numerical quality targets and omissions. Reconciliation returns bounded potential overlaps for caller judgment rather than classifying collisions from raw cross-encoder logits. No raw-score cutoff or cutoff-calibration gate is required.

### 6.5 Phase 3 — kind and concepts

Use a new `ManagedRepositoryMemoryKind` enum with explicit stable values: Unspecified, Constraint, Decision, Convention, Requirement, Finding. Model-facing `kind` values are lower-camel strings. Historical `RepositoryMemoryKind` remains unchanged. Kind describes content and grants neither authority nor relevance weight; `memoryType` still owns selection behavior.

Descriptions: constraint is a restriction; decision a chosen design/rationale; convention an established practice; requirement a desired outcome; finding an observed fact; unspecified the compatibility default. Classification is not proof of truth.

Add optional kind/concepts to add/update. Add defaults to Unspecified/empty. On update, omission or null preserves existing metadata; explicit empty concepts clears the set; explicit unspecified clears classification. Keep complete replacement text required in V1, including metadata-only updates. Other actions reject write metadata.

One host normalizer serves memory and invocation concepts: trim, Unicode NFKC, invariant lowercase, ordinal deduplication and deterministic ordinal ordering. Accept Unicode letters/digits and internal ASCII hyphens separating nonempty segments; reject whitespace, controls, markup and other punctuation. Use `csharp`/`dotnet` for punctuation-heavy names.

Initial defensive limits: 8 concepts per input, 48 Unicode scalar values per normalized concept and 64 distinct concepts per turn. These bound resource use, not relevance, and must have schema/overflow tests and measured sizing before release. Invalid supplied input is rejected; turn-cap overflow omits additional hints with a bounded diagnostic.

One deployed guidance asset encourages bounded specific technical concerns/components/technologies/behaviors: authentication, serialization, cancellation, streaming, logging, telemetry, concurrency, provider, configuration, memory, retrieval. Discourage code/file/class/method/implementation/change/work without inventing an enforced ontology.

### 6.6 Persistence and revision behavior

Extend `managed_memories` with kind. Add relational concepts with primary key `(repository_identity, memory_id, concept)`, composite foreign key to the live row, cascade delete and reverse lookup index `(repository_identity, concept, memory_id)`. Enable foreign keys on actual connections; parameterize queries. Batch metadata reads rather than issuing one query per memory.

Replace concept sets transactionally with text/type/kind. No-op comparison includes text, memory type, kind and normalized concept set: extend both service and store early returns. Meaningful metadata changes increment entry/repository revisions, reset usage/receipts using existing update semantics and invalidate rankings.

Extend the existing same-text `CreateTypeUpdatedEntry` path: retain vector bytes/hash/space and advance `EmbeddingRevision` to the new entry revision. Otherwise retrieval would rebuild an unchanged vector. Embedding attachment still fences revision/hash/model compatibility.

Migration defaults live rows to Unspecified/no concepts and preserves IDs/text/types/times/embeddings. Preserve historical DTO serialization. Use the next available migration version and existing backup/forward-migration conventions, not a version number guessed from this review. Test delete, eviction, rollback and repository isolation.

### 6.7 spellfix1 packaging and resolution gate

Exact indexed concept lookup works independently of spellfix1. Only concepts without an exact match use bounded fuzzy lookup; no synonyms/semantic expansion. Shared memory-text fuzzy lookup similarly expands unmatched query terms against the same-snapshot FTS vocabulary, with per-term and total bounds while preserving exact results and original-term qualification. Uniform vocabulary rank avoids a hidden frequency boost. Order accepted alternatives by distance then normalized concept. Calibrate typo/ambiguity and short-token rejection before enabling fuzzy matching.

[SQLite documentation](https://www.sqlite.org/spellfix1.html) identifies spellfix1 as a separately built loadable extension, outside standard builds. [Microsoft.Data.Sqlite documentation](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/extensions) describes connection-level extension loading. Neither establishes verified packaging for this repository.

Prove a pinned source/build or package with hashes, licensing, entry point, provider ABI compatibility and published load/query smoke tests for all release RIDs: win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64. Load only absolute verified app-owned paths through a persistence-owned connection helper. No repository-configured library path or general SQL loading authority.

Missing/unloadable assets visibly degrade ordinary recall to exact-only. Database opening and core CRUD remain independent of the native extension; an enabled reconciliation operation requires its configured discovery branches to complete before authorizing its write. Do not install extension-dependent CRUD triggers or require loading during core schema migration.

Spellfix vocabulary is disposable derived state, keyed by canonical repository identity and memory-set revision. A connection-local temporary vocabulary is acceptable for the bounded memory set; build once per resolver snapshot and vocabulary kind and reuse, not once per term. Retain at most concept and FTS-text vocabularies in the existing native owner. Bound its lifetime through the existing persistence/resolver owner. Relational concepts remain authoritative; stale/failed vocabulary is discarded.

### 6.8 Phase 4 — native tool concept transport

Use composition: an opt-in native input interface with a concrete nullable `Concepts` property on eligible DTOs, shared normalization/validation and schema guidance enrichment. Do not rely on default interface members or introduce a base DTO hierarchy. Existing `Tool<TInput,TOutput>` remains the deserializer/executor; no JSON-stripping proxy or alternate invocation envelope.

Initial eligibility is ordinary native repository inspection/execution tools plus memories add/update. Inventory actual registrations and record the exact DTO list in the implementing change. Exclude host control calls (completion, plans, approval/replan, delegation), MCP and extension tools. Display source strings cannot establish native provenance. On memories, concepts label the proposed memory; no second ambiguous concept field. List/remove do not contribute hints.

Update generated and hand-written schemas and provider-schema conversion tests. Preserve strict unknown-member validation. Omitted/null concepts are empty hints except memory-update preservation semantics.

Observe normalized concepts centrally once an authorized invocation reaches its ordinary execution boundary, never during preflight/deserialization. Carry host-owned observations through existing invocation results. Execution failures may still yield intent hints; denied/invalid/cancelled-before-start calls do not. Merge using ordinary result processing, including prepared batches and configured wrappers. Concept changes must not bypass operational duplicate-call suppression, scheduling, policy or approvals.

### 6.9 Phase 5 — user-turn lifecycle

Extend existing conversation-loop state with bounded observed concepts, resolved identities, candidate IDs and admitted memory ID/revision pairs. Do not add a singleton/session-global store.

A turn is the top-level user request/run, not a model round, batch or compaction checkpoint. Steering stays in that turn. A new user turn starts empty; completion, cancellation, failure, reset and repository switch discard state. Never persist it as memory or restore it across restart. Child hints never merge automatically into the parent's turn; children remain subject to their existing memory/context policy.

Merge parallel tool results in deterministic existing call order before the next continuation. Resolution state is keyed by repository, memory-set revision, query/steering identity and resolver configuration. Only new concepts need lookup while this identity is unchanged. Database changes invalidate previous resolutions, including empty matches; steering reranks retained candidates. Mark resolution complete only after successful lookup; degraded lookup does not permanently consume hints. Retain prior concept candidate identities when processing the next delta.

### 6.10 Phase 6 — progressive recall

Union concept candidates with lexical/semantic candidates in shared search. Keep existing text-branch RRF unchanged; deterministically append concept-only candidates into a dedicated bounded portion of the reranker window so text candidates cannot crowd them all out. Choose total/window sizes from calibration; no Kind/frequency weights.

Rerank with the unchanged conversational query. Concepts do not expand it with tool/model prose. Measure whether this query admits useful subtask memories before enabling concept recall. Concept-only admission requires successful complete cross-encoder scoring; raw scores only order discovered candidates, and normal candidate/context bounds control selection. Disabled/unavailable/truncated reranking retains existing text recall fallback and defers concept-only admission with a diagnostic.

Before an ordinary continuation, merge observations, resolve the required delta and invalidate `FrozenContext` when concepts/candidates change. Rebuild via the normal context assembler and canonical provider continuation path. No extra model call, timer or background loop. Preserve compaction and exact pending tool groups.

Retain admitted unchanged memory identities through relevance fluctuations during the turn, with stable order and new admissions appended. This is conditional retention: deletion/eviction, changed content, repository/mode/policy changes, sensitivity and capacity override it. Revalidate changed revisions; rebuild obsolete blocks instead of pinning stale text. Standing preferences retain their separate rules.

The total situational count/token budget remains the configured limit, not a per-round allowance. Retain prior eligible admissions first; when full, diagnose omitted new candidates rather than growing an unbounded union. Hard capacity reduction can remove retained entries with a reason.

Only existing final-dispatch accounting records submitted IDs/revisions. Discovery, reranking, inspection and retained-but-not-submitted items create no inclusion receipts.

## 7. Public Contracts and Configuration

Use two configuration blocks under `tools:config:memories`: `Recall` and `Reconciliation`. Both own SemanticMinimum, RerankerEnabled, RerankerCandidateLimit, Concepts (Enabled/CandidateLimit), Fuzzy (Enabled/MaximumDistance) and Lexical expansion limits. One fuzzy policy per scenario governs both text and concepts. Recall additionally owns MaximumResults and MaximumQueryCharacters; reconciliation owns Enabled and requires RerankerEnabled=true when enabled. Top-level settings cover cross-operation storage and shared resource limits. Retired paths fail with migration guidance; layered overrides and reopen behavior remain intact. Reconciliation scope is both origins and both memory types, not a caller-controlled bypass.

Freeze configuration at existing operation/user-turn boundaries and preserve normal layering/reopen semantics. Validate semantic cosine thresholds, positive fuzzy bounds and consistent windows. Cross-encoder logits only order candidates; no minimum score setting exists. Fuzzy edit-cost defaults to 1 for text and both concept consumers.

Model memory schemas add kind, concepts, `confirmDistinctFrom: [{id,revision}]` and optional update `expectedRevision`. Results distinguish collision/no write, committed write, duplicate, conflict and failure. Manual command/result contracts expose equivalent semantics. Keep vectors/internal provenance out of projections. Candidate text obeys sanitation, sensitivity and serialized output-byte bounds; relevance scores remain diagnostic.

## 8. Project/File Changes

Extend existing owners:

- Core managed-memory/search/inspection DTOs, manual command results and prompt catalog; retain historical meanings.
- Context shared search extraction, retriever adapter, memory service and assembler.
- Persistence existing store/storage helpers, forward migrations/registration and connection-owned concept resolver.
- Tools memories input/output/schema, configuration, native input DTOs, definition factory, pipeline/result metadata.
- Execution memory application, existing conversation-loop state, refresh/rebuild and lifetime cleanup; preserve final dispatch ownership.
- Interaction `InteractionShell`/`InteractionCoordinator` and CLI `HeadlessShell` memory parsing/result formatting.
- App DI, deployed prompts/native assets and release verification in `eng/release`.
- Existing ConversationContext, ModelTooling, PersistenceMcpHardening, Planning, CoreRuntime, Architecture, Embeddings.Local and Reranking.Local test suites.

## 9. Ordered Tasks and Gates

1. Register the proposed capability milestone/detail/DAG under planning governance; this document alone owns its work status/prerequisites.
2. Extract direct-query shared search and verify conversational parity. No behavioral migration in this step.
3. Implement reconciliation options, shared service outcome, manual/model resolution, complete-vector reuse and transactional searched-revision fence.
4. Evaluate shipped-model calibration/held-out fixtures; record numerical quality targets, candidate limits, omissions and latency before enabling production reconciliation.
5. Add managed kind/concepts and migration with no-op/vector/revision/usage compatibility.
6. Implement exact lookup and prove native packaging on all six RIDs before completing fuzzy support.
7. Implement concrete native input/schema transport through the existing central pipeline.
8. Extend turn lifecycle/inspection; verify behavior remains unchanged with concept recall disabled.
9. Add calibrated progressive discovery/admission through existing context reconstruction and dispatch.
10. Complete manual, integration, release and owned-document updates; record evidence and limitations here.

Concepts in reconciliation remain an optional later task, requiring measured discovery misses. No phase silently bypasses a failed gate.

## 10. Testing

Use existing deterministic provider/embedding/cross-encoder fixtures for contracts and real shipped assets for quality. Follow repository MTP commands from CONTRIBUTING. Run focused affected projects, solution build/architecture checks and required integration tests before declaring implementation complete.

- Shared search: baseline ranking/calibration, standing preferences, bounds, zero recall limit with reconciliation enabled, independent caches/options, rebuild/error/cancellation paths.
- Reconciliation: exact no-op, paraphrase/correction stop, related-distinct/unrelated behavior, both origins/types, bounded escaped candidates, pair overflow, inference failure, disabled behavior and no false success rendering.
- Concurrency: changed candidate revision, new candidate, stale expected update revision, two connections/processes adding concurrently, no eviction on interruption, transaction rollback, capped retry and no inference under lock.
- Manual surfaces: remember/update/confirmation parsing, repeated flags, malformed IDs/revisions, literal option-like text, legacy syntax, no-write output and headless/interactive parity.
- Metadata: historical migration/serialization, enum schema, omitted/null/clear behavior, Unicode/limits/malicious input, atomic replacement/cascade, both no-op paths, unchanged vector bytes and advanced embedding revision, receipts/cache invalidation.
- Transport: real generated/manual schemas through provider conversion/deserialization, batch preflight/wrappers, denied versus admitted failure hints, duplicate suppression and unchanged MCP/extension inputs.
- Turn integration: empty start, deterministic parallel merge, delta lookup, revision/steering invalidation, degraded retry, same-turn new candidate, frozen-context refresh, compaction, deletion/update/type changes, capacity, stateless/denied memory, repository switch, restart/cancellation cleanup and child isolation.
- Dispatch: no extra generative call, final submitted revisions counted once, required opaque-continuation invalidation and no stale injected text.
- Spellfix: exact-first, accepted typo/rejected noisy-short-term, absent/invalid assets, exact-only CRUD, stale vocabulary discard and published native load/query on every RID.
- Performance: default 20 entries and larger configured capacities/max concepts; snapshot/query/rebuild counts, warm/cold latency and time to visible activity. Capacity-bounded snapshot scans do not justify repository/history scans or repeated per-concept vector inference.

### Implementation evidence and regression ownership

Validation on 2026-10-04 in the active checkout: solution build passed with zero warnings/errors; the complete solution test run reported 3,840 passed, 34 skipped and zero failed (3,874 total). Skips include platform-dependent symlink and opt-in performance/environment fixtures; they are not counted as verification. Source documentation packaging/link closure, release-contract checks and whitespace validation passed. Clean-context adversarial review and re-review found no remaining actionable issue in the added coverage, documentation and release fixture changes. Local command outputs are retained under ignored `artifacts/memory-build.log`, `artifacts/memory-test-verified.log` and `artifacts/memory-release-contracts.log`. Release-contract checks stage and verify the host RID only, not all six native runtimes.

- `MemoryReconciliationTests` covers both origins/types, collision/no-write outcomes, score-independent review, stale confirmation/update revisions, interrupted/incomplete inference, prepared vector reuse, concurrent snapshot changes and bounded retry.
- `MemoryToolFlowTests` drives the production conversation, tool pipeline, shared service and SQLite store with deterministic model decisions: both types receive collisions and choose same-ID superseding or distinct insertion. `MemoryConceptRecallTests` covers Unicode bounds, reserved candidates, conditional retention, updated/deleted entries, cache recovery and context removal.
- Existing CoreRuntime, ModelTooling, NativeTools, PersistenceMcpHardening, Architecture and Skills checks cover manual response/schema/migration contracts. `NativeConceptTransportTests` covers policy admission, normalization, non-serialized hints and duplicate identity. ExecutionOrchestration exercises recall across plan tranches and isolation on the next run.
- Regression coverage found and corrected a composition gap: `RepositoryBoundMemoryStore` must forward `IRepositoryMemoryConceptResolver`, otherwise actual application binding silently skips fuzzy resolution despite direct-store success.
- Isolated live Kimi K2.6 tool calls with actual deployed embedding/cross-encoder assets and SQLite passed 15 exercised checks: both types create, relevant context, standing preferences on unrelated turns, collision feedback, same-ID superseding, updated context, removal and distinct insertion. The exercise used a raw-score cutoff of zero solely to drive the decision branch; it does not calibrate production. A local Qwen fixture emitted tool-shaped prose rather than executable calls, so it supplies no tool-flow evidence. Ignored local reports reside in `artifacts/memory-live/` and are not release evidence.
- Windows x64 loaded the staged spellfix extension and resolved `cancelation` to `cancellation` at edit cost 10. This does not validate the other RIDs or select a production fuzzy distance.

Maintained manual procedures are MTP-260/261/262 plus MTP-276/277; Scenario AM owns acceptance. The test matrix above includes future quality/performance and platform checks; deterministic passing tests alone do not close those gates.

## 11. Security and Permissions

Concepts and memory text are untrusted data. They cannot alter access, approval, claims, repository identity, provider routing or write authority. Preserve sanitizer, sensitivity, escaping, context-mode and disabled-tool rules. Manual and model adds both reconcile, but normal origin/policy/provenance still differ and remain host-owned.

Load only verified app-owned models/native assets. Propagate cancellation at async boundaries. No provider, extension or native library types cross Core/persistent/public contracts.

## 12. Observability

Extend existing memory operation/tool activity, structured diagnostics and `/context inspect`. Show consumer/options identity, branch qualification, complete/degraded scores, ranking outcome, searched repository revision, acknowledged/unacknowledged/omitted candidates and actual commit outcome.

For recall expose active/new/resolved concepts, exact/fuzzy mappings, concept-qualified candidates, reranker rejection, retained admissions and revision/policy/capacity removals. Use bounded host-owned snapshots; no vectors/raw transcripts/hidden reasoning. Manual checks need the same inspectable outcomes; model reconciliation stays inside ordinary memory-tool events/spans. Inspection performs no retrieval or usage accounting.

## 13. Migration and Compatibility

Existing live rows default to Unspecified/empty; existing calls remain valid but add may now report a collision. Preserve MemoryType and historical enums. Exact text uniqueness spans kinds/concepts. Metadata-only updates keep current usage-reset semantics and vector compatibility.

All-type FTS changes corpus statistics and requires measured recall checks. Missing spellfix cannot block core data operations. Follow forward-migration/backup practices and current release tooling. Pin dependencies centrally; no silent database downgrade promise.

## 14. Acceptance Criteria

Completion requires all six phases, calibrated quality and native deployment evidence, not just fake-score tests. Both manual and model adds return bounded discovered candidates for review, never commit unresolved collisions, and truthfully describe failures/no writes. Concurrent change cannot bypass the searched snapshot. Metadata round-trips; concepts reach normal orchestration; new memories can enter a later ordinary continuation within existing budgets; obsolete memory disappears; no extra generative request occurs.

Use Scenario AM and MTP-260/261/262 as existing regression anchors. Extend owning acceptance/manual catalogs when observable behavior is implemented; assign any new IDs there. Preserve receipt, mode, continuation and migration invariants.

## 15. Risks

- Relevance reranking does not classify replacements versus related notes; callers must decide whether to update or confirm a distinct memory.
- Two individually valid notes can exceed the cross-encoder pair window; incomplete comparisons cannot authorize an add.
- All-type FTS changes BM25 corpus statistics.
- Conditional retention can fill the existing small recall budget and omit later candidates; measure usefulness.
- The original conversational query may reject subtask concepts; measure before enabling rather than silently using tool prose as query text.
- Six-RID spellfix deployment is unproven; exact-only fallback does not satisfy final fuzzy-support completion.
- Larger configured memory sets stress snapshot scans and serialized inference; measure before adding infrastructure.

## 16. Documentation

The planning README, M33 index/detail and dependency DAG register this capability; completed milestone details remain frozen.

During implementation update ADR-59 for new arguments/reconciliation/concept discovery, configuration/tool/manual command references, memory/context inspection docs and owning acceptance/manual procedures only where behavior changes.

Model-facing guidance is a deployed prompt asset with flat catalog registration, token checks and publish validation. Any filename/purpose/token change updates `docs/operations/prompts.md` and `docs/prompt-file-reference.md` in the same change. Do not copy divergent concept guidance across tools or use prose as host validation.

Record model/native asset hashes and calibration reports with owning fixtures and link implementation evidence here.

## 17. Decisions and Agent Handoff

**User decisions confirmed during readiness review:**

- Reconciliation applies to both manual and model adds.
- Search both standing preferences and situational memories.
- Treat local inference as an available required dependency; do not introduce an advisory fail-open product mode. Unexpected runtime errors retain normal visible no-write behavior.

**Implementation decisions resolved here:** separate managed Kind enum; composition-based native input concepts; revision-specific confirmation; searched-repository transaction fence; metadata vector-revision reuse; conditional rather than stale-content retention; unchanged conversational reranking query; exact-only fuzzy degradation.

**Architectural follow-up:** the shared engine accepts independent `RepositoryMemorySearchOptions.MaximumResults`; recall maps its context policy while reconciliation maps its own comparison/result limits. Host-owned search details expose branch outcomes and comparison-window/result-limit omissions through existing operation, tool and context-inspection projections. Selection applies one final result bound after retained/current candidates are merged, preserving current ranking evidence. Ranking cache identity includes concept policy and query completeness. The persistence owner reuses one native connection with separate bounded concept and FTS-text vocabularies for the same repository/revision/vocabulary snapshot and discards changed, failed or cancelled derived state. Regression coverage includes independent limits, structured scoring failures/truncation, retention omissions, cache provenance and native vocabulary reuse/invalidation/disposal.

**Follow-up validation (2026-10-04):** solution build passed with zero warnings/errors; the complete rerun passed 3,850 tests, skipped 34 and failed zero (3,884 total). All 185 conversation-context tests and 96 persistence tests passed, including real Windows x64 native vocabulary reuse, replacement and disposal after staging the previously verified local spellfix asset into the ignored test output. Clean-context architectural/native reviews and re-review found no remaining actionable issue in this follow-up. Source documentation packaging/link closure and whitespace validation passed. Local outputs are under ignored `artifacts/memory-search-refinement-build.log`, `artifacts/memory-search-refinement-tests.log` and `artifacts/memory-search-refinement-docs.log`. This does not supply calibration or other-RID runtime evidence.

**Shared discovery follow-up:** both consumers can use exact/fuzzy concept discovery. Reconciliation takes concepts from the proposed add, reserves independent concept candidate slots and ranks against the complete proposed text. Per-scenario `Lexical` expansion limits and `Fuzzy` settings bound typo expansion from the same-snapshot FTS vocabulary; exact matches and scores are preserved, alternatives qualify as one original term, and fuzzy-only lexical matches follow exact matches. Semantic/reranker queries remain unchanged. New settings are documented in the configuration example and conversation-context reference. User configuration carries existing choices forward with missing settings initialized to documented defaults.

**Shared discovery validation (2026-10-04):** solution build passed with zero warnings/errors; full solution tests passed 3,860, skipped 34 and failed zero (3,894 total). Focused suites passed all 193 conversation-context tests and 97 persistence tests. Coverage includes exact/fuzzy concepts during reconciliation, independent full-text ranking (the historical exercise used cutoffs, removed by the ranking-only follow-up), both text-discovery consumers, exact-score preservation, bounded expansion and original-term qualification, repository vocabulary isolation, native text/concept cache coexistence, real Windows x64 FTS typo lookup and revision refresh, plus failed-discovery no-write behavior. Clean-context search and persistence reviews and the ownership re-review found no actionable issue. Source documentation packaging/link validation and whitespace checks passed. Local reports use ignored `artifacts/memory-shared-fuzzy-*.log`. Other-RID runtime and ranking-quality/distance evaluation remain open.

**Remaining evidence tasks:** evaluated candidate windows and held-out quality targets, realistic capacity/latency measurements, and pinned spellfix native load/query validation on all six release RIDs. These are ordered implementation gates, not invitations to invent defaults or request repeated permission.

The shared implementation is present. Reconciliation, concept recall and fuzzy matching remain disabled by default, with fuzzy distance defaulting to 1 and no cross-encoder score cutoffs. Milestone completion remains blocked on the evidence gates above. Report failed gates and unassessed environments explicitly. Do not stage, commit or push unless requested.

**Ranking-only follow-up:** removed all cross-encoder minimum-score options and filtering. Finite negative and positive scores only order discovered candidates; candidate/result bounds control volume, and reconciliation delegates overlap decisions to the caller. Concept-only candidates still require complete successful reranking. Semantic cosine qualification is unchanged. Text lexical, recall-concept and reconciliation-concept fuzzy maximum distances default to 1 in native spellfix weighted edit-cost units. Historical cutoff exercises above are superseded by this contract.

**Ranking-only validation (2026-10-04):** solution build passed with zero warnings/errors. Final full-suite regression passed 3,863 tests, skipped 34 and failed zero (3,897 total); all 195 conversation-context tests passed. Coverage verifies negative-score eligibility, complete concept reranking, caller reconciliation decisions, unchanged semantic qualification and all three fuzzy defaults through configuration binding. Two obsolete fixture assumptions were corrected (zero-score rejection and missing-distance rejection); revision-fence and invalid-zero checks remain covered. Clean-context adversarial review found no remaining actionable issue. Source documentation validation and whitespace checks passed. User memory configuration removed all three score keys, preserved unrelated settings, retained reranking enabled and uses fuzzy distances of 1. Reports are in ignored `artifacts/memory-ranking-only-*.log`. Native validation on other RIDs and production ranking-quality evidence remain outside this follow-up.

**Scenario configuration follow-up (2026-10-04):** configuration now exposes exactly two search blocks, `Recall` and `Reconciliation`, with matching semantic/reranker/concept/fuzzy/lexical settings. One fuzzy policy controls both text and concepts within each scenario. Recall-only count/query limits are nested under Recall; shared storage/resource limits remain top-level. Existing DTOs and the shared search path are reused, with independent reconciliation lexical policy and explicit required reranking. Retired paths produce migration guidance. Startup validates through the same binder before creating or migrating persistence; repository rebinding retains validation-before-publication. User settings were migrated with unrelated settings preserved, both rerankers enabled and fuzzy distances 1.

Validation: solution build passed with zero warnings/errors; full regression passed 3,871 tests, skipped 34 and failed zero (3,905 total). Three subsequently added startup-boundary cases passed in the rebuilt architecture suite (315 passed, four skipped). Tests cover scenario isolation, fuzzy text/concept mapping, layered overrides/reopen, explicit reranker dependency and retired-key validation. Clean-context review found a stale startup binder, which was fixed and re-reviewed with no remaining actionable findings. Source documentation validation and whitespace checks passed. Local logs: ignored `artifacts/memory-scenario-config-*.log`.

**Provider replay follow-up (2026-10-04):** concept-triggered refresh preserves the complete previously delivered prefix while a private provider response envelope is retained. A changed current memory snapshot is appended after completed tool results through the existing continuation-group path, with earlier snapshots explicitly historical. Current inclusions, sensitivity and receipt accounting follow the refreshed selection. Authority changes still fail, actual history rewrites keep their generation fences, and non-replay context replacement retains its generation increment. No adapter replay checks or tool-call correlations were relaxed. The new deployed prompt is registered and documented in the existing catalog.

Targeted verification reproduces the original failure using the production Anthropic adapter, HTTP/SSE fixtures, real response envelopes and the normal host conversation loop. All four cases pass: matching hints, no matches, memory replacement and removal across multiple envelopes. All 108 orchestration tests pass. Clean-context adversarial re-review found no remaining production defect; steering combined with refresh was source-reviewed, not runtime-tested. Solution build has zero warnings/errors; source documentation validation and the corrected architecture suite (315 passed, four skipped) pass. Final full-suite regression passed 3,878 tests, skipped 34 and failed zero (3,912 total). Logs are retained under ignored artifacts/memory-anthropic-replay-complete-build.log, artifacts/memory-anthropic-replay-verified-tests.log and artifacts/memory-anthropic-replay-complete-docs.log. No live provider call or other-RID native validation was performed for this targeted fix.
