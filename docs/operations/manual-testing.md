# Installed-Release Smoke Test

This is a short operator check for a packaged release. The exhaustive regression plan remains [source-repository material](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/main/docs/implementation-plans/manual-test-plan.md) and is intentionally not installed as product help.

Run `Threadsmith.App --tui` from a small repository you can safely inspect. Bare `--tui` launches the retained TUIKit frontend; use `--tui=original` only when testing the previous PrettyPrompt/Spectre frontend. Add `--repository`, `--trust`, or `--solution` when the current directory and automatic choices are unsuitable.

## Startup and terminal

1. Confirm startup identifies the effective model, repository, trust, solution, semantic confidence, and selected frontend.
2. In TUIKit, confirm `Enter` submits, `Ctrl+Enter` inserts a newline, paste arrives as one operation, `F7` switches transcript/composer focus, and `Ctrl+C` copies selected text or cancels when nothing is selected.
3. In TUIKit, confirm required startup choices precede the splash. Type/paste during initial loading and confirm it is discarded without a later submission. Successful startup phases and remembered-solution hints must remain only in the splash; warnings/failures remain visible afterward.
4. Run `/help` and verify a scrollable modal with command/description columns, no help appended to the transcript, and Esc returning to the draft. Compare its catalog with the [interactive command reference](keyboard-shortcuts.md).
5. Run `/open` against another disposable repository and confirm the footer/status identify it only after selection succeeds. TUIKit retains `Threadsmith >`; the original frontend updates its repository-named prompt. Check rounded borders, heading spacing, provider/model sorting, and visible gaps between agent tabs.

## Governed behavior

1. Start at inspection or read trust and confirm build- or mutation-requiring operations are unavailable or denied with a reason.
2. Run `/tools`, inspect one non-essential tool, and cancel without changing its state.
3. Ask a read-only repository question and confirm tool activity is bounded and the final response does not claim omitted evidence was inspected.
4. If mutation testing is appropriate, use a disposable clean repository. Confirm plan review precedes implementation, the exact staged diff is shown before authorization, and rejection leaves the repository unchanged.
5. Cancel an active request with `Esc Esc` and confirm the shell returns to a usable composer without late output being presented as current.

## Management, agents, and usage

1. Select a theme with `/theme`, then toggle several hooks, extensions, tools, and configured MCP connections in their respective dialogs. Confirm each change applies immediately, failures show actual state, and Esc retains completed changes.
2. If an authorized OAuth MCP profile is configured, use F3 → Sign in / Authenticate from `/mcp`; test Esc cancellation and updated connection status. Static-token profiles should have no sign-in action.
3. Ask for two read-only subagents with different roles. Inspect both tabs, MAIN’s live delegation status rows, independent timers, final rows below the completed tool header, and absence of duplicate outcome/GUID announcements. Return to MAIN before steering.
4. Run a native and an MCP tool when available. Confirm activity appears before completion and MCP uses its own label. Confirm the active-turn hint disappears after completion/cancellation. Edit a disposable source file externally and verify refresh output plus toasts preserve the draft.
5. Check F2 per-request cache information and provider-reported reasoning counts when available. Missing reasoning adds no placeholder; reasoning is included in output totals. Use controlled fixtures for zero and unavailable reports when the live provider cannot produce both.

## Installed documentation

1. Confirm `ThreadsmithDocs/manifest.json`, `README.md`, `LICENSE`, `docs/index.md`, `docs/user-guide.md`, and `docs/operations/skills.md` exist beside the application payload.
2. Ask a natural Threadsmith usage question and confirm the maintained documentation skill returns local path, heading, line, and snippet citations.
3. Ask a question the installed documentation does not answer and confirm the result reports a partial or unavailable answer instead of guessing.
4. Confirm the bundle contains no `docs/implementation-plans`, `docs/features`, release-readiness assessment, repository runtime state, or linked files.

## Headless parity

Submit a harmless positional request and confirm standard output is one structured result. Repeat with an invalid argument and verify the documented nonzero exit behavior without an interactive prompt.
