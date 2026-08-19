# Implementation Plan 01: Repository Bootstrap and Architecture Enforcement

**Product name:** **Threadsmith.NET** — a .NET-native coding harness. This plan bootstraps the repo that will become Threadsmith.NET.
**Milestone:** M0 — Architecture Spikes and Repository Bootstrap
**Strategy source:** §27 (M0), §28 (Foundation), §8 (solution organization), §8.1 (dependency rules), §29 (ADRs 1–6), §30 (risks)

## 1. Objective
Stand up the solution scaffold, central package management, static-analysis + dependency-direction enforcement, and CI — and validate the five highest-risk technology choices via bounded spikes — so every later plan builds on a known-good foundation. The product being bootstrapped is **Threadsmith.NET**.

## 2. Architectural Context
Parent subsystem: Foundation (§28 DAG root). This plan produces no business logic; it produces the project graph, the build/test toolchain, and evidence that Terminal.Gui v2, `MSBuildWorkspace`, OpenAI-compatible streaming, collectible `AssemblyLoadContext`, SQLite, and process-tree cancellation all behave as the architecture assumes. Read `00-shared-context.md` §C–§D before starting.

## 3. Scope
- Solution + project layout per §8 (`src/ Threadsmith.App … Threadsmith.Mcp`, `tests/`, `samples/`, `docs/`).
- Central Package Management (`Directory.Packages.props`) with all external packages pinned per §6.
- Code analyzers + style enforcement (stylecop, analyzer ruleset) aligned to repo guardrails.
- **Architecture tests** that enforce §8.1 dependency direction (e.g., `Threadsmith.Core` references no UI/Roslyn/SDK; `Threadsmith.Tui` references no persistence impl; abstractions stays small).
- CI on target platforms (Windows + Linux at minimum).
- Bounded spikes (throwaway projects under `spikes/`): Terminal.Gui v2 instance lifecycle; `MSBuildWorkspace` load + symbol find; OpenAI-compatible streaming; collectible ALC load/unload; SQLite event write/restore; process-tree cancellation.

## 4. Non-Scope
- No domain types beyond the minimal identifiers needed to compile the spike harness (those land in plan-02).
- No TUI screens, no tool runtime, no model abstraction — only spikes that prove the tech.
- No persistence schema design (plan-18).

## 5. Current State
Implemented. `src/Threadsmith.sln` contains the 16 product projects with central package management, solution-wide nullable/analyzer enforcement, architecture tests, and Windows/Linux CI. The bounded spikes record MSBuildWorkspace, streaming, SQLite, collectible ALC, process cancellation, and Terminal.Gui findings; ADR-9 supersedes the original v2 UI choice with Terminal.Gui v1.19 after interactive validation.

## 6. Proposed Design
- One `.sln` under `src/`, projects per §8 (collapse closely related ones per §8.2 if it simplifies the spike phase, but keep namespaces + dependency boundaries intact).
- CPM via `Directory.Packages.props`; a single `Directory.Build.props` sets `Nullable` enable, `ImplicitUsings`, analyzers, target framework.
- Architecture tests use a Roslyn-based or reflection-based dependency checker that asserts the §8.1 rules fail the build on violation.
- Each spike is a console project with a `PASS`/`FAIL` exit and a short notes file recording observed behavior, version numbers, and any deviations from assumptions (feeds the ADRs).

## 7. Public Contracts
- The solution + project structure (§8). No public APIs yet.
- ADRs 1–6 (§29) drafted from spike results: .NET 10 LTS target; Terminal.Gui v2; SQLite + artifact files; Roslyn + MSBuild as semantic truth; (UI-as-projection and event-oriented session model are design ADRs, recorded here but proven by later plans).

