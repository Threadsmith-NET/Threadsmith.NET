# Bordered fenced content and clipboard copy

**Status:** Active — implementation applied; automated Windows validation passed. Physical-terminal and Linux/macOS validation remain open.
**Delivery track:** Maintenance — extend the existing interactive Markdown presentation.
**Prerequisites:** Confirm the active checkout. Read `AGENTS.md`, `planning-governance.md`, shared context §G, and the portable C# guardrails before implementation. Use the pinned TUIKit package. Complete Task 1 before changing presentation contracts.
**Writing standard:** ASD-STE100 controlled-language style. Repository identifiers and API names are technical names. This document does not claim formal language certification.

## 1 Objective

Show every fenced content block in model output inside a bordered box. The fence defines eligibility. The body can contain prose, data, logs, code, or any other text. Do not require a recognized language or valid syntax. Put the content type in the top border, at the left. Put a mouse-operated **Copy** control in the top border, near the right. Leave at least one border cell between the control and the right corner. Show the control only when the active platform and terminal support the clipboard transport.

Copy the block body. Do not copy the opening fence, closing fence, content-type label, border, control label, or display wrapping.

Example layout:

    ╭─ json ─────────────────────── Copy ─╮
    │ {                                   │
    │     "property": "true"              │
    │ }                                   │
    ╰─────────────────────────────────────╯

Use TUIKit components where possible. Preserve the existing output window, transcript ownership, model execution, and clipboard policy. "Copy" should use hyperlink styling / TUI role. 

## 2 Architectural Context

The interactive terminal projects engine output. It must not change model requests, tool execution, stored answers, or headless output. Keep terminal-library types in the terminal frontend.

Use the existing semantic Markdown parser and presentation path. Extend the existing transcript layout and action handling. Do not add a second Markdown parser, transcript store, or clipboard transport.

Follow [planning governance](planning-governance.md), [shared context](00-shared-context.md), and [ADR-15](../architecture/adr-15-conversation-first-terminal.md). Preserve the existing semantic Markdown behavior in Scenario AC. Add the behavior specified in SC-FENCED-COPY-01.

## 3 Scope

- Fenced blocks in accepted model answer output, including MAIN and child-agent output.
- Any fenced body: plain prose, JSON, YAML, XML, CSV, logs, Markdown, code, mixed content, unknown content types, and blocks without a label.
- Invalid or incomplete data inside a closed fence. Content validation must not control box or copy availability.
- Standard backtick fences, including fences longer than three characters. Apply the same presentation to tilde fences already accepted by the parser.
- Multiple blocks, empty blocks, blocks in lists or quotes, Unicode, long lines, resize, scrolling, and transcript retention.
- Mouse copy and an equivalent keyboard action through the existing focus system.
- Clipboard capability detection, byte limits, explicit failure, and truthful status.

## 4 Non-Scope

- Executing, validating, repairing, or formatting the block content.
- A new Markdown implementation, clipboard backend, package upgrade, or general document editor.
- New syntax highlighting. Preserve existing styling where supported.
- Changing inline code, ordinary indented code, raw tool results, user input, or script source-view behavior.
- Changing raw/headless output, model context, or persisted answer text.
- Changing answer buffering or event flush boundaries to introduce token-by-token Markdown rendering.

## 5 Current State

Source inspection on 2026-11-17 found these owners:

| Owner | Current behavior | Required extension |
|---|---|---|
| `Threadsmith.Interaction/Markdown/MarkdownParser.cs` | Uses Markdig. Converts fenced and indented code to `MarkdownCodeBlock`. Escapes terminal controls in code text. | Retain fenced-block identity and a safe association with the admitted original body. |
| `MarkdownDocument.cs` | `MarkdownCodeBlock` has `Code` and `Language`. | Distinguish fenced content from indented code without exposing Markdig types. |
| `Presentation/TerminalMarkdownLayout.cs` | Emits literal fence lines and code segments. | Preserve block structure for interactive box layout. Keep other frontend behavior compatible. |
| `Threadsmith.Tui.TuiKit/TuiMarkdownLayout.cs` | Uses shared layout and TUIKit display-cell metrics. Preserves source ending separators. | Reuse these measurements and separator rules. |
| `TranscriptView.cs` and `TranscriptView.Source.cs` | Own retained lines, continued chunks, selection, source actions, mouse handling, and reflow. | Add block action ownership and box geometry within this lifecycle. |
| `TuiKitSurface.cs` | Owns clipboard output, byte checks, and notices. Uses `ClipboardWriter.BuildSequence`. | Gate availability and report block-copy results accurately. |
| `TuiKitConsoleBackend.cs` | Carries `TerminalCapabilities.ClipboardOsc52`. | Use the active backend capability, not an OS-name guess. |
| `Directory.Packages.props` | Pins TUIKit 1.11.2. | Keep the package version unchanged unless a demonstrated gap requires separate review. |

