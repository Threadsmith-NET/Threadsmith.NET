# ADR-4: Roslyn + MSBuild as semantic sources of truth

> **Direct-editing amendment (2026-10-05):** The execution workflow portions of the original decision below are superseded as described in the amendment at the end of this document.

- **Status:** Accepted
- **Date:** 2026-07-31
- **Strategy source:** §6 (Technology Choices), §29 (ADR 7)
- **Validated by:** `spikes/Spike.MsBuildWorkspace` (plan-01 task 11)

## Context
The harness must be meaningfully compiler-aware: load solutions, resolve symbols, find references/implementations, classify generated/linked code, and (later) propose semantic mutations. Roslyn types must not leak across boundaries unless the consumer is explicitly compiler-aware (§8.1).

## Decision
Use **Roslyn** (`Microsoft.CodeAnalysis.*` 5.9.0) + **MSBuild** (`Microsoft.CodeAnalysis.Workspaces.MSBuild`) as the semantic sources of truth. `MSBuildLocator.RegisterDefaults()` must run before creating an `MSBuildWorkspace`. Roslyn object references are never persisted (§7.1).

## Consequences
- `Microsoft.Build.Locator` 1.11.2 is required to register the MSBuild host; `Microsoft.Build.Framework` must be excluded from runtime output (`ExcludeAssets="runtime"`) to avoid assembly-load conflicts.
- Roslyn/MSBuild APIs may be non-cooperatively cancellable. The implemented semantic path uses the plan-06/plan-12 abandon-and-discard pattern with a bounded-wait backstop (§13).
- `SemanticConfidenceLevel` (gap #2) will be encoded in plan-06.

## Validation
`Spike.MsBuildWorkspace` loads `src/Threadsmith.sln` and resolves `Threadsmith.App.Program` (type kind, namespace, assembly) → `PASS` (exit 0). See `spikes/Spike.MsBuildWorkspace/README.md` and `docs/architecture/spike-notes.md`.

Plan 117 revalidated this boundary with Roslyn 5.9.0 and the SDK 10.0.401-owned MSBuild 18.9.11 runtime. The validation covers solution and direct-project loading, C# 14 symbols, analyzers, source generators, generated-source queries, scripting isolation, cancellation, dependency closure, and runtime-assembly exclusion.

## Direct-editing amendment (2026-10-05)

The persistent Roslyn engine supplies versioned advisory candidate and committed diagnostics during ordinary editing. Operations retain the shared queue, cancellation and workspace lifetime owners; semantic coverage never grants write authority. See [the current conversation flow](../operations/conversation-loop.md), [mutation ownership](mutation-model.md), and [recovery contract](../operations/execution-resumption.md). The original decision remains historical architectural rationale.
