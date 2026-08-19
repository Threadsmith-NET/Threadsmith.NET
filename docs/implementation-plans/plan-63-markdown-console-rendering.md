# Plan 63 — Semantic Markdown Console Rendering

**Milestone:** M22.3 — Semantic Markdown Console Rendering

**Prerequisites:** plans 03, 24–26, and 49

**Depends on by:** future rich-output and syntax-highlighting enhancements

**Status:** Implementation and focused automated coverage complete; maintained real-terminal, cross-platform, and release-payload closeout pending

## 1 Objective

Make Markdown a first-class, default interactive presentation for ordinary model output without binding Threadsmith's transcript, event pipeline, tests, or themes to PrettyPrompt, Spectre.Console, or a third-party Markdown widget.

Threadsmith parses each complete contiguous non-reasoning answer block with Markdig, translates the bounded AST into a host-owned semantic `TuiMarkdownDocument`, and writes that document through the existing serialized `IConsoleSurface`. The current Spectre.Console adapter may remain the first backend, and PrettyPrompt may remain the composer, but neither owns the Markdown contract.

Rendered mode deliberately replaces visible token-by-token answer streaming with complete-block output. `THINKING` remains visible while a block is accumulating. Every ordered event that starts, stops, or replaces visible output, activity, status, or prompt state—and every declared lifecycle boundary—closes and writes the preceding block before its own projection; unknown non-model events close conservatively. Timer-driven in-place refreshes of an already active `THINKING`, `TOOLS`, or `MCP` row are serialized non-boundary redraws: they update elapsed text without entering event classification or closing the block. Thus a tool invocation, host status, or diagnostic cannot overtake earlier answer text, post-boundary continuation becomes a separate block, and `RunCompleted` closes the final block before the composer returns. Raw `ModelOutputObserved` text still appends immediately to terminal-neutral transcript state in exact event order.

Markdown rendering defaults on. Users may select source mode to restore the existing streamed model-text cadence for ordinary safe text. Exact raw source remains durable/headless data; every interactive-terminal source or failure projection passes through a host-owned control-neutralizing encoder first. Reasoning, tool/MCP output, host status, diagnostics, diffs, prompts, persistence, context, restoration, and headless output retain their existing contracts.

## 2 Architectural Context

The current interactive path is `ModelOutputObserved` → `ConversationTranscript.Apply()` → `TuiEventSegments.Append()` → `IConsoleSurface.WriteSegmentsAsync()`. `ConversationTranscript.Apply(IDomainEvent)` is the single raw transcript append boundary, `UiEventDispatcher` preserves ordered batches, and `PrettyPromptConsoleSurface` serializes output/activity through `_consoleGate`.

A native-scrollback terminal cannot safely stream raw Markdown and later replace it in place: wrapping, resize, selection, scrollback, and interleaved tool events make cursor rewriting unreliable. Printing a second formatted copy creates duplication. Because complete output is preferred over live Markdown, Plan 63 explicitly changes only the default visible assistant-answer projection: raw events continue to append live internally, but the current answer block is presented once when complete.

The prior MDView approach is rejected as the production architecture. MDView returns Spectre `IRenderable` values with literal colors and a fixed code theme, making semantic theme adaptation, backend replacement, deterministic structural testing, and plain-mode parity unnecessarily difficult. Direct Markdig integration provides a source-located AST without dictating terminal layout or colors.

### 2.1 Layering

```text
ModelOutputObserved chunks
        │
        ├── exact ordered raw text ──> ConversationTranscript / persistence / context / headless
        │
        └── contiguous answer collector
                    │ block closes
                    v
             IMarkdownParser
               (Markdig adapter)
                    │
                    v
          TuiMarkdownDocument
     (host-owned semantic block/inline nodes)
                    │
                    v
       IConsoleSurface.WriteDocumentAsync
                    │ serialized gate
                    v
      Spectre adapter today / future adapter later
```

Parsing, semantic representation, and terminal adaptation are separate responsibilities. Markdig types stop at the parser adapter. Spectre and PrettyPrompt types remain inside the current concrete terminal adapter.

### 2.2 Default behavior decision

`tui:renderMarkdown` defaults to `true` for interactive output. This is an intentional user-approved change to the prior visible-answer streaming behavior. When `false`, model chunks use the existing streamed `TuiTextSegment` path unchanged.

`NO_COLOR` suppresses style but does not disable semantic Markdown layout. The same headings, list markers, table structure, code indentation, links, words, and line breaks render without color/decorations. Redirected and headless output remain exact raw Markdown source unless a future output-format plan explicitly changes them.

## 3 Scope

- Add a direct centrally pinned Markdig dependency to `Threadsmith.Tui`; do not add MDView, TextMateSharp, or another Markdown widget.
- Define an internal immutable `TuiMarkdownDocument` block/inline model with no Markdig, Spectre, PrettyPrompt, ANSI, or persistence types.
- Configure an explicit allowlist of Markdown syntax rather than `UseAdvancedExtensions()`.
- Add a Markdig adapter that validates bounds and maps supported AST nodes into the host model.
- Add default-on `tui:renderMarkdown`; `false` selects streamed terminal-safe source mode with unchanged cadence for ordinary safe text.
- Collect contiguous non-reasoning answer blocks and close them before every ordered event that starts, stops, or replaces visible output/activity/status/prompt state and before every declared lifecycle boundary; timer-driven in-place refreshes of the already active activity row are non-boundary redraws.
- Extend `IConsoleSurface` with a serialized semantic-document operation.
- Map document nodes to semantic TUI roles and terminal-native layout in the current Spectre adapter.
- Preserve raw Markdown exactly in transcript state, events, persistence, context, restoration, and headless output.
- Add deterministic parser/model/layout/order/fallback/security/terminal coverage.
- Update the TUI DOX streaming contract when implementation lands.

