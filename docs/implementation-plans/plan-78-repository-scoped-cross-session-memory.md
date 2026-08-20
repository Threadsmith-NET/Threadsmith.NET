# Implementation Plan 78: Repository-Scoped Cross-Session Memory

**Status:** Planned

**Delivery track:** M25 - Repository-Scoped Cross-Session Memory
**Strategy source:** User-requested repository-level memory expansion using existing ignored repo SQLite persistence; ADR-31, ADR-42, plans 33-35, 53-56, and 74-75
**Prerequisite plans:** plans 33-35, 18, 51-56, and 74-75

## 1. Objective

Add host-governed memory that persists across Threadsmith sessions for the same repository. The memory must use the existing repository-local `.threadsmith/threadsmith.db` persistence boundary, remain ignored by Git, and provide bounded, inspectable, provenance-preserving facts to future requests without replaying transcripts or introducing shared/team memory.

## 2. Architectural Context

ADR-31 and plans 33-35 added session-scoped conversation archive, structured memory, compaction, retrieval, and context modes. ADR-42 and Plan 56 added durable session lifecycle and repository rebinding. Plans 53-55 added canonical instruction/evidence layout, cache-aware compaction, and continuation identity. Plan 75 requires plan sanity and approval policy to remain host-owned.

This plan adds a separate repository-scoped memory source. It complements session memory but does not replace it. Repository memory is durable local state associated with a repository identity and selected from the existing ignored `.threadsmith/threadsmith.db` store during request assembly.

## 3. Scope

- Define repository memory contracts with stable ids, typed category, authority, validity, sensitivity, repository identity, optional repository revision, path/symbol/project scope, provenance, created/updated timestamps, and supersession links.
- Persist repository memory in the existing repository-local SQLite database at `.threadsmith/threadsmith.db` using additive migrations.
- Add user and headless commands to:
  - remember an explicit repo-scoped fact;
  - list active/stale/superseded/rejected repo memory;
  - inspect one memory item and its provenance;
  - supersede/correct one item;
  - forget or reject one item without deleting audit metadata;
  - validate/recheck repository-dependent items.
- Promote host-observed repository facts from accepted plans, completed execution/build/test results, and governed evidence when provenance is sufficient.
- Permit optional model-proposed repository memory candidates only through bounded structured output and strict host validation.
- Retrieve active relevant repository memory during context assembly with deterministic ranking, budget accounting, sensitivity checks, and context-inspection rationale.
- Invalidate repository-dependent memory at turn boundaries when supporting files, symbols, projects, solution state, or repository revision become stale.
- Include backup/restoration, retention, diagnostics, and redaction behavior compatible with existing persistence and diagnostic-bundle boundaries.

## 4. Non-Scope

- No shared/team memory, tracked memory files, synchronization service, marketplace memory package, or organization-wide memory policy.
- No global personal memory across unrelated repositories.
- No embedding service, vector database, or provider-managed conversation ID.
- No automatic trust of repository-authored files as memory authority.
- No unbounded transcript replay or opaque summary append.
- No mutation, Git commit, branch, push, or configuration change authority from memory content.

## 5. Current State

Threadsmith stores session-scoped conversation messages, conversation memory, provenance edges, summaries, and mode state. That memory restores session continuity, supports compaction, and can be cloned with a session, but it is not a durable repository-level fact base shared by independent sessions opened against the same repository.

The repository already has an ignored `.threadsmith/threadsmith.db` file suitable for local runtime state. `.gitignore` permits tracked `.threadsmith` examples/prompts/skills while continuing to ignore local runtime database state.

## 6. Proposed Design

### 6.1 Repository memory model

Introduce host-owned repository memory records, names illustrative:

```text
RepositoryMemoryItem
RepositoryMemoryId
RepositoryMemoryKind
RepositoryMemoryAuthority
RepositoryMemoryValidity
RepositoryMemoryProvenance
RepositoryMemoryScope
RepositoryMemoryRetrievalResult
```

Initial kinds:

- UserPreference
- UserConstraint
- RepositoryConvention
- ArchitectureDecision
- WorkflowFact
- KnownFailure
- UnresolvedQuestion
- EvidenceBackedRepositoryFact
- RejectedOrSuperseded

Authority values distinguish `UserAuthored`, `HostObserved`, `EvidenceBacked`, and `ModelProposedValidated`. Authority affects preservation, supersession, retrieval priority, and validation requirements.

### 6.2 Persistence boundary

Store records in additive SQLite tables inside `.threadsmith/threadsmith.db`. Suggested tables:

