# AGENTS.md — Threadsmith.Tui.TuiKit

## Purpose and ownership

Provide the default interactive frontend selected by bare `--tui` and explicit `--tui=tuikit`. PrettyPrompt/Spectre remains available through `--tui=original`. Reference only `Threadsmith.Interaction` and centrally pinned TUIKit; keep commands, reviews, trust, execution, parsing, and host queries in shared layers.

## Contracts

- One TuiApplication loop owns input, widgets, and terminal writes. A bounded 64-entry FIFO admits semantic updates; at most 32 drain per frame. Complete callers only after their mutation runs. Never add another console reader.
- Keep transcript, activity, four editable composer rows, and fixed bottom status separate. Below 40 x 12, preserve state and reject editing until resize recovers.
- Advertise retained activity independently of retained session status. The activity row remains visible through streamed reasoning and buffered answers, without restarting its animation or elapsed time for each transcript batch; completion and review/input boundaries clear it through the shared coordinator.
- Composer purposes own separate exact drafts/history. Normalize CRLF to LF, edit whole graphemes, cap drafts/history at one MiB, and retain bounded delta undo. Cancellation, selectors, and active-run leases must not consume an ordinary draft. Before clearing a committed ordinary submission, move its prompt and safe text into the retained transcript exactly once; never echo secondary or steering input. Before the coordinator opens its initial conversation read, accept at most one ordinary submission, show it as queued, and deliver it automatically without consuming text entered afterward.
- Project only shared safe text/Markdown. Preserve roles, validated link targets, chunk continuity, and authoritative option IDs. Filter only selector views; Escape never selects an option. Every selector clears the complete application frame above the persistent status row so underlying characters cannot leak around its edges. F2 exposes full option text.
- Detached transcript output must lead the activity row with its unseen count and focus-aware follow keys, including after the ready composer reopens; generic shortcut hints must not clip that notice at supported widths. Preserve the detached reading position until explicit navigation.
- Transcript retention is 1024 chunks/512 KiB with visible eviction; durable session history remains authoritative. Selection copies original text without soft-wrap newlines. F8 exposes validated retained links; no automatic link execution.
- Clipboard reads happen only on explicit paste, are bounded to one MiB/two seconds, and discard stale destinations. OSC 52 copies are explicit and limited to 64 KiB; F12 permits terminal-native selection.
- Resolve shared themes on render, including suppressed styles and a visible selector marker. Rendering never queries host state; retained status comes from the shared coordinator.
- Command discovery metadata comes only from the shared catalog through `TuiKitCommandDiscovery`. `ComposerCommandCompletion` owns reversible command-name edits and rejects stale draft/input-lifetime targets. Discovery may request a composer edit but never dispatch a host command or submit input. Keep matching and completion logic out of surface orchestration.
- `CommandPaletteModal` uses the toolkit fuzzy list with bounded query/label rendering; `ComposerAutocomplete` owns prefix-list selection and dismissal across frames. Only unmodified suggestion keys take precedence over the composer. Use `ModalFrame` for minimum size and modal clearing, resolve caret geometry from the rendered composer/layout, and keep inline suggestions off activity/status rows. F3 is ordinary-draft discovery only; arguments and original-frontend parity are out of scope.
- Always stop/dispose the backend and join pending input/update/clipboard tasks on every exit and failure path. F1 opens key help. Ctrl+C copies a visible transcript/composer selection; with no selection it invokes process cancellation. Active-run double Escape remains a separate semantic action.

## Verification

Use the small `TuiKitFrontendTests` in the existing CoreRuntime suite and startup/dependency tests in Architecture. Keep new unit cases below two seconds. Run long load diagnostics manually rather than as unit tests. The manual acceptance matrix lives in `manual-test-plan.md`; do not claim physical-terminal results from a headless run.
