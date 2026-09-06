# ADR-50: Local Repository-Scoped Cross-Session Memory

- **Status:** Accepted
- **Date:** 2026-08-21
- **Deciders:** Threadsmith.NET maintainers

## Context

Session-scoped conversation memory preserves continuity for one session or clone, but
independent sessions in the same repository cannot currently reuse durable local
facts without replaying transcripts. Milestone 25 adds repository-scoped memory
using the existing ignored `.threadsmith/threadsmith.db` runtime persistence
boundary. The capability must not create team/shared memory, tracked memory files,
or repository-authored authority.

## Decision

Threadsmith will store repository-scoped memory as host-owned, structured records
in the repository-local SQLite database:

- every item is keyed by the same canonical, platform-aware, non-disclosing
  repository identity used by durable session lifecycle and by repository-memory id;
- items carry kind, authority, validity, sensitivity, content hash, optional
  repository revision, path/symbol/project scope, provenance sources, timestamps,
  and supersession/audit state;
- user-authored, host-observed, evidence-backed, and model-proposed-validated
  authority are distinct and influence future preservation and retrieval policy;
- stale, superseded, forgotten, and rejected items remain auditable but are not
  eligible for prompt assembly;
- active-item capacity demotions and the insertion or supersession that requires
  them commit in one persistence transaction;
- repository files, prompt appends, skills, hooks, and ordinary configuration are
  untrusted data and cannot silently create, authorize, or elevate memory;
- repository memory remains local and ignored by Git by default, with no sharing or
  synchronization contract.

Repository memory is a separate governed context source from session conversation
memory. It may be retrieved only through deterministic host policy with bounded
budgets, sensitivity checks, current-task relevance, validity checks, and
inspectable inclusion/omission rationale.

## Consequences

- Independent sessions in the same repository can reuse explicit and
  evidence-backed local facts without transcript replay.
- The local database becomes the durable audit boundary for repository memory;
  backup, diagnostics, retention, and redaction behavior must include the new
  tables without exposing secrets.
- Invalidation must remain conservative: path, symbol, project, solution, and
  revision-dependent items become stale when their support changes.
- Future command and context-assembly work must route through host-owned DTOs and
  cannot let model prose or repository-authored files grant authority.
