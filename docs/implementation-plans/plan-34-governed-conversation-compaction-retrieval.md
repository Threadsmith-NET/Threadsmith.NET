# Plan 34 — Governed Conversation Compaction, Retrieval, and Invalidation

**Milestone:** 7.4 (Cross-Turn Conversation Context and Compaction)
**Prerequisites:** plan-33
**Depends on by:** plan-35
**Status:** Complete.

**Implementation result:** Deterministic authoritative promotion, strict candidate validation, bounded once-per-session compaction, atomic snapshots, stable rationale-bearing retrieval, conservative invalidation, soft-failure continuation, and focused Plan 34 tests are implemented.

## 1 Objective

Implement host-governed promotion, structured compaction, deterministic retrieval, supersession, and repository-aware invalidation so older conversation content becomes bounded, attributable session memory without turning an opaque model summary into authoritative state.

## 2 Architectural Context

Plan 33 supplies an immutable archive and typed memory schemas. This plan supplies the policy that derives governed memory from archive/evidence. The host—not the model—decides when compaction runs, which source material is eligible, the schema, validation, provenance, sensitivity limits, repository validity, atomic replacement, and failure behavior.

A model may propose structured memory candidates through the provider-neutral model facade. Its output remains untrusted until schema, category, provenance, source support, size, sensitivity, and supersession validation succeeds. Explicit user decisions and corrections receive stronger preservation than inferred findings.

## 3 Scope

- Promote explicit user requirements, constraints, decisions, corrections, and unresolved questions.
- Promote validated repository findings and completed work from governed evidence/execution records.
- Mark rejected or superseded information without deleting audit history.
- Trigger compaction from token pressure and bounded archive thresholds at turn boundaries.
- Generate a typed `ConversationSummaryCandidate`, validate it, and atomically replace the active snapshot.
- Retrieve relevant older memory using deterministic host scoring and category policy.
- Invalidate repository-dependent memory after file/symbol/project/revision changes.
- Preserve source messages/runs/evidence/artifacts for every promoted or compacted item.
- Make compaction idempotent, cancellable, budgeted, retry-classified, and recoverable.

## 4 Non-Scope

- Raw recent-turn injection into model requests.
- Conversation mode UI/configuration commands.
- Final request category budgeting and inspection rendering.
- External embedding APIs, vector databases, or provider-specific retrieval types.
- Treating assistant claims as facts without evidence or user confirmation.
- Deleting archived messages as a compaction side effect.
- Compacting hidden reasoning.

## 5 Current State

Evidence selection ranks decisions, relevance, recency, and stable ID inside the current run. Exact content is deduplicated and repository invalidation exists for evidence. There is no cross-run memory promotion, typed session summary, conversation retrieval score, compaction trigger, or mapping from repository invalidation to remembered claims.

## 6 Proposed Design

### 6.1 Promotion policy

`IConversationMemoryGovernor` evaluates new archive/evidence at turn boundaries. Promotion sources have explicit trust classes:

1. User-authored explicit requirement/correction/decision/constraint.
2. Host-observed accepted plan, mutation, build, test, or completion result.
3. Repository finding backed by current governed evidence.
4. Assistant-proposed interpretation requiring validation and source support.

User-authored corrections supersede conflicting inferred memory. Host-observed completion cannot be inferred from assistant text; it requires execution events. Repository findings must carry evidence and revision provenance.

### 6.2 Structured compaction request/output

The host builds a bounded compaction request from an immutable archive range, current active memory, supersession graph, and source metadata. The provider receives no secrets or hidden reasoning. Output uses a versioned structured contract grouped into the seven categories, with source message IDs and proposed supersession links.

The validator rejects:

- unknown categories or schema versions;
- missing/nonexistent source IDs;
- content not supportable by cited sources;
- invented completion/validation outcomes;
- excessive item counts or lengths;
- cycles in supersession;
- repository claims without evidence/revision;
- secret-like or unsanitized output;
- duplicate active items;
- attempts to remove explicit user decisions/corrections without a later user source.

