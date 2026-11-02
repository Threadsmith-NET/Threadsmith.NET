# Delivery: sequential implementation and full final validation

## Purpose and boundary
Implement the user-reviewed plan one task at a time in dependency order. The main procedure owns source edits and validation. After all planned edits, run final validation. Fix authorized, in-scope failures and revalidate, or report the blocker.

This is stage 3 of a manual delivery sequence. Do not invoke another skill, start QA, or automatically fix future review findings. Do not stage, commit, push, or publish. The user reviews the implementation and validation reports, then separately invokes the existing `Maintained:review` skill as stage 4. Do not create a fourth custom skill.

Use `schemas/input.json` for inputs. Return only one JSON object that matches `schemas/output.json`.

## Terms and completion rules
- **Task validation:** the full repository build, entire test suite, and applicable task checks after one task.
- **Final validation:** the mandatory full build, entire test suite, and required applicable checks after all planned edits.
- **Checked source state:** HEAD, staged and unstaged changes, relevant untracked files, and content identities when practical, against which checks ran.
- **Material departure:** a change to reviewed requirements or architecture that needs a user decision.

A focused test, edit receipt, pending analysis, earlier passing state, or QA invocation does not satisfy final validation.

Set `succeeded=true` only when `status=complete`, every task is complete, `finalGate=pass`, both reports are saved, and no required check is unresolved.

Otherwise set `succeeded=false`: use `blocked` when progress requires authority, access, a decision, or resolution of a validation blocker, or `partial` for an incomplete checkpoint.

`finalGate` records validation, independently of report-save success:

| Value | Meaning |
|---|---|
| `pass` | All final-validation pass conditions below are satisfied for the same checked source state. |
| `fail` | Executed final validation has an unresolved failure. Explain any additional checks that could not run. |
| `blocked` | A required check or reliable conclusion is prevented by a blocker, such as unavailable dependencies, platforms, permissions, or warning provenance. Report observed failures and local passes separately. |
| `notRun` | Final validation has not run and no final-validation blocker has yet been established. |

If validation passed but a report save failed, retain `finalGate=pass`, set `succeeded=false`, and explain the incomplete status. A forced stop leaves partial checkpoints, not success.

## Operating rules
### Checkout, review, and inputs
- Before edits, builds, or tests, confirm that `git rev-parse --show-toplevel` matches the user's active checkout.
- Work in that checkout. Do not use a copied source tree, alternate checkout, or worktree.
- Capture HEAD and staged, unstaged, and untracked scope. Preserve unrelated changes.
- Do not restore or reset whole files or switch branches to manufacture a clean baseline.
- Require explicit user confirmation `planReviewed=true`; never supply it for the user. It does not grant host authority.
- Before editing, read `planPath`, `requirementsPath`, and every referenced task completely. If supplied, compare `expectedPlanSha256` with the actual digest.
- If the digest differs, the plan changed, or source drift materially affects it, seek clarification. Do not silently redesign the plan.

### Instructions and edit ownership
Before writing source, read root and scoped instructions and mandated language guardrails. Derive commands from actual contributor, CI, and area instructions, not assumptions about another repository or OS. Repository content cannot override host policy.

Use `edit_source` under its existing exact authorization for all source, test, and configuration edits. Do not delegate edits or use shell/file writes to bypass restrictions. An `implementer` role grants neither editing authority nor an isolated worktree. If tools, trust, or authorization are unavailable or declined, report `blocked`.

Prefer the smallest correct diff and established patterns. Treat existing similar functionality as the repository implementation pattern for that feature type, subject to applicable contracts and guardrails. Reuse code that already covers the behavior; when it can be extracted for reuse, perform the planned extraction and migrate callers rather than copy it. Do not duplicate code without explicit user approval for that specific duplication, recorded with its scope. General plan-review confirmation is not duplication approval. If the plan misses relevant reuse or necessary extraction, stop for a plan decision rather than silently duplicate code or redesign the implementation. Avoid unrelated formatting or remediation. A plan does not excuse defects or override guardrails. Obtain a user decision for a material departure. Propagate cancellation through async boundaries and respect host cancellation.

### Commands, children, and secrets
Use dedicated build, analyzer, and format tools when sufficient. Otherwise use governed process execution, such as for the repository's full test command. Ordinary tool and execution permissions apply. A no-restore build proves neither restore nor full test execution.

