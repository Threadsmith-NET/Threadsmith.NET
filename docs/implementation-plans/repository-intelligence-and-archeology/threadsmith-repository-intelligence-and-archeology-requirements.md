# Threadsmith.NET: Repository Intelligence and Repository Archeology

**Document type:** Parent requirements specification  
**Version:** 0.3 — freshness admission, one-off retention, and context eligibility  
**Date:** 2026-10-07  
**Status:** Proposed requirements for review  
**Product scope:** Single-user, local-first Threadsmith.NET

## 1. Purpose and intended outcome

Threadsmith.NET needs durable, evidence-backed knowledge of a repository's engineering intent and architectural evolution. A capable coding agent can understand current code yet still propose a change that reverses a deliberate decision, recreates a historical failure, or violates an invariant whose original implementation has since disappeared.

This specification defines two complementary capabilities:

- **Repository Intelligence** maintains structured, selectively retrievable knowledge about decisions, constraints, conventions, migrations, reversals, historical failures, persistent patterns, and unresolved tensions.
- **Repository Archeology** investigates current and historical repository evidence to discover, explain, challenge, and update that knowledge.

The intended outcome is better coding decisions: relevant guidance appears when useful, the agent can inspect the reasoning behind it, and both the agent and user can verify the supporting evidence. Producing an architectural report is a useful projection, but the primary product is maintained intelligence that affects everyday repository work.

This document is the parent specification for later incremental implementation plans. It defines required behavior, conceptual information, integration boundaries, and acceptance outcomes. It does not prescribe database tables, DTOs, prompt templates, scoring formulas, class structure, implementation order, or a delivery schedule.

### 1.1 Basis and requirement language

