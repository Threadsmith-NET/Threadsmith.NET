# Plan 56 — Interactive Session Lifecycle, Resume, and Clone

**Milestone:** M20 — Interactive Session Lifecycle and Continuity

**Prerequisites:** plans 02–03, 18, 26, 33–35, 37–38, 48–49, and 51–55

**Depends on by:** future session export/import, remote session synchronization, and true forked agent processes

**Status:** Implementation complete with focused automated coverage; maintained restart/load/real-terminal closeout pending.

## 1 Objective

Add host-owned `/new`, `/resume`, and `/clone` commands that let an interactive user move safely among durable sessions without restarting Threadsmith or confusing terminal state with session authority.

`/new` starts an independent empty session. `/resume [session-id]` restores an existing SQLite-backed session either by exact ID or through the keyboard-friendly selector used by solution selection. `/clone` durably checkpoints the current session and creates an independent session whose initial context is an exact host-governed copy of the source session at one complete boundary. Resuming or cloning restores the effective model and reasoning selection when those values belong to persisted session state, rebuilds governed context from durable host state rather than provider transcripts, and refreshes every session-derived status before the next composer opens.

A clone is a new top-level session, not a Plan-38 child run, worker, process, Git worktree, execution fork, or future agent subprocess.

## 2 Architectural Context

Plan 18 established tolerant event replay and explicitly deferred a user-facing session browser. Plans 33–35 added durable sanitized conversation archives, governed memory, modes, compaction, retrieval, and request reconstruction. Plan 48 introduced a mutable active-model authority, but its durable preference is currently repository-scoped; this milestone must distinguish repository defaults from the model/reasoning snapshot actually active in a session. Plans 37–38 own interruption-safe execution and worker state, while Plans 51–55 require context/cache generations and optional provider continuations to be invalidated or reconstructed conservatively.

A session transition cannot be implemented as three TUI-only commands. Repository, projections, conversation state, usage, model selection, reasoning, context inspection, active run/checkpoint state, cache generations, and live activity must switch as one serialized host operation. The TUI renders the resulting snapshot but never owns the active `SessionId`.

SQLite events alone are not a complete reconstruction source for current conversation context. Resume must compose tolerant event restoration with the conversation store, governed memory, session preferences, execution checkpoints, and other persisted host-owned session records. Raw provider requests, hidden reasoning, transient activity, and opaque provider continuation handles are never treated as canonical context.

## 3 Scope

- Add a host-owned session catalog, exact-ID lookup, transition commands, immutable transition results, and one active-session authority shared by TUI and headless callers.
- Persist bounded session metadata needed for deterministic listing: session ID, repository identity, created/updated time, last visible bounded preview, state, source relation, and schema version.
- Add `/new` to checkpoint the current safe state and activate a fresh session with empty conversation, memory, evidence, usage, run, context-inspection, and transient activity state.
- Add `/resume` with an optional exact session ID and a numbered keyboard selector when no ID is supplied.
- Rebuild resumable state from SQLite, artifacts, conversation storage, and versioned host records with existing legacy/partial-state behavior.
- Persist and restore the session's effective provider/profile/reasoning snapshot independently of repository defaults, while validating it against the current effective provider catalog.
- Add `/clone` to checkpoint the current session and create a new independent top-level session initialized from a transactionally consistent copy of its reconstructible governed context.
- Display an actionable ` /resume <source-session-id>` return command immediately after entering a clone.
- Refresh repository, solution, model, reasoning, context, usage, policy, tool availability, session identity, and applicable execution-status projections atomically before accepting new input.
- Add interactive and headless application boundaries, persistence migration, events/projections, diagnostics, tests, documentation, Scenario V, and maintained manual coverage.

## 4 Non-Scope

