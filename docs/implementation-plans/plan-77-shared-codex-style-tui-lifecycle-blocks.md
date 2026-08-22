# Implementation Plan 77: Shared Codex-Style TUI Lifecycle Blocks

**Status:** Complete

**Delivery track:** M23.3 - Codex-Style TUI Presentation
**Strategy source:** User-requested interactive output consistency refinement; Plans 24-26, 37, 49, 63, 73, and 75
**Prerequisite plans:** plans 24-26, 37, 49, 63, 73, and 75

## 1. Objective

Extend the Codex-style interactive presentation work from completed-tool and mutation-diff output into a reusable terminal-neutral block formatter used by TOOLS, SEMANTIC CHECKS, PLAN proposal, and PLAN auto-approval lifecycle output.

The visible outcome is one consistent family of expandable, bracketed blocks with shared handling for headers, muted body text, ordered items, elapsed duration, outcome text, semantic roles, and exactly one blank line between major visible lifecycle events.

Example plan proposal shape:

```text
 PLAN: revision 1
 │ Revert the Name property override in SectorEntityStandardizer.cs from the string literal "Test" back to the original StandardizerName
 │ expression, undoing the previously approved change.
 │
 │ Steps:
 └ 1. Revert Name property to StandardizerName - The Name property will once again return the StandardizerName value, matching the pattern used before the test change.
```

This plan is presentation-only. It must not change plan approval authority, mutation approval authority, semantic validation behavior, durable event payloads, raw Markdown authority, canonical diffs, headless machine output, or provider/tool execution.

## 2. Architectural Context

- Plans 24-26 own TUI semantic text roles, theme resolution, terminal-native output, and composer-adjacent presentation constraints.
- Plan 37 owns approved-plan execution, separate plan and mutation approval boundaries, mutation preparation, validation, correction, and durable resume.
- Plan 49 owns host-measured operation durations and transient activity lifecycle.
- Plan 63 owns ordered visible-event flushing, semantic Markdown answer rendering, terminal-safe fallback, and raw Markdown/durable transcript authority.
- Plan 73 owns the first Codex-style completed-tool block and mutation-diff presentation refinements.
- Plan 75 owns plan approval policy and plan auto-approval provenance; this plan changes only how those events are displayed interactively.

## 3. Scope

- Introduce one shared TUI block formatter that can render:
  - a header with label, primary title, closed outcome/status text, optional revision/risk/policy metadata, and optional elapsed duration;
  - wrapped body lines with a vertical guide prefix;
  - intentional blank body separators as guide-only lines;
  - ordered or unordered child rows with deterministic `├`/`└` connectors;
  - per-section `TuiTextRole` values supplied by the caller.
- Convert completed TOOLS output to the shared formatter while preserving the visible grammar from Plan 73 unless intentionally refined by this plan's acceptance criteria.
- Convert completed SEMANTIC CHECKS output to the same shared formatter, including closed outcome text and optional elapsed duration.
- Convert PLAN proposal output to the shared formatter:
  - header `PLAN: revision <n>`;
  - summary/body text displayed as muted guided lines;
  - explicit `Steps:` label;
  - each implementation step displayed as an ordered row combining title and expected outcome;
  - no redundant approval-boundary sentence such as `Host approval decision pending; mutation approval and validation remain separate.`
- Convert PLAN auto-approval output to the shared formatter with concise provenance, including revision, risk, policy, closed approval status, and reason.
- Ensure major visible lifecycle events are separated by exactly one presentation-owned blank line, including transitions between tools, plan blocks, semantic checks, mutation preview/preparation, validation, and mutation results.
- Clarify semantic-check display text where a check occurs after preview but before mutation application by preserving the phase and/or concise purpose, so baseline checks are understandable as pre-apply baseline capture rather than post-apply validation.
- Preserve host-owned event ordering and never infer routing or authority from presentation text.

## 4. Non-Scope

- No changes to model prompts, model schemas, tool schemas, tool authorization, mutation staging, mutation approval, plan approval, policy evaluation, validation stages, correction loops, or execution checkpoints.
- No changes to durable domain event payloads, persisted transcripts, artifact content, raw unified diffs, or headless machine-readable output.
- No terminal full-screen layout, mouse handling, composer rewrite, or Markdown rendering expansion.
- No provider-specific Codex dependency.
- No semantic-validation lifecycle reorder; baseline checks may still occur after preview and before mutation apply when the host is capturing immutable pre-mutation evidence.

## 5. Current State

