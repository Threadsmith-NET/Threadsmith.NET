# Plan 117 — Roslyn and .NET semantic toolchain upgrade

**Status:** Implemented — supported CI validation pending
**Delivery track:** Maintenance — compiler-service compatibility, semantic correctness, and upgrade evidence  
**Prerequisites:** The existing Roslyn/MSBuild semantic engine and confidence contract; external semantic refresh; advanced semantic tools; isolated C# scripting worker; central package management; .NET 10 LTS build baseline; and release-license closure.  
**Baseline and target:** `Microsoft.CodeAnalysis.*` `5.6.0` → `5.9.0`; .NET SDK floor `10.0.204` with `latestFeature` roll-forward → `10.0.401` with patch roll-forward inside the 10.0.4xx feature band. The target SDK supplies the supported MSBuild runtime. Keep `Microsoft.Build.Locator` `1.11.2` and the compile-only `Microsoft.Build.Framework` `17.11.48` pin unless the exact restored target closure proves a change is required. Versions were verified against official release and NuGet metadata on 2026-09-25.

## 1. Objective

Upgrade Threadsmith.NET's compiler-service stack as one verified unit without weakening semantic truth, generated-source coverage, cancellation, confinement, packaging, or extension boundaries.

The upgrade must align Threadsmith's Roslyn host with the current .NET 10 SDK feature band, continue loading MSBuild from the installed SDK through `Microsoft.Build.Locator`, and prove that solution/project evaluation, compilations, semantic queries, diagnostics, source generators, refresh, mutation analysis, and the isolated scripting worker remain correct. It must also establish phase-level semantic-load measurements so future performance work is based on observed `MSBuildWorkspace` evaluation and compilation costs rather than a package-version assumption.

This is a compatibility and evidence plan. It does not change the semantic confidence model or introduce lazy/parallel compilation during the dependency upgrade.

## 2. Architectural Context

