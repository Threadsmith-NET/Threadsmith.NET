# Agent workspace operations

The default TUIKit frontend presents MAIN plus accepted active children. Names are display labels; session/delegation/assignment/run/generation identities own routing. Selecting a child does not change any model, repository, execution target, or policy. See the [user guide](../user-guide.md#retained-tuikit-frontend-default) and [keys](keyboard-shortcuts.md).

Bordered panes and all modal dialogs use rounded corners and reserve one cell of side and bottom padding inside their borders. Modal headings sit immediately below the top border, followed by one blank line before the body; the startup logo follows the same rule, with the blank line after `Forge better code, not slop.`. This applies to startup, selection, toggle, command-palette, and help dialogs. Single-row title, tab, and repository bars have horizontal padding. The agent status header spans the unpadded row immediately inside the output's top border, followed by one blank line before streamed content. The composer also omits top padding, placing `Threadsmith >` immediately inside its top border while retaining side and bottom padding. Vertical padding collapses on short terminals to preserve usable output and editor rows. There is no reserved hotkey-hint row; transient activity, unread-output counts, and actionable notices use the existing bottom border. F1 still opens keyboard help. Normal terminal sizes retain five composer rows. The `/models` selector lists `(Provider Name) Model Name`, sorted by provider name and then model name, with `[current]` marking the active model.

Successful startup phases and remembered-solution details, including the `--solution` hint, appear only in the startup modal. They are discarded when the composer opens. Startup warnings and failure diagnostics remain available in the output transcript.

External semantic changes and recovery also produce TUIKit toast notifications when refresh starts, completes, or fails. These brief echoes appear at the upper right of the output content, below the agent header, and use the current status, success, or error theme role. Up to five stack at once and each expires after four seconds. The original output messages remain in MAIN; toasts do not take focus, change composer input queueing, or become part of copied transcript text.

`/help` opens a read-only modal with command usage in the first column and its description in the second, using TUIKit's column formatter and text wrapping. Both columns wrap to fit the terminal, including long command arguments. Up/Down, PgUp/PgDn, Home, and End scroll the list; Esc closes it and returns to the composer. Help text stays out of the output transcript. F1 continues to show keyboard help.

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

Agent details also show the latest observed request's stage/round, input/output, cache reads/writes, and cache-hit percentage separately from cumulative totals. Open F2 from output, then F2 for full text. Missing counters remain unavailable and reported zero remains zero; see [cache reporting and vLLM setup](cache-optimized-context.md).

## Verification procedure

Run Windows Terminal/PowerShell and a supported Unix terminal independently. Record OS, terminal/version, dimensions, theme, keyboard protocol, mouse mode, and exit path. At 120×35, 80×24, and 40×12, stream MAIN and several children, switch with Ctrl+Left/Right from output and captured mouse, and retire selected/unselected children. Resize below and back above minimum with a Unicode multiline draft and detached child scroll position.

Check actual OS clipboard paste, bracketed paste, selected-copy Ctrl+C in both panes, Ctrl+C cancellation without selection, F12 native selection, F2 long labels, modal background clicks, and immediate denied/consent/group toggles. Enter and paste on children must never steer MAIN. Verify startup choices precede the splash, input is discarded, and success/cancellation/failure restore terminal modes. Check no footer scrolling, blank-row corruption, or escape leakage. Physical terminal results and measured render latency must be recorded separately from headless test results.