## 4 Non-Scope

- MDView.Renderer, Spectre.Console.Next.Markdown, NTokenizers Markdown output, or another end-to-end Markdown widget.
- Replacing PrettyPrompt composer input as part of this milestone.
- Removing Spectre.Console as the initial terminal-layout backend.
- Cursor-based replacement, alternate-screen transcript ownership, mouse capture, or duplicate raw/rendered answers.
- Character-level or token-level incremental Markdown rendering.
- Syntax highlighting or a fixed Dark+/Light+ code palette.
- Images, media, diagrams, mathematics, YAML front matter, executable HTML, embedded terminal markup, or browser behavior.
- Markdown rendering for reasoning, tools/MCP, host output, diagnostics, diffs, prompts, restored history, or headless output.
- Persisting parsed ASTs, layout measurements, styles, or terminal objects.

## 5 Current State

- `ConversationTranscript.Apply(IDomainEvent)` appends accepted raw model text immediately; `TuiEventSegments.Append()` maps domain events to semantic text segments, and `UiEventDispatcher` preserves ordered event batches.
- Ordinary interactive `ModelOutputObserved` text currently reaches `IConsoleSurface.WriteSegmentsAsync()` at model-chunk cadence. There is no Markdown parser, semantic Markdown document, or `tui:renderMarkdown` setting.
- `PrettyPromptConsoleSurface` is the current concrete composer/output adapter. Spectre.Console rendering, dynamic status, and ordinary semantic writes are serialized by `_consoleGate`.
- Plan 49 activity refresh is timer-driven rather than a new domain-event projection: `RefreshActivityUntilCompletedAsync` updates the already active Spectre status text at most every 250 ms while its activity owns the console gate. A refresh changes elapsed display text in place; it does not append transcript output, change activity identity, or represent a lifecycle boundary.
- Plan 26 emits the host-owned composer-adjacent session-status projection after run output and before the next composer. Raw transcript, persistence, context, restoration, redirected output, and headless output remain terminal-neutral.
- Existing tests cover semantic segments, console-gate/activity lifecycle, Plan 49 duration/detail behavior, Plan 26 status layout, and dependency direction, but no Markdown parsing or complete-answer-block collector exists.

## 6 Proposed Design

### 6.1 Binding Invariants

1. `ConversationTranscript.Apply(IDomainEvent)` remains the only raw transcript append boundary.
2. Every accepted `ModelOutputObserved.Text` appends immediately and exactly once to raw transcript state.
3. Rendered mode defers only the visible projection of the active answer block; it never rewrites or duplicates scrollback.
4. Source mode (`tui:renderMarkdown=false`) preserves the existing chunk-by-chunk cadence and all safe characters; terminal controls are visibly escaped before `IConsoleSurface` receives them.
5. A block never crosses a tool, MCP, visible host-status/diagnostic/output event, run-terminal, session, cancellation, or failure boundary; unknown non-model events close conservatively before projection. A timer-driven in-place redraw of the already active activity row is explicitly not a boundary.
6. Every boundary flush completes before that event's visible segment, activity start/stop/replacement, status, prompt, or completion output is projected; a pre-tool answer therefore renders before the tool marker/activity, and post-tool continuation is a distinct block. Periodic refresh callbacks remain inside the current activity operation and never invoke block classification or flush.
7. Markdig types stop at `IMarkdownParser`; terminal-library types stop at the concrete console adapter.
8. `TuiMarkdownDocument` contains only bounded immutable host-owned nodes and validated terminal-safe text/link metadata.
9. All document, semantic-segment, activity, status, and prompt writes use the same serialized console gate.
10. Themes style semantic roles only; rendering code contains no literal foreground/background colors.
11. Style suppression changes style only, not the semantic document's textual layout or markers.
12. Parsing/layout failure and size-limit transition emit the terminal-safe source projection exactly once and preserve event order.
13. Raw Markdown—not rendered or terminal-safe presentation text—is authoritative for persistence, context, restoration, inspection, redirected output, and headless output.
14. No interactive output item accepted by `IConsoleSurface` contains raw ANSI/OSC/C0/C1 controls, active HTML, unsafe links, or terminal commands; capability-proven redirected raw-source items can never route to an interactive backend.
15. Control neutralization changes presentation only: exact untrusted source is retained upstream and is never overwritten by escaped display text.
16. Markdown collection/parsing/layout may transform only accepted `ModelOutputObserved` presentation. It never parses, reconstructs, suppresses, or changes the semantic role/content of reasoning, tool/MCP activity, tool completion, host status/error, diagnostics, diffs, prompts, validation, or session-status projections.
17. Plan 49 remains authoritative for `tui:showOperationDurations`, monotonic request/tool/MCP timing, four-updates-per-second refresh, source classification, sanitized activity detail, final outcome/duration markers, legacy omission, and duplicate-MCP suppression. The sole lifecycle amendment is that final-answer `THINKING` stops immediately before the completed document becomes visible rather than on the first buffered raw delta; no clock owner, start timestamp, formatter, detail, or final duration changes.
18. Plan 26 remains authoritative for the complete composer-adjacent session-status projection, including its existing folder/repository, branch, model, reasoning, context, token, width, priority/omission, semantic-role, and suppression behavior; the final answer flush completes before that unchanged row and composer are written.

### 6.2 Package integration

Add Markdig through Central Package Management and reference it only from `Threadsmith.Tui`. The currently evaluated stable package is Markdig 1.3.2 (BSD-2-Clause, .NET 10 compatible); implementation must revalidate the exact stable version, license, advisories, and resolved dependency graph before pinning it in `Directory.Packages.props`.

