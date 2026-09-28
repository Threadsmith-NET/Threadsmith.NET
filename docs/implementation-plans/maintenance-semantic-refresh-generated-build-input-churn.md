# Semantic Refresh Generated Build-Input Churn Maintenance

**Status:** Planned.
**Delivery track:** Maintenance — prevent derived MSBuild output from causing redundant semantic reloads
**Prerequisites:** Implemented [Plan 97](plan-97-external-semantic-refresh.md), the current `SemanticRefreshCoordinator`, `SemanticEngine` refresh inventory, shared semantic refresh path policy, and deterministic semantic-refresh test seams
**Strategy source:** [Shared implementation context](00-shared-context.md), especially one host-owned refresh authority, immutable Roslyn snapshots, controlled publication, cancellation propagation, bounded observation, and maintenance-track routing
**Related contracts:** [planning governance](planning-governance.md), [maintenance track](milestones/maintenance-track.md), [Plan 97](plan-97-external-semantic-refresh.md), [Scenario AQ](acceptance-scenarios.md#scenario-aq---external-semantic-freshness-and-request-admission), [MTP-256](manual-test-plan.md#mtp-256--external-semantic-refresh-blocked-admission-and-manual-recovery), [semantic confidence](../architecture/semantic-confidence.md), [root AGENTS](../../AGENTS.md), and [portable C# guardrails](../guardrails/portable-csharp-guardrails.md)

---

## 1 Objective

Prevent ordinary build and restore output from starting a semantic refresh when MSBuild rewrites derived `obj/**/<Project>.GeneratedMSBuildEditorConfig.editorconfig` files already present in Roslyn's analyzer-config inventory.

Preserve the Plan 97 freshness boundary for real source, project-system, analyzer-config, and additional-document changes. In particular, a user-authored file explicitly loaded from an otherwise ignored directory must continue to refresh; the correction applies only to a narrowly proven MSBuild-derived artifact whose authoritative inputs are the project and build configuration files already covered by full-refresh classification.

## 2 Architectural Context

Plan 97 owns one workspace-scoped `SemanticRefreshCoordinator`. Filesystem notifications first pass through `IsPotentiallyRelevant`, then settled changes pass through `Classify`. Production inventory comes from the current Roslyn solution, and loaded document identities can add exact non-recursive watcher roots beneath normally ignored directories.

`SemanticRefreshPathPolicy.IsIgnoredPath` already treats `obj` and `bin` as generated build-output directories. `SemanticRefreshCoordinator`, however, admits a path found in `AnalyzerConfigDocuments` or `AdditionalDocuments` before applying that general ignored-path result. Its later classifier likewise keeps loaded additional and analyzer-config documents as relevant exceptions. That behavior is intentional for explicitly loaded user-owned inputs, but it also admits Roslyn's generated MSBuild editor-config documents under `obj`.

The correction belongs in the existing path-policy, inventory, watcher-root, and coordinator flow. It must not add a second watcher, refresh authority, state store, or semantic lifecycle.

## 3 Scope

- Add one shared path-policy classification for MSBuild-generated editor-config artifacts beneath build-output directories.
- Exclude those derived artifacts from production refresh inventory/loaded-identity projections while leaving them in the Roslyn solution used for compilation.
- Defensively reject the same derived paths at coordinator notification admission and settled-change classification.
- Prevent a derived artifact from adding an explicit watcher root beneath `obj`.
- Preserve full refresh for user-authored `.editorconfig`, `.globalconfig`, analyzer configuration, additional documents, project/solution files, props/targets, references, and membership changes.
- Preserve the existing exception that an explicitly loaded, non-derived additional or analyzer document beneath an ignored directory can be watched and refreshed.
- Add deterministic regressions for direct observation, watcher-root construction, real production inventory projection, and unaffected legitimate inputs.
- Record the corrected build-output case in Scenario AQ and MTP-256 when implementation lands.

## 4 Non-Scope

- No change to compaction, model requests, tools, active-run context, or Plan 113 behavior.
- No general suppression of `AnalyzerConfigDocuments` or `AdditionalDocuments` under `obj`, `artifacts`, or another normally ignored directory.
- No change to Roslyn compilation inputs or removal of generated editor-config documents from the loaded `Solution`.
- No weakening of request-admission freshness, dirty/applied version convergence, watcher recovery, manual `/semantic_refresh`, or generation fencing.
- No attempt to interpret arbitrary build output as authoritative source.
- No new configuration switch, repository-controlled ignore pattern, persistence migration, public DTO, domain event, or telemetry payload.
- No broad watcher rewrite or replacement of `FileSystemWatcher`.

## 5 Current State

An interactive run on 2026-09-25 produced the intended active-context reduction while a separate semantic refresh ran concurrently. The semantic lifecycle recorded:

- reason `ExternalChange`;
- mode `Full`;
- one path when the refresh started;
- 25 paths and dirty version 110 by convergence; and
- 137,000 milliseconds elapsed.

During that refresh, exactly 25 project-specific files matching `src/*/obj/Debug/net10.0/*.GeneratedMSBuildEditorConfig.editorconfig` were rewritten. No tracked source, project, props, targets, solution, or user-authored analyzer-config file had a matching write timestamp in the observed interval. The persisted lifecycle intentionally does not retain raw paths, so the first notification cannot be named conclusively; the 25-path convergence count and file timestamps establish the generated editor-config feedback strongly enough to define a deterministic regression.

The current implementation has three relevant ownership points:

1. `SemanticEngine.GetRefreshInventory` and `GetLoadedDocumentsAsync` project Roslyn text-document membership into refresh inventory and identity tracking.
2. `SemanticRefreshCoordinator.IsPotentiallyRelevant` admits loaded analyzer/additional documents before its general ignored-path check.
3. `SemanticRefreshCoordinator.Classify` exempts loaded analyzer/additional documents from the general ignored-path rejection, while `WorkspaceBinding.GetWatcherRoots` adds exact roots for every applied identity.

Existing tests correctly prove that arbitrary loaded additional documents under `obj` can receive a watcher root and refresh. They also prove generated C# source under build-output directories is ignored. There is no equivalent regression for generated MSBuild editor-config documents.

## 6 Proposed Design

### 6.1 One narrow derived-artifact predicate

Extend `SemanticRefreshPathPolicy` with one case-insensitive, path-normalized predicate for a generated MSBuild editor-config document. It returns true only when both conditions hold:

1. the path is beneath a recognized build-output directory, initially `obj`; and
2. the filename matches the MSBuild convention `*.GeneratedMSBuildEditorConfig.editorconfig`.

Do not classify arbitrary `.editorconfig`, `.globalconfig`, JSON additional documents, or user-selected analyzer inputs as derived merely because they are loaded or reside under a directory with an unfortunate name. Keep path confinement, prohibited-path, reparse-point, and platform-comparer behavior unchanged.

### 6.2 Filter refresh projections, not Roslyn compilation

Keep the generated analyzer-config document in the Roslyn `Solution`; it remains an MSBuild evaluation product used by compilation. Filter it only from the refresh-specific projections returned by `SemanticEngine.GetRefreshInventory` and `GetLoadedDocumentsAsync`.

This prevents production composition from seeding an applied identity or exact watcher root for the derived path. Project files, `Directory.Build.*`, props/targets, SDK selection, and other authoritative graph inputs remain in their existing full-reload inventory and continue to refresh the solution when their contents change.

### 6.3 Defend the coordinator boundary

Apply the shared predicate before loaded analyzer/additional exceptions in both `IsPotentiallyRelevant` and `Classify`. This keeps alternate/test backends and future inventory implementations from reintroducing the churn by reporting the generated file as loaded.

When binding or replacing applied identities after a full refresh, do not retain a derived generated editor-config identity even if a backend returns one. Reuse the same predicate rather than duplicating filename logic. This guarantees that a full refresh cannot recreate the explicit `obj` watcher root that caused its own follow-up work.

### 6.4 Preserve legitimate loaded ignored-directory inputs

Retain current handling for a non-derived loaded file such as `obj/loaded.json` or an explicitly included custom analyzer configuration. Such a file remains in refresh inventory, receives its exact non-recursive watcher root when required, and forces the existing supported refresh mode when changed.

The implementation must demonstrate this distinction directly. Passing a broad `IsIgnoredPath` check before every inventory lookup would break an existing Plan 97 contract and is therefore not an acceptable fix.

## 7 Public Contracts

No public contract changes.

The corrected observable behavior is that a build or restore which changes only derived generated editor-config artifacts does not publish `SemanticRefreshStarted`, block later request admission, advance dirty/applied versions, or run a semantic reload. Changes to authoritative project inputs and explicitly loaded user-owned semantic inputs continue to use the existing Plan 97 lifecycle.

Existing event schemas, `SemanticRefreshReason`, `SemanticRefreshMode`, `/semantic_refresh`, confidence levels, and headless/interactive projections remain unchanged.

## 8 Project/File Changes

- `src/Threadsmith.DotNet/SemanticRefreshPathPolicy.cs` — shared derived generated-editor-config classification.
- `src/Threadsmith.DotNet/SemanticEngine.cs` — filter only refresh inventory and loaded-identity projections; preserve the Roslyn solution.
- `src/Threadsmith.DotNet/SemanticRefreshCoordinator.cs` — defensive admission/classification and applied-identity/watcher-root filtering through the shared policy.
- `tests/Threadsmith.ModelTooling.Tests/SemanticRefreshCoordinatorTests.cs` — focused path-policy, coordinator, watcher-root, inventory, and compatibility regressions.
- `docs/implementation-plans/acceptance-scenarios.md` — add build-output silence to Scenario AQ when implemented.
- `docs/implementation-plans/manual-test-plan.md` — add the build/restore case to MTP-256 when implemented.
- `docs/implementation-plans/maintenance-semantic-refresh-generated-build-input-churn.md` — this active maintenance document and eventual completion evidence.
- `docs/implementation-plans/README.md` — navigation row only.

## 9 Ordered Tasks

1. Re-read root `AGENTS.md`, the portable C# guardrails, Plan 97, Scenario AQ, MTP-256, and the semantic refresh coordinator tests before editing C#.
2. Trace generated analyzer-config paths through production `SemanticEngine` inventory, loaded-document identities, coordinator relevance/classification, full-refresh replacement, and watcher-root construction.
3. Add the narrow shared path-policy predicate with Windows and Unix separator/case coverage.
4. Filter generated MSBuild editor configs from production refresh inventory and loaded-document identity projection without removing them from the Roslyn solution.
5. Apply the same shared policy defensively at coordinator notification admission, settled classification, initial identity seeding, and full-refresh identity replacement.
6. Add a deterministic test whose backend reports a generated editor config as both loaded and analyzer-config inventory; prove direct observation remains a no-op with zero refresh calls and no lifecycle events.
7. Add a watcher-root test proving that the generated file cannot add an `obj` watcher root.
8. Preserve and strengthen the existing test proving a non-derived explicitly loaded file beneath `obj` still adds an exact watcher root and refreshes.
9. Add a production-inventory test proving the generated config is absent from refresh projections while a repository-authored analyzer config remains present and compilation loading still succeeds.
10. Run focused semantic refresh tests, the full Model Tooling test project, architecture tests, and the solution build.
11. Perform an independent adversarial review against the real watcher, inventory, full-refresh, and request-admission paths; fix valid findings and rerun affected checks.
12. Update Scenario AQ, MTP-256, and this document with final verification evidence. Leave completed Plan 97 unchanged.

## 10 Testing

Focused automated coverage must verify:

- nested `obj/**/<Project>.GeneratedMSBuildEditorConfig.editorconfig` paths are recognized case-insensitively on supported path shapes;
- a same-named file outside a build-output directory is not suppressed solely by its name;
- arbitrary repository-authored `.editorconfig`, `.globalconfig`, additional documents, and analyzer inputs retain current classification;
- a backend-reported generated analyzer config cannot make `IsCurrent` false, advance refresh work, invoke the backend refresh, or publish `SemanticRefreshStarted`, `Completed`, or `Failed`;
- a generated config cannot add an exact ignored-directory watcher root during bind or after full refresh;
- a non-derived explicitly loaded additional/analyzer document beneath `obj` remains watched and forces the expected full refresh;
- a real project load excludes the generated editor config from refresh inventory/identity projection while keeping user-authored analyzer configuration and a usable semantic solution;
- real `.cs`, `.csproj`, solution, props/targets, and repository-authored analyzer-config changes retain existing incremental/full classification; and
- manual `/semantic_refresh` still forces a full refresh when the workspace is otherwise clean.

Verification commands:

```powershell
dotnet test tests\Threadsmith.ModelTooling.Tests\Threadsmith.ModelTooling.Tests.csproj --no-restore --filter-class Threadsmith.ModelTooling.Tests.SemanticRefreshCoordinatorTests
dotnet test tests\Threadsmith.ModelTooling.Tests\Threadsmith.ModelTooling.Tests.csproj --no-restore
dotnet test tests\Threadsmith.Architecture.Tests\Threadsmith.Architecture.Tests.csproj --no-restore
dotnet build src\Threadsmith.sln --no-restore
git diff --check
```

For live verification, open a disposable trusted solution, wait until semantic state is current, run a no-source-change `dotnet build`, and wait beyond the configured settle/burst interval. Confirm no external semantic-refresh lifecycle appears and the next request starts without refresh admission delay. Then edit a loaded `.cs` file and a repository-authored `.editorconfig` in separate passes and confirm the existing incremental and full refresh paths still occur.

## 11 Security/Permissions

The change does not expand filesystem authority. All watcher paths remain confined to the active repository, prohibited paths and reparse points remain rejected, and repository configuration cannot add ignore patterns or suppress freshness for authoritative inputs.

Do not log raw changed paths, source, generated file content, project evaluation values, or exception dumps in ordinary lifecycle output. Tests use disposable paths and synthetic content only.

## 12 Observability

No new domain event or telemetry payload is required. Correct behavior for derived build churn is deliberate silence: no semantic refresh lifecycle, no dirty-version advance that survives reconciliation, and no admission wait.

Existing refresh lifecycle and metrics remain authoritative for real changes. Automated tests should observe the event stream and backend refresh counter rather than infer success only from elapsed time. The implementation record may retain bounded aggregate evidence such as refresh count and duration, but not raw repository paths from user environments.

## 13 Migration/Compatibility

No migration is required. Existing sessions, semantic state, persistence, provider configuration, and repository configuration remain compatible.

The fix takes effect after application restart. A generated artifact already queued before upgrade may complete under the old process; the new process rebuilds its refresh-specific identity and watcher projections during semantic binding.

## 14 Acceptance Criteria

- A rewrite, create, delete, or rename notification for `obj/**/<Project>.GeneratedMSBuildEditorConfig.editorconfig`, even when reported in loaded analyzer-config inventory, produces no semantic refresh work or lifecycle event.
- Binding and full-refresh reconciliation do not add an applied identity or exact watcher root solely for a generated MSBuild editor config.
- Generated editor configs remain available to Roslyn compilation; only refresh inventory and monitoring projections exclude them.
- A repository-authored analyzer config outside generated build output still forces a full refresh.
- A non-derived explicitly loaded additional or analyzer document beneath an ignored directory retains its exact watcher and refresh behavior.
- Source edits still refresh incrementally; project graph, membership, reference, and user analyzer-config changes still refresh fully.
- `/semantic_refresh`, failure recovery, dirty/applied convergence, cancellation, rebinding, and request-admission gates retain existing behavior.
- Focused regressions prove zero backend refresh calls and zero refresh lifecycle events for generated build churn without relying only on sleeps.
- Scenario AQ and MTP-256 include the corrected build-output case when implementation lands.
- Focused Model Tooling tests, the complete Model Tooling project, architecture tests, the solution build, and `git diff --check` pass.
- Independent adversarial review finds no valid remaining defect in watcher ownership, inventory filtering, legitimate loaded-input compatibility, convergence, cancellation, or observability.

## 15 Risks

- **Suppressing an authoritative input:** constrain the predicate to the exact MSBuild-generated filename convention under build output; prove repository-authored configs still refresh.
- **Filtering only one entry point:** apply one shared predicate to production projections and defensive coordinator boundaries so full refresh cannot recreate the watcher root.
- **Breaking explicit ignored-directory documents:** retain and rerun the existing loaded `obj/loaded.json` contract; do not move the general ignored-path check ahead of all inventory exceptions.
- **Hiding project-property changes:** project, props/targets, SDK, package, reference, and graph-control inputs remain authoritative and continue to force full reload; only their derived editor-config output is ignored.
- **Platform mismatch:** normalize paths through existing helpers and cover separator and case behavior without hand-built substring checks.
- **Flaky watcher testing:** assert watcher roots and direct coordinator observation deterministically; keep one bounded filesystem watcher integration case as secondary evidence.

## 16 Documentation

On implementation, add one build/restore-output case to Scenario AQ and MTP-256. State that derived generated editor configs are ignored while repository-authored analyzer configuration remains authoritative.

No user-guide or operations change is required unless implementation changes a visible command, diagnostic, recovery procedure, or supported configuration. Keep completed Plan 97 frozen and record completion only in this maintenance document.

## 17 Open Decisions

None. The fix is intentionally narrow: classify the known MSBuild-derived artifact once, preserve it in Roslyn compilation, and exclude it from refresh monitoring and identity projections.
