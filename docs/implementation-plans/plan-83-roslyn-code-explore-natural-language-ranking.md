# Implementation Plan 83: Roslyn Code Explore Natural-Language Ranking

**Status:** Active. Production implementation, focused automated coverage, user/operator documentation, and a headless MTP-250 smoke pass are in place; repeated deterministic evaluation, interactive MTP-250 evidence, Scenario AO review, and broader gates remain before completion.

**Delivery track:** Milestone 28 — deterministic discovery and source allocation
**Strategy source:** Shared Context §A.1, §A.2, §A.5, §C, and §G; Milestone 28; Scenario AO
**Prerequisite plans:** plans 81–82; Plan 82 acceptance and MTP-249 must pass before implementation begins

## 1. Objective

Allow an ordinary natural-language C# architecture or behavior question to use `code_explore` without prior stable symbol IDs. Resolve likely compiler-known anchors through deterministic lexical and structural evidence, connect them using Plan 82, and allocate source so the returned result is sufficient enough to stop repetitive search/read loops.

This plan is independently user-testable. Plan 84 must not begin until deterministic retrieval, ranking, source-allocation tests and MTP-250 pass on fixed questions across the maintained small, multi-project, and large-repository fixtures.

## 2. Architectural Context

Plans 81–82 establish exact anchors, current source, multi-anchor paths, dispatch, impact, confidence, and completeness. This plan adds a bounded discovery layer before those operations. Roslyn remains the authority: natural-language terms select among compiler-known declarations, qualified identities, semantic relationships, and repository-confined paths; they do not become executable queries or provider-owned embeddings.

The primary optimization target is fewer dependent model rounds with equal or better correctness. A large but irrelevant result can reduce call count while making every later turn more expensive, so retrieval must be evaluated by sufficiency, recall misses, allocation usefulness, residual model-visible content, latency, and final-answer quality together.

The local repository at `C:\source\repos\codegraph` is available only as a functional reference for the observable behavior of natural-language-to-code exploration and task-sufficient source allocation. Threadsmith is not copying, porting, reverse engineering, depending on, or pursuing compatibility with that codebase. Its implementation, source, algorithms, constants, schemas, ranking weights, prompts, tests, internal names, and repository-specific heuristics are neither normative nor reusable.

## 3. Scope

- Build a bounded per-workspace-generation catalog of compiler-known C# declaration names, qualified names, containing types/namespaces, kinds, projects/TFMs, source paths, and identifier segments.
- Parse a bounded natural-language query into exact identifiers, qualified names, path-like spans, and ordinary normalized terms without executing query text.
- Resolve exact and distinctive identifiers first; support case-insensitive, CamelCase, PascalCase, snake_case, and qualified-name segment matching.
- Pin explicit symbols and paths from Plans 81–82 ahead of inferred candidates.
- Rank candidate symbols/files using deterministic host-owned evidence: exactness, qualification, containing-type corroboration, distinct term coverage, co-location, semantic graph connectivity, named/spine membership, symbol kind, project/source classification, and explicit user focus.
- Penalize incidental isolated common-word matches, generated/low-value material, and unrelated test helpers without hard-excluding generated or test code when the query targets them.
- Return compact selection reasons, unresolved query terms, ambiguity groups, candidate counts, and deterministic order.
- Allocate a model/profile-aware bounded source budget across named anchors, flow spine, dispatch branches, and supporting files, guaranteeing a minimum useful section or returning a pointer instead of a fragment.
- Keep larger-repository budgets bounded by the selected model's effective request/output reserve and the canonical tool-result ceiling, not repository size alone.
- Project concise completeness and exact follow-up anchors for relevant files/symbols that did not fit.
- Make `code_explore` the described primary tool for C# survey/flow questions while retaining granular tools as secondary exact follow-ups; do not initially remove them from model inventory.

## 4. Non-Scope

- Embeddings, vector databases, provider-generated retrieval plans, arbitrary natural-language-to-code generation, or an external indexing service.
- A multi-language semantic graph or fuzzy search over every repository text file.
- Repository-specific ranking constants copied from another product or benchmark overfitting without Threadsmith evidence.
- Cross-call source deduplication and associated non-C# artifacts.
- Autonomous stopping, arbitrary exploration-round cutoffs, host rewriting of tool calls, or a generic batch tool.
- Changes to mutation, plan approval, build/test authority, or semantic confidence rules.

## 5. Current State

`find_symbol` accepts one declaration-name query. Text search handles exact repository content but is prohibited as a substitute for applicable semantic tools. Plans 81–82 accept exact anchors and compose source/flow, but the model must already know those anchors.

Threadsmith now has a deterministic host-owned natural-language declaration catalog and structural relevance allocator for semantic source results. Remaining acceptance work is evaluation and closure: repeated fixed-task comparison, interactive MTP-250 evidence, Scenario AO review, and broader regression gates.

## 6. Proposed Design

### 6.1 Generation-scoped declaration catalog

