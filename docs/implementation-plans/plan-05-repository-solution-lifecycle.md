# Implementation Plan 05: Repository and Solution Lifecycle

**Milestone:** M2 — Repository and .NET Semantic Discovery
**Strategy source:** §13.2 (solution loading), §13.3 (multi-targeting), §22.2 (repository trust), §21.2 (repo config), §19.2 (persisted data), §29 (ADR 7 precursor)
**Prerequisite plans:** plan-02 (commands/events/ids incl. `WorkspaceId`), plan-03 (TUI/CLI surfaces)

## 1. Objective
Make the harness open a repository, establish trust, select a solution, record a workspace baseline, and persist repo facts — the lifecycle scaffolding the semantic engine (plan-06) reads against.

## 2. Architectural Context
Parent: Foundation → Repository lifecycle (§28). This is `Threadsmith.Workspaces` (repository sessions, baselines, snapshots) + the repository open/trust/select flow. It produces the `WorkspaceId` and baseline snapshot that plan-06's semantic engine attaches to. Read `00-shared-context.md` §C–§D before starting.

## 3. Scope
- Open repository dialog/CLI command with tiered trust prompt (§22.2): safe untrusted inspection remains available, while reads and execution require higher trust.
- Solution/project selection (multi-solution repos).
- MSBuild environment resolution (locate `dotnet`, SDKs, restore).
- Workspace baseline: hash of tracked files at open time (foundation for plan-10 conflict detection).
- Repo configuration load (§21.2): `.threadsmith/config.*` if present.
- Persisted repo facts (§19.2): solution path, TFM inventory summary, trust state.
- `RepositoryOpened` + `SolutionLoaded` events (§9.4).

## 4. Non-Scope
- No Roslyn `Compilation` or symbol work (plan-06). No mutation (plan-10). No worktree isolation (plan-10 option). Baseline here is a *hash snapshot*, not a copy-on-write staging area (that's plan-10, built on §10.7).

## 5. Current State
Implemented. `RepositoryLifecycle` provides tiered and persisted trust, confined configuration, recursive solution/project discovery, explicit multi-candidate selection, declared-TFM inventory, deterministic approved-root baselines, and repository/solution events. `DotNetEnvironmentResolver` records SDK/MSBuild facts and permits restore only at `TrustedBuild`; SQLite stores repository facts. The current directory is the default repository when `--repository` is omitted. Interactive startup accepts optional `--trust` and `--solution` values; otherwise numbered Up/Down/Enter prompts govern trust and ambiguous solution selection. `/trust` changes or upgrades the active repository through the same lifecycle, while persisted higher trust remains monotonic. TUI and CLI workflows reuse persisted trust and skip reparse/unreadable/reserved-name entries safely.

## 6. Proposed Design
- `OpenRepositoryCommand` → trust prompt → `SelectSolutionCommand` → `RecordBaselineCommand`. `UntrustedInspection` stops before selection/baseline; persisted build trust skips the prompt on reopen, while persisted read trust can be retained or explicitly upgraded to build trust.
- Baseline = a manifest of `(relativePath, hash)` for files under approved roots; stored in the workspace; this is the *immutable baseline* referenced by §10.7's turn contract (plan-10 will layer staging on top).
- MSBuild environment resolution reuses the plan-01 spike findings. Restore is invoked only at `TrustedBuild` or above; compilation is plan-06's job.
- Trust state is persisted; reopening with build trust skips the prompt, while read trust can be retained or upgraded (§22.2).
- Interactive surfaces query persisted trust through `GetRepositoryTrustCommand`; the TUI offers inspect, read, and build trust for a new repository, offers an explicit build upgrade for persisted read trust, and requires explicit selection when discovery returns multiple solutions.
- Repository-confined reads reject any existing symbolic-link or junction component beneath the root. Prohibited paths use slash-normalized glob semantics: `*`/`?` match within a segment, `**` crosses segments, and a trailing `/` excludes descendants.

## 7. Public Contracts
- `GetRepositoryTrustCommand`, `OpenRepositoryCommand`, `SelectSolutionCommand`, `RecordBaselineCommand`.
- `WorkspaceBaseline` (file hash manifest + metadata).
- `RepositoryTrustState`.
- `RepositoryOpened`, `SolutionLoaded` events.

## 8. Project and File Changes
- `Threadsmith.Workspaces/`: repository session, baseline manifest, MSBuild env resolution, trust state.
- `Threadsmith.Persistence/`: repo-facts table (minimal).
- TUI/CLI: open/trust/select flows.

## 9. Ordered Implementation Tasks
1. `OpenRepositoryCommand` + tiered trust prompt (§22.2); retain safe `UntrustedInspection`.
2. Repo config load (§21.2).
3. MSBuild environment resolution (from plan-01 spike).
4. `SelectSolutionCommand` (multi-solution support).
5. Baseline manifest: hash tracked files under approved roots.
6. Persist repo facts + trust state (§19.2, §22.2).
7. `RepositoryOpened` / `SolutionLoaded` events.
8. TUI/CLI open/trust/select flows.

## 10. Testing
- Open a sample solution (plan-01 `samples/repositories/SmallDotNetSolution`); baseline manifest produced.
- Reopening with persisted build trust skips the prompt; persisted read trust can be retained or upgraded explicitly.
- Untrusted inspection discovers safe candidates; solution selection, content hashing, and repository execution remain blocked until the required trust is granted (§22.2).
- No write occurs outside approved roots (M5 invariants start here).
- Configured solutions and project references that traverse a symbolic-link or junction ancestor are rejected.
- Initial build trust and persisted-read-to-build upgrades are explicit TUI choices; multiple solution candidates require an explicit TUI choice.

## 11. Security and Permissions
- Trust gate (§22.2) is mandatory; untrusted repos cannot proceed to file reads, MSBuild evaluation, restore, build, or mutation.
- Path access confined to approved roots (§22.1 threat: unsafe repo execution).

## 12. Observability
- Baseline hashing time + file count metrics (large-repo concern, §30.8).

## 13. Migration and Compatibility
N/A — new persistence table; plan-18 will finalize schema.

## 14. Acceptance Criteria
- M2 subset: a repository opens, trust is established, a solution is selected, a baseline manifest is recorded.
- `RepositoryOpened` + `SolutionLoaded` events emitted and persisted.
- Untrusted repositories are confined to non-executing candidate inspection; solution selection and baseline capture are blocked.

## 15. Risks and Mitigations
- **Large repo baseline cost (§30.8):** hashing is I/O-bound; parallelize + stream; metric-gated.
- **MSBuild env ambiguity (custom SDKs):** record the resolved SDK; feed plan-06 (and §13.x confidence degradation).

## 16. Documentation
- `docs/operations/opening-a-repository.md` (trust flow).

## 17. Current Decisions
- Baselines hash files selected by approved-root and include/exclude policy, including source and project files; linked paths must remain explicitly approved.
- Trust is persisted per user in the repository-facts database.
