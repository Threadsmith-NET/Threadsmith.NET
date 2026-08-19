# Plan 29 — Solution Selection Memory and Repository Initialization

**Milestone:** 7.2 (Quality of Life Enhancements)
**Prerequisites:** plan-05, plan-27
**Depends on by:** none
**Status:** Implementation complete; maintained manual verification remains.

## 1 Objective

Remember the last explicitly or automatically selected .NET solution per repository, auto-load a valid preference on later interactive and headless startup, recover safely from stale preferences, and offer minimal repository configuration scaffolding only for an empty unconfigured repository.

## 2 Architectural Context

Repository configuration is host-owned data. `Threadsmith.Core` owns serializable commands/results, `Threadsmith.Workspaces` owns confined filesystem inspection and atomic persistence, and TUI/CLI surfaces consume the command boundary. No mutable configuration provider or persistence implementation crosses into terminal code.

The draft's literal JSON property `"solution:path"` is corrected to nested JSON:

```json
{
  "solution": {
    "path": "src/MySolution.sln"
  }
}
```

`Microsoft.Extensions.Configuration` exposes this as `solution:path`.

## 3 Scope

- Safe relative `solution:path` persistence after every successful selection.
- Valid remembered-solution auto-selection with explicit `--solution` precedence.
- Stale preference cleanup followed by normal discovery.
- Interactive startup notification and headless notification.
- Initialization eligibility inspection and a numbered interactive initialization prompt.
- Atomic, non-overwriting `.threadsmith/config.json` scaffolding.
- Automated lifecycle, controller, CLI, and terminal-neutral shell coverage.
- Configuration, README, manual-plan, milestone, and DOX updates.

## 4 Non-Scope

- Creating a `.sln`, project, source tree, Git repository, prompt file, extension selection, secrets, or user-level configuration.
- Overwriting any existing `.threadsmith/config.json`.
- Prompting in headless/CI mode.
- Treating configuration as executable code.

## 5 Current State

Plan 05 already discovered `.sln`, `.slnx`, and supported project files and read `solution:path`, but a stale value aborted repository opening, a valid value only reordered candidates, and selection was not written back. Plan 29 completes that lifecycle without changing trust or path-confinement rules.

## 6 Proposed Design

### Solution memory

`SelectSolutionCommand` remains the sole successful selection boundary. After validation, the lifecycle derives a slash-normalized repository-relative path and atomically updates nested `solution.path`, preserving unrelated JSON and existing case-insensitive property spelling. Preference-write failure is logged without invalidating an otherwise successful solution load.

During open, a configured path is normalized beneath the repository root. A valid, non-prohibited file remains the first candidate and is exposed in `RepositoryConfigurationSnapshot`. A missing file causes best-effort atomic clearing and normal discovery; escaping, prohibited, linked, or malformed paths still fail closed.

TUI and CLI choose in this order:

1. explicit `--solution` / requested path;
2. valid remembered path;
3. one discovered candidate;
4. interactive selector or headless ambiguity result.

Interactive auto-load prints:

```text
Loading remembered solution: src/MySolution.sln
  (Use --solution to change)
```

### Repository initialization

`GetRepositoryInitializationStatusCommand` reports whether `.threadsmith/` exists and whether a supported solution/project candidate exists. The composition root captures directory existence before initializing repository-local runtime persistence and supplies that snapshot to the initial interactive check, so host-created database storage cannot hide the prompt. Later `/open` operations use live state. Interactive startup offers initialization only when both are absent. Declining continues current empty-config behavior.

`InitializeRepositoryCommand` invokes `RepositoryInitializer`, which:

- normalizes and confines the repository root;
- rejects root or configuration-directory reparse points;
- creates `.threadsmith/config.json` via a same-directory temporary file and non-overwriting move;
- returns idempotently when configuration already exists;
- writes strict UTF-8 JSON with minimal neutral structure:

```json
{
  "solution": { "path": null },
  "tools": { "disabled": [], "config": {} }
}
```

The scaffold intentionally omits `tools:enabled`; an empty enabled array is a deny-all allowlist under Plan 27.

## 7 Public Contracts

- `RepositoryInitializationStatus`
- `RepositoryInitializationResult`
- `GetRepositoryInitializationStatusCommand`
- `InitializeRepositoryCommand`
- `RepositoryOpenWorkflowResult.UsedRememberedSolution`
- `RepositoryInitializer.GetStatusAsync`
- `RepositoryInitializer.InitializeAsync`

All contracts are host-owned and terminal/configuration-library neutral.

