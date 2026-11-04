# Priority 2: Consolidate path checks

**Status:** Proposed. **Track:** Maintenance.
**Baseline:** `7eb1bd26f12870e94a5de189a438b09819ff88a4`.
**Prerequisite:** Approval to implement. This plan does not authorize commits or publication.

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

- [ ] Migrated callers use the Core path comparison implementation.
- [ ] Unauthorized sibling paths remain outside the grant.
- [ ] Root intersections never widen access.
- [ ] Link checks include the authority root and preserve trusted ancestors above it.
- [ ] Empty-scope, missing-target, and captured-evidence behavior remains correct.
- [ ] Existing permission and control-file tests pass.
- [ ] Replaced comparisons and walkers are removed.
- [ ] The final report identifies intentional behavior corrections and three-platform test results.

## Stop Conditions

Stop and report if consolidation requires broader access, a public API break, or a new project dependency.

Do not stage, commit, push, or open a pull request without explicit approval. If this plan enters repository documentation, apply its planning template and documentation checks. Do not reopen completed milestones.
