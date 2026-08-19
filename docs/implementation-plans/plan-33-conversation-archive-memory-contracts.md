# Plan 33 — Conversation Archive and Governed Memory Contracts

**Milestone:** 7.4 (Cross-Turn Conversation Context and Compaction)
**Prerequisites:** plans 02, 03, 09, and 18
**Depends on by:** plans 34 and 35
**Status:** Complete.

**Implementation result:** Host-owned contracts, migration 2 persistence, deterministic sequencing, sanitization/artifact offloading, tolerant restoration, retention integration, visible-boundary capture, metadata-only events, and focused Plan 33 tests are implemented.

## 1 Objective

Add host-owned, versioned, durable contracts for conversation messages, cross-turn provenance, selectable conversation modes, structured session memory, supersession, and restoration so later compaction and request assembly never depend on replaying an opaque provider transcript.

## 2 Architectural Context

ADR-12 correctly rejected unbounded raw-transcript replay but over-constrained context assembly by declaring conversation history not to be an input category. The current `EvidenceStore` retains evidence by session, yet `SelectForRequest` admits only the current `RunId` or explicitly session-scoped entries. User messages, visible assistant responses, decisions, corrections, unresolved questions, and prior-run tool findings are therefore not reliably available to a later turn.

ADR-31 amends that decision: raw messages are an archived provenance source, while bounded recent turns and typed governed memory are legitimate context categories according to an explicit mode. The host owns schemas, sanitization, selection, invalidation, and inspection.

## 3 Scope

- Add a stable `ConversationMessageId` and versioned host-owned conversation/message contracts.
- Add `ConversationContextMode`: `ConversationAware`, `GovernedMemoryOnly`, and `Stateless`, with compiled default session state.
- Archive sanitized user messages and user-visible assistant responses with session/run/message provenance.
- Define typed structured memory categories: user requirements, decisions, constraints, unresolved questions, repository findings, completed work, and rejected/superseded information.
- Preserve links from each memory item and summary item to originating message IDs, run IDs, evidence IDs, repository revision, and optional artifact IDs.
- Add explicit supersession and repository-dependency metadata.
- Persist and restore archive and governed memory state with ordered migrations and tolerant schema versions.
- Add projections required for inspection without exposing provider SDK or raw reasoning types.
- Establish retention behavior: full messages may move to artifacts but provenance and structured memory remain durable according to policy.

## 4 Non-Scope

- Selecting recent turns for a model request.
- Model-assisted summary generation.
- Semantic/lexical retrieval ranking.
- Token-pressure compaction triggers.
- Layered mode/budget configuration, TUI/headless commands, or context-inspection rendering.
- Persisting hidden model reasoning, raw provider requests/responses, secrets, or extension-owned types.
- Embedding/vector-database infrastructure.

## 5 Current State

`SessionProjection` and persisted domain events track execution state, tool activity, usage, repository status, and completion, but do not represent an ordered conversation archive. `ContextInspectionProjection` reports assembled categories for one request. `EvidenceItem` supports `RunId`, relevance, repository dependency, and provenance fields, but prior-run tool evidence is excluded by current selection. Session restoration restores events and projections, not a structured conversational memory model.

## 6 Proposed Design

### 6.1 Conversation modes

```csharp
public enum ConversationContextMode
{
    ConversationAware,
    GovernedMemoryOnly,
    Stateless,
}
```

The compiled default session state is `ConversationAware`. Plan 35 adds normal layered initialization and host-owned session commands. Changing mode affects future request assembly; it does not delete archive or memory.

### 6.2 Archived messages

A versioned `ConversationMessage` record contains:

- stable `ConversationMessageId`;
- `SessionId` and `RunId`;
- monotonic sequence number;
- role limited to host-owned `User` and `Assistant` values;
- sanitized visible content or an artifact reference for bounded storage;
- occurred-at timestamp;
- content hash and token estimate;
- sensitivity classification;
- repository revision at capture where applicable;
- schema version.

Only user-visible assistant content is archived. Hidden reasoning, provider wire content, raw tool payloads, and transient status text are excluded. Tool findings enter governed evidence/memory with their own provenance rather than masquerading as assistant messages.

### 6.3 Structured memory

Use typed items rather than one opaque summary string:

```csharp
public enum ConversationMemoryKind
{
    UserRequirement,
    Decision,
    Constraint,
    UnresolvedQuestion,
    RepositoryFinding,
    CompletedWork,
    RejectedOrSuperseded,
}

public sealed record ConversationMemoryItem
{
    public required ConversationMemoryId Id { get; init; }
    public required ConversationMemoryKind Kind { get; init; }
    public required string Content { get; init; }
    public required IReadOnlyList<ConversationMessageId> SourceMessageIds { get; init; }
    public IReadOnlyList<RunId> SourceRunIds { get; init; } = [];
    public IReadOnlyList<EvidenceId> SourceEvidenceIds { get; init; } = [];
    public string? RepositoryRevision { get; init; }
    public bool RepositoryDependent { get; init; }
    public ConversationMemoryId? SupersedesId { get; init; }
    public MemoryValidity Validity { get; init; }
}
```

The concrete design may use separate category records if that improves validation. Every item must remain independently attributable, invalidatable, and supersedable.

### 6.4 Structured summary snapshot

`ConversationSummarySnapshot` is a host-owned aggregate of ordered memory IDs grouped by the seven categories, plus sequence/revision/schema metadata. It is not free-form provider output. Snapshot replacement is atomic; older snapshots remain auditable through events/artifacts until retention removes them.

### 6.5 Events and projections

Add versioned events such as:

- `ConversationMessageArchived`;
- `ConversationModeChanged`;
- `ConversationMemoryPromoted`;
- `ConversationMemorySuperseded`;
- `ConversationMemoryInvalidated`;
- `ConversationSummarySnapshotReplaced`.

