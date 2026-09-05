# Interactive Terminal Commands and Keys

- `Enter`: submit the current composer text.
- `Shift+Enter`: insert a soft newline in the multiline composer.
- `Ctrl+V` or `Shift+Insert`: paste clipboard text into the composer as one operation.
- `Ctrl+C`: cancel the current composer input or an active run. When the terminal emulator has a native selection, it copies that selection instead.
- `Ctrl+Shift+C`: copy a PrettyPrompt editor selection where supported.
- `Ctrl+T`: on an empty composer, show or hide the latest sanitized reasoning view. Reasoning remains collapsed by default.
- Mouse drag: create a native terminal selection across composer text or any transcript output.
- Terminal keyboard mark mode: select transcript or composer output without the mouse; the activation key is terminal-specific.
- `/open [path]`: open or switch repositories, choose trust, and select a solution when multiple candidates exist.
- `/trust [inspect|read|build|mutation]`: show the trust selector or set/upgrade the active repository trust directly. Persisted higher trust is not downgraded.
- `/new`, `/resume [id]`, and `/clone`: start, restore, or copy a durable repository session.
- `/models`: select and persist the repository model.
- `/auth openai-codex [login|status|logout]`: manage OpenAI Codex authentication.
- `/reasoning [none|minimal|low|medium|high]`: set reasoning effort when the active model supports the requested level.
- `/thinking`: show or hide the latest reasoning view, equivalent to `Ctrl+T` on an empty composer.
- `/extensions`: browse, load, and unload extensions.
- `/tools`: browse and toggle non-essential repository tools.
- `/fetch-authorize <url> [redirect ...]`: authorize one exact URL chain for `web_fetch`.
- `/mcp [list|inspect|connect|disconnect|reconnect|capabilities|capability|enable|disable|resource|prompt|auth|logout|revoke|switch-account|diagnose]`: inspect or manage configured MCP profiles through the shared host authority. Omit exact IDs to use numbered selectors; identity mutations require an exact confirmation. See [MCP connections](mcp-connections.md).
- `/hooks [list|inspect|enable|disable|test|approve|revoke|audit]`: govern lifecycle hooks.
- `/context [mode|inspect|compact]`: inspect or control bounded cross-turn context.
- `/validation retry`: resume interrupted post-apply validation.
- `/agents <id> [cancel|cancel-child <id>]`: inspect or cancel a delegation tree.
- `/skills [list|inspect|verify|enable|disable|pin|use|status|cancel]`: govern declarative skills.
- `/plan-policy [name|current]`: select or report plan approval policy.
- `/policy [name|current]`: select or report exact-diff mutation approval policy.
- `/theme`: choose a built-in or configured theme with the numbered Up/Down/Enter selector.
- `/theme <id>`: switch directly and save the user-level default; `/theme current` reports the effective theme.
- `/help`: display available interactive commands.
- `/quit`: exit cleanly.

Repository trust, multi-solution, and theme choices show numbered labels and support Up/Down plus Enter. Plan and mutation approvals appear as numbered, fail-closed review prompts. Invalid choices do not authorize an action. State-changing choices submit application commands; PrettyPrompt and Spectre.Console remain terminal adapters and do not call execution services directly.

The composer prompt displays the current repository directory name, such as `main >`, and updates after a successful `/open`. Names are bounded for terminal stability. When enabled, a responsive composer-adjacent status row appears immediately before the prompt; it remains ordinary native scrollback and is refreshed at the next prompt boundary rather than pinned with cursor controls.
