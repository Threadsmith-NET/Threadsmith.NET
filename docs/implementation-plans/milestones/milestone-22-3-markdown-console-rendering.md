# Milestone 22.3 — Semantic Markdown Console Rendering  *(plan 63)*

**Status:** See the [authoritative milestone index](../milestones.md).

**Objective:** Make complete-block Markdown the default interactive presentation for ordinary model output through a host-owned semantic document model, without binding transcript state, themes, tests, or event ordering to a particular composer, terminal backend, or third-party Markdown widget.

**Deliverables:**
- Direct centrally pinned Markdig parsing isolated inside `Threadsmith.Tui`; no MDView or end-to-end Markdown widget.
- A closed, bounded, immutable `TuiMarkdownDocument` block/inline model plus an explicit CommonMark/table/task/strikethrough syntax profile with HTML and active content disabled.
- Default-on `tui:renderMarkdown`; `false` restores existing source-mode chunk cadence for safe text while visibly escaping terminal controls.
- Ordered complete-block projection: each contiguous non-reasoning answer block renders once before every ordered event that changes visible projection state and before every declared lifecycle boundary, while raw model text still appends immediately to terminal-neutral transcript state; timer-driven in-place refreshes of the already active activity row are non-boundary redraws.
- A terminal-neutral semantic-document operation on `IConsoleSurface`; the current Spectre adapter renders it under the existing serialized gate using semantic theme roles and terminal-native defaults.
- Deterministic width-aware headings, lists, quotes, tables with narrow-width degradation, inline/fenced code, links, emphasis, tasks, strikethrough, and style-free parity under `NO_COLOR`.
- Exact raw Markdown retained for persistence, context, restoration, redirected output, and headless output; every interactive source/failure projection uses validated terminal-safe text, and no AST, layout, ANSI, Markdig, PrettyPrompt, or Spectre types cross the TUI boundary.
- Opaque preservation of Plan 49 request/tool/MCP elapsed displays, bounded refresh, source/outcome/final-duration markers, sanitized activity details (including reviewed paths/line ranges/commands), configuration behavior, legacy omission, and single-row MCP projection.
- Opaque preservation of the complete Plan 26 composer-adjacent session-status field set plus its width, priority/omission, semantic-role, styling-suppression, and ordering behavior after final Markdown output and before composer return.
- Scenario AC plus focused parser/model/layout/order/security/fallback/theme/cancellation/architecture tests, preserved Plan 26/49 regression suites with an additive rendered/source branch for the sole final-visible-answer lifecycle amendment, and maintained real-terminal verification.

**Exit criteria:**
- With no setting, a complete Markdown-rich answer renders once before composer return; there is no raw/rendered duplication or scrollback rewrite.
- `tui:renderMarkdown=false` preserves current visible model-chunk cadence for safe text and deterministically escapes ANSI/OSC/C0/C1 controls.
- An `answer A` → host status/diagnostic → tool → `answer B` run flushes A before each triggering visible boundary and preserves exact order; reasoning and tool/host output never enter Markdown parsing.
- Raw `ModelOutputObserved` text remains immediately appended and authoritative for durable/context/headless projections.
- The closed syntax profile, bounds, inert HTML handling, validated link policy, and shared terminal-safe text encoder prevent active content or raw controls from reaching the terminal backend in rendered, source, oversize, or failure modes.
- All Markdown presentation uses semantic roles; styled and `NO_COLOR` output retain identical words, markers, structure, and line layout.
- All document/segment/activity/prompt writes share the existing console gate, and neither the shell nor parser writes through Spectre directly.
- Markdown transforms only accepted model-answer presentation. Repeated periodic activity refreshes never fragment an accumulating block; Plan 49 timing/detail/source/outcome behavior and Plan 26 session status retain their current content, roles, configuration, ordering, and focused regression coverage.
- Syntax highlighting is not required; fenced code remains exact, structured, selectable, and terminal-native.
- Focused automated/architecture/package coverage, preserved Plan 26/49 assertions plus additive rendered/source final-answer-stop coverage, Scenario AC, maintained real-terminal checks, documentation, status, and DOX are current.

**Prerequisites:** plans 03, 24–26, and 49.

**Scope decisions:**
- Intentionally replace default visible token streaming with complete contiguous answer-block rendering; retain immediate exact raw transcript appends and terminal-safe streamed source mode.
- Do not rewrite native scrollback, duplicate answers, cross tool boundaries, enable alternate-screen/mouse ownership, or couple Markdown to composer input.
- Use Markdig only as a bounded AST parser; Threadsmith owns semantic nodes, safe syntax, styles, layout, fallback, and tests.
- Keep Spectre.Console as an optional initial adapter detail and PrettyPrompt as the current composer detail; replacing either is outside this milestone.
- Default Markdown on with semantic theme roles; do not use literal Markdown palettes.
- Defer syntax highlighting and keep reasoning, tools/MCP, host output, diffs, status, restored history, and headless output on existing paths; treat their host-owned semantic projections as opaque and never parse/reconstruct them through Markdown.
- Keep Plan 49 authoritative for elapsed timing, activity detail/source/outcome markers, configuration, refresh, restoration, and MCP deduplication, and Plan 26 authoritative for the composer-adjacent status row. The sole Plan 49 lifecycle amendment stops final-answer `THINKING` immediately before the buffered document becomes visible rather than on its first raw delta; original timing metadata and final durations remain unchanged.
- Flush before every ordered event that starts, stops, replaces, or appends visible output/activity/status/prompt state and before every declared lifecycle boundary, not only tool/run events; unknown non-model events close conservatively before projection. Periodic timer callbacks that redraw only the already active activity row remain outside event classification and do not flush.
- Keep M22.3 independent of M23; Plan 59 continues to depend on Plan 62, not Plan 63.

---

[Back to the milestone index](../milestones.md) · [Dependency DAG](dependency-dag.md)