```text
repository_memory
repository_memory_sources
repository_memory_snapshots
repository_memory_invalidations
```

Rows are keyed by repository identity and memory id. The store records schema version, content hash, sensitivity, validity, repository revision, path/symbol/project scope, supersession links, and source references. Forget/reject operations mark validity and retain bounded audit metadata rather than silently deleting provenance.

### 6.3 Creation and promotion

Creation paths:

1. Explicit user command, for example `/memory remember repo "Use src/Threadsmith.sln for full solution builds."`.
2. Host-observed promotion from accepted plans, completed execution outcomes, builds, tests, and validated diagnostics.
3. Evidence-backed repository fact promotion from current governed evidence with path/symbol/revision provenance.
4. Optional compaction-like model proposal using a bounded structured request and schema-valid candidate output.

Assistant prose alone is never sufficient for host-observed completion, validation, repository state, or policy authority.

### 6.4 Commands and headless contracts

Interactive commands, exact names to finalize during implementation:

```text
/memory remember repo <text>
/memory list repo [filter]
/memory inspect <memory-id>
/memory supersede <memory-id> <replacement-text>
/memory forget <memory-id>
/memory validate repo
```

Headless contracts mirror the interactive commands and return stable JSON DTOs. Commands must be cancellation-safe, bounded, deterministic, and tolerant of future schema versions.

### 6.5 Context assembly and inspection

Context assembly adds repository memory as a separate governed source after stable host policy, repository instruction bundles, and current task state, and before lower-authority retrieved historical material. Selection considers:

- explicit user-authored priority;
- current task terms, paths, symbols, projects, and plan scope;
- phase-specific category priority;
- repository revision validity;
- sensitivity/model policy;
- recency and repeated confirmation;
- supersession/validity state;
- configured token/item budgets.

`/context inspect` reports included, omitted, stale, superseded, and sensitivity-blocked repository memory with deterministic rationale and token accounting.

### 6.6 Invalidation and revalidation

Repository-dependent memory subscribes to the same turn-boundary repository and semantic invalidation signals used by evidence/context. Items scoped to paths, symbols, projects, solution state, or repository revision become stale when those supports change. User preferences and constraints are not invalidated unless explicitly scoped to repository facts.

Validation can recheck source files/symbols/revisions where possible and either reactivate, keep stale, or mark rejected with a bounded reason.

### 6.7 Privacy and storage UX

User documentation must make clear that repository memory is local and repository-scoped, stored in `.threadsmith/threadsmith.db`, and ignored by Git by default. It must also explain that shared/team memory is not implemented and that tracked repository files cannot self-authorize durable memory.

## 7. Public Contracts

Potential public contracts:

- `IRepositoryMemoryStore`
- `IRepositoryMemoryGovernor`
- `IRepositoryMemoryRetriever`
- `IRepositoryMemoryInvalidator`
- `RememberRepositoryMemoryCommand`
- `ListRepositoryMemoryCommand`
- `InspectRepositoryMemoryCommand`
- `SupersedeRepositoryMemoryCommand`
- `ForgetRepositoryMemoryCommand`
- `ValidateRepositoryMemoryCommand`
- repository memory DTOs for context inspection and headless output

Contracts must use Threadsmith-owned DTOs only; no provider SDK, terminal-library, Roslyn, SQLite, or extension implementation types leak across boundaries.

## 8. Project/File Changes

Expected areas:

- `Threadsmith.Core` — memory ids, records, commands, events, DTOs.
- `Threadsmith.Context` — retrieval, context assembly integration, inspection projection.
- `Threadsmith.Persistence` — SQLite migration/store in the existing repository DB boundary.
- `Threadsmith.Execution` — host-observed promotion at safe turn/completion boundaries.
- `Threadsmith.App` / `Threadsmith.Cli` / `Threadsmith.Tui` — composition and interactive/headless command routing.
- `tests/Threadsmith.Milestone7_4.Tests` or a new focused milestone test project — persistence, promotion, retrieval, invalidation, and command parity.
- `docs/user-guide.md`, `docs/operations/conversation-context.md`, and architecture docs — implemented behavior updates when shipped.

## 9. Ordered Tasks