```xml
<PackageVersion Include="Markdig" Version="1.3.2" />
```

No HTML conversion is used. The adapter consumes Markdig syntax nodes directly. Architecture tests reject Markdig references from Core, Execution, Persistence, public projections, and non-TUI product projects.

### 6.3 Closed Markdown syntax profile

Build one immutable `MarkdownPipeline` at composition time with an explicit allowlist:

- CommonMark paragraphs, headings, blockquotes, thematic breaks, inline/fenced code, ordered/unordered lists, emphasis/strong emphasis, and links;
- pipe tables;
- task lists;
- strikethrough;
- safe autolinks limited by the host link policy.

Do not call `UseAdvancedExtensions()`. Disable HTML parsing/rendering. Unsupported/raw HTML is always displayed as inert visibly escaped source text within the semantic document; it never triggers active HTML rendering. Terminal-safe source fallback is reserved for bound, parse, validation, layout, cancellation, or adapter failure. Do not enable media, diagrams, mathematics, YAML, custom containers, attributes, emoji expansion, or smart punctuation implicitly.

The profile has a stable internal schema/version identifier included in focused diagnostics and golden fixtures so later syntax changes are deliberate.

### 6.4 Host-owned semantic document model

Define internal immutable nodes such as:

```text
TuiMarkdownDocument
  Blocks:
    Paragraph
    Heading(level)
    BlockQuote
    List(ordered, start)
    ListItem(taskState?)
    CodeBlock(language?)
    Table(columns, rows)
    ThematicBreak
  Inlines:
    Text
    Emphasis
    Strong
    Strikethrough
    InlineCode
    Link(label, validatedTarget?)
    SoftBreak
    HardBreak
```

Names are illustrative; implementation follows repository naming precedent. Nodes carry content and semantic structure, not colors, terminal widths, Markdig spans, Spectre objects, or executable behavior.

Validate at construction:

- maximum source length;
- maximum block/inline node count;
- maximum nesting depth;
- maximum list items;
- maximum table rows, columns, and cell text;
- maximum code-block and link lengths;
- valid heading/list/table values;
- no raw ANSI, OSC, disallowed controls, or invalid Unicode.

The parser returns a discriminated success/fallback result with bounded sanitized diagnostics. It never logs model content.

### 6.5 Link and untrusted-content policy

Markdown is untrusted presentation data.

- HTML is never executed or passed through as terminal markup.
- Add one host-owned terminal-safe text encoder shared by semantic documents, source mode, oversize transition, and every parse/layout/cancellation fallback. It preserves printable Unicode, line feed, and tab; it renders carriage return, other C0 controls, DEL, C1 controls (including ESC/CSI/OSC), and invalid Unicode code units as deterministic visible uppercase `\\uXXXX`/`\\UXXXXXXXX` escapes.
- The encoder operates on presentation copies only. Exact source remains unchanged in transcript, persistence, context, redirected output, and headless output.
- For interactive writes, `IConsoleSurface` accepts validated terminal-safe text/document DTOs and defensively rejects any unsafe unencoded control rather than writing it. No fallback may bypass this boundary or call the terminal backend with raw model text.
- Exact redirected output uses a distinct raw-source output item admitted only when the immutable surface capability snapshot proves output is non-terminal; the adapter must reject that item if attached to an interactive terminal. This preserves byte-exact redirected data without creating a terminal-injection path.
- Only host-validated `https`/`http` targets may become clickable through the existing link abstraction; other schemes remain visible inert text.
- Link labels and destinations are bounded; query content is not newly logged or projected into diagnostics.
- Image syntax renders bounded alt text and an inert validated destination marker, or falls back plainly; it never fetches content.
- Code fences preserve exact printable content; unsafe controls are visibly escaped. Language identifiers are bounded labels only and never select executable behavior.

### 6.6 Ordered answer-block collector

Add a TUI-internal `ModelAnswerBlockCollector` owned by the shell projection, not Core or durable state.

For every ordered event in the interactive projection:

1. Apply the event to `ConversationTranscript` first so exact raw state is immediately current.
2. Classify the event before projecting any visible segment, activity transition, status update, prompt, or completion signal. The classifier is closed and deterministic:
   - an accepted `ModelOutputObserved` delta is answer content;
   - a proven invisible, non-boundary bookkeeping event may leave the active block open;
   - a timer-driven callback that only redraws elapsed text for the already active `THINKING`, `TOOLS`, or `MCP` row is a non-event, non-boundary refresh and leaves the active block open;
   - every ordered event that starts, stops, replaces, appends, or otherwise projects visible output/activity/status/prompt state, plus every tool/MCP/run/session/cancellation/failure/shutdown boundary, closes the active block first;
   - an unknown non-model event closes conservatively before projection.
3. If Markdown rendering is disabled, encode each accepted model delta as terminal-safe source text and pass it to the existing segment path with the same chunk cadence.
4. If enabled, append accepted model deltas to the active block without emitting them visibly.
5. When classification requires closure, stop/await answer activity, parse and write the active block (or its terminal-safe source fallback), and await that write before projecting the current event. This applies to host status, diagnostics, tool/MCP markers, run terminal output, session transitions, and any other visible event—not only `ToolInvocationStarted` and `RunCompleted`.
6. After a boundary, a later accepted model delta opens a new block.
7. `RunCompleted` closes/writes the final block before run-completion spacing, completion signaling, and composer return.
8. Failure/cancellation/shutdown terminalizes accepted partial text through a bounded parse attempt or terminal-safe source fallback; it never discards text or bypasses control neutralization.

The flush and the triggering event's output are enqueued as one ordered `TuiOutputItem` batch where possible, or are awaited sequentially under the same serialized output authority. No event projection may overtake a pending block flush. Leading whitespace suppression stays owned by `ConversationTranscript`; the collector receives only accepted deltas.

