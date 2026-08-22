# Implementation Plan 25: Configured Themes and `/theme` Selection

**Milestone:** M7.1 — TUI Visual System and Session Footer
**Strategy source:** User-approved post-strategy feature; §5.6 (UI as projection), §18 (interactive terminal), §21.1–§21.2 (layered configuration), §22.2 (untrusted repository configuration)
**Prerequisite plans:** plan-24 (semantic styles and theme contracts), plan-03 (selection surface and slash-command routing)
**Status:** Complete. Layered theme configuration, deterministic catalog replacement/order, four built-ins, atomic user-default persistence, immediate adapter refresh, and local `/theme` selector/direct/current commands are implemented and covered by terminal-neutral tests.

## 1. Objective

Add layered configuration for an ordered array of visual themes, ship several usable built-in themes, and add a host-owned `/theme` command that changes the active session theme through the same Up/Down/Enter selector used for solutions, models, trust, and extensions.

## 2. Architectural Context

Plan-24 supplies semantic roles and safe style resolution. This plan binds theme data through `Microsoft.Extensions.Configuration`, merges configured themes with compiled built-ins, owns active-theme session state, and projects selection through the existing `IConsoleSurface.SelectAsync` seam. The command remains local to the TUI and must never be sent to the model.

Theme choice is a presentation preference, not execution state. It initializes from layered configuration; interactive selection atomically updates `tui.defaultTheme` in ordinary user configuration while preserving unrelated settings. It does not become a durable domain event or repository capability, and higher-precedence ordinary layers may override the user default.

## 3. Scope

- Add `tui:defaultTheme` and ordered `tui:themes[]` configuration options.
- Bind, validate, and merge themes deterministically by case-insensitive id; later configuration layers may replace a whole theme by id, never partially merge ambiguous array indices.
- Ship at least four compiled themes:
  - `system` — terminal-native foreground/background; the default.
  - `forge-dark` — restrained dark-background palette with distinct success/failure/selection roles.
  - `ocean` — cool blue/cyan palette.
  - `high-contrast` — strong contrast and redundant decorations for accessibility.
- Add `/theme` with no arguments to open the usual arrow-key selector.
- Add `/theme <id>` for scripts and keyboard-only direct selection, plus `/theme current` to report the effective theme.
- Apply a selected theme immediately to subsequent transcript output, selectors, spinners, hyperlinks, composer labels, and the plan-26 footer.
- Show built-in and configured themes together, clearly label the active theme, and include a Cancel choice that leaves it unchanged.

Example configuration shape:

```json
{
  "tui": {
    "defaultTheme": "system",
    "themes": [
      {
        "id": "project-blue",
        "name": "Project Blue",
        "styles": {
          "Default": { "foreground": null, "background": null },
          "Hyperlink": { "foreground": "#5FAFFF", "underline": true },
          "ToolSuccess": { "foreground": "green" },
          "ToolFailure": { "foreground": "red", "bold": true },
          "SelectionHighlight": { "foreground": "black", "background": "cyan", "bold": true }
        },
        "ui": {
          "spinner": "dots",
          "selectionMarker": ">",
          "footerSeparator": " | "
        }
      }
    ]
  }
}
```

## 4. Non-Scope

- No online theme marketplace, remote theme loading, arbitrary theme files, or extension-provided executable renderers.
- No automatic light/dark terminal detection beyond terminal-default inheritance.
- No persistence in SQLite or synchronization of theme choice across machines.
- No user-defined glyph widths, cursor-control sequences, scripts, or custom Spectre markup.
- No footer implementation; plan-26 consumes the selected theme's validated UI settings.

## 5. Current State

`TuiThemeConfigurationLoader` binds the effective `tui` configuration, validates untrusted theme/style/UI data, and merges it into `ConfiguredThemeCatalog` after the four compiled themes. `SessionThemePreferences` owns active process state, and `UserConfigurationThemePreferenceStore` performs an atomic syntax-preserving targeted update of `tui.defaultTheme`, retaining unrelated `~/.threadsmith/config.json` settings, comments, trailing commas, and surrounding formatting. `ConversationalShell` routes selector, direct, and current `/theme` commands locally, while `PrettyPromptConsoleSurface.SetThemeAsync` refreshes subsequent PrettyPrompt and Spectre.Console rendering without changing prior scrollback.

## 6. Proposed Design

- Add a `TuiThemeConfigurationLoader` and `ConfiguredThemeCatalog` inside `Threadsmith.Tui`. Built-ins load first; configured entries replace the same id or append in configuration order after validation.
- Avoid relying on array-index overlays across configuration layers. Bind each provider's `tui:themes` array independently, then normalize all definitions by id in provider order with deterministic last-definition-wins semantics and a warning for replacement.
- Keep a small `SessionThemePreferences` object owned by the interactive composition root. It exposes the validated active theme id/snapshot and changes only at serialized input boundaries.
- `/theme` calls `SelectAsync` with numbered choices. On selection, update the session preference and the surface's resolver atomically before printing confirmation. Cancel or cancellation leaves the active theme unchanged.
- Theme `ui` settings are a bounded allow-list initially containing `spinner`, `selectionMarker`, and `footerSeparator`. Unknown settings fail validation so typos do not silently drift.
- Built-in themes are code/data assets covered by snapshot-style semantic tests; they never assume a terminal background for `system`.

## 7. Public Contracts