### 6.3 Trigger policy

Compaction is evaluated only at turn boundaries. Configurable bounded triggers include:

- archived uncompacted token estimate;
- archived message count;
- expected next-request context pressure percentage;
- maximum active memory item count.

Compaction must never begin during mutation application or while mutable turn state is visible. One compaction operation per session runs at a time. Cancellation retains the previous active snapshot.

### 6.4 Atomic replacement and failure

Write new memory items, provenance edges, and snapshot in one transaction/event sequence. The old snapshot remains active until validation and durable write succeed. Failures classify as malformed output, unsupported provenance, provider transient failure, cancellation, or persistence failure. Retry is bounded and budgeted; ordinary conversation may continue using the prior snapshot unless hard safety validation failed.

### 6.5 Retrieval

`ConversationMemoryRetriever` scores eligible active items using host-owned inputs:

- exact/normalized terms from current task and acceptance criteria;
- category priority appropriate to the execution phase;
- explicit user-authored priority;
- unresolved-question relevance;
- repository path/symbol overlap where provenance exists;
- recency and repeated confirmation;
- supersession/validity state;
- sensitivity and mode policy.

Tie-breaking is stable by category, score, source sequence, and memory ID. The first implementation is deterministic lexical/metadata retrieval; embeddings remain optional future work. Retrieval returns rationale and provenance, not only content.

### 6.6 Invalidation and supersession

Repository-dependent memory subscribes to the same turn-boundary invalidation signals as evidence. File/symbol/project/revision changes mark affected items stale. Stale items remain archived and inspectable but cannot be selected until revalidated. User constraints and preferences are not repository-invalidated unless explicitly scoped to repository facts. A later user correction creates a superseding item and demotes the older item to `RejectedOrSuperseded`.

### 6.7 Preservation rules

- Current user message is never compacted before its run completes.
- Explicit user requirements, decisions, constraints, and corrections remain verbatim in archive and as bounded typed memory.
- Full messages are archived; compaction never deletes them.
- The summary snapshot references memory IDs, which reference original provenance.
- A model-generated phrase never replaces the source record.

## 7 Public Contracts

Host-owned contracts may include:

- `IConversationMemoryGovernor`;
- `IConversationCompactor`;
- `IConversationSummaryValidator`;
- `IConversationMemoryRetriever`;
- `ConversationCompactionPolicy`;
- `ConversationSummaryCandidate`;
- `ConversationRetrievalRequest/Result/Rationale`;
- compaction/retrieval/invalidation events and inspection DTOs.

Provider-neutral structured-output contracts remain in `Threadsmith.Models` or `Threadsmith.Context`; no concrete provider types leak.

## 8 Project/File Changes

- `Threadsmith.Context` — governor, compactor, validator, retriever, policies, invalidation integration.
- `Threadsmith.Execution` — turn-boundary trigger and budgets; accepted-plan/result promotion.
- `Threadsmith.Models` — provider-neutral structured compaction request/output schema if needed.
- `Threadsmith.Persistence` — atomic snapshot/memory transactions and query indexes.
- `Threadsmith.Telemetry` — secret-free compaction/retrieval spans and counters.
- Context, execution, persistence, and model fake-script tests.
- ADR-31 and DOX references as implementation lands.

## 9 Ordered Tasks

1. Define compaction policy, candidate, validation, retrieval, and rationale contracts.
2. Implement deterministic promotion of explicit user requirements/corrections and host-observed decisions/results.
3. Implement repository-finding promotion only from current governed evidence.
4. Add turn-boundary compaction trigger with one-operation-per-session concurrency and linked cancellation.
5. Assemble bounded provider-neutral compaction requests and fake-model scripts.
6. Implement strict candidate validation, source support, sensitivity checks, and supersession-cycle rejection.
7. Persist validated items/snapshot atomically; preserve the prior snapshot on every failure path.
8. Implement deterministic lexical/metadata retrieval with stable tie-breaking and phase category policy.
9. Connect evidence/repository invalidation to repository-dependent memory and revalidation.
10. Add structured events, metrics, logs, and context-inspection source data.
11. Add adversarial, cancellation, budget, idempotency, restoration, and property-based ordering tests.
12. Update architecture/context documentation and DOX.

