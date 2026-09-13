# TUI Themes

Threadsmith uses semantic terminal roles rather than hard-coded screen colors. The default `system` theme inherits the terminal foreground and background. Every built-in theme also leaves ordinary transcript and composer backgrounds at the console default; explicit backgrounds are reserved for actual highlights. No built-in theme automatically inverts role colors. Set `invert: true` explicitly in a custom theme when reverse video is desired, including for `SessionStatus`, `TitleBarRole`, and `AgentSelectedTabRole`. The composer prompt and transient `THINKING` indicator have distinct foreground roles without backgrounds. `NO_COLOR`, redirected output, and limited terminals suppress styling while preserving text and markers.

## Built-in themes

- `system` — terminal-native colors.
- `forge-dark` — restrained dark palette.
- `ocean` — blue/cyan palette.
- `high-contrast` — strong contrast with redundant bold/underline emphasis.

Use `/theme` for the single-selection filtered modal in TUIKit (numbered Up/Down/Enter selector in the original frontend), `/theme <id>` for direct selection, or `/theme current` to report the active theme. Selection atomically persists `tui.defaultTheme` to `~/.threadsmith/config.json` through a syntax-preserving targeted update; unrelated settings, comments, trailing commas, and surrounding formatting remain intact. Higher-precedence repository, session, CLI, or environment configuration may still override the user default at startup. The original frontend does not rewrite native scrollback; TUIKit repaints retained views with the current theme. Theme selection does not persist a domain event.

## Configuration

Set `tui:defaultTheme` and an ordered `tui:themes[]` array in normal layered configuration. Built-ins load first. A configured id replaces the complete earlier theme case-insensitively; a new id appends in declared order.

```json
{
  "tui": {
    "defaultTheme": "project-blue",
    "footer": { "enabled": true },
    "themes": [
      {
        "id": "project-blue",
        "name": "Project Blue",
        "styles": {
          "Hyperlink": { "foreground": "#5FAFFF", "underline": true },
          "SessionStatus": { "invert": true },
          "ToolSuccess": { "foreground": "green" },
          "ToolFailure": { "foreground": "red", "bold": true }
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

Styles accept existing semantic role names and the workspace roles below, supported named colors or `#RRGGBB`, and boolean `bold`, `dim`, `italic`, `underline`, `strikethrough`, and `invert` decorations. Missing values inherit only from the active theme's `Default` role. There is no fallback to another theme, including `system`. If `Default` also omits a value, colors use terminal defaults and decorations are off.

Named colors are case-insensitive: `black`, `red`, `green`, `yellow`, `blue`, `magenta`, `cyan`, `white`, `grey`, `brightblack`, `brightred`, `brightgreen`, `brightyellow`, `brightblue`, `brightmagenta`, `brightcyan`, and `brightwhite`. These use the terminal palette; `#RRGGBB` supplies an explicit RGB value. The TUIKit frontend preserves RGB output on modern Windows consoles even when Visual Studio or default-terminal activation omits terminal environment markers. On Linux and macOS, `COLORTERM=truecolor`/`24bit` and direct-color `TERM` entries preserve RGB output. An explicit terminal color limit and `NO_COLOR` remain respected. At startup and after a theme change, a visible warning identifies RGB themes being approximated by a limited terminal, or styling disabled by the environment. `gray` is not an accepted alias.

Each style accepts `foreground` and `background`, plus boolean `bold`, `dim`, `italic`, `underline`, `strikethrough`, and `invert`. Decorations can be combined; their visible effect depends on terminal support. `invert` reverses foreground/background. Foreground and background inherit independently. Decorations inherit as a set: omitting all decoration flags inherits the set from `Default`; specifying any flag replaces that set with the flags explicitly set to `true`. For example, `bold: false` alone clears inherited decorations. Built-in themes declare their Markdown decorations explicitly; custom themes must define any desired Markdown styling themselves or through `Default`. Styling suppression preserves text, borders, and navigation behavior.

Transient request, tool, and MCP activity uses the same semantic roles and serialized console boundary. With `tui:showOperationDurations` omitted or `true`, elapsed text updates only when its compact invariant value changes and never more than four times per second. Disabled mode retains operation state while hiding elapsed-duration text; there are no periodic duration updates. Timer ticks are presentation-only and never become events or transcript rows. Completed transcripts contain no host-generated `THINKING` marker.

