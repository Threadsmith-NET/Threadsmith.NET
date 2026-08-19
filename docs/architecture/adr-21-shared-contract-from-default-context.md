# ADR-21: Shared Contract Assembly Resolution from the Default Context

Status: Accepted
Date: 2026-08-04
Strategy: §17.11, §17.14, §29 (item 14); gap #5

## Context

The #1 `AssemblyLoadContext` bug is an extension bundling its own copy of the shared contract
assembly (`Threadsmith.Extensions.Abstractions`). When the extension's ALC loads that bundled copy,
there are two `IThreadsmithExtension` types in the process with the same name but different
identities; reflection discovery fails and the extension's ALC can never unload because the host
holds the contract type from `Default`.

## Decision

The extension ALC's `Load` override resolves the shared contract assembly from
`AssemblyLoadContext.Default` and **never** from the extension's directory.

- `ExtensionLoadContext.Load` returns `null` for the shared contract name, deferring to `Default`,
  which holds the host's single copy.
- Before deferring, the resolver probes for a *bundled* copy; if one exists, the load is rejected
  with `DuplicateContractAssemblyException` (fail fast, §17.11).
- The authoring guard (ADR-19) prevents the bundled copy at build time; the runtime check is the
  defense-in-depth backstop for extensions built without the guard.
- The exact reference convention validated by the `MinimalToolExtension` sample is
  `PrivateAssets="all"` + `ExcludeAssets="runtime"`, confirmed to keep the contract DLL out of the
  extension output and load the host's copy from `Default`.

## Consequences

- Exactly one contract assembly in the process; type identity is consistent across the host and all
  extensions.
- Extensions built without the guard are rejected at load with an actionable error rather than
  silently breaking.
- Unload is not defeated by a second contract copy (ADR-20).

## Alternatives considered

- Loading whatever copy the resolver finds: rejected — the root cause of the #1 ALC bug.
- Renaming the contract assembly per extension: rejected — destroys type identity.