At the configured source-size limit, atomically flush the accumulated block as terminal-safe source text, switch that block to streamed-source mode, and encode/stream subsequent deltas. This avoids unbounded allocation without losing, duplicating, or directly emitting unsafe source content.

### 6.7 Activity lifecycle

In default rendered mode, `THINKING` remains active while model chunks accumulate. Its existing timer may redraw elapsed text in place under the activity's serialized surface ownership; those callbacks do not pass through the event classifier and do not close or fragment the block. Before a real boundary makes a block visible:

1. signal and await activity completion;
2. acquire the serialized console gate;
3. render the semantic document or terminal-safe source fallback;
4. emit the triggering status/diagnostic/tool/run semantic items in event order;
5. release the gate;
6. start the triggering activity or signal run/session completion as appropriate.

An answer therefore becomes visible before any later status, diagnostic, prompt, `TOOLS`/`MCP` activity, run output, or session transition. Tool completion may resume `THINKING` for the continuation. The composer never opens before the final document/fallback write completes.

Plan 49 compatibility is binding:

- the total-turn monotonic start time is retained while a Markdown block accumulates and across tool/MCP continuations; it is never restarted by parsing or rendering;
- `THINKING` continues its bounded elapsed refresh while answer text is buffered, then stops and is observed immediately before the document write—the necessary amendment because the raw delta is not yet visible;
- after a pre-invocation document flush, the existing source-specific `TOOLS` or `MCP` live row uses the original activity start/timing metadata, same bounded sanitized detail, and same elapsed-time behavior; if display was delayed by the ordered answer flush, its first elapsed value catches up rather than restarting at zero;
- completion markers retain the exact host-projected source label, bounded name, `completed|failed|cancelled|timed out` outcome, optional sanitized detail, and authoritative final duration; imported MCP tools still produce one MCP row, not a duplicate generic-tool row;
- built-in detail examples such as a `read_file` requested path/line range and a sanitized bounded `run_process` command remain ordinary semantic activity text and never enter Markdig;
- `tui:showOperationDurations=false` still removes duration suffixes and periodic redraw only; activity words, outcome, source, and sanitized detail remain;
- legacy events without source/duration continue to omit unknown values rather than fabricating them;
- the complete Plan 26 session-status row renders with unchanged host-derived fields, width/priority/omission/style/suppression rules, and ordering after the final document and before the next composer.

The Markdown collector consumes no duration clock, activity-detail formatter, source classifier, or session-status data. It coordinates only stop/observe/write/start ordering through the existing shell authority.

### 6.8 Parser boundary

Introduce an internal boundary such as:

```csharp
internal interface IMarkdownParser
{
    MarkdownParseResult Parse(string source);
}
```

The Markdig adapter owns pipeline configuration, AST traversal, source extraction, unsupported-node policy, and limits. The shell depends only on the interface/result. Tests use both deterministic fake results and real Markdig fixtures.

Parsing is synchronous CPU work over a bounded block. Check cancellation before parsing and use the existing bounded terminalization path if cancellation is already requested. If measured pathological input can exceed the interactive latency budget, move parsing behind the repository's bounded abandon-and-discard pattern or an isolated adapter; do not introduce unbounded background tasks.

### 6.9 Semantic roles and theme integration

Extend `TuiTextRole`/theme resolution only for stable Markdown semantics that materially require distinct styling, for example:

- `MarkdownHeading`;
- `MarkdownEmphasis`;
- `MarkdownStrong`;
- `MarkdownQuote`;
- `MarkdownInlineCode`;
- `MarkdownCodeBlock`;
- `MarkdownTableHeader`;
- existing `Hyperlink`, `Default`, and `Muted` where sufficient.

The exact minimal role set is selected after inspecting existing role fallbacks. Every new role has:

- a terminal-native default with no ordinary transcript background;
- deterministic fallback through `Default` or `Muted`;
- entries or documented fallback in all compiled themes;
- bounded validated configured-theme support;
- style-free parity under `NO_COLOR`.

Code blocks use structure, indentation, and an optional terminal-native border/label—not a literal background palette. Syntax-token roles are deferred.

### 6.10 Console-surface document boundary

Extend `IConsoleSurface` with a terminal-neutral operation such as:

```csharp
Task WriteDocumentAsync(
    TuiMarkdownDocument document,
    CancellationToken cancellationToken = default);
```

For exact ordering with adjacent boundary-triggering status/diagnostic/prompt/tool/run/session segments, prefer an ordered `TuiOutputItem` batch that can contain semantic segments and semantic documents under one gate acquisition. A distinct raw-source item is valid only for a capability-proven redirected surface.

The current `PrettyPromptConsoleSurface`/Spectre adapter:

- validates the document before acquiring the gate;
- measures current terminal display-cell width at the write boundary;
- maps document nodes to Spectre `Text`, rows, tables, and panels internally;
- obtains every style from `TuiThemeResolver` semantic roles;
- escapes all markup and never accepts raw renderables from the shell;
- inserts one presentation-owned blank line before the first interactive item of every model answer block, including source-mode/fallback blocks, without changing redirected raw source;
- writes the whole ordered batch while holding `_consoleGate`;
- produces an equivalent style-free structure when styling is suppressed.

Fake surfaces capture `TuiMarkdownDocument`/`TuiOutputItem` values structurally. Tests do not depend broadly on ANSI snapshots. A future non-Spectre adapter can implement the same document contract without changing parsing, collection, transcript, or shell ordering.

### 6.11 Layout rules

Define deterministic, width-aware layout behavior:

- headings strip ATX/setext source delimiters and preserve text and level through semantic style plus deterministic block spacing; H1/H2 add bounded theme-neutral double/single underline rules so hierarchy remains visible when decoration is unavailable;
- ordered/unordered/task lists retain visible markers and wrap with hanging indentation;
- blockquotes retain a visible `>`-equivalent marker;
- code preserves whitespace exactly, wraps only according to one documented policy, and remains fully selectable;
- tables render as tables only above a proven minimum width, otherwise degrade deterministically to labeled rows without dropping cells;
- thematic breaks use a stable plain-text marker;
- links preserve visible labels and an honest destination representation when terminal hyperlink support is unavailable;
- Unicode cell width uses the existing Plan-26 measurement contract;
- resizing affects only documents not yet written; native scrollback is never rewritten.

Styled and `NO_COLOR` output use the same textual layout and structural markers; heading source delimiters never reappear after semantic parsing.

### 6.12 Configuration and output modes

Add one layered scalar setting:

```text
tui:renderMarkdown = true
```

- `true` (default): collect and render complete semantic answer blocks interactively.
- `false`: preserve existing model-chunk cadence through the `Default` semantic role, using terminal-safe source text that is identical for ordinary safe input and visibly escapes controls.

The setting is snapshotted at each submitted-turn boundary and does not change within a block. Redirected/headless output remains raw source regardless of the TUI setting. Do not add multiple renderer/theme/syntax options in this milestone.

### 6.13 Transcript, restoration, and copying

Raw Markdown remains authoritative in `ConversationTranscript`, persisted events/archive, context assembly, diagnostics-safe projections, and headless output. No AST or rendered terminal text is persisted.

Restored historical transcript is not automatically reparsed because the current restored string does not retain all live answer/tool block boundaries as presentation identities. Newly observed blocks render normally after resume. Native selection copies the visible rendered layout; a future explicit raw-source inspection/export surface is separate work.

## 7 Public Contracts

- Markdig is referenced only by `Threadsmith.Tui`.
- `IMarkdownParser`, parse results, nodes, output items, and layout contracts are internal TUI-owned types.
- No public Core event, persistence schema, or model-provider contract changes.
- No terminal-library or Markdown-parser type enters Core, Execution, Persistence, durable events, context, extensions, MCP, or headless projections.
- PrettyPrompt remains an input detail and Spectre remains a replaceable output-adapter detail.
- Architecture tests enforce these boundaries.

## 8 Project/File Changes

| Area | Expected files | Change |
|---|---|---|
| Packages | `Directory.Packages.props`, `src/Threadsmith.Tui/Threadsmith.Tui.csproj` | Central Markdig pin and TUI-only reference |
| Markdown model/parser | new focused files under `src/Threadsmith.Tui` | Host semantic nodes, bounds, parser result, Markdig adapter |
| TUI roles/themes | `TuiVisualStyles.cs` and existing theme files | Minimal semantic Markdown roles and validated fallbacks |
| Event projection | `TuiShell.cs`, `TuiEventSegments.cs`, collector file | Raw append plus default rendered/source-mode branching and ordered block closure |
| Console surface | `ConversationalShell.cs` or extracted adapter files | Gated semantic-document/output-item operation and Spectre mapping |
| Configuration/composition | existing TUI display-options and App composition files | Default-on scalar setting and turn snapshot |
| Tests | `Threadsmith.Milestone1.Tests`, `Threadsmith.Milestone3.Tests`, `Threadsmith.Milestone8.Tests`, `Threadsmith.Milestone9.Tests`, architecture tests, focused M22.3 suite if justified | Parser, structure, layout, ordering, fallback, security, configuration, terminal seams, plus unchanged Plan 26/49 duration/activity/detail/status regressions |
| Docs/config | `.threadsmith/config.example`, user guide, manual plan, source/test/root DOX | Implemented behavior and changed visible-streaming contract when code lands |

Follow local composition and file-organization precedent after structural inspection; do not create abstractions with only one trivial use unless they establish the parser/adapter test boundary described above.

## 9 Ordered Tasks

### Task 1 — Pin and isolate Markdig

1. Revalidate current stable Markdig version, .NET 10 compatibility, BSD-2-Clause license, advisories, and dependency footprint.
2. Add the exact version to Central Package Management and an unversioned TUI reference.
3. Add architecture coverage proving parser types cannot leak outside TUI.
4. Build/package all release RIDs and record the incremental payload.

### Task 2 — Define the closed syntax and semantic model

1. Create bounded immutable document/block/inline nodes.
2. Define construction validation and stable syntax-profile version.
3. Configure the explicit Markdig allowlist with HTML disabled.
4. Map every admitted AST node; give every unsupported node a deterministic inert/fallback policy.
5. Add structural fixtures for CommonMark plus tables, tasks, and strikethrough.

### Task 3 — Implement security and bounds

1. Enforce source, node, depth, list, table, code, link, and text limits during traversal.
2. Implement the shared terminal-safe text encoder and validated DTO boundary for rendered documents, streamed source, oversize transition, and all failure/cancellation fallbacks.
3. Visibly escape ANSI, OSC, disallowed C0/C1 controls, DEL, carriage return, and malformed Unicode; keep exact raw source unchanged upstream.
4. Render active HTML, unsafe schemes, and images/media inertly; use the existing validated link abstraction only for eligible targets.
5. Ensure diagnostics contain only bounded type/outcome/position metadata, never model content or full URLs.
6. Measure pathological nesting/table/emphasis fixtures and establish the synchronous parse budget.

### Task 4 — Add semantic roles and layout rules

1. Select the smallest new Markdown role set after inspecting existing fallbacks.
2. Update system and compiled themes with terminal-native defaults/fallbacks.
3. Implement width-aware block/inline mapping in the current console adapter.
4. Add table narrow-width degradation, hanging list indentation, code whitespace, link fallback, and Unicode-width behavior.
5. Prove styled and `NO_COLOR` layouts have identical text/markers.

