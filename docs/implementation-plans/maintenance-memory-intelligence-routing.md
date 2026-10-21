# Memory and repository intelligence routing

**Status:** Implementation delivered; captured-runtime evaluation and promotion remain pending.
**Delivery track:** Maintenance.
**Prerequisites:** Shared memory search, standing-preference required-context admission, native concept transport, repository-intelligence ownership/monitoring, and deployed prompt assets.

## 1 Objective

Implement the supplied memory/intelligence plan: independent automatic intelligence routing, explicit intelligence search, and opt-in situational concept eligibility.

## 2 Architectural context

Reuse `HybridRepositoryMemoryRetriever`, `IRepositoryMemorySearch`, `MemoriesTool`, existing tool admission, context assembly, and repository settings. Preserve completed memory and intelligence capability contracts.

## 3 Scope

Backward-compatible settings, bounded search, instruction provenance, Off/Shadow/Enforce, diagnostics, regression tests, rollout and rollback documentation.

## 4 Non-scope

No scoring promotion, semantic intent inference, retagging, embedding migration, new collection flow, catalog, or automatic pre-edit hook.

## 5 Current state

Baseline inspected at `38ec9967bc41bb0477d7c92ba482a291c415a7ba`, using .NET SDK 10.0.402 in `C:\source\repos\Threadsmith`. The compiled registered tool is `memories`; its pre-change schema had no query search. Local repository configuration has intelligence explicitly disabled and no new recall setting. It was not changed. External installed extension implementations were not inspected.

Automatic entry points converge through `SessionApplication.RefreshMemoryContextAsync`, `ContextAssembler`, and `HybridRepositoryMemoryRetriever.RetrieveAsync`. Standing preferences already have separate required-context handling. Direct reconciliation uses `SearchAsync` with independent options.

## 6 Proposed design

The implemented design gates automatic intelligence before search. Explicit search uses the same backend with embedding maintenance disabled. A run-owned intent state captures normalized admitted-tool hints after each batch; steering/repository changes advance its epoch. Eligibility runs on discovered ordinary situational candidates before CE allocation. Shadow retains baseline selection; Enforce requires exact matching only when provenance is usable.

## 7 Public contracts

`Intelligence.AutomaticRecallEnabled` defaults true for compatibility. `Recall.SituationalConceptFilterMode` defaults Off; invalid values produce an Off fallback diagnostic. Existing `Concepts.Enabled` remains discovery-only. `memories` adds `search(query, ownership: intelligence)` with bounded PascalCase results and explicit availability/completeness outcomes. See the [operator contract](../operations/memory-intelligence-routing.md).

## 8 Project/file changes

Core owns the host DTOs and bounded intent state. Context owns eligibility placement, cache identity and diagnostics. Execution transports epochs and snapshots. Tools binds settings and extends the existing tool. App composition supplies the existing search service. Prompt inventories describe the new guidance with unchanged tokens.

## 9 Ordered tasks

Route separation, tool discoverability, provenance, shadow decisions, enforcement and reversible configuration are implemented. No production repository was opted into enforcement. Captured-runtime evaluation and default promotion remain outstanding.

## 10 Testing

Regression suites cover required preferences in assembled context, automatic intelligence omission, Off/Shadow context equality, exact/nonmatch/untagged/no-current decisions, typo-only rejection, incomplete-comparison safety, retained selection, bounded epochs, cross-repository/run isolation, config fallback/round trips, tool bounds/availability/cancellation, provider schema transport, native admission and next-round concept supply. Existing reconciliation tests remain in the context suite.

Validation in the active checkout:

| Command | Result |
|---|---|
| `dotnet build src/Threadsmith.App/Threadsmith.App.csproj --no-restore -v quiet` | Passed, zero warnings/errors |
| `dotnet test --project tests/Threadsmith.ConversationContext.Tests --no-restore` | 249 passed |
| `dotnet test --project tests/Threadsmith.ModelTooling.Tests --no-restore` | 1,126 passed, 22 skipped |
| `dotnet test --project tests/Threadsmith.ExecutionOrchestration.Tests --no-restore` | 19 passed |
| `dotnet test --project tests/Threadsmith.Architecture.Tests --no-restore` | 387 passed, 1 skipped |
| `git diff --check` and planning-governance searches | Passed |

Skipped checks require Windows link privileges, optional performance/SDK/image fixtures or live-provider opt-in; they are not counted as passes. Captured-runtime quality evaluation and provider-backed manual procedures were not run.

## 11 Security/permissions

Search accepts no repository selector, rejects ordinary ownership and validates returned ownership/repository. Existing policy remains authoritative, including archeology's list-only restriction. No collection bypass, hidden writes, vectors or tool arguments are exposed.

## 12 Observability

Bounded candidate evidence carries policy reason codes, mode/version, epoch, concept count, mixed-origin state, and exact/spelling-assisted discovery. Existing search diagnostics retain elapsed time and completeness. Full arguments are not logged for provenance. Shadow collects runtime evidence independently but evaluates decisions at baseline assembly boundaries, avoiding additional searches and provider-history rewrites just for instrumentation.

## 13 Migration/compatibility

Missing automatic recall retains legacy behavior; explicit false survives activation and publication. Rollback requires only true/Off settings. Standing preferences, scorer defaults, CE mechanics, reconciliation, collection and durable memory data are unchanged.

## 14 Acceptance criteria

See Scenario MEMORY-ROUTING and MTP-MEMORY-ROUTING. Broad activation additionally requires the supplied plan's held-out quality gate; branch tests cannot satisfy it.

## 15 Risks

Initial rounds have no tool-derived intent. Multiple different tool hint sets conservatively fall back. Real concept coverage and recall/latency tradeoffs are not established. The reused backend still reads a snapshot and performs corpus work; no indexed-catalog performance claim is made.

## 16 Documentation

[Routing, migration, rollback and evaluation limits](../operations/memory-intelligence-routing.md), [on-demand configuration](../../samples/configuration/memory-intelligence-on-demand.json), prompt inventories, acceptance scenario and manual procedure.

## 17 Open decisions

No captured runtime dataset, frozen relevance judgments, family split or reviewed useful-recall/latency bounds was supplied. Real quality metrics and offline hypothetical-ranking replay remain unevaluated. Keep Off as the production default until that evidence is available and reviewed.
