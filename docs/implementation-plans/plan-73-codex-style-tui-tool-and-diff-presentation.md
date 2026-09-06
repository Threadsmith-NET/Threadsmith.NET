# Implementation Plan 73: Codex-Style TUI Tool and Diff Presentation

**Milestone:** M23.3 - Codex-Style TUI Presentation
**Strategy source:** User-requested interactive presentation refinement; Plans 24-26, 37, 49, 57, and 63
**Prerequisite plans:** plans 24-26, 37, 49, 57, and 63

## 1. Objective

Make interactive TUI operation output more compact and Codex-like without changing host authority, durable execution records, raw Markdown authority, tool availability, mutation approval, or exact diff validation. Every completed tool invocation shown in the TUI is rendered as a stable two-line presentation block:

```text
• TOOLS: <tool-name> - <completed|failed|cancelled> · <elapsed>
  └ <bounded sanitized tool detail>
```

Also adjust TUI mutation diff rendering so hunk headers are followed by one presentation-owned blank line before displayed code, and expose neutral/context code text through a configurable semantic TUI role while preserving the existing added/removed line role behavior.

## 2. Architectural Context

- Plan 49 owns host-measured request/tool/MCP durations, sanitized operation detail, default-visible duration projection, and transient activity lifecycle.
- Plan 57 owns parallel sibling tool execution and canonical original-order continuations; presentation must not imply a different execution order than the ordered host projection.
- Plan 63 owns semantic Markdown rendering, visible-event boundary flushing, terminal-safe fallback, semantic roles, and raw Markdown as terminal-neutral authority.
- Plan 37 owns governed mutation approval, exact diffs, transactional application, and authoritative cumulative final diff.
- Plans 24-26 own theme roles, terminal-native output, and native-scrollback-safe presentation.

## 3. Scope

- Add one centralized TUI presentation path for completed tool invocations, covering built-in native tools, MCP-imported tools, extension capabilities exposed as tools, and future ordinary tool adapters that emit the host-owned operation completion projection.
- Render every completed tool block with:
  - bullet prefix `•`;
  - uppercase source label `TOOLS:`;
  - host-owned display name;
  - closed outcome text: `completed`, `failed`, or `cancelled`;
  - formatted elapsed duration using the existing operation-duration formatter;
  - a second indented detail line prefixed by `└`.
- Reuse and harden the existing bounded sanitized activity-detail source so the detail line describes what the tool did without exposing secrets, raw provider/MCP arguments, exception internals, or untrusted terminal controls.
- Add fallback detail text for tools with no specific summary, such as `no additional detail`, while preferring concise specific summaries for file reads, symbol searches, searches, shell/process commands, MCP tools, and mutation-related tool calls when safe.
- Update TUI diff presentation for all interactive mutation-diff surfaces so each hunk header line is followed by exactly one blank presentation line before the displayed hunk body.
- Add or confirm a semantic role for neutral/context code lines in displayed diffs; if a suitable role already exists, wire mutation-diff code text to it, otherwise add a new role and theme configuration surface.
- Preserve current added-line and removed-line role behavior.

## 4. Non-Scope

- No changes to model prompts, tool schemas, tool authorization, tool execution behavior, mutation authority, policy, approval, or validation.
- No changes to durable execution records, canonical tool continuations, or raw unified diff payloads used for validation and replay.
- No terminal full-screen widget work, mouse-capture changes, or composer/layout rewrite.
- No syntax highlighting beyond exposing the neutral code text role requested here.
- No provider-specific formatting or Codex runtime dependency.

## 5. Current State

- Tool and MCP activity durations are already host-measured and projected by the TUI, but completed operation lines do not yet use one uniform Codex-style block across every tool invocation.
- Some tools already provide bounded useful activity context, but the presentation contract is not a reusable two-line completed-tool shape.
- Mutation diffs display ordinary unified diff hunks without the requested presentation-owned blank line after `@@ ... @@` hunk headers.
- Added and removed diff text already have distinct role behavior; neutral/context code text may not have a dedicated user-settable role on every mutation-diff surface.

## 6. Proposed Design

### 6.1 Completed-tool projection

Introduce a terminal-neutral completed-tool presentation model in the TUI boundary, conceptually:

```csharp
internal sealed record TuiCompletedToolPresentation(
    string ToolName,
    TuiToolOutcome Outcome,
    string? ElapsedText,
    string DetailText);
```

The model is built only from host-owned operation completion data and bounded sanitized detail strings. It is a presentation projection, not durable state.

Outcome mapping is closed:

| Host outcome | TUI text |
|---|---|
| Success | `completed` |
| Failure | `failed` |
| Cancelled | `cancelled` |

Elapsed text uses the existing `OperationDurationFormatter` behavior. When duration display is disabled by effective configuration, the header omits only the elapsed suffix while preserving the same block shape:

```text
• TOOLS: <tool-name> - <completed|failed|cancelled>
  └ <detail>
```

### 6.2 Detail-line summarization

Create one detail-summary formatter that consumes the already-sanitized metadata/detail available at the tool boundary and returns a single bounded plain-text line. It must:

- neutralize terminal controls;
- cap length deterministically;
- never print raw JSON argument payloads by default;
- never print secrets, bearer tokens, OAuth artifacts, environment-variable values, repository secret-store values, or exception stack traces;
- preserve useful safe context for common operations.

Expected examples:

```text
• TOOLS: find_symbol - completed · 688ms
  └ symbol: SomeClassName

• TOOLS: read_file - completed · 3ms
  └ lines 1-200, some_file_path/someFile.cs

• TOOLS: git_diff - failed · 42ms
  └ repository diff query failed; see sanitized error above
```

Specific summaries should be implemented through host-owned typed metadata where available, not brittle parsing of rendered strings. Unknown tools use the safe fallback.

### 6.3 Ordering and parallelism

Completed-tool blocks are emitted at the same lifecycle boundary currently used for completed operation projection. For parallel tools, the visible order remains the host-owned canonical continuation/order established by Plan 57, even if actual completion times differ. Live transient activity rows may continue to refresh in place; completed blocks are ordinary visible boundary events.

### 6.4 Diff hunk spacing

Add a TUI diff presentation formatter used by every interactive mutation-diff display surface. It preserves raw diff text as authoritative input, then renders hunk headers with one blank presentation line before code lines:

```diff
@@ -1,41 +1,41 @@

 using Example;
+public override string Name => "Test";
-public override string Name => SomeNameName;
```

Rules:

- Insert the blank line only in TUI presentation, not in canonical diff payloads, mutation baselines, JSON outputs, validation input, or persisted evidence.
- Insert exactly one blank presentation line after each hunk header, including multi-hunk diffs.
- Do not add extra blank lines after file headers (`---`, `+++`) or between ordinary context lines.
- Preserve no-newline markers, binary-file markers, rename/copy headers, mode changes, and non-hunk metadata without synthetic spacing unless already present.

### 6.5 Diff semantic roles

Route displayed diff lines through semantic roles:

| Diff line kind | Role behavior |
|---|---|
| Added lines beginning `+` but not `+++` | existing added-line role unchanged |
| Removed lines beginning `-` but not `---` | existing removed-line role unchanged |
| Hunk header `@@ ... @@` | existing header/metadata role if present, otherwise current behavior |
| File headers and metadata | existing metadata role behavior |
| Neutral/context code lines and blank presentation lines | configurable neutral code role |

If no neutral code role exists, add a role such as `DiffContextCode` / configuration key equivalent consistent with the existing TUI theme role naming. Default styling should preserve current visual output except where the user configures the new role.

## 7. Public Contracts

- Add no new durable public execution contracts.
- If the theme role taxonomy is public or user-configurable, add one documented semantic role for neutral/context diff code text.
- Do not expose terminal-library types outside `Threadsmith.Tui`.
- Do not add provider SDK, MCP SDK, Roslyn, or extension implementation types to TUI contracts.

## 8. Project/File Changes

Expected implementation areas:

- `src/Threadsmith.Tui/`
  - completed tool block formatter and renderer;
  - activity/event segment routing;
  - diff presentation formatter;
  - semantic role addition or role wiring;
  - terminal-control neutralization and bounded detail handling.
- `src/Threadsmith.Core/`, `src/Threadsmith.Tools/`, `src/Threadsmith.Mcp/`, `src/Threadsmith.Extensions.Runtime/` only if existing operation-completion metadata is insufficient for safe typed detail summaries.
- `tests/Threadsmith.Tui.Tests/` or the nearest existing TUI test project for formatter, role, ordering, and golden-output tests.
- `docs/user-guide.md` for the visible TUI format and role configuration if implementation exposes a user-facing role.
- `docs/implementation-plans/manual-test-plan.md` for interactive real-terminal checks before plan completion.

## 9. Ordered Tasks

1. Inventory all current TUI completed operation/tool/MCP render paths and mutation-diff display surfaces.
2. Identify the existing host-owned operation completion DTO/event and bounded detail metadata used by Plan 49.
3. Add a centralized completed-tool presentation formatter with closed outcome mapping and elapsed formatting.
4. Route every ordinary tool completion surface through the new formatter, including MCP-imported and extension-backed tool projections where they already appear as tools.
5. Add typed safe detail summaries for high-value built-in tools and a deterministic fallback for unknown tools.
6. Add unit tests proving success, failure, cancellation, no-duration mode, missing-detail fallback, secret/control-character neutralization, and representative built-in summaries.
7. Inventory mutation-diff display call sites used for plan review, mutation approval, cumulative final diff, rollback/reconciliation, and any interactive diff command output that represents code mutations.
8. Add one TUI diff presentation formatter that inserts one blank presentation line after each hunk header.
9. Wire neutral/context code lines to the existing appropriate role or add a new semantic role and configuration mapping without changing added/removed defaults.
10. Add golden tests for single-hunk, multi-hunk, metadata-only, no-newline, added/removed/context, and already-blank hunk cases.
11. Update user-facing docs and the manual test plan.
12. Run focused TUI tests, affected project tests, and any architecture tests impacted by new role/contracts.