- `TuiPresentationFormatter` centralizes completed TOOLS and SEMANTIC CHECKS two-line text formatting, but the shapes are specialized rather than a shared block primitive.
- PLAN proposal, PLAN auto-approval, mutation preview preparation, and other host status output are assembled as `Threadsmith:` system responses in the conversation transcript.
- PLAN proposal currently appends redundant explanatory approval-boundary text that is not useful in routine interactive output.
- Event-boundary spacing is distributed through transcript append helpers and can produce more than one blank line between adjacent major host events.
- `TuiEventSegments` assigns roles after text rendering; PLAN blocks currently fall through to general status styling rather than caller-specified section roles.

## 6. Proposed Design

### 6.1 Shared block model

Add a small TUI-owned presentation model, names illustrative:

```csharp
internal sealed record TuiBlockPresentation(
    TuiBlockHeader Header,
    IReadOnlyList<TuiBlockSection> Sections);

internal sealed record TuiBlockHeader(
    string Label,
    string Title,
    string? Outcome,
    string? ElapsedText,
    TuiTextRole Role);
```

The final implementation may use focused methods instead of records if that is simpler, but it must keep the reusable block logic in one place and keep terminal-library types out of the model.

The formatter returns terminal-neutral `TuiTextSegment` values or an equivalent role-aware structure, not only raw strings, so call sites can supply semantic roles for header, body, metadata, details, success, warning, failure, and muted text.

### 6.2 Header, outcome, and elapsed handling

The shared formatter owns the common header grammar and closed suffix ordering:

```text
 <LABEL>: <title> - <outcome> · <elapsed>
```

Rules:

- Outcome text is supplied from closed host-owned enums or explicit caller-owned values; callers must not pass raw exception text as an outcome.
- Elapsed text is produced through `OperationDurationFormatter` before or inside the shared formatter using the same Plan 49 semantics.
- When durations are disabled or unavailable, the elapsed suffix is omitted without changing the rest of the block shape.
- Outcome role is caller-selectable; successful tools and completed/skipped semantic checks use success styling, failed/cancelled tools and checks use failure/warning/status according to existing role policy.

### 6.3 Guided body and item layout

The shared formatter owns line wrapping and guide prefixes:

```text
 │ body line
 │ wrapped continuation
 │
 ├ item one
 └ item two
```

Rules:

- Multi-line caller text is split and each visible line receives the guide prefix.
- Empty body separators render as a guide-only line (`│`).
- Item rows use `└` for the final item and `├` for preceding items.
- Text remains bounded and terminal-control-neutralized before rendering.
- Wrapping should be deterministic and terminal-neutral; if width-aware wrapping is not practical in this work item, preserve explicit line breaks and keep width-aware wrapping as an open decision rather than adding ad hoc reflow.

### 6.4 PLAN proposal block

Replace the current plain system response for structured `PlanProposed` events with the shared block formatter.

Expected shape:

```text
 PLAN: revision 1
 │ <plan summary>
 │
 │ Steps:
 └ 1. <step title> - <expected outcome>
```

Presentation details:

- Header role: status or dedicated plan role if one already exists.
- Summary/body role: muted.
- `Steps:` role: muted or status, depending on existing theme precedent.
- Step rows: muted by default.
- Remove redundant approval-boundary suffix text entirely.
- Preserve review prompt routing through typed `ApprovalRequested` plan events only.

### 6.5 PLAN auto-approval block

Replace the current plain auto-approval system response with the shared block formatter.

Expected shape, illustrative:

```text
 PLAN: auto-approved
 │ Revision: 1
 │ Risk: High
 │ Risk basis: model declared 1 risk; 1 file affected
 │ Policy: AutoApproveAllValid
 └ Reason: Policy AutoApproveAllValid approved a High risk plan after sanity checks.
```

Auto-approval remains plan approval only and must not imply mutation approval. When prior TUI context can explain the classification, the block includes a concise bounded risk basis.

### 6.6 MUTATION lifecycle blocks

Mutation proposal preparation, mutation proposal repair, applied mutation notices, and non-semantic post-apply validation starts use the same shared lifecycle formatter instead of plain `Threadsmith:` prose. Attempt and reason/path/detail rows remain muted. Policy-auto-applied mutations identify the approval-policy provenance in the header, and the separate `Validating applied mutation...` status line is omitted when a following semantic-check block conveys validation activity. If the configured validation stages exclude semantic validation, a `MUTATION: Validating applied mutation` block preserves feedback during compile/diagnostics/test waits.

Expected shape, illustrative:

```text
 MUTATION: Preparing preview
 └ Attempt: 1/2

 MUTATION: Retrying proposal with correction evidence
 │ Attempt: 2/2
 └ Reason: ReplaceText expectedText was not found in 'src/File.cs'.

 MUTATION: Applied under the active approval policy
 │ Mutation applied: src/File.cs
 └ The expression uses the approved value.

 MUTATION: Validating applied mutation
 └ Stages: compile, diagnostics, tests
```

