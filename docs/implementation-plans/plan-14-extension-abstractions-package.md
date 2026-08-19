# Implementation Plan 14: Extension Abstractions Package

**Milestone:** M7 — Extension SDK and Runtime
**Strategy source:** §17.4 (Stable Extension Contract Assembly), §17.5–§17.8 (interfaces + manifest), §17.9 (packaging), §17.25 (authoring SDK), §29 (ADRs 12, 14, 16), §36 (no raw Terminal.Gui views), assessment gap #5
**Prerequisite plans:** plan-02 (ids: `ExtensionId`, `ExtensionGenerationId`, `CapabilityId`), plan-08 (tool contracts that capabilities mirror)

## 1. Objective
Deliver `Threadsmith.Extensions.Abstractions` — the small, stable, versioned contract package extensions depend on — with the primary extension interface, capability contracts, an optional manifest schema, packaging conventions, and the **abstractions-package reference convention + authoring analyzer** (gap #5) that prevents the duplicate-contract-assembly bug.

## 2. Architectural Context
Parent: Foundation → Extension abstractions (§28). This package is referenced by *extensions* (not `Threadsmith.Extensions.Runtime`). It must stay small and stable (§8.1). The single biggest ALC failure — a second copy of the contract assembly loaded into the extension's collectible context (§17.11, §17.18) — is **prevented here** via the reference convention + analyzer (gap #5). Read `00-shared-context.md` §D (§8.1) + §H (gap #5) before starting.

## 3. Scope
- `Threadsmith.Extensions.Abstractions` package: `IThreadsmithExtension`, capability contracts (mirror plan-08 tool contracts where practical — §8.1), lifecycle interfaces, manifest schema (§17.8), activation + DI hooks (§17.12), invocation lease contracts (§17.15), extension configuration + permissions (§17.22, §17.23).
- Package versioning + compatibility (§17.4).
- Packaging convention (§17.9): extensions are NuGet packages or drop-in DLLs.
- **Abstractions reference convention (gap #5):** the abstractions package is referenced by extensions with `PrivateAssets="all"` + `ExcludeAssets="runtime"` (or equivalent) so it is **not copied into the extension package**, ensuring the extension loads the host's single copy from the default ALC.
- **Authoring analyzer (gap #5):** a Roslyn analyzer that flags a non-shared reference to the contract assembly in an extension project.
- Authoring SDK: project templates + samples (§17.25).
- **No raw terminal-library views from extensions (§18.12, ADR 16, §36):** capabilities return DTOs, never UI-library controls.

## 4. Non-Scope
- No runtime (plan-15): no ALC, no loading, no reflection discovery. No unload (plan-17). No MCP (plan-19).

## 5. Current State
plan-02 provides `ExtensionId`/`ExtensionGenerationId`/`CapabilityId` + events. plan-08 provides tool contracts to mirror. `Threadsmith.Extensions.Abstractions` is an empty project.

## 6. Proposed Design
- Interfaces only; no implementation. Stable versioning with a compatibility contract (§17.4) — minor additions allowed, breaking changes require a new contract package generation.
- Capability contracts mirror plan-08 `ITool` so a built-in tool and an extension-provided tool are indistinguishable to the runtime (§8.1 "same capability contracts where practical").
- Manifest (§17.8) is optional but, when present, declares capabilities, permissions, contract version.
- The reference convention + analyzer are the **prevention**; plan-17's leak fixture is the **detection**. Both are required.

## 7. Public Contracts
- `IThreadsmithExtension` (§17.5), `IExtensionLifecycle` (§17.12), `ICapability` (§17.13), `IInvocationLease` (§17.15).
- `ExtensionManifest` schema (§17.8).
- `ExtensionConfiguration`, `ExtensionPermissions` (§17.22, §17.23).
- Abstractions package version + compatibility contract (§17.4).
- **`IModelPreferenceContributor`** (a capability kind per §17.13): lets a skill/extension express **advisory** model preferences over the host's *configured* model list — a `ModelPreferenceHint` carries a `workloadClass` (plan-07 `WorkloadClass`), a preferred `ModelProfileId` or constraints (min context window, required capabilities, max cost-per-Mtoken), a `Priority` (int, higher wins; ties broken by host default), and a rationale string. **Advisory only:** the contributor returns hints; it never receives keys, endpoints, or arbitrary provider config, and the host (plan-09 + plan-07 `IModelSelectionPolicy`) makes the final pick. Multiple contributors' hints are aggregated by the host. This is the contract plan-16 registers and plan-09 consumes.

## 8. Project and File Changes
- `Threadsmith.Extensions.Abstractions/`: interfaces, manifest schema, capability contracts.
- `Threadsmith.Extensions.Abstractions.Analyzers/`: the authoring analyzer (gap #5).
- `samples/extensions/MinimalToolExtension/`: template (§17.25).
- `docs/extension-authoring/`: packaging + reference convention.

## 9. Ordered Implementation Tasks
1. `IThreadsmithExtension` + lifecycle (§17.5, §17.12).
2. Capability contracts mirroring plan-08 (§17.13, §8.1).
3. Invocation lease contract (§17.15).
4. Manifest schema (§17.8).
5. Configuration + permissions contracts (§17.22, §17.23).
6. **`IModelPreferenceContributor` capability contract** + `ModelPreferenceHint` DTO (references plan-07 `WorkloadClass`/`ModelProfileId`; advisory only; no keys/endpoints).
7. Package versioning + compatibility policy (§17.4).
8. **Reference convention (gap #5):** document + encode `PrivateAssets`/`ExcludeAssets` in the template `.csproj`.
9. **Authoring analyzer (gap #5):** flag non-shared contract-assembly references in extension projects.
10. Minimal-tool-extension template + sample (§17.25), incl. a sample `IModelPreferenceContributor`.
11. Document "no raw terminal-library views" (ADR 16, §18.12, §36).
12. ADRs 12 (stable abstractions), 14 (shared contract resolution from default context), 16 (no raw TUI views from extensions) finalized.

## 10. Testing
- Abstractions package compiles standalone (no dependency on `Threadsmith.Extensions.Runtime` — architecture test).
- Analyzer: an extension project that copies the contract assembly locally → flagged.
- Template extension builds with the reference convention → the output package does **not** contain the abstractions DLL (the #1 ALC bug precondition).
- Capability contract parity: an extension tool and a built-in tool are invokable through the same surface (forward reference to plan-16).

## 11. Security and Permissions
- Permissions declared in the manifest (§17.23) are the extension's *request*; the host grants/denies (plan-16).

## 12. Observability
N/A (interfaces only).

## 13. Migration and Compatibility
- Compatibility policy (§17.4) is the contract: additive changes in minor; breaking → new package generation (ties to plan-17 hot replacement).

## 14. Acceptance Criteria
- Abstractions package small, stable, standalone.
- Reference convention + analyzer prevent the duplicate-contract-assembly bug (gap #5).
- Template extension's output package excludes the abstractions DLL.
- No terminal-library type in any capability contract (architecture test; ADR 16).

## 15. Risks and Mitigations
- **Duplicate contract assembly (§17.11, §17.18, gap #5):** prevention here (convention + analyzer) + detection in plan-17 (leak fixture). Both required.
- **Abstractions drift from plan-08 tool contracts:** mirror at authoring; keep a contract-parity test.

## 16. Documentation
- ADRs 12, 14, 16.
- `docs/extension-authoring/getting-started.md`, `docs/extension-authoring/packaging.md` (incl. reference convention).

## 17. Open Decisions
- Exact `PrivateAssets`/`ExcludeAssets` combination (gap #5) — validate with a spike that the extension loads the host's copy from the default ALC; record in ADR 14.
- Whether manifest is JSON or YAML — recommend JSON for schema validation tooling.
