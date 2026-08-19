# Plan 41 — Git, Repository, and Package Inventory Tools

**Milestone:** M14 — Rich Native Tool Inventory

**Prerequisites:** plans 05–08, 18, and 27

**Depends on by:** plans 42–44

**Status:** Complete. Production implementation, focused automated coverage, documentation, and milestone integration are complete; maintained real-environment cases remain compatibility checks.

## 1 Objective

Add typed, bounded read-only tools for Git history/comparison and .NET repository inventory so models and users can inspect repository state without routing ordinary discovery through `run_process`.

## 2 Architectural Context

Threadsmith currently exposes Git status, file/text search, and semantic lookup through the central tool pipeline. Repository discovery already owns selected solution/project facts, and the workspace layer owns Git process policy. M14 extends those owners rather than creating a second Git, MSBuild, or package parser.

This is a user-approved post-strategy milestone. Existing strategy rules remain authoritative: the host owns control flow and policy, tools return host-owned DTOs, raw output is bounded evidence, cancellation is end-to-end, and repository content cannot grant authority.

## 3 Scope

- `git_diff`, `git_log`, `git_show`, `git_blame`, and branch-comparison tools.
- Working-tree, staged, commit, range, and merge-base-aware comparisons with explicit modes.
- Solution, project, target-framework, project-reference, package-reference, central-package, and test-project inventory.
- Stable schemas, provenance, truncation, path/revision validation, tool availability, configuration, events, telemetry, and interactive/headless parity.
- Reuse of repository identity, selected solution, semantic confidence, Git adapter, process manager, and evidence pipeline.

## 4 Non-Scope

- Git mutation: add, commit, checkout, switch, merge, rebase, reset, clean, push, fetch, or pull.
- Remote repository access or implicit network I/O.
- Arbitrary Git option forwarding, revision expressions that execute helpers, external diff/text-conversion drivers, or pager/editor invocation.
- Dependency vulnerability analysis or restore (plan 42).
- Semantic impact analysis (plan 43).

## 5 Current State

Plan 08 provides typed Git status through a bounded process adapter. Plans 05–06 expose selected solution/project and target-framework facts. Plan 13 identifies test projects. There is no typed history/diff/blame/branch-comparison inventory, and package facts are not available as one normalized tool result.

## 6 Proposed Design

Create closed request modes rather than a generic Git command wrapper. Resolve revisions with argument arrays under the existing repository-confined Git adapter, disable pager/color/external diff behavior, use literal repository-relative path filters after `--`, and normalize results before they enter evidence.

Repository inventory is assembled from the authoritative loaded solution/project graph when available and may fall back to bounded project-file evaluation when confidence is degraded. Every result identifies its source, selected solution, repository revision, confidence, omissions, and whether evaluation or restore-derived assets were used.

## 7 Public Contracts

- `GitDiffRequest/Result`, `GitDiffEntry`, `GitHunkSummary`, and `GitComparisonMode`.
- `GitLogRequest/Result` and `GitCommitSummary`.
- `GitShowRequest/Result` and `GitObjectKind`.
- `GitBlameRequest/Result` and `GitBlameRange`.
- `GitBranchComparisonRequest/Result`, merge-base identity, ahead/behind counts, and changed paths.
- `DotNetInventoryRequest/Result`, `SolutionInventory`, `ProjectInventory`, `TargetFrameworkInventory`, `ProjectReferenceInventory`, `PackageReferenceInventory`, and `PackageVersionSource`.

Contracts contain no Git-library, process, Roslyn, MSBuild, NuGet-client, terminal, or persistence implementation types.

## 8 Project/File Changes

- `Threadsmith.Workspaces` — closed Git query adapter and normalized Git DTO production.
- `Threadsmith.DotNet` — inventory facade over existing solution/project evaluation.
- `Threadsmith.Tools` — descriptors, schemas, handlers, bounds, and availability.
- `Threadsmith.Core` / `Threadsmith.Persistence` — only shared projections/events or schema migration if durable contracts require them.
- `Threadsmith.App`, `Threadsmith.Tui`, and `Threadsmith.Cli` — registration and equivalent rendering.
- Dedicated M14 tests, fixtures, operations/user documentation, configuration example, acceptance/manual scenarios, and DOX updates.

## 9 Ordered Tasks

1. Inventory existing Git, repository, solution, package, process, policy, evidence, and tool-schema contracts; record ownership decisions.
2. Define closed request/result schemas and common bounded Git revision/path validation.
3. Implement Git diff, log, show, blame, and branch comparison using the existing confined process infrastructure.
4. Implement normalized solution/project/TFM/reference/package/test-project inventory over existing discovery state.
5. Register typed tools with repository trust, semantic confidence, availability, cancellation, budgets, provenance, and redaction.
6. Add bounded rendering and evidence/context integration without persisting unbounded raw command output.
7. Add deterministic repositories covering staged/unstaged changes, branches, merges, renames, binary files, blame, central package management, multi-targeting, unloaded projects, and malformed inputs.
8. Update docs, Scenario N, manual cases, roadmap status, and DOX when implementation lands.

## 10 Testing

Verify each request mode, path/revision rejection, merge-base and ahead/behind behavior, rename/binary/truncation handling, no pager/driver/network execution, cancellation and process-tree cleanup, degraded semantic inventory, central/transitive version attribution where knowable, deterministic ordering, redaction, interactive/headless equivalence, and architecture boundaries.

## 11 Security/Permissions

All tools are read-only but still require repository trust and central invocation policy. Revisions and paths are data, never command fragments. Git configuration capable of executing external helpers is neutralized for these operations. No command contacts a remote, opens an editor/pager, or invokes an external diff/text-conversion driver.

## 12 Observability

Record tool ID, repository/workspace, request mode, bounded revision/path metadata, result counts/bytes, truncation, confidence, duration, cancellation, and normalized failure code. Do not log file contents, diff bodies, commit messages, package-source credentials, or raw Git output.

## 13 Migration/Compatibility

Existing `git_status` and discovery contracts remain compatible. New tools are registered through Plan 27 availability and compiled defaults. No repository configuration migration is required unless per-tool scalar limits are added.

## 14 Acceptance Criteria

- Each named Git operation is a distinct typed tool with bounded host-owned results.
- Branch comparison reports validated endpoints, merge base, ahead/behind counts, and normalized changed paths without network I/O.
- Solution/project/package inventory reports selected-solution provenance, TFMs, references, package version source, test classification, confidence, and omissions.
- Invalid paths/revisions/options fail before unsafe execution; cancellation terminates tracked Git work.
- Equivalent interactive/headless invocations produce equivalent normalized results.
- Existing repository, semantic, tool, persistence, and architecture suites remain passing.

## 15 Risks

- Git becomes a disguised raw-command surface: use closed enums and fixed argument construction.
- Large history/diffs overwhelm context: cap commits, files, hunks, lines, bytes, and artifact references independently.
- Inventory duplicates discovery truth: adapt existing lifecycle/semantic state and disclose degraded fallbacks.
- Git attributes/config execute helpers: explicitly disable external diff, text conversion, pager, and optional locks where applicable.

## 16 Documentation

Document schemas, limits, availability, confidence, truncation, revision/path rules, absence of remote access, and examples. Do not present the tools as available until implemented.

## 17 Decisions

- One typed tool per high-value Git operation; no generic Git router.
- Repository inventory is a normalized facade over existing discovery owners.
- Git reads remain local-only and never invoke user-configured executable helpers.
