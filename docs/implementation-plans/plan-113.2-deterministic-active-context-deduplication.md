# Implementation Plan 113.2: Deterministic Active Context Deduplication

**Status:** Complete (2026-09-25).

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

No partial fragment splitting, cross-producer coverage between `read_file` and `code_explore`, search/symbol projectors, semantic-identity supersession, unique-source elision, extension projection API, new summarizer, new project, or duplicated child/mutation planner. No fuzzy matching, prose parsing, guessed relevance, or new production efficiency trigger. These belong to measured decisions in 113.3.

## 5. Current State

Continuation groups retain messages and bounded group provenance; per-call evidence associations are not a first-class projection. Ordinary `read_file` output does not populate its snapshot-only content digest. The existing frontier recognizes `code_explore` ranges. Child evidence recovery already supports bounded line/column reads but rejects stale ordinary evidence.

The evidence store retains the sanitized, already tool-bounded model result. It is not an archive of omitted tool output or the entire original repository file. Preserve this meaning throughout recovery and documentation.

## 6. Proposed Design

### 6.1 Minimal result and fragment contract

Retain ordered host-owned exchange records inside existing groups. Reference existing raw messages and evidence identities rather than copying another transcript. Record only non-derivable delivery/projection state and bounded proof metadata. Render the same call/result correlation and sibling order before and after projection.

Use tool-owned structured fragments, each with an exact source claim and non-removable surrounding result facts. The projector renders retained fragments and compact receipts from a host coverage decision; orchestration must not edit arbitrary prose or switch on tool IDs. Begin with a small internal contract and the two built-in producers; do not build a general public claim framework.

### 6.2 Exact proof

A source fragment may be omitted only when another exact source fragment in the final request contains its entire range, with matching repository/workspace, normalized path, snapshot identity, compatible generation, and model-visible content identity. Prefer the later observation as survivor. Source receipts and summaries never prove exact visibility.

Specify digest domains explicitly: file snapshot digest versus delivered-range digest, encoding/line conventions, and sanitation identity. A larger range's hash cannot be compared directly to a contained range's hash. Prove containment using the same captured snapshot and exact delivered content, or an equivalent bounded intersection verification. Capture proof during the producing observation; never reread a file later to invent its earlier identity. File reads and `code_explore` agree on raw-file SHA-256 and exact sanitized delivered-line comparison. `code_explore` additionally requires an equal workspace generation. Coverage remains within one producer because its presentation convention is part of delivered identity; cross-producer normalization is deferred until measurement justifies it.

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

Recovery references remain authorized only while their receipts are exposed in raw retained groups. Summarizing a receipt-bearing group revokes its reference; this delivery does not add source-attribution metadata to model-written summaries or imply that a summary preserves the omitted body exactly. Paginate within existing output/model bounds; reuse keyed store lookup if available or add a focused lookup rather than repeatedly snapshotting/sorting all session evidence. No recovery-of-recovery reference chain is required: point to the original admitted evidence.

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

Existing active-turn inspection records the pressure reason, raw/final estimates, candidate/removed/retained/opaque counts, reclaimed characters, avoided-summary status, attempt outcome, and rewrite/frontier generation. Proof failures remain fail-closed in the aggregate opaque/retained counts; this delivery does not add a cardinality-bearing reason dimension. Recovery availability is observable from the dynamically advertised tool schema, and recovery calls use the ordinary tool invocation events and activity instead of a second counter path. No source/query/result bodies enter normal telemetry. The evaluation labels planner-work bounds as source-derived; CPU and allocation measurements remain unclaimed.

## 13. Migration/Compatibility

Old results without proof metadata remain raw or use existing model-summary eligibility. Keep full stored evidence and existing audit retention. Preserve Plan 84 behavior through the same frontier. Providers without stateful continuation continue statelessly. Do not broaden child recovery permissions while sharing mechanics.

## 14. Acceptance Criteria

| ID | Agent-verifiable pass condition | Required evidence |
|---|---|---|
| D1 | Capture-only integration renders identical canonical messages, including existing `ModelResultContent` projections; siblings and first delivery remain valid after activation, and hidden content cannot prove visibility. | Before/after request assertions through conversation entry. |
| D2 | Equal/contained exact source from the same supported producer is removed; partial overlap, cross-producer overlap, mismatch, uncertainty, and unique facts are retained. | Coverage/projector theory, including sanitation and bounds. |
| D3 | Every receipt in retained raw groups is supported in the final request; summary removal revokes its reference, and truncation/invalidation of surviving support cannot strand it. | A/B and A/B/C scenarios, combined-summary candidate projection, and final-frontier assertions. |
| D4 | Deterministic reduction precedes summarization; a resolving reduction invokes no summary; complete savings include recovery schema. | Scripted provider counts and full wire estimates. |
| D5 | Activated changes increment generation once; rejected/cancelled/no-op changes preserve history and provider state. | Cache/history and retry-frontier scenarios. |
| D6 | Recovery returns bounded admitted historical evidence only, supports paging, labels stale/missing bodies, and preserves child restrictions. | Ordinary tool-entry authorization/paging tests. |
| D7 | Overlap replay has strictly lower cumulative input than 113.1 after overhead; no-op controls preserve facts and provider-call counts. | Reproducible fixtures, report, commands, metadata/work bounds. |
| D8 | 113.1 admission and existing Plan 84 contracts pass; no competing frontier/planner or unexplained validation failures remain. | Focused/build/architecture/format/governance results and adversarial review. |

