# Spike Notes (plan-01 tasks 10–15)

Recorded 2026-07-31 from the throwaway spike projects under `spikes/`. These observations feed ADRs 1–6 and later plans. Each spike ends with an automated `PASS`/`FAIL` assertion (plan-01 §10).

| # | Spike | Project | Result | Key observation |
|---|---|---|---|---|
| 10 | Terminal.Gui v2 instance lifecycle + streaming | `Spike.TerminalGui` | PASS (decision superseded) | The spike proved streaming mechanics but did not test interactive latency. Windows input/redraw was unusably slow; ADR-9 first fell back to v1.19 and ADR-15 later retired Terminal.Gui. |
| 11 | MSBuildWorkspace load + symbol find | `Spike.MsBuildWorkspace` | PASS | Roslyn 5.6.0 + `Microsoft.Build.Locator` 1.11.2 loads `src/Threadsmith.sln` and resolves `Threadsmith.App.Program`. `MSBuildLocator.RegisterDefaults()` must run before creating the workspace. |
| 12 | OpenAI-compatible streaming + cancellation | `Spike.OpenAiStreaming` | PASS | `Microsoft.Extensions.AI` 10.8.3 `IChatClient.GetStreamingResponseAsync` → `IAsyncEnumerable<ChatResponseUpdate>`. Fake provider (no keys/network); cancellation propagates as `OperationCanceledException`. |
| 13 | Collectible ALC load/invoke/unload | `Spike.CollectibleAlc` (+ `.Extension`) | PASS | `AssemblyLoadContext(isCollectible: true)` + `LoadFromStream` + `AssemblyDependencyResolver`. Unloads after 1 GC iteration in **Release**; Debug JIT keeps locals alive longer (run Release for the authoritative check). |
| 14 | SQLite event write/close/reopen/read | `Spike.Sqlite` | PASS | `Microsoft.Data.Sqlite` 10.0.10 async APIs; write → close → reopen → read matches. Transitive `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 has a known CVE (NU1903) — plan-18 must pin a fixed version. |
| 15 | Process-tree cancellation | `Spike.ProcessTree` (+ `.Worker`) | PASS | Windows Job Object (`KILL_ON_JOB_CLOSE`) + explicit grandchild kill; Linux process-group `kill(-pid, SIGKILL)`. Pid exchange via temp pidfiles (not stdout piping, which deadlocks). Both child + grandchild die; no orphans. |

## Versions observed

| Package | Version |
|---|---|
| PrettyPrompt | 6.0.4 (active inline composer) |
| Spectre.Console | 0.57.0 (active bounded output) |
| Terminal.Gui | 1.19.0 and 2.4.17 (superseded evidence) |
| Microsoft.CodeAnalysis.Workspaces.MSBuild | 5.6.0 |
| Microsoft.CodeAnalysis.CSharp | 5.6.0 |
| Microsoft.Build.Locator | 1.11.2 |
| Microsoft.Extensions.AI | 10.8.3 |
| Microsoft.Data.Sqlite | 10.0.10 |
| .NET SDK | 10.0.204 |

## Deviations from assumptions

- **Terminal.Gui TTY requirement:** v2 requires a real TTY to init the console driver. The spike includes a headless fallback so CI (no TTY) can still prove the streaming mechanism. plan-03 must account for this in UI tests.
- **Collectible ALC Debug-vs-Release:** the extension unloads reliably in Release; in Debug the JIT keeps locals alive and the `WeakReference` may not die within the retry window. plan-17's unload-verification fixture should run in Release (or use the isolated-method + `LoadFromStream` pattern this spike established).
- **SQLite CVE:** `Microsoft.Data.Sqlite` 10.0.10 pulls a vulnerable transitive `SQLitePCLRaw.lib.e_sqlite3`. Not a blocker for M0 (spike suppressed NU1903); plan-18 must resolve.
- **Process-tree pid exchange:** redirected stdout deadlocks when the child reads the grandchild's stdout while the parent reads the child's stdout. The spike uses temp pidfiles instead. plan-08's process tools should use the same pattern (or async stream reads).

## Plan 26 footer feasibility inventory

Recorded from the production dependency inventory for PrettyPrompt 6.0.4 and Spectre.Console 0.57.0. PrettyPrompt's public configuration surface owns the prompt and completion pane but provides no fixed footer/editor-status integration. Threadsmith's existing adapter already requires a private completion-pane workaround; Plan 26 does not add another private dependency. A reserved-row implementation would require cursor save/restore and redraw coordination around PrettyPrompt input, which would take ownership of native scrollback and cannot establish selection, paste, streaming, selector, and resize safety through public APIs.

**Decision:** ship the mandatory composer-adjacent status row through the existing serialized `IConsoleSurface` boundary. Do not ship a permanently pinned row. This retains ordinary terminal scrollback, performs no mouse capture or alternate-screen transition, emits no footer in redirected output, and reevaluates terminal width before each composer opens. Real-terminal regression results remain tracked in the maintained manual plan.

## Plan 91 active-run input feasibility inventory

Recorded from the current `ConversationalShell`, `PrettyPromptConsoleSurface`, ADR-15, and the Plan-26 decision. During an active conversation run, the shell waits on the execution task, review-decision channel, and serialized output drain; it does not read ordinary terminal input. PrettyPrompt owns the only normal composer read and holds the shared console gate until that read completes. This preserves append-only native scrollback and prevents Spectre output from racing the editor.

The evaluated active-run key watcher cannot prove byte ownership across the transition to the next PrettyPrompt read. A background `Console.ReadKey`-style reader could consume Enter, Escape, paste, or slash-command bytes intended for the composer. A full-text arbiter would duplicate PrettyPrompt editing and paste behavior. Cursor-managed pinning, alternate-screen input, or private PrettyPrompt integration would change ADR-15 terminal ownership and repeats the Plan-26 failure mode.

**Decision:** fail the current active-run input gate. Do not ship delegation steering or double-`Esc` cancellation in Plan 91. Ship the model-callable `delegate_agents` fork/join path with existing `Ctrl+C`, linked caller cancellation, and `/agents <delegation-id> cancel[-child]` controls. Reconsider steering only with a terminal architecture that exposes one public, serialized input owner and passes real-terminal selection, paste, resize, streaming, and command-routing gates.

## Open items for later plans

- plan-03: real-terminal selection and paste-latency checks remain in the maintained manual plan; CI uses a terminal-neutral surface.
- plan-06: `MSBuildLocator.RegisterDefaults()` ordering; non-cooperative cancellation of Roslyn/MSBuild (gap #7).
- plan-07: real OpenAI-compatible adapter implementing `IChatClient` against an HTTP backend.
- plan-17: Release-mode unload-verification fixture; blocked-unload detection.
- plan-18: pin a `Microsoft.Data.Sqlite`/`SQLitePCLRaw` version without the CVE.

## TUIKit frontend gate

Recorded 2026-09-04 on branch `feat/plan-100-tuikit-frontend`, based on commit
`418530025723fe95228bc6d25eb1f1353f00fc4f`. **Initial stock-editor precheck: FAIL.**
Both packaged stock editors fail the checks below. This result does not evaluate
a Threadsmith-owned composer or establish a complete adapter no-go. The revised
[implementation plan](../implementation-plans/plan-100-tuikit-alternate-interactive-frontend.md#681-recovery-design-a-threadsmith-composer-hosted-by-tuikit)
defines an owned composer and a same-version public-API recovery. That recovery
was first implemented in an isolated throwaway spike; its measurements follow the
historical stock-editor evidence below. The original failures are retained.
No production adapter, package reference, launch selector, or accepting ADR was
added. PrettyPrompt/Spectre remains the existing interactive frontend.

### Recorded evidence

The temporary TUIKit spike was removed after the production adapter and focused
regressions replaced it. This section retains the package identities, hashes,
measurements, failures, and terminal observations gathered before removal. The
production project and tests now provide the executable verification surface.

Environment: Windows 11, reported OS build `10.0.26200.0`, x64, .NET SDK
`10.0.302`, runtime `10.0.10`. These are **headless** packaged-binary checks using
`HeadlessBackend`, not Windows Terminal or physical-terminal evidence. Durations
use `Stopwatch` around the checks and exclude compilation and package restore.

| Check | TUIKit 0.9.0 | TUIKit 0.10.1 |
|---|---|---|
| 10 KiB multiline bracketed paste: one event, exact normalized newlines, no implicit submit | PASS (46 ms) | PASS (44 ms) |
| 100 KiB multiline bracketed paste: same assertions | PASS (10 ms) | PASS (9 ms) |
| Enter submits, Ctrl+Enter inserts a newline, repeated Stop | PASS, included above | PASS, included above |
| Backspace after one emoji | FAIL: leaves an unpaired UTF-16 high surrogate | Same failure |
| Left then insert a character before an emoji | FAIL: caret moves inside the surrogate pair; insertion corrupts the draft | Same failure |
| Render a focused emoji with the caret at Home | FAIL: caret overlay replaces the grapheme with an unpaired-surrogate cell | Same failure |
| Render 80 characters in a 40-column, four-row composer | FAIL: no wrapping/horizontal scrolling; trailing text and caret are invisible | Same failure |
| Complete initial precheck duration | 67 ms | 65 ms |

The final pre-integration probe additionally asserted exact extra newlines for Ctrl+Enter
(`CSI 13;5u`) and Shift+Enter (`CSI 13;2u`). These remain headless input-decoder
checks; they make no claim about which chords a physical terminal reports.

The editor failures are distinct from ordinary platform newline normalization,
which the paste assertions explicitly allow. The rendering probe inspects public
`CellBuffer` cells, and strict UTF-8 encoding detects invalid UTF-16 without
replacing it silently. All tested widget behavior uses public APIs. No upstream
source is compiled, no private field is read, and no reflection-based widget
access is used.

The four-region layout is wired to exercise the input host, but its presence
does **not** certify fixed-footer snapshots or modal behavior. The fixture stops
at the first failing gate rather than building the remaining frontend around an
unproven editor. A thin key filter can fix submit/newline routing; it does not
fix `TextEditor`'s code-unit caret model or its rendering. No replacement editor,
private implementation dependency, or vendored patch was introduced to waive
the planned multiline-editor gate.

### Package provenance

Both packages were downloaded from the official NuGet v3 flat-container source.
Their metadata points to `https://github.com/jchristn/TUIKit`, declares MIT, and
contains compiled `net10.0` assets with an empty dependency group. Restore and
compilation succeeded against those assets. The archives have no `build/`,
`buildTransitive/`, install script, or native runtime payload. Root product
Central Package Management and release/legal inputs are unchanged.

