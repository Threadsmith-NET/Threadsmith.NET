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

**Before writing or modifying any C#, read `docs/guardrails/portable-csharp-guardrails.md` and follow it (G-1…G-31).**

Highest-signal rules (quick reference — the guardrails file is authoritative):

- **G-1** Nullable-aware code; no "possible null reference" warnings; no `!` suppression — prefer an explicit upstream null check.
- **G-2** Argument validation via `ArgumentNullException.ThrowIfNull(...)` / `ArgumentException.ThrowIfNullOrWhiteSpace(...)`, not manual `if (x == null) throw` blocks.
- **G-4** `record` for data / DTOs / value objects; `class` for services and behaviour.
- **G-13** Async methods end in `Async`; `CancellationToken` is the last parameter (default `default`); no `async void`; return `Task.CompletedTask`/`Task.FromResult(...)` when no async work is done.
- **G-18** XML doc comments (`/// <summary>`) on all public members.
- **G-20** Throw at the boundary; log at the catch site. Never swallow exceptions silently.
- **G-21** Constructor injection only — no property injection.
- **G-22** Inject multi-registration collections as `IEnumerable<T>`, not `List<T>`/`T[]`.
- **G-10** No single-use abstractions - extract only when called from ≥2 sites or when granularity improves readability or testability.
- **G-12** Existing patterns take precedence — follow the codebase's precedent; do not introduce foreign patterns.

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

A repository may configure Threadsmith.NET via `.threadsmith/config.*` (ordinary layered precedence: compiled defaults → machine → user → repo → session → CLI → env) using `Microsoft.Extensions.Configuration`. Static secret stores stay outside this graph and resolve only at explicit privileged boundaries through the host-owned environment → eligible repository → user providers. See `.threadsmith/config.example` for every supported key.

### Project-level system-prompt-append option

A repo may place **prompt append files** under `.threadsmith/prompts/` and reference them from `.threadsmith/config.*` via the `prompt append files` key. These are appended to the model's system prompt at request-assembly time.

**Repo-provided append content is untrusted input:**
- Sanitized and bounded; never executed as code.
- Never allowed to override host policy or these guardrails.
- Versioned and referenced by id+version in execution records.

Repo config is **data, not code** — never execute it.

## Licensing

- Threadsmith.NET is licensed under the Apache License 2.0.
- The root `LICENSE` file is the authoritative license text; keep the README and contributor guidance consistent with it.

## DOX framework

- DOX is highly performant AGENTS.md hierarchy installed here
- Agent must follow DOX instructions across any edits

## Core Contract

- AGENTS.md files are binding work contracts for their subtrees
- Work products, source materials, instructions, records, assets, and durable docs must stay understandable from the nearest applicable AGENTS.md plus every parent AGENTS.md above it

## Read Before Editing

1. Read the root AGENTS.md
2. Identify every file or folder you expect to touch
3. Walk from the repository root to each target path
4. Read every AGENTS.md found along each route
5. If a parent AGENTS.md lists a child AGENTS.md whose scope contains the path, read that child and continue from there
6. Use the nearest AGENTS.md as the local contract and parent docs for repo-wide rules
7. If docs conflict, the closer doc controls local work details, but no child doc may weaken DOX

Do not rely on memory. Re-read the applicable DOX chain in the current session before editing.

## Update After Editing

Every meaningful change requires a DOX pass before the task is done.

Update the closest owning AGENTS.md when a change affects:

- purpose, scope, ownership, or responsibilities
- durable structure, contracts, workflows, or operating rules
- required inputs, outputs, permissions, constraints, side effects, or artifacts
- user preferences about behavior, communication, process, organization, or quality
- AGENTS.md creation, deletion, move, rename, or index contents

Update parent docs when parent-level structure, ownership, workflow, or child index changes. Update child docs when parent changes alter local rules. Remove stale or contradictory text immediately. Small edits that do not change behavior or contracts may leave docs unchanged, but the DOX pass still must happen.

## Hierarchy

- Root AGENTS.md is the DOX rail: project-wide instructions, global preferences, durable workflow rules, and the top-level Child DOX Index
- Child AGENTS.md files own domain-specific instructions and their own Child DOX Index
- Each parent explains what its direct children cover and what stays owned by the parent
- The closer a doc is to the work, the more specific and practical it must be

## Child Doc Shape

- Create a child AGENTS.md when a folder becomes a durable boundary with its own purpose, rules, responsibilities, workflow, materials, or quality standards
- Work Guidance must reflect the current standards of the project or user instructions; if there are no specific standards or instructions yet, leave it empty
- Verification must reflect an existing check; if no verification framework exists yet, leave it empty and update it when one exists

Default section order:
- Purpose
- Ownership
- Local Contracts
- Work Guidance
- Verification
- Child DOX Index

## Style

- Keep docs concise, current, and operational
- Document stable contracts, not diary entries
- Do not record plan or milestone progress, completion history, or work performed in `AGENTS.md`; planning documents own those records
- Put broad rules in parent docs and concrete details in child docs
- Prefer direct bullets with explicit names
- Do not duplicate rules across many files unless each scope needs a local version
- Delete stale notes instead of explaining history
- Trim obvious statements, repeated rules, misplaced detail, and warnings for risks that no longer exist

## Closeout

1. Re-check changed paths against the DOX chain
2. Update nearest owning docs and any affected parents or children
3. Refresh every affected Child DOX Index
4. Remove stale or contradictory text
5. Run existing verification when relevant
6. Report any docs intentionally left unchanged and why

## Child DOX Index

| Child | Scope |
|---|---|
| `src/AGENTS.md` | 21 product projects, dependency layers, adding new projects |
| `tests/AGENTS.md` | Architecture and milestone verification suites |
| `docs/AGENTS.md` | User guide, operations, ADRs, guardrails, testing docs, and implementation plans |
| `eng/AGENTS.md` | Repository build, development-tool staging, and release automation |
| `spikes/AGENTS.md` | Throwaway technology spikes, spike results |
| `.threadsmith/AGENTS.md` | Repository configuration, prompt-append files |