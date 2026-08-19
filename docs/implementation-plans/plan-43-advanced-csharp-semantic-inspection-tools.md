# Plan 43 — Advanced C# Semantic Inspection Tools

**Milestone:** M14 — Rich Native Tool Inventory

**Prerequisites:** plans 06, 08, 11–13, 27, and 41–42

**Depends on by:** plan 44

**Status:** Complete. Production implementation, focused automated coverage, documentation, and milestone integration are complete; maintained real-repository scale, multi-TFM/source-generator, cancellation, and terminal cases remain compatibility checks.

## 1 Objective

Add typed call-hierarchy, symbol-impact, syntax-aware C# pattern-search, and generated-code inspection tools on top of the existing Roslyn/MSBuild semantic engine.

## 2 Architectural Context

Plan 06 owns workspace lifecycle, symbol identity, references/implementations, generated/linked classification, invalidation, and semantic confidence. Plan 11 owns semantic mutations. M14 extends read-only analysis only; it does not expose Roslyn objects, create a second workspace, or imply certainty beyond the active semantic confidence and bounded analysis depth.

## 3 Scope

- Incoming/outgoing call hierarchy with dispatch kind and source provenance.
- Bounded symbol-impact analysis across references, callers, implementations/overrides, project dependencies, tests, diagnostics, and generated/linked files.
- Syntax-aware C# pattern search using a closed host-owned pattern schema.
- Generated-code inventory and bounded source inspection with generator/origin metadata where available.
- Confidence, completeness, truncation, cycles, invalidation generation, availability, evidence, telemetry, and interactive/headless parity.

## 4 Non-Scope

- Whole-program soundness, runtime reflection/dynamic-call resolution, execution tracing, debugger integration, or cross-language AST patterns.
- Arbitrary Roslyn scripts, user-supplied analyzers/code, regex-to-syntax compilation, or raw syntax-tree serialization.
- Editing generated files, running generators implicitly, or treating generated output as durable source authority.
- Semantic mutation or automatic change-scope approval.

## 5 Current State

Implemented. Threadsmith now exposes four closed `TrustedBuild`, read-only tools: `call_hierarchy`, `symbol_impact`, `csharp_pattern_search`, and `generated_code_query`. Host-owned contracts live in Core; `AdvancedSemanticQueryService` reuses the existing workspace-owned Roslyn solution and fences every result to a monotonically increasing semantic generation. Call traversal reports incoming/outgoing edges, dispatch, ambiguity, cycles, confidence, provenance, and explicit depth/node/edge/time omissions. Impact composes references, callers, implementations/overrides, dependent projects/tests, and generated/linked classification with a reason for every edge while explicitly excluding runtime-only and unavailable diagnostic facts.

The version-1 pattern schema supports declaration/type/method/property/field/attribute/invocation/object-creation/member-access shapes, exact names, containing type, a closed modifier set, exact attributes, and a bounded named whole-node capture. It accepts no source, regex, predicate, script, analyzer, or plugin input. Generated-code query inventories only convention-classified and Roslyn-exposed source-generated documents already present in the loaded snapshot; content, document count, scope, origin, and omissions are bounded, and generators are never run by the query. The four adapters use the shared registry/policy/evidence pipeline, so interactive and headless operation is identical.

## 6 Proposed Design

Extend the existing semantic engine with query services bound to one immutable workspace generation. All traversals have explicit node/depth/edge/time budgets and return visited/omitted reasons. Results identify symbol keys, project/TFM, source range, dispatch/relationship kind, confidence, and workspace generation.

Syntax search uses a closed versioned pattern AST covering declarations, attributes, modifiers, type/member relationships, invocation/object-creation/member-access shapes, and named captures. It is not source code, regex, or executable query text. Unsupported constructs fail schema validation.

Generated-code inspection reads only files/documents already classified by the semantic engine and reports origin evidence when Roslyn/MSBuild exposes it. Missing generator provenance is represented as unknown, never inferred from content alone.

## 7 Public Contracts

- `CallHierarchyRequest/Result`, `CallHierarchyNode`, `CallHierarchyEdge`, and `CallDispatchKind`.
- `SymbolImpactRequest/Result`, `ImpactNode`, `ImpactEdge`, `ImpactKind`, and bounded traversal summary.
- Versioned `CSharpPattern`, closed node predicates/captures, `CSharpPatternSearchRequest/Result`, and matched source ranges.
- `GeneratedCodeQuery/Result`, `GeneratedDocumentInfo`, `GeneratedCodeOrigin`, and bounded content/artifact references.

