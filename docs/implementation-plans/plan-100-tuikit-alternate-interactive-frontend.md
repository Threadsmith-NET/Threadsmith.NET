# Implementation Plan 100: Selectable TUIKit Interactive Frontend

**Status:** Planned.

**Delivery track:** Product capability — optional alternate full-screen interactive frontend.

**Prerequisites:** [Plan 98](plan-98-frontend-neutral-interaction-coordination.md) has been implemented in full, including `Threadsmith.Interaction`, `IInteractionSurface`, shared command/review/run/session/repository coordination, shared semantic presentation, shared session-status assembly, shared Markdown document generation, and the terminal-free interaction conformance harness. The existing PrettyPrompt/Spectre frontend remains working and is the default interactive frontend.

**Strategy source:** [Shared implementation context](00-shared-context.md), especially UI-as-projection, host-owned authority, external-framework isolation, bounded immutable DTOs, cancellation propagation, and headless parity.

**Related contracts:** [ADR-15](../architecture/adr-15-conversation-first-terminal.md), [Plan 03](plan-03-tui-application-shell.md), [Plan 24](plan-24-tui-semantic-styles-theme-contracts.md), [Plan 25](plan-25-configured-themes-theme-command.md), [Plan 26](plan-26-tui-session-footer.md), [Plan 63](plan-63-markdown-console-rendering.md), [Plan 96](plan-96-active-run-steering-and-double-escape.md), [Plan 98](plan-98-frontend-neutral-interaction-coordination.md), [ADR-49](../architecture/adr-49-public-release-license-compliance.md), and [release-license evidence](../../eng/release/release-license-evidence.json).

