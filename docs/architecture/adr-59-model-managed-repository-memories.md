# ADR-59: Model-managed repository memories with hybrid retrieval

Status: Accepted

Supersedes the automatic creation, authority categories, invalidation, preservation, and retrieval decisions in [ADR-50](adr-50-repository-scoped-cross-session-memory.md). The ignored repository-local SQLite boundary and canonical repository identity remain.

## Context

Automatic workflow promotion duplicated requirements, decisions, findings, questions, and completion receipts across repository and conversation-memory stores. Word overlap, authority exemptions, and age limits could inject irrelevant notes while missing paraphrases. Users need explicit, inspectable recall without a second generative model request or inferred host facts.

## Decision

Only the admitted `memories` tool and explicit manual commands create, update, or delete repository memory. The tool accepts exactly `action` (`add`, `update`, `remove`, `list`), optional nullable `id`, and optional nullable `text`; the host supplies repository, origin, sensitivity, time, and provenance. These are serialized repository-state writes, excluded from read-only children, with no code-mutation preview workflow. Tool enable/deny policy also controls automatic injection; manual management remains available.

Use one shared memory service and fresh SQLite content, usage, FTS5, and vector state. Defaults are twenty stored entries and three retrieved entries, configured only through `tools:config:memories`. Sanitize and normalize newlines/outer whitespace without folding case or meaningful interior whitespace. New or corrected text must fit 2,000 characters and the encoder's complete-input limit; no truncated embedding can authorize a write. Exact duplicates and unchanged updates are no-ops. Updates preserve identity, increment content revision, and reset usage/recency; conflicting updates fail without merging entries.

`ITextEmbeddingGenerator` lives in Core and owns a stable embedding-space descriptor, dimensions, complete token count, and truncation reporting. The initial implementation is CPU-only all-MiniLM-L12-v2 with 384 normalized components and a 256-token complete sequence limit, behind ONNX Runtime and Microsoft.ML.Tokenizers. Only host-deployed verified model assets are loaded. Conversation-model changes cannot change the embedding space.

Query current user instructions/steering first, followed by useful active task intent. Never expand queries with tool transcripts, generated answers, retrieved notes, or retired automatic facts. Read both branches from the same SQLite snapshot: safely quoted FTS5 OR terms with at least two distinct matches (one for a one-term query), ranked by BM25, and exact cosine over compatible vectors above the calibrated MiniLM minimum of 0.47. Fuse eligible one-based ranks equally with `1/(60+rank)` and stable-ID ties. Usage, age, and origin never boost retrieval. Bounded caches reuse unchanged query embeddings and rankings; content changes invalidate rankings, and failed semantic support is retried on a later user turn.

Apply context-mode, sensitivity, framing/token, and complete request budgets after qualification. Stateless omits prior memory. Model-facing blocks carry only stable IDs and escaped text as untrusted background data; all scores and changing metadata stay in diagnostics. Final provider dispatch, not selection or preview, records repository/run/memory/content-revision receipts. The store increments once per user turn, fences delayed old-revision receipts, and keeps usage out of text/FTS revisions. Accounting failures are visible and never resubmit the model request. A durable run-to-repository-database binding keeps receipt retention attached to the database that received the inclusions when a run switches repositories; pruning the owning run also prunes those bound receipts. Changed memory content forces request reconstruction so obsolete injected blocks cannot persist through opaque continuation.

Capacity eviction is transactional and treats manual/model origins equally. Protect meaningful adds/updates for seven days when older candidates exist. Otherwise select the oldest protected note. Among unprotected notes, minimize `(1 + log2(1 + inclusion_count)) * 2^(-age_days/30)` using last inclusion or meaningful content time; break ties by last activity, creation time, and ID. Incoming notes are never their own eviction candidate. Duplicate retries do not renew protection.

Remove automatic conversation/repository promotion, old runtime stores/governors/invalidation/relevance paths, and all prompt restoration of their snapshots. Keep ordinary transcript continuity, current explicit task/approved-state context, evidence invalidation, and model-generated active-turn compaction. Legacy serialized DTOs may remain for historical reading without supplying current prompt memory.

Forward migration verifies a SQLite-consistent backup before changing the database. Import only active records with explicit manual/user-command provenance, preserve IDs/text/timestamps where possible, reset usage, enforce capacity, and retire old repository tables. Imported encoder-overflow text stays inspectable and eligible for bounded lexical retrieval until explicitly corrected. Rebuild vectors outside write transactions and attach only to unchanged content. Failed embedding support uses qualified lexical retrieval with a diagnostic; failed SQLite search omits memory; cancellation propagates.

## Consequences

Recall is explicit, bounded, repository-local, and approximate. A request to remember something is not a guarantee of permanent storage or inclusion on every turn; always-applicable rules belong in `AGENTS.md`. Notes can become incorrect without host invalidation and require model or user correction. The local model increases release size and cold-start cost; pinned assets, parity, held-out relevance, latency, and supported-runtime packaging are verification requirements. A Git downgrade does not downgrade SQLite, and restoring the pre-migration backup deliberately loses later database changes.

## Production embedding reference refinement

The local adapter now follows the user-supplied `MlNetAllMiniLML12V2` / `MpnetEmbedderEngine` implementation. An internal engine owns the complete vector transformation; the host provider owns verified lazy loading, serialized lifetime and cancellation and returns finished vectors unchanged. Compatibility includes the built-in BERT tokenizer's default boundary tokens followed by the source engine's additional CLS/SEP pair. Complete-input accounting includes all four before clipping. The transformation has a new `v2` embedding-space identity, so older vectors rebuild against unchanged note revisions before semantic comparison; oversized notes remain eligible for lexical fallback.

Source/package/asset hashes, independent original-engine goldens, and offline regeneration are maintained in the source-repository [embedding fixture documentation](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/main/tests/Threadsmith.Embeddings.Local.Tests/Fixtures/README.md). The already pinned 0.47 minimum is retained only while it remains calibration-optimal on the unchanged fixture; the report exposes the highest equivalent cutoff separately. Held-out queries and realistic model-written notes evaluate recall and false inclusions without selecting a new threshold.