Create a bounded, immutable, lazily built catalog keyed by workspace generation. Store only host-owned normalized entries or ephemeral Roslyn-backed lookup state within `Threadsmith.DotNet`; never persist Roslyn objects. Invalidation discards or rebuilds the affected generation. Catalog construction is cancellable, measured, and covered by the existing non-cooperative abandon-and-discard backstop where necessary.

### 6.2 Query interpretation

Classify exact identifiers, qualified/container-qualified names, stable IDs, and path-like spans before ordinary terms. Split identifier humps and separators conservatively. Remove only a host-owned closed stop-word set; preserve domain terms and report ignored/unresolved terms in inspection data. Query interpretation remains deterministic for fixed text and configuration.

### 6.3 Structural candidate ranking

Use explicit ordered evidence tiers rather than opaque scores alone:

1. user-pinned paths/stable IDs and exact qualified names;
2. exact distinctive identifiers and containing-type corroboration;
3. multiple independent query-term matches in a declaration/path/project;
4. compiler-known connectivity to higher-tier candidates and selected flow paths;
5. single-term lexical candidates and peripheral semantic neighbors.

Within tiers, use documented deterministic factors and stable identity/location tie-breaking. Return a compact reason set for every selected anchor/file. Generated, test, declaration-only, and peripheral files receive contextual allocation treatment, not blanket exclusion.

### 6.4 Source allocation

Compute one total result envelope from host/model limits. Reserve meta/coverage space first, then allocate source to pinned/named anchors, flow spine, material dispatch branches, and supporting evidence. A selected file receives either a useful declaration/call-site section above a minimum threshold or a pointer with exact follow-up anchors. Never consume budget with an empty fence or misleading sliver.

### 6.5 Evaluation feedback

Add deterministic transcript evaluation for:

- whether the next action rereads a returned file;
- whether it reads/searches a file that exploration missed;
- whether it explores again or answers;
- share of returned source associated with final cited files/evidence;
- residual exploration bytes/tokens in later rounds;
- total rounds, calls, latency, cumulative input, and answer correctness.

These metrics guide changes but do not replace correctness review or product telemetry privacy rules.

## 7. Public Contracts

Expected additive host-owned contracts include:

- query interpretation details in `CodeExploreRequest`/inspection, with bounded exact, path, and ordinary terms;
- `CodeExploreSelectionReason` as a closed enum or flags for pinned, exact, qualified, containing-type, multi-term, co-located, graph-connected, flow-spine, implementation, caller, project/test, and peripheral evidence;
- `CodeExploreCandidateSummary` — selected/alternative identity, reason set, confidence, and rank tier;
- `CodeExploreAllocationSummary` — total/reserved/source characters, per-file allowance/spend, omission reason, and useful-section classification;
- coverage additions for unresolved terms, candidate truncation, catalog completeness, and model-result budget source.

Do not expose raw Roslyn symbols, internal score formulas, provider state, hidden reasoning, or unbounded candidate lists.

## 8. Project/File Changes

Expected areas:

- `Threadsmith.Core` — additive selection reason, candidate, allocation, and coverage DTOs.
- `Threadsmith.DotNet` — generation-scoped declaration catalog, deterministic term resolution, structural ranking, and allocation inputs.
- `Threadsmith.Tools` — query/limit validation, canonical schema descriptions, result bounds, and primary/secondary usage guidance.
- `Threadsmith.Context` — concise canonical exploration guidance and result/evidence admission where generic policy is insufficient.
- `Threadsmith.Execution`/Models/Telemetry — request-budget input and evaluation/inspection integration without provider leakage.
- Focused semantic/tool/context/provider tests, deterministic evaluation fixtures, docs, Scenario AO, and MTP-250.

## 9. Ordered Tasks

1. Verify Plan 82 acceptance evidence and MTP-249; re-read applicable DOX and C# guardrails.
2. Freeze a fixed evaluation corpus/questions, including the prior long FUSION-style audit, and capture granular/Plans 81–82 baselines.
3. Define catalog lifecycle, query token classes, selection tiers/reasons, source allocation invariants, budgets, and completeness semantics.
4. Implement bounded generation-scoped declaration catalog construction and invalidation.
5. Implement deterministic exact/segmented/qualified/path/ordinary-term candidate discovery.
6. Implement structural candidate/file ranking and stable tie-breaking over Plan 82 graph evidence.
7. Implement model-aware source allocation, useful-section minimums, pointer fallbacks, and compact diagnostics.
8. Update canonical tool descriptions/context guidance without hiding granular tools.
9. Add collision, overload, co-location, connected/peripheral, generated/test, large-repository, pressure, partial-confidence, cancellation, and determinism fixtures.
10. Run focused tests, architecture/provider/context/tool tests, solution build, formatting, planning-governance checks, and repeated evaluation.
11. Run MTP-250 interactively and headlessly; record checkpoint evidence before changing status.
12. Complete docs/DOX closeout. Begin Plan 84 only after this plan's acceptance and user-testable gate pass.