Completion evidence (2026-09-25):

- D1/D2/D3: `Plan113ActiveTurnSourceProjectionTests` exercises the ordinary conversation path, exact equal/contained A/B/C coverage, visible and hidden `code_explore` siblings, partial/cross-producer/snapshot/sanitation/workspace mismatch, first delivery, failed/evicted/unknown results, the 256-candidate bound, final receipt validation, an activated projection flowing into a later summary candidate, ordinary-profile attribution, and matched activity completion after a delivered start reports publication failure. The existing Plan 80 rollback/cancellation suite remains green.
- D4/D5: the resolving overlap fixture performs three ordinary model calls, no summary call, and one history-generation increment. The tiny-duplicate control keeps both results raw, advertises no recovery schema, performs the same three calls, and leaves generation at zero. Summary fallback reprojects each exact selected prefix, so a receipt never depends on source outside the candidate input.
- D6: recovery paging, stale, missing, unauthorized, repository-mismatch, and multi-file provenance cases all enter through `ToolInvocationPipeline`. Authorization binds current session/run, repository identity, request-exposed evidence ID, and the original tool invocation ID; lookup uses keyed `IEvidenceStore.Find` and never rereads the repository.
- D7: [the deterministic evaluation report](evidence/plan-113.2-deduplication-evaluation.md) asserts request estimates of 3,141, 12,628, and 13,098 tokens. Against the 22,161-token third-request 113.1 representation, the projected third request saves 9,063 tokens (40.9%); cumulative estimated input falls from 37,930 to 28,867 tokens (23.9%) with unchanged provider-call count. The report states workload limits and bounded planner-work estimates.
- D8 validation: Plan 113.2 focused tests passed 18/18; Plan 84 frontier tests passed 8/8; read-file focused tests passed 15/15. Full suites passed: Planning 164 before the added lifecycle-observability regression, Conversation Context 150, Model Tooling 836 with 11 declared skips, Execution Orchestration 86, Context Caching 53 with three filesystem-capability skips, and Architecture 281 with four live-environment skips. The repository-supported `dotnet build src/Threadsmith.sln --no-restore` passed with zero warnings/errors, and `git diff --check` passed. Repository-wide `dotnet format --verify-no-changes` still reports the documented pre-existing whitespace/final-newline baseline across unrelated files; the changed `ActiveTurnCompaction.cs` file was formatted directly. An extra `-p:AnalysisLevel=latest-all` diagnostic is not a repository gate and reports the solution's existing opt-in analyzer backlog, so it is not claimed as passing evidence.
- The supported optimizer is the primary `SessionApplication` ordinary conversation loop for built-in `read_file`→`read_file` and `code_explore`→`code_explore` observations. Child-agent, skill, mutation, MCP, search/symbol, extension, and cross-producer projection remain unsupported and raw. Summaries revoke references from removed raw receipt groups because this delivery does not add exact source attribution to model-written summaries.
- Two independent adversarial reviews traced runtime ownership, summary integration, recovery, bounds, tests, and evidence. Valid findings were fixed: multi-file authorization, decoding compatibility, dynamic recovery schemas, visible/hidden sibling preservation, dependency-safe summary inputs, active-projection reuse, attempt identity, ordinary-pipeline recovery, exact metric assertions, no-savings control, bounded fail-closed cases, and documentation accuracy. Both final reviews were clean.

## 15. Risks

Missing observation identities can reduce hit rate; fail closed rather than expanding scope. Recovery and schema costs can erase savings. Restoring dependency source can increase a later request; final admission remains mandatory. Bounded metadata must not become another transcript. First delivery is an eligibility guard, not proof the model will remember removed content.

## 16. Documentation

Update context/tools/inspection operations for exact deduplication, recovery, staleness, triggers, and limits. Add required prompt assets/catalog/reference changes together. Update acceptance/manual documents only for a stable changed workflow, and cite their resulting IDs here. Keep completed plans unchanged.

## 17. Resolved Decisions

- Fragment and coverage machinery remains internal to `Threadsmith.Execution`; subsystem boundaries use small host-owned result/evidence DTOs, with no extension API.
- `read_file` hashes the original file bytes with SHA-256, preserves BOM-aware replacement decoding, sanitizes before line selection, and hashes selected sanitized lines joined by LF as UTF-8. Coverage additionally compares the captured delivered lines exactly. `code_explore` uses its captured file/range identities, exact numbered lines, and workspace generation. Coverage is same-producer only.
- Reconciliation walks at most 256 candidates newest-to-oldest and points A/B/C receipts directly at the newest retained raw support. Combined summary fallback independently reprojects each of at most 48 exact prefixes; uncertain or out-of-bound material remains raw.
- `IEvidenceStore.Find` owns keyed historical lookup. The recovery identifier is the original evidence ID, authorized by request-local reference plus session, run, repository, and producing invocation identity. `read_active_turn_evidence` is advertised only while such a receipt remains in the final request.
