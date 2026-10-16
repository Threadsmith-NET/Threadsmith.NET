# Test Audit CI Infrastructure

**Status:** Active — audit remediation batches; cross-platform validation pending
**Delivery track:** Maintenance
**Prerequisites:** 2026-10-15 test audit, current solution CI contract, portable C# guardrails

## 1 Objective

Reproduce and repair recurring CI infrastructure failures, strengthen behavioral oracles, measure setup improvements, and consider only independently reviewed consolidation candidates. Consistent GitHub builds are an acceptance requirement, not an inference from isolated reruns.

## 2 Architectural Context

Use the existing test projects, tool invocation pipeline, and three-OS build workflow. The audit is evidence to revalidate, not an implementation mandate.

## 3 Scope

The user approved batch A, then authorized the remaining work. Keep file-scoped batches separately reviewable: A source fixture/reporting; B Git identity and shared-state isolation; C behavioral oracles and any exposed production defects; D measured fixture/setup improvements; E independently reviewed consolidation candidates. Every changed finding is revalidated before implementation.

## 4 Non-Scope

No blanket implementation of the audit, arbitrary test-count target, skipped correctness cases, weaker assertions, automatic merge, or unapproved Git publication. Cross-project helper sharing is not allowed by guardrail G-27; the audit does not override that rule.

## 5 Current State

Audited revision: `de5b969549adcea8c9a22d5078672d9230e62885`. Starting revision: `1b2ebfa2075363a2fa7ee0365df38b50e884d5f2`. Nine intervening files changed; the SYS01 test and CI workflow are unchanged. The baseline test resolves a physical file through CallerFilePath; the CI-property reproduction below establishes that failure locally.

