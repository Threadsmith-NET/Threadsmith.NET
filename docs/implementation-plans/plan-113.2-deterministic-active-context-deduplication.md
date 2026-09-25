# Implementation Plan 113.2: Deterministic Active Context Deduplication

**Status:** Planned.

**Delivery track:** Maintenance — exact active-turn source deduplication and recovery.
**Prerequisites:** [113.1](plan-113.1-active-context-budget-protection-and-measurement.md) accepted with reproducible baseline; implemented plans 80/84/86 and ADR-12/31. Plan 84 retains ownership of its observable `code_explore` capability and remaining acceptance work.
**Sequence:** Second delivery in [plan 113](plan-113-supersession-aware-active-context-management.md).

## 1. Objective

Remove provably duplicated source from previously delivered results before invoking the existing summarizer. Deliver a small, lossless coverage operation over equal or wholly contained ranges, with final-request proof and bounded recovery. Measure incremental savings against 113.1.

## 2. Architectural Context

Keep complete chronological continuation groups as native protocol and summary-cut boundaries. Add per-result identity inside them. Generalize `ModelVisibleSourceFrontierBuilder` in place; there must be one current-request visibility authority. Reuse evidence storage, summary validation, canonical wire estimation, and 113.1 admission.

Read root `AGENTS.md`, planning governance, shared context §G, and C# guardrails. Confirm the active checkout before edits. All execution still passes normal tools, policy, events, activity, and cancellation.

## 3. Scope

- Bounded per-result records with invocation/call/evidence identities and delivery state.
- Built-in exact file reads and `code_explore` source fragments only.
- Equal-range and whole-fragment containment deduplication with snapshot proof.
- One deterministic-before-summary orchestration path in ordinary conversation.
- Final-request visibility validation, atomic history rewrite, and bounded evidence recovery.
- Baseline comparison including overlap, unique evidence, invalidation, and recovery.

## 4. Non-Scope

No partial fragment splitting, search/symbol projectors, semantic-identity supersession, unique-source elision, extension projection API, new summarizer, new project, or duplicated child/mutation planner. No fuzzy matching, prose parsing, guessed relevance, or new production efficiency trigger. These belong to measured decisions in 113.3.

## 5. Current State

Continuation groups retain messages and bounded group provenance; per-call evidence associations are not a first-class projection. Ordinary `read_file` output does not populate its snapshot-only content digest. The existing frontier recognizes `code_explore` ranges. Child evidence recovery already supports bounded line/column reads but rejects stale ordinary evidence.

The evidence store retains the sanitized, already tool-bounded model result. It is not an archive of omitted tool output or the entire original repository file. Preserve this meaning throughout recovery and documentation.

## 6. Proposed Design

### 6.1 Minimal result and fragment contract

Retain ordered host-owned exchange records inside existing groups. Reference existing raw messages and evidence identities rather than copying another transcript. Record only non-derivable delivery/projection state and bounded proof metadata. Render the same call/result correlation and sibling order before and after projection.

Use tool-owned structured fragments, each with an exact source claim and non-removable surrounding result facts. The projector renders retained fragments and compact receipts from a host coverage decision; orchestration must not edit arbitrary prose or switch on tool IDs. Begin with a small internal contract and the two built-in producers; do not build a general public claim framework.

### 6.2 Exact proof

A source fragment may be omitted only when another exact source fragment in the final request contains its entire range, with matching repository/workspace, normalized path, snapshot identity, compatible generation, and model-visible content identity. Prefer the later observation as survivor. Source receipts and summaries never prove exact visibility.

Specify digest domains explicitly: file snapshot digest versus delivered-range digest, encoding/line conventions, and sanitation identity. A larger range's hash cannot be compared directly to a contained range's hash. Prove containment using the same captured snapshot and exact delivered content, or an equivalent bounded intersection verification. Capture proof during the producing observation; never reread a file later to invent its earlier identity. File reads and `code_explore` must agree on the proof convention.

Bounds apply per invocation and active turn. Missing digest, incompatible generation, edited source, malformed metadata, proof eviction, or uncertain identity retains raw content. Never hash or scan the whole repository. Partial overlap retains the entire older fragment in this delivery.

Preserve errors, truncation, omissions, negative/completeness state, relationships, continuation instructions, and all unique source. First verbatim delivery means the tool's declared sanitized, bounded model-facing result reached a completed later model request, using the established contract. This includes an existing `ModelResultContent` projection, not necessarily raw process output or the full typed result. History projection eligibility must not bypass that delivery; hidden structured content does not count as model-visible source. This delivery does not change existing first-delivery projections except for the proof metadata required by its two source producers.

