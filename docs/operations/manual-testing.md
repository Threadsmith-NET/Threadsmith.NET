# Current Manual Test Surface

The authoritative, maintained regression procedure is the [Threadsmith.NET manual test plan](../implementation-plans/manual-test-plan.md). Keep this concise overview aligned with that plan.

Launch from the repository you want to inspect:

```powershell
dotnet run --project C:\source\repos\Threadsmith\src\Threadsmith.App -- --tui
```

The current directory is the default repository. Use `--repository`, `--trust`, and `--solution` to override startup values. When trust or a solution remains ambiguous, a numbered highlighted selector accepts Up/Down and Enter.

## Expected to work

- Native transcript: submitted inputs, streamed responses, repository results, plans, and diffs remain ordinary terminal scrollback. Mouse selection or terminal keyboard mark mode plus `Ctrl+C` copies prior text.
- Startup: an ASCII Threadsmith.NET wordmark and `Forge better code, not slop.` followed by a blank line precede a status block containing the effective model, repository, trust, solution, session-status mode, target frameworks when known, semantic confidence, and interactive mode. Pending initial semantic discovery displays `Loading...` and later appends its resolved confidence.
- Composer: the prompt is `<current-repository-name> >` and changes after `/open`. `Enter` submits, `Shift+Enter` inserts a newline, `Ctrl+V` performs bulk paste, and `Ctrl+C` cancels input or active work when the terminal has no native selection.
- Repository startup: the current directory opens automatically. A single solution selects automatically; multiple solutions require explicit selection.
- Startup cancellation: Cancel in a trust, upgrade, or solution selector exits before the composer; cancelling an in-session `/open` remains non-terminal.
- Host commands: `/open [path]`, `/trust [inspect|read|build|mutation]`, `/help`, and `/quit`. Unknown slash commands fail locally and never reach the model.
- Trust: Trusted Read permits content inventory without repository execution. Trusted Build warns that repository code may execute. Trusted Mutation additionally permits explicitly approved confined changes. Persisted higher trust is not downgraded.
- Review: structured plan and mutation decisions use sequential fail-closed prompts backed by application commands.
- Headless parity: positional text submits a request. With no positional request, repository discovery uses `--repository` or the current directory.

## Present but not interactive yet

- Dedicated build, diagnostic, test, extension, model-selection, reload, and session-resume commands arrive in later milestones.
- Direct mutation authoring is not yet a public terminal command; exact M5 previews and approval/discard prompts render when the engine stages a proposal.
- Read-only semantic and filesystem tools are visible when invoked through a configured model; there is no direct tool-browser command yet.