## 10 Testing

Automated tests must verify:

- explicit user corrections supersede older conflicting memory;
- explicit decisions/constraints survive repeated compactions;
- assistant assertions cannot fabricate accepted work or repository facts;
- missing/wrong provenance and supersession cycles are rejected;
- compaction runs only at turn boundaries and once per session;
- cancellation/provider/persistence failure preserves the prior snapshot;
- retry and token/call/cost budgets remain bounded;
- repeated compaction over the same source range is idempotent;
- retrieval is deterministic, phase-aware, provenance-preserving, and excludes stale/superseded items;
- file/symbol/revision mutations invalidate dependent findings but not unrelated user preferences;
- full message archive remains unchanged after compaction;
- secret canaries never enter candidates, snapshots, logs, events, or artifacts;
- restored sessions retrieve the same eligible memory in stable order.

## 11 Security/Permissions

Archived conversation and model-generated candidates are untrusted. Sanitize inputs/outputs, apply strict schemas and hard limits, require source IDs, prohibit secret expansion, and treat repository-dependent claims as stale after invalidation. Repository configuration may lower thresholds or choose mode in plan 35 but cannot authorize unsupported claims, remove provenance, include hidden reasoning, or disable hard bounds.

## 12 Observability

Emit compaction trigger reason, source range IDs, item counts by category, token estimates, validation classification, retry count, duration, snapshot version, invalidation counts, retrieval candidate/selected counts, and bounded rationale codes. Never log content, full prompts, message bodies, hidden reasoning, secrets, or raw model output.

## 13 Migration/Compatibility

Sessions created by plan 33 but not yet compacted retain an empty summary and archive-only state. The governor incrementally promotes/compacts on a later turn; it does not rewrite message IDs. If compaction is disabled by mode, memory may still be maintained for later mode changes according to plan 35 policy, but it is not selected.

## 14 Acceptance Criteria

- Older conversation is represented by structured, source-linked memory rather than an opaque paragraph.
- All seven categories are promoted/compacted under host validation.
- Explicit user decisions and corrections survive compaction and supersede older conflicts.
- Retrieval returns relevant older memory with deterministic rationale and provenance.
- Repository-dependent claims invalidate at turn boundaries after relevant mutations/revisions.
- Compaction never deletes archived messages and never replaces the active snapshot on failure.
- No unsupported assistant claim, secret, hidden reasoning, or provider-specific type becomes governed memory.

## 15 Risks

- Model summaries can hallucinate. Mitigate with source-bound structured candidates and validation; preserve source archive.
- Lexical retrieval may miss paraphrases. Prefer deterministic correctness first; evaluate embeddings later behind host-owned interfaces.
- Over-promotion can pollute memory. Use trust classes, category limits, deduplication, and explicit supersession.
- Compaction adds model cost/latency. Run at turn boundaries, budget separately, and continue safely on soft failure.
- Invalidation may be too coarse. Start conservative and expose rationale/counts for tuning.

## 16 Documentation

Document compaction contracts, validation, trigger defaults, invalidation, failure preservation, observability, and the explicit deferral of embeddings. User-facing configuration and commands belong to plan 35.

## 17 Decisions

- Local embeddings remain deferred until deterministic lexical/category retrieval produces measured relevance gaps. The host-owned retrieval interface permits a later implementation without changing durable memory contracts.
- User-authored requirements, decisions, and constraints remain active for the owning session until explicitly superseded or invalidated by host policy; they have no time-based expiration in Milestone 7.4.
