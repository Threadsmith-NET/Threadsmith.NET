# Implementation Plan 24: TUI Semantic Styles and Theme Contracts

**Milestone:** M7.1 — TUI Visual System and Session Footer
**Strategy source:** User-approved post-strategy feature; §5.6 (UI as projection), §7.1 (host-owned boundaries), §18 (interactive terminal), §21.1–§21.2 (layered configuration), §22.2 (untrusted repository configuration)
**Prerequisite plans:** plan-03 (conversation-first terminal), plan-07 (configured model profiles), plan-08 (tool activity), plan-09 (context inspection)
**Status:** Complete. Semantic theme contracts, the terminal-native `system` theme, validated colors/decorations/link targets, capability-aware plain-text fallback, mixed-role rendering, and dedicated tool/hyperlink/reasoning/validation/diff segments are implemented and covered by terminal-neutral tests.

## 1. Objective

Replace the hard-coded amber/accent presentation with the terminal's native foreground and background by default, and establish a host-owned semantic visual system in which themes map stable text roles to optional foreground, background, and decoration settings.

## 2. Architectural Context

`ConversationalShell` currently passes a coarse internal `ConsoleTone` to `PrettyPromptConsoleSurface`; the adapter maps `Accent` to `Gold1`, which creates the default amber presentation. Selection prompts and transient status displays use Spectre.Console defaults independently. M7.1 must unify those visual choices without moving application state into the terminal adapter, leaking Spectre.Console or PrettyPrompt types across boundaries, or weakening the native-scrollback contract from plan-03 and ADR-15.

The theme contract is terminal presentation data. It belongs in `Threadsmith.Tui` (Layer 5), not `Threadsmith.Core`, durable domain events, extension contracts, or headless projections.

## 3. Scope

- Replace `ConsoleTone` with a semantic text-role vocabulary used by all TUI rendering paths.
- Add immutable host-owned theme/style records with optional foreground, background, and decorations.
- Make the compiled default theme inherit the terminal's current foreground and background for ordinary text and every unspecified style value.
- Define strict parsing and validation for named console colors and `#RRGGBB` values; never accept raw ANSI escape sequences.
- Keep style resolution inside the TUI adapter and preserve plain terminal-neutral text in tests and projections.
- Define fallback and accessibility behavior for missing colors, limited-color terminals, redirected output, `NO_COLOR`, and invalid configuration.

The initial semantic roles are:

| Role | Applies to |
|---|---|
| `Default` | Text with no more specific role, including assistant transcript text |
| `Brand` | Startup identity and product label |
| `Muted` | Secondary operational detail and collapsed markers |
| `Status` | Neutral lifecycle and command status |
| `Hyperlink` | Rendered URI/file link text; underline is a theme decoration, not embedded ANSI |
| `ToolSuccess` | Successful tool completion and bounded successful tool output |
| `ToolFailure` | Failed tool completion and bounded failed tool output |
| `SelectionPrompt` | Selection title and instructions |
| `SelectionItem` | Unselected solution/model/theme/trust/extension choices |
| `SelectionHighlight` | The currently highlighted selection item |
| `Success` | Non-tool success and completed validation |
| `Warning` | Warnings, degraded confidence, and approaching context limits |
| `Error` | Errors and rejected operations |
| `UserPrompt` | Composer label and user-turn label |
| `Reasoning` | Revealed sanitized reasoning and reasoning indicators |
| `DiffAdded` | Added diff lines |
| `DiffRemoved` | Removed diff lines |

## 4. Non-Scope

- No footer or footer telemetry; plan-26 owns that work.
- No `/theme` command or interactive theme switching; plan-25 owns selection and session state.
- No user-supplied markup, ANSI fragments, fonts, terminal palette mutation, or OSC palette commands.
- No theme data in domain events, SQLite session history, extension contracts, or headless CLI output.
- No change to native terminal selection, clipboard behavior, composer editing, or streaming batch size.

## 5. Current State

`ConsoleTone` exposes `Normal`, `Accent`, `Status`, and `Error`. `PrettyPromptConsoleSurface.WriteAsync` maps `Accent` to Spectre.Console `Gold1`, `Status` to grey, `Error` to red, and `Normal` to the terminal default. Selection prompts construct Spectre.Console choices without a shared Threadsmith style resolver. This is insufficient for tool outcomes, hyperlinks, highlighted selections, diff text, or user-configurable themes.

## 6. Proposed Design

- Add `TuiTextRole` and immutable `TuiTextStyle`/`TuiTheme` records in `Threadsmith.Tui`. A style contains nullable `Foreground`, nullable `Background`, and a bounded decoration set (`bold`, `dim`, `italic`, `underline`, `strikethrough`, `invert`). Null color values mean "inherit the terminal default"; they do not mean black or transparent.
- Add `ITuiThemeResolver` or one reused concrete resolver (do not introduce a single-use abstraction) that resolves a role through: selected theme → compiled `System` theme → terminal default.
- Add one internal rendering value passed through `IConsoleSurface`, such as `StyledTextSegment(Text, Role)`, so mixed-role output does not require embedded Spectre markup. Preserve a plain-text path for terminal-neutral tests and redirected output.
- Treat hyperlinks as text plus an optional validated URI target. When terminal hyperlink support is absent, render the visible URI/path as ordinary underlined or default text; never emit an unbounded OSC 8 payload.
- Honor `NO_COLOR` by suppressing foreground/background/decorations while retaining all words, status markers, selection prefixes, and failure distinctions.
- The compiled `System` theme assigns no foreground or background to `Default` or `Brand`. It may use safe decorations only where useful, but Threadsmith must not impose amber or any other base palette.

