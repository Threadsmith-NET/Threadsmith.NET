# Agent workspace operations

The default TUIKit frontend presents MAIN plus accepted active children. Names are display labels; session/delegation/assignment/run/generation identities own routing. Selecting a child does not change any model, repository, execution target, or policy. See the [user guide](../user-guide.md#retained-tuikit-frontend-default) and [keys](keyboard-shortcuts.md).

Bordered panes and all modal dialogs use rounded corners and reserve one cell of side and bottom padding inside their borders. Modal headings sit immediately below the top border, followed by one blank line before the body; the startup logo follows the same rule, with the blank line after `Forge better code, not slop.`. This applies to startup, selection, toggle, command-palette, and help dialogs. Single-row title, tab, and repository bars have horizontal padding. The agent status header spans the unpadded row immediately inside the output's top border, followed by one blank line before streamed content. The composer also omits top padding, placing `Threadsmith >` immediately inside its top border while retaining side and bottom padding. Vertical padding collapses on short terminals to preserve usable output and editor rows. There is no reserved hotkey-hint row; transient activity, unread-output counts, and actionable notices use the existing bottom border. F1 still opens keyboard help. Normal terminal sizes retain five composer rows. The `/models` selector lists `(Provider Name) Model Name`, sorted by provider name and then model name, with `[current]` marking the active model.

Successful startup phases and remembered-solution details, including the `--solution` hint, appear only in the startup modal. They are discarded when the composer opens. Startup warnings and failure diagnostics remain available in the output transcript.

External semantic changes and recovery also produce TUIKit toast notifications when refresh starts, completes, or fails. These brief echoes appear at the upper right of the output content, below the agent header, and use the current status, success, or error theme role. Up to five stack at once and each expires after four seconds. The original output messages remain in MAIN; toasts do not take focus, change composer input queueing, or become part of copied transcript text.

`/help` opens a read-only modal with command usage in the first column and its description in the second, using TUIKit's column formatter and text wrapping. Both columns wrap to fit the terminal, including long command arguments. Up/Down, PgUp/PgDn, Home, and End scroll the list; Esc closes it and returns to the composer. Help text stays out of the output transcript. F1 continues to show keyboard help.

`ENTER to steer; ESC-ESC to cancel` appears after the activity timer at the bottom of MAIN only while active-turn input is available. It disappears when the turn completes, is cancelled, or pauses for input, and is not retained in the output transcript.

Tool calls appear inside the selected agent's output pane while they run, using TUIKit `ActivityIndicator` widgets and independent elapsed timers. MCP calls use `MCP:`; built-in and extension tools use `TOOLS:`. Concurrent calls remain visible independently, and each completion replaces its live entry with one retained result block. The existing operation-duration preference applies. Short panes omit detail lines first, then show an explicit count of additional running tools when necessary. Animation frames are transient and do not accumulate in copied or durable conversation history.

## Management dialogs

`/theme` remains a single-selection modal: choose one theme with Enter; Esc cancels.

`/hooks` (or `/hooks list`), `/mcp` (or `/mcp list`), and `/extensions` use the same keyboard-only CheckTree as `/tools`. Space toggles an item or the unlocked, filtered members of a group; each operation applies immediately and the dialog stays open for further changes. Esc closes without rolling back completed changes. Type to filter and use F2 for details.

OAuth-enabled MCP profiles display sign-in status. Select an individual profile and press F3 to open Actions, then choose **Sign in / Authenticate**. This uses the existing browser OAuth flow and may reuse cached credentials. While authentication is pending, Esc cancels the attempt and keeps the MCP list open; afterward the status and connection checkbox refresh from the manager. The Actions menu targets one profile; group connection changes still follow each profile's existing authentication requirements. Static-token profiles do not offer it. Use `/mcp logout <profile>` to clear an existing identity before signing in again; the modal does not offer Switch account.

Hook checkboxes mean enabled, extension checkboxes mean loaded, and MCP profile checkboxes mean connected. MCP connection changes do not change startup auto-connect configuration or individual tool preferences; `/mcp capabilities [profile]` manages tool enablement separately. Hook enablement does not grant repository approval. Every checkbox is reconciled against current host state after an operation, including failed connections and blocked unloads. Direct subcommands and the original frontend remain available.

## Name configuration

Ordinary `tui.agentNames.defaultNames` supplies an optional shared list. `tui.agentNames.byRole` accepts exactly the keys below. Resolution is independently per list, highest ordinary provider first (compiled/machine/user/repository/session/CLI/environment). A provider’s indexed list replaces the whole lower list, so setting `THREADSMITH_tui__agentNames__defaultNames__0` or the CLI key `tui:agentNames:defaultNames:0` never leaves lower-provider tails. Explicit nonempty role lists take precedence over shared names even if the shared list comes from a higher provider. Missing/unusable lists fall through to shared names and then compiled defaults.

| Role key | Compiled and example defaults |
|---|---|
| `explorer` | Amundsen, Cousteau, Humboldt, Magellan, Shackleton, Zheng He |
| `implementer` | Lovelace, Hopper, Thompson, Ritchie, Hamilton, Liskov |
| `securityReviewer` | Turing, Shannon, Diffie, Hellman, Rivest, Shamir |
| `testReviewer` | Dijkstra, Hoare, Myers, Hamming, Knuth, Hopper |
| `performanceReviewer` | Amdahl, Gustafson, Cray, Hennessy, Patterson, Knuth |
| `architectureReviewer` | Brooks, Parnas, Kay, Dijkstra, Liskov, Shaw |

Names are trimmed and NFC-normalized, deduplicated case-insensitively, limited to 128 entries per list and 32 UTF-16 units per normalized name. Inputs over 64 units are rejected before normalization. Control, format, multiline, malformed, and unassigned characters are rejected; bounded warnings never echo rejected values. Printable Unicode names use measured cell widths; full names and roles remain accessible through F2. A catalog is immutable until process restart; live children never rename.

Allocation reserves names atomically across roles. Exhaustion randomly chooses a base from that role and takes its smallest available positive suffix (`_1`, `_2`, …), checking complete candidates against all active names. Retirement releases that reservation without renaming survivors. Do not place secrets in name lists.

## Retention, privacy, and counters

MAIN retains the existing 1,024 logical chunks and 512 KiB text limit. Child views share a 4 MiB source/projected-text budget and an 8,192-chunk allocation, each capped at the existing per-view limits; child churn never consumes MAIN's allowance. The display queue holds at most 256 fragments of 4,096 characters. Overflow produces an omission notice. Presentation metadata retains at most 64 closed delegations for late outcome corrections, in addition to active work; retired transcripts are released.

Ordinary safe lines can stream immediately. A potentially sensitive suffix is held and sanitized together through the end of the response, including multiline or incomplete quoted credentials and private-key blocks. If that pending suffix exceeds 16,384 characters, the entire remaining suffix is omitted with a visible marker. Sanitizers without the streaming-safety contract use the bounded whole-response fallback.

The header shows the latest effective request's provider name, model, reasoning, and cumulative normalized input/cached-input/output for that owner. Context usage comes last, right-aligned as `Context [bar] 44% of 256K`, using a TUIKit `ProgressTask` rendered through `MultiProgress` with the header's semantic style. Narrow headers abbreviate metadata and use `Ctx [bar] 44%/256K`; F2 retains full details. Unknown usage displays `?%`, and the bar caps at full when the estimate exceeds the limit while the percentage still reports the excess. Cached input is included in input. MAIN excludes child requests; the session total still includes them exactly once. Restored historical aggregate usage is not guessed into agent totals: displayed owner counters carry `since resume`. Closing a tab does not remove billed session usage.

`/thinking` controls public reasoning visibility. Each child response captures its display eligibility when it starts; enabling visibility cannot expose a partly hidden response. Turning it off also suppresses pending display delivery. Child loops request public summaries consistently; Anthropic display-only summaries do not consume the legacy local reasoning-character cap, and provider-reported usage remains authoritative. Signed private replay remains confined to the active provider loop.

The footer abbreviates staged, modified, untracked, and conflict counts as `S`, `M`, `U`, and `!`. `Git ?` means unavailable, not clean. Counts above 999 use `999+`; a final `+` means the captured Git status was partial. Folder and branch labels are clipped independently so the counters remain visible at the supported minimum.

When the provider reports reasoning-token usage, the header adds `(reasoning n)` after output tokens (or `(Rn)` in compact layouts), and F2 includes the latest request's breakdown. These tokens are already included in output totals and cost; they are never added again. Missing or invalid optional counts add no label or placeholder, while reported zero remains visible. Cumulative reasoning is shown only when all contributing requests report it; a partially known cumulative sum is hidden. Restored historical totals without a breakdown remain unknown, while agent counters since resume use new observations. OpenAI Responses/Codex uses `output_tokens_details.reasoning_tokens`, OpenAI-compatible servers may supply `completion_tokens_details.reasoning_tokens`, and Anthropic supplies `output_tokens_details.thinking_tokens` in final usage. Threadsmith does not estimate reasoning counts from visible text.

Agent details also show the latest observed request's stage/round, input/output, cache reads/writes, and cache-hit percentage separately from cumulative totals. Open F2 from output, then F2 for full text. Missing counters remain unavailable and reported zero remains zero; see [cache reporting and vLLM setup](cache-optimized-context.md).

## Verification procedure

Run Windows Terminal/PowerShell and a supported Unix terminal independently. Record OS, terminal/version, dimensions, theme, keyboard protocol, mouse mode, and exit path. At 120×35, 80×24, and 40×12, stream MAIN and several children, switch with Ctrl+Left/Right from output and captured mouse, and retire selected/unselected children. Resize below and back above minimum with a Unicode multiline draft and detached child scroll position.

Check actual OS clipboard paste, bracketed paste, selected-copy Ctrl+C in both panes, Ctrl+C cancellation without selection, F12 native selection, F2 long labels, modal background clicks, and immediate denied/consent/group toggles. Enter and paste on children must never steer MAIN. Verify startup choices precede the splash, input is discarded, and success/cancellation/failure restore terminal modes. Check no footer scrolling, blank-row corruption, or escape leakage. Physical terminal results and measured render latency must be recorded separately from headless test results.

Delegated agents report their current named role and queued, running, or final status inside the live `delegate_agents` tool block. Each agent has one row that updates as lifecycle events arrive; repeated final checkpoints do not duplicate it. The completed tool block retains the final rows beneath its timer. Delegation-to-tool correlation uses the originating invocation ID, including when calls overlap. The automatic delegation GUID and `/agents` navigation notices are omitted; the explicit command remains available. On short panes, an omission count replaces progress rows that cannot fit.