In the original frontend, the enabled status surface is rendered immediately before each composer through the same serialized console boundary. It shows the working folder, repository, effective model and reasoning level, latest governed context estimate/limit/percentage, and cumulative provider tokens. `~` marks estimated values; `--` marks unknown context or wholly unavailable usage, and `+?` marks a known token subtotal followed by a provider request that omitted usage metadata. Long folders use end-biased abbreviation; narrow terminals omit folder and repository first, then truncate the model, rather than wrapping. Non-empty rows are padded by measured terminal cells to the current window width, and the complete row uses the selected theme's `SessionStatus` style, inheriting unspecified values only from that theme's `Default` role. Redirected output contains no status row. Set `tui:footer:enabled` to `false` to hide it without disabling usage accounting. Aggregate session usage retains durable totals; per-agent TUIKit counters after resume explicitly cover only newly observed requests. Startup reports the selected composer-adjacent or disabled mode and why a fixed footer is unavailable. A permanently pinned row is deferred because PrettyPrompt 6.0.4 has no public fixed-status API and cursor-managed pinning would violate native-scrollback compatibility.

Theme data is untrusted and bounded: at most 32 configured themes, ids up to 40 safe characters, names up to 80 characters, and UI values up to 40 characters. Control characters, raw ANSI/OSC data, unknown roles/settings, invalid colors, and unsupported spinners fail validation. The initial UI allow-list is `spinner` (`dots`), `selectionMarker`, and `footerSeparator`; the latter two are reserved for selector/footer consumption.

## Retained workspace role reference

Each role inherits unspecified style values only from its own theme's `Default` role. Values also omitted from `Default` use terminal colors and no decorations. Plain/NO_COLOR output keeps tab labels and borders. Live theme changes repaint all retained views.

| Role | Surface |
|---|---|
| `TitleBarRole` | The fixed top row containing `Threadsmith.NET`, including its background and padding. |
| `AgentTabHeaderRole` | The base background, unused space, and one-cell gaps between MAIN/child tabs. |
| `AgentSelectedTabRole` | The selected tab's text and background, including when MAIN is the only tab. Tab labels have one leading space and no selection marker. |
| `AgentNotSelectedTabRole` | Unselected tab labels and backgrounds, and overflow arrows. |
| `AgentStatusPaneRole` | The full-width unpadded status row immediately inside the output's top border: `Using model: (provider) model`, reasoning, token counts, and the right-aligned context progress bar/percentage/capacity. The graph shares this role's foreground and background; its unspecified background falls back to the output pane. |
| `OutputStreamPaneRole` | The output border, blank padding and separator below the header, plus the base background behind streamed text. Streamed text retains its own semantic foreground and decorations. |
| `ComposerBackgroundPaneRole` | The composer border, padding, base background, and read-only banner on child tabs. Prompt and entered text retain their existing text roles. |

Within the composer and output panes, the pane role paints the background of blank cells and ordinary text, including the composer input. A text role with its own explicit `background` overrides the pane behind that text; a background inherited from `Default` does not. Foreground colors and decorations continue to resolve from the text role and the same theme's `Default`. The pane role itself still uses `Default.background` when its background is omitted.

Foreground roles for Markdown, reasoning, tool outcomes, errors, and selection continue to apply over pane backgrounds. `SessionStatus` still styles the fixed repository footer. Agent context and counters live in the output header even when `tui:footer:enabled` is false.

Configure these exact role names under `tui.themes[].styles`; each accepts foreground, background, and the decorations described above. Dialogs reuse existing roles: `Default` for frames, padding and ordinary content; `SelectionPrompt` for titles; `SelectionHighlight` for selected choices; and `Status`/`Muted` for progress and hints. The startup logo uses `Brand`, completed startup phases use `Success`, and retained startup failure/cancellation messages use `Error`. The composer prompt uses `ComposerPrompt`, with entered text using `Default`.

Resource and retention numbers above are defaults. Configure the applicable `tui:limits` and `execution` settings as described in [Resource limits](resource-limits.md). Screen geometry and key bindings remain unchanged.
