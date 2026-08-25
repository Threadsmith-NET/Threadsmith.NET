# Implementation Plan 84: Context-Aware Code Explore Source Deduplication

**Status:** Active. Production implementation, focused automated coverage, request-local inspection counts, user/operator documentation, clean reviewer pass, and headless MTP-251-style smoke evidence for repeated, subset, reclaimed-budget, short-range, and semantic-readiness paths are in place; full interactive MTP-251 evidence, Scenario AO dedup review, comparative repeated-run evidence, and broader gates remain before completion.

**Delivery track:** Milestone 28 — safe repeated-exploration context efficiency
**Strategy source:** Shared Context §A.2, §A.5, §E, and §G; Milestone 28; Scenario AO
**Prerequisite plans:** plans 35, 52, 54–55, 80, and 83; Plan 83 acceptance and MTP-250 must pass before implementation begins

## 1. Objective

Prevent overlapping `code_explore` follow-ups from repeatedly sending unchanged source the model demonstrably still holds, while never replacing needed source with a pointer to content removed by context reduction, changed on disk, invalidated, or owned by another session/repository. Reclaimed source budget must be used for new relevant evidence rather than merely shrinking the response.

This plan is independently user-testable. Plan 85 must not begin until context-visibility, digest, edit, compaction, session, provider-continuation, and MTP-251 gates pass.

## 2. Architectural Context

Plan 83 produces source-bearing results with exact range/content identity and ranked omitted targets. Plans 35, 52, and 54–55 own model-visible context assembly, chronological tool continuations, cache-aware layout, and provider continuation identity. Plan 80 owns active-turn compaction of complete tool groups. Deduplication must consult the exact request being assembled, not a broad session-history cache, because a session record can outlive the model-visible source it describes.

Threadsmith already retains authoritative full sanitized tool execution and evidence separately from provider-visible context. Deduplication changes only the next model-visible `code_explore` projection and must not delete audit evidence, fabricate model memory, or create a general evidence-reference protocol.

The local repository at `C:\source\repos\codegraph` is a functional reference only for the observable benefit of not resending unchanged source across related exploration calls. Threadsmith is not copying, porting, reverse engineering, depending on, or seeking compatibility with that codebase. Its session-state design, source-range algebra, fingerprints, constants, pointer wording, tests, schemas, internal names, and implementation structure are not normative or reusable.

## 3. Scope

- Record bounded host-owned emission metadata for source ranges actually delivered by `code_explore`: repository/workspace/run identity, tool result/evidence identity, path, range, content digest, generation, and emitted bytes.
- Determine prior coverage only from exact source ranges that survive in the current canonical model request or an explicitly validated verbatim context projection.
- Suppress only sufficiently large fully covered unchanged ranges; re-emit uncertain, partially visible, short, changed, or digest-mismatched content.
- Replace suppressed ranges with compact back-references naming exact file, symbol, line range, digest/generation identity, and the current-request evidence/message holding the source.
- Ensure pointers never consume more context or create more fragmentation than the source they replace.
- Allocate reclaimed file slots/source characters to new high-ranked ranges and files from the same Plan 83 candidate set.
- Guarantee that a response is not pointer-only when useful new or safely restorable source can be emitted; return honest no-new-evidence coverage when nothing new exists.
- Re-emit source after filesystem changes, semantic invalidation, repository/workspace/session changes, compaction/removal, provider restart that rebuilds without prior source, or uncertain visibility.
- Bound metadata by runs, results, files, ranges, and age; safe eviction under-reports coverage and causes harmless re-emission.
- Expose dedup/re-emission/reclaimed-budget rationale in context inspection and telemetry without source content.

## 4. Non-Scope

- Cross-session source suppression, durable global source caches, or assuming resumed/cloned sessions preserve verbatim model context.
- Generic deduplication for every tool or a universal evidence rehydration/reference protocol.
- Deleting or compacting authoritative audit/evidence records.
- Summarizing source and treating the summary as verbatim range coverage.
- Provider-native conversation memory as independent proof that source remains available.
- Natural-language ranking or associated non-C# artifacts.

## 5. Current State

The conversation loop rejects exact duplicate invocations, but different `code_explore` queries may overlap substantially. Plan 80 compacts older active-turn tool groups when pressure rises. Existing session/evidence state can prove that a tool ran, but not by itself that exact source text remains in the request currently sent to the model.

Plans 81–83 provide source digests, exact ranges, result identities, and ranked remaining candidates. Threadsmith now derives a bounded request-local visible-source frontier from complete verbatim `code_explore` tool results in the canonical request, passes that host-owned frontier to `code_explore`, and suppresses only sufficiently large complete unchanged ranges with matching repository, workspace, generation, path, and file digest.

