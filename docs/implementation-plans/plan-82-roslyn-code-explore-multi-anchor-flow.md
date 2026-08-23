# Implementation Plan 82: Roslyn Code Explore Multi-Anchor Flow

**Status:** Planned

**Delivery track:** Milestone 28 — compiler-proven flow and impact composition
**Strategy source:** Shared Context §A.1, §A.3, §A.5, §C, and §G; Milestone 28; Scenario AO
**Prerequisite plans:** plans 43 and 81; Plan 81 acceptance and MTP-248 must pass before implementation begins

## 1. Objective

Extend `code_explore` from exact source-bearing anchors into a task-sufficient multi-anchor semantic slice. One query should connect named C# symbols through bounded compiler-proven call paths, return the source bodies and call sites needed to understand the path, expose interface/virtual implementation branches, mark runtime uncertainty, and attach a compact blast radius.

This plan must remain independently user-testable. Plan 83 must not begin until the automated flow/dispatch gates and MTP-249 pass on the maintained semantic-flow fixture and one disposable multi-project repository.

## 2. Architectural Context

Plan 43 owns call hierarchy, dispatch classification, symbol impact, traversal summaries, and generation fencing. Plan 81 owns the `code_explore` contract, exact anchor resolution, source sections, current-content identity, and completeness dimensions. This plan composes those capabilities inside the same snapshot-scoped service; it does not invoke registered semantic tools or ask the model to sequence their IDs.

Roslyn can prove direct calls, constructors, extension methods, local functions, interface/virtual targets, and declaration relationships more precisely than a language-agnostic graph. It cannot prove arbitrary reflection, runtime dependency injection, dynamic binding, or all delegate targets. The result must make that boundary useful rather than filling it with guessed edges.

The local repository at `C:\source\repos\codegraph` is a functional reference only for the observable utility of returning a flow before its relevant source and impact context. Threadsmith is not copying, porting, reverse engineering, depending on, or implementing compatibility with that codebase; no source, algorithm, traversal constant, schema, prompt, test, internal name, or implementation structure from it is normative or reusable.

## 3. Scope

- Accept two or more exact resolved anchors and a bounded `Auto`, `Flow`, `Survey`, or `Impact` exploration intent.
- Build one deterministic semantic subgraph from the Plan 81 snapshot and resolved anchor identities.
- Find and rank compiler-proven paths among named anchors, favoring direct short paths and permitting bounded unnamed bridge symbols where necessary.
- Return flow nodes, typed edges, exact call sites, cycles, depth, and source projection linkage.
- Expand interface and virtual dispatch into bounded compiler-known implementation/override branches with total/returned counts.
- Mark delegate, dynamic, reflection, dependency-injection, framework, and runtime-only boundaries explicitly, with exact known call sites and omissions rather than invented continuations.
- Include declaration bodies for named anchors and flow-spine nodes, plus focused source around material call sites where full bodies do not fit.
- Add compact callers, implementations, dependent projects, and tests for the primary anchors with reasoned relationships and continuation targets.
- Preserve deterministic limits for anchors, paths, bridges, branches, nodes, edges, depth, files, source, references, projects, tests, and time.
- Reuse Plan 81 confidence, generation, content identity, drift, provenance, and completeness contracts.

## 4. Non-Scope

- Natural-language anchor discovery and graph-based relevance ranking across unanchored candidates; Plan 83 owns it.
- Heuristic or execution-derived runtime call edges, traces, profiler/debugger data, or whole-program soundness.
- General control-flow/data-flow proof beyond the bounded semantic call and type relationships explicitly accepted here.
- Cross-call source deduplication or associated non-C# artifacts.
- Automatic mutation scope, plan generation, test selection authority, or validation acceptance.
- Hiding or removing granular call hierarchy, impact, reference, or implementation tools.

## 5. Current State

`call_hierarchy` can traverse incoming/outgoing compiler-known calls for one stable symbol and reports dispatch, ambiguity, cycles, limits, and omissions. `symbol_impact` returns references, callers, implementations, dependent projects/tests, and generated/linked classification for one root. Neither query finds a path among several named endpoints or includes their source bodies.

