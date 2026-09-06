# Implementation Plan 82: Roslyn Code Explore Multi-Anchor Flow

**Status:** Active. Production implementation, focused automated coverage, and user/operator documentation are in place; MTP-249 interactive/headless evidence and final broader gates remain before completion and before Plan 83 begins.

**Delivery track:** Milestone 28 — compiler-proven flow and impact composition
**Strategy source:** Shared Context §A.1, §A.3, §A.5, §C, and §G; Milestone 28; Scenario AO
**Prerequisite plans:** plans 43 and 81. Plan 81 acceptance and MTP-248 have passed and are no longer blockers for this work item.

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

`call_hierarchy` still traverses incoming/outgoing compiler-known calls for one stable symbol and reports dispatch, ambiguity, cycles, limits, and omissions. `symbol_impact` still returns references, callers, implementations, dependent projects/tests, and generated/linked classification for one root. They remain granular follow-up tools.

Plan 81 now provides exact resolved anchors and source sections. The current Plan 82 implementation composes those anchors into bounded flow and impact evidence inside `code_explore` without invoking registered semantic tools or guessing runtime-only continuations.

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

Implemented areas:

- `Threadsmith.Core` - additive flow, dispatch-boundary, mode, limit, and blast-radius DTOs.
- `Threadsmith.DotNet` - snapshot-scoped path selection, bounded shared traversal budgets, dispatch branch expansion, unresolved-boundary capture, transitive dependent-project/test impact summarization, and source linkage.
- `Threadsmith.Tools` - mode/limit validation, path-policy confinement for source/flow/branch/blast evidence, provenance, truncation, and activity detail.
- `Threadsmith.NativeTools.Tests` - maintained semantic-flow fixture covering direct paths, interface/virtual dispatch, compact impact, unresolved boundaries, policy denial, and tool-adapter confinement.
- User/operations docs, Scenario AO, and MTP-249 procedure text.

`Threadsmith.Context` and provider projection remain compatible through the additive host-owned result contract; no Roslyn or tool-runtime implementation type enters durable state or public projections.

## 9. Ordered Tasks

Completed implementation tasks:

1. Verified Plan 81 acceptance/MTP-248 and re-read applicable DOX and C# guardrails.
2. Froze path-selection, bridge, dispatch-branch, runtime-boundary, blast-radius, source-priority, and completeness semantics for the first Plan 82 slice.
3. Implemented deterministic bounded multi-anchor graph gathering against one captured snapshot.
4. Implemented compiler-proven path selection, typed call sites, bounded interface/virtual branches, explicit unresolved boundaries, and fail-closed policy filtering.
5. Integrated named-anchor-first source allocation, operative call-site source projection, and compact impact summaries over direct/transitive dependent projects and tests.
6. Added limits, cancellation propagation, generation fencing, provenance, tool activity, result confinement, and truncation reporting.
7. Added focused direct/interface/virtual/impact/unresolved-boundary/policy-confinement automated coverage.

Remaining before completion:

1. Run MTP-249 interactively and headlessly on the maintained semantic-flow fixture and one disposable multi-project repository.
2. Run the broader focused semantic/tool/context/provider/architecture gates required by release readiness.
3. Record final checkpoint evidence and change this plan's status only after the acceptance criteria pass.

## 10. Testing

Automated coverage now includes a maintained `Plan82CodeExploreFlowTests` fixture for direct bridge paths, call-site source projection, interface dispatch frontiers and branches, virtual override branches, direct/transitive dependent project and test impact, capped unresolved dynamic boundaries, source-policy denial, and defense-in-depth tool confinement of flow/branch/blast evidence.

Additional automated gates before completion should cover the remaining dispatch kinds and large-boundary cases where practical, plus architecture/provider/tool schema compatibility, serialization, cancellation/invalidation, and unchanged granular tool behavior. The user-testable checkpoint is [MTP-249](manual-test-plan.md#mtp-249--multi-anchor-semantic-flow-and-dispatch-branches). Its successful completion remains a blocking prerequisite for Plan 83.

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

## 17. Resolved Decisions

- Flow composition uses bounded pairwise directed searches among resolved source-bearing anchors, with deterministic ranking that prefers complete paths, shorter compiler-proven evidence, stable semantic identities, and usable source.
- Unnamed connector, depth, node, edge, path, dispatch-branch, blast-radius, file, source, and time budgets are explicit request limits validated by the tool adapter; traversal budgets are shared across pair searches within one request.
- Interface and virtual runtime ambiguity is represented by compiler-known dispatch-boundary edges plus separate bounded `CodeExploreDispatchBranch` entries; the result does not invent continuation edges to possible runtime targets.
- Named anchor source is projected before optional flow/impact expansion. Flow-spine declarations and operative call-site windows are added only while source budgets and path policy permit them.
- `Impact` evidence is compact planning context over callers, implementations, and direct/transitive dependent projects/tests; it is not exhaustive validation scope and does not authorize mutation or test selection.
