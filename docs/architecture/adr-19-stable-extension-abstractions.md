# ADR-19: Stable Extension Abstractions Package

Status: Accepted
Date: 2026-08-04
Strategy: §17.4, §17.5–§17.8, §29 (item 12)

## Context

Extensions are unloadable plugins loaded into collectible `AssemblyLoadContext`s (ADR-20). The host
and every extension must agree on a single, stable set of contract types (`IThreadsmithExtension`,
`ICapability`, `IToolCapability`, manifest records, lifecycle states). If each extension bundles its
own copy of the contracts, type identity breaks: an `IThreadsmithExtension` loaded from the
extension's copy is a *different* type than the host's `IThreadsmithExtension`, and reflection
discovery finds nothing.

## Decision

Ship a single, versioned, stable abstractions package — `Threadsmith.Extensions.Abstractions` — that
is the only source of extension contract types.

- The package is **Layer 0**: it references no other `Threadsmith.*` project and no UI, Roslyn,
  terminal-library, or model-provider SDK types (§8.1).
- Extensions reference it with `PrivateAssets="all"` and `ExcludeAssets="runtime"` so the contract
  DLL is **never copied into an extension's output**. The extension loads the host's single shared
  copy from the default `AssemblyLoadContext` (ADR-21).
- A build-time MSBuild target (`build/Threadsmith.Extensions.Abstractions.targets`) fails the
  extension build when the contract DLL is detected in the extension output, closing the #1 ALC bug
  (gap #5).
- Contracts are string-/own-enum-based where they must stay free of host dependencies; capability
  contracts are independently defined here (not in `Threadsmith.Tools`) so the Layer-0 constraint
  holds.
- Contract versions are explicit (`ExtensionContractVersion`); breaking changes require a new
  contract version and a host-side compatibility gate.

## Consequences

- One copy of every contract type in the process; reflection discovery and type identity work.
- Extensions cannot accidentally pin their own ALC by bundling the contracts (ADR-21, ADR-20).
- The abstractions package is a stability-critical surface: changes require versioning discipline
  and are gated by the architecture tests (`DependencyDirectionTests`).

## Alternatives considered

- Bundling contracts per extension: rejected — breaks type identity and prevents unload.
- Defining capability contracts in `Threadsmith.Tools`: rejected — would force Abstractions to
  reference Tools, violating Layer 0.