### 6.7 TOOLS and SEMANTIC CHECKS conversion

Rebuild existing `FormatToolCompletion` and `FormatSemanticCheckCompletion` on top of the shared formatter rather than hand-assembling their line shapes.

TOOLS and SEMANTIC CHECKS use the same unbulleted, one-character-indented lifecycle block grammar:

```text
 TOOLS: <tool-name> - <completed|failed|cancelled|timed out> · <elapsed>
   └ <detail>
```

The unbulleted grammar is pinned in tests so TOOLS, SEMANTIC CHECKS, PLAN proposal, and PLAN auto-approval blocks align consistently.

Semantic-check labels/details should distinguish phases where helpful, especially:

- pre-mutation overlay syntax/compilation checks before preview;
- semantic baseline diagnostics after preview/approval but before mutation apply;
- semantic post-mutation diagnostics after mutation apply.

### 6.8 Event-boundary spacing

Centralize major-event spacing so adjacent visible event blocks produce exactly one blank line between them and never accumulate multiple blank lines because both the previous event and next event append trailing/leading separators.

Rules:

- The first visible non-model event after submitted input still starts after one presentation-owned blank line.
- Model answer blocks keep Plan 63 answer-block spacing.
- Adjacent host lifecycle blocks render with exactly one blank line between major events.
- Run completion should not add redundant blank lines beyond the established event boundary.

## 7. Public Contracts

- No new durable public execution contracts.
- No new Core contracts unless an existing host-owned presentation DTO is insufficient; prefer TUI-internal types.
- New or changed semantic TUI roles are allowed only if needed for caller-supplied block sections; they must remain terminal-neutral and configurable through the existing theme system if exposed.
- Terminal-library, Roslyn, model-provider, MCP SDK, and extension implementation types must not leak outside owning projects.

## 8. Project/File Changes

Expected implementation areas:

- `src/Threadsmith.Tui/TuiPresentationFormatter.cs`
  - shared role-aware block formatter;
  - conversion of TOOLS and SEMANTIC CHECKS formatting;
  - plan proposal and plan auto-approval helpers if useful.
- `src/Threadsmith.Tui/TuiShell.cs`
  - transcript application for PLAN, auto-approval, semantic checks, tools, mutation status, and exact event-boundary spacing.
- `src/Threadsmith.Tui/TuiEventSegments.cs`
  - role-aware segment routing for shared blocks, especially muted plan body/steps and outcome-specific statuses.
- `tests/Threadsmith.CoreRuntime.Tests/`
  - formatter, transcript, event-segment role, spacing, semantic-check phase, and visible-output regression coverage.
- `docs/user-guide.md` and `docs/implementation-plans/manual-test-plan.md` if the implemented visible interaction contract changes user/operator procedures.
- `src/Threadsmith.Tui/AGENTS.md` if the durable TUI rendering contract changes.

## 9. Ordered Tasks

1. Inventory every live transcript path for TOOLS, SEMANTIC CHECKS, PLAN proposal, PLAN auto-approval, mutation preview/preparation, validation output, mutation applied, rollback, and run completion.
2. Add focused tests that capture current unwanted behavior: redundant plan approval-boundary text, specialized duplicated tool/semantic formatting, plan text without bracket guides, and extra blank lines between adjacent major events.
3. Design a minimal shared formatter that produces terminal-neutral segments with caller-supplied roles and common outcome/elapsed handling.
4. Convert completed TOOLS formatting to call the shared formatter without regressing outcome text, duration display, source display, detail fallback, or sanitization.
5. Convert completed SEMANTIC CHECKS formatting to call the shared formatter without regressing outcome mapping, duration display, detail fallback, or concurrent-check correlation.
6. Add PLAN proposal formatting through the shared formatter, including muted summary/steps, multiline expansion, and removal of redundant approval-boundary text.
7. Add PLAN auto-approval formatting through the shared formatter with stable revision/risk/policy/reason projection.
8. Centralize event-boundary spacing so major visible blocks are separated by exactly one blank line.
9. Clarify semantic-check baseline/pre/post labels or details where needed without changing validation order.
10. Update segment-role routing so shared blocks preserve the intended roles in interactive output and style suppression still preserves text.
11. Update user-facing docs/manual tests/DOX only for durable visible behavior or workflow changes.
12. Run focused TUI tests and affected plan/validation tests.

## 10. Testing

Automated coverage:

- Shared formatter tests for header-only, body, multiline body, blank body separator, one item, multiple items, outcome, elapsed enabled/disabled, and per-section roles.
- TOOLS regression tests for success, failure, cancelled, timed out, MCP/source identity, no-duration mode, detail fallback, and bounded terminal-control neutralization.
- SEMANTIC CHECKS regression tests for completed, failed, degraded, skipped, cancelled, unknown, no-duration mode, phase-aware text, and concurrent correlation.
- PLAN proposal tests for exact block text, multiline summary, multiple steps, no redundant approval-boundary sentence, muted body/step roles, and typed approval routing unchanged.
- PLAN auto-approval tests for exact block text, revision/risk/policy/reason content, role assignment, and no implication of mutation approval.
- Spacing tests proving exactly one blank line between tools, plan blocks, semantic checks, mutation preview/preparation, mutation applied, validation/test output, and run completion.
- Existing Markdown answer-block tests proving Plan 63 answer spacing is unchanged.

Manual coverage before completion:

- Real-terminal flow with tool use followed by plan proposal shows one blank line and the guided PLAN block.
- Auto-approved plan displays a consistent PLAN auto-approved block before mutation preparation.
- Semantic baseline diagnostics after mutation preview but before mutation apply clearly read as pre-apply baseline capture.
- Tool, semantic-check, plan, and mutation-preview blocks retain native selection/copy behavior and do not corrupt the composer.

## 11. Security/Permissions

- Shared block text is display data and must be bounded and terminal-control-neutralized before rendering.
- Do not print raw tool arguments, raw MCP payloads, raw extension data, provider request bodies, environment values, secrets, exception stack traces, or raw untrusted terminal controls.
- Do not let presentation text determine approval routing, policy decisions, validation behavior, or mutation authority.
- Preserve exact plan approval, mutation approval, and build/test validation separation.

## 12. Observability

- Preserve existing domain events and Plan 49 duration measurements.
- Do not add telemetry that records new raw plan text, raw tool arguments, semantic diagnostic source contents, or secrets.
- If formatter fallback paths are observable for debugging, expose only bounded category names or test-only assertions.

## 13. Migration/Compatibility

- Existing durable sessions, event streams, transcripts, execution records, and artifacts remain valid.
- Headless machine-readable output remains unchanged unless a surface explicitly reuses the interactive formatter.
- Existing themes continue to work; any new roles must have safe defaults and unknown-role fallback behavior.
- Tests that asserted the old plan text or spacing are intentionally updated to the new interactive presentation contract.

## 14. Acceptance Criteria

- TOOLS, SEMANTIC CHECKS, PLAN proposal, and PLAN auto-approval interactive output all use one shared formatter path for block layout, outcome/status text, elapsed suffixes, and section roles.
- The plan proposal block renders as `PLAN: revision <n>` with guided muted summary/body text, a `Steps:` section, and connected ordered step rows.
- The plan auto-approval block uses the same block family and displays revision, risk, available risk basis, policy, and reason concisely.
- Mutation proposal preparation, mutation proposal repair, applied mutation notices, and non-semantic validation starts use the same block family and omit redundant validation-start prose before semantic-check blocks.
- The sentence `Host approval decision pending; mutation approval and validation remain separate.` no longer appears in routine interactive plan proposal output.
- Adjacent major visible lifecycle events are separated by exactly one blank line.
- Semantic baseline checks that occur after preview but before mutation apply are visibly understandable as pre-apply baseline capture.
- Existing TOOLS and SEMANTIC CHECKS outcome/duration behavior is preserved or intentionally refined by updated tests.
- Caller-supplied `TuiTextRole` values are honored for headers, muted body/details, and outcome-specific text.
- Durable records, canonical diffs, approval authority, validation behavior, and headless machine outputs remain unchanged.

## 15. Risks

- Centralizing formatter behavior can accidentally alter existing TOOLS/SEMANTIC CHECKS text relied on by tests or users.
- Fixing event spacing globally may affect Markdown answer-block cadence or run-completion output if not tested carefully.
- Role-aware block output may require changes to transcript-delta segmentation; doing this incorrectly could flatten intended styling.
- Over-clarifying semantic-check labels could imply a lifecycle reorder unless wording is precise.

## 16. Documentation

- Update `docs/user-guide.md` if the implemented block grammar is user-facing enough to document.
- Update `docs/implementation-plans/manual-test-plan.md` with real-terminal checks for plan blocks, auto-approved plans, semantic baseline placement, shared tool/semantic blocks, and blank-line spacing.
- Update `src/Threadsmith.Tui/AGENTS.md` if the shared block formatter becomes part of the durable TUI rendering contract.
- No acceptance-scenario update is required unless implementation changes product-level acceptance behavior rather than presentation details.

## 17. Open Decisions

- Whether width-aware wrapping belongs in this work item or should be deferred in favor of preserving explicit line breaks.
- Final PLAN auto-approval grammar: compact metadata in the header versus guided metadata lines.
- Whether mutation preview preparation/status should also move immediately to the shared block formatter or remain a `Threadsmith:` host status until a broader status-output pass.
