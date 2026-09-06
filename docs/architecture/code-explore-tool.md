# `code_explore` implementation guide

## Purpose

`code_explore` answers questions about loaded C# code with current, compiler-backed evidence. It accepts a small model-facing request and performs the larger retrieval, ranking, graph, source, and output-bounding workflow inside the host.

The tool is designed to return enough evidence for an agent to answer a question without forcing it to assemble the same context through many separate symbol, reference, hierarchy, and file reads. It remains bounded and conservative: incomplete workspace coverage, runtime-only behavior, source drift, and output truncation are reported rather than inferred away.

This guide describes the implementation. The user-facing contract remains in [`docs/user-guide.md`](../user-guide.md#code_explore-task-sufficient-c-exploration), and the operator-facing tool summary remains in [`docs/operations/tools.md`](../operations/tools.md).

## Model-facing contract

The model passes:

- required `query`
- optional `maxFiles`

The host supplies repository identity, workspace identity, trust, path policy, source reader, current-context source frontier, selected-model budget, and all traversal and output limits. The model does not choose internal modes, graph depth, candidate counts, source bytes, timeouts, or continuation identity.

The query can contain:

- a natural-language architecture or behavior question
- an exact symbol name, qualified name, overload-like signature, or stable symbol id
- a repository-relative C# path
- feature terms when the exact declaration is unknown
- a host-issued `code_explore:continue:...` cursor

## Request pipeline

```text
model query
    |
    v
validate and capture one workspace generation
    |
    +--> continuation replay, when the query is a host-issued cursor
    |
    v
interpret query and resolve exact/path/stable-id anchors
    |
    v
load or build the generation-owned declaration catalog and indexes
    |
    v
retrieve candidates through independent evidence channels
    |
    v
rank, graph-expand, rerank, and select a diverse semantic spine
    |
    v
plan whole source sections and verify current file identity
    |
    +--> optional flow, impact, and associated-artifact evidence
    |
    v
fit the structured result and Markdown to the model envelope
    |
    v
return evidence, omissions, and exact continuation targets
```

Every phase observes cancellation and the captured workspace generation. A newer generation invalidates unfinished cold catalog or graph work before it can be published as current evidence.

## Workspace snapshot and readiness

`AdvancedSemanticQueryService` obtains one `AdvancedSemanticSnapshot` from `SemanticEngineRegistry`. The snapshot carries the loaded solution, compiled-project coverage, repository root, semantic confidence, and monotonic workspace generation.

An unloaded or insufficient workspace returns a bounded availability result instead of throwing a generic tool error. Partial compilation can still produce evidence, but coverage is marked incomplete and unloaded or uncompilable projects are not presented as searched.

The request also creates a `SemanticSourceProjection`. It caches linked-document counts, target frameworks, and generated-source classification for the immutable query snapshot. Generated classification is the union of source-generated identity, generated path conventions, and source headers.

## Declaration catalog

Natural-language discovery operates over compiler-known declarations, not arbitrary repository text. Catalog construction walks admitted C# documents and records host-owned metadata for declarations that can be resolved to Roslyn symbols:

- stable symbol identity and display name
- simple, metadata, fully qualified, containing-type, and namespace names
- declaration kind and accessibility
- project, target framework, path, and source range
- linked, test, and generated classification
- normalized name, signature, context, and full terms
- qualified-name forms
- explicitly constructed type names from method bodies and initializers
- catalog-derived agent-facing tool capability relationships

The catalog is indexed by exact names, identities, qualified names, normalized terms, tool families, and segmented declaration-name concepts. Indexes and sorted vocabularies let retrieval use bounded lookups and scans instead of rescoring every declaration for every query.

Catalog construction is single-flight per workspace generation. Concurrent requests await the same host-owned build. A cancelled waiter does not cancel shared work needed by other requests, and an older snapshot cannot start new cold work after a newer generation has been observed.

## Query interpretation

The query interpreter extracts bounded, inert evidence:

- stable symbol ids
- path-like spans
- qualified names
- code-shaped exact identifiers
- normalized natural-language terms
- ignored common terms
- test, generated, artifact, flow, impact, and tool-capability intent

Normalization creates weighted literal, stem, alias, and prefix-compatible forms. Literal forms carry the strongest evidence, stems are weaker, and aliases are weaker still. Prose terms never become synthetic exact identifiers.

Declaration names are segmented across PascalCase, camelCase, underscores, punctuation, and acronym boundaries. Segment matching tracks concept co-occurrence, rarity, declaration coverage, and primary query subjects. For flow and impact questions, relationship words such as caller, reference, or dependency select the traversal but do not become required subject-name concepts. Exact symbol and ordinary ranking evidence remain unchanged, and a unique literal declaration-name match is sufficient to keep a project-scoped relationship query from falling back as missing. A complete compound name match can seed graph expansion without being treated as an exact symbol.

## Candidate retrieval

Candidate retrieval is a separate host-owned stage. It uses independent channels and merges duplicate declarations by maximum score while retaining all evidence reasons:

- stable identity
- exact identifier
- qualified name
- C# path
- literal, stem, and alias term postings
- bounded prefix vocabulary scans
- bounded fuzzy name recovery
- segmented declaration-name matches
- catalog-derived tool-family relationships
- structurally linked composition declarations

Tool-family postings are admission evidence, not automatic rank. Rank still comes from exact names, query terms, declaration structure, requested roles, and other corroboration. This prevents a broad capability family from behaving like an exact symbol match.

Fuzzy and prefix retrieval are deliberately limited. They recover likely names when exact vocabulary lookup fails; they do not replace exact or multi-term evidence.

## Ranking

Ranking is deterministic. The strongest applicable evidence wins within a channel, and independent corroboration adds bounded adjustments. Important signals include:

- pinned identity, path, qualified name, and exact identifier evidence
- declaration-name concept coverage and rarity
- literal, stem, and alias term coverage
- multiple query concepts on one declaration
- containing-type and same-file corroboration
- explicit declaration-kind focus
- requested agent-facing tool definitions, contracts, and composition surfaces
- graph mass and centrality
- generated and test focus

Generated and test declarations are penalized for ordinary production questions, but explicit generated/test focus removes that disadvantage. Private helpers and constructors are down-ranked for broad capability explanations unless the query identifies them directly.

Multi-term corroboration is calculated before candidate truncation. This prevents many weak single-term matches from excluding a declaration that explains several parts of the question.

Exact candidates are selected before capability or lifecycle reservations. Structural composition evidence can reserve one context slot only when the query and declaration directly support that relationship. Constructed tool types link host composition methods to catalog-derived tool families without adding their type names to unrelated full-text terms.

## Graph expansion

Strong exact and compound roots can expand into a bounded declaration graph. Compiler-resolved calls, references, implementations, and overrides contribute edges where available. Project dependency relationships are reported separately as impact evidence.

Expansion is bounded by host-owned depth, node, edge, reference-location, time, and concurrency limits. The service computes a deterministic random-walk relevance mass from the selected roots. Graph mass is supporting evidence: it cannot make a disconnected low-relevance declaration outrank strong direct query evidence.

Per-symbol graph-neighbor discovery is cached and single-flight within a workspace generation. Each query assembles its bounded adjacency view and random-walk ranking from those neighbors. Runtime-only dispatch, reflection, dependency-injection resolution, and dynamic calls remain unresolved unless Roslyn exposes an explicit boundary that can be reported.

## Selection and diversity

Selection produces the semantic spine used by later source and relationship phases. It preserves:

- exact and pinned declarations
- directly requested tool-family definitions
- directly supported composition context
- distinct requested capability families
- focused compound-name matches
- relevant files and containing types

Per-file and per-type diversity caps prevent one implementation cluster from consuming every anchor. Exact declarations bypass those diversity caps. Already selected identities do not consume another candidate slot or diversity count.

Relative score floors remove weak tail candidates after ranking while protecting exact roots and directly supported structural context. Candidate summaries retain selected anchors even when diagnostic summary counts are reduced.

## Source planning

The source planner allocates the host-owned source envelope across selected files. It uses effective relevance weight, exact and graph-spine protection, and these general rules:

- reserve fixed rendering overhead
- allocate a useful minimum to admitted files
- distribute the remaining envelope proportionally
- prevent one file from consuming the whole response
- remove the weakest unprotected file when the envelope cannot support all files

Selected declarations in the same file can be clustered when their source ranges are close. The planner allocates whole declaration or clustered ranges and never emits a partial source line. Projection can return the complete lines that fit from an allocated range, mark that section partial, and issue a continuation for the remainder.

Before emission, each file is read through `ICodeExploreSourceReader` and checked against path policy, current identity, size, and drift. Complete source carries file and range digests. A complete unchanged range already visible in the canonical model request may become a smaller exact back-reference; otherwise it is re-emitted or omitted conservatively.

When an allocated section cannot fit in full, the result records an exact source continuation target rather than silently dropping the omitted range.

## Flow and impact evidence

The host derives flow or impact emphasis from the query and resolved anchors. These sections are not added to every response.

Flow can include:

- compiler-resolved caller/callee paths
- call sites
- interface and virtual dispatch branches
- unresolved runtime boundaries
- cycle and truncation notes

Impact can include bounded samples and totals for:

- callers and references
- implementations and overrides
- directly and transitively dependent projects
- test projects and classified test source

These are compiler/project-metadata views, not whole-program claims. Dynamic, reflective, external, unloaded, and runtime-only behavior remains outside proven coverage.

## Associated artifacts

Non-C# artifacts are a separate supplement seeded from verified selected source. Relationships can come from repository-relative literals, logical names, exact-name inventory matches, Roslyn additional/analyzer documents, project metadata, and bounded textual project items.

Broad project artifacts are admitted only for explicit artifact or configuration focus. Artifact content is untrusted data and is never executed, imported, or used as C# authority. Every artifact is rechecked through path, size, media, secret-shape, reparse/device, and current-digest policy.

Artifact budgets and continuations are independent from source budgets so a large configuration file cannot displace the semantic source spine.

## Output fitting

`CodeExploreTool` returns the structured `CodeExploreResult`. `CodeExploreOutputFormattingTool` renders the default concise Markdown projection.

Both projections share a ceiling derived from the selected model's effective input budget and the tool's hard result bound. Fitting is progressive and source-first:

1. retain whole primary source sections and exact anchors
2. compact artifacts, flow, impact, candidate diagnostics, and other secondary evidence
3. create continuations for removed whole sections
4. remove oversized continuation text before reducing to the minimal availability/coverage result

The final Markdown includes only sections relevant to the question. Internal scores, generation values, allocation diagnostics, and digests remain structured audit data while they fit instead of dominating model context.

## Continuations

Source, artifact, and impact continuations are opaque host-issued cursors rendered as `code_explore:continue:...` retry queries. The cursor encodes the exact continuation kind and host-owned identity needed for replay.

On replay, the host validates the cursor version and kind. Source and artifact cursors also carry the path, range, workspace generation, and optional digest needed for current-source checks; invocation path policy confines every replay to the active repository. Symbol and impact cursors carry their semantic anchor. A decoded cursor that is stale, drifted, or policy-denied fails closed. Text that is not a valid cursor is interpreted as an ordinary query, and there is no separate model-facing cursor argument.

## Cancellation and concurrency

Cancellation propagates through ordinary async work. Shared catalog and graph builds use host-owned tasks so cancellation of one waiter does not corrupt or cancel a result another request still needs. Non-cooperative Roslyn/MSBuild operations use the repository's bounded abandon-and-discard policy.

The service rechecks workspace generation before publishing cached or final evidence. Results from stale work are discarded rather than labeled current.

## Main implementation files

- `src/Threadsmith.Core/CodeExploreContracts.cs`: host-owned request, result, evidence, coverage, continuation, and allocation DTOs
- `src/Threadsmith.Core/CodeExploreQueryIntentPolicy.cs`: shared relationship-intent parsing and consumed relationship terms
- `src/Threadsmith.DotNet/AdvancedSemanticQueryService.cs`: snapshot, catalog, ranking, graph, source, flow, impact, artifact, and continuation orchestration
- `src/Threadsmith.DotNet/CodeExploreCandidateRetriever.cs`: indexed identity, name, tool-family, term, prefix, path, and fuzzy retrieval
- `src/Threadsmith.DotNet/NaturalLanguageNameSegmentMatcher.cs`: declaration-name segmentation and concept matching
- `src/Threadsmith.DotNet/CodeExploreTermNormalizer.cs`: weighted morphology and aliases
- `src/Threadsmith.DotNet/CodeExploreRelevancePolicy.cs`: shared relevance and classification policy
- `src/Threadsmith.DotNet/CodeExploreSourceAllocationPlanner.cs`: score-proportional source planning
- `src/Threadsmith.DotNet/CodeExploreGeneratedSourceClassifier.cs`: generated path/header classification
- `src/Threadsmith.DotNet/CodeExploreToolCapabilityClassifier.cs`: compiler-derived tool identities, roles, contracts, and families
- `src/Threadsmith.Tools/CodeExploreTool.cs`: minimal model-facing adapter and structured result bounding
- `src/Threadsmith.Tools/CodeExploreOutputFormattingTool.cs`: Markdown rendering and terminal output fitting

## Reading a result

For a query such as "Explain how the model resolver works, and list everywhere it is used":

1. exact and segmented-name channels should identify the resolver declaration and its containing contract
2. term and graph evidence should identify directly related implementations and use sites
3. relationship wording should enable bounded reference/caller/project evidence
4. source planning should emit the resolver and the strongest supporting sections before diagnostics
5. omissions should state partial compilation, graph bounds, runtime-only gaps, or output truncation
6. follow-up targets should replay exact omitted source or impact evidence

A healthy result is question-specific. It should not include every declaration sharing words such as `model`, `tool`, or `resolver`, and it should not add flow, impact, artifacts, or large diagnostic tables without supporting intent.

## Known limits

- Only loaded C# projects and documents participate in semantic discovery.
- Compilation failures can reduce symbol, call, implementation, and reference coverage.
- Runtime dependency-injection resolution, reflection, dynamic dispatch, generated code not loaded in the workspace, and external code are not inferred.
- Catalog, graph, source, artifact, time, and model-envelope limits can make results incomplete.
- Natural-language ranking is deterministic lexical and compiler-structural retrieval; it does not call a provider-side reranker or embedding service.
- Continuations recover omitted host-known evidence, not evidence that was absent from the captured workspace.
