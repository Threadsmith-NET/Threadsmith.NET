# Test Suite Runtime and Signal Maintenance

**Status:** Complete
**Delivery track:** Maintenance — behavior-preserving test-suite optimization
**Prerequisites:** The current test suite and CI build gate; [Plan 99](plan-99-test-fixture-scope-determinism-cleanup.md) as implemented; the completed 2026-09-22 audit findings summarized below
**Evidence:** 2026-09-22 local test-suite audit in `.inbox` (ignored working note; this document contains the durable scope)
**Affected contracts:** [planning governance](planning-governance.md), [maintenance track](milestones/maintenance-track.md), [Plan 99](plan-99-test-fixture-scope-determinism-cleanup.md), [Plan 13](plan-13-test-discovery-selection-execution.md), and the existing contributor/CI test contract

## 1 Objective

Make the default developer and CI test run faster and more diagnostic by removing disproportionate setup, replacing weak tests with meaningful assertions, and consolidating repeated coverage without weakening product, security, or cross-platform behavior. Implement this in small, independently reviewable slices. Preserve the existing GitHub Actions restore/build/test, release-package, and Pages-deployment structure; optimizing tests does not authorize rebuilding those workflows.

## 2 Architectural Context

The solution currently has 25 test projects. `build.yml` restores and builds `src/Threadsmith.sln`, then runs `dotnet test --solution ... --no-build` on Windows, Linux, and macOS (with the existing per-OS module concurrency). `release.yml` separately verifies release contracts and packages six RIDs; `deploy.yml` publishes documentation to Pages. `CONTRIBUTING.md` names the same solution build and test commands as the pre-PR baseline. These are established contracts, not scaffolding to replace.

Plan 99 already reduced oversized fixtures and explicitly retained real production-boundary, Roslyn/MSBuild, filesystem, process, security, and architecture checks where those mechanisms are the contract. Its historical document is frozen. This work addresses residual runtime and signal problems identified after Plan 99; it must not repeat completed cleanup or delete a test just because it uses a large literal or rare input.

## 3 Scope

- Act on the audited candidates directly. Measure a particular test only when its cost matters to the proposed change; do not repeat the repository-wide inventory or require a new all-OS timing baseline before beginning.
- For a proposed deletion/consolidation, make a brief, local check of the behavior and neighboring assertions so the edit does not remove unique coverage. Do not create a second suite-wide contract map.
- Shrink redundant fixture work using existing test seams and share setup only within a coherent owning project when it improves readability and failure isolation.
- Replace avoidable real sleeps with signals, fake time, or explicit lifecycle events; keep watchdog timeouts for deadlock diagnosis.
- Separate correctness tests from allocation/latency experiments where doing so preserves an equivalent required correctness assertion.
- Improve test output so a long project run identifies the current/slow tests and leaves useful failure artifacts.
- Preserve or improve cross-platform coverage and the existing solution-level CI gate through every slice.

## 4 Non-Scope

- Rewriting `build.yml`, `release.yml`, or `deploy.yml`; adding a new CI framework, matrix, service, or required status check; changing packaging or publishing scripts.
- Removing all integration tests from the default command or redefining the current CI run as “unit-only.” A fast local subset may be documented after measurement, but cannot silently replace the full required gate.
- Changing production behavior, limits, public configuration, trust/approval rules, or product code solely to make tests easier, except an existing narrow internal test seam with the same production default when justified and separately reviewed.
- Weakening exact-boundary, security, portability, prompt-catalog, release, or architecture gates based on runtime alone.
- Reopening Plan 99 or completed milestone documents; mass-renaming milestone/plan tests for aesthetics; creating a cross-project mega-fixture.

## 5 Current State

The completed audit scanned 218 C# test files with 2,355 declared `[Fact]`/`[Theory]` methods. Its concrete starting list is: the recording-only `Plan89CodeExploreTests` case; stress-scale Jira ADF and provider-text cases; the >1 MB code-explore result test; the repeated compaction role matrix; allocation measurements; fixed sleeps and wall-clock assertions; and repeated full semantic workspace construction in `NativeTools`. It also identified overlap across code-explore, Jira, and prompt-catalog tests. A no-build `NativeTools` run had no named-test progress or completion after approximately two minutes and was stopped; this does **not** identify a single slow test. These findings are sufficient to start implementation. Do not rescan all 25 projects as an entry task.

`NativeTools` disables assembly parallelization because Roslyn's in-memory SQLite cache is described as process-wide on non-Windows. Do not remove that protection based on a Windows-only improvement: either show a safe cross-platform narrower isolation strategy or retain it and reduce the expensive fixtures underneath it.