Public results contain no Roslyn syntax, semantic, symbol, workspace, generator-driver, MSBuild, terminal, or persistence implementation types.

## 8 Project/File Changes

- `Threadsmith.DotNet` — semantic query implementations, pattern compiler, impact traversal, generated-document adapter, caching/invalidation.
- `Threadsmith.Tools` — typed schemas, handlers, confidence/availability gates, policy, and result bounds.
- `Threadsmith.Context` — provenance-aware evidence selection for graph/pattern/generated results where existing generic evidence is insufficient.
- `Threadsmith.App`, TUI, CLI, telemetry, dedicated M14 tests/fixtures, docs, scenarios, and DOX.

## 9 Ordered Tasks

1. Inventory semantic workspace, symbol identity, confidence, invalidation, generated-code, diagnostic, test, and project-graph contracts.
2. Define relationship taxonomy, traversal budgets, completeness semantics, and stable host-owned DTOs.
3. Implement direct incoming/outgoing call edges, then bounded hierarchy traversal with cycles and dispatch classification.
4. Implement impact graph composition over existing semantic/project/diagnostic/test facts with reasoned edges.
5. Define and validate the closed C# pattern schema; implement compilation to Roslyn queries without executable input.
6. Add generated-document inventory/content queries with classification and best-effort origin metadata.
7. Integrate tool availability, immutable-generation snapshots, cancellation, caching, invalidation, evidence, redaction, and telemetry.
8. Add fixtures for interfaces, virtual/override, extension/local functions, delegates, partials, generics, multi-TFM, generated/linked code, broken projects, cycles, and large graphs.
9. Update docs, Scenario N, manual cases, roadmap status, and DOX when implementation lands.

## 10 Testing

Verify direct and transitive calls, interface/virtual ambiguity, cycles, deterministic ordering, depth/node/time limits, impact reason edges, affected tests/projects, degraded confidence, invalidation fencing, every pattern kind/capture, invalid/oversized patterns, no code execution, generated/source-linked distinctions, origin unknown handling, cancellation, redaction, interactive/headless equivalence, and architecture isolation.

## 11 Security/Permissions

Patterns are inert structured data and cannot load assemblies, analyzers, scripts, or plugins. Source content remains repository-confined and sensitivity-policy governed. Generated output is untrusted repository/build evidence. Queries never trigger build, restore, generator execution, mutation, or network access implicitly.

## 12 Observability

Record query kind, workspace generation, confidence, root symbol/pattern identity hash, visited/returned/omitted counts, bounds reached, cache status, duration, cancellation, and normalized failure. Do not log patterns containing sensitive names, source snippets, generated contents, or raw Roslyn diagnostics.

## 13 Migration/Compatibility

Existing semantic tool schemas and symbol IDs remain supported. New query caches are ephemeral unless a durable host-owned artifact is justified; restored sessions do not retain Roslyn object graphs. Unknown future relationship/pattern versions fail closed but remain inspectable as unsupported evidence.

## 14 Acceptance Criteria

- Call hierarchy returns bounded incoming/outgoing relationships with dispatch kind, provenance, confidence, and omissions.
- Symbol impact explains each included project/symbol/test/diagnostic/generated relationship and never claims whole-program certainty.
- Syntax-aware search accepts only the closed typed pattern schema and returns captures/ranges without executing user/model code.
- Generated-code inspection identifies classified documents and bounded content/origin metadata without implicit generation.
- All queries run against one fenced semantic generation, discard late stale results, propagate cancellation, and degrade honestly.
- Roslyn/MSBuild types remain confined to compiler-aware internals and architecture tests pass.

## 15 Risks

- Impact is mistaken for proof: expose relationship reasons, confidence, unknown dynamic edges, and completeness limits.
- Traversals explode: enforce depth/node/edge/time/result-byte budgets and deterministic truncation.
- Pattern language becomes a scripting language: keep a closed declarative schema with no predicates or executable extensions.
- Generated provenance varies by project system: represent unknown explicitly and test degraded cases.

## 16 Documentation

Document relationship semantics, confidence/completeness, traversal limits, supported patterns, capture schemas, generated-code provenance, invalidation, and examples. Do not advertise implementation before it lands.

## 17 Decisions

- Advanced semantic tools extend the existing semantic engine and workspace generation.
- Impact analysis is explainable bounded evidence, not a sound whole-program guarantee.
- C# pattern search is a closed typed AST, not source snippets, regex routing, or scripting.
- Generated-code queries never run generators implicitly.
