# Semantic Input Relevance Maintenance

**Status:** Implementation, automated validation, and live filesystem verification complete. Interactive terminal presentation was not assessed.
**Delivery track:** Maintenance
**Prerequisites:** Existing semantic refresh coordinator, evaluated compiler-input inventory, and direct edit analysis.

## 1 Objective

Ordinary non-code files must trigger neither semantic refresh nor compiler diagnostics across edits, creates, deletes, and renames.

## 2 Architectural Context

Keep one semantic owner and the existing watcher, host-write attribution, publication, and advisory-analysis paths. Follow [planning governance](planning-governance.md) and [portable C# guardrails](../guardrails/portable-csharp-guardrails.md).

## 3 Scope

Share a finite C# source/build configuration catalog and evaluated source, additional-file, analyzer-configuration, and reference membership across semantic entry points.

## 4 Non-Scope

No new limits, tool permissions, compiler languages, or background execution owners.

## 5 Current State

Arbitrary non-code lifecycle notifications previously forced full refresh. Candidate analysis also treated every non-C# endpoint as requiring reconciliation, allowing unrelated edits to replace pending source feedback.

## 6 Proposed Design

`SemanticRefreshPathPolicy` owns classification. Source changes reuse incremental feedback and refresh membership when needed; configuration and registered compiler inputs refresh; unrelated files are skipped before snapshot reads or diagnostics. Registered inputs override ordinary directory exclusions; known derived build documents remain excluded.

Directory lifecycle handling maintains existing per-directory watchers. Watcher handoff compares discovered directory roots against installed coverage independently of compiler-file topology, so empty directories created during recovery receive monitoring without compiler work. Compiler ancestor membership is indexed once per evaluated inventory snapshot and reused for lifecycle admission and batch classification, including missing directories and explicit inputs beneath ignored roots. Compiler work occurs only for relevant descendants. Startup notifications await evaluated membership before batch classification.

## 7 Public Contracts

`ISourceEditAnalyzer.HasSemanticInputs` lets execution avoid semantic admission for unrelated edits. Candidate analysis returns null for such edits. No Roslyn or terminal types cross this boundary.

## 8 Project/File Changes

Core analyzer contracts, DotNet policy/coordinator/engine/registry, Execution edit application, semantic/edit regression tests, and semantic operations documentation.

## 9 Ordered Tasks

- [x] Inspect refresh, watcher topology, host-write, candidate, and rollback paths.
- [x] Share compiler-input eligibility and remove arbitrary lifecycle fallback.
- [x] Preserve pending analysis and mixed source/report candidate promotion.
- [x] Preserve startup reconciliation, directory monitoring, and explicit custom inputs.
- [x] Add regression coverage and update owned behavior documentation.
- [x] Complete solution build, focused semantic tests, mutation suite, and architecture suite.

## 10 Testing

Automated coverage exercises unrelated lifecycle events, actual transactional create/edit/move/delete, registered JSON additional inputs, references, source membership, startup reconciliation, watcher handoff, and candidate reuse. Validation: `dotnet build src/Threadsmith.sln --no-restore -v quiet` completed with zero warnings/errors. Focused `SemanticRefreshCoordinatorTests` and `SourceEditAnalysisTests`: 105 passed. Complete mutations suite: 115 passed. Architecture suite: 326 passed, one opt-in live-provider test skipped. `git diff --check` passed.

Review regression coverage disables the old watcher and creates an empty directory after replacement roots are captured, verifies watcher-only reconciliation, and detects a later physical C# create through native monitoring. An instrumented inventory verifies no repeated enumeration across 400 unrelated lifecycle notifications, preserves deleted compiler ancestor detection, and follows inventory replacement.

Live verification ran the actual headless `Threadsmith.App` from the active checkout against a disposable trusted .NET 10 project on Windows, with a local controlled provider holding an active turn. Real filesystem writes exercised native watchers without injected notifications. All 35 scenarios passed: 23 ignored changes, two incremental refreshes, and ten full refreshes, with no recovery refreshes or refresh failures. Ordinary file create/edit/rename/delete, source membership, registered JSON/analyzer configuration/reference changes, project/build configuration, report-folder lifecycle, and new-source detection in a newly watched directory were covered. Each completed refresh converged its dirty/applied versions.

Live testing exposed a native watcher-root deletion error that previously requested full recovery for report-only directories. Removed/moved roots now reuse directory lifecycle reconciliation and watcher maintenance. Regression tests preserve full refresh for registered inputs beneath ignored directories and recovery for genuine errors on existing roots. Evidence is retained in `.inbox/semantic-input-live-verification/summary.json`, `results.json`, and `final-semantic-events.json`.

## 11 Security/Permissions

Preserve repository confinement, prohibited paths, reparse protections, cancellation, and exact mutation authorization. Unrelated candidate endpoints still undergo confinement validation.

## 12 Observability

Unrelated edits emit neither compiler-check nor semantic-refresh lifecycle events. Relevant operations retain existing attribution, progress, completion, and failure handling.

## 13 Migration/Compatibility

No configuration or persisted data migration. Native host implementations and test adapters implement the updated analyzer contract.

## 14 Acceptance Criteria

[Scenario AQ](acceptance-scenarios.md#scenario-aq---external-semantic-freshness-and-request-admission) covers ordinary-file no-ops and authoritative compiler inputs. C# and graph-input refresh behavior remains intact.

## 15 Risks

Compiler membership is unavailable during initial loading, so classification must be deferred. Watcher maintenance must not hide subsequently created C# source or turn report-only directory changes into compilation.

## 16 Documentation

Updated [semantic refresh operations](../operations/semantic-refresh.md), Scenario AQ, and [MTP-256](manual-test-plan.md#mtp-256--semantic-refresh-during-active-runs). Live filesystem behavior was verified headlessly. Interactive UI presentation remains unassessed because the available terminal lacks cursor support.

## 17 Open Decisions

None.