The installed TUIKit 1.11.2 XML documentation lists `SurfaceExtensions.DrawBox`, `BorderStyle.Rounded`, `BorderStyle.Ascii`, title alignment/inset support, `Widgets.InlineButton`, `Widgets.Button`, and clipboard sequence helpers. Their suitability for a clipped transcript region still requires an implementation check. The presence of an API does not prove that it solves retention or scrolling.

## 6 Proposed Design

### 6.1 Reuse TUIKit components

Use TUIKit box drawing and border styles for the frame. Use an `InlineButton` for Copy if it supports the transcript input and clipping contract. Use existing TUIKit surface, Unicode-width, focus, mouse, and clipboard components.

Keep one transcript owner. Do not allocate a separate scrolling widget, window, or modal for each block. A thin adapter may map retained block rows to TUIKit drawing and input primitives. Before adding custom drawing or hit testing, record the exact component limitation and the smallest required extension in this plan.

### 6.2 Preserve content and identity

Extend the existing parser conversion to identify fenced blocks and their original body. Use parser source positions and line information. Do not search rendered text for fence strings. Keep Markdig objects inside the parser boundary.

Use host-owned metadata to associate each rendered block with its retained answer and block ordinal. Keep exact copy text, or a checked source range, under the same retained presentation owner. Prefer a range into an already retained admitted source over a duplicate string.

Copy only admitted, user-visible answer content after existing sanitization. Do not recover pre-sanitization provider text. Preserve body characters, tabs, trailing spaces, blank lines, and original line endings. Exclude the opening and closing fence lines. The line ending after the final body line belongs to the body when present. For nested list or quote blocks, remove only the Markdown container prefixes that the parser identifies; preserve body indentation. Add fixtures that define this mapping.

Never reconstruct copy content from rendered cells, soft wraps, escaped display text, or syntax styles. Escape controls for display. Keep clipboard data in the existing encoded clipboard transport; do not emit it as terminal commands.

### 6.3 Render the box

Use the standard rounded frame and theme roles. Use the standard ASCII fallback when required. Put the bounded content-type token at the top left. Use **text** when the fence has no content-type label. Treat extra info-string attributes as inert text; do not turn them into commands or actions.

Put **Copy** on the top border with one or more border cells before the right corner. Keep the label and control separate. Give Copy priority when shortening the content-type label. Never overlap the corner, content, or another control.

Use display-cell width, not UTF-16 length, for geometry. Wrap long body lines within the box. Wrapping changes display only. Preserve blank lines. Do not make a new horizontal scroll owner.

At widths that cannot fit a valid frame and control, use a documented compact plain-text fallback. Do not draw a clipped clickable label or an off-screen hit target. Restore the normal box when space becomes available.

### 6.4 Bind copy to the retained block

Bind the action to the block identity and current layout generation. Build hit regions from the same layout that draws the control. Translate viewport and scroll offsets through the existing transcript path.

A plain left click on Copy copies that block once. Body clicks and drags retain selection behavior. Keyboard focus can activate the same action with Enter or Space. Follow existing key routing; do not take Ctrl+C away from selection or cancellation.

Rebuild hit regions after resize and reflow. Remove them when retention evicts the block, the child view closes, or the session changes. Reject stale actions. Never copy a different block because its old screen coordinates were reused.

### 6.5 Honor clipboard support and limits

Use the existing surface callback, byte policy, backend capability, and TUIKit clipboard encoder. Hide Copy when the active transport is unsupported. Do not infer support from Windows, Linux, or macOS alone. Do not read or write the clipboard during rendering or discovery.

