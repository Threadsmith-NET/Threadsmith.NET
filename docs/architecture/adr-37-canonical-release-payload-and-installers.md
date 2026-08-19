# ADR-37: Canonical Release Payload and Platform Installers

**Status:** Accepted

## Context

Threadsmith.App is not a complete deployment by itself. The optional scripting capability launches `Threadsmith.Scripting.Worker.dll` beside the application, startup scaffolding reads shipped configuration examples, and releases must include the repository license terms. The previous application project copied worker runtime files from a framework-dependent intermediate `bin` path, which does not define a reliable same-RID self-contained release.

Threadsmith also depends on reflection, collectible extension loading, serialization, Roslyn/MSBuild, MCP, and provider adapters. Trimming, Native AOT, or assuming a single-file main executable is therefore unsafe without separate compatibility proof.

## Decision

A repository-owned PowerShell entry point publishes `Threadsmith.App` and `Threadsmith.Scripting.Worker` explicitly for the same declared RID, Release configuration, and self-contained mode. It overlays those outputs into one canonical staged directory, adds configuration examples and authoritative license files, validates required files, and writes a SHA-256 staged-layout manifest.

The initial target matrix is `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`. Trimming, Native AOT, and single-file publishing are disabled.

Platform packages consume the canonical logical payload:

- Windows uses Inno Setup standalone setup executables unless an enterprise MSI requirement is approved later.
- macOS uses architecture-specific `.pkg` installers with explicit signed/notarized or unsigned-development state.
- Linux always provides architecture-specific `.tar.gz` archives with bounded install/uninstall scripts; distro-native packages are optional additions.

User-owned configuration, credentials, sessions, repositories, extensions, skills, and caches remain outside installer-owned locations and are not removed during upgrade or uninstall.

## Consequences

- Application and worker architecture/runtime identity can be verified together before native packaging.
- Installer implementations share one payload contract instead of independently assembling files.
- Release size is larger than trimmed, AOT, or single-file alternatives, but compatibility remains aligned with the tested runtime.
- Native packaging must run on the matching operating-system runner and preserve executable permissions.
- Signing and notarization remain optional for local development but must be represented honestly and verified when required for production.

## Amendment — Self-Contained Worker Launch

Each RID publish restores the requested runtime assets before publishing, and canonical staging requires the application and scripting-worker apphosts in addition to their managed runtime files. Runtime composition launches the colocated worker apphost directly so the self-contained payload does not depend on a separately installed `dotnet` muxer; `dotnet exec` of the worker DLL remains only a framework-dependent development/test fallback.

## Amendment — Pinned Ripgrep Search Runtime

Every canonical RID payload includes the matching official BurntSushi/ripgrep executable under `tools/`, selected from a repository-owned six-RID manifest that pins the upstream version, archive name, SHA-256 digest, source repository, and `MIT OR Unlicense` metadata. Release staging downloads the archive only at build time over HTTPS, rejects any digest mismatch before extraction, stages only the executable plus MIT/Unlicense provenance notices, records those files as the `ripgrep` component, and smoke-tests the native binary on an architecture-matching runner. Runtime search resolves this installer-owned app-local executable before using a development `PATH` fallback; repositories cannot select or replace it. Generated third-party archives and binaries remain outside Git history.