## 6 Proposed Design

Use the audit as the work queue. For each selected candidate:

1. Confirm the specific test still matches the audit and identify any unique assertion before changing it. This is a focused safety check, not a new audit deliverable.
2. Make the smallest local change. Keep required correctness in the current solution test command. For a stress-only probe, retain a documented opt-in command or diagnostic only after a small required assertion covers the real contract; do not let a skip masquerade as a pass.
3. Run the focused project and neighboring contract tests, inspect skip/failure counts, then run the unchanged solution-level command before merging a slice. Record a targeted before/after duration where practical. Revert a change that only moves cost elsewhere or obscures the failure.

Test projects and existing xUnit/Microsoft.Testing.Platform infrastructure remain the owners. Prefer test-project-local helpers and supported runner output. A new project, category convention, or CI lane requires demonstrated need, a migration/compatibility review, and explicit approval before implementation; it is not the default design.

## 7 Public Contracts

No product-facing API or configuration changes. The contributor-facing contract remains the existing solution build and test command. If a useful *optional* fast local command is established, document exactly what it excludes and retain the full pre-PR/CI command beside it. Required CI success must continue to mean that the preserved correctness, architecture, and integration checks ran (or were explicitly and visibly skipped for existing environment-gated live tests).

## 8 Project/File Changes

Expected changes are focused files under `tests/` and, only if measurement demonstrates a need, a small diagnostic adjustment to `.github/workflows/build.yml` that leaves its three-OS matrix, restore/build/test sequence, and test command intact. `CONTRIBUTING.md` changes only if a supported optional command or required check actually changes. No planned changes to `release.yml`, `deploy.yml`, `eng/release/`, acceptance scenarios, manual test procedures, or completed milestone/Plan 99 documents.

## 9 Ordered Tasks

### Slice A — immediate weak-signal and high-volume cases

1. Fix or retire `Plan89CodeExploreTests.CodeExplore_AllocationComparison_RecordsCurrentBehavior`, whose only behavioral assertion is non-null coverage. Preserve useful manual-report instructions if needed.
2. Tackle the audited 100,000-chunk provider-text test and 20,000-mark/50,100-row Jira cases. Check whether the exact production number is the contract; if not, use the smallest distinguishing input or make only the performance experiment opt-in after a required correctness replacement.
3. Review the audited >1 MB code-explore rendering case. Preserve its large-file product contract via the least costly real-path test; test option propagation and formatter behavior with smaller fixtures where possible.

### Slice B — coordination and repeated setup

4. Replace the fixed sleeps/negative-event timing checks identified in the audit with signals or injectable time where semantically equivalent. Keep one real watcher/process test when OS behavior is the contract, with a bounded diagnostic timeout.
5. Reduce repeated expensive setup in the audited 12-row compaction/evidence theory only after checking which rows exercise distinct branches. Keep representative end-to-end child execution/evidence cases and test role/profile mapping with small inputs where equivalent.
6. Use named-test progress or timing to isolate the `NativeTools` long tail, then reduce repeated workspace construction in the responsible fixtures. This is targeted diagnosis of the already-observed stalled project, not a new suite-wide profiling phase. Reuse only immutable inputs safely; do not share mutable semantic registry/workspace state. Evaluate narrower xUnit isolation only after Windows, Linux, and macOS evidence demonstrates the Roslyn cache hazard is contained; otherwise retain assembly serialization.

### Slice C — consolidation where the audit found overlap

7. Compare only the identified code-explore tests across Plans 81/85/89/94, Jira ADF expansion tests, and prompt-catalog architecture checks. Consolidate duplicated *arrangement* or identical assertions; preserve distinct tool, semantic, output, security, and deployed-asset paths. Do not build a new repository-wide contract matrix.
8. Split a large mixed-concern test file only when the targeted edits expose duplicated setup or poor failure locality. File splitting alone is not a performance win.
9. Run the unchanged full solution command and summarize retained coverage, skips, targeted before/after times, and any remaining long tail. Do not claim an unmeasured speedup.

Each slice can land independently. Start with Slice A; later work follows the audited candidates, not a mandate to re-examine every test project.

## 10 Testing