### Task 5 — Add the gated document surface

1. Add `WriteDocumentAsync` or ordered `WriteOutputAsync` to `IConsoleSurface` using only host-owned DTOs.
2. Distinguish validated interactive terminal-safe items from exact redirected raw-source items; admit the latter only under a capability snapshot that proves non-terminal output.
3. Implement the Spectre mapping and fail-closed interactive/raw-item validation solely inside the concrete adapter.
4. Acquire `_consoleGate` once for a document plus immediately following ordered semantic items.
5. Preserve existing segment/status/activity methods without nested gate acquisition.
6. Add fake-surface structural capture and focused adapter tests; avoid broad ANSI goldens.

### Task 6 — Add ordered block collection and default mode

1. Add default-true `tui:renderMarkdown` through existing display configuration.
2. Snapshot mode at submitted-turn boundaries.
3. Keep every exact raw transcript append immediate.
4. Add the closed event classifier and close/write before every ordered event that starts, stops, replaces, or appends visible output/activity/status/prompt state, every declared lifecycle boundary, and every unknown non-model event; keep periodic in-place activity-refresh callbacks outside classification as non-boundary redraws.
5. Await each flush before projecting its triggering event so visible output cannot overtake earlier model text.
6. In source mode, retain current chunk cadence for safe text while passing every delta through terminal-safe encoding.
7. At size limit, flush accumulated terminal-safe source once and continue encoded source streaming.
8. On cancellation/failure/shutdown, render a bounded partial block or use terminal-safe source fallback without loss/duplication/control emission.

### Task 7 — Preserve activity, duration, detail, and session-status contracts

1. Keep Plan 49's original total-turn monotonic start and bounded `THINKING` elapsed refresh active while a rendered-mode answer block accumulates.
2. Stop/observe activity before writing a completed document without resetting or recomputing request/tool/MCP durations.
3. Render the active document before every triggering visible status/diagnostic/prompt/tool/run/session projection.
4. Start the existing source-specific `TOOLS`/`MCP` state after a pre-tool flush with unchanged name, source, sanitized activity detail, cancellation ownership, and original activity timing metadata; delayed display catches up instead of restarting elapsed time.
5. Preserve completion marker role/text, outcome, detail, authoritative duration, legacy omission, and one-row MCP behavior byte-for-byte for the same host projection.
6. Resume `THINKING` for a post-tool continuation using the original total-turn start; when durations are disabled, retain activity/detail/outcome words and perform no periodic duration redraw.
7. Render the final block before the unchanged Plan 26 session-status row, `renderedRunCompletion`, and composer return.
8. Prove failures release the gate, observe refresh tasks, and propagate through the shell boundary without dropping activity details or final markers.

### Task 8 — Add deterministic verification

Cover:

- default-on complete-block rendering and explicit source-mode streaming with control neutralization;
- adversarial chunk boundaries producing the same semantic document;
- `answer A` → visible host-status/diagnostic → tool → `answer B` order with every boundary flush preceding its triggering output;
- reasoning/host/tool/diff exclusion;
- leading whitespace behavior;
- every admitted block/inline node;
- unsupported/raw HTML and unsafe link/image/control input;
- all configured size/depth/table/list/code limits;
- narrow/wide terminal layout and Unicode cell width;
- semantic theme roles and exact style-suppression text parity;
- parse/layout exception, oversize, source-mode, and cancellation fallbacks without loss/duplication or raw control emission;
- byte-exact capability-proven redirected output plus rejection of redirected raw-source items by an interactive backend;
- activity/gate/composer ordering under deterministic concurrency;
- repeated timer-driven `THINKING` refreshes while one answer accumulates without fragmenting or flushing that block;
- Plan 49 default-on and disabled timing, original total-turn resume, four-per-second refresh cap, source-specific tool/MCP rows, authoritative final duration, legacy omission, and no duplicate MCP row;
- unchanged bounded sanitized built-in details, including `read_file` path/line range and `run_process` command, in live and completion rows without Markdown parsing;
- unchanged tool/MCP names, outcomes, roles, provenance, and hidden raw extension/MCP arguments;
- unchanged complete Plan 26 session-status field set and width/priority/omission/style/suppression behavior after final document output;
- the existing Plan 49/Scenario S focused regression suites in addition to Plan 63 tests; preserve every timing/detail/configuration/status assertion and add source/rendered branches only for the explicit final-visible-answer stop amendment;
- raw persistence/context/restoration/headless authority;
- Markdig/TUI and terminal-library dependency isolation.

At minimum, run the existing Plan 49 owners—`Threadsmith.Milestone1.Tests` (including `OperationDurationFormatterTests` and activity projection), `Threadsmith.Milestone3.Tests` (including `Plan49ToolTimingTests` and reviewed activity-detail fixtures), relevant Milestone 8/9 MCP timing suites, and architecture tests—plus the new Plan 63 suite. Retain their existing assertions. Only the final-visible-answer activity-stop fixture may gain additive source-mode (first streamed chunk) and rendered-mode (immediately before document write) expectations; deleting or weakening duration/detail/status/configuration assertions is not acceptable.

### Task 9 — Real-terminal verification

Validate Windows Terminal plus maintained Linux/macOS/SSH/multiplexer coverage for long responses, tool continuations, request/tool/MCP elapsed displays enabled and disabled, `read_file` path/line-range detail, sanitized `run_process` command detail, MCP single-row identity, Plan 26 session status, light/dark/custom themes, `NO_COLOR`, resize, native selection, `Ctrl+C`, 10 KB/100 KB paste, scrollback, tables, code, links, cancellation, and composer return. Confirm no elapsed/detail/status regression, alternate screen, mouse capture, cursor rewrite, or duplicate answer.

