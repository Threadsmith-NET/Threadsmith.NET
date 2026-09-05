# ADR-52: Selectable TUIKit Frontend

- **Status:** Accepted
- **Date:** 2026-09-04; amended 2026-09-05
- **Deciders:** Threadsmith.NET maintainers

## Context

The current PrettyPrompt/Spectre frontend provides native scrollback. A retained alternative needs a fixed footer and independent transcript/composer regions without duplicating interactive policy. The initial TUIKit stock editor failed input parity checks; the isolated recovery verified a Threadsmith-owned grapheme composer, bounded retained projection, public input ownership, and teardown.

## Decision

Add `Threadsmith.Tui.TuiKit` with exact TUIKit 0.10.1. Bare `--tui` and explicit `--tui=tuikit` select TUIKit; `--tui=original` selects the previous PrettyPrompt/Spectre frontend. App composes both over the same InteractionCoordinator and host dependencies. MCP and authentication preserve precedence.

Interaction additionally owns terminal-free theme values/catalog/preferences and the presentation-only theme command. Configuration binding remains outside it. Retained status refresh is a neutral opt-in capability; shared code assembles snapshots and invalidates old repository/session lifetimes. No terminal/backend types enter shared contracts.

The new adapter owns one input/render loop, a bounded update queue, three composer-purpose drafts, stable-ID selectors, safe semantic rendering, theme conversion, clipboard mechanics, and unconditional terminal cleanup. The stock TextEditor is not used. Retained output is bounded and does not replace durable history. Existing Markdown layout semantics are preserved using TUIKit display-cell measurement.

Package evidence preserves MIT plus embedded font headers/attribution and permissive WTFPL v2. No upstream posting or permission request is part of implementation.

## Consequences

Users choose retained full-screen interaction by requesting `--tui`, or native scrollback through `--tui=original`. Key mapping and placement differ where documented; command/review/trust/execution authority does not. F2 exposes complete selector labels; F8 exposes validated link targets for explicit copying. Physical terminal, OS clipboard, and cross-platform visual acceptance must be recorded separately from automated backend checks. ADR-15 continues to govern the original PrettyPrompt frontend; this ADR refines ADR-51 only for shared terminal-neutral themes and opt-in status refresh.
