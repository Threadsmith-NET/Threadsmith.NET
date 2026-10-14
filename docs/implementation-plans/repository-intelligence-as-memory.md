# Repository Intelligence as Memory

**Status:** Implementation signed off; documentation and regression verification complete.  
**Delivery track:** Repository Intelligence and Archeology capability; milestone registration remains separate from this planning-only change.  
**Prerequisites:** Existing managed repository memories, shared hybrid recall and reconciliation, ordinary tool/model execution, workspace lifecycle, and context inspection on `main`.  
**Implementation baseline:** `feature/intelligence`, created from `main` at `ed6627c`.  
**Authority:** This single document replaces the earlier intelligence requirements and twenty-task breakdown for the new implementation. Those documents and `feat/repository-intelligence-t01-boundary` are reference material only; their proof machinery, limits, and per-task approval procedure are not requirements of this plan.

## 1 Objective

Implement repository intelligence as repository-scoped memories with architectural assertions, concepts, and primary supporting files. Use the existing vector, lexical, concept, reranking, reconciliation, persistence, and context assembly machinery. Guidance is advisory; changed evidence produces a warning, never a freshness gate on recall.

Intelligence defaults OFF. Only explicit user onboarding creates the initial intelligence collection. Enabling configuration alone must not explore the repository or create memories. Most feature-specific code belongs in `Threadsmith.RepositoryIntelligence`.

## 2 Architectural Context

