# Threadsmith.NET User Guide

Threadsmith.NET is a terminal-first, .NET-native coding harness. It combines conversational assistance with host-enforced repository boundaries, trust levels, tool policy, review, transactional mutation, and validation.

This guide documents the currently implemented user-facing behavior. Features described as planned in implementation documents are not treated as available here until they ship.

## Contents

1. [Requirements and installation](#requirements-and-installation)
2. [Starting Threadsmith](#starting-threadsmith)
   - [Customizing deployed prompts](#customizing-deployed-prompts)
3. [Opening and initializing a repository](#opening-and-initializing-a-repository)
   - [Keeping the semantic workspace current](#keeping-the-semantic-workspace-current)
4. [Trust levels](#trust-levels)
5. [Using the interactive terminal](#using-the-interactive-terminal)
   - [Active-turn tool continuation compaction](#active-turn-tool-continuation-compaction)
6. [How repository changes are governed](#how-repository-changes-are-governed)
7. [Tools and tool availability](#tools-and-tool-availability)
8. [Model providers, secrets, and reasoning](#model-providers-secrets-and-reasoning)
9. [Repository configuration](#repository-configuration)
10. [Themes and session status](#themes-and-session-status)
11. [Extensions](#extensions)
12. [Governed skills and reusable workflows](#governed-skills-and-reusable-workflows)
13. [Headless and automated use](#headless-and-automated-use)
14. [Persistence, retention, and diagnostics](#persistence-retention-and-diagnostics)
15. [MCP connection profiles](#mcp-connection-profiles)
16. [Safety model](#safety-model)
17. [Troubleshooting](#troubleshooting)
18. [Further reference](#further-reference)

## Requirements and installation

Threadsmith can run from source or from a self-contained release artifact. Source builds require:

- .NET 10 SDK;
- PowerShell for the commands shown in this guide;
- a terminal with ordinary native scrollback and clipboard support for the best interactive experience;
- a model endpoint only when using a configured non-fake model profile.

Clone the repository, then restore and build it:

```powershell
git clone <repository-url> Threadsmith
Set-Location Threadsmith
dotnet restore src\Threadsmith.sln
dotnet build src\Threadsmith.sln
```

To validate the complete repository:

```powershell
dotnet test --solution src\Threadsmith.sln
```

Threadsmith uses centrally pinned dependencies and targets .NET 10. If the SDK cannot select the target framework, confirm `dotnet --info` reports an installed .NET 10 SDK.

Self-contained Windows x64/ARM64 setup executables, Linux x64/ARM64 archives, and macOS Intel/Apple-silicon packages include the matching .NET runtime. Verify the release checksum and select the artifact matching the operating system and architecture. Linux archive installation refuses symbolic-link prefixes, unrelated existing launchers, and non-empty directories that lack an exact matching `.threadsmith-install-root` ownership marker; a first install may target only an absent or empty directory. See [release packaging and publication](operations/release-packaging.md) for platform commands and verification details.

## Starting Threadsmith

### Interactive terminal

From the repository you want Threadsmith to open:

```powershell
dotnet run --project C:\source\repos\Threadsmith\src\Threadsmith.App -- --tui
```

When running from the Threadsmith source tree itself, the shorter form is:

```powershell
dotnet run --project src\Threadsmith.App -- --tui
```

The first `--` ends `dotnet run` options. Arguments after it are passed to Threadsmith.

For a lower-overhead interactive launch:

```powershell
dotnet run --configuration Release --project src\Threadsmith.App -- --tui
```

### Headless request

Headless mode is the default:

```powershell
dotnet run --project src\Threadsmith.App -- "inspect this repository"
```

A headless request can also open a specific repository and solution before submitting the model turn:

```powershell
dotnet run --project src\Threadsmith.App -- --repository C:\source\my-repo --trust TrustedBuild --solution src\MyRepo.sln "explain the request pipeline"
```

When repository options and a request are supplied together, Threadsmith opens the repository, selects the solution, records the baseline, waits briefly for `PartialCompilation` semantic readiness, and fails closed without submitting the request if semantic tools would be unusable. With no request, Threadsmith performs repository discovery without granting file-read trust:

```powershell
dotnet run --project src\Threadsmith.App
```

### Compiled application

Build once and run the application DLL directly:

```powershell
dotnet build src\Threadsmith.sln
dotnet src\Threadsmith.App\bin\Debug\net10.0\Threadsmith.App.dll --tui
dotnet src\Threadsmith.App\bin\Debug\net10.0\Threadsmith.App.dll "inspect this repository"
```

### Customizing deployed prompts

Every built or installed application includes a flat `prompts/` directory beside the executable. It contains the Threadsmith-authored system, phase, correction, tool, delegation, skill, provider, and model-visible result prose used by that build. Threadsmith loads the complete declared catalog once at startup; edits affect only a newly started process, and a missing, corrupt, oversized, linked, colliding, or incomplete catalog fails startup before model or tool activity.

Prompt files control wording only. They cannot add or enable tools, change schemas, approve mutations, widen repository or delegated-child authority, alter trust, select models, grant network or secret access, or bypass validation. Prompt content can be sent to the selected provider and is present in explicitly enabled privileged raw-model logs, so never add secrets. Installed upgrades replace the shipped defaults instead of merging local edits; back up experiments before upgrading. See the [prompt file reference](prompt-file-reference.md) for the categorized file-by-file catalog and complete placeholder glossary, and [deployed prompt assets](operations/prompts.md) for source ownership, deployment paths, capacity, logging, and upgrade behavior.

### Startup arguments

Common arguments include:

| Argument | Purpose |
|---|---|
| `--tui` | Start the default retained TUIKit terminal. |
| `--tui=tuikit` | Explicitly start the retained TUIKit terminal. |
| `--tui=original` | Start the original PrettyPrompt/Spectre terminal. |
| `--repository <path>` | Open a repository other than the current directory. |
| `--solution <path>` | Select a solution or supported project explicitly. |
| `--trust <level>` | Request a trust level without an interactive selector. |
| `--set:<key>=<value>` | Apply a one-process configuration override. Do not use this for real secrets. |

Example:

```powershell
dotnet run --project src\Threadsmith.App -- --tui --repository C:\source\my-repo --trust TrustedRead --solution src\MyRepo.sln
```

## Opening and initializing a repository

Threadsmith uses the current directory as the repository unless `--repository` is supplied. In the interactive terminal, `/open [path]` switches repositories.

Repository discovery:

- recursively detects supported solution and project candidates;
- skips `.git`, `bin`, `obj`, inaccessible entries, and reparse-point subtrees;
- automatically selects a single candidate;
- presents an interactive numbered selector when multiple candidates exist;
- returns an ambiguity result in headless mode rather than choosing arbitrarily.

### Remembered solution

After a successful selection, Threadsmith stores a slash-normalized repository-relative path at `solution:path` in `.threadsmith/config.json`. On a later launch:

1. an explicit `--solution` value wins and refreshes the preference;
2. a valid remembered solution loads automatically with a notification;
3. otherwise normal discovery and selection run.

If the remembered file no longer exists, Threadsmith clears the stale preference on a best-effort basis and resumes discovery. Invalid paths that escape the repository or traverse prohibited/reparse locations fail closed instead of being treated as merely stale.

The JSON representation is nested:

```json
{
  "solution": {
    "path": "src/MyRepo.sln"
  }
}
```

### Initializing an empty repository

When interactive startup finds neither `.threadsmith/` nor a supported solution/project candidate, it offers **Initialize** or **Continue without initialization**.

Initialization creates a minimal UTF-8 `.threadsmith/config.json` containing neutral defaults for solution memory and tool configuration. It:

- never overwrites existing configuration;
- uses atomic same-directory publication;
- rejects linked repository roots or configuration directories;
- does not grant trust or enable privileged tools;
- makes no source-code changes.

Declining leaves the repository unchanged.

### Keeping the semantic workspace current

After a solution or supported project is loaded, Threadsmith monitors relevant files beneath the active repository. A short bounded settling interval coalesces editor save bursts before one workspace-scoped refresh begins. An edit to an existing loaded C# document is applied incrementally when its settled path still exists and its identity and project membership remain stable, including editors that save by atomically replacing the same file. Actual document membership changes, project/solution files, build props and targets, analyzer configuration, uncertain changes, and watcher recovery use a complete semantic reload.

An externally attributed cycle prints `External changes detected; updating semantic model...` once, followed by one completion or actionable failure. TUIKit also echoes refresh start, completion, and failure as transient toasts without taking focus or changing queued-input handling. Watcher recovery instead starts with `External changes require semantic recovery; updating semantic model...` and uses the same single terminal projection. The refresh uses the same serialized console boundary as the composer, so background output does not submit, clear, or discard a draft. Compiler diagnostics may reduce the resulting semantic confidence without making refresh infrastructure fail.

The semantic update itself runs without waiting for Threadsmith to regain focus or for composer input. When the composer is empty, refresh lifecycle output automatically closes and reopens that empty prompt, so the update appears without a keypress or other console interaction. Once any draft text exists, including whitespace, lifecycle output waits until the draft is submitted, cancelled, or cleared back to empty; the refresh never submits, clears, or discards the draft. Focusing the window alone does not trigger or release semantic work. A submitted model request still waits for the already-running refresh before a run is created.

A model request submitted while relevant changes are settling or refreshing waits for the same single-flight refresh before a run identity, budget, conversation entry, model call, or tool call is created. A refresh failure leaves the workspace dirty and rejects new model requests until a later change, retry, or manual refresh establishes current state. Non-model commands such as `/help`, `/quit`, and `/semantic_refresh` remain local.

Run `/semantic_refresh` to force and await one complete refresh even when the workspace appears clean. It reports the changed-file count, duration, and resulting confidence, creates no model run, and returns a clear error if no repository and solution are bound. Cancelling the waiter does not corrupt or cancel refresh work already shared with another trigger.

See [Semantic refresh](operations/semantic-refresh.md) for the full command behavior and the exact external-edit trigger and ignore rules.

## Trust levels

Trust controls what the host may inspect or execute. It is separate from model capability, tool availability, and per-invocation approval.

| Level | User-facing effect |
|---|---|
| `UntrustedInspection` | Loads safe repository configuration and lists candidates without reading repository file content. |
| `TrustedRead` | Reads files, selects solutions/projects, inventories target frameworks, and creates text baselines. It does not execute repository build logic. |
| `TrustedBuild` | Permits restore, MSBuild evaluation, analyzers, source generators, compilation, tests, and approved build/process tools. Repository-controlled code may execute. |
| `TrustedMutation` | Adds explicitly approved repository file mutations under configured roots. |
| `FullyTrustedAutomation` | Enables the highest-trust automation capabilities, including explicitly enabled C# scripting, while hard host guardrails remain active. |

Use `/trust` for the selector or `/trust inspect`, `/trust read`, `/trust build`, `/trust mutation`, or `/trust automation` in the interactive terminal. `/trust FullyTrustedAutomation` is also accepted. Automation is the highest repository trust; it does not enable optional tools or bypass invocation policy.

Persisted higher trust can be reused from the per-user repository-facts database. Trust is monotonic during repository use: requesting a lower level does not silently erase a persisted higher grant.

Grant `TrustedBuild` or above only to repositories whose build scripts, analyzers, generators, and tests you trust.

## Using the interactive terminal

Startup displays the Threadsmith identity, repository and solution state, effective model, trust, target frameworks, semantic confidence, and terminal mode. The default TUIKit composer uses `Threadsmith >`; the repository appears in the fixed footer. The original frontend labels its composer with the current repository directory name.

Ordinary prompts are conversational. A greeting or question can complete as a normal assistant response. A repository-change request remains in the same model turn, but the model must call the host-owned `propose_plan` tool before governed planning begins.

### Commands

| Command | Purpose |
|---|---|
| `/agents [<id> [cancel\|cancel-child <id>]]` | List, inspect, or cancel delegation trees; use tabs to view active agents. |
| `/auth openai-codex [login\|status\|logout]` | Manage Codex authentication. |
| `/clone` | Create and activate an independent governed copy of the current session. |
| `/code_explore_inspect {on\|off}` | Show or hide future `code_explore` output in interactive tool blocks. |
| `/code_explore_output {structured\|markdown}` | Select the session's diagnostic `code_explore` output format. |
| `/context compact` | Retired automatic fact-promotion command; model-generated active-turn compaction remains automatic. |
| `/context inspect` | Inspect the latest run's included, omitted, retrieved, stale, and reduced context. |
| `/context mode` | Report the effective cross-turn conversation mode. |
| `/context mode <conversation-aware\|governed-memory\|stateless>` | Change mode for the next request. |
| `/extensions` | Open the loaded-extension checkbox dialog. |
| `/fetch-authorize <url> [redirect ...]` | Authorize an exact URL chain for `web_fetch`. |
| `/help` | Open a scrollable command/description modal in TUIKit; print command help in the original frontend. |
| `/hooks [list\|inspect\|enable\|disable\|test\|approve\|revoke\|audit]` | Open the hook checkbox dialog or manage a specific handler. |
| `/mcp [action] [profile]` | Open the connection checkbox dialog, authenticate eligible profiles, or manage capabilities and identity. See [MCP commands](operations/mcp-connections.md#lifecycle-commands). |
| `/memory remember [--type standingPreference\|situational] <text>` | Explicitly save a repository note; the default type is situational. |
| `/memory list` | List current note IDs, types, origins, and text. |
| `/memory inspect <id>` | Inspect one note with timestamps, usage, and embedding status; no vector components. |
| `/memory update <id> [--type standingPreference\|situational] <text>` | Correct the same stable ID; omitted type preserves it. `supersede` is a compatibility alias. |
| `/memory forget <id>` | Delete current memory content, search state, and usage. |
| `/memory validate` | Retired; returns migration guidance, as do old category/validity arguments. |
| `/models [status\|refresh <provider-id>]` | Select the active repository model, inspect discovery, or refresh metadata for the next startup. |
| `/new` | Checkpoint the current session and activate a fresh empty session. |
| `/open [path]` | Open or switch repositories. |
| `/plan-policy [name\|current\|reset\|revoke]` | Select, report, or revoke the plan approval policy. |
| `/policy [name\|current]` | Select or report the mutation approval policy for exact staged diffs. |
| `/quit` | Exit cleanly. |
| `/reasoning [level]` | Show or set the reasoning level supported by the active model. |
| `/resume [session-id]` | Resume an exact durable session or use the repository selector. |
| `/semantic_refresh` | Force and await a complete semantic refresh without creating a model run. |
| `/skills [action]` | Inspect and manage governed skills; see [skill operations](operations/skills.md). |
| `/theme` | Select one theme in the filtered modal. |
| `/theme <id>` | Apply a theme and save it as the user-level default. |
| `/theme current` | Report the active theme. |
| `/thinking [on\|off]` | Stream future sanitized reasoning, or toggle when no argument is supplied. |
| `/tools` | Browse and toggle non-essential repository tools. |
| `/trust [inspect\|read\|build\|mutation\|automation]` | Show or change repository trust. |
| `/validation retry` | Resume interrupted post-apply validation. |


### Durable session lifecycle

`/new`, `/resume`, and `/clone` use one serialized host-owned transition boundary and require active model, tool, mutation, validation, hook, skill, and delegated work to finish or be cancelled. `/resume` lists only sessions for the currently open repository, newest first; an exact ID from another repository reports a mismatch without changing repository, trust, solution, or working directory.

A resumed session reconstructs tolerant event projections and the sanitized conversation archive, explicit repository memories, mode, persisted usage, and compatible model/reasoning selection. Stale context inspections and provider continuation/cache handles are invalidated. A clone receives new session-local identities and independent future history; it does not duplicate live execution authority, approvals, transactions, leases, credentials, hidden reasoning, or provider transcripts. Clone output includes a copyable `/resume <source-session-id>` return command. See [Session lifecycle operations](operations/session-lifecycle.md).

### Retained TUIKit frontend (default)

Run `threadsmith --tui` for the default full-screen interface; `threadsmith --tui=tuikit` is equivalent. Run `threadsmith --tui=original` for the previous PrettyPrompt/Spectre interface with native scrollback. Both use the same commands, repository/session workflows, approvals, policies, models, themes, source/Markdown settings, and run coordination. No frontend is selected by configuration. Invalid or repeated frontend selectors fail before startup, and the former `--tui=pretty` spelling is rejected.

TUIKit keeps a title bar, MAIN and active-child tabs, a bordered output pane with the selected agent’s status and activity, a bordered multiline composer (normally five content rows), and a fixed repository footer. The footer shows the folder, branch/detached state, and staged/modified/untracked/conflict counts from a bounded periodic Git query. It requires at least 40 columns by 12 rows; at that minimum it retains two output rows and one editor row. Shrinking further preserves state until the terminal grows again. `tui:footer:enabled=false` hides the repository footer while agent status and accounting remain available.

Pane borders are rounded. Content has side and bottom padding; modal headings and the output status header sit flush below their top borders with a blank line beneath them. The composer has no top padding. Agent tabs have one leading space and a one-cell gap using the tab bar background; MAIN is selected even when it is the only tab. The output header starts with `Using model: (Provider Name) Model Name`, followed by reasoning effort and usage. A right-aligned `Context` bar shows the latest context percentage and capacity. `/models` lists `(Provider Name) Model Name`, sorted by provider and then model name.

Enter submits unless a command suggestion is visible, in which case it only inserts the selected command name. TUIKit moves each committed ordinary entry into the retained transcript before clearing the composer. After required startup choices, the logo splash shows actual loading phases and elapsed times. Input and paste during startup are discarded. Successful startup phases and remembered-solution messages stay in the splash and are discarded when it closes. Warnings and failure/cancellation diagnostics remain in MAIN; ordinary input starts after loading finishes. Ctrl+Enter inserts a newline; Shift+Enter and Alt+Enter do so where the terminal distinguishes them. Ctrl+Alt+Enter submits. Editing supports grapheme/word movement, selection, multiline paste, bounded undo/redo, indentation, and submission history. Ordinary, secondary, and steering prompts keep separate drafts. During a run, Enter requests steering at a safe boundary when no suggestion is visible; double Escape cancels the run. Ctrl+C copies selected text and otherwise exits through process cancellation.

F1 opens a non-selectable key-help list; arrows or PageUp/PageDown scroll it when needed. F7 switches keyboard focus between MAIN’s composer and output. Child tabs are read-only: selecting one preserves MAIN’s draft and blocks editing, paste, completion, submission, and steering. F7 keeps output focus on a child. Ctrl+Left/Right cycles all agents only from output; the composer retains word movement. Captured mouse clicks select tabs, including overflow arrows. F2 exposes complete agent status and labels. In the transcript, arrows/PageUp/PageDown/Home/End scroll; shifted movement selects. Incoming output preserves detached scroll position. The activity row starts with an unseen-output count and follow keys, even after a run returns to the ready prompt. Press F7 then End from the composer, or End while the transcript has focus, to reveal the latest output. Ctrl+L clears the visible viewport while bounded earlier content remains reachable with Home. Ctrl+C and F6 copy visible selected text; Ctrl+C cancels the process only when nothing is selected. Ctrl+Shift+C copies the focused selection or complete draft. F8 lists validated links from retained output so Enter can copy one. F12 releases or recaptures the mouse, allowing terminal-native selection while released. Explicit application copy is limited to 64 KiB and depends on OSC 52 terminal support. Ctrl+V/Shift+Insert request an OS clipboard read bounded to one MiB and two seconds; terminal bracketed paste is also supported.

F3 opens the command palette when the ordinary composer is focused and contains only whitespace or a partial leading slash command. Search names and descriptions with a fuzzy query, navigate with arrows/PageUp/PageDown/Home/End, and press Enter to insert the selected canonical name. Usage and description appear below the results, with F6 to copy them. Escape or F3 closes without changing the draft; paste stays within a single-line, 256-character query.

Typing a partial slash token, such as `/rea`, shows up to six prefix suggestions. Unmodified navigation keys choose a result, Tab or Enter inserts it, and Escape dismisses only the suggestions. Acceptance never executes a command, submits input, or appends a space: add arguments and press Enter separately. Undo restores the prior draft in one step. Exact names, arguments, prose, multiline or selected text, secondary/steering prompts, other modals, transcript focus, and undersized terminals do not show suggestions. Modified Enter retains its existing behavior. Argument completion is not supported. These discovery interfaces are specific to TUIKit; the original frontend is unchanged.

Selectors use centered frames and block background keys, paste, and mouse input. They filter labels while preserving stable option identities. Arrows/PageUp/PageDown/Home/End navigate, Enter selects, and Escape cancels. F2 opens complete scrollable option details, including long paths and model descriptions. F8 lists validated links from retained output; Enter copies the selected target and F2 shows the complete target. Links never execute automatically.

`/tools`, `/hooks` (also `/hooks list`), `/mcp` (also `/mcp list`), MCP tool availability, and `/extensions` use keyboard-only checkbox trees. Arrows navigate; Space applies each toggle immediately and leaves the modal open for more changes. Typing/paste filters, Backspace edits the filter, F2 shows details, and Esc/Enter closes while keeping completed changes. Group toggles affect only visible eligible members. Checked means tool available, hook enabled, MCP profile connected, or extension loaded. Hook enablement does not grant repository approval; connecting a server does not enable its tools or change startup auto-connect. Failed operations restore the reported host state. `/theme` is a single-selection modal.

In `/mcp`, select an eligible OAuth profile and press F3, then choose **Sign in / Authenticate**. Esc during authentication cancels the attempt and returns to the connection list; status refreshes afterward. Static-token profiles have no sign-in action. To change identity, use `/mcp logout <profile>` and sign in again. See [management dialogs](operations/agent-workspace.md#management-dialogs).

`/help` opens a modal with wrapped command and description columns. Up/Down, PageUp/PageDown, Home, and End scroll it; Esc closes it. It does not add help text to the output transcript.

Children receive a stable random person name plus role, such as `Shackleton · Explorer`. Each has independent output, scroll, selection, activity, effective request model/reasoning, latest context estimate/capacity, and input/cache-read/output counters. MAIN counters exclude children; aggregate session accounting is unchanged. `~` means estimated, `?` unavailable, and `+?` a known subtotal with missing usage. After resume, per-agent counters report only observations since resume; historical totals are not assigned to MAIN. Each child’s queued, running, and final status updates a named row inside MAIN’s live `delegate_agents` tool block; the terminal update is captured before the child tab disappears. The completed tool block retains final rows beneath its timer. Repeated checkpoint outcomes are deduplicated. The old delegation-GUID announcement and inspection hint are omitted; `/agents` still provides IDs for explicit inspection or cancellation.

Live tool blocks appear in the selected agent’s output pane before their operations complete. Native/extension calls use `TOOLS:` and MCP calls use `MCP:`. Concurrent calls retain independent timers and final blocks, including out-of-order completions. Short panes disclose omitted progress rows or additional running tools. `ENTER to steer; ESC-ESC to cancel` appears at the bottom of MAIN after its activity timer only during an active turn, then disappears. Live frames and hints do not accumulate in copied transcript history.

When supplied by a provider, usage adds a breakdown such as `out 2,000 (reasoning 1,200)`. Missing counts leave the display unchanged, and reported zero is shown. Reasoning is already included in output totals and cost. Cumulative reasoning is hidden if any contributing request lacks the count; F2 can still show a reported count for the latest request. This is independent of `/thinking` text visibility. F2 also shows per-request cache reads, writes, and hit percentage; see [cache reporting](operations/cache-optimized-context.md).

The retained transcript keeps up to 1024 chunks/512 KiB and visibly announces eviction. Child source and projected text additionally share a 4 MiB budget, with at most 8192 logical chunks across children. Transient display queues hold 256 fragments of at most 4096 UTF-16 units each; overflow is reported without dropping execution decisions, lifecycle, or usage. Completed child views are released. It is a view, not durable session history or native terminal scrollback. Themes and `NO_COLOR` remain supported; selector markers remain visible without color.

For name configuration and full themed defaults, see [Agent workspace operations](operations/agent-workspace.md).

### Original frontend keyboard and clipboard

The following keys apply to `--tui=original`. The default retained TUIKit frontend has its own keyboard and clipboard details [above](#retained-tuikit-frontend-default).

| Input | Action |
|---|---|
| `Enter` | Submit the composer. |
| `Shift+Enter` | Insert a soft newline. |
| `Ctrl+V` or `Shift+Insert` | Paste clipboard content as one operation. |
| `Ctrl+C` | Cancel current input or an active run; with a native terminal selection, copy that selection. |
| `Ctrl+Shift+C` | Copy a PrettyPrompt editor selection where supported. |
| `Ctrl+T` | On an empty composer, toggle future reasoning streaming. |
| Mouse drag / terminal mark mode | Select across the native transcript and composer output. |

Threadsmith deliberately retains native terminal scrollback and does not enable mouse capture. Terminal-specific selection shortcuts remain controlled by the terminal emulator.

### Reasoning display

Reasoning is hidden by default. While a turn is active, Threadsmith shows transient `THINKING` activity and removes it before the first visible answer or terminal outcome; completed transcripts contain no host-generated `THINKING` marker. During active-turn candidate work, `COMPACTING CONTEXT` temporarily replaces `THINKING`, shows the current before/target token counts, candidate profile, and elapsed time, then emits one bounded completion line with actual before/after/savings/status/profile/duration before `THINKING` resumes. No summary, prompt, source, or tool-result content is displayed by default. `/thinking on` enables live streaming of future sanitized reasoning chunks using the `Reasoning` semantic style. In the default TUIKit frontend, the separate `THINKING` row stays active alongside that text, including the wait after the last reasoning chunk and while the answer is buffered. The original frontend releases its transient spinner before writing reasoning into native scrollback. `/thinking off` disables future streaming, and `/thinking` or `Ctrl+T` toggles the same in-session setting. Turning streaming off cannot remove reasoning already written to native scrollback. This does not expose credentials or raw unsanitized provider content.

### Cross-turn conversation context

Conversation continuity is bounded and host-owned. Threadsmith archives only sanitized accepted user requests and final visible assistant responses. When the host records a completed or failed execution outcome, it also records one compact, sanitized host-authored assistant receipt with the original request, reported status, changed files, behavior summary, validation gate, rollback availability, and final diff reference. The JSON is historical data rather than instructions or repository memory; a failed receipt can still list changed files, so its reported status remains authoritative. Recent exchanges pair requests and responses from the same run and are ordered by completion. New clones preserve these associations under fresh run IDs; older cloned archives retain their original adjacent pairs where run associations were not preserved. Hidden reasoning, provider wire payloads, and raw tool output never enter the conversation archive. Large bodies use the content-addressed artifact store while metadata, hashes, ordering, and provenance remain durable.

- **Conversation-aware** (default): current input, bounded recent complete turns, and relevant explicitly saved repository memories.
- **Governed-memory-only**: current input plus relevant explicitly saved repository memories; no raw prior messages.
- **Stateless**: current input and current-run governed state only. Mode changes preserve the archive for later use.

The host does not promote requirements, decisions, questions, findings, or completion receipts into automatic memories. Old automatic snapshots never return through resume or clone. Ordinary transcript continuity and model-generated active-turn compaction remain available.

### Repository-scoped memory

Memories are concise notes saved in the current repository's ignored `.threadsmith/threadsmith.db`. They survive `/new`, `/resume`, clone, and restart for that repository, and are shared by its local sessions. They are not tracked by Git or shared with other repositories.

Ask Threadsmith to remember a durable preference, correction, or project detail, and the model can call the single `memories` tool. Its console block identifies the operation (`add`, `update`, `remove`, or `list`), the actual outcome, and the returned memory IDs and text. Removed notes remain visible in that operation's output. Lists report omitted entries, and failed add/update attempts label the text as requested rather than saved. Typical notes display in full beyond the ordinary 240-character tool-detail limit; an overall display bound remains in effect. Headless tool results expose the same action, outcome, and returned text in their bounded JSON preview.

You can also manage notes directly:

```text
/memory remember <text>
/memory list
/memory inspect <memory-id>
/memory update <memory-id> <replacement-text>
/memory forget <memory-id>
```

`supersede` is a compatibility alias for `update`: it now corrects the same ID rather than creating an inactive audit copy. `validate`, old category arguments, and validity filters are retired and return migration guidance. Both terminal frontends and headless commands use the same service. Terminal `/memory list` shows each note’s ID, origin, memory type, and text. `/memory inspect <id>` also shows creation/update times, inclusion count, last inclusion, and embedding identity or unavailability. Headless list output includes the entry metadata. Embedding vector components are excluded from headless JSON and terminal output.

New and updated text must fit 2,000 characters after sanitization and the local model's 256-token complete sequence limit, including boundary tokens. Shorten an oversized note; Threadsmith never saves full text with an embedding of only its beginning. Exact normalized duplicates return the existing ID, unchanged updates do nothing, and an update duplicating another note reports that note's ID without merging. If another caller changes a note while an update is being prepared, the update reports a conflict; inspect the current note and retry. Case and meaningful interior whitespace are preserved. Removing a note deletes current search/usage state; already transmitted context and historical conversation records remain historical.

Embedding upgrades preserve note IDs, text, content revisions, and usage history. On the next semantic lookup, vectors from an older embedding space are rebuilt locally against the current text. Notes that no longer fit the encoder remain inspectable and lexically searchable; shorten them with `update` to restore semantic search. The current encoder counts four boundary tokens within its 256-token limit, leaving up to 252 content tokens.

By default, at most twenty notes are stored. Situational notes use the existing zero-to-three relevant-note context limit; standing preferences are always included when memory is enabled in a non-`Stateless` request. They bypass retrieval, reranking, and that cap, reserve required context, and cause a normal request-capacity failure if they cannot fit rather than being silently omitted. Retrieval combines SQLite lexical matches and local semantic similarity, then applies mode, sensitivity, and token budgets. Semantic candidates must score strictly above `SemanticMinimum`, which defaults to `0.47`; lexical matching is unaffected. Unrelated notes need not appear. `/context inspect` separates selection and final budget inclusion from actual-submission receipt outcomes, and reports branch contributions, the effective semantic threshold, query truncation, cache reuse, and fallback. Listing or previewing context does not count as inclusion; repeated tool rounds count once per user turn/content revision.

Memory is best-effort recall. At capacity, older/disused notes may be evicted, with the same policy for manual and model notes. Meaningful adds/corrections receive a seven-day recency window when older candidates exist; if all notes are new, the oldest can still be evicted. Put instructions that must always apply in `AGENTS.md`. Routine conversation, approvals, mutations, rollback, and completion do not automatically create notes, and repository edits do not automatically invalidate them.

Configure these memory settings through ordinary machine, user, repository, session, CLI, and `THREADSMITH_` environment layering:

```json
{
  "reranking": {
    "cpuThreads": 8
  },
  "tools": {
    "config": {
      "memories": {
        "MaxNumberOfRepoMemories": 20,
        "MaxRepoMemoriesInContext": 3,
        "SemanticMinimum": 0.47,
        "RerankerEnabled": false,
        "RerankerCandidateLimit": 8,
        "RerankerMinimumScore": null,
        "standingPreferenceWarningThreshold": 3
      }
    }
  }
}
```

Storage capacity must be positive; the situational context limit may be zero to disable situational retrieval and cannot effectively exceed storage capacity. A lower capacity is enforced only when the next repository bind/configuration refresh succeeds. Failed repository opens preserve the target repository's stored memories and pending memory migration. `SemanticMinimum` must be a finite double from `-1` through `1`; higher values are more selective, lower values allow weaker semantic matches, and `1` admits no semantic candidates because comparison is strict. Lexical candidates still qualify independently. Memory configuration is captured for each operation and user turn when a repository is bound; after editing a configuration file, restart Threadsmith or reopen the repository to apply it. Changing only this threshold reranks the cached selection for the next request while reusing compatible query vectors, so it neither rebuilds vectors nor changes the embedding space. Tool enable/deny controls withhold model operations and automatic memory injection together; explicit manual management remains available. Old `context:repositoryMemory` settings are ignored with a deprecation diagnostic.

The bundled CPU encoder works locally and independently of the conversational model. If it is unavailable, writes that change text fail visibly and situational retrieval falls back to qualified lexical matches. Type-only updates reuse the stored vector. A failed search index preserves readable standing preferences; if the memory database cannot be read, context assembly fails with an explicit error. Imported older manual notes remain inspectable even when too long for the encoder and can be corrected with `update`. See [conversation context operations](operations/conversation-context.md) for migration backups and recovery.

Optional local memory reranking is enabled with `tools:config:memories:RerankerEnabled=true`. It is disabled by default, scores up to `RerankerCandidateLimit` qualified candidates (default 8, range 1–64), and preserves hybrid retrieval if the cross-encoder is unavailable or a query-memory pair is truncated. `RerankerMinimumScore` is an optional finite raw-logit cutoff; its default `null` applies no rejection threshold. `reranking:cpuThreads` controls the startup CPU thread budget (default 8, range 1–32). Restart for CPU changes; reopen the repository or restart after editing memory settings. See [conversation context operations](operations/conversation-context.md) for staging and configuration details. Retrieved situational notes are introduced as **Repository memories that may be helpful**, with guidance to use them only when relevant. Use `/memory remember [--type standingPreference|situational] <text>` or `/memory update <id> [--type standingPreference|situational] <text>` to set a type; omitted `remember` defaults to situational and omitted `update` preserves the type. When standing preferences after eviction exceed `standingPreferenceWarningThreshold` (default 3), startup, additions, and explicit type promotions warn that some may belong in `AGENTS.md`.

#### How context optimization works

Threadsmith arranges every model request so content that changes least appears first and request-local content appears last:

1. stable host policy;
2. the applicable repository instruction bundle;
3. instructions for the current execution phase;
4. relevant explicit repository memories and complete recent user/assistant turns in chronological order;
5. current governed state and attributable evidence;
6. the current user input;
7. correlated tool calls and results appended during an otherwise unchanged multi-round request.

This layout gives providers that support exact-prefix caching the best opportunity to reuse an unchanged prefix. It does **not** weaken host behavior: tool eligibility, trust, approval, evidence, model capacity, validation, and recovery are evaluated normally whether a remote cache is used or not. Hidden reasoning is never added to later requests.

Eligible tool definitions are canonicalized into one deterministic inventory. A provider with native tool support receives that inventory through its native protocol rather than receiving a second copy in prompt text. Legacy adapters may receive one deterministic textual inventory. This avoids paying for duplicate schemas and prevents unrelated tool ordering from changing unpredictably.

During an unchanged tool round, Threadsmith freezes the assembled prefix and appends the assistant tool call plus its correlated result. It deliberately rebuilds the request when the execution phase, repository trust or policy, eligible tools, applicable instructions, conversation compaction generation, repository-memory content, selected model, or request layout changes. Rebuilding is a correctness boundary, not a cache failure.

Capacity checks use the estimated provider-wire request rather than only visible prompt text. The estimate includes structured content, native or textual tool schemas, provider framing, and the selected model's output reserve. Context reduction occurs before dispatch; a request that still cannot fit fails before contacting the provider.

##### Active-turn tool continuation compaction

Active-turn compaction keeps one long user request from repeatedly sending every earlier tool result until the active main model's context window is exhausted. It applies automatically to ordinary multi-round evidence collection and planning. It does not end the turn, ask the user to resubmit the request, change the active main model, or expose a compaction tool to the model. Candidate generation can optionally use a separately configured auxiliary model profile.

`/context compact` is retired and returns guidance; it no longer creates structured facts from completed conversation turns. Active-turn compaction is an automatic pre-sampling operation over tool continuation generated **inside the request currently running**. There is currently no user or repository setting that disables, postpones, or manually triggers the active-turn reliability boundary.

Threadsmith uses these terms:

| Term | Meaning |
|---|---|
| **Frozen context** | The initially assembled host policy, repository instructions, current user input, output contract, canonical tool inventory, and other authority-bearing context. Active-turn compaction never rewrites it. |
| **Complete group** | All sibling assistant tool-call messages produced by one model round followed by exactly one matching result for each call, in call order. An incomplete, mismatched, duplicate, or orphaned call/result set is not compactable. |
| **Delivered group** | A complete group that has already been sent verbatim to the model in a later completed request. |
| **Eligible prefix** | The oldest contiguous delivered groups that can be considered for replacement while newer groups remain exact. |
| **Cumulative summary** | One bounded, explicitly untrusted historical assistant message that replaces an eligible prefix. A later compaction receives the complete previous summary plus newly old raw activity and returns one updated checkpoint. |

The exact sequence is:

1. **Form and deliver a complete group.** Threadsmith buffers sibling calls and results independently, then emits every call before the matching call-ordered results. The next model request receives that group exactly. A newly completed group cannot be summarized on this first delivery.
2. **Estimate before every later model request.** The host estimates the complete provider-wire request, including frozen messages, any active summary, raw continuation groups, native or textual tool schemas, provider framing, and the selected profile's effective request output reserve.
3. **Compare with the pressure target.** The operational target is 75% of the usable selected-model input budget. The usable budget is bounded by both the host request budget and the model context window after its effective output reserve. Below that target, Threadsmith sends the unchanged request.
4. **Select only an old delivered prefix.** At pressure, Threadsmith retains complete recent groups covering the newest 12,000 estimated raw continuation tokens, or all available groups when there is less history. Fixed instructions and the summary allowance do not reduce that retention target to meet the preferred request size. It always keeps at least the newest complete group, never splits a sibling group, and never selects a group that has not already been delivered verbatim. The model's actual input-capacity checks and emergency reduction remain separate.
5. **Prepare a bounded candidate request.** Preflight validates aggregate group, message, source, and file-list bounds and projects all candidate input from one candidate-profile-derived capacity budget. The candidate receives the required task objective and required-first acceptance intent, the complete previous active-turn summary when present, and bounded projections of the selected raw tool activity. Preflight performs no provider I/O and creates no hook, usage, call, or cost record. If the configured candidate profile prohibits sensitive input, preflight rejects it before provider I/O.
6. **Generate a Markdown checkpoint.** An actual candidate attempt uses the separately configured compaction profile when present, or the active main profile as a backward-compatible fallback. The candidate profile may identify a different model or provider and owns candidate context/reserve, maximum output, reasoning, temperature, timeout, retry, sensitivity, and pricing. Candidate requests advertise no tools, use the `Summary` workload when independently configured, set a request-specific model-output ceiling equal to 80% of the summary budget by default, and cross the normal managed before/after model-request hook boundary. They are real model requests: profile identity, reported usage, missing usage, call count, duration, and any partial usage emitted before a later failure are accounted under each attempt's unique identity. One transient retry is allowed, for at most two candidate calls.
7. **Validate the replacement.** The candidate returns a completed Markdown summary. The host validates schema, the exact cumulative source-group range, host-observed file lists, sanitization, and authority markers. It rejects empty or incomplete output, but does not convert the requested token budget into a character cutoff or reject a completed summary using an estimated output-token count. The host strips any model-emitted `Files read` or `Files changed` sections and appends its own cumulative file lists. Provider stream controls and the rebuilt request's capacity checks still apply.
8. **Build and re-estimate the replacement.** The model receives the complete prior summary on update attempts and returns one replacement checkpoint rather than an append-only delta. The candidate activates when the rebuilt complete request is smaller than before. It does not have to get below the pressure target in a single operation.
9. **Replace atomically and continue.** On success, one assistant message labeled as untrusted earlier active-turn history replaces only the selected old prefix. Newer groups and frozen context remain exact, the history rewrite generation increments, and the same user turn continues with the rebuilt provider-neutral request.

For example, suppose one request produces eight complete tool groups. Group 1 becomes eligible only after a later completed request has received it exactly; the same rule independently applies to every later group. If the full request then reaches pressure, Threadsmith might replace delivered groups 1–5 with summary version 1 while retaining groups 6–8 verbatim. If the turn continues and later reaches pressure again, the candidate receives the complete version-1 summary plus the next eligible raw prefix and returns one version-2 checkpoint. The original group results are not deleted from the evidence/audit record.

Current host-owned defaults are:

| Boundary | Default behavior |
|---|---|
| Pressure trigger | 75% of the effective active-main-model input budget |
| Main output reserve | Active main profile's effective request reserve; 8,192 tokens only if no profile reserve is available |
| Newest raw retention target | 12,000 estimated tokens in complete groups, independent of the pressure target; at least the newest complete group remains exact |
| Summary allowance | 16,384 tokens used to calculate the requested model output budget, not an exact rendered-text limit |
| Model-written summary output | 80% of the summary budget by default; 13,107 tokens with the default 16,384-token budget |
| Minimum required savings | Any positive rebuilt-request reduction |
| Candidate profile | Trusted explicit profile when configured; otherwise the active main profile |
| Candidate input | Lesser of 65,536 estimated tokens and the candidate profile's available input capacity |
| Candidate source shape | At most 48 groups and 512 aggregate call/result messages |
| Candidate summary shape | One bounded Markdown checkpoint; host appends file lists |
| Task projection | Up to 4,000 objective characters and 32 required-first acceptance items, bounded to 1,000 characters each and 4,000 total characters |
| Candidate attempts | One initial call plus at most one transient retry; two calls total |
| Failure backoff | Skip candidate generation for the next two failed pressure assessments |

To select one compaction model and reasoning level for the main loop and every subagent role, put settings in machine or user configuration (`~/.threadsmith/config.json`), not repository configuration:

```json
{
  "context": {
    "activeTurnCompaction": {
      "profileId": "00000000-0000-0000-0000-000000000000",
      "reasoningLevel": "medium",
      "summaryBudgetTokens": 16384,
      "modelOutputBudgetPercent": 80
    }
  }
}
```

Replace the example GUID with an enabled profile ID from `/models`. The profile must support streaming and the `summary` workload, or have no workload restriction. It can use another provider. `reasoningLevel` is optional and defaults to the profile's reasoning; an explicit value must be supported by that profile. Invalid values fail startup. The same profile and reasoning apply to main-loop and subagent compaction, independently of `agents:roleModels` and the model doing the task. There is no separate subagent compaction-model setting.

The profile and its provider instructions resolve from the catalog without repository overrides. Candidate credentials must come from user-owned-or-higher secret providers; repository secret stores cannot supply or replace them. The request-specific generation limit is the lower of the profile's effective output reserve and the configured model-output percentage of the summary budget. With main-loop defaults, a 16,384-token summary reserve requests `13,107` output tokens. Subagent summary budgets remain under `agents:delegation:compaction:summary`; choosing a shared model does not change those budgets.

Remove or set both `profileId` and `reasoningLevel` to `null` to let each loop summarize with its own active model and reasoning. A reasoning override without a profile is rejected. These settings are resolved at startup; restart Threadsmith after changing them. Repository configuration at this path is ignored. Each task model still owns its 75% trigger and ordinary-request capacity; a smaller compaction model does not make it compact earlier. If the compaction model fails, the original history remains active; Threadsmith does not silently switch the configured compaction model.

Compaction is deliberately lossy only in the model-visible working set. It does not change current user intent, host or repository instructions, trust, tool eligibility, approvals, mutation authority, output requirements, active main model, or sensitivity policy. Tool-result groups are conservatively classified as repository-sensitive for auxiliary routing; a configured candidate profile that prohibits sensitive input fails preflight with no hook or provider call. An active-turn summary never becomes system/developer/current-user content, durable conversation memory, or repository memory. The original sanitized tool events and evidence remain under the existing audit, artifact, retention, and redaction rules. Ordinary active-turn summary checkpoints are kept in memory for the running turn.

Cancellation, hook denial, provider failure, invalid output, validation rejection, and zero-or-negative savings leave the original continuation active and start bounded backoff. If that unchanged request still fits, the turn continues with exact raw groups. If it reaches the emergency boundary, the deterministic compatibility reducer may shorten only older results that were already delivered verbatim. It never shortens a never-delivered group. If the request cannot fit without doing so, Threadsmith fails with a controlled message of the form `Tool continuation requires <tokens> input tokens but the selected model budget is <budget>.`

`/context inspect` shows the latest assessment without exposing summary or tool-result content. Its `active-turn` line reports the status; before/after input estimate; pressure target and maximum; main output reserve; effective/configured retention; candidate profile ID; eligible, compacted, and retained group counts; retained tokens; summary version; cumulative pruned-item count; history generation; remaining backoff; and host rationale. Status values mean:

| Status | Meaning |
|---|---|
| `Disabled` | Host composition did not provide active-turn compaction. |
| `BelowPressure` | The canonical complete request is below the operational target. |
| `NoEligiblePrefix` | Pressure was reached, but no complete previously delivered prefix can be cut. |
| `Backoff` | A prior failure temporarily suppressed another candidate attempt. |
| `Completed` | A validated cumulative summary atomically replaced the reported prefix. |
| `ValidationRejected` | Candidate schema, fact, source, authority, sensitivity, range, or bound validation failed. |
| `ProviderFailure` | Candidate generation exhausted its bounded call budget or was denied at the managed provider boundary. |
| `Cancelled` | Cancellation retained the original continuation. |
| `InsufficientSavings` | A valid candidate did not reduce the rebuilt canonical request. |
| `EmergencyReduction` | The compatibility reducer shortened only older already-delivered result content. |
| `CapacityExceeded` | The request could not fit without reducing a group that had not yet been delivered exactly. |

Provider cache support and reporting vary. A successful rewrite increments a provider-neutral history generation, and compiled providers receive the complete rebuilt stateless request rather than reusing an incompatible opaque conversation identity. The unchanged stable prefix remains eligible for provider prefix caching. Threadsmith reports cache-read or cache-write tokens only when the provider supplies them; a missing counter is **unavailable**, not zero, and latency alone is never treated as proof of a cache hit.

Outside the active-turn line, `/context inspect` reports logical content tokens, provider-wire input/budget, stable-prefix tokens, tool transport, mode/source, included/omitted messages and memories, hybrid branch scores, query truncation/cache/fallback diagnostics, actual-submission receipt outcomes, and pressure reductions. Retired automatic summary fields remain empty. Headless callers receive the same host-owned inspection projection as stable JSON.

Configure budgets under `context:conversation` in `.threadsmith/config.*`; `.threadsmith/config.example` documents recent-turn, pressure, and artifact bounds. Invalid values fail before model invocation. See [Conversation context operations](operations/conversation-context.md) for continuity defaults, failure behavior, retention, restoration, and headless contracts. See [Cache-optimized context operations](operations/cache-optimized-context.md) for request ordering, instruction confinement, diagnostics, and provider-acceleration safety.

## How repository changes are governed

Threadsmith treats the model as a reasoning component, not as the owner of control flow.

A typical change follows this sequence:

1. **Evidence collection** — authorized read-only tools gather bounded repository context. Tool availability and invocation policy determine what can be advertised or called.
2. **Plan proposal** — the model calls the host-owned `propose_plan` tool. Text that merely describes edits is not enough to enter mutation flow.
3. **Plan sanity and approval policy** — the host validates plan schema 2, runs cheap repository sanity checks over structured `fileIntents` (`Modify`, `Create`, `Delete`, `Move`, or `Rename`), classifies risk, and either returns repairable scope failures to the model for a bounded plan revision, prompts for manual review, or auto-approves according to `/plan-policy` / `planning:approvalPolicy`. Plan approval authorizes implementation work only, not repository writes.
4. **Implementation proposal** — after manual or policy plan approval, the host refreshes only the approved file endpoints from disk, so a previous edit or manual rollback is reflected in the new execution baseline. The TUI shows `MUTATION: Generating edits` while the parent model receives eligible source evidence and proposal-only `propose_mutations`. The model supplies changes; the host correlates them to accepted plan steps. For a unique nonempty `expectedText`, offsets and lengths may be omitted. The host computes the UTF-16 range, avoiding model character counting. Empty insertions and repeated anchors require an exact offset.
5. **Proposal validation before Roslyn** — the host assigns identities and validates the proposal schema, plan-step ids, scope, paths, trust, current mutation baseline, baseline hashes, exact replacement text, lifecycle preconditions, structured-output size, and budgets. C# `RenameSymbol` proposals expand through the loaded semantic workspace when the model supplies the semantic symbol id and new identifier. When replacement matching differs only in CRLF, LF, or CR line endings, the host recovers a unique match without another model call, including in mixed-ending files. The replacement uses the matched region's first line-ending style; text outside that region stays unchanged. Literal source escapes are preserved. Ambiguous matches and other text differences still require repair. Retries and approval use the frozen execution generation; later external edits remain conflicts. Repairable host validation failures, such as wrong `expectedText`, are returned to the model for a bounded proposal retry; exhausted retries report the rejection reason, and non-repairable policy/trust/path failures fail closed.
6. **Pre-mutation Roslyn screening** — before staging or user approval, proposed `.cs` changes are applied only to an in-memory overlay. Threadsmith parses the would-be source with Roslyn and, when a loaded project is available at sufficient semantic confidence, runs fast compilation diagnostics against the overlay without `dotnet build`. Blocking diagnostics are mapped to file/range, diagnostic id, message, changed line/hunk, containing syntax when available, and explicit omissions, then returned to the model for proposal-phase repair. Repository files remain unchanged. Trusted or isolated analyzer/code-style checks may participate when implemented and available; ordinary repository-supplied third-party analyzers and source generators are not loaded pre-approval and are reported as degraded omissions until post-approval validation.
7. **Private staging and exact diff** — only a proposal that passes host validation and cheap pre-mutation gates enters the private staged workspace. Threadsmith records the exact bounded diff. Interactive TUI presentation shows compact changed hunks with bounded unchanged context, hidden-line markers, and one blank display line after each hunk header without changing canonical diff content.
8. **Mutation approval policy** — depending on `/policy` and `mutation:approvalPolicy`, Threadsmith either prompts for approval or applies the host-authorized set automatically. Every policy preserves the exact diff and invariant guardrails.
9. **Authoritative pre-write baseline** — under the configured post-mutation validation stages, Threadsmith captures the exact affected pre-mutation diagnostic baseline before write-ahead mutation intent. When semantic-only validation is configured, this uses the loaded semantic workspace without launching a build; when compile/diagnostics stages are configured, affected projects are built to capture authoritative baseline diagnostics.
10. **Transactional application** — after authorization and baseline capture, the host records write-ahead intent, hash- and path-checks authorized files, applies atomic replacements/lifecycle operations, and reconciles the result before advancing. The next transactional baseline reuses unchanged captured files and reads only changed endpoints, preserving later external-edit checks without rereading the entire repository.
11. **Post-mutation validation** — configured validation stages run in order. The default is semantic, compile, diagnostics, and affected tests. Build/test validation remains authoritative even if pre-mutation Roslyn screening passed.
12. **Correction or completion** — introduced compiler/test failures can stage a bounded correction against a promoted transactional baseline while retaining the original diagnostic baseline. Every correction repeats proposal validation, pre-mutation screening, exact diff, policy, transaction, and validation gates. The host records an authoritative final outcome rather than trusting model success claims.

Approval is fail-closed. The model cannot expose mutation tools to itself before the host has accepted the plan, and plan scope does not authorize destructive Git operations or writes outside approved repository roots.

### Pre- and post-mutation validation controls

Threadsmith has two validation layers around mutations:

| Layer | When it runs | What it does | How to enable or disable |
|---|---|---|---|
| Plan sanity checks | Before plan review or auto-approval | Checks structured plan `fileIntents` for repository-relative path safety, empty or ambiguous scope, modify/delete/move/rename sources that are missing, create/move/rename destinations that already exist, protected/secret/`.git` paths, generated/binary files, lifecycle/delete/move risk, dependency/configuration changes, and bounded scope size. Repairable failures are returned to the model for plan revision. | Always on when a plan is proposed. `execution:maxCorrectiveTurns` bounds repair attempts. `/plan-policy` controls approval after checks pass; it cannot disable checks. |
| Proposal/schema/path/baseline validation | Before staging | Validates typed mutation shape, plan-step correlation, scope, trust, repository-relative paths, baseline identities, exact replacement text, lifecycle preconditions, size limits, and budgets. | Always on. It cannot be disabled by repository configuration or approval policy. |
| Pre-mutation Roslyn syntax screening | Before staging and before approval | Parses proposed `.cs` overlay text in memory and returns blocking syntax diagnostics for model repair. It does not write files or run a build. | Automatic for proposed `.cs` mutations when the pre-mutation analyzer is available. There is no separate user/repository toggle. Non-C# changes skip this layer. |
| Pre-mutation Roslyn semantic/compilation screening | Before staging and before approval | Uses the loaded semantic workspace to run fast overlay compilation diagnostics without `dotnet build` when confidence permits. Unknown/orphan `.cs` files degrade to syntax-only analysis. | Enabled by having a loaded solution/project with semantic confidence. It degrades explicitly when the workspace is absent, stale, unloaded, or below required confidence. It is not controlled by `validation:stages`. |
| Pre-mutation analyzer/code-style screening | Before staging and before approval | May run only host-owned, allowlisted, trusted-policy-approved, or isolated analyzer/code-style checks. | Ordinary repository-supplied third-party analyzers/source generators are not loaded pre-approval. They degrade to omissions and remain covered by post-approval build/analyzer validation. |
| Exact diff and mutation approval | After staging, before writes | Shows/records the exact diff and applies `/policy` / `mutation:approvalPolicy`. | Select interactively with `/policy`; configure defaults with `mutation:approvalPolicy` and `mutation:largeDiffThreshold`. Invariant guardrails remain always on. |
| Pre-write diagnostic baseline | After approval, before writes | Captures the authoritative diagnostic baseline used to distinguish baseline from introduced failures. | Controlled by `validation:stages`; semantic-only avoids build, compile/diagnostics uses affected `dotnet build --no-restore`. |
| Post-mutation compile/diagnostic validation | After transactional apply | Builds affected projects and classifies/correlates diagnostics. | Include `compile` and/or `diagnostics` in `validation:stages`. Removing them narrows validation and is not the recommended default. |
| Post-mutation affected tests | After successful affected build/diagnostics | Selects and runs relevant tests with host-authored rationale. | Include `tests` in `validation:stages`; configure `validation:testScope` where supported. |
| Correction loop | After failed post-mutation validation | Lets the model propose a bounded correction; every correction repeats all proposal, pre-mutation, diff, policy, transaction, and post-validation gates. | Bounded by `execution:maxCorrectiveTurns`; cannot bypass approval, transaction, or validation. |

The default repository configuration leaves post-mutation validation broad:

```json
{
  "validation": {
    "stages": [ "semantic", "compile", "diagnostics", "tests" ],
    "ignoreBaselineDiagnostics": true,
    "testScope": "affected"
  }
}
```

`validation:stages` controls the authoritative post-approval validation gate, not the automatic pre-mutation Roslyn proposal screening. Stages run in order and a failing stage blocks later stages. Supported stage names are:

- `semantic` — fast in-process semantic diagnostics from the loaded workspace without launching a build;
- `compile` — affected project compilation through direct `dotnet build --no-restore`;
- `diagnostics` — normalized compiler diagnostic classification and mutation correlation;
- `tests` — affected test discovery and execution.

Semantic activity is visible in the interactive transcript as `SEMANTIC CHECKS` rows with elapsed time when duration display is enabled. Pre-mutation rows cover overlay syntax and compilation checks; semantic-only validation rows cover baseline and post-mutation diagnostics. Details are bounded host-authored summaries such as file/project counts, diagnostic counts, blocking diagnostics, omissions, and completion state. If post-apply validation is configured without the `semantic` stage, the TUI shows a `MUTATION: Validating applied mutation` lifecycle block so compile/diagnostics/test waits are not silent.

A repository may narrow `validation:stages`, for example to `[ "semantic" ]` for a fast local experiment, but doing so reduces the authoritative gate. Use this only when another trusted process supplies the missing build/test assurance. Pre-mutation screening remains active for `.cs` proposals where possible even when post-mutation stages are narrowed.

Mutation-related controls:

```json
{
  "planning": {
    "approvalPolicy": "reviewAll"
  },
  "mutation": {
    "approvalPolicy": "reviewAll",
    "largeDiffThreshold": 500
  },
  "execution": {
    "maxCorrectiveTurns": 3,
    "maxModelRounds": 0,
    "maxPlanningToolRounds": 0,
    "maxStructuredOutputCharacters": 8388608
  },
  "formatting": {
    "style": "editorconfig",
    "applyOnMutation": true
  }
}
```

- `planning:approvalPolicy` sets plan approval behavior; `/plan-policy` saves every choice except `TrustSession` only in the active repository’s `.threadsmith/config.json`, including `AlwaysTrustRepo`. No user-side trust record is needed.
- `TrustSession` for either approval command applies in memory only and leaves the saved repository policy untouched. Restarting or switching back to the repository restores its configured policy.
- `mutation:approvalPolicy` sets exact-diff mutation approval behavior; `/policy` saves `ReviewAll`, `ReviewRisky`, `TrustPlan`, and `AlwaysTrustRepo` only in the active repository’s `.threadsmith/config.json`.
- `mutation:largeDiffThreshold` controls when `ReviewRisky` treats an exact diff as large.
- `execution:maxCorrectiveTurns` bounds active-turn correction attempts for recoverable malformed or invalid model-authored requests, including malformed `propose_plan` arguments, invalid pre-execution tool batches, repairable plan revisions, mutation-proposal retries, and post-validation correction attempts.
- `execution:maxModelRounds` optionally bounds total model continuation rounds for a request, and `0` disables that separate cutoff; `execution:maxPlanningToolRounds` optionally bounds the initial planning rounds that advertise inspection tools before only `propose_plan` remains, and `0` disables that separate cutoff so exploration can use the full model-round budget; `execution:maxStructuredOutputCharacters` bounds mutation proposal output when positive, and `0` disables that configured output cap.
- `formatting:applyOnMutation` controls configured formatting around proposed mutations where formatting support is available; formatting does not bypass exact diff review.

### Plan approval policies

`/plan-policy` opens a numbered selector; `/plan-policy current` reports the active choice, `/plan-policy reset` saves `ReviewAll` in repository configuration, and `/plan-policy <name>` selects directly:

| Policy | Behavior |
|---|---|
| `ReviewAll` | Default. Prompt for every valid sanity-checked plan. Persisted in repository settings when selected through `/plan-policy`. |
| `ReviewRisky` | Auto-approve low-risk valid plans; prompt for moderate or high risk. Persisted in repository settings when selected through `/plan-policy`. |
| `TrustSession` | Auto-approve low- and moderate-risk valid plans for the current process session. Session-only; does not rewrite repository settings. |
| `AlwaysTrustRepo` | Persistently auto-approve low- and moderate-risk valid plans for this repository. Saved only in repository configuration. |
| `AutoApproveAllValid` | Strongest explicit mode. Auto-approve every valid non-blocked plan after sanity checks, subject to repository trust and hard guardrails. Persisted in repository settings when selected through `/plan-policy`. |

Plan policy is distinct from mutation policy. Auto-approved plans still appear in the transcript as `PLAN: auto-approved`, remain durable structured contracts, and only allow the implementation proposal phase to start. They do not approve exact staged diffs, writes, process execution, validation results, commits, pushes, or external-system effects.

### Mutation approval policies

`/policy` opens a numbered selector; `/policy current` reports the active choice, and `/policy <name>` selects directly. Every choice except `TrustSession` is saved only in repository configuration:

| Policy | Behavior |
|---|---|
| `ReviewAll` | Default. Prompt for every staged mutation set. |
| `ReviewRisky` | Auto-apply ordinary edits; prompt for moves, deletions, configuration or dependency changes, diffs over `mutation:largeDiffThreshold`, and invalid/outside-repository targets. |
| `TrustPlan` | Auto-apply only mutations contained by the accepted plan's declared files. Scope expansion still requires review. |
| `TrustSession` | Auto-apply valid in-repository mutations until the process session ends. Leaves the saved repository policy untouched. |
| `AlwaysTrustRepo` | Auto-apply valid in-repository mutations and save `mutation.approvalPolicy` only in this repository. |

Trust-based choices print a warning. Every policy still requires `TrustedMutation`, preserves the exact diff in events/projections, validates baseline hashes and approved roots, rejects prohibited/secret-bearing paths and `.git` metadata, runs configured validation, and never commits, pushes, resets, cleans, or otherwise performs destructive Git operations.

Optional Git-worktree isolation may be used by configured workflows, but Threadsmith does not treat Git as a transaction mechanism and does not perform destructive Git operations.

### Structured file lifecycle mutations

File creation, deletion, and movement are explicit versioned mutation operations, not ordinary tools or inferred shell commands. They are advertised only through `propose_mutations` during eligible implementation/correction phases and retain the same accepted-plan, exact-diff, approval, transaction, validation, and resume authority as text edits.

- **Create** requires an absent path and bounded text content. The proposal may select UTF-8 with or without BOM and LF or CRLF newlines.
- **Delete** requires the exact baseline SHA-256 and byte count. Baseline bytes remain privately available for compensation and rollback; deleted content is not logged.
- **Move** requires an exact source identity and an absent destination. Both endpoints must be declared by the accepted plan and worker assignment. A content descriptor makes move-plus-edit explicit; no namespace, project, or reference rewrite is inferred.
- **Case-only move** is represented explicitly and committed through the same private temporary-file transaction so case-insensitive filesystems do not lose or duplicate the file.
- **Project inclusion** is metadata only. Threadsmith never hides a project-file edit; an explicit project change must be part of the reviewed mutation set.

The aggregate preview always includes exact add/delete source and destination diffs and exposes lifecycle kind, risk, destination, and case-only status. `ReviewRisky` prompts for every move or delete and for project-system lifecycle changes. Commit detects the repository filesystem's casing behavior, removes baseline identities before publishing final identities, attempts every compensation cleanup/restore effect without honoring caller cancellation, and verifies final hashes before reporting `Applied`; incomplete compensation is aggregated and fails closed. Recovery distinguishes `NotStarted`, `Applied`, `Compensated`, `Conflicted`, and `Indeterminate`; ambiguous state fails closed rather than replaying the move. Rollback refuses to overwrite any file changed after commit.

Directory/glob operations, directory-tree deletion/movement, links, alternate streams, permissions, implicit project/namespace rewrites, overwrite moves, and Git staging/commit/move remain unsupported. Outside-root, traversal, `.git`, secret, prohibited, and reparse-point paths are denied under every policy.

### Cancellation, checkpoints, and resume

Execution writes versioned checkpoints at safe phase boundaries and write-ahead mutation-commit intent before repository effects. Cancellation before application leaves repository bytes unchanged. Interrupted builds/tests do not admit late output as authoritative. Explicit `ResumeRunCommand` uses the same host boundary for interactive and headless adapters. Resume fails closed for terminal runs, session/checkpoint identity mismatch, missing or corrupt continuation artifacts, unsupported schema, or an unresolved pending side effect; normal workspace baseline and external-change guards still apply before any later mutation commit. See [execution resumption operations](operations/execution-resumption.md).

### Parallel agents and isolated workers

Threadsmith starts subagents only when the model invokes `delegate_agents`. Plan approval, mutation preparation, corrections, preflight, and execution resume never launch subagents automatically. Normal implementation stays under the parent run and session model settings, with existing approval and validation controls. Requested subagents can explore code, suggest implementation approaches, or provide independent security, test, performance, and architecture reviews; all conversation-delegated roles are read-only. There is one delegation layer: a subagent cannot start another subagent. Child agents are asynchronous runs inside the Threadsmith process; they are never separate agent executables. Existing Git, build, test, MCP, and authorized tool processes remain tracked infrastructure and do not host an agent.

During an ordinary trusted conversation with an open semantic workspace, the model can call `delegate_agents` to fork one to three assignments by default and wait for their joined result. The tool is advertised only when a configured model can satisfy the actual child request's capability and capacity requirements; sensitive assignments repeat selection with the frozen sensitivity policy. Trusted machine/user configuration controls child admission. Each child request contains `task`, `context`, `toolAccess`, and an optional `role`:

```json
{
  "agents": [
    {
      "task": "Trace how child assignments are admitted and joined.",
      "context": "Focus on the execution scheduler and cite current repository files.",
      "toolAccess": "readOnly"
    },
    {
      "task": "Review cancellation and checkpoint behavior.",
      "context": "Identify terminal outcomes and any explicit omissions.",
      "role": "testReviewer",
      "toolAccess": "inherit"
    }
  ]
}
```

Independent assignments in one `agents` array can run concurrently, subject to configured scheduler concurrency. Separate `delegate_agents` calls run sequentially, even when the model requests them in the same response.

#### Subagent roles

Ask for subagents in ordinary conversation, for example: "Have an explorer trace cancellation, a test reviewer check its coverage, and a security reviewer inspect file access." Give each one a distinct question and the relevant files, symbols, context, and constraints. The parent model prepares the delegation request; you do not need to write the JSON yourself.

All six roles below are available to ordinary conversation delegation. The role names are exact and case-sensitive; `explorer` is the default when `role` is omitted. Unknown roles, different capitalization, and unknown request fields are rejected.

| Role | What it is for and can do | What it cannot do in ordinary delegation |
|---|---|---|
| `explorer` | Find relevant code, trace behavior and relationships, explain contracts, and answer focused questions using accessible files and evidence. Useful for learning how an unfamiliar part of the repository works. | Cannot edit the code, execute it to confirm behavior, or inspect files outside its permitted access. Source inspection is not proof of a successful runtime test. |
| `implementer` | Work out a requested change using the repository's existing patterns. Can suggest an implementation approach, code or a diff, affected files, and validation steps. | Cannot save changes to files, apply a suggested diff, run validation, approve changes, or start a worktree worker just because this role was selected. Actual implementation follows the separate approved-change workflow below. |
| `securityReviewer` | Inspect authorization, injection risks, secret handling, data exposure, and file or network access. Can explain supported risks and suggest mitigations. | Cannot run active security tests, change security settings, publish a review, or approve a change. Its review is not a guarantee that the code is secure. |
| `testReviewer` | Compare implementation behavior with existing tests. Can identify missing cases, weak assertions, and useful test improvements, or explain why inspected coverage looks sufficient. | Cannot run tests, change test files, or establish that tests passed merely by reading them. Suggested tests are proposals, not completed validation. |
| `performanceReviewer` | Inspect repeated work, allocations, I/O, concurrency waits, and resource lifetime. Can suggest improvements and measurements that would confirm a suspected problem. | Cannot run benchmarks or profilers, apply optimizations, or establish measured timings from source inspection alone. |
| `architectureReviewer` | Compare code and dependencies with applicable `AGENTS.md` instructions and architecture documents. Can review module responsibilities, dependency direction, composition, and public contracts, distinguishing documented conflicts from design preferences. | Cannot restructure the code, change architectural decisions, or approve or merge a change. Its recommendations do not create new repository rules. |

These specialties steer the model through a system-prompt amendment, together with the selected model and available tools. They are not required checklists or answer templates. Reviewers can report that they found no supported concern, ask a question, or explain uncertainty. Any role may return prose, Markdown, code, JSON, whitespace, or an empty reply; no role fields, citation GUIDs, or response-format repair are required. Replies remain advisory, and a completed run does not certify that its answer is useful or correct.

#### Tools, responses, and model selection

`readOnly` admits only approval-free, non-network read tools that were visible to the parent request. `inherit` starts from the parent's exact currently eligible read-only surface and may retain eligible network-backed read tools, but both modes remove mutation, process/code-execution, approval-required, workflow-transition, and delegation tools for every role. Every retained call remains narrowed by child trust, path, phase, per-tool bounds, network, and sensitivity policy, so children cannot approve, mutate, run commands, change host state, or create descendants. Model-supplied child context is untrusted data and cannot widen those boundaries. A proposed validation plan does not mean that tests ran.

These restrictions apply to every ordinary role, not just reviewers. A role does not unlock extra tools, and `inherit` does not grant the parent's write or command-execution permissions. A child cannot choose a different model, enable tools, expand its access, or change its assignment. Model choices for all six roles come from the configuration described below, not from the child's reply.

The parent waits asynchronously at the tool-call boundary while children run concurrently through the existing scheduler. The joined result carries child responses separately from host-owned delegation and assignment IDs, roles, statuses, model-selection details, usage, and any envelope omissions. Child transcripts, hidden reasoning, and provider transport payloads do not cross the join. The host stores the joined checkpoint before exposing joined responses, and monotonically revisioned persistence rejects stale progress writes that arrive after terminal state. A present response, even empty, can complete; status describes transport and join mechanics, not answer quality. Failed or cancelled transport remains unsuccessful. Successful siblings remain in a `Partial` result when another child fails.

The ordinary body is stored in `AgentRunOutcome.Response`. It is not parsed into findings, semantically graded, or promoted to verified parent evidence. JSON-looking replies are still ordinary responses. Legacy structured outcomes and checkpoints remain supported separately; an empty response is distinct from a missing/null legacy response.

Trusted `agents:delegation:childBudget:wallTime` sets the child deadline (five minutes by default). Zero disables it, as does `agents:delegation:enforceOperationalLimits: false`. The delegation tool adds no separate timer; caller cancellation and provider settings still apply. Delegation count, task/context length, child-output, tool-argument, tool-call, correction-text, joined-output, structured-result, model-projection, detail, omission, and prepared-validation limits all use the same convention: positive values enforce that cap; zero disables that specific cap, and `enforceOperationalLimits: false` disables the configurable delegation caps together. Disabled count and length caps are omitted from the advertised `delegate_agents` schema instead of being replaced by compiled defaults. `maximumSummaryCharacters` controls fallback summaries for failed, cancelled, or legacy outcomes; zero or disabled operational limits preserves those summaries without clipping. Ordinary final responses are not clipped by that summary setting.

Every child has a frozen role, objective, tasks, stopping condition, contract marker and runner version, baseline, scope, model and reasoning selection, tool allow/deny set, trust ceiling, sensitivity, deadline, dependencies, and hierarchical budget. The ordinary `agent-response/1` marker imposes no body shape. Read-only children cannot receive mutation tools or mutation trust. Children receive governed evidence and applicable instructions. The host checks identity, generation, authority, cancellation, and real model capacity independently of final-response contents.

Trusted user/machine `agents:roleModels` configuration may select an existing provider, profile GUID, and optional reasoning level for each role. An application assignment pin takes precedence, then the role mapping, inherited preference, and compatible default. Each actual request is checked again; a compatible fallback and its reason remain visible. Assignments, checkpoints, and outcomes retain configured and effective provider/profile/reasoning and the selection source. Repository settings cannot change these trusted routes, including through a same-ID provider endpoint override. Changes require restart; the TUI does not edit role mappings. Legacy `agents:roleProfiles` is rejected in trusted configuration. See [models for delegated roles](operations/model-providers.md#models-for-delegated-roles).

Role keys and field names are case-sensitive; provider IDs and supported reasoning names are case-insensitive. Invalid role configuration stops startup. Only a selection from a trusted role mapping uses the repository-excluding catalog and credentials with user-owned or higher authority. Application pins, inherited preferences, and defaults use ordinary model routing, including its normal repository settings and eligible secret sources. Choosing `toolAccess: inherit` does not change model routing or trust.

#### Approved implementation and isolated workers

The approved-plan workflow is separate from an ordinary `implementer` assignment. When models are configured, implementation and correction turns use an Implementer child to prepare a mutation proposal for the accepted plan. Threadsmith validates that proposal, and the parent stages it only after the child result has been saved and joined. The existing exact-diff approval, transactional application, validation, and correction steps still apply; the child cannot independently approve or apply changes. Failed or cancelled preparation does not stage changes. This path does not automatically apply parallel worktree changes. With no configured models, the deterministic offline flow keeps the direct mutation proposal path.

The existing isolated-worker APIs require an approved plan and host-proven non-overlapping ownership. Ambiguous paths, directories, symbols, projects, generated outputs, solution files, central package/build configuration, or other shared surfaces fall back to serial execution. An authorized worktree worker uses a detached worktree under the host-managed temporary root and the normal mutation proposal, exact-diff, approval, transaction, validation, correction, and cancellation gates. A worktree isolates file state; it is not a security sandbox.

Worker results are frozen structured change sets, not branches to merge. Before selected changes enter the primary worktree, Threadsmith rejects incomplete or stale packages, out-of-scope paths, worker overlap, and changed parent baselines. The parent converts and restages selected changes through the existing transactional workspace, presents one fresh aggregate diff under the current mutation policy, and reruns aggregate affected builds/tests. Threadsmith does not merge, commit, rebase, cherry-pick, push, or resolve conflicts automatically.

#### Subagent history and evidence

Each ordinary subagent keeps its own conversation. Its role prompt, task, supplied context, repository instructions, and later steering stay intact. Inherited evidence is delivered in full initially, then becomes eligible for summarization alongside older completed tool exchanges. The original evidence stays in the evidence store and remains available through `read_agent_evidence` when that tool is permitted. Calls and results are removed together, and at least the latest complete exchange stays verbatim. This never prescribes the format of the final answer or shares sibling histories.

Children with tool access also receive `read_agent_evidence`, a read-only lookup of results already delivered to that child. Working notes associate useful evidence IDs with findings; an `archivedEvidenceIds` index keeps the older IDs available even if a summary omits one. The lookup returns the stored sanitized result, not a fresh repository read. It cannot access another child's results, undisclosed session evidence, missing results, or stale evidence. It cannot run commands, modify files, or enlarge file/network permissions. Retrieved evidence does not create a duplicate stored result.

Structured tool results stay valid JSON when secrets are redacted, both in the child's request and in stored evidence. Redaction operates on JSON values instead of treating the enclosing document as plain text. Plain-text results remain text. Children currently return fresh `code_explore` source rather than replacing it with source back-references.

By default, subagents use the same 75% input-pressure trigger as the main loop, with no absolute token trigger. Existing explicit trigger overrides still apply; remove them or use the values below to adopt the defaults.

Configure this independently of task limits in trusted user/machine `agents:delegation:compaction` settings, then restart. Repository configuration cannot change it:

```json
{
  "agents": {
    "delegation": {
      "compaction": {
        "enabled": true,
        "triggerTokens": 0,
        "triggerPercent": 75,
        "targetTokens": 20000,
        "recentTokens": 12000,
        "minimumRoundsBetweenAttempts": 3,
        "minimumSavingsTokens": 2000,
        "summary": {
          "summaryBudgetTokens": 3000
        }
      }
    }
  }
}
```

Either trigger can start an attempt. `triggerPercent` uses the selected model's context window minus its output reserve. Set either trigger to `0` to disable it, or `enabled: false` to disable compaction altogether. `recentTokens` determines the recent raw history retained in complete exchanges; `recentTokens: 0` retains only the newest exchange. Fixed instructions and the summary allowance do not subtract from this setting. `targetTokens` remains an advisory total-size target reported in compaction diagnostics, not a retention limit; `0` leaves that advisory target unspecified. Zero spacing allows attempts at every boundary, and zero minimum savings still requires a smaller request. The default spacing is three rounds, including after failure. These are estimated-token tuning values, not task limits or guaranteed request sizes. For example, 17,000 tokens of fixed context, a 3,000-token summary, and 12,000 tokens of recent history require about 32,000 tokens, plus metadata and whole-exchange rounding, even with a 20,000-token advisory target. The selected model's actual input capacity is still checked before a request is sent.

The same summary generator used for the parent conversation uses the global `context:activeTurnCompaction:profileId` and optional `reasoningLevel` described above. When no global profile is set, it uses the child's selected model and reasoning. Compaction never changes the model assigned to the child's task. The generator receives the complete original assignment message, including supplied context and explicit tasks, alongside the previous summary and selected older exchanges. Assignment context counts toward the compaction model's input capacity and is not clipped to the short objective's character allowance. If it cannot fit with the oldest complete exchange, the attempt is skipped and original history remains active.

Its configurable `compaction:summary` settings use `ActiveTurnCompactionPolicy`; child defaults set `summaryBudgetTokens` to 3000 and allow one provider call with no extra retry. The allowance requests 2400 output tokens by default, subject to the model's supported controls. It is not a character limit or an exact estimated-token limit on completed summaries. Empty, incomplete, invalid, unsuccessful, or insufficiently smaller candidates leave the original history active. The prefix stays unchanged between replacements. Reported summary usage, including usage received before a later failure, is included in session and child token totals; diagnostic activities record before/after estimates and the outcome. If an attempt ends before the provider sends usage, it is recorded as missing, not zero. Provider stream controls and request-capacity checks still apply even with compaction disabled.

Smaller requests are not necessarily cheaper or faster. Summaries add model calls and output, and replacing history can reduce cache reuse. Compare total input, cached and uncached input, output, elapsed time, and answer usefulness with compaction enabled and disabled. Include failed summary attempts; totals with missing usage are lower bounds, not exact savings.

Child summaries default `compaction:summary:maximumInputTokens` to `0`, meaning no additional input cap beyond the selected model's capacity. A positive value adds an optional cap. A cap too small for the oldest complete exchange can prevent compaction, so it should not be confused with the trigger or target. The parent's separate default remains 65,536 tokens. No setting can exceed the selected model's actual capacity.

#### Inspecting and cancelling subagents

Accepted children appear as person/role tabs in TUIKit. MAIN shows each child’s current status inside the live `delegate_agents` tool block, then retains final rows below its completion timer. The TUI omits automatic delegation-GUID and inspection-hint messages. Use bare `/agents` for a bounded, active-first list of delegations observed in the current interactive session; assignment IDs appear as child lifecycle events arrive. The list is a convenience index rather than durable history. Use `/agents <delegation-id>` to inspect the latest durable checkpoint. `/agents <delegation-id> cancel` requests hierarchical delegation cancellation; `/agents <delegation-id> cancel-child <assignment-id>` cancels one child and policy-declared dependents. The detailed display contains stable IDs, phase, generation, role, terminal status, effective provider/profile/reasoning, selection source and fallback reason, bounded usage, lifecycle reason, and next legal action. Final child replies return through the joined result, not as interleaved child transcripts or hidden reasoning. Persisted assignments also retain the contract marker, runner version, and configured model preference. These records support inspection; there is no automatic resume API for an interrupted delegated model loop. Further delegation starts in a new generation. Approved-plan execution retains its separate [resume lifecycle](operations/execution-resumption.md).

Headless callers use `GetDelegationCommand`, `CancelDelegationCommand`, and `CancelAgentAssignmentCommand` through the same dispatcher to inspect or cancel model-requested delegations. There is no direct headless delegation-start command. Configure scheduler admission under `agents` as documented in `.threadsmith/config.example` and ordinary delegation through trusted machine/user `agents:delegation` settings. Existing finite defaults and active request, tool, transport, and result-envelope controls remain distinct from response-format freedom. See [parallel-agent operations](operations/parallel-agents.md) and [`delegate_agents` under the hood](architecture/delegate-agents-tool.md).

During an active conversation on MAIN, TUIKit shows `ENTER to steer; ESC-ESC to cancel` after its activity timer at the bottom of the output pane. The hint disappears when the turn ends or pauses and is not added to the transcript. Return from a child tab to MAIN before steering. Enter creates one idempotent request and immediately acknowledges that Threadsmith is waiting for the current model/tool boundary. Pressing Enter again while the request is pending has no additional effect.

Threadsmith finishes the in-flight provider response or tool batch before opening the frontend’s steering composer as `steer >`. During a delegation, every still-running child first pauses before its next provider request or becomes terminal. The parent run remains paused while the composer is displayed, so further tool/model output cannot scroll it away. Submitted text becomes sanitized lower-authority user context for the parent and eligible children; empty/cancel resumes unchanged. Bare `/agents` can recover delegation and assignment IDs from the steering prompt before a detailed inspection or cancellation command. Completed children are not reopened and are counted as undelivered in the joined steering summary.

Press unmodified Escape twice within 850 ms to cooperatively cancel the active run. `Ctrl+C` remains supported. An in-flight provider or tool must still observe cancellation normally; neither shortcut fabricates mid-operation suspension. On MAIN, ordinary non-hot-key typing and multi-key paste bursts received during an active run are buffered for the next composer. This differs from startup splash input, which is discarded in TUIKit; child tabs also reject composer input.

## Tools and tool availability

### Availability versus invocation policy

Tool availability determines whether a tool is advertised to the model and resolvable for invocation. Invocation policy separately determines whether an available invocation is allowed, denied, or requires approval.

Repository availability uses:

```json
{
  "tools": {
    "enabled": [ "git_status", "find_references" ],
    "disabled": [ "find_implementations" ]
  }
}
```

When `enabled` is present, it is an allowlist for non-essential tools. `disabled` wins when an ID appears in both lists. Essential inspection and validation tools cannot be disabled.

Independent sibling tool calls may run concurrently after the complete model response is collected. The host derives confined resource claims from validated arguments and current repository/workspace/request state; the model cannot declare safety or choose a wave. Read/read compiled built-ins overlap only when their reviewed scheduling descriptor permits it. Approval, code/process, workflow, semantic-workspace, MCP, extension, unknown, and conflicting resource calls remain sequential. Configure `tools:parallel:enabled`, `maximumConcurrency` (1–16), and `failureMode`; see [Parallel tool execution](operations/parallel-tools.md) for metadata and scheduling details.

Use `/tools` to view enabled state, stable ID, category, source, and essential status. Selecting a non-essential entry persists the change immediately while preserving unrelated configuration. Persistence errors are reported in the terminal without ending the shell.

Loaded extension tools appear in the same catalog. Unloading an extension removes its active tools without deleting their saved preferences.

### Invocation policy

These settings narrow runtime use independently of availability:

- `tools:allow` and `tools:deny`;
- `tools:requireApproval`;
- `tools:allowedExecutables`;
- `tools:allowedNetworkHosts`.

`run_process` exposes a general `command` plus optional `timeoutSeconds`, executes through `tools:runProcess:shellExecutable`, and is available during ordinary conversation only when that bare shell name appears in `tools:allowedExecutables`. The shell runs in the repository root and supports its normal composition language, including pipelines. Allowing a shell therefore also permits nested commands launched by that shell; the allowlist governs the outer shell boundary, not tokens inside a command. Threadsmith resolves the shell only from absolute host `PATH` entries, never from the repository working directory. Cancellation terminates the complete tracked process tree. Process calls require approval by default and are withheld from ordinary conversation because that pipeline cannot prompt interactively. To advertise and permit unattended shell calls, set `tools:runProcess:requireApproval` to `false` in machine or user configuration. Repository configuration cannot grant that exemption, but it can reimpose approval by including `run_process` in `tools:requireApproval`; because ordinary conversation cannot prompt, that withholds the tool. Results retain bounded stdout and stderr.

### Built-in tools

For `list_files` and `search`, the optional `path` is relative to the repository. Omitting it, passing `null`, an empty or whitespace-only string, or `"."` selects the repository root. This works the same way for the main agent and subagents, without granting access outside their approved roots or to prohibited files. `read_file` still needs a nonblank file path, and `search` still needs a nonblank query.

The catalog includes repository listing/reading/search, typed local Git inspection, normalized .NET inventory, semantic symbol/reference/implementation discovery, controlled process execution, current date/time, and other host-governed capabilities. Recursive repository listing and text search skip prohibited/reparse-point descendants; installed releases include a RID-matched ripgrep executable and use it through a bounded `rg` fast path for whole-repository literal text searches, respecting repository ignore files while including relevant hidden files. Source-development launches prefer the same app-local `tools/rg(.exe)` layout and may use an `rg` found on `PATH` when no staged payload exists. Regex searches, narrowed globs, configured prohibited-path boundaries, unavailable ripgrep, or a failed native invocation use the confined managed scanner. Search prunes `.git`, `bin`, `obj`, SQLite databases (including `.threadsmith/threadsmith.db`), and oversized files as applicable; files that become locked, inaccessible, or unavailable are skipped without aborting the managed scan. Results and native output remain bounded. On Windows these tools also skip reserved DOS device-name entries such as `nul` so one unopenable path cannot abort the remaining inspection. Use `code_explore` when natural-language C# questions, exact C# symbols, stable symbol IDs, or repository-relative C# paths should return current source or safe current-context back-references, compiler-proven flow among named anchors, dispatch branches, or compact impact context; use granular semantic tools for exact follow-up; use `search` for exact text and regular expressions.

The typed Git tools are `git_diff`, `git_log`, `git_show`, `git_blame`, and `git_compare_branches`. They accept closed modes and validated revision tokens, treat path filters as literal repository-relative data after `--`, preserve unusual filenames through NUL-delimited normalization, classify blobs before text decoding, disable pagers/color/external diff and text-conversion behavior, perform no remote access, and truthfully report bounded commits, paths, lines, patches, bytes, and execution time. `git_diff` supports working-tree, staged, root/ordinary commit, direct-range, and merge-base comparisons. Branch comparison reports its merge base, ahead/behind counts, and normalized changed paths. Git is evaluated by executable policy, and recursive evidence omits descendants outside approved roots or matching prohibited paths.

`dotnet_inventory` projects the authoritative selected loaded semantic workspace into deterministic solution/project, TFM, project-reference, package-reference, central-version-source, and test-project results. Caller-supplied path text cannot replace selected-solution provenance. It reports semantic confidence and omissions; degraded or absent workspace state is not presented as complete evaluation. Every selected solution, loaded project, and `Directory.Packages.props` metadata access is confined by approved-root, prohibited-path, and reparse-point policy before bounded reads; inventory never restores packages.

All typed Git tools require `TrustedRead`, use the central availability/invocation policy and evidence pipeline, and return the same normalized JSON through interactive and headless model turns.

The .NET health and validation catalog includes `nuget_health`, `dotnet_build`, `dotnet_analyzers`, `dotnet_format_check`, `diagnostic_query`, `test_discover`, and `test_run_targeted`. These are distinct typed operations rather than argument routers. They accept only host-defined target, configuration, framework, limit, query, and identity fields; no arbitrary MSBuild property, logger, response file, adapter, runsettings, environment, command, or filter expression is accepted.

`nuget_health` reads bounded existing `obj/project.assets.json` data to distinguish direct and transitive resolved dependencies without restoring. Offline results report asset freshness, completeness, and omissions. Configured-source mode runs separate bounded vulnerable, deprecated, and outdated queries against HTTPS sources supplied only by trusted machine/user configuration; source hosts must also pass invocation network policy. Optional private sources pair a bounded source name and username with a logical `secrets:` reference. Private-source credentials require `UserOwned` source trust, so repository values are ineligible. The value is resolved only at the final process boundary into the NuGet child environment; the generated temporary NuGet configuration contains source names/URIs but no credential and is deleted after use. Credentials never enter arguments, normalized results, or provenance. The tool never adds, removes, updates, restores, or writes package state.

`dotnet_build` and `dotnet_analyzers` require `TrustedBuild`, execute through the tracked process manager, use closed Debug/Release and validated TFM scopes, always pass `--no-restore`, and normalize diagnostics. `dotnet_format_check` uses `dotnet format --verify-no-changes --no-restore`; it reports drift but never applies formatting. Applying formatting remains a normal approved transactional mutation with exact-diff review.

`diagnostic_query` pages the bounded process-local exploratory index by invocation/run, project, file, code, severity, compiler/analyzer origin, and baseline class. `test_discover` enumerates one confined supported test project without restore/build, derives host-issued repository-bound identities, and can narrow results by exact namespace, class, method, or available trait metadata. `test_run_targeted` accepts only one unexpired issued identity, rechecks its project against current path policy, and generates an exact effective filter. Unknown, cross-repository, expired, or ambiguous identities fail closed.

Every result from these exploratory .NET tools is labeled `Exploratory`. These tools cannot replace, overwrite, or satisfy authoritative baseline, affected-project, test-selection, acceptance, or correction evidence. Process cancellation kills the tracked tree; output, dependencies, advisories, diagnostics, tests, time, and pagination are bounded. Interactive and headless turns use the same registry and normalized results.

#### `code_explore`: task-sufficient C# exploration

Use `code_explore` when a question is primarily about **how loaded C# code is structured or behaves** and the answer needs current source, semantic identity, flow, impact, or nearby prompt/configuration context. It is the high-level semantic exploration tool: it often replaces a manual sequence of `find_symbol` → `find_references`/`call_hierarchy` → `read_file` → `search` for ordinary code-understanding turns.

For the host-side declaration catalog, retrieval, ranking, graph, source-allocation, continuation, and output-fitting design, see [`docs/architecture/code-explore-tool.md`](architecture/code-explore-tool.md).

Good fits include:

- “How does this request reach the response builder?”
- “Show the source for this exact type, overload, or stable symbol ID.”
- “What declaration is at `src/Foo.cs` line 42?”
- “How do these two named methods connect?”
- “What callers/projects/tests look affected if this method changes?”
- “This C# method loads a prompt/config/resource; include the checked-in artifact that explains the behavior.”
- “I know the feature words but not the exact symbol names; find the likely compiler-known declarations.”

`code_explore` requires an opened repository at `TrustedBuild` with a semantic workspace loaded to at least partial compilation. Headless repository requests wait for semantic readiness before submitting a model request and fail closed instead of advertising unusable semantic tools when readiness is too low.

The model-facing schema is intentionally strict and minimal: required `query` plus optional `maxFiles`. Unknown fields are rejected. Users and models do not pass separate mode, path-anchor, artifact-anchor, traversal-depth, graph-size, source-limit, artifact-limit, timeout, digest, workspace-generation, or cursor fields; the host owns those controls.

Use the `query` text for:

- **Natural-language C# questions** — ordinary words are tokenized into bounded identifiers, qualified names, path-like spans, and ranking terms.
- **Exact C# symbols** — simple names, qualified names, overload-like signatures, or stable symbol IDs returned by earlier semantic tools.
- **Repository-relative `.cs` paths** — include the path, and optionally a line or line-range description, in the query text rather than as a separate argument.
- **Focused code terms** — feature words, type/member names, or nearby phrases when the exact symbol is unknown.
- **Host-issued continuation cursors** — paste the entire `code_explore:continue:...` value from a Markdown follow-up target as the next `query` to replay exact source, artifact, or impact continuation identity.

When a question names C# files, `code_explore` resolves those files individually instead of substituting loosely related declarations. A bare filename can identify a unique file in the loaded workspace; duplicate names return path alternatives. A missing name is a coverage gap, not proof that the file is absent from the repository. Use file listing to locate it, then read it directly or provide its repository-relative path. An exact path outside the loaded project can return permitted source without claiming compiler-backed symbol identity.


Configure operational limits under `tools:codeExplore` and restart. Positive values enforce a cap; zero disables it; negatives are rejected. `enforceOperationalLimits: false` disables these tool caps while preserving actual model capacity, caller cancellation, path policy and source identity checks. Main agents and subagents use the same settings with their own visibility and model capacity. An omitted/nonpositive `maxFiles` hint uses the configured default; a positive hint narrows the enabled file cap.

The `limits` object controls file/anchor counts, source and artifact allowances, flow/impact counts and the semantic query timeout. `outerTimeoutMilliseconds` controls the central tool timer; zero disables either timer without disabling cancellation. `maximumCurrentSourceFileBytes`, `maximumResultBytes`, `maximumMarkdownBytes`, presentation/continuation limits and discovery/inventory settings control their respective downstream consumers. Raising a source allowance does not disable a separately enabled output cap. See the full configuration-to-consumer inventory in the [code exploration implementation guide](architecture/code-explore-tool.md#operational-configuration-and-consumers) and the shipped configuration example.

`adaptiveSizingEnabled: false` keeps configured source/output allowances without repository-scale reductions. When enabled, `tiny`, `small`, `medium`, `large`, and `veryLarge` provide configurable `maximumFiles`, `maximumSourceCharacters`, `maximumPerFileSourceCharacters`, `maximumMarkdownBytes`, `recommendedFollowUpCount`, and `presentationVerbosity` defaults. Zero disables an individual tier cap, and a tier cannot restore a disabled controlling cap. Follow-up counts are display advice, not task limits.

A model-provided `maxFiles` hint narrows the file count without disabling adaptive source defaults. Older versions could accidentally skip source adaptation when the hint differed from eight. To retain the configured base source allowances, set `adaptiveSizingEnabled: false`; operational and model-capacity limits still apply.

Source allocation reuses space left by small, already-visible or unavailable sections and unused source-bearing file slots. A nearby declaration or small file completes when remaining total/per-file space permits. Exact short ranges and useful partial code remain available; incomplete output carries precise continuation ranges. Artifact allowances remain separate.

The host derives the internal exploration emphasis from the query and resolved anchors. Dependency, caller, affected-project, blast-radius, or test-impact wording derives the internal impact path so single-symbol questions such as “what depends on Foo?” return caller/project/test evidence without a model-visible `mode` field. Call-flow evidence appears when the resolved question supports a bounded compiler-proven path; broad tool-capability and structural-survey questions do not receive unsolicited flow or blast-radius sections.

The default model-visible result is concise Markdown. It includes an exploration heading, relevant symbol/file count, blast-radius or call-flow evidence when relevant, grouped line-numbered current source or precise current-context back-references, associated artifacts when useful, bounded artifact completeness/omission notes, a kind-diverse set of follow-up targets with pasteable retry query cursors when truncation occurs, and bounded omissions. Blast-radius Markdown shows returned/total counts plus representative callers, implementations, projects, and tests. Before terminal model bounding, host totals and omission state remain in the structured result, but progressive bounding may remove individual impact items and follow-up targets. The structured result and rendered Markdown share the selected-model byte ceiling; if the final Markdown still exceeds it, Threadsmith closes any open code fence and replaces the remaining tail with an explicit output note. At the 1 KiB terminal envelope, the structured projection may retain only workspace generation, confidence, conservative incomplete coverage, and, when it fits, a bounded top-level omission. Candidate-ranking tables, allocation summaries, adaptive-budget details, file/range SHA-256 digests, workspace generation values, emitted-range records, and other audit metadata remain available through diagnostic/structured projections only while they fit.

Natural-language discovery is deterministic and Roslyn-backed. Query text is inert data: it is never executed, provider-reranked, embedded, or treated as an unbounded repository text search. Ranking favors exact/pinned evidence, qualified names, distinctive identifiers, multi-term/co-located structure, graph connectivity, and explicit generated/test focus over isolated common-word collisions. If the result is ambiguous or incomplete, it reports alternatives, omissions, and continuation targets rather than silently guessing.

`code_explore` can also return **associated non-C# artifacts** as a separate supplement to the C# semantic spine. Automatic discovery keeps relationships proven by selected C# source, including repository-relative literals, logical prompt/configuration names, and bounded exact-name matches. Roslyn additional documents, analyzer configuration documents, loaded project metadata files, and bounded textual project item/resource references are added only when artifact/configuration evidence is explicitly requested or a host-issued artifact continuation enables them. The same physical project artifact is emitted once even when several selected projects load it. Markdown labels returned artifacts with their relationship, evidence strength, origin C# source, content excerpt when available, and deduplicated omission/completeness notes when content is absent or truncated; the authoritative structured result also carries media kind, current digest, line range, completeness, and replay metadata. Raw project-file item text is marked as weaker textual inference when item conditions/imports/removes have not been evaluated. Prompt templates, JSON/YAML configuration, XML/resx/project metadata, Markdown, schemas, and text templates remain untrusted repository data: Threadsmith never executes, evaluates, imports, renders, expands external entities, or grants authority from their contents.

Source and artifact output have independent host-owned limits and may also be clamped by the selected model budget. Overflow is reported through omissions and Markdown follow-up targets; paste a `code_explore:continue:...` retry cursor as the next `query` to replay exact host-owned continuation state. Artifacts are omitted when they are binary-shaped, malformed, oversized, missing, changed during read, outside approved roots, prohibited, reparse/device paths, secret/credential-shaped, Git metadata, build output, generated transient output, unsupported media, or unavailable through bounded inventory. Exact-name artifact lookup uses declared, policy-allowlisted host-owned Git inventory when available, parses NUL-framed records before sanitization, caches repeated directory inventories within the query, and fails closed if inventory cannot be trusted.

For overlapping follow-ups, unchanged complete C# source ranges may be replaced with compact back-references only when the host proves the exact range is still present verbatim in the current canonical model request for the same repository, workspace, generation, path, and digest, and only when the serialized pointer is smaller than the source it replaces. Edits, compaction, different sessions/repositories, short spans, partial output, pointer-larger-than-source cases, policy denial, or uncertainty cause safe re-emission or omission. Artifact range deduplication is intentionally separate and conservative.

Use the granular tools when they are the better fit: `find_symbol` for exact symbol lists, `find_references` or `find_implementations` for focused follow-up, `call_hierarchy` or `symbol_impact` for a standalone graph query, `generated_code_query` for generated-document inventory, and `search`/`read_file` for exact text, known-file inspection, or non-C# files. A direct read or a text search scoped with `path` to a known C# file does not require a preliminary semantic call. Choose relevant ranges when the question is local; a whole-file read remains appropriate when the surrounding implementation matters.

Compiler-backed semantic analysis includes `call_hierarchy`, `symbol_impact`, `csharp_pattern_search`, and `generated_code_query`. All four require an opened `TrustedBuild` semantic workspace, are read-only, run no process/network/build/restore/generator/mutation operation, and execute against one captured workspace generation. If invalidation or reload changes that generation before completion, the late result is discarded rather than projected as current.

`call_hierarchy` accepts a stable symbol ID, optional incoming/outgoing/both direction, and one optional depth hint. Node counts, edge counts, timeouts, and all other traversal limits are host-owned. The default model-visible projection is a compact call list: caller, callee, source call site, direct/static/constructor/interface/virtual/extension/local-function/delegate/unknown dispatch, ambiguity, cycle closure, and bounded omissions. The richer structured result remains host-owned audit data. Dynamic, reflection, and runtime-only targets are omissions; results never claim whole-program completeness.

`symbol_impact` accepts only a stable symbol ID. Traversal depth, node counts, edge counts, and timeouts are host-owned. The default model-visible projection is a deterministic ranked impact list over loaded references, callers, implementations/overrides, dependent projects and test projects, plus generated/linked source classification, with compact reasons and omissions. Runtime effects and diagnostics not present in the loaded snapshot are explicitly not inferred, so impact is planning evidence rather than proof or mutation authorization.

`csharp_pattern_search` uses a flat inert model schema: required shape kind plus optional exact name, containing type, repository-relative path, closed C# modifiers, and exact attribute names. Supported shapes are declaration, type, method, property, field, attribute, invocation, object creation, and member access. Version fields, nested pattern wrappers, capture names, result limits, timeouts, arbitrary source snippets, regex, scripts, callbacks, analyzers, assemblies, and executable predicates are not model-facing schema fields; unsupported modifiers and malformed or oversized names fail closed. The compact model-visible projection lists bounded file/range matches and omissions, while confidence, workspace generation, and richer structured fields remain host-owned audit data.

`generated_code_query` inventories only documents already classified in the loaded workspace through `.g.cs`/`.generated.cs`/`obj` convention or Roslyn source-generator exposure. It reports project, path/name, linked classification, explicit origin (`FileConvention`, `SourceGenerator`, `CompilerOrSdk`, or `Unknown`), and optional bounded source content. Missing generator identity is unknown rather than guessed; document/content limits disclose truncation, and the query never runs a generator implicitly.

All graph and search results carry semantic confidence, workspace generation, provenance, completeness, and omissions. The same tool definitions and normalized JSON are available to interactive and headless model turns.

`datetime` is enabled by default and returns round-trip UTC/local timestamps, local timezone ID, and effective offset.

`csharp_script` is disabled by default and requires `FullyTrustedAutomation`. Use `/trust automation`, enable **C# Script** through `/tools`, then ask the model to invoke `csharp_script` with `kind: expression` and `code: 6 * 7`. Confirm the activity names `csharp_script` and inspect the actual `Success`, `Output`, `Error`, `ExecutionMs`, and `IsTruncated` fields; successful output is `42`. The same running build picks up trust and availability changes without restart. Invocation still requires `dotnet` in `tools:allowedExecutables`; allow/deny and approval policy remain enforced.

It executes each request in a fresh tracked worker process with bounded standard input, output, and time. It rejects directives and file, network, process, environment, reflection, native, dynamic, unsafe, and non-allowlisted namespace access.

These restrictions are defense in depth, not an operating-system sandbox. Enable scripting only for repositories you trust.

Configure it with scalar values:

```json
{
  "tools": {
    "config": {
      "csharp_script": {
        "timeout_ms": "5000",
        "max_output_bytes": "65536",
        "allowed_assemblies": "System.Linq,System.Collections,System.Collections.Generic"
      }
    }
  }
}
```

### Governed web search

`web_search` searches an external index through the compiled Brave Search adapter. It is read-only with respect to the repository, but each invocation sends the query text to an external service. For that reason, the tool is disabled by default and has an additional consent gate beyond ordinary tool availability, repository trust, and invocation policy.

#### Enable, consent, and revoke

In the interactive terminal:

1. Open the repository for which search should be available.
2. Run `/tools` and select **Web Search**.
3. Read the outbound-disclosure prompt and choose **Yes — grant consent and enable**.

Consent is bound to the canonical path of the active repository and stored in user-owned local application data at `Threadsmith/outbound-consent.json`, outside the repository. The stored record contains a hash of the canonical path, not the path or any search query. A checked-in configuration file, repository trust grant, prompt, model call, or extension cannot create consent. Opening the same content through a different canonical path therefore requires a fresh confirmation.

Selecting **Web Search** again in `/tools` disables it and immediately revokes consent for that repository. Missing, malformed, unknown-version, or path-mismatched consent fails closed. A repository may request that the tool be enabled in configuration, but `/tools` continues to report **consent required** and the model cannot see or invoke it until the user confirms.

#### Request and result bounds

The live `web_search` tools block displays the search query. The live `web_fetch` block displays the resolved destination URL, including ordinary query parameters and the final destination after a redirect. Long details use the normal 240-character display limit and an ellipsis; detected credential values remain redacted. This display metadata is not saved in event history, so restored older blocks retain their stored summaries.

The tool accepts one search per invocation using these exact argument names:

| Argument | Accepted value |
|---|---|
| `query` | Required non-empty plain-text string, at most 500 characters and 75 whitespace-delimited words. |
| `maximumResults` | Optional integer from 1 through 20; default 5. |
| `locale` | Optional supported search language, optionally with a two-letter region; examples include `en`, `en-US`, `en-GB`, `fr-CA`, `pt-BR`, `ja-JP`, `zh-Hans`, and `zh-Hant`. |
| `freshnessDays` | Optional integer from 1 through 365; omit for no freshness filter. |

```json
{"query":".NET release notes","maximumResults":5,"locale":"en-US","freshnessDays":7}
```

Omit unused optional fields. Do not use provider parameter names such as `q`, `count`, `search_lang`, or `freshness`, arrays of queries, or a freshness string such as `"7d"`. The advertised schema includes numeric and text bounds, and the deployed description includes this argument contract and example. Empty queries, control characters, unsupported search languages, invalid bounds, and queries detected as containing credentials or other sensitive data are rejected before network access. Rejected raw queries are not retained.

Threadsmith translates locale hints into Brave's supported search-language codes; for example, `en-US` sends language `en` and country `US`. A region that Brave does not support as a country hint leaves the language hint in effect. Chinese region hints select simplified (`zh-CN`) or traditional (`zh-TW`/`zh-HK`) search, while bare `pt`, `zh`, and `no` map to `pt-pt`, `zh-hans`, and `nb`. Freshness windows of 1, 7, 31, and 365 days use Brave's presets; other windows use an invariant UTC date range. The host retains its 500-character bound and enforces the provider's 75-word limit. See the [Brave Web Search API contract](https://api-dashboard.search.brave.com/api-reference/web/search/get).

Threadsmith does not fetch result pages, crawl sites, submit forms, manage cookies, or provide authenticated browsing. It accepts only normalized HTTPS result URLs and returns bounded titles, snippets, rank, provider ID, retrieval time, and query provenance. Markup and control characters are removed. Titles and snippets enter model context as **untrusted external evidence**: they cannot override host policy, grant approval, invoke another tool, or authorize a repository change.

#### Configuration locations

Credential-bearing web-search provider settings and the network-host allowlist come only from the repository-excluding trusted view:

- machine-wide: `%ProgramData%/Threadsmith/config.json`;
- user-wide: `~/.threadsmith/config.json`;
- exact `THREADSMITH_` environment variables for ordinary configuration keys.

Repository and session configuration may narrow tool availability and policy, but cannot replace the Brave endpoint/reference, grant its network-host allowlist, or create outbound consent. `--set:` participates in ordinary effective configuration but is intentionally excluded from this credential-bearing trusted view. Never put the Brave API key in any ordinary configuration file or command argument; provide it through the separate static-secret resolver described below.

A complete provider and tool-policy example is:

```json
{
  "tools": {
    "enabled": [ "web_search" ],
    "allow": [ "web_search" ],
    "allowedNetworkHosts": [ "api.search.brave.com" ]
  },
  "webSearch": {
    "provider": {
      "id": "brave",
      "kind": "brave",
      "endpoint": "https://api.search.brave.com/res/v1/web/search",
      "secretReference": "secrets:BRAVE_SEARCH_API_KEY",
      "timeoutSeconds": 15,
      "maximumResponseBytes": 1048576,
      "retryLimit": 1,
      "minimumRequestIntervalMilliseconds": 200
    }
  }
}
```

If `tools.enabled` is present, it is an allowlist for non-essential tools, so include every other non-essential tool that should remain available. `tools.disabled` wins over `tools.enabled`. The configured endpoint host must also appear in `tools.allowedNetworkHosts`, and `tools.allow` must permit `web_search`; these policy settings still do not replace explicit consent. An enabled tool can use its configured credential without a separate secret allowlist, provided the credential is available from an eligible source.

Provider settings and accepted bounds are:

| Key | Default | Accepted values |
|---|---:|---|
| `webSearch:provider:id` | `brave` | Stable provider label used in provenance. |
| `webSearch:provider:kind` | `brave` | `brave` only; other provider kinds are not compiled into this host. |
| `webSearch:provider:endpoint` | `https://api.search.brave.com/res/v1/web/search` | Absolute HTTPS URL without embedded credentials. Redirects must remain on the configured HTTPS origin. |
| `webSearch:provider:secretReference` | `secrets:BRAVE_SEARCH_API_KEY` | Logical name beginning with `secrets:`. |
| `webSearch:provider:timeoutSeconds` | `15` | 1–60 seconds for the complete operation. |
| `webSearch:provider:maximumResponseBytes` | `1048576` | 1,024–4,194,304 bytes. |
| `webSearch:provider:retryLimit` | `1` | 0–2 bounded transient retries. |
| `webSearch:provider:minimumRequestIntervalMilliseconds` | `200` | 0–60,000 ms between requests in the current process. |

### Governed web fetch

`web_fetch` is a separate, default-disabled public-HTTPS textual retrieval capability. It remains absent from unrelated model requests and becomes visible for an eligible search reference, explicit exact grant, fresh current-user URL, live session approval, or saved user hostname. Repository configuration cannot grant consent or URL authority.

Search results pre-authorize their exact hostnames for the current run. The model can fetch a result URL, revisit it, or retrieve another public HTTPS page on that hostname without another approval. This also works when it passes a raw URL instead of the opaque result ID. The hostname permission survives consumption or expiry of result IDs but ends with the run; it does not extend to subdomains, other sessions, or other repositories. Up to 100 distinct scoped hostname grants are retained, with the least recently issued evicted at capacity. No permanent hostname whitelist is needed.

Consent schema 3 explains that Threadsmith may send search terms, retrieve selected results, contact an exact public HTTPS URL in the current request only when the model invokes fetch, and supply untrusted fetched content to the model. Schema 2 remains valid for existing search-result and `/fetch-authorize` behavior but does not enable fresh-message URL inference; the first eligible turn offers visible re-consent and denial continues without network traffic.

After consent, `Read https://example.com/docs` can produce a one-shot opaque `userUrlId` without a separate command. Only the newly submitted raw top-level message is scanned, with bounded deterministic recognition and no DNS/network activity. Candidates must begin the message or follow a supported opening/token delimiter; embedded substrings such as `prefixhttps://...` are not URLs for this authority route. A URL span reaching the 32-KiB scan boundary is rejected unless the raw message ends there; Threadsmith never authorizes its truncated prefix. Authority is exact, message/repository/session/run/generation/expiry-bound, non-restorable, and revoked at the next turn or lifecycle/policy boundary. Prior conversation, memory, repository text, prompts, model/tool output, fetched pages, extensions, MCP, and hooks cannot mint these references.

If the model supplies a URL in `reference` without an existing exact, search-host, session-host, or user-host grant while fetch is active, interactive mode shows a host-owned approval-duration prompt containing model provenance, sanitized origin, a conservatively redacted path shape, query presence, and a digest—never path tokens or query values. Prompt details and URL-free lifecycle notifications remain process-local and are not written to session history, projections, telemetry, hooks, or restoration. The one-attempt choice applies only to that pending invocation and never to a redirect, retry, sibling, origin, session, or later run. Headless mode never prompts and reports `DirectAuthorizationRequired` with the sanitized origin, redacted path shape, and exact digest needed to identify the destination; automation can create an exact grant and retry. When a headless session is reused, only tool activity from the current run is printed.

The interactive prompt offers four choices:

| Choice | Permission |
|---|---|
| Deny | No fetch and no saved grant. |
| Approve one attempt | Only the exact pending URL and invocation. |
| Approve for this session | Public default-port HTTPS pages on the exact hostname across turns in this live repository-bound session. |
| Add to user allowed list | Add the exact hostname to `tools.allowedNetworkHosts` in `~/.threadsmith/config.json`, effective immediately and in future sessions. |

Session approval ends when the live session changes, the repository changes, or tool/consent/options authority is reset; completing or cancelling one run does not end it. It is held only in memory, with up to 100 distinct scoped hostname entries and oldest-issuance eviction. Resuming or cloning a session does not restore it. Saved user entries survive restart and apply across repositories where fetch is enabled and consented. Remove a hostname from the user list to revoke that saved permission; the next check rereads the list. Search or session grants for the same hostname retain their own remaining lifetimes.

Hostname approvals exclude subdomains and permit only bounded same-origin redirects. Existing exact invocation and explicit redirect-chain grants take priority. Current-user references retain their exact one-shot scope. The permanent choice uses the existing user network-host list, so it also supplies that ordinary network policy claim; it does not enable a tool or replace consent. Repository, machine, environment, session, and CLI host-list values do not create this user-owned fetch grant. Unrelated user configuration values are preserved when saving; invalid configuration, write failure, or cancellation fails the save without fetching or substituting a temporary approval.

`/fetch-authorize <initial-public-https-url> [redirect-public-https-url ...]` and headless `AuthorizeWebFetch`/`AuthorizeWebFetchChain` remain the advance-authorization and exact redirect-chain surfaces. Search-result retrieval permits only bounded same-origin redirects. Current-message and inline-approved routes authorize only their initial URL. Every destination is DNS/address validated and connection-pinned; local, private, metadata, reserved, mixed, and rebound targets fail closed. Cookies, ambient credentials/proxies, authentication, active content, subresources, binary/PDF content, and automatic redirects are disabled. Returned HTML/plain/Markdown/JSON is bounded readable text framed as untrusted evidence, with query-free provenance and content digests. See [Governed web fetch operations](operations/web-fetch.md).

| Key | Default | Meaning |
|---|---:|---|
| `webFetch:maximumUrlCharacters` | `2048` | URL bound; compiled hard maximum 8,192. |
| `webFetch:maximumRedirects` | `3` | Manual redirect bound; compiled hard maximum 5. |
| `webFetch:timeoutSeconds` | `15` | Whole-operation deadline; compiled hard maximum 60 seconds. |
| `webFetch:maximumCompressedBytes` | `1048576` | Wire-body bound; compiled hard maximum 4 MiB. |
| `webFetch:maximumDecodedBytes` | `2097152` | Decompressed source bound; compiled hard maximum 8 MiB. |
| `webFetch:maximumExtractedCharacters` | `131072` | Readable-text bound; compiled hard maximum 512 KiB. |

Invalid provider kinds, insecure or credential-bearing endpoints, non-secret credential references, and out-of-range limits fail during configuration. Retries apply only to bounded transient failures, honor the overall timeout and cancellation, and never allow a redirect to another origin.

#### Supply the Brave API key

Create or sign in to a Brave Search API account and generate a subscription key in the [Brave Search API dashboard](https://api.search.brave.com/app/keys). Review the plan and pricing presented by Brave before subscribing because API availability, included credits, and usage charges are provider-controlled and may change.

The default logical reference is `secrets:BRAVE_SEARCH_API_KEY`. The recommended durable source is the user store at `~/.threadsmith/secrets/config.json`:

```json
{
  "secrets": {
    "BRAVE_SEARCH_API_KEY": "<your-Brave-Search-API-key>"
  }
}
```

An exact environment variable may override the user store for one process. PowerShell uses double underscores to represent reference separators:

```powershell
$env:THREADSMITH_secrets__BRAVE_SEARCH_API_KEY = "<your-Brave-Search-API-key>"
dotnet run --project src\Threadsmith.App -- --tui
```

On Bash-compatible shells:

```bash
THREADSMITH_secrets__BRAVE_SEARCH_API_KEY='<your-Brave-Search-API-key>' \
  dotnet run --project src/Threadsmith.App -- --tui
```

Brave requires `UserOwned` source trust, so the lower-trust repository store is ineligible even when ignored and untracked. If `secretReference` is changed, use the corresponding nested user-store path or environment key. For example, `secrets:search:brave` is stored under `secrets` → `search` → `brave` and maps to `THREADSMITH_secrets__search__brave`. Credentials are resolved only at the transport boundary and are not exposed to the model, recorded in consent, or included in result provenance.

## Model providers, secrets, and reasoning

Threadsmith loads model providers from two dedicated catalogs:

1. `~/.threadsmith/providers.json` — optional user-level base catalog;
2. `<repository>/.threadsmith/providers.json` — optional repository overrides.

Providers and their nested models merge by stable `id`, not array position. Matching entries inherit omitted settings, other arrays replace the inherited array, and new entries append in repository order. Set `enabled` to `false` to disable an inherited provider or model. An override cannot change an inherited entry's `type` discriminator. If an inherited provider has a secret reference, a repository override also cannot change its provider-specific connection or authentication settings, including `baseUri` and `secretKeyReference`.

Each provider owns an array of typed models. `defaultProviderId` is a case-insensitive stable provider string; `defaultModelId` is the model's stable GUID. A complete secret-free [provider example](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/main/.threadsmith/providers.example.json) is available in the source repository.

```json
{
  "schemaVersion": 1,
  "defaultProviderId": "local-openai",
  "defaultModelId": "4d36e96e-292b-4c25-bb63-2f63821d5729",
  "providers": [
    {
      "type": "openai-compatible",
      "id": "local-openai",
      "name": "Local OpenAI-compatible",
      "baseUri": "http://127.0.0.1:1234/v1/",
      "chatCompletionsPath": "chat/completions",
      "headers": { "X-Client-Name": "Threadsmith.NET" },
      "secretKeyReference": "secrets:models:local-openai",
      "models": [
        {
          "type": "openai-compatible",
          "id": "4d36e96e-292b-4c25-bb63-2f63821d5729",
          "name": "Local coding model",
          "modelId": "local-model",
          "contextWindow": 32768,
          "maximumOutputTokens": 4096,
          "capabilities": { "streaming": true, "toolCalls": true, "structuredOutput": true },
          "supportedReasoningLevels": [ "none", "medium" ],
          "defaultReasoningLevel": "medium"
        }
      ]
    }
  ]
}
```

Only compiled, explicitly registered discriminators are accepted. Unknown types, case-insensitive duplicate properties/IDs, invalid or disabled defaults, excessive input, inline credential fields, and non-`secrets:` references fail before model activation. OpenAI-compatible `baseUri` paths are preserved whether or not they end in `/`; `chatCompletionsPath` must be a bounded relative path beneath that base. Optional request headers are bounded and cannot contain authorization, proxy authorization, cookies, API-key-like names, hop-by-hop fields, or control characters. Authentication and allowed configured headers are applied to each request rather than `HttpClient` defaults.

### OpenAI Codex authentication and discovery

Threadsmith's `openai-codex` provider uses native Responses and a Threadsmith-owned OAuth grant. It never reads Pi credentials, settings, model catalogs, or runtime state. Authenticate with `threadsmith --codex-login` (headless device flow), `threadsmith --tui --codex-login` (browser PKCE/localhost callback), or the equivalent `threadsmith [--tui] /auth openai-codex [login|status|logout]` form. Use `--codex-status` and `--codex-logout` for bounded status and removal.

After login, Threadsmith queries the protected Codex `/models` resource and projects every distinct model returned for that account. There is no hard-coded Codex model list. Stable Threadsmith profile IDs are derived deterministically from provider/model identity; returned context and reasoning metadata is bounded and normalized. Credential-free model metadata is cached under the user-owned `~/.threadsmith` directory and becomes selectable at the next process start. Logout removes both Threadsmith's Codex grant and this metadata cache. Repository provider catalogs cannot replace the host-owned `openai-codex` definition.

Codex authorization/resource authorities, client identity, scopes, redirect URI, and credential headers are compiled policy. Browser login requires `http://localhost:1455/auth/callback`; if that port is occupied, stop the conflicting process and retry. Headless login displays an OpenAI verification URI and one-time user code. Authentication failures never fall back silently to another provider.

### Anthropic API-key setup and discovery

The native `anthropic` provider uses Anthropic's Messages API and discovers models through its Models API. Add an enabled descriptor to the user `~/.threadsmith/providers.json` catalog and keep its API key in the separate user-level `~/.threadsmith/secrets/config.json` store. Repository keys and ambient SDK authentication cannot supply this provider. The complete descriptor and secret-reference example is in [native Anthropic operations](operations/model-providers.md#native-anthropic).

Use `/models status <provider-id>` to inspect discovered model IDs, profile GUIDs, eligibility, and exclusion reasons. `/models refresh <provider-id>` updates bounded credential-free metadata; restart to rebuild the immutable selectable catalog. Select a model explicitly with `/models`; discovery does not replace your default. These status and refresh commands also work headlessly.

Anthropic thinking display defaults to off. `/thinking on|off` applies to the next model request, including a tool continuation, and headless requests accept `--thinking on|off`. The setting controls summarized display independently of `/reasoning` effort. It never restarts an in-flight request. Private signed blocks needed by the native protocol are retained only during the active loop and are never restored from logs or sessions. A continuation that cannot fit fails with capacity guidance instead of compacting required signed content.

### Legacy model-profile migration

Legacy `model:profiles[]` remains available for a bounded compatibility period when neither dedicated catalog exists. Threadsmith adapts those profiles only in memory, preserves each profile GUID and exact endpoint/request settings, writes no configuration, and emits one deprecation warning per startup. The removal milestone is not yet selected and requires a later announced decision.

If a dedicated provider catalog and any legacy profile are both present, startup fails with an ambiguity error. Migrate mechanically by moving the legacy endpoint's authority/base path to provider-level `baseUri`, keeping `chat/completions` as `chatCompletionsPath`, moving `secretKeyReference` to the provider, retaining the legacy profile GUID as the nested model `id`, and copying model ID, capability, cost, workload, sensitivity, reasoning, temperature, timeout, and retry settings to that model. Do not place the resolved credential or an authorization header in `providers.json`.

### Model HTTP transport

The shared application-lifetime connection pool uses normal layered configuration under `model:http`: `pooledConnectionLifetimeSeconds` (default 900, range 60–86400), `pooledConnectionIdleTimeoutSeconds` (default 120, range 10–3600), `connectTimeoutSeconds` (default 30, range 1–300), and `maxConnectionsPerServer` (default 16, range 1–1024). Invalid values fail startup. Cookies remain disabled to prevent cross-provider state, and the global `HttpClient` timeout remains disabled because each model's `timeoutSeconds` owns the complete request deadline.

Profile selection continues to check capability, context window, cost ceiling, sensitive-data policy, and workload compatibility. A compatible configured default wins before advisory or least-cost alternatives.

Context assembly is model-specific. For each request, Threadsmith uses the selected profile's `contextWindow` and subtracts its effective `requestOutputTokenReserve`; when omitted by a legacy catalog the reserve defaults to `maximumOutputTokens`. The provider maximum is a separate hard capability and may equal the context window when an explicit smaller reserve leaves positive input capacity. The remaining capacity is available for governed input. There is no shared `context:maxTokensPerRequest` ceiling across models. The composer status denominator reports the selected model's full configured context window, while context inspection records the smaller effective input budget after the output reserve. Switching models changes both values at the next request boundary.

The native Codex endpoint does not accept a per-request output-token limit. Its request budget reserves input capacity and guides summary length; exceeding that budget alone does not discard a completed answer or summary. Threadsmith still checks reported output usage against the configured model profile's separate `maximumOutputTokens` and applies configured stream-byte and tool-call limits. Other providers continue to receive their supported request output limits.

The OpenAI-compatible adapter sends `max_completion_tokens` to the server. When the server omits token usage, Threadsmith marks its fallback counts as estimates. Those estimates remain in usage accounting, but the child loop does not treat them as proof that a response exceeded the model's output-token maximum. Checks on actual reported tokens and configured stream limits still apply.

### Repository model selection

Use `/models` to open the keyboard selector. Choices show provider/model identity, context/output limits, reasoning capability, and the current marker. The selected provider id, stable profile GUID, and effective reasoning level are written together to `.threadsmith/config.json`; unrelated settings are preserved.

Repository selection wins over `defaultProviderId`/`defaultModelId` in the user provider catalog. Those defaults apply only when both repository selection ids are absent. Partial, malformed, mismatched, missing, or disabled repository intent fails closed with repair guidance rather than silently selecting a user default.

A switch changes provider routing for the next request. In-flight work retains its captured model. An exact supported reasoning level is preserved; otherwise reasoning becomes `none` when supported, or the validated profile default when reasoning cannot be disabled, and the terminal lists valid `/reasoning` choices. `/reasoning` changes persist to the same repository selection. Opening another repository reloads its selection (or the user default when no repository selection exists) and redirects later `/models` and `/reasoning` writes to that repository. Current-context occupancy is cleared until the next request is assembled for the new context limit; cumulative provider token usage is not reset.

The TUI and headless adapter use the same list, current-selection, select-model, and set-reasoning host commands. Headless callers therefore observe the same validation, reset, persistence, and repository-rebinding behavior.

### Secrets

Supported static-credential fields contain logical `secrets:` references, never values. Threadsmith retains the reference and resolves it through one host-owned boundary only when the owning model, Brave, MCP, trusted hook, or private NuGet operation needs to authenticate. Ordinary strings, configuration inspection, prompts, tools, and model output never trigger discovery.

The recommended durable personal store is the strict-JSON file `~/.threadsmith/secrets/config.json`:

```json
{
  "secrets": {
    "BRAVE_SEARCH_API_KEY": "<credential>",
    "models": {
      "example": "<credential>"
    }
  }
}
```

Reference segments map directly to nested properties below the root `secrets` object. Empty placeholder values are rejected, so a checked-in example or newly prepared file fails closed until real values are supplied.

The optional repository-local store uses the same nested format at `.threadsmith/secrets/config.json`. It is lower trust and is accepted only when the exact confined file is untracked/not staged **and** covered by an effective Git ignore rule. Threadsmith checks both conditions before reading it and fails closed when Git state is unavailable or indeterminate. Add `.threadsmith/secrets/` to `.gitignore`; ignore is protection against accidental commits, not encryption. Rotate any value that was ever committed.

For an eligible request, fixed host-owned precedence is:

1. the exact environment variable;
2. the active repository store;
3. the user store.

Repository values cannot satisfy credentials requiring user-owned or managed authority. Current trust requirements are:

| Consumer | Secret requirement | Minimum source trust |
|---|---|---|
| OpenAI-compatible configured model | Optional `secretKeyReference` | `RepositoryOwned` |
| Brave `web_search` | Required API-key reference | `UserOwned` |
| MCP stdio scope, static HTTP headers, and optional OAuth client secret | Conditional profile references | `UserOwned` |
| Managed HTTP lifecycle hook | Conditional single bearer reference | `UserOwned` |
| Private configured NuGet advisory source | Conditional source credential | `UserOwned` |
| Native Codex and MCP OAuth access/refresh tokens | Lifecycle-managed caches, not static providers | User-owned specialized cache |

Repository configuration cannot add/reorder providers or weaken source trust. For `secrets:models:example`, the compatible variable is:

```powershell
$env:THREADSMITH_secrets__models__example = "<credential>"
```

Environment variables are optional rather than mandatory. The user store is edited explicitly outside Threadsmith; no command accepts a secret in normal command arguments. The operating system controls file access; Threadsmith does not inspect or change user-store ownership, Windows ACLs, or Unix permission bits. MCP and Codex access/refresh-token caches remain lifecycle-specific and must not be copied into these static stores.

Failures report only the logical reference, component, safe attempted/skipped sources, stable classification, and remediation. Values, store/environment contents, and raw provider exceptions never enter model context, status, logs, events, persistence, diagnostics, hooks, or support bundles. See [static secret discovery](operations/secret-discovery.md) for setup, Git remediation, trust rules, and troubleshooting.

### Reasoning levels

Models declare their own string names in `supportedReasoningLevels`, plus a `defaultReasoningLevel` from that list. Threadsmith has no shared allowlist of reasoning names: `["none", "low", "medium", "xhigh"]` and other model-specific choices are accepted. Custom names survive selection, saved sessions, and effort-based provider requests; `none` retains the existing disabled/reset behavior. OpenAI-compatible models may opt into versioned closed `reasoningCompatibility` modes for standard/mapped effort, compiled chat-template or fixed additions, always-on reasoning, or unsupported reasoning. `/reasoning` distinguishes selectable, always-on, and unsupported models; level changes are accepted only when selectable and advertised. Without an argument, it shows the active model, one reasoning-control summary (including selectable levels), and the current setting. Switching models preserves a supported level, otherwise chooses `none` when supported or the new profile default when reasoning cannot be disabled. Selectable effort does not imply that reasoning can be disabled. Hidden reasoning is transient-only; migration 7 purges historical reasoning-event rows and new reasoning text is excluded from durable events, conversation, memory, hooks, telemetry, evidence, and diagnostics. See [model-provider operations](operations/model-providers.md).

Qwen3.8 profiles can explicitly select the `enableThinkingWithPreservationAndEffort` chat-template kind with a complete `levelMap` to transmit the chosen effort alongside the thinking and preservation flags. Existing compatibility modes and other model profiles retain their request formats. See the [Qwen3.8 configuration example](operations/model-providers.md#qwen38-thinking-and-effort).

For full provider fields, retry, timeout, usage, and cost behavior, see [operations/model-providers.md](operations/model-providers.md). Transient DNS and connection failures use the selected profile's bounded retry policy. A connection timeout is distinct from the profile's overall model-request timeout: slow inference and streaming retain the longer configured request allowance. Failed turns display the reported diagnostic once. Ordinary conversation is not charged to an execution-token budget; cumulative session usage shown in the TUI is telemetry, not a quota. Mutation-proposal operations receive fresh configured budget scopes.

## Repository configuration

Repository configuration lives at `.threadsmith/config.json`. It is data, not executable code. The complete annotated [configuration example](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/main/.threadsmith/config.example) is available in the source repository and as `config.example` beside an installed Threadsmith executable.

Important sections include:

| Section | Purpose |
|---|---|
| `solution.path` | Remembered solution/project selection. |
| `model` | Provider profiles and defaults. |
| `tools` | Availability, invocation policy, allowlists, and per-tool scalar configuration. |
| `mutation` | Approval policy and the `ReviewRisky` large-diff threshold. |
| `context.conversation` | Default mode plus recent-turn, pressure, and artifact bounds. |
| `tools.config.memories` | Repository-memory storage/context limits and semantic minimum: defaults 20/3/0.47; zero context disables retrieval. |
| `repository` | Editable roots, prohibited paths, and lifecycle policy. |
| `tui` | Theme and session-status configuration. |
| `extensions` / `.threadsmith/extensions.json` | Extension runtime selection and settings. |
| prompt append files | Ordered repository-provided model context. |

Threadsmith combines compiled, machine, user, repository, session, CLI, and ordinary environment layers using Microsoft.Extensions.Configuration semantics. Static secret stores and the normalized `THREADSMITH_secrets__...` environment subtree are deliberately excluded from that graph and are consulted only by `ISecretResolver` at an explicit privileged boundary. Malformed JSON stops startup with exit code `2` and a concise standard-error message identifying the affected file and parser location rather than an unhandled stack trace.

Repository JSON reads accept comments and trailing commas. Solution, model/reasoning, tool, and mutation-policy preference updates serialize their complete read-modify-replace operations through one repository settings coordinator, preserving unrelated keys and concurrent changes.

### Repository instruction bundles and prompt append files

For each turn, Threadsmith builds one repository instruction bundle from:

1. applicable `AGENTS.md` files from the repository root toward the active working scope, in parent-to-child order; then
2. configured prompt append files, conventionally under `.threadsmith/prompts/`, in configured order and with distinct provenance.

The root `AGENTS.md` is discovered automatically; do not add it to `prompt append files`. Existing duplicate entries are handled as a fallback: each canonical file appears once, with AGENTS.md retaining its parent-to-child position. Distinct files remain distinct even when their contents match.

This means a nested `AGENTS.md` applies only when its directory contains the active scope; a sibling instruction file does not apply. A missing parent file does not prevent a deeper applicable file from being discovered. All repository-provided instructions are untrusted input: they cannot override host policy, approval rules, tool policy, trust, or coding guardrails.

The ordered bundle is content-addressed and revalidated at every turn boundary. An applicable instruction change therefore takes effect on the next request even if a filesystem watcher notification is lost. Changing an ordinary source file does not change the instruction bundle or its stable identity. Assembly omits a prior `read_file` result when its exact path, range, and text are already present in that request's instruction bundle. Context inspection explains the omission, and the original evidence remains stored. Changed content and out-of-scope files remain eligible as evidence.

Instruction and append paths must remain inside the repository, avoid prohibited paths and symbolic-link/junction/reparse traversal, decode as strict UTF-8, remain unchanged during their bounded snapshot read, and satisfy configured per-file, total-size, count, and depth limits. Unsafe or changing sources fail closed before model invocation.

## Themes and session status

The default `system` theme inherits native terminal foreground/background. Other built-ins are:

- `forge-dark`;
- `ocean`;
- `high-contrast`.

Use `/theme`, `/theme <id>`, or `/theme current`. TUIKit repaints retained views immediately; the original frontend applies the selection to future output. Selection atomically updates only `tui.defaultTheme` in `~/.threadsmith/config.json`, preserving unrelated settings, comments, trailing commas, and surrounding formatting. Normal configuration precedence still applies, so a higher-precedence repository, session, CLI, or environment value may override the user default at startup. Theme changes do not rewrite the original frontend’s native scrollback or create domain events.

Configured themes use semantic roles rather than fixed screen coordinates. A configured theme's `styles` object may contain these role names:

| Role | Styled content |
|---|---|
| `Default` | Ordinary model/transcript text and the fallback style for unspecified roles. |
| `Brand` | Startup branding and identity. |
| `Muted` | Secondary or de-emphasized information. |
| `Status` | General host status messages. |
| `SessionStatus` | TUIKit’s fixed repository footer; composer-adjacent repository/model/context/token status in the original frontend. |
| `Hyperlink` | Validated clickable links. |
| `ToolSuccess` | Successful tool completion. |
| `ToolFailure` | Failed tool completion. |
| `SelectionPrompt` | Selector prompts. |
| `SelectionItem` | Unselected selector entries. |
| `SelectionHighlight` | The currently selected entry. |
| `Success` | General successful outcomes. |
| `Warning` | Warnings. |
| `Error` | Errors and failures. |
| `UserPrompt` | User-authored transcript content. TUIKit moves each committed ordinary composer entry into its retained transcript once; the original frontend keeps the native prompt line in terminal scrollback. |
| `ComposerPrompt` | `Threadsmith >` in TUIKit; the repository-name prompt in the original frontend. |
| `ThinkingIndicator` | The transient `THINKING` indicator. |
| `Reasoning` | Streaming reasoning enabled with `/thinking` or `Ctrl+T`. |
| `DiffAdded` | Added diff lines. |
| `DiffRemoved` | Removed diff lines. |
| `DiffContext` | Neutral/context diff lines, including hunk/file headers and display-only hunk spacing. |

TUIKit also accepts the following workspace roles. See [the full role reference](operations/tui-themes.md#retained-workspace-role-reference) for padding, fallback, and dialog styling.

| Role | Styled content |
|---|---|
| `TitleBarRole` | The fixed `Threadsmith.NET` title row. |
| `AgentTabHeaderRole` | The tab bar base, unused space, and one-cell inter-tab gaps. |
| `AgentSelectedTabRole` | The selected tab label/background, including a lone MAIN tab. |
| `AgentNotSelectedTabRole` | Inactive tab labels/backgrounds and overflow arrows. |
| `AgentStatusPaneRole` | The unpadded `Using model:` header, provider/model/usage, and context progress bar. |
| `OutputStreamPaneRole` | Output border, padding, and background beneath semantic text styles. |
| `ComposerBackgroundPaneRole` | Composer border, padding, background, and child read-only banner. |

Each role accepts optional `foreground` and `background` colors plus the Boolean decorations `bold`, `dim`, `italic`, `underline`, `strikethrough`, and `invert`. An omitted or `null` color inherits. Unspecified role colors fall back through `Default`, then the built-in `system` theme, whose ordinary foreground and background inherit from the terminal. Stable system semantic decorations—such as bold headings—remain active unless that exact role explicitly overrides its decorations.

Colors accept a case-insensitive named value or an exact six-digit RGB value in `#RRGGBB` form. The complete named-color allow-list is:

| Standard | Bright |
|---|---|
| `black` | `brightblack` |
| `red` | `brightred` |
| `green` | `brightgreen` |
| `yellow` | `brightyellow` |
| `blue` | `brightblue` |
| `magenta` | `brightmagenta` |
| `cyan` | `brightcyan` |
| `white` | `brightwhite` |
| `grey` | — |

`gray`, three-digit hex, eight-digit hex, alpha values, CSS color names, and terminal escape sequences are not accepted. Named colors are normalized to lowercase and RGB values to uppercase. Theme ids may contain only ASCII letters, digits, `-`, and `_`. Invalid colors, ids, control characters, ANSI/OSC content, unknown roles/settings, and excessive values invalidate only the affected theme; valid configured siblings remain available. Interactive startup prints a sanitized warning that identifies each rejected theme and the validation reason. If `tui.defaultTheme` does not identify a remaining configured or built-in theme, Threadsmith reports that setting separately and uses `system`. Catalog-wide safety-limit failures also report the reason and fail closed to `system` rather than terminating the session.

For example:

```json
{
  "tui": {
    "defaultTheme": "project-blue",
    "themes": [
      {
        "id": "project-blue",
        "name": "Project Blue",
        "styles": {
          "Default": { "foreground": null, "background": null },
          "ComposerPrompt": { "foreground": "brightcyan", "bold": true },
          "ThinkingIndicator": { "foreground": "yellow", "dim": true },
          "ToolSuccess": { "foreground": "green" },
          "ToolFailure": { "foreground": "brightred", "bold": true },
          "SelectionHighlight": {
            "foreground": "black",
            "background": "#5FAFFF",
            "bold": true
          }
        }
      }
    ]
  }
}
```

`NO_COLOR`, redirected output, and limited terminals use plain-text fallback without changing semantic words or markers.

The composer-adjacent status row can show working folder, repository, current local Git branch, model profile name (without the redundant provider model id), reasoning, governed context use, and cumulative provider token usage. It remains ordinary native scrollback rather than a cursor-managed pinned footer. Disable it with `tui:footer:enabled=false`.

### Interactive Markdown answers

Ordinary interactive model answers render as complete semantic Markdown documents by default. Threadsmith buffers each contiguous answer block while `THINKING` remains active, then writes it once before a tool, status, diagnostic, completion, or other visible lifecycle boundary. Every new interactive model answer begins after one blank line, including answers that follow tool activity; redirected raw output does not gain this presentation separator. Native scrollback is never rewritten and a raw duplicate is not printed. Headings remove their source `#`/setext delimiters and use semantic styling plus block spacing; H1 and H2 additionally use bounded double/single underline rules so they remain visibly distinct even when terminal decoration is unavailable; emphasis, strikethrough, lists and tasks, blockquotes, inline and fenced code, public HTTPS links, thematic breaks, and pipe tables use host-owned semantic roles and structural layout. Narrow tables degrade to labeled rows. `NO_COLOR` removes decoration without restoring heading delimiters or changing words, structural markers, indentation, or layout.

Raw Markdown remains authoritative for conversation state, persistence, context, restoration, and headless output. Markdig is used only inside the TUI parser; HTML is inert, unsafe link schemes are plain visible text, parser/size failures fall back once to visibly escaped source, and terminal controls never pass through the interactive source or fallback path unchanged.

Set `tui:renderMarkdown=false` to restore terminal-safe model-source chunk cadence:

```json
{
  "tui": {
    "renderMarkdown": false
  }
}
```

The setting follows normal layered configuration precedence and is snapshotted by the interactive shell. It does not affect reasoning, tool/MCP markers, diffs, status, historical transcript restoration, or headless output.

When a model emits recoverable malformed or invalid tool, plan, mutation, pre-mutation, or post-apply validation output, Threadsmith appends bounded corrective feedback to the active turn and asks the model to retry rather than silently repairing the request. If one sibling in a tool batch is invalid before execution, the whole batch is rejected before any sibling runs; after a successful correction, rejected corrective messages are removed from future model history while successful executed evidence remains. `execution:maxCorrectiveTurns` is the single correction budget; exhaustion fails closed without approving, staging, or executing invalid output.

Operation durations are enabled by default through `tui:showOperationDurations`. One Boolean controls interactive request, ordinary-tool, extension-tool, and MCP duration text together. Active request timing covers the complete accepted turn and resumes from the original start after a tool continuation. Ordinary-tool completion timing covers only `ITool.ExecuteAsync`; MCP completion timing covers the remote transport invocation. Completed rows use compact invariant formatting (`47ms`, `8.6s`, `1:02`, `1:02:03`). Missing or invalid legacy timing is omitted rather than shown as zero.

Tool activity also includes concise context when a built-in explicitly defines a safe display field. For example, file reads show the repository-relative path and requested line range, listings show their root, searches show the query, and `run_process` shows the command. The host sanitizes, collapses to one line, and rune-safely bounds this detail before it reaches activity events or the terminal. Process-command display additionally masks common named CLI credential switches such as `--api-key`, `--password`, and `--client-secret`, including whitespace-separated values; avoid placing credentials directly on command lines because no redactor can recognize every application-specific syntax. Raw argument objects, extension arguments, and MCP arguments remain hidden by default.

```text
 TOOLS: read_file - completed · 47ms
   └ lines 1-200, src/Program.cs

 TOOLS: run_process - completed · 8.6s
   └ dotnet test src/Threadsmith.sln
```

Structured plan proposals, plan auto-approval notices, mutation proposal status, generic correction status, and applied mutation notices use the same one-character-indented interactive lifecycle block family. Proposal bodies, steps, mutation-attempt rows, correction reasons, and applied-mutation detail are guided muted text; auto-approval shows plan-approval provenance and, when prior TUI context explains the classification, a concise risk basis. It does not imply mutation approval.

```text
 PLAN: revision 1
 │ Update the formatter output.
 │
 │ Steps:
 └ 1. Add shared lifecycle blocks - Tool, plan, and semantic-check output align.

 PLAN: auto-approved
 │ Revision: 1
 │ Risk: High
 │ Risk basis: model declared 1 risk; 1 file affected
 │ Policy: AutoApproveAllValid
 └ Reason: Policy AutoApproveAllValid approved a High risk plan after sanity checks.

 MUTATION: Generating edits
 └ Attempt: 1/4

 CORRECTION: Retrying model request
 │ Attempt: 1/3
 ├ Category: MutationProposal
 └ Reason: ReplaceText expectedText was not found in 'src/File.cs'.

 MUTATION: Applied under the active approval policy
 │ Mutation applied: src/File.cs
 └ The expression uses the approved value.

 MUTATION: Validating applied mutation
 └ Stages: compile, diagnostics, tests
```

Adjacent visible lifecycle blocks are separated by exactly one presentation-owned blank line. Semantic baseline checks may be labeled as pre-apply baseline capture when they occur after preview but before mutation application.

```json
{
  "tui": {
    "showOperationDurations": false
  }
}
```

The setting follows normal machine/user/repository/session/CLI/environment/secrets precedence and affects presentation only. Disabling it preserves `THINKING`, `TOOLS`, `MCP`, and outcome words, performs no periodic elapsed repaint, and does not alter execution, telemetry, persistence, or headless structured output.

## Extensions

Threadsmith extensions use stable host-owned contracts and load into collectible `AssemblyLoadContext` generations. Extensions may contribute governed capabilities and model preferences.

Operational principles:

- extension implementations depend on `Threadsmith.Extensions.Abstractions`, not host runtime internals;
- terminal-library, provider-SDK, and extension implementation types do not enter durable host state;
- capability invocation uses leases and bounded per-extension budgets;
- unload drains active invocations and verifies collectible generations;
- hot replacement publishes the new generation without allowing an old unload to remove it;
- load-context isolation is not a security boundary, so in-process extensions must be trusted.

In TUIKit, `/extensions` opens a checkbox tree: checked means loaded. Toggle several extensions or a filtered group without closing the dialog; each load/unload takes effect immediately and the checkbox reflects the resulting state. Esc keeps completed changes. The original frontend retains its selector. Repository-level selection uses `.threadsmith/extensions.json`.

Extension authors should read [extension-authoring/authoring-guide.md](extension-authoring/authoring-guide.md).

## Governed skills and reusable workflows

Skills provide reusable declarative procedures without making package content executable or authoritative. Threadsmith searches organization, machine, user, repository, and maintained catalogs by bounded metadata. Startup does not open instruction/schema/reference bodies. A candidate reports scope, id, semantic version, SHA-256 digest, publisher/source, declared requirements, verification state, and enablement state.

Skills are data packages, not extensions. They cannot ship assemblies or scripts, add tools, grant trust, approve work, create agents, schedule tasks, mutate a repository, access the network/processes directly, or claim build/test success. Extension packages remain the executable capability mechanism. Skill workflows can only use existing host tools and return closed typed host-action proposals.

### Claude-style compatibility

Threadsmith also discovers bounded metadata from `.claude/skills/<name>/SKILL.md` in the repository and user profile. `/skills list` marks these entries with the `claude:` source format, and `/skills inspect claude:<scope>:<name>` shows the pinned contract, mapped/unavailable tools, restrictions, and unsigned-source warning.

Discovery reads only safe bounded frontmatter from explicit nonlinked roots. Explicit activation resolves the current catalog generation, reparses and compares frontmatter, revalidates confinement, rejects linked/reparse roots and descendants plus unsafe YAML features, loads strict-UTF-8 instructions/text resources under aggregate limits, and computes a deterministic SHA-256 identity over path, length, and raw bytes. Scripts and binaries are identity inputs but never execute automatically. `allowed-tools` is advisory; mappings still pass through repository availability, trust, phase, consent, and the central tool policy. Hook, agent, fork, dynamic-shell, or unmapped requirements remain restricted or unsupported rather than acquiring authority.

Use `/skills verify claude:<scope>:<name>` to compute and inspect the exact identity, `/skills enable claude:<scope>:<name>` to persist an external digest/source authorization outside the repository, and `/skills use claude:<scope>:<name> <json>` to invoke it through the same governed workflow/checkpoint boundary used by native packages. Headless skill commands and `invoke_skill` accept the same selector. Source changes invalidate the old authorization and any resume attempt under its digest.

See [the pinned compatibility contract](skill-compatibility-spec-v1.md) and [skill operations](operations/skills.md). Native signed packages retain stronger verification and distinct `native:` listing labels.

### Finding and selecting skills

```text
/skills list [text]
/skills refresh
/skills inspect <selector>
/skills provenance <selector>
/skills install <archive-path> <source>
/skills uninstall <selector>
```

Selectors become more specific from left to right:

```text
fix-analyzer-warnings
fix-analyzer-warnings@1.0.0
Maintained:fix-analyzer-warnings@1.0.0
Maintained:fix-analyzer-warnings@1.0.0+<sha256>
```

If more than one candidate matches, Threadsmith fails with an ambiguity message rather than applying scope or directory precedence. Invocations and pins retain the exact scope/id/version/digest.

### Verification, enablement, and revocation

```text
/skills verify <selector>
/skills enable <selector>
/skills disable <selector>
/skills pin <selector>
```

`install` accepts a local ZIP path plus its configured provenance source, verifies it in quarantine, and atomically imports it into the user content-addressed catalog. Install a new immutable version and pin it to update; pin an older installed version to roll back. `uninstall` is user-scope only and refuses pinned packages or packages retained by active workflows.

Verification re-reads the manifest, validates every declared hash/length/path, rejects undeclared files and links, and then applies revocation plus signature/exact-allowlist policy. A valid signature proves origin and integrity; it does not independently grant tool/trust/action authority and does not automatically enable third-party content. `enable` records an exact digest/publisher/source authorization outside the repository in `%USERPROFILE%\.threadsmith\skill-policy.json`. Repository files cannot add trusted signers, allowlists, enablement, or revoke exceptions. Revocation wins at the next verification/action/resume boundary.

Maintained packages are enabled after their shipped integrity verifies:

- `fix-analyzer-warnings` — investigates supplied analyzer diagnostics and proposes a governed remediation plan;
- `upgrade-package` — assesses one Central Package Management upgrade and proposes compatibility/rollback/validation steps;
- `review-pr` — returns bounded security, test, performance, and architecture findings without publishing or mutating;
- `threadsmith-docs-help` — answers Threadsmith product and authoring questions from the installed local documentation bundle with exact path, heading, line, and snippet citations.

For a natural question such as “How do I compact context?”, the model prefers `threadsmith-docs-help` when `invoke_skill`, the maintained package, current trust, and a compatible model are available. The skill can use only existing `search` and `read_file` capabilities rebound to `ThreadsmithDocs`; it cannot inspect the opened repository, access the network or secrets, execute processes, or mutate anything. If the shipped docs are missing or do not answer the question, it returns `partial` or `unavailable` and states the gap instead of guessing. Shipped documentation is evidence, not policy, and cannot override current host behavior, user instructions, approvals, or repository instructions.

### Copyable maintained-skill examples

Start at `TrustedRead`, inspect the exact package, and use one-line JSON because the remainder of `/skills use` is parsed as the input document:

```text
/trust read
/skills inspect Maintained:fix-analyzer-warnings@1.0.0
/skills verify Maintained:fix-analyzer-warnings@1.0.0
/skills use Maintained:fix-analyzer-warnings@1.0.0 {"diagnostics":["CA1822: Member 'Normalize' does not access instance data","IDE0058: Expression value is never used"],"scope":["src/Example/Normalizer.cs","tests/Example.Tests/NormalizerTests.cs"]}
```

A successful analyzer procedure returns schema-versioned `propose_plan` arguments and pauses with a typed `ProposePlan` action. Review and submit that action through the ordinary planning boundary. Do not manufacture a successful continuation. Only after the host has accepted the plan should the adapter continue the workflow with the actual host result, for example:

```text
/skills continue <invocation-id> {"accepted":true,"planId":"<host-plan-id>"}
```

Accepting the proposed plan still does not apply edits. The normal approval, implementation, exact-diff policy, transaction, build/test validation, and correction flow follows.

The conversational equivalent is to ask the model to use an exact selector and provide the typed input, for example: `Use Maintained:fix-analyzer-warnings@1.0.0 with diagnostics [...] and scope [...]`. During eligible evidence collection at `TrustedRead` or higher, the model may call `invoke_skill`; the host performs the same selection, schema, compatibility, budget, and workflow checks as `/skills use`. The tool is not available as a way to invoke nested skills or during an ineligible phase.

Assess a Central Package Management upgrade without implicitly restoring packages or accessing the network:

```text
/skills inspect Maintained:upgrade-package@1.0.0
/skills use Maintained:upgrade-package@1.0.0 {"packageId":"Microsoft.Extensions.Logging","targetVersion":"10.0.1","constraints":["Keep versions in Directory.Packages.props","Do not change target frameworks","Include rollback and focused tests"]}
```

Run a bounded review over explicit paths and focuses:

```text
/skills inspect Maintained:review-pr@1.0.0
/skills use Maintained:review-pr@1.0.0 {"changeSummary":"Add repository-scoped API-key rotation and audit events","paths":["src/Example/Auth","tests/Example.Auth.Tests"],"focus":["security","tests","performance","architecture"]}
```

`review-pr` produces evidence-backed structured findings; it does not publish, approve, merge, create reviewers, or mutate. Use ordinary conversation or a custom delegation-capable workflow when independent Plan-38 reviewers are required.

### Model selection for skills

Skills do not name arbitrary provider endpoints and cannot download or activate models. Their manifest narrows the already configured model catalog. For example:

```json
{
  "requirements": {
    "model": {
      "workloads": [ "Planning", "CodeEdit" ],
      "requiresToolCalls": true,
      "requiresStructuredOutput": true,
      "minimumContextWindow": 16384,
      "allowedProfiles": [
        { "value": "4d36e96e-292b-4c25-bb63-2f63821d5729" }
      ],
      "deniedProfiles": []
    }
  }
}
```

`allowedProfiles` is a strict allowlist of configured model-profile GUIDs; the first compatible entry is also the package preference. `deniedProfiles` removes profiles even when their capabilities otherwise match. Leaving `allowedProfiles` empty lets host selection choose among compatible configured profiles. The host then applies workload compatibility, tool-call/structured-output capabilities, minimum context, provider sensitivity policy, configured default/preference, and cost policy. The selected profile ID is frozen in the workflow checkpoint, so resume does not silently switch models.

Use `/skills inspect <selector>` before invocation to see declared workloads and compatibility denials such as `no-compatible-model`. Configure provider/model capabilities and intended workloads in user/repository `.threadsmith/providers.json`; see [Model providers, secrets, and reasoning](#model-providers-secrets-and-reasoning). `/reasoning` controls supported ordinary session-turn reasoning. Skill procedure requests do not expose a manifest reasoning selector and currently use the host's fixed request policy; neither skill text nor a package manifest can elevate reasoning, switch the selected profile after admission, expose a sensitive request to a prohibited provider, or borrow another workflow's budget.

Examples:

1. For local-only sensitive review, permit sensitive data on the intended local profile and place only that profile ID in `allowedProfiles`.
2. For inexpensive analyzer planning, leave `allowedProfiles` empty and configure a compatible default or lower-cost `Planning`/`CodeEdit` model.
3. For a large review, raise the model's configured context window only when the provider really supports it; a skill cannot override the provider profile or its host-level context ceiling.

### Skills that propose subagents

A custom skill may declare bounded Plan-38 role templates and emit only `ProposeDelegation` or `RequestReviews`. It cannot create tasks, choose concurrency dynamically, recurse into another delegation layer, or start children itself. This abbreviated authoring fragment declares two eligible reviewers:

```json
{
  "budget": {
    "delegatedChildren": 2,
    "parallelChildren": 2,
    "worktrees": 0,
    "reviewerFindings": 32
  },
  "agents": [
    {
      "role": "SecurityReviewer",
      "maximumChildren": 1,
      "outputSchemaPath": "schemas/reviewer-output.json",
      "budget": { "modelTokens": 12000, "toolCalls": 12, "evidenceItems": 32, "files": 64, "bytes": 4194304, "mutations": 0, "processes": 0, "builds": 0, "tests": 0, "corrections": 0, "wallTime": "00:05:00" }
    },
    {
      "role": "TestReviewer",
      "maximumChildren": 1,
      "outputSchemaPath": "schemas/reviewer-output.json",
      "budget": { "modelTokens": 12000, "toolCalls": 12, "evidenceItems": 32, "files": 64, "bytes": 4194304, "mutations": 0, "processes": 0, "builds": 0, "tests": 0, "corrections": 0, "wallTime": "00:05:00" }
    }
  ],
  "workflow": {
    "schemaVersion": 1,
    "workflowId": "review-with-specialists",
    "steps": [
      { "stepId": "scope", "kind": "invokeProcedure", "dependsOn": [], "instructionAsset": "instructions/scope.md", "inputSchemaAsset": "schemas/input.json", "outputSchemaAsset": "schemas/delegation-request.json", "maximumIterations": 1 },
      { "stepId": "request-reviews", "kind": "requestReviews", "dependsOn": [ "scope" ], "outputSchemaAsset": "schemas/delegation-result.json", "maximumIterations": 1 }
    ]
  }
}
```

The complete manifest must still declare and hash every referenced asset and satisfy aggregate package/host budgets; see [Declarative skill authoring](skill-authoring.md). At runtime:

1. `/skills use` runs the bounded `scope` procedure with the selected skill model.
2. The workflow pauses and displays the complete typed `ProposeDelegation` payload.
3. The host validates that request against the delegation policy: current trust, sensitivity, approved plan where mutation is involved, eligible roles, one-level depth, paths, tools, models, deadlines, child/aggregate budgets, and non-overlap all still apply.
4. Only an accepted host request creates a delegation ID, which the TUI prints immediately. Use bare `/agents` to list observed delegation and assignment IDs, inspect with `/agents <delegation-id>`, and cancel with `/agents <delegation-id> cancel` or `cancel-child <assignment-id>`.
5. After the delegation reaches its authoritative structured join, the adapter supplies that real result through `/skills continue <invocation-id> <delegation-result-json>`. The next workflow step receives only the schema-valid structured result—not raw child transcripts or hidden reasoning.

Model selection is hierarchical. The skill model selected above prepares the proposal; each accepted child receives a host-selected model/reasoning choice constrained by the parent, repository policy, role template, sensitivity, and remaining aggregate budget. A package preference can narrow candidates but cannot force an incompatible model, elevate a child, or bypass the parent ledger. Assignments using the isolated-worker APIs additionally require an approved plan, host-proven non-overlapping ownership, isolated worktrees, parent restaging, a fresh aggregate diff decision, and aggregate validation.

### Package lifecycle example

Use immutable selectors during update and rollback:

```text
/skills install C:\packages\contoso-review-1.0.0.zip contoso-internal
/skills verify User:contoso-review@1.0.0
/skills enable User:contoso-review@1.0.0+<digest-1>
/skills pin User:contoso-review@1.0.0+<digest-1>
/skills install C:\packages\contoso-review-1.1.0.zip contoso-internal
/skills pin User:contoso-review@1.1.0+<digest-2>
/skills pin User:contoso-review@1.0.0+<digest-1>
/skills uninstall User:contoso-review@1.1.0+<digest-2>
```

The second-to-last command rolls back future selection without modifying an already-running workflow. Uninstall refuses the currently pinned package and any package retained by a nonterminal checkpoint. Trusted signer keys, source/digest allowlists, organization denies, and revocations must already exist in repository-excluding configuration; typing a source name does not establish trust.

### Invocation and workflow control

```text
/skills use <selector> <json-input>
/skills status <invocation-id>
/skills continue <invocation-id> <host-result-json>
/skills resume <invocation-id>
/skills cancel <invocation-id>
```

Invocation validates bounded JSON input and current host/tool/trust/model/phase requirements before loading content. It loads only current-step assets, rechecks hashes, uses strict UTF-8 and sanitization, omits optional references under pressure, and fails if required content does not fit. Procedure turns advertise only declared available tools and still use the central tool pipeline.

A skill's tool declarations cannot expand `tools:allow` or override `tools:deny`. Each procedure turn offers only the intersection of the skill's declared tools and current tool-ID policy, then checks current policy again before executing a call. No overlap means no tools are permitted. An absent or empty configured `tools:allow` keeps its existing meaning of no additional allowlist restriction; this is different from an empty computed intersection. The same checks apply after `continue` or `resume`, including policy narrowed while the workflow was paused. Calling a skill through the model also requires permission for the outer `invoke_skill` tool.

A workflow may pause with a typed host action such as `ProposePlan`, `ExecuteApprovedPlan`, `ProposeDelegation`, `Validate`, or `AskUserInput`. The proposal does not perform the action. Normal planning, approval, exact-diff, transaction, build/test validation, Plan-38 scheduling/worktrees/reviews, cancellation, and authoritative outcomes remain mandatory. `continue` accepts the result only after the corresponding host action completes and validates it against the declared step-result schema.

Workflow checkpoints pin package identity, input, selected model/tools, budget, completed steps, attempt/generation, and next action. Resume revalidates the exact package and current policy; it never switches to a newer version or replays a completed effect. See [governed skills operations](operations/skills.md) for catalog locations, trusted configuration, import/quarantine behavior, detailed commands, and recovery.

## Lifecycle hooks and policy automation

Lifecycle hooks provide opt-in typed automation at repository, model, tool, planning, mutation, validation, correction, run, extension, and MCP boundaries. With no handlers configured, behavior is unchanged.

Repository configuration may declare handlers under `hooks:repositoryHandlers`, but declarations start disabled and cannot approve themselves. Interactive users use `/hooks list`, `/hooks inspect <id>`, `/hooks enable|disable <id>`, `/hooks test <id>`, `/hooks approve|revoke <id>`, and `/hooks audit [id]`. The matching `HeadlessShell` methods expose the same operations for automation. Both surfaces dispatch the same Core commands (`ListHooksCommand`, `InspectHookCommand`, `ApproveRepositoryHookCommand`, `RevokeRepositoryHookCommand`, `SetHookEnabledCommand`, `TestHookCommand`, and `QueryHookAuditCommand`); a test command invokes only its selected handler.

In TUIKit, `/hooks` or `/hooks list` opens the checkbox dialog. Space enables/disables an item or filtered group immediately; checked means enabled, and Esc keeps completed changes. Repository approval remains a separate action.

Repository handlers are always advisory and fail-open. Only repository-excluding managed organization/machine/user configuration can grant blocking or fail-closed authority, and only at eligible pre-action points for an immutable handler identity and allowlisted denial codes. Completed/terminal hooks cannot undo or invalidate completed work. No result is an approval or a command.

Every invocation is bounded by timeout, input/output size, concurrency, retry, call-chain depth, effective data scope, and logical-secret scope. The host abandons and discards late results when a handler ignores cancellation. `MutationStaged` runs once after exact-diff staging, `MutationApplied` runs once after the completed transaction rather than once per file, repository plan hooks receive the open repository identity, and `McpConnected` runs after successful auto-connect publishes imported capabilities. HTTP requires HTTPS except literal loopback development endpoints and does not follow redirects. Executable arguments are never shell interpolated. MCP must already be connected, and extension invocation retains existing lease/budget ownership.

See [Lifecycle hook operations](operations/lifecycle-hooks.md), [hook authoring](hook-authoring.md), and [ADR-35](architecture/adr-35-host-owned-lifecycle-hooks.md).

## Headless and automated use

The headless adapter uses the same command dispatcher and policy path as the TUI. `ForceSemanticRefreshAsync` dispatches `ForceSemanticRefreshCommand` and returns the same structured refresh result as `/semantic_refresh`; it does not create a model run. Active-model automation uses `ListActiveModelsCommand`, `GetActiveModelSelectionCommand`, `SelectActiveModelCommand`, and `SetActiveReasoningCommand`; selection and persistence behavior is identical to `/models` and `/reasoning`. Skill automation uses `RefreshSkillsCommand`, `ListSkillsCommand`, `GetSkillCommand`, `GetSkillCompatibilityCommand`, `InstallSkillCommand`, `UninstallSkillCommand`, `VerifySkillCommand`, `SetSkillEnabledCommand`, `PinSkillCommand`, `InvokeSkillCommand`, `ContinueSkillCommand`, `ResumeSkillCommand`, `GetSkillInvocationCommand`, and `CancelSkillInvocationCommand`; verification, schema, compatibility, workflow, persistence, and restoration behavior is identical to `/skills`.

Headless mode writes model/tool activity to standard output and uses these primary exit codes:

| Code | Meaning |
|---|---|
| `0` | Success. |
| `1` | Failure. |
| `2` | Discovery needs trust/selection, or an exact MCP profile/capability was not found. |
| `3` | A model-proposed web destination needs exact authorization, or MCP policy/source eligibility denied the operation. |
| `4` | MCP authentication, unsupported-authentication, or revocation state requires user action. |
| `124` | An MCP lifecycle operation timed out. |
| `130` | Cancellation. |

Inspect-only discovery:

```powershell
dotnet run --project src\Threadsmith.App -- --repository C:\source\my-repo
```

Trusted discovery with explicit selection:

```powershell
dotnet run --project src\Threadsmith.App -- --repository C:\source\my-repo --trust TrustedRead --solution src\MyRepo.sln
```

Trusted headless startup auto-loads a valid remembered solution. If multiple candidates remain, it lists them and exits `2` rather than prompting or choosing arbitrarily.

For exit `3`, headless output includes the sanitized `DirectAuthorizationRequired` tool error. Create an exact grant with `HeadlessShell.AuthorizeWebFetch` or `AuthorizeWebFetchChain`, then retry under a fresh invocation; headless mode never opens a prompt or reads opportunistically from stdin.

Redirected output avoids interactive styling and session-status control sequences. Cancellation should be propagated by the invoking host; Threadsmith normalizes user cancellation to exit code `130`.

## Persistence, retention, and diagnostics

Threadsmith creates a repository-local SQLite database at `.threadsmith/threadsmith.db` and stores large sanitized bodies by content hash under `.threadsmith/artifacts`. Migration 5 adds bounded skill verification provenance, immutable package pins, and workflow checkpoints; age-based retention removes expired workflow checkpoints while retaining small trust/pin provenance until explicitly replaced. On every startup the host applies ordered transactional migrations, runs the configured redaction audit, and then performs one retention pass. A failed schema migration rolls back; unsupported restored event versions are represented as partial legacy state instead of crashing the whole restore.

The repository configuration may override the locations and cleanup policy:

```jsonc
{
  "persistence": {
    "path": ".threadsmith/threadsmith.db",
    "artifactDirectory": ".threadsmith/artifacts",
    "retention": {
      "enabled": true,
      "sessionAgeDays": 30,
      "metadataOnly": false,
      "retainFullPrompts": true,
      "retainFullModelOutput": true,
      "retainProcessLogs": false,
      "retainSourceExcerpts": true,
      "retainDiffs": true,
      "retainTelemetry": false,
      "retainSessionSummaries": true,
      "retainConversationBodies": false,
      "conversationMessageBodyAge": "30.00:00:00"
    },
    "redactionAudit": {
      "enabled": true,
      "repairArtifacts": true
    }
  }
}
```

`sessionAgeDays` and `conversationMessageBodyAge` must be positive. `metadataOnly` removes eligible aged artifact bodies regardless of their kind. Otherwise each `retain...` switch decides whether an eligible aged kind is deleted. `retainConversationBodies: false` detaches old visible message bodies after their independent age window while keeping archive metadata and explicit repository memories under their separate retention/capacity rules. Retention runs at startup rather than continuously, so a long-running process does not clean newly expired records until its next launch.

The redaction audit is defense in depth, not a substitute for keeping secrets out of prompts and tool output. Event history is append-only and findings there are reported but not rewritten. With `repairArtifacts: true`, unsafe artifact bodies are sanitized. Disabling the audit or retention increases local-data exposure and should be a deliberate repository-owner decision.

The host also supplies a bounded `DiagnosticBundleGenerator` contract used by tests and future support surfaces. Bundle entries are sanitized and size-limited, and a canary-secret gate verifies generated ZIP content. There is not yet an interactive or headless command that generates a bundle; consequently the `diagnostics` example keys are reserved contract settings rather than an available user command.

## MCP connection profiles

Threadsmith uses one host-owned MCP manager for startup auto-connect, repository transitions, interactive commands, headless commands, and shutdown. Profiles marked `autoConnect` in repository-excluding trusted machine/user/environment configuration connect best-effort during startup; repository-owned profiles cannot authorize command execution or grant themselves trust. An eligible profile's connection failure is sanitized and does not prevent the shell from opening; malformed profile configuration fails closed during composition. Defined profiles remain visible while disconnected.

In TUIKit, `/mcp` and `/mcp list` open the connection checkbox dialog: checked means connected, and Space applies each connect/disconnect immediately. Select an eligible OAuth profile and press F3 for **Sign in / Authenticate**; Esc cancels an active sign-in and returns to the list. The modal refreshes actual state after each operation. It has no Switch account action; log out with `/mcp logout <profile>` before signing in again. Tool enablement remains separate. See [MCP lifecycle commands](operations/mcp-connections.md#lifecycle-commands).

Interactive lifecycle commands are:

```text
/mcp list
/mcp inspect [profile]
/mcp connect|disconnect|reconnect [profile]
/mcp capabilities [profile] [kind]
/mcp capability [profile] [capability]
/mcp enable|disable [profile] [tool]
/mcp resource read [profile] [resource-or-template] [key=value ...]
/mcp prompt get [profile] [prompt] [key=value ...]
/mcp auth|logout|revoke|switch-account|diagnose [profile]
```

Omitted profile or capability IDs open bounded numbered selectors. `logout`, `revoke`, and `switch-account` show the exact profile and sanitized endpoint before confirmation. Switch-account asks whether to perform local logout or advertised remote revocation first. Resource and prompt values containing spaces may be quoted (for example, `name="review this file"`). Resource and prompt output is marked untrusted, bounded, and displayed only for the explicit operation; aggregate truncation remains visible when server items are omitted, and content is not silently added to model context. `/mcp enable|disable` delegates to the ordinary tool-availability authority. Imported tools start disabled and require an exact repository-bound, capability/schema-digest-bound approval stored outside repository control in the user-owned `~/.threadsmith/mcp-tool-approvals.json` file. Repository `tools:enabled` and `tools:defaultEnabledOverrides` entries cannot grant that approval, so a different repository or changed server schema fails closed until explicitly reviewed and enabled again. Imported tool ids are profile-qualified canonical ids such as `profile:tool`; providers that require narrower function-name syntax receive an internal per-request alias, but host configuration, enablement, policy, and diagnostics continue to use the canonical id. Servers that advertise list-change notifications trigger a debounced complete rediscovery within the 256-capability connection bound; registry publication is replaced atomically, the manager generation advances, and previously resolved tools from the replaced capability generation can no longer invoke.

Headless automation uses the same manager and stable JSON result envelope:

```powershell
threadsmith --mcp list
threadsmith --mcp connect local-docs
threadsmith --mcp capabilities local-docs tools
threadsmith --mcp resource-read local-docs <capability-id> name=value
threadsmith --mcp logout remote-sso --confirm
threadsmith --mcp switch-account remote-sso --confirm --revoke-current
threadsmith --mcp revoke remote-sso --confirm --allow-local-cleanup
```

Exact IDs are required headlessly. When `--mcp` selects management mode, `--confirm` is mandatory for identity mutations, `--revoke-current` selects revocation before switch-account, and `--allow-local-cleanup` permits local cleanup only after an unconfirmed remote revocation. Without `--mcp`, these tokens remain unchanged ordinary conversational request text. MCP results use exit `0` for success, `2` for not found, `3` for ineligible/policy denial, `4` for authentication/revocation outcomes requiring action, `124` for timeout, `130` for cancellation, and `1` for other failure. Headless mode never substitutes an interactive selector or confirmation prompt.

The accepted profile shape is:

```jsonc
{
  "mcp": {
    "defaultDrainKillTimeoutSeconds": 10,
    "profiles": [
      {
        "id": "example",
        "name": "Example server",
        "transport": "stdio",
        "command": "npx",
        "arguments": ["-y", "package-name"],
        "trust": "TrustedRead",
        "secretScope": [],
        "startupTimeoutSeconds": 30,
        "requestTimeoutSeconds": 60,
        "drainKillTimeoutSeconds": 10,
        "allowedCapabilities": ["tools"],
        "environment": {},
        "workingDirectory": ".",
        "headers": {},
        "oauth": { "enabled": false },
        "autoConnect": true
      }
    ]
  }
}
```

Valid transports are `stdio`, `sse`, and `http`; valid trust values are `Untrusted`, `TrustedRead`, `TrustedExecution`, and `FullyTrusted`. An untrusted profile cannot connect. Capability values accept singular or plural `tool(s)`, `resource(s)`, `resource-template(s)`, and `prompt(s)`; unknown values fail startup rather than broadening access. Omitting the capability list permits all four kinds at the adapter boundary.

A stdio command must be a bare executable name—path-qualified commands are rejected. Arbitrary parent environment variables are not inherited; Threadsmith forwards a curated OS startup set plus explicit profile values and scoped secrets. SSE/HTTP commands are absolute endpoints and imported tools expose the endpoint host to the standard network allowlist.

HTTP `headers` support ordinary values and static-token SSO. A `secrets:` header value resolves only when the exact reference is also in `secretScope`; values are not retained in connection status or logs.

For interactive SSO, set `oauth.enabled` to `true`, requested `scopes`, and a loopback `redirectPort` (or `0` for an ephemeral port). You may provide a pre-registered `clientId`; if it is omitted, an explicit connect or authentication operation asks the MCP SDK to use advertised OAuth metadata plus dynamic client registration for a public native PKCE client. A configured `clientSecret` requires `clientId` and must be a logical `secrets:` reference included in `secretScope`. OAuth is HTTP/SSE-only and cannot be combined with an `Authorization` header. Threadsmith uses the official MCP SDK for protected-resource and advertised authorization-server discovery, dynamic registration when needed, authorization-code + PKCE, state and issuer validation, token exchange, bearer attachment, and refresh. The configured scopes are an upper bound even when the server advertises broader scopes. `oauth.discoveryUrl` is rejected because the pinned SDK does not support an arbitrary discovery-document override.

Interactive mode binds the localhost callback listener before opening the system browser. Automatic startup and repository-rebind connections may reuse or refresh a coherent cached OAuth identity but cannot invoke dynamic registration or the authorization callback; missing or unusable identity remains an explicit connect/authentication action instead of creating a remote client, opening a browser, or waiting for input. Headless mode writes the authorization URL and complete pasted-callback prompt to standard error only for an explicit connect/authentication operation, bypasses optional extension startup even when `--tui` is also present, and preserves the single-JSON standard-output contract. Access tokens, refresh tokens, dynamically registered client ids/secrets, and token metadata are cached outside the repository in the user-owned `~/.threadsmith/mcp-oauth-tokens.json` secret cache as one atomically replaced grant under `mcp:oauth:<profileId>:*`; each read uses one immutable grant snapshot, superseded grants and staged registrations are pruned, Unix cache files are owner-only, malformed optional cache content is recoverable, and credentials never appear in connection status, logs, projections, diagnostic bundles, or repository configuration. Cached dynamic-registration client credentials are reused only when their redirect URI exactly matches the current callback URI; with `redirectPort: 0`, a later explicit re-authentication may require local logout to force a fresh registration. Listing, inspection, and diagnostics never launch a browser. Local logout clears only the selected profile namespace and makes no remote claim. Remote revoke requires a same-origin advertised HTTPS revocation endpoint and uses the grant-bound `none`, `client_secret_post`, or `client_secret_basic` authentication method; configured client credentials remain authoritative and their secret reference is resolved afresh so rotation takes effect. Metadata redirects are not followed, request timeouts and other unconfirmed outcomes remain explicit, and explicitly authorized local-only cleanup still clears the selected cache after a timeout. One identity remains cached per profile; switch-account replaces it rather than retaining multiple accounts. Stdio OAuth remains out of scope. See [MCP connections](operations/mcp-connections.md) for command details, lifecycle behavior, and live-test variables.

## Safety model

Threadsmith provides governed execution, not a general operating-system sandbox.

Hard boundaries include:

- normalized repository containment for configured, selected, and mutated paths;
- rejection or skipping of symbolic links, junctions, prohibited paths, and reserved/inaccessible entries as appropriate;
- immutable baseline hashes and turn-boundary invalidation;
- explicit trust and approval checks;
- central tool policy and bounded result handling;
- no destructive Git operations;
- no writes outside approved repository roots;
- logical secret references resolved only at the final invocation boundary;
- transactional mutation with rollback;
- tracked process-tree termination on cancellation and timeout;
- sanitization and bounding of terminal, model, extension, diagnostic, and repository-provided content.

`AssemblyLoadContext`, scripting restrictions, and in-process policy checks reduce risk but do not isolate hostile native or managed code from the operating system. Use trusted repositories, models, extensions, build logic, and automation settings.

## Troubleshooting

### Startup reports `TextOnly` semantic confidence

`TrustedRead` deliberately avoids MSBuild evaluation. Reopen or upgrade to `TrustedBuild` when compiler-backed symbol discovery is required.

### Multiple solutions are reported in headless mode

Pass `--solution <repository-relative-path>`. A successful selection is remembered for future launches.

### A remembered solution was not loaded

Confirm the file still exists beneath the repository and that `.threadsmith/config.json` contains nested `solution.path`. Missing entries are cleared automatically; escaping, prohibited, or linked paths are rejected.

### Semantic refresh failed or requests remain blocked

Run `/semantic_refresh` after confirming the selected solution still exists and the repository remains readable at its current trust level. The command forces a complete reload and reports bounded failure detail without exposing source or exception dumps. Compiler errors can yield a successful reduced-confidence refresh; repeated infrastructure failure leaves the workspace dirty so a model request cannot start from known-stale semantics. Reopen the repository when its root or selected solution changed.

### A tool is missing

Check, in order:

1. `/tools` availability;
2. `tools:enabled` and `tools:disabled`;
3. `tools:allow` and `tools:deny` invocation policy;
4. required trust level;
5. semantic confidence for compiler-backed tools;
6. extension load state for extension tools.

Essential tools cannot be disabled.

### A tool invocation is denied

Inspect the displayed reason for trust, policy, approval, path, executable, network, timeout, cancellation, or output-bound failure. Enabling a tool does not bypass invocation policy.

### An MCP profile is missing or will not connect

Run `/mcp list` and `/mcp inspect`. Profiles come from repository-excluding trusted configuration; an `Untrusted` profile is visible but ineligible. Check the bare stdio executable or HTTPS endpoint, trust, allowed capability kinds, secret references, and startup timeout. Use `/mcp diagnose` for structured safe checks. A profile-local startup failure is nonfatal to the shell.

### An MCP tool remains disabled after reconnect

This is expected when it has never been enabled or when the server capability/schema digest changed. Inspect the capability, then explicitly run `/mcp enable` after review. Enabling records the current repository's exact reviewed MCP schema in the user-owned approval store; repository configuration alone cannot enable it. Enabling does not bypass repository trust, tool invocation, executable, network, secret, hook, approval, timeout, or output policy.

### MCP logout or revocation did not remove remote access

`logout` is local-only by design. Use `revoke` when the authorization server advertises RFC 7009 support. An unsupported or unconfirmed result is not remote success; retry later or explicitly choose local-only cleanup. Static-token profiles require rotation/removal in the external secret source.

### Model requests fail immediately

Verify the endpoint is the complete chat-completions URL, the profile advertises the capabilities required by the interactive flow, and the logical secret reference resolves. Credentials are intentionally absent from logs.

### Startup reports a prompt catalog error

Restore the complete `prompts/` directory from the same Threadsmith build or reinstall that build. Do not mix files from different versions. The startup diagnostic identifies safe filename/category metadata but deliberately omits prompt bodies and rendered token values.

### Terminal output has no colors or status row

Styling is suppressed under `NO_COLOR`, redirected output, or limited-terminal detection. The status row is also absent when `tui:footer:enabled` is false.

### Builds or tests execute repository code

This is expected at `TrustedBuild` and above. Downgrade future use by reviewing persisted trust outside the active session and do not grant build trust to an untrusted repository.

### Script execution times out

The worker process tree is terminated. Reduce the work, increase `tools:config:csharp_script:timeout_ms` within accepted bounds, or keep scripting disabled. Restrictions do not make arbitrary code safe.

## Further reference

- [Opening a repository](operations/opening-a-repository.md)
- [Semantic refresh](operations/semantic-refresh.md)
- [Session lifecycle, resume, and clone](operations/session-lifecycle.md)
- [Interactive commands and keys](operations/keyboard-shortcuts.md)
- [Tool runtime operations](operations/tools.md)
- [MCP connections and lifecycle](operations/mcp-connections.md)
- [Model providers](operations/model-providers.md)
- [Prompt file reference](prompt-file-reference.md)
- [Deployed prompt assets](operations/prompts.md)
- [TUI themes](operations/tui-themes.md)
- [Project prompt append](operations/project-prompt-append.md)
- [Cache-optimized context](operations/cache-optimized-context.md)
- [Extension authoring](extension-authoring/authoring-guide.md)
- [Architecture decisions](architecture/README.md)
- [Implementation roadmap (source repository)](https://github.com/Threadsmith-NET/Threadsmith.NET/tree/main/docs/implementation-plans)
- [Maintained regression test plan (source repository)](https://github.com/Threadsmith-NET/Threadsmith.NET/blob/main/docs/implementation-plans/manual-test-plan.md)

## Maintaining this guide

This is the primary user-facing reference. Update it in the same change whenever implemented behavior affects installation, startup, commands, configuration, trust, tools, models, extensions, safety boundaries, output, exit codes, or troubleshooting. Keep the README concise and link here for operational detail. Do not document planned behavior as available.

### Saving reports and data with `write_file`

`write_file` creates text artifacts directly during a conversation. It avoids change planning, mutation proposals, builds, and tests. For an existing answer, use `{"path":".inbox/report.md","useLastResponse":true}`: the host copies the latest archived assistant answer from the current session exactly. Missing/removed answer bodies produce an error instead of a substituted report. For new content, supply `content` instead of `useLastResponse`.

Configure the folder list in machine, user, or repository `config.json`:

```json
{
  "tools": {
    "writeFile": {
      "allowedFolders": [".inbox", "reports", "C:/Reports"]
    }
  }
}
```

The default list is `[".inbox"]`. Higher-precedence lists replace lower lists completely; `[]` or `null` permits no writes. Relative entries resolve under the active repository; outside folders must be absolute. Entries are literal folders, include descendants, and do not accept globs or relative `..` escapes. Absolute paths may identify folders elsewhere on the machine. Repository settings can grant these destinations as requested by the operator; an allowlisted folder is direct file-write authority. Restart after configuration edits. Opening another repository rebinds its folder grants without inheriting the former repository's overrides.

Parent folders are created as needed. Files use UTF-8 without a BOM and preserve supplied text and line endings. Existing files are preserved unless the call explicitly sets `overwrite:true`; replacement publishes a completed sibling temporary file. Supported extensions are `.txt`, `.md`, `.markdown`, `.json`, `.csv`, `.tsv`, `.yaml`, `.yml`, `.xml`, `.log`, and `.rst`. Both content modes have a 1 MiB UTF-8 limit. Source/project changes retain the mutation workflow. Git metadata, `.threadsmith` settings, `AGENTS.md`, prohibited paths, and symlink/junction traversal are rejected even under an allowed folder. Ordinary read tools do not inherit access to external write folders.

The tool remains subject to repository trust, `/tools` availability, `tools.allow`/`deny`/`requireApproval`, normal tool audit, and cancellation. If an existing configuration has a nonempty `tools.allow` or explicit `tools.enabled` list, add `write_file`; remove any legacy placeholder denial of that name. It has a file-write side effect, serializes with conflicting work, and is excluded from read-only delegated-agent tool sets. Tool activity shows the destination; successful results report path and byte count without echoing the saved report.