**Evaluated upstream baseline (2026-09-04):** [TUIKit](https://github.com/jchristn/TUIKit) 0.9.0, an MIT-licensed, pre-1.0 .NET TUI framework. Its upstream README and [changelog](https://github.com/jchristn/TUIKit/blob/main/CHANGELOG.md) describe `net10.0` support, a retained full-screen host, docked regions, a multiline editor, typed modals, a fixed status bar, raw terminal input, bracketed paste, mouse selection/capture control, terminal restoration, and a headless backend. The same documents explicitly classify the API as alpha and state that the real console backend remains manually validated rather than benchmarked. The version and package facts must be revalidated by the spike before any product reference is added.

---

## 1 Objective

Add a second, explicitly selected interactive frontend for Threadsmith.NET using TUIKit while retaining the existing PrettyPrompt/Spectre frontend.

The new frontend provides a retained full-screen conversation surface with:

- a scrollable transcript;
- a multiline composer;
- a fixed activity row;
- a fixed bottom session-status bar;
- typed modal selectors and confirmations;
- safe copying/selection and scroll-lock behavior;
- shared Markdown, commands, approvals, steering, cancellation, repository, and session behavior supplied by `Threadsmith.Interaction`.

Launch selection is explicit and backward compatible:

```text
threadsmith --tui             # existing PrettyPrompt/Spectre frontend
threadsmith --tui=pretty      # existing PrettyPrompt/Spectre frontend, explicitly named
threadsmith --tui=tuikit      # new TUIKit frontend
```

Bare `--tui` continues to mean the existing frontend. Headless invocation remains the default when no TUI switch is supplied. Plan 100 does not replace, rename, deprecate, or silently redirect the current frontend.

## 2 Architectural Context and Suitability

### 2.1 Post-Plan-98 boundary

Plan 98 makes interactive policy reusable. The TUIKit frontend therefore implements an adapter, not a second shell:

```text
                            Threadsmith.App
                                  |
                      InteractiveFrontendKind
                         /                 \
                        v                   v
          Threadsmith.Tui          Threadsmith.Tui.TuiKit
        PrettyPrompt/Spectre              TUIKit
                         \                 /
                          v               v
                         Threadsmith.Interaction
                                  |
                   existing host commands/projections
```

Both frontends consume the same coordinator. Neither frontend owns trust, policy, plans, mutation approval, tool authority, repository lifecycle, session lifecycle, run identity, persistence, model selection, or validation.

### 2.2 TUIKit fit

| Requirement | Upstream capability | Plan 100 disposition |
|---|---|---|
| fixed footer | docked regions and `StatusBar` | strong fit; status occupies the bottom row for the complete TUIKit session |
| live transcript | retained thread-safe `Pane`, bounded ring, scroll lock, “new” indicator | strong fit; consume shared presentation batches rather than TUIKit's independent agent model |
| multiline input | `TextEditor`, input routing, bracketed paste, submit-key helper | promising; exact 10 KiB/100 KiB multiline paste and newline behavior are hard spike gates |
| selectors/reviews | awaitable typed modals and `SelectAsync`/`PromptAsync`/`ConfirmAsync` | strong fit; visible labels map back to Plan 98 stable option IDs |
| semantic output | styled text, tables, panes, themes, Unicode width | strong fit; adapter maps Threadsmith roles and documents without exposing TUIKit types |
| Markdown | upstream Markdown renderer and streaming helper | not used for parsing or answer collection; Plan 98 remains authoritative |
| command routing | upstream command registry and slash routing | key-binding registry may be used; slash dispatch remains in Plan 98 |
| terminal support | Windows VT, Unix `termios`, resize, enhanced keys, bracketed paste | promising; physical terminal verification remains mandatory |
| copy/selection | TUIKit selection, OSC 52, mouse-capture toggle, native-selection handoff | promising but behavior differs from ADR-15; must pass spike and be explicitly documented |
| deterministic tests | headless backend, snapshots, input record/replay | strong fit for adapter tests |
| operational maturity | pre-1.0 alpha, breaking minor releases, no benchmark suite | material risk; exact pin, isolation, spike gate, and upgrade policy are mandatory |

TUIKit is conditionally suitable. Its retained layout directly solves the fixed-footer limitation, and Plan 98 removes the largest architectural risk: duplicating interaction policy. Its alpha status and full-screen terminal ownership make a measured spike necessary before adding it to production.

### 2.3 Relationship to ADR-15

ADR-15 remains authoritative for the default PrettyPrompt frontend. It explicitly requires a new ADR and real-terminal latency evidence before a full-screen surface is adopted. Plan 100 therefore requires:

1. a passing TUIKit spike;
2. recorded real-terminal evidence;
3. a new ADR governing the optional TUIKit frontend;
4. no amendment or weakening of the existing PrettyPrompt/native-scrollback contract.

The new ADR must describe the frontends as parallel choices. It must not supersede ADR-15 globally.

## 3 Scope

- Build and run a focused `Spike.TuiKit` before production implementation.
- Add one production adapter project, `src/Threadsmith.Tui.TuiKit/`.
- Add one focused test project, `tests/Threadsmith.Tui.TuiKit.Tests/`.
- Pin one exact TUIKit version through Central Package Management after the spike passes.
- Add `--tui=pretty` and `--tui=tuikit`, preserving bare `--tui`.
- Compose exactly one interactive frontend per process.
- Implement `IInteractionSurface` using TUIKit public APIs only.
- Implement the fixed transcript/activity/composer/status layout defined in section 6.
- Map shared presentation text roles, Markdown documents, lifecycle blocks, diffs, activity, status, and safe-source output to TUIKit primitives.
- Implement ordinary composer input, multiline paste, selectors, confirmations, steering, double-Escape cancellation, `Ctrl+C`, buffered active-run input, resize, scroll lock, selection, copying, theme changes, and clean terminal teardown.
- Reuse the same validated theme catalog and preference semantics across both frontends without duplicating unsafe configuration parsing.
- Preserve every host command, approval, repository, session, run, Markdown, and security contract from Plan 98.
- Add TUIKit package/license/SBOM/release-payload evidence.
- Add product acceptance, manual verification, user, keyboard, architecture, and DOX documentation for the additional frontend.

## 4 Non-Scope

- Replacing or removing `Threadsmith.Tui`, PrettyPrompt, or Spectre.Console.
- Changing which frontend bare `--tui` selects.
- Automatically selecting TUIKit by terminal capability, operating system, configuration, prior choice, or package availability.
- Persisting a preferred frontend in user or repository configuration.
- Falling back silently from a failed TUIKit launch to PrettyPrompt.
- Sharing TUIKit widgets, input records, styles, colors, cells, buffers, geometry, or terminal backends through `Threadsmith.Interaction`.
- Reimplementing slash-command parsing, help text, approval decisions, session/repository workflows, event projection, answer collection, or Markdown parsing in the new adapter.
- Using TUIKit's `CommandRegistry.ResolveSlash`, `StreamingTranscript` Markdown finalization, raw markup parser, or arbitrary renderables as an alternate policy path.
- Adding sidebars, file browsers, dashboards, charts, telemetry panes, image protocols, command palettes, menus, key-binding editors, or a general IDE layout in the first TUIKit frontend.
- Changing headless behavior or output.
- Reformatting or reparsing durable historical transcript data.
- Making the full-screen transcript a new durable store.
- Loading TUIKit from extensions or allowing extensions/repository content to register widgets, keys, commands, themes, or terminal handlers.
- Using floating package versions, Git submodules, source-vendored snapshots, or an unreviewed build of TUIKit `main`.
- Upgrading TUIKit after the production version is pinned without rerunning the spike and release checks.

## 5 Assumed Starting State

Plan 100 is written against the architecture after Plan 98, even if the branch containing this planning document has not yet implemented it.

The starting state is assumed to include:

- `Threadsmith.Interaction` as the sole implementation of interactive coordination;
- public immutable `IInteractionSurface` request/result contracts;
- shared slash-command routing and help generation;
- shared sequential selection and review coordination using stable option IDs;
- shared run, steering, cancellation, and buffered-input coordination;
- shared repository-open and session-transition workflows;
- shared session-status snapshots;
- shared semantic presentation batches and roles;
- shared bounded Markdig parsing, immutable Markdown documents, answer collection, and safe fallback;
- a terminal-free recording surface/conformance harness;
- a thin `Threadsmith.Tui` PrettyPrompt/Spectre adapter;
- bare `--tui` composition of that existing adapter.

If Plan 98 is incomplete, Plan 100 implementation must not compensate by copying its missing behavior into the TUIKit project. Complete Plan 98 first.

## 6 Proposed Production Design

### 6.1 Project boundary

`Threadsmith.Tui.TuiKit` references:

- `Threadsmith.Interaction`;
- TUIKit at the exact approved version;
- framework/BCL assemblies required by the adapter.

It must not reference:

- `Threadsmith.Tui` or PrettyPrompt/Spectre.Console;
- Persistence implementations;
- model-provider SDKs;
- Roslyn/MSBuild implementations;
- extension implementations;
- execution internals not already represented by Interaction contracts.

`Threadsmith.Interaction` must not reference TUIKit. TUIKit types terminate inside the new adapter project.

### 6.2 Launch contract

Replace the startup boolean as the source of truth with a host-owned enum such as:

```text
InteractiveFrontendKind.None
InteractiveFrontendKind.Pretty
InteractiveFrontendKind.TuiKit
```

`UseInteractiveTerminal` may remain as a derived compatibility property while in-repository callers migrate.

Parsing rules:

- no `--tui` switch → `None`;
- `--tui` → `Pretty`;
- `--tui=pretty` → `Pretty`;
- `--tui=tuikit` → `TuiKit`;
- frontend IDs are case-insensitive and canonicalized for diagnostics;
- an empty or unknown `--tui=` value returns an actionable side-effect-free command-line error and exit code 2;
- more than one TUI selector returns an ambiguity error, even when values are equivalent;
- `--tui tuikit` retains current parsing: `tuikit` is request text, not an optionally consumed switch value;
- `--help` documents all three forms;
- headless MCP management retains precedence and never initializes either TUI, including with `--tui=tuikit`;
- Codex authentication retains its current interactive-browser meaning and does not initialize a TUI merely because a frontend was selected.

There is no `--tuikit` alias and no configuration default in Plan 100.

### 6.3 Full-screen layout

The first production layout stays conversation-focused:

```text
┌──────────────────────────────────────────────────────────────┐
│ scrollable conversation transcript                         │
│                                                              │
│                                      ↓ 3 new (when detached) │
├──────────────────────────────────────────────────────────────┤
│ activity: Running — Enter to steer; Esc Esc to stop.        │
├──────────────────────────────────────────────────────────────┤
│ repository > multiline composer                             │
│              ...                                             │
├──────────────────────────────────────────────────────────────┤
│ branch | model/reasoning | context | tokens | repo/folder   │
└──────────────────────────────────────────────────────────────┘
```

- The transcript fills remaining height.
- The activity region is one fixed row immediately above the composer.
- The composer is a fixed four-row region in the initial implementation and scrolls internally for larger drafts.
- The session-status bar is one fixed bottom row and is never appended to transcript history.
- Modal selectors/prompts overlay the layout and trap focus without replacing status truth.
- A terminal below the spike-approved minimum dimensions shows a bounded “terminal too small” screen and preserves the draft/status/transcript state until resizing recovers.
- Width-aware status omission follows the shared Plan 26 priority, but the row remains fixed rather than being appended before each prompt.
- No sidebar or secondary tool panel is included.

The spike may adjust exact minimum dimensions or composer height when physical-terminal evidence requires it. Such a change must be recorded before production implementation and must not alter the fixed bottom status requirement.

### 6.4 `IInteractionSurface` implementation

Add `TuiKitInteractionSurface`, which owns one `TuiApplication` and implements the Plan 98 port.

Surface calls are marshalled to the TUIKit loop using its public scheduler. Each call completes only after the corresponding state mutation has been accepted on the UI loop. The render loop may coalesce frames, but it may not reorder shared presentation batches, selection results, status updates, or input completions.

The adapter owns:

- TUIKit lifecycle and disposal;
- regions/widgets and focus;
- conversion from Threadsmith presentation values to TUIKit styled content;
- frontend-local editor buffers and selection state;
- modal construction and stable-ID mapping;
- active-run key/paste capture;
- terminal capability handling;
- the single effective Ctrl+C/interrupt route;
- theme-to-TUIKit style mapping.

It does not inspect host projections or dispatch host commands directly.

### 6.5 Transcript projection

- Append shared `PresentationBatch` items in order through one adapter queue.
- Use TUIKit pane batches or equivalent public atomic operations so one Threadsmith batch is not visually interleaved with another.
- Maintain semantic line/block identity for mutable activity only; completed lifecycle output becomes immutable transcript content.
- Preserve Threadsmith text, roles, links, blank-line ownership, diff markers, source labels, duration text, and answer-boundary order.
- Preserve scroll position when detached from the tail and show an accurate new-item count.
- Returning to the bottom reattaches to live output.
- Keep a bounded visible transcript by both logical line count and UTF-8 byte estimate. When older visible content is evicted, retain a bounded first-row omission notice stating that durable session history remains authoritative.
- Never treat the pane ring as persistence or reconstruct domain state from it.
- A resize reflows current visible data without changing semantic order or duplicating content.

The exact visible line/byte caps are host-owned adapter constants selected from the spike. They are not repository configuration in Plan 100.

### 6.6 Markdown rendering

The TUIKit frontend consumes the shared `MarkdownDocument` generated by Plan 98.

- Do not pass raw model source to TUIKit's Markdown parser.
- Do not use TUIKit `StreamingTranscript` to recollect or finalize model answers.
- Map shared headings, paragraphs, emphasis, strong, strikethrough, lists, tasks, quotes, inline/fenced code, links, tables, and thematic breaks to styled TUIKit content.
- Preserve the shared safe-link destination and never enable automatic links from untrusted text.
- Preserve inert HTML/media behavior.
- Preserve source-mode and safe-source fallback exactly as delivered by the shared presentation item.
- Preserve displayed structural meaning under `NO_COLOR` and ASCII fallback.
- Allow TUIKit-native reflow to the current pane width, but do not alter raw/durable Markdown or re-run parsing on resize.

Specialized TUIKit widgets such as `Table` may be used only when they preserve all shared content, bounds, selection, narrow-width fallback, and plain-text meaning. Otherwise render through styled lines.

### 6.7 Status and activity

- Store the latest shared `SessionStatusSnapshot` and render it into the fixed bottom row.
- Update the row in place when a new snapshot arrives; do not append status rows to the transcript.
- Preserve the shared values and estimate/unknown semantics for folder, repository, branch, model, reasoning, context, and token usage.
- Recompute only frontend layout on resize; do not synchronously query Git, models, context, usage, or repositories from the render loop.
- Render the current `ActivityPresentation` in the fixed activity row, replacing it in place at the existing bounded refresh cadence.
- Clear or replace transient activity at the same shared lifecycle boundaries.
- Rendering timers remain presentation-only and do not publish domain events.

### 6.8 Composer and ordinary input

- Use the multiline `TextEditor`, not a single-line prompt field.
- `Enter` submits the current ordinary composer through the shared coordinator.
- `Shift+Enter` inserts a newline where the terminal reports the modified chord.
- `Ctrl+J` is the portable newline fallback and is documented for the TUIKit frontend.
- Bracketed paste inserts one exact operation, retains embedded CR/LF as normalized composer newlines, and never submits implicitly.
- `Ctrl+V`/platform paste and `Shift+Insert` use the same bounded paste path where the terminal reports them.
- Input limits, cancellation, empty submission, slash-command handling, repository prompt label, and successful `/open` label changes remain shared contracts.
- Submitting swaps the draft into an immutable shared input result before clearing the editor.
- A failed submit handoff restores the exact draft and caret/selection where public TUIKit APIs permit; otherwise it preserves the exact draft with a documented caret fallback.

### 6.9 Active-run steering and cancellation

The full-screen composer does not create a second active input policy.

- While a run is active, the ordinary composer remains mounted but does not submit ordinary requests.
- A standalone `Enter` produces one semantic `SteerRequested` signal through the Plan 98 active-run lease.
- Repeated Enter reuses the same pending steering request.
- Two unmodified Escape presses within the shared 850 ms window produce `CancelRequested`.
- `Ctrl+C` requests cooperative cancellation through one idempotent Threadsmith cancellation route.
- Other typed text and bracketed-paste bursts enter an adapter-owned pending ordinary-input buffer and are restored into the next ordinary composer.
- When the shared coordinator reports a safe steering boundary, the editor switches to the `steer >` context without consuming the pending ordinary draft.
- Empty/cancelled steering resumes the run; submitted steering and `/agents` use the shared coordinator.
- The ordinary pending draft is restored after steering completes or the run terminalizes.
- Model/tool output cannot take focus from the steering editor or render over a modal.

The spike must prove that TUIKit input precedence, key sequences, paste routing, and Ctrl+C policy can implement these semantics through public APIs.

### 6.10 Selectors, reviews, and prompts

- Map each shared `SelectionRequest` to a typed TUIKit modal.
- Keep the stable option ID separately from its bounded visible label.
- Return selected/cancelled only once.
- Escape and modal close are cancellation, never the first/default option.
- Plan review, mutation review, trust, solution, model, extension, tool, MCP, skill, policy, theme, repository, and session selectors remain sequential.
- Confirmation labels and warnings retain shared wording and risk provenance.
- Output/status updates may refresh behind an open modal but cannot alter its option identities, steal focus, dismiss it, or make a stale choice authoritative.
- Rejected stale identities fail closed through the shared coordinator.

TUIKit is an additional presentation of the existing interaction surface, not a reduced command-only mode. Every selection list available through the implemented Plan 98 coordinator must remain available and usable in the TUIKit frontend. A user must not have to memorize or type an ID merely because TUIKit was selected.

At implementation start, regenerate the selection inventory from the implemented Plan 98 characterization traces and current PrettyPrompt behavior. The inventory below is the minimum known baseline; any selector added before Plan 100 starts is included automatically rather than treated as new scope.

| Existing workflow | Required TUIKit parity |
|---|---|
| model selection | list every currently available model in host order; preserve the active marker, model/provider identity, context and output limits, reasoning capability/levels, cancellation, repository persistence result, and status refresh |
| repository tool enablement | list every tool with enabled/disabled/consent-required state, display name, stable ID, category, source, and essential marker; preserve `Back`, essential-tool protection, consent disclosure/confirmation, enable/disable behavior, and refreshed state after each action |
| extension management | list every discovered extension with loaded state, name, version, status, and tool count; preserve load/unload action choice, cancellation/`Back`, diagnostics, and refreshed state after each action |
| resumable sessions | list the same repository-confined sessions in the same host order with current/state/clone marker, timestamp, model/reasoning identity, and bounded preview; preserve cancellation and the exact selected session ID |
| MCP management | preserve profile lists, capability/resource/prompt lists and their state/kind metadata, enable/disable choices, switch-account mode, logout/revoke confirmation, and unconfirmed-revocation cleanup choice |
| repository open | preserve repository initialization choice, trust-level choice and upgrade choice, multi-solution selection, remembered-solution behavior, cancellation, and atomic failure semantics |
| policies and themes | preserve mutation-policy, plan-policy, and theme lists, current/active markers, descriptions and warnings, explicit cancel choice, persistence outcome, and immediate status/theme refresh |
| approvals and reviews | preserve current-message URL consent, direct-fetch approval, plan approve/reject/revise/cancel, mutation apply/discard, and every other Plan 98 approval branch with the same wording, risk context, follow-up text prompt, and fail-closed result |
| skills, hooks, and other contributed selectors | render every `SelectionRequest` exposed by the shared coordinator, including nested action choices, without introducing adapter-owned authority or skipping options |

All list selectors preserve the current keyboard interaction contract: a visible highlight, Up/Down navigation, Enter activation, Escape cancellation where cancellation is allowed, scrolling for lists taller than the modal, and incremental search/filtering equivalent to the current PrettyPrompt/Spectre selection prompt. Filtering changes only the visible view; it cannot change stable IDs, host ordering among matches, or the authoritative option set. Dynamic management lists such as tools and extensions remain open and refresh from authoritative state after an action exactly as the current workflow does.

Selection parity is semantic and informational, not pixel-identical. TUIKit may wrap, truncate with an inspectable continuation, or responsively rearrange bounded labels, but it may not omit state markers, warnings, descriptions, or identity information needed to make the same decision. When the terminal is too narrow to present a safe unambiguous choice, the modal must provide a scroll/detail path or refuse the decision safely; it must not silently shorten distinct choices into the same visible label.

### 6.11 Selection, copy, and scrolling

The TUIKit frontend deliberately has different transcript ownership from the PrettyPrompt frontend and must make that visible rather than pretending native scrollback is unchanged.

- Application mouse capture is enabled by default for pane focus, scrolling, and TUIKit text selection.
- The adapter supports TUIKit selection across retained transcript lines and explicit copy through the library's bounded public clipboard/OSC 52 path.
- `Ctrl+Shift+C` requests copy of the current application selection where the terminal does not intercept it.
- `F12` toggles mouse capture, matching TUIKit's documented sample convention, so users can hand drag-selection back to the terminal and then resume application mouse routing.
- The fixed footer shows the current mouse mode in a compact hint when space permits.
- Copy is always explicit and bounded; merely selecting text never emits OSC 52.
- Selection contains only text already visible to the operator and never hidden reasoning, raw tool arguments, secrets, or non-projected durable content.
- PageUp/PageDown scroll the transcript when it has focus; Home/End move to its start/end; returning to end clears the new-item count.
- Keyboard-only use remains complete when mouse support is unavailable or disabled.

The spike must confirm the exact public copy API and the behavior of common terminals that intercept `Ctrl+Shift+C`. If that chord cannot be observed reliably, retain F12/native selection and bind one documented non-conflicting application-copy chord selected in the spike; do not ship an advertised dead shortcut.

### 6.12 Theme reuse

Plan 98 intentionally left concrete theme behavior in the frontend because there was only one consumer. Plan 100 creates the second consumer and therefore justifies moving only the library-neutral portion into `Threadsmith.Interaction`:

- immutable semantic theme definitions keyed by shared presentation role;
- validated configured-theme catalog values;
- active session theme identity;
- user preference persistence semantics;
- the frontend-local `/theme` result contract.

PrettyPrompt maps the shared semantic theme to PrettyPrompt/Spectre styles. TUIKit maps the same theme to TUIKit styles and region backgrounds. Terminal-library colors and style objects stay in their adapters.

Preserve:

- current built-in/configured IDs and ordering;
- configuration precedence and validation;
- exact targeted persistence behavior;
- `/theme`, `/theme <id>`, and `/theme current` semantics;
- active-theme unchanged on persistence failure;
- `NO_COLOR` behavior;
- no historical transcript restyling guarantee for the inline frontend.

The TUIKit frontend may repaint currently retained cells when a theme changes because retained full-screen rendering makes that natural. This difference must be documented and has no effect on transcript or domain truth.

### 6.13 Terminal lifecycle and capability handling

- Construct and start TUIKit only after the launch selector chooses it.
- Use only public `ConsoleBackend`/`TuiApplication` lifecycle APIs.
- Ensure one effective Ctrl+C route reaches the existing process cancellation source exactly once.
- Await adapter shutdown and dispose TUIKit in `finally` before returning or allowing a fatal exception to reach `Program.Main`.
- Restore raw/cooked mode, cursor visibility, bracketed paste, enhanced-keyboard flags, mouse reporting, and alternate screen on normal exit, `/quit`, startup cancellation, `Ctrl+C`, double Escape, initialization failure, render failure, and unhandled exception.
- Make teardown idempotent under races between Threadsmith cancellation, TUIKit lifecycle callbacks, and process exit.
- Print terminal outcome/fatal text only after leaving the alternate screen.
- Suspend/resume and resize use public lifecycle signals and do not create domain events.
- Do not emit image protocols or arbitrary OSC sequences.

An explicit `--tui=tuikit` invocation requires an interactive input/output terminal. A non-TTY, `TERM=dumb`, or unsupported initialization fails with a concise actionable message and exit code 2; it does not silently switch to PrettyPrompt or headless mode. `NO_COLOR` is supported and changes styling only.

## 7 Mandatory TUIKit Spike

### 7.1 Purpose and isolation

Create `spikes/Spike.TuiKit/` and add it only to `spikes/Spikes.sln`. The spike may reference the implemented `Threadsmith.Interaction` contracts and the candidate TUIKit package. It remains outside `src/Threadsmith.sln`, release payloads, and product dependency graphs.

The spike is a decision gate, not a prototype to polish or merge wholesale. Record exact package identity/version, resolved dependencies, terminal/OS versions, measurements, failures, and PASS/FAIL in `docs/architecture/spike-notes.md`.

### 7.2 Candidate package gate

Start with exact TUIKit 0.9.0 because that is the evaluated upstream baseline. Verify:

- the package exists on the approved NuGet source;
- package metadata points to the expected upstream project;
- `net10.0` assets restore and compile without source builds;
- the resolved modern-target dependency closure matches the reviewed claim;
- the package and bundled assets are MIT-compatible and all notices are available;
- no install/build target executes unreviewed repository code;
- all six Threadsmith release RIDs can resolve/publish the managed dependency.

If 0.9.0 is unavailable or a later version is required for a blocking fix, update this plan's evaluated baseline, pin one exact replacement, and rerun the entire spike. Do not use a floating version or source checkout as production input.

### 7.3 Spike fixture

Build the smallest surface that exercises real risk:

- fill transcript pane;
- one mutable activity row;
- four-row multiline editor;
- fixed one-row status bar containing a monotonically changing sentinel;
- typed selector and confirmation modal;
- semantic presentation batches including Markdown, links, diffs, controls, Unicode, and long lines;
- scripted background output at model-chunk and activity-refresh rates;
- active-run Enter, repeated Enter, double Escape, Ctrl+C, and buffered paste/text;
- selection/copy, mouse-capture toggle, scroll detach/new counter/reattach;
- normal, cancellation, exception, resize, suspend/resume, and process-exit teardown.

### 7.4 Automated spike checks

Using TUIKit's headless backend:

- render deterministic snapshots at 40×12, 80×24, 120×40, and 200×60;
- prove the status bar remains on the last row through output, activity refresh, modal open/close, and resize;
- prove accepted presentation batches retain FIFO order;
- prove modal selection returns the original stable ID and Escape returns cancellation;
- prove unsafe controls and raw markup are inert;
- prove Markdown source is consumed only by the Threadsmith document generator;
- prove long Unicode and grapheme clusters neither corrupt the buffer nor split into invalid cells;
- prove detached scrolling retains position and counts new items;
- prove bounded ring eviction produces the omission notice;
- prove theme/`NO_COLOR` changes style without changing semantic text;
- prove repeated stop/dispose is safe.

### 7.5 Physical-terminal checks

Run on Windows Terminal and at least one real Linux or macOS terminal before PASS. When release access permits, run all three operating-system families. Record terminal name/version and remote/multiplexer conditions.

Required cases:

1. exact 10 KiB and 100 KiB multiline bracketed paste into the editor with no submission, loss, duplication, per-character latency, or newline stripping;
2. Enter submit plus Shift+Enter where supported and Ctrl+J portable newline;
3. 100 background presentation updates per second for 60 seconds while typing, scrolling, opening/cancelling a modal, and resizing, with no lost/reordered accepted item or sustained input lag;
4. fixed footer remains stable during output and at 40/80/120/200 columns;
5. scroll detach/new-count/reattach and transcript eviction notice;
6. TUIKit selection/copy across off-screen retained transcript, bounded OSC 52 behavior, and F12 mouse handoff to terminal-native selection;
7. `Ctrl+C`, double Escape, `/quit`, startup cancellation, forced adapter exception, and a second concurrent teardown request;
8. terminal state after every exit: normal echo, cursor, arrow keys, mouse wheel, paste, and shell scrollback work normally with no leaked escape mode;
9. wide Unicode, combining marks, emoji, long paths/model names, links, code, tables, and diffs;
10. SSH or tmux when available, including selection/copy limitations and capability fallback.

### 7.6 Performance gate

The spike must measure rather than rely on subjective impressions:

- input events continue to be accepted while presentation is saturated;
- the presentation queue returns to zero after the 100 Hz producer stops;
- no frame/update queue grows without a configured bound;
- no single ordinary key echo or editor action is delayed more than 100 ms in the controlled 120×40 saturation run, excluding OS scheduling outliers that are recorded and repeated;
- modal open/close and resize settle within 250 ms in the same controlled run;
- idle rendering does not continuously consume a full logical processor;
- memory stabilizes after bounded transcript eviction and repeated modal/activity cycles.

Record hardware and measurement method. These thresholds are acceptance gates for the tested environment, not permanent cross-machine telemetry or runtime self-tests.

### 7.7 Go/no-go rule

The spike passes only if all of the following are true:

- fixed status/footer behavior works through public APIs;
- exact multiline paste passes;
- active-run input semantics are implementable without a competing reader;
- selection/copy has both an application path and terminal handoff path;
- terminal restoration passes every tested exit path;
- input remains responsive under bounded concurrent output;
- headless snapshots are deterministic;
- package/license/release closure is acceptable;
- no required behavior depends on reflection, private fields, vendored patches, or a build from `main`.

On FAIL, do not add the production package/project/launch selector. Record the blockers, mark Plan 100 `Deferred — spike failed`, and keep the existing frontend unchanged. A later retry requires a new upstream version and a complete rerun, not a waived gate.

## 8 Public Contracts and Dependency Rules

### 8.1 Host startup contract

- `InteractiveFrontendKind` is host-owned startup data and contains no terminal-library type.
- It is not persisted in sessions, repository configuration, events, checkpoints, or projections.
- App composition selects one frontend factory after noninteractive command precedence is resolved.
- Unsupported frontend IDs fail before repository/model/tool side effects.

### 8.2 Interaction contract

- TUIKit implements the Plan 98 `IInteractionSurface` contract.
- If the spike demonstrates that a small missing capability is required, amend `IInteractionSurface` using a terminal-neutral semantic operation and update the PrettyPrompt adapter plus conformance harness in the same change.
- Do not add a TUIKit-shaped method, generic arbitrary-renderable escape hatch, raw key stream, terminal cell API, or service locator.
- Shared coordinator behavior must be identical for both surfaces.

### 8.3 Adapter API

Expose only the factory/entry point needed by App composition. Widgets, backend, theme resolver, renderer, input router, modal adapters, clipboard adapter, and lifecycle state remain internal to `Threadsmith.Tui.TuiKit`.

### 8.4 Dependency assertions

- `Threadsmith.Tui.TuiKit` → `Threadsmith.Interaction` + TUIKit.
- `Threadsmith.Tui` → `Threadsmith.Interaction` + PrettyPrompt/Spectre.
- neither frontend references the other;
- Interaction and lower layers reference neither frontend nor terminal package;
- App may reference both adapter projects and selects exactly one;
- TUIKit appears in no public signature outside its adapter;
- extensions cannot reference or load the adapter through host contracts.

## 9 Project and File Changes

| Area | Planned change |
|---|---|
| `spikes/Spike.TuiKit/` | minimal gated full-screen/copy/input/teardown spike |
| `spikes/Spikes.sln` | add spike project only |
| `docs/architecture/spike-notes.md` | record exact evidence and PASS/FAIL |
| `docs/architecture/adr-51-*.md` | if the next ADR number remains 51, record optional full-screen frontend decision after PASS |
| `Directory.Packages.props` | exact approved TUIKit pin after PASS |
| `src/Threadsmith.Tui.TuiKit/` | new adapter project, surface, layout, rendering, input, modals, themes, lifecycle |
| `src/Threadsmith.Interaction/` | only terminal-neutral contract/theme refinements justified by the second consumer |
| `src/Threadsmith.App/CommandLineParser.cs` | parse frontend kind and invalid/duplicate selectors |
| `src/Threadsmith.App/ShellRunner.cs` | compose one selected frontend and coordinate cancellation ownership |
| `src/Threadsmith.App/Program.cs` | updated help text only; no frontend logic |
| `src/Threadsmith.App/Threadsmith.App.csproj` | reference the new adapter project |
| `src/Threadsmith.sln` | add production adapter and focused tests |
| `tests/Threadsmith.Tui.TuiKit.Tests/` | headless rendering, input, modal, ordering, status, bounds, and lifecycle tests |
| existing Interaction/TUI/App/Architecture tests | shared conformance, PrettyPrompt non-regression, launch grammar, dependencies |
| release legal evidence/package graph | TUIKit identity, MIT notice, source/provenance, release closure |
| user/operations/planning docs | launch, keys, full-screen selection, fixed status, acceptance/manual verification |

Verify the next ADR number at implementation time rather than overwriting an intervening ADR.

## 10 Ordered Implementation Tasks

### P100-01 Freeze post-Plan-98 conformance

1. Verify Plan 98 is complete and its terminal-free surface tests cover commands, selections, reviews, run input, repository/session transitions, status, semantic output, and Markdown.
2. Run those tests against the PrettyPrompt adapter and record the baseline.
3. Inventory the final public Interaction contracts rather than coding against the illustrative names in Plans 98/100.
4. Confirm the current branch has no unrelated unfinished interaction-boundary migration.

**Gate:** no TUIKit product work begins while Plan 98 behavior is duplicated or incomplete.

### P100-02 Run the mandatory spike

1. Create the isolated spike and exact package reference.
2. Complete package, headless, physical-terminal, performance, selection/copy, input, and teardown checks from section 7.
3. Record evidence in `spike-notes.md`.
4. Decide PASS or FAIL strictly from the go/no-go rule.

**Gate:** FAIL ends implementation without product changes. PASS authorizes P100-03 onward.

### P100-03 Record architecture and planning ownership

1. Add the next ADR for the optional full-screen frontend, citing ADR-15 and the spike.
2. Register an owning product milestone/index/DAG entry if required by the planning state when implementation is activated; do not attach the capability to a completed milestone.
3. Add product acceptance Scenario AR (or the next available stable ID) for explicit frontend selection, fixed status, shared authority, full-screen interaction, and default-frontend non-regression.
4. Reserve MTP-256 (or the next available stable ID) for the real-terminal TUIKit matrix.

### P100-04 Pin package and close licensing

1. Add the exact TUIKit version to Central Package Management.
2. Add exact NuGet/source/license/copyright evidence to release legal inputs.
3. Generate/verify notices and SPDX/CycloneDX closure through existing release tooling.
4. Verify the package contributes no unexpected native/runtime-specific dependency for the six release RIDs.
5. Add architecture tests that only the TUIKit adapter references the package.

### P100-05 Scaffold the adapter and tests

1. Add `Threadsmith.Tui.TuiKit` and its project DOX.
2. Add `Threadsmith.Tui.TuiKit.Tests` and test DOX/index updates.
3. Add project references and solution configurations.
4. Implement a factory/entry point with no host policy dependencies.
5. Add forbidden-reference and public-signature architecture guards before feature code.

### P100-06 Add launch selection and composition

1. Introduce `InteractiveFrontendKind` and exact parser rules.
2. Update help/usage.
3. Add parser tests for no selector, bare selector, both named values, case, empty, unknown, duplicate, request-text preservation, MCP precedence, and auth behavior.
4. Refactor `ShellRunner` to choose one adapter factory without duplicating its shared dependencies.
5. Prove `--tui` still constructs only PrettyPrompt and no TUIKit type is loaded/initialized on headless or PrettyPrompt paths.

### P100-07 Build layout and UI-loop serialization

1. Create transcript, activity, composer, and fixed status regions.
2. Marshal surface operations through one UI-loop queue.
3. Implement minimum-size, resize, focus, and modal overlay behavior.
4. Add deterministic headless snapshots for all supported sizes and narrow-state recovery.
5. Prove footer position and surface-call FIFO order.

### P100-08 Render semantic presentation and Markdown

1. Map every shared text role to TUIKit styles.
2. Map lifecycle blocks, source/status/error text, links, diffs, and safe source.
3. Map every shared Markdown node without invoking the TUIKit Markdown parser.
4. Add transcript bounds, omission notice, scroll detach/new count/reattach, and resize reflow.
5. Run shared presentation and Plan 63 conformance tests against both adapters.

### P100-09 Implement fixed status and activity

1. Cache/render the latest status snapshot in the bottom row.
2. Implement responsive omission and width measurement through TUIKit.
3. Implement one mutable activity row and bounded refresh.
4. Preserve estimate/unknown values and ensure no synchronous host query occurs while rendering.
5. Test updates during ordinary input, run output, modal display, resize, repository/model/reasoning change, and session transition.

### P100-10 Implement composer, paste, and run input

1. Wire multiline editor submit/newline behavior.
2. Implement one-operation bracketed paste with exact multiline preservation and current bounds.
3. Implement ordinary cancellation and exact draft handoff/restoration.
4. Implement active-run Enter, repeated Enter, double Escape, Ctrl+C, ordinary input buffering, safe-boundary steering, and draft restoration.
5. Run Plan 96/98 conformance and targeted real-terminal paste/input checks before proceeding.

### P100-11 Implement selectors, reviews, and workflows

1. Regenerate and freeze the complete selector inventory from the implemented Plan 98 traces and current PrettyPrompt behavior before writing the adapter.
2. Adapt selection, confirmation, and prompt requests to typed modals without replacing any list with command-text or raw-ID entry.
3. Map stable IDs independently of labels and preserve ordered metadata, active/enabled/current markers, descriptions, warnings, `Back`, and cancellation semantics.
4. Implement Up/Down, Enter, Escape, long-list scrolling, visible highlight, and incremental search/filtering parity.
5. Exercise model selection; repository tool enable/disable and consent; extension load/unload; resumable sessions; MCP profiles/capabilities/actions; repository trust/solution/init; mutation and plan policies; themes; approvals/reviews; and every skill, hook, or later shared selector.
6. Test post-action refresh for dynamic lists plus cancellation, stale options, duplicate labels, narrow-width detail access, background status/output, resize, and shutdown while a modal is open.
7. Prove both frontends dispatch identical host commands for the same shared interaction script.

### P100-12 Implement themes, selection, copy, and accessibility

1. Extract only the newly justified neutral theme values/preferences into Interaction and adapt PrettyPrompt without changing its output.
2. Map themes, configured colors/decorations, high contrast, ASCII, and `NO_COLOR` to TUIKit.
3. Implement application selection/copy, bounded OSC 52, F12 mouse handoff, and keyboard scrolling.
4. Add visible key/mouse-mode hints without exposing secrets or reducing status truth.
5. Verify keyboard-only operation and terminals without mouse/clipboard support.

### P100-13 Harden lifecycle and failure paths

1. Establish one idempotent cancellation/interrupt route.
2. Dispose in the correct order for normal, cancellation, startup failure, modal failure, render failure, and fatal exception.
3. Ensure final messages render after alternate-screen exit.
4. Add best-effort headless backend teardown tests and run the complete physical terminal restoration matrix.
5. Fail non-TTY/unsupported launches clearly without fallback.

### P100-14 Complete regression, release, and documentation

1. Run the complete automated matrix from section 11.
2. Run MTP-256 and affected existing interactive procedures on both frontends.
3. Publish all six release RIDs and verify package/legal closure and launch behavior.
4. Update user guide, keyboard shortcuts, README usage, architecture, acceptance, manual, release, and DOX owners.
5. Review the final diff for duplicated interaction policy or TUIKit leakage.
6. Mark Plan 100 implemented only after all gates pass; do not copy completion status into the implementation-plan README.

## 11 Testing

### 11.1 Shared conformance

Run the Plan 98 interaction conformance suite against both surface implementations. The same scripted interaction must produce the same:

- submitted semantic inputs;
- slash-command resolutions and local errors;
- selection requests with the same stable IDs, host ordering, labels, metadata, state markers, descriptions, warnings, and cancellation behavior;
- selected stable IDs, dynamic-list refreshes, nested action choices, and cancellation results;
- host command types, IDs, arguments, and ordering;
- approval/revision/rejection outcomes;
- steering/cancellation signals;
- repository/session transition outcomes;
- semantic presentation item content/order;
- session-status truth;
- Markdown document/fallback/source-mode behavior.

Frontend placement, wrapping, color, scroll model, and fixed-versus-appended status are allowed to differ where explicitly defined by this plan.

### 11.2 TUIKit adapter tests

Use the upstream headless backend for:

- complete layout snapshots at representative sizes;
- fixed last-row status under transcript growth;
- focus and key-precedence behavior;
- ordinary/steering editor state transitions;
- pasted multiline data and draft restoration;
- every section 6.10 selector family through typed modal selection/cancellation;
- Up/Down, Enter, Escape, highlight, long-list scrolling, incremental search/filtering, `Back`, nested actions, and post-action refresh;
- duplicate/truncated visible labels resolving only through stable IDs and a safe narrow-width inspect/detail path;
- presentation FIFO and atomic batches;
- Markdown-node rendering and raw-parser non-use;
- controls/markup/link safety;
- scroll lock, new count, eviction, and omission notice;
- activity line replacement;
- theme/`NO_COLOR`/ASCII/high-contrast text parity;
- idempotent stop/dispose and bounded queues.

Do not make broad ANSI snapshots the primary semantic oracle. Assert cell-buffer text, roles/styles where relevant, and shared conformance traces.

### 11.3 App and architecture tests

- exact CLI grammar and side-effect-free errors;
- bare `--tui` default compatibility;
- one frontend constructed per process path;
- MCP/headless/auth precedence;
- forbidden references and terminal-type isolation;
- package referenced only by the new adapter;
- no TUIKit initialization on PrettyPrompt/headless paths;
- release project/package inclusion for six RIDs;
- legal evidence and notice generation.

### 11.4 PrettyPrompt non-regression

Run existing TUI tests and real-terminal procedures without changing their expected behavior. In particular, bare `--tui` retains native scrollback, composer-adjacent status, current paste/selection, current themes, and current cancellation.

### 11.5 Manual verification

Add MTP-256 with prerequisites, exact commands, terminal matrix, and expected results for:

- launch selector/default/invalid cases;
- fixed status and activity rows;
- multiline submit/paste;
- Markdown and lifecycle output;
- every selector/review family;
- active-run steering and cancellation;
- scrolling, selection, copy, F12 handoff;
- resize and minimum-size recovery;
- theme and `NO_COLOR`;
- normal/failure/cancellation teardown;
- Windows, Linux/macOS, SSH/tmux where available;
- PrettyPrompt default non-regression.

Affected existing MTP cases should gain a TUIKit variant only when their executable procedure genuinely applies. Do not rewrite PrettyPrompt-native expectations as though both frontends own native scrollback.

### 11.6 Minimum verification commands

After the spike passes and production projects exist, run at minimum:

```powershell
dotnet run --project spikes\Spike.TuiKit\Spike.TuiKit.csproj
dotnet build src\Threadsmith.sln --no-restore
dotnet test tests\Threadsmith.Tui.TuiKit.Tests\Threadsmith.Tui.TuiKit.Tests.csproj --no-restore
dotnet test tests\Threadsmith.CoreRuntime.Tests\Threadsmith.CoreRuntime.Tests.csproj --no-restore
dotnet test tests\Threadsmith.SessionStatus.Tests\Threadsmith.SessionStatus.Tests.csproj --no-restore
dotnet test tests\Threadsmith.RepositoryLifecycle.Tests\Threadsmith.RepositoryLifecycle.Tests.csproj --no-restore
dotnet test tests\Threadsmith.Planning.Tests\Threadsmith.Planning.Tests.csproj --no-restore
dotnet test tests\Threadsmith.Mutations.Tests\Threadsmith.Mutations.Tests.csproj --no-restore
dotnet test tests\Threadsmith.Architecture.Tests\Threadsmith.Architecture.Tests.csproj --no-restore
dotnet test src\Threadsmith.sln --no-restore
pwsh eng\release\Test-ReleaseLicenseEvidence.ps1
git diff --check
```

Use the repository's actual release commands for the six RID payloads and record them in completion evidence.

## 12 Security and Permissions

- TUIKit is a rendering/input dependency, not an authority boundary.
- All state-changing actions continue through shared typed host commands and policy checks.
- Stable option IDs, approval IDs, session/run IDs, and repository identities never derive from modal labels or widget state.
- Unknown slash commands remain local errors and cannot fall through to a model.
- Do not use raw TUIKit markup for untrusted content. Build styled values from already safe Threadsmith text segments.
- Do not invoke TUIKit's Markdown parser on model/tool/repository content.
- Disable automatic linkification. Register only shared validated link targets.
- Do not enable terminal images, arbitrary OSC, or external URL launching in the initial adapter.
- OSC 52 copy occurs only after an explicit user copy action, is byte-bounded, and contains only selected visible text.
- Paste follows existing size limits and cannot inject key commands, modal decisions, terminal controls, or implicit submission.
- Mouse/key events map only to surface actions; they cannot dispatch host commands outside shared coordination.
- Full-screen buffers, snapshots, input recordings, and frame diagnostics must not be persisted or logged in production.
- Terminal teardown is safety-critical: failure paths must restore modes before printing diagnostics or returning control to the shell.
- Frontend selection is process-local and cannot be set by repository content.
- No new network access is performed at runtime by the adapter. NuGet access is build/restore-time only.

## 13 Observability

Add bounded, content-free startup diagnostics for:

- selected frontend ID;
- TUIKit version;
- detected terminal capability class;
- color/ASCII mode;
- mouse capture enabled/disabled;
- non-TTY or initialization failure category;
- clean versus fallback teardown result.

Do not log terminal input, clipboard text, selected transcript text, presentation content, links, prompts, diffs, Markdown, modal labels containing user data, frame buffers, or raw escape sequences.

Frame timing, queue depth, dropped/coalesced frames, and memory counters may be exposed to test/spike instrumentation. Production telemetry must remain bounded and off the per-frame hot path. Presentation-frame ticks are never domain events.

## 14 Migration and Compatibility

- No persisted data, event, checkpoint, repository config, or database migration is required.
- Bare `--tui` and all existing headless commands remain compatible.
- Existing scripts that pass a request token immediately after `--tui` continue to treat that token as request text; only `--tui=<id>` selects a named frontend.
- PrettyPrompt remains installed and shipped.
- TUIKit adds package and payload size but no selection unless explicitly requested.
- Sessions created in either frontend can be resumed by the other because frontend state is not durable.
- Raw Markdown and durable transcript remain frontend-independent.
- TUIKit's visible retained transcript is bounded and may not contain an entire long session; the durable session remains authoritative.
- Theme identity/configuration is shared, but current retained TUIKit cells may repaint while prior native PrettyPrompt scrollback cannot.
- Unknown/unsupported TUIKit terminals fail explicitly; they never change the saved preference because no preference is persisted.
- An upstream TUIKit upgrade is a compatibility project: update the exact pin, review breaking changes/license/dependencies, rerun the spike, both adapters' conformance, release builds, and MTP-256.

## 15 Acceptance Criteria

1. The TUIKit spike passes every package, input, selection/copy, performance, terminal-restoration, and release gate using public APIs only.
2. A new ADR authorizes TUIKit as an optional full-screen frontend without superseding ADR-15 for PrettyPrompt.
3. `threadsmith --tui` and `threadsmith --tui=pretty` launch the existing frontend; `threadsmith --tui=tuikit` launches the new frontend.
4. Empty, unknown, or duplicate frontend selectors fail before startup side effects with exit code 2.
5. Headless, MCP-management, and authentication precedence remain unchanged and do not initialize TUIKit.
6. Exactly one frontend consumes the shared Plan 98 coordinator in a process.
7. The TUIKit project references Interaction and TUIKit, not the PrettyPrompt frontend or host implementation layers.
8. No TUIKit type crosses App startup data, Interaction contracts, Core, events, projections, persistence, extensions, or public host APIs.
9. The transcript scrolls independently while activity, composer, and session status remain fixed.
10. The status bar occupies the bottom row and updates in place without transcript repetition.
11. Status values and estimate/unknown semantics match the shared snapshot and PrettyPrompt surface.
12. Semantic presentation content, order, roles, lifecycle boundaries, diffs, durations, safe-source behavior, and terminal outcomes remain equivalent across frontends.
13. TUIKit renders the shared Markdown document and never independently parses/recollects model answers.
14. Raw Markdown remains exact and authoritative in transcript persistence, context, restore, and headless output.
15. Enter submits; Shift+Enter works where reported; Ctrl+J always inserts a newline; 10 KiB and 100 KiB multiline paste is exact and never implicitly submits.
16. Every selection list present after Plan 98—including models, repository tool enablement, extensions, sessions, MCP profiles/capabilities/actions, repository trust/solutions/init, policies, themes, approvals/reviews, and contributed selectors—remains available in TUIKit with equivalent ordering, decision-relevant information, navigation/search, dynamic refresh, stable-ID mapping, and fail-closed cancellation; the same choices dispatch the same typed host commands as PrettyPrompt.
17. Active-run Enter, repeated Enter, double Escape, Ctrl+C, buffered ordinary input, safe-boundary steering, `/agents`, and draft restoration satisfy Plan 96/98.
18. Detached scrolling preserves position and reports new items; reattaching clears the count; bounded eviction is disclosed.
19. Application selection/copy and F12 terminal mouse handoff work in the tested terminal matrix, with an honest documented fallback where OSC 52 is unavailable.
20. Themes, configured values, `/theme`, high contrast, ASCII, and `NO_COLOR` remain safe and semantically consistent across adapters.
21. Normal exit, `/quit`, startup cancellation, Ctrl+C, double Escape, exception, initialization failure, and concurrent teardown restore the terminal completely.
22. Explicit TUIKit launch on a non-TTY/unsupported terminal fails clearly without silent fallback.
23. TUIKit is exactly pinned, package/license/SBOM/notices are complete, and all six release RIDs publish successfully.
24. The existing PrettyPrompt frontend passes its unchanged automated and real-terminal regression gates.
25. Scenario AR, MTP-256, user/keyboard/architecture/release documentation, and DOX describe the final behavior accurately.
26. Full solution build/tests, planning-governance searches, release legal checks, and `git diff --check` pass.

## 16 Risks and Mitigations

| Risk | Mitigation |
|---|---|
| TUIKit is alpha and breaks between minor versions | exact pin, package isolation, mandatory spike, explicit upgrade procedure, no use of `main` |
| Full-screen mode regresses selection/copy | TUIKit selection + bounded explicit copy + F12 terminal handoff; physical-terminal gate; keep PrettyPrompt default |
| Raw mode leaves the shell damaged | one cancellation owner, idempotent `finally` teardown, upstream lifecycle APIs, forced-failure terminal tests |
| 0.9.0 paste fix covers fields but not multiline editor needs | exact TextEditor 10/100 KiB multiline spike gate; no production work on failure |
| TUIKit command/Markdown helpers duplicate shared policy | architecture tests and source review prohibit slash routing, streaming finalization, and raw Markdown parsing in adapter |
| UI-loop marshalling reorders output | one adapter queue, loop-post acknowledgements, shared differential traces, saturation tests |
| Persistent composer changes steering semantics | explicit active-run mode, separate ordinary draft buffer, shared lease signals, Plan 96 conformance |
| Ctrl+C conflicts with TUIKit and `ShellRunner` handlers | spike exact policy; one idempotent cancellation route; teardown-race tests |
| Retained transcript grows without bound | line+byte caps, stable eviction, omission notice, memory stabilization test, durable history remains separate |
| Fixed regions leave too little room | measured minimum size, compact status, internal composer scrolling, recoverable too-small screen |
| OSC 52 leaks content or is unsupported | explicit bounded copy only, visible text only, no automatic copy, capability fallback, F12 native handoff |
| Theme support is duplicated or inconsistent | move only now-reused semantic theme data/preferences into Interaction; backend mappings stay local |
| The alternate frontend quietly drops or simplifies existing selector workflows | branch-derived selector inventory, explicit section 6.10 parity matrix, shared differential traces, every-family adapter/manual tests, and no command-text-only substitute |
| Package increases release/legal risk | package gate, exact provenance/license evidence, generated notices/SBOM, six-RID closure before completion |
| A TUIKit failure silently changes authority or frontend | no fallback after selection; restore and fail clearly; coordinator/host state remains authoritative |
| Initial scope turns into an IDE dashboard | fixed minimal four-region layout; panels, menus, browser, charts, images, palette, and editor customization are non-scope |

## 17 Documentation, Closed Decisions, and Completion

### 17.1 Documentation

After a passing spike and during production implementation:

- update `docs/architecture/spike-notes.md` with exact evidence;
- add the next ADR for the optional full-screen frontend;
- add/update the owning milestone documents required by planning governance without reopening completed milestones;
- add Scenario AR or the next available acceptance ID;
- add MTP-256 or the next available manual-test ID;
- update `docs/user-guide.md` with launch selection, fixed status, transcript bounds, full-screen behavior, and differences from native scrollback;
- update `docs/operations/keyboard-shortcuts.md` with frontend-specific input, selection, copy, mouse handoff, and newline keys;
- update root README usage examples without changing the default;
- update root/src/tests/frontend project DOX and child indexes;
- update release package graph, legal evidence/status, notice/SBOM verification, and deployment documentation;
- update the active Plan 100 status/evidence only when implementation state changes.

Do not edit completed milestone details or rewrite ADR-15. Link to the new optional-frontend ADR.

### 17.2 Closed decisions

| Question | Decision |
|---|---|
| Existing frontend | retained and remains the bare-`--tui` default |
| New launch syntax | `--tui=tuikit`; explicit existing form is `--tui=pretty` |
| Frontend persistence | none |
| Architecture | one additional adapter over implemented Plan 98 coordination |
| TUIKit package | exact version only; 0.9.0 is the initial spike candidate |
| Production gate | mandatory spike + ADR; failure means no product integration |
| Layout | transcript fill + fixed activity + fixed four-row composer + fixed bottom status |
| Markdown | Plan 98 document generation; no TUIKit reparse or recollection |
| Slash commands/approvals | Plan 98 only |
| Status | shared truth, fixed TUIKit placement |
| Selection/copy | application selection/copy plus F12 terminal mouse handoff |
| Multiline newline | Enter submit; Shift+Enter when distinguishable; Ctrl+J portable newline |
| Non-TTY | explicit failure, no silent fallback |
| Extra dashboard features | deferred |

### 17.3 Open decisions

No product decision requires user input before this plan is executable. The spike is authorized to determine only evidence-bound adapter constants and compatibility details: minimum terminal size, visible transcript line/byte caps, and a reliable application-copy chord if `Ctrl+Shift+C` cannot be observed. It may not change the launch syntax, default frontend, fixed-footer requirement, shared authority boundary, or go/no-go criteria.

Plan 100 is complete only after the spike passes, the new frontend is explicitly selectable and release-complete, both adapters pass shared conformance, PrettyPrompt remains unchanged by default, and real terminals demonstrate responsive input, exact paste, usable copying/selection, and complete terminal restoration.
