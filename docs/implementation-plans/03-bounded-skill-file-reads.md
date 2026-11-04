# Plan 03: Share safe file reading

**Status:** Proposed
**Delivery track:** Maintenance
**Prerequisites:** Read `AGENTS.md`, `planning-governance.md`, and `docs/implementation-plans/00-shared-context.md`. Check current code before work.
**Source baseline:** `7eb1bd26f12870e94a5de189a438b09819ff88a4`

## 1 Objective

Use one file reader for secret stores and compatible skills. Reject files that exceed the limit before allocating large buffers.

## 2 Architectural Context

`SecretResolution` already has stronger file checks. Skills already references Tools. Reuse this code without adding a project or dependency cycle.

## 3 Scope

Share file opening, handle checks, path resolution, and byte-limit enforcement. Use the reader for skill activation and frontmatter discovery.

## 4 Non-Scope

Do not change skill approval, secret permissions, configuration defaults, or memory retrieval.

## 5 Current State

- `src/Threadsmith.Skills/ClaudeSkillCompatibilityCatalog.cs:272–283` checks the total limit after reading the complete file.
- Lines 472–534 allocate from file length and incorrectly convert Windows UNC paths.
- Lines 544 onward use another reader for frontmatter.
- `src/Threadsmith.Tools/SecretResolution.cs:998–1145` already checks the opened file and limits actual bytes read.

These findings come from source inspection. Tests have not run.

## 6 Proposed Design

Extract the existing reader into one suitable existing project. Give it a byte limit and cancellation token.

Check file length before allocation. Also check bytes during reading because a file can grow. Preserve no-follow opening, regular-file checks, handle validation, and disposal.

Keep secret ownership and permission checks in `SecretResolution`. Run its required handle checks before reading.

For skill activation, pass the remaining total budget into each read. Also apply the instruction-file limit where required. Preserve strict UTF-8 and the separate frontmatter limit.

## 7 Public Contracts

Keep tool schemas and stored records unchanged. Expose only the helper needed by the two assemblies.

## 8 Project/File Changes

Change the two files listed above. Add one shared reader. Reuse `MacFileHandlePath`. Extend existing skill and secret-store tests.

## 9 Ordered Tasks

1. List each caller's limits, checks, and error behavior.
2. Add tests for oversized skill files, file growth, and UNC conversion. Confirm the current failures.
3. Extract the reader. Migrate the secret caller first. Preserve its validation and error behavior.
4. Migrate activation and discovery. Pass the correct limits.
5. Remove unused duplicate methods and native declarations.
6. Run tests. Review the diff for broader file access or weaker checks.

Do not stage, commit, or push unless requested.

## 10 Testing

Test:

- Empty files, exact limits, and one byte above each limit.
- Exhausted total budget and file growth during reading.
- Cancellation and handle disposal.
- Invalid UTF-8 and frontmatter limits.
- Windows local and UNC paths.
- Links, dangling links, and file swaps during opening.
- Unix FIFOs and devices without hanging.
- Existing secret ownership, permission, and Git-policy checks.

Start with `tests/Threadsmith.Skills.Tests/SkillSubsystemTests.CompatibleCatalog.cs` and the existing secret-store tests. Use synchronization, not timing assumptions. Run required repository checks. Report unavailable platform tests as unverified.

## 11 Security/Permissions

The reader does not grant file access. Callers retain authorization. Reject unverifiable files. Never log file contents or credentials. Do not describe path checks as a complete filesystem sandbox.

## 12 Observability

Preserve current error categories. Do not add per-file success logs.

## 13 Migration/Compatibility

No migration is required. Valid skills remain valid. Oversized files fail earlier. Preserve caller-specific error behavior.

## 14 Acceptance Criteria

- [ ] Activation checks the remaining budget before large allocation.
- [ ] File growth cannot bypass the byte limit.
- [ ] Activation and discovery use the shared reader.
- [ ] Windows UNC paths remain correct.
- [ ] Unix nonregular files fail without hanging.
- [ ] Secret-store checks remain intact.
- [ ] Replaced duplicate code is removed.
- [ ] Relevant tests pass; missing platform coverage is reported.

## 15 Risks

Do not move secret-specific policy into generic defaults. Preserve platform-specific flags. Give each handle one clear owner. Handle checks do not eliminate every ancestor-replacement race.

## 16 Documentation

Record changes and test results in this plan. Update security documentation only if its guarantees change.

## 17 Open Decisions

Choose the helper's existing project and the mechanism for secret-specific handle validation. Stop if either requires a new framework or dependency cycle.
