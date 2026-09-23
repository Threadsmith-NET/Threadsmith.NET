# Retire the Original Scrollback TUI

**Status:** Active — implementation and automated validation complete; physical-terminal smoke pending
**Delivery track:** Maintenance
**Prerequisites:** TUIKit remains the default interactive frontend; shared interaction coordination and headless execution remain in place.

## 1 Objective

Remove the PrettyPrompt/Spectre native-scrollback frontend, its selection option, frontend-only code and tests, package dependencies, and current user/operator guidance. Keep the TUIKit and headless behaviors that already use shared host services.

## 2 Architectural Context

`Threadsmith.App` currently selects between `Threadsmith.Tui` and `Threadsmith.Tui.TuiKit`. Both call the same `InteractionCoordinator`. `Threadsmith.Tui` also owns configuration binding and theme persistence used by TUIKit, so removing the whole project before relocating those services would break the current frontend. ADR-15 and ADR-52 record the prior terminal choices; a new decision must state that TUIKit is the sole interactive frontend and supersede the relevant active guidance.

## 3 Scope

- Retire `--tui=original`; retain bare `--tui` and `--tui=tuikit` as TUIKit selectors. Reject the retired spelling before startup side effects.
- Remove the original frontend project and its PrettyPrompt/Spectre-only source, tests, solution/project references, packages, and release evidence that is no longer in the published dependency closure.
- Move shared configuration loading and theme persistence to an appropriate surviving host/configuration assembly without changing settings, precedence, persistence, or TUIKit behavior.
- Remove current documentation and manual procedures specific to the old frontend; update architecture authority and retained-frontend wording.

## 4 Non-Scope

Do not change the interactive coordinator, command policy, approvals, model/tool execution, headless output, or TUIKit rendering and input semantics except where a test or contract must be retargeted to the surviving path.

## 5 Current State

- `src/Threadsmith.App/InteractiveFrontendRunner.cs` constructs either `PrettyPromptConsoleSurface` or `TuiKitSurface`; `CommandLineParser.cs` accepts `--tui=original`.
- `src/Threadsmith.Tui/` contains the old surface and compatibility shell, plus `TuiDisplayOptions.cs` and `TuiThemes.cs`, which App still uses for TUIKit.
- Several test projects reference `Threadsmith.Tui`. `TuiPresenter`/`TuiController` are compatibility wrappers used by tests of shared `InteractionPresenter`/`InteractionController`. Other tests exercise only PrettyPrompt, native scrollback, and the old status/Markdown adapters.
- `Directory.Packages.props`, release legal evidence/scripts, package inventory, the solution, and current docs still describe or carry PrettyPrompt/Spectre.

## 6 Proposed Design

Make the TUIKit construction path the single interactive path in App. Put display option binding and theme loading/persistence in App or another existing configuration-owning assembly, with no terminal-library dependency. Instantiate `InteractionCoordinator` once through the surviving path. Remove the compatibility wrappers after retargeting shared-behavior tests directly to Interaction types; delete tests that only assert removed surface behavior.

## 7 Public Contracts

The CLI contract changes: `--tui=original` becomes an invalid selector. Shared `IInteractionSurface`, host DTOs, events, persistent records, and headless contracts remain unchanged. Preserve `tui:*` configuration keys still used by TUIKit; remove only old-frontend-only keys after an explicit usage audit.

## 8 Project/File Changes

- App: `InteractiveFrontendKind`, `CommandLineParser`, `InteractiveFrontendRunner`, relevant composition and CLI tests.
- Surviving shared configuration/theme code: relocate `TuiDisplayOptions` and theme loader/preference store; update imports and tests.
- Remove `src/Threadsmith.Tui/`; update `src/Threadsmith.sln`, App and test project references, architecture assertions, and central package pins.
- Tests: retarget shared controller/presenter, configuration, status, Markdown, and lifecycle coverage; delete only old-surface tests and old fixtures. Check `Milestone1Tests`, `TuiMarkdownRenderingTests`, `Plan96ActiveRunInputTests`, `SessionStatusTests`, repository lifecycle tests, and every project reference to `Threadsmith.Tui`.
- Docs/release: update README, user guide, keyboard/theming/semantic-refresh/resource-limit/manual-testing guidance, acceptance scenarios, manual test cases, ADR authority, package inventory, release scripts and license evidence.