- No child process, subprocess agent, Plan-38 delegation, worktree, branch, merge, or parent/child execution scheduling.
- No live branching from an in-flight model/tool/mutation/validation operation.
- No automatic merge, synchronization, or propagation between a clone and its source after clone creation.
- No copying or reusing provider-side opaque continuation/cache handles.
- No raw provider transcript, hidden reasoning, transient `THINKING`, terminal scrollback, selector state, or unredacted tool payload persistence.
- No cross-repository resume inside an already opened repository in this plan; the interactive catalog is repository-scoped and exact IDs bound to another repository produce an actionable diagnostic.
- No session deletion, rename, tagging, search, export/import, cloud sync, or retention-policy redesign.
- No change to Plan-37's meaning of resuming interrupted approved-plan execution; session resume reconstructs that durable checkpoint but does not bypass its explicit safety gates.

## 5 Current State

Threadsmith creates and persists session/event/conversation state and can restore projections through `SessionRestorer`, including bounded warnings for legacy events. Conversation archives and structured memory can be queried by session ID. The active TUI shell, usage projection, context inspection, model/reasoning authority, and startup composition are still assembled around one startup session, and there is no session catalog, interactive restore selector, atomic in-process session transition, or clone contract.

Repository model/reasoning memory currently supplies startup selection. It does not by itself prove which selection an older session used, and using current repository defaults during resume could silently change model capacity, reasoning, provider routing, and status from the original session.

## 6 Proposed Design

### 6.1 Session identity, metadata, and repository binding

Keep `SessionId` as the stable exact identifier accepted by `/resume`. Add a versioned session metadata record stored transactionally in SQLite with:

- `SessionId`;
- canonical repository identity and non-sensitive display name;
- creation and last-activity timestamps from an injectable clock;
- lifecycle state such as `Active`, `Idle`, `Interrupted`, `Completed`, `Legacy`, or `Unavailable`;
- bounded sanitized last-visible-message preview and message count;
- active conversation mode;
- optional clone source session ID;
- persisted model-selection snapshot version;
- schema version and restoration availability.

The repository identity is host-derived and stable across ordinary working-tree revisions. It must not expose credentials or treat a path supplied by persisted data as authority to open a different repository. The picker lists only sessions for the currently opened repository. An exact ID is resolved globally only to produce either a same-repository restore or a bounded repository-mismatch diagnostic; it never silently changes repository, trust, solution, or working directory.

### 6.2 One serialized transition authority

Add host-owned commands equivalent to:

- `CreateNewSessionCommand`;
- `ListResumableSessionsCommand`;
- `ResumeSessionCommand(SessionId)`;
- `CloneSessionCommand`;
- `GetActiveSessionCommand`.

One application service serializes transitions and returns an immutable snapshot containing active session identity, transition kind, source ID when cloned, restoration/legacy warnings, effective model/reasoning, conversation mode, usage, context status, repository/solution identity, and execution checkpoint summary.

A transition is accepted only at a complete safe boundary. If a model stream, ordinary tool, MCP call, selector, mutation transaction, validation, hook, skill workflow, or delegated run is active, the host must either wait through the existing cancellation/safe-boundary contract or reject with an actionable message. It must never swap shared projections beneath in-flight work.

Transition steps are prepare → validate → persist/checkpoint current session → reconstruct candidate state off to the side → atomically publish the new active snapshot → render confirmation. Cancellation or failure before publication leaves the original session active and usable. A failure after durable clone creation but before activation reports the new ID without pretending it became active.

### 6.3 `/new`

`/new` has no arguments. At a safe boundary it:

1. flushes all accepted visible conversation and durable session records for the current session;
2. marks the prior session idle/completed as appropriate without rewriting its history;
3. creates a new top-level `SessionId` bound to the current repository and solution context;
4. initializes compiled/session-mode defaults and resolves the effective current repository model/reasoning preference for a genuinely new session;
5. clears conversation archive selection, structured memory, evidence, run/checkpoint, usage, latest context inspection, cache/continuation, and transient activity from active projections;
6. publishes fresh statuses and prints the new session ID.

Repository configuration, trust, selected solution, enabled tools, themes, and repository mutation policy are repository state rather than conversational context and remain effective. No prior conversational content is included in the first model request.

### 6.4 `/resume [session-id]`