- Before each slice: use the audit finding, check the affected test and nearby assertions, and record targeted duration/result/skips only where needed to evaluate the edit.
- After each slice: run focused tests, architecture gates if relevant, then `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` and the existing solution test command with `--max-parallel-test-modules 4` on Windows/Linux or `1` on macOS. Verify all required checks are still discovered.
- For concurrency/scheduler changes: repeat focused runs, include cancellation/failure paths, and test non-Windows before changing Roslyn serialization.
- For any opt-in stress probe: run it explicitly at least once in its supported environment and document the command and owner. A default skip must be reported as a skip, not counted as preserved required coverage.
- Compare like-for-like wall times for changed hot spots when practical. Report measurements, not source-based runtime estimates or unmeasured percentage targets; do not require a fresh all-project timing census.

## 11 Security/Permissions

Keep real symlink/reparse, path containment, secret-redaction, OAuth callback, malformed-provider-output, release-license, and prompt-asset boundary tests in the required gate unless an equivalent real-path replacement is demonstrated. Do not reduce production limits or hide failures with blanket retries, skips, or swallowed cleanup errors. Test-created roots and processes remain explicitly owned and safely cleaned.

## 12 Observability

The test workflow should show which project failed or stalled and, for the observed `NativeTools` long tail, the named long-running case or a usable test result artifact. Diagnostic output must avoid raw secrets, full model transcripts, and huge source payloads. Record targeted measurements with OS, SDK, project/case, elapsed time, outcome, and skip count where collected.

## 13 Migration/Compatibility

No workflow migration is required. The existing required `build.yml` job continues to call the same solution test command on its current OS matrix throughout the work. Release packaging and Pages deployment are unaffected. Introduce optional local/diagnostic commands additively; do not change branch protection or required check names as part of this maintenance plan. If a future CI split is independently justified, propose it as a separate decision with equivalent coverage and explicit approval.

## 14 Acceptance Criteria

1. The audited candidates are addressed in focused slices without repeating the suite-wide audit. The observed `NativeTools` long tail is identified by named test or the runner's diagnostic limitation is recorded.
2. Each removed or consolidated test has a written contract/coverage disposition; no required product or security contract is silently lost.
3. The recording-only Plan 89 case is made meaningful or removed from the required suite, and identified stress/measurement cases are proportionate or deliberately opt-in with required correctness replacements.
4. Avoidable fixed sleeps and redundant high-volume fixtures in the measured hot paths are reduced without adding global mutable test state or flaky scheduling assumptions.
5. The same solution-level CI test command remains a reliable cross-platform gate; `build.yml` retains its existing restore/build/test sequence and OS matrix, and release/deploy workflows are unchanged.
6. Focused and full test results, skip counts, targeted before/after times where collected, and residual limitations are recorded. A performance improvement is claimed only where measured.

## 15 Risks

- **False savings:** moving a large test to an opt-in path can make CI faster by removing coverage. Check its unique assertion and add a required replacement first.
- **Roslyn concurrency:** removing assembly serialization may create non-Windows corruption or nondeterminism. Keep it until supported cross-platform evidence proves a narrower strategy.
- **Measurement noise:** CI machine contention and JIT/cache effects can dominate short runs. Repeat comparisons and avoid hard thresholds based on one sample.
- **Over-consolidation:** a shared mega-fixture can conceal distinct failures. Prefer small local helpers and explicit end-to-end cases.
- **Historical overlap:** Plan 99 already made proportional-fixture decisions. Review those retained boundaries before changing them; this plan does not supersede that completed record.

## 16 Documentation

This active document owns status and results. `README.md` gets one navigation row. Update `CONTRIBUTING.md` only when a tested optional local command is introduced or its required command actually changes. No acceptance-scenario or manual-test-plan change is expected because product behavior and user-executable behavior remain unchanged.

## 17 Open Decisions

- Whether supported MTP/xUnit output is enough to identify slow named `NativeTools` cases without any CI YAML edit; decide during the targeted `NativeTools` slice.
- Whether any stress/measurement cases need a maintained opt-in command after small correctness replacements; decide per contract, not by a blanket category rule.
- Whether `NativeTools` serialization can be narrowed safely across all supported OSes; default is to keep it.

## 18 Implementation Results (2026-09-23)

Implementation completed on Windows `10.0.26200` x64 with .NET SDK `10.0.303`. The existing workflow files, release/deploy paths, contributor command, production behavior, and assembly-wide `NativeTools` serialization were not changed.

### Coverage dispositions