OSC 52 can send data to the terminal, but it cannot confirm clipboard acceptance. Report **Sent to terminal clipboard** for that transport. Do not claim confirmed local clipboard success. A denied terminal policy, write failure, or unavailable transport must not produce a success notice.

Measure the full body in UTF-8 bytes before copy. Never silently truncate it. Show an explicit notice when the retained body is incomplete or exceeds policy. An empty block has an empty body; disable Copy with an explicit empty-content state rather than preserve an old clipboard while reporting success.

### 6.6 Preserve streaming and retention rules

Reuse existing answer completion and visible-event flush boundaries. Do not draw an action for an incomplete live source fragment. A finalized answer with an unclosed fence may use the parser's end-of-document body rule; mark it incomplete and test that rule explicitly.

Count block metadata and retained copy content in existing resource limits. Eviction must release content and actions together. If only part of a block remains, retain a clear omission state and remove full-copy availability. Do not fetch evicted source from disk or a model provider.

Do not confuse continued storage chunks with real newlines. Use the existing continuation metadata when measuring, selecting, or retaining block content. Keep tool results, source-view controls, and transcript separators intact under small limits.

## 7 Public Contracts

Prefer internal changes. If shared semantic records need fields for fenced-block identity or source ranges, use host-owned values and compatible defaults for existing callers. Do not leak TUIKit or Markdig types into core, events, persistence, or extension contracts.

Do not add a model-facing schema or prompt token. Clipboard capability is a frontend concern. Headless output keeps its original fences and bytes.

## 8 Project/File Changes

| Area | Planned files |
|---|---|
| Semantic structure and safe source association | `src/Threadsmith.Interaction/Markdown/MarkdownParser.cs`, `MarkdownDocument.cs`, `MarkdownValidator.cs`; existing presentation contracts only if needed |
| Shared layout compatibility | `src/Threadsmith.Interaction/Presentation/TerminalMarkdownLayout.cs` |
| TUIKit projection and controls | `src/Threadsmith.Tui.TuiKit/TuiMarkdownLayout.cs`, `TranscriptView.cs`, focused transcript partial if useful |
| Clipboard dispatch | `src/Threadsmith.Tui.TuiKit/TuiKitSurface.cs`; existing backend capability plumbing |
| Regression coverage | `tests/Threadsmith.CoreRuntime.Tests/TuiMarkdownRenderingTests.cs` and existing transcript, source-view, and backend test fixtures |
| Documentation | This plan, planning README, acceptance scenarios, manual test plan, and applicable user operation guides at implementation completion |

Resolve exact test file ownership before editing. Do not create a parallel renderer to avoid extending these owners.

## 9 Ordered Tasks

1. Inspect the pinned TUIKit APIs and existing transcript owners. Test DrawBox and InlineButton in a clipped, scrolled region. Record reuse decisions and any API limitation. Confirm clipboard capability semantics.
2. Add parser and copy-body tests. Cover fence kinds, absent/unknown labels, nested containers, line endings, and terminal controls. Add compatible host-owned metadata.
3. Extend the retained Markdown/transcript path. Add frame geometry, labels, Unicode wrapping, narrow-width fallback, and theme behavior through TUIKit components.
4. Connect Copy to the existing surface callback. Add capability gating, byte checks, empty/incomplete states, truthful notices, and keyboard activation.
5. Integrate reflow, selection, chunk continuation, eviction, child views, and session replacement. Test stale action rejection.
6. Run automated and physical-terminal checks. Update operation documentation and record exact commands, results, platform limits, and component decisions in this plan. Mark Complete only after required checks pass.

## 10 Testing

Use deterministic fixtures. Use the test runner token, explicit completion barriers, and a controllable clock where a deadline exists. Do not use sleeps, retry-until-green, or timing assertions. Snapshot UI state only at its owner boundary.

