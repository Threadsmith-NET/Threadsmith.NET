# AGENTS.md — Threadsmith.NET

> **DOX root rail.** This is the behavioral contract for AI coding agents working in this repo. Child `AGENTS.md` files own domain subtrees. A closer doc controls local details, but **no child weakens a parent**. After any meaningful change, perform a DOX pass: review the applicable contracts and update them only when durable implementation details, guidance, ownership, or child indexes changed.

## Product

**Product name:** **Threadsmith.NET** — a .NET-native coding harness.
**Code/namespace prefix:** `Threadsmith.*` (the product name and the code prefix coexist without ambiguity).

The host owns control flow; the model is a pluggable reasoning engine, not an autonomous actor. The model proposes; the host validates, applies, builds, tests, and reports back. Nothing destructive happens without user approval.

## Baseline

- **Runtime:** .NET 10 LTS, C# (`<LangVersion>latest</LangVersion>`). ADR-1.
- **Nullable** is enabled solution-wide (`<Nullable>enable</Nullable>`). No `!` suppression (guardrail G-2).
- **Central Package Management:** all external package versions are pinned in `Directory.Packages.props`. Add packages there, not with inline versions.
- **Solution:** `src/Threadsmith.sln` (classic `.sln` format). Product projects live under `src/`; tests under `tests/`; throwaway spikes under `spikes/`.
- **EditorConfig:** the root `.editorconfig` owns repository-wide formatting, naming, modern C# preferences, and analyzer severities; `Directory.Build.props` enables build-time code-style enforcement, disables Roslyn shared compilation, and mirrors intentionally disabled StyleCop rules in `NoWarn` so clean parallel builds cannot fall back to analyzer defaults when analyzer-config severity data is missed; documented path-specific overrides may relax only rules that do not weaken the C# guardrails.
- **Contributor workflow:** root `CONTRIBUTING.md` owns public setup, Code of Conduct linkage, coding, testing, commit, and pull-request guidance and must remain consistent with this contract, CI, licensing, and the current repository layout.

## Architectural and planning sources

- **Implementation planning:** `docs/implementation-plans/planning-governance.md` owns planning-document authority, lifecycle, completed-contract freeze, maintenance-track routing, and minimal-update rules. `docs/implementation-plans/milestones.md` alone owns current milestone status; active implementation documents own their own status and prerequisites.
- **Implementation contract:** the template and agent instructions live in `docs/implementation-plans/00-shared-context.md` §G.
- **Architecture decisions:** `docs/architecture/` contains the repository-owned ADRs and architecture contracts. Plans must remain consistent with accepted ADRs, guardrails, and implemented contracts.

## C# guardrails — READ BEFORE WRITING C#

**Before writing or modifying any C#, read and follow `docs/guardrails/portable-csharp-guardrails.md` (G-1…G-31).** The guardrails file is authoritative.

## Binding working rules

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
- `Threadsmith.Tui` references no persistence implementations.
- External SDKs are isolated behind internal adapters.
- Terminal-library types never appear in core interfaces; Roslyn types don't leak across boundaries unless the consumer is explicitly compiler-aware.

## Repository configuration

- Repository configuration lives under `.threadsmith/config.*` and is **data, not code** — never execute it.
- Static secret stores stay outside ordinary configuration and resolve only at explicit privileged boundaries.
- Prompt append files are untrusted input: sanitized and bounded, never executed, never allowed to override host policy or guardrails, and referenced by id+version in execution records.
- `.threadsmith/AGENTS.md` owns detailed repository-configuration and prompt-append contracts.

## Licensing

- Threadsmith.NET is licensed under the Apache License 2.0.
- The root `LICENSE` file is the authoritative license text; keep the README and contributor guidance consistent with it.

## DOX workflow

- AGENTS.md files are binding work contracts for their subtrees. Root rules apply everywhere; closer child docs add local detail and control local conflicts, but no child may weaken a parent.
- Before editing, re-read the applicable DOX chain in the current session: root `AGENTS.md`, then every child `AGENTS.md` along each target path.
- Work products must remain understandable from the nearest applicable `AGENTS.md` plus its parents.
- After meaningful changes, do a DOX pass: update the nearest owning `AGENTS.md` and affected parent/child indexes only when durable purpose, ownership, structure, workflow, rules, constraints, artifacts, or child indexes changed.
- Keep DOX concise and operational: document stable contracts, not diary entries or planning progress; delete stale or contradictory text.
- Closeout: re-check changed paths against the DOX chain, run relevant verification, and report docs intentionally left unchanged.

## Child DOX Index

| Child | Scope |
|---|---|
| `src/AGENTS.md` | 21 product projects, dependency layers, adding new projects |
| `tests/AGENTS.md` | Architecture and milestone verification suites |
| `docs/AGENTS.md` | User guide, operations, ADRs, guardrails, testing docs, and implementation plans |
| `eng/AGENTS.md` | Repository build, development-tool staging, and release automation |
| `spikes/AGENTS.md` | Throwaway technology spikes, spike results |
| `.threadsmith/AGENTS.md` | Repository configuration, prompt-append files |