Plan 81 will provide exact resolved anchors and source sections but no composed multi-anchor graph. Without this plan, the model must still join hierarchy/impact results and decide which files/ranges to read in subsequent rounds.

## 6. Proposed Design

### 6.1 Bounded semantic slice

Resolve all anchors first, then gather only relationship neighborhoods needed to connect them and explain material impact. Use deterministic pairwise or multi-terminal shortest-path selection over compiler-known edges, with fixed tie-breaking by edge certainty, path length, symbol identity, and source location. Bound connector discovery so high-fan-out utility methods do not cause graph explosion.

### 6.2 Dispatch branches and boundaries

Every edge retains `CallDispatchKind`, call-site provenance, ambiguity, and cycle state. For interface/virtual calls, query compiler-known implementations/overrides and include bounded branches with counts. For delegates and unknown/runtime-only mechanisms, report where static proof stops, what symbol/type is known, and which exact follow-up anchors may help. Heuristic candidates, if ever proposed later, require a separate labeled confidence contract and are not part of this plan.

### 6.3 Source-first usefulness

Allocate Plan 81 source sections in this order: explicitly named anchors, selected flow spine, implementation branches required to understand dispatch, then compact impact context. Full declaration bodies are preferred where bounded; otherwise include signature plus exact call-site/body windows with honest omitted ranges. A returned path without its operative source is incomplete.

### 6.4 Blast radius

Summarize direct callers, implementations, dependent projects, and tests for primary anchors. Return counts and a small reasoned location set rather than full reference arrays. Preserve exact granular continuation anchors for detailed follow-up.

## 7. Public Contracts

Additive host-owned contracts include:

- `CodeExploreMode` — `Auto`, `Survey`, `Flow`, and `Impact`;
- `CodeExploreFlow` — selected paths, nodes, edges, boundaries, cycles, and completeness;
- `CodeExploreFlowNode` — semantic identity, role, depth, source-section reference, and named/connector status;
- `CodeExploreFlowEdge` — caller/callee IDs, dispatch kind, call site, ambiguity, cycle, and proof classification;
- `CodeExploreDispatchBranch` — dispatch root, implementation identity/location, returned/total counts, and omissions;
- `CodeExploreBlastRadius` — bounded callers, implementations, projects, tests, reasons, counts, and continuation anchors.

Existing Plan 81 contracts evolve additively and remain serializable, provider-neutral, and free of Roslyn implementation types.

## 8. Project/File Changes

Expected areas:

- `Threadsmith.Core` — additive flow, dispatch-boundary, and blast-radius DTOs.
- `Threadsmith.DotNet` — snapshot-scoped graph gather, path selection, implementation expansion, impact summarization, and source linkage.
- `Threadsmith.Tools` — mode/limit validation, result bounds, provenance, and activity detail.
- `Threadsmith.Context` — compact semantic-flow evidence selection only where generic tool evidence is insufficient.
- Telemetry, TUI/headless projections, focused semantic/tool/context tests, docs, Scenario AO, and MTP-249.

## 9. Ordered Tasks

1. Verify Plan 81 acceptance evidence and MTP-248; re-read applicable DOX and C# guardrails.
2. Profile current `find_symbol`/`call_hierarchy`/`find_implementations`/`symbol_impact`/`read_file` chains on a fixed multi-anchor task.
3. Freeze path-selection, bridge, dispatch-branch, runtime-boundary, blast-radius, source-priority, and completeness semantics.
4. Implement deterministic bounded multi-anchor graph gathering against one captured snapshot.
5. Implement compiler-proven path selection, cycles, typed call sites, interface/virtual branches, and explicit unresolved boundaries.
6. Integrate declaration/call-site source allocation and compact impact summaries.
7. Add limits, cancellation, stale-generation discard, provenance, telemetry, and provider projection.
8. Add direct/interface/virtual/delegate/extension/local/constructor/overload/cycle/disconnected/large-fan-out fixtures.
9. Run focused tests, architecture tests, provider/tool tests, solution build, formatting, and planning-governance checks.
10. Run MTP-249 interactively and headlessly; record checkpoint evidence before changing plan status.
11. Complete docs/DOX closeout. Begin Plan 83 only after this plan's acceptance and user-testable gate pass.