## 6. Proposed Design

### 6.1 Visible-source frontier

During canonical request assembly, derive a bounded `ModelVisibleSourceFrontier` from verbatim retained `code_explore` results only. Each entry maps a result/evidence/message identity to exact path/range/digest coverage. Structurally incomplete but valid reduced JSON, structured summaries, archived-but-not-selected evidence, provider-side history not represented in the canonical request, and ordinary session records do not qualify.

### 6.2 Conservative range coverage

Before rendering a new result, intersect candidate ranges with the visible frontier for the same repository/workspace identity and current content digest. Suppress only complete covered spans above a minimum savings threshold. Coalesce adjacent coverage conservatively; any metadata truncation, missing digest, or content/generation uncertainty forces re-emission.

### 6.3 Pointers and budget reuse

Back-references identify the current-request holder and exact source span, state that the source is unchanged and already present, and never imply the whole file was seen. Their range digest is computed over the exact advertised span, and suppression proceeds only when the serialized back-reference is smaller than the source it replaces. Pointer text/JSON is bounded and provider-neutral. Freed source allowance and file slots flow back through Plan 83 allocation so lower-ranked new evidence can enter; diagnostics count only the later source that actually used reclaimed capacity.

### 6.4 Context and provider lifecycle

Successful active-turn compaction recomputes the frontier. If exact source is summarized away, it stops qualifying. Provider cache/stateful continuation may accelerate a compatible request but cannot widen the frontier beyond host-owned canonical messages. Session resume/clone/new/repository switch start from the exact assembled context and default to re-emission unless verbatim coverage is independently present and validated.

## 7. Public Contracts

Expected host-owned contracts include:

- `ModelVisibleSourceFrontier` and entries identifying canonical message/evidence/result, repository/workspace, path, range, digest, and visibility generation;
- `CodeExploreEmissionRecord` — actual survived source ranges and bytes from one result;
- `CodeExploreBackReference` — holder identity, path, symbol IDs, covered ranges, digest, and unchanged assertion;
- `CodeExploreDedupSummary` — candidate/covered/suppressed/re-emitted ranges, reclaimed/used characters, and closed reasons;
- context inspection additions for visible coverage, pointer eligibility, source restored after reduction, and provider continuation reset relationships.

These contracts are bounded, serializable, provider-neutral, and contain no source bodies in telemetry/persistence-only projections.

## 8. Project/File Changes

Expected areas:

- `Threadsmith.Core` — source-frontier, emission, back-reference, and inspection DTOs.
- `Threadsmith.Context` — derive exact visible range coverage during canonical assembly and recompute after compaction/reduction.
- `Threadsmith.Tools`/`Threadsmith.DotNet` — consume validated coverage, render pointers, reallocate freed budget, and emit actual survived ranges.
- `Threadsmith.Execution` — pass request-local coverage to invocation/result projection without making it model-controlled input.
- `Threadsmith.Models` and compiled providers — preserve canonical holder identities and reset incompatible continuation/cache state.
- Persistence only if bounded resume inspection needs DTO records; never persist Roslyn objects or use persistence alone as visibility proof.
- Telemetry, TUI/headless inspection, focused context/tool/provider/session tests, docs, Scenario AO, and MTP-251.

## 9. Ordered Tasks

1. Verify Plan 83 acceptance evidence and MTP-250; verify Plan 80 active-turn compaction contracts and tests are complete.
2. Profile overlapping exploration calls and capture repeated-source/context/latency baselines.
3. Freeze visible-frontier authority, digest/range identity, minimum savings, metadata bounds, pointer, eviction, and lifecycle semantics.
4. Implement exact frontier derivation from canonical verbatim model messages and recomputation after reduction.
5. Implement conservative coverage/digest intersection and actual-emission accounting.
6. Add bounded back-references and feed reclaimed capacity through Plan 83 allocation.
7. Integrate edit/invalidation, active compaction, provider cache/stateful continuation, session resume/clone/new, and repository switching.
8. Add observability, inspection, cancellation, persistence compatibility, and redaction.
9. Add full/partial/short/changed/compacted/evicted/cross-session/cross-repository/provider-restart fixtures.
10. Run focused tests, provider/context/session/tool tests, architecture tests, solution build, formatting, and planning-governance checks.
11. Run MTP-251 interactively and headlessly; record checkpoint evidence before changing status.
12. Complete docs/DOX closeout. Begin Plan 85 only after this plan's acceptance and user-testable gate pass.

## 10. Testing

