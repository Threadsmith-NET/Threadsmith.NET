# Plan 107 — In-TUI Mermaid text rendering

**Status:** Planned; repository research and a scratch Jint feasibility probe completed on 2026-09-13. No product implementation has started.
**Delivery track:** Maintenance — extend the existing retained TUIKit presentation capability.
**Prerequisites:** Implemented shared Markdown presentation (Plan 98 / ADR-51), TUIKit frontend and agent workspace (Plans 100 and 105 / ADR-52), normal layered configuration, current theme background semantics, and release-license closure (ADR-49). Plan 106 is not a prerequisite.

## 1 Objective

Render Mermaid fenced blocks as readable Unicode box-drawing or plain ASCII diagrams inside Threadsmith's TUIKit output pane. Provide an explicit configuration switch. MAIN and subagents must use the same implementation. Non-Mermaid content must retain its current streaming and formatting behavior; a diagram must never make the application buffer the rest of a response or wait for diagram rendering before displaying subsequent text or tool activity.

The user accepted reuse of the upstream renderer through Jint, subject to proper licensing. Implement a small dedicated adapter project, not a C# rewrite of Mermaid and not a terminal-output postprocessor.

## 2 Architectural Context

Follow [AGENTS.md](../../AGENTS.md), [shared context](00-shared-context.md), [planning governance](planning-governance.md), [C# guardrails](../guardrails/portable-csharp-guardrails.md), [ADR-51](../architecture/adr-51-frontend-neutral-interaction-coordination.md), [ADR-52](../architecture/adr-52-optional-tuikit-frontend.md), and [ADR-49](../architecture/adr-49-public-release-license-compliance.md).

- Interaction owns terminal-neutral source collection, Markdown parsing, and presentation contracts.
- TUIKit owns retained display state, viewport measurements, drawing, and the UI update queue.
- App composes the optional renderer. Jint and upstream JavaScript remain inside the new implementation project.
- Rendering is a projection only. Provider requests, tool authority, execution state, raw model logs, conversation persistence, and model-visible replay remain unchanged.
- Do not introduce a general renderer plugin framework, another event bus, a separate Markdown parser in TUIKit, or provider-specific handling.

## 3 Scope

- Configuration-controlled rendering of complete `mermaid` code fences in assistant output, including child tabs.
- Streaming recognition across arbitrary model chunk boundaries; render a completed block without waiting for the entire response to end.
- Flowcharts, sequence, state, class, and entity-relationship diagrams within the renderer's verified supported syntax.
- Width-aware text layout, current theme styling, resize/reflow, scrollback, and selection using the existing output architecture.
- Visible source fallback for incomplete, unsupported, invalid, cancelled, oversized-for-the-viewport, or failed renderings.
- A pinned, offline-at-runtime ASCII renderer bundle embedded in a .NET project using Jint.
- Dependency isolation, license notices, package/source provenance, packaging verification, configuration documentation, and Windows/Linux/macOS verification.

## 4 Non-Scope

- SVG, bitmap, browser, WebView, Mermaid Live, network rendering, Node.js runtime, or launching processes to display a diagram.
- A Mermaid-generating tool, model self-correction loop, parser messages inserted into model context, or prompt changes encouraging diagrams.
- Replacing Threadsmith's Markdown implementation with TUIKit's Markdown renderer.
- Broad changes to ordinary text streaming, tool blocks, reasoning visibility, composer behavior, or provider protocol handling.
- Arbitrary JavaScript from users, repositories, diagrams, configuration, or extensions.
- Full mermaid.js language parity; graphical themes, CSS, click handlers, icons, or external resources from Mermaid source.
- A new diagram editor, dedicated diagram modal, horizontal-scrolling subsystem, or slash command. Configuration is the enable/disable mechanism in this plan.
- Diagram rendering in the original frontend, redirected output, or headless execution. Their source and execution behavior remain intact.

## 5 Current State and Research

### 5.1 What pi-mermaid actually does

Research checkout: `C:\source\repos\pi-mermaid`, revision `34cab3ae794422d43707f129120a73ea39f51742`, package version 0.3.0. [Source](https://github.com/Gurpartap/pi-mermaid/blob/34cab3ae794422d43707f129120a73ea39f51742/index.ts), [dependency lock](https://github.com/Gurpartap/pi-mermaid/blob/34cab3ae794422d43707f129120a73ea39f51742/package-lock.json).

The extension extracts fenced Mermaid source, optionally validates it using `mermaid.parse`, and calls `beautiful-mermaid`'s `renderMermaidAscii` with colors disabled. Its automatic assistant hook is `agent_end`; it posts a separate custom message after completion. It also handles submitted input and offers a command to render the previous answer.

Its lockfile resolves beautiful-mermaid 1.0.2 and Mermaid 11.12.3. The extension tries four spacing presets, selects one that fits, and clips if none fits. It has source/count limits, small caches, and a collapsed preview. It can put validation diagnostics and source into model context. Reuse the text-layout approach, not those integration decisions or arbitrary thresholds.

### 5.2 How the layout works

Research checkout: `C:\source\repos\beautiful-mermaid`, revision `2ac8bbbb060ca0a65a6a21f3200bd99b1587b488`, package version 1.1.3. This is newer than Pi's lockfile and must not be described as the exact version Pi runs.

- [ASCII entry point](https://github.com/lukilabs/beautiful-mermaid/blob/2ac8bbbb060ca0a65a6a21f3200bd99b1587b488/src/ascii/index.ts): synchronous type dispatch and plain-text output. Explicit `colorMode: "none"` avoids ANSI/HTML generation.
- [Graph parser](https://github.com/lukilabs/beautiful-mermaid/blob/2ac8bbbb060ca0a65a6a21f3200bd99b1587b488/src/parser.ts): produces graph nodes, edges, and subgraphs from Mermaid text.
- [Grid layout](https://github.com/lukilabs/beautiful-mermaid/blob/2ac8bbbb060ca0a65a6a21f3200bd99b1587b488/src/ascii/grid.ts), [pathfinding](https://github.com/lukilabs/beautiful-mermaid/blob/2ac8bbbb060ca0a65a6a21f3200bd99b1587b488/src/ascii/pathfinder.ts), and [drawing](https://github.com/lukilabs/beautiful-mermaid/blob/2ac8bbbb060ca0a65a6a21f3200bd99b1587b488/src/ascii/draw.ts): position graph nodes on a grid, route connections, and draw characters into a canvas. Other diagram families have specialized text layouts.

The ASCII import graph can be bundled separately from the SVG renderer, ELK, and `entities`. The scratch bundle had only local upstream source inputs. Do not import the full package entry point into the shipped bundle.

### 5.3 .NET feasibility evidence

A scratch .NET 10 console probe used **Jint 4.16.2**, its resolved **Acornima 1.7.0** dependency, and **esbuild 0.25.9** to bundle the ASCII entry point as an IIFE. The generated, unminified script was 207,900 characters. All five core diagram families rendered successfully with `colorMode: "none"`.

On this Windows machine, one run measured approximately 550 ms for engine/bundle initialization, 146 ms for the first small flowchart, and 40 ms per repeated warm flowchart. Other small diagrams took roughly 7–43 ms. These are feasibility observations, not product benchmarks or cross-platform performance guarantees. The probe had no `process`, `require`, `fetch`, `System`, or `importNamespace` globals; an already-cancelled token produced Jint's `ExecutionCanceledException`.

Consequences: initialize once off the UI thread, reuse the engine under exclusive ownership, render only completed sources, and cache results. Recreating an engine or rendering four variants on each delta would be unacceptable.

[Jint](https://github.com/sebastienros/jint) is the managed interpreter, not a Node.js installation. Pin a released version; do not select unreleased Jint main merely because its README describes newer APIs. The inspected NuGet package points to revision `730db51d99d3bede8072ca55b0437c3206b83599`.

### 5.4 Known upstream limitations

The probe/source inspection found:

- `flowchart RL` is laid out as LR in the researched ASCII implementation. Do not display this as a faithful right-to-left diagram.
- A single-line `graph LR; A --> B` is rejected by this ASCII entry point.
- Label sizing uses JavaScript string lengths in several places. Wide CJK text, combining sequences, and emoji can misalign even when the final line is measured correctly.
- Aggressive compact spacing can obscure labels; one compact ER fixture clipped its relationship label. Fitting the overall width alone does not establish correctness.
- Supported diagram families do not imply support for every Mermaid construct. Some upstream parsing is permissive; successful rendering is not official Mermaid syntax validation.

Build fixtures around these cases. Initially use source fallback for unsupported cases unless a small, well-tested upstream fix is included with clear provenance. Do not silently reverse directions, remove meaningful statements, transliterate labels, or clip away relationships to force a diagram to fit.

The native .NET [Mermaider](https://github.com/nullean/mermaider) alternative was examined; its documented public rendering API targets SVG. It is not a ready replacement for this text renderer. A C# port or SVG-to-text conversion would substantially expand this work.

### 5.5 Threadsmith integration points

| Existing component | Relevant behavior |
|---|---|
| `src/Threadsmith.Interaction/Markdown/ModelAnswerCollector.cs` | Collects Markdown until an ordered output boundary; source mode emits deltas immediately. Its overflow/cancellation paths preserve source. |
| `MarkdownParser.cs`, `MarkdownDocument.cs`, `MarkdownValidator.cs` | Markdig is already isolated behind a closed host-owned document. Code blocks have code/language, but no explicit completed-fence metadata. |
| `Presentation/PresentationBatch.cs` | Source and Markdown items retain raw and terminal-safe copies. Batches carry an optional child target. |
| `Coordination/InteractionCoordinator.cs` | MAIN uses the collector and emits presentation before subsequent lifecycle/tool events. |
| `Agents/AgentWorkspaceProjection.cs` | Each child has a collector. Current paragraph/fence flushing includes a chunk-local fence toggle and a character-triggered flush; these cannot establish Mermaid fence completeness. |
| `Presentation/TerminalMarkdownLayout.cs` | Shared formatting converts semantic blocks into styled segments; fenced code currently remains literal code. |
| `src/Threadsmith.Tui.TuiKit/TranscriptView.cs` | Retains items, projects them, wraps lines, copies selected visible text, and reprojects on width changes. |
| `TuiKitSurface.cs`, `AgentViews.cs` | Own the UI update queue and isolated per-agent transcripts. |
| `src/Threadsmith.Tui/TuiDisplayOptions.cs`, `src/Threadsmith.App/InteractiveFrontendRunner.cs` | Load effective display settings and compose frontends. |

Use this path. TUIKit supplies display-cell measurement and retained drawing, but the inspected local TUIKit source has no Mermaid renderer to enable directly.

## 6 Proposed Design

### 6.1 Configuration and precedence

Add the following documented defaults:

```json
{
  "tui": {
    "mermaid": {
      "enabled": true,
      "characterSet": "unicode"
    },
    "limits": {
      "mermaid": {
        "executionTimeoutMilliseconds": 2000,
        "maximumEngineMemoryBytes": 134217728,
        "maximumCacheBytes": 8388608,
        "maximumPendingRenders": 32
      }
    }
  }
}
```

- `enabled` is the explicit switch for **TUIKit** Mermaid rendering. False uses the current source/code-block path and does not initialize Jint or its queue/cache.
- `characterSet` accepts `unicode` or `ascii`. This controls box/connector glyphs, not replacement of user labels. Default Unicode matches the existing TUI's box-drawing approach.
- Keep `tui.renderMarkdown` independent: it continues to control ordinary Markdown formatting. With Mermaid enabled and Markdown disabled, only complete Mermaid blocks become diagrams; surrounding content keeps its terminal-safe source streaming. The four switch combinations must have tests.
- Bind through the established effective configuration, not a renderer-specific file. Current precedence is compiled defaults < machine < user < repository < session < CLI `--set:` < `THREADSMITH_` environment overrides.
- Examples: `--set:tui:mermaid:enabled=false` and `THREADSMITH_TUI__MERMAID__ENABLED=false`.
- Read one immutable effective display snapshot using the existing frontend lifecycle. A restart applies file edits; hot reload and a new command are not required.
- Validate known keys, enum values, booleans, positive work limits, and representable arithmetic. Bad settings must produce a diagnostic naming the key through established configuration error handling; never silently accept an unused or malformed value.

The proposed limits govern optional display work only. Reuse existing Markdown/source and transcript retention settings rather than importing Pi's five-block/400-line/20,000-character restrictions. There is no new limit on repository size, model output, valid diagrams per response, or execution. When optional rendering cannot proceed, retain source and continue the turn. Cache capacity zero may disable caching; document that exception to positive-value validation. Measure defaults during implementation and record any justified adjustment once, keeping code/example/docs consistent.

### 6.2 Dedicated renderer boundary

Create `Threadsmith.Rendering.Mermaid` under `src/` with an internal Jint adapter and embedded, pinned ASCII-only bundle.

Dependency direction:

```text
Threadsmith.App -> Threadsmith.Rendering.Mermaid -> Threadsmith.Interaction
Threadsmith.App -> Threadsmith.Tui.TuiKit        -> Threadsmith.Interaction
Threadsmith.Rendering.Mermaid -> Jint -> Acornima
```

Declare the small renderer contract and immutable DTOs in `Threadsmith.Interaction/Mermaid`. No references from Core, Execution, Context, persistence, providers, or extension contracts to the adapter. Only App constructs it; TUIKit receives the interface. Do not add an extra abstractions project for these few contracts.

The adapter accepts Mermaid source, output options/available columns, and cancellation. It returns terminal-neutral lines or a typed fallback reason. It never writes to the console, invokes tools, accesses the repository, or mutates a Markdown document.

### 6.3 Preserve streaming; recognize only real Mermaid fences

Extend the shared answer collection path with a focused fence-aware component used by both MAIN and child collectors. Do not search already-rendered terminal text or sanitize Mermaid with a global regex.

Required behavior:

1. With Mermaid disabled, retain the existing fast path and chunk/flush behavior.
2. Non-Mermaid spans go through the existing Markdown/source collector. Do not wait for `agent_end`, scan the full conversation, or hold ordinary deltas until a diagram worker completes.
3. An opening fence can be split across chunks. Carry only the necessary line/fence recognition state; recognize both backtick and tilde fences with matching marker and sufficient closing length. A four-backtick outer code sample containing three-backtick Mermaid examples must stay literal.
4. Use Markdig's existing parse tree and source spans to confirm semantic fenced blocks. Extend the host-owned code-block metadata to distinguish an explicitly closed fence from an end-of-input code block. Do not assume Markdig accepting an unterminated fence makes it complete for diagram rendering.
5. Buffer only the candidate Mermaid block until its closing fence is established. Keep raw source intact. Completed blocks may be emitted before later response text or the final response boundary. Any small ambiguity buffer for a possible opening marker must be released as soon as it is ordinary content.
6. A single delta may complete a fence and contain ordinary text or another diagram. Return an ordered collection of presentation items when necessary, preserving every remainder exactly once. Do not introduce artificial answer spacing between pieces of the same answer.
7. Tool events, response changes, run completion, cancellation, and shutdown remain ordered boundaries. Flush incomplete Mermaid as safe source at those boundaries; never join fences across responses/runs/agents. A cancelled diagram must not consume the rest of the next answer.
8. For nested list/quote fences, preserve the containing Markdown structure and indentation. If incremental emission cannot yet finalize that container correctly, use the existing container flush boundary; do not split it into misleading top-level blocks.

Do not retain the child's chunk-local `InFence` toggle as a second Mermaid parser. Share the authoritative fence state, and ensure the existing character-triggered child flush cannot lose half a Mermaid block. Restrict changes to what this integration needs; ordinary code, paragraph, and tool presentation must retain their behavior.

### 6.4 Semantic presentation and asynchronous replacement

Recognize Mermaid as a specialized fenced-code presentation in the shared document/layout path. Preserve ordinary `MarkdownCodeBlock` rendering as the fallback. Add only the host-owned metadata/result types needed for complete-fence identity and rendering; do not spread Jint values or upstream AST objects into presentation records.

- Retain the source item in its original transcript position immediately. While the optional result is pending, display safe code source or a short diagram-local pending label. Subsequent ordinary output is admitted normally.
- Assign retained item/block identities scoped to the owning transcript. Schedule work outside `TuiKitSurface`'s render/update action and outside the event pump. An update action must not call Jint, block on a task, or render every retained diagram.
- Apply a completed result through the existing surface update queue to that same retained item/block. Replace its projection in place; do not append a second answer, custom tool result, or end-of-turn diagram message.
- Before replacement, verify session, agent target/generation, retained item identity, source identity, and width/options revision. Discard results for closed tabs, switched sessions, evicted items, or obsolete width requests.
- MAIN and child tabs use the same transcript renderer and scheduling implementation. Tab selection must not determine the destination of incoming work.
- Preserve scroll-follow/unseen-output behavior, selection semantics, one-blank-line answer/input boundaries, and tool ordering. If replacement changes row counts, retain the user's anchor to the same retained item where possible. Invalidate only selections whose source projection changed; never copy stale row offsets.

Keep `TerminalMarkdownLayout` deterministic. It may consume already-computed diagram results through a small read-only lookup/layout input; it must not own background work, configuration access, or Jint initialization.

### 6.5 Width, display fidelity, and theme behavior

- Determine available columns from the actual output content viewport after border, pane padding, and list/quote prefixes. Do not use console window width or the current `Math.Max(40, _width)` / layout minimum as a fictional diagram width.
- Try normal spacing first; calculate a more compact variant only when needed. Cache variants. Do not generate all variants during every paint, resize tick, or token delta.
- A successful diagram must fit without soft-wrapping any diagram line. Keep rectangular spacing and route all text through existing terminal-safe segment validation.
- If a tested compact layout still cannot fit, display the Mermaid source plus a concise explanation. Do not cut off arrows, node names, or connectors. Widening the pane can retry that retained block; ordinary source fallback can wrap normally.
- Measure using TUIKit's display-cell/grapheme rules. The upstream label-layout limitation is distinct from measuring the final output. Until a coherent width-aware upstream fix is tested, use an explicit source fallback for affected label sequences; never claim wide-label support from a final `string.Length` check.
- Support ordinary Unicode box drawing on Windows, Linux, and macOS; provide plain ASCII geometry through configuration. Do not choose behavior solely from OS name.
- Reuse `MarkdownCode` for diagram text and `Muted` for a local fallback explanation. Apply the existing output-pane background composition; explicit text-role foreground/background/decorations still win. No upstream ANSI colors, terminal palette changes, implicit inversion, or new cross-role inheritance.
- Existing copy-selection copies the visible diagram as plain text. The original source remains in retained source/persistence/raw logs as applicable; never replace the authoritative source with the rendered art.

### 6.6 Work ownership, caching, and failure behavior

- Use a single exclusively owned Jint engine per composed renderer initially. This is thread-affinity/resource ownership, not a new agent concurrency policy. Do not share one engine concurrently between tabs.
- Initialize lazily off the UI thread; a background warm-up after the frontend is ready is allowed when enabled. Neither startup choices nor semantic loading may wait for it.
- Prepare the bundled script once and reuse the initialized engine. Set per-request cancellation/timeout constraints using the pinned Jint version's actual APIs; reset them correctly between calls. Normalize Jint cancellation to the host's cancellation semantics. Recreate a failed engine when necessary without breaking subsequent diagrams.
- Key the display-only cache by full source identity, renderer/bundle version, character set, layout options, and effective width or selected layout variant. Use a full digest or exact source comparison, not Pi's eight-character hash. Cache expected invalid/unsupported outcomes to avoid repeated work; do not permanently cache cancellation.
- Account for source, rendered variants, pending work, and projected text within their respective configured budgets. Reuse transcript eviction notifications to cancel/drop orphaned work. Cache eviction must not remove source or cause a completed visible diagram to flicker away.
- If the work queue is full, preserve the source immediately with a local rendering-deferred explanation. Do not block provider/event ingestion. Retry only through explicit retained-view lifecycle opportunities, not a busy loop on every paint.
- One malformed diagram must not cancel a run, terminate the UI, suppress another agent, or prevent later diagrams from rendering. Expected failures produce a local fallback; real adapter defects are logged once at the owning boundary.

### 6.7 Reproducible bundle and licensing

Use the inspected beautiful-mermaid revision as the initial pin. Vendor only its ASCII entry-point dependency closure and relevant notices into the adapter's asset directory, with any modifications kept as explicit reviewed patches. Generate a checked-in bundle using a pinned maintenance-only bundling script/toolchain. Ordinary `dotnet build`, publish, first launch, and rendering must not install npm packages or download renderer code.

Record upstream repository, revision/version, source file manifest, license paths, bundler version/options, patch list, and bundle SHA-256. Rebuilding the bundle from those inputs must be reproducible. Preserve source attribution in the repository and include legal material in every distribution containing the embedded bundle.

Verified license evidence:

| Component | Observed license | Required treatment |
|---|---|---|
| beautiful-mermaid / Craft Docs | [MIT](https://github.com/lukilabs/beautiful-mermaid/blob/2ac8bbbb060ca0a65a6a21f3200bd99b1587b488/LICENSE) | Retain Craft Docs copyright, permission notice, and disclaimer with copied source and distributed bundle. |
| Original mermaid-ascii algorithm / Alexander Grooff | [MIT](https://github.com/AlexanderGrooff/mermaid-ascii/blob/master/LICENSE) | Preserve upstream attribution and corresponding notice for the ported drawing/layout material; pin the notice's source revision during vendoring. |
| Jint 4.16.2 | BSD-2-Clause; confirmed in restored package metadata | Include exact applicable copyright/license/disclaimer and package/source provenance. |
| Acornima 1.7.0 | BSD-3-Clause; confirmed in restored package metadata | Include the transitive dependency's complete notices, including upstream parser notices shipped by the package. |
| pi-mermaid | [MIT](https://github.com/Gurpartap/pi-mermaid/blob/34cab3ae794422d43707f129120a73ea39f51742/LICENSE) | Research inspiration only by default. If implementation copies extension code, include its copyright/license too. Do not list it as a runtime dependency when it is not shipped. |

These permissive licenses support reuse with their notice conditions. Threadsmith's Apache-2.0 license does not replace third-party licenses. The user's choice authorizes this implementation approach, not fabrication of release-owner approval records. Follow the existing ADR-49 evidence/review process.

Add Jint/Acornima to the centrally pinned and reviewed NuGet closure. Extend existing release legal generation to include the embedded non-NuGet source bundle: NuGet enumeration alone will miss it. Include the bundle and applicable notices in notices/SBOM/manifests, and verify payload hashes. Do not introduce a second licensing pipeline or weaken existing release gates.

## 7 Public Contracts

Names below describe intended ownership; refine signatures after inspecting consumers, keeping the boundary small:

- `MermaidDisplayOptions`: enabled flag and character set, loaded outside Interaction.
- `MermaidRenderingLimits`: validated display-work/cache settings under `tui:limits:mermaid`.
- `IMermaidTextRenderer.RenderAsync(MermaidRenderRequest, CancellationToken)`: returns a host-owned `MermaidRenderResult` with immutable lines, dimensions, and a supported outcome/fallback reason. No public Jint/Markdig/TUIKit types.
- Completed-fence/source identity metadata in the shared Markdown presentation vocabulary, validated alongside existing block types.
- Frontend-local retained block keys and result revisions for in-place projection; they are not persistent domain-event IDs.

If a frontend capability is needed to enable the shared collector behavior, add one narrowly named Mermaid-presentation capability rather than testing concrete frontend types in Interaction. Original/headless surfaces use their existing source behavior. Do not turn the optional render result into an execution event or tool result.

## 8 Project and File Changes

| Area | Planned changes |
|---|---|
| `src/Threadsmith.Rendering.Mermaid/` | New adapter, embedded ASCII bundle, relevant upstream source/notices, provenance manifest, Jint execution ownership. |
| `src/Threadsmith.Interaction/Mermaid/` | Small terminal-neutral contract/options/result vocabulary. |
| `src/Threadsmith.Interaction/Markdown/` | Shared complete-fence handling and semantic metadata; retain existing Markdig boundary. |
| `Interaction/Coordination/InteractionCoordinator.cs`, `Interaction/Agents/AgentWorkspaceProjection.cs` | Consume ordered collector output and share fence behavior without duplicating MAIN/child implementations. |
| `Interaction/Presentation/TerminalMarkdownLayout.cs` and related presentation contracts | Diagram-aware layout using ready results and existing source fallback. |
| `src/Threadsmith.Tui.TuiKit/TranscriptView.cs`, `AgentViews.cs`, `TuiKitSurface*.cs`, `TuiMarkdownLayout.cs` | Retained block identity/replacement, background scheduling, width-aware reflow, cancellation and eviction integration. Extract a focused helper rather than growing the surface into a renderer. |
| `src/Threadsmith.Tui/TuiDisplayOptions.cs`, `Interaction/Contracts/InteractionDisplayOptions.cs`, `TuiResourceLimits.cs` | Configuration projection and validation. |
| `src/Threadsmith.App/InteractiveFrontendRunner.cs`, configuration defaults | Optional composition only for TUIKit; no startup dependency when disabled. |
| `src/Threadsmith.sln`, `Directory.Packages.props` | Project registration and exact released dependency pins. |
| `tests/Threadsmith.Mermaid.Tests/` | Focused real-adapter/compatibility fixtures; self-contained project matching repository conventions. |
| Existing CoreRuntime, ParallelAgents, Architecture tests | Streaming/order/configuration/frontend integration and dependency isolation. |
| `.threadsmith/config.example`, user/operator docs, release scripts/evidence | Configuration examples, supported behavior, licensing and publish validation. |

Do not reference the local research checkouts or scratch probe from product builds. They are research evidence only.

## 9 Ordered Implementation Tasks

1. **Characterize before wiring.** Read the referenced contracts/current checkout. Add a concise fixture corpus for five core families, width/layout failures, malformed input, multiline labels, semicolon syntax, leading comments, nested fences, and RL. Confirm the exact pinned package/bundle runs with the production cancellation/memory constraints. Capture cold/warm timings and allocations. Decide source fallback versus narrowly scoped upstream fixes for each known gap; document the actual supported subset.
2. **Add the isolated adapter and legal assets.** Create the new project and reproducible ASCII-only bundle; centralize dependency pins; preserve upstream notices and provenance. Add architecture checks that only the adapter references Jint. Do not change provider or execution projects.
3. **Bind configuration.** Add typed options/defaults/validation and all four Markdown/Mermaid combinations. Implement disabled composition first, proving no engine creation or renderer initialization. Update sample configuration at the same time.
4. **Implement shared fence-aware collection.** Add terminal-neutral completed-fence metadata and source-preserving ordered output. Exercise arbitrary chunk splits and boundaries before wiring UI rendering. Replace duplicate child Mermaid detection with the shared mechanism while retaining non-Mermaid behavior.
5. **Integrate retained rendering once.** Schedule optional render work independently of text/tool admission. Keep source-position identity, apply ready results via the existing UI queue, and reuse that path for MAIN and child views. Ensure pending/failed items never become a second appended transcript entry.
6. **Complete width/theme/lifecycle behavior.** Test realistic viewport widths, source fallback, compact-label fidelity, resizing, scrolling away from the tail, selection/copy, tab eviction/session switches, and shutdown. Keep colors owned by Threadsmith's current theme.
7. **Complete license/release closure and docs.** Update existing legal artifact generation and verification for both NuGet packages and the embedded bundle. Verify no Node/browser/runtime download dependency in published output. Update durable user documentation and the affected acceptance/manual cases.
8. **Verify and review.** Run the focused suites, architecture suite, normal verification build, and applicable release contract checks. Perform deterministic fake-stream TUI tests and physical-terminal verification on Windows/Linux/macOS. Use independent adversarial review of source/order preservation, dependency/lifetime boundaries, and licensing; fix valid relevant findings before declaring completion. Keep reviews scoped to this feature.

Do not commit/push or start unrelated cleanup without a user request. Mark this plan implemented only after its acceptance criteria are met; distinguish automated validation from physical-terminal checks.

## 10 Testing and Verification

### Streaming and source preservation

- Feed the same non-Mermaid chunks/events with Mermaid on and off. Compare visible text, ordinary source-mode delivery boundaries, Markdown formatting, tool order, and spacing. A fake renderer that never completes must not delay ordinary output.
- Split a Mermaid opener, header, CRLF pair, closer, Unicode scalar, and surrounding text at every relevant boundary, including one-character chunks. No text loss, duplication, or fabricated blank lines.
- Two diagrams in one delta; text before/between/after; quoted/list-contained fences; four-backtick examples; inline code mentioning Mermaid; ordinary fenced languages; indented code; marker mismatch; too-short closer; trailing unclosed fence.
- Closed diagrams can begin rendering before the final model response. Non-Mermaid text following a closed diagram appears while its renderer remains pending.
- Tool start/completion, reasoning boundary, provider response change, cancellation, child flush threshold, and shutdown cannot splice source across responses or strand buffered text.

### Rendering and frontend

- Real adapter fixtures for flowchart, sequence, class, ER, and state diagrams, including branches, labels, common subgraphs, and multiline labels. Preserve node and relationship meaning, not merely a nonempty output string.
- Explicit tests for the known upstream RL, semicolon, Unicode-width, and compact ER-label cases. Unsupported syntax must be source-visible with an honest reason.
- Unicode/ASCII geometry; no ANSI/OSC escapes; labels containing control characters/markup/URLs remain data.
- Narrow and wide actual content areas; list/quote prefix width; no wrapped/cut graph rows; shrink then grow restores rendering from retained source. Do not assert a fake minimum 40-column viewport.
- MAIN plus simultaneous child diagrams, including offscreen tabs and identical diagram sources. Out-of-order worker completion updates the correct original positions.
- Selection/copy, scroll anchoring, cleared output, transcript eviction, closed agent generation, session switch, theme change, and terminal teardown while rendering is pending.
- Renderer cancellation, timeout, allocation constraint, invalid syntax, queue pressure, and engine reinitialization leave ordinary interaction operational.

### Configuration, packaging, and performance

- Default, explicit enable/disable, both character sets, all Markdown/Mermaid combinations, invalid values, hierarchy overrides, and configured budgets. Headless/original frontend do not initialize the renderer or alter source/replay.
- Count render calls: normal paints/token updates reuse results; unchanged source/width/options do not rerender; unrelated theme changes do not recompute geometry.
- Measure cold initialization, first render, warm/cache-hit render, source fallback, representative larger graphs, and concurrent tab responsiveness. Do not turn hardware-dependent millisecond results into brittle unit assertions.
- Verify generated bundle input closure excludes Mermaid's DOM parser, SVG, ELK, `entities`, Node APIs and unneeded files. Check hashes and reproducibility.
- Release tests require correct full notices, source provenance, SBOM entry and staged bundle attribution. Verify Jint/Acornima transitive metadata against the exact restored packages, not a README alone.
- Automated CI across Windows/Linux/macOS; physical checks in Windows Terminal, a Linux terminal, and macOS Terminal or iTerm2. Record unavailable manual environments honestly.

## 11 Security and Permissions

Only the shipped, hash-recorded JavaScript bundle executes. Pass Mermaid as a string argument to a predefined function; never interpolate it into JavaScript source or evaluate diagram directives. Do not enable CLR interop, dynamic module loading, filesystem, network, browser APIs, `require`, or process launch facilities. Audit the reachable renderer code for unintended dynamic evaluation.

Disable upstream color/environment detection by explicitly selecting plain output. Reapply Threadsmith's terminal-control encoding to rendered text and diagnostics; diagram URLs/click instructions must not create active links or fetch resources.

Jint constraints protect optional display responsiveness, not an OS security boundary. Propagate cancellation and contain rendering failures without changing repository admission or tool policy. Do not log complete diagram text by default.

## 12 Observability

Use the existing logger at the adapter/scheduler boundary for renderer version, outcome category, source length, effective width, elapsed time, cache hit, and cancellation/timeout information as appropriate. Aggregate or deduplicate repeated resize/fallback messages; avoid logging every frame.

Expected unsupported/too-wide/invalid outcomes show a concise message adjacent to the retained source. No TOOLS/MCP entry, model context correction, hidden retry request, or synthetic assistant message is emitted. Initialization failure is reported once while all diagrams retain readable source.

## 13 Migration and Compatibility

- Existing configurations obtain `tui.mermaid.enabled = true`; setting it false restores ordinary code/source presentation. No existing theme needs a new role.
- `tui.renderMarkdown` retains its current meaning for all non-Mermaid text; Mermaid is independently configurable.
- Preserve exact persisted/provider source and raw logs. No database, model contract, or replay migration.
- Existing original/headless operation remains source-based. The bundle may be distributed with App, but no engine is initialized on those paths.
- New renderer-only limits never reject repository loading, user input, or a model answer. They only trigger diagram-source fallback.

## 14 Acceptance Criteria

- [ ] Supported complete Mermaid fences render in place in TUIKit MAIN and subagent output through one shared path.
- [ ] The explicit layered configuration switch disables rendering and avoids initializing the adapter.
- [ ] Non-Mermaid content retains existing streaming/formatting, including source-mode cadence; rendering never blocks later text or tool activity.
- [ ] Chunk boundaries, multiple blocks, cancellation, tool boundaries and agent/session lifetimes cannot lose or duplicate text.
- [ ] Text layout fits the actual content width without corrupting connections; unsupported/invalid/too-wide cases remain readable source with a reason.
- [ ] Threadsmith's theme and terminal safety rules apply uniformly; Unicode and plain ASCII geometry are supported.
- [ ] Caching/reuse prevents work on each paint/token; background work remains cancellable and discarded when its owner is gone.
- [ ] Jint/upstream types stay within the implementation boundary; execution/providers/history remain unchanged.
- [ ] Published app works offline without Node.js/browser installation; pinned bundle and NuGet closure are covered by complete notices/provenance/release checks.
- [ ] Focused tests, architecture checks, verification build, and scoped reviews pass; cross-platform/manual results are accurately recorded.

## 15 Risks and Mitigations

| Risk | Mitigation |
|---|---|
| Upstream parses/renders a subset and may simplify unsupported syntax | Characterize real output, publish a support matrix, and visibly fall back for known unsupported or lossy constructs. Avoid claims of mermaid.js equivalence. |
| Interpreter cold start and layout latency | One background-owned engine, lazy/warm initialization, on-demand spacing variants, cache, and no UI/event-pump waits. |
| Streaming implementation changes ordinary output | Shared fence-aware integration with disabled fast path, non-Mermaid parity fixtures, and no response-wide buffering. |
| Late result replaces the wrong agent/block or resurrects evicted output | Stable scoped retained identities plus source/width/generation checks before UI-queue replacement. |
| Narrow terminal, wide glyphs, or compact spacing destroys meaning | Display-cell checks plus layout-specific fixtures; use source fallback instead of misleading graph art. |
| Embedded JavaScript is omitted from NuGet-derived legal inventory | Explicit bundled-source provenance, full notices, and release artifact tests alongside the existing package closure. |

## 16 Documentation

During implementation update `.threadsmith/config.example`, `docs/user-guide.md`, `docs/operations/tui-themes.md` where the existing Markdown role's new usage matters, and the relevant configuration/operational-limit reference. Document all defaults, hierarchy examples, independent Markdown/Mermaid switches, restart behavior, supported syntax/fallbacks, copy behavior, and absence of Node/network requirements.

Update `docs/third-party-license-inventory-status.md`, the exact package graph/evidence, applicable legal source texts, and existing release scripts/tests. Update the durable frontend architecture contract only for changed ownership/capability facts. Add affected product acceptance/manual cases using stable IDs; do not reopen completed milestones or add historical completion prose to their contracts.

The planning README receives only a navigation row. This planning task does not change application code, dependencies, user configuration, or release assets.

## 17 Decisions and Remaining Implementation Checks

**Resolved with the user:** use Jint to reuse the upstream text renderer; honor original licensing and attribution; use a clean shared presentation integration; provide configuration enable/disable; preserve non-Mermaid streaming.

**Plan defaults:** enabled for TUIKit, Unicode geometry, independent ordinary Markdown setting, existing MarkdownCode styling, source fallback rather than clipped graphs, no new command and no renderer model-context feedback.

**Implementation checks, not reasons to delay this plan:** confirm exact Jint constraint/reset behavior and measured defaults; verify the selected upstream patch set and supported syntax; finalize minimal retained-block metadata after exercising streaming fixtures; complete the existing release-license review/evidence process. If a requirement would force a broad Markdown rewrite or substantial renderer fork, surface that specific scope change before proceeding instead of introducing it silently.
