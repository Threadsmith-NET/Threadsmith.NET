# Memory recall and reconciliation repairs

**Status:** Implemented — Windows validation recorded in the work log; cross-platform CI remains outstanding.
**Delivery track:** Maintenance.
**Prerequisites:** Shared memory search, intelligence ownership, local inference and conversational context refresh contracts.

## 1 Objective

Improve complete comparisons and current relevance for ordinary and intelligence memories with measured offline evidence.

## 2 Architectural Context

Extend shared retrieval, local inference, context assembly and the existing tool pipeline. Preserve host-owned DTOs and caller-model reconciliation decisions.

## 3 Scope

Canonical 512-token reranker pairs; overflow safety; current relevance over retained slots; independent intelligence concepts; partition inspection; exact mixed-case identifier discovery; measured eight-pair native batch cap.

## 4 Non-Scope

No new model, generative query or reconciliation pass, raw-score cutoff, chunk subsystem or unmeasured default tuning.

## 5 Current State

The audited baseline duplicated framing, clipped documents, retained unrelated entries, gated intelligence hints on ordinary concepts and dropped intelligence inspection details.

## 6 Proposed Design

Frame once and preserve complete candidate memories. Recall uses a common bounded question that prioritizes current instruction endpoints over historical task context; reconciliation preserves both complete texts. Reject pairs that still exceed capacity. Fall back to hybrid ordering for the whole pass; incomplete reconciliation cannot write. Requalify current selections. Collect hints for either active consumer. Reuse lexical lookup for explicit code tokens.

## 7 Public Contracts

Add host-owned overflow failure, optional per-side counts, bounded candidate evidence and independent intelligence inspection. Legacy retention metadata remains accepted but has no selection or cache effect.

## 8 Project/File Changes

Core, Context, Execution, local reranker, Persistence lexical search, Tools output bounds, focused tests and release asset validation.

## 9 Ordered Tasks

Retention, independent concepts, complete pairs, diagnostics, then one-at-a-time measured retrieval experiments.

## 10 Testing

Deterministic tokenizer, SQLite, reconciliation and tool-continuation regressions. Opt-in native inference covers tails, near-limit pairs, overflow recovery and topic changes in both partitions. Offline evaluation uses a read-only intelligence snapshot, the stored standing preference and isolated situational records; timings are observations rather than test deadlines. Expected recall improved from 17/18 to 18/18, with all expected results reaching the assembled prompt. These agent-labelled probes do not establish general production accuracy.

## 11 Security/Permissions

Evaluation does not mutate user data. Asset hashes stay pinned. Diagnostics omit memory text. Existing trust, approval, sanitization and cancellation boundaries remain authoritative.

## 12 Observability

Separate partition outcomes, bounded per-memory stages, model identity, original search elapsed time and pair counts. Existing projections own final prompt inclusion. Tool responses omit detailed rows before they can crowd out actionable collision entries.

## 13 Migration/Compatibility

Reranker identity changes to invalidate ranking caches. Embedding capacity remains 256; its preprocessing and persisted vectors remain unchanged absent a separately validated migration.

## 14 Acceptance Criteria

Both memory types preserve complete comparison and write safety. New relevance can displace earlier selections. Intelligence-only hints reach continuation. Inspection preserves failures. Exact code names survive the lexical term cap.

## 15 Risks

Recall extracts at most 128 question tokens from opening and closing sentences without a model call; instructions whose key content lies only in the omitted middle can still lose relevance. Strict reconciliation never shortens either memory. Existing character admission bounds remain visible as incomplete input. Identifier hints use FTS equality, not compiler symbol identity. Small-corpus results do not establish universal precision or latency.

## 16 Documentation

Mutable operations and acceptance guidance describe behavior. The requested single local work log owns detailed measurements, commands and review outcomes: `.inbox/memory-recall-reconciliation-work-log.md`.

## 17 Open Decisions

Canonical embedding preprocessing and a larger comparison window showed no recall gain in the available corpus; lowering the semantic floor added more unlabelled results than the targeted identifier repair. Keep existing defaults. Richer retrieval text and partition budget changes remain deferred without representative evidence of benefit. Windows packaged payload validation is available locally; Linux/macOS CI and provider-backed interactive checks remain unassessed.