## 9 Ordered Tasks

1. Inventory every `Threadsmith.Tui` type and test call site; classify shared behavior, configuration, and retired surface behavior. Record the public CLI/configuration compatibility decision.
2. Move the shared display/theme services to their surviving owner and retarget their tests. Verify TUIKit startup, theme changes, limits, and user configuration writes before deleting the old project.
3. Simplify frontend selection and App composition; add CLI rejection coverage for `--tui=original` and keep bare/explicit TUIKit and headless/MCP precedence checks.
4. Retarget shared behavior tests to `Threadsmith.Interaction`; delete PrettyPrompt/Spectre-only tests and fixtures. Remove the old project and references once no required tests depend on it.
5. Remove unneeded package pins and update release evidence from the actual new publish closure. Adapt license validation so it no longer requires PrettyPrompt; retain notices for any package that remains transitively shipped.
6. Update current docs and executable manual cases. Retire obsolete MTP IDs with replacement links; update affected acceptance scenarios. Record the new architecture decision rather than silently leaving accepted ADR-15/52 as current guidance. Treat completed implementation documents as historical records under planning governance.
7. Run the solution build, affected automated suites, publish/license/SBOM checks for supported RIDs, and a real-terminal TUIKit smoke check for startup, input, resize, cancellation, theme changes, and clean exit. Audit remaining references for accidental stale instructions.

## 10 Testing

Test CLI selection and rejection, shared controller/presenter behavior after migration, TUIKit configuration and theme persistence, startup and interaction flows, and headless/MCP precedence. Use architecture tests to assert no reference to the retired project or packages in the shipped graph. Validate published artifacts and notices from their resolved dependency closure.

## 11 Security/Permissions

Keep existing approval and trust paths in `InteractionCoordinator`. Removing a frontend must not bypass approval, cancellation, or output sanitization. Do not delete legal evidence merely because a direct package reference disappeared; verify the shipped closure first.

## 12 Observability

Existing domain events and headless results remain the source of truth. No new event or log format is required. TUIKit should continue showing progress, errors, and cancellation through its current surface.

## 13 Migration/Compatibility

Users invoking `--tui=original` receive a clear unsupported-selector error with `--tui` guidance. Existing valid TUIKit invocations and shared `tui:*` settings continue to work. Remove old-only settings only after confirming no current TUIKit consumer.

## 14 Acceptance Criteria

- The original frontend cannot be selected or built as a product project; TUIKit is the only interactive implementation.
- Shared behavior coverage remains through Interaction/TUIKit tests, while no old-frontend-only test or fixture remains.
- PrettyPrompt/Spectre are absent from the publish dependency closure and current package/legal metadata unless another live dependency requires them.
- Current user/operator docs contain no instructions to launch or test the retired frontend; historical decisions are clearly superseded.
- Build, affected suites, release validation, and manual TUIKit smoke checks pass.

## 15 Risks

Deleting `Threadsmith.Tui` before relocating shared configuration/theme code can silently alter the current frontend. Bulk deletion of tests can also erase shared behavior coverage hidden behind compatibility wrappers. Release metadata must follow the actual dependency closure, not a source-text search alone.

## 16 Documentation

Update current README/user/operator guidance, acceptance scenarios, manual procedures, and architecture authority in the implementation change. Keep completed implementation plans as historical evidence unless planning governance is deliberately revised.

## 17 Open Decisions

Confirm whether `--tui=tuikit` remains as an explicit alias after the old selector is removed. The proposed plan retains it to avoid breaking current callers.