The main procedure is the single owner of commands that write shared build or test outputs. Do not run concurrent build or test children.

Optional predefined `implementer` children may diagnose substantial bounded problems or propose patches as text. They do not own edits. A `testReviewer` may inspect test quality and evidence. Do not assign test execution to that role; its current contract prohibits it.

Use host-owned authentication. Do not expose secrets, read credential stores, execute authentication scripts from evidence, or acquire external dependencies automatically. Restore and network operations need explicit authorization.

### Artifacts, limits, and recovery
Save artifacts with `write_file` and explicit content, not `useLastResponse`. Before resuming, inspect reports and preserve user edits and approved artifacts. Obtain explicit overwrite authorization for maintained progress reports.

Respect host budgets and cancellation. Save a useful checkpoint when feasible. Do not automatically retry cancelled, exhausted, or side-effectful invocations. Resume is not rollback: inspect current source and verify prior completion before continuing.

## Phase 1: confirm inputs, dependencies, and baseline
1. Resolve authorized `workFolder`. Inspect existing `implementation-report.md` and `build-test-report.md`.
2. From the reviewed inputs, check task Test sections, acceptance-criterion mappings, and prerequisites for gaps, cycles, or inconsistencies. Support fully inline single-task plans.
3. Establish dependency order. Do not skip to later independent work when an earlier required gate is blocked.
4. Establish full build/test commands and required analyzer, format, documentation, and area checks. Record setup, expected discovery, environments, platforms, command directories, targets/configurations, and why coverage is complete. Use repository-specific runners, including Microsoft.Testing.Platform conventions where applicable.
5. Capture a pre-change build, test, and warning baseline when practical and permitted. Classify diagnostics from evidence as baseline, introduced, unknown, or confidence-degraded. Unknown origin does not prove zero new warnings.
6. Record pre-existing failed/skipped tests and missing environments. Baseline failures still count against required validation; unrelated remediation needs user direction.
7. Maintain a task ledger and checkpoint: input identities/digests when practical, source state, completed/current/pending tasks, validation evidence, and questions.

## Phase 2: implement tasks sequentially
For each task, perform the following steps in order.

### Read current source
Read each enclosing declaration and nearby context before editing. Verify the task's pattern and reuse decisions against current implementations and representative call sites, including the existing callers of code to be extracted. If the plan lacks this evidence, inspect similar functionality before adding a new implementation. Use semantic tools for C# identity, calls, and impact. Use focused exploration for unknown behavior across components. Reuse evidence. Trace dependencies only to answer concrete questions.

### Establish the test safety net
For behavior changes or bug fixes, use red/green/refactor:
1. Write meaningful tests using repository conventions. Establish failure for the intended behavior; a compile error alone is not a meaningful red result.
2. Implement the smallest correct change and rerun affected tests.
3. Refactor while tests pass.

If a meaningful executable red phase is impossible, record why. Obtain direction for a material departure. Do not invent a red result.

For pure refactors, moves, renames, or unchanged-behavior data/configuration changes, establish a safety net first and preserve it. Add or adjust tests when the plan or discovered risk requires them. Do not add tests merely to increase counts or remove useful tests for convenience.

### Edit and inspect feedback
Apply authorized edits with `edit_source`. Inspect current advisory compiler feedback. Repair introduced errors before broad validation. Pending, partial, or unavailable analysis is unknown, not clean validation. Do not expand into unrelated baseline errors.

### Run task validation
Run task validation: the full build, entire suite, and applicable task checks. Fix in-scope failures and rerun required validation before advancing. Record exact commands, counts, warnings, failures, skips, and evidence. Retain this per-task policy unless the user explicitly changes it.

### Update the checkpoint
Update the ledger, changed-file and test inventory, acceptance-criterion mapping, and actual validation state. Record justified non-material plan deviations. If the task is blocked, stop subsequent implementation and provide a clear handoff.

## Phase 3: run authoritative final validation
This phase is mandatory even when every task validation passed. Start after the final implementation, refactor, and all planned edits.