Add a `ConversationMemoryProjection` containing only host-owned sanitized DTOs and bounded previews. Update `docs/architecture/event-catalog.md` during implementation.

### 6.6 Persistence and restoration

Add an ordered migration for conversation messages, memory items, message-memory provenance edges, summary snapshots, and mode state. Large message content uses `ArtifactStore`; SQLite retains order, hash, role, sensitivity, and artifact linkage. `SessionRestorer` tolerates unknown future memory schema versions by preserving archive references and reporting a restoration warning rather than fabricating memory.

## 7 Public Contracts

Public Core contracts should be limited to identifiers, enums, immutable commands/events/projections, and serializable DTOs. Persistence implementation records remain internal. No terminal, provider, embedding, Roslyn, or extension implementation types cross the boundary.

Commands should include host-owned equivalents of:

- `SetConversationContextMode`;
- query conversation archive metadata;
- query structured memory/snapshot metadata.

Mutation of summary/memory contents remains an internal governed application operation, not a public arbitrary-write command.

## 8 Project/File Changes

- `Threadsmith.Core` — IDs, modes, memory/message DTOs, commands, events, projections.
- `Threadsmith.Execution` — capture visible user/assistant messages at authoritative boundaries.
- `Threadsmith.Context` — archive/memory interfaces and immutable snapshots.
- `Threadsmith.Persistence` — migration, stores, artifact linkage, restoration.
- `Threadsmith.Tui` / `Threadsmith.Cli` — no feature surface yet; compile against projection additions only.
- Milestone 4/8 and persistence tests plus architecture tests.
- Event catalog, ADR-31 references, DOX indexes.

Any JSON fixtures must use `CopyToOutputDirectory=PreserveNewest`.

## 9 Ordered Tasks

1. Add `ConversationMessageId`, `ConversationMemoryId`, enums, schema versions, and serialization tests.
2. Define immutable archived-message, memory, summary, provenance, supersession, and validity contracts.
3. Add versioned domain events and bounded projections; update the event catalog.
4. Capture each accepted user request before execution and each final user-visible assistant response after sanitization.
5. Exclude hidden reasoning, provider wire content, raw tool output, and transient rendering from the archive.
6. Add conversation mode session state with compiled `ConversationAware` default; defer layered initialization and controls to plan 35.
7. Add transactional persistence migration and stores, using artifacts for large message bodies.
8. Extend session restoration and unknown-version tolerance.
9. Add retention behavior that can remove archived bodies without orphaning provenance metadata.
10. Add unit, persistence, restoration, sanitization, ordering, and architecture tests.
11. Update shared context, event catalog, milestone status, and nearest DOX docs.

## 10 Testing

Automated tests must verify:

- stable message/memory IDs round-trip and compare correctly;
- concurrent capture produces deterministic per-session order;
- only sanitized visible user/assistant content is archived;
- hidden reasoning, secrets, provider DTOs, and raw tool output are absent;
- structured categories and provenance edges round-trip;
- supersession preserves the old item and identifies the replacement;
- large bodies use artifacts and hashes verify on restoration;
- mode state restores without deleting data from other modes;
- unknown event/memory schema versions produce bounded warnings;
- retention cannot leave a summary item with fabricated provenance;
- architecture tests reject provider/terminal/extension types in durable contracts.

## 11 Security/Permissions

Conversation archive is sensitive durable state. Sanitize before persistence, classify sensitivity, prohibit secret/reference expansion, confine artifacts to the existing artifact store, and include archive/memory in retention and diagnostic-bundle redaction audits. Future mode/budget configuration cannot disable sanitization, provenance, hard byte limits, or hidden-reasoning exclusion.

## 12 Observability

Emit bounded counts, sequence numbers, category, sensitivity class, artifact use, schema version, and restoration outcome. Never log message bodies, summary contents, secret values, raw prompts, hidden reasoning, or provider responses.

## 13 Migration/Compatibility

Existing sessions have no conversation archive. Restore them with an empty archive/memory projection and an explicit `LegacySessionWithoutConversationArchive` diagnostic; do not reconstruct messages from unrelated historical events. Existing evidence and execution records remain unchanged.

## 14 Acceptance Criteria

- The host durably archives sanitized visible user/assistant messages with stable provenance and deterministic order.
- The three conversation modes exist, default to `ConversationAware`, persist per session, and do not delete underlying data.
- Structured memory distinguishes all seven required categories.
- Every memory item traces to originating messages/runs and optional evidence/repository revision.
- Supersession and invalidation preserve audit history.
- Session restoration, retention, diagnostic redaction, and schema tolerance cover the new records.
- No raw reasoning, provider wire types, terminal types, extension types, or secrets enter durable conversation state.

## 15 Risks

- Archiving increases sensitive-data retention. Mitigate with sanitization, artifacts, policy bounds, and retention.
- Assistant text may contain untrusted claims. Archive is provenance, not truth; promotion/validation belongs to plan 34.
- Schema proliferation can complicate restoration. Use versioned host-owned records and ordered migrations.
- Capturing at rendering boundaries could diverge across CLI/TUI. Capture once in application coordination before projection.

## 16 Documentation

Update event catalog, shared context, architecture docs, persistence/retention operations guidance, implementation status, and DOX. User-facing mode and command documentation waits for plan 35 implementation.

## 17 Decisions

- Full conversation bodies use an independent 30-day compiled retention age (`ConversationMessageBodyAge`), raw turns have a seven-day hot eligibility window, and compacted structured memory/provenance remains cold durable session state. `RetainConversationBodies` may preserve bodies; compaction never deletes them.
- Vector embeddings are not part of the archive contract. Milestone 7.4 uses deterministic host-owned retrieval with no external vector service.
