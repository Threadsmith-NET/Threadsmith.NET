# Interactive Terminal Commands and Keys

- `Enter`: submit the current composer text. While a conversation run is active and no composer is visible, request one steering prompt at the next safe model/tool boundary; repeated Enter presses reuse the same pending request.
- `Shift+Enter`: insert a soft newline in the multiline composer.
- `Ctrl+V` or `Shift+Insert`: paste clipboard text into the composer as one operation.
- `Ctrl+C`: cancel the current composer input or an active run. When the terminal emulator has a native selection, it copies that selection instead.
- `Esc Esc`: while a conversation run is active, cooperatively cancel it when both unmodified Escape presses occur within 850 ms.
- `Ctrl+Shift+C`: copy a PrettyPrompt editor selection where supported.
- `Ctrl+T`: on an empty composer, toggle live streaming of future sanitized reasoning. Reasoning streaming is off by default.
- Mouse drag: create a native terminal selection across composer text or any transcript output.
- Terminal keyboard mark mode: select transcript or composer output without the mouse; the activation key is terminal-specific.
- `/help`: display available interactive commands.
- `/mcp [list|inspect|connect|disconnect|reconnect|capabilities|capability|enable|disable|resource|prompt|auth|logout|revoke|switch-account|diagnose]`: inspect or manage configured MCP profiles through the shared host authority. Omit exact IDs to use numbered selectors; identity mutations require an exact confirmation. See [MCP connections](mcp-connections.md).
- `/open [path]`: open or switch repositories, choose trust, and select a solution when multiple candidates exist.
- `/quit`: exit cleanly.
- `/reasoning [none|minimal|low|medium|high]`: set reasoning effort when the active model supports the requested level.
- `/semantic_refresh`: force and await one complete semantic refresh without creating a model run. Requires a bound repository and solution.
- `/theme`: choose a built-in or configured theme with the numbered Up/Down/Enter selector.
- `/theme <id>`: switch directly and save the user-level default; `/theme current` reports the effective theme.
- `/thinking [on|off]`: enable, disable, or toggle live streaming of future sanitized reasoning using the `Reasoning` semantic style, equivalent to `Ctrl+T` on an empty composer when no argument is supplied. Already printed reasoning remains in native scrollback.
- `/trust [inspect|read|build|mutation]`: show the trust selector or set/upgrade the active repository trust directly. Persisted higher trust is not downgraded.

Repository trust, multi-solution, and theme choices show numbered labels and support Up/Down plus Enter. Plan and mutation approvals appear as numbered, fail-closed review prompts. Invalid choices do not authorize an action. State-changing choices submit application commands; PrettyPrompt and Spectre.Console remain terminal adapters and do not call execution services directly.

The composer prompt displays the current repository directory name, such as `main >`, and updates after a successful `/open`. Names are bounded for terminal stability. When enabled, a responsive composer-adjacent status row appears immediately before the prompt; it remains ordinary native scrollback and is refreshed at the next prompt boundary rather than pinned with cursor controls.
