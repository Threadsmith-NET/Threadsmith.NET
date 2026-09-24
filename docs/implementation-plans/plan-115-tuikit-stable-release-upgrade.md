# Plan 115 — Upgrade TUIKit to the stable release line

**Status:** In progress — automated upgrade and release checks; physical-terminal matrix remains open  
**Delivery track:** Maintenance  
**Prerequisites:** Existing sole TUIKit frontend and shared interaction authority under ADR-62; the active checkout's retained agent workspace and release-license pipeline. No dependency on completion of plan 114.  
**Baseline:** TUIKit `0.10.1` → `1.1.1`, verified against upstream and NuGet on 2026-09-24.

## 1. Objective

Upgrade Threadsmith.NET's interactive frontend to the current stable TUIKit package, preserving established user interactions, host authority, bounded rendering, and release compliance. Deliver a centrally pinned dependency, necessary adapter changes, regression evidence, and matching shipped license evidence.

Implementation evidence and remaining validation are recorded in §18.

## 2. Architectural Context

Read root `AGENTS.md`, [planning governance](planning-governance.md), and [shared context §G](00-shared-context.md#g-implementation-document-template-and-agent-instructions). Before modifying C#, read [portable C# guardrails](../guardrails/portable-csharp-guardrails.md).

[ADR-62](../architecture/adr-62-sole-tuikit-interactive-frontend.md) owns the sole interactive frontend decision. `Threadsmith.Interaction` owns frontend-neutral commands and coordination; `Threadsmith.Tui.TuiKit` renders host projections and submits input. Keep policy, execution, approvals, tool/MCP dispatch, persistence, and cancellation on their established paths. Dependency direction is enforced by `tests/Threadsmith.Architecture.Tests/DependencyDirectionTests.cs`.

Release evidence must satisfy the existing [ADR-49](../architecture/adr-49-public-release-license-compliance.md) contract. A stable upstream label does not replace examination of the shipped package and embedded assets.

## 3. Scope

- Pin TUIKit `1.1.1` through Central Package Management.
- Adapt consumed APIs and event ordering in the existing frontend.
- Preserve terminal capability detection, input ownership, modal isolation, selection/copy, retained output, themes, and terminal restoration.
- Use capability-gated synchronized rendering through the existing TUIKit host.
- Refresh exact package, assembly, font, license, and publish-closure evidence.
- Validate automated behavior, physical terminals, and supported release artifacts.

## 4. Non-Scope

No new frontend, selectable legacy frontend, interaction coordinator, rendering loop, model tool, or execution path. No session/configuration schema migration. No automatic adoption of new widgets, terminal shell-out, full-repaint mode, or upstream text selection. Do not redesign the composer, agent workspace, Markdown pipeline, or context inspector as part of the dependency update.

## 5. Current State

Repository observations from the active checkout:

| Owner | Upgrade relevance |
|---|---|
| `Directory.Packages.props` | Pins `TUIKit` to `0.10.1`; product project consumes an unversioned package reference. |
| `TuiKitConsoleBackend.cs` | Wraps `ConsoleBackend`; its Windows/direct-color override reconstructs `TerminalCapabilities` using eight fields. |
| `TuiKitSurface.cs` | Owns one `TuiApplication`, a bounded update channel, custom Ctrl+C policy, key/paste handlers, disabled automatic mouse routing, and application mouse dispatch. |
| `TuiKitSurface.Workspace.cs` | Routes pointer input to modals, tabs, transcript, and composer; owns workspace focus behavior. |
| `TuiKitSurface.cs` overlay | Writes an orphan continuation cell at the bottom-right corner to avoid a terminal scroll/footer artifact. |
| `TuiKitComposer`, `ComposerBuffer`, `TranscriptView`, `UnicodeWidth` | Implement editing, selection, Unicode handling, reflow, retained-output limits, and copy semantics. |
| `TuiKitSurface.Retained.cs`, modal classes, `ContextUsageModal.cs` | Present host choices and information using the same frontend lifecycle. |
| `tests/Threadsmith.CoreRuntime.Tests/TuiKit*Tests.cs` | Existing frontend, command input, discovery, headless-backend, input ownership, copy/cancel, and rendering-failure coverage. There is no separate TuiKit test project. |
| `eng/release/release-license-evidence.json` | Records the exact `0.10.1` package and supplemental notices. |
| `eng/release/legal/TUIKit-0.10.1-candidate/` | Historical package/assembly/font hashes and legal texts; preserve as version-specific evidence. |
| `eng/release/Test-ReleaseContracts.ps1` | Includes a literal `TUIKit 0.10.1` notice assertion that must follow the new evidence. |

Upstream evidence, checked 2026-09-24:

- `1.0.0` is the first stable release. It adds synchronized rendering, full repaint, suspension, and a ninth `TerminalCapabilities` constructor parameter, `synchronizedOutput`.
- `1.1.0` adds optional screen-based mouse selection; `1.1.1` fixes first-press behavior for `DoubleTapToExit`. Threadsmith currently uses `Custom`.
- Intermediate releases add styling, modal/widget mouse handling, charts, and editor wrapping. `0.10.2` shipped a stale assembly and was superseded by `0.10.3`.

Sources: [upstream changelog](https://github.com/jchristn/TUIKit/blob/main/CHANGELOG.md), [NuGet version index](https://api.nuget.org/v3-flatcontainer/tuikit/index.json), [target package](https://www.nuget.org/packages/TUIKit/1.1.1). Moving upstream documentation is discovery evidence; implementation resolved the target package's immutable source revision and inspected that source and binary. Validation status is recorded in §18.

### 5.1 Target compatibility ledger

The `1.1.1` archive identifies source revision `3496eeac430d1718e5acc34520565305e089d5ae`. The exact `net10.0` assembly and this revision were inspected on 2026-09-24.

| Consumed behavior | Caller and target behavior | Change and verification |
|---|---|---|
| Terminal capabilities | `TuiKitConsoleBackend` reconstructs the detected set when correcting RGB depth; target adds `SynchronizedOutput`. | Preserve the ninth flag without inferring support from color depth. Four override/pass-through combinations are tested. |
| Rendering | `TuiApplication` sets `TerminalRenderer.SynchronizedOutput` from backend capabilities; the renderer wraps each nonempty frame in one balanced write. Teardown sends an end sequence. | Keep the established renderer and incremental mode. Headless tests check capability-gated pairs and backend stop. |
| Modal mouse dispatch | Target `DispatchMouse` sends every event to the top modal before `MouseReceived`, even with automatic widget routing disabled. | `ContextUsageModal.HandleMouse` now overrides `Modal.HandleMouse`; remove the old unreachable modal branch from `RouteMouse`. Preserve capture/startup/size gating and clear any prior drag owner while a modal is active. Application-pump and capture-disabled regressions cover delivery and isolation. |
| Selection and capture | The exact target `TuiApplication` has no `MouseTextSelectionEnabled` API. Threadsmith's transcript/composer and F12 handoff remain the selection owners. | Keep `EnableMouseRouting = false` and existing raw routing. Existing copy, focus, and input tests pass. |
| Bottom-right cell | Target `TerminalRenderer.EmitRow` still writes every non-continuation cell through the last column, including on full first frames. | Retain the continuation-cell workaround. A physical-terminal reproduction remains required before removal. |
| Key, paste, disposal | Target still supports `CtrlCPolicy.Custom`, modal paste trapping, `PumpInputOnce`, and `Stop` cleanup. | Keep existing loop, leases, and bounded channel; remove the unreachable second modal-paste branch in the global handler. Focused input and rendering-failure suites exercise them. |

## 6. Proposed Design

### 6.1 Exact package and compatibility audit

Target `1.1.1`, the latest published stable version at planning time, rather than stopping at the initial `1.0.0`. At implementation time confirm availability, source revision, supported framework asset, dependency closure, package hashes, and actual assembly identity/API. Do not silently substitute a newer release or floating range; record any target change and repeat the relevant audit.

Build a small compatibility ledger in this document: consumed API/event, current caller, target behavior, required edit or justified retention, and verifying test. Inspect the exact source between `0.10.1` and the target for hosting, rendering, input routing, modal dispatch, capability detection, and teardown. A successful compile is only one gate.

### 6.2 Capability wrapper and rendering

Extend the existing capability reconstruction to preserve `detected.SynchronizedOutput` alongside all existing fields. The RGB override must change only color depth. Test both override and pass-through paths, with synchronized output supported and unsupported; do not infer synchronization support merely from Windows or true-color support.

Allow `TuiApplication` to wire synchronized rendering from backend capabilities. Do not introduce another renderer or write duplicate frame wrappers. Verify frame start/end pairing and terminal restoration after failures with a recording backend, then in a real supporting terminal.

Keep incremental rendering as the default. Inspect the bottom-right continuation workaround against the target renderer and reproduce its original trigger. Remove it only if the target handles the final cell correctly and regression coverage demonstrates a fixed footer without autoscroll; otherwise retain it with a precise explanation. Do not enable persistent full repaint to conceal an unexplained defect.

### 6.3 One mouse and selection owner

Trace target `TuiApplication` dispatch through modal trapping, `EnableMouseRouting`, and `MouseReceived` before modifying handlers. Threadsmith currently dispatches modal pointer events via `RouteMouse`; target modal interception could change whether that fallback is reached. Exercise actual injected input through the application pump, not only direct widget calls.

Preserve exactly one delivery for each pointer event. If the new modal hook intercepts events before the existing route, adapt the existing modal implementations to that hook and remove the redundant modal branch. Keep coordinate translation consistent with each hook's contract. Do not turn on automatic widget routing while leaving a competing custom route active.

Keep upstream `MouseTextSelectionEnabled` disabled and make this choice explicit at application construction. Retain Threadsmith's transcript/composer selection, copy behavior, focus rules, and F12 handoff. Replacing these with screen-cell selection requires a separate behavior proposal and parity evidence; sharing a copy formatter would not establish shared selection ownership.

### 6.4 Input and lifecycle

Preserve `CtrlCPolicy.Custom`, input leases, ordinary/secondary/steering draft ownership, paste sanitization, Unicode editing, completion insertion without execution, modal cancellation, and startup input suppression. Threadsmith's custom composer should not be replaced merely because upstream gained wrapping or styles.

Preserve the single UI loop and bounded update channel. Inspect event subscriptions, pending reads, cancellation, disposal, and backend cleanup after startup/render failure. No second process-exit handler or suspension lifecycle is needed for this upgrade. Propagate cancellation through all changed asynchronous boundaries.

### 6.5 Exact legal and release closure

Use the established release scripts and evidence format. Create version-specific `eng/release/legal/TUIKit-1.1.1/` evidence from the actual archive and its source revision: package SHA-256/SHA-512, selected .NET assembly identity/hash, embedded-resource inventory/hashes, original license and attribution texts, and font headers where applicable. Compare against the prior evidence; do not copy old digests or assume the resource count/terms stayed constant.

Update the canonical evidence entry, supplemental notices, dependency inventory, and notice assertions atomically with the package upgrade. Reuse existing notice/SPDX generation and compliance checks. Preserve the old evidence directory as a historical record. New or changed terms must follow the existing review-owner process; do not manufacture an approval or bypass an expired/failed gate.

## 7. Public Contracts

No new public host DTOs, commands, configuration keys, events, or persistence formats are expected. Terminal and widget types remain internal to the frontend. Bare `--tui` and `--tui=tuikit` still select the sole frontend; invalid/retired selectors still fail before startup. Headless outcomes and approval semantics remain identical.

Any necessary observable behavior change must be documented here before implementation and reflected in the owning acceptance/manual documentation. An upstream default is not authority to change host behavior.

## 8. Project/File Changes

All source paths below are relative to the repository root; frontend filenames are under `src/Threadsmith.Tui.TuiKit/`.

| Path | Expected work |
|---|---|
| `Directory.Packages.props` | Pin `1.1.1`; no inline versions. |
| `TuiKitConsoleBackend.cs` | Preserve the additional capability through the existing color override. |
| `TuiKitSurface.cs`, `.Workspace.cs`, `.Retained.cs`, affected modal classes | Focused target-API/routing adaptations and explicit selection policy, based on the audit. |
| Existing composer/transcript/render helpers | Change only where a demonstrated target incompatibility requires it. |
| `tests/Threadsmith.CoreRuntime.Tests/TuiKitFrontendTests.cs`, `TuiKitCommandInputTests.cs`, `TuiKitCommandDiscoveryTests.cs` | Extend existing behavioral coverage; reuse headless/recording backend seams. |
| `tests/Threadsmith.Architecture.Tests/DependencyDirectionTests.cs` | Run existing boundary checks; modify only if a legitimate contract change is identified. |
| `eng/release/legal/TUIKit-1.1.1/`, `eng/release/release-license-evidence.json` | Exact reviewed package and embedded-asset evidence. |
| `eng/release/Test-ReleaseContracts.ps1` | Target-version notice checks without weakening closure requirements. |
| `docs/third-party-license-inventory-status.md`, current version-bearing guidance | Reflect the resolved dependency and evidence. |
| This plan and planning `README.md` | Plan owns progress/evidence; README contains one navigation row. |

## 9. Ordered Tasks

1. **Establish baseline.** Confirm `git rev-parse --show-toplevel` is `C:/source/repos/Threadsmith`, inspect working changes and instructions, and read the affected implementation/tests. Record baseline build/test failures separately. Work in this checkout; do not substitute another source tree.
2. **Close target evidence.** Resolve `1.1.1` package/source identity and fill the compatibility ledger. Identify affected transitive packages and legal differences. Keep target package inspection bounded to the consumed library and published closure.
3. **Implement the focused upgrade.** Change the central pin, capability adapter, and demonstrated API/routing incompatibilities together. Keep existing selection ownership and incremental rendering. Add meaningful regression tests for affected boundaries.
4. **Close packaging evidence.** Generate exact version-specific evidence through the established workflow, update canonical notices/inventory/assertions, and run legal contract checks. Do not declare the package shippable while these disagree.
5. **Validate integration.** Run focused tests, architecture tests, then the required solution build/test gate. Re-review manual input, model-driven updates, and internal progress paths through the shared coordinator and the real UI pump.
6. **Validate terminals and artifacts.** Execute the matrix in §10; record commands, platform/terminal, results, and unavailable environments. Produce local release evidence for all supported RIDs using isolated output directories within the workspace. Do not publish externally.
7. **Close documentation and review.** Update only current owned contracts/procedures affected by the upgrade. Perform the adversarial review in §10, run planning checks and `git diff --check`, and record completion only when all acceptance gates are satisfied.

Do not stage, commit, push, reset, or perform destructive Git operations. If blocked, leave a precise remaining-task record here instead of declaring unexercised paths clean.

## 10. Testing

### Automated regression gates

Extend existing tests to cover consequences of this upgrade:

- Capability propagation preserves every non-color flag under RGB override; synchronized rendering is gated by actual backend support. Emitted frames and failure teardown cannot leave synchronization held open.
- Real application dispatch delivers modal clicks once, blocks underlying tabs/composer while a modal is open, and resumes normal focus/routing after closure. Include wheel, resize, startup overlay, and mouse-capture-disabled cases.
- Selection/copy via Ctrl+C, F6, and Ctrl+Shift+C retains existing behavior; Ctrl+C with no selection reaches host cancellation exactly once. Test F12 handoff and Unicode selection through the existing pipeline.
- Existing draft ownership, completion, command discovery, paste, startup suppression, active-run input, and rendering-failure tests pass unchanged or with justified behavioral improvements.
- Retained output stays bounded and reflows correctly; footer/composer/modal edges remain intact at 40 x 12 and larger dimensions, including wide/combining text and the bottom-right cell.
- Existing shared-interaction and architecture coverage verifies no terminal types/dependencies escape the frontend and no alternate execution path appears.

Run focused `Threadsmith.CoreRuntime.Tests` and `Threadsmith.Architecture.Tests` with the repository's Microsoft.Testing.Platform conventions, followed by the contributor/CI gate:

```powershell
dotnet restore src/Threadsmith.sln
dotnet build src/Threadsmith.sln --configuration Debug --no-restore
dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4
pwsh -File eng/release/Test-ReleaseLicenseEvidence.ps1
pwsh -File eng/release/Test-ReleaseContracts.ps1
```

Use existing `Publish-Release.ps1`, `New-ReleaseLegalArtifacts.ps1`, and `Test-ReleaseCompliance.ps1` workflows to verify `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64` artifacts. Inspect script parameters before invocation: `Publish-Release.ps1` cleans its output root, so use a new verified workspace-local output directory per RID. Verify the published assembly/version, dependency closure, notices, and SPDX output all describe the target. Cross-publishing does not count as native execution.

### Physical-terminal gates

Run [MTP-257 and MTP-259](manual-test-plan.md), plus MTP-271 for the context inspector, using their current procedures. Cover Windows Terminal/PowerShell, a Linux terminal, and macOS Terminal; include a synchronization-capable terminal and an unsupported/fallback environment. Record terminal versions, dimensions, theme/`NO_COLOR`, and capability observations.

Verify streaming plus resize, modal pointer isolation, agent tabs, offscreen-output notices, Unicode copy/paste, final-row stability, and restoration on normal quit, Ctrl+C, cancellation, startup failure, and rendering failure. Check synchronized-output restoration explicitly. Reuse disposable fixtures for controlled failure paths. A headless backend cannot establish terminal mode restoration or flicker behavior on physical terminals.

Acceptance references: Scenarios H, AR, AS, AU, and AX for interactive behavior; Scenario AI for release-license closure; Scenario O for packaged release behavior. Exercise the existing host outcome fixtures behind those scenarios rather than adding a parallel coordinator.

### Adversarial review

Trace changed capabilities to all relevant callers outside the diff. Check for duplicate input/selection owners, modal mouse events lost before fallback, direct frontend execution bypassing policy, invisible or duplicated tool/child activity, and cancellation/disposal races. Review whether upstream functionality actually replaces an old workaround before removing it.

Compare streaming/resize work with the baseline using the same bounded transcript fixture. Check retained memory, frame work, and responsiveness for regression; record measurements and distinguish source-based estimates. Do not introduce full-history preloads, full transcript rebuilds per input event, unbounded queues, or unconditional repainting. Record unassessed platforms explicitly.

## 11. Security/Permissions

Existing trust, approvals, terminal-content sanitization, clipboard/link validation, and secret redaction remain authoritative. Test that pasted input cannot submit itself or escape a modal into the composer. Upstream capabilities do not grant shell execution, file mutation, clipboard access beyond current behavior, or delegation authority. Package/legal downloads are inspection inputs; repository configuration remains data.

## 12. Observability

Preserve existing activity/progress/completion events and shared logs. Rendering and copy gestures must not create duplicate domain events or tool invocations. Record package/source/hash identity and validation results as implementation/release evidence. Use existing sanitized diagnostic paths for failures; do not log drafts, clipboard contents, secrets, or raw terminal input.

## 13. Migration/Compatibility

This is a binary dependency and adapter upgrade, with no expected user-data migration. Existing themes, shortcuts, command selectors, session restore, headless operation, and resource limits remain compatible.

If the target cannot satisfy a required contract, retain proposed/in-progress status and record the failing trigger and evidence. Any rollback must restore the central pin, matching adapter code, and canonical legal/notices as one reviewed change; never combine an old binary with new evidence. Do not execute destructive rollback commands without authorization.

## 14. Acceptance Criteria

- [x] Central pin and actual resolved/published package are `1.1.1`, with exact immutable provenance recorded (the assembly identity is `1.1.0.0`).
- [x] Compatibility ledger covers consumed APIs, routing, capabilities, rendering workarounds, and teardown.
- [x] Capability reconstruction preserves synchronized-output support without broadening detection.
- [x] One input/render lifecycle and one selection owner remain; modal mouse input is delivered once and cannot reach underlying controls.
- [x] Existing input, cancellation, command discovery, agent workspace, retained transcript, and headless contracts pass.
- [x] Required automated build/test, architecture, and release contract gates pass; baseline failures are distinguished.
- [ ] Physical-terminal matrix demonstrates rendering and restoration; unavailable required environments remain open work.
- [x] All six RID artifacts have matching reviewed legal evidence, notices, SPDX, and dependency closure.
- [x] Current version-bearing documents are accurate; historical plans/evidence and completed milestones remain intact.
- [x] Adversarial review has no unresolved upgrade regression; measured claims include evidence and limitations.

## 15. Risks

| Risk | Mitigation |
|---|---|
| Constructor compiles after a default is supplied but drops a capability | Preserve detected fields and test both adapter branches. |
| New modal dispatch starves or duplicates existing mouse handling | Inspect target event ordering and test through the application pump. |
| Upstream selection competes with transcript/composer selection | Explicitly disable it; retain existing ownership. |
| Final-cell workaround interacts badly with new rendering | Reproduce on a real terminal and record remove/retain evidence. |
| Full repaint hides corruption while increasing output/CPU | Keep it off; diagnose through the established renderer. |
| Package metadata disagrees with its binary or bundled legal assets | Verify the archive, actual assembly, and embedded resources. |
| Automated tests pass while terminal modes remain broken | Require physical-terminal exit/failure evidence. |
| Parallel feature work changes modals/workspace during implementation | Re-read the active checkout and adapt the surviving paths without duplicating them. |

## 16. Documentation

Add one navigation row for this plan. During implementation update the current TUIKit version in `00-shared-context.md` and other active version-bearing guidance, plus the release inventory/evidence. Preserve historical ADR decisions, completed plans, and old package evidence. ADR-62's frontend ownership decision does not need reopening for a version bump.

Update acceptance scenarios only for changed observable behavior/invariants; update manual procedures only for changed executable checks, including the synchronized-output check where needed. Do not backfill milestone status, historical completion prose, or work-item dependencies into navigation/acceptance documents.

Run the planning-governance prohibited-bookkeeping searches and `git diff --check`. Record implementation status and remaining verification here only.

## 17. Open Decisions

1. **Bottom-right workaround:** retain unless target-source inspection and terminal evidence establish that removal is correct. Decide during task 3 and record the reproduction.
2. **Modal hook adaptation:** choose the minimal single-path change after inspecting target dispatch; do not assume the old `MouseReceived` fallback remains reachable.
3. **Target changes after planning:** `1.1.1` is fixed for this plan. A later package requires an explicit documented target revision and renewed compatibility/legal evidence.

Upstream mouse-selection adoption, permanent full repaint, new widgets, and suspend/resume features are outside this upgrade and do not block it.

## 18. Implementation and verification record (2026-09-24)

The active checkout was `C:/source/repos/Threadsmith`; the baseline Debug solution build passed with zero warnings and errors. The central pin now resolves `TUIKit/1.1.1` and the exact package/source/binary inventory is in `eng/release/legal/TUIKit-1.1.1/candidate-evidence.json`. The selected `net10.0` assembly identity is `TUIKit, Version=1.1.0.0`. The raw signed archive SHA-256 is `e1c5e872c78acf1e700fc2e7a778e612e158c9dac8dd56063173a84ba077dff3`; its raw SHA-512 and NuGet restore content SHA-512 are recorded separately because they differ. All 83 resource hashes and five document hashes were rechecked from the new archive and source revision. The prior 0.10.1 legal files are byte-identical where reused, so no new font/license terms were observed.

The existing frontend remains the only terminal path. Target-source review found the modal mouse trap before raw `MouseReceived`, capability-gated synchronized frames, balanced teardown, and no target `MouseTextSelectionEnabled` API. The context inspector now uses the modal hook, guards its wheel behavior when capture/startup/size disallow mouse input, and clears stale workspace drag state. The global paste handler no longer contains an unreachable second modal route. The final-cell continuation remains because the target renderer still emits every ordinary cell through the last column. No full repaint or suspension path was enabled.

Focused `Threadsmith.CoreRuntime.Tests` passed (645 succeeded, 2 skipped); `Threadsmith.Architecture.Tests` passed (269 succeeded, 4 skipped). A pre-existing strict sample-binding failure was repaired by removing six obsolete semantic keys from `.threadsmith/resource-limits.example`. Two pre-existing semantic-admission tests used the generic query `Widget`, which no longer identifies a C# target; their fixture now uses `Widget.cs`, and the isolated parallel-agent suite passes (279 succeeded, 1 skipped). The initial four-module solution run had those two failures plus one timing-sensitive skill-progress assertion; the latter passed in the isolated CoreRuntime run. The closing Debug solution build passed with zero warnings and errors; the four-module solution test passed with 3,527 succeeded, 28 skipped, and zero failed. The focused CoreRuntime suite passed again after the final teardown assertion was strengthened.

`Test-ReleaseLicenseEvidence.ps1` and `Test-ReleaseContracts.ps1` pass. Isolated `Publish-Release.ps1` runs for `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, and `osx-arm64` passed local compliance. Every staged payload contains the exact inspected TUIKit DLL hash, a `TUIKit/1.1.1` dependency, the TUIKit 1.1.1 notice including the font terms, and an SPDX entry declaring MIT with aggregate `NOASSERTION`. Final artifacts are under `artifacts/plan115/final-release/` and were not uploaded. The GitHub build, release, and deploy workflows use central restore and the existing release scripts without a version literal, so no workflow edit is required.

The physical-terminal matrix is unassessed. This command environment is Windows 10.0.26200 with redirected input/output, `TERM=dumb`, and `NO_COLOR`; it cannot establish terminal flicker, final-row stability, or mode restoration. Linux and macOS terminals are unavailable here. MTP-257, MTP-259, and MTP-271 therefore remain required before marking this plan complete. Streaming/resize performance was reviewed from source: the bounded update channel and incremental renderer remain in use, and synchronized mode adds one bounded wrapper per nonempty frame; no same-fixture baseline measurement was available, so responsiveness equivalence is not claimed.

The first adversarial review traced the changed frontend through the existing interaction surface and coordinator without finding a new tool, model, or execution path. It found and resolved the modal mouse trap, stale drag ownership, capture-disabled wheel handling, redundant modal paste branch, and a miscopied assembly digest in the new evidence file. A follow-up review identified a 250 ms wait after each successful startup phase, missing application-pump guard coverage, and lost high-cardinality context-modal coverage. The repeated wait was replaced by one caller-cancellable, startup-wide visual dwell capped at 250 ms; production-pipeline tests now exercise F12 capture, resize, and startup guards, and a 1,200-entry End/resize/allocation regression was restored. The exact source still writes the bottom-right ordinary cell, so the established continuation workaround remains. Physical terminal behavior and same-fixture performance measurement remain explicitly unassessed.