### 6.3 Final-request lifecycle

At existing context pressure or 113.1 admission pressure, propose deterministic reductions, then the existing summary fallback if still necessary. Estimate the entire final request, including dynamically advertised recovery schemas, before admission. A deterministic rewrite must produce positive complete-request savings; otherwise retain the previous representation.

Coverage decisions are dependencies on surviving exact source, not permanent deletion. Example: A is covered by B; B is later summarized. Revalidate the proposed final request and either restore A from its retained original or include its authoritative content in the summary input when A's group is also summarized. Never leave a receipt justified only by a removed B. Apply the same rule to emergency truncation, invalidation, and chains A/B/C.

Use bounded deterministic ordering, no cyclic receipt proof, and at most the documented bounded reconciliation passes; failure to establish a valid plan retains prior material or fails admission safely. Candidate failure/cancellation leaves prior active state intact. A successfully activated combined plan increments history generation once, clears incompatible provider continuation state, and rebuilds the existing frontier. Below pressure, messages and generations remain stable.

Retain attempt identities so identical no-savings attempts are not repeated. Key retries by eligible input identities, projection/model/configuration generations, and relevant budget state; normal round counters alone must not reset the frontier.

### 6.4 Recovery

Extract shared bounded evidence-read mechanics from the child path without broadening child authority or changing its stale-evidence behavior. Advertise the main recovery tool only when the final canonical request contains authorized recoverable references. Include schema cost before deciding savings/admission.

Authorize against current session/run, repository identity, and the IDs actually exposed in that request. Accept bounded range/continuation arguments; return historical sanitized stored content with provenance, staleness, and missing/expired status. Recovered content uses the ordinary tool pipeline and first-delivery rule. A missing body never produces fabricated source or an unrestricted repository read.

Preserve recovery references through cumulative summaries via host-owned metadata. Revoke references no longer exposed. Paginate within existing output/model bounds; reuse keyed store lookup if available or add a focused lookup rather than repeatedly snapshotting/sorting all session evidence. No recovery-of-recovery reference chain is required: point to the original admitted evidence.

## 7. Public Contracts

Keep exchange/fragment/coverage decisions internal unless subsystem boundaries require host DTOs. Additive state is bounded and versioned where persisted. No extension contract changes. One generalized frontier may expose a compatibility view for Plan 84, with one underlying authority.

## 8. Project/File Changes

- Existing tool contracts/pipeline and `BuiltInTools.cs` read-file producer.
- `CodeExploreTool` and its existing typed output boundary/projector.
- `ModelVisibleSourceFrontierBuilder.cs` and `ActiveTurnCompaction.cs`.
- `SessionApplication.ConversationLoop.cs` group capture, final planning, and rewrite activation.
- `ChildAgentEvidenceTool.cs`, existing evidence interfaces/store, and a main recovery tool using shared mechanics.
- Existing conversation-context, model-tooling, caching, architecture, and execution tests; operations and prompt documentation when contracts change.

## 9. Ordered Tasks

1. Confirm 113.1 evidence; inventory group/frontier/evidence/emergency/provider-history consumers and record unsupported loops.
2. Specify digest domains, fragment-to-content ownership, bounds, summary dependency handling, and recovery authorization here. Trace existing producers before choosing DTO placement.
3. Capture per-result identities and verify byte-for-byte unchanged canonical requests before enabling reduction.
4. Implement observation-time proof and registered projectors for file reads and `code_explore`; reject unknown/unverifiable metadata safely.
5. Implement pure equal/contained-fragment coverage and final-request dependency validation; retain partial overlaps.
6. Integrate deterministic-first planning with existing compactor, 113.1 admission, emergency reducer, retry frontier, and atomic cache/history invalidation.
7. Add bounded recovery through the ordinary tool path with shared read mechanics and current-request authorization.
8. Run the required invariant scenarios and replay corpus against 113.1. Review runtime work and metadata growth at declared bounds.
9. Run validation from §10, review actual entry points, update owned docs, and record acceptance evidence. Hand measurements and deferred opportunities to 113.3.

## 10. Testing

Extend `Plan80ActiveTurnCompactionTests`, `Plan84VisibleSourceFrontierTests`, execution-orchestration, model-tooling, and caching suites. No new project or method-count quota.

