## Milestone 7.4 — Cross-Turn Conversation Context and Compaction  *(plans 33, 34, 35)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Make Threadsmith conversation-aware by default without reverting to unbounded transcript replay. Archive visible conversation with provenance, promote and compact older content into validated structured session memory, retrieve relevant prior decisions/findings/questions across turns, and offer governed-memory-only and stateless modes for stricter operation.

**Deliverables:**
- Versioned durable conversation archive with stable message/session/run provenance and no hidden reasoning/provider wire content.
- `ConversationAware`, `GovernedMemoryOnly`, and `Stateless` modes; Conversation-aware is the compiled default.
- Host-owned structured memory distinguishing user requirements, decisions, constraints, unresolved questions, repository findings, completed work, and rejected/superseded information.
- Provenance from every memory item to originating messages, runs, evidence, artifacts, and repository revision where applicable.
- Turn-boundary promotion/compaction with strict structured validation, atomic snapshot replacement, cancellation, idempotency, budgets, and prior-snapshot fallback.
- Deterministic retrieval of relevant older memory with phase/category policy, stable rationale, and no external embedding dependency.
- Repository-aware invalidation and explicit user-correction supersession.
- Bounded recent complete-turn selection, typed-summary and retrieved-memory budgets, and deterministic pressure reduction.
- Normal layered configuration and session/TUI/headless controls for all modes and safe compaction requests.
- Context inspection showing mode, included/omitted recent turns, summary categories/version, retrieved provenance/rationale, compacted ranges, stale/superseded exclusions, and pressure reductions.
- Persistence migration, restoration, retention, diagnostic redaction, telemetry, manual tests, and E2E acceptance Scenario I.

**Exit criteria:**
- A user can refer to a relevant requirement, correction, decision, repository finding, or unresolved question from an older turn and the next request includes it with originating provenance.
- Conversation-aware mode includes the current turn verbatim, bounded recent turns, active structured summary, and relevant older memory within the selected model context window.
- Governed-memory-only includes no raw prior messages; Stateless includes no prior raw turns, summary, or retrieved memory.
- Explicit user decisions/corrections survive compaction and supersede conflicting inferred memory.
- Repository mutations/revisions invalidate dependent remembered findings at turn boundaries without invalidating unrelated user constraints.
- Compaction never deletes archived messages, never activates an invalid candidate, and preserves the previous snapshot after cancellation/provider/persistence failure.
- Hidden reasoning, secrets, provider wire data, raw tool output, and unsupported assistant claims never enter durable or assembled conversation memory.
- Context inspection explains every conversation inclusion, retrieval, compaction, invalidation, supersession, mode exclusion, and token-pressure omission.
- Session restoration preserves archive order, mode, active summary, provenance, and deterministic retrieval.
- Interactive and headless requests are identical for equivalent session state and commands; all automated suites and maintained manual cases pass.

**Prerequisites:** plans 02 (events/projections), 03 (conversation shell), 09 (context governance), 18 (persistence/restoration), and 26 (context/usage projection). It may proceed independently of M9/M10.

**Scope decisions (confirmed with user):**
- Conversation-aware is default; governed-memory-only and stateless remain explicit options.
- Current user input is preserved before optional historical categories.
- Full visible messages are archived with provenance; compaction never deletes them.
- Structured memory is host-owned and categorized, not one opaque assistant paragraph.
- Deterministic lexical/metadata retrieval ships before any embedding/vector infrastructure.
- Cross-session/global personal memory, team-shared memory, and hidden-reasoning replay are excluded.

---

---

[Back to the milestone index](../milestones.md) Â· [Dependency DAG](dependency-dag.md)
