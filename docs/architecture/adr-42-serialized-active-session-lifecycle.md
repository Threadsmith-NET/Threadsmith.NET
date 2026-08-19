# ADR-42 — Serialized Active-Session Lifecycle and Governed Clone Authority

**Status:** Accepted

## Context

Durable events, conversation archives, governed memory, execution checkpoints, model selection, usage, and terminal projections previously shared one startup session identity. In-process `/new`, `/resume`, and `/clone` cannot safely replace only terminal state: doing so would permit split-brain session identity, stale model/context generations, or duplicated live execution authority.

## Decision

- One `SessionLifecycleApplication` serializes create, resume, clone, list, and active-session commands.
- Transitions are accepted only when `SessionApplication` reports a complete run boundary. Candidate projection and durable state are prepared before the active catalog entry is published.
- SQLite migration 8 owns repository-bound catalog metadata, session model/reasoning snapshots, usage subtotals, clone provenance, deterministic listing indexes, and atomic clone insertion.
- Repository identity is derived by the host from the canonical currently opened path. Persisted paths and exact session IDs never authorize repository switching.
- Resume reconstructs projections through tolerant event replay plus the conversation archive. It invalidates process-local context inspections; provider continuations remain non-authoritative and are not persisted or cloned.
- A clone is a new top-level session. It copies sanitized visible conversation and governed memory using new session-local message, memory, and run identities. It shares immutable content-addressed bodies where safe, records source provenance, and does not copy events or runnable execution/delegation authority.
- Repository configuration, trust, solution, tools, and policy remain repository-scoped. A new session starts with empty conversational state and usage; resumed sessions restore their persisted selection when compatible.
- TUI and headless surfaces dispatch the same host-owned commands. Only the TUI supplies the repository-scoped numbered selector.

## Consequences

Session transitions cannot race active model/tool/governed runs, clone graphs are transactionally selectable or absent, and source/clone histories diverge independently. Missing or incompatible historical model state is diagnosed rather than silently rewritten. Migration 8 is additive; older databases remain migratable through the ordered migration runner.