## 8 Project/File Changes

- `Threadsmith.Core/RepositoryContracts.cs` — commands and DTOs.
- `Threadsmith.Workspaces/RepositoryInitializer.cs` — confined inspection/scaffolding.
- `Threadsmith.Workspaces/RepositoryLifecycle.cs` — handlers and atomic preference lifecycle.
- `Threadsmith.Tui/TuiShell.cs`, `ConversationalShell.cs` — orchestration, prompt, and notification.
- `Threadsmith.Cli/HeadlessShell.cs` — remembered headless selection and notification.
- `Threadsmith.Milestone2.Tests/RepositoryLifecycleTests.cs` — observable lifecycle/surface tests.
- User documentation, config catalog, manual plan, milestone status, and owning DOX.

## 9 Ordered Tasks

1. Add host-owned initialization contracts.
2. Implement confined, idempotent repository scaffolding.
3. Persist successful selection while preserving unrelated configuration.
4. Clear stale memory without blocking discovery.
5. Auto-select valid memory in TUI and CLI with explicit override precedence.
6. Add interactive initialization prompt and startup notification.
7. Add positive, stale, preservation, ambiguity, idempotence, and shell tests.
8. Update configuration, user documentation, maintained manual coverage, status, and DOX.

All implementation tasks are complete.

## 10 Testing

Automated Milestone 2 coverage verifies:

- relative preference persistence, case-insensitive existing-key spelling, and unrelated JSON preservation;
- stale preference clearing and continued discovery;
- valid remembered auto-load under multiple-solution ambiguity;
- explicit requested solution precedence;
- headless and interactive notifications;
- initializer eligibility, strict JSON structure, and idempotence;
- interactive initialization confirmation;
- existing trust, selector cancellation, path escape, reparse-point, baseline, and projection behavior.

MTP-030H owns maintained real-terminal and restart verification.

## 11 Security/Permissions

- Every configured, remembered, selected, and scaffold path remains beneath a normalized non-reparse repository root.
- `.threadsmith` cannot be a symbolic link or junction for scaffolding or preference writes.
- Existing configuration is parsed as bounded JSON data and unrelated values are preserved.
- Initialization never overwrites an existing config and creates no executable content.
- Explicit selection authorizes only the host preference write; it does not grant trust or mutation authority.

## 12 Observability

Existing `RepositoryOpened` and `SolutionLoaded` events remain authoritative. Preference persistence/cleanup failures are structured warnings. UI notifications do not add durable domain state or expose absolute paths when a repository-relative solution is available.

## 13 Migration/Compatibility

Existing nested `solution.path` configuration remains compatible. Repositories without a preference keep existing discovery behavior. A stale path now degrades to normal discovery instead of aborting startup. Explicit `--solution` continues to bypass selection and now refreshes memory. No scaffold is offered when `.threadsmith/` predates runtime storage initialization or a supported .NET candidate already exists.

## 14 Acceptance Criteria

- Successful selection persists a slash-normalized relative path and preserves unrelated config.
- Valid remembered paths auto-load in interactive and headless startup.
- Explicit requested solutions override memory and become the new memory.
- Missing remembered paths are cleared and discovery continues.
- Escaping, prohibited, malformed, and linked paths remain rejected.
- Empty unconfigured repositories receive a numbered initialization offer even when default runtime persistence creates `.threadsmith/` before the shell opens.
- Accepting creates minimal valid JSON atomically; declining creates nothing; repeated initialization never overwrites.
- Existing selectors, trust, cancellation, semantic startup, and baseline behavior remain passing.
- Build, automated tests, manual plan, README, config catalog, milestones, and DOX are current.

## 15 Risks

- Preference writes can fail on read-only filesystems. Mitigation: selection succeeds, failure is logged, and the next startup prompts normally.
- A solution can move after startup. Mitigation: every open revalidates and clears stale memory.
- Recursive eligibility inspection can encounter inaccessible entries. Mitigation: ignore inaccessible paths, skip reparse points, and preserve the lifecycle's later authoritative discovery validation.
- A full example config would accidentally introduce repository-specific sample values or an empty deny-all allowlist. Mitigation: scaffold only neutral keys.

## 16 Documentation

Updated `.threadsmith/config.example`, `README.md`, `milestones.md`, `manual-test-plan.md`, root/source/workspace/TUI/test DOX, and this plan.

## 17 Open Decisions

A future explicit `/solution` in-session command may reuse the existing numbered selector. Plan 29 keeps the current startup selector and `--solution` override rather than adding a new command surface.