`/resume <session-id>` validates the exact bounded ID and resumes it directly. `/resume` with no argument opens the existing `IConsoleSurface.SelectAsync`-style keyboard selector. Choices are deterministic, newest activity first with stable session-ID tie-breaking, and show a bounded ID, activity time, state, last-message preview, model/reasoning when known, and a clone marker when applicable. The current session is marked and selecting it is idempotent. Cancel changes nothing.

Resume reconstruction combines:

- tolerant domain-event replay into a fresh projection set;
- conversation archive, mode, structured memory, summary/compaction generation, provenance, and repository-validity state;
- session usage and latest context inspection only when their persisted model/context generation matches;
- Plan-37/38 checkpoints and interrupted state without automatically continuing work;
- persisted session model/reasoning selection;
- durable policy/tool/config identities that are safe to restore, revalidated against current repository configuration and trust.

Unknown future schema or missing artifacts produce the existing visible partial/legacy behavior. A session that cannot safely accept new turns becomes inspectable/read-only rather than being presented as fully resumed.

### 6.5 Session model and reasoning restoration

Persist a session-scoped model-selection snapshot whenever a session is created and whenever `/models` or `/reasoning` successfully changes active state. It contains stable provider ID, profile ID, exact host `ReasoningLevel`, selection generation/version, and effective capability identity—never endpoint, credential, secret reference, or provider DTO.

On resume, validate that snapshot against the current effective provider catalog and compatibility rules:

- if provider, profile, and exact reasoning remain available, restore them before rebuilding model-dependent context/status;
- if the model exists but exact reasoning no longer does, set session reasoning to `None`, persist the repaired session snapshot, and show the same actionable compatibility guidance as Plan 48;
- if the provider/profile is disabled, missing, or materially incompatible, do not silently substitute repository defaults. Enter a bounded selection-required state and prompt `/models` before a model-backed turn;
- repository model memory remains the default for `/new`, not an override of an existing session snapshot.

The active-model authority, provider dispatcher, footer/status, context limit, context inspection generation, and next request must all observe the restored selection. A resume never combines persisted context occupancy from one model generation with another model's limit.

### 6.6 `/clone`

`/clone` takes no arguments in M20. At a complete safe boundary it persists/checkpoints the source, reads one transactionally consistent reconstructible snapshot, creates a new top-level session ID, and copies:

- sanitized visible conversation archive and message ordering with new session-local message identities plus explicit source provenance;
- governed memory, supersession/invalidation, summary, conversation mode, and compaction generation;
- currently valid evidence/context inputs and repository revision identities;
- effective model/profile/reasoning snapshot;
- cumulative usage as inherited historical usage, clearly distinguished from tokens consumed after cloning;
- repository/solution/config/trust identities and safe status inputs.

The clone does not copy an active run as runnable work, pending approval prompt, mutation transaction, worker lease, hook invocation, cancellation source, transient activity, or opaque provider continuation. Durable completed history may remain referenced by immutable provenance; interrupted Plan-37/38 work is represented as historical/paused context and requires its ordinary explicit resume path rather than being duplicated as two writable executions.

After activation print a concise message containing both IDs and, on its own copyable line:

```text
/resume <source-session-id>
```

The source and clone diverge independently after creation. Resuming either reconstructs only that session's later state. Clone creation and initial copied state are atomic; partial copied graphs are never selectable.

### 6.7 Cache, context, and status behavior

Session transition invalidates process-local provider continuation handles, request-prefix caches, active instruction/evidence assemblies, and stale context inspections. The first request after resume or clone is canonically reconstructed through the Plan-51–55 stateless path. Provider-reported durable usage may be restored, but unavailable cache state remains unavailable rather than fabricated.

Before the next composer opens, refresh or explicitly mark pending:

- active session ID and clone provenance;
- repository/folder/solution and trust;
- effective provider/model/reasoning and generation;
- conversation mode and memory/compaction state;
- enabled tools, policy, extension/MCP availability under current configuration;
- latest context occupancy only if reconstructed for the matching generation;
- cumulative usage and inherited-versus-post-clone accounting;
- interrupted execution/delegation status;
- operation activity, which must be idle.

No status may retain values from the session being left merely because reconstruction lacks a value; use an honest unknown/pending/legacy projection.

## 7 Public Contracts

Public contracts remain host-owned immutable identifiers, commands, results, catalog entries, restoration summaries, events, and projection DTOs. Add versioned events equivalent to `SessionCreated`, `SessionActivated`, and `SessionCloned` only where durable audit requires them; do not emit an activation event into the destination session before its durable creation succeeds.

Persistence-specific rows stay in `Threadsmith.Persistence`. Terminal selector types stay in `Threadsmith.Tui`. Provider SDK/wire/cache types, SQLite types, terminal-library types, and mutable service containers do not cross subsystem boundaries.

The clone contract explicitly records `SourceSessionId`, clone snapshot sequence/time, and copied-state schema version. It is provenance, not a parent-child execution relationship.

## 8 Project/File Changes

- `Threadsmith.Core` — session transition commands/results, catalog DTOs, lifecycle/provenance events, and active-session projection contracts.
- `Threadsmith.Persistence` — ordered migration, session metadata/catalog queries, session-scoped model preferences, transactional clone snapshot/copy, tolerant composite restoration, and indexes.
- `Threadsmith.Execution` — serialized transition coordinator, safe-boundary/checkpoint integration, projection replacement, context/cache invalidation, and model/reasoning restoration.
- `Threadsmith.Context` — reconstructible conversation/memory/evidence snapshot import with provenance and generation validation.
- `Threadsmith.Models` / compiled providers — consume the atomically published active selection; no provider-owned session store.
- `Threadsmith.Tui` — `/new`, `/resume`, `/clone`, keyboard selector, confirmation/return output, and complete status refresh.
- `Threadsmith.Cli` — shared noninteractive list/create/resume/clone application boundary where useful for tests and automation; no prompting in headless mode.
- App composition — lifetime changes required for replaceable session-scoped state without shortening repository/provider/process-owned resources.
- Persistence, conversation, execution, model-selection, TUI, architecture, and integration tests.
- Event catalog, user/operations documentation, manual tests, milestone/index/scenario/status, and DOX chain.

Any new JSON or SQLite fixture copied by a project uses `CopyToOutputDirectory=PreserveNewest`.

## 9 Ordered Tasks

1. Inventory every session-derived mutable singleton/projection and classify its lifetime as process, repository, or replaceable session state.
2. Define session metadata/catalog, repository binding, lifecycle state, clone provenance, session model/reasoning snapshot, transition commands/results, and versioned serialization.
3. Add an ordered transactional SQLite migration, bounded catalog indexes/queries, exact-ID lookup, and backward-compatible metadata synthesis for legacy sessions.
4. Extend composite restoration to rebuild events, projections, conversation, memory, mode, usage, execution checkpoints, and session preferences into an unpublished candidate state.
5. Introduce one serialized active-session authority and atomic projection/state publication with rollback-on-failure and safe-boundary enforcement.
6. Integrate session-scoped provider/profile/reasoning persistence with `/models` and `/reasoning`; validate restoration against the current catalog without silent fallback.
7. Implement `/new`, including durable checkpoint of the old session, fresh state creation, repository-default selection, complete context clearing, and status refresh.
8. Implement session catalog listing and `/resume [session-id]`, including deterministic selector labels, cancellation/idempotence, repository mismatch, partial legacy restoration, and selection-required states.
9. Implement transactionally consistent `/clone`, copied provenance graph, independent IDs/sequences, inherited usage labeling, exclusion of live execution/continuation handles, and the copyable `/resume <source-id>` message.
10. Invalidate provider continuations/cache families and stale inspections on every transition; prove canonical stateless first-request reconstruction.
11. Add shared headless commands/results and stable diagnostics without introducing interactive prompting outside TUI.
12. Add migration, catalog, transition atomicity, restoration, model/reasoning, context, execution-checkpoint, clone independence, privacy, concurrency, TUI, headless, restart, and real-terminal tests.
13. Update event catalog, ADR if session-lifetime ownership changes materially, user/operations docs, manual test plan, Scenario V, milestones/index/status, and DOX.

