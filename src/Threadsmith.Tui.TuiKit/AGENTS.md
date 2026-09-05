# AGENTS.md — Threadsmith.Tui.TuiKit

## Purpose and ownership

Provide the default interactive frontend selected by bare `--tui` and explicit `--tui=tuikit`. PrettyPrompt/Spectre remains available through `--tui=original`. Reference only `Threadsmith.Interaction` and centrally pinned TUIKit; keep commands, reviews, trust, execution, parsing, and host queries in shared layers.

## Contracts

- One TuiApplication loop owns input, widgets, and terminal writes. A bounded 64-entry FIFO admits semantic updates; at most 32 drain per frame. Complete callers only after their mutation runs. Never add another console reader.
- Keep transcript, activity, four editable composer rows, and fixed bottom status separate. Below 40 x 12, preserve state and reject editing until resize recovers.
- Composer purposes own separate exact drafts/history. Normalize CRLF to LF, edit whole graphemes, cap drafts/history at one MiB, and retain bounded delta undo. Cancellation, selectors, and active-run leases must not consume an ordinary draft. Before clearing a committed ordinary submission, move its prompt and safe text into the retained transcript exactly once; never echo secondary or steering input. Before the coordinator opens its initial conversation read, accept at most one ordinary submission, show it as queued, and deliver it automatically without consuming text entered afterward.
- Project only shared safe text/Markdown. Preserve roles, validated link targets, chunk continuity, and authoritative option IDs. Filter only selector views; Escape never selects an option. F2 exposes full option text.
- Transcript retention is 1024 chunks/512 KiB with visible eviction; durable session history remains authoritative. Selection copies original text without soft-wrap newlines. F8 exposes validated retained links; no automatic link execution.
- Clipboard reads happen only on explicit paste, are bounded to one MiB/two seconds, and discard stale destinations. OSC 52 copies are explicit and limited to 64 KiB; F12 permits terminal-native selection.
- Resolve shared themes on render, including suppressed styles and a visible selector marker. Rendering never queries host state; retained status comes from the shared coordinator.
- Always stop/dispose the backend and join pending input/update/clipboard tasks on every exit and failure path. F1 opens key help. Ctrl+C copies a visible transcript/composer selection; with no selection it invokes process cancellation. Active-run double Escape remains a separate semantic action.

## Verification

Use the small `TuiKitFrontendTests` in the existing CoreRuntime suite and startup/dependency tests in Architecture. Keep new unit cases below two seconds. Run long load diagnostics manually rather than as unit tests. The manual acceptance matrix lives in `manual-test-plan.md`; do not claim physical-terminal results from a headless run.
