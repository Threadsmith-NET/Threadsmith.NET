# Interactive Terminal Commands and Keys

Bare `--tui` and `--tui=tuikit` launch the default retained TUIKit frontend. `--tui=original` launches the previous PrettyPrompt/Spectre frontend.

## Default TUIKit frontend

- `Enter`: accept a highlighted command suggestion when visible; otherwise submit the current composer text. A committed ordinary entry moves into the retained transcript before the composer clears. During a turn on MAIN, Enter requests one steering pause at the next safe model/tool boundary; repeated presses reuse the pending request. Startup input is discarded while the timed loading splash owns the screen.
- `Ctrl+Enter`: insert a newline without submitting.
- `Ctrl+V` or `Shift+Insert`: paste clipboard text into the composer as one operation.
- `Ctrl+C`: copy selected transcript or composer text. When no text is selected, exit or cancel through Threadsmith's normal cancellation path.
- `Ctrl+Shift+C`: copy the complete composer draft.
- `Esc Esc`: while a conversation run is active, cooperatively cancel it when both unmodified Escape presses occur within 850 ms.
- `Ctrl+T`: on an empty composer, toggle live streaming of future sanitized reasoning. Reasoning streaming is off by default.
- `F1`: show the non-selectable key-help list; arrows or PageUp/PageDown scroll it, and `Esc` closes it.
- `F3`: open the fuzzy command palette on a focused ordinary composer containing only an empty draft or a partial slash command. Type to search command names and descriptions, navigate with arrows/PageUp/PageDown/Home/End, and press `Enter` to insert the canonical name. `Esc` or `F3` closes without changing the draft. The palette shows usage and description; `F6` copies those details.
- `F7`: switch MAIN composer/output focus; child viewing keeps output focus.
- `Ctrl+Left/Right`: select previous/next agent from output only; composer word navigation is preserved.
- `F2`: show the selected agent’s complete status and label.
- Child views preserve MAIN’s draft and block every editing, paste, submit, steering, and command-discovery path.
- `F8`: show validated links retained in the transcript; Enter copies the selected address.
- `F12`: hand mouse selection to the terminal; press it again to restore application mouse control.
- Arrow keys scroll the focused transcript. `Shift` plus arrow keys selects text.

A leading partial slash token shows up to six autocomplete rows in catalog order. While visible, unmodified arrows/PageUp/PageDown/Home/End select a suggestion, `Tab` or `Enter` inserts it, and `Esc` dismisses only the suggestions. Completion never executes or submits a command, adds a space, or changes submission history; undo restores the previous draft in one step. Add any arguments and press Enter separately to submit. Ctrl+Enter still inserts a newline; hidden suggestions leave normal Tab indentation and editor navigation unchanged.

Discovery is absent for exact command names, arguments, prose, multiline input, selected text, secondary/steering prompts, other modals, transcript focus, and terminals below 40 x 12. Argument completion is not supported. Palette queries are limited to 256 characters and single-line paste; command discovery performs no host queries. F3 and this retained autocomplete are TUIKit-only; the original frontend is unchanged.

Selectors are centered, padded below their headings, and block background mouse input. `/tools`, `/hooks`/`/hooks list`, `/mcp`/`/mcp list`, `/extensions`, and MCP tool availability use keyboard-only checkbox trees: arrows navigate, Space applies now, typing/paste filters, F2 shows details, F6 copies selected detail text, and Esc/Enter closes while keeping acknowledged changes. Group changes affect only currently visible eligible members; essential and consent restrictions remain enforced by the host.

## Original PrettyPrompt/Spectre frontend