Follow [AGENTS.md](../../AGENTS.md), [planning governance](planning-governance.md), [shared context section G](00-shared-context.md#g-implementation-document-template-and-agent-instructions), [ADR-59](../architecture/adr-59-model-managed-repository-memories.md), and [context policy](../architecture/context-policy.md). Read the C# guardrails before implementation.

Project separation establishes ownership, not permission to duplicate infrastructure. Extend existing owners only for capabilities they lack. Core owns cross-project DTOs; Persistence owns SQLite changes; Context owns shared retrieval and assembly; Execution owns model/tool execution; Interaction and concrete frontends own presentation. Intelligence owns collection policy, source tracking coordination, onboarding, incremental refresh, and archeology orchestration through those owners.

## 3 Scope

- Intelligence memories in the existing repository-local memory store, with source provenance and dirty state.
- Independent intelligence recall/reconciliation configuration using existing search implementations, and a separate maximum intelligence inclusion count.
- Explicit onboarding/re-onboarding and incremental refresh; optional commit-triggered refresh, default OFF.
- Supervised background file-change monitoring with clean cancellation and visible failure handling.
- Archeology using current memories, source, and Git history on demand.
- Shared memory output role and complete context-viewer coverage for every memory category.
- Enabled-only exploration prose encouraging reuse, historical investigation, source verification when appropriate, and clarification of conflicting requests.

## 4 Non-Scope

No parallel memory database, retrieval/ranking engine, reconciliation engine, context assembler, renderer, model loop, or tool pipeline. No continuous model-driven maintenance option. No per-turn file scans, source rereads, Git-index fingerprints, dependency proofs, semantic freshness validation, or cache introduced to accelerate such proofs. No requirement that SQLite contain all history needed for archeology. No automatic onboarding. No changes to source files as a side effect of onboarding, refresh, or archeology.

## 5 Current State

Verified existing owners on the new branch:

| Owner | Reuse/extension |
|---|---|
| `Context/HybridRepositoryMemoryRetriever.cs` and `.Reranking.cs` | Shared lexical, vector, concept and cross-encoder search; add category filtering/configuration through this path. |
| `Context/RepositoryMemoryService.cs` | Existing add/update/remove, embedding reuse for metadata-only changes, and reconciliation search. Related candidates return `reconciliationRequired`; callers resolve through the existing interaction. |
| `Core/ManagedMemoryContracts.cs` | Existing memory entries, write DTOs, types and options; extend provenance/category without duplicating records. |
| `Persistence/SqliteManagedRepositoryMemoryStore*` | Existing memory persistence and indexes; own migrations and source relationships here. |
| `Context/ContextAssembler.cs`, `Execution/RepositoryMemoryDispatch.cs` | Shared inclusion, context capacity, sensitivity, and actual-dispatch accounting. |
| `Interaction/Coordination/InteractionCoordinator.cs`, `InteractionShell.cs` | Existing inspection includes repository-memory projections. Verify actual modal content, not just DTO presence. |
| `Workspaces/RepositoryLifecycle.cs`, existing Git tools | Workspace lifecycle and history/source access owners. Commit observation must not launch native Git commands. |
| `DotNet/SemanticRefreshCoordinator.cs` | Existing filesystem watcher ownership, directory coverage, rename/error handling, coalescing and disposal. Reuse/extend its observation mechanism before considering any additional watcher. Its downstream semantic relevance filtering is not sufficient for intelligence supports. |

Before editing each area, trace its actual manual, model, internal and frontend call sites. Older branch code may inform intelligence gathering only after this ownership check.

## 6 Proposed Design

### 6.1 Memory representation and configuration

Extend existing memories with an intelligence category and optional supporting-source metadata. Keep memory text, concepts, embedding, stable ID, revisions, and normal reconciliation. Category must not grant authority or relevance bonuses. Keep intelligence distinct from always-injected standing preferences.

Each primary support records a repository-relative path, the identity of the content used during collection, and dirty state; record the refresh time and revision when available. A memory's dirty flag summarizes its supports. Deleted/renamed supports count as changed. Preserve an explicit unknown/monitoring-gap signal rather than interpreting absent observation as proof of unchanged content. Store normalized source relationships for efficient path-to-memory updates.

Provide intelligence `Enabled` (default false), `Recall`, `Reconciliation`, and `RefreshOnCommit` (default false) settings through normal configuration layering. Reuse shared search option types. Intelligence has its own configurable maximum inclusion count, independent of ordinary contextual memory and preference limits; all remain subject to the existing total context budget. The exact inclusion default should follow existing memory defaults, not an invented new proof/acquisition ceiling. Intelligence creation uses the existing reconciliation interaction by default; independent configuration does not permit an alternate writer.

Add one global memory-output setting, default true, applying to all memory categories. Turning this display off does not disable recall or hide content from the context inspector. Document effective-setting application using existing configuration lifecycle conventions.

#### Storage capacity and ownership isolation

Intelligence has separately configured and enforced storage capacity as well as separate recall limits. Extend the existing store with two ownership partitions: ordinary memories (including existing contextual/preference behavior) and intelligence. Existing `MaxNumberOfRepoMemories` applies only to the ordinary partition; intelligence has its own storage maximum. Never borrow capacity or evict across this boundary. Preserve existing ordinary/preference rules rather than silently redefining them.

Apply partition-scoped counting and eviction in every add, batch replacement, refresh, explicit capacity enforcement and repository-bind path, including configuration reductions. Ordinary writes cannot evict intelligence, and intelligence writes cannot evict ordinary memories or preferences. Disabling intelligence preserves its records and must not apply ordinary capacity to them. Keep one SQLite store and one eviction implementation parameterized by ownership. Report intelligence capacity exhaustion/evictions through existing operation results; do not silently claim complete onboarding if assertions were omitted. Storage settings are independent of context inclusion counts and the shared total context budget.

#### Exact duplicates and reconciliation ownership

Make intelligence ownership explicit and immutable through ordinary memory updates. Scope exact-text duplicate detection and the SQLite unique content-hash constraint by repository and ownership partition. Preserve current duplicate behavior within the ordinary partition, including contextual/preference duplicates, and within intelligence. Migrate existing rows to ordinary ownership without changing IDs or contents.

Identical text may exist once in each partition with independent IDs, provenance, capacity and lifecycle. An ordinary duplicate must not short-circuit an intelligence add, acquire intelligence ownership, or become subject to re-onboarding deletion. Cross-partition matches remain visible through shared reconciliation as informational related guidance; collection may resolve mutations only against intelligence IDs. The established distinctness confirmation can acknowledge a separately owned ordinary match before adding intelligence. Do not introduce another reconciliation engine. Within intelligence, identical text with changed source provenance must update provenance through the shared metadata-only path instead of returning an unchanged duplicate that loses the new supports. This must not regenerate a compatible text embedding.

### 6.2 Onboarding and feature state

Explicit user onboarding explores the current repository through ordinary read/exploration tools and model execution, identifies useful architectural assertions and primary supports, and submits memories through existing reconciliation. Prefer assertions that explain ownership, existing implementations, conventions, and significant design decisions. Do not preload entire history.

Collection uses a host-enforced capability restriction on the existing execution/tool-policy path: permitted source/history reads and intelligence-memory operations only. Exclude source mutations, arbitrary shell/process execution, external writes, configuration changes and ordinary/preference memory mutations. Approved read-only Git tools may use their existing internal process implementation; this does not expose arbitrary process execution to collection. Intersect these capabilities with existing user/tool permissions; collection never expands them. Validate target ownership for update/remove/replace in the shared memory service as well as at the tool boundary, not merely in prompts or a displayed tool list. Lifecycle activation and replacement remain host command responsibilities.

Reuse the existing memory tool and reconciliation interaction with this operation-scoped write authority; do not classify memory writes as read-only to bypass policy. If delegation is used, children remain source-read-only under the existing delegation filter and return proposed assertions/supports to the owning collection run. Only that run performs authorized intelligence writes through the shared service. This avoids granting the current read-only child a workflow tool it cannot legally execute. Automatic refresh uses the same restricted execution authority; archeology is source/history-read-only and does not acquire collection write authority.

Successful explicit onboarding activates intelligence for that repository through the existing settings mechanism. Configuration enablement without onboarding reports that onboarding is required and performs no collection. Disable preserves stored memories but stops intelligence recall, tools/prose activation, monitoring, and automatic work. Re-enabling a previously onboarded repository does not rebuild it.

Re-onboarding replaces only intelligence memories, never ordinary memories or preferences. Build replacement results before the transactional replacement so failure/cancellation preserves the prior usable collection. Reuse the existing memory validation/reconciliation and persistence owner; do not build a second durable staging store. Onboarding must record sources as actually observed; edits during collection remain dirty instead of being cleared by completion.

### 6.3 Background monitoring

Reuse/extend the existing filesystem observation mechanism in `SemanticRefreshCoordinator`; do not create a parallel repository watcher for intelligence. That implementation already owns file notifications, watcher-root coverage, rename/error handling and disposal. Expose shared host-owned notifications before semantic relevance filtering, with independent semantic and intelligence consumers. If its current placement prevents this, extract only the observation/lifetime portion into an appropriate shared owner and migrate the existing semantic consumer to it. Do not copy the watcher or introduce a general-purpose monitoring framework.

Use a repository-scoped, supervised .NET background worker to consume/coalesce those notifications and update intelligence bookkeeping. Prefer asynchronous event/channel processing; a dedicated long-running thread is appropriate only for genuinely blocking work. Do not hold a thread-pool thread in a permanent blocking loop or launch unobserved fire-and-forget tasks. Intelligence must work without a loaded semantic model; neither its startup nor dirty-state processing waits for semantic refresh.

Observe additions, edits, renames and deletions throughout the relevant repository, including non-C# sources and changes from external tools. Exclude host database/log/temp outputs and established generated-output exclusions to avoid feedback loops; do not silently exclude a declared primary support. Ensure shared watcher coverage includes intelligence sources such as Markdown even where semantic relevance filtering would discard their changes. Harness-owned writes must also dirty affected intelligence; semantic echo suppression must not suppress the intelligence notification. Keep consumer filtering independent so broader observation does not trigger unnecessary semantic refreshes.

Callbacks enqueue paths; the worker updates dirty state through shared persistence. It performs no generative inference. Dirty-state updates do not re-embed unchanged memory text. Track newly changed paths even when no memory cites them, so refresh can discover new architecture. Recall only reads stored state and never waits for the monitor, hashes files, or catches up a scan.

Persist changes needed for later refresh. On restart/re-enable, reconcile the observation gap in the background using recorded supports/change checkpoints. Until covered, label potentially affected provenance as uncertain. Handle watcher overflow as lost observation, never silently continue claiming clean state. Coalesce noisy events; use existing resource policies and a recovery marker rather than silently dropping affected paths.

Feature disable cancels and awaits the intelligence consumer and releases its subscription without stopping observation still required by semantics. Repository close/switch and application shutdown dispose the shared observer through its owner and cancel/await consumers using the existing shutdown boundary. Cancel automatic refresh as part of the same lifecycle. Do not permit late callbacks to update a new repository or reactivate the feature. An intelligence-consumer failure must not disable semantic monitoring; shared observation failures notify both consumers so each applies its existing recovery/failure contract.

An unexpected worker failure, persistence failure that prevents tracking, or unrecoverable observation loss emits a visible output error and structured event, then disables effective intelligence for that repository: no further intelligence recall, automatic refresh, or enabled-only prose. Preserve data and ordinary memories. Retain a fault marker so restart does not silently hide the failure. Expose the reason; explicit re-enable after the cause is corrected must initialize healthy monitoring and account for the gap before clearing the fault. Expected cancellation is not a crash. Monitoring failure must not crash the application.

### 6.4 Advisory recall

Use the same hybrid recall, concepts, reranker, admission, and final-dispatch path as ordinary memories, with intelligence's own configuration and inclusion count. Separate category settings may call the shared search with category filters; they do not justify another search algorithm or all-type retrieval followed by truncation that starves a category.

Inject primary supporting paths with guidance. For dirty evidence, include wording equivalent to:

> One or more key supporting files seem to have changed since this intelligence was refreshed. You may want to read these files to check whether the assertions remain accurate. If they conflict, make choices based on the current files as the overriding authority.

Identify affected paths where known; state uncertainty for an observation gap. No warning suppresses an otherwise eligible memory or automatically triggers source/model validation. Normal policy and context limits still apply. The model can reread sources using ordinary tools when useful. Warning metadata must reach the actual request and context viewer without modifying the embedded assertion text or forcing re-embedding.

#### Metadata and continuation invalidation

Every persisted support/provenance or dirty/unknown-state change advances the existing repository memory-set revision atomically with the metadata update. Separate that revision from embedding content identity: unchanged assertion text retains its compatible vector. Shared retrieval caches containing complete candidates must observe the new revision and reload metadata; update frozen selections and assembled context through the existing continuation invalidation path.

Include effective intelligence enablement/fault state and relevant configuration in that same path's state comparison, alongside ordinary memory enablement. A disable/fault transition invalidates already-selected intelligence and enabled-only prose before the next actual request, even if the database write recording the fault failed. Re-enabling also invalidates cached eligibility. Reuse existing final-dispatch/continuation rebuilding and provider-context invalidation; do not add an alternate request builder. Recheck this lightweight in-memory/database state at the existing submission boundary, without reading source files or waiting for the monitor. Already-submitted requests cannot be retroactively changed; subsequent requests must reflect the latest observed state.

### 6.5 Incremental and optional commit refresh

Explicit refresh uses dirty supports and all added/modified/deleted paths since the last successful refresh. It revisits affected assertions and can discover new assertions in changed areas. The model resolves reconciliation candidates using the existing memory workflow, including updating stable IDs, retaining distinct assertions, or removing obsolete intelligence. Ordinary user preferences are not silently overwritten by architectural collection; preserve provenance and use existing reconciliation to surface cross-category conflicts.

Clear only the dirty work successfully covered by a refresh. Changes arriving during that refresh remain pending, using a simple change sequence/checkpoint. Cancellation or failure retains outstanding work and prior usable memories. Serialize overlapping onboarding/refresh operations through existing execution ownership.

Optional automatic refresh observes commit metadata without calling native Git executables or depending on a particular commit command/tool. Support commits from Threadsmith and external applications, linked worktrees, and reference updates. Reuse or narrowly extend the workspace/Git owner for detection; do not hand-build another Git object/history engine. Distinguish commit creation from checkout/reset/reference maintenance. Metadata notifications alone are not proof of a new commit. The implementation review must demonstrate the chosen detection mechanism; do not substitute generic HEAD-change refresh without documenting the limitation.

Queue/coalesce commit-triggered work and run it visibly through normal execution, model/tool policy, cancellation and concurrency controls. It must not append uncontrolled inference to an active foreground turn or block ordinary recall. File changes alone never trigger model refresh. When the app is closed, no monitoring daemon is introduced; account for changes on the next repository open and report any pending refresh.

### 6.6 Archeology and exploration instructions

Archeology starts with recalled intelligence and investigates current code and Git history through existing tools as needed. It is on-demand, not a separate historical database-building exercise. Ordinary tool history reads remain available; the prohibition on native Git applies to automatic commit detection.

Only when intelligence is effectively enabled and onboarded, append deployed exploration prose strongly suggesting that the model use intelligence/archeology to understand the code before implementation, reuse or extend existing functionality, follow similar implementations and established patterns, and investigate why earlier approaches failed. Explicit user direction can request a different approach.

The prose must also instruct the model to stop and ask the user for clarification when intelligence appears to conflict with the requested task. Present the apparent conflict rather than silently treating remembered guidance as a veto or silently overriding the request. This is model guidance, not a host-enforced validity gate. Describe the advisory nature of stale evidence and current-file authority.

### 6.7 Output and context viewer

Use one shared Memory TUI role for actual memory additions to submitted context, with labels for intelligence, contextual memories, preferences and other existing memory categories. Show the included content and intelligence support warnings. Emit through existing host events/projections so frontends and headless consumers see consistent inclusion facts. Retrieval candidates and entries omitted by final capacity admission must not be presented as injected. Avoid duplicate display caused solely by transport retries.

The context viewer modal must show ALL memories in the inspected context, including their actual included text, category and applicable provenance/warning, for initial and continuation requests. Verify conversation-derived memories as well as repository memories and standing preferences. This inspection remains available when memory output is disabled. Preserve existing sensitivity handling and context accounting.

## 7 Public Contracts

Extend existing memory entry/write/search/options/inclusion DTOs and persistence contracts only where necessary. Introduce host-owned support-path and dirty/unknown metadata without leaking watcher, Git-library, SDK or terminal types. Reuse existing command registration for user onboarding, refresh, enable/disable and status, and ordinary tool schemas for model-facing intelligence/archeology access. Onboarding/reset authority belongs to the user; a model cannot silently onboard or replace the collection.

Finalize command spelling and configuration paths against the established catalog before implementation; keep a single public spelling per action. Preserve existing memory API behavior for callers that supply no intelligence fields.

## 8 Project/File Changes

- `Threadsmith.RepositoryIntelligence`: majority of new feature code; collection, refresh, monitoring coordination, feature state and archeology policy.
- `Threadsmith.Core`: minimal shared memory/provenance/configuration/event contracts.
- `Threadsmith.Persistence`: existing store schema and transactional source/dirty bookkeeping.
- `Threadsmith.Context`: extend shared search and context output for category configuration and provenance.
- `Threadsmith.Workspaces`: focused file/commit observation capability only if existing services lack it.
- `Threadsmith.Execution`, `Threadsmith.Tools`, `Threadsmith.App`: existing command/tool execution, composition, dispatch and shutdown wiring.
- `Threadsmith.Interaction` and active TUI: shared memory role and complete context-viewer presentation.
- Existing deployed prompt assets/catalogs and focused tests: update in their current owners.

Do not import the old feature wholesale. Every reused fragment must have a named need and fit the established path. Keep one implementation document rather than recreating twenty subplans.

## 9 Ordered Tasks

1. Trace shared owners, actual recall/reconciliation call sites, configuration, inclusion rendering and lifecycle; record narrowly missing capabilities here.
2. Extend shared schema/configuration for intelligence and provenance, preserving ordinary memory behavior and independent inclusion counts.
3. Wire intelligence retrieval through shared recall and expose all memory categories through the common output role and inspector.
4. Extend/extract the existing observation mechanism for both consumers, preserving semantic behavior; implement intelligence change tracking, cancellation, fault disablement, restart-gap handling, and cheap advisory dirty metadata.
5. Implement explicit onboarding/replacement and incremental refresh using normal exploration, execution and reconciliation.
6. Add opt-in commit observation and refresh scheduling without native Git detection; test real external commits and worktrees.
7. Add on-demand archeology and enabled-only exploration prose, then perform end-to-end review and measurement.

At each step, perform adversarial review against user behavior and surrounding existing code: ownership/reuse, observable integration, work proportional to scope, and concrete evidence. Fix demonstrated issues before proceeding. Run meaningful tests with each increment. Do not add approval checkpoints inherited from the discarded plan; subsequent implementation still requires a user request to begin.

## 10 Testing

- Existing ordinary-memory recall, reconciliation, preferences, concepts and embeddings remain correct; intelligence uses the same engine and writer.
- Independent maximum counts work under shared context capacity, including zero limits and mixed-category results.
- Fill ordinary storage (including preferences), then onboard/refresh to intelligence capacity: ordinary IDs/text remain unchanged. Fill intelligence storage and add ordinary memories: intelligence remains unchanged. Exercise both capacity reductions, repository rebind, disabled intelligence, and batch re-onboarding; eviction stays in the relevant partition and omissions are reported.
- Onboard/re-onboard assertion text identical to an ordinary memory and a preference: separate intelligence provenance is retained, ordinary IDs/ownership survive, and replacement removes only intelligence. Same-partition duplicates retain existing behavior; same-text intelligence refresh updates supports without embedding inference.
- Metadata-only dirty updates do not invoke embedding or generative inference; recall does not read source contents, invoke Git, or wait for monitoring.
- In a real multi-round execution, first inject clean intelligence, then mark a support dirty/unknown without changing text: the next actual provider request and inspector show the warning. Repeat with monitor fault/disable, including failed fault persistence: subsequent requests omit intelligence and enabled-only prose. Cover retained selections, frozen context and provider continuation reuse, not only new assemblies.
- Attempt source writes, arbitrary processes, ordinary/preference-memory updates and cross-partition ID substitution from onboarding and automatic refresh: host policy/service rejects them. Authorized intelligence reconciliation succeeds through the normal tool path. Read-only delegated collection returns proposals without acquiring memory-write authority.
- Monitor handles external edits, additions, deletes, renames, noise, overflow, restart gaps and changes during refresh. Cancellation on disable/close/shutdown leaves no worker, callbacks or inference running.
- Shared observation covers Markdown/non-semantic supports and harness-owned writes without duplicate watchers or unnecessary semantic refresh. Disabling/faulting intelligence leaves semantic monitoring operational; intelligence also works without a loaded solution. Shared watcher errors reach both consumers and obsolete-binding notifications are discarded.
- Injected monitor faults visibly disable intelligence and preserve ordinary memories and stored guidance; successful explicit recovery clears the fault appropriately.
- Default OFF and enable-without-onboarding create no intelligence. Re-onboarding replaces intelligence only, with failure/cancellation preserving prior records.
- Incremental refresh revises/removes affected facts, discovers facts in new files, retains concurrent changes and exercises real reconciliation collisions.
- Commit option OFF performs no automatic refresh; ON observes internal/external commits without Git-process detection, avoids duplicate runs and handles worktrees, checkout/reset and active foreground work.
- Output shows only actually included memories; global display setting applies to all types. Context modal shows every included category, content and support warning regardless of display setting.
- Enabled-only prompts encourage clarification of task conflicts; archeology can retrieve information absent from SQLite through ordinary history/source tools.
- Measure warmed recall latency versus ordinary-memory baseline and monitor cost on realistic repository changes. Report local embedding/reranker time separately from collection/refresh model latency. No fabricated absolute latency target or arbitrary proof-size ceiling.
- Use focused provider-backed onboarding, incremental refresh and archeology checks where needed to establish integration; report results and limitations without treating passing fixtures as architectural proof.

## 11 Security/Permissions

Stored guidance and repository text remain untrusted context, not policy. Existing tool permissions, sensitivity routing, sanitization, source-path confinement, execution authority and cancellation continue to apply. Background monitoring performs bookkeeping only; it grants no new model or mutation authority. User onboarding permits intelligence replacement, not removal of ordinary memories. No secrets in diagnostics or automatic unrestricted history collection.

## 12 Observability

Expose disabled/not-onboarded/ready/refreshing/faulted state through existing status and activity projections. Show collection and refresh progress, completion, cancellation and failure. Show dirty-support warnings when guidance is injected. Monitor failure includes an actionable reason and visibly states intelligence was disabled. Do not emit a noisy output line for every filesystem event.

## 13 Migration/Compatibility

Extend the existing SQLite migration path. Existing memories retain semantics and require no artificial supporting files. Default-OFF introduction leaves existing applications unchanged except the requested default-enabled shared memory display. No migration from the abandoned branch's separate intelligence database is required for this restart; do not load its schema opportunistically. Amend current architecture/configuration contracts when implementation introduces category-specific search settings; do not rewrite historical completed plans.

## 14 Acceptance Criteria

The feature is complete only when each requirement in sections 3 and 6 is implemented and tested through real entry points, ordinary memories regress cleanly, the majority of feature logic lives in the Intelligence project, and review finds no competing recall/reconciliation/store/execution path. Changed supports remain advisory. Explicit onboarding and default-OFF behavior are mandatory. Monitoring adds no synchronous source-validation step to context assembly. All included memories are inspectable. Commit refresh is optional and its detection does not launch Git.

## 15 Risks

Filesystem events are lossy and eventual: preserve uncertainty and reconcile gaps in the background rather than promise instantaneous correctness. Commit/reference changes have different causes: demonstrate detection semantics before declaring commit automation complete. Collection can race edits: preserve pending changes rather than implement dependency proofs. Category additions can accidentally bypass existing FTS filters, reconciliation, capacity or continuation handling: test their actual call sites. Shared presentation can accidentally show candidates as included: use actual dispatch evidence.

## 16 Documentation

This is the sole new implementation document. Add one README navigation link. During implementation, update relevant acceptance scenarios and manual procedures for changed observable behavior, operating/configuration documentation, and ADR-59/current context contracts where needed. Any prompt filename/purpose/token-contract changes update both `docs/operations/prompts.md` and `docs/prompt-file-reference.md`. Milestone lifecycle remains owned by `milestones.md`; this planning-only change does not claim implementation completion.

## 17 Open Decisions

No further product clarification is required to begin once implementation is requested. Resolve exact command/configuration names, the shared representation of intelligence category, and the commit-observation mechanism by inspecting existing owners in task 1. Record concrete choices here. If commit detection cannot distinguish the requested cases without disproportionate machinery, surface that specific limitation rather than silently changing behavior or building a new Git engine.

## 18 Implementation decisions and verification

The commands are `/intelligence onboard|refresh|archeology <question>|enable|disable|status`; settings live under `tools:config:memories:Intelligence`. The default-on global memory display setting is `tools:config:memories:ShowMemoryOutput`. Intelligence uses migration 14 and the shared memory service/search/writer, with separate ownership capacities and provenance. App owns the shared file observer; DotNet and intelligence subscribe independently. Workspaces uses LibGit2Sharp for automatic detection without native Git processes.

The user accepted validated standard reflog creation records: custom/suppressed reflogs may be missed, with a visible limitation and manual refresh available. The user also chose to hide disabled intelligence from ordinary model listing while preserving manual inspection. Implementation sign-off authorized deferred documentation and test changes. Acceptance behavior is Scenario BA; manual verification is MTP-278. Operational settings and the ADR-59 amendment are the durable contracts.

Clean-context review and provider-backed checks exercised onboarding/replacement, incremental refresh, archeology, cancellation, external commits/worktrees, changed-evidence continuations, disabled/enabled listing, and shared memory display using GPT 6.1 Sol at medium. Controlled runtime checks covered ownership/capacity preservation, metadata-only vector reuse, monitor recovery/lifecycle, post-checkpoint commit retention and terminal-control encoding. Small-fixture warmed recall measurements do not establish realistic-repository throughput. Full-screen TUI input/draft timing and provider-backed fault revocation with failed fault persistence remain unassessed; rendering was checked through the actual modal and cell buffer.

Final contract verification: the Architecture, CoreRuntime, RepositoryLifecycle, PersistenceMcpHardening, ExecutionOrchestration, Skills, ModelTooling and ConversationContext suites reported 2,502 passed, zero failed and 19 explicitly skipped tests. The solution built with zero warnings/errors. Focused prompt inventory and collection-checkpoint tests passed after the final documentation and test-liveness edits. Clean-context re-review found no remaining actionable findings in those changes. A final GPT 6.1 Sol/medium archeology run read only the two requested source files, returned the storage boundary, and displayed actual ordinary/intelligence memory context through shared output. The skips and unassessed environments above are not claimed as passing runtime coverage.