- Configuration keys are `tui:defaultTheme` and `tui:themes[]`.
- Theme ids are stable, case-insensitive configuration identities; names are display labels only.
- The theme catalog and session preference remain TUI-owned. The App composition root may construct and inject them but does not interpret style values.
- `/theme`, `/theme <id>`, and `/theme current` are host commands and never enter the model transcript.

## 8. Project and File Changes

- `src/Threadsmith.Tui/` — configuration DTOs/loader, catalog, built-in themes, session preference, command handler, and surface refresh.
- `src/Threadsmith.App/Program.cs` — bind the effective theme catalog and initialize the interactive preference.
- `.threadsmith/config.example` — document the complete theme array and supported semantic/UI settings.
- `tests/Threadsmith.SessionStatus.Tests/` (preferred) — configuration, catalog, command routing, selection, and built-in theme tests.
- `docs/operations/tui-themes.md` — operator reference and examples.
- `docs/operations/keyboard-shortcuts.md` — add `/theme` commands.
- `docs/implementation-plans/manual-test-plan.md` — selection, immediate switching, invalid config, accessibility, and cancellation cases.

## 9. Ordered Implementation Tasks

1. Define the configuration DTO shape and safe validation limits using plan-24 style contracts.
2. Implement the built-in `system`, `forge-dark`, `ocean`, and `high-contrast` themes.
3. Implement deterministic catalog construction and configured-theme replacement by id.
4. Add session-owned active-theme state and initialize it from `tui:defaultTheme`, falling back to `system` with a warning.
5. Route `/theme`, `/theme <id>`, and `/theme current` locally and update `/help`.
6. Atomically persist successful selections as the user-layer `tui.defaultTheme` while preserving unrelated settings and leaving active state unchanged on write failure.
7. Render the no-argument command with the existing numbered Up/Down/Enter selector and a fail-closed Cancel choice.
8. Apply theme changes atomically to all subsequent render paths without recoloring prior scrollback.
9. Add configuration, command, selector, cancellation, persistence, built-in completeness, and no-model-dispatch tests.
10. Update config examples, operator docs, the manual test plan, and applicable DOX documents.

## 10. Testing

- Every built-in defines or safely inherits every plan-24 semantic role.
- `system` remains foreground/background-neutral.
- Multiple configured themes retain declared order; duplicate ids resolve deterministically with an observable warning.
- Unknown default ids, invalid colors, controls in labels/markers, oversized arrays/strings, and unsupported UI settings fail or safely fall back as documented.
- `/theme` uses `SelectAsync`, marks the active theme, handles Cancel, and does not dispatch a model command.
- `/theme <id>` changes subsequent semantic rendering immediately, atomically preserves unrelated user settings while persisting the default, and leaves prior native scrollback untouched.
- `NO_COLOR` suppresses styling even when a colored theme is active.

## 11. Security and Permissions

Themes are untrusted presentation data. Apply bounds to theme count, ids, labels, style count, strings, and separators. Reject control characters, raw escape sequences, URI loads, local file references, commands, and executable hooks. Repository theme configuration cannot broaden permissions or override host policy. A repo may suggest a theme only through normal precedence; trusted CLI/session configuration can override it.

## 12. Observability

Emit one structured startup log for catalog size/effective theme id and one log for each user-initiated theme change. Warnings identify invalid or replaced theme ids without echoing unsafe payloads. Do not log every style resolution or render.

## 13. Migration and Compatibility

Absent configuration selects `system`, preserving terminal-native colors from plan-24. Existing config files remain valid because `tui` is optional. Theme changes affect only future output and do not rewrite scrollback. Headless output is unchanged.

## 14. Acceptance Criteria

- Configuration accepts an ordered array of validated themes with foreground, background, decorations, and bounded UI settings.
- `system` is the default and uses the console's current color scheme.
- At least `system`, `forge-dark`, `ocean`, and `high-contrast` ship as built-ins.
- `/theme` shows configured and built-in themes in the established Up/Down/Enter selection experience.
- Cancel leaves the theme unchanged; direct selection of an unknown id fails locally and never reaches the model.
- A selected theme applies immediately and consistently across transcript, hyperlinks, tool outcomes, selectors, composer labels, and later footer rendering.
- Invalid/untrusted theme data cannot inject terminal control sequences or change host behavior.

## 15. Risks and Mitigations

- **Layered arrays are ambiguous:** normalize the effective array by stable id and document replacement semantics.
- **Theme changes create mixed-color scrollback:** explicitly apply only to future output and show a concise confirmation.
- **Color-only meaning harms accessibility:** retain textual success/failure markers and ship a high-contrast theme with redundant emphasis.
- **Over-configurability destabilizes layout:** expose only enumerated, bounded UI settings.

## 16. Documentation

- Add `docs/operations/tui-themes.md` with schema, built-ins, precedence, accessibility, `NO_COLOR`, examples, and limits.
- Update `.threadsmith/config.example`, `/help`, keyboard shortcuts, and `manual-test-plan.md` during implementation.
- Update `src/Threadsmith.Tui/AGENTS.md` with catalog/session/theme-command ownership.

## 17. Open Decisions

- Resolved: a user-selected theme persists to the ordinary user configuration layer as `tui.defaultTheme`; it remains presentation-only and is still subject to normal higher-layer precedence.
- Whether repo-configured themes should be selectable but never auto-activated. Default assumption: normal layered precedence may set `tui:defaultTheme`; validation and higher trusted layers remain authoritative.