## 10. Testing

Focused automated coverage now verifies natural-language candidate ranking/source allocation, allowed-path-only ranking statistics, omission of unresolvable local functions from the natural-language catalog, selected-model metadata/result bounding, and headless fail-closed behavior when semantic readiness remains below `PartialCompilation`. Existing Plan 81/82 coverage continues to verify exact anchors, source identity, path policy, continuations, flow, dispatch, impact, generated content, semantic-first descriptions, and granular fallback tools.

Remaining acceptance coverage must still include broader exact-before-fuzzy priority, identifier segmentation, qualified/container resolution, path pinning, stop-word boundaries, generated/test focus exceptions, deterministic repeated ranking, stable tie-breaking, catalog invalidation, cancellation/timeouts, schema/provider parity, prompt/context guidance, redaction, unchanged granular tools, Scenario AO, and full MTP-250 interactive/headless evidence.

Repeated fixed-task evaluation must compare round count, calls, cumulative input, result bytes, latency, next-action sufficiency, recall misses, rereads of returned files, allocation usefulness, residual context, and independently reviewed answer correctness.

The user-testable checkpoint is [MTP-250](manual-test-plan.md#mtp-250--natural-language-semantic-discovery-and-source-allocation). It blocks Plan 84.

## 11. Security/Permissions

Natural-language input is inert bounded data. It cannot become source, regex, SQL, scripts, reflection, analyzer/plugin loading, process arguments, network requests, or repository authority. Catalog and ranking operate only over the already-authorized semantic workspace and confined host-owned metadata. Source/path output retains Plans 81–82 policy, sensitivity, and provenance. Repository configuration may narrow eligible paths/limits but cannot inject executable ranking logic or widen trust.

## 12. Observability

Record query length/class counts, catalog generation/size/build/cache status, candidate/selected/omitted counts by tier/reason, graph connectivity statistics, allocation/residual characters, completeness flags, model-budget source, duration, cancellation, and sanitized outcome. Evaluation tooling may inspect explicit opt-in local transcripts; product telemetry must not emit query terms, symbol names, paths, source, prompts, hidden reasoning, or provider payloads.

## 13. Migration/Compatibility

Natural-language discovery is additive to exact anchors; explicit stable IDs and paths continue to dominate. Existing tool preferences and granular contracts remain valid. Catalogs are ephemeral per workspace generation unless a later plan justifies durable host-owned index artifacts. Configuration defaults are host-owned and versioned; unknown future ranking/selection schema versions fail closed.

## 14. Acceptance Criteria

- Ordinary prose C# questions resolve useful anchors without prior `find_symbol` calls and explain their selection.
- Fixed query/generation/configuration inputs produce deterministic anchor and file ordering.
- Structural and multi-term evidence outrank isolated common-word collisions while explicit generated/test focus remains possible.
- Source allocation gives named/spine files useful bodies or honest pointers under the effective model/tool budget.
- Results expose unresolved terms, ambiguity, coverage, omissions, and continuation anchors without overstating completeness.
- Granular tools remain supported and no embeddings, external retrieval service, or provider-owned ranking state is introduced.
- Focused tests, architecture/provider/context/tool tests, repeated comparative evaluation, Scenario AO retrieval behavior, and MTP-250 pass.
- The fixed task shows fewer redundant searches/reads and dependent rounds with equal or better correctness and acceptable latency.

## 15. Risks

- **Natural language produces unstable retrieval:** use deterministic tiers, explicit reasons, fixed tie-breaking, and repeated tests.
- **Ranking overfits one benchmark:** maintain varied fixtures and require control-task non-regression.
- **Catalog construction is expensive:** build lazily per generation, bound work, measure latency, and abandon stale results.
- **Large outputs merely move the cost:** measure residual context and allocation usefulness, not call count alone.
- **Tests/generated code disappear:** use contextual penalties and query-focus exceptions, never hard exclusions.
- **Tool descriptions cause over-selection:** state C# semantic scope and honest fallback boundaries concisely.

## 16. Documentation

When implemented, document natural-language scope, deterministic selection reasons, ranking limitations, budgets, source allocation, evaluation interpretation, and granular fallbacks in user/operations docs. Maintain Scenario AO, MTP-250, schema fixtures, and relevant DOX only when owned contracts change.

All implementation and review notes must preserve the boundary that `C:\source\repos\codegraph` is functional reference only. Threadsmith must not copy or reverse engineer its code, algorithms, constants, rankings, schemas, prompts, tests, internal names, or architecture.

## 17. Open Decisions

- Exact closed stop-word/identifier segmentation rules and localization expectations.
- Whether catalog entries include private/local symbols by default or only after containing-member selection.
- Final ordered selection tiers and which factors are exposed publicly versus only as closed reason codes.
- Effective model-aware source budget formula and minimum useful declaration/call-site size.
- Materiality thresholds for declaring a ranking/allocation improvement in repeated evaluation.
