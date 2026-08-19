# ADR-31: Bounded Conversational Continuity Amends Governed Context

**Status:** Accepted

## Context

ADR-12 established phase-specific governed context and rejected raw transcript replay. That decision protects context windows from unbounded growth, prevents stale repository claims from silently persisting, and makes selection/omission inspectable. Its implementation went further by declaring conversation history not to be an input category and selecting most evidence only within the current run.

That stronger restriction conflicts with Threadsmith's conversation-first product direction and the strategy's requirements for context summarization, preservation of accepted decisions, outstanding questions, session summaries, and provenance-preserving reduction. A later user turn cannot reliably refer to an earlier requirement, correction, decision, finding, or unresolved question.

## Decision

ADR-12 remains authoritative for policy precedence, typed governed state, phase-specific budgets, invalidation, and rejection of unbounded transcript replay. This ADR amends it as follows:

- Conversation history is a governed input category, not an authoritative transcript state machine.
- Sanitized visible user and assistant messages are archived with stable message/session/run provenance. Hidden reasoning, provider wire payloads, secrets, and raw tool output are excluded.
- The compiled default is **Conversation-aware**: include the current user turn verbatim, a bounded recent complete-turn window, a host-owned structured session summary, and relevant retrieved older memory.
- **Governed-memory-only** includes no raw prior messages and uses only current input plus validated promoted/compacted memory.
- **Stateless** includes current input and current-run state only, preserving deterministic isolated automation.
- The structured summary distinguishes user requirements, decisions, constraints, unresolved questions, repository findings, completed work, and rejected/superseded information.
- Every memory/summary item retains provenance to originating messages, runs, evidence, and repository revision where applicable.
- Explicit user decisions and corrections receive stronger preservation and supersession authority than inferred assistant content.
- Repository-dependent memory invalidates at turn boundaries after relevant file, symbol, project, or revision changes.
- Compaction never deletes the full archive. Large bodies may move to retained artifacts; provenance remains durable.
- Under context pressure, retain current input first, then explicit active user decisions/corrections, reduce oldest complete raw turns, use structured summary, reduce lower-ranked retrieved memory, and fail rather than silently omit required host policy/task/schema.
- Context inspection reports effective mode, included recent turns, summary version/categories, retrieved memory and rationale, compacted ranges, stale/superseded/excluded items, and pressure reductions.
- Historical content remains untrusted and cannot override current host policy, permissions, tool authorization, sensitive-data policy, or output schema.

## Implementation

Implemented by Milestone 7.4 plans 33-35:

- `ConversationMessageId`, `ConversationMemoryId`, `ConversationMessage`, seven typed `ConversationMemoryKind` values, provenance-bearing `ConversationMemoryItem`, and versioned `ConversationSummarySnapshot` are host-owned Core contracts.
- SQLite migration 2 adds ordered archive, memory, provenance-edge, summary, and session-mode tables; large sanitized bodies use the existing content-addressed artifact store.
- `ConversationMemoryGovernor`, `ConversationSummaryValidator`, `ConversationCompactor`, `ConversationMemoryRetriever`, and `ConversationMemoryInvalidator` enforce source authority, bounds, deterministic ordering, atomic replacement, and repository-aware staleness.
- `ContextAssembler` always includes the current turn, then applies Conversation-aware, Governed-memory-only, or Stateless selection with separate recent-turn, summary, and retrieval budgets plus inspectable pressure reductions.
- `/context mode`, `/context inspect`, and `/context compact` use the same host command contracts exposed to headless callers.

## Consequences

Threadsmith gains ordinary conversational continuity without making an opaque transcript or model summary the source of truth. Token/cost use increases in the default mode, so budgets, compaction pressure, usage projection, and omissions must be visible. Durable conversation state increases privacy and retention obligations and therefore uses existing sanitization, artifact, migration, restoration, retention, and diagnostic-redaction boundaries.

Three sequential implementation plans own the change:

- Plan 33: archive, modes, structured memory contracts, provenance, persistence, and restoration.
- Plan 34: promotion, validated compaction, deterministic retrieval, supersession, and invalidation.
- Plan 35: request assembly, budgets, configuration/session controls, inspection, and terminal/headless integration.

Cross-session/global personal memory, team-shared memory, embeddings, and provider-managed conversation IDs remain outside Milestone 7.4.