- Removed `CodeExplore_AllocationComparison_RecordsCurrentBehavior`. It had no unique oracle beyond non-null coverage; exact-source allocation, configured ceilings, continuation, per-file caps, and adaptive-budget behavior remain asserted by the neighboring Plan 89 configuration/boundary tests.
- Added a modest required disabled-output-cap/current-source case and retained the >1 MiB real Roslyn/output path as an explicit `Performance` probe.
- Replaced the required 100,000-chunk provider-text allocation loop with a 64-chunk exact-content contract. The original 100,000-chunk allocation probe remains explicit.
- Added a small exact nested-mark Jira assertion and retained the 20,000-mark allocation probe as explicit. The exact 50,000-item Jira projection-work limit remains required because that fixed resource boundary is the contract; its fixture now builds JSON directly instead of allocating and serializing 50,000 anonymous rows.
- Marked the Plan 68 allocation harness explicit. Removed the 1,200-entry context-usage allocation experiment after Visual Studio timing showed that explicit selection still imposed disproportionate cost; its coarse 10 MB ceiling had no unique correctness oracle. The required context-usage resize/safe-label matrix exercises the same native modal path with a representative 20-entry inventory.
- Reduced the compaction/evidence end-to-end matrix from 12 rows to four distinct branch combinations and moved its 300,000-character token-scale probe to explicit execution. Reviewer-role/profile mapping remains covered by `ModelExplorerAssignmentRunnerTests.Roles`, `AgentRoleOutputTests`, and related cheap role-contract tests.
- Consolidated seven Plan 94 theory arrangements so each coherent method loads one isolated semantic registry/workspace and evaluates all of its existing inputs sequentially. A class fixture owns only the immutable repository files; registries and workspaces remain per-test and are never shared. Query text is written to test output for failure locality.
- Code-explore tests across Plans 81/85/89/94 retain distinct semantic, artifact, formatter, continuation, graph, and output contracts. Jira ADF mechanisms remain distinct. The two prompt-catalog architecture checks retain separate deployment-bijection and documentation/glossary obligations, so no assertion consolidation was justified there.
- Replaced the two-second sibling fake-runner delay with cancellation completion, removed an unnecessary post-recovery watcher sleep after `EnsureCurrentAsync` had drained the worker, and replaced MCP elapsed-time assertions with bounded task/process watchdogs. One 200 ms negative real-watcher observation remains because OS event non-delivery is the contract.

### Diagnostics and measurements

The `NativeTools` project was run with xUnit detailed output, `--long-running 10`, a five-minute watchdog, and TRX output. The runner named every completed test; no diagnostic limitation or unidentified hang remained. The longest baseline case was the removed Plan 89 recorder at 10.280 seconds. The residual long tail is many serialized Roslyn/MSBuild integration cases, generally 1.5-3 seconds each, rather than one stalled case. Without three-OS evidence that Roslyn's non-Windows process-wide cache hazard is contained, assembly serialization remains in place.

Like-for-like Debug/no-build measurements on this host:

| Scope | Before | After | Result |
|---|---:|---:|---|
| `Plan94CodeExploreAgentQualityTests` | 39 tests, 1m 41.158s | 23 tests, 1m 10.020s | All original input rows passed; repeated workspace loads removed |
| Compaction/evidence theory | 12 rows, 3.552s | 4 required rows, 2.095s | All required rows passed; scale probe explicit |
| `NativeTools` project | 176 passed, 4m 52.758s | 159 passed, 1 skipped, 4m 00.796s | 52-second (17.8%) measured reduction |
| Jira empty-row work-limit case | 562ms | 499ms | Exact 50,000-item limit retained |
| Context-usage native modal | 1,200-entry allocation probe, 553ms in isolated CLI execution and reported as disproportionate in Visual Studio | 5-row required resize/safety matrix, 430ms total; slowest row 64ms | Coarse allocation experiment removed; modal rendering, resize, navigation, sanitization, and chart output remain required |

The first three comparisons use runner-reported durations from isolated executions; process startup time was excluded. Short-case measurements are retained as evidence only and are not CI thresholds.

### Verification

- All six retained explicit `Performance` probes passed when invoked with `--explicit only --filter-trait Category=Performance` across `ModelTooling`, `ParallelAgents`, `CoreRuntime`, and `NativeTools`. The supported per-project form is:

  ```powershell
  dotnet test --project <test-project.csproj> --configuration Debug --no-build --filter-trait Category=Performance --explicit only
  ```

- `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` passed with 0 warnings and 0 errors.
- The unchanged Windows solution command passed after the Visual Studio follow-up in 4m 44.538s: 3,547 total, 3,519 succeeded, 28 skipped, 0 failed.

  ```powershell
  dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4
  ```

The 28 skips include six explicit performance probes plus existing environment/platform-gated integration cases. Local Linux/macOS execution was not available; the unchanged three-OS CI matrix remains the cross-platform verification owner.