- `Enter`: submit the current composer text. While a conversation run is active and no composer is visible, request one steering prompt at the next safe model/tool boundary; repeated Enter presses reuse the same pending request.
- `Shift+Enter`: insert a soft newline in the multiline composer.
- `Ctrl+V` or `Shift+Insert`: paste clipboard text into the composer as one operation.
- `Ctrl+C`: cancel the current composer input or an active run. When the terminal emulator has a native selection, it copies that selection instead.
- `Esc Esc`: while a conversation run is active, cooperatively cancel it when both unmodified Escape presses occur within 850 ms.
- `Ctrl+Shift+C`: copy a PrettyPrompt editor selection where supported.
- `Ctrl+T`: on an empty composer, toggle live streaming of future sanitized reasoning. Reasoning streaming is off by default.
- Mouse drag: create a native terminal selection across composer text or any transcript output.
- Terminal keyboard mark mode: select transcript or composer output without the mouse; the activation key is terminal-specific.

## Shared interactive commands

- `/agents [<id> [cancel|cancel-child <id>]]`: list, inspect, or cancel delegations; tabs provide live child views.
- `/hooks [list|inspect|enable|disable|test|approve|revoke|audit]`: open the hook checkbox modal or manage a handler.
- `/extensions`: open the loaded-extension checkbox modal; Space loads/unloads and leaves the modal open.
- `/tools`: open the tool-availability checkbox modal; host-locked entries cannot be toggled.

- `/help`: display available interactive commands. In TUIKit, opens a scrollable modal with wrapped command and description columns; Up/Down, PgUp/PgDn, Home, and End scroll, and Esc closes it. Help text is not appended to the output pane.
- `/mcp [list|inspect|connect|disconnect|reconnect|capabilities|capability|enable|disable|resource|prompt|auth|logout|revoke|switch-account|diagnose]`: inspect or manage configured MCP profiles through the shared host authority. With no action, open the connection checkbox modal in TUIKit. Direct subcommands with missing IDs open selection dialogs; identity mutations require an exact confirmation. See [MCP connections](mcp-connections.md).
- `/open [path]`: open or switch repositories, choose trust, and select a solution when multiple candidates exist.
- `/quit`: exit cleanly.
- `/reasoning [none|minimal|low|medium|high]`: set reasoning effort when the active model supports the requested level.
- `/semantic_refresh`: force and await one complete semantic refresh without creating a model run. Requires a bound repository and solution.
- `/theme`: choose one built-in or configured theme with the filtered Up/Down/Enter modal; Esc cancels. The original frontend uses a numbered selector.
- `/theme <id>`: switch directly and save the user-level default; `/theme current` reports the effective theme.
- `/thinking [on|off]`: enable, disable, or toggle live streaming of future sanitized reasoning using the `Reasoning` semantic style, equivalent to `Ctrl+T` on an empty composer when no argument is supplied. Previously displayed reasoning remains in the retained transcript or the original frontend’s native scrollback.
- `/trust [inspect|read|build|mutation|automation]`: show the trust selector or set/upgrade the active repository trust directly. Persisted higher trust is not downgraded.

Repository trust and multi-solution choices use selection dialogs; TUIKit themes use a filtered single-selection modal. The original frontend provides numbered choices with Up/Down plus Enter. Plan and mutation approvals appear as numbered, fail-closed review prompts. Invalid choices do not authorize an action. State-changing choices submit application commands; PrettyPrompt and Spectre.Console remain terminal adapters and do not call execution services directly.

The default TUIKit prompt is `Threadsmith >`; its fixed footer identifies the repository. Agent tabs have a one-cell gap using `AgentTabHeaderRole`. The original frontend uses the repository directory name, such as `main >`, and updates it after `/open`. Names are bounded for terminal stability. TUIKit keeps activity and session status in fixed rows. During a turn, MAIN shows `ENTER to steer; ESC-ESC to cancel` after its activity timer; this transient hint disappears at completion or pause. In the original frontend, a responsive composer-adjacent status row appears immediately before the prompt; it remains ordinary native scrollback and is refreshed at the next prompt boundary rather than pinned with cursor controls.

In the MCP connection checkbox modal, F3 opens actions for the selected OAuth-enabled profile. **Sign in / Authenticate** starts the existing browser flow; Esc cancels an active sign-in attempt and returns to the list. Space still controls connection checkboxes and group toggles. F2 shows details.
