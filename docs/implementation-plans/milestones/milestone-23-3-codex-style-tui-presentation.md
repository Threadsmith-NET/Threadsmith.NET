## Milestone 23.3 - Codex-Style TUI Presentation *(plan 73)*

**Status:** See the [authoritative milestone index](../milestones.md).

**Objective:** Make interactive TUI operation, plan, semantic-check, and mutation-diff output easier to scan by adopting reusable Codex-style presentation blocks and adding presentation-only compact hunk context, hunk spacing, and configurable neutral diff-code styling, without changing execution authority, approval authority, validation behavior, durable records, canonical diff data, or existing added/removed diff roles.

**Summary:** Refines interactive TUI presentation so completed tool invocations, plan proposal/auto-approval events, and semantic checks share a consistent Codex-style block formatter with bounded sanitized text, caller-supplied semantic roles, elapsed/outcome handling, and exact major-event spacing. Mutation diff presentation shows compact changed hunks with bounded unchanged context, hunk-header breathing room, hidden-line markers, and a configurable neutral code role while preserving added/removed styling and canonical diff authority.

**Deliverables:**

- Centralized interactive completed-tool presentation for built-in, MCP-imported, extension-backed, failed, cancelled, and unknown tools.
- Bounded sanitized single-line tool details that reuse host-owned metadata instead of raw arguments.
- Shared role-aware Codex-style lifecycle block formatting for TOOLS, SEMANTIC CHECKS, PLAN proposal, and PLAN auto-approval output, including common outcome/status and elapsed-duration suffix handling.
- Exactly one presentation-owned blank line between adjacent major visible lifecycle events.
- Presentation-only compact mutation-diff hunks with bounded unchanged context, hidden-line markers, and hunk spacing after `@@ ... @@` headers across governed mutation surfaces.
- Semantic neutral/context diff-code role support with added/removed role behavior unchanged.
- Focused automated coverage, user-facing docs, maintained manual-test-plan updates, and DOX closeout.

**Plans:**

- [Plan 73 - Codex-Style TUI Tool and Diff Presentation](../plan-73-codex-style-tui-tool-and-diff-presentation.md)
- [Plan 77 - Shared Codex-Style TUI Lifecycle Blocks](../plan-77-shared-codex-style-tui-lifecycle-blocks.md)

**Exit criteria:**

- All interactive completed tool invocations, including built-in, MCP-imported, extension-backed, failed, cancelled, and unknown tools, render through the shared lifecycle block formatter with common outcome and elapsed-duration handling.
- Duration-disabled mode preserves the same block shape without the elapsed suffix.
- Tool detail text is concise, bounded, host-owned, terminal-safe, and secret-free.
- PLAN proposal output renders through the same block family with muted guided summary/step text and without redundant approval-boundary prose.
- PLAN auto-approval output renders through the same block family with concise revision, risk, policy, and reason provenance while preserving plan/mutation approval separation.
- SEMANTIC CHECKS output renders through the same block family, preserves outcome/duration behavior, and makes baseline/pre/post phases understandable where placement could otherwise be surprising.
- Adjacent major visible lifecycle events render with exactly one blank line between them.
- Interactive mutation diffs display compact changed hunks with bounded unchanged context and exactly one presentation-owned blank line after each `@@ ... @@` hunk header.
- Added and removed diff role behavior remains unchanged, and neutral/context diff code text is configurable through a semantic TUI role.
- Canonical raw diffs, approval authority, mutation validation, durable records, headless machine outputs, and tool execution authority remain unchanged.
- Focused automated TUI tests, affected project tests, user-facing docs, manual test plan updates, and DOX closeout are complete.

**Prerequisites:** Plans 24-26, 37, 49, 57, and 63.

**Scope decisions:**

- This milestone is presentation-only: it does not alter model prompts, tool schemas, authorization, execution, plan approval, mutation approval, validation, persistence, or provider behavior.
- Shared lifecycle block formatting applies to the interactive TUI projection, not to raw durable transcript authority or machine-readable headless outputs.
- Presentation text never determines approval routing; typed host-owned events and request kinds remain authoritative.
- Compact hunk context, hidden-line markers, and hunk spacing are presentation-owned and must never become canonical diff content.
