## Milestone 25 - Repository-Scoped Cross-Session Memory *(plan 78)*

**Status:** See the [authoritative milestone index](../milestones.md).
**Objective:** Add host-governed repository memory that survives across Threadsmith sessions for the same repository, using the existing ignored repository SQLite store while keeping team/shared memory out of scope.

**Deliverables:**
- Repository-scoped durable memory records stored in the existing repository-local `.threadsmith/threadsmith.db` persistence boundary.
- Typed memory categories for explicit user preferences/constraints, repository conventions, workflow facts, architecture decisions, known failures, unresolved questions, and evidence-backed repository facts.
- Explicit user commands and headless contracts to remember, list, inspect, supersede, forget, and validate repository memory.
- Host-observed promotion from accepted plans, completed execution/build/test results, and governed evidence without treating assistant prose as authoritative.
- Optional model-proposed memory candidates only through bounded structured output with strict host validation, provenance, sanitization, and supersession checks.
- Repository identity, revision, path/symbol/file provenance, sensitivity, authority, validity, and invalidation metadata on every memory item.
- Context assembly integration that retrieves only active, relevant, bounded, sensitivity-compatible repository memory and exposes inclusion/omission rationale in context inspection.
- Retention, diagnostics, redaction, backup/restoration, and migration behavior consistent with existing repository persistence.

**Exit criteria:**
- Repository memory survives `/new`, `/resume`, process restart, and independent sessions opened against the same repository identity.
- Repository memory does not leave the existing ignored repository SQLite persistence boundary unless the user invokes an explicit future export feature; no shared/team memory is introduced.
- A repository cannot self-authorize memory authority through tracked files, prompt appends, skills, hooks, or ordinary repository configuration.
- User-authored memory has explicit source provenance and stronger preservation/supersession authority than model-proposed or assistant-inferred content.
- Host-observed facts require host-owned events or governed evidence; assistant claims cannot fabricate completed work, validation, repository facts, or policy grants.
- Repository-dependent memory is invalidated at turn boundaries when supporting files, symbols, project state, or repository revision become stale.
- Context inclusion is bounded by configured budgets, selected-model sensitivity policy, current phase, relevance, and validity; omitted memory is inspectable.
- Commands and headless contracts are deterministic, cancellation-safe, schema/version tolerant, and covered by focused persistence/context tests.
- User/operator documentation explains storage location, privacy boundary, commands, invalidation, and how repo memory differs from session conversation memory.

**Prerequisites:** M7.4, M8, M19, M20, and M23.4.

**Scope decisions:**
- Use `.threadsmith/threadsmith.db` as the default repository memory store; the file remains ignored by Git.
- Memory is repository-scoped and user-local by default, not global personal memory and not shared/team memory.
- Shared tracked memory files, team synchronization, marketplace memory packs, embeddings, vector databases, and provider-managed conversation IDs are out of scope.
- Repository memory is structured, attributable, bounded, inspectable, and invalidatable; it is never an opaque transcript summary appended to every request.
- Existing session conversation memory remains session-scoped; repository memory is a separate retrieval source with separate authority and invalidation.

---

[Back to the milestone index](../milestones.md) · [Dependency DAG](dependency-dag.md)