Required cases: exact duplicate; whole containment; partial overlap retained; different snapshots/workspaces/generations; sanitation mismatch; metadata eviction; sibling calls; never-delivered results; truncated/failed results; A covered by B then B summarized/truncated/invalidated; A/B/C chain; candidate rollback; no-savings backoff; recovery paging, stale/missing evidence and unauthorized IDs; recovery schema eliminating savings; and unknown tool fallback.

Replay overlapping, mostly unique, mutation/invalidation, and recovery-heavy traces with fixed scripted responses. Compare 113.1 and 113.2 using complete estimated requests plus scripted/reported usage as separate measures. Require a strict cumulative-input reduction on the overlap fixture after schema/receipt/recovery overhead, unchanged unique facts, and no increased provider calls on no-op controls. Report other tradeoffs, including metadata size and planner work at bounds.

Run focused suites, architecture tests, solution build, formatting/analyzers, and governance checks using repository-supported commands. Record exact commands and results. A live provider run is optional diagnostic evidence here; deterministic acceptance must not depend on credentials.

## 11. Security/Permissions

All results, receipts, and recovered content remain untrusted. Projection cannot declassify sensitive content or alter policy/approval/tool authority. No arbitrary historical evidence access or extension-owned state. Propagate cancellation through projection, recovery, persistence, and rebuild; do not invoke providers on cancellation or failed admission.

## 12. Observability

Extend existing inspection/events with pressure reason, raw/final estimates, removed/retained source counts, closed proof-rejection reasons, opaque results, recovery availability/calls, avoided summaries, attempt outcome, and rewrite/frontier generation. No source/query/result bodies in normal telemetry. Attribute CPU/allocation measurements separately from source estimates.

## 13. Migration/Compatibility

Old results without proof metadata remain raw or use existing model-summary eligibility. Keep full stored evidence and existing audit retention. Preserve Plan 84 behavior through the same frontier. Providers without stateful continuation continue statelessly. Do not broaden child recovery permissions while sharing mechanics.

## 14. Acceptance Criteria

| ID | Agent-verifiable pass condition | Required evidence |
|---|---|---|
| D1 | Capture-only integration renders identical canonical messages, including existing `ModelResultContent` projections; siblings and first delivery remain valid after activation, and hidden content cannot prove visibility. | Before/after request assertions through conversation entry. |
| D2 | Equal/contained exact source is removed; partial overlap, mismatch, uncertainty, and unique facts are retained. | Coverage/projector theory, including sanitation and bounds. |
| D3 | Every receipt is supported in the final request; summary/truncation/invalidation of its support cannot strand it. | A/B and A/B/C multi-round scenarios and final-frontier assertions. |
| D4 | Deterministic reduction precedes summarization; a resolving reduction invokes no summary; complete savings include recovery schema. | Scripted provider counts and full wire estimates. |
| D5 | Activated changes increment generation once; rejected/cancelled/no-op changes preserve history and provider state. | Cache/history and retry-frontier scenarios. |
| D6 | Recovery returns bounded admitted historical evidence only, supports paging, labels stale/missing bodies, and preserves child restrictions. | Ordinary tool-entry authorization/paging tests. |
| D7 | Overlap replay has strictly lower cumulative input than 113.1 after overhead; no-op controls preserve facts and provider-call counts. | Reproducible fixtures, report, commands, metadata/work bounds. |
| D8 | 113.1 admission and existing Plan 84 contracts pass; no competing frontier/planner or unexplained validation failures remain. | Focused/build/architecture/format/governance results and adversarial review. |

Completion evidence: **Pending.** Record test/report paths, commands/results, digest and lifecycle decisions, supported/unsupported consumers, and limitations here. If D7 fails, revise or narrow this delivery; do not claim optimization from source reasoning alone.

## 15. Risks

Missing observation identities can reduce hit rate; fail closed rather than expanding scope. Recovery and schema costs can erase savings. Restoring dependency source can increase a later request; final admission remains mandatory. Bounded metadata must not become another transcript. First delivery is an eligibility guard, not proof the model will remember removed content.

## 16. Documentation

Update context/tools/inspection operations for exact deduplication, recovery, staleness, triggers, and limits. Add required prompt assets/catalog/reference changes together. Update acceptance/manual documents only for a stable changed workflow, and cite their resulting IDs here. Keep completed plans unchanged.

## 17. Open Decisions

Resolve before Task 3: minimal fragment API placement, snapshot/sanitation proof convention, bounded reconciliation algorithm, evidence lookup extraction, and recovery identifier. The defaults are whole-fragment containment, host verification, fail-closed retention, and no extension API. Record concrete decisions rather than leaving algorithmic gaps to later steps.