Read root `AGENTS.md`, [planning governance](planning-governance.md), [shared context §G](00-shared-context.md#g-implementation-document-template-and-agent-instructions), and [portable C# guardrails](../guardrails/portable-csharp-guardrails.md) before implementation.

[ADR-4](../architecture/adr-04-roslyn-msbuild-semantic-truth.md) makes Roslyn plus MSBuild the semantic sources of truth. `SemanticEngine` registers MSBuild through `MSBuildLocator.RegisterDefaults()`, creates one `MSBuildWorkspace`, opens the selected solution or project, confines the resulting inputs, and currently prepares each loaded project compilation before publishing terminal load state. [Semantic confidence](../architecture/semantic-confidence.md) owns the meaning of `None`, `TextOnly`, `ProjectGraphOnly`, `PartialCompilation`, and `FullSemantic`; a dependency update cannot relabel incomplete state as complete.

The package named `Microsoft.Build.Framework` is not Threadsmith's MSBuild runtime. It is deliberately referenced with `ExcludeAssets="runtime"` and `PrivateAssets="all"` so MSBuild assemblies do not ship beside the application and conflict with the instance selected by the locator. The actual evaluation engine comes from the installed .NET SDK or supported Visual Studio instance. Therefore the safe MSBuild upgrade path is the .NET 10 SDK feature-band update, not adding `Microsoft.Build`, `Microsoft.Build.Tasks.Core`, or `Microsoft.Build.Runtime` to the product.

`Microsoft.CodeAnalysis.CSharp.Scripting` is consumed by the separately launched `Threadsmith.Scripting.Worker`; it shares the central Roslyn version and must be validated as part of the same atomic upgrade. Roslyn types remain internal to compiler-aware projects and never enter durable host state or extension contracts.

## 3. Scope

- Pin all direct `Microsoft.CodeAnalysis.*` packages to `5.9.0` through Central Package Management in one change.
- Move the repository SDK baseline to .NET SDK `10.0.401` while remaining on .NET 10 LTS and preventing accidental .NET 11 selection.
- Use the MSBuild runtime carried by the selected .NET 10 SDK through the existing locator path; record the exact selected instance in validation evidence.
- Keep `Microsoft.Build.Locator` `1.11.2` unless exact target-package dependency/API evidence requires a change.
- Keep `Microsoft.Build.Framework` `17.11.48` as a compile-only reference unless the restored Roslyn 5.9 closure requires a higher minimum; never copy it to runtime output.
- Audit Roslyn 5.6-to-5.9 public API and behavior changes at every Threadsmith call site before adapting code.
- Verify C# 14 parsing and semantic binding, including extension blocks and other syntax that can affect symbol search, call hierarchy, patterns, and source allocation.
- Verify analyzer and source-generator loading against SDK 10.0.401 and prove generated documents remain visible to the established generated-code and code-explore paths.
- Verify solution, direct-project, multi-target, project-reference, linked-file, degraded-load, and older-SDK-targeted repository fixtures.
- Verify the isolated C# scripting worker builds, publishes, starts, evaluates, times out, truncates, and disposes under Roslyn 5.9.
- Add bounded phase-level semantic-load measurement around MSBuild evaluation and initial compilation preparation, using existing logging/metrics ownership and no raw source content.
- Capture comparable pre-upgrade and post-upgrade cold/warm load evidence for the small semantic fixture and the full Threadsmith solution.
- Refresh exact dependency-closure, license, notice, SBOM, and package provenance evidence affected by the version changes.
- Update current architecture, contributor, and operations documentation that owns exact SDK/Roslyn versions.

## 4. Non-Scope

- No .NET 11, C# 15 preview, prerelease Roslyn, nightly feed, floating package range, or Visual Studio-only toolchain dependency.
- No standalone or bundled `Microsoft.Build` runtime packages and no direct adoption of MSBuild 18.10 merely because its NuGet packages exist.
- No change to `TargetFramework` (`net10.0`), `LangVersion` (`latest`), nullable policy, or analyzer severity policy.
- No general package refresh of `Microsoft.Extensions.*`, model SDKs, test frameworks, SourceLink, Roslynator, StyleCop, or terminal dependencies.
- No staged startup, lazy project compilation, bounded parallel `GetCompilationAsync`, project filtering, persistent Roslyn cache, or semantic confidence redesign. Measurements may justify a separate plan, but this upgrade must not hide a lifecycle change inside package adaptation.
- No second semantic engine, workspace loader, scripting path, build host, event model, or state store.
- No weakening of repository trust, MSBuild execution trust, prohibited-path confinement, request freshness, mutation validation, cancellation backstops, or diagnostic classification.
- No rewriting frozen historical implementation plans or spike results to make their original version evidence appear current.

## 5. Current State

### 5.1 Version and runtime ownership

| Component | Current state | Upgrade relevance |
|---|---|---|
| `global.json` | Minimum SDK `10.0.204`, `rollForward: latestFeature` | May silently select any later .NET 10 feature band installed. The active development machine currently selects 10.0.303. |
| Build compiler on the active machine | Roslyn `5.6.0` from SDK 10.0.303 | Matches the current runtime package line but not the target 10.0.4xx toolchain. |
| Actual MSBuild on the active machine | MSBuild `18.6.14` from SDK 10.0.303 | Demonstrates that the central `Microsoft.Build.Framework` pin is not the runtime version. |
| Direct Roslyn packages | `Workspaces.MSBuild`, `CSharp`, `CSharp.Scripting`, and `CSharp.Workspaces` at `5.6.0` | Must move together to avoid assembly-family mismatch. |
| Transitive Roslyn closure | Common, Workspaces.Common, Scripting.Common, MSBuild.Contracts, and Analyzers from the 5.6 closure | Must be regenerated and reviewed rather than manually guessed. |
| `Microsoft.Build.Locator` | `1.11.2` | Current stable locator supports MSBuild 18.x; no upgrade is presently required. |
| `Microsoft.Build.Framework` | `17.11.48`, excluded from runtime and private | Satisfies the published Roslyn 5.9 minimum. Raising it alone would not upgrade runtime MSBuild. |

Official target evidence:

- [.NET 10 download metadata](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) identifies SDK 10.0.401 as the current stable .NET 10 SDK and the 10.0.4xx feature band as the Visual Studio/MSBuild 18.9 line.
- [Roslyn MSBuild workspace 5.9.0](https://www.nuget.org/packages/Microsoft.CodeAnalysis.Workspaces.MSBuild/5.9.0) targets .NET 10 and declares its exact Roslyn workspace family plus minimum MSBuild/framework dependencies.
- [Roslyn issue 84137](https://github.com/dotnet/roslyn/issues/84137) records the failure mode where an older workspace host cannot load analyzers built against the newer SDK compiler, causing missing generated source and symbols.
- [MSBuild Locator guidance](https://learn.microsoft.com/en-us/visualstudio/msbuild/find-and-use-msbuild-versions) requires locating MSBuild rather than copying its runtime assemblies into the application.
- [MSBuild's 18.10 release record](https://github.com/dotnet/msbuild/issues/14562) identifies it as a Visual Studio-only release line rather than the runtime carried by a .NET 10 SDK; this plan does not substitute that independent line for the SDK-supported host.

### 5.2 Semantic load path

For `TrustedBuild`, `SemanticEngine.LoadCoreAsync` creates `MSBuildWorkspace`, enables metadata loading for referenced projects, and calls `OpenProjectAsync` or `OpenSolutionAsync` without a progress observer. It then iterates the loaded projects and awaits `GetCompilationAsync` for each project before replacing shared state and publishing terminal load completion.

The complete Threadsmith solution currently contains 51 projects: 25 product projects, 25 test projects, and one sample project. A full initial load therefore places project evaluation plus every first requested compilation on the startup-critical path. Existing refresh work reuses immutable Roslyn solutions for stable document edits and performs complete reloads for graph-affecting changes.

No phase-level production measurement currently distinguishes:

- solution/project parsing and MSBuild design-time evaluation;
- workspace build-host startup;
- project/document materialization;
- per-project first compilation preparation;
- generated-source contribution; and
- confinement/inventory work after workspace load.

The absence of those measurements means an observed startup delay cannot yet be attributed reliably to Roslyn, MSBuild, generators, solution size, or Threadsmith's eager readiness policy.

### 5.3 Compatibility-sensitive consumers

- `SemanticEngine` and `SemanticMutationEngine` use syntax, compilation, symbols, diagnostics, locations, solution snapshots, and workspace APIs.
- `AdvancedSemanticQueryService` uses compiler symbols, operations, reference finding, generated documents, patterns, and compilation readiness across many query shapes.
- `SemanticRefreshCoordinator` relies on immutable solution/document replacement and generation fencing.
- Validation and pre-mutation analysis compare baseline and introduced Roslyn diagnostics and require stable path/range projection.
- `Threadsmith.Scripting.Worker` uses `CSharpScript`, `ScriptOptions`, compilation diagnostics, and isolated process execution.
- Release evidence records every shipped direct and transitive Roslyn package by exact version, hash, license, and provenance.

## 6. Proposed Design

### 6.1 Fixed, coherent target

Use these exact direct targets:

| Owner | Target | Decision |
|---|---:|---|
| `global.json` | `10.0.401` | Set `rollForward` to `latestPatch` so local development and CI remain within the approved 10.0.4xx feature band. |
| `Microsoft.CodeAnalysis.Workspaces.MSBuild` | `5.9.0` | Upgrade atomically with the other Roslyn packages. |
| `Microsoft.CodeAnalysis.CSharp` | `5.9.0` | Keep compiler APIs aligned with workspaces. |
| `Microsoft.CodeAnalysis.CSharp.Scripting` | `5.9.0` | Keep the scripting worker on the same compiler family. |
| `Microsoft.CodeAnalysis.CSharp.Workspaces` | `5.9.0` | Keep C# language services aligned with common workspaces. |
| `Microsoft.Build.Locator` | `1.11.2` | Retain; exact stable target already supports the required MSBuild family. |
| `Microsoft.Build.Framework` | `17.11.48` | Retain as compile-only/private unless restore proves it violates the target package minimum. |

Do not silently replace these with a later version discovered during implementation. If an identified target is withdrawn, vulnerable, or incompatible, stop and amend this plan with the new exact target and evidence before implementation continues.

All direct Roslyn versions must change in one central edit. Restore must resolve one version for each `Microsoft.CodeAnalysis.*` assembly family across product, tests, spikes included in validation, and the scripting worker. A structural test or deterministic closure check must fail when direct pins diverge.

### 6.2 SDK-owned MSBuild

Treat the selected SDK as the MSBuild distribution boundary:

1. `global.json` selects SDK 10.0.401 with patch-only roll-forward.
2. `Microsoft.Build.Locator` registers the supported installed instance before any Roslyn/MSBuild type is used.
3. `MSBuildWorkspace` uses that registered instance and Roslyn's packaged build host.
4. Publish output contains `Microsoft.Build.Locator.dll` and the Roslyn build-host payload required by `Workspaces.MSBuild`, but no product-local `Microsoft.Build.dll`, `Microsoft.Build.Framework.dll`, `Microsoft.Build.Tasks.Core.dll`, or `Microsoft.Build.Utilities.Core.dll`.

Add validation that records SDK version, informational MSBuild version, Roslyn assembly versions, selected MSBuild discovery type, and build-host success. Do not expose installation paths in durable events or model context. A sanitized debug log may record the selected version and discovery type; paths remain local diagnostic detail under existing logging policy.

The test matrix must include a target repository with an older available .NET 10 `global.json` to establish whether the 10.0.401 host evaluates it correctly. It must also include a missing-SDK case and preserve the current bounded degraded/unavailable outcome rather than falling back to an unrelated SDK silently.

### 6.3 API and behavior compatibility ledger

Before editing production call sites, build a ledger in this plan or an implementation-owned evidence section with:

- Roslyn/MSBuild API used by Threadsmith;
- current caller and lifecycle owner;
- 5.9 signature or semantic change;
- whether source adaptation is required;
- expected confidence/diagnostic/query behavior; and
- the focused test that proves it.

At minimum audit `MSBuildWorkspace.Create`, `OpenSolutionAsync`, `OpenProjectAsync`, `ProjectLoadProgress`, workspace diagnostics, `Solution` document replacement, `Project.GetCompilationAsync`, compilation diagnostics, symbol identity/display, `SymbolFinder`, operation trees used by advanced queries, generated-document APIs, parsing options, and scripting APIs.

Adapt the existing path only where the exact target requires it. A compatibility shim is acceptable only when it keeps one execution path and has an identified removal condition. Do not fork a 5.6 and 5.9 implementation.

Implementation audit (2026-09-28):

| API or behavior | Current owner and call path | 5.9 audit result | Adaptation and focused evidence |
|---|---|---|---|
| `MSBuildLocator.RegisterDefaults`, `MSBuildWorkspace.Create` | `SemanticEngine.LoadCoreAsync` before workspace construction | Source-compatible; locator ordering and SDK-owned runtime remain required. | No adaptation. Architecture/load tests verify registration precedes workspace creation and publish outputs exclude MSBuild runtime assemblies. |
| `OpenSolutionAsync`, `OpenProjectAsync`, `ProjectLoadProgress` | Initial load and complete refresh through `SemanticEngine.LoadCoreAsync` | Existing overloads remain compatible; the progress overload provides the plan-required bounded evaluation observations. | Pass one aggregate progress observer through both existing branches; solution and direct-project tests cover the shared path. |
| Workspace diagnostics | `RegisterWorkspaceFailedHandler` in `SemanticEngine.LoadCoreAsync` | Event and diagnostic-kind contract is source-compatible. Analyzer/build-host failures remain confidence-relevant. | No classification change. Existing degraded-load coverage plus target analyzer/generator failure coverage prevents false `FullSemantic`. |
| Immutable `Solution` document replacement | `SemanticEngine` incremental refresh, diagnostics overlay, mutation overlay | `WithDocumentText`, `AddDocument`, `RemoveDocument`, additional-document, and analyzer-config operations are source-compatible. | No adaptation. Refresh, generation-fence, validation, and mutation suites exercise the established path. |
| `Project.GetCompilationAsync` and compilation diagnostics | Initial preparation, refresh, validation, mutation, and advanced queries | Source-compatible. A non-null compilation alone does not override workspace-failure or missing-project confidence rules. | Add phase timing around the existing sequential eager loop only; diagnostic and confidence tests retain current decisions. |
| Symbol identity/display and `SymbolFinder` | `SemanticEngine` and `AdvancedSemanticQueryService` | Declaration, reference, implementation, override, and caller APIs used by Threadsmith are source-compatible. Extension members add target-language symbols without a parallel query path. | No source adaptation. C# 14 fixture assertions cover host-owned names, kinds, ranges, references, implementations, and code exploration. |
| Operation trees and syntax parsing | `CodeExploreToolCapabilityClassifier`, advanced query parsing and pattern flows | `GetOperation`, C# parsing, and operation interfaces used by Threadsmith are source-compatible. New syntax requires behavioral coverage rather than a compatibility shim. | No source adaptation. C# 14 extension/property fixture coverage exercises the real semantic/code-explore path. |
| Generated documents and analyzer loading | Generated-code inventory and advanced semantic queries over the loaded `Solution` | Existing generated-document APIs are source-compatible; analyzer/compiler family mismatch remains the material behavioral risk. | No API adaptation. A deterministic target-line generator/analyzer fixture and generated-symbol queries prove visibility; failure coverage proves honest confidence. |
| C# scripting (`CSharpScript`, `ScriptOptions`, diagnostics) | Isolated `Threadsmith.Scripting.Worker` process | Creation, compilation, evaluation, reference, import, and diagnostic APIs used by the worker are source-compatible. | No adaptation. Real worker success, diagnostic, bounds, timeout, cancellation, cleanup, and publish checks cover the process boundary. |
| Non-Windows workspace storage composition | `RoslynWorkspaceHost` MEF composition | The optional SQLite export identity must be verified against the exact restored 5.9 implementation; package version alone is not evidence that process-shared contention is gone. | Retain the nonpersistent composition and pinned-identity contract test unless Windows inspection and Linux/macOS CI prove the target implementation changed. |

### 6.4 Semantic correctness fixtures

Extend the existing small semantic fixture rather than constructing a second integration harness. Add the smallest source set that proves Roslyn 5.9 behavior relevant to Threadsmith:

- a C# 14 extension block with instance and static extension members;
- a `field`-backed property or null-conditional assignment where syntax/operation inspection is relevant;
- cross-project symbol/reference/implementation resolution;
- one linked file and one multi-target project where current fixture support already exists;
- a deterministic source generator and analyzer compiled against the target Roslyn line; and
- a generated symbol discoverable through generated-code inventory and code exploration.

Assert host-owned outputs rather than Roslyn object identity: project facts, symbol names/kinds, source ranges, generated classification, diagnostic IDs/severity, confidence, omissions, and bounded source projection. Preserve exact current behavior for baseline/introduced diagnostic correlation and affected-project validation.

Add a negative compatibility fixture in which analyzer loading fails or the required SDK is unavailable. Threadsmith must report reduced confidence or unavailable semantics honestly; it must not claim `FullSemantic`, omit the workspace failure, or present missing generated source as complete.

### 6.5 C# scripting worker

Build and publish `Threadsmith.Scripting.Worker` with Roslyn 5.9 and verify the worker's dependency manifest contains a coherent Roslyn closure. Exercise the real worker process for:

- successful expression and statement evaluation;
- target-language syntax accepted by the upgraded scripting package;
- compiler diagnostic projection;
- disabled-by-default and trust/approval gates;
- output bounds and serialization;
- timeout, cancellation, process-tree termination, and cleanup; and
- framework-dependent development launch plus supported self-contained application packages.

The upgrade must not move scripting into the host process or grant new references, imports, filesystem authority, or mutation authority.

### 6.6 Semantic-load measurement

Instrument the existing load path without changing readiness semantics. Use `ProjectLoadProgress` on `OpenSolutionAsync`/`OpenProjectAsync` and monotonic timing around established phases. Aggregate observations before logging so large solutions do not emit an unbounded event per file or target.

Capture at least:

- total load duration;
- MSBuild/workspace evaluation duration and progress-operation counts;
- loaded, expected, excluded, failed, and compiled project counts;
- aggregate initial compilation-preparation duration;
- slowest bounded project-compilation samples by duration, using sanitized project names only in local debug logs and not durable events;
- resulting confidence and workspace failure count; and
- cold/warm classification plus process working-set sample where the existing telemetry substrate can provide it safely.

Measurements must use `TimeProvider` or `Stopwatch` according to existing ownership, remain non-negative, and never include source, command-line properties, environment variables, package credentials, analyzer exception dumps, or repository-external paths. Cancellation and abandoned non-cooperative work must not publish a successful duration/result.

Do not add a second public lifecycle event solely for the upgrade. Prefer structured logs/metrics and the existing terminal completion event. If current telemetry cannot represent aggregate phase data without leaking implementation types, add one internal host-owned measurement record confined to `Threadsmith.DotNet`/telemetry composition.

### 6.7 Comparative performance gate

Before changing versions, collect a baseline from the active checkout using:

- the checked-in small semantic solution;
- a direct `Threadsmith.App.csproj` load; and
- the complete `src/Threadsmith.sln` load.

Run the same release/debug configuration, SDK installation set, trust, solution selection, machine power state, and warm/cold procedure after the upgrade. Record SDK, Roslyn, and MSBuild versions, project counts, total/evaluation/compilation durations, confidence, workspace diagnostics, and peak or sampled working set. Use repeated runs sufficient to distinguish process/JIT/cache noise and report medians plus ranges; do not turn a single wall-clock observation into a claim.

Material regression is a review trigger, not a number to waive automatically. Investigate a repeatable increase in time-to-terminal-confidence, evaluation time, compilation preparation, working set, or generator failures. Attribute the cause before acceptance. An upstream regression may justify rollback, an isolated workaround on the existing path, or a separately approved exception with evidence.

The implementation record must state whether evaluation or eager compilation dominates on the full solution. If eager compilation is material, propose a separate maintenance plan for staged readiness/lazy compilation and bounded parallel warming. That later work must preserve the existing confidence, freshness, headless admission, cancellation, and query-generation contracts; it is not authorized by this plan.

### 6.8 Dependency and release closure

After restore, regenerate the canonical product package graph and compare the exact closure. Review every added, removed, or version-shifted package, including `Microsoft.CodeAnalysis.Analyzers`, `Microsoft.CodeAnalysis.Common`, workspaces common/contracts, scripting common, composition, immutable collections, and MSBuild-related dependencies.

Update `eng/release/release-license-evidence.json` with exact package versions, hashes, license expressions/text ownership, and immutable provenance. Regenerate notices and SPDX using the existing release scripts. Do not hand-edit generated legal artifacts to make a gate pass. Keep `Microsoft.Build.Framework` categorized as build/source compliance only while its runtime asset remains excluded.

Inspect all supported self-contained publish outputs and the scripting worker payload. Fail if product-local MSBuild runtime assemblies appear, Roslyn families are mixed, the workspace build host is missing, or notices/SBOM disagree with the artifact.

## 7. Public Contracts

No new public commands, tools, configuration keys, domain events, persistence schemas, extension contracts, or semantic confidence levels are expected.

Observable semantic results may legitimately improve for C# 14 syntax, newer SDK analyzers, and generated source that 5.6 could not load. Such corrections must retain existing host-owned result schemas, provenance, confidence, bounds, and omissions. Any diagnostic or symbol behavior change must be traced to target Roslyn behavior and recorded; tests must not simply loosen assertions.

Initial semantic loading continues to publish one terminal `SemanticLoadCompleted` result after the current eager readiness work. Interactive and headless admission behavior remains unchanged. A later staged-readiness proposal requires its own contract review.

## 8. Project/File Changes

| Path | Expected work |
|---|---|
| `Directory.Packages.props` | Atomically pin the four direct Roslyn packages to 5.9.0; retain locator/framework decisions unless exact restore evidence requires an amendment. |
| `global.json` | Select SDK 10.0.401 with patch-only roll-forward. |
| `src/Threadsmith.DotNet/SemanticEngine.cs` | Target-required API adaptations and bounded phase measurement on the established load path. |
| `src/Threadsmith.DotNet/AdvancedSemanticQueryService.cs`, `SemanticMutationEngine.cs`, refresh code | Change only where the compatibility ledger demonstrates a 5.9 behavior/API requirement. |
| `src/Threadsmith.Scripting.Worker/` | Target-required scripting adaptations only; preserve process isolation and protocol. |
| `tests/fixtures/semantic/SmallDotNetSolution/` | Minimal C# 14/generated-source compatibility additions. |
| `tests/Threadsmith.ModelTooling.Tests/` | Load, diagnostics, refresh, scripting, cancellation, measurement, and degraded-compatibility coverage. |
| `tests/Threadsmith.NativeTools.Tests/` | Advanced semantic/query/generated-code behavior over the upgraded engine. |
| `tests/Threadsmith.Validation.Tests/`, `tests/Threadsmith.Mutations.Tests/` | Diagnostic classification, pre-mutation analysis, overlay compilation, and rollback regressions. |
| `tests/Threadsmith.Architecture.Tests/` | Version-family, dependency direction, runtime-exclusion, worker/publish, and package-closure guards. |
| `docs/architecture/adr-01-net10-lts-target.md`, `adr-04-roslyn-msbuild-semantic-truth.md` | Update current exact SDK/Roslyn decisions after validation; preserve the decisions and boundaries. |
| `CONTRIBUTING.md` | Update the contributor SDK requirement and verification command. |
| `docs/dotnet-package-graph.json` | Regenerate exact restored closure. |
| `eng/release/release-license-evidence.json` and generated legal outputs | Refresh exact dependency evidence, notices, and SBOM inputs. |
| `docs/implementation-plans/plan-117-roslyn-dotnet-semantic-toolchain-upgrade.md` | Record implementation, measurements, deviations, review findings, and completion evidence. |

Historical spike notes and completed implementation plans retain the versions they actually validated. Do not rewrite them as current-version documentation.

## 9. Ordered Tasks

### P117-01 — Freeze the executable baseline

1. Confirm the active checkout and clean/known worktree state.
2. Install or provision SDK 10.0.401 without removing the existing baseline SDK needed for compatibility fixtures.
3. Record current SDK, compiler, informational MSBuild, locator, Roslyn assembly, and resolved dependency versions.
4. Add or run a repeatable semantic-load measurement harness against the small fixture, direct app project, and full solution using the existing production load path.
5. Record confidence, workspace diagnostics, generated-document visibility, elapsed phase data available before instrumentation, and memory observations.
6. Run the focused semantic, scripting, validation, mutation, native-tool, and architecture suites before changing versions.

### P117-02 — Audit target packages and APIs

1. Resolve immutable NuGet metadata and hashes for every direct target.
2. Compare 5.6 and 5.9 APIs used by Threadsmith and populate the compatibility ledger.
3. Inspect release/issue evidence for workspace, compiler, generator, operation-tree, and scripting changes relevant to actual call sites.
4. Confirm Roslyn 5.9's restored minimum MSBuild/framework dependencies.
5. Confirm locator 1.11.2 supports the selected SDK/MSBuild instance.
6. Stop and amend the plan if any exact target is prerelease, withdrawn, vulnerable, or incompatible with `net10.0`.

### P117-03 — Upgrade the SDK and Roslyn family atomically

1. Update `global.json` to 10.0.401 with `latestPatch`.
2. Update the four direct Roslyn pins to 5.9.0 in `Directory.Packages.props`.
3. Restore and prove one coherent Roslyn family in every affected project and worker.
4. Leave `Microsoft.Build.Framework` compile-only/private and keep MSBuild runtime assemblies out of output.
5. Make the smallest source adaptations identified by the ledger; do not introduce a parallel implementation.
6. Build immediately and resolve warnings/errors before adding unrelated work.

### P117-04 — Lock semantic and generator compatibility

1. Extend the existing semantic fixture with minimal C# 14 constructs.
2. Add deterministic target-line analyzer/generator coverage and generated-symbol discovery.
3. Verify direct project and solution loading, multi-target/project references, linked/generated documents, symbol/reference/implementation queries, operations/patterns, and diagnostics.
4. Verify partial/failure confidence when a generator, analyzer, project, or SDK cannot load.
5. Re-run refresh and generation-fence tests through incremental and full reload paths.
6. Verify pre-mutation and post-mutation diagnostics still distinguish baseline and introduced failures.

### P117-05 — Validate the scripting worker

1. Build and publish the real worker with the coherent target closure.
2. Run success, diagnostic, bounds, timeout, cancellation, and process cleanup cases.
3. Inspect framework-dependent and self-contained application outputs for the worker apphost, dependency manifest, and matching Roslyn assemblies.
4. Confirm no scripting SDK type or authority leaks into host-owned public contracts.

### P117-06 — Add bounded load measurements

1. Attach `ProjectLoadProgress` to both solution and direct-project open paths.
2. Time established evaluation, confinement/inventory, and initial compilation-preparation phases.
3. Aggregate counts and bounded slow-project observations without per-document or unbounded logs.
4. Cover success, reduced confidence, failure, cancellation, supersession, and disposal.
5. Verify measurement cannot change load ordering, confidence, event count, cancellation ownership, or terminal presentation.

### P117-07 — Compare performance and decide follow-up

1. Repeat the P117-01 matrix under the upgraded stack on the same host/configuration.
2. Record medians, ranges, project counts, phase durations, confidence, failures, generated documents, and working-set observations.
3. Investigate any repeatable material regression before acceptance.
4. State whether MSBuild evaluation or eager compilation is the primary full-solution cost.
5. If optimization remains warranted, draft a separate plan for staged/lazy readiness and bounded warming; do not implement it here.

### P117-08 — Close documentation, package, and release evidence

1. Regenerate and review the exact package graph.
2. Update release-license evidence, notices, and SPDX inputs/outputs through existing scripts.
3. Update ADR-1, ADR-4, and contributor guidance with the validated current versions.
4. Preserve historical spike and completed-plan evidence unchanged.
5. Validate supported publish outputs and assert absence of bundled MSBuild runtime assemblies.

### P117-09 — Final adversarial review

1. Trace initial load, refresh, semantic queries, validation, mutation analysis, scripting, and publish paths outside the diff.
2. Challenge mixed assembly families, duplicate loaders, silent generator loss, optimistic confidence, changed diagnostic filtering, non-cooperative cancellation leaks, unbounded measurement work, and non-Windows persistent-index cache contention.
3. Re-run fixes through real solution/direct-project entry points rather than adding wrapper-only tests.
4. Run the full build/test/release gates and record unassessed environments explicitly.
5. Record completion only after every acceptance criterion is satisfied.

## 10. Testing

### 10.1 Restore and build gates

```powershell
dotnet --version
dotnet --info
dotnet restore src/Threadsmith.sln
dotnet build src/Threadsmith.sln --configuration Debug --no-restore
dotnet list src/Threadsmith.DotNet/Threadsmith.DotNet.csproj package --include-transitive
```

Assert SDK 10.0.401 (or a later 10.0.4xx patch permitted by `latestPatch`), one Roslyn 5.9 family, locator 1.11.2, and no unexpected MSBuild runtime package in the product closure.

### 10.2 Focused automated gates

Run the built test executables or repository Microsoft.Testing.Platform commands for:

- `Threadsmith.ModelTooling.Tests`;
- `Threadsmith.NativeTools.Tests`;
- `Threadsmith.Validation.Tests`;
- `Threadsmith.Mutations.Tests`;
- `Threadsmith.RepositoryLifecycle.Tests`;
- `Threadsmith.Architecture.Tests`; and
- any scripting/publish coverage owned by `Threadsmith.CoreRuntime.Tests` or application bootstrap tests.

The compatibility suite must exercise real `MSBuildWorkspace` and real worker processes where those boundaries are the behavior under test. Unit fakes remain appropriate for isolated confidence, measurement, cancellation, and failure-classification logic.

Run concurrent independent-workspace symbol and `code_explore` queries on Linux and macOS. Roslyn 5.6 attaches each workspace to the same non-Windows `file::memory:?cache=shared` SQLite write cache while synchronizing per workspace. `RoslynWorkspaceHost` omits the optional SQLite service on non-Windows platforms so Roslyn selects its built-in nonpersistent fallback; query serialization cannot cover background flushes. Retain this composition unless the target Roslyn implementation and repeated cross-platform evidence prove that the process-wide collision is gone. Verify the pinned service-export identity and fallback contract on every platform. Do not substitute test-runner serialization or retries.

### 10.3 Full regression gate

```powershell
dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4
pwsh -File eng/release/Test-ReleaseLicenseEvidence.ps1
pwsh -File eng/release/Test-ReleaseContracts.ps1
```

Run affected concurrency/cancellation-sensitive suites three consecutive times after the final fix. Repetition is a race regression check, not permission to retry away a failure.

### 10.4 SDK/MSBuild compatibility matrix

At minimum validate on Windows, Linux, and macOS CI with SDK 10.0.401 available. Exercise target repositories representing:

- no `global.json`;
- `global.json` selecting 10.0.401;
- an installed older .NET 10 SDK selection;
- a declared but unavailable SDK;
- full solution and direct project;
- generated source/analyzer use; and
- multi-target and linked-source projects.

Record the actual selected SDK/MSBuild/Roslyn versions for every real-boundary run. Do not infer them from package pins.

### 10.5 Publish and artifact gates

Use the existing release workflow for all supported RIDs. Inspect script parameters before invocation because release scripts may clean their output root; use new verified workspace-local output directories. For each artifact verify:

- application and scripting worker start successfully where native execution is available;
- Roslyn assemblies and build-host payload are complete and version-coherent;
- no product-local MSBuild runtime assemblies are present;
- notices, SBOM, and dependency graph match actual bytes; and
- headless semantic load can reach expected confidence on a disposable trusted fixture.

Cross-publishing alone does not establish native execution.

### 10.6 Performance evidence

Use the production semantic engine, not a handwritten approximation. Separate cold process/SDK-host startup from warm process/cache runs. Record machine, OS, CPU/logical core count, memory, storage class, SDK, Roslyn, MSBuild, configuration, solution/project selection, project counts, repetitions, medians, and ranges.

Compare time to terminal semantic confidence, evaluation phase, compilation-preparation phase, workspace failures, generated-document count, and working set. Treat measurements as environment-specific evidence. Do not claim a universal speedup from one machine.

### 10.7 Adversarial review focus

- Does any product output now contain MSBuild runtime assemblies that can compete with the locator?
- Can a newer SDK analyzer fail silently while Threadsmith still reports `FullSemantic`?
- Do extension blocks and generated symbols flow through the actual code-explore/query path?
- Did operation-tree or diagnostic changes alter validation decisions rather than merely text?
- Do cancelled/superseded loads dispose or abandon target resources under the established backstop?
- Does measurement enumerate or retain unbounded project/document data?
- Are scripting worker dependencies and application Roslyn dependencies coherent but process-isolated?
- Were historical documents rewritten instead of updating current owners?

## 11. Security and Permissions

The upgrade does not change the `TrustedBuild` boundary: MSBuild evaluation may execute repository build logic only where current policy permits it. `TrustedRead` remains text/project-metadata-only. Repository configuration cannot select arbitrary compiler/MSBuild assemblies, NuGet feeds, build hosts, or locator instances through this plan.

Keep solution/project/document confinement, prohibited paths, reparse checks, stable reads, refresh admission, and generated-source bounds unchanged. Workspace/analyzer failures are sanitized before events or user presentation. Logs and measurements contain no source, secrets, environment variables, compiler command lines, package credentials, or unrestricted exception payloads.

The scripting worker retains its current explicit enablement, trust, approval, process, time, output, and cancellation boundaries. A newer compiler does not grant additional references or operating-system authority.

## 12. Observability

Use existing structured logging and metrics ownership. Record version facts at startup/debug level once per process and aggregate semantic-load phases once per load. Include workspace/session correlation already available to the semantic lifecycle without persisting Roslyn or MSBuild objects.

Required observable facts are selected SDK/MSBuild/Roslyn versions, load mode, phase durations, bounded project counts, workspace failure count, result confidence, cancellation/failure classification, and whether the run was cold or warm when explicitly measured. Keep high-cardinality project detail out of durable events and ordinary info logs.

Existing `SemanticConfidenceChanged`, `SemanticLoadCompleted`, and refresh events remain authoritative for product lifecycle. Measurement must not duplicate or reorder them.

## 13. Migration and Compatibility

There is no persisted-state or configuration migration. Existing repository/session configuration remains valid.

The source/build prerequisite changes to SDK 10.0.401. Contributors and CI without the 10.0.4xx feature band receive the standard SDK-selection failure with an actionable documented prerequisite; Threadsmith must not silently build with .NET 11.

Rollback is one atomic reversal of the SDK and four Roslyn direct pins plus any target-only source adaptations, documentation, closure, and legal evidence. Do not roll back only one Roslyn package. Retain baseline measurements and failure evidence so rollback does not erase the reason.

Repositories targeting older .NET versions remain supported to the extent the selected .NET 10 SDK/MSBuild host supports their project systems. Compatibility claims require tested fixtures; unsupported legacy project systems must degrade explicitly rather than being reported as fully semantic.

## 14. Acceptance Criteria

1. `global.json` selects stable SDK 10.0.401 with patch-only roll-forward and does not admit .NET 11.
2. All four direct Roslyn packages are pinned to exactly 5.9.0 and the restored closure contains one coherent `Microsoft.CodeAnalysis.*` family.
3. `Microsoft.Build.Locator` registers the SDK-supported MSBuild instance before workspace creation.
4. `Microsoft.Build.Framework` remains excluded from runtime output, and no standalone MSBuild runtime assembly is bundled in supported application artifacts.
5. Solution and direct-project loads reach the same honest confidence outcomes as before for equivalent fixtures.
6. C# 14 extension members and other selected constructs parse, bind, and project correctly through host-owned semantic results.
7. Target-line analyzers and source generators load; generated documents and symbols are visible through established inventory/query tools.
8. Analyzer/generator/SDK load failure cannot produce false `FullSemantic` or silently complete results.
9. Symbol, reference, implementation, call hierarchy, impact, pattern, generated-code, and code-explore tests pass with justified target-behavior updates only.
10. Semantic refresh, generation fencing, source-drift protection, and request admission remain correct.
11. Pre-mutation, baseline, post-mutation, and introduced-diagnostic classification remain correct.
12. The real scripting worker passes success, diagnostic, limit, timeout, cancellation, and publish tests with a coherent 5.9 closure.
13. Non-cooperative Roslyn/MSBuild cancellation still uses abandon-and-discard with the bounded cleanup backstop.
14. Phase measurements distinguish evaluation from compilation preparation without changing lifecycle behavior or leaking sensitive/high-cardinality data.
15. Comparable baseline and target measurements are recorded for the small fixture, direct app project, and full solution.
16. Every material performance or memory regression is explained and resolved, rolled back, or explicitly accepted with evidence before completion.
17. The implementation record states the dominant measured full-solution load phase and routes any lazy/parallel readiness proposal to a separate plan.
18. Focused suites, full solution build/tests, and repeated concurrency/cancellation regressions pass.
19. Windows, Linux, and macOS CI validate the supported SDK/MSBuild/Roslyn stack; unavailable native release runs are recorded rather than inferred.
20. Exact package graph, license evidence, notices, SBOM, and supported publish artifacts match the upgraded bytes.
21. ADR-1, ADR-4, and contributor guidance describe the validated current stack; historical spike/plan evidence remains unchanged.
22. Final adversarial review finds no mixed assembly families, duplicate execution paths, silent generated-source loss, confidence inflation, or runtime MSBuild conflict.
23. Independent semantic workspaces pass repeated concurrent symbol-index queries on Linux and macOS; `RoslynWorkspaceHost` retains or removes its nonpersistent composition according to verified target behavior rather than package-version inference.

## 15. Risks

| Risk | Mitigation |
|---|---|
| Roslyn assemblies resolve at mixed versions | Change all direct pins atomically; inspect every project/worker closure; add a structural family-alignment guard. |
| SDK analyzers require a newer compiler host | Pair Roslyn 5.9 with the 10.0.4xx SDK and exercise real generated-source fixtures. |
| Raising the compile-only MSBuild reference creates runtime assumptions | Retain 17.11.48 unless exact dependency evidence requires more; test runtime exclusion and selected instance explicitly. |
| Bundled MSBuild competes with locator registration | Keep runtime assets excluded and inspect every supported publish artifact. |
| Compiler behavior changes diagnostics or operation trees | Assert host-owned semantic outcomes and review each changed expectation against upstream behavior. |
| New language syntax is parsed but advanced tools mishandle it | Exercise real symbol, operation, flow, pattern, generated-code, and source-allocation paths. |
| Source generators disappear without a hard load failure | Verify generated documents/symbols and treat analyzer failures as confidence-relevant evidence. |
| Full solution load becomes slower or consumes more memory | Capture same-host phase baselines, investigate material regressions, and roll back or isolate before completion. |
| Performance work broadens the compatibility change | Explicitly prohibit staged/lazy/parallel readiness here and route measured follow-up separately. |
| SDK pin breaks contributors or CI | Update setup documentation/workflows together and validate all hosted OSes. |
| Scripting worker closure diverges from the host | Inspect/publish the real worker and exercise process-boundary integration tests. |
| Non-Windows Roslyn workspaces contend on the process-shared SQLite write cache | Exercise independent workspaces concurrently on Linux/macOS; retain the nonpersistent workspace composition until the target implementation proves isolation. |
| Legal/package evidence drifts from actual artifacts | Regenerate from restored/published bytes and run existing fail-closed release gates. |

## 16. Documentation

Update only current owners:

- `CONTRIBUTING.md` for the SDK prerequisite;
- ADR-1 for the current .NET 10 SDK pin and roll-forward policy;
- ADR-4 for the current Roslyn target while preserving locator/runtime-exclusion decisions;
- dependency graph and release legal evidence for exact package closure; and
- this plan with implementation, measurements, compatibility ledger, deviations, rollback decisions, review findings, and completion evidence.

Update the user guide only if an observable diagnostic, supported project-system statement, or operator recovery procedure changes. No acceptance-scenario or manual-test-plan change is expected for a behavior-preserving upgrade; add one only if implementation changes observable behavior or introduces a new executable verification procedure.

Do not update historical spike notes or completed plans merely to replace their original tested versions.

## 17. Open Decisions

No implementation-blocking product decision remains at planning time. The target versions and runtime-ownership rules are fixed above.

The implementation must resolve and record these evidence questions without silently expanding scope:

1. What exact informational MSBuild version does SDK 10.0.401 select on each supported OS?
2. Does the exact 5.9 restore require any higher compile-only `Microsoft.Build.Framework` version? The default decision is no change.
3. Which load phase dominates the full Threadsmith solution after upgrade?
4. Do any target Roslyn behavior changes require a documented correction to host-owned semantic output?
5. Is a separate staged/lazy semantic-readiness plan justified by measured benefit after compatibility closure?

## 18. Implementation Record

### 18.1 Selected stack and compatibility adaptations

Implementation used SDK 10.0.401 (`latestPatch`), MSBuild 18.9.11, and the four direct Roslyn 5.9.0 packages. `Microsoft.Build.Locator` remains 1.11.2 and `Microsoft.Build.Framework` remains a compile-only/private 17.11.48 reference. The regenerated closure contains one 5.9 Roslyn runtime/compiler/workspaces family; `Microsoft.CodeAnalysis.Analyzers` resolves to `5.9.0-1.26328.17`, and the 5.9 closure no longer contains `Microsoft.CodeAnalysis.Workspaces.MSBuild.Contracts`.

The only target-required production query adaptation is in the established generated-code path. Roslyn 5.9 can resolve a source-generated symbol from a compilation to a generated `Document`, while the previous helper treated that document as ordinary disk-backed source. The helper now identifies generated documents before ordinary solution documents, reads their bounded Roslyn text, and preserves the existing repository-policy path for every non-generated document. No alternate workspace, semantic engine, compilation lifecycle, or scripting path was added.

Initial loads now attach one `ProjectLoadProgress` observer, emit one aggregate structured measurement, and publish low-cardinality phase/count histograms. Completed, degraded, thrown-failure, text-only, and cancelled attempts share one terminal measurement owner. The existing sequential eager compilation and terminal lifecycle remain unchanged. Analyzer references are validated once per unique reference during the existing load loop, including Roslyn's `AnalyzerLoadFailed` event; a target analyzer/generator load failure reduces confidence and produces a sanitized diagnostic instead of allowing false `FullSemantic`. Cancellation cleans up abandoned workspace results, while ownership transfers atomically with committed engine state before lifecycle-event delivery.

### 18.2 Correctness and package evidence

The existing small semantic fixture now contains the minimum C# 14, multi-target, analyzer, and incremental-generator inputs needed for the upgrade. The target tests prove extension-block members, a field-backed property, generated-document inventory, generated-symbol `code_explore`, cross-project references, direct-project/solution load behavior, corrupt analyzer degradation, unavailable SDK degradation, and an installed older 10.0.303 repository SDK. The real isolated scripting worker evaluated C# 14 null-conditional assignment and retained its existing success, diagnostic, output-bound, timeout, cancellation, and cleanup behavior.

`docs/dotnet-package-graph.json` and `eng/release/release-license-evidence.json` were regenerated from the target restore. The existing legal approval for self-contained .NET runtime 10.0.4 remains unchanged: it owns distributed runtime bytes and is distinct from the repository's 10.0.401 build-SDK selection.

All six supported RIDs (`win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64`) published through the existing release workflow and were republished from the final post-review bytes. Every staged artifact contained the Roslyn build host, locator, and scripting worker apphost, used Roslyn file version `5.900.26.35703`, and omitted product-local `Microsoft.Build.dll`, `Microsoft.Build.Framework.dll`, `Microsoft.Build.Tasks.Core.dll`, and `Microsoft.Build.Utilities.Core.dll`. The final `win-x64` application and native worker were executed locally, including the C# 14 scripting probe; the other RIDs were cross-publish inspections only.

### 18.3 Performance evidence and limitation

Measurements were collected on Windows 11 Pro 10.0.26200, a 13th Gen Intel Core i9-13980HX with 32 logical processors, 63.7 GiB RAM, and SSD storage, using Debug configuration, SDK 10.0.401, MSBuild 18.9.11, and Roslyn 5.9.0. Durations are milliseconds and working set is process bytes at terminal load.

| Selection | Run | Total | Evaluation | Confinement | Compilation | Working set | Result |
|---|---|---:|---:|---:|---:|---:|---|
| Small semantic fixture | Cold | 3,914.5 | 2,094.0 | included in remainder | 1,808.6 | 180,965,376 | `FullSemantic` |
| Small semantic fixture | Warm range (2) | 1,603.2–1,608.2 | 1,443.9–1,472.1 | included in remainder | 126.2–159.8 | 204,759,040–217,640,960 | `FullSemantic` |
| Direct App project | Cold | 12,112.7 | 10,599.5 | included in remainder | 1,498.8 | 221,663,232 | `FullSemantic` |
| Direct App project | Warm range (2) | 5,504.6–5,682.0 | 5,342.5–5,511.9 | included in remainder | 156.5–164.4 | 245,874,688–294,670,336 | `FullSemantic` |
| Full Threadsmith solution | Cold | 19,197.1 | 12,972.4 | 5,944.7 | included in remainder | 424,488,960 | `PartialCompilation` |
| Full Threadsmith solution | Warm range (2) | 15,225.3–15,291.3 | 11,632.3–12,489.9 | included in remainder | 2,373.6–3,391.1 | 476,516,352–586,502,144 | `PartialCompilation` |

The full solution contains 52 solution entries after adding the test-only analyzer/generator project and materializes 63 target-specific projects. Its 25 workspace failures are the existing confinement of Microsoft.Testing.Platform-generated `DefaultRunnerReporters.cs` paths outside the repository, so the honest terminal result is `PartialCompilation`. MSBuild evaluation is the dominant warm-load phase; any lazy, parallel, or staged-readiness proposal belongs in a separate plan.

A comparable Roslyn 5.6 phase baseline could not be produced. Temporarily restoring the baseline pins and fixture state caused the 5.6 build host to fail during evaluation with an RPC null-result failure on this SDK/host, before the new phase boundaries could complete. The target pins and fixture were restored immediately. The recorded 5.9 figures are target measurements, not evidence of a speedup or regression against 5.6.

### 18.4 Verification completed on the implementation host

- Exact-SDK restore and final Debug build passed with zero warnings and zero errors.
- The final full solution run passed 3,600 tests, skipped 31 environment/explicit tests, and failed none.
- The combined Plan 117 and `SemanticRefreshCoordinatorTests` selection passed 70/70 with two opt-in skips three consecutive times; `Plan43AdvancedSemanticToolTests` passed 15/15 three consecutive times.
- The opt-in older-SDK matrix passed with installed SDK 10.0.303, and the declared-but-unavailable SDK case degraded without false semantic confidence.
- The opt-in real scripting-worker conversation matrix passed 7/7, and the targeted C# 14 worker/bounds/diagnostic/timeout test passed.
- Release-license evidence and all release-contract checks passed.
- Supported-RID publish, notice, SBOM, dependency, and runtime-assembly exclusion gates passed for all six artifacts.

The follow-up analyzer-validation cancellation fix passed four real blocked-constructor cases: analyzer and generator construction during initial load and full refresh. Cancellation completed before releasing each constructor, published no replacement state, and retained usable prior state during refresh. The focused upgrade suite passed 11 tests with two opt-in skips, and all 63 refresh-coordinator tests passed. The full-solution and release results above precede this focused follow-up.

The generated-source compatibility path classifies documents already resolved by Roslyn through the public `SourceGeneratedDocument` subtype. Ordinary document locations therefore return in constant time without enumerating a project's generated documents; project-wide generated-document enumeration is reserved for syntax trees that the captured solution cannot resolve directly. After this follow-up, the focused upgrade suite again passed 11 tests with two opt-in skips and the 15 advanced semantic-tool tests passed. The full-solution and release results above precede this focused follow-up.

Linux/macOS native execution, their independent concurrent-workspace query checks, and hosted CI remain unassessed locally. They must run in the repository's supported CI environments; cross-publishing is not counted as native validation. The implementation therefore does not infer acceptance criteria 19 or 23 from Windows evidence.

### 18.5 Adversarial review

The independent adversarial review traced the completed diff and the established load, refresh, query, mutation, scripting, and publish entry points. Its valid findings were fixed and re-reviewed:

- analyzer load failures reported through `AnalyzerFileReference.AnalyzerLoadFailed` are confidence-relevant and covered by a valid assembly whose analyzer construction fails;
- physical generated-looking files retain ordinary path and drift enforcement, while actual source-generated provenance is explicit;
- generated-document lookup uses Roslyn document/project identity and cannot select another target framework by matching a virtual path;
- abandoned and post-evaluation cancellation paths dispose only unowned workspaces, with ownership transferred at the exact state-commit boundary before event delivery;
- synchronous analyzer/generator validation runs through the existing bounded abandon-and-discard wrapper, with blocked-construction cancellation coverage for load and full refresh;
- code exploration classifies directly resolved source-generated documents without rescanning every generated document for ordinary locations;
- all terminal load outcomes emit one bounded measurement; and
- project-count observations receive independent low-cardinality tag sets.

The final independent re-review was clean. It identified source-generated path continuations as an optional broader enhancement, not a Plan 117 blocker; this upgrade does not add that new continuation mechanism.
