# Priority 1: Consolidate Git execution

**Status:** Proposed. **Track:** Maintenance.
**Baseline:** `7eb1bd26f12870e94a5de189a438b09819ff88a4`.
**Prerequisite:** Approval to implement. This plan does not authorize commits or publication.

## Goal

Reuse the Git process code in `GitQueryService`. Remove duplicate code from baseline capture and worktree management.

## Problem

- Baseline capture runs ordinary `git status`. It does not disable repository-configured fsmonitor commands.
- Its separate process runner does not terminate Git on cancellation.
- Worktree management reads all process output before truncation.
- `GitQueryService` already supplies safer process settings, output limits, and cancellation cleanup.

Sources: [baseline runner](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/7eb1bd26f12870e94a5de189a438b09819ff88a4/src/Threadsmith.Workspaces/RepositoryLifecycle.cs#L796-L885), [GitQueryService](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/7eb1bd26f12870e94a5de189a438b09819ff88a4/src/Threadsmith.Workspaces/GitQueryService.cs#L691-L778), [worktree runner](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/7eb1bd26f12870e94a5de189a438b09819ff88a4/src/Threadsmith.Workspaces/GitWorktreeManager.cs#L106-L180).

## Implementation

1. Read `AGENTS.md`, the C# guardrails, and the current test instructions. Confirm the active checkout. Compare the relevant code with the baseline above.
2. Add a regression test through actual baseline capture. Reuse the fsmonitor fixture in `Plan41InventoryToolTests`.
3. Extract the necessary process setup, output capture, and cleanup from `GitQueryService` into a small internal Workspaces helper.
4. Keep the query service's behavior unchanged. Move baseline queries to the same read-only execution path.
5. Make worktree commands reuse output capture and cleanup. Keep their mutation policy separate.
6. Remove the replaced runners and readers. Check that no caller still uses them.
7. Run focused tests, then the required repository checks. Review the final diff for unrelated changes.

Complete and test each migration before starting the next one.

## Rules

- Keep the helper in `Threadsmith.Workspaces`. Do not add a dependency on Tools.
- Preserve query settings that disable fsmonitor, external diff, and interactive prompts.
- Use `ArgumentList`; do not invoke a shell.
- Read stdout and stderr concurrently. Continue draining after the retained-output limit.
- Preserve existing output formats, error mapping, and resource limits.
- Preserve baseline behavior when Git is missing or returns an error.
- Propagate caller cancellation. Terminate owned processes and limit the cleanup wait.
- Keep worktree authority checks. Do not apply a read-only command policy to mutations.
- Do not add a general process framework, configuration migration, or fallback to the old runner.

## Tests

- Baseline capture does not execute the repository's fsmonitor command.
- Query and baseline output remain unchanged.
- Large stdout and stderr do not exceed retention limits or block process exit.
- Cancellation terminates Git and its descendants where supported.
- Timeout remains distinct from caller cancellation.
- Missing Git and nonzero exit preserve existing caller behavior.
- Worktree creation and removal still work.

Use deterministic fixtures. Do not assert exact elapsed times. Run the applicable tests on Windows, Linux, and macOS. Report unavailable platform checks.

## Acceptance Criteria

- [ ] Baseline capture uses the shared read-only Git execution path.
- [ ] The baseline fsmonitor regression test passes.
- [ ] Worktree output is limited during capture, not after complete buffering.
- [ ] Cancellation and cleanup tests pass.
- [ ] Existing output, error, and permission behavior is preserved.
- [ ] Replaced runners and readers are removed.
- [ ] The final report lists changed files, test commands, results, and skipped checks.

## Stop Conditions

Stop and report if reuse requires a public API break, a new project dependency, or a change in trust policy.

Do not stage, commit, push, or open a pull request without explicit approval. If this plan enters repository documentation, apply its planning template and documentation checks. Do not reopen completed milestones.