## 7. Public Contracts

- `TuiTextRole`, `TuiTextStyle`, `TuiTheme`, and theme parsing remain internal to `Threadsmith.Tui` unless a second product surface proves a shared host-owned contract is necessary.
- `IConsoleSurface` accepts semantic segments or roles, never Spectre.Console `Color`, `Style`, `Markup`, PrettyPrompt types, or raw ANSI.
- Existing Core events and projections remain unchanged.

## 8. Project and File Changes

- `src/Threadsmith.Tui/` — semantic roles, theme/style records, validation, resolution, and surface rendering.
- `src/Threadsmith.Tui/ConversationalShell.cs` — replace `ConsoleTone` call sites with semantic roles/segments.
- `tests/Threadsmith.CoreRuntime.Tests/` or a new `tests/Threadsmith.SessionStatus.Tests/` — role mapping, terminal-default inheritance, plain-text parity, invalid-style, and `NO_COLOR` coverage.
- `docs/operations/` — document theme color syntax and accessibility behavior when plan-25 lands.
- `docs/implementation-plans/manual-test-plan.md` — add real-terminal color/default/background/selection regression cases during implementation.

## 9. Ordered Implementation Tasks

1. Inventory every `ConsoleTone`, Spectre markup, selection, spinner, diff, tool, hyperlink, and composer-label rendering path.
2. Add the semantic role and immutable style/theme records with bounded validation.
3. Add the compiled `System` theme with terminal-default foreground/background and remove the `Gold1` default mapping.
4. Adapt `IConsoleSurface` to render semantic segments while retaining plain text for fakes and redirected output.
5. Migrate ordinary transcript, brand, status, error, tool, selection, reasoning, hyperlink, and diff output to the appropriate roles.
6. Add `NO_COLOR`, limited-color, invalid-color, and missing-role fallbacks.
7. Add unit and terminal-neutral tests before adding configured themes in plan-25.
8. Update the maintained manual test plan and applicable DOX documents.

## 10. Testing

- Verify `Default` and `Brand` resolve to no explicit foreground/background in the `System` theme.
- Verify every semantic role resolves deterministically and missing roles fall back to `Default`.
- Verify configured foreground and background values are applied independently.
- Verify invalid names, malformed hex, unsupported decorations, duplicate role keys, and raw control characters fail configuration validation without reaching the terminal.
- Verify `NO_COLOR` produces identical words and status markers with no styling sequences.
- Verify terminal-neutral shell tests remain independent of Spectre.Console types and styles.

## 11. Security and Permissions

Repository configuration is untrusted. Theme ids, labels, colors, URI targets, and decorations are length-bounded and character-validated. Raw ANSI, OSC, C0/C1 controls, markup, file reads, commands, and executable hooks are rejected. Themes can alter presentation only; they cannot alter permissions, approvals, tool availability, or host policy.

## 12. Observability

Log the selected theme id and validation/fallback reason at debug or warning level. Do not log full untrusted theme payloads or emit one event per rendered segment. Visual output remains a projection, not a durable domain-event stream.

## 13. Migration and Compatibility

The default visual behavior changes from an explicit amber accent to terminal-native colors. Existing terminal-neutral text remains byte-for-byte equivalent except where role-specific labels are intentionally clarified. Unsupported terminals and redirected output receive plain text. No persisted-state migration is required.

## 14. Acceptance Criteria

- A default interactive session does not set amber (or any other explicit base foreground/background) for ordinary or branded text.
- All listed semantic roles support independently optional foreground and background colors plus validated decorations.
- Success and failed tool output, hyperlinks, and every selection list use distinct semantic roles rather than embedded colors.
- Missing or partially specified styles inherit safely from `Default` and then the terminal.
- `NO_COLOR` and redirected output remain fully understandable without color.
- No terminal-library or theme type leaks into Core, extensions, durable events, or headless projections.

## 15. Risks and Mitigations

- **Foreground/background combinations may be unreadable:** ship contrast guidance and a high-contrast built-in; do not silently rewrite user colors.
- **Spectre and PrettyPrompt styling differ:** normalize through one TUI-owned resolver and test both output and selection paths.
- **Role proliferation:** keep roles semantic and stable; do not add roles named after literal colors or individual screens.
- **Escape-sequence injection:** accept only parsed named/hex colors and enumerated decorations.

## 16. Documentation

- Update `src/Threadsmith.Tui/AGENTS.md` with semantic-role and terminal-default contracts.
- Add theme syntax and `NO_COLOR` behavior to an operator-facing theme document in plan-25.
- Update `manual-test-plan.md` with Windows Terminal and Linux/macOS terminal cases when implemented.

## 17. Open Decisions

- Whether the terminal adapter should expose semantic segments directly or a small render-document type; choose the smaller design after the task-1 inventory.
- Whether OSC 8 hyperlinks are enabled by capability detection or remain visible URI text only in the first release.