## 8. Project and File Changes
- `src/Threadsmith.sln` (the Threadsmith.NET solution; project prefix remains `Threadsmith.*` per §8), `Directory.Build.props`, `Directory.Packages.props`
- All `src/Threadsmith.*` projects (csproj + empty namespaces, per §8) — these compose the Threadsmith.NET product
- `tests/Threadsmith.Architecture.Tests/` (dependency-direction tests)
- `spikes/` (6 throwaway spike projects + notes)
- CI pipeline definition
- `docs/architecture/` seeded with ADRs 1–6
- **Root `AGENTS.md`** — the behavioral contract for AI coding agents working in this repo (see task 6). States the product name is **Threadsmith.NET**.
- `docs/guardrails/portable-csharp-guardrails.md` — the portable C# guardrails copied from the Inference repo's guardrails (source: `.inbox/dotnet-native-coding-harness-implementation-plans/portable-csharp-guardrails.md`), referenced by `AGENTS.md`
- **Repository configuration schema** including the **project-level system-prompt-append option** (§21.2 "Prompt append files") — see task 7
- `.threadsmith/` repo-config layout (config file + `prompts/` directory for append files) — see task 7

## 9. Ordered Implementation Tasks
1. Create `.sln` + `Directory.Build.props` (Nullable, analyzers, TFM).
2. Add `Directory.Packages.props` with pinned versions (§6 table).
3. Scaffold the §8 projects with minimal `.csproj`s and correct `ProjectReference` directions.
4. Add analyzer ruleset + editorconfig aligned to repo guardrails.
5. Write architecture-direction tests (§8.1) — these should pass on the empty scaffold and fail if a wrong reference is added.
6. **Bootstrap root `AGENTS.md`** — the DOX root contract for AI agents in this repo. It must, at minimum:
   - **State the product name is Threadsmith.NET** and that the project prefix `Threadsmith.*` is the code/namespace prefix (so the product name and the code prefix coexist without ambiguity).
   - State the repo purpose + .NET 10 LTS / C# baseline + that `Nullable` is enabled solution-wide.
   - Point at the strategy document as the architectural source of truth and at this implementation-plan package (`docs/implementation-plans/`, copied from `.inbox/dotnet-native-coding-harness-implementation-plans/`) as the sequenced work breakdown.
   - Instruct agents to **read `docs/guardrails/portable-csharp-guardrails.md` before writing or modifying any C#** and to follow it (G-1…G-29). Inline the highest-signal rules as a quick reference: nullable-aware code (G-1), no `!` suppression (G-2), `record` for data / `class` for behaviour (G-4), constructor injection only (G-21), `IEnumerable<T>` for multi-reg (G-22), async methods end in `Async` + `CancellationToken` last + no `async void` (G-13), no single-use abstractions (G-10), existing patterns win (G-12), XML docs on all public members (G-18), throw-at-boundary/log-at-catch (G-20).
   - State the binding working rules: read before writing; inspect existing code before proposing abstractions; propagate `CancellationToken`; return host-owned DTOs across subsystem boundaries (no SDK/Roslyn/extension/Terminal.Gui types leak — §7.1, §8.1); keep extension types out of durable state; use `AssemblyLoadContext` not `AppDomain` for extension unloading (§36); **do not stage, commit, push, or do destructive Git operations unless explicitly requested**.
   - Reference the DOX framework (root `AGENTS.md` is the rail; child `AGENTS.md` files own domain subtrees; closer doc controls local details; no child weakens a parent) and state that a DOX pass (update the nearest owning `AGENTS.md` + affected Child DOX Index) is required after any meaningful change.
   - Reference the plan template + agent instructions (strategy §32 / `00-shared-context.md` §G) as the per-plan contract.
   - Document the **project-level system-prompt-append option**: a repo may place append files under `.threadsmith/prompts/` and reference them from `.threadsmith/config.*` (§21.2 "Prompt append files"); these are appended to the model's system prompt at request-assembly time (plan-09 / §14.3). State that repo-provided append content is **untrusted input** (§22.2) — sanitized + bounded, never executed as code, never allowed to override host policy or the guardrails — and that append files are versioned + referenced by id+version in execution records (§11.6).
