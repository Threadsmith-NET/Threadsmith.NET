# TUI Themes

Threadsmith uses semantic terminal roles rather than hard-coded screen colors. The default `system` theme inherits the terminal foreground and background. Every built-in theme also leaves ordinary transcript and composer backgrounds at the console default; explicit backgrounds are reserved for actual highlights. The full-width `SessionStatus` bar uses reverse video over its theme's effective default foreground/background without introducing literal terminal colors. The composer prompt and transient `THINKING` indicator have distinct foreground roles without backgrounds. `NO_COLOR`, redirected output, and limited terminals suppress styling while preserving text and markers.

## Built-in themes

- `system` — terminal-native colors.
- `forge-dark` — restrained dark palette.
- `ocean` — blue/cyan palette.
- `high-contrast` — strong contrast with redundant bold/underline emphasis.

Use `/theme` for the numbered Up/Down/Enter selector, `/theme <id>` for direct selection, or `/theme current` to report the active theme. Selection affects only subsequent output and atomically persists `tui.defaultTheme` to `~/.threadsmith/config.json` through a syntax-preserving targeted update; unrelated settings, comments, trailing commas, and surrounding formatting remain intact. Higher-precedence repository, session, CLI, or environment configuration may still override the user default at startup. Selection does not rewrite scrollback or persist a domain event.

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

Styles accept plan-24 semantic role names, supported named colors or `#RRGGBB`, and boolean `bold`, `dim`, `italic`, `underline`, `strikethrough`, and `invert` decorations. Missing values inherit from `Default` and then `system`.

Plan 49 reuses the same semantic roles and serialized console boundary for transient request/tool/MCP activity. With `tui:showOperationDurations` omitted or `true`, elapsed text updates only when its compact invariant value changes and never more than four times per second. Disabled mode retains activity words without periodic repaint. Timer ticks are presentation-only and never become events or transcript rows. Completed transcripts contain no host-generated `THINKING` marker.

The enabled Plan 26 status surface is rendered immediately before each composer through the same serialized console boundary. It shows the working folder, repository, effective model and reasoning level, latest governed context estimate/limit/percentage, and cumulative provider tokens. `~` marks estimated values; `--` marks unknown context or wholly unavailable usage, and `+?` marks a known token subtotal followed by a provider request that omitted usage metadata. Long folders use end-biased abbreviation; narrow terminals omit folder and repository first, then truncate the model, rather than wrapping. Non-empty rows are padded by measured terminal cells to the current window width, and every compiled theme renders the complete row with its effective default foreground/background reversed. Redirected output contains no status row. Set `tui:footer:enabled` to `false` to hide it without disabling usage accounting. Usage is process/session-local and intentionally restarts at zero rather than entering restored durable session state. Startup reports the selected composer-adjacent or disabled mode and why a fixed footer is unavailable. A permanently pinned row is deferred because PrettyPrompt 6.0.4 has no public fixed-status API and cursor-managed pinning would violate native-scrollback compatibility.

Theme data is untrusted and bounded: at most 32 configured themes, ids up to 40 safe characters, names up to 80 characters, and UI values up to 40 characters. Control characters, raw ANSI/OSC data, unknown roles/settings, invalid colors, and unsupported spinners fail validation. The initial UI allow-list is `spinner` (`dots`), `selectionMarker`, and `footerSeparator`; the latter two are reserved for selector/footer consumption.