1. Add an ADR for repository-scoped cross-session memory and its local SQLite/privacy boundary.
2. Define Core contracts, commands, events, DTOs, categories, authority, validity, and provenance records.
3. Add SQLite migration/store tables in the existing repository persistence path.
4. Implement explicit user-authored remember/list/inspect/supersede/forget/validate commands and headless DTOs.
5. Add deterministic host-observed promotion from accepted plan/execution/build/test/evidence outcomes.
6. Add optional bounded model-proposed candidate generation and strict validation, or defer it behind a disabled feature flag if implementation risk is high.
7. Implement repository memory retrieval, ranking, budgets, sensitivity checks, and context assembly integration.
8. Add repository/semantic invalidation and validation flows.
9. Extend `/context inspect` or add `/memory inspect` projections for inclusion, omission, staleness, and provenance rationale.
10. Add persistence restoration, migration tolerance, retention, diagnostics, and redaction tests.
11. Update user/operator/architecture docs after behavior ships.
12. Run focused milestone, persistence, context, architecture, and formatting verification.

## 10. Testing

Automated coverage must verify:

- explicit user repo memory survives process restart and independent sessions opened against the same repository identity;
- `.threadsmith/threadsmith.db` remains the persistence boundary and tracked repository files cannot create or authorize memory;
- list/inspect/supersede/forget/validate commands have TUI/headless parity and stable JSON output;
- user corrections supersede older repository memory and demote conflicting lower-authority items;
- host-observed facts require real host events and cannot be fabricated by assistant prose or model candidate output;
- model-proposed candidates with bad schema, unsupported sources, excessive size, secret-like content, or invented outcomes are rejected;
- repository-dependent memory invalidates on relevant file/symbol/project/revision change and unrelated user preferences remain active;
- retrieval is deterministic, bounded, phase-aware, sensitivity-compatible, and provenance-preserving;
- context inspection reports included/omitted/stale/superseded/sensitivity-blocked repo memory and pressure reductions;
- migration/restoration tolerate older stores and preserve audit metadata.

## 11. Security/Permissions

Repository memory is untrusted input when assembled into prompts. It cannot override host policy, current user instructions, AGENTS.md contracts, tool availability, approval policy, mutation authority, sensitive-data routing, or output schemas. Repository configuration, prompt appends, skills, and hooks cannot grant memory authority or write memory without explicit host command paths. Secret-like content is rejected or redacted according to existing sanitizer and diagnostic-bundle rules.

## 12. Observability

Emit secret-free events/logs for memory creation, supersession, rejection, invalidation, validation, retrieval, and context inclusion/omission. Diagnostics include ids, categories, authority, validity, source counts, token estimates, and reasons, but never raw secrets, hidden reasoning, provider payloads, or unbounded source bodies.

## 13. Migration/Compatibility

Additive SQLite migrations must be idempotent and rollback-safe. Existing repositories without repository memory tables open normally. Existing session conversation memory remains unchanged. If repository identity cannot be resolved, repository memory commands fail with an actionable message and context assembly omits repository memory.

## 14. Acceptance Criteria

Scenario AM is the product-level acceptance specification for this capability.

- A user can explicitly remember a repository fact, open a new Threadsmith session in the same repository, and see that fact retrieved or listed with provenance.
- A remembered fact stored in `.threadsmith/threadsmith.db` is not tracked by Git and is not represented as shared/team memory.
- Changing a supporting repository file or symbol makes dependent memory stale at the next turn boundary and excludes it from prompt assembly until revalidated.
- A user correction supersedes an older memory item and retrieval prefers the replacement while preserving audit provenance.
- Context inspection explains every included and omitted repository memory item with bounded token accounting.
- A malicious repository file, prompt append, skill, hook, or config cannot silently create, authorize, or elevate repository memory.

## 15. Risks

- Repository memory may make stale local assumptions feel authoritative if invalidation is too weak.
- Users may expect repo memory to be shared because it is repository-scoped; documentation must emphasize local ignored storage.
- Automatic promotion may over-preserve incidental facts; start conservative and prefer explicit user-authored memory.
- Context pressure may increase unless retrieval and budgets remain strict.

## 16. Documentation

When implemented, update:

- `docs/user-guide.md` for commands, storage, privacy, and context behavior;
- `docs/operations/conversation-context.md` or a new memory operations page for operator details;
- `docs/architecture/` with the repository-memory ADR;
- applicable AGENTS.md files only if durable ownership/workflow contracts change.

## 17. Open Decisions

- Final command names and whether `/memory` owns both session and repository memory operations.
- Whether model-proposed repository memory candidates ship in the first implementation or remain disabled until explicit user opt-in.
- Exact repository identity key when the Git remote, path, or `.git` directory changes.
- Default repository-memory token/item budgets and retention policy.