7. **Define the repository configuration schema** (§21.2) with the ordinary layered-precedence model from §21.1 (compiled defaults → machine → user → repo → session → CLI → env) using `Microsoft.Extensions.Configuration`. Plan 62 subsequently moved static secret values outside this graph into a separate host-owned resolver; only logical references remain in typed ordinary configuration. The schema must include all §21.2 keys (solution selection, build commands, test policy, editable roots, prohibited paths, model profile, context rules, tool permissions, extension allow list, **prompt append files**, formatting rules, validation requirements). Wire the **prompt-append-files** key to resolve one or more files under `.threadsmith/prompts/` (or absolute paths within the repo root) into an ordered list of append segments; load is deferred to plan-09, but plan-01 establishes the config binding + a `.threadsmith/config.example` documenting every key. Per §21.2, repo config is data, not code — never execute it.
8. Copy `portable-csharp-guardrails.md` from the implementation-plan package into `docs/guardrails/` so `AGENTS.md` can reference it by repo-relative path.
9. CI pipeline: restore + build + test on Windows + Linux.
10. Terminal.Gui v2 spike: instance-based app showing streaming fake text without freezing.
11. `MSBuildWorkspace` spike: load a sample solution, find a symbol, print it.
12. OpenAI-compatible streaming spike: stream a canned completion with cancellation.
13. Collectible ALC spike: load an extension DLL, invoke, `Unload()`, `WeakReference` dead.
14. SQLite spike: write an event row, close, reopen, read it back.
15. Process-tree cancellation spike: launch a child that launches a grandchild, cancel the token, assert both die.
16. Write ADRs 1–6 from spike observations.

## 10. Testing
- Architecture-direction tests (task 5) are the primary correctness gate.
- Each spike ends with an automated `PASS`/`FAIL` assertion, not just manual observation.
- CI must be green on both platforms.

## 11. Security and Permissions
- Spikes must not embed real provider keys; use a fake/deterministic provider for the streaming spike.
- No secrets in CI; use masked variables if any spike needs a key (prefer not to).

## 12. Observability
- Spikes record version numbers + observed behavior to `docs/architecture/spike-notes.md` for ADR traceability.

## 13. Migration and Compatibility
N/A — greenfield.

## 14. Acceptance Criteria
- All M0 exit criteria (§27) met: sample solution loads + symbol found; Terminal.Gui streams without freeze; extension discovered-by-reflection → invoked → unloaded; durable event written + restored; child process cancellable.
- Architecture-direction tests pass and fail fast on a deliberately wrong reference.
- Root `AGENTS.md` exists, references the strategy document, the implementation-plan package, and `docs/guardrails/portable-csharp-guardrails.md`; states the C# baseline, the binding working rules (incl. no staging/commit unless asked), the DOX pass requirement, **and the project-level system-prompt-append option (`.threadsmith/prompts/` + `.threadsmith/config.*`) with the untrusted-input caveat**.
- `docs/guardrails/portable-csharp-guardrails.md` is present in the repo.
- `.threadsmith/config.example` documents every §21.2 key including `prompt append files`; the config binding loads without error.
- CI green on both platforms.
- ADRs 1–6 recorded.

## 15. Risks and Mitigations
- **Terminal.Gui v2 instability** (§30.3): spike isolates the risk; if v2 is unusable, escalate before plan-03.
- **`MSBuildWorkspace` load failures on real-world SDKs** (§30.1): spike uses a sample solution; document SDK assumptions; feed plan-06.
- **ALC unload blocked by static refs** (§30.9, §17.18): the spike extension is deliberately clean; the blocked case is a plan-17 concern.

## 16. Documentation
- ADRs 1–6 in `docs/architecture/`.
- Spike notes in `docs/architecture/spike-notes.md`.
- Root `AGENTS.md` (task 6) — the DOX rail + agent behavioral contract, incl. the project-level system-prompt-append option + untrusted-input caveat.
- `docs/guardrails/portable-csharp-guardrails.md` (task 8) — the C# guardrails `AGENTS.md` points agents to.
- `.threadsmith/config.example` (task 7) — documents every §21.2 repository-configuration key, including `prompt append files`.

## 17. Current Decisions
- The solution-wide analyzer and formatting rules are defined by `Directory.Build.props`, `stylecop.ruleset`, `.editorconfig`, and `docs/guardrails/portable-csharp-guardrails.md`.
- All 16 §8 product projects remain separate; no temporary `Threadsmith.Runtime` project was introduced.