The current baseline [GitHub build run 38893318723](https://github.com/Threadsmith-NET/Threadsmith.NET/actions/runs/38893318723) was retrieved read-only. All three OS jobs built successfully, then failed tests: the investigator source-file failure appears on every OS; macOS additionally reports seven Architecture repository-intelligence failures. Raw job metadata and failed-job logs are retained in `.inbox/test_audit/current-ci-*`. This supersedes relying solely on the audit's historical run.

Reviewed both changed test files since the audit: new `RepositoryMemoryBindingTests.Recall.cs` exercises enabled and disabled intelligence recall through commands, preserving ordinary recall; `ContextUsageFormatterTests.cs` adds grouped Markdown expectations and adjusts formatting/control encoding expectations. These tests and their current product behavior must be retained. The initial branch was already based on this exact newer commit.

## 6 Proposed Design

Give each investigator test invocation its own synthetic source file and guaranteed cleanup. Preserve ordinary pipeline reads and all authority/evidence assertions. Use reporting supported by the pinned runner and retain diagnostics on failed CI runs.

## 7 Public Contracts

No product API changes. Keep Windows, Linux, and macOS solution gates, existing module concurrency, and explicit skip reporting.

## 8 Project/File Changes

The batch evidence tables below identify changed files and recommendation IDs. The register and raw execution logs remain in ignored `.inbox/test_audit`; durable batch evidence belongs here.

## 9 Ordered Tasks

Reproduce infrastructure failures before editing; strengthen behavior with negative controls; measure setup changes before and after; transfer assertions before independently reviewed consolidation; finish with clean CI-property discovery, solution execution, required-case checks, and exact-commit hosted validation.

## 10 Testing

Clean Debug build with `GITHUB_ACTIONS=true`; focused execution from an unrelated working directory; full ParallelAgents and solution regression. Inspect structured results, including case counts and skip reasons. Preserve failure exit codes when capturing logs.

### Batch A evidence — 2026-10-16

Local environment: Windows x64, SDK 10.0.402 (allowed by `latestPatch`), runtime 10.0.13. Evaluated properties: `ContinuousIntegrationBuild=true`, `Configuration=Debug`, `TargetFramework=net10.0`, `Deterministic=true`. Starting checkout was clean on `test-audit/infrastructure-remediation`; changes remain uncommitted.

Commands below ran with `$env:GITHUB_ACTIONS='true'`:

```powershell
dotnet clean src/Threadsmith.sln --configuration Debug
dotnet restore src/Threadsmith.sln
dotnet build src/Threadsmith.sln --configuration Debug --no-restore
# From C:\Windows\Temp, before and after the fixture change:
& C:/source/repos/Threadsmith/tests/Threadsmith.ParallelAgents.Tests/bin/Debug/net10.0/Threadsmith.ParallelAgents.Tests.exe --filter-method '*IntelligenceInvestigators_AreNarrowAndCannotSupplyParentReceipts' --report-xunit --report-ctrf
# Rebuild the changed project before the green run:
dotnet build tests/Threadsmith.ParallelAgents.Tests/Threadsmith.ParallelAgents.Tests.csproj --configuration Debug --no-restore
dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4 --list-tests
dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4 --report-xunit --report-ctrf --diagnostic --diagnostic-verbosity Warning --output Detailed
dotnet test --project tests/Threadsmith.ModelTooling.Tests/Threadsmith.ModelTooling.Tests.csproj --configuration Debug --no-build --filter-method '*SemanticRegistry_IsolatesWorkspaceState' --report-xunit --report-ctrf
```

Focused runs used separate absolute `--results-directory` paths under `.inbox/test_audit`; the green run also enabled the workflow's diagnostic/output flags. Raw logs, runner help, evaluated properties, and copies of all 25 solution reports are retained there with the `batch-a-` prefix.

| Check | Result |
|---|---|
| Clean solution build | Passed; zero warnings/errors |
| Focused baseline | Failed, exit 2: requested repository file absent; stack/source path mapped to `/_/tests/...` |
| Changed project build | Passed; zero warnings/errors after correcting analyzer-required argument formatting |
| Focused corrected test, unrelated cwd | Passed, 1/1; no investigator temp directory left behind |
| Solution discovery | 3,907 cases in 25 assemblies |
| Solution regression | Exit 2: 3,872 passed, 1 failed, 27 skipped, 7 not-run; runner aggregates skips/not-run as 34 |
| ParallelAgents within solution run | 273 passed, 1 not-run, zero failures; 274 total |
| Failing semantic test alone | Passed, 1/1; this does not erase the full-run failure |
| Report verification | 25 xUnit XML and 25 CTRF reports; XML totals reconcile with runner summary |
| Source documentation, diff whitespace, planning bookkeeping searches | Passed; searches found no prohibited bookkeeping |
| Workflow script syntax | PowerShell blocks parse; native-crash Python block compiles |
| Hosted Windows/Linux/macOS jobs on exact proposed commit | Pending; no proposed commit created or published |

The full-run failure is the unchanged `Milestone3Tests.SemanticRegistry_IsolatesWorkspaceState` at line 495: expected `FullSemantic`, actual `PartialCompilation`. Its focused rerun passed. It uses the shared staged semantic solution; the cause was unproven at this stage; the final-regression investigation below subsequently reproduced the generated-file collision. No assertion, timeout, or scheduling adjustment was made to hide it.

SYS01 retains every original assertion and all five theory rows in this file. It adds content checks for child and parent reads, forwards runner cancellation through the affected test, and owns cleanup in `finally`. SYS03 retains the existing matrix, fail-fast setting, module concurrency, and test selection. The solution runner writes reports at root `TestResults`; the upload includes that directory as well as project-local results. Raw macOS crash reports are copied into the uploaded evidence directory before parsing.

Adversarial review traced both real `read_file` pipeline invocations, evidence recovery, and denial checks. No alternate execution path, shared fixture state, new helper framework, removed case, or weakened assertion was introduced. The audit's historical remote run was not retrieved, so local reproduction proves this path defect without claiming attribution for every historical failure. Prompt-loader deployment and Git/temp identity remain separate work.

### Subsequent batch evidence — work in progress

All batches below remain **cross-platform validation pending**. “Implemented” records a source change, not acceptance or completion. Mutations are temporary negative controls restored before green validation, not shipped changes.

| Batch / recommendation IDs | Changed files and retained or strengthened coverage | Validation and remaining risk |
|---|---|---|
| B: SYS02, UA08 | `GitQueryService.cs`, `Plan41InventoryToolTests.Identity.cs`, `RepositoryMemoryBindingTests.History.cs`: accept physical aliases of the same Git worktree while retaining logical execution paths, root/subdirectory/missing/escape guards, linked-worktree coverage, Windows casing, isolated Git configuration and owned cancellation. | Two new alias rows fail against the original root comparison. All 49 repository-memory cases passed locally, including current recall tests. Final clean Windows regression passes; macOS alias behavior still needs hosted execution. UA09 general fixture-wrapper work is deferred while logical aliases remain exercised; MN03 cross-project helper unification is rejected under G-27. The audit’s warning against blanket normalization is preserved. |
| B: SM01 | `Plan64CommandTelemetryTests.cs`: per-test trace identity and concurrent capture; unrelated real middleware activity remains as a permanent isolation canary. | Both affected tests fail before trace filtering; all 14 telemetry cases pass after it. No collection-wide serialization added. |
| B: MN05, MN17, PS02, PE04, SM06, SM13 | `MacFileHandlePathTests.cs`, `SemanticRefreshCoordinatorTests.cs`, `Milestone17CompatibilityTests.cs`, `SecretResolutionTests.cs`, `McpOAuthFlowTests.cs`, `DeployedPromptLoaderTests.cs`, `RepositoryLifecycleTests.cs`: truthful off-platform skips; separate linked-directory case; probe actual filesystem case capability. | Supported Unix link failures fail; Windows links skip only recognized unsupported/privilege errors. Adds one case, removes none. Current-source clean Windows regression passes. `eng/Test-CiRequiredCases.ps1` requires named platform contracts and retained consolidation cases to pass; synthetic reports verify all three OS selections and reject skipped/missing rows. Workflow supplies a fresh per-run/attempt results directory, preventing historical results from masking omissions. |
| B: MN01, SM05, R07 (partial) | `Milestone6Tests.cs` and `Milestone5Tests.cs`: reuse existing ProcessManager for restore/Git subprocess ownership, concurrent output, cancellation and bounded lifetime; clean failed fixture setup before ownership transfer; cancellation begins after observed process entry. | Validation 38/38, Git-worktree case 1/1 pass. Removing failed-setup cleanup makes both new failure/cancellation cases fail; fallback cleanup removes their roots. Restored two cases pass. Real child PID/handle-release oracle retained. R07 genuine transitive-graph expansion remains deferred; existing graph is not claimed to prove transitivity. |
| B follow-up: SYS03, shared-state isolation | ModelTooling `Milestone3Tests.cs`, `Plan117RoslynUpgradeTests.cs`, `SemanticCompilationCoordinatorTests.cs`, `SourceEditAnalysisTests.cs`, project file, new `TestSemanticSolution.cs` and `SemanticFixture.Build.props`: manifest-owned mutable state; immutable analyzer shared; scoped build policy; bounded abandoned-work cleanup. | Full module 1,004 passed, 21 existing skips, zero failures; build zero warnings/errors. New locked-generated-output regression passes; sharing the first root causes the expected MSBuild failure. Original cases retained plus one. Two actual fixture copies cost 78.33ms under module load; filesystem analog median 17.094ms/copy (20 samples), not a suite-speed claim. Both unchanged-source Windows solution runs pass; hosted matrix pending. |
| C: SM02, SM12, SM14, SM15 | `Plan20HardeningTests.cs`, `Plan56SessionLifecycleTests.cs`, `Plan18PersistenceTests.cs`: raw artifact bytes plus store reads, meaningful report/repair modes, late clone failure after partial copy, schema/data canaries before migration failure, durable retained data. | Persistence selection: 42 passed, zero skipped. No-op repair, report-mode mutation, committing failed clone/migration, and retention deletion with unchanged counters each fail the new assertions. Existing destination rejection remains separately covered. |
| C: exposed artifact repair defect | `RedactionAudit.cs`, `docs/user-guide.md`: use existing content-addressed StoreAsync and DeleteAsync; store sanitized content under its correct hash and remove the unsafe original. Detect unsanitized content by comparison with sanitizer output. | Original repair only incremented a counter. Repeated audit now finds no unsafe payload. Old immutable references to the removed unsafe hash become unavailable; documentation records this fail-closed behavior. No alternate artifact lookup or reference migration introduced. |
| C: SM11 | `Plan19McpTests.cs`: actual registry/pipeline entry, exact echo JSON, transport call count, policy events, denied call with no transport invocation. | Mutations discarding payload or bypassing denial fail. TrustedBuild policy matches the imported tool contract. |
| C: R01 | `Milestone1Tests.cs`: strict deep equivalence on complete event catalog through serializer and SQLite, preserving both layers and prior-schema assertions. | 14 selected cases pass. Ignoring nested Supports data fails both round-trip layers (2 failures), demonstrating payload sensitivity. |
| C: UA20, R08 | `MemoriesToolTests.cs`, `WebFetchTests.cs`: raw accounting calls alongside unique receipts; returned-after-deadline response before extraction; invalid candidates before aggregate cap with no authority. | 18 selected model cases pass. Duplicate accounting mutation fails 3 applicable rows while legitimate per-request accounting remains; deadline-check removal produces UnsupportedContent instead of Timeout and fails; invalid-candidate bypass fails 3 cases. Original transport-timeout and all six accounting theory rows retained. |
| C: MN10 | `OpenAiCompatibleModelProvider.cs`, `OpenAiCompatibleTimeoutTests.cs`: internal optional TimeProvider drives existing deadline and retry delay, default System; test observes backoff before advancing 49ms then 1ms. | 16 timeout cases pass; disabled-deadline mutation fails immediately. Caller cancellation remains distinct and attempts stay one. Latest bounded cleanup/early-completion handshake refinements pass the final clean build and Windows regression. Original 50ms scheduling race was source-confirmed, not reproduced. |
| C: UA02 | `TuiKitFrontendTests.cs`: replace formatter self-comparison with independently specified content and semantic-role assertions; retain broad Markdown fixture at widths 40 and 120. | Both rows pass; empty formatter output causes both to fail. No fixture removal. |
| C: MN08, MN14, NF02, SM03, UA12, UA16 | `Plan89CodeExploreTests.Boundaries.cs`, `Milestone3Tests.cs`, `Plan43AdvancedSemanticToolTests.cs`, `SessionConversationTests.MemoryRecall.cs`, `ContextUsageTests.cs`, `TuiMarkdownRenderingTests.cs`, `ToolActivityRenderingTests.cs`: require actual allocation, initial diagnostics, connected nonempty graph, second-run requests, initial viewport and meaningful bounded content. | Baselines: native 2, core 24 (including catalog 14), recall 1, semantic 3, clone 1 all pass. Controls removing allocation, graph nodes, second recall, small-viewport fallback, Markdown content, inspection content, or semantic diagnostics fail the protected assertions. Intentionally blank below-minimum modal size and cap-1 output remain valid. |
| D: NF01 | `Plan82CodeExploreFlowTests.cs`: stub-only policy test owns minimal source files rather than loading an unused four-project workspace; all path-policy assertions and real-workspace integration tests retained. | Three before case timings: 12.4021, 5.9417, 5.6234 seconds; after: 0.4942, 0.3572, 0.3499 seconds. Median 5.9417 → 0.3572 seconds. Fresh root and finally cleanup each run. |
| D: UA13 | `CommandHelpModalTests.cs`: bounded traversal stops at known End page; keeps all three width/height rows, usage/description tails, Home, resize, paste and escape assertions. | Three before sums: 0.6861, 0.1068, 0.1028 seconds; after: 0.0626, 0.0666, 0.0637 seconds. Median 0.1068 → 0.0637 seconds. An initial adjacent-page stability shortcut failed original 40x12 tail assertion and was discarded. |
| D: PE01 (partial) | MCP lifecycle/transports project files and fixture resolvers: explicit test-server project dependency and build staging to `AppContext.BaseDirectory/fixtures/mcp-server`; remove hardcoded Debug lookup. | Separately clean/build/test each project in both Debug and Release: each configuration has lifecycle 47 passed; transports 35 passed, 1 existing live-HTTP opt-in skip. All four builds have zero warnings/errors. Resolver uses only the configuration-specific staged path; no Debug fallback in Release. Extension/prompt deployment portions remain deferred, so the full recommendation is not marked complete. |
| E: UA01, UA03 | `CompositionBoundariesTests.cs`, `Milestone1Tests.cs`: remove implied aggregate count and exact duplicate saturation scenario. Retain all five responsibility counts (13, 11, 8, 5, 2), immutability guards and the identical SessionStatus saturation test. | Current saturation bodies matched after whitespace removal (`ua03-equivalence.txt`). Baseline runner discovery contains removed and retained cases. An extra HostCompositionInputs property fails the retained expected-size row (1 of 5); unchecked input-token addition fails the retained saturation case. Restored builds and all six retained cases pass. Final discovery retains all six cases; full Windows regression passes. CI gate requires all six retained cases on every OS. |
| E: PE03 (partial), PE11a/b/c | `ExtensionRuntimeTests.cs`, `UnloadTests.cs`: transfer discovery/activation, draining/unloaded and draining/unload-failed assertions into retained load/clean/leak tests; strengthen exact event order/session/extension identity. Leaked generation recovery now uses the original host and checks independent clean generation activation, unload and ALC death. | Full Extensions 53/53 passed, zero skips; discovery confirms three retained and three removed Facts, no theory changes. Suppressed lifecycle publication makes each retained test fail; original-host refusal also fails the recovery test. Sources restored and rebuilt green. Capability-switch and generic/element ownership portions of PE03 remain deferred, not claimed fixed. |

Cost measurements use the same Windows SDK/CI configuration and fresh test processes, with warm filesystem/build caches after the first run. They are case durations from xUnit output, not process/solution wall time or cross-platform benchmarks. Raw measurements are `cost-before.json`, `cost-after.json`, and `batch-cost-*.log`; repeat command script is `measure-cost-after.ps1`. Concurrent editor/agent activity and cache state limit causal precision; no suite-wide speedup is claimed.

### Final-regression infrastructure finding

The clean final build passed with zero warnings/errors (1m44.75s), and discovery found 3,914 cases in 25 assemblies. The first full regression reproduced `SemanticRegistry_IsolatesWorkspaceState`. Added diagnostics identify an actual concurrent write failure in the shared staged fixture: MSBuild could not access `Contracts/obj/Debug/net10.0/Contracts.GeneratedMSBuildEditorConfig.editorconfig` because another process held it. Both project confidences were FullSemantic; the MSBuild workspace failure correctly kept aggregate confidence PartialCompilation. This supersedes the earlier unknown-cause assessment. The correction gives every semantic fixture consumer an owned source/project root and independent generated build state. A manifest retains the linked source and shares only the immutable deployed analyzer DLL, which Roslyn holds for process lifetime. Explicit fixture build policy and deployed global.json preserve the original net10.0/implicit-usings evaluation. No confidence assertion or scheduling gate is relaxed. Raw evidence: `final-build.log`, `final-discovery.log`, `final-windows-run-1.log`.

The new `SemanticFixtures_BuildOutputLocksRemainLocalToOwner` regression holds an exclusive lock on the first owner's generated editorconfig while a second owner loads with FullSemantic confidence and resolves symbols. It passes with independent roots; deliberately reusing the first root fails with an MSBuild Failure diagnostic. The CI required-case gate includes this case on every OS. Existing abandoned-work tests now await workspace disposal with an independent bounded cleanup token before deleting their owned roots. Initial experiments exposed analyzer-DLL cleanup locks and target-framework evaluation drift; both were corrected while retaining the original assertions. The first clean full-suite regression passes: 3,871 passed, 37 skipped, 7 not-run, zero failures; 3,915 total. The unchanged-source repeat passes with identical counts.

Behavior mutation commands and exact fault substitutions are preserved in `.inbox/test_audit/run-behavior-mutations.py`, with each command, exit status, and output in `behavior-evidence/*.log`. Restored green commands use `GITHUB_ACTIONS=true`, Debug, `dotnet test --project ... --no-build --filter-method ... --report-xunit`; selected results are `behavior-persistence-final-green.log`, `behavior-model-final-green.log`, and `behavior-catalog-final-green.log`. Eighteen distinct behavior mutations were rejected, including seven non-vacuity controls. The final clean solution build resolved the earlier extension analyzer errors; its subsequent test failure and ownership correction are recorded separately above.

### Final local validation and disposition

Final source is on local branch `test-audit/infrastructure-remediation`, based on `1b2ebfa2075363a2fa7ee0365df38b50e884d5f2`. This is the pre-publication validation snapshot. The user subsequently authorized staging, reviewable commits, branch publication, and hosted matrix execution; merging remains excluded. All batches remain **exact-commit hosted Windows/Linux/macOS validation pending**.

| Final check | Result |
|---|---|
| Clean, restore, Debug build with `GITHUB_ACTIONS=true` | Passed; zero warnings/errors; build 1m40.67s |
| CI-settings discovery | 3,915 cases in 25 assemblies |
| Full Windows run 2, four concurrent modules | 3,871 passed, 37 skipped, 7 not-run, zero failed; 2m36.264s |
| Full Windows run 3, identical source and binaries | Same counts, zero failed; 2m08.204s |
| Required-case gate | Both runs pass all 12 Windows methods/17 cases, including new collision regression, both physical-alias rows and retained consolidation cases |
| Evidence integrity | Each run has 25 xUnit reports; totals reconcile with runner output; SHA-256 manifest unchanged between runs |
| Discovery delta | 3,907 → 3,915: 13 new cases, five independently reviewed removals, two renamed rows; every original parameterized input retained |
| Documentation, whitespace, gate syntax | Passed; packaged documentation contains 113 files; all three synthetic OS gate selections pass |
| Hosted matrix for proposed commit | Pending; no proposed commit exists yet |

Repeat commands are `.inbox/test_audit/run-final-windows.ps1 -Run 2` (clean/restore/build/discovery/full test) and `-Run 3 -SkipBuild` (unchanged-source full test). Both use the workflow's Debug, CI property, module parallelism, xUnit/CTRF, diagnostic Warning and Detailed output settings, with fresh per-run results directories. `summarize-final.py 2 3` reconciles the reports and discovery. Evidence includes `final-windows-run-{2,3}.log`, `final-run-{2,3}-summary.json`, `final-required-cases-{2,3}.log`, `final-discovery-delta.json`, and `final-tested-source-manifest.json`. These results support the local fixes, not a claim that hosted builds are consistently green.

All 110 register entries have explicit dispositions: 38 implemented, 16 rejected, 56 deferred (including partial recommendations). Genuine outstanding assertion and lifecycle gaps remain documented; no deferred finding is represented as repaired. Rejected recommendations include incompatible helper sharing and optional consolidation beyond the reviewed first tranche. Behavior proof includes 18 distinct rejected behavioral mutations; infrastructure and retained-consolidation controls are recorded separately above. Only the five reviewed redundant scenarios were removed.

Automatic approval review rejected cleanup of the identity probe and 39 directories left by the initial copied-analyzer experiment, stating “blocked by policy.” No bypass was attempted. Paths are recorded in `.inbox/test_audit/cleanup-blocked-summary.json` and `semantic-owned-cleanup-blocked.json`. These leftovers are from rejected experiments; final fixture ownership cleanup passed both full runs.

## 11 Security/Permissions

Preserve investigator restrictions, lack of child-supplied parent receipts, parent observation, memory denial, and evidence ownership checks. No staging, commits, pushes, or merging without explicit authorization.

## 12 Observability

Capture revision, SDK/RID, configuration, CI properties, test results, durations, and failure diagnostics. Do not equate local Windows success with CI completion.

## 13 Migration/Compatibility

No data migration. Fixture ownership stays within its test project.

## 14 Acceptance Criteria

Reproduced failure repaired without lost assertions or cases; supported reporting verified; exact proposed commit passes applicable Windows, Linux, and macOS jobs. Until those jobs are observed, validation remains pending.

## 15 Risks

The intermittent semantic-confidence failure was traced to shared generated MSBuild state; independent fixture ownership and its negative control now pass a complete Windows regression; hosted matrix execution remains pending. The current main run reproduces source-file and macOS repository-memory failures, but local alias evidence does not replace macOS execution. Local SDK patch resolution can differ from hosted CI. Optional native/performance/older-SDK lanes are not provisioned by this change.

Artifact repair intentionally invalidates old unsafe content hashes. Review traced existing readers: conversation reads report a missing artifact, and required source-edit snapshot reads fail rather than consume deleted content. Store-before-delete ordering preserves the original if replacement storage fails, but failure/cancellation between those operations and cross-session sanitized-hash deduplication have not received new fault-injection tests; existing artifact-store ownership semantics are unchanged.

## 16 Documentation

This document owns batch status and durable evidence. Update the local register with implemented, rejected, or deferred dispositions and reasons.

## 17 Open Decisions

The implemented batches are not complete until the exact proposed commit passes applicable hosted Windows, Linux, and macOS jobs. Final clean local solution validation and an unchanged-source repeat both pass. The reproduced semantic-confidence collision requires an ownership fix and negative-control evidence; a later pass alone is insufficient. The register distinguishes implemented source changes from validation status and records rejected/deferred recommendations; no arbitrary count target or blanket audit acceptance applies. The user has authorized reviewable commits, branch publication, and hosted matrix execution. Actual run results will be recorded in the audit register and handoff; do not infer hosted success from this pre-publication snapshot.