## 10. Testing

Automated coverage:

- Completed-tool formatter tests for exact header grammar, duration enabled/disabled, all outcomes, detail fallback, and bounded sanitization.
- Integration-style TUI projection tests proving every tool completion event uses the `TOOLS:` block, not legacy ad hoc output.
- Parallel-tool ordering test proving rendered completed blocks follow host canonical order.
- Diff formatter tests proving exactly one blank presentation line after each hunk header and no mutation of raw canonical diff text.
- Theme/role tests proving neutral diff code can be styled independently while added/removed line roles retain current behavior.
- Regression tests for terminal-control neutralization in tool detail and diff source/failure fallback.

Manual coverage before completion:

- Real-terminal read-file, symbol/code-search, shell/process, MCP or mocked imported tool, and failing tool examples show the two-line block.
- A governed mutation preview and final cumulative diff show the blank line after `@@ ... @@` and preserve added/removed styling.
- Native selection/copy and bulk paste remain unaffected.

## 11. Security/Permissions

- Tool detail text is untrusted display data even when derived from host metadata; sanitize and bound it before rendering.
- Do not print raw arguments, raw MCP payloads, raw extension data, environment values, secrets, exception stack traces, or provider request bodies.
- Keep exact mutation approval and diff validation based on canonical raw diff data, not presentation-spaced text.
- Preserve Plan 30/37 mutation approval and hard guardrails.

## 12. Observability

- Preserve Plan 49 duration semantics and monotonic timing source.
- Do not add telemetry containing raw tool arguments or diff contents beyond existing approved events.
- If formatter fallback is used for a known built-in tool that should have a specific summary, expose only a sanitized debug/test signal, not user secrets.

## 13. Migration/Compatibility

- Existing themes continue to work; the new neutral diff code role defaults to current neutral text styling unless explicitly configured.
- Existing durable transcripts and execution records remain valid because canonical records are unchanged.
- Headless JSON and machine-readable outputs are unchanged unless they already consume the TUI renderer explicitly.
- The visible output format changes intentionally for interactive completed tool events.

## 14. Acceptance Criteria

- Every completed interactive tool invocation is displayed as:
  - `• TOOLS: <name> - <completed|failed|cancelled> · <elapsed>` when durations are enabled;
  - the same header without elapsed when durations are disabled;
  - followed by `  └ <detail>`.
- No completed tool path still uses a legacy one-line/ad hoc completed-operation format in the interactive TUI.
- Details are concise, useful, bounded, and sanitized for representative built-in, MCP-imported, extension-backed, failed, cancelled, and unknown tools.
- Interactive mutation diffs display exactly one blank presentation line after every hunk header.
- Added and removed diff line roles remain unchanged.
- Neutral/context diff code text is configurable through a semantic TUI role.
- Raw canonical diffs, mutation validation, durable state, and headless machine outputs remain unchanged.
- Focused automated tests and manual-test-plan updates are complete.

## 15. Risks

- Multiple existing TUI paths may render tool completions independently; missing one would produce inconsistent output.
- Tool detail summarization can accidentally disclose unsafe data if it falls back to raw arguments.
- Presentation-only hunk spacing must not contaminate canonical diffs used for approval, validation, persistence, or copyable machine output.
- Adding a theme role can break configuration compatibility if defaults and unknown-role handling are not preserved.

## 16. Documentation

- Update `docs/user-guide.md` with the completed-tool block grammar and representative detail examples.
- Update theme documentation/user-guide role tables if a new neutral diff code role is added.
- Update `docs/implementation-plans/manual-test-plan.md` with real-terminal checks for tool blocks and mutation diff spacing.
- DOX pass: update nearest relevant `AGENTS.md` files only if implementation changes durable structure, responsibilities, contracts, workflow, or child indices.

## 17. Open Decisions

- Final semantic role name for neutral/context diff code text if no existing role is suitable.
- Whether failed tool detail should include a generic sanitized failure reason from the host-owned operation outcome or always point to the existing sanitized error projection.
- Whether direct non-mutation `git_diff` tool output should use the same hunk-spacing renderer or remain a raw tool-result payload; mutation-diff surfaces must use the new spacing either way.