## 10. Testing

Automated coverage must verify path correctness and deterministic tie-breaking, no-path results, bounded unnamed bridges, all existing dispatch kinds, interface/virtual implementation counts, delegate/runtime boundaries, cycles, overload identity, source linkage, call-site ranges, impact reasons/counts, dependent projects/tests, generated/linked nodes, depth/node/edge/time/source limits, partial compilation, cancellation, invalidation, interactive/headless parity, result serialization, architecture isolation, and unchanged granular tool behavior.

The user-testable checkpoint is [MTP-249](manual-test-plan.md#mtp-249--multi-anchor-semantic-flow-and-dispatch-branches). Its successful completion is a blocking prerequisite for Plan 83.

## 11. Security/Permissions

The extension remains a read-only semantic operation under Plan 81 trust and path policy. It cannot execute a discovered call, load arbitrary code, infer authority from dependency injection/configuration, run tests/builds/generators, mutate source, contact the network, or approve scope. Project and test relationships are evidence only. Source/query/symbol content is not logged.

## 12. Observability

Record anchor/path/node/edge/bridge/branch/boundary counts, selected path lengths, dispatch-kind counts, impact counts, source allocation by role, completeness/limit flags, workspace generation, confidence, duration, cancellation, and sanitized outcome. Do not log graph bodies, source, query text, symbol names where existing privacy policy disallows them, raw paths, provider payloads, or hidden reasoning.

## 13. Migration/Compatibility

Flow fields are additive to the versioned Plan 81 result. Clients that understand only exact anchors can ignore them. Existing `call_hierarchy`, `symbol_impact`, `find_references`, and `find_implementations` remain stable and independently callable. Stored generic tool evidence remains readable; no Roslyn graphs or object references are persisted.

## 14. Acceptance Criteria

- Multiple exact anchors produce deterministic bounded compiler-proven paths or an honest no-path result from one workspace generation.
- Named/spine source and material call sites accompany the path under declared budgets.
- Interface/virtual branches are compiler-known and counted; delegate/dynamic/runtime boundaries are explicit and not guessed.
- Blast-radius evidence explains included callers, implementations, projects, and tests without impersonating exhaustive validation scope.
- Plan 81 source identity, drift, confidence, provenance, and completeness remain intact.
- Focused tests, architecture/provider/tool tests, solution build, Scenario AO flow behavior, and MTP-249 pass.
- The fixed task uses fewer dependent model rounds than the granular baseline with equal or better flow correctness.

## 15. Risks

- **Graph explosion:** bound connector search, branches, depth, fan-in/out, and time; preserve counts/continuations.
- **Shortest path is not explanatory path:** prioritize named anchors, dispatch certainty, and operative source with deterministic scoring.
- **Runtime gaps tempt false inference:** expose exact static boundary and omissions; never synthesize unlabeled edges.
- **Impact data crowds source:** reserve source for named/spine nodes and summarize impact by count/location.
- **Repeated Roslyn solution scans are slow:** reuse one snapshot and bounded per-query caches without durable Roslyn state.

## 16. Documentation

When implemented, document flow modes, dispatch classifications, runtime limitations, source allocation, blast radius, and granular follow-ups in the user guide and native-tool operations reference. Maintain Scenario AO and MTP-249 procedures, tool schema fixtures, event catalog additions, and relevant DOX only when owned contracts change.

Implementation reviews must continue to state that `C:\source\repos\codegraph` is functional reference only. No copied or reverse-engineered source, algorithms, thresholds, schemas, prompts, tests, names, or internal structure may enter Threadsmith.

## 17. Open Decisions

- Exact multi-terminal path-selection algorithm and deterministic tie-break order.
- Maximum unnamed connector count and whether callers and callees have different costs.
- Whether implementation branches appear inline on flow edges or in a separate bounded branch collection.
- Minimum source required for an edge to count as sufficiently explained.
- Which impact categories are always present versus requested only in `Impact` mode.
