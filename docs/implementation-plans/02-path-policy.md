# Priority 2: Consolidate path checks

**Status:** Complete; follow-up Windows validation and limitations are recorded below. Linux/macOS validation remains unavailable locally. **Track:** Maintenance.
**Baseline:** `7eb1bd26f12870e94a5de189a438b09819ff88a4`.
**Prerequisite:** Implementation authorized by the user. Commits and publication remain unauthorized.

## Goal

Reuse Core path comparison and link inspection. Remove competing implementations without changing permission ownership.

## Problem

Some callers assume all Windows directories are case-insensitive. `RepositoryPathPolicy` checks filesystem behavior instead. These paths can therefore make different access decisions.

Separate link walkers also disagree about the authority root, missing targets, and inspection failures.

Sources: [Core path policy](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/7eb1bd26f12870e94a5de189a438b09819ff88a4/src/Threadsmith.Core/RepositoryPathPolicy.cs#L14-L40), [Core link check](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/7eb1bd26f12870e94a5de189a438b09819ff88a4/src/Threadsmith.Core/ScriptFileScope.cs#L68-L97), [tool checks](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/7eb1bd26f12870e94a5de189a438b09819ff88a4/src/Threadsmith.Tools/ToolPolicy.cs#L352-L415), [write checks](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/7eb1bd26f12870e94a5de189a438b09819ff88a4/src/Threadsmith.Tools/WriteFileTool.cs#L145-L165).

## Implementation

1. Read `AGENTS.md`, the C# guardrails, and current test instructions. Confirm the active checkout and compare it with the baseline.
2. List the relevant callers. For each caller, record its authority root, empty-scope meaning, missing-target rule, error result, and path output format.
3. Add tests for Core containment, equality, link inspection, and root intersection.
4. Move `ToolPathRules` and `WriteFileTool` containment checks to `RepositoryPathPolicy`.
5. Move ordinary `ToolPathRules` link inspection to `ScopedFilePaths.RejectLinks`. Preserve tool-specific restrictions.
6. Share root intersection mechanics across `AgentToolPolicy`, `WriteFileConfiguration`, and `SecurityPolicyConfigurationLoader`.
7. Migrate `SemanticPathSafety`, `ValidationPathGuard`, and `MutationApprovalPolicyService` only after their distinct contracts have tests.
8. Remove replaced methods. Search for remaining duplicate comparisons and walkers. Record a concrete reason for each remaining implementation.
9. Run focused tests, then required repository checks. Review access decisions through actual tool entry points.

Complete and test each caller migration separately. Keep case-handling corrections distinct from mechanical code extraction.

## Rules

- Use `RepositoryPathPolicy` as the path comparison owner. Do not create another path utility.
- Use consistent comparison for equality, containment, intersection, and duplicate removal.
- An intersection must not grant more access than either input.
- Preserve each caller's empty-scope meaning and absolute or relative output format.
- Preserve external grants, child scopes, control-file restrictions, scratchpad rules, and write approvals.
- Include the authority root in link inspection. Keep trusted ancestors above that boundary outside the check.
- Preserve missing write targets. Preserve existing-target requirements for other operations.
- Preserve `inspectFileSystem:false` for captured evidence. Do not inspect current files in that mode.
- Keep existing opened-handle checks. Path inspection does not prevent a file from changing after the check.
- Keep helpers in Core. Do not add cross-layer dependencies or a new permission model.

## Tests

- Equal, nested, disjoint, and sibling-prefix paths.
- Filesystem roots, drive roots, UNC roots, and trailing separators.
- Case-sensitive Linux and Windows directories; case-insensitive macOS where available.
- Distinct sibling names that differ only by case.
- Linked authority roots, internal links, target links, and dangling links.
- Trusted links above the authority boundary.
- Missing write targets and unreadable components.
- Empty scopes, external grants, child worktree rebasing, and control-file restrictions.
- Captured evidence with filesystem inspection disabled.
- Consistent read, list, explore, and write decisions for equivalent grants.

Start with `ParallelAgentTests`, `SecurityPolicyConfigurationTests`, and `ScratchpadScriptTests`. Do not change machine security settings to enable a test without approval. Report unavailable filesystem features and skipped platform checks.

## Acceptance Criteria

- [x] Migrated callers use the Core path comparison implementation.
- [x] Unauthorized sibling paths remain outside the grant.
- [x] Root intersections never widen access.
- [x] Link checks include the authority root and preserve trusted ancestors above it.
- [x] Empty-scope, missing-target, and captured-evidence behavior remains correct.
- [x] Existing permission and control-file tests pass.
- [x] Replaced comparisons and walkers are removed.
- [x] The final report identifies intentional behavior corrections and three-platform test results.

## Stop Conditions

Stop and report if consolidation requires broader access, a public API break, or a new project dependency.

Do not stage, commit, push, or open a pull request without explicit approval. If this plan enters repository documentation, apply its planning template and documentation checks. Do not reopen completed milestones.

## Implementation record

### Caller contracts

| Caller | Authority and empty scope | Missing target / errors | Output |
|---|---|---|---|
| Ordinary `ToolPathRules` | Repository plus explicit approved roots; empty roots deny | Missing descendants allowed for operation-specific checks; unauthorized access or inspection error | Absolute |
| Scratchpad tool paths | Active scratchpad; inspect from repository if contained, otherwise scratchpad | Missing write targets allowed; unauthorized access or inspection error | Absolute |
| `WriteFileTool` | Strict descendant of configured folder or scratchpad; empty folders deny ordinary writes; child scope still applies | Missing targets allowed; repository boundary for internal grants, granted folder for external grants | Absolute |
| `AgentToolPolicy` | Intersection with parent; empty child scope means repository; empty intersection rejected | No target existence requirement; unauthorized access on escaping scope | Relative approved roots; absolute effective-policy roots after worktree rebasing |
| `WriteFileConfiguration` | Trusted folders intersect replacing restrictions; empty list denies | Missing folders allowed | Absolute |
| `SecurityPolicyConfigurationLoader` | Trusted ceilings intersect restrictions; caller retains null/unrestricted versus empty/denied meaning | Missing folders allowed | Absolute, deterministic ordinal ordering |
| `SemanticPathSafety` | Repository inclusive; trusted ancestors excluded | Missing inputs allowed for caller handling; inspection errors/outside paths return unsafe | Boolean |
| `ValidationPathGuard` | Repository inclusive; trusted ancestors excluded | Every component must exist; invalid-operation error with target context | None |
| Mutation consent persistence | Existing repository inclusive; missing config descendants allowed | Inspection errors reset consent to review-all; disappeared bound root cannot acquire consent | None |
| Mutation target policy | Repository strict descendant and effective mutation roots | Missing targets allowed; mutation-policy error for forbidden paths | None |

### Intentional corrections

- Grant containment, equality, intersection and deduplication share Core mechanics. A component's parent governs its case, including the authority root's own name. Exact spelling needs no filesystem probe. Unknown or absent parents cannot establish differently cased grants.
- Ordinary tool and validation link inspection now includes the authority root and catches dangling links. Direct writes stop at repository/external grant authority, preserving trusted ancestors above that boundary. Opened-handle protections remain intact.
- Captured PR evidence (`inspectFileSystem:false`) performs no live filesystem inspection: grant spelling is ordinal and prohibitions conservatively fold case. Differently cased grants can therefore hide evidence previously visible on Windows; differently cased prohibitions can hide evidence previously visible on case-sensitive filesystems. This is intentional fail-closed narrowing when live case evidence is unavailable, rather than a new persisted case-state subsystem.
- Root output formats, empty-scope meanings, external grants, control-file restrictions, write approvals and child-worktree rebasing remain caller-owned.

### Remaining comparisons and inspections

- `GetPathComparer` remains for existing collection consumers; migrated grant comparisons do not infer unknown directory case from ancestors. Glob matching retains `RepositoryPathCaseContext` because prohibition uncertainty must fold case instead of granting aliases.
- Extension, executable, tool ID, Git/control-file and secret filename comparisons are policy-name rules, not filesystem identity comparisons.
- `SemanticEngine`, `AdvancedSemanticQueryService`, semantic refresh and validation target resolvers retain selection/index comparisons and single-entry enumeration filters. These broader catalog/query selection contracts are outside the named caller migrations; semantic admission still uses the migrated guard. Their OS-based selection behavior is not claimed corrected here.
- `ScratchpadLifecycle`, repository initialization/lifecycle, transactional workspaces, and secret-store handling retain their ownership-specific lifecycle checks. Secret opened-handle checks and transactional mutation inspections are deliberately not replaced by path inspection; they protect different operation boundaries.
- The policy-generation repository identity check in `DefaultPolicyEngine` remains part of policy binding, rather than the migrated per-resource path authority.

### Validation

Commands executed from the active checkout on Windows:

- `dotnet restore src/Threadsmith.sln` â€” passed.
- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` â€” final build in progress.
- `dotnet test --project tests/Threadsmith.Architecture.Tests/Threadsmith.Architecture.Tests.csproj --configuration Debug --no-build --filter-class '*RepositoryPathPolicyTests'` â€” 3 passed, 1 symbolic-link privilege skip before the final additional assertions.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4` â€” final run in progress. The initial run exposed one incorrect new test expectation about `AGENTS.md`; it was corrected to the existing `.threadsmith/config.json` protection, without changing production policy.
- `pwsh -NoProfile -File eng/Stage-DevelopmentRipgrep.ps1 -OutputRoot artifacts/test-ripgrep` â€” passed, pinned executable verified.
- `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1` â€” passed.
- `pwsh -NoProfile -File eng/Test-PrSubmission.ps1` â€” passed (gate contract tests only; no PR or push).
- `dotnet format src/Threadsmith.sln --verify-no-changes --no-restore --include <changed C# files>` â€” passed.
- `dotnet format analyzers src/Threadsmith.sln --verify-no-changes --no-restore --severity info --include <changed C# files>` â€” no remaining introduced findings. Seven existing informational suggestions remain in unchanged logic: IDE0007, CA1865, RCS1085, IDE0032, RCS1205 and two xUnit2032 suggestions. The command exits nonzero for those existing suggestions; rules were not disabled.
- `git diff --check` â€” passed.

A reviewer with no implementation conversation context reviewed the diff and surrounding entry points. Valid feedback was fixed and re-reviewed until no concrete findings remained; final test/format deltas were also reviewed. Review was source-based, not a substitute for runtime results.

Linux and macOS checks were not run on this Windows host; three-platform CI remains unverified because no commit/push was authorized. Real UNC shares, unreadable Unix components, case-insensitive macOS volumes, and mixed per-directory Windows case modes were not exercised. UNC lexical boundaries have a network-free Windows test. Symbolic-link tests skip when creation privileges are absent; existing Windows junction entry-point tests cover linked validation roots. No machine security settings were changed.

### Follow-up review corrections

Two subsequently reported bypasses were fixed without adding another execution path:

- Direct writes retain the first strict-descendant folder that actually grants the destination. External link inspection uses that retained grant, so an exact-destination allowlist entry cannot shorten inspection and hide a junction beneath the granting folder.
- Core `NormalizeContainedPath` verifies containment and adopts the authority root's spelling. Ordinary tool paths, shared reads and direct writes normalize repository aliases before computing relative paths or checking prohibitions; ordinary tools also return the normalized path. This avoids the runtime's OS-based relative-path case rules producing `../repo/...` for an admitted case alias on a case-insensitive Linux filesystem.

Small regressions extend the existing write junction test and exercise shared normalization, prohibited reads, and `list_files` output. The live alias test runs only where the temporary filesystem is case-insensitive; an explicit-comparer Core test exercises normalization without requiring such a volume. A fresh source reviewer found no remaining concrete issues in these corrections. Follow-up final validation is recorded below.

Follow-up validation on Windows:

- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore`: passed, zero warnings/errors.
- `dotnet test --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Debug --no-build --filter-class '*WriteFileTests' --filter-class '*ToolPathRulesTests'`: 36 passed, 1 symbolic-link privilege skip. The external junction regression and live alias/listing regression both passed.
- `dotnet test --project tests/Threadsmith.Architecture.Tests/Threadsmith.Architecture.Tests.csproj --configuration Debug --no-build --filter-class '*RepositoryPathPolicyTests'`: 4 passed, 1 symbolic-link privilege skip.
- `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4`: 4,985 passed, 66 skipped, 1 failed. The failure was `RepositoryMemoryBindingTests.Onboarding_supplies_history_without_model_requests_and_requires_every_page(false, false)`, an OS `UnauthorizedAccessException` at the unchanged `RepositorySettingsCoordinator.UpdateAsync` settings-file `File.Move`, not a path-policy assertion.
- `dotnet test --project tests/Threadsmith.Architecture.Tests/Threadsmith.Architecture.Tests.csproj --configuration Debug --no-build --filter-method '*Onboarding_supplies_history_without_model_requests_and_requires_every_page'`: all 3 cases passed on rerun. The intermittent file-move failure was not reproduced or fixed; the full-suite run is not reported as passing.
- `dotnet format src/Threadsmith.sln --verify-no-changes --no-restore --include <follow-up C# files>`: passed. The equivalent analyzer check with `--severity info` reported only the pre-existing CA1865 and RCS1085 suggestions, exiting nonzero for those suggestions.
- Packaged-documentation preflight and `git diff --check`: passed.

The earlier platform limitations still apply. In particular, a real case-insensitive Linux mount was not available for runtime verification; the normalization unit test is portable and the live alias integration passed on Windows. No commit or push was performed.
