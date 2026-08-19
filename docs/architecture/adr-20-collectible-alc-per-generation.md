# ADR-20: One Collectible AssemblyLoadContext per Extension Generation

Status: Accepted
Date: 2026-08-04
Strategy: §17.2, §17.10, §17.16, §29 (item 13)

## Context

Extensions must be isolatable, replaceable without a process restart (hot replacement, §17.20), and
unloadable so their assemblies can be reclaimed (§17.17, §17.19). .NET offers `AppDomain` and
`AssemblyLoadContext` (ALC) for isolation. `AppDomain` is not supported for unloading on .NET Core+
and is heavy; collectible ALCs are the modern, supported unload mechanism.

## Decision

Load each extension *generation* into its own **collectible `AssemblyLoadContext`**
(`ExtensionLoadContext`, `isCollectible: true`).

- One ALC per generation, not per extension id: a hot replacement creates a fresh ALC for the new
  generation while the old generation drains and unloads (ADR-24).
- The ALC uses `AssemblyDependencyResolver` to resolve the extension's private dependencies from its
  shadow-copied staging directory; shared contracts resolve from `Default` (ADR-21).
- An `AssemblyLoadContext` is an isolation/unload mechanism, **not a security boundary** (§17.24,
  §36). Trusted extensions run in-process; untrusted extensions are refused or require a future
  out-of-process host (ADR-22).
- The lifecycle state machine (Discovered → Validating → Loading → Activating → Active → Draining →
  Deactivating → Unloading → Unloaded / UnloadBlocked) gates every transition (§17.16).
- A `WeakReference` to the ALC is retained for unload verification (§17.19, ADR-24).

## Consequences

- Conflicting private dependency versions coexist across extensions (each ALC resolves its own
  copy).
- Unload is cooperative and verifiable; retained references are diagnosed, not hidden (ADR-24).
- Defense-in-depth resource bounds (per-extension invocation budget, §22.2) remain necessary because
  the ALC is not a security boundary.

## Alternatives considered

- `AppDomain`: rejected — not unloadable on .NET 10, heavyweight.
- A single shared ALC for all extensions: rejected — no isolation, no per-extension unload, version
  conflicts.