| Test group | Required checks |
|---|---|
| Parse/body | Prose, JSON, YAML, XML, CSV, logs, Markdown, C#, mixed text, invalid structured data, unknown/no label, empty body, multiple blocks, tilde/long fences, embedded shorter fences, nested lists/quotes, unclosed fence |
| Exact copy | Tabs, trailing spaces, blank lines, LF/CRLF, final newline, Unicode, braces, literal Markdown, escaped display controls; no fence or frame bytes |
| Layout | Standard and ASCII frames, label/control inset, wide and minimum widths, resize, Unicode cell widths, long lines, NO_COLOR |
| Input | Mouse click once, body selection, keyboard activation, scrolling, stale coordinates, two visually identical blocks |
| Capability | Supported and unsupported fake backends, OSC 52 dispatch, rejected writes, byte boundaries, multibyte limits, empty/incomplete bodies |
| Retention | Tiny line/byte limits, continued chunks, partial block eviction, source-view controls, child close, session replacement, restored answers |
| Integration | Ordinary answer, child answer, model output within skills through the same answer projection, raw/headless/source-mode compatibility |

Run the affected test project and the full solution under CONTRIBUTING.md. Use restore, Debug build, and `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4` locally on Windows. Read current CI commands before submission. Run focused whitespace and analyzer checks. Perform MTP-FENCED-COPY-01 on available platforms. Report unavailable platforms as unverified.

## 11 Security/Permissions

Only explicit user input can start a copy. Model text cannot supply an action ID, a command, or clipboard authority. Preserve existing sanitization and clipboard byte limits. Do not execute language labels, HTML, code, or terminal controls. Do not add clipboard reads or remote fetches.

## 12 Observability

Use the existing transient notice path for copy status and failure. Do not log copied text, model block contents, or clipboard payloads. Do not append repeated notices on repaint. Retain normal model/tool activity and completion visibility.

## 13 Migration/Compatibility

No database migration is planned. Keep original answer storage and headless output unchanged. Keep `tui:renderMarkdown=false` as source mode. Preserve the original terminal frontend until its existing compatibility contract explicitly changes. Restored answers use the same safe parsing and bounded retention path, with no serialized control IDs.

## 14 Acceptance Criteria

SC-FENCED-COPY-01 passes. Every fenced content block has a readable box and top-left type label. Copy appears only with clipboard support and valid layout. The control has a visible inset from the top-right corner. Copy returns the exact admitted body, without fences or decoration. Correct block ownership survives scroll and resize. Limits and failures are explicit. Existing script-source controls and raw output remain correct.

TUIKit components are reused for available capabilities. Every custom substitute has a documented technical reason. Required automated checks pass and manual platform evidence is recorded.

## 15 Risks

| Risk | Control |
|---|---|
| Parser-normalized text loses exact whitespace | Keep a tested association with admitted source. |
| Box drawing and clicks disagree after reflow | Use one layout generation for cells and hit regions. |
| Clipboard support is advertised but terminal policy rejects it | Report dispatch, not confirmed clipboard acceptance. |
| Per-block widgets increase retained work | Reuse transcript ownership; draw visible regions and bound retained metadata. |
| Longer headers break retention tests | Assert logical continued text and retained controls, not accidental storage boundaries. |
| Shared layout changes alter another frontend | Keep frontend-neutral metadata and test original/headless behavior. |

## 16 Documentation

This plan owns status and implementation evidence. The planning README links here. SC-FENCED-COPY-01 owns product behavior. MTP-FENCED-COPY-01 owns physical-terminal verification. At implementation completion, update the agent-workspace, keyboard, theme, and resource-limit guides only where their contracts change. Do not describe unimplemented controls as available in user documentation.

## 17 Open Decisions

Task 1: use pinned TUIKit 1.11.2. Release source was inspected at upstream commit `7516c2255c643ece18f86a50c52f47346bff3977`; the external working checkout is newer and was not changed. `FencedBlockComponentTests` exercises the installed package. Command: `dotnet test --project tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj --configuration Debug --no-restore -- --filter-class '*FencedBlockComponentTests'`. Result: 3 passed on Windows. The initial missing XML comments were corrected. The component fixtures are included in the later focused fenced test run.

Reuse `DrawBox`, `BorderStyle.Rounded`/`Ascii`, left-aligned titles, `SurfaceView`, `InlineButton`, and `ClickRegionMap`. Draw only visible frame strips through a one-row surface with a synthetic three-row box. This keeps work bounded: `DrawBox` clips writes but otherwise visits the complete rectangle. It has one title slot, so reserve space for the action and overlay the button. `InlineButton` is a stateless drawing primitive, not a focus owner. It always draws brackets; use `[Copy]` in the Hyperlink role. Register only fully visible controls in transcript coordinates. Keep keyboard routing and ownership validation in `TranscriptView`. Clear the map at layout/input lifecycle boundaries. The compact fallback applies below 17 display cells of available block width and contains no copy target.

