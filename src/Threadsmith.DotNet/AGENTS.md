# AGENTS.md — Threadsmith.DotNet

## Purpose

Own .NET SDK, MSBuild, Roslyn workspace, confidence-aware semantic discovery, and semantic mutation adapters.

## Ownership

- `SemanticEngine.cs` — solution loading, project/TFM inventory, symbol operations, fast Roslyn diagnostics, in-memory pre-mutation syntax/compilation analysis, invalidation, promotion, and non-cooperative cancellation backstop.
- `SemanticEngineRegistry.cs` — one semantic-engine lifetime per `WorkspaceId` and workspace-scoped query/diagnostic resolution.
- `SemanticLifecycleObserver.cs` — non-reentrant repository/solution event queue into semantic loading.
- `SemanticMutationEngine.cs` — Roslyn symbol rename and exact syntax-node replacement that emit host-owned transactional text mutations.
- `DotNetInventoryService.cs` — normalized selected-solution/project/TFM/reference/package/test facade over loaded semantic state with bounded central-package metadata inspection.
- `AdvancedSemanticQueryService.cs` — Generation-fenced natural-language and exact code-explore source projection, call hierarchy, explainable impact, closed C# structural-pattern search, and already-loaded generated-document inspection.
- `NaturalLanguageNameSegmentMatcher.cs` — Declaration-name segmentation, prose-concept co-occurrence, rarity, and primary-subject confidence for natural-language exploration ranking.
- `CodeExploreTermNormalizer.cs`, `CodeExploreCandidateRetriever.cs`, `CodeExploreRelevancePolicy.cs`, `CodeExploreSourceAllocationPlanner.cs`, `CodeExploreGeneratedSourceClassifier.cs`, and `CodeExploreToolCapabilityClassifier.cs` — Shared morphology/alias normalization, indexed candidate retrieval, ranking constants and kind penalties, score-proportional source-envelope planning, index-time generated-source classification, and compiler-derived tool-family contracts. Core-owned `CodeExploreQueryIntentPolicy` supplies the canonical relationship parse shared with the model-facing tool adapter.

## Local Contracts