## 10 Testing

Automated and maintained tests must cover:

- session metadata migration from existing databases, deterministic ordering, exact ID parsing, pagination/bounds, repository filtering, malformed rows, future schema, and unavailable artifacts;
- `/new` clearing conversation, memory, evidence, run state, usage, context inspection, continuation/cache state, and transient activity while retaining repository-scoped trust/configuration/solution/tool policy;
- `/resume` direct ID, no-argument selector, current-session idempotence, cancellation, missing ID, repository mismatch, legacy/partial read-only restoration, and atomic failure rollback;
- restoration equivalence across events, conversation ordering, structured memory/provenance, modes, compaction, repository validity, usage, context generations, and paused execution checkpoints;
- model/reasoning exact restore, unsupported reasoning repair to `None`, missing/disabled model selection-required behavior, provider routing of the next request, and footer/context-limit refresh;
- transitions attempted during model streaming, tools, MCP, mutation, validation, hook, skill, selector, and delegated work, proving wait/reject behavior and no split-brain state;
- clone atomicity, new identities, copied sanitized context/provenance, independent divergence, source immutability, inherited usage labeling, and copyable source-resume command;
- clone exclusion of live runs, approvals, mutation transactions, worker leases, cancellation objects, transient reasoning/activity, raw provider content, and opaque continuation handles;
- first post-transition request canonical stateless reconstruction and absence of stale context/tool/policy/model generations;
- TUI/headless parity, narrow-terminal labels, native selection/copy, bulk paste, resize, cancellation, restart, and repeated new/resume/clone cycles.

## 11 Security/Permissions

Session persistence is sensitive. Continue sanitizing before durability, resolving no secrets during catalog display, bounding previews and list size, parameterizing SQLite access, and excluding bodies from logs/telemetry. Exact session IDs authorize only lookup within the local user-owned store; they do not grant repository trust, cross-repository access, model credentials, mutation approval, skill/extension enablement, or tool permission.

Clone copies only already-sanitized host-owned reconstructible state. Revalidate repository-dependent memory/evidence, provider availability, tool policy, trust, secret scope, extensions, MCP, hooks, and active instructions at the transition boundary. Do not copy expanded secrets, credential caches, OAuth tokens, process environment, raw diagnostic artifacts, or external-provider continuation references.

A persisted repository path or clone source is data, not code and not authority. Symlink/reparse, ownership, confinement, and current-repository identity rules remain enforced.

## 12 Observability

Emit bounded secret-free transition telemetry: transition kind, source/destination session IDs in the existing safe identifier format, duration, catalog count, restored record counts, legacy/partial counts, model/reasoning outcome, context/cache invalidation, clone copied-category counts, persistence outcome, and failure phase.

Never log conversation/message/memory bodies, selector previews, prompts, hidden reasoning, tool results, repository secrets, credentials, provider wire data, or opaque continuation references. Transition duration uses the existing monotonic operation timing boundary; no completed `THINKING` row is introduced.

## 13 Migration/Compatibility

Add the next ordered migration without rewriting append-only history. Existing sessions lacking catalog metadata or session model preferences remain discoverable through bounded synthesized metadata where repository identity can be proven. They display an `unknown model`/legacy marker and use current safe restoration behavior; they are not silently assigned today's repository default as historical truth.

Unknown newer session-state versions restore as partial/read-only with warnings. Migration failure rolls back and leaves the prior database readable. Clone state uses its own version so later additions can migrate copied graphs without changing the immutable source.

Plan-37 execution resume remains distinct: `/resume <session-id>` re-enters the session and exposes its checkpoint, but any continuation of interrupted mutations/workers still uses Plan-37/38's explicit reconciliation and approval rules.

## 14 Acceptance Criteria

