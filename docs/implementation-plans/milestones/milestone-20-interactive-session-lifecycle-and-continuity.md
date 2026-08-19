## Milestone 20 — Interactive Session Lifecycle and Continuity  *(plan 56)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Let users create, resume, and clone durable top-level sessions from the conversation-first terminal while reconstructing governed context and every session-derived status through one atomic host-owned transition boundary.

**Deliverables:**
- `/new` to checkpoint the current session and activate a distinct empty session while retaining repository-scoped configuration, trust, solution, tools, and policy.
- `/resume [session-id]` with exact-ID restore or a deterministic repository-scoped keyboard selector equivalent to solution selection.
- Composite SQLite-backed reconstruction of events, projections, sanitized conversation, governed memory/provenance, mode, compaction, valid evidence, usage, and interrupted execution checkpoints.
- Session-scoped provider/profile/reasoning persistence and validation, with exact restoration when available and visible selection-required behavior rather than silent model substitution.
- `/clone` to transactionally checkpoint the source and create an independent top-level session with copied reconstructible governed context and provenance but no duplicated live execution authority or opaque provider continuation.
- A copyable `/resume <source-session-id>` line after entering a clone.
- One serialized active-session authority, safe-boundary enforcement, atomic candidate-state publication, stale context/cache invalidation, and complete status refresh before the next composer.
- Versioned session metadata/catalog persistence, legacy compatibility, Scenario V, focused automated coverage, maintained restart/real-terminal tests, documentation, and DOX closeout.

**Exit criteria:**
- `/new` leaves the prior session durably resumable and proves the next request contains no prior session conversation, memory, evidence, usage, run, context-inspection, or continuation state.
- Direct-ID and selector-based `/resume` restore identical repository-bound SQLite state; cancellation, invalid ID, mismatch, and pre-publication failure leave the current session unchanged.
- Resume rebuilds governed context without raw provider transcript or hidden-reasoning replay and restores model/reasoning exactly when currently valid.
- Missing or incompatible historical model selection is visible and requires correction; repository defaults initialize new sessions but never silently rewrite resumed history.
- `/clone` creates a new independent session with copied sanitized context/provenance and new identities while excluding active runs, approvals, transactions, worker leases, cancellation state, transient activity, secrets, and opaque provider cache/continuation handles.
- Clone entry prints the source session's copyable `/resume <session-id>` command, and source/clone histories diverge independently.
- Transitions occur only at complete safe boundaries and atomically update session, model, reasoning, context, usage, repository, policy/tool, execution, and activity projections before accepting input.
- The first post-resume/clone request uses canonical stateless reconstruction; stale provider/session generations cannot cross the transition.
- Focused persistence/conversation/execution/model/TUI coverage, architecture gates, Scenario V, maintained restart/load/real-terminal checks, docs, status, and DOX pass.

**Prerequisites:** plans 02–03, 18, 26, 33–35, 37–38, 48–49, and 51–55.

**Scope decisions:**
- This is one cohesive plan because `/new`, `/resume`, and `/clone` require the same persistence schema and atomic transition authority.
- The picker is repository-scoped and newest-first. Exact IDs for another repository diagnose the mismatch but never switch repositories automatically.
- A clone is a top-level session copy, not a Plan-38 child, process, worktree, execution fork, or future subprocess agent.
- Repository defaults initialize `/new`; persisted session model/reasoning owns resume truth.
- Clones copy reconstructible governed context and provenance, not live execution authority or provider-owned opaque state.
- Session deletion/tagging/export, cloud synchronization, cross-repository switching, clone merging, and true forked agents are excluded.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
