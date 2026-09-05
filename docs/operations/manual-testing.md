# Installed-Release Smoke Test

This is a short operator check for a packaged release. The exhaustive regression plan remains [source-repository material](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/main/docs/implementation-plans/manual-test-plan.md) and is intentionally not installed as product help.

Run `Threadsmith.App --tui` from a small repository you can safely inspect. Use `--repository`, `--trust`, and `--solution` when the current directory or automatic solution choice is unsuitable.

## Startup and terminal

1. Confirm startup identifies the effective model, repository, trust, solution, semantic confidence, and terminal mode.
2. Confirm `Enter` submits, `Shift+Enter` inserts a newline, paste arrives as one operation, and `Ctrl+C` cancels input or active work when the terminal has no native selection.
3. Run `/help` and compare the displayed catalog with the [interactive command reference](keyboard-shortcuts.md).
4. Run `/open` against another disposable repository and confirm the composer label and status change only after selection succeeds.
5. Confirm ordinary transcript output remains selectable through native terminal scrollback.

## Governed behavior

1. Start at inspection or read trust and confirm build- or mutation-requiring operations are unavailable or denied with a reason.
2. Run `/tools`, inspect one non-essential tool, and cancel without changing its state.
3. Ask a read-only repository question and confirm any tool activity is bounded and the final response does not claim omitted evidence was inspected.
4. If mutation testing is appropriate, use a disposable clean repository. Confirm plan review precedes implementation, the exact staged diff is shown before authorization, and rejection leaves the repository unchanged.
5. Cancel an active request and confirm the shell returns to a usable prompt without late output being presented as current.

## Installed documentation

1. Confirm `ThreadsmithDocs/manifest.json`, `README.md`, `LICENSE`, `docs/user-guide.md`, and `docs/operations/skills.md` exist beside the application payload.
2. Ask a natural Threadsmith usage question and confirm the maintained documentation skill returns local path, heading, line, and snippet citations.
3. Ask a question the installed documentation does not answer and confirm the result reports a partial or unavailable answer instead of guessing.
4. Confirm the documentation bundle contains no `docs/implementation-plans`, `docs/features`, repository runtime state, or linked files.

## Headless parity

Submit a harmless positional request and confirm standard output is one structured result. Repeat with an invalid argument and verify the documented nonzero exit behavior without an interactive prompt.
