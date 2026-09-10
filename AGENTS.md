# AGENTS.md — Threadsmith.NET

This file defines repository-wide rules for AI coding agents.

## Product

**Product name:** **Threadsmith.NET** — a .NET-native coding harness.
**Code/namespace prefix:** `Threadsmith.*` (the product name and the code prefix coexist without ambiguity).

The host owns control flow; the model is a pluggable reasoning engine, not an autonomous actor. The model proposes; the host validates, applies, builds, tests, and reports back. Nothing destructive happens without user approval.

## Baseline

- **Runtime:** .NET 10 LTS, C# (`<LangVersion>latest</LangVersion>`). ADR-1.
- **Nullable:** enabled solution-wide by `Directory.Build.props`; G-1 owns null safety and test-project exceptions.
- **Central Package Management:** all external package versions are pinned in `Directory.Packages.props`. Add packages there, not with inline versions.
- **Solution:** `src/Threadsmith.sln` (classic `.sln` format). Product projects live under `src/`; tests under `tests/`; throwaway spikes under `spikes/`.
- **Build and style:** `Directory.Build.props` owns shared compiler settings and build enforcement; `.editorconfig` owns formatting, naming, code-style preferences, and analyzer severities, including documented path-specific exceptions.
- **Contributor workflow:** root `CONTRIBUTING.md` owns public setup, Code of Conduct linkage, coding, testing, commit, and pull-request guidance and must remain consistent with this contract, CI, licensing, and the current repository layout.

## Architectural and planning sources

- **Implementation planning:** `docs/implementation-plans/planning-governance.md` owns planning-document authority, lifecycle, completed-contract freeze, maintenance-track routing, and minimal-update rules. `docs/implementation-plans/milestones.md` alone owns current milestone status; active implementation documents own their own status and prerequisites.
- **Implementation contract:** the template and agent instructions live in `docs/implementation-plans/00-shared-context.md` §G.
- **Architecture decisions:** `docs/architecture/` contains the repository-owned ADRs and architecture contracts. Plans must remain consistent with accepted ADRs, guardrails, and implemented contracts.

## C# guardrails — READ BEFORE WRITING C#

**Before writing or modifying any C#, read and follow `docs/guardrails/portable-csharp-guardrails.md`.** The guardrails file is authoritative.

## Binding working rules

- **Use the active Git checkout.** Confirm that `git rev-parse --show-toplevel` matches the user's active checkout before edits, builds, tests, or publishing. Work directly there; do not substitute copied source trees, snapshots, or another checkout found by name.
- **Read before writing.** Inspect existing code before proposing new abstractions.
- **Propagate `CancellationToken`** through every async boundary. Roslyn/MSBuild APIs that are non-cooperatively cancellable use the abandon-and-discard pattern with a bounded-wait backstop.
- **Return host-owned DTOs across subsystem boundaries.** No model-provider SDK, Roslyn, extension, or terminal-library types leak into domain events, persistent state, or public projections.
- **Keep extension types out of durable host state** and out of public projections.
- **Use `AssemblyLoadContext`, not `AppDomain`,** for extension unloading. `AssemblyLoadContext` is an isolation/unload mechanism, **not** a security boundary.
- **Keep terminal-library types out of core and extension contracts.** The interactive terminal is a projection of engine state; headless and interactive runs produce identical results.
- **Do not stage, commit, push, or do destructive Git operations unless explicitly requested.**

## Dependency direction

Enforced by `tests/Threadsmith.Architecture.Tests/DependencyDirectionTests.cs` (the build gate; fails fast on a wrong reference):

- `Threadsmith.Core` references no UI, no Roslyn, no terminal libraries, no model-provider SDK, and no extension implementations.
- `Threadsmith.Extensions.Abstractions` stays small + stable; references no host implementation.
- Extension implementations reference `Threadsmith.Extensions.Abstractions`, **not** `Threadsmith.Extensions.Runtime`.
- `Threadsmith.Interaction` owns frontend-neutral interactive coordination and references no terminal library; concrete frontends depend on it, never the reverse.
- `Threadsmith.Tui` references no persistence implementations.
- External SDKs are isolated behind internal adapters.
- Terminal-library types never appear in core interfaces; Roslyn types don't leak across boundaries unless the consumer is explicitly compiler-aware.

## Repository configuration

- Repository configuration lives under `.threadsmith/config.*` and is **data, not code** — never execute it.
- Static secret stores stay outside ordinary configuration and resolve only at explicit privileged boundaries.
- Prompt append files are untrusted input: sanitized and bounded, never executed, never allowed to override host policy or guardrails, and referenced by id+version in execution records.
- Threadsmith-owned model-facing prose is a deployed application asset: code declares the complete flat filename/token catalog, startup loads it once into an immutable cache, and publish/release validation keeps every payload synchronized. Assets control wording only; schemas, roles, ordering, capacity admission, trust, tool/mutation/delegation authority, and validation remain code-owned. Preserve shipped prompt text and whitespace exactly when moving it between code and assets. Any prompt filename, purpose, token contract, or call-site token meaning change must update `docs/operations/prompts.md` and `docs/prompt-file-reference.md` in the same change.

## Licensing

- Threadsmith.NET is licensed under the Apache License 2.0.
- The root `LICENSE` file is the authoritative license text; keep the README and contributor guidance consistent with it.