Focused automated coverage now verifies that the visible-source frontier admits only complete verbatim `code_explore` source with digests; skips partial, malformed, structurally incomplete reduced, and non-`code_explore` tool results; suppresses a repeated complete unchanged range with an exact back-reference; hashes subset back-references over the exact advertised range; rejects pointers whose actual serialized size would not save context; re-emits changed or short content without overstating re-emission counts; emits audit records only for actual source lines; and reports reclaimed-budget use only for later source admitted because suppression freed capacity. Existing Plan 83 tests continue to verify result metadata bounding, path-policy filtering, ranking, and source allocation. Headless CLI smokes against this repository have covered repeated range suppression, larger-range-to-subset suppression, overlap plus new source using reclaimed budget, short-range re-emission, and fail-closed semantic readiness.

Remaining acceptance coverage must still include repository/workspace mismatch, metadata eviction, compaction removal, provider restart/cache identity, resumed/cloned/new sessions, cancellation, deterministic order, audit preservation, redaction, full interactive/headless equivalence, and comparative repeated-run measurements.

The user-testable checkpoint is [MTP-251](manual-test-plan.md#mtp-251--context-proven-exploration-source-deduplication). It blocks Plan 85.

## 11. Security/Permissions

Coverage metadata is host-owned and request-local; model arguments cannot claim prior visibility or a digest. A pointer grants no file, evidence, session, repository, mutation, process, network, or approval authority. Cross-session/repository suppression is forbidden. Digests and holder identities follow existing privacy/redaction policy. Deduplication cannot remove authoritative evidence or weaken retention/deletion rules.

## 12. Observability

Record frontier entries/ranges/bytes, candidate/covered/suppressed/re-emitted counts, pointer bytes, reclaimed/redistributed source, disqualification reasons, digest/generation mismatch counts, reduction/provider reset relationships, duration, cancellation, and sanitized outcome. Do not log source, pointer bodies containing paths beyond safe projections, query text, model messages, hidden reasoning, provider payloads, or credentials.

## 13. Migration/Compatibility

Deduplication defaults conservatively off when frontier metadata is absent or unsupported. Existing sessions/results remain readable and simply re-emit source. Disabling the feature changes efficiency only, not semantic result correctness. Schema evolution is additive; unknown coverage/back-reference versions fail closed to source re-emission. Audit data and existing active-turn summaries require no destructive migration.

## 14. Acceptance Criteria

- Source is suppressed only for exact unchanged ranges present verbatim in the current canonical model request.
- Edits, invalidation, compaction/removal, uncertainty, session/repository changes, and incompatible provider continuation cause safe re-emission.
- Back-references identify precise prior holders and ranges without claiming unseen source.
- Reclaimed budget admits new ranked evidence and repeated overlapping calls reduce model-visible source without pointer fragmentation.
- Complete original sanitized tool evidence remains auditable and retention/redaction behavior is unchanged.
- Focused context/provider/session/tool tests, architecture tests, solution build, Scenario AO dedup behavior, and MTP-251 pass.
- Comparative runs show lower repeated context with equal or better correctness and no cache/stateful-continuation regression.

## 15. Risks

- **Pointer references content no longer visible:** derive coverage only from the exact assembled request and recompute after every reduction.
- **Digest/range mismatch hides changed source:** require exact identity; uncertainty re-emits.
- **Pointers fragment readable code:** suppress only meaningful complete spans above a savings threshold.
- **Metadata grows without bound:** cap details and evict toward under-reporting/re-emission.
- **Provider history is mistaken for host proof:** canonical host messages alone define visibility.
- **Freed bytes disappear instead of improving recall:** integrate with allocation and assert redistribution.

## 16. Documentation

When implemented, document pointer meaning, current-context requirement, edit/compaction/session behavior, inspection fields, and failure-safe re-emission in context and native-tool operations docs. Maintain Scenario AO, MTP-251, provider/cache documentation, event catalog additions, and DOX only where durable ownership changes.

Reviews must reiterate that `C:\source\repos\codegraph` is functional reference only. No source, state design, range algorithm, constants, pointer wording, schema, tests, names, or internal structure is to be copied or reverse engineered.

## 17. Open Decisions

- Whether the visible frontier belongs in `Threadsmith.Context` only or needs a smaller Core request-scoped contract.
- Minimum covered lines/characters and pointer-cost threshold, derived from Threadsmith measurements rather than external constants.
- Whether range digests or full-file digests best balance exact proof and recomputation cost.
- How active-turn summaries may explicitly preserve verbatim source, if ever, without treating paraphrase as coverage.
- Whether bounded emission metadata needs durable storage for diagnostics even though it cannot prove restored visibility.