The active backend's `IsInteractive` and `Capabilities.ClipboardOsc52` gate transport availability. This capability is optimistic; a successful write cannot prove terminal acceptance. Extend the existing surface callback and report `Sent to terminal clipboard` only after write and flush. Physical-terminal verification remains required.

Implementation uses parser-owned original-source ranges, the existing Markdown projection callback, and one retained transcript. Stable item/ordinal identities survive reflow. Display soft wraps remain continued logical text; ordinary selection excludes synthetic frame padding. Existing retention budgets include source mappings and projected frame rows. MAIN and child views share the surface callback. The theme's optional `ui.fencedCodeAscii` boolean selects the standard ASCII fallback; it defaults to rounded frames.

Test fixtures use in-memory buffers and injected backends. Clipboard transport failures and byte admission are asserted synchronously. An initial event-loop fixture stalled on terminal-output observation and was replaced, not given a larger deadline. No new fixture uses terminal-output waits, sleeps, timing assertions, process-wide environment changes, or an OS clipboard. Malformed UTF-16 is constructed at runtime to avoid attribute-metadata encoding changes. Final validation commands and results follow; physical terminal and unavailable platform checks remain open.

## 18 Validation evidence

Environment: Windows, .NET SDK `10.0.404`, `net10.0`, x64. Read `.github/workflows/build.yml`; it owns Windows/Linux parallelism 4 and macOS parallelism 1. No push or PR was requested, so no GitHub Actions run was started.

| Command | Result |
|---|---|
| `dotnet restore src/Threadsmith.sln` | Passed. |
| `dotnet build src/Threadsmith.sln --configuration Debug --no-restore` (native build tool) | Passed; no reported diagnostics. Final build followed the parser whitespace correction. |
| `dotnet test --project tests/Threadsmith.CoreRuntime.Tests/Threadsmith.CoreRuntime.Tests.csproj --configuration Debug --no-build -- --filter-class '*Fenced*' --filter-class '*TuiKitClipboardCopyTests'` | 63 passed, 0 failed/skipped. Test execution 983 ms; runner total 2.301 seconds. |
| `dotnet test --solution src/Threadsmith.sln --configuration Debug --no-build --max-parallel-test-modules 4` | 6,701 passed, 0 failed, 92 skipped; 6,793 total. Whole-solution duration 4 minutes 20.588 seconds, not an individual-test duration. All 27 modules passed; CoreRuntime module took 48.631 seconds. |
| `pwsh -NoProfile -File eng/release/Test-SourceDocumentation.ps1` | Passed; 121 packaged files. Subsequent edits affect only this excluded source plan. |
| `dotnet format whitespace src/Threadsmith.sln --verify-no-changes --no-restore --include src/Threadsmith.Interaction/Markdown/MarkdownParser.cs` | Passed after correcting switch-block indentation. The prior scoped check covered all 16 changed C# files and reported only that indentation group; unchanged files reuse that coverage. |

The full run used the existing pinned ripgrep fixture at `artifacts/test-ripgrep/stage/tools/rg.exe`. Skips concern existing opt-in/live, platform, and unavailable symbolic-link fixtures; none of the 63 focused cases was skipped. Background compiler feedback reported no errors or warnings. Scoped analyzer feedback covered 16 documents with no errors or warnings and 39 suggestion-level findings in changed documents; it is not a clean solution-wide analyzer claim. No equivalent analyzer rerun was requested.

Review corrected a display-wrap selection regression and added two-width selection coverage. A final separate test review found no fixture-level OS, path, culture, ambient-environment, wait, polling, or deadline dependency. This is source-review evidence, not proof of another platform run.

Open gates: execute MTP-FENCED-COPY-01 in physical terminals, including actual clipboard-policy refusal, rounded/ASCII and NO_COLOR display, and independent paste comparison. Linux/macOS and exact submitted-revision CI remain unverified. Do not mark this plan Complete until its required gates pass.
