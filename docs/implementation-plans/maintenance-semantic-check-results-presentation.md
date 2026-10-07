# Semantic Check Results Presentation

**Status:** Implementation and automated verification complete; interactive terminal was not manually exercised.
**Delivery track:** Maintenance
**Prerequisites:** Existing direct-edit candidate analysis, exact-input promotion, semantic activity events and terminal projection.

## 1 Objective

Explain which advisory checks ran, their results and coverage, and reuse after commit.

## 2 Architectural Context

Preserve [mutation ownership](../architecture/mutation-model.md), [planning governance](planning-governance.md) and [C# guardrails](../guardrails/portable-csharp-guardrails.md).

## 3 Scope

Candidate/committed phase labels, syntax measurements, compiler coverage, comparable error deltas, bounded findings/omissions and reuse detail. The edit completion detail names actual changed files from the existing durable receipt, including applied subsets, and bounds large lists with a remaining-file count.

## 4 Non-Scope

No additional compiler passes, analyzer/build/test execution, write gates, queues or permissions.

## 5 Current State

Semantic completion previously supplied generic detail despite retained compiler evidence; phase and committed reuse were not visible.

## 6 Proposed Design

Carry the immutable bounded analysis snapshot on the existing completion event. Extend the existing renderer and tool activity detail. Record syntax completion and comparison availability at their existing execution boundaries.

## 7 Public Contracts

Add optional completion evidence and additive host-owned analysis properties. Missing evidence preserves historical rendering. Missing comparison evidence never establishes zero deltas.

## 8 Project/File Changes

Core contracts, DotNet analysis/promotion, Execution projections/edit tool, Interaction activity/renderer and their existing test projects.

## 9 Ordered Tasks

1. Extend measurements and events without changing execution ownership.
2. Project bounded readable findings and unknown evidence honestly.
3. Verify real candidate analysis/reuse, terminal projection and durable replay.

## 10 Testing

Real multi-project candidate tests, source-edit receipt tests, semantic activity projection/replay tests, architecture checks, product build and focused analyzer/style checks.

Verification: product solution build passed without warnings or errors. CoreRuntime (654 passed), Mutations (120 passed), ModelTooling (979 passed) and Architecture (326 passed) suites passed, with 19 optional/environment-dependent skips in total. CoreRuntime includes single/multiple edited-file names, durable replay, reuse status, bounded file lists and rejected/malformed receipts. Focused whitespace verification and `git diff --check` passed. Suggestion-level analyzer verification reported existing findings on unchanged lines; none were introduced by this change.

## 11 Security/Permissions

No new authority. Diagnostic text is bounded and sanitized by the existing renderer. Roslyn and terminal-library types remain inside their owners.

## 12 Observability

Existing semantic activities report syntax/compiler results; existing edit-tool completion reports actual edited paths, exact-input reuse and pending work. Applied paths come from the durable result and remain visible on replay; non-applied or malformed receipts never establish edited paths.

## 13 Migration/Compatibility

Existing event schema and discriminator remain unchanged; additive optional evidence supports historical payloads.

## 14 Acceptance Criteria

[Scenario AK](acceptance-scenarios.md#scenario-ak--advisory-incremental-semantic-feedback), [MTP-241](manual-test-plan.md#mtp-241--advisory-incremental-compiler-feedback), [Scenario AJ](acceptance-scenarios.md#scenario-aj--tool-and-mutation-diff-presentation) and [MTP-240](manual-test-plan.md#mtp-240--tool-and-diff-presentation).

## 15 Risks

Incomplete or bounded diagnostics must not appear as compiler-clean proof. Graph replacement has unknown before/after origins and deltas.

## 16 Documentation

Update mutation presentation contract, Scenarios AJ/AK and MTP-240/MTP-241; preserve completed milestone contracts.

## 17 Open Decisions

None.
