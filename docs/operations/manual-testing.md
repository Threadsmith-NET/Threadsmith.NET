# Installed-Release Smoke Test

This is a short operator check for a packaged release. The exhaustive regression plan remains [source-repository material](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/main/docs/implementation-plans/manual-test-plan.md) and is intentionally not installed as product help.

Run `Threadsmith.App --tui` from a small repository you can safely inspect. Bare `--tui` launches the retained TUIKit frontend; use `--tui=original` only when testing the previous PrettyPrompt/Spectre frontend. Add `--repository`, `--trust`, or `--solution` when the current directory and automatic choices are unsuitable.

## Startup and terminal

1. Confirm startup identifies the effective model, repository, trust, solution, semantic confidence, and selected frontend.
2. In TUIKit, confirm `Enter` submits, `Ctrl+Enter` inserts a newline, paste arrives as one operation, `F7` switches transcript/composer focus, and `Ctrl+C` copies selected text or cancels when nothing is selected.
3. Submit one message during initial semantic loading. Confirm it is retained as queued and sends automatically when repository semantics become available.
4. Run `/help` and compare the displayed catalog with the [interactive command reference](keyboard-shortcuts.md).
5. Run `/open` against another disposable repository and confirm the composer label and fixed status rows change only after selection succeeds.

## Governed behavior

1. Start at inspection or read trust and confirm build- or mutation-requiring operations are unavailable or denied with a reason.
2. Run `/tools`, inspect one non-essential tool, and cancel without changing its state.
3. Ask a read-only repository question and confirm tool activity is bounded and the final response does not claim omitted evidence was inspected.
4. If mutation testing is appropriate, use a disposable clean repository. Confirm plan review precedes implementation, the exact staged diff is shown before authorization, and rejection leaves the repository unchanged.
5. Cancel an active request with `Esc Esc` and confirm the shell returns to a usable composer without late output being presented as current.

## Installed documentation

1. Confirm `ThreadsmithDocs/manifest.json`, `README.md`, `LICENSE`, `docs/index.md`, `docs/user-guide.md`, and `docs/operations/skills.md` exist beside the application payload.
2. Ask a natural Threadsmith usage question and confirm the maintained documentation skill returns local path, heading, line, and snippet citations.
3. Ask a question the installed documentation does not answer and confirm the result reports a partial or unavailable answer instead of guessing.
4. Confirm the bundle contains no `docs/implementation-plans`, `docs/features`, release-readiness assessment, repository runtime state, or linked files.

## Headless parity

Submit a harmless positional request and confirm standard output is one structured result. Repeat with an invalid argument and verify the documented nonzero exit behavior without an interactive prompt.
