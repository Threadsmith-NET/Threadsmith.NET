# task3: /reasoning command + transcript reasoning surfacing (Tui)

Owner: implementer. Depends on task1. Parallel with task2. Files: Threadsmith.Tui only
(+ wiring already provided by task2 in Program.cs).

## Changes

### src/Threadsmith.Tui/ConversationalShell.cs
- Constructor receives the catalog, effective startup id, and shared `SessionModelPreferences`.
  `/reasoning` resolves the current identity from shared state first so workload-driven model changes
  are reflected and are not reverted to startup state.
- Add `/reasoning` handling before the `Unknown command` fallback:
  - `string.Equals(commandText, "/reasoning", OrdinalIgnoreCase)` (no arg):
    - Resolve active profile = `activeProfileId` is null ? null : `modelCatalog.Get(activeProfileId)`.
    - Print: `Model: <name>`, `Reasoning levels: <comma list of SupportedReasoningLevels>`,
      `Current: <sessionPreferences.Reasoning>`. Use `ConsoleTone.Status`.
  - `commandText.StartsWith("/reasoning", OrdinalIgnoreCase) && (len==10 || whitespace)`:
    - `string arg = commandText[10..].Trim();`
    - Parse `arg` to `ReasoningLevel` (case-insensitive). Invalid ⇒ error: `Unknown reasoning level
      '<arg>'. Supported: <list>.`
    - Active profile's `SupportedReasoningLevels` doesn't contain the level ⇒ error:
      `Model '<name>' does not support reasoning level '<arg>'. Supported: <list>.` Keep current value.
    - Otherwise call the atomic shared-state setter for the current profile and print
      `Reasoning set to <level> for <name>.`
  - If no active profile (no model configured) ⇒ `No model is configured.` (`ConsoleTone.Error`).
- Update `/help` text to include:
  `/reasoning [level]  Set reasoning effort for the active model (none|minimal|low|medium|high)`

### src/Threadsmith.Tui/TuiShell.cs
- `ConversationTranscript.Apply`: add a case for `ModelReasoningObserved reasoning`:
  - Append reasoning text distinctly. Suggested: write a leading marker line once per run (e.g. a dim
    `// reasoning:`) then append `reasoning.Text`. Keep it separate from the `Threadsmith:` answer line.
  - Use a distinct `ConsoleTone` for reasoning if the surface supports it; otherwise append in a
    prefixed form: `_text.Append("  [reasoning] ").Append(reasoning.Text)`.
  - Defer `Threadsmith:` until the first answer delta, emit it after a reasoning line, and do not emit
    an empty answer label for reasoning-only completion.
  - Return true (transcript changed).

## Verify
- `dotnet build src\Threadsmith.sln`
- `dotnet test tests\Threadsmith.CoreRuntime.Tests` (transcript projection tests, if any, still pass)
- Manual smoke (after full integration): `/reasoning` shows model + levels; `/reasoning medium` then
  `hello` streams reasoning then content.