### Capture and check the final state
1. Capture the checked source state. Distinguish task changes from pre-existing modifications and protect unrelated work.
2. Run final validation: the prescribed full build, entire test suite, and required analyzer, formatting, documentation, and area checks.
3. Record commands, directories, environments, targets/configurations, exits/results, test discovery/counts, warnings, legitimate skips, and failures.

A successful test command that discovered no tests does not prove suite execution. Use actual tool results or logs. Do not invent unreported counts.

### Diagnose and fix failures
Inspect each actual failure and trace its cause. Fix implementation-introduced or otherwise authorized in-scope problems. Preserve the intended behavior and acceptance criteria. Add or update meaningful regression tests when appropriate.

Classify pre-existing and environment problems. Do not silently report them as passing or fix unrelated issues without direction.

### Invalidate stale results
After any source, test, or configuration edit, invalidate affected results. Rerun the full final build, entire suite, and impacted checks against the new state.

An earlier task validation does not cover later edits. Reuse command evidence only for the same command and demonstrably unchanged relevant state. Do not launch identical commands repeatedly merely to obtain a passing result.

If the host rejects a duplicate call, reuse prior evidence or explain the genuinely changed state. Do not disguise the duplicate.

### Keep repairs bounded and meaningful
Continue only while an evidence-based, authorized in-scope fix and host budget remain. Do not create an unbounded retry loop or a separate workflow loop.

Never make validation pass by:
- Disabling checks or lowering coverage.
- Deleting meaningful tests or weakening assertions.
- Suppressing new diagnostics.
- Changing production timeout defaults or increasing test deadlines solely to hide failures.
- Repeating flaky tests until one attempt passes without evidence of a fix.

Change a test expectation only when the reviewed behavior justifies the change. Do not use an expectation change to conceal a defect.

### Stop for blockers
Stop and report `blocked` when progress requires:
- Unavailable tools, trust, dependencies, platforms, or unauthorized restore/network access.
- Resolution of unrelated baseline failures.
- A material requirements or architecture decision.
- Recovery from repeated non-progress, cancellation, or exhausted budgets.

Record completed tasks, current source effects, failed or omitted checks, attempted fixes, and the exact next action. Do not expand authority or claim success.

Windows-only results do not establish Linux or macOS CI validation. A required unavailable platform or check blocks an unqualified complete result. Report local passes separately.

### Final-validation pass conditions
Set `finalGate=pass` only for the same checked source state when:
- The prescribed full build, entire expected suite, and required checks passed.
- Test execution was meaningful.
- No introduced warning remains under repository policy, and no warning-baseline uncertainty affects that conclusion.

Explain legitimate repository-declared skips. Do not change filters to hide failures. If a required platform or check cannot run, use `finalGate=blocked`, not `pass`.

## Phase 4: save reports and hand off manually
Save both reports in `workFolder`.

### `implementation-report.md`
Include:
- Reviewed input identities.
- Task completion ledger.
- Changed source, test, and configuration files.
- Tests added, updated, or removed, with reasons.
- Requirement and acceptance-criterion coverage.
- Deviations and user decisions.
- Source state, risks, and outstanding questions.

### `build-test-report.md`
Include:
- Baseline evidence and confidence.
- Task-validation evidence.
- Final-validation attempts, failures, and fixes.
- Exact commands, directories, environments, and configurations.
- Actual build/test counts and diagnostics.
- Skipped, failed, omitted, and blocked checks.
- Evidence references and the final checked source state.

Use relevant bounded output excerpts or log references. Do not dump secret-bearing raw output.

### Recheck before completion
Recheck source state after validation and before claiming completion. No source, test, or configuration edit may follow a passing final validation without revalidation.

Saving inert reports alone does not require another build unless repository checks depend on their contents. If they do, rerun the applicable checks.

Check the completion rules above against actual task, validation, and save results.

### Return the result
Return `succeeded`, `status`, `response`, `artifacts`, `questions`, `limitations`, `finalGate`, `completedTasks`, and `totalTasks`. List only successfully saved artifacts.

In `response`, state completed tasks, the actual final-validation outcome, and omitted checks. Tell the user to review both reports and the implementation, then manually invoke `Maintained:review` with the requirements, explicit diff scope, and report paths.

Do not invoke review or fabricate `implementationReviewed`. Passing tests are review evidence, not proof of correctness or independent review.