The design follows the converged discussion in [Improve Threadsmith Memory Recall](chatgpt-conversation://6aa8abb7-a5a4-83e9-907e-81f6d56c8d7a), including the two profiling experiments and the subsequent hybrid pipeline discussion. The experiments inform the requirements; their output is not treated as verified architectural truth about the current Threadsmith.NET repository.

Context admission and invalidation must preserve the existing [context policy](../../architecture/context-policy.md) and [ADR-31 conversation modes](../../architecture/adr-31-bounded-conversational-continuity.md). All operations, including nonpersistent investigations, remain subject to the normal [ADR-6 durable session/event model](../../architecture/adr-06-event-oriented-durable-session-model.md). The behavioral decisions below extend these boundaries through narrow integration touch points; they do not authorize alternate execution or context paths.

**Must** denotes required behavior. **Should** denotes a preferred outcome that a later plan may vary with an explicit rationale. **May** denotes an optional capability. Requirement identifiers are stable references for implementation plans and acceptance checks; their order is not an implementation sequence.

## 2. Scope and operating principles

### 2.1 Required scope

The optional capability covers explicit repository onboarding, a pinned repository profile, bounded historical investigation, durable intelligence records, incremental maintenance, automatic selective recall, explicit tool access, code exploration integration, evidence inspection, and linkage with existing memory. These requirements define supported behavior when requested or enabled; none of these features is mandatory for ordinary Threadsmith.NET use. All feature-specific production implementation must reside in one new solution project, with existing-code changes confined to narrow integration touch points.

It must be useful for repositories with extensive documentation and for repositories where intent must be reconstructed from code, tests, and Git evolution. Missing history or unavailable inference must result in an explicitly limited capability rather than fabricated completeness.

### 2.2 Non-goals

- Multi-user knowledge sharing, team workflows, approval hierarchies, and organizational knowledge governance.
- Agent teams, delegated implementation orchestration, or a new agent scheduling system.
- An exhaustive changelog, full-history narration, or a replacement for source browsing and semantic code analysis.
- Replacing existing memory with intelligence records or automatically converting every discovery into memory.
- Automatically enforcing inferred architectural guidance as policy or changing repository code during analysis.
- Requiring cloud storage, a hosted knowledge service, embeddings, or a particular model provider.

### 2.3 Governing principles

**PR-01 — Hybrid responsibility.** Deterministic host code must own repository access, identity, snapshot boundaries, evidence collection, validation, reconciliation rules, persistence, and lifecycle. Bounded LLM inference must provide semantic interpretation where it adds value.

**PR-02 — Evidence before plausibility.** The system must distinguish observed facts, documented rationale, reconstructed intent, and weaker inference. A sensible explanation is not evidence that it was the original reason for a decision.

**PR-03 — Significance before volume.** Intelligence should capture knowledge that can materially improve engineering decisions. Ordinary changes must not become durable intelligence merely because they appear in Git history.

**PR-04 — Progressive disclosure.** Runtime use must progress from a small capsule, to a complete intelligence item, to source evidence. A complete profile or history report must not become routine model context.

**PR-05 — Preserve valuable history.** Supersession changes applicability; it must not erase useful failures, reversals, or the reasoning behind earlier approaches.

### 2.4 Fully optional activation

**OPT-01 — Disabled by default.** Repository Intelligence and Repository Archeology must be fully optional for every repository. Opening a repository, starting a session, or discovering existing analysis data must not implicitly start intelligence-specific scanning, model calls, storage initialization, recall, or maintenance. Normal coding, tools, memory, and code exploration must remain available without either capability.

**OPT-02 — Explicit, independent controls.** Activation must be an explicit user choice scoped to the local repository context. Persistent intelligence, on-demand Archeology, automatic recall (including intelligence enrichment of code exploration), and automatic maintenance must be independently controllable. Running a baseline must not silently enable recall or maintenance. Repository-controlled configuration or content must not grant activation authority. Enablement is necessary but not sufficient for automatic recall: request-mode and assignment eligibility under RT-10–12 and freshness admission under MT-10–12 also apply.

**OPT-03 — Archeology without onboarding.** A user must be able to request a bounded, one-off investigation without first creating a repository-wide baseline or enabling persistent intelligence. The invocation authorizes that investigation only. Nonpersistent means no canonical intelligence writes or feature-owned durable evidence cache, run store, or checkpoint; normal host session/event retention still applies. The answer and cited sources must be useful during the invocation without those feature stores. AR-10–11 define cleanup, inspection, and restart behavior; promoting findings into canonical intelligence requires persistent intelligence to be enabled.

**OPT-04 — Explicit disablement and data control.** Disabling a capability must stop its feature-specific activity, cancel its ongoing work through normal host cancellation boundaries, and prevent further retrieval/recall or maintenance as applicable. Stored intelligence may remain dormant for later reuse, with a separate explicit deletion/reset action. Disablement must not silently delete data or rewrite independently curated memory.

**OPT-05 — Inactive compatibility.** With these capabilities disabled, existing execution paths must preserve their behavior and avoid feature-specific database creation/migration, repository analysis, inference, recurring work, or intelligence context injection. Optional integration checks must remain cheap and must not eagerly initialize feature services.

**OPT-06 — Visible onboarding scope.** Before baseline work begins, the user must be able to select its repository/subsystem scope, optional historical enrichment, and configured resource limits, with the inference provider and intended work made visible. A current-state baseline must be usable without historical enrichment. Additional history can be analyzed later through explicit investigation or enabled maintenance.

### 2.5 Mandatory assembly isolation

**ISO-01 — One new production project.** All production implementation specific to Repository Intelligence and Repository Archeology must reside in a single new project within `src/Threadsmith.sln`, proposed as `Threadsmith.RepositoryIntelligence`. Both capabilities belong to that assembly. The project must follow existing solution, build, package-management, and dependency-direction rules.

**ISO-02 — Feature ownership.** The new assembly must own onboarding/profile orchestration, episode discovery, evidence-packet composition, bounded inference contracts and interpretation, intelligence records and feature-specific persistence logic, validation/reconciliation, freshness, retrieval/ranking, capsules, investigations, and maintenance behavior. Feature algorithms and lifecycle rules must not be distributed across existing projects. Existing shared services may supply repository facts and infrastructure.

**ISO-03 — Narrow host touch points.** Changes to existing production projects must be limited to integration with the new assembly: composition/registration, activation settings, minimal boundary contracts, tool registration/delegation, context and `code_explore` callbacks, memory links, and required host infrastructure connections. These touch points must delegate feature behavior to the new assembly. Solution/project references, dependency-gate updates, deployed prompt-asset registration, and required documentation are supporting integration changes. Broad refactoring or relocation of existing capabilities is outside this feature's scope.

**ISO-04 — Reuse established host infrastructure.** Assembly isolation must reuse the existing tool/model execution, repository/process access, policy, permissions, budgets, cancellation, events, logs/progress, presentation, prompt assets, and persistence infrastructure where applicable. It must not introduce competing execution, lifecycle, repository-reader, or renderer paths merely to keep feature code separate. Manual, model-driven, and internal entry points must retain normal host governance and visibility.

**ISO-05 — Dependency-safe contracts.** Integrations must preserve the existing dependency direction. `Threadsmith.Core` must not depend on the new implementation assembly; public boundaries must use host-owned contracts and DTOs, with provider SDK, Roslyn, extension, and terminal-library types remaining behind their existing boundaries. Minimal shared contracts or adapters count as integration touch points, not permission to move feature implementation into existing projects. Exact references and contract placement belong in subsequent plans.

**ISO-06 — Reviewable change boundary.** Each implementation plan must enumerate its proposed changes outside the new project and explain the integration purpose of each. Subsequent review must verify that host changes delegate through those touch points, that feature behavior remains owned by the new assembly, and that disabled operation remains compatible. Once touch points are established, ordinary feature development should proceed within the new project without repeated changes to host algorithms.

**ISO-07 — Focused verification.** Feature tests should reside in a dedicated test project under `tests/`, included in the solution. Existing architecture and integration tests may receive focused changes to verify project boundaries, normal host execution, and disabled behavior. The requirement for one production assembly does not require tests or repository-owned documentation to be placed inside that assembly.

## 3. Capability boundaries and conceptual lifecycle

| Capability | Primary question | Durable result | Main use |
|---|---|---|---|
| Repository profile | What repository state and architectural boundaries were observed? | Pinned structural snapshot, coverage, and analysis provenance | Orientation, scope selection, freshness comparison |
| Repository Archeology | What happened, why, and what does the evidence support? | With persistence enabled: investigation record, episodes, evidence, candidates, uncertainties. Otherwise: an answer and ordinary host session records under AR-10–11. | Targeted historical answers and intelligence discovery or reevaluation |
| Repository Intelligence | What should an agent understand when working here? | Versioned intelligence items, relationships, scope, and capsules | Selective recall, explicit inspection, and decision support |
| Existing memory | What durable user or working context should be remembered? | Memory governed by its existing semantics | Preferences, explicit intent, and curated recurring guidance |

Archeology is an investigative capability used during onboarding, on demand, and during maintenance. Intelligence is the maintained knowledge product consumed during coding. An investigation may conclude that no durable item is justified; a useful historical answer need not become a permanent rule.

```text
Pinned repository state + available Git/code/docs/tests
                         |
            Deterministic discovery and evidence
                         |
             Bounded analysis and interpretation
                         |
           Validated candidates and reconciliation
                         |
          Local Repository Intelligence + provenance
                         |
        Selective capsules -> full items -> evidence
```

The diagram describes responsibilities and information flow inside the new feature assembly and its host integrations, not a mandatory internal class topology. Work occurs only when explicitly requested or enabled. Historical enrichment is optional, and one-off Archeology does not require a persisted baseline.

## 4. Initial onboarding and repository profile

**ON-01 — Repository identity and context.** On explicit setup or first enablement, Threadsmith must establish a stable local repository identity, checkout or worktree context, selected branch or ref, captured HEAD commit, working-tree state, available history boundaries, and relevant analysis settings. Copies, worktrees, and branches must not silently exchange incompatible current guidance. Setup must honor the optional activation controls in OPT-01–06.

**ON-02 — Pinned analysis.** A run must capture its target commit before collecting committed code, documentation, tests, or history. All committed-state conclusions must be evaluated against that target. Historical evidence must retain its actual source revision. If HEAD advances during analysis, the run must finish against its original target and report the newer state as pending maintenance.

**ON-03 — Working-tree treatment.** The committed baseline must remain distinct from uncommitted edits. An investigation may consider a working-tree overlay when relevant, but it must label and identify that overlay sufficiently to avoid presenting mutable content as commit-backed evidence. Onboarding must not require a clean checkout or modify the user's files.

**ON-04 — Structural profile.** Deterministic discovery must capture the repository facts needed for orientation and retrieval: solution/project or module boundaries, project references, major dependencies, significant source areas, documentation and ADR locations, test organization, and relevant build/configuration metadata. Symbol-level information should be included where available through existing code services. Discovery must state exclusions and unavailable analyses.

**ON-05 — Initial intelligence acquisition.** The baseline must support current-state profiling without historical enrichment. When historical enrichment is selected, it must combine explicit decisions in documentation and commit rationale with reconstruction from code evolution, diff sequences, tests, failures, and reversals. Available streams must be reconciled without privileging documentation that has been superseded by later evidence. Omitting historical enrichment must be recorded as a coverage limitation rather than treated as onboarding failure.

**ON-06 — Bounded coverage.** Initial analysis must have an explicit, inspectable scope and resource budget. It must prioritize consequential subsystems and episodes rather than require exhaustive history analysis before becoming useful. The result must record history availability, selected ranges and areas, analyzed coverage, exclusions, and unresolved gaps. Discovery coverage and completed semantic analysis coverage must be distinguishable.

**ON-07 — Initial deliverables.** A successful onboarding run must provide:

- A pinned repository profile and run record, including limitations and coverage.
- Validated evidence and any justified intelligence items with scopes and capsules; significant episodes when historical enrichment was selected.
- Important uncertainties, conflicts, and potential areas for deeper investigation.
- A concise human-readable overview and an inspectable readiness/freshness status.
- A known maintenance baseline from which subsequent changes can be evaluated.

The overview and architecture narrative must be projections over the maintained records. They must not become a competing, opaque source of truth.

**ON-08 — Progressive availability and recovery.** Validated results must become usable without waiting for an exhaustive analysis. Interrupted or failed work must retain completed valid results and expose incomplete coverage. Onboarding must support bounded continuation and deliberate reprofiling without silently deleting prior knowledge or duplicating it.

## 5. Repository Archeology requirements

**AR-01 — Targeted investigations.** Archeology must support a concrete question or target expressed through a subsystem, path, symbol, concept, intelligence item, architectural concern, or commit range. Representative questions include: “Why does this boundary exist?”, “Was this approach tried before?”, and “Which invariant survived this migration?”

**AR-02 — Deterministic candidate discovery.** Host code must use Git structure and repository facts to identify candidate changes and related episodes. Relevant signals include changed scope, project/dependency changes, introduced or removed abstractions, renames, reversions, repeated corrective work, and associated documentation or tests. These signals nominate evidence for interpretation; they do not establish intent by themselves.

**AR-03 — Episodes rather than commit summaries.** Related changes must be representable as an architectural episode: for example, introduction, failure, corrective redesign, and stabilization. Episodes must retain their constituent evidence and historical bounds. One commit need not equal one episode, and one episode need not equal one intelligence item.

**AR-04 — Bounded evidence packets.** Each inference operation must receive a bounded question and host-collected evidence packet. Packets must identify the target snapshot, historical revisions, observed scope, evidence identifiers, selected excerpts, and material omissions. If evidence is insufficient, the analysis must return uncertainty or request a bounded expansion through the host.

**AR-05 — Bounded semantic operations.** Inference must be separable into focused tasks such as significance assessment, intent extraction, failure/reversal interpretation, reevaluation, cross-episode synthesis, and relationship classification. The system must not depend on a single model call that reads the entire repository/history and replaces the complete intelligence store.

**AR-06 — Cross-episode understanding.** The capability must support discovering persistent patterns or higher-order decisions from selected episode-derived candidates. Such synthesis must remain bounded and traceable to underlying evidence. Global graph construction or free-form synthesis must not bypass validation.

**AR-07 — Historical explanations and current applicability.** Answers must separate what existed then, what replaced it, what rationale is supported, and what still applies at the target commit. A removed mechanism must not imply that every invariant it protected was also abandoned.

**AR-08 — Evidence fidelity.** The model must reference host-supplied evidence identifiers. Unknown commits, paths, symbols, evidence identifiers, and relationship endpoints must be rejected or resolved and validated by host code before persistence. Causal claims require supporting evidence; temporal adjacency alone is insufficient.

**AR-09 — Investigation outcomes.** An investigation must return an answer or explicit inability to conclude, its supporting and conflicting evidence, coverage limits, and any proposed intelligence changes. When persistent intelligence is enabled, findings selected for retention must pass through the same reconciliation lifecycle as onboarding results. Nonpersistent investigations must remain usable under AR-10–11 without writing canonical intelligence. Inconclusive or insignificant investigations must not manufacture durable items to fill an output format.

**AR-10 — Nonpersistent retention boundary.** A nonpersistent investigation must use the normal host tool/model execution and durable session/event path, including ordinary sanitized requests, receipts, visible results, diagnostics, and artifacts only as existing host retention rules permit. It must not create or update canonical intelligence, feature-owned persistent evidence caches/indexes, durable investigation-run state, or restart checkpoints. Bounded in-memory evidence, transient checkpoints, and temporary scratch files are permitted only for the live invocation; they must be released or cleaned up on completion/cancellation using normal host lifecycle facilities. Restart cleanup must treat orphaned scratch as disposable rather than as a resumable cache. Ordinary session retention is not an opt-in to persistent intelligence, and nonpersistent mode must not introduce a nonlogged execution path or bypass normal sanitization/retention controls.

**AR-11 — Nonpersistent inspection and restart.** During a live nonpersistent invocation, bounded evidence drill-down must resolve invocation-scoped evidence identifiers. Responses must also expose source locators and pinned revisions so their meaning does not depend solely on those transient identifiers. Completion, cancellation, or host restart ends the feature-owned packet/checkpoint lifetime: there is no durable investigation-resume or full-packet inspection guarantee. After restart, ordinary session replay may show only the answer, citations, excerpts, and receipts actually retained by the host; it must not imply the original packet was restored. A new explicit source read may resolve a cited revision if it remains available, or a new investigation may be requested. Retrying a cancelled or restarted nonpersistent investigation is a new run, not checkpoint resumption.

## 6. Conceptual information requirements

The following describes information the product must preserve. It is not a storage schema or final API contract.

### 6.1 Intelligence items

**IN-01 — Common item model.** Each durable item must have a stable identity, revision, kind, title, substantive statement, rationale or lesson where supported, applicability, confidence, evidence classification, scope, concepts, supporting evidence references, capsule, and creation/evaluation provenance. Unknown information must remain unknown rather than be invented to populate a field.

**IN-02 — Intelligence kinds.** The model must express at least:

| Kind | Knowledge represented |
|---|---|
| Decision | A consequential choice among meaningful alternatives |
| Constraint | An invariant or boundary future work should preserve |
| Convention | A repository-specific intentional practice |
| Migration | A significant transition between approaches |
| Reversal | An approach deliberately abandoned or substantially undone |
| Historical failure | A failure that materially influenced later engineering |
| Persistent pattern | A boundary or practice surviving multiple changes |
| Open tension | An unresolved, evidence-backed architectural conflict or question |

Type-specific details must preserve meaningful distinctions such as original approach, replacement, failure impact, and resolution without requiring an enormous universal payload full of irrelevant fields.

**IN-03 — Independent assessment dimensions.** The system must keep these dimensions separate:

- **Applicability:** current, superseded, partially superseded, historical only, or uncertain.
- **Confidence:** strength of the conclusion, with reasons for material uncertainty.
- **Evidence classification:** documented, strongly reconstructed, or inferred.
- **Freshness:** applicability to the selected repository context and whether reevaluation is needed.
- **Coverage:** how much relevant evidence/history has actually been examined.

A documented statement can be obsolete. A high-confidence historical finding can have uncertain current applicability. A recent evaluation can still have incomplete historical coverage.

**IN-04 — Concrete scope and concepts.** Items must support repository-wide applicability, projects/modules, paths or path patterns, and symbols where supported. Concepts must provide semantic retrieval signals shared with the existing memory/tool vocabulary where appropriate. Host-derived scope candidates should constrain model refinement. Unsupported scope must remain empty rather than be guessed.

Concept normalization must be lightweight and consistent: normalize obvious casing/formatting, prefer singular canonical nouns where applicable, and avoid generic tags such as `code` or `change`. A large mandatory ontology is outside this specification.

**IN-05 — Runtime capsules.** Each recallable item must provide a short, self-contained capsule that preserves its actionable meaning and material uncertainty. One or two sentences, normally around 300 characters or less, is a preferred target rather than a fixed schema limit. Historical items must be recognizable as history. A capsule must identify the item and be traceable to the item revision that produced it; substantive item changes must invalidate or regenerate it.

### 6.2 Evidence, episodes, relationships, and uncertainty

**IN-06 — Normalized evidence.** Evidence must be independently addressable and reusable by multiple items, episodes, relationships, and uncertainties. It must identify its type, repository, source locator, relevant revision or explicitly labeled working-tree overlay, and enough context to retrieve or explain it. Commit/diff evidence, files, documents/ADRs, tests, and configuration must be supported. A test's existence or assertion must not be represented as proof that the test ran successfully.

**IN-07 — First-class episodes and relationships.** Episodes must link historical bounds, scope, evidence, and derived intelligence. Relationships must carry typed direction, endpoints, explanation, confidence, and supporting evidence where needed. The model must distinguish meaningful relations such as motivation, preservation, supersession, reversal, correction, implementation, and reinforcement from a generic association. Relationships must be reevaluable when their supporting items or evidence change.

**IN-08 — Uncertainty and conflict.** Important unknowns and contradictory evidence must be inspectable, linked to the affected records, and phrased so further investigation or a user clarification can resolve them. The system must not collapse competing explanations into a false consensus or treat absence of evidence as evidence that no historical decision existed.

**IN-09 — Canonical records and projections.** Items, evidence, relationships, episodes, run provenance, and uncertainties must form the durable knowledge model. Reports, executive summaries, architecture narratives, and high-value rankings must be reproducible projections. Stored capsules remain revision-linked runtime projections of individual items.

## 7. Validation, reconciliation, and incremental maintenance

**MT-01 — Deterministic validation and durable identity.** Host code must validate structure, references, repository context, evidence locators, and allowed lifecycle changes before accepting generated output. It must own identifiers and durable state transitions. Semantic duplicate or merge suggestions may use inference, but acceptance must follow explicit reconciliation rules.

**MT-02 — Explicit reconciliation outcomes.** New candidates must be recorded as added, corroborating, revised, merged, superseding, conflicting, rejected, or deferred as appropriate. Equivalent findings should consolidate under stable identities. Existing valuable intelligence must not disappear because a later inference omits it. Merges and replacements must preserve provenance and resolve older references.

**MT-03 — Pinned incremental ranges.** Maintenance must compare a known baseline commit A with a newly captured target B using Git ancestry and actual changes. Commits arriving after B belong to a later pass. Timestamps alone must not determine update coverage. Run-level coverage checkpoints and per-item evaluation anchors must remain distinguishable. Commit-range maintenance does not replace admission-time checks of uncommitted working-tree changes under MT-10–12.

**MT-04 — Impact-based reevaluation.** Changed evidence, overlapping scope, affected symbols/projects, and relevant dependencies or relationships must identify existing records potentially needing reevaluation. New changes must also be examined for new episodes and intelligence. Transitive architectural impact must be considered where known; matching exact file paths alone is insufficient.

**MT-05 — Honest freshness.** Each item must retain a `lastEvaluatedCommit` or equivalent evaluation anchor and an inspectable freshness assessment for the selected context. Freshness must distinguish evaluated, unaffected by checked changes, pending reevaluation, incompatible context, and unavailable/uncertain evidence. A run completing at B must not imply every item received semantic reevaluation at B. Deterministic carry-forward decisions must retain their method and checked range.

**MT-06 — Changed history and branch context.** Branch switches, divergent history, rebases, resets, renames, deleted sources, and shallow or missing history must not silently invalidate the maintenance model. When A-to-B incremental reasoning is unsafe, the system must expose the gap. Bounded comparison or reprofiling may repair it only through explicitly requested or enabled maintenance; automatic admission must use the withholding rule in MT-11 without waiting for repair. Historical evidence that is no longer locally available must be marked unavailable without erasing the associated conclusion or pretending it was reverified.

**MT-07 — Controlled publication and recovery.** Only validated, internally coherent revisions may become recallable. Interrupted analysis must not leave dangling references, mismatched capsules, or a false completed baseline. Failures must preserve the last valid knowledge and expose pending work. Retries or repeated runs must not create duplicate records or lose significant historical lessons.

**MT-08 — User control and correction.** The user must be able to inspect coverage, request a targeted refresh or reprofile, correct or dispute a conclusion, and suppress unsuitable automatic recall. Corrections must retain attribution and scope. Maintenance must preserve explicit user corrections or surface a conflict rather than silently overwrite them.

**MT-09 — Maintenance triggers.** When automatic maintenance is enabled, detected repository changes may schedule bounded analysis and reevaluation. The user must also have an explicit refresh path without enabling automatic maintenance. Automatic maintenance must respect configured resource limits and may defer expensive inference while marking affected knowledge accordingly. Freshness detection and context admission under MT-10–12 are required for otherwise eligible intelligence use even when automatic maintenance is off; they must not silently enable repair work. Disabled repositories must not acquire maintenance activity, and this specification does not require an always-running service or scheduled background job.

**MT-10 — Freshness admission independent of maintenance.** Before intelligence is admitted to the next model request, the host must apply queued repository/semantic invalidations through the existing before-assembly context boundary. Required deterministic checks must cover the selected repository/worktree identity, branch/ref and HEAD changes, and relevant uncommitted additions, edits, deletions, and moves, including externally made changes, applied mutations, and rollback. These checks apply with recall on and maintenance off, and to previously selected capsules as well as new candidates. Otherwise eligible `code_explore` enrichment and explicit intelligence reads must receive the corresponding current freshness assessment. Child evidence is assessed against its assigned immutable baseline under RT-11, not silently against a different live checkout.

**MT-11 — Withhold automatically, qualify explicitly.** A capsule whose relevant scope/evidence has changed, whose context is incompatible, or whose freshness cannot be established must be withheld from automatic recall and automatic `code_explore` enrichment until a deterministic unaffected assessment or authorized reevaluation restores eligibility. Unaffected capsules may remain eligible. Explicit search/inspection may return affected items only with their evaluated snapshot, stale/unverified reason, and applicability clearly identified. Admission checks must not launch inference, onboarding, or maintenance; they must not advance `lastEvaluatedCommit` or claim semantic reevaluation. Freshness assessment is context-local admission state and may remain transient. Historical conclusions remain inspectable, and a stale-current warning must not erase their historical evidence.

**MT-12 — Invalidation completeness and proportional work.** Checks must reuse existing repository-change notifications, source/dependency identities, and context invalidation facilities, targeting candidate or already admitted items and their declared dependencies rather than repeatedly profiling the repository. Missing dependency coverage, lost notifications, watcher gaps, restart, or an unverifiable checkout identity must invalidate cached freshness assumptions and cause bounded rechecking or conservative withholding. Cached recall selections and frozen/request context reuse must not preserve affected capsules as eligible guidance into the next request; normal context invalidation/reassembly must account for them. Archived messages and historical evidence need not be erased, but must not be used to bypass current admission. With these capabilities unused/disabled, no feature-specific checking is required beyond the cheap inactive gate in OPT-05.

## 8. How intelligence is used during coding

### 8.1 Automatic selective recall

**RT-01 — Relevant task context.** When automatic recall is enabled and the request is eligible under RT-10–12, it must use the active task and available repository context, including concepts, projects, files, symbols, and significant tool activity. It should reconsider selection when meaningful task scope changes. Semantic similarity may supplement these signals but must not be the sole required retrieval mechanism. Request eligibility must be established before automatic intelligence lookup or enrichment.

**RT-02 — Small, bounded selection.** Automatic recall must select a small set of useful capsules within a configured context budget. Zero capsules is a valid result; one to three relevant capsules is the preferred normal operating range. Selection must consider task relevance, scope overlap, current applicability, confidence, freshness, redundancy, and likely engineering value. Repository-wide importance alone must not cause unrelated guidance to appear repeatedly.

**RT-03 — Safe interpretation of recalled knowledge.** Recall must identify each item and communicate material applicability and uncertainty. Historical failures and reversals may be recalled when otherwise eligible and relevant, but must not be phrased as current commands. Stale or freshness-unverified capsules must be withheld automatically under MT-11; confidence qualifications do not substitute for freshness admission. Explicit inspection may show them with warnings. Intelligence informs reasoning; it does not grant execution authority or override user instructions and host policy.

**RT-04 — Continuity and transparency.** Recall must avoid injecting the same unchanged capsule repeatedly into an active context and must account for already available memory/intelligence content. The user must be able to inspect which items were selected and why, including whether none were selected. Omission reasons must distinguish disabled recall, ineligible request/child context, freshness failure, relevance, and budget limits. A profile's existence must not force a full-store scan or LLM call on every tool invocation. Context-mode and freshness changes must invalidate cached selection/admission assumptions before reuse.

### 8.2 Explicit `repository_intelligence` access

**RT-05 — Explicit capability surface.** Threadsmith must provide an optional `repository_intelligence` tool surface for the agent and corresponding inspectable user access. Availability and work must respect OPT-01–06, including explicitly requested one-off investigations. Its conceptual operations must cover:

| Operation | Required behavior |
|---|---|
| Status/profile | Explain analyzed snapshot, coverage, freshness, pending work, and limitations |
| Search/query | Find relevant items by question, concepts, concrete scope, kinds, or applicability |
| Inspect | Load a complete item, its revision, rationale, scope, assessments, and relationships |
| Evidence | Resolve evidence identifiers to bounded source material at the cited revision |
| Investigate | Request targeted Archeology with an explicit question/scope and bounded work |
| Refresh | Request incremental maintenance or deliberate reprofiling within configured limits |

Exact operation names and arguments are deferred. Read operations must not silently launch expensive investigations. A query with insufficient coverage must distinguish “no match found” from “this area has not been analyzed.” Existing cached findings should be reused when their context and coverage are suitable.

**RT-06 — Evidence drill-down.** For retained intelligence, the agent must be able to move from capsule to complete item to the supporting commit, diff, document, code, or test excerpt without rediscovering the source, including after restart subject to source availability. Responses must identify the source revision, separate supporting from conflicting evidence, and make unavailable references explicit. Full output must remain bounded or support further narrowing. Nonpersistent investigation identifiers and restart inspection follow AR-11; ordinary session replay is not a durable feature evidence store.

### 8.3 `code_explore` integration

**RT-07 — Exploration context.** When intelligence-assisted exploration is enabled and the request is eligible, `code_explore` must be able to associate explored projects, paths, and symbols with relevant intelligence. It should return compact item references/capsules and freshness information alongside code findings, with access to deeper inspection through `repository_intelligence`. Adding stored intelligence automatically to a tool result counts as automatic recall, even when `code_explore` itself was explicitly invoked: the same MT-10–12 freshness and RT-10–12 context gates apply. Stateless and delegated calls must not receive this automatic enrichment. The touch point must delegate feature selection and enrichment behavior to the new assembly.

Code facts and historical interpretation must remain distinguishable. Existing semantic exploration must remain useful when intelligence is absent, stale, disabled, or incomplete. Ordinary exploration must not implicitly run a full archeology pass; it may identify an evidence gap and offer or request a targeted investigation through the explicit capability.

### 8.4 Memory linkage with separate semantics

**RT-08 — Linked, distinct knowledge.** Repository Intelligence and memory must remain separate record types with separate lifecycle and retrieval semantics. They may share concepts, repository scope conventions, and cross-references. A memory linked to intelligence must retain the source item/revision so its origin and later changes can be inspected.

**RT-09 — Curated promotion and change awareness.** Frequently useful current guidance may be proposed as a repository-memory candidate under existing memory rules. Discovery must not automatically copy the full intelligence store or historical reports into memory. If a source item changes, is disputed, or becomes superseded, linked memory must be identifiable for reconsideration; explicit user intent must not be silently rewritten as model-derived fact. Combined recall must avoid duplicate or contradictory injections.

### 8.5 Representative usage

The examples below describe expected interactions, not assertions about Threadsmith.NET's actual architecture.

| Coding situation | Expected use |
|---|---|
| Add a model provider | Recall a relevant provider-boundary capsule; inspect its rationale if the proposed integration challenges that boundary |
| Parallelize a subsystem's startup | Retrieve scoped lifecycle/concurrency constraints and prior failure lessons; inspect cited tests and corrective changes |
| Restore an older workflow | Surface a related reversal, distinguish the abandoned mechanism from surviving invariants, and investigate unresolved applicability |
| Explore an unfamiliar project | Use the profile for orientation and `code_explore` for code facts plus a few relevant intelligence references |
| Ask why an undocumented abstraction exists | Run targeted Archeology over its introduction and evolution; return a supported answer or an explicit uncertainty |
| Change code cited by existing guidance | Withhold affected capsules at the next admission boundary even with maintenance off; reevaluate and update them only through requested/enabled maintenance |

### 8.6 Request modes and delegated contexts

**RT-10 — Request-mode eligibility.** Automatic intelligence recall must follow the existing non-stateless parent context boundary: it is eligible in top-level Conversation-aware and Governed-memory-only requests only when explicitly enabled and freshness-admissible. It must be excluded from Stateless requests and all delegated child requests. Repository/session enablement must not override those exclusions or trigger automatic lookup in an ineligible context. `code_explore` enrichment and memory-link traversal must not provide indirect paths around these gates. Current user-supplied content and authorized explicit tool results remain governed current-request inputs under RT-12.

| Context | Automatic capsules and `code_explore` enrichment | Other permitted intelligence use |
|---|---|---|
| Top-level Conversation-aware | Eligible only with opt-in, freshness admission, and normal budgets | Authorized explicit operations |
| Top-level Governed-memory-only | Same eligibility as Conversation-aware; no raw history is added | Authorized explicit operations |
| Top-level Stateless | Excluded; no automatic intelligence lookup | Current-request user input and authorized explicit tool results |
| Delegated child, regardless of parent mode | Excluded; no independent automatic retrieval or enrichment | Bounded parent-admitted evidence and assigned inspection under RT-11 |

**RT-11 — Delegated intelligence boundary.** A child may receive relevant intelligence only as role-eligible, parent-admitted governed evidence in the existing bounded `AgentContextSnapshot`, with item/revision, source provenance, and applicability identified. The admitted intelligence must be freshness-admissible against the assignment's immutable baseline. This is ordinary governed evidence selection, not a child intelligence-store search. If the assignment permits `repository_intelligence` tool access, it must be restricted to inspecting those admitted items and their cited evidence at that baseline; child access must not discover additional items, run new Archeology, refresh, or enable persistence. Parent/sibling transcripts and mutable sibling results remain excluded, budgets/tool intersections still apply, and findings must satisfy existing citation and durable-join rules. Later parent admission uses the existing invalidation boundary; a child result cannot silently refresh guidance for the parent's live checkout. This requirement integrates with existing delegation and does not add feature-owned team orchestration.

**RT-12 — Explicit access in isolated contexts.** A top-level Stateless request may use an available, authorized `repository_intelligence` operation through normal tool execution when explicitly invoked for that request. Its bounded result enters as current-run tool evidence, with normal freshness labels, retention mode, and budgets; it does not enable automatic recall, import prior conversation memory, or enable maintenance. Calling `code_explore` alone is not an explicit request for stored intelligence. Delegated explicit access remains limited by RT-11, even when the parent's repository settings allow broader operations.

## 9. Local-first operation and quality requirements

**NQ-01 — Local ownership.** When persistent intelligence is enabled, canonical intelligence and its durable evidence metadata, run state, and retrieval state must persist locally for one user and remain usable without a hosted knowledge service. Repository isolation, deletion/reset, and human-readable export must be supported. Export must preserve snapshot and provenance information sufficiently to interpret the results. Nonpersistent investigations are explicitly exempt from feature-owned durability and use AR-10–11; existing host session persistence still applies.

**NQ-02 — Inference availability.** Analysis may use the user's configured inference provider under existing data-access and transmission settings. Local-first does not require every model to run locally. When inference is unavailable, existing intelligence and deterministic profile/status/evidence functions must remain usable; newly required inference must be reported as pending or unavailable.

**NQ-03 — Bounded resources and cancellation.** Onboarding, investigations, maintenance, and retrieval must have controlled work and output budgets. Expensive work must expose progress and support cancellation without damaging valid knowledge. With persistent intelligence enabled, onboarding, investigations, and maintenance must retain validated checkpoints for completed bounded work and support explicit resumption after interruption/restart, subject to snapshot/source availability. Nonpersistent investigations use transient state only, with cleanup and new-run retry rather than durable resumption under AR-10–11. Large repositories and large histories must be manageable through prioritization and partial coverage.

**NQ-04 — Existing trust boundaries.** Repository content, historical documents, commit messages, and inferred capsules must remain data, not executable instructions. Collection must use existing safe repository-access boundaries, avoid unintended execution of repository-controlled hooks or build logic, and preserve existing secret/access controls. Archeology must not modify code, run tests, or expand execution authority merely because it is analyzing evidence.

**NQ-05 — Inspectable provenance and diagnostics.** Operation results and normal host diagnostics must expose the repository snapshot, analysis bounds, operation/model provenance where applicable, completion state, coverage, resource use, and rejection/defer reasons needed to understand results. Persistent runs must retain this provenance in their feature run records. Nonpersistent work must use the ordinary host result/event/diagnostic retention path under AR-10–11 without creating a separate durable run store. Diagnostic detail must not routinely consume the coding agent's context budget.

**NQ-06 — Stability and evaluability.** Fixed deterministic inputs must produce stable repository facts and identities. Semantic inference may vary, but reconciliation must prevent unexplained loss of retained knowledge. Extraction quality and retrieval usefulness must be evaluated separately; a plausible report alone is insufficient evidence of success.

## 10. Acceptance outcomes

Later implementation plans must define executable checks or reviewable evidence for their claimed requirement subset. The following scenarios form the parent acceptance set; they do not prescribe a test framework or release sequence.

| ID | Scenario and required observable result | Main requirements |
|---|---|---|
| AC-01 | A previously unseen repository is onboarded. A pinned profile, validated intelligence, evidence, declared coverage/limitations, and a maintenance baseline are available. | ON-01–08 |
| AC-02 | HEAD advances from A to B during onboarding. The published result remains internally consistent at A and exposes B as pending work. Dirty files are labeled separately. | ON-02–03, MT-03 |
| AC-03 | A repository has few ADRs. Bounded analysis reconstructs a supported historical decision from code/history/tests or explicitly reports insufficient evidence; it does not invent rationale. | AR-02–09, IN-03 |
| AC-04 | An investigation finds a failed approach and its replacement. The result preserves the failure, replacement, and surviving invariants with resolvable evidence and qualified causal claims. | AR-03, AR-07–08, IN-06–08 |
| AC-05 | The model supplies an unknown evidence ID or unsupported locator. Host validation prevents publication of the invalid candidate and preserves prior valid state. | AR-08, MT-01, MT-07 |
| AC-06 | The same evidence is analyzed again. Stable items are corroborated or explicitly revised/merged; significant lessons do not vanish due to model omission. | MT-01–02, MT-07, NQ-06 |
| AC-07 | A commit changes evidence for one subsystem. Affected records and new episodes are considered within a pinned range; unrelated records are carried forward with honest freshness provenance. | MT-03–05 |
| AC-08 | The user switches to a divergent branch or history becomes unavailable. Affected/unverifiable capsules are withheld automatically; explicit inspection labels the evaluated context and gaps without claiming freshness. | ON-01, MT-06, MT-10–12 |
| AC-09 | A representative coding task has a predefined expected set of relevant items. Automatic recall selects useful bounded capsules; an unrelated task can return none. Selection and omissions can be inspected. | RT-01–04, NQ-06 |
| AC-10 | An agent follows a capsule to a complete item and then to a cited source revision. Evidence resolves or is explicitly unavailable, and material conflicts remain visible. | RT-05–06, IN-06–08 |
| AC-11 | In an eligible top-level context with recall enabled, `code_explore` examines an area with fresh relevant intelligence. Compact guidance appears alongside distinguishable code facts; exploration also works when intelligence is absent. | RT-07, RT-10 |
| AC-12 | Linked intelligence is superseded or disputed. Its memory links are discoverable for reconsideration, and combined recall avoids presenting contradictory statements as settled guidance. | RT-08–09, MT-08 |
| AC-13 | Persistent analysis is cancelled or inference fails. Previously valid local results remain usable, incomplete work is visible, and checkpoint resumption after restart does not duplicate or corrupt knowledge. | ON-08, MT-07, NQ-01–03 |
| AC-14 | A user corrects a conclusion or suppresses its recall. The correction is attributed and persists through refresh, or a new evidence conflict is surfaced explicitly. | MT-08, RT-03–04 |
| AC-15 | A repository is used with both capabilities disabled. Existing coding, tools, memory, and code exploration behave normally, without feature-specific analysis, inference, storage initialization, maintenance, or recall. | OPT-01–02, OPT-05 |
| AC-16 | A user requests a current-state baseline with history, automatic recall, and maintenance off. A usable pinned baseline is produced, its historical coverage limits are visible, and automation remains off. | OPT-02, OPT-06, ON-05–07 |
| AC-17 | A user requests one targeted Archeology answer with no baseline or persistent intelligence enabled. The bounded answer includes evidence/limits and follows normal host session retention without enabling feature persistence or recurring work. | OPT-03, AR-01, AR-09–11 |
| AC-18 | A feature increment is reviewed. All feature-specific production behavior resides in one new solution project; existing-code changes are enumerated integration touch points, and entry points reuse normal host governance. | ISO-01–07 |
| AC-19 | A user disables active analysis or automation. Relevant work is cancelled through host boundaries, further activity stops, and existing data remains dormant unless deletion is explicitly requested. | OPT-04–05, NQ-03 |
| AC-20 | Recall is on and maintenance off when a relevant file is edited, added, moved, deleted, or rolled back without a new commit. Before the next model request/enriched result, affected capsules are withheld, cached selections are invalidated, unaffected items may remain, and explicit inspection labels the stale state without inference or maintenance. | MT-10–12, RT-03–04, RT-07 |
| AC-21 | With maintenance off, HEAD/branch changes or a watcher gap/restart invalidates freshness assumptions. Bounded checks establish compatibility or withhold affected/unverifiable capsules before cached context reuse; no reprofile is launched. | MT-06, MT-09–12 |
| AC-22 | A nonpersistent investigation completes or is cancelled, then the host restarts. Normal session events/results follow existing retention; no feature cache/checkpoint/store is created, scratch state is released, and packet inspection/resumption is unavailable rather than falsely restored. Retained citations may support a new explicit source read or investigation. | AR-10–11, NQ-01, NQ-03, NQ-05 |
| AC-23 | With repository recall enabled, otherwise equivalent top-level requests use Conversation-aware, Governed-memory-only, and Stateless modes. The first two may receive fresh bounded capsules; Stateless receives none, including no `code_explore` enrichment or memory-link backfill, while authorized explicit intelligence tool results remain current-run evidence. | RT-10, RT-12 |
| AC-24 | A delegated child runs while parent recall is enabled. It receives only eligible parent-admitted intelligence within its frozen baseline/budget; no automatic store lookup or `code_explore` enrichment occurs. Assigned inspection resolves only admitted items/evidence and rejects broader search/investigate/refresh operations; normal citation/join rules remain enforced. | RT-07, RT-10–12 |
| AC-25 | Persistent analysis resumes after restart with its pinned checkpoint and retained evidence available. Item/evidence drill-down works with provenance; unavailable sources are labeled, contrasting with the nonpersistent lifetime in AC-22. | RT-06, NQ-01, NQ-03, AR-11 |

Evaluation should include predefined coding tasks with expected relevant intelligence before measuring retrieval, plus irrelevant-task cases. Useful measures include capsule relevance, missed consequential guidance, redundant context, evidence resolution, unsupported claims, and maintenance impact accuracy. Numerical thresholds belong in the applicable implementation/evaluation plans.

## 11. Boundaries for subsequent implementation plans

Every incremental plan must identify the requirement IDs it satisfies, the user-visible behavior it enables, its acceptance evidence, and the remaining coverage or capability limitations. It must enumerate existing-code touch points under ISO-06 and preserve fully optional operation under OPT-01–06. Plans must apply the independent freshness admission, nonpersistent retention, and request/child-context decisions in MT-10–12, AR-10–11, and RT-10–12; these are product requirements, not deferred callback or storage choices. Plans must preserve the distinction between repository facts, inference, canonical intelligence, and runtime projections even when delivering a narrow initial slice.

The following decisions are intentionally deferred:

- Physical persistence design, indexing, serialization, and schema migration mechanics.
- Exact DTOs, tool arguments, item-kind representations, and relationship vocabulary.
- Episode selection/clustering heuristics, evidence-packet sizing, model choice, and prompts.
- Retrieval ranking, concept matching, optional embeddings, and configured context budgets.
- Maintenance trigger placement, caching, retention, and background execution strategy.
- UI presentation, default coverage/resource limits, and measured performance targets.

These decisions may refine how requirements are achieved. They must not remove fully optional activation, the single new production project and narrow integration boundary, pinned snapshots, evidence provenance, bounded inference, durable reconciliation, honest freshness, progressive disclosure, or the single-user/local-first scope without an explicit revision to this parent specification.

## 12. Implementation Notes
