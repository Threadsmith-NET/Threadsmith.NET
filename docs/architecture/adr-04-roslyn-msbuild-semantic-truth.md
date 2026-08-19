# ADR-4: Roslyn + MSBuild as semantic sources of truth

- **Status:** Accepted
- **Date:** 2026-07-31
- **Strategy source:** §6 (Technology Choices), §29 (ADR 7)
- **Validated by:** `spikes/Spike.MsBuildWorkspace` (plan-01 task 11)

## Context
The harness must be meaningfully compiler-aware: load solutions, resolve symbols, find references/implementations, classify generated/linked code, and (later) propose semantic mutations. Roslyn types must not leak across boundaries unless the consumer is explicitly compiler-aware (§8.1).

## Decision
Use **Roslyn** (`Microsoft.CodeAnalysis.*` 5.6.0) + **MSBuild** (`Microsoft.CodeAnalysis.Workspaces.MSBuild`) as the semantic sources of truth. `MSBuildLocator.RegisterDefaults()` must run before creating an `MSBuildWorkspace`. Roslyn object references are never persisted (§7.1).

## Consequences
- `Microsoft.Build.Locator` 1.11.2 is required to register the MSBuild host; `Microsoft.Build.Framework` must be excluded from runtime output (`ExcludeAssets="runtime"`) to avoid assembly-load conflicts.
- **Open issue (gap #7):** Roslyn/MSBuild APIs may be non-cooperatively cancellable. plan-06/plan-12 must use the abandon-and-discard pattern with a bounded-wait backstop (§13).
- `SemanticConfidenceLevel` (gap #2) will be encoded in plan-06.

## Validation
`Spike.MsBuildWorkspace` loads `src/Threadsmith.sln` and resolves `Threadsmith.App.Program` (type kind, namespace, assembly) → `PASS` (exit 0). See `spikes/Spike.MsBuildWorkspace/README.md` and `docs/architecture/spike-notes.md`.