### Task 10 — Documentation and DOX closeout

When implementation lands:

- document default rendered mode and `tui:renderMarkdown=false` source mode in `.threadsmith/config.example` and `docs/user-guide.md`;
- update the maintained manual test plan;
- amend `src/Threadsmith.Tui/AGENTS.md` from visible answer-chunk streaming to the approved complete-block default while retaining immediate raw transcript appends and source-mode streaming;
- update source/test/root/docs DOX, milestone status, current-state summaries, and Scenario AC coverage;
- amend Scenario S only to add the rendered-mode final-visible-answer stop branch while retaining its source-mode and all duration/detail/configuration/status assertions;
- do not document the setting as available before implementation.

## 10 Testing

Testing is layered and deterministic:

- Parser/model unit tests cover every allowed node, inert raw HTML, unsafe links/media, malformed Unicode, controls, bounds, and deterministic fallback without depending on ANSI snapshots.
- Collector tests use scripted events and fake surfaces to prove chunk-boundary independence, exact raw appends, flush-before-boundary order, unknown-event closure, source-mode cadence, and lossless cancellation/failure/oversize terminalization.
- Fake-time concurrency tests run multiple 250 ms `THINKING` refresh callbacks during one accumulating answer and prove they update elapsed activity without classifying an event, flushing, fragmenting, or reparsing the block. Separate fixtures cover real activity start/stop/replacement boundaries.
- Adapter tests prove one serialized gate, semantic/style-free text parity, current-width layout, interactive raw-control rejection, and capability-gated exact redirected output.
- Existing Plan 26 and Plan 49 suites retain their status, timing, detail, source, outcome, configuration, restoration, and MCP-deduplication assertions. Only final-visible-answer termination gains explicit source/rendered branches.
- At minimum run `Threadsmith.Milestone1.Tests`, `Threadsmith.Milestone3.Tests`, relevant `Threadsmith.Milestone7_1.Tests`, `Threadsmith.Milestone8.Tests`, `Threadsmith.Milestone9.Tests`, `Threadsmith.Architecture.Tests`, and the focused Plan 63 suite selected during implementation.
- Scenario AC and maintained Windows/Linux/macOS/SSH/multiplexer checks remain milestone gates. Task 8 and Task 9 define the complete automated and real-terminal matrix.

## 11 Security/Permissions

- Plan 63 adds no repository mutation, process execution, network fetch, browser action, credential access, trust grant, approval bypass, or new user permission. Markdown remains untrusted presentation data.
- Markdig is an in-process TUI parser, not an execution or sanitization authority. The host allowlists syntax, disables active HTML, bounds work, validates links, neutralizes terminal controls, and maps only host-owned nodes.
- Interactive surfaces accept only validated terminal-safe documents/items. Exact raw-source items require an immutable non-terminal capability snapshot and fail closed if routed to an interactive backend.
- Links never bypass existing scheme validation; images/media never fetch. Code-language labels never select executable behavior.
- Diagnostics and logs never contain model source, full URLs, raw tool/MCP arguments, credentials, or terminal-control payloads.
- Resource bounds and deterministic fallback limit parser/layout denial of service; cancellation never permits a raw unsafe write.

## 12 Observability

- Add no durable domain event, transcript field, or persistence schema for parsed documents. Exact raw source remains the authoritative observable record.
- Focused bounded diagnostics may record rendering mode, syntax-profile version, success/fallback outcome, limit/failure category, source-length bucket, node-count bucket, and parse/layout duration; they must not include source text, link queries, raw URLs, or exception data containing model content.
- Existing Plan 49 request/tool/MCP clocks, refresh cadence, activity text, details, outcomes, and final durations remain the visible timing authority. Periodic in-place refresh callbacks remain non-boundary redraws and must not emit collector events or document-count telemetry.
- Existing host error/status projection reports a bounded rendering fallback without duplicating the answer or exposing unsafe source. Fake surfaces and deterministic clocks provide test observability for order, gate ownership, document count, and activity transitions.

## 13 Migration/Compatibility

- This is an intentional default interactive presentation change: existing configurations with no key use complete-block Markdown; `tui:renderMarkdown=false` restores the prior safe-text chunk cadence.
- No data migration is required. Existing events, raw transcripts, archives, context, restored sessions, redirected output, and headless output retain their formats and authority; restored historical transcript is not implicitly reparsed.
- No public Core/provider/persistence/extension/MCP contract changes. Internal `IConsoleSurface` implementations and fakes must add the host-owned document/output-item operation.
- Plan 49 activity refresh remains compatible by explicit classification: timer-driven redraws of the current row do not close blocks, while ordered activity start/stop/replacement events do. Rendered mode alone moves final-answer `THINKING` termination to immediately before document presentation.
- Plan 26 status content/layout/order, semantic themes, `NO_COLOR`, native selection, scrollback, paste, cancellation, and composer behavior retain their current contracts.
- Parse/layout/bound failures degrade to terminal-safe source presentation without altering authoritative raw state. Syntax highlighting and historical re-rendering remain deferred, so no compatibility dependency is created.

## 14 Acceptance Criteria

- [ ] Markdig is centrally pinned, TUI-only, and used only to build a host-owned semantic document.
- [ ] Markdown rendering defaults on interactively; `tui:renderMarkdown=false` restores current chunk cadence for safe source and visibly escapes terminal controls.
- [ ] Default mode renders each contiguous non-reasoning answer block once, never rewrites scrollback, and never duplicates raw output.
- [ ] Every active block flushes before the triggering visible or lifecycle-boundary event; unknown non-model events close conservatively, periodic in-place activity refreshes do not close or fragment it, and no later projection overtakes earlier answer text.
- [ ] Markdown changes only accepted model-answer presentation; all reasoning, tool/MCP, host, diagnostic, diff, prompt, validation, and session-status projection content/roles remain on existing paths.
- [ ] Raw model text appends immediately and remains authoritative for persistence, context, restoration, and headless output.
- [ ] The semantic model and parser enforce closed syntax, bounds, inert HTML/control handling, and safe links.
- [ ] The console surface writes host documents through the existing serialized gate; shell/parser layers contain no Spectre calls.
- [ ] All Markdown presentation uses semantic roles with terminal-native defaults and style-suppression text parity.
- [ ] Tables, lists, headings, quotes, links, inline/fenced code, emphasis, tasks, and strikethrough have deterministic structural/layout coverage.
- [ ] Syntax highlighting is not required for milestone completion.
- [ ] Source mode, cancellation, failure, oversize transition, and shutdown lose or duplicate no accepted text; interactive items never carry raw terminal controls, while capability-proven redirected output remains byte-exact and cannot route to an interactive backend.
- [ ] Plan 49 request/tool/MCP elapsed displays, `tui:showOperationDurations` precedence/disabled behavior, refresh bound, source/outcome/final-duration markers, sanitized activity details, legacy omission, and single MCP row pass unchanged regression coverage.
- [ ] The complete Plan 26 session status retains all existing host-derived fields and width/priority/omission/style/suppression behavior and appears after final Markdown output before composer input.
- [ ] Native selection, paste, resize, scrollback, activity, tool order, and composer return pass maintained terminal gates.
- [ ] Scenario AC, package/architecture tests, user/config/manual docs, status, and DOX are current before M22.3 closes.

## 15 Risks

| Risk | Mitigation |
|---|---|
| Default complete-block output feels slower than token streaming | Keep truthful `THINKING`/tool activity, render boundary-closed blocks promptly, provide a documented source-mode escape hatch. |
| Host renderer grows into a general widget framework | Keep a closed Markdown node set, TUI-only scope, native scrollback, and no input/layout ownership outside document writes. |
| Tables/code wrap poorly at narrow widths | Width-aware adapter, deterministic table degradation, Unicode-cell tests, real-terminal gates. |
| Untrusted Markdown injects terminal control or unsafe links | Closed syntax, HTML disabled, validated text/link DTOs, markup escaping, control rejection, no active media. |
| Parser or AST becomes durable coupling | Persist raw source only; Markdig stops at one adapter; schema-version semantic fixtures protect behavior. |
| Theme expansion becomes inconsistent | Minimal roles, explicit fallbacks, all built-ins updated, configured-theme validation, `NO_COLOR` parity. |
| Large/pathological input blocks UI | Terminal-safe streaming source fallback at a hard size bound, AST/depth/table limits, measured parse budget, bounded cancellation terminalization. |
| A visible non-tool event overtakes buffered answer text | Closed event classification, conservative unknown-event closure, awaited flush-before-projection, and deterministic status/diagnostic/tool/run ordering tests. |
| Markdown integration regresses elapsed time, filenames/commands, outcomes, or session status | Treat non-model projections as opaque host-owned semantic items, preserve Plan 49/26 owners, and require their existing suites plus explicit interleaving fixtures as milestone gates. |
| Spectre/PrettyPrompt replacement later is expensive | Host document and console-surface contracts contain no library types; current libraries remain adapters only. |

## 16 Documentation

Planning updates include:

- this plan and the M22.3 detail contract;
- `milestones.md`, `README.md`, `00-shared-context.md`, and Scenario AC;
- the independent dependency-DAG branch based on plans 03, 24–26, and 49 without changing M23;
- root/docs DOX and the durable user preference for complete-block Markdown output.

Implementation later updates configuration, user/manual docs, TUI/test DOX, and current status. Add an ADR only if implementation makes the semantic document model public/cross-project, changes durable transcript representation, replaces native scrollback, or expands beyond the bounded TUI-private decision recorded here.

## 17 Open Decisions

None. The decisions required to begin implementation are resolved below; implementation must report any new architectural decision rather than choosing silently.

| Resolved decision | Resolution |
|---|---|
| Production Markdown library | Direct Markdig parsing; no MDView or end-to-end Markdown widget. |
| Ownership | Threadsmith owns syntax profile, semantic nodes, styles, layout, fallback, and tests. |
| Default | Rendered Markdown is on by default interactively. |
| Streaming | Default visible answer output is complete-block; raw transcript events still append immediately. Source mode preserves current chunk cadence but visibly escapes terminal controls. |
| Failure/source safety | Exact raw source remains upstream; every interactive presentation and fallback uses validated terminal-safe text. |
| Event boundaries | Flush before every ordered event that starts, stops, replaces, or appends visible projection state and before every declared lifecycle boundary; unknown non-model events close conservatively. Timer-driven in-place refreshes of the already active activity row are non-boundary redraws. |
| Existing TUI features | Plan 49 owns durations/activity/details and Plan 26 owns session status; Markdown treats both as opaque unchanged projections and adds regression gates. |
| Tool ordering | Close/render before tool start; continuation is a separate block. |
| Theme behavior | Semantic roles and terminal-native defaults; `NO_COLOR` preserves identical text/layout without style. |
| Console backend | Spectre may remain the initial gated adapter but is not part of the document contract. |
| Composer | PrettyPrompt remains for now; replacement is unrelated to Markdown. |
| Syntax highlighting | Deferred; fenced code is structured and semantically styled without token colors. |
| Durable representation | Exact raw Markdown only; no AST, layout, ANSI, or terminal object persistence. |
| Historical restoration | Existing restored history remains raw; only newly observed answer blocks render. |