- Public boundaries use `Threadsmith.Core` semantic DTOs; Roslyn and MSBuild types remain internal.
- `TrustedRead` permits text/project metadata only; `TrustedBuild` permits MSBuild evaluation and compilation.
- Every semantic result carries stable identity, source location, target framework, generated/linked classification, and `SemanticConfidenceLevel`.
- Semantic state is isolated by `WorkspaceId`; a query must resolve the workspace captured by host invocation context.
- The registry exposes immutable project-inventory snapshots, fast host-owned Roslyn diagnostics, and `IPreMutationAnalyzer` so validation/execution can provide default semantic feedback without launching `dotnet build` or tests. Diagnostic refresh must update existing affected-project source documents from disk plus changed source text, and structurally add/remove created/deleted source documents before compilation. Pre-mutation analysis must use only in-memory overlays, never mutate disk or replace the live semantic solution, return Core DTOs only, publish bounded semantic-check activity for syntax/compilation progress through non-cancellable lifecycle-event boundaries, and record explicit omissions for unavailable semantic/analyzer coverage.
- Lifecycle event handlers enqueue loads and return before the engine publishes confidence changes and a terminal completion fact, including unavailable (`None`) results.
- `FullSemantic` requires every expected project to load and compile without an MSBuild workspace failure; omitted projects degrade confidence.
- Direct `.csproj`, `.fsproj`, and `.vbproj` selections use project loading rather than solution loading.
- Apply queued invalidations only at a turn boundary. Abandoned Roslyn/MSBuild results must never replace newer state.
- Text-reference fallback skips reparse points, build/Git directories, and configured prohibited paths; it bounds enumerated entries, files, file size, and total matches.
- Semantic mutations require `PartialCompilation` or better, capture one serialized engine snapshot, and never apply changes through Roslyn workspace APIs. They emit baseline-hashed `MutationSet` DTOs for `Threadsmith.Workspaces`.
- Rename changes only documents in the compiled subset, correlates every emitted mutation to the stable symbol id, and reports omitted/uncompiled or non-baseline documents as warnings.
- Bounded syntax replacement requires an exact expression, statement, or member node span; invalid replacement syntax is rejected and formatting is limited to the annotated replacement region.
- Semantic source projection caches linked-document counts and parsed target-framework metadata once per query.
- Inventory derives selected-solution provenance and project metadata paths from the authoritative loaded workspace, exposes every metadata read for tool-policy confinement, preserves semantic confidence and omissions, bounds project/XML reads, attributes project versus central package versions where knowable, and never restores or builds.
- Advanced semantic queries capture the existing workspace solution, compiled-project set, confidence, repository root, and monotonically increasing generation once; late stale results fail rather than replacing current evidence. `code_explore` first captures non-throwing readiness so low-confidence or unloaded workspaces return explicit availability instead of generic tool failure. Code exploration resolves exact symbols, stable IDs, confined C# path anchors, and deterministic Roslyn-backed natural-language candidate declarations when exact anchors are not known. Natural-language ranking uses only compiler-known declarations, allowed paths, stable IDs, catalog-derived declaration-name segment co-occurrence and frequency, token/qualified-name/co-location/graph evidence, and explicit bounds; code-shaped identifiers retain exact priority, while prose never becomes a synthetic exact identifier. Project-scoped impact and flow queries require primary-clause name evidence before later query terms can suppress the compact incomplete-scope fallback. Ranking must not use embeddings, provider reranking, runtime execution, or catalog entries outside invocation path policy for scoring statistics. Code exploration verifies current file identity before source emission, suppresses repeated source only when request-local visible-source frontier entries prove exact unchanged complete coverage for the same repository/workspace/generation/path/digest and the serialized exact-range pointer is smaller than source, and otherwise safely re-emits or omits through ordinary drift/policy/budget checks. It returns bounded host-sanitized line-numbered source projection, exact-range current-context back-references, actual source-emission records, allocation/deduplication summaries, ambiguity/omission reporting, and continuations. Associated artifacts remain separate supplements seeded only by verified-current selected source or validated current-context back-references: selected-source literal/logical/exact-name relationships outrank broad project metadata under default limits, candidate admission stops before probing once the explicit bound is reached, exact-name lookup has per-query literal/scan bounds with cached repeated lookups, and raw XML project item/resource references are weaker textual inference unless Roslyn-loaded project evidence proves them. Traversals are deterministic and depth/node/edge/time bounded, impact edges carry reasons, runtime/dynamic unknowns remain omissions, patterns use only the closed version-1 host schema, and generated queries inspect only already-loaded classified documents without running generators; generated document-count and per-document content bounds are host-owned service policy.
- Natural-language code exploration merges literal, stem, alias, prefix, fuzzy, exact, qualified-name, path, tool-family, and graph channels by maximum evidence while preserving reasons; multi-term corroboration is applied before truncation; exact named roots remain protected while complete compound matches seed bounded graph expansion without becoming synthetic exact roots. Catalog and graph construction are generation-owned single-flight work, and direct compiler-resolved tool construction links composition declarations to catalog-derived tool families. Random-walk mass, relative score floors, kind/generated/test penalties, file/type diversity, and score-proportional source allocation remain deterministic host policy. Explicitly requested tool-family definitions and every selected-anchor summary survive diversity/diagnostic caps. Relationship intent requires relationship syntax rather than tool-name co-mention alone, and relationship vocabulary is excluded from project-scope subject confidence without weakening exact identifier or ordinary ranking evidence.

## Work Guidance

- Register MSBuild through `Microsoft.Build.Locator` before creating `MSBuildWorkspace`.
- Propagate cancellation; wrap non-cooperative APIs with bounded abandon-and-discard behavior.
- Do not execute repository build targets below `TrustedBuild`.

## Verification

- `dotnet test tests/Threadsmith.ModelTooling.Tests/`
- `dotnet test tests/Threadsmith.Mutations.Tests/ --filter "FullyQualifiedName~SemanticMutation"`

## Child DOX Index

No child AGENTS.md files yet.