| Candidate | Recorded upstream commit | NuGet archive SHA-256 |
|---|---|---|
| [0.9.0](https://www.nuget.org/packages/TUIKit/0.9.0) | `c1bf529ae966e5f083b1b5a76d12cef252ee0280` | `04A6CC4561315BD2D6200E63F30C42DD6ED67F53DEB1CDF3232FC7DE817763C6` |
| [0.10.1](https://www.nuget.org/packages/TUIKit/0.10.1) | `820e8ef5e199549a119360647c13427d0c36d63e` | `88B84C1A50788D6E1EAD387F30F1CC48F2E82B6722AB0D16F9FCFC4B64D5A07A` |

The 0.10.1 retry was evaluated because its published changelog fixes teardown on
closed/non-writable output, a required failure-path concern. It does not fix the
editor: `src/TUIKit/Widgets/TextEditor.cs` at both package-recorded commits has
SHA-256 `2C90D780D1FB41D2AD784397700BD140672456962402D05D108C5E9CDF48723D`.
Source inspection corroborates the packaged-binary results: Backspace, Left,
and the caret overlay use individual UTF-16 code units; Render draws each
logical line at column zero without horizontal scrolling or wrapping.

The packages also bundle FIGlet font data with `fonts/LICENSE.figlet.txt` and
`fonts/REMOVED.txt`. Metadata inspection is not a complete review of the font
terms or release attribution. **Full legal/SBOM/notice closure and six-RID
publication were not performed or approved.**

### Existing frontend baseline and remaining work

The existing CoreRuntime regression project built with zero warnings/errors;
its complete existing suite passed: **276 passed, 0 failed**, 2.409 seconds.
That suite covers the shared shell/recording surface, PrettyPrompt behavior,
commands, Markdown, and semantic output. An existing uncommitted change to
`tests/Threadsmith.CoreRuntime.Tests/Milestone1Tests.cs` was present before this
work and was preserved; it is included in these baseline results. This baseline
is not proof of full cross-frontend conformance, and the full solution was not
certified by this run.

Inspection confirmed the implemented public names are `IInteractionSurface`,
`ComposerRequest`/`InteractionInput`, `InteractionSelectionRequest` with stable
option IDs, `PresentationBatch`, `SessionStatusSnapshot`, `InteractionActivity`,
and `IActiveRunInputLease`. The coordinator owns model, tools/consent,
extensions/actions, resumable-session, MCP profile/capability/account-action,
policy, repository trust/upgrade/solution/initialization selectors. Theme
selection is a frontend contribution; plan and mutation reviews currently use
secondary composer reads. A future implementation must preserve those actual
contracts and regenerate complete characterization traces rather than assume
every review is already a `SelectAsync` request.

At the initial stock-editor checkpoint, not run: complete selector differential traces; deterministic snapshots at all
four sizes; shared Markdown/semantic rendering; active-run steering and buffered
drafts; bounded transcript and UI queue; off-screen selection/copy and F12;
60-second saturation, latency/CPU/memory measurements; physical Windows and
Linux/macOS terminal matrix; failure/restoration matrix; six-RID release closure;
complete solution verification. None is marked passing or waived. The planned
owned composer may address these blockers on the same exact
published package; it must pass the original behavioral assertions and the
**entire** spike before production integration. At this checkpoint, ADR-15 and the default frontend were unchanged.

### Owned-composer and retained-host recovery (2026-09-04)

The recovery now implements its own `ComposerBuffer` and `TuiKitComposer` using
public widget/focus/mouse/surface APIs. StringInfo boundaries keep edits and
undo/redo on complete text elements; the four-row view wraps at grapheme
boundaries and scrolls to the caret. Paste is one bounded delta operation.
`--stock-editor` preserves the original four failures without weakened assertions.

The isolated `HostFixture` adds the four regions, a changing fixed footer,
searchable stable-ID modal with full-label detail view, bounded FIFO admission,
retained transcript bounds, detached scrolling/new count, explicit copy/F12,
simulated active-run signals and separate drafts. Enhanced Escape normalization
and explicit modal paste routing were necessary with the pinned public decoder.
The shared coordinator's two MCP indexing guards and switch-account Escape guard
were fixed for both frontends. The existing user test-file diff remains unchanged.

| Executed check | Result and scope |
|---|---|
| Owned composer + host fast prechecks, Windows x64 | PASS, most recent run 334 ms; no new unit-test project |
| Same self-contained prechecks, Ubuntu/WSL2 | PASS, 758 ms; Linux kernel `6.6.87.2-microsoft-standard-WSL2` |
| Owned Unicode editing | PASS for emoji, combining marks, flag, skin modifier, ZWJ sequence and CJK; exact undo/redo/selection replacement and line joins |
| Exact 10/100 KiB bracketed paste | PASS; normalized CR/LF, one insertion/undo operation, no implicit submission |
| Retained snapshots | PASS for valid cells and last-row sentinel at 40x12, 80x24, 120x40, 200x60, modal selection/cancellation and resize; these compose through the same public layout/widget APIs, not an ANSI screen emulator |
| Queue / transcript / input | PASS generic FIFO/drain, line/byte bounds, eviction notice, detach/new-count/reattach, stable-ID filter, ordinary/steering separation, repeated Enter, double Escape, F12 and explicit bounded OSC 52 |
| Measured 100 Hz saturation | PASS: 6,000 updates in 60.0 s, 509 keys, max key 47.4 ms, max combined modal/resize 91.8 ms, queue drained to 0; observed queue peak 7 of 64 |
| Resource observations during saturation | CPU 4.2 s; sampled managed heap 1.6–18.9 MiB; 56,320 retained UTF-8 bytes, 4,976 evictions. These are observations, not a separate idle/steady-state memory certificate |
| Candidate self-contained publishes | PASS: win-x64, win-arm64, linux-x64, linux-arm64, osx-x64, osx-arm64. Cross-platform asset/publish verification, not six native executions or product release closure |
| Existing CoreRuntime regression | PASS: 276/276, zero build warnings/errors; 8.667 s while the load probe was also running |

The first timer-based load attempt produced 6,000 updates over 75.3 s due to
coalesced timer ticks (max key 69.9 ms, modal/resize 106.5 ms). It does **not**
count as the required 100 Hz run. The corrected producer schedules against
elapsed monotonic time, catches up bounded pending work, and asserts the 60-second
duration. Build/restore is excluded from all printed diagnostic durations.

The real Linux `ConsoleBackend` was exercised inside `script(1)` under WSL2.
`/quit` exited successfully and `backend-check.sh` verified exact `stty -g`
restoration. Exit output contained bracketed-paste/mouse disable, cursor-show,
and alternate-screen-leave sequences. A forced F9 failure also restored the
same terminal attributes before reporting the deliberate exception. This is
also true for the Ctrl+C exit: the wrapper compared identical before/after
terminal attributes. This is
PTY backend evidence; it is **not** the physical Windows Terminal/Linux visual,
copy/selection, shell scrollback or full restoration matrix. This session's
Windows PTY wrapper was reported as noninteractive by `ConsoleBackend` and was
rejected rather than silently falling back.

### Candidate licensing disposition and remaining acceptance

The [digest-bound candidate licensing bundle](../../eng/release/legal/TUIKit-0.10.1-candidate/README.md)
preserves the pinned TUIKit MIT source license, bundled attribution/removal
statements, all 83 embedded font headers, per-resource hashes and the full WTFPL
v2 license. Three font headers name MIT, six name WTFPL v2, eighteen contain
modification-credit permission, and fifty-six have no explicit license grant
in their declared comment header. WTFPL is permissive; the missing header labels
are inventory observations, not evidence of prohibited redistribution. No
upstream contact is required or authorized. Product package graph, canonical
release evidence and generated product SBOM will be updated with integration,
preserving the supplemental font notices through the existing release process.

The full gate is still pending: shared Markdown/semantic projection, neutral
themes and retained-status scheduling, complete editor history/clipboard/key
inventory, true coordinator/surface differential traces for all commands and
selectors, physical selection fidelity, idle/steady-state memory checks,
the complete physical terminal/exit matrix and product release
closure. The upstream clipboard reader trims trailing newlines and performs an
unbounded `ReadToEnd` before its timeout, so exact platform clipboard support
must not reuse it unchanged. Generic fixture behavior is not full product parity.
The parity inventory was incorporated into Plan 100's acceptance criteria. The
production adapter, named launch selectors and acceptance ADR were not added
before the mandatory gates passed.

### Editor and licensing follow-up (2026-09-04)

The licensing documentation now identifies WTFPL v2 as permissive and removes
any proposed upstream-contact prerequisite. Nothing was posted; the owner
explicitly forbids external posting. The candidate JSON records notice-inventory
status, not a fabricated product release approval. All five preserved notice
file digests and the 83-resource inventory still verify.

The owned editor now includes bounded session-local history with draft
restoration and the existing prefix/substring search priority; grapheme/word
navigation; logical-line cut/delete, smart Home, indentation and undo/redo;
Alt+Enter/Ctrl+Enter newline and Ctrl+Alt+Enter submission; empty Ctrl+T; and
Ctrl+L without deleting retained transcript. Enhanced-protocol Enter, Tab,
Escape and Backspace are normalized before routing. The modifier precheck
caught and corrected an enhanced Shift+Enter decoding difference.

An owned asynchronous clipboard reader preserves exact text/trailing newlines,
uses strict UTF-8, a 1 MiB byte limit and a two-second total deadline, drains
stderr without retaining it, and terminates/closes its helper on failure or
cancellation. Clipboard paste reaches the active composer or selector filter;
late completion cannot move into a different input destination. The fixture
checks exact stream content, bounds, UI handoff and one-edit undo, without
reading the operator's clipboard during automated checks. Physical OS clipboard
acceptance is still unrun. Plan 100's exact admitted paste contract preserves
indentation/tabs rather than applying PrettyPrompt's automatic dedent filter.

Transcript selection now supports keyboard ranges and highlights only the
selected graphemes, including both cells of a wide character. The fast fixture
also verifies clear-screen scrollback retention and selector clipboard routing.
The latest Windows run passed all fast checks in **367 ms**, with zero build
warnings/errors. No new unit-test project or long-running unit test was added.
Earlier Linux, six-RID and saturation results above predate this follow-up;
they must not be represented as validation of these later editor changes.

### Plan 100 product integration (2026-09-04)

The user directed integration to continue after recovery. Product work is on the real local branch `feat/plan-100-tuikit-frontend`; nothing was posted, pushed, staged, or committed. `Threadsmith.Tui.TuiKit` now references only Interaction plus exact TUIKit 0.10.1. App selects TUIKit for bare `--tui` and `--tui=tuikit`; `--tui=original` retains PrettyPrompt/Spectre. The same composition supplies all coordinator dependencies. MCP/authentication keep precedence.

The product has a bounded UI queue, retained transcript/activity/composer/footer, owned grapheme editor, separate prompt-purpose drafts/history, stable-ID filtered selectors with full details, explicit clipboard/native-selection/link access, shared themes, shared Markdown layout semantics, and unconditional teardown. Shared opt-in status refresh keeps usage/context current without terminal-owned host queries. Ctrl+C cancellation is translated to exit 130 even when the coordinator returns normally after cancellation.

Verification completed during integration:

- Solution build: zero warnings/errors.
- CoreRuntime: 283 pre-existing/new checks passed, then the additional failure-cleanup case passed with all eight focused adapter cases. The final focused run took 1.395 seconds total; individual cases ranged from 0.0009 to 0.4673 seconds. No new long-running unit fixture was added.
- Architecture/startup: 146 passed; SessionStatus: 16; RepositoryLifecycle: 29; Planning: 98; Mutations: 59; Validation: 33; ConversationContext: 81. Existing longer suites were retained unchanged.
- All six supported self-contained local product publishes included both frontend assemblies. Each generated exact package-closure notices/SPDX with MIT, permissive WTFPL v2, and embedded font attributions. Existing recorded runtime version 10.0.4 was supplied explicitly for these legal-closure checks; no new runtime approval was asserted.
- Release contracts passed after supporting both SDK representations of exact runtime packs (libraries and download dependencies). Generation still rejects wrong/unpinned versions and wrong RIDs; supplemental notice files are required.
- Real Linux ConsoleBackend in an isolated PTY/profile entered and left alternate screen and restored exact `termios` attributes after command/theme interaction followed by `/quit`, and after Ctrl+C. The final published product returned 0 for `/quit` and 130 for Ctrl+C, with exact terminal attributes restored in both cases. Windows published help/version and invalid/duplicate frontend selectors also passed without initializing a TUI.

Physical Windows Terminal/macOS input, OS clipboard interoperability, and the complete operator workflow matrix have not been executed in this environment. MTP-257 and Scenario AR remain the explicit acceptance procedures; headless/PTY and cross-publish results do not claim physical operator sign-off. The earlier stock-editor failures are retained as diagnostic history and do not describe the product-owned composer.

### Plan 100 final code and performance review (2026-09-05)

The final review removed the copied Markdown and session-status implementations from both terminal adapters. Their terminal-neutral algorithms now live once in `Threadsmith.Interaction`, with small backend-specific Unicode-width adapters and no terminal-library types crossing the shared boundary. Theme roles and command contribution code likewise remain shared rather than copied between frontends.

The retained frontend now caches fixed status, prompt, help, selector, activity, eviction-notice, and visible transcript graphemes. The transcript cache is bounded to 512 wrapped rows and invalidates on streaming replacement, resize, and eviction. Streaming row removal, selection lookup, and large-draft vertical caret movement use ordered searches instead of full retained-buffer scans. Composer UTF-8 size enforcement tracks edit deltas; clipboard reads reuse a pooled buffer; semantic styles use an enum-indexed palette; and the render loop runs at 30 FPS while transient activity changes at the existing maximum four visual updates per second. No unbounded cache or queue was introduced.

The lifecycle review also converted a closed presentation channel into a controlled cancellation outcome, made active-input disposal join its pending read without a race, and restored original-frontend double-Escape parity: an Escape outside the 850 ms chord window re-arms cancellation instead of being ignored. Initial semantic loading retains one visibly queued ordinary submission and preserves subsequent draft text.

Final verification after these changes: a non-incremental solution build passed with zero warnings and errors; all 1,697 discovered tests completed with 1,692 passing, five expected environment/live-integration skips, and no failures; the focused TUIKit/Markdown, session-status, and architecture runs passed 35, 16, and 50 checks respectively; and every release contract passed, including exact TUIKit package/license evidence, supplemental embedded-font notices, SPDX generation, runtime legal staging, and aggregate closure. No test was added during this optimization pass. MTP-257 remains the required physical-terminal sign-off.