- `/new` durably leaves the prior session resumable, activates a distinct session, and proves the next request contains no prior conversation, memory, evidence, usage, run, context-inspection, or provider-continuation state.
- `/resume <session-id>` and the no-argument keyboard selector use one host boundary, restore the same SQLite-backed session state, and change nothing on cancellation, invalid ID, mismatch, or pre-publication failure.
- Resume reconstructs sanitized conversation, governed memory/provenance, mode, compaction, valid evidence, usage, checkpoints, and projections without replaying raw provider transcripts or hidden reasoning.
- Persisted session provider/profile/reasoning is restored exactly when still valid. Missing/incompatible selection is visible and never silently replaced by repository defaults; all model, reasoning, context-limit, usage, and footer/status projections agree before the next input.
- `/clone` first checkpoints the source and atomically creates an independent top-level session with copied reconstructible context, stable clone provenance, new identities, and no duplicated live execution authority.
- Entering a clone prints a directly copyable `/resume <source-session-id>` command. Resuming source and clone later shows independent post-clone histories.
- Session transitions occur only at complete safe boundaries and cannot race model/tool/MCP/mutation/validation/hook/skill/delegation work or leave mixed projections.
- The first model request after resume/clone is a canonical stateless reconstruction; opaque provider continuation/cache state is not copied or trusted.
- Picker previews and diagnostics are bounded, deterministic, repository-scoped, secret-free, and usable with native keyboard selection, copy, bulk paste, resize, and cancellation.
- Migration, architecture, focused automated coverage, Scenario V, maintained restart/real-terminal tests, docs, status, and DOX pass.

## 15 Risks

- **Split-brain active state:** centralize transitions, reconstruct off-side, and atomically publish one immutable session snapshot.
- **Resume appears complete but omits context:** define a composite restoration manifest and test equivalence for every persisted category.
- **Historical model silently changes:** persist session selection and require correction rather than default substitution.
- **Clone duplicates dangerous live authority:** copy only reconstructible completed/context state; never duplicate active runs, approvals, transactions, leases, or continuation handles.
- **Large session lists or clones hurt responsiveness:** bounded indexed catalogs, paged queries, transactional set-based copying, progress/activity projection, cancellation before publication, and measured load tests.
- **Persisted stale repository facts contaminate a new turn:** revalidate repository-dependent memory/evidence/instructions/trust and mark stale/invalid rather than assuming validity.
- **Legacy databases become unresumable:** synthesize bounded metadata, tolerate schemas, and preserve inspectable partial/read-only access.

## 16 Documentation

- Update `docs/user-guide.md` and keyboard command references for `/new`, `/resume [session-id]`, `/clone`, selector labels, model/reasoning restoration, partial sessions, and clone return output.
- Add or update session persistence/restoration operations guidance covering database location, repository scoping, migration, troubleshooting, privacy, retention, and distinction from Plan-37 execution resume.
- Update `docs/architecture/event-catalog.md` for new durable events and add an ADR if replaceable session-lifetime composition or clone-copy authority changes established ownership.
- Add maintained manual cases for direct and selector resume, restart, legacy state, incompatible model, new-session clearing, clone divergence, return command, interrupted execution, terminal interaction, and failure atomicity.
- Update shared context, Scenario V, milestone dependency/status, plan index, root/docs DOX, and project status references.

## 17 Open Decisions

Resolved for planning:

- M20 uses one cohesive Plan 56 because all three commands require the same transition authority and persistence schema.
- `/resume` without an ID is repository-scoped and newest-first; exact cross-repository IDs report where the session belongs but do not switch repositories automatically.
- `/new` retains repository-scoped configuration/trust/solution/tool policy but starts with no conversational/session context and resolves current repository model defaults.
- Existing sessions own a persisted model/reasoning snapshot once available. Repository defaults initialize new sessions only and never silently rewrite resumed history.
- Clone usage includes an inherited historical subtotal plus post-clone usage so copied context is honest without double-attributing new provider consumption.
- Clones copy reconstructible governed context and provenance, not live execution authority or opaque provider state.
- The clone confirmation's `/resume <source-session-id>` points back to the source session the user just left.
- True forked/subprocess agents, cross-repository switching, session deletion/tagging/export, and automatic clone merging remain future